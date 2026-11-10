using Llm.Core.Access;
using Llm.Api.Access;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Branding;
namespace Llm.Api.Operations;

public sealed record Probe(string Name, string Purpose, bool Ok, string Detail);

/// <param name="Repo">One repository to bring up to date (its path); null: a whole pass.</param>
public sealed record IndexRequest(string[]? Branches, bool AllowPartial = false, string? Repo = null);

/// <param name="Included">In the index or out; null: unchanged.</param>
/// <param name="Branches">Branches indexed besides the default one (names or globs); null: unchanged.</param>
/// <param name="Schedule">Its own schedule (pass, off, hours:N, daily:HH:MM, weekly:D:HH:MM), "" for the default; null: unchanged.</param>
public sealed record RepoChoiceRequest(bool? Included = null, string[]? Branches = null, string? Schedule = null);

/// <param name="Ids">The repositories (GitLab project ids).</param>
/// <param name="Action">include, exclude, reindex, schedule, add_branches or remove.</param>
/// <param name="Schedule">For schedule: as <see cref="RepoChoiceRequest.Schedule"/>.</param>
/// <param name="Branches">For add_branches: names or globs.</param>
public sealed record RepoBatchRequest(long[] Ids, string Action, string? Schedule = null, string[]? Branches = null);

/// <param name="LeaveOut">Leave the repository out of the index too; else it is built anew by the next pass or an update.</param>
public sealed record RemoveIndexRequest(bool LeaveOut = false);

/// <param name="Schedule">Five-field cron; "": code-intel does not reindex by itself.</param>
/// <param name="TimeZone">IANA, e.g. Europe/Berlin.</param>
public sealed record IndexScheduleRequest(string Schedule, string TimeZone);

/// <param name="NewRepos">include or exclude: whether a repository GitLab lists for the first time is indexed; null: unchanged.</param>
/// <param name="Schedule">The schedule of every repository without one of its own (pass, off, hours:N, daily:HH:MM, weekly:D:HH:MM); null: unchanged.</param>
/// <param name="TimeZone">The IANA zone daily and weekly schedules are in; null: unchanged.</param>
public sealed record RepoPolicyRequest(string? NewRepos = null, string? Schedule = null, string? TimeZone = null);
public sealed record PackRequest(string? Source, string? Sha256, string? Name, string? IndexUrl, string? File = null);

public static class OperationsEndpoints
{
    public static void MapOperations(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequirePermissions(Llm.Core.Access.Permissions.MonitoringRead, Llm.Core.Access.Permissions.SettingsManage);
        g.MapGet("/overview", OverviewAsync);
        g.MapGet("/services", async (IHttpClientFactory f, IOptions<StackOptions> s, IOptions<CodeIntelOptions> a, IOptions<Dashboards.DashboardOptions> d, Features features,
            Models.MediaControl media, Gateway.ModelGateway own, CancellationToken ct) => Results.Ok(await ProbeAllAsync(f, s.Value, a.Value, d.Value, features, media, own, ct)));
        // People, their emails and spend: people.read's, not monitoring's.
        g.MapGet("/people.csv", PeopleCsvAsync).RequirePermission(Llm.Core.Access.Permissions.PeopleRead);

        var codeIntel = g.MapGroup("/code-intel").RequirePermissions(Llm.Core.Access.Permissions.CodeIntelRead, Llm.Core.Access.Permissions.CodeIntelManage, new Dictionary<string, string>
        {
            ["POST /api/admin/code-intel/webhook"] = Llm.Core.Access.Permissions.CodeIntelWebhook,
            ["DELETE /api/admin/code-intel/webhook"] = Llm.Core.Access.Permissions.CodeIntelWebhook,
            ["POST /api/admin/code-intel/index"] = Llm.Core.Access.Permissions.CodeIntelIndex,
            ["POST /api/admin/code-intel/repos/discover"] = Llm.Core.Access.Permissions.CodeIntelRepos,
            ["PATCH /api/admin/code-intel/repos/{gitlabId:long}"] = Llm.Core.Access.Permissions.CodeIntelRepos,
            ["POST /api/admin/code-intel/repos/batch"] = Llm.Core.Access.Permissions.CodeIntelRepos,
            ["POST /api/admin/code-intel/repos/{gitlabId:long}/index/remove"] = Llm.Core.Access.Permissions.CodeIntelIndex,
            ["PUT /api/admin/code-intel/repos/settings"] = Llm.Core.Access.Permissions.CodeIntelRepos,
            ["PUT /api/admin/code-intel/schedule"] = Llm.Core.Access.Permissions.CodeIntelIndex,
            ["POST /api/admin/code-intel/packs/{action}"] = Llm.Core.Access.Permissions.CodeIntelPacks,
        });
        codeIntel.MapGet("/status", (CodeIntelAdmin a, CancellationToken ct) => Relay(() => a.GetAsync("index/status", ct), a));
        // The GitLab webhook: on a push or a merge, the repository's index is brought up to date.
        codeIntel.MapGet("/webhook", (CodeIntelWebhook w, CodeIntelAdmin a, CancellationToken ct) => Relay(async () => (JsonNode?)await w.ViewAsync(ct), a));
        codeIntel.MapPost("/webhook", (CodeIntelWebhook w, CodeIntelAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var token = await w.RotateAsync(ct);
            await audit.WriteAsync("code-intel.webhook_secret", "gitlab");
            // The only time the secret is shown: code-intel keeps its hash, the app nothing.
            var view = await w.ViewAsync(ct);
            view["token"] = token;
            return (JsonNode?)view;
        }, a));
        codeIntel.MapDelete("/webhook", (CodeIntelWebhook w, CodeIntelAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            await w.DisableAsync(ct);
            await audit.WriteAsync("code-intel.webhook_off", "gitlab");
            return (JsonNode?)await w.ViewAsync(ct);
        }, a));
        codeIntel.MapPost("/index", (IndexRequest body, CodeIntelAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            if (body.Repo is { Length: > 0 } repo)
            {
                var one = await a.PostAsync("index", new JsonObject { ["repo"] = repo.Trim() }, ct);
                await audit.WriteAsync("code-intel.index_repo", repo.Trim());
                return one;
            }
            var branches = (body.Branches ?? []).Select(b => b.Trim()).Where(b => b.Length > 0).ToArray();
            var res = await a.PostAsync("index", new JsonObject
            {
                ["branches"] = new JsonArray([.. branches.Select(b => JsonValue.Create(b))]),
                ["allow_partial"] = body.AllowPartial,
            }, ct);
            await audit.WriteAsync("code-intel.index", string.Join(",", branches), detail: body.AllowPartial ? "partial enumeration allowed" : null);
            return res;
        }, a));
        // Which repositories and branches are indexed (code-intel keeps the choices).
        codeIntel.MapGet("/repos", (CodeIntelAdmin a, CancellationToken ct) => Relay(() => a.GetAsync("repos", ct), a));
        codeIntel.MapPost("/repos/discover", (CodeIntelAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var res = await a.PostAsync("repos/discover", new JsonObject(), ct);
            await audit.WriteAsync("code-intel.repos_discover", null);
            return res;
        }, a));
        codeIntel.MapPatch("/repos/{gitlabId:long}", (long gitlabId, RepoChoiceRequest body, CodeIntelAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var payload = new JsonObject();
            if (body.Included is { } included)
            {
                payload["included"] = included;
            }
            if (body.Branches is { } branches)
            {
                payload["branches"] = new JsonArray([.. branches.Select(b => JsonValue.Create(b.Trim()))]);
            }
            if (body.Schedule is { } schedule)
            {
                payload["schedule"] = schedule.Trim();
            }
            var res = await a.PatchAsync($"repos/{gitlabId}", payload, ct);
            var repo = res?["repo"]?.GetValue<string>() ?? gitlabId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await audit.WriteAsync("code-intel.repo_choice", repo,
                detail: string.Join("; ", new[]
                {
                    body.Included is { } i ? (i ? "indexed" : "not indexed") : null, body.Branches is { } b ? $"branches: {string.Join(", ", b)}" : null,
                    body.Schedule is { } s ? $"schedule: {(s.Trim().Length > 0 ? s.Trim() : "default")}" : null,
                }.OfType<string>()));
            return res;
        }, a));
        // Several at once, each with its outcome: in or out, updated now, a schedule, more branches, removed.
        codeIntel.MapPost("/repos/batch", (RepoBatchRequest body, CodeIntelAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var payload = new JsonObject { ["action"] = body.Action, ["ids"] = new JsonArray([.. (body.Ids ?? []).Select(id => JsonValue.Create(id))]) };
            if (body.Schedule is { } schedule)
            {
                payload["schedule"] = schedule.Trim();
            }
            if (body.Branches is { } branches)
            {
                payload["branches"] = new JsonArray([.. branches.Select(b => JsonValue.Create(b.Trim()))]);
            }
            var res = await a.PostAsync("repos/batch", payload, ct);
            var results = res?["results"] as JsonArray ?? [];
            await audit.WriteAsync("code-intel.repos_batch", body.Action,
                detail: $"{results.Count(r => r?["ok"]?.GetValue<bool>() == true)} of {results.Count} repositories" +
                        (body.Schedule is { } s ? $"; schedule: {(s.Trim().Length > 0 ? s.Trim() : "default")}" : "") +
                        (body.Branches is { } b ? $"; branches: {string.Join(", ", b)}" : ""));
            return res;
        }, a));
        codeIntel.MapGet("/repos/{gitlabId:long}/log", (long gitlabId, int? runs, CodeIntelAdmin a, CancellationToken ct) =>
            Relay(() => a.GetAsync($"repos/{gitlabId}/log?runs={Math.Clamp(runs ?? 5, 1, 20)}", ct), a));
        codeIntel.MapGet("/repos/{gitlabId:long}/branches", (long gitlabId, CodeIntelAdmin a, CancellationToken ct) => Relay(() => a.GetAsync($"repos/{gitlabId}/branches", ct), a));
        // Its index removed now; left out too, or kept in to be built anew.
        codeIntel.MapPost("/repos/{gitlabId:long}/index/remove", (long gitlabId, RemoveIndexRequest body, CodeIntelAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var res = await a.PostAsync($"repos/{gitlabId}/index/remove", new JsonObject { ["leave_out"] = body.LeaveOut }, ct);
            await audit.WriteAsync("code-intel.index_remove", res?["repo"]?.GetValue<string>() ?? gitlabId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                detail: body.LeaveOut ? "left out" : "kept in, built anew");
            return res;
        }, a));
        codeIntel.MapPut("/repos/settings", (RepoPolicyRequest body, CodeIntelAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var payload = new JsonObject();
            if (body.NewRepos is { } policy)
            {
                payload["new_repos"] = policy;
            }
            if (body.Schedule is { } schedule)
            {
                payload["schedule"] = schedule.Trim();
            }
            if (body.TimeZone is { } zone)
            {
                payload["schedule_tz"] = zone.Trim();
            }
            var res = await a.PutAsync("repos/settings", payload, ct);
            await audit.WriteAsync("code-intel.repo_policy", body.NewRepos ?? body.Schedule,
                detail: string.Join("; ", new[]
                {
                    body.NewRepos is { } n ? $"new repositories: {n}" : null, body.Schedule is { } s ? $"schedule for all: {s.Trim()}" : null,
                    body.TimeZone is { } z ? $"time zone: {z.Trim()}" : null,
                }.OfType<string>()));
            return res;
        }, a));
        codeIntel.MapGet("/schedule", ScheduleAsync);
        codeIntel.MapPut("/schedule", SetScheduleAsync);
        codeIntel.MapGet("/packs", (CodeIntelAdmin a, CancellationToken ct) => Relay(() => a.GetAsync("packs", ct), a));
        codeIntel.MapPost("/packs/{action}", (string action, PackRequest body, CodeIntelAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            JsonObject payload = action switch
            {
                "install" => new() { ["source"] = body.Source, ["sha256"] = body.Sha256 },
                "update" => new() { ["name"] = body.Name, ["index_url"] = body.IndexUrl },
                "remove" => new() { ["name"] = body.Name },
                "load" => new() { ["file"] = body.File },
                "renew" => new() { ["name"] = body.Name },
                _ => throw new CodeIntelException("Unknown pack action.", 404),
            };
            var res = await a.PostAsync("packs/" + action, payload, ct);
            await audit.WriteAsync("code-intel.packs." + action, body.Name ?? body.Source ?? body.File);
            return res;
        }, a));
        codeIntel.MapGet("/explore", (string? q, string? repo, int? limit, CodeIntelAdmin a, CancellationToken ct) =>
        {
            var query = $"explore?q={Uri.EscapeDataString(q ?? "")}&repo={Uri.EscapeDataString(repo ?? "")}&limit={Math.Clamp(limit ?? 50, 1, 500)}";
            return Relay(() => a.GetAsync(query, ct), a);
        });
        // Explore's searches (references, code, docs) and opening a result: passed through as asked.
        foreach (var (what, keys) in new[]
        {
            ("references", new[] { "name", "repo", "limit" }), ("code", new[] { "q", "repo", "limit" }), ("file", new[] { "repo_id", "path" }),
            ("docs", new[] { "q", "mode", "source", "limit" }), ("doc", new[] { "path", "source" }),
        })
        {
            codeIntel.MapGet("/explore/" + what, (HttpRequest request, CodeIntelAdmin a, CancellationToken ct) =>
            {
                var query = string.Join('&', keys.Select(k => $"{k}={Uri.EscapeDataString(request.Query[k].ToString())}"));
                return Relay(() => a.GetAsync($"explore/{what}?{query}", ct), a);
            });
        }
    }

    /// <summary>When code-intel reindexes by itself: the schedule, its zone, and the next passes.</summary>
    private static IResult ScheduleAsync(IOptionsMonitor<CodeIntelIndexOptions> options, TimeProvider clock)
    {
        var o = options.CurrentValue;
        var (cron, zone, problem) = CodeIntelIndexSchedule.Read(o);
        return Results.Ok(new
        {
            schedule = o.Schedule ?? "", timeZone = zone.Id, problem,
            nextRuns = cron?.NextRuns(clock.GetUtcNow(), zone, 3) ?? [],
        });
    }

    private static async Task<IResult> SetScheduleAsync(IndexScheduleRequest body, Settings.SettingsService settings, Identity.Audit audit, TimeProvider clock, CancellationToken ct)
    {
        var schedule = (body.Schedule ?? "").Trim();
        var (zone, zoneProblem) = Models.Hours.Zone(body.TimeZone);
        if (zoneProblem is not null)
        {
            return AuthEndpoints.Problem(400, "time_zone", $"\"{body.TimeZone}\" is not a time zone: use an IANA name such as Europe/Berlin.");
        }
        if (schedule.Length > 0)
        {
            var (cron, problem) = Schedules.Cron.Parse(schedule);
            if (cron is null)
            {
                return AuthEndpoints.Problem(400, "schedule", problem!);
            }
            if (cron.Next(clock.GetUtcNow(), zone) is null)
            {
                return AuthEndpoints.Problem(400, "schedule", "That schedule never comes round (31 February?).");
            }
            if (cron.ShortestGap(clock.GetUtcNow(), zone) is { } gap && gap < CodeIntelIndexSchedule.MinInterval)
            {
                return AuthEndpoints.Problem(400, "schedule", $"At most one pass every {CodeIntelIndexSchedule.MinInterval.TotalMinutes:0} minutes: a pass reads every repository's changes.");
            }
            schedule = cron.Expression;
        }
        await settings.SaveAsync([new Settings.SettingChange("CodeIntelIndex:Schedule", schedule), new Settings.SettingChange("CodeIntelIndex:TimeZone", zone.Id)], ct);
        await audit.WriteAsync("code-intel.index_schedule", schedule.Length > 0 ? schedule : "off", detail: zone.Id);
        return Results.NoContent();
    }

    /// <summary>code-intel's answer as it is, or a sentence saying why there is none.</summary>
    private static async Task<IResult> Relay(Func<Task<JsonNode?>> call, CodeIntelAdmin codeIntel)
    {
        if (!codeIntel.Enabled)
        {
            return Results.Ok(new { configured = false });
        }
        try
        {
            return Results.Ok(await call());
        }
        catch (CodeIntelException ex)
        {
            return AuthEndpoints.Problem(ex.Status, "code-intel", ex.Message);
        }
    }

    private static async Task<IResult> OverviewAsync(AppDbContext db, UserManager<AppUser> users, Ledger ledger, CodeIntelAdmin codeIntel,
        IHttpClientFactory factory, IOptions<StackOptions> stack, IOptions<CodeIntelOptions> codeIntelOptions, IOptions<Dashboards.DashboardOptions> dashboards, Chat.ChatModels models,
        Dashboards.PromDatasource prom, TimeProvider clock, Replicas replicas, Storage.StorageDisks disks, IOptionsMonitor<Storage.StorageOptions> storage,
        Models.MediaControl media, Access.AccessService access, Downloads.CodeArenaClients codeArena, Features features, Gateway.ModelGateway own, CancellationToken ct)
    {
        var people = await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync(ct);
        var admins = (await access.OwnersAsync(ct: ct)).Count;
        // This month's spend, and who has used up a credit of their own (of any kind).
        var spending = await ledger.ReadAsync(ct);
        var warning = spending.Problem;
        var over = people.Where(p => spending.Over(p).Count > 0).Select(p => p.UserName).ToList();

        var probes = ProbeAllAsync(factory, stack.Value, codeIntelOptions.Value, dashboards.Value, features, media, own, ct);
        var certificate = Certificates.ReadAsync(prom, stack.Value, clock.GetUtcNow(), ct);
        var space = disks.ListAsync(ct);
        JsonNode? index = null;
        string? indexError = null;
        if (codeIntel.Enabled)
        {
            try
            {
                var st = await codeIntel.GetAsync("index/status", ct);
                index = st?["index"]?.DeepClone();
                if (index is JsonObject o && st?["job"] is JsonObject job)
                {
                    o["returncode"] = job["returncode"]?.DeepClone();
                    o["finished"] = job["finished"]?.DeepClone();
                    o["state"] = job["state"]?.DeepClone();
                }
            }
            catch (CodeIntelException ex)
            {
                indexError = ex.Message;
            }
        }
        return Results.Ok(new
        {
            people = people.Count,
            admins,
            spend = spending.Total,
            overCredit = over,
            warning,
            services = await probes,
            index = new { configured = codeIntel.Enabled, summary = index, error = indexError },
            model = models.DefaultName,
            certificate = await certificate,
            // The fullest disks, and which are past the share that raises the storage alert (Admin → Storage).
            storage = await StorageAsync(space, storage.CurrentValue.AlertPercent),
            // The app's replicas on the database, and whether the one answering leads (runs the once-only background work).
            replicas = new { count = replicas.Count, leads = replicas.IsLeader, id = replicas.Id },
            // Who used a Code Arena older than the one this Arena serves, in the last 30 days (each was told in their bell).
            oldCodeArena = codeArena.Latest is { } latest
                ? new { latest, people = (await codeArena.OldAsync(db, ct)).Select(o => new { id = o.Person, name = o.Name, version = o.Version, lastSeen = o.LastSeen }) }
                : null,
        });
    }

    private static async Task<object> StorageAsync(Task<(IReadOnlyList<Storage.Disk> Disks, string? Problem)> space, int threshold)
    {
        var (list, problem) = await space;
        return new
        {
            alertPercent = threshold,
            problem,
            disks = list.Take(4).Select(d => new { d.Id, d.Name, d.Size, d.Free, d.Percent, above = d.Percent >= threshold, d.Holds }),
        };
    }

    /// <summary>
    /// Each service's probe, of the services the stack runs (a feature that is off runs none: it is not down). The
    /// picture and video servers run only while their model is loaded (MediaControl): one whose model is unloaded, as
    /// meant (idle ten minutes, or never asked for), or that has no model in the library yet, is idle, not down.
    /// </summary>
    public static async Task<IReadOnlyList<Probe>> ProbeAllAsync(IHttpClientFactory factory, StackOptions s, CodeIntelOptions a, Dashboards.DashboardOptions d,
        Features features, Models.MediaControl? media = null, Gateway.ModelGateway? own = null, CancellationToken ct = default)
    {
        var http = factory.CreateClient("probe");
        // The app's own gateway (Gateway:Mode own): its health is the app's, each model's server asked in the app.
        Task<Probe>? gateway = null;
        if (own is { ForApi: true })
        {
            gateway = own.HealthAsync(ct).ContinueWith(t => t.IsCompletedSuccessfully
                ? new Probe("Model gateway", "in the app: API keys, credit, the models' routes", t.Result.Ok, t.Result.Detail)
                : new Probe("Model gateway", "in the app: API keys, credit, the models' routes", false, "its health could not be read"), TaskScheduler.Default);
        }
        var services = new (string Service, string Name, string Purpose, string Url)[]
        {
            ("litellm", "Model gateway", "LiteLLM: API keys, credit, the chat's model", s.LiteLlmProbeUrl.TrimEnd('/') + "/health/liveliness"),
            ("prometheus", "Prometheus", "metrics and alert rules", s.PrometheusUrl.TrimEnd('/') + "/-/healthy"),
            ("alertmanager", "Alertmanager", "firing alerts and their notifications", d.AlertmanagerUrl.TrimEnd('/') + "/-/healthy"),
            ("loki", "Loki", "every service's logs", d.LokiUrl.TrimEnd('/') + "/ready"),
            ("llamacpp", "Engine", "llama.cpp: the chat models", "http://llamacpp:8080/health"),
            ("imagegen", "Pictures", "the image tool's server", Models.MediaModels.ImageUrl + "/v1/models"),
            ("videogen", "Video", "the video tool's server", Models.MediaModels.VideoUrl + "/v1/models"),
            ("audio", "Speech", "speech to text and text to speech", Models.MediaModels.AudioUrl + "/health"),
            ("searxng", "Web search", "SearXNG, the web tool's search", "http://searxng:8080/healthz"),
            ("code-intel", $"{Brand.CodeIntel}", "the code index", a.Url.TrimEnd('/') + "/healthz"),
        };
        var probes = await Task.WhenAll(services
            .Where(x => features.Runs(x.Service) && (x.Service != "code-intel" || a.Enabled) && (gateway is null || x.Service != "litellm"))
            .Select(x => ProbeAsync(http, x.Name, x.Purpose, x.Url)));
        if (gateway is not null)
        {
            probes = [await gateway, .. probes];
        }
        if (media is null)
        {
            return probes;
        }
        // The picture and video servers, one model loaded at a time (the speech server's several models run in one that always
        // does): what each runs now, the model loaded, loading or failed first. A list that cannot be read leaves the probes as
        // they are: the page shows the services, never an error for one of them.
        Dictionary<string, string> now;
        try
        {
            now = (await media.ListAsync(ct)).Where(x => x.Model.Id is null).GroupBy(x => x.Model.Server, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Now).OrderBy(n => n switch { "loaded" => 0, "loading" or "unloading" => 1, "failed" => 2, _ => 3 }).First(),
                    StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return probes;
        }
        return [.. probes.Select(p => p.Ok || p.Name is not ("Pictures" or "Video")
            ? p
            : now.GetValueOrDefault(p.Name == "Pictures" ? "imagegen" : "videogen") switch
            {
                "unloaded" or "off" => p with { Ok = true, Detail = "idle: its model is not loaded (it loads when asked for)" },
                "waiting" or null => p with { Ok = true, Detail = "idle: its model is not in the library yet (Admin, Models)" },
                _ => p,
            })];
    }

    /// <summary>Is something answering, and how fast. An HTTP error below 500 is still an answer.</summary>
    private static async Task<Probe> ProbeAsync(HttpClient http, string name, string purpose, string url)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var res = await http.GetAsync(new Uri(url));
            return new Probe(name, purpose, (int)res.StatusCode < 500, $"{(int)res.StatusCode} · {sw.ElapsedMilliseconds} ms");
        }
        catch (HttpRequestException ex)
        {
            return new Probe(name, purpose, false, ex.HttpRequestError == HttpRequestError.NameResolutionError ? "not running" : "unreachable");
        }
        catch (TaskCanceledException)
        {
            return new Probe(name, purpose, false, "no answer in time");
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or ArgumentException)
        {
            // Its address is not one (a setting left empty or mistyped): that service is not reached, the others are.
            return new Probe(name, purpose, false, $"its address is not valid ({url}): {ex.Message}");
        }
    }

    private static async Task<IResult> PeopleCsvAsync(AppDbContext db, Access.AccessService access, Ledger ledger, CancellationToken ct)
    {
        var all = await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct);
        var memberships = await access.MembershipsAsync(all, ct);
        var spending = await ledger.ReadAsync(ct);
        // This month's spend, then each kind's spend and the person's own credit of it (empty: no limit).
        var sb = new StringBuilder("username,display_name,email,role,source,disabled,api_access,spend_this_month,"
            + string.Join(',', Credits.Kinds.Select(k => $"{Credits.Name(k)}_spend,{Credits.Name(k)}_credit")) + "\n");
        foreach (var u in all)
        {
            var member = memberships[u.Id];
            sb.AppendJoin(',', new[]
            {
                Csv(u.UserName), Csv(u.DisplayName), Csv(u.Email), member.Snapshot.IsOwner(member.Principal with { Disabled = false }, member.Now) ? "admin" : "member",
                UserSources.Name(u.Source), u.IsDisabled ? "yes" : "no", member.ApiOff ? "off" : "on",
                spending.Of(u.Email).ToString("0.0000", CultureInfo.InvariantCulture),
            }.Concat(Credits.Kinds.SelectMany(k => new[]
            {
                spending.Of(u.Email, k).ToString("0.0000", CultureInfo.InvariantCulture),
                Credits.Of(u, k)?.ToString(CultureInfo.InvariantCulture) ?? "",
            }))).Append('\n');
        }
        return new CsvResult(sb.ToString(), $"people-{DateTime.UtcNow:yyyy-MM-dd}.csv");
    }

    /// <summary>Quoted, and a leading = + - @ neutralised: a spreadsheet would run it as a formula.</summary>
    public static string Csv(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0], StringComparison.Ordinal))
        {
            text = "'" + text;
        }
        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private sealed class CsvResult(string body, string fileName) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.ContentType = "text/csv; charset=utf-8";
            httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            await httpContext.Response.WriteAsync(body);
        }
    }
}
