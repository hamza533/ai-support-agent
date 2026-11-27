using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.Data.Jobs;
using Brightline.IntegrationTests.Infrastructure;
using Brightline.Knowledge.Ingestion;
using Brightline.Knowledge.Search;
using Brightline.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Brightline.IntegrationTests.Knowledge;

public sealed class KnowledgeFixture(PostgresFixture postgres) : IAsyncLifetime
{
    public FakeEmbeddingGenerator Embeddings { get; } = new();

    public AppFactory Factory { get; private set; } = null!;

    /// <summary>The sources right after first start, before other tests upload or re-index (tests share this fixture).</summary>
    public IReadOnlyList<Source> InitialSources { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        Factory = new AppFactory(
            await postgres.CreateDatabaseAsync("knowledge"),
            services => services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(Embeddings));
        _ = Factory.Server; // migrates and seeds the sample documents (jobs queued)
        await RunJobsAsync();
        InitialSources = await WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Sources.AsNoTracking().ToListAsync());
    }

    public async ValueTask DisposeAsync() => await Factory.DisposeAsync();

    /// <summary>Runs queued jobs to completion with the real worker logic.</summary>
    public async Task<int> RunJobsAsync()
    {
        var worker = new JobWorker(
            Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new WorkerOptions()),
            NullLogger<JobWorker>.Instance);
        var ran = 0;
        while (await worker.RunOnceAsync(CancellationToken.None))
        {
            ran++;
        }

        return ran;
    }

    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}

public sealed class KnowledgeTests(KnowledgeFixture fixture) : IClassFixture<KnowledgeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Sample_help_centre_documents_are_ingested_on_first_start()
    {
        var sources = fixture.InitialSources;

        Assert.True(sources.Count >= 10, $"expected the sample docs, got {sources.Count}");
        Assert.All(sources, s =>
        {
            Assert.Equal(SourceStatus.Indexed, s.Status);
            Assert.True(s.ChunkCount > 0);
            Assert.Equal(1, s.ActiveVersion);
        });
    }

    [Fact]
    public async Task Hybrid_search_finds_the_return_policy_for_a_headphone_question()
    {
        var hits = await fixture.WithScopeAsync(sp => sp.GetRequiredService<IKnowledgeSearch>()
            .SearchAsync("Can I return headphones if one side stopped working?", ct: Ct));

        Assert.NotEmpty(hits);
        Assert.Contains(hits.Take(4), h => h.Title.Contains("Returns", StringComparison.Ordinal) || h.Title.Contains("Headphones", StringComparison.Ordinal));
        Assert.All(hits, h => Assert.StartsWith("/help/", h.Uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_falls_back_to_full_text_when_embeddings_are_down()
    {
        fixture.Embeddings.Fail = true;
        try
        {
            var hits = await fixture.WithScopeAsync(sp => sp.GetRequiredService<IKnowledgeSearch>()
                .SearchAsync("gift card expire", ct: Ct));

            Assert.Contains(hits, h => h.Title.Contains("Gift Cards", StringComparison.Ordinal));
        }
        finally
        {
            fixture.Embeddings.Fail = false;
        }
    }

    [Fact]
    public async Task Reindex_swaps_in_a_new_version_and_removes_the_old_one()
    {
        var source = await fixture.WithScopeAsync(sp => sp.GetRequiredService<AppDbContext>().Sources.AsNoTracking()
            .FirstAsync(s => s.FileName == "warranty.md", Ct));

        await fixture.WithScopeAsync(sp => sp.GetRequiredService<KnowledgeService>().ReindexAsync(source.Id, Ct));
        await fixture.RunJobsAsync();

        var (after, versions, active) = await fixture.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var s = await db.Sources.AsNoTracking().SingleAsync(x => x.Id == source.Id, Ct);
            var v = await db.Chunks.Where(c => c.SourceId == source.Id).Select(c => c.Version).Distinct().ToListAsync(Ct);
            var a = await db.Chunks.CountAsync(c => c.SourceId == source.Id && c.IsActive, Ct);
            return (s, v, a);
        });

        Assert.Equal(source.ActiveVersion + 1, after.ActiveVersion);
        Assert.Equal([after.ActiveVersion], versions);
        Assert.Equal(after.ChunkCount, active);
    }

    [Fact]
    public async Task Uploads_are_validated_by_content_and_size()
    {
        var result = await fixture.WithScopeAsync(sp => sp.GetRequiredService<KnowledgeService>()
            .UploadAsync("malware.md", [0x4D, 0x5A, 0x90, 0x00, 0x03], ct: Ct));
        Assert.False(result.Ok);

        result = await fixture.WithScopeAsync(sp => sp.GetRequiredService<KnowledgeService>()
            .UploadAsync("new-policy.md", "# Holiday returns\n\n## Extended window\n\nItems bought in December can be returned until January 31."u8.ToArray(), ct: Ct));
        Assert.True(result.Ok);
        Assert.NotNull(result.JobId);
        await fixture.RunJobsAsync();

        var hits = await fixture.WithScopeAsync(sp => sp.GetRequiredService<IKnowledgeSearch>().SearchAsync("December holiday returns January", ct: Ct));
        Assert.Contains(hits.Take(3), h => h.Title == "Holiday returns"); // ties with the returns policy are broken by id
    }

    [Fact]
    public async Task Uploading_the_same_unchanged_file_again_does_nothing()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(RepoRoot(), "sample_data", "help-center", "gift-cards.md"), Ct);

        var result = await fixture.WithScopeAsync(sp => sp.GetRequiredService<KnowledgeService>().UploadAsync("gift-cards.md", bytes, ct: Ct));

        Assert.True(result.Ok);
        Assert.Null(result.JobId);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Brightline.slnx")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
