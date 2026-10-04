using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Knowledge;

/// <summary>A passage found for a question: its text and place, and the document it is from.</summary>
public sealed class Passage
{
    public Guid DocumentId { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    /// <summary>The source's name; null for a chat's file.</summary>
    public string? Source { get; init; }
    public Guid? AttachmentId { get; init; }
    public int Line { get; init; }
    public int EndLine { get; init; }
    public string? Heading { get; init; }
    public required string Text { get; init; }
    /// <summary>Cosine similarity to the question, -1 to 1.</summary>
    public double Score { get; init; }
}

/// <summary>
/// Whether Postgres compares vectors with pgvector. The stack's Postgres image has it: the extension is
/// made once, if it can be. Without it (another Postgres) the comparison is plain SQL, slower but the same.
/// </summary>
public sealed partial class VectorSupport(IOptionsMonitor<KnowledgeOptions> options, ILogger<VectorSupport> logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool? _has;

    public async Task<bool> HasAsync(AppDbContext db, CancellationToken ct)
    {
        if (!options.CurrentValue.PgVector)
        {
            return false;
        }
        if (_has is { } known)
        {
            return known;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_has is null)
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector", ct);
                    _has = true;
                }
                catch (PostgresException ex)
                {
                    LogNoVector(logger, ex.MessageText);
                    _has = false;
                }
            }
            return _has.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "pgvector is not available ({Reason}): knowledge is compared in plain SQL, which is slower")]
    private static partial void LogNoVector(ILogger logger, string reason);
}

/// <summary>Documents and their passages in Postgres: saved whole (a document's passages replaced at once), and searched by meaning.</summary>
public sealed class KnowledgeStore(AppDbContext db, VectorSupport vectors)
{
    /// <summary>A document with its passages and their vectors, replacing what it had, in one transaction.</summary>
    public async Task SaveAsync(KnowledgeDocument doc, IReadOnlyList<Chunker.Chunk> chunks, IReadOnlyList<float[]> embeddings, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.KnowledgeDocuments.AnyAsync(d => d.Id == doc.Id, ct))
        {
            await db.KnowledgeChunks.Where(c => c.DocumentId == doc.Id).ExecuteDeleteAsync(ct);
            db.KnowledgeDocuments.Update(doc);
        }
        else
        {
            db.KnowledgeDocuments.Add(doc);
        }
        var rows = chunks.Select((c, i) => new KnowledgeChunk
        {
            DocumentId = doc.Id, Ordinal = i, Line = c.Line, EndLine = c.EndLine, Heading = Cut(c.Heading, 500), Text = c.Text, Embedding = embeddings[i],
        }).ToList();
        db.KnowledgeChunks.AddRange(rows);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // Nothing of it stays tracked: a sync saves thousands.
        db.Entry(doc).State = EntityState.Detached;
        rows.ForEach(r => db.Entry(r).State = EntityState.Detached);
    }

    /// <summary>The passages nearest the question among the documents these readers may read.</summary>
    public Task<List<Passage>> SearchAsync(float[] query, IReadOnlyCollection<string> readers, int limit, CancellationToken ct) =>
        NearestAsync("d.\"Readers\" && @filter", new NpgsqlParameter("filter", readers.ToArray()), query, limit, ct);

    /// <summary>The passages nearest the question in these chat files.</summary>
    public Task<List<Passage>> SearchFilesAsync(float[] query, IReadOnlyCollection<Guid> attachments, int limit, CancellationToken ct) =>
        NearestAsync("d.\"AttachmentId\" = ANY(@filter)", new NpgsqlParameter("filter", attachments.ToArray()), query, limit, ct);

    private async Task<List<Passage>> NearestAsync(string filter, NpgsqlParameter filterValue, float[] query, int limit, CancellationToken ct)
    {
        // The vectors are of unit length: cosine is their dot product. Vectors of another size (another model's) are left out.
        var distance = await vectors.HasAsync(db, ct)
            ? "(c.\"Embedding\"::vector <=> @q::real[]::vector)"
            : "(1 - (SELECT sum(a * b) FROM unnest(c.\"Embedding\", @q) AS t(a, b)))";
        var sql = string.Concat(
            "SELECT c.\"DocumentId\", d.\"Title\", d.\"Url\", s.\"Name\" AS \"Source\", d.\"AttachmentId\", c.\"Line\", c.\"EndLine\", c.\"Heading\", c.\"Text\", ",
            "(1 - ", distance, ")::double precision AS \"Score\" ",
            "FROM knowledge_chunks c JOIN knowledge_documents d ON d.\"Id\" = c.\"DocumentId\" LEFT JOIN knowledge_sources s ON s.\"Id\" = d.\"SourceId\" ",
            "WHERE ", filter, " AND cardinality(c.\"Embedding\") = @dims ",
            "ORDER BY ", distance, " LIMIT @limit");
        return await db.Database.SqlQueryRaw<Passage>(sql,
            new NpgsqlParameter("q", query), filterValue, new NpgsqlParameter("dims", query.Length), new NpgsqlParameter("limit", limit)).ToListAsync(ct);
    }

    private static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}
