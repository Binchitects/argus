using System.Globalization;
using Llm.Api.Access;
using Llm.Api.Dashboards;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Gateway;

/// <summary>Configuration section "Gateway": the company's rate limits for API keys (Settings → API keys). 0: no limit.</summary>
public sealed class RateLimitOptions
{
    /// <summary>Requests a minute each API key may send to the gateway.</summary>
    public int RequestsPerMinute { get; set; }

    /// <summary>Tokens a minute each API key may use at the gateway (what the model reads and writes).</summary>
    public int TokensPerMinute { get; set; }
}

/// <summary>A key's rate limits at the gateway, a minute each; null: no limit.</summary>
public sealed record KeyRate(int? RequestsPerMinute, int? TokensPerMinute)
{
    /// <summary>No limit at all.</summary>
    public static readonly KeyRate None = new(null, null);
}

/// <summary>One limit and where it comes from: "person" (their own), "group" (named), "company" (Settings), or "none" (no limit set anywhere).</summary>
/// <param name="Value">The limit a minute; null: no limit.</param>
public sealed record RateLimit(int? Value, string From, string? Group = null);

/// <summary>A person's limits, each with where it comes from.</summary>
public sealed record PersonRates(RateLimit RequestsPerMinute, RateLimit TokensPerMinute)
{
    /// <summary>What each of their keys carries at the gateway.</summary>
    public KeyRate Key => new(RequestsPerMinute.Value, TokensPerMinute.Value);
}

/// <summary>What the gateway refused a key for, in a time: "requests" or "tokens" (a minute), "at once", or "other".</summary>
public sealed record Refusal(string Limit, long Count, DateTimeOffset Last);

/// <summary>What a person's keys used in the last minute (requests the gateway took, tokens it counted), and what it refused them in the last day.</summary>
public sealed record RateUse(long Requests, long Tokens, IReadOnlyList<Refusal> Refused);

/// <summary>
/// Rate limits for API keys: requests and tokens a minute. The app decides each person's (their own, else the most
/// generous of their groups that set one, else the company's); the gateway counts each key's minute in the database
/// (<see cref="RateWindows"/>, one count for every app replica) and refuses the rest with HTTP 429 and Retry-After
/// (<see cref="Admission"/>). The chat carries none: it has its fair line. Nothing is limited until an admin sets a limit.
/// </summary>
public sealed partial class RateLimits(AppDbContext db, AccessService access, ApiKeys keys, LedgerSql ledger, IOptionsMonitor<RateLimitOptions> options, TimeProvider clock,
    ModelCalls running)
{
    /// <summary>
    /// The chat's tools that reach a model: pictures, speech, video, and deep research ("research",
    /// which Arena MCP runs as a chat of the person's own). Through Arena MCP they go with the chat's
    /// own key (video with none), so the gateway cannot count them against the person's key: the app
    /// counts each call as one request of theirs a minute (<see cref="StartModelCallAsync"/>). The
    /// requests a deep research run makes are the chat's: the research a day and the fair line hold them.
    /// </summary>
    public static readonly string[] ModelTools = ["image", "speech", "video", "research"];

    /// <summary>The audit log's word for an Arena MCP call refused for the person's requests a minute.</summary>
    public const string McpRefused = "mcp.rate_limited";

    /// <summary>The most requests a minute an admin may set (0 is no limit).</summary>
    public const int MaxRequests = 1_000_000;

    /// <summary>The most tokens a minute an admin may set (0 is no limit).</summary>
    public const int MaxTokens = 1_000_000_000;

    /// <summary>
    /// One limit: their own (0: no limit), else the most generous of their groups that set one
    /// (a group's 0 is no limit, so it wins), else the company's (0: none).
    /// </summary>
    public static RateLimit Resolve(int? own, IEnumerable<(string Name, int? Value)> groups, int company)
    {
        if (own is { } mine)
        {
            return new RateLimit(mine > 0 ? mine : null, "person");
        }
        var set = groups.Where(g => g.Value is not null).ToList();
        if (set.Count > 0)
        {
            var best = set.OrderBy(g => g.Value == 0 ? 0 : 1).ThenByDescending(g => g.Value).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).First();
            return new RateLimit(best.Value > 0 ? best.Value : null, "group", best.Name);
        }
        return company > 0 ? new RateLimit(company, "company") : new RateLimit(null, "none");
    }

    /// <summary>A person's limits and where each comes from.</summary>
    public async Task<PersonRates> ForAsync(AppUser user, CancellationToken ct = default)
    {
        var member = await access.MembershipAsync(user, ct);
        var groups = await db.Groups.AsNoTracking().Where(g => member.Groups.Contains(g.Id) && (g.RequestsPerMinute != null || g.TokensPerMinute != null))
            .Select(g => new { g.Name, g.RequestsPerMinute, g.TokensPerMinute }).ToListAsync(ct);
        return Rates((user.RequestsPerMinute, user.TokensPerMinute), [.. groups.Select(g => (g.Name, g.RequestsPerMinute, g.TokensPerMinute))]);
    }

    private PersonRates Rates((int? Requests, int? Tokens) own, IReadOnlyList<(string Name, int? Requests, int? Tokens)> groups)
    {
        var company = options.CurrentValue;
        return new PersonRates(
            Resolve(own.Requests, groups.Select(g => (g.Name, g.Requests)), company.RequestsPerMinute),
            Resolve(own.Tokens, groups.Select(g => (g.Name, g.Tokens)), company.TokensPerMinute));
    }

    /// <summary>
    /// What these keys (hashed tokens) used in the last minute, and what the gateway refused the
    /// person (any key of theirs, older ones too: a new key must not hide them) in the last day,
    /// from its ledger. The gateway writes it every second or so, and a streamed answer when it
    /// ends, so the minute is close, not exact. Tokens count as the gateway counts them: what
    /// the model read and wrote, less the prompt it read from its cache.
    /// </summary>
    public async Task<RateUse> UseAsync(IReadOnlyCollection<string> tokens, string? email, CancellationToken ct = default)
    {
        if (tokens.Count == 0 && email is null)
        {
            return new RateUse(0, 0, []);
        }
        var now = clock.GetUtcNow();
        var p = new Dictionary<string, object>
        {
            ["keys"] = tokens.ToArray(),
            ["me"] = email?.ToLowerInvariant() ?? "",
            ["day"] = now.AddDays(-1),
        };
        var (requests, used) = await MinuteAsync(tokens, ct);
        // A key's refusals are booked to its person: their own, by any key of theirs.
        var refused = await ledger.QueryAsync("""
            select case r.refusal when 'rpm' then 'requests' when 'tpm' then 'tokens' when 'parallel' then 'at once' else 'other' end as "limit",
                   count(*) as refusals, max(r.started_at) as last
            from gateway.requests r
            where r.started_at >= @day and r.status = 'refused' and r.refusal in ('rpm', 'tpm', 'parallel')
              and (r.key_hash = any(@keys) or r.person = @me)
            group by 1 order by 2 desc
            """, p, ct);
        return new RateUse(requests, used,
            [.. refused.Rows.Select(r => new Refusal((string)r[0]!, Convert.ToInt64(r[1] ?? 0L, CultureInfo.InvariantCulture), Utc(r[2])))]);
    }

    /// <summary>What these keys (hashed tokens) sent in the last minute that the gateway took: requests, and tokens less those read from the cache.</summary>
    private async Task<(long Requests, long Tokens)> MinuteAsync(IReadOnlyCollection<string> tokens, CancellationToken ct)
    {
        if (tokens.Count == 0)
        {
            return (0, 0);
        }
        var minute = await ledger.QueryAsync("""
            select count(*) as requests, coalesce(sum(greatest(r.total_tokens - r.cached_tokens, 0)),0) as tokens
            from gateway.requests r
            where r.key_hash = any(@keys) and r.started_at >= @minute and r.status <> 'refused'
            """, new Dictionary<string, object> { ["keys"] = tokens.ToArray(), ["minute"] = clock.GetUtcNow().AddMinutes(-1) }, ct);
        return (Convert.ToInt64(minute.Rows[0][0] ?? 0L, CultureInfo.InvariantCulture), Convert.ToInt64(minute.Rows[0][1] ?? 0L, CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The person's Arena MCP calls of <see cref="ModelTools"/> that started since then and have ended
    /// (the audit log has each one when it ends, at the time it started).
    /// </summary>
    private Task<int> ModelCallsSinceAsync(Guid user, DateTimeOffset since, CancellationToken ct) =>
        db.AuditEvents.AsNoTracking().CountAsync(a => a.Action == "mcp.call" && a.ActorId == user && a.At >= since && ModelTools.Contains(a.Target!), ct);

    /// <summary>
    /// Before an Arena MCP call of a tool that reaches a model, started now: whether the person's
    /// requests a minute leave room for it. Each request counts in the minute it started, as the
    /// gateway counts them, however long it runs: what their keys sent in the last minute (the
    /// gateway's log), their Arena MCP model calls that started in it and have ended (the audit log,
    /// one for every replica), and those that started in it and run now on this replica. Room: a slot
    /// (none without a limit) to dispose of once the call is audited. No room: why. Tokens a minute
    /// does not apply: pictures, speech and video have no tokens, as the gateway counts them, and a deep
    /// research run's are the chat's.
    /// </summary>
    public async Task<(IDisposable? Slot, string? Refusal)> StartModelCallAsync(AppUser user, DateTimeOffset started, CancellationToken ct)
    {
        if ((await ForAsync(user, ct)).RequestsPerMinute.Value is not { } most)
        {
            return (null, null);
        }
        var counted = await ModelCallsSinceAsync(user.Id, started.AddMinutes(-1), ct);
        try
        {
            counted += (int)(await MinuteAsync([.. (await keys.KeysAsync(user, ct)).Select(k => k.Token)], ct)).Requests;
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            // The ledger cannot be read now: what the app counted itself still holds.
        }
        if (running.TryStart(user.Id, started, now => counted + now < most) is { } slot)
        {
            return (slot, null);
        }
        return (null, $"Your API key reached its limit of {(most == 1 ? "1 request" : $"{most.ToString(CultureInfo.InvariantCulture)} requests")} a minute; pictures, speech, video and deep research " +
            "started through Arena MCP count too. Try again in a minute. Your account → API key shows your limits and what you used.");
    }

    /// <summary>The log's times are UTC without a zone.</summary>
    private static DateTimeOffset Utc(object? value) => value switch
    {
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        DateTimeOffset o => o,
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) => t,
        _ => default,
    };

    /// <summary>
    /// A person's limits as their key's page and their admin page show them: each limit with where
    /// it comes from, their own (for the admin's form), requests at once, and what the keys used
    /// in the last minute and were refused in the last day (null when the request log cannot be read).
    /// </summary>
    public async Task<object> ViewAsync(AppUser user, IReadOnlyList<GatewayKey> keys, int? atOnce, CancellationToken ct = default)
    {
        var rates = await ForAsync(user, ct);
        RateUse? use = null;
        try
        {
            use = await UseAsync([.. keys.Select(k => k.Token)], user.Email, ct);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            // The limits still show; only what was used is missing.
        }
        if (use is not null)
        {
            // Arena MCP's pictures, speech, video and deep research count against requests a minute too (with a limit, those that run
            // now on this replica as well), and their refusals are in the audit log.
            var now = clock.GetUtcNow();
            var day = now.AddDays(-1);
            var mcp = await ModelCallsSinceAsync(user.Id, now.AddMinutes(-1), ct) + running.Running(user.Id, now);
            var refused = await db.AuditEvents.AsNoTracking().Where(a => a.Action == McpRefused && a.ActorId == user.Id && a.At >= day).Select(a => a.At).ToListAsync(ct);
            var requests = use.Refused.FirstOrDefault(r => r.Limit == "requests");
            use = use with
            {
                Requests = use.Requests + mcp,
                Refused = refused.Count == 0 ? use.Refused :
                [
                    new Refusal("requests", (requests?.Count ?? 0) + refused.Count, refused.Append(requests?.Last ?? default).Max()),
                    .. use.Refused.Where(r => r.Limit != "requests"),
                ],
            };
        }
        return new
        {
            requestsPerMinute = new { value = rates.RequestsPerMinute.Value, from = rates.RequestsPerMinute.From, group = rates.RequestsPerMinute.Group },
            tokensPerMinute = new { value = rates.TokensPerMinute.Value, from = rates.TokensPerMinute.From, group = rates.TokensPerMinute.Group },
            own = new { requestsPerMinute = user.RequestsPerMinute, tokensPerMinute = user.TokensPerMinute },
            atOnce,
            used = use is null ? null : new { requests = use.Requests, tokens = use.Tokens },
            refused = use?.Refused.Select(r => new { limit = r.Limit, count = r.Count, last = r.Last }),
        };
    }
}

/// <summary>
/// Arena MCP's calls that reach a model, running now on this replica, by person, with when each
/// started: until the audit log has them, they count against the person's requests a minute in the
/// minute they started (so calls started at once are counted too), and not after it, however long
/// they run.
/// </summary>
public sealed class ModelCalls
{
    private readonly Dictionary<Guid, List<DateTimeOffset>> _running = [];

    /// <summary>
    /// A slot for one more call, started at <paramref name="at"/>, when <paramref name="may"/> allows it
    /// given how many of theirs run now that started in the minute before; null when not.
    /// </summary>
    public IDisposable? TryStart(Guid user, DateTimeOffset at, Func<int, bool> may)
    {
        lock (_running)
        {
            if (!may(Counted(user, at)))
            {
                return null;
            }
            if (!_running.TryGetValue(user, out var started))
            {
                _running[user] = started = [];
            }
            started.Add(at);
            return new Slot(this, user, at);
        }
    }

    /// <summary>How many of the person's calls run now that started in the minute before <paramref name="at"/>.</summary>
    public int Running(Guid user, DateTimeOffset at)
    {
        lock (_running)
        {
            return Counted(user, at);
        }
    }

    private int Counted(Guid user, DateTimeOffset at) =>
        _running.TryGetValue(user, out var started) ? started.Count(s => s >= at.AddMinutes(-1)) : 0;

    private void End(Guid user, DateTimeOffset at)
    {
        lock (_running)
        {
            if (_running.TryGetValue(user, out var started) && started.Remove(at) && started.Count == 0)
            {
                _running.Remove(user);
            }
        }
    }

    private sealed class Slot(ModelCalls calls, Guid user, DateTimeOffset at) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                calls.End(user, at);
            }
        }
    }
}
