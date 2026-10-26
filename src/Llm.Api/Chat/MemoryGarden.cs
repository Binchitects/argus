using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// The memory garden: now and then, a person's memories are tidied, so hundreds of them stay worth reading over months.
/// Near-duplicates (alike by meaning, or by their words) are merged into one that keeps every detail; one replaced over a
/// month ago, or not used for months, is let go; a long one is tightened. Each change is kept with what the memories were
/// (Memory → History), and Undo puts them back. The company's memories and pinned ones are never let go. Leader only.
/// </summary>
public sealed partial class MemoryGarden(IServiceScopeFactory scopes, Operations.Replicas replicas, TimeProvider clock, IConfiguration config, ILogger<MemoryGarden> logger)
    : BackgroundService
{
    /// <summary>How often it looks, and how long a person's memories stay as they are after a tidy.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(1), Again = TimeSpan.FromDays(1);

    /// <summary>A replaced memory is let go this long after; one never used, this long after it was made; one used once, this long after.</summary>
    public static readonly TimeSpan ReplacedFor = TimeSpan.FromDays(30), NeverUsedFor = TimeSpan.FromDays(180), UnusedFor = TimeSpan.FromDays(365);

    /// <summary>People tidied in one pass, and the fewest memories worth tidying.</summary>
    public const int MostPeople = 20, Fewest = 5;

    /// <summary>Alike enough to be one: by meaning (their vectors), or by their words.</summary>
    public const double Alike = 0.9, WordsAlike = 0.8;

    /// <summary>A memory longer than this is tightened; the most merged into one at once.</summary>
    public const int LongChars = 300, MostMerged = 4;

    /// <summary>For the gateway's and the bill's tags.</summary>
    public const string Tag = "memory-garden";

    private const string MergeInstructions =
        "You tidy what an AI assistant remembers about a person. The memories given say the same thing, or nearly: write the one memory that " +
        "says all they say, keeping every detail (names, numbers, the reason for a decision); when they disagree, the newest (the last) wins. " +
        "One or two short sentences, in the language they are written in. Write only the memory.";

    private const string TightenInstructions =
        "You tidy what an AI assistant remembers about a person. Write the memory given in at most 200 characters, keeping every name, number " +
        "and reason. Write only the memory.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Settings → Chat → Tidy memories.
        if (bool.TryParse(config["Memory:Garden"], out var on) && !on)
        {
            return;
        }
        using var timer = new PeriodicTimer(Every, clock);
        do
        {
            if (!replicas.IsLeader)
            {
                continue;
            }
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass: the people whose memories were not tidied for a day, those tidied longest ago first; how many changes.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!scope.ServiceProvider.GetRequiredService<Memories>().Enabled)
        {
            return 0;
        }
        var before = clock.GetUtcNow() - Again;
        var people = await db.Users
            .Where(u => !u.MemoryOff && u.ProcessingRestrictedAt == null && (u.MemoryTidiedAt == null || u.MemoryTidiedAt < before)
                && db.Memories.Count(m => m.UserId == u.Id && m.Scope != Memory.Company && !m.Proposed) >= Fewest)
            .OrderBy(u => u.MemoryTidiedAt).Select(u => u.Id).Take(MostPeople).ToListAsync(ct);
        var changes = 0;
        foreach (var id in people)
        {
            changes += await TidyAsync(scope.ServiceProvider, id, ct);
        }
        return changes;
    }

    /// <summary>Tidies one person's memories now (also Memory → Tidy now): how many changes it made.</summary>
    public async Task<int> TidyAsync(IServiceProvider services, Guid userId, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var memories = services.GetRequiredService<Memories>();
        if (await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct) is not { } person)
        {
            return 0;
        }
        var now = clock.GetUtcNow();
        var mine = await db.Memories.Where(m => m.UserId == userId && m.Scope != Memory.Company && !m.Proposed).ToListAsync(ct);
        var changes = new List<MemoryChange>();

        // Let go: replaced long ago, or not used for months (never the pinned).
        void LetGo(IEnumerable<Memory> gone, string reason)
        {
            var list = gone.ToList();
            if (list.Count == 0)
            {
                return;
            }
            changes.Add(new MemoryChange { UserId = userId, Action = MemoryChange.LetGo, Reason = reason, Before = Snapshot(list), At = now });
            db.Memories.RemoveRange(list);
            mine.RemoveAll(list.Contains);
        }
        LetGo(mine.Where(m => m.ReplacedBy is not null && m.UpdatedAt < now - ReplacedFor), "replaced by a newer memory over a month ago");
        LetGo(mine.Where(m => !m.Pinned && m.ReplacedBy is null && m.Uses == 0 && m.CreatedAt < now - NeverUsedFor), "never used in six months");
        LetGo(mine.Where(m => !m.Pinned && m.ReplacedBy is null && m.UsedAt is { } used && used < now - UnusedFor), "not used for a year");

        // Merged: those that say the same (of one scope and project), into the one used most (or pinned, or the newest).
        var active = mine.Where(m => m.ReplacedBy is null).ToList();
        var words = active.ToDictionary(m => m.Id, m => Memories.Words(m.Text));
        var merged = new HashSet<Guid>();
        foreach (var m in active.OrderBy(m => m.CreatedAt))
        {
            if (merged.Contains(m.Id))
            {
                continue;
            }
            var group = active.Where(o => o.Id != m.Id && !merged.Contains(o.Id) && o.Scope == m.Scope && o.ProjectKey == m.ProjectKey && Same(m, o, words))
                .OrderBy(o => o.CreatedAt).Take(MostMerged - 1).Prepend(m).ToList();
            if (group.Count < 2)
            {
                continue;
            }
            var asked = await AskAsync(services, person, MergeInstructions, string.Join('\n', group.Select(g => "- " + g.Text)), ct);
            if (asked is not { } text)
            {
                continue;
            }
            var keep = group.OrderByDescending(g => g.Pinned).ThenByDescending(g => g.Uses).ThenByDescending(g => g.UpdatedAt).First();
            changes.Add(new MemoryChange { UserId = userId, Action = MemoryChange.Merged, Reason = $"{group.Count} said the same", Before = Snapshot(group), MemoryId = keep.Id, After = text, At = now });
            (keep.Text, keep.Pinned, keep.Uses, keep.UpdatedAt) = (text, group.Any(g => g.Pinned), group.Sum(g => g.Uses), now);
            keep.UsedAt = group.Max(g => g.UsedAt);
            keep.Embedding = await memories.DocumentVectorAsync(text, ct);
            var gone = group.Where(g => g.Id != keep.Id).ToList();
            foreach (var g in gone)
            {
                merged.Add(g.Id);
            }
            merged.Add(keep.Id);
            // What pointed at those let go points at the one kept.
            foreach (var o in mine.Where(o => o.ReplacedBy is { } r && gone.Any(g => g.Id == r)))
            {
                o.ReplacedBy = keep.Id;
            }
            db.Memories.RemoveRange(gone);
        }

        // Tightened: a long one, in fewer words.
        foreach (var m in active.Where(m => !merged.Contains(m.Id) && m.Text.Length > LongChars))
        {
            if (await AskAsync(services, person, TightenInstructions, m.Text, ct) is { } text && text.Length < m.Text.Length)
            {
                changes.Add(new MemoryChange { UserId = userId, Action = MemoryChange.Tightened, Reason = $"{m.Text.Length} characters", Before = Snapshot([m]), MemoryId = m.Id, After = text, At = now });
                (m.Text, m.UpdatedAt, m.Embedding) = (text, now, await memories.DocumentVectorAsync(text, ct));
            }
        }

        person.MemoryTidiedAt = now;
        db.MemoryChanges.AddRange(changes);
        await db.SaveChangesAsync(ct);
        return changes.Count;
    }

    /// <summary>Whether two memories say the same: by meaning when both have vectors, else by their words.</summary>
    private static bool Same(Memory a, Memory b, Dictionary<Guid, HashSet<string>> words) =>
        a.Embedding is { Length: > 0 } x && b.Embedding is { Length: > 0 } y && x.Length == y.Length
            ? Memories.Dot(x, y) >= Alike
            : Memories.WordScore(words[a.Id], words[b.Id]) >= WordsAlike;

    /// <summary>The small model's memory for these, checked as a memory is (null: refused, empty, or not one).</summary>
    private static async Task<string?> AskAsync(IServiceProvider services, AppUser person, string instructions, string given, CancellationToken ct)
    {
        try
        {
            var asked = await services.GetRequiredService<OneQuestion>().AskAsync(person, given, instructions, "small", 300, Tag, ct);
            if (asked.Problem is not null || asked.Text?.Trim().Trim('"') is not { Length: > 0 } text)
            {
                return null;
            }
            var (words, problem) = Memories.Check(text.StartsWith("- ", StringComparison.Ordinal) ? text[2..] : text);
            return problem is null ? words : null;
        }
        catch (Exception ex) when (ex is ChatGatewayException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>The memories as they are, for Undo: every field but the vector (written again when it is put back).</summary>
    internal static string Snapshot(IEnumerable<Memory> memories) => new JsonArray([.. memories.Select(m => (JsonNode)new JsonObject
    {
        ["id"] = m.Id, ["text"] = m.Text, ["scope"] = m.Scope, ["kind"] = m.Kind, ["project"] = m.ProjectKey, ["source"] = m.Source, ["sourceId"] = m.SourceId,
        ["pinned"] = m.Pinned, ["uses"] = m.Uses, ["usedAt"] = m.UsedAt, ["replacedBy"] = m.ReplacedBy, ["createdAt"] = m.CreatedAt, ["updatedAt"] = m.UpdatedAt,
    })]).ToJsonString();

    /// <summary>The memories a change's snapshot holds, as they were.</summary>
    internal static List<Memory> Restored(string snapshot, Guid userId) => [.. (JsonNode.Parse(snapshot) as JsonArray ?? []).OfType<JsonObject>().Select(j => new Memory
    {
        Id = j["id"]!.GetValue<Guid>(), UserId = userId, Text = j["text"]!.GetValue<string>(), Scope = j["scope"]!.GetValue<string>(), Kind = j["kind"]!.GetValue<string>(),
        ProjectKey = j["project"]?.GetValue<string>(), Source = j["source"]!.GetValue<string>(), SourceId = j["sourceId"]?.GetValue<Guid>(),
        Pinned = j["pinned"]!.GetValue<bool>(), Uses = j["uses"]!.GetValue<int>(), UsedAt = j["usedAt"]?.GetValue<DateTimeOffset>(),
        ReplacedBy = j["replacedBy"]?.GetValue<Guid>(), CreatedAt = j["createdAt"]!.GetValue<DateTimeOffset>(), UpdatedAt = j["updatedAt"]!.GetValue<DateTimeOffset>(),
    })];

    /// <summary>
    /// Puts back what a change found: each memory as it was (made again when it was let go or merged away); false when there
    /// is no such change of the person's, or it was undone already.
    /// </summary>
    public static async Task<bool> UndoAsync(AppDbContext db, Guid userId, Guid changeId, CancellationToken ct)
    {
        if (await db.MemoryChanges.SingleOrDefaultAsync(c => c.Id == changeId && c.UserId == userId && c.UndoneAt == null, ct) is not { } change)
        {
            return false;
        }
        var was = Restored(change.Before, userId);
        var ids = was.Select(m => m.Id).ToList();
        var there = await db.Memories.Where(m => m.UserId == userId && ids.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        foreach (var m in was)
        {
            if (there.TryGetValue(m.Id, out var now))
            {
                (now.Text, now.Scope, now.Kind, now.ProjectKey, now.Pinned, now.Uses, now.UsedAt, now.ReplacedBy, now.UpdatedAt) =
                    (m.Text, m.Scope, m.Kind, m.ProjectKey, m.Pinned, m.Uses, m.UsedAt, m.ReplacedBy, m.UpdatedAt);
                // Its vector is written again (MemoryUpkeep).
                now.Embedding = null;
            }
            else
            {
                db.Memories.Add(m);
            }
        }
        change.UndoneAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>A change as Memory → History shows it: what it did and why, the memories as they were, what it left, and whether it was undone.</summary>
    public static object View(MemoryChange c) => new
    {
        c.Id, c.Action, c.Reason, before = Restored(c.Before, c.UserId).Select(m => new { m.Id, m.Text, m.Kind }), c.MemoryId, c.After, c.At, c.UndoneAt,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "The memory garden failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
