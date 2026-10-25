using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Identity;

/// <summary>A day's anchor: the newest entry's id and hash then, kept apart from the database (<see cref="AuditChain.AnchorFile"/>).</summary>
public sealed record AuditAnchor(DateTimeOffset At, long Id, string Hash, long Entries);

/// <summary>What walking the chain found.</summary>
/// <param name="BrokenAt">The first entry whose hash does not follow (it, or one before it, was changed, removed or slipped in); null: none.</param>
/// <param name="AnchorsWrong">Anchors whose entry is gone or has another hash now (the chain was rewritten after them).</param>
public sealed record AuditChainReport(DateTimeOffset At, long Entries, long? FirstId, long? LastId, string? LastHash, long? BrokenAt, string? Why,
    int Anchors, IReadOnlyList<long> AnchorsWrong)
{
    public bool Holds => BrokenAt is null && AnchorsWrong.Count == 0;
}

/// <summary>
/// The audit log as a hash chain: each entry's hash covers its fields and the hash before it, so an entry changed, removed or slipped
/// in breaks every hash after it, and a day's anchor kept apart shows a chain rewritten from some point on. Entries are sealed in the
/// order of their ids under one advisory lock, so replicas writing at once still make one chain.
/// </summary>
public static class AuditChain
{
    /// <summary>The advisory lock every writer of the audit log takes (the bytes of "AUDIT").</summary>
    public const long Lock = 0x4155444954;

    public const string AnchorFile = "anchors.jsonl";

    /// <summary>As Postgres keeps a time (microseconds): the hash is made from what is stored.</summary>
    public static DateTimeOffset Stored(DateTimeOffset at) => new(at.UtcTicks - at.UtcTicks % 10, TimeSpan.Zero);

    /// <summary>SHA-256 (hex) of the hash before and each field of the entry, each with its length, so no two entries read alike.</summary>
    public static string HashOf(string? before, AuditEvent e)
    {
        var text = new StringBuilder();
        void Field(string? v) => text.Append(v is null ? "-" : v.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(v).Append('\n');
        Field(before);
        Field(e.Id.ToString(CultureInfo.InvariantCulture));
        Field(e.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
        Field(e.ActorId?.ToString());
        Field(e.Actor);
        Field(e.Action);
        Field(e.Target);
        Field(e.Success ? "1" : "0");
        Field(e.Ip);
        Field(e.Detail);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// Seals the entries without a hash, in the order of their ids, from the hash of the sealed one before them. Inside a transaction
    /// holding <see cref="Lock"/> (taken here when none is open).
    /// </summary>
    public static async Task<int> SealAsync(AppDbContext db, CancellationToken ct = default)
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({Lock})", ct);
            var sealedCount = 0;
            while (true)
            {
                var open = await db.AuditEvents.Where(e => e.Hash == null).OrderBy(e => e.Id).Take(1000).ToListAsync(ct);
                if (open.Count == 0)
                {
                    break;
                }
                var first = open[0].Id;
                var before = await db.AuditEvents.Where(e => e.Id < first && e.Hash != null).OrderByDescending(e => e.Id).Select(e => e.Hash).FirstOrDefaultAsync(ct);
                foreach (var e in open)
                {
                    e.At = Stored(e.At);
                    before = e.Hash = HashOf(before, e);
                }
                await db.SaveChangesAsync(ct);
                sealedCount += open.Count;
            }
            if (own is not null)
            {
                await own.CommitAsync(ct);
            }
            return sealedCount;
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync();
            }
        }
    }

    /// <summary>Walks the whole chain from its first entry (the oldest kept), then checks each anchor against the entry it names.</summary>
    public static async Task<AuditChainReport> VerifyAsync(AppDbContext db, IReadOnlyList<AuditAnchor> anchors, DateTimeOffset now, CancellationToken ct = default)
    {
        string? before = null;
        long count = 0;
        long? first = null, last = null, broken = null;
        string? why = null;
        var hashes = new Dictionary<long, string>();
        var wanted = anchors.Select(a => a.Id).ToHashSet();
        var after = long.MinValue;
        while (broken is null)
        {
            var page = await db.AuditEvents.AsNoTracking().Where(e => e.Id > after && e.Hash != null).OrderBy(e => e.Id).Take(5000).ToListAsync(ct);
            if (page.Count == 0)
            {
                break;
            }
            foreach (var e in page)
            {
                // The oldest kept is the start, taken as given (older ones may have aged out, so what came before it is unknown);
                // every one after must follow from it. An anchor covers the start too.
                if (first is not null && HashOf(before, e) != e.Hash)
                {
                    broken = e.Id;
                    why = "it was changed, or an entry just before it was removed or slipped in";
                    break;
                }
                first ??= e.Id;
                before = e.Hash;
                last = e.Id;
                count++;
                if (wanted.Contains(e.Id))
                {
                    hashes[e.Id] = e.Hash!;
                }
            }
            after = page[^1].Id;
        }
        var wrong = anchors.Where(a => (first is null || a.Id >= first) && (!hashes.TryGetValue(a.Id, out var h) || h != a.Hash)).Select(a => a.Id).ToList();
        if (broken is not null)
        {
            // Past the break nothing was read: only the anchors before it are judged.
            wrong = [.. wrong.Where(id => id < broken)];
        }
        return new AuditChainReport(now, count, first, last, before, broken, why, anchors.Count, wrong);
    }

    public static IReadOnlyList<AuditAnchor> ReadAnchors(string? dir)
    {
        var path = dir is null ? null : Path.Combine(dir, AnchorFile);
        if (path is null || !File.Exists(path))
        {
            return [];
        }
        var list = new List<AuditAnchor>();
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                if (JsonSerializer.Deserialize<AuditAnchor>(line, Json) is { } a)
                {
                    list.Add(a);
                }
            }
            catch (JsonException)
            {
                // A line cut short by a crash: the anchors before and after it still count.
            }
        }
        return list;
    }

    public static void AppendAnchor(string dir, AuditAnchor anchor)
    {
        Directory.CreateDirectory(dir);
        File.AppendAllText(Path.Combine(dir, AnchorFile), JsonSerializer.Serialize(anchor, Json) + "\n");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
