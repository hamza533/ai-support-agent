using System.Diagnostics;
using System.Text.Json;
using Brightline.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Brightline.Data.Jobs;

/// <summary>Handles one kind of job. Must be idempotent: a job can run again after a crash or a retry.</summary>
public interface IJobHandler
{
    string Kind { get; }

    Task HandleAsync(JobContext context, CancellationToken ct);
}

public sealed class JobContext(Job job, Func<int, Task> reportProgress)
{
    public Job Job { get; } = job;

    public T Payload<T>() => JsonSerializer.Deserialize<T>(Job.Payload, JobQueue.Json)
        ?? throw new InvalidOperationException($"Job {Job.Id} has an empty payload.");

    public Task ReportProgressAsync(int percent) => reportProgress(percent);
}

public sealed class WorkerOptions
{
    public const string Section = "Worker";

    /// <summary><c>embedded</c> (in the web process, free demo host), <c>separate</c> (web does not run jobs; a worker container does), <c>disabled</c>.</summary>
    public string Mode { get; set; } = "embedded";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan Lease { get; set; } = JobQueue.DefaultLease;
}

/// <summary>Polls the jobs table and runs handlers. Releases the in-flight job on shutdown.</summary>
public sealed class JobWorker(
    IServiceScopeFactory scopes,
    IOptions<WorkerOptions> options,
    ILogger<JobWorker> logger) : BackgroundService
{
    public static readonly ActivitySource Activity = new("Brightline.Jobs");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Job worker started (poll {Poll})", options.Value.PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            bool ranJob;
            try
            {
                ranJob = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Job worker loop error");
                ranJob = false;
            }

            if (!ranJob)
            {
                try
                {
                    await Task.Delay(options.Value.PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Job worker stopped");
    }

    /// <summary>Claims and runs at most one job. Returns true if a job was claimed.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        var job = await queue.ClaimAsync(options.Value.Lease, ct);
        if (job is null)
        {
            return false;
        }

        using var activity = Activity.StartActivity($"job {job.Kind}");
        activity?.SetTag("job.id", job.Id);
        activity?.SetTag("job.attempt", job.Attempts);
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["JobId"] = job.Id, ["JobKind"] = job.Kind });

        var handler = scope.ServiceProvider.GetServices<IJobHandler>().FirstOrDefault(h => h.Kind == job.Kind);
        if (handler is null)
        {
            await queue.FailAsync(DeadLetter(job), $"No handler registered for job kind '{job.Kind}'.", CancellationToken.None);
            return true;
        }

        try
        {
            var context = new JobContext(job, p => queue.ReportProgressAsync(job.Id, p, CancellationToken.None));
            await handler.HandleAsync(context, ct);
            await queue.CompleteAsync(job.Id, CancellationToken.None);
            logger.LogInformation("Job {JobId} ({Kind}) succeeded", job.Id, job.Kind);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await queue.ReleaseAsync(job.Id, CancellationToken.None);
            logger.LogInformation("Job {JobId} released on shutdown", job.Id);
            throw;
        }
        catch (Exception ex)
        {
            var status = await queue.FailAsync(job, ex.Message, CancellationToken.None);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            logger.LogWarning(ex, "Job {JobId} ({Kind}) failed on attempt {Attempt}: now {Status}", job.Id, job.Kind, job.Attempts, status);
        }

        return true;
    }

    // An unknown kind will never succeed: dead-letter it immediately.
    private static Job DeadLetter(Job job)
    {
        job.Attempts = job.MaxAttempts;
        return job;
    }
}
