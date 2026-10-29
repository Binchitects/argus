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

    /// <summary>The passages nearest the question among the documents these readers may read: one of its readers, and all it requires.</summary>
    public Task<List<Passage>> SearchAsync(float[] query, IReadOnlyCollection<string> readers, int limit, CancellationToken ct) =>
        NearestAsync("d.\"Readers\" && @filter AND d.\"Requires\" <@ @filter", new NpgsqlParameter("filter", readers.ToArray()), query, limit, ct);

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

    private const string Readable = "d.\"Readers\" && @readers AND d.\"Requires\" <@ @readers";

    /// <summary>
    /// The passages that hold these words (all of them, any case), among the documents these readers may read: for the wiki,
    /// where people search without a model. A title that holds them counts most.
    /// </summary>
    public async Task<List<Passage>> KeywordAsync(string words, IReadOnlyCollection<string> readers, int limit, CancellationToken ct)
    {
        var terms = words.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8)
            .Select(t => "%" + t.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%").ToArray();
        if (terms.Length == 0)
        {
            return [];
        }
        var sql = string.Concat(
            "SELECT c.\"DocumentId\", d.\"Title\", d.\"Url\", s.\"Name\" AS \"Source\", d.\"AttachmentId\", c.\"Line\", c.\"EndLine\", c.\"Heading\", c.\"Text\", ",
            "(CASE WHEN d.\"Title\" ILIKE ALL(@terms) THEN 1.0 ELSE 0.5 END)::double precision AS \"Score\" ",
            "FROM knowledge_chunks c JOIN knowledge_documents d ON d.\"Id\" = c.\"DocumentId\" LEFT JOIN knowledge_sources s ON s.\"Id\" = d.\"SourceId\" ",
            "WHERE ", Readable, " AND d.\"SourceId\" IS NOT NULL AND (c.\"Text\" ILIKE ALL(@terms) OR d.\"Title\" ILIKE ALL(@terms)) ",
            "ORDER BY \"Score\" DESC, d.\"Title\", c.\"Ordinal\" LIMIT @limit");
        return await db.Database.SqlQueryRaw<Passage>(sql, new NpgsqlParameter("readers", readers.ToArray()), new NpgsqlParameter("terms", terms),
            new NpgsqlParameter("limit", limit)).ToListAsync(ct);
    }

    /// <summary>A source's documents these readers may read, by title (its words, when given), a page at a time.</summary>
    public async Task<List<WikiDocument>> DocumentsAsync(Guid? source, IReadOnlyCollection<string> readers, string? words, int skip, int take, CancellationToken ct)
    {
        var sql = string.Concat(
            "SELECT d.\"Id\", d.\"Title\", d.\"Url\", d.\"SourceId\", s.\"Name\" AS \"Source\", d.\"SyncedAt\", ",
            "(SELECT count(*) FROM knowledge_chunks c WHERE c.\"DocumentId\" = d.\"Id\")::int AS \"Passages\" ",
            "FROM knowledge_documents d JOIN knowledge_sources s ON s.\"Id\" = d.\"SourceId\" ",
            "WHERE ", Readable, source is null ? "" : " AND d.\"SourceId\" = @source", words is null ? "" : " AND d.\"Title\" ILIKE @words",
            " ORDER BY d.\"Title\" OFFSET @skip LIMIT @take");
        List<NpgsqlParameter> args = [new("readers", readers.ToArray()), new("skip", skip), new("take", take)];
        if (source is { } id)
        {
            args.Add(new("source", id));
        }
        if (words is not null)
        {
            args.Add(new("words", "%" + words.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%"));
        }
        return await db.Database.SqlQueryRaw<WikiDocument>(sql, [.. args]).ToListAsync(ct);
    }

    /// <summary>How many documents of each source these readers may read.</summary>
    public async Task<Dictionary<Guid, int>> CountsAsync(IReadOnlyCollection<string> readers, CancellationToken ct) =>
        (await db.Database.SqlQueryRaw<SourceCount>(string.Concat("SELECT d.\"SourceId\" AS \"Source\", count(*)::int AS \"Count\" FROM knowledge_documents d WHERE ",
            Readable, " AND d.\"SourceId\" IS NOT NULL GROUP BY d.\"SourceId\""), new NpgsqlParameter("readers", readers.ToArray())).ToListAsync(ct))
        .ToDictionary(c => c.Source, c => c.Count);

    /// <summary>A whole document's passages, in order, when these readers may read it; null when they may not (or it is not there).</summary>
    public async Task<(WikiDocument Document, List<KnowledgeChunk> Passages)?> ReadAsync(Guid id, IReadOnlyCollection<string> readers, CancellationToken ct)
    {
        var doc = (await db.Database.SqlQueryRaw<WikiDocument>(string.Concat(
            "SELECT d.\"Id\", d.\"Title\", d.\"Url\", d.\"SourceId\", s.\"Name\" AS \"Source\", d.\"SyncedAt\", 0 AS \"Passages\" ",
            "FROM knowledge_documents d JOIN knowledge_sources s ON s.\"Id\" = d.\"SourceId\" WHERE d.\"Id\" = @id AND ", Readable),
            new NpgsqlParameter("id", id), new NpgsqlParameter("readers", readers.ToArray())).ToListAsync(ct)).FirstOrDefault();
        if (doc is null)
        {
            return null;
        }
        var passages = await db.KnowledgeChunks.AsNoTracking().Where(c => c.DocumentId == id).OrderBy(c => c.Ordinal).ToListAsync(ct);
        return (doc, passages);
    }

    private static string? Cut(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}

/// <summary>A document as the wiki lists it.</summary>
public sealed class WikiDocument
{
    public Guid Id { get; init; }
    public required string Title { get; init; }
    public string? Url { get; init; }
    public Guid? SourceId { get; init; }
    public string? Source { get; init; }
    public DateTimeOffset SyncedAt { get; init; }
    public int Passages { get; init; }
}

internal sealed class SourceCount
{
    public Guid Source { get; init; }
    public int Count { get; init; }
}
