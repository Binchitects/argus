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
/// Rate limits for API keys: requests and tokens a minute. The app decides each person's
/// (their own, else the most generous of their groups that set one, else the company's) and
/// puts it on each of their keys at the gateway; LiteLLM counts every request and refuses the
/// rest with HTTP 429 and Retry-After, in one place for every app replica. The chat's own key
/// carries none: the chat has its fair line. Nothing is limited until an admin sets a limit, but
/// an upgrade keeps a limit a key already carried (<see cref="KeepRow"/>).
/// </summary>
public sealed partial class RateLimits(AppDbContext db, AccessService access, ILiteLlm gateway, SqlDatasource sql, IOptionsMonitor<RateLimitOptions> options, TimeProvider clock,
    ModelCalls running, Audit audit, ILogger<RateLimits> logger)
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

    /// <summary>
    /// The settings row (not a setting of the Settings page) that the upgrade to rate limits adds,
    /// at the time it ran. Until the first key check after it has seen everyone's keys, a limit that
    /// keys made before it carry (an admin's, set in LiteLLM's own pages: the only way to limit a
    /// key before) is kept as their person's own instead of being lifted. That check removes it.
    /// </summary>
    public const string KeepRow = "gateway.rate_limits_keep";

    /// <summary>The most requests a minute an admin may set (0 is no limit).</summary>
    public const int MaxRequests = 1_000_000;

    /// <summary>The most tokens a minute an admin may set (0 is no limit).</summary>
    public const int MaxTokens = 1_000_000_000;

    /// <summary>A rate-limit refusal in the gateway's request log: LiteLLM's own limiter (its error class), never a refusal for credit.</summary>
    public const string Refused = """(s.status = 'failure' and s.metadata->'error_information'->>'error_class' = 'ProxyRateLimitError')""";

    /// <summary>Not a rate-limit refusal: such a request never reached a model, so usage does not count it (it is counted as refused).</summary>
    public const string NotRefused = """coalesce(s.metadata->'error_information'->>'error_class','') <> 'ProxyRateLimitError'""";

    /// <summary>Which limit refused it, from LiteLLM's message ("Limit type: requests"): requests, tokens, at once, or other.</summary>
    public const string Kind = """
        case substring(s.metadata->'error_information'->>'error_message' from 'Limit type: ([a-z_]+)')
          when 'requests' then 'requests' when 'tokens' then 'tokens' when 'max_parallel_requests' then 'at once' else 'other' end
        """;

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

    /// <summary>Puts a person's limits on each of their keys at the gateway; returns how many keys changed.</summary>
    public async Task<int> ApplyAsync(AppUser user, CancellationToken ct = default) =>
        user.Email is { } email ? await ApplyAsync(user, email, (await ForAsync(user, ct)).Key, async () => (await ForAsync(user, ct)).Key, await UpgradeAsync(ct), ct) : 0;

    /// <summary>
    /// What a new key of theirs carries when it replaces these: their limits, once a limit these
    /// carried from before the upgrade is kept as their own (a new key must not drop it).
    /// </summary>
    public async Task<KeyRate> ForNewKeyAsync(AppUser user, IReadOnlyList<GatewayKey> replaced, CancellationToken ct = default)
    {
        var want = (await ForAsync(user, ct)).Key;
        if (replaced.Any(k => (k.Rate ?? KeyRate.None) != want) && await UpgradeAsync(ct) is { } upgrade && await KeepAsync(user, replaced, want, upgrade, ct))
        {
            want = (await ForAsync(user, ct)).Key;
        }
        return want;
    }

    /// <param name="recheck">What the person's keys should carry as the database says it now, asked only when a key is to change.</param>
    /// <param name="upgrade">When the upgrade to rate limits ran, while limits keys carried before it are still to be kept (<see cref="KeepRow"/>).</param>
    private async Task<int> ApplyAsync(AppUser user, string email, KeyRate want, Func<Task<KeyRate>> recheck, DateTimeOffset? upgrade, CancellationToken ct)
    {
        var keys = await gateway.KeysAsync(email, ct);
        if (keys.Any(k => (k.Rate ?? KeyRate.None) != want))
        {
            if (upgrade is { } since)
            {
                await KeepAsync(user, keys, want, since, ct);
            }
            want = await recheck();
        }
        var changed = 0;
        foreach (var key in keys)
        {
            if ((key.Rate ?? KeyRate.None) != want)
            {
                await gateway.SetKeyRateAsync(key.Token, want, ct);
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// Brings everyone's keys to their limits (groups, their members and the company's may have
    /// changed); returns how many keys changed. A person's own limit is read again before their
    /// keys change: an admin may have set it, and put it on their keys, since this sync began.
    /// </summary>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        var upgrade = await UpgradeAsync(ct);
        var groups = await db.Groups.AsNoTracking().Where(g => g.RequestsPerMinute != null || g.TokensPerMinute != null).ToListAsync(ct);
        var ids = groups.Select(g => g.Id).ToList();
        var added = (await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.GroupId)).ToListAsync(ct)).ToLookup(m => m.UserId, m => m.GroupId);
        var changed = 0;
        foreach (var user in await db.Users.AsNoTracking().Where(u => u.Email != null).ToListAsync(ct))
        {
            var mine = groups.Where(g => g.Directory is { } d ? AccessService.InDirectoryGroup(user.DirectoryGroups, d) : added[user.Id].Contains(g.Id))
                .Select(g => (g.Name, g.RequestsPerMinute, g.TokensPerMinute)).ToList();
            var want = Rates((user.RequestsPerMinute, user.TokensPerMinute), mine).Key;
            changed += await ApplyAsync(user, user.Email!, want, async () =>
            {
                var own = await db.Users.AsNoTracking().Where(u => u.Id == user.Id).Select(u => new { u.RequestsPerMinute, u.TokensPerMinute }).SingleOrDefaultAsync(ct);
                return own is null ? want : Rates((own.RequestsPerMinute, own.TokensPerMinute), mine).Key;
            }, upgrade, ct);
        }
        if (upgrade is not null)
        {
            // Everyone's keys were seen: what they carried from before the upgrade is kept, and from now on a key carries the app's limits.
            await db.Settings.Where(s => s.Key == KeepRow).ExecuteDeleteAsync(ct);
        }
        return changed;
    }

    /// <summary>When the upgrade to rate limits ran, while limits keys carried before it are still to be kept; null once they are.</summary>
    private Task<DateTimeOffset?> UpgradeAsync(CancellationToken ct) =>
        db.Settings.AsNoTracking().Where(s => s.Key == KeepRow).Select(s => (DateTimeOffset?)s.UpdatedAt).SingleOrDefaultAsync(ct);

    /// <summary>
    /// Before the app first changes a person's keys after the upgrade: a limit that their keys made
    /// before it carry, stricter than what the app would give them, becomes their own (the strictest
    /// of their keys'), where they have none of their own of that kind; audited. So the upgrade lifts
    /// no limit. True when it kept one.
    /// </summary>
    private async Task<bool> KeepAsync(AppUser user, IReadOnlyList<GatewayKey> keys, KeyRate want, DateTimeOffset upgrade, CancellationToken ct)
    {
        var before = keys.Where(k => k.CreatedAt is not { } made || made < upgrade).Select(k => k.Rate ?? KeyRate.None).ToList();
        var requests = user.RequestsPerMinute is null ? Stricter(before.Min(r => r.RequestsPerMinute), want.RequestsPerMinute) : null;
        var tokens = user.TokensPerMinute is null ? Stricter(before.Min(r => r.TokensPerMinute), want.TokensPerMinute) : null;
        // Only where the database has none of their own either: an admin may have set one since this began.
        if (requests is not null && await db.Users.Where(u => u.Id == user.Id && u.RequestsPerMinute == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.RequestsPerMinute, requests), ct) == 0)
        {
            requests = null;
        }
        if (tokens is not null && await db.Users.Where(u => u.Id == user.Id && u.TokensPerMinute == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TokensPerMinute, tokens), ct) == 0)
        {
            tokens = null;
        }
        if (requests is null && tokens is null)
        {
            return false;
        }
        user.RequestsPerMinute ??= requests;
        user.TokensPerMinute ??= tokens;
        var kept = string.Join("; ", new[] { Words("requests a minute", requests), Words("tokens a minute", tokens) }.OfType<string>());
        await audit.WriteAsync("person.set_limits", user.UserName, detail: $"kept from their API key, set at the gateway before the upgrade: {kept}");
        LogKept(logger, user.UserName ?? user.Email ?? user.Id.ToString(), kept);
        return true;

        static string? Words(string what, int? value) => value is { } n ? $"{what} {n.ToString("N0", CultureInfo.InvariantCulture)}" : null;
    }

    /// <summary>A key's limit when it is stricter than what the app would give; null when it has none, or it is not.</summary>
    private static int? Stricter(int? key, int? want) => key is { } k && (want is not { } w || k < w) ? k : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rate limits: {Person}'s API key carried {Kept}, set at the gateway before the upgrade; kept as their own")]
    private static partial void LogKept(ILogger logger, string person, string kept);

    /// <summary>
    /// What these keys (hashed tokens) used in the last minute, and what the gateway refused the
    /// person (any key of theirs, older ones too: a new key must not hide them) in the last day,
    /// from its request log. LiteLLM writes the log every few seconds, and a streamed answer when
    /// it ends, so the minute is close, not exact. Tokens count as the gateway counts them: what
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
            ["day"] = Logged(now.AddDays(-1)),
        };
        var (requests, used) = await MinuteAsync(tokens, ct);
        // A key's refusals are booked to its person (its user id is their email).
        var refused = await sql.QueryAsync($"""
            select {Kind} as "limit", count(*) as refusals, max(s."startTime") as last
            from "LiteLLM_SpendLogs" s
            where s."startTime" >= @day and {Refused}
              and (s.api_key = any(@keys) or lower(coalesce(nullif(s."user",''), s.metadata->>'user_api_key_user_id', '')) = @me)
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
        var minute = await sql.QueryAsync($"""
            select count(*) as requests, coalesce(sum(greatest(coalesce(s.total_tokens,0) - {UsageEndpoints.Cached}, 0)),0) as tokens
            from "LiteLLM_SpendLogs" s
            where s.api_key = any(@keys) and s."startTime" >= @minute and coalesce(s.metadata->'error_information'->>'error_code','') <> '429'
            """, new Dictionary<string, object> { ["keys"] = tokens.ToArray(), ["minute"] = Logged(clock.GetUtcNow().AddMinutes(-1)) }, ct);
        return (Convert.ToInt64(minute.Rows[0][0] ?? 0L, CultureInfo.InvariantCulture), Convert.ToInt64(minute.Rows[0][1] ?? 0L, CultureInfo.InvariantCulture));
    }

    /// <summary>The log's times are UTC without a zone: compared as they are, its index on startTime serves.</summary>
    private static DateTime Logged(DateTimeOffset at) => DateTime.SpecifyKind(at.UtcDateTime, DateTimeKind.Unspecified);

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
            counted += (int)(await MinuteAsync([.. (await gateway.KeysAsync(user.Email!, ct)).Select(k => k.Token)], ct)).Requests;
        }
        catch (Exception ex) when (ex is GatewayException or Npgsql.NpgsqlException or InvalidOperationException)
        {
            // The gateway or its log cannot be read now: what the app counted itself still holds.
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
