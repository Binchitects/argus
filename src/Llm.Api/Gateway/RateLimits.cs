using System.Globalization;
using Llm.Api.Access;
using Llm.Api.Dashboards;
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
/// carries none: the chat has its fair line. Nothing is limited until an admin sets a limit.
/// </summary>
public sealed class RateLimits(AppDbContext db, AccessService access, ILiteLlm gateway, SqlDatasource sql, IOptionsMonitor<RateLimitOptions> options, TimeProvider clock)
{
    /// <summary>The most requests a minute an admin may set (0 is no limit).</summary>
    public const int MaxRequests = 1_000_000;

    /// <summary>The most tokens a minute an admin may set (0 is no limit).</summary>
    public const int MaxTokens = 1_000_000_000;

    /// <summary>A rate-limit refusal in the gateway's request log: LiteLLM's own limiter (its error class), never a refusal for credit.</summary>
    public const string Refused = """(s.status = 'failure' and s.metadata->'error_information'->>'error_class' = 'ProxyRateLimitError')""";

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
        return Rates(user, [.. groups.Select(g => (g.Name, g.RequestsPerMinute, g.TokensPerMinute))]);
    }

    private PersonRates Rates(AppUser user, IReadOnlyList<(string Name, int? Requests, int? Tokens)> groups)
    {
        var company = options.CurrentValue;
        return new PersonRates(
            Resolve(user.RequestsPerMinute, groups.Select(g => (g.Name, g.Requests)), company.RequestsPerMinute),
            Resolve(user.TokensPerMinute, groups.Select(g => (g.Name, g.Tokens)), company.TokensPerMinute));
    }

    /// <summary>Puts a person's limits on each of their keys at the gateway; returns how many keys changed.</summary>
    public async Task<int> ApplyAsync(AppUser user, CancellationToken ct = default) =>
        user.Email is { } email ? await ApplyAsync(email, (await ForAsync(user, ct)).Key, ct) : 0;

    private async Task<int> ApplyAsync(string email, KeyRate want, CancellationToken ct)
    {
        var changed = 0;
        foreach (var key in await gateway.KeysAsync(email, ct))
        {
            if ((key.Rate ?? KeyRate.None) != want)
            {
                await gateway.SetKeyRateAsync(key.Token, want, ct);
                changed++;
            }
        }
        return changed;
    }

    /// <summary>Brings everyone's keys to their limits (groups, their members and the company's may have changed); returns how many keys changed.</summary>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        var groups = await db.Groups.AsNoTracking().Where(g => g.RequestsPerMinute != null || g.TokensPerMinute != null).ToListAsync(ct);
        var ids = groups.Select(g => g.Id).ToList();
        var added = (await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.GroupId)).ToListAsync(ct)).ToLookup(m => m.UserId, m => m.GroupId);
        var changed = 0;
        foreach (var user in await db.Users.AsNoTracking().Where(u => u.Email != null).ToListAsync(ct))
        {
            var mine = groups.Where(g => g.Directory is { } d ? AccessService.InDirectoryGroup(user.DirectoryGroups, d) : added[user.Id].Contains(g.Id))
                .Select(g => (g.Name, g.RequestsPerMinute, g.TokensPerMinute)).ToList();
            changed += await ApplyAsync(user.Email!, Rates(user, mine).Key, ct);
        }
        return changed;
    }

    /// <summary>
    /// What these keys (hashed tokens) used in the last minute, and what the gateway refused them
    /// in the last day, from its request log. LiteLLM writes the log every few seconds, and a
    /// streamed answer when it ends, so the minute is close, not exact. Tokens count as the
    /// gateway counts them: what the model read and wrote, less the prompt it read from its cache.
    /// </summary>
    public async Task<RateUse> UseAsync(IReadOnlyCollection<string> tokens, CancellationToken ct = default)
    {
        if (tokens.Count == 0)
        {
            return new RateUse(0, 0, []);
        }
        var now = clock.GetUtcNow();
        // The log's times are UTC without a zone: compared as they are, its index on startTime serves.
        DateTime Logged(DateTimeOffset at) => DateTime.SpecifyKind(at.UtcDateTime, DateTimeKind.Unspecified);
        var p = new Dictionary<string, object>
        {
            ["keys"] = tokens.ToArray(),
            ["minute"] = Logged(now.AddMinutes(-1)),
            ["day"] = Logged(now.AddDays(-1)),
        };
        var used = await sql.QueryAsync($"""
            select count(*) as requests, coalesce(sum(greatest(coalesce(s.total_tokens,0) - {UsageEndpoints.Cached}, 0)),0) as tokens
            from "LiteLLM_SpendLogs" s
            where s.api_key = any(@keys) and s."startTime" >= @minute and coalesce(s.metadata->'error_information'->>'error_code','') <> '429'
            """, p, ct);
        var refused = await sql.QueryAsync($"""
            select {Kind} as "limit", count(*) as refusals, max(s."startTime") as last
            from "LiteLLM_SpendLogs" s
            where s.api_key = any(@keys) and s."startTime" >= @day and {Refused}
            group by 1 order by 2 desc
            """, p, ct);
        var row = used.Rows[0];
        return new RateUse(Convert.ToInt64(row[0] ?? 0L, CultureInfo.InvariantCulture), Convert.ToInt64(row[1] ?? 0L, CultureInfo.InvariantCulture),
            [.. refused.Rows.Select(r => new Refusal((string)r[0]!, Convert.ToInt64(r[1] ?? 0L, CultureInfo.InvariantCulture), Utc(r[2])))]);
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
            use = await UseAsync([.. keys.Select(k => k.Token)], ct);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            // The limits still show; only what was used is missing.
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
