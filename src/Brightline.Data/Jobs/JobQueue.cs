using System.Text.Json;
using Brightline.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Brightline.Data.Jobs;

/// <summary>
/// Durable job queue on a Postgres table. Workers claim with <c>FOR UPDATE SKIP LOCKED</c>, so several instances can
/// poll safely; expired leases are re-claimed (jobs survive crashes and restarts); failures back off exponentially with
/// jitter and end in <see cref="JobStatus.Dead"/> after <see cref="Job.MaxAttempts"/>.
/// </summary>
public sealed class JobQueue(AppDbContext db, TimeProvider time)
{
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Enqueues a job. With an idempotency key, an existing job with the same key is returned instead.</summary>
    public async Task<Job> EnqueueAsync(string kind, object payload, string? idempotencyKey = null, int maxAttempts = 5, CancellationToken ct = default)
    {
        if (idempotencyKey is not null)
        {
            var existing = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
            {
                return existing;
            }
        }

        var now = time.GetUtcNow();
        var job = new Job
        {
            Kind = kind,
            Payload = JsonSerializer.Serialize(payload, Json),
            IdempotencyKey = idempotencyKey,
            MaxAttempts = maxAttempts,
            RunAfter = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Jobs.Add(job);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } && idempotencyKey is not null)
        {
            db.Entry(job).State = EntityState.Detached;
            return await db.Jobs.AsNoTracking().SingleAsync(j => j.IdempotencyKey == idempotencyKey, ct);
        }

        db.Entry(job).State = EntityState.Detached;
        return job;
    }

    /// <summary>Claims the next runnable job (queued and due, or running with an expired lease), or null.</summary>
    public async Task<Job?> ClaimAsync(TimeSpan lease, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var leaseUntil = now + lease;
        var jobs = await db.Jobs.FromSqlInterpolated($"""
            UPDATE jobs
               SET status = 'Running', lease_until = {leaseUntil}, attempts = attempts + 1, updated_at = {now}
             WHERE id = (
                   SELECT id FROM jobs
                    WHERE (status = 'Queued' AND run_after <= {now})
                       OR (status = 'Running' AND lease_until < {now})
                    ORDER BY run_after, id
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);
        return jobs.SingleOrDefault();
    }

    public Task ReportProgressAsync(long jobId, int percent, CancellationToken ct = default) =>
        db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Progress, Math.Clamp(percent, 0, 100))
            .SetProperty(j => j.UpdatedAt, time.GetUtcNow()), ct);

    public Task CompleteAsync(long jobId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Succeeded)
            .SetProperty(j => j.Progress, 100)
            .SetProperty(j => j.Error, (string?)null)
            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.CompletedAt, now)
            .SetProperty(j => j.UpdatedAt, now), ct);
    }

    /// <summary>Records a failure: re-queued with backoff, or dead-lettered after the last attempt.</summary>
    public async Task<JobStatus> FailAsync(Job job, string error, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        var dead = job.Attempts >= job.MaxAttempts;
        var status = dead ? JobStatus.Dead : JobStatus.Queued;
        var runAfter = dead ? now : now + Backoff(job.Attempts);
        var trimmed = error.Length > 2000 ? error[..2000] : error;

        await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.Error, trimmed)
            .SetProperty(j => j.RunAfter, runAfter)
            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.CompletedAt, dead ? now : null)
            .SetProperty(j => j.UpdatedAt, now), ct);
        return status;
    }

    /// <summary>Graceful shutdown: give the job back without counting the interrupted attempt.</summary>
    public Task ReleaseAsync(long jobId, CancellationToken ct = default) =>
        db.Jobs.Where(j => j.Id == jobId && j.Status == JobStatus.Running).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Queued)
            .SetProperty(j => j.Attempts, j => j.Attempts > 0 ? j.Attempts - 1 : 0)
            .SetProperty(j => j.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(j => j.UpdatedAt, time.GetUtcNow()), ct);

    /// <summary>Admin "Retry": puts a failed or dead job back in the queue with a fresh attempt budget.</summary>
    public Task<int> RetryAsync(long jobId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db.Jobs.Where(j => j.Id == jobId && (j.Status == JobStatus.Dead || j.Status == JobStatus.Failed))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.Queued)
                .SetProperty(j => j.Attempts, 0)
                .SetProperty(j => j.Progress, 0)
                .SetProperty(j => j.RunAfter, now)
                .SetProperty(j => j.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(j => j.UpdatedAt, now), ct);
    }

    /// <summary>Exponential backoff (2^attempt seconds, capped at 10 min) with ±20 % jitter.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(600, Math.Pow(2, Math.Max(1, attempt)));
        var jitter = 1 + ((Random.Shared.NextDouble() * 0.4) - 0.2);
        return TimeSpan.FromSeconds(seconds * jitter);
    }
}
