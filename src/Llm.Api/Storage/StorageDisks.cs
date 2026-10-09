using System.Globalization;
using Llm.Api.Dashboards;
using Llm.Api.Models;
using Microsoft.Extensions.Options;

namespace Llm.Api.Storage;

/// <summary>A disk: the host's, as node-exporter reports it, or one only the app sees (a folder it mounts).</summary>
/// <param name="Id">"/dev/nvme0n1p2" for the host's (its device), "app:/library" for one only the app sees.</param>
/// <param name="Name">Where the host mounts it ("/", "/data"), or the folder the app sees.</param>
/// <param name="Holds">What of this stack is on it: "The model library", "Backups"…</param>
public sealed record Disk(string Id, string Name, string? Device, string? FsType, long Size, long Free, string Source, IReadOnlyList<string> Holds)
{
    public long Used => Math.Max(0, Size - Free);

    public double Percent => Size > 0 ? Math.Round(100.0 * Used / Size, 1) : 0;
}

/// <summary>How a disk filled over the last weeks: used bytes over time, and its pace over the last seven days.</summary>
/// <param name="Points">[epoch ms, used bytes].</param>
/// <param name="PerDay">Bytes a day (negative: it is emptying); null with too few points.</param>
public sealed record DiskTrend(string Id, IReadOnlyList<double?[]> Points, double? PerDay);

/// <summary>
/// The disks: the host's from node-exporter (through Prometheus), folded by device as the alert rules
/// fold them, and the app's own view of the folders it mounts (the model library, backups, Docker's
/// data behind its own root), matched to the host's disk they are on.
/// </summary>
public sealed class StorageDisks(PromDatasource prom, IOptions<StorageOptions> storage, IOptions<EngineOptions> engine, TimeProvider clock)
{
    /// <summary>As the alert rules choose: no memory or image filesystems.</summary>
    private const string Real = "fstype!~\"tmpfs|overlay|squashfs\"";

    /// <summary>The disks now, and why the host's are missing when they are.</summary>
    public async Task<(IReadOnlyList<Disk> Disks, string? Problem)> ListAsync(CancellationToken ct)
    {
        List<Disk> host = [];
        string? problem = null;
        try
        {
            host = await HostAsync(ct);
            if (host.Count == 0)
            {
                problem = "Prometheus has no disk of the host (node-exporter is not running, or not scraped yet): only what the app sees itself is shown.";
            }
        }
        catch (Exception ex) when (ex is DatasourceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            problem = "Prometheus did not answer, so the host's disks are missing: only what the app sees itself is shown.";
        }
        var disks = host.ToDictionary(d => d.Id, StringComparer.Ordinal);
        var holds = host.ToDictionary(d => d.Id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (label, path) in Mounts())
        {
            if (Space(path) is not { } space)
            {
                continue;
            }
            // The host's disk this folder is on: the same size, and the free space closest to it.
            var on = host.Where(d => Math.Abs(d.Size - space.Size) <= d.Size / 200)
                .OrderBy(d => Math.Abs(d.Free - space.Free)).FirstOrDefault();
            if (on is not null)
            {
                holds[on.Id].Add(label);
                continue;
            }
            var id = "app:" + path;
            if (disks.Values.FirstOrDefault(d => d.Source == "app" && d.Size == space.Size && Math.Abs(d.Free - space.Free) <= d.Size / 200) is { } same)
            {
                holds[same.Id].Add(label);
                continue;
            }
            disks[id] = new Disk(id, path, null, null, space.Size, space.Free, "app", []);
            holds[id] = [label];
        }
        return ([.. disks.Values.Select(d => d with { Holds = holds[d.Id] }).OrderByDescending(d => d.Percent)], problem);
    }

    /// <summary>What the app mounts that takes room, by what it is.</summary>
    private IEnumerable<(string Label, string Path)> Mounts()
    {
        yield return ("The model library", engine.Value.LibraryDir);
        yield return ("Backups", storage.Value.BackupDir);
        // The app's own root is Docker's: its images, and the named volumes (the database, Argus, Prometheus, Loki).
        yield return ("Docker's data: images and volumes (the database, Argus, Prometheus, Loki)", storage.Value.DockerDir);
    }

    /// <summary>A folder's filesystem: its size and free space, or null when it is not there.</summary>
    public static (long Size, long Free)? Space(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }
            var drive = new DriveInfo(path);
            return drive.TotalSize > 0 ? (drive.TotalSize, drive.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private async Task<List<Disk>> HostAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sizes = await prom.InstantAsync($"node_filesystem_size_bytes{{{Real}}}", now, ct);
        var free = await prom.InstantAsync($"node_filesystem_avail_bytes{{{Real}}}", now, ct);
        var freeBy = free.GroupBy(Key).ToDictionary(g => g.Key, g => g.Select(Value).Max(), StringComparer.Ordinal);
        // One disk per device: Docker Desktop shows a drive at several mount points.
        return [.. sizes.GroupBy(Key).Select(g =>
        {
            var first = g.OrderBy(s => s.Labels.GetValueOrDefault("mountpoint", "").Length).ThenBy(s => s.Labels.GetValueOrDefault("mountpoint", ""), StringComparer.Ordinal).First();
            var device = first.Labels.GetValueOrDefault("device", "");
            return new Disk(device.Length > 0 ? device : g.Key, first.Labels.GetValueOrDefault("mountpoint", device), device, first.Labels.GetValueOrDefault("fstype"),
                (long)g.Select(Value).Max(), (long)freeBy.GetValueOrDefault(g.Key), "host", []);
        }).Where(d => d.Size > 0)];
    }

    private static string Key(RawSeries s) => s.Labels.GetValueOrDefault("instance", "") + "|" + s.Labels.GetValueOrDefault("device", s.Labels.GetValueOrDefault("mountpoint", ""));

    private static double Value(RawSeries s) => s.Points.Count > 0 && s.Points[^1][1] is { } v ? v : 0;

    /// <summary>The host's disks over the last 30 days, and their pace over the last seven; empty when Prometheus has none.</summary>
    public async Task<IReadOnlyList<DiskTrend>> TrendsAsync(CancellationToken ct)
    {
        var end = clock.GetUtcNow();
        var step = TimeSpan.FromHours(3);
        var series = await prom.RangeAsync($"max by (instance, device) (node_filesystem_size_bytes{{{Real}}} - node_filesystem_avail_bytes{{{Real}}})",
            end.AddDays(-30), end, step, ct);
        return [.. series.Select(s =>
        {
            var device = s.Labels.GetValueOrDefault("device", "");
            var (perDay, _) = Pace(s.Points, end.AddDays(-7).ToUnixTimeMilliseconds());
            return new DiskTrend(device, s.Points, perDay);
        })];
    }

    /// <summary>Bytes a day by least squares over the points since <paramref name="sinceMs"/>; null with fewer than two.</summary>
    public static (double? PerDay, int Points) Pace(IReadOnlyList<double?[]> points, double sinceMs)
    {
        var xs = points.Where(p => p[0] >= sinceMs && p[1] is not null).Select(p => (X: p[0]!.Value / 86_400_000.0, Y: p[1]!.Value)).ToList();
        if (xs.Count < 2)
        {
            return (null, xs.Count);
        }
        var mx = xs.Average(p => p.X);
        var my = xs.Average(p => p.Y);
        var sxx = xs.Sum(p => (p.X - mx) * (p.X - mx));
        return sxx <= 0 ? (null, xs.Count) : (xs.Sum(p => (p.X - mx) * (p.Y - my)) / sxx, xs.Count);
    }

    /// <summary>Days until a disk is full at a pace; null when it is not filling.</summary>
    public static double? FullIn(Disk disk, double? perDay) =>
        perDay is > 0 ? Math.Round(disk.Free / perDay.Value, 1) : null;

    /// <summary>"1.5 GB", as the alerts and the audit log say sizes.</summary>
    public static string Size(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        double n = bytes;
        var i = 0;
        while (Math.Abs(n) >= 1024 && i < units.Length - 1)
        {
            n /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes:N0} bytes" : n.ToString(n >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + units[i];
    }
}
