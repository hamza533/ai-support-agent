using System.Diagnostics;
using System.Text.RegularExpressions;
using Brightline.Data;
using Brightline.Knowledge.Ingestion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;

namespace Brightline.Knowledge.Search;

public sealed record SearchHit(
    Guid ChunkId,
    Guid SourceId,
    string Title,
    string Uri,
    string HeadingPath,
    int? Page,
    string Text,
    double Score);

public interface IKnowledgeSearch
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int? top = null, CancellationToken ct = default);
}

/// <summary>
/// Hybrid retrieval: pgvector cosine search + Postgres full-text search over active chunks, merged with
/// Reciprocal Rank Fusion (score = Σ 1 / (k + rank)). Falls back to full-text only if embeddings are unavailable.
/// </summary>
public sealed partial class HybridSearch(
    AppDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    VectorStoreCollection<Guid, ChunkRecord> chunks,
    IOptions<KnowledgeOptions> options,
    ILogger<HybridSearch> logger) : IKnowledgeSearch
{
    private static readonly ActivitySource _activity = new("Brightline.Knowledge");

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int? top = null, CancellationToken ct = default)
    {
        using var activity = _activity.StartActivity("hybrid search");
        var o = options.Value;
        var k = top ?? o.TopK;

        var vectorRanked = await VectorSearchAsync(query, o.CandidateK, ct);
        var textRanked = await FullTextSearchAsync(query, o.CandidateK, ct);
        var fused = Rrf(o.RrfK, vectorRanked, textRanked).Take(k).ToList();
        activity?.SetTag("hits.vector", vectorRanked.Count);
        activity?.SetTag("hits.fts", textRanked.Count);
        if (fused.Count == 0)
        {
            return [];
        }

        var ids = fused.Select(f => f.Id).ToList();
        var rows = await db.Chunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Join(db.Sources, c => c.SourceId, s => s.Id, (c, s) => new { c.Id, c.SourceId, c.Title, s.Uri, c.HeadingPath, c.Page, c.Text })
            .ToDictionaryAsync(r => r.Id, ct);

        return [.. fused.Where(f => rows.ContainsKey(f.Id)).Select(f =>
        {
            var r = rows[f.Id];
            return new SearchHit(r.Id, r.SourceId, r.Title, r.Uri, r.HeadingPath, r.Page, r.Text, f.Score);
        })];
    }

    /// <summary>Reciprocal Rank Fusion of several ranked id lists.</summary>
    public static IEnumerable<(Guid Id, double Score)> Rrf(int k, params IReadOnlyList<Guid>[] rankings) =>
        rankings
            .SelectMany(list => list.Select((id, rank) => (id, score: 1.0 / (k + rank + 1))))
            .GroupBy(x => x.id)
            .Select(g => (Id: g.Key, Score: g.Sum(x => x.score)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Id);

    private async Task<IReadOnlyList<Guid>> VectorSearchAsync(string query, int k, CancellationToken ct)
    {
        try
        {
            var embedding = await embeddings.GenerateAsync(query, cancellationToken: ct);
            var results = new List<Guid>();
            await foreach (var hit in chunks.SearchAsync(embedding.Vector, k, new VectorSearchOptions<ChunkRecord> { Filter = c => c.IsActive }, ct))
            {
                results.Add(hit.Record.Id);
            }

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Vector search unavailable; using full-text search only");
            return [];
        }
    }

    private async Task<IReadOnlyList<Guid>> FullTextSearchAsync(string query, int k, CancellationToken ct)
    {
        var expression = ToOrQuery(query);
        if (expression.Length == 0)
        {
            return [];
        }

        return await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value"
              FROM chunks
             WHERE is_active AND tsv @@ to_tsquery('english', {expression})
             ORDER BY ts_rank_cd(tsv, to_tsquery('english', {expression})) DESC, id
             LIMIT {k}
            """).ToListAsync(ct);
    }

    /// <summary>"Can I return headphones?" → "can | return | headphones" (OR semantics; stop words dropped by Postgres).</summary>
    public static string ToOrQuery(string query) =>
        string.Join(" | ", WordRegex().Matches(query.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(w => w.Length > 1)
            .Distinct()
            .Take(32));

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordRegex();
}
