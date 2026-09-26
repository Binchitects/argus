using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Dashboards;

/// <summary>
/// A person's own usage: the same rows and the same attribution as the
/// "Usage by person" dashboard, filtered to them, with their email as a parameter.
/// </summary>
public static class UsageEndpoints
{
    /// <summary>Who a request belongs to. Identical to the dashboards' expression, so the numbers agree.</summary>
    public const string Person =
        """coalesce(nullif(s."user",''), s.metadata->>'user_api_key_user_id', v.user_id, nullif(s.end_user,''), '(unattributed)')""";

    private const string Cached =
        """coalesce((s.metadata->'usage_object'->'prompt_tokens_details'->>'cached_tokens')::bigint, 0)""";

    private const string From = """ from "LiteLLM_SpendLogs" s left join "LiteLLM_VerificationToken" v on v.token = s.api_key """;

    public static void MapUsage(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/usage/me", MeAsync).RequireAuthorization();

    private static async Task<IResult> MeAsync(DateTimeOffset from, DateTimeOffset to, long? intervalMs, ClaimsPrincipal principal,
        UserManager<AppUser> users, SqlDatasource sql, CancellationToken ct)
    {
        if (to <= from || to - from > DashboardEndpoints.MaxRange)
        {
            return AuthEndpoints.Problem(400, "range", "The time range must be positive and at most 400 days.");
        }
        var me = (await users.GetUserAsync(principal))!;
        var interval = intervalMs is > 0 ? TimeSpan.FromMilliseconds(intervalMs.Value) : SqlMacros.RoundInterval((to - from) / 60);
        var p = new Dictionary<string, object>
        {
            ["me"] = me.Email!.ToLowerInvariant(),
            ["from"] = from.UtcDateTime,
            ["to"] = to.UtcDateTime,
            ["secs"] = interval.TotalSeconds,
        };
        var where = $""" where lower({Person}) = @me and s."startTime" between @from and @to """;
        try
        {
            var totals = await sql.QueryAsync($"""
                select count(*) as requests,
                       coalesce(sum(greatest(coalesce(s.prompt_tokens,0) - {Cached}, 0)),0) as input_miss,
                       coalesce(sum({Cached}),0) as input_hit,
                       coalesce(sum(s.completion_tokens),0) as output,
                       coalesce(sum(s.spend),0) as cost
                {From}{where}
                """, p, ct);
            var byTime = await sql.QueryAsync($"""
                select floor(extract(epoch from s."startTime")/@secs)*@secs as "time",
                       sum(greatest(coalesce(s.prompt_tokens,0) - {Cached}, 0)) as "Input, cache miss",
                       sum({Cached}) as "Input, cache hit",
                       sum(coalesce(s.completion_tokens,0)) as "Output",
                       sum(coalesce(s.spend,0)) as cost
                {From}{where}
                group by 1 order by 1
                """, p, ct);
            var byModel = await sql.QueryAsync($"""
                select s.model as model, count(*) as requests, coalesce(sum(s.total_tokens),0) as tokens, coalesce(sum(s.spend),0) as cost
                {From}{where}
                group by 1 order by cost desc
                """, p, ct);
            var t = totals.Rows[0];
            return Results.Ok(new
            {
                intervalMs = (long)interval.TotalMilliseconds,
                totals = new { requests = t[0], inputMiss = t[1], inputHit = t[2], output = t[3], cost = t[4] },
                series = SeriesShaping.ToSeries(byTime),
                models = byModel.Rows.Select(r => new { model = r[0], requests = r[1], tokens = r[2], cost = r[3] }),
            });
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return AuthEndpoints.Problem(503, "usage", "Usage cannot be read right now: " + ex.Message);
        }
    }
}
