using System.Globalization;
using Llm.Api.Dashboards;

namespace Llm.Api.Gateway;

/// <param name="People">Each person's spend and budget, by email.</param>
/// <param name="Total">Everything spent, by anyone (unattributed requests too).</param>
/// <param name="Problem">Why the spend is the gateway's own counters instead (its request log could not be read).</param>
public sealed record Spending(IReadOnlyDictionary<string, GatewayUser> People, decimal Total, string? Problem = null);

/// <summary>
/// What each person has spent, and their budget. The budget is the gateway's; the spend
/// is what its request log puts to them over every path (chat, API keys, agents), by the
/// same rule as the usage dashboards, so every page says what the dashboards say.
/// LiteLLM's own counters split a person in two (the chat is booked to them as an end
/// user, their keys as an internal user): the internal one alone missed the chat.
/// </summary>
public sealed partial class Ledger(ILiteLlm gateway, SqlDatasource sql, ILogger<Ledger> logger)
{
    public async Task<Spending> ReadAsync(CancellationToken ct = default)
    {
        var users = await gateway.UsersAsync(ct);
        Dictionary<string, decimal> spent;
        try
        {
            var rows = await sql.QueryAsync($"""
                select lower({UsageEndpoints.Person}) as person, coalesce(sum(s.spend),0) as spend
                {UsageEndpoints.From}
                group by 1
                """, ct);
            spent = rows.Rows.ToDictionary(r => (string)r[0]!, r => Convert.ToDecimal(r[1] ?? 0m, CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException)
        {
            LogUnread(logger, ex);
            return new Spending(users, users.Values.Sum(u => u.Spend), "Spend is the gateway's own count (its request log cannot be read): " + ex.Message);
        }
        var people = new Dictionary<string, GatewayUser>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, u) in users)
        {
            people[id] = u with { Spend = spent.GetValueOrDefault(id) };
        }
        foreach (var (id, s) in spent)
        {
            if (id.Contains('@') && !people.ContainsKey(id))
            {
                people[id] = new GatewayUser(id, s, null);
            }
        }
        return new Spending(people, spent.Values.Sum());
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The gateway's request log could not be read; spend is the gateway's own counters")]
    private static partial void LogUnread(ILogger logger, Exception ex);
}
