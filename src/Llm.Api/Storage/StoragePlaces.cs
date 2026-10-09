using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Llm.Api.Models;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Storage;

/// <summary>A file of the model library and what uses it.</summary>
/// <param name="Path">Relative to the library; a split model by its first part.</param>
/// <param name="Use">engine (a model of Admin → Models), pictures, video, embeddings, laya (the servers'), or unused.</param>
/// <param name="Models">The engine's models that use it, with their state ("Qwen (loaded, kept)").</param>
public sealed record LibraryUse(string Path, long Bytes, int Parts, string Kind, string Use, IReadOnlyList<string> Models, string? Note);

/// <summary>A download's file not yet whole (name.part), and the download it belongs to (null: none, it is left over).</summary>
public sealed record PartialFile(string Path, long Bytes, DateTimeOffset Modified, string? Download);

/// <summary>The model library: its files by what uses them, its folders, and downloads cut short.</summary>
public sealed record LibraryReport(string Dir, bool Exists, long Bytes, IReadOnlyList<LibraryUse> Files, IReadOnlyList<FolderSize> Folders, IReadOnlyList<PartialFile> Partials);

/// <summary>A folder and what it takes.</summary>
public sealed record FolderSize(string Name, long Bytes);

/// <summary>A backup (one folder of scripts/backup.sh) and how it ended.</summary>
/// <param name="At">When it was taken, as the host's clock named it.</param>
public sealed record Backup(string Name, long Bytes, DateTime? At, string? Result, bool Latest);

/// <summary>The backups folder as the app sees it (read only).</summary>
/// <param name="State">ok, missing (not mounted) or unreadable (another user's).</param>
/// <param name="OtherBytes">What else is in the folder (not taken by backup.sh, never touched).</param>
public sealed record BackupReport(string Dir, string State, long Bytes, long OtherBytes, IReadOnlyList<Backup> Backups);

/// <summary>Something on disk no database row or running job owns: a download's part nobody resumes, a sandbox job left behind.</summary>
public sealed record Leftover(string Path, long Bytes, DateTimeOffset Modified, string What);

/// <summary>Sizes of folders, measured at most every two minutes (walking the library and the backups is not free).</summary>
public sealed class StorageCache(TimeProvider clock)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, long Bytes)> _sizes = new(StringComparer.Ordinal);

    /// <summary>The bytes of the files under a folder, links not followed.</summary>
    public long Bytes(string dir)
    {
        if (_sizes.TryGetValue(dir, out var known) && clock.GetUtcNow() - known.At < Fresh)
        {
            return known.Bytes;
        }
        var bytes = Measure(dir);
        _sizes[dir] = (clock.GetUtcNow(), bytes);
        return bytes;
    }

    /// <summary>Measured again at the next look (after a clean-up).</summary>
    public void Forget() => _sizes.Clear();

    public static long Measure(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return File.Exists(dir) ? new FileInfo(dir).Length : 0;
        }
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = [.. new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var e in entries)
            {
                try
                {
                    if (e.LinkTarget is not null)
                    {
                        continue;
                    }
                    if (e is DirectoryInfo d)
                    {
                        pending.Push(d.FullName);
                    }
                    else if (e is FileInfo f)
                    {
                        total += f.Length;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Gone or unreadable meanwhile: not counted.
                }
            }
        }
        return total;
    }
}

/// <summary>
/// What the app keeps on disk, or sees there: the model library (MODELS_DIR) and what uses each model,
/// downloads cut short, the sandbox's jobs left behind, and the backups (BACKUP_DIR, mounted at /backups).
/// </summary>
public sealed partial class StoragePlaces(AppDbContext db, ModelLibrary library, ModelDownloads downloads, EngineState engine, IServiceProvider services,
    IOptions<EngineOptions> engineOptions, IOptions<StorageOptions> options, IOptionsMonitor<Chat.Tools.SandboxOptions> sandbox, StorageCache cache, TimeProvider clock)
{
    /// <summary>A sandbox job older than this is left behind (the sandbox itself clears them after 15 minutes while it runs).</summary>
    public static readonly TimeSpan JobLeftAfter = TimeSpan.FromHours(1);

    private static readonly (string Dir, string Use)[] ServerDirs =
    [
        (MediaModels.ImageDir, "pictures"), (MediaModels.VideoDir, "video"), (MediaModels.EmbedDir, "embeddings"), (MediaModels.LayaDir, "laya"),
    ];

    public string LibraryDir => engineOptions.Value.LibraryDir;

    public async Task<LibraryReport> LibraryAsync(CancellationToken ct)
    {
        var root = LibraryDir;
        if (!Directory.Exists(root))
        {
            return new LibraryReport(root, false, 0, [], [], []);
        }
        var models = await db.LocalModels.AsNoTracking().Select(m => new { m.Name, m.File, m.Projector, m.DraftHead }).ToListAsync(ct);
        var kept = Kept();
        string Named(string name)
        {
            var state = engine.StatusOf(name);
            var parts = new[] { state is "loaded" or "loading" ? state : null, kept.Contains(name) ? "kept loaded" : null }.OfType<string>().ToList();
            return parts.Count > 0 ? $"{name} ({string.Join(", ", parts)})" : name;
        }
        var finished = await db.ModelDownloads.AsNoTracking().Where(d => d.State == "done").ToListAsync(ct);
        var files = new List<LibraryUse>();
        foreach (var e in await Task.Run(library.List, ct))
        {
            var path = e.File.Path;
            var users = models.Where(m => m.File == path).Select(m => Named(m.Name))
                .Concat(models.Where(m => m.Projector == path).Select(m => $"{Named(m.Name)}: its vision projector"))
                .Concat(models.Where(m => m.DraftHead == path).Select(m => $"{Named(m.Name)}: its draft head")).ToList();
            var server = ServerDirs.FirstOrDefault(s => path.StartsWith(s.Dir.TrimEnd('/') + "/", StringComparison.Ordinal)).Use;
            var use = users.Count > 0 ? "engine" : server ?? "unused";
            string? note = null;
            if (use == "unused" && finished.FirstOrDefault(d => d.Files.Any(f => downloads.PathOf(d, f) is { } p && p == Path.Combine(Path.GetFullPath(root), path))) is { } got)
            {
                note = $"Downloaded {got.FinishedAt ?? got.CreatedAt:yyyy-MM-dd} from {got.Repo}, not added as a model (Admin → Models → Add a model).";
            }
            files.Add(new LibraryUse(path, e.File.Size, e.File.Parts, e.Profile.Kind, use, users, note ?? e.Profile.Note));
        }
        var folders = new List<FolderSize>();
        long loose = 0;
        foreach (var entry in SafeEntries(root))
        {
            if (entry is DirectoryInfo d && d.LinkTarget is null)
            {
                folders.Add(new FolderSize(d.Name, cache.Bytes(d.FullName)));
            }
            else if (entry is FileInfo f && f.LinkTarget is null)
            {
                loose += f.Length;
            }
        }
        if (loose > 0)
        {
            folders.Add(new FolderSize(".", loose));
        }
        return new LibraryReport(root, true, folders.Sum(f => f.Bytes), [.. files.OrderByDescending(f => f.Bytes)], [.. folders.OrderByDescending(f => f.Bytes)],
            await PartialsAsync(ct));
    }

    /// <summary>The models kept loaded now (pinned, or by working hours).</summary>
    private HashSet<string> Kept()
    {
        try
        {
            return [.. services.GetRequiredService<ModelCatalog>().Kept()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Downloads' parts (name.part) under the library, each with the download it belongs to, if any.</summary>
    public async Task<IReadOnlyList<PartialFile>> PartialsAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath(LibraryDir);
        if (!Directory.Exists(root))
        {
            return [];
        }
        var open = await db.ModelDownloads.AsNoTracking().Where(d => d.State != "done").ToListAsync(ct);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in open)
        {
            foreach (var f in d.Files)
            {
                if (downloads.PathOf(d, f) is { } p)
                {
                    owners[p + ".part"] = $"{d.Repo} ({d.State})";
                }
            }
        }
        return [.. Find(root, "*.part", 5).Select(path =>
        {
            var info = new FileInfo(path);
            return new PartialFile(Path.GetRelativePath(root, path), info.Length, info.LastWriteTimeUtc, owners.GetValueOrDefault(path));
        }).OrderByDescending(p => p.Bytes)];
    }

    /// <summary>What no row and no running job owns: downloads' parts no download resumes, and sandbox jobs left behind.</summary>
    public async Task<IReadOnlyList<Leftover>> LeftoversAsync(CancellationToken ct)
    {
        var root = Path.GetFullPath(LibraryDir);
        var left = (await PartialsAsync(ct)).Where(p => p.Download is null)
            .Select(p => new Leftover(Path.Combine(root, p.Path), p.Bytes, p.Modified, "A download's part with no download to finish it")).ToList();
        var jobs = sandbox.CurrentValue.Dir;
        var before = clock.GetUtcNow() - JobLeftAfter;
        foreach (var sub in new[] { "in", "out", "cancel" })
        {
            foreach (var e in SafeEntries(Path.Combine(jobs, sub)))
            {
                if (e.LastWriteTimeUtc < before)
                {
                    left.Add(new Leftover(e.FullName, e is DirectoryInfo ? StorageCache.Measure(e.FullName) : ((FileInfo)e).Length, e.LastWriteTimeUtc,
                        "A Python sandbox job left behind"));
                }
            }
        }
        return left;
    }

    /// <summary>Whether a path is one <see cref="LeftoversAsync"/> may name: under the library or the sandbox's jobs.</summary>
    public bool MayRemove(string path)
    {
        var full = Path.GetFullPath(path);
        var library = Path.GetFullPath(LibraryDir).TrimEnd('/') + "/";
        var jobs = Path.GetFullPath(sandbox.CurrentValue.Dir).TrimEnd('/') + "/";
        return (full.StartsWith(library, StringComparison.Ordinal) && full.EndsWith(".part", StringComparison.Ordinal))
            || (full.StartsWith(jobs, StringComparison.Ordinal) && full[jobs.Length..].Split('/') is [ "in" or "out" or "cancel", { Length: > 0 }]);
    }

    public BackupReport Backups()
    {
        var dir = options.Value.BackupDir;
        if (!Directory.Exists(dir))
        {
            return new BackupReport(dir, "missing", 0, 0, []);
        }
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(dir).EnumerateFileSystemInfos()];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new BackupReport(dir, "unreadable", 0, 0, []);
        }
        var latest = entries.FirstOrDefault(e => e.Name == "latest")?.LinkTarget is { } target ? Path.GetFileName(target.TrimEnd('/')) : null;
        var backups = new List<Backup>();
        long other = 0;
        foreach (var e in entries)
        {
            if (e.LinkTarget is not null)
            {
                continue;
            }
            if (e is DirectoryInfo d && BackupName().IsMatch(d.Name))
            {
                backups.Add(new Backup(d.Name, cache.Bytes(d.FullName), At(d.Name), Result(d.FullName), d.Name == latest));
            }
            else
            {
                other += e is DirectoryInfo od ? cache.Bytes(od.FullName) : ((FileInfo)e).Length;
            }
        }
        backups.Sort((a, b) => string.CompareOrdinal(b.Name, a.Name));
        return new BackupReport(dir, "ok", backups.Sum(b => b.Bytes), other, backups);
    }

    /// <summary>The backups beyond the newest <paramref name="keep"/>, never the latest one nor the newest that ended well.</summary>
    public static IReadOnlyList<Backup> Beyond(BackupReport report, int keep)
    {
        var newestOk = report.Backups.FirstOrDefault(b => b.Result == "ok")?.Name;
        return [.. report.Backups.Skip(Math.Max(1, keep)).Where(b => !b.Latest && b.Name != newestOk)];
    }

    private static string? Result(string dir)
    {
        try
        {
            var path = Path.Combine(dir, "RESULT");
            return File.Exists(path) ? File.ReadLines(path).FirstOrDefault()?.Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? At(string name) =>
        DateTime.TryParseExact(name, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;

    private static IEnumerable<FileSystemInfo> SafeEntries(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? [.. new DirectoryInfo(dir).EnumerateFileSystemInfos()] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> Find(string dir, string pattern, int depth)
    {
        string[] here;
        string[] subdirs;
        try
        {
            here = Directory.GetFiles(dir, pattern);
            subdirs = depth > 0 ? [.. Directory.GetDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.') && new DirectoryInfo(d).LinkTarget is null)] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var f in here)
        {
            yield return f;
        }
        foreach (var d in subdirs)
        {
            foreach (var f in Find(d, pattern, depth - 1))
            {
                yield return f;
            }
        }
    }

    [GeneratedRegex(@"^20\d\d-\d\d-\d\d_\d{6}$")]
    private static partial Regex BackupName();
}
