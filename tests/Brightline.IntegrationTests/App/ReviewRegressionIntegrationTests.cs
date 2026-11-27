using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.Data.Jobs;
using Brightline.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Brightline.IntegrationTests.App;

/// <summary>Job-queue findings from the code review: poison jobs dead-letter, stale workers are fenced off.</summary>
public sealed class JobQueueReviewTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_job_that_keeps_crashing_the_worker_is_dead_lettered_instead_of_looping()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        await db.Jobs.ExecuteDeleteAsync(Ct);
        var job = await queue.EnqueueAsync("poison", new { }, maxAttempts: 2, ct: Ct);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.NotNull(await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct));
            // simulate the process dying mid-job: the lease just expires
            await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
        }

        Assert.Null(await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct));
        var stored = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct);
        Assert.Equal(JobStatus.Dead, stored.Status);
        Assert.Contains("Lease expired", stored.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_worker_cannot_complete_a_job_that_was_reclaimed()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        await db.Jobs.ExecuteDeleteAsync(Ct);
        await queue.EnqueueAsync("slow", new { }, ct: Ct);

        var first = (await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct))!;
        await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
        var second = (await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct))!;

        Assert.Equal(0, await queue.CompleteAsync(first, Ct)); // fenced: the attempt number moved on
        Assert.Equal(1, await queue.CompleteAsync(second, Ct));
    }

    [Fact]
    public async Task Progress_extends_the_lease()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        await db.Jobs.ExecuteDeleteAsync(Ct);
        await queue.EnqueueAsync("long", new { }, ct: Ct);
        var job = (await queue.ClaimAsync(TimeSpan.FromSeconds(5), Ct))!;

        await queue.ReportProgressAsync(job, 40, TimeSpan.FromMinutes(10), Ct);

        var stored = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct);
        Assert.True(stored.LeaseUntil > DateTimeOffset.UtcNow.AddMinutes(9));
        Assert.Equal(40, stored.Progress);
    }
}
