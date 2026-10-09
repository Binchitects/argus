using System.Text.RegularExpressions;
using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Storage;

/// <summary>One thing a clean-up would remove: a person's files or chats, a file on disk, a backup, a model.</summary>
/// <param name="Id">What a run is told to take when it takes some only (a model's path).</param>
public sealed record CleanupItem(string Id, string Name, string? Person, long Bytes, DateTimeOffset? At, string? Note);

/// <summary>What a clean-up would remove now and the room it frees, what legal hold keeps, and why it cannot run when it cannot.</summary>
/// <param name="Count">Things removed: files, chats, backups, models.</param>
/// <param name="Items">The things, the largest first (the first 500).</param>
/// <param name="Days">The age it took as old, for those that take one.</param>
/// <param name="Keep">The backups it keeps, for that one.</param>
/// <param name="Command">For one the app does not run itself (old backups, which it sees read only): what does, on the host.</param>
public sealed record CleanupPlan(string Kind, long Count, long Bytes, Amount Held, IReadOnlyList<CleanupItem> Items, string? Problem, string? Warning, int? Days, int? Keep,
    string? Command = null);

/// <summary>What a clean-up removed, what legal hold kept, and what could not be removed, with why.</summary>
public sealed record CleanupDone(string Kind, long Count, long Bytes, Amount Held, IReadOnlyList<string> Failed);

public sealed class CleanupException(string message) : Exception(message);

/// <summary>
/// The storage page's clean-ups, each shown before it runs (what would go, and the room it frees) and run on
/// request, audited. Nothing of a person on legal hold is ever removed. Files of deleted chats go as retention's
/// sweep would take them; files in no chat and the tools' old pictures, videos and speech past an age; on disk,
/// downloads' parts no download owns and sandbox jobs left behind; and models nothing uses, chosen one by one.
/// Backups beyond a count (never the latest, nor the newest that ended well) are only shown: the app sees them
/// read only, as they hold every secret of the stack, and scripts/backup.sh --prune removes them on the host.
/// </summary>
public sealed partial class StorageCleanups(AppDbContext db, StorageFiles files, StoragePlaces places, Retention.Retention retention, StorageCache cache,
    IOptionsMonitor<StorageOptions> options, Audit audit, TimeProvider clock, ILogger<StorageCleanups> logger)
{
    public static readonly IReadOnlyList<string> Kinds = ["deleted-chats", "unused-files", "old-media", "disk-orphans", "old-backups", "unused-models"];

    /// <summary>A file nobody uses yet may be one being written: younger than this it is never a leftover.</summary>
    public const int DefaultUnusedDays = 7;

    private const int Shown = 500;

    /// <summary>What a clean-up would remove now; null for one that does not exist.</summary>
    public async Task<CleanupPlan?> PlanAsync(string kind, int? days, int? keep, CancellationToken ct) => kind switch
    {
        "deleted-chats" => await DeletedChatsAsync(ct),
        "unused-files" => await FilesAsync(kind, Days(days, DefaultUnusedDays), new FileFilter(States: ["none"]), null, ct),
        "old-media" => await FilesAsync(kind, Days(days, options.CurrentValue.MediaDays), new FileFilter(Origins: ["picture", "video", "speech"], States: ["chat", "deleted", "none"]),
            "The chats keep their words; the pictures, videos and sound files are gone from them.", ct),
        "disk-orphans" => await OrphansAsync(ct),
        "old-backups" => await BackupsAsync(Keep(keep), ct),
        "unused-models" => await ModelsAsync(ct),
        _ => null,
    };

    /// <summary>Runs a clean-up as its plan says now. <paramref name="only"/>: the models chosen (that one needs them).</summary>
    public async Task<CleanupDone?> RunAsync(string kind, int? days, int? keep, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        if (await PlanAsync(kind, days, keep, ct) is not { } plan)
        {
            return null;
        }
        if (plan.Problem is { } problem)
        {
            throw new CleanupException(problem);
        }
        var done = kind switch
        {
            "deleted-chats" => await EraseHiddenAsync(ct),
            "unused-files" or "old-media" => await DeleteFilesAsync(kind, plan, ct),
            "disk-orphans" => await RemoveOrphansAsync(plan, ct),
            "old-backups" => throw new CleanupException($"The app sees the backups read only and never deletes one. On the host, in deploy/: {plan.Command}"),
            _ => await RemoveModelsAsync(plan, only ?? [], ct),
        };
        cache.Forget();
        return done;
    }

    private static int Days(int? days, int fallback) => Math.Clamp(days ?? fallback, 1, 36500);

    private int Keep(int? keep) => Math.Clamp(keep ?? options.CurrentValue.BackupsKept, 1, 1000);

    private static string Of(Amount a, string one, string many) => $"{a.Count} {(a.Count == 1 ? one : many)} ({StorageDisks.Size(a.Bytes)})";

    // ------------------------------------------------------------------------------- the chat's files --

    /// <summary>Chats deleted while their owner was on legal hold: kept while the hold lasts, theirs to go once it ended.</summary>
    private async Task<CleanupPlan> DeletedChatsAsync(CancellationToken ct)
    {
        var hidden = await db.Conversations.IgnoreQueryFilters().AsNoTracking().Where(c => c.DeletedAt != null)
            .Join(db.Users, c => c.UserId, u => u.Id, (c, u) => new { c.UserId, u.UserName, u.DisplayName, Held = u.LegalHoldSince != null, c.DeletedAt })
            .ToListAsync(ct);
        var (rows, _) = await files.ListAsync(new FileFilter(States: ["deleted"], Take: 100_000), ct);
        var held = rows.Where(r => r.Held).Aggregate(Amount.None, (a, r) => a.Plus(new Amount(1, r.Bytes)));
        var bytesOf = rows.Where(r => !r.Held).GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => (Count: g.LongCount(), Bytes: g.Sum(r => r.Bytes)));
        var items = hidden.Where(c => !c.Held).GroupBy(c => c.UserId).Select(g =>
        {
            var first = g.First();
            var theirs = bytesOf.GetValueOrDefault(g.Key);
            return new CleanupItem(g.Key.ToString(), $"{g.Count()} chat{(g.Count() == 1 ? "" : "s")} and {theirs.Count} file{(theirs.Count == 1 ? "" : "s")}",
                first.DisplayName is { Length: > 0 } d ? d : first.UserName, theirs.Bytes, g.Max(c => c.DeletedAt), "Deleted while on legal hold; the hold has ended");
        }).OrderByDescending(i => i.Bytes).ToList();
        var heldChats = hidden.Count(c => c.Held);
        return new CleanupPlan("deleted-chats", hidden.Count(c => !c.Held), items.Sum(i => i.Bytes), held, [.. items.Take(Shown)], null,
            heldChats > 0 ? $"{heldChats} chat{(heldChats == 1 ? "" : "s")} deleted by people still on legal hold stay, with their files, until the hold ends." : null, null, null);
    }

    private async Task<CleanupDone> EraseHiddenAsync(CancellationToken ct)
    {
        var people = await db.Users.AsNoTracking().Where(u => u.LegalHoldSince == null &&
            db.Conversations.IgnoreQueryFilters().Any(c => c.UserId == u.Id && c.DeletedAt != null)).ToListAsync(ct);
        var (before, _) = await files.ListAsync(new FileFilter(States: ["deleted"], Take: 100_000), ct);
        var total = Retention.Erased.None;
        foreach (var person in people)
        {
            var hidden = await db.Conversations.IgnoreQueryFilters().Where(c => c.UserId == person.Id && c.DeletedAt != null).Select(c => c.Id).ToListAsync(ct);
            var erased = await retention.EraseAsync(person.Id, hidden, ct);
            if (erased.Chats > 0)
            {
                await audit.WriteAsync("storage.cleanup", person.UserName, detail: $"deleted-chats: {erased.Chats} chats and {erased.Files} files they deleted while on legal hold");
            }
            total = total.Plus(erased);
        }
        var bytes = before.Where(r => people.Any(p => p.Id == r.UserId)).Sum(r => r.Bytes);
        var held = before.Where(r => r.Held).Aggregate(Amount.None, (a, r) => a.Plus(new Amount(1, r.Bytes)));
        LogRan(logger, "deleted-chats", total.Chats, bytes);
        return new CleanupDone("deleted-chats", total.Chats, bytes, held, []);
    }

    private async Task<CleanupPlan> FilesAsync(string kind, int days, FileFilter filter, string? warning, CancellationToken ct)
    {
        var (rows, _) = await files.ListAsync(filter with { Before = clock.GetUtcNow().AddDays(-days), Take = 100_000 }, ct);
        var held = rows.Where(r => r.Held).Aggregate(Amount.None, (a, r) => a.Plus(new Amount(1, r.Bytes)));
        var go = rows.Where(r => !r.Held).ToList();
        return new CleanupPlan(kind, go.Count, go.Sum(r => r.Bytes), held,
            [.. go.Take(Shown).Select(r => new CleanupItem(r.Id.ToString(), r.Name, r.Person, r.Bytes, r.CreatedAt, r.ChatTitle))], null, warning, days, null);
    }

    private async Task<CleanupDone> DeleteFilesAsync(string kind, CleanupPlan plan, CancellationToken ct)
    {
        var (deleted, held) = await files.DeleteAsync([.. await AllAsync(kind, plan.Days, ct)], ct);
        var people = deleted.Keys.ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
        foreach (var (person, amount) in deleted)
        {
            await audit.WriteAsync("storage.cleanup", names.GetValueOrDefault(person), detail: $"{kind}: {Of(amount, "file", "files")}" + (plan.Days is { } d ? $" older than {d} days" : ""));
        }
        var total = deleted.Values.Aggregate(Amount.None, (a, b) => a.Plus(b));
        LogRan(logger, kind, total.Count, total.Bytes);
        return new CleanupDone(kind, total.Count, total.Bytes, held.Plus(plan.Held), []);
    }

    /// <summary>Every file a files clean-up takes (its plan shows the first ones only).</summary>
    private async Task<IEnumerable<Guid>> AllAsync(string kind, int? days, CancellationToken ct)
    {
        var filter = kind == "old-media" ? new FileFilter(Origins: ["picture", "video", "speech"], States: ["chat", "deleted", "none"]) : new FileFilter(States: ["none"]);
        var (rows, _) = await files.ListAsync(filter with { Before = clock.GetUtcNow().AddDays(-(days ?? DefaultUnusedDays)), Take = 100_000 }, ct);
        return rows.Where(r => !r.Held).Select(r => r.Id);
    }

    // ------------------------------------------------------------------------------------- on disk --

    private async Task<CleanupPlan> OrphansAsync(CancellationToken ct)
    {
        var left = await places.LeftoversAsync(ct);
        return new CleanupPlan("disk-orphans", left.Count, left.Sum(l => l.Bytes), Amount.None,
            [.. left.OrderByDescending(l => l.Bytes).Take(Shown).Select(l => new CleanupItem(l.Path, Relative(l.Path), null, l.Bytes, l.Modified, l.What))], null, null, null, null);
    }

    private string Relative(string path)
    {
        var library = Path.GetFullPath(places.LibraryDir).TrimEnd('/') + "/";
        return path.StartsWith(library, StringComparison.Ordinal) ? "MODELS_DIR/" + path[library.Length..] : path;
    }

    private async Task<CleanupDone> RemoveOrphansAsync(CleanupPlan plan, CancellationToken ct)
    {
        var failed = new List<string>();
        var done = Amount.None;
        foreach (var item in await places.LeftoversAsync(ct))
        {
            if (!places.MayRemove(item.Path))
            {
                continue;
            }
            try
            {
                if (Directory.Exists(item.Path))
                {
                    Directory.Delete(item.Path, recursive: true);
                }
                else
                {
                    File.Delete(item.Path);
                }
                done = done.Plus(new Amount(1, item.Bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{Relative(item.Path)}: {ex.Message}");
            }
        }
        await audit.WriteAsync("storage.cleanup", "disk-orphans", detail: $"{Of(done, "leftover", "leftovers")} removed" + (failed.Count > 0 ? $"; {failed.Count} could not be" : ""));
        LogRan(logger, "disk-orphans", done.Count, done.Bytes);
        return new CleanupDone("disk-orphans", done.Count, done.Bytes, Amount.None, failed);
    }

    /// <summary>
    /// The backups beyond the newest <paramref name="keep"/>, with the command that removes them. Backups hold the data of
    /// people on legal hold from before their hold (what they deleted since, too): with anyone on hold, it says so, and
    /// marks the backups taken before the earliest hold began.
    /// </summary>
    private async Task<CleanupPlan> BackupsAsync(int keep, CancellationToken ct)
    {
        var report = places.Backups();
        var problem = report.State switch
        {
            "missing" => $"The app does not see the backups: {report.Dir} is not mounted (BACKUP_DIR, in the app's volumes).",
            "unreadable" => $"The app may not read the backups: {report.Dir} belongs to another user (scripts/backup.sh ran as root?).",
            _ => null,
        };
        var holds = await db.Users.AsNoTracking().Where(u => u.LegalHoldSince != null).Select(u => u.LegalHoldSince!.Value).ToListAsync(ct);
        DateTimeOffset? since = holds.Count > 0 ? holds.Min() : null;
        // A backup from before scripts/backup.sh wrote its start in UTC has only its name, the host's local time in a zone
        // the app does not know: one named up to 14 hours after the hold began may be from before it (no zone is further
        // ahead of UTC), and counts as before.
        bool Before(Backup b) => since is { } s
            && (b.At ?? (StoragePlaces.Named(b.Name) is { } named ? new DateTimeOffset(named, TimeSpan.Zero) - TimeSpan.FromHours(14) : null)) < s;
        var beyond = StoragePlaces.Beyond(report, keep);
        var warnings = new List<string>();
        if (report.Backups.Count > 0)
        {
            warnings.Add($"The newest {keep} stay, and always the latest one and the newest that ended well.");
        }
        if (since is { } start)
        {
            var taken = beyond.Count(Before);
            warnings.Add($"{holds.Count} {(holds.Count == 1 ? "person is" : "people are")} on legal hold, the first since {start:yyyy-MM-dd}: a backup taken before then holds their data " +
                "from before the hold, what they deleted since too, and may be its only copy." +
                (taken > 0 ? $" {taken} of these {(taken == 1 ? "is" : "are")} from before it: ask whoever placed the hold before removing {(taken == 1 ? "it" : "them")}." : ""));
        }
        return new CleanupPlan("old-backups", beyond.Count, beyond.Sum(b => b.Bytes), Amount.None,
            [.. beyond.Select(b => new CleanupItem(b.Name, b.Name, null, b.Bytes, b.At,
                string.Join(" · ", new[] { b.Result, Before(b) ? "taken before a legal hold began" : null }.OfType<string>()) is { Length: > 0 } note ? note : null))],
            problem, warnings.Count > 0 ? string.Join(" ", warnings) : null, null, keep, $"scripts/backup.sh --prune --keep {keep}");
    }

    private async Task<CleanupPlan> ModelsAsync(CancellationToken ct)
    {
        var library = await places.LibraryAsync(ct);
        if (!library.Exists)
        {
            return new CleanupPlan("unused-models", 0, 0, Amount.None, [], $"The model library ({library.Dir}) is not there.", null, null, null);
        }
        var unused = library.Files.Where(f => f.Use == "unused").ToList();
        return new CleanupPlan("unused-models", unused.Count, unused.Sum(f => f.Bytes), Amount.None,
            [.. unused.Select(f => new CleanupItem(f.Path, f.Path, null, f.Bytes, null, f.Note ?? f.Kind))], null,
            "No model of Admin → Models uses these, and no server reads them. Deleted, a model is gone from the library: using it again means downloading it again.",
            null, null);
    }

    private async Task<CleanupDone> RemoveModelsAsync(CleanupPlan plan, IReadOnlyCollection<string> only, CancellationToken ct)
    {
        if (only.Count == 0)
        {
            throw new CleanupException("Choose the models to delete.");
        }
        var chosen = plan.Items.Where(i => only.Contains(i.Id)).ToList();
        if (chosen.Count < only.Count)
        {
            throw new CleanupException("A model chosen is in use now, or not in the library: look again.");
        }
        var root = Path.GetFullPath(places.LibraryDir);
        var failed = new List<string>();
        var done = new List<CleanupItem>();
        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in chosen)
        {
            var full = Path.GetFullPath(Path.Combine(root, item.Id));
            if (!full.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                continue;
            }
            // A split model: every part of it.
            var parts = SplitPart().Match(full) is { Success: true } m
                ? Directory.GetFiles(Path.GetDirectoryName(full)!, Path.GetFileName(full).Replace($"-{m.Groups[1].Value}-of-", "-*-of-", StringComparison.Ordinal))
                : [full];
            try
            {
                foreach (var part in parts)
                {
                    File.Delete(part);
                    gone.Add(part);
                }
                done.Add(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{item.Name}: {ex.Message}");
            }
        }
        // A finished download whose files are all gone would offer "Add as a model" for nothing.
        var finished = await db.ModelDownloads.Where(d => d.State == "done").ToListAsync(ct);
        foreach (var d in finished)
        {
            var paths = d.Files.Select(f => Path.GetFullPath(Path.Combine(root, d.Dir, f.Target ?? f.Path))).ToList();
            if (paths.Any(gone.Contains) && paths.All(p => !File.Exists(p)))
            {
                db.ModelDownloads.Remove(d);
            }
        }
        await db.SaveChangesAsync(ct);
        var amount = new Amount(done.Count, done.Sum(d => d.Bytes));
        await audit.WriteAsync("storage.cleanup", "unused-models",
            detail: $"{Of(amount, "model", "models")} deleted from the library: {string.Join(", ", done.Select(d => d.Name))}" + (failed.Count > 0 ? $"; {failed.Count} could not be" : ""));
        LogRan(logger, "unused-models", amount.Count, amount.Bytes);
        return new CleanupDone("unused-models", amount.Count, amount.Bytes, Amount.None, failed);
    }

    [GeneratedRegex(@"-(\d{5})-of-\d{5}\.gguf$")]
    private static partial Regex SplitPart();

    [LoggerMessage(Level = LogLevel.Information, Message = "Storage: the {Kind} clean-up removed {Count} things ({Bytes} bytes)")]
    private static partial void LogRan(ILogger logger, string kind, long count, long bytes);
}
