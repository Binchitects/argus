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

namespace Llm.Api.Operations;

public sealed record Probe(string Name, string Purpose, bool Ok, string Detail);

/// <param name="Repo">One repository to bring up to date (its path); null: a whole pass.</param>
public sealed record IndexRequest(string[]? Branches, bool AllowPartial = false, string? Repo = null);

/// <param name="Included">In the index or out; null: unchanged.</param>
/// <param name="Branches">Branches indexed besides the default one (names or globs); null: unchanged.</param>
public sealed record RepoChoiceRequest(bool? Included = null, string[]? Branches = null);

/// <param name="LeaveOut">Leave the repository out of the index too; else it is built anew by the next pass or an update.</param>
public sealed record RemoveIndexRequest(bool LeaveOut = false);

/// <param name="Schedule">Five-field cron; "": Argus does not reindex by itself.</param>
/// <param name="TimeZone">IANA, e.g. Europe/Berlin.</param>
public sealed record IndexScheduleRequest(string Schedule, string TimeZone);

/// <param name="NewRepos">include or exclude: whether a repository GitLab lists for the first time is indexed.</param>
public sealed record RepoPolicyRequest(string NewRepos);
public sealed record PackRequest(string? Source, string? Sha256, string? Name, string? IndexUrl, string? File = null);

public static class OperationsEndpoints
{
    public static void MapOperations(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("/overview", OverviewAsync);
        g.MapGet("/services", async (IHttpClientFactory f, IOptions<StackOptions> s, IOptions<ArgusOptions> a, IOptions<Dashboards.DashboardOptions> d) =>
            Results.Ok(await ProbeAllAsync(f, s.Value, a.Value, d.Value)));
        g.MapGet("/people.csv", PeopleCsvAsync);

        var argus = g.MapGroup("/argus");
        argus.MapGet("/status", (ArgusAdmin a, CancellationToken ct) => Relay(() => a.GetAsync("index/status", ct), a));
        // The GitLab webhook: on a push or a merge, the repository's index is brought up to date.
        argus.MapGet("/webhook", (ArgusWebhook w, ArgusAdmin a, CancellationToken ct) => Relay(async () => (JsonNode?)await w.ViewAsync(ct), a));
        argus.MapPost("/webhook", (ArgusWebhook w, ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var token = await w.RotateAsync(ct);
            await audit.WriteAsync("argus.webhook_secret", "gitlab");
            // The only time the secret is shown: Argus keeps its hash, the app nothing.
            var view = await w.ViewAsync(ct);
            view["token"] = token;
            return (JsonNode?)view;
        }, a));
        argus.MapDelete("/webhook", (ArgusWebhook w, ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            await w.DisableAsync(ct);
            await audit.WriteAsync("argus.webhook_off", "gitlab");
            return (JsonNode?)await w.ViewAsync(ct);
        }, a));
        argus.MapPost("/index", (IndexRequest body, ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            if (body.Repo is { Length: > 0 } repo)
            {
                var one = await a.PostAsync("index", new JsonObject { ["repo"] = repo.Trim() }, ct);
                await audit.WriteAsync("argus.index_repo", repo.Trim());
                return one;
            }
            var branches = (body.Branches ?? []).Select(b => b.Trim()).Where(b => b.Length > 0).ToArray();
            var res = await a.PostAsync("index", new JsonObject
            {
                ["branches"] = new JsonArray([.. branches.Select(b => JsonValue.Create(b))]),
                ["allow_partial"] = body.AllowPartial,
            }, ct);
            await audit.WriteAsync("argus.index", string.Join(",", branches), detail: body.AllowPartial ? "partial enumeration allowed" : null);
            return res;
        }, a));
        // Which repositories and branches are indexed (Argus keeps the choices).
        argus.MapGet("/repos", (ArgusAdmin a, CancellationToken ct) => Relay(() => a.GetAsync("repos", ct), a));
        argus.MapPost("/repos/discover", (ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var res = await a.PostAsync("repos/discover", new JsonObject(), ct);
            await audit.WriteAsync("argus.repos_discover", null);
            return res;
        }, a));
        argus.MapPatch("/repos/{gitlabId:long}", (long gitlabId, RepoChoiceRequest body, ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
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
            var res = await a.PatchAsync($"repos/{gitlabId}", payload, ct);
            var repo = res?["repo"]?.GetValue<string>() ?? gitlabId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await audit.WriteAsync("argus.repo_choice", repo,
                detail: string.Join("; ", new[] { body.Included is { } i ? (i ? "indexed" : "not indexed") : null, body.Branches is { } b ? $"branches: {string.Join(", ", b)}" : null }.OfType<string>()));
            return res;
        }, a));
        argus.MapGet("/repos/{gitlabId:long}/branches", (long gitlabId, ArgusAdmin a, CancellationToken ct) => Relay(() => a.GetAsync($"repos/{gitlabId}/branches", ct), a));
        // Its index removed now; left out too, or kept in to be built anew.
        argus.MapPost("/repos/{gitlabId:long}/index/remove", (long gitlabId, RemoveIndexRequest body, ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var res = await a.PostAsync($"repos/{gitlabId}/index/remove", new JsonObject { ["leave_out"] = body.LeaveOut }, ct);
            await audit.WriteAsync("argus.index_remove", res?["repo"]?.GetValue<string>() ?? gitlabId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                detail: body.LeaveOut ? "left out" : "kept in, built anew");
            return res;
        }, a));
        argus.MapPut("/repos/settings", (RepoPolicyRequest body, ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var res = await a.PutAsync("repos/settings", new JsonObject { ["new_repos"] = body.NewRepos }, ct);
            await audit.WriteAsync("argus.repo_policy", body.NewRepos);
            return res;
        }, a));
        argus.MapGet("/schedule", ScheduleAsync);
        argus.MapPut("/schedule", SetScheduleAsync);
        argus.MapGet("/packs", (ArgusAdmin a, CancellationToken ct) => Relay(() => a.GetAsync("packs", ct), a));
        argus.MapPost("/packs/{action}", (string action, PackRequest body, ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            JsonObject payload = action switch
            {
                "install" => new() { ["source"] = body.Source, ["sha256"] = body.Sha256 },
                "update" => new() { ["name"] = body.Name, ["index_url"] = body.IndexUrl },
                "remove" => new() { ["name"] = body.Name },
                "load" => new() { ["file"] = body.File },
                _ => throw new ArgusException("Unknown pack action.", 404),
            };
            var res = await a.PostAsync("packs/" + action, payload, ct);
            await audit.WriteAsync("argus.packs." + action, body.Name ?? body.Source ?? body.File);
            return res;
        }, a));
        argus.MapGet("/explore", (string? q, string? repo, int? limit, ArgusAdmin a, CancellationToken ct) =>
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
            argus.MapGet("/explore/" + what, (HttpRequest request, ArgusAdmin a, CancellationToken ct) =>
            {
                var query = string.Join('&', keys.Select(k => $"{k}={Uri.EscapeDataString(request.Query[k].ToString())}"));
                return Relay(() => a.GetAsync($"explore/{what}?{query}", ct), a);
            });
        }
    }

    /// <summary>When Argus reindexes by itself: the schedule, its zone, and the next passes.</summary>
    private static IResult ScheduleAsync(IOptionsMonitor<ArgusIndexOptions> options, TimeProvider clock)
    {
        var o = options.CurrentValue;
        var (cron, zone, problem) = ArgusIndexSchedule.Read(o);
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
            if (cron.ShortestGap(clock.GetUtcNow(), zone) is { } gap && gap < ArgusIndexSchedule.MinInterval)
            {
                return AuthEndpoints.Problem(400, "schedule", $"At most one pass every {ArgusIndexSchedule.MinInterval.TotalMinutes:0} minutes: a pass reads every repository's changes.");
            }
            schedule = cron.Expression;
        }
        await settings.SaveAsync([new Settings.SettingChange("ArgusIndex:Schedule", schedule), new Settings.SettingChange("ArgusIndex:TimeZone", zone.Id)], ct);
        await audit.WriteAsync("argus.index_schedule", schedule.Length > 0 ? schedule : "off", detail: zone.Id);
        return Results.NoContent();
    }

    /// <summary>Argus's answer as it is, or a sentence saying why there is none.</summary>
    private static async Task<IResult> Relay(Func<Task<JsonNode?>> call, ArgusAdmin argus)
    {
        if (!argus.Enabled)
        {
            return Results.Ok(new { configured = false });
        }
        try
        {
            return Results.Ok(await call());
        }
        catch (ArgusException ex)
        {
            return AuthEndpoints.Problem(ex.Status, "argus", ex.Message);
        }
    }

    private static async Task<IResult> OverviewAsync(AppDbContext db, UserManager<AppUser> users, Ledger ledger, ArgusAdmin argus,
        IHttpClientFactory factory, IOptions<StackOptions> stack, IOptions<ArgusOptions> argusOptions, IOptions<Dashboards.DashboardOptions> dashboards, Chat.ChatModels models,
        Dashboards.PromDatasource prom, TimeProvider clock, Replicas replicas, CancellationToken ct)
    {
        var people = await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync(ct);
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Count(u => !u.IsDisabled);
        var spending = new Spending(new Dictionary<string, GatewayUser>(), 0);
        string? warning = null;
        try
        {
            spending = await ledger.ReadAsync(ct);
            warning = spending.Problem;
        }
        catch (GatewayException ex)
        {
            warning = "Spend and credit are missing: " + ex.Message;
        }
        var standing = spending.People;
        var mine = people.Select(p => (p, g: standing.GetValueOrDefault(p.Email ?? ""))).ToList();
        var over = mine.Where(x => x.g is { Budget: > 0 } g && g.Spend >= g.Budget).Select(x => x.p.UserName).ToList();

        var probes = ProbeAllAsync(factory, stack.Value, argusOptions.Value, dashboards.Value);
        var certificate = Certificates.ReadAsync(prom, stack.Value, clock.GetUtcNow(), ct);
        JsonNode? index = null;
        string? indexError = null;
        if (argus.Enabled)
        {
            try
            {
                var st = await argus.GetAsync("index/status", ct);
                index = st?["index"]?.DeepClone();
                if (index is JsonObject o && st?["job"] is JsonObject job)
                {
                    o["returncode"] = job["returncode"]?.DeepClone();
                    o["finished"] = job["finished"]?.DeepClone();
                    o["state"] = job["state"]?.DeepClone();
                }
            }
            catch (ArgusException ex)
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
            index = new { configured = argus.Enabled, summary = index, error = indexError },
            model = models.DefaultName,
            certificate = await certificate,
            // The app's replicas on the database, and whether the one answering leads (runs the once-only background work).
            replicas = new { count = replicas.Count, leads = replicas.IsLeader, id = replicas.Id },
        });
    }

    public static async Task<IReadOnlyList<Probe>> ProbeAllAsync(IHttpClientFactory factory, StackOptions s, ArgusOptions a, Dashboards.DashboardOptions d)
    {
        var http = factory.CreateClient("probe");
        var checks = new List<Task<Probe>>
        {
            ProbeAsync(http, "Model gateway", "LiteLLM: API keys, credit, the chat's model", s.LiteLlmProbeUrl.TrimEnd('/') + "/health/liveliness"),
            ProbeAsync(http, "Prometheus", "metrics and alert rules", s.PrometheusUrl.TrimEnd('/') + "/-/healthy"),
            ProbeAsync(http, "Alertmanager", "firing alerts and their notifications", d.AlertmanagerUrl.TrimEnd('/') + "/-/healthy"),
            ProbeAsync(http, "Loki", "every service's logs", d.LokiUrl.TrimEnd('/') + "/ready"),
            ProbeAsync(http, "Engine", "llama.cpp: the chat models", "http://llamacpp:8080/health"),
            ProbeAsync(http, "Pictures", "the image tool's server", Models.MediaModels.ImageUrl + "/v1/models"),
            ProbeAsync(http, "Video", "the video tool's server", Models.MediaModels.VideoUrl + "/v1/models"),
            ProbeAsync(http, "Speech", "speech to text and text to speech", Models.MediaModels.AudioUrl + "/health"),
            ProbeAsync(http, "Web search", "SearXNG, the web tool's search", "http://searxng:8080/healthz"),
        };
        if (a.Enabled)
        {
            checks.Add(ProbeAsync(http, "Argus", "the code index", a.Url.TrimEnd('/') + "/healthz"));
        }
        return await Task.WhenAll(checks);
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
    }

    private static async Task<IResult> PeopleCsvAsync(AppDbContext db, UserManager<AppUser> users, Ledger ledger, CancellationToken ct)
    {
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var standing = (await ledger.ReadAsync(ct)).People;
        var sb = new StringBuilder("username,display_name,email,role,source,disabled,spend,budget,credit_left\n");
        foreach (var u in await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct))
        {
            var g = standing.GetValueOrDefault(u.Email ?? "");
            var spend = g?.Spend ?? 0;
            var left = g?.Budget is > 0 ? Math.Max(0, g.Budget.Value - spend).ToString("0.0000", CultureInfo.InvariantCulture) : "";
            sb.AppendJoin(',', new[]
            {
                Csv(u.UserName), Csv(u.DisplayName), Csv(u.Email), admins.Contains(u.Id) ? "admin" : "member",
                UserSources.Name(u.Source), u.IsDisabled ? "yes" : "no",
                spend.ToString("0.0000", CultureInfo.InvariantCulture),
                g?.Budget?.ToString(CultureInfo.InvariantCulture) ?? "", left,
            }).Append('\n');
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
