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

public sealed record IndexRequest(string[]? Branches, bool AllowPartial = false);
public sealed record PackRequest(string? Source, string? Sha256, string? Name, string? IndexUrl);

public static class OperationsEndpoints
{
    public static void MapOperations(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("/overview", OverviewAsync);
        g.MapGet("/services", async (IHttpClientFactory f, IOptions<StackOptions> s, IOptions<ArgusOptions> a) => Results.Ok(await ProbeAllAsync(f, s.Value, a.Value)));
        g.MapGet("/model", ModelInfo);
        g.MapGet("/settings", SettingsInfo);
        g.MapGet("/people.csv", PeopleCsvAsync);

        var argus = g.MapGroup("/argus");
        argus.MapGet("/status", (ArgusAdmin a, CancellationToken ct) => Relay(() => a.GetAsync("index/status", ct), a));
        argus.MapPost("/index", (IndexRequest body, ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            var branches = (body.Branches ?? []).Select(b => b.Trim()).Where(b => b.Length > 0).ToArray();
            var res = await a.PostAsync("index", new JsonObject
            {
                ["branches"] = new JsonArray([.. branches.Select(b => JsonValue.Create(b))]),
                ["allow_partial"] = body.AllowPartial,
            }, ct);
            await audit.WriteAsync("argus.index", string.Join(",", branches), detail: body.AllowPartial ? "partial enumeration allowed" : null);
            return res;
        }, a));
        argus.MapGet("/packs", (ArgusAdmin a, CancellationToken ct) => Relay(() => a.GetAsync("packs", ct), a));
        argus.MapPost("/packs/{action}", (string action, PackRequest body, ArgusAdmin a, Identity.Audit audit, CancellationToken ct) => Relay(async () =>
        {
            JsonObject payload = action switch
            {
                "install" => new() { ["source"] = body.Source, ["sha256"] = body.Sha256 },
                "update" => new() { ["name"] = body.Name, ["index_url"] = body.IndexUrl },
                "remove" => new() { ["name"] = body.Name },
                _ => throw new ArgusException("Unknown pack action.", 404),
            };
            var res = await a.PostAsync("packs/" + action, payload, ct);
            await audit.WriteAsync("argus.packs." + action, body.Name ?? body.Source);
            return res;
        }, a));
        argus.MapGet("/explore", (string? q, string? repo, int? limit, ArgusAdmin a, CancellationToken ct) =>
        {
            var query = $"explore?q={Uri.EscapeDataString(q ?? "")}&repo={Uri.EscapeDataString(repo ?? "")}&limit={Math.Clamp(limit ?? 50, 1, 500)}";
            return Relay(() => a.GetAsync(query, ct), a);
        });
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

    private static async Task<IResult> OverviewAsync(AppDbContext db, UserManager<AppUser> users, ILiteLlm gateway, ArgusAdmin argus,
        IHttpClientFactory factory, IOptions<StackOptions> stack, IOptions<ArgusOptions> argusOptions, CancellationToken ct)
    {
        var people = await db.Users.AsNoTracking().Where(u => !u.IsDisabled).ToListAsync(ct);
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Count(u => !u.IsDisabled);
        IReadOnlyDictionary<string, GatewayUser> standing = new Dictionary<string, GatewayUser>();
        string? warning = null;
        try
        {
            standing = await gateway.UsersAsync(ct);
        }
        catch (GatewayException ex)
        {
            warning = "Spend and credit are missing: " + ex.Message;
        }
        var mine = people.Select(p => (p, g: standing.GetValueOrDefault(p.Email ?? ""))).ToList();
        var over = mine.Where(x => x.g is { Budget: > 0 } g && g.Spend >= g.Budget).Select(x => x.p.UserName).ToList();

        var probes = ProbeAllAsync(factory, stack.Value, argusOptions.Value);
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
            spend = standing.Values.Sum(g => g.Spend),
            overCredit = over,
            warning,
            services = await probes,
            index = new { configured = argus.Enabled, summary = index, error = indexError },
            model = stack.Value.ModelName,
        });
    }

    public static async Task<IReadOnlyList<Probe>> ProbeAllAsync(IHttpClientFactory factory, StackOptions s, ArgusOptions a)
    {
        var http = factory.CreateClient("probe");
        var checks = new List<Task<Probe>>
        {
            ProbeAsync(http, "Model gateway", "LiteLLM: API keys, credit, the chat's model", s.LiteLlmProbeUrl.TrimEnd('/') + "/health/liveliness"),
            ProbeAsync(http, "Prometheus", "metrics and alert rules", s.PrometheusUrl.TrimEnd('/') + "/-/healthy"),
            ProbeAsync(http, "Grafana", "the dashboards not yet in the app", s.GrafanaProbeUrl.TrimEnd('/') + "/api/health"),
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

    private static IResult ModelInfo(IOptions<StackOptions> options)
    {
        var s = options.Value;
        return Results.Ok(new
        {
            running = new
            {
                name = s.ModelName, file = s.ModelFile, context = s.ModelContext, maxOutput = s.ModelMaxOutput,
                mtpDraftMax = s.MtpDraftMax, gpuPowerLimitW = s.GpuPowerLimitW, cpuPowerLimitW = s.CpuPowerLimitW,
                thinkingPresets = s.ThinkingPresets,
            },
            prices = new { input = s.PriceInputPerMtok, cachedInput = s.PriceCachedInputPerMtok, output = s.PriceOutputPerMtok },
            samples = EnvSamples.Read(s.EnvSamplesDir),
        });
    }

    /// <summary>An allow-list of names, never the environment: a dump is how a console leaks a master key into a screenshot.</summary>
    private static IResult SettingsInfo(IOptions<StackOptions> stack, IOptions<ArgusOptions> argus, IOptions<Identity.AuthOptions> auth, IOptions<Ldap.LdapOptions> ldap)
    {
        var s = stack.Value;
        var a = argus.Value;
        object Row(string name, string? value, string purpose) => new { name, value = string.IsNullOrEmpty(value) ? null : value, purpose };
        return Results.Ok(new[]
        {
            new { title = "Deployment", rows = new[]
            {
                Row("LLM_DOMAIN", auth.Value.Domain, "the domain every address hangs off"),
                Row("COMPOSE_PROFILES", s.ComposeProfiles, "which parts of the stack run"),
                Row("ADMIN_GROUP", auth.Value.AdminGroup, "the group name admins carry in other services"),
                Row("LDAP_URL", ldap.Value.Url, "the company directory, if any"),
            } },
            new { title = "Model", rows = new[]
            {
                Row("MODEL_NAME", s.ModelName, "what the gateway serves"),
                Row("MODEL_CONTEXT", s.ModelContext, "token window"),
                Row("MODEL_MAX_OUTPUT", s.ModelMaxOutput, "longest reply"),
                Row("MODEL_REASONING_EFFORT", s.ModelReasoningEffort, "default thinking level"),
                Row("MODEL_ENABLE_THINKING", s.ModelEnableThinking, "whether it thinks at all"),
                Row("THINKING_PRESETS", s.ThinkingPresets, "per-chat presets offered in chat"),
            } },
            new { title = "Prices (per 1M tokens)", rows = new[]
            {
                Row("PRICE_INPUT_PER_MTOK", s.PriceInputPerMtok, "input, cache miss"),
                Row("PRICE_CACHED_INPUT_PER_MTOK", s.PriceCachedInputPerMtok, "input served from the prefix cache"),
                Row("PRICE_OUTPUT_PER_MTOK", s.PriceOutputPerMtok, "generated tokens, reasoning included"),
                Row("LITELLM_DEFAULT_USER_BUDGET", s.DefaultUserBudget, "default credit per person"),
            } },
            new { title = "Argus", rows = new[]
            {
                Row("ARGUS_URL", a.Enabled ? a.Url : null, "where the code index is reached"),
                Row("ARGUS_GITLAB_URL", a.GitlabUrl, "the GitLab it indexes"),
                Row("ARGUS_GITLAB_AUTH", a.GitlabAuth, "token or password"),
                Row("ARGUS_GITLAB_USERNAME", a.GitlabUsername, "who it signs in as, in password mode"),
            } },
        });
    }

    private static async Task<IResult> PeopleCsvAsync(AppDbContext db, UserManager<AppUser> users, ILiteLlm gateway, CancellationToken ct)
    {
        var admins = (await users.GetUsersInRoleAsync(Roles.Admin)).Select(u => u.Id).ToHashSet();
        var standing = await gateway.UsersAsync(ct);
        var sb = new StringBuilder("username,display_name,email,role,source,disabled,spend,budget,credit_left\n");
        foreach (var u in await db.Users.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct))
        {
            var g = standing.GetValueOrDefault(u.Email ?? "");
            var spend = g?.Spend ?? 0;
            var left = g?.Budget is > 0 ? Math.Max(0, g.Budget.Value - spend).ToString("0.0000", CultureInfo.InvariantCulture) : "";
            sb.AppendJoin(',', new[]
            {
                Csv(u.UserName), Csv(u.DisplayName), Csv(u.Email), admins.Contains(u.Id) ? "admin" : "member",
                u.Source == UserSource.Ldap ? "ldap" : "local", u.IsDisabled ? "yes" : "no",
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
