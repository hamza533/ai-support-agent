using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.Data.Jobs;
using Brightline.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Brightline.IntegrationTests.App;

public sealed class JobQueueTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(AsyncServiceScope Scope, JobQueue Queue, AppDbContext Db)> NewScopeAsync()
    {
        var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Jobs.ExecuteDeleteAsync(Ct);
        return (scope, scope.ServiceProvider.GetRequiredService<JobQueue>(), db);
    }

    [Fact]
    public async Task Enqueue_with_the_same_idempotency_key_returns_the_existing_job()
    {
        var (scope, queue, db) = await NewScopeAsync();
        await using var _ = scope;

        var first = await queue.EnqueueAsync("test", new { n = 1 }, "same-key", ct: Ct);
        var second = await queue.EnqueueAsync("test", new { n = 2 }, "same-key", ct: Ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await db.Jobs.CountAsync(Ct));
    }

    [Fact]
    public async Task Concurrent_claims_never_return_the_same_job()
    {
        var (scope, queue, _) = await NewScopeAsync();
        await using var __ = scope;
        for (var i = 0; i < 10; i++)
        {
            await queue.EnqueueAsync("test", new { i }, ct: Ct);
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var s = fixture.Factory.Services.CreateAsyncScope();
            return await s.ServiceProvider.GetRequiredService<JobQueue>().ClaimAsync(TimeSpan.FromMinutes(1), Ct);
        }));

        var ids = claims.Where(c => c is not null).Select(c => c!.Id).ToList();
        Assert.Equal(10, ids.Count);
        Assert.Equal(10, ids.Distinct().Count());
    }

    [Fact]
    public async Task Failures_back_off_then_dead_letter_after_max_attempts()
    {
        var (scope, queue, db) = await NewScopeAsync();
        await using var _ = scope;
        await queue.EnqueueAsync("test", new { }, maxAttempts: 2, ct: Ct);

        var job = await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct);
        Assert.Equal(JobStatus.Queued, await queue.FailAsync(job!, "boom", Ct));
        Assert.Null(await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct)); // backing off

        await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.RunAfter, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
        job = await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct);
        Assert.Equal(2, job!.Attempts);
        Assert.Equal(JobStatus.Dead, await queue.FailAsync(job, "boom again", Ct));

        Assert.Equal(1, await queue.RetryAsync(job.Id, Ct));
        Assert.NotNull(await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct));
    }

    [Fact]
    public async Task Jobs_with_an_expired_lease_are_reclaimed_after_a_crash()
    {
        var (scope, queue, db) = await NewScopeAsync();
        await using var _ = scope;
        await queue.EnqueueAsync("test", new { }, ct: Ct);

        var crashed = await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct);
        Assert.Null(await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct));

        await db.Jobs.ExecuteUpdateAsync(s => s.SetProperty(j => j.LeaseUntil, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
        var reclaimed = await queue.ClaimAsync(TimeSpan.FromMinutes(1), Ct);

        Assert.Equal(crashed!.Id, reclaimed!.Id);
        Assert.Equal(2, reclaimed.Attempts);
    }

    [Fact]
    public async Task Worker_runs_the_handler_and_marks_success()
    {
        var (scope, queue, db) = await NewScopeAsync();
        await using var _ = scope;
        var handler = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IJobHandler>(handler);
        services.AddScoped(_ => fixture.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<JobQueue>());
        var worker = new JobWorker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkerOptions()),
            NullLogger<JobWorker>.Instance);

        var job = await queue.EnqueueAsync(RecordingHandler.KindName, new { value = "hello" }, ct: Ct);
        Assert.True(await worker.RunOnceAsync(Ct));

        Assert.Equal("hello", handler.Received);
        var stored = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, Ct);
        Assert.Equal(JobStatus.Succeeded, stored.Status);
        Assert.Equal(100, stored.Progress);
    }

    private sealed class RecordingHandler : IJobHandler
    {
        public const string KindName = "record";

        public string Kind => KindName;

        public string? Received { get; private set; }

        public async Task HandleAsync(JobContext context, CancellationToken ct)
        {
            Received = context.Payload<Payload>().Value;
            await context.ReportProgressAsync(50);
        }

        private sealed record Payload(string Value);
    }
}
