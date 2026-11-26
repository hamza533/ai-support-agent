using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Brightline.Ai;
using Brightline.Data;
using Brightline.Data.Entities;
using Brightline.Data.Jobs;
using Brightline.Knowledge.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.VectorData;

namespace Brightline.Knowledge.Ingestion;

/// <summary>Vector-store view of the <c>chunks</c> table (schema owned by EF migrations).</summary>
public sealed class ChunkRecord
{
    [VectorStoreKey(StorageName = "id")]
    public Guid Id { get; set; }

    [VectorStoreData(StorageName = "source_id", IsIndexed = true)]
    public Guid SourceId { get; set; }

    [VectorStoreData(StorageName = "version")]
    public int Version { get; set; }

    [VectorStoreData(StorageName = "is_active", IsIndexed = true)]
    public bool IsActive { get; set; }

    [VectorStoreData(StorageName = "chunk_index")]
    public int ChunkIndex { get; set; }

    [VectorStoreData(StorageName = "title")]
    public string Title { get; set; } = string.Empty;

    [VectorStoreData(StorageName = "text")]
    public string Text { get; set; } = string.Empty;

    [VectorStoreData(StorageName = "heading_path")]
    public string HeadingPath { get; set; } = string.Empty;

    [VectorStoreData(StorageName = "page")]
    public int? Page { get; set; }

    [VectorStoreVector(768, StorageName = "embedding", DistanceFunction = DistanceFunction.CosineDistance, IndexKind = IndexKind.Hnsw)]
    public ReadOnlyMemory<float>? Embedding { get; set; }
}

public sealed class KnowledgeOptions
{
    public const string Section = "Knowledge";
    public int MaxUploadBytes { get; set; } = 10 * 1024 * 1024;
    public int ChunkChars { get; set; } = 1000;
    public int OverlapChars { get; set; } = 150;
    public int EmbeddingBatchSize { get; set; } = 32;
    public int TopK { get; set; } = 6;
    public int CandidateK { get; set; } = 20;
    public int RrfK { get; set; } = 60;
    public string SampleDataPath { get; set; } = "sample_data/help-center";
}

public static class JobKinds
{
    public const string IngestSource = "ingest-source";
}

public sealed record IngestPayload(Guid SourceId, string ContentHash);

/// <summary>
/// Parses, chunks and embeds a source as a NEW version, then swaps it in atomically: readers see either the old
/// version or the new one, never a mix. Re-running the same version overwrites the same chunk ids (idempotent).
/// </summary>
public sealed class KnowledgeIngestor(
    AppDbContext db,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    Microsoft.Extensions.VectorData.VectorStoreCollection<Guid, ChunkRecord> chunks,
    IOptions<KnowledgeOptions> options,
    TimeProvider time,
    ILogger<KnowledgeIngestor> logger)
{
    public static readonly ActivitySource Activity = new("Brightline.Knowledge");

    public const string KeywordOnlyNote = "Indexed for keyword search only (no AI provider configured). Re-index after adding an API key for semantic search.";

    public async Task<int> IngestAsync(Guid sourceId, Func<int, Task>? progress = null, CancellationToken ct = default)
    {
        using var activity = Activity.StartActivity("ingest source");
        activity?.SetTag("source.id", sourceId);
        var source = await db.Sources.SingleOrDefaultAsync(s => s.Id == sourceId, ct)
            ?? throw new InvalidOperationException($"Source {sourceId} no longer exists.");
        var o = options.Value;
        var newVersion = source.ActiveVersion + 1;

        var parsed = DocumentParser.Parse(source.Type, source.Content, source.Title);
        var textChunks = Chunker.Chunk(parsed.Sections, o.ChunkChars, o.OverlapChars);
        if (textChunks.Count == 0)
        {
            throw new InvalidOperationException("The document contains no extractable text.");
        }

        var title = string.IsNullOrWhiteSpace(source.Title) ? parsed.Title : source.Title;
        await Report(progress, 10);

        var records = new List<ChunkRecord>(textChunks.Count);
        var semantic = true;
        for (var start = 0; start < textChunks.Count; start += o.EmbeddingBatchSize)
        {
            var batch = textChunks.Skip(start).Take(o.EmbeddingBatchSize).ToList();
            // Title + heading give each chunk its context for retrieval.
            var inputs = batch.Select(c => $"{title}\n{c.HeadingPath}\n\n{c.Text}").ToList();
            IReadOnlyList<ReadOnlyMemory<float>?> vectors = batch.Select(_ => (ReadOnlyMemory<float>?)null).ToList();
            if (semantic)
            {
                try
                {
                    var generated = await embeddings.GenerateAsync(inputs, cancellationToken: ct);
                    vectors = generated.Select(v => (ReadOnlyMemory<float>?)v.Vector).ToList();
                }
                catch (AiNotConfiguredException ex)
                {
                    // No AI provider yet: index for keyword (full-text) search only; re-index later for vectors.
                    logger.LogWarning("Indexing {SourceId} without embeddings: {Reason}", sourceId, ex.Message);
                    semantic = false;
                }
            }

            records.AddRange(batch.Select((c, i) => new ChunkRecord
            {
                Id = ChunkId(sourceId, newVersion, c.Index),
                SourceId = sourceId,
                Version = newVersion,
                IsActive = false,
                ChunkIndex = c.Index,
                Title = title,
                Text = c.Text,
                HeadingPath = c.HeadingPath,
                Page = c.Page,
                Embedding = vectors[i],
            }));
            await Report(progress, 10 + (int)(70.0 * Math.Min(textChunks.Count, start + batch.Count) / textChunks.Count));
        }

        await chunks.UpsertAsync(records, ct);
        await Report(progress, 90);

        // Atomic swap: flip active flags and the source's active version in one transaction.
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Chunks.Where(c => c.SourceId == sourceId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsActive, c => c.Version == newVersion), ct);
            await db.Sources.Where(s => s.Id == sourceId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.ActiveVersion, newVersion)
                .SetProperty(x => x.ChunkCount, records.Count)
                .SetProperty(x => x.Title, title)
                .SetProperty(x => x.Status, SourceStatus.Indexed)
                .SetProperty(x => x.Error, semantic ? null : KeywordOnlyNote)
                .SetProperty(x => x.UpdatedAt, time.GetUtcNow()), ct);
            await tx.CommitAsync(ct);
        }

        // Old versions are no longer readable; remove them.
        await db.Chunks.Where(c => c.SourceId == sourceId && c.Version != newVersion).ExecuteDeleteAsync(ct);
        logger.LogInformation("Indexed source {SourceId} version {Version}: {Chunks} chunks", sourceId, newVersion, records.Count);
        return records.Count;
    }

    public static Guid ChunkId(Guid sourceId, int version, int index)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceId:N}:{version}:{index}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static Task Report(Func<int, Task>? progress, int value) => progress?.Invoke(value) ?? Task.CompletedTask;
}

public sealed class IngestionJobHandler(KnowledgeIngestor ingestor, AppDbContext db, TimeProvider time) : IJobHandler
{
    public string Kind => JobKinds.IngestSource;

    public async Task HandleAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload<IngestPayload>();
        try
        {
            await ingestor.IngestAsync(payload.SourceId, context.ReportProgressAsync, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var finalAttempt = context.Job.Attempts >= context.Job.MaxAttempts || ex is AiNotConfiguredException;
            await db.Sources.Where(s => s.Id == payload.SourceId && s.ActiveVersion == 0).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, finalAttempt ? SourceStatus.Failed : SourceStatus.Processing)
                .SetProperty(x => x.Error, ex.Message)
                .SetProperty(x => x.UpdatedAt, time.GetUtcNow()), CancellationToken.None);
            if (ex is AiNotConfiguredException)
            {
                context.Job.Attempts = context.Job.MaxAttempts; // retrying cannot help until configured
            }

            throw;
        }
    }
}

public sealed record UploadResult(bool Ok, string? Error, Guid? SourceId, long? JobId);

/// <summary>Admin-facing document operations: upload (validated), re-index, delete.</summary>
public sealed class KnowledgeService(AppDbContext db, JobQueue queue, IOptions<KnowledgeOptions> options, TimeProvider time)
{
    public async Task<UploadResult> UploadAsync(string fileName, byte[] content, string? title = null, CancellationToken ct = default)
    {
        fileName = Path.GetFileName(fileName);
        if (content.Length == 0)
        {
            return new UploadResult(false, "The file is empty.", null, null);
        }

        if (content.Length > options.Value.MaxUploadBytes)
        {
            return new UploadResult(false, $"Files must be {options.Value.MaxUploadBytes / 1024 / 1024} MB or smaller.", null, null);
        }

        var type = FileTypeDetector.Detect(fileName, content);
        if (type is null)
        {
            return new UploadResult(false, "Only PDF, DOCX and Markdown files are supported (checked by file content).", null, null);
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        var now = time.GetUtcNow();
        var source = await db.Sources.SingleOrDefaultAsync(s => s.FileName == fileName, ct);
        if (source is null)
        {
            source = new Source
            {
                FileName = fileName,
                Title = title ?? string.Empty, // filled from the document's own heading during ingestion
                Uri = string.Empty,
                Type = type.Value,
                CreatedAt = now,
            };
            source.Uri = $"/help/{source.Id}";
            db.Sources.Add(source);
        }
        else if (source.ContentHash == hash && source.Status == SourceStatus.Indexed)
        {
            return new UploadResult(true, null, source.Id, null); // unchanged: nothing to do
        }

        source.Type = type.Value;
        source.Content = content;
        source.ContentHash = hash;
        source.Status = SourceStatus.Processing;
        source.Error = null;
        source.UpdatedAt = now;
        if (title is not null)
        {
            source.Title = title;
        }

        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(JobKinds.IngestSource, new IngestPayload(source.Id, hash), $"ingest:{source.Id}:{hash}", ct: ct);
        if (job.Status is JobStatus.Dead or JobStatus.Failed)
        {
            await queue.RetryAsync(job.Id, ct); // same content uploaded again after a failure: try again
        }

        return new UploadResult(true, null, source.Id, job.Id);
    }

    public async Task<long?> ReindexAsync(Guid sourceId, CancellationToken ct = default)
    {
        var source = await db.Sources.SingleOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source is null)
        {
            return null;
        }

        source.Status = SourceStatus.Processing;
        source.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var job = await queue.EnqueueAsync(JobKinds.IngestSource, new IngestPayload(source.Id, source.ContentHash), $"reindex:{source.Id}:{Guid.NewGuid():N}", ct: ct);
        return job.Id;
    }

    public async Task<bool> DeleteAsync(Guid sourceId, CancellationToken ct = default) =>
        await db.Sources.Where(s => s.Id == sourceId).ExecuteDeleteAsync(ct) > 0; // chunks cascade
}
