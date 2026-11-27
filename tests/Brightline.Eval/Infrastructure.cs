using Brightline.Ai;
using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.Data.Jobs;
using Brightline.Knowledge.Ingestion;
using Brightline.LegacyOrders.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.Eval;

public static class EvalPaths
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Brightline.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

/// <summary>Defaults that match docker-compose.yml (overridden by .env and environment variables).</summary>
public static class EvalDefaults
{
    public static Dictionary<string, string?> Settings(string host) => new()
    {
        ["ConnectionStrings:App"] = $"Host={host};Port=55432;Database=brightline_app;Username=brightline;Password=brightline-dev-only",
        ["LegacyOrders:BaseUrl"] = $"http://{host}:5081",
        ["LegacyOrders:ApiKey"] = "dev-legacy-api-key-change-me",
        ["LegacyOrders:AdminKey"] = "dev-legacy-admin-key-change-me",
        ["Ai:MaxRequestsPerMinute"] = "10",
        ["Eval:JudgeModel"] = "gemini-3.1-flash-lite",
        ["Eval:JudgeMaxRequestsPerMinute"] = "12",
    };
}

/// <summary>Minimal .env reader (KEY=VALUE, # comments) so `make eval` uses the same key as docker compose.</summary>
public static class EnvFile
{
    public static Dictionary<string, string?> Load(string path)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
            {
                continue;
            }

            var key = line[..line.IndexOf('=')].Trim();
            var value = line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
            if (value.Length == 0 || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal) || key.StartsWith("LegacyOrders__BaseUrl", StringComparison.Ordinal))
            {
                continue; // compose-internal hostnames don't work from the host
            }

            values[key.Replace("__", ":", StringComparison.Ordinal)] = value;
        }

        return values;
    }
}

public static class Preflight
{
    public static async Task<string?> RunAsync(IServiceProvider sp, IConfiguration config)
    {
        var ai = config.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
        var hasKey = ai.Provider switch
        {
            AiProviders.GeminiOpenAiCompat => !string.IsNullOrWhiteSpace(ai.Gemini.ApiKey),
            AiProviders.AzureOpenAi => !string.IsNullOrWhiteSpace(ai.AzureOpenAI.Endpoint),
            AiProviders.OpenAi => !string.IsNullOrWhiteSpace(ai.OpenAI.ApiKey),
            _ => false,
        };
        if (!hasKey)
        {
            return "No AI provider credentials. Add Ai__Gemini__ApiKey to .env (https://aistudio.google.com/apikey) and re-run `make eval`.";
        }

        try
        {
            await using var scope = sp.CreateAsyncScope();
            if (!await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.CanConnectAsync())
            {
                return "Can't reach the app database. Start the stack first: `make dev`.";
            }

            await scope.ServiceProvider.GetRequiredService<ILegacyOrdersClient>().VerifyCustomerAsync("BL-10482", "maya.t@example.com");
        }
        catch (Exception ex)
        {
            return $"Stack not reachable ({ex.GetType().Name}: {ex.Message}). Start it with `make dev`.";
        }

        return null;
    }
}

public static class KnowledgeIndex
{
    /// <summary>Uploads the sample docs if missing and re-indexes anything that was indexed without embeddings.</summary>
    public static async Task EnsureSemanticIndexAsync(IServiceProvider sp, string sampleDir)
    {
        await using (var scope = sp.CreateAsyncScope())
        {
            var knowledge = scope.ServiceProvider.GetRequiredService<KnowledgeService>();
            foreach (var file in Directory.EnumerateFiles(sampleDir, "*.md"))
            {
                await knowledge.UploadAsync(Path.GetFileName(file), await File.ReadAllBytesAsync(file));
            }

            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var keywordOnly = await db.Sources.Where(s => s.Error == KnowledgeIngestor.KeywordOnlyNote || s.Status == SourceStatus.Failed)
                .Select(s => s.Id).ToListAsync();
            foreach (var id in keywordOnly)
            {
                await knowledge.ReindexAsync(id);
            }

            if (keywordOnly.Count > 0)
            {
                Console.WriteLine($"Re-indexing {keywordOnly.Count} documents with embeddings…");
            }
        }

        var worker = sp.GetRequiredService<JobWorker>();
        while (await worker.RunOnceAsync(CancellationToken.None))
        {
            // drain the queue in-process (the web app's worker may also pick jobs up; SKIP LOCKED keeps them apart)
        }

        await using var check = sp.CreateAsyncScope();
        var pending = await check.ServiceProvider.GetRequiredService<AppDbContext>().Sources
            .CountAsync(s => s.Status == SourceStatus.Processing);
        for (var i = 0; pending > 0 && i < 60; i++)
        {
            await Task.Delay(1000);
            pending = await check.ServiceProvider.GetRequiredService<AppDbContext>().Sources.CountAsync(s => s.Status == SourceStatus.Processing);
        }
    }
}
