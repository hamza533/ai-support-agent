using System.Text.RegularExpressions;
using Brightline.Data.Entities;
using Brightline.Data.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Brightline.Data;

/// <summary>Key/value settings editable by admins. Typed views live in the projects that own them.</summary>
public sealed class SettingsStore(AppDbContext db, TimeProvider time)
{
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default) =>
        await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);

    public async Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        (await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, ct))?.Value;

    public async Task SetManyAsync(IReadOnlyDictionary<string, string> values, string? updatedBy, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var keys = values.Keys.ToList();
        var existing = await db.Settings.Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, ct);
        foreach (var (key, value) in values)
        {
            if (existing.TryGetValue(key, out var setting))
            {
                setting.Value = value;
                setting.UpdatedAt = now;
                setting.UpdatedBy = updatedBy;
            }
            else
            {
                db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = now, UpdatedBy = updatedBy });
            }
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Masks personal data before it reaches logs, traces or stored tool arguments.</summary>
public static partial class PiiMasker
{
    /// <summary>"maya.t@example.com" → "m***@example.com".</summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{email[0]}***{email[at..]}";
    }

    /// <summary>Masks every email address and phone-like number in free text.</summary>
    public static string MaskText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var masked = EmailRegex().Replace(text, m => MaskEmail(m.Value));
        return PhoneRegex().Replace(masked, "[phone]");
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\+?\d[\d\s().-]{7,}\d")]
    private static partial Regex PhoneRegex();
}

public sealed class RetentionOptions
{
    public const string Section = "Retention";
    public int ConversationDays { get; set; } = 90;
    public int TestConversationDays { get; set; } = 7;
    public int CompletedJobDays { get; set; } = 30;
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>Purges conversations (with messages and tool calls) and finished jobs past their retention period.</summary>
public sealed class RetentionService(
    IServiceScopeFactory scopes,
    IOptions<RetentionOptions> options,
    TimeProvider time,
    ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention purge failed");
            }

            try
            {
                await Task.Delay(options.Value.Interval, time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = time.GetUtcNow();
        var o = options.Value;
        var cutoff = now.AddDays(-o.ConversationDays);
        var testCutoff = now.AddDays(-o.TestConversationDays);
        var jobCutoff = now.AddDays(-o.CompletedJobDays);

        var conversations = await db.Conversations
            .Where(c => c.LastActivityAt < cutoff || (c.IsTest && c.LastActivityAt < testCutoff))
            .ExecuteDeleteAsync(ct);
        var jobs = await db.Jobs
            .Where(j => j.Status == JobStatus.Succeeded && j.CompletedAt < jobCutoff)
            .ExecuteDeleteAsync(ct);

        if (conversations + jobs > 0)
        {
            logger.LogInformation("Retention purge removed {Conversations} conversations and {Jobs} jobs", conversations, jobs);
        }

        return conversations + jobs;
    }
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";
    public bool MigrateOnStartup { get; set; }
}

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddBrightlineData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("App")
            ?? throw new InvalidOperationException("ConnectionStrings:App is required.");

        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));
        services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(connectionString), ServiceLifetime.Scoped);
        services.TryAddTimeProvider();
        services.AddScoped<JobQueue>();
        services.AddScoped<SettingsStore>();
        services.Configure<WorkerOptions>(configuration.GetSection(WorkerOptions.Section));
        services.Configure<RetentionOptions>(configuration.GetSection(RetentionOptions.Section));
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.Section));
        return services;
    }

    /// <summary>Registers the job worker and retention purge (embedded mode, or the dedicated worker process).</summary>
    public static IServiceCollection AddBrightlineBackgroundWork(this IServiceCollection services)
    {
        services.AddSingleton<JobWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<JobWorker>());
        services.AddHostedService<RetentionService>();
        return services;
    }

    private static void TryAddTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
