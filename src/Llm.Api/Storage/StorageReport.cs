using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Dashboards;
using Llm.Api.Operations;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Storage;

/// <summary>A database on the stack's Postgres and what it holds.</summary>
public sealed record DatabaseSize(string Name, long Bytes, string? What);

/// <summary>One of a database's largest tables, with its indexes and long values.</summary>
public sealed record TableSize(string Database, string Name, long Bytes, long Rows, string? What);

/// <summary>Prometheus's own data: what it takes and how long it keeps it.</summary>
public sealed record MetricsStore(long? Bytes, string? Keeps, string? MaxSize, DateTimeOffset? Oldest, string? Problem);

/// <summary>Loki's logs: how long it keeps them, and what each container wrote over the last week (before compression).</summary>
public sealed record LogStore(string? Keeps, IReadOnlyList<FolderSize> Week, long? WeekStored, string? Problem);

/// <summary>A rule that decides how long something stays.</summary>
public sealed record KeepRule(string What, string Rule);

/// <summary>
/// The storage page's picture: the disks, the databases and their largest tables, the chat's files, the model
/// library, Argus's index and packs, the backups, logs and metrics, how long each is kept, what the app cannot
/// see, and how each grew (the daily samples). Each part answers on its own: one missing says why, the rest stand.
/// </summary>
public sealed partial class StorageReport(AppDbContext db, StorageDisks disks, StorageFiles files, StoragePlaces places, StorageCache cache, ArgusAdmin argus,
    PromDatasource prom, LokiDatasource loki, IConfiguration config, IOptions<DashboardOptions> dashboards, IOptionsMonitor<StorageOptions> options,
    IOptionsMonitor<Retention.RetentionOptions> retention, IOptionsMonitor<Gateway.AnswerCacheOptions> answerCache, TimeProvider clock, ILogger<StorageReport> logger)
{
    /// <summary>What the app cannot see without Docker's socket, which it never has (root on the host for anyone who reaches it).</summary>
    public static readonly IReadOnlyList<string> Unseen =
    [
        "Docker's images and its build cache: `docker system df -v` on the host lists them, and `docker image prune` removes the unused ones.",
        "The containers' own logs: Docker keeps at most 5 files of 20 MB for each container (the stack's logging options), about 100 MB each at most.",
        "The volumes the app does not mount (postgres, argus, prometheus, loki, audio, alertmanager, acme): their contents are measured here by what runs in them (the databases, Argus, Prometheus), not as folders.",
        "The speech server's models (the audio volume), fetched from Hugging Face by the speech server itself.",
    ];

    private static readonly Dictionary<string, string> TableWhat = new(StringComparer.Ordinal)
    {
        ["chat_attachments"] = "the chat's files",
        ["attachment_pages"] = "pages drawn of documents, and videos' frames",
        ["chat_messages"] = "the chats' messages",
        ["conversations"] = "the chats",
        ["knowledge_chunks"] = "passages of company knowledge and of long files, with their embeddings",
        ["knowledge_documents"] = "company knowledge's documents",
        ["cached_answers"] = "the answer cache for API keys",
        ["audit_events"] = "the audit log",
        ["canvas_versions"] = "the canvas's versions",
        ["canvases"] = "the canvas's documents",
        ["notifications"] = "the bell's news",
        ["memories"] = "what people asked the chat to remember",
        ["storage_samples"] = "the storage page's trends",
        ["scheduled_runs"] = "scheduled tasks' runs",
        ["LiteLLM_SpendLogs"] = "the gateway's request log: usage, cost, the dashboards",
        ["LiteLLM_ErrorLogs"] = "the gateway's errors",
        ["LiteLLM_AuditLog"] = "the gateway's audit log",
        ["LiteLLM_DailyUserSpend"] = "spend a day, per person",
        ["LiteLLM_DailyTeamSpend"] = "spend a day, per team",
        ["LiteLLM_VerificationToken"] = "API keys",
    };

    public StorageOptions Settings => options.CurrentValue;

    /// <summary>The whole page; <paramref name="fresh"/>: the folders (and Argus's) measured again now.</summary>
    public async Task<JsonObject> BuildAsync(bool fresh, CancellationToken ct)
    {
        if (fresh)
        {
            cache.Forget();
        }
        // What needs no database runs beside what does (one DbContext answers one query at a time).
        var disksTask = DisksAsync(ct);
        var argusTask = ArgusAsync(fresh, ct);
        var metricsTask = MetricsAsync(ct);
        var logsTask = LogsAsync(ct);
        var backupsTask = Task.Run(places.Backups, ct);

        var (databases, tables, dbProblem) = await DatabasesAsync(ct);
        var groups = await Part(() => files.GroupsAsync(ct), "the chat's files");
        var people = await Part(() => files.PeopleAsync(ct), "people's files");
        var library = await Part(() => places.LibraryAsync(ct), "the model library");
        var rules = await RulesAsync(ct);
        var trends = await TrendsAsync(90, ct);
        var o = options.CurrentValue;
        return new JsonObject
        {
            ["at"] = clock.GetUtcNow(),
            ["settings"] = Node(new { o.AlertPercent, o.PersonMegabytes, o.MediaDays, o.BackupsKept }),
            ["disks"] = await disksTask,
            ["databases"] = Node(new { list = databases, tables, problem = dbProblem }),
            ["files"] = Node(new
            {
                total = groups.Value is { } g ? new Amount(g.Sum(x => x.Count), g.Sum(x => x.Bytes)) : null,
                groups = groups.Value, people = people.Value?.Take(10), problem = groups.Problem ?? people.Problem,
            }),
            ["library"] = Node(new { report = library.Value, problem = library.Problem }),
            ["argus"] = await argusTask,
            ["backups"] = Node(await backupsTask),
            ["logs"] = Node(await logsTask),
            ["metrics"] = Node(await metricsTask),
            ["unseen"] = Node(Unseen),
            ["rules"] = Node(rules),
            ["trends"] = Node(trends),
        };
    }

    private static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, JsonSerializerOptions.Web);

    /// <summary>One part of the page, or why it is missing.</summary>
    private async Task<(T? Value, string? Problem)> Part<T>(Func<Task<T>> run, string what) where T : class
    {
        try
        {
            return (await run(), null);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogPart(logger, what, ex.Message);
            return (null, $"{char.ToUpperInvariant(what[0])}{what[1..]} could not be measured: {ex.Message}");
        }
    }

    /// <summary>The disks with their last 30 days, how fast each fills, and which are past the alert's share.</summary>
    public async Task<JsonObject> DisksAsync(CancellationToken ct)
    {
        var (list, problem) = await disks.ListAsync(ct);
        IReadOnlyList<DiskTrend> trends = [];
        try
        {
            trends = await disks.TrendsAsync(ct);
        }
        catch (Exception ex) when (ex is DatasourceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The disks stand without their history.
        }
        var threshold = options.CurrentValue.AlertPercent;
        return new JsonObject
        {
            ["alertPercent"] = threshold,
            ["problem"] = problem,
            ["list"] = new JsonArray([.. list.Select(d =>
            {
                var trend = trends.FirstOrDefault(t => t.Id == d.Id);
                var node = Node(d)!.AsObject();
                node["used"] = d.Used;
                node["percent"] = d.Percent;
                node["above"] = d.Percent >= threshold;
                node["trend"] = Node(trend?.Points ?? []);
                node["perDay"] = trend?.PerDay;
                node["fullInDays"] = StorageDisks.FullIn(d, trend?.PerDay);
                return (JsonNode)node;
            })]),
        };
    }

    /// <summary>Every database on the server the app can reach, and the largest tables of the app's and the gateway's.</summary>
    public async Task<(IReadOnlyList<DatabaseSize> Databases, IReadOnlyList<TableSize> Tables, string? Problem)> DatabasesAsync(CancellationToken ct)
    {
        var app = db.Database.GetDbConnection().Database;
        var gateway = dashboards.Value.SqlDatabase;
        var list = new List<DatabaseSize>();
        var tables = new List<TableSize>();
        string? problem = null;
        try
        {
            var conn = db.Database.GetDbConnection();
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT datname, pg_database_size(datname) FROM pg_database
                        WHERE NOT datistemplate AND datallowconn AND has_database_privilege(datname, 'CONNECT') ORDER BY 2 DESC
                        """;
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        var name = r.GetString(0);
                        list.Add(new DatabaseSize(name, r.GetInt64(1), DatabaseWhat(name, app, gateway)));
                    }
                }
                tables.AddRange(await LargestAsync(conn, app, ct));
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            problem = $"The databases' sizes could not be read: {ex.Message}";
        }
        if (list.Any(d => d.Name == gateway))
        {
            try
            {
                await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(config)) { Database = gateway }.ConnectionString);
                await conn.OpenAsync(ct);
                tables.AddRange(await LargestAsync(conn, gateway, ct));
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                problem ??= $"The gateway's tables could not be read: {ex.Message}";
            }
        }
        return (list, tables, problem);
    }

    private static async Task<List<TableSize>> LargestAsync(System.Data.Common.DbConnection conn, string database, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.relname, pg_total_relation_size(c.oid), greatest(c.reltuples, 0)::bigint
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'm') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'
            ORDER BY 2 DESC LIMIT 10
            """;
        var list = new List<TableSize>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            list.Add(new TableSize(database, name, r.GetInt64(1), r.GetInt64(2), TableWhat.GetValueOrDefault(name)));
        }
        return list;
    }

    private static string? DatabaseWhat(string name, string app, string gateway) =>
        name == app ? "The app: chats and their files, settings, company knowledge, the audit log"
        : name == gateway ? "The gateway: API keys, credit, and its log of every request"
        : name switch
        {
            "langfuse" => "Langfuse: traces",
            "argus" => "Argus",
            "postgres" => "Postgres's own (usually empty)",
            _ => null,
        };

    /// <summary>Argus's disk (its index, GitLab mirrors, trees and packs) and its packs, as Argus reports them.</summary>
    public async Task<JsonObject> ArgusAsync(bool fresh, CancellationToken ct)
    {
        if (!argus.Enabled)
        {
            return new JsonObject { ["configured"] = false };
        }
        var result = new JsonObject { ["configured"] = true };
        try
        {
            result["report"] = await argus.GetAsync(fresh ? "storage?fresh" : "storage", ct);
        }
        catch (ArgusException ex)
        {
            result["problem"] = ex.Status == 404 ? "This Argus does not report its storage (an older version)." : ex.Message;
        }
        try
        {
            var packs = await argus.GetAsync("packs", ct);
            result["packs"] = packs?["packs"]?.DeepClone();
            result["library"] = packs?["library"]?.DeepClone();
            result["libraryDir"] = packs?["library_dir"]?.DeepClone();
        }
        catch (ArgusException ex)
        {
            result["packsProblem"] = ex.Message;
        }
        return result;
    }

    /// <summary>Prometheus's data: its blocks, write-ahead log and head, how long it keeps them, and its oldest sample.</summary>
    public async Task<MetricsStore> MetricsAsync(CancellationToken ct)
    {
        try
        {
            var now = clock.GetUtcNow();
            double? One(IReadOnlyList<RawSeries> s) => s.Count > 0 ? s.Sum(x => x.Points.Count > 0 ? x.Points[^1][1] ?? 0 : 0) : null;
            var blocks = One(await prom.InstantAsync("prometheus_tsdb_storage_blocks_bytes", now, ct));
            var wal = One(await prom.InstantAsync("prometheus_tsdb_wal_storage_size_bytes", now, ct));
            var head = One(await prom.InstantAsync("prometheus_tsdb_head_chunks_storage_size_bytes", now, ct));
            var lowest = One(await prom.InstantAsync("prometheus_tsdb_lowest_timestamp_seconds", now, ct));
            string? keeps = null, max = null;
            try
            {
                var flags = (await prom.GetAsync("/api/v1/status/flags", ct)).GetProperty("data");
                keeps = flags.TryGetProperty("storage.tsdb.retention.time", out var t) ? Duration(t.GetString()) : null;
                max = flags.TryGetProperty("storage.tsdb.retention.size", out var s) && s.GetString() is { Length: > 0 } size && size != "0B" ? size : null;
            }
            catch (DatasourceException)
            {
                // An older Prometheus: no flags.
            }
            var bytes = blocks is null && wal is null && head is null ? (long?)null : (long)((blocks ?? 0) + (wal ?? 0) + (head ?? 0));
            return new MetricsStore(bytes, keeps, max, lowest is > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)lowest.Value) : null,
                bytes is null ? "Prometheus does not report its own storage (it does not scrape itself)." : null);
        }
        catch (Exception ex) when (ex is DatasourceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new MetricsStore(null, null, null, null, "Prometheus did not answer.");
        }
    }

    /// <summary>Loki: how long it keeps logs (its own configuration), and what each container wrote over the last week.</summary>
    public async Task<LogStore> LogsAsync(CancellationToken ct)
    {
        string? keeps = null;
        try
        {
            keeps = LokiKeeps(await loki.TextAsync("/config", ct));
        }
        catch (Exception ex) when (ex is DatasourceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new LogStore(null, [], null, "Loki did not answer.");
        }
        var end = clock.GetUtcNow();
        var start = end.AddDays(-7);
        var week = new List<FolderSize>();
        string? problem = null;
        try
        {
            var data = (await loki.GetAsync($"/loki/api/v1/index/volume?query={Uri.EscapeDataString("{container=~\".+\"}")}&start={LokiDatasource.Nanos(start)}" +
                $"&end={LokiDatasource.Nanos(end)}&limit=100&targetLabels=container&aggregateBy=series", ct)).GetProperty("data");
            foreach (var r in data.GetProperty("result").EnumerateArray())
            {
                var name = r.GetProperty("metric").TryGetProperty("container", out var c) ? c.GetString() ?? "?" : "?";
                if (long.TryParse(r.GetProperty("value")[1].GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes))
                {
                    week.Add(new FolderSize(name, bytes));
                }
            }
        }
        catch (Exception ex) when (ex is DatasourceException or JsonException or KeyNotFoundException or InvalidOperationException or HttpRequestException)
        {
            problem = "Loki does not say what each container wrote (its volume API is off).";
        }
        long? stored = null;
        try
        {
            var series = await prom.InstantAsync("sum(increase(loki_ingester_chunk_stored_bytes_total[7d]))", end, ct);
            stored = series.Count > 0 && series[0].Points.Count > 0 && series[0].Points[^1][1] is { } v ? (long)v : null;
        }
        catch (Exception ex) when (ex is DatasourceException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Without Prometheus: the week's stored bytes are unknown.
        }
        return new LogStore(keeps, [.. week.OrderByDescending(w => w.Bytes)], stored, problem);
    }

    /// <summary>
    /// How long Loki keeps logs, from its configuration (GET /config): limits_config's retention_period, which the
    /// compactor applies when its retention is on. "forever" when nothing deletes them.
    /// </summary>
    public static string? LokiKeeps(string yaml)
    {
        string? Within(string section, string key)
        {
            var inside = false;
            foreach (var line in yaml.Split('\n'))
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
                {
                    inside = line.TrimEnd() == section + ":";
                    continue;
                }
                var trimmed = line.Trim();
                // Only the section's own keys (indented once), not a nested one.
                if (inside && trimmed.StartsWith(key + ":", StringComparison.Ordinal) && line.Length - line.TrimStart().Length <= 2)
                {
                    return trimmed[(key.Length + 1)..].Trim().Trim('"', '\'');
                }
            }
            return null;
        }
        var enabled = Within("compactor", "retention_enabled");
        var period = Within("limits_config", "retention_period");
        if (enabled is null && period is null)
        {
            return null;
        }
        if (enabled != "true" || period is null || ParseDuration(period) is not { } span || span <= TimeSpan.Zero)
        {
            return "forever";
        }
        return Days(span);
    }

    /// <summary>"336h" or "30d" in words ("14 days"); the text itself when it is not a duration.</summary>
    public static string? Duration(string? text) => text is null ? null : ParseDuration(text) is { } span && span > TimeSpan.Zero ? Days(span) : text;

    private static string Days(TimeSpan span) =>
        span.TotalDays >= 1 && span.TotalDays % 1 == 0 ? $"{span.TotalDays:0} day{(span.TotalDays == 1 ? "" : "s")}"
        : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hours" : $"{span.TotalMinutes:0} minutes";

    /// <summary>Prometheus's and Go's durations: 336h, 720h0m0s, 30d, 2w, 1y.</summary>
    public static TimeSpan? ParseDuration(string text)
    {
        var parts = DurationPart().Matches(text.Trim());
        if (parts.Count == 0 || string.Concat(parts.Select(p => p.Value)) != text.Trim())
        {
            return null;
        }
        var total = TimeSpan.Zero;
        foreach (Match p in parts)
        {
            var n = double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture);
            total += p.Groups[2].Value switch
            {
                "ms" => TimeSpan.FromMilliseconds(n),
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                "h" => TimeSpan.FromHours(n),
                "d" => TimeSpan.FromDays(n),
                "w" => TimeSpan.FromDays(7 * n),
                _ => TimeSpan.FromDays(365 * n),
            };
        }
        return total;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)(ms|s|m|h|d|w|y)")]
    private static partial Regex DurationPart();

    /// <summary>The rules that decide how long things stay: the chats' retention, the clean-ups' defaults, logs, metrics, backups.</summary>
    public async Task<IReadOnlyList<KeepRule>> RulesAsync(CancellationToken ct)
    {
        var o = options.CurrentValue;
        var days = retention.CurrentValue.Days;
        var groups = await db.Groups.AsNoTracking().Where(g => g.RetentionDays != null).OrderBy(g => g.RetentionDays).Select(g => new { g.Name, g.RetentionDays }).ToListAsync(ct);
        var held = await db.Users.CountAsync(u => u.LegalHoldSince != null, ct);
        var rules = new List<KeepRule>
        {
            new("Chats and their files", (days is > 0 ? $"Deleted {days} days after their last message" : "Kept forever") +
                (groups.Count > 0 ? $"; {groups.Count} group{(groups.Count == 1 ? "" : "s")} keep their own ({string.Join(", ", groups.Take(4).Select(g => $"{g.Name}: {g.RetentionDays} days"))}{(groups.Count > 4 ? "…" : "")})" : "") +
                ". Files in no chat go when they are as old. Settings → Data retention, and Groups."),
            new("Legal hold", held == 0 ? "Nobody is on legal hold." : $"{held} {(held == 1 ? "person is" : "people are")} on legal hold: nothing of theirs is deleted, by retention, by them or here. " +
                "Backups taken before a hold began hold their data from then, and scripts/backup.sh removes old backups by count, hold or not: the old backups' preview says which."),
            new("Pictures, videos and speech the tools made", $"Kept as long as their chat. The clean-up of old ones takes {o.MediaDays} days as old (Settings → Storage)."),
            new("Rooms for files", o.PersonMegabytes is > 0 ? $"{StorageDisks.Size(o.PersonMegabytes.Value * 1024L * 1024)} each, unless an admin gave someone their own" : "No limit, unless an admin gave someone their own"),
            new("The answer cache", answerCache.CurrentValue.On ? $"Answers are kept {answerCache.CurrentValue.AnswerCacheTtl.TotalHours:0.#} hours (Settings → API keys)." : "Off: nothing is kept."),
            new("Backups", $"scripts/backup.sh keeps the newest {o.BackupsKept} (BACKUP_KEEP in .env) after each good backup, and removes older ones on request (--prune). The app sees them read only: it never changes or deletes one."),
            new("Partial downloads", "Kept while their download can go on from where it got to; a part no download owns is a leftover."),
            new("Container logs", "Docker keeps 5 files of 20 MB for each container."),
        };
        return rules;
    }

    /// <summary>Each thing measured, a point a day over the last <paramref name="days"/>: [epoch ms, bytes].</summary>
    public async Task<Dictionary<string, List<long[]>>> TrendsAsync(int days, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddDays(-days);
        var rows = await db.StorageSamples.AsNoTracking().Where(s => s.Day >= from).OrderBy(s => s.Day).ToListAsync(ct);
        return rows.GroupBy(r => r.Key, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Select(r => new[] { new DateTimeOffset(r.Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds(), r.Bytes }).ToList(), StringComparer.Ordinal);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage: {What} could not be measured: {Reason}")]
    private static partial void LogPart(ILogger logger, string what, string reason);
}
