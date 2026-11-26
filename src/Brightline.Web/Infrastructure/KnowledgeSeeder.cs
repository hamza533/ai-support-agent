using Brightline.Data;
using Brightline.Knowledge.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Brightline.Web.Infrastructure;

/// <summary>
/// Loads the synthetic help-centre documents from <c>Knowledge:SampleDataPath</c>. On start-up only when the
/// knowledge base is empty; <c>make seed</c> (re)uploads them, and unchanged files are skipped by content hash.
/// </summary>
public sealed class KnowledgeSeeder(
    KnowledgeService knowledge,
    AppDbContext db,
    IOptions<KnowledgeOptions> options,
    IHostEnvironment env,
    ILogger<KnowledgeSeeder> logger) : ISeeder, IStartupSeeder
{
    async Task ISeeder.SeedAsync(CancellationToken ct) => await UploadAllAsync(ct);

    async Task IStartupSeeder.SeedAsync(CancellationToken ct)
    {
        if (!await db.Sources.AnyAsync(ct))
        {
            await UploadAllAsync(ct);
        }
    }

    public async Task<int> UploadAllAsync(CancellationToken ct)
    {
        var dir = ResolveDirectory();
        if (dir is null)
        {
            logger.LogWarning("Sample data folder {Path} not found; skipping document seed", options.Value.SampleDataPath);
            return 0;
        }

        var count = 0;
        foreach (var file in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var result = await knowledge.UploadAsync(Path.GetFileName(file), await File.ReadAllBytesAsync(file, ct), ct: ct);
            if (result.Ok)
            {
                count++;
            }
            else
            {
                logger.LogWarning("Could not seed {File}: {Error}", Path.GetFileName(file), result.Error);
            }
        }

        logger.LogInformation("Queued {Count} help-centre documents for indexing", count);
        return count;
    }

    private string? ResolveDirectory()
    {
        var configured = options.Value.SampleDataPath;
        foreach (var candidate in new[]
        {
            configured,
            Path.Combine(env.ContentRootPath, configured),
            Path.Combine(AppContext.BaseDirectory, configured),
            Path.Combine(env.ContentRootPath, "..", "..", configured),
        })
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
