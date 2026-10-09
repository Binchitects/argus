using System.Text.Json.Nodes;
using Argus.Configuration;

namespace Argus.Server;

public static partial class ArgusServer
{
    static readonly TimeSpan StorageFresh = TimeSpan.FromMinutes(10);
    static readonly Lock StorageLock = new();
    static (DateTimeOffset At, JsonObject Report)? _storage;

    /// <summary>
    /// What Argus keeps on its disk, for the app's storage page: the index database (with its write-ahead log),
    /// the GitLab mirrors, the checked-out trees, the packs (a pack loaded from the library is a link: only its
    /// own copies count), the rest, and the disk's size and free space. Measured at most every ten minutes.
    /// </summary>
    static void MapStorage(WebApplication app, ArgusConfig cfg)
    {
        app.MapGet(AdminPrefix + "storage", (HttpRequest request) =>
        {
            if (!Authorised(request)) return Forbidden();
            try
            {
                return Json(StorageReport(cfg, request.Query.ContainsKey("fresh")));
            }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
            {
                return Json(new JsonObject { ["error"] = Short(exc) }, 500);
            }
        });
    }

    static JsonObject StorageReport(ArgusConfig cfg, bool fresh)
    {
        lock (StorageLock)
        {
            if (!fresh && _storage is { } known && DateTimeOffset.UtcNow - known.At < StorageFresh && known.Report["data_dir"]?.GetValue<string>() == cfg.Index.DataDir)
                return (JsonObject)known.Report.DeepClone();
        }
        var data = cfg.Index.DataDir;
        var db = cfg.Index.DbPath;
        long index = new[] { db, db + "-wal", db + "-shm" }.Sum(f => File.Exists(f) ? new FileInfo(f).Length : 0);
        var mirrors = Bytes(cfg.Index.MirrorsDir);
        var trees = Bytes(cfg.Index.TreesDir);
        var packs = Bytes(cfg.PacksDir);
        var all = Bytes(data);
        // The packs folder may be outside the data folder (packs.dir): then it is not part of "all".
        var inData = Path.GetFullPath(cfg.PacksDir).StartsWith(Path.GetFullPath(data).TrimEnd('/') + "/", StringComparison.Ordinal);
        var report = new JsonObject
        {
            ["data_dir"] = data,
            ["index_bytes"] = index,
            ["mirrors_bytes"] = mirrors,
            ["trees_bytes"] = trees,
            ["packs_bytes"] = packs,
            ["other_bytes"] = Math.Max(0, all - index - mirrors - trees - (inData ? packs : 0)),
            ["library_dir"] = cfg.PackLibrary,
            ["library_bytes"] = cfg.PackLibrary is { Length: > 0 } lib && Directory.Exists(lib) ? Bytes(lib) : null,
            ["measured_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        try
        {
            var drive = new DriveInfo(Directory.Exists(data) ? data : "/");
            report["disk"] = new JsonObject { ["size_bytes"] = drive.TotalSize, ["free_bytes"] = drive.AvailableFreeSpace };
        }
        catch (Exception exc) when (exc is IOException or UnauthorizedAccessException or ArgumentException)
        {
            report["disk"] = null;
        }
        lock (StorageLock)
        {
            _storage = (DateTimeOffset.UtcNow, (JsonObject)report.DeepClone());
        }
        return report;
    }

    /// <summary>The bytes of the files under a folder, links not followed (a loaded pack is a link to the library).</summary>
    static long Bytes(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            var here = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(here).EnumerateFileSystemInfos(); }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                try
                {
                    if (e.LinkTarget is not null) continue;
                    if (e is DirectoryInfo d) pending.Push(d.FullName);
                    else if (e is FileInfo f) total += f.Length;
                }
                catch (Exception exc) when (exc is IOException or UnauthorizedAccessException)
                {
                    // Gone or unreadable meanwhile: not counted.
                }
            }
        }
        return total;
    }
}
