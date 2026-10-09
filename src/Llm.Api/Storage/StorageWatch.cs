using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using Llm.Api.Dashboards;
using Llm.Api.Identity;
using Llm.Api.Notifications;
using Llm.Api.Operations;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Storage;

/// <summary>
/// Looks at the disks every five minutes and measures what takes room every six hours, on the replica that leads.
/// A disk fuller than Settings → Storage allows is an alert like the stack's own: given to Alertmanager (so it
/// fires on the Alerts page, reaches the bell, email and the alerts webhook once, and Alertmanager's own
/// receivers), resent while it lasts and ended when the disk is below again. With Alertmanager away, the admins
/// are told directly, once each time the disk passes the share. The measures are one row a day for each thing:
/// the storage page's trends.
/// </summary>
/// <remarks>Notifications:Watch=false turns the looking off (tests, which call the checks themselves).</remarks>
public sealed partial class StorageWatch(IServiceScopeFactory scopes, IConfiguration config, TimeProvider clock, Replicas replicas,
    IOptionsMonitor<StorageOptions> options, IOptions<AuthOptions> auth, ILogger<StorageWatch> logger) : BackgroundService
{
    public const string AlertName = "DiskAboveThreshold";
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan SampleEvery = TimeSpan.FromHours(6);

    /// <summary>Samples older than this are dropped.</summary>
    public const int KeepDays = 400;

    /// <summary>What Argus keeps besides its packs: its index, mirrors, trees and the rest.</summary>
    private static readonly string[] ArgusParts = ["index_bytes", "mirrors_bytes", "trees_bytes", "other_bytes"];

    /// <summary>Each disk past the share now: since when, and the labels its alert has.</summary>
    private readonly ConcurrentDictionary<string, (DateTimeOffset Since, Dictionary<string, string> Labels)> _above = new(StringComparer.Ordinal);

    /// <summary>The disks past the share at the last look, with since when.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Above => _above.ToDictionary(a => a.Key, a => a.Value.Since, StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notifications:Watch", true))
        {
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), clock, stoppingToken);
            var sampled = DateTimeOffset.MinValue;
            using var timer = new PeriodicTimer(CheckEvery, clock);
            do
            {
                if (!replicas.IsLeader)
                {
                    continue;
                }
                await CheckAsync(stoppingToken);
                if (clock.GetUtcNow() - sampled >= SampleEvery)
                {
                    await SampleAsync(stoppingToken);
                    sampled = clock.GetUtcNow();
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>The disks against the share: alerts raised, kept up and ended.</summary>
    public async Task CheckAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var (disks, _) = await scope.ServiceProvider.GetRequiredService<StorageDisks>().ListAsync(ct);
            var threshold = options.CurrentValue.AlertPercent;
            var now = clock.GetUtcNow();
            var alerts = new JsonArray();
            var news = new List<(Disk Disk, DateTimeOffset Since)>();
            foreach (var d in disks.Where(d => d.Percent >= threshold))
            {
                var labels = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["alertname"] = AlertName, ["severity"] = "warning", ["component"] = "storage", ["source"] = "app",
                    ["device"] = d.Device ?? d.Id, ["mountpoint"] = d.Name,
                };
                var known = _above.TryGetValue(d.Id, out var was);
                var since = known ? was.Since : now;
                _above[d.Id] = (since, labels);
                if (!known)
                {
                    news.Add((d, since));
                }
                alerts.Add(Alert(labels, Summary(d), Description(d, threshold), since, now + CheckEvery * 3));
            }
            // Below the share again (or the share was raised): their alerts end.
            foreach (var id in _above.Keys.Where(id => !disks.Any(d => d.Id == id && d.Percent >= threshold)).ToList())
            {
                if (_above.TryRemove(id, out var gone))
                {
                    alerts.Add(Alert(gone.Labels, null, null, gone.Since, now));
                }
            }
            if (alerts.Count == 0)
            {
                return;
            }
            try
            {
                await scope.ServiceProvider.GetRequiredService<AlertmanagerClient>().PushAsync(alerts, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DatasourceException && !ct.IsCancellationRequested)
            {
                // Alertmanager is away: the admins are told here, once each time a disk passes the share.
                LogNoAlertmanager(logger, ex.Message);
                var delivery = scope.ServiceProvider.GetRequiredService<NewsDelivery>();
                foreach (var (d, since) in news)
                {
                    await delivery.ToAdminsAsync(new News("alert", "Warning: " + Summary(d), Description(d, threshold), "/admin/storage",
                        $"storage:{d.Id}:{threshold}:{since.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)}"), ct);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DatasourceException or DbUpdateException or Npgsql.NpgsqlException
            or IOException && !ct.IsCancellationRequested)
        {
            LogSkipped(logger, "the disks", ex.Message);
        }
    }

    private static string Summary(Disk d) => $"Disk {d.Name} is {d.Percent.ToString("0", CultureInfo.InvariantCulture)}% full";

    private static string Description(Disk d, int threshold) =>
        $"{StorageDisks.Size(d.Free)} free of {StorageDisks.Size(d.Size)}; the app warns above {threshold}% (Settings → Storage)." +
        (d.Holds.Count > 0 ? $" On it: {string.Join("; ", d.Holds)}." : "") +
        " Admin → Storage shows what takes the room, and its clean-ups.";

    private JsonObject Alert(Dictionary<string, string> labels, string? summary, string? description, DateTimeOffset starts, DateTimeOffset ends)
    {
        var alert = new JsonObject
        {
            ["labels"] = new JsonObject([.. labels.Select(l => KeyValuePair.Create(l.Key, (JsonNode?)l.Value))]),
            ["startsAt"] = starts.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["endsAt"] = ends.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            ["generatorURL"] = auth.Value.Origin + "/admin/storage",
        };
        if (summary is not null)
        {
            alert["annotations"] = new JsonObject { ["summary"] = summary, ["description"] = description };
        }
        return alert;
    }

    /// <summary>What takes room now, one row a day for each thing (today's measured again), and samples past <see cref="KeepDays"/> dropped.</summary>
    public async Task SampleAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<AppDbContext>();
            var report = sp.GetRequiredService<StorageReport>();
            var samples = new Dictionary<string, long>(StringComparer.Ordinal);
            var (disks, _) = await sp.GetRequiredService<StorageDisks>().ListAsync(ct);
            foreach (var d in disks)
            {
                samples["disk:" + d.Id] = d.Used;
            }
            var (databases, _, _) = await report.DatabasesAsync(ct);
            foreach (var d in databases)
            {
                samples["db:" + d.Name] = d.Bytes;
            }
            var groups = await sp.GetRequiredService<StorageFiles>().GroupsAsync(ct);
            samples["files"] = groups.Sum(g => g.Bytes);
            foreach (var origin in groups.GroupBy(g => g.Origin))
            {
                samples["files:" + origin.Key] = origin.Sum(g => g.Bytes);
            }
            var places = sp.GetRequiredService<StoragePlaces>();
            var library = await places.LibraryAsync(ct);
            if (library.Exists)
            {
                samples["models"] = library.Bytes;
            }
            if (places.Backups() is { State: "ok" } backups)
            {
                samples["backups"] = backups.Bytes + backups.OtherBytes;
            }
            if ((await report.ArgusAsync(fresh: false, ct))["report"] is JsonObject argus && argus["index_bytes"] is not null)
            {
                samples["argus"] = ArgusParts.Sum(k => argus[k]?.GetValue<long>() ?? 0);
                samples["packs"] = argus["packs_bytes"]?.GetValue<long>() ?? 0;
            }
            if ((await report.MetricsAsync(ct)).Bytes is { } metrics)
            {
                samples["metrics"] = metrics;
            }
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var keys = samples.Keys.ToList();
            await db.StorageSamples.Where(s => (s.Day == today && keys.Contains(s.Key)) || s.Day < today.AddDays(-KeepDays)).ExecuteDeleteAsync(ct);
            db.StorageSamples.AddRange(samples.Select(s => new StorageSample { Day = today, Key = s.Key, Bytes = s.Value }));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DatasourceException or ArgusException or DbUpdateException
            or Npgsql.NpgsqlException or IOException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            LogSkipped(logger, "what takes room", ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage watch: Alertmanager did not take the disk alerts ({Reason}); the admins are told directly")]
    private static partial void LogNoAlertmanager(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage watch: {What} not measured now: {Reason}")]
    private static partial void LogSkipped(ILogger logger, string what, string reason);
}
