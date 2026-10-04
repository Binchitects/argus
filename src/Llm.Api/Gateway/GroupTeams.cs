using Llm.Core.Access;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>
/// Groups with credit, mirrored at the gateway as LiteLLM teams (ids "group-..."), so the
/// gateway itself holds API keys to a group's credit: each team has the credit per month
/// (shared, or each member's), the group's members, and their keys. A key is in one team
/// only: a person in several groups with credit has their keys in the one with the least
/// (the app's own checks, <see cref="Credit"/>, hold them to all of them). The chat's key is
/// in no team; the app checks the chat before each answer.
/// </summary>
public sealed class GroupTeams(AppDbContext db, Credit credit, ILiteLlm gateway)
{
    /// <summary>A team's budget is per calendar month (LiteLLM resets it on the first, UTC).</summary>
    public const string Duration = "1mo";

    public static string TeamId(Guid group) => LiteLlmClient.TeamPrefix + group.ToString("N");

    /// <summary>Brings the teams, their members and everyone's keys in step with the groups; returns how many things changed.</summary>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        var groups = await db.Groups.AsNoTracking().Where(g => g.Credit != null).ToListAsync(ct);
        var existing = (await gateway.TeamsAsync(ct)).ToDictionary(t => t.Id);
        if (groups.Count == 0 && existing.Count == 0)
        {
            return 0;
        }
        var changed = 0;
        var teamOf = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var members = (await credit.MembersAsync(g, ct)).Order(StringComparer.Ordinal).ToList();
            var want = new GatewayTeam(TeamId(g.Id), g.Name, g.CreditPerMember ? null : g.Credit, g.CreditPerMember ? g.Credit : null, members);
            if (!existing.TryGetValue(want.Id, out var now) || now.Alias != want.Alias || now.Budget != want.Budget || now.MemberBudget != want.MemberBudget ||
                !now.Members.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(members, StringComparer.OrdinalIgnoreCase))
            {
                await gateway.SetTeamAsync(want, Duration, ct);
                changed++;
            }
            foreach (var email in members)
            {
                // The least credit wins the key.
                if (!teamOf.TryGetValue(email, out var other) || g.Credit < other.Credit)
                {
                    teamOf[email] = g;
                }
            }
        }
        var stillHolding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var email in await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => u.Email!).ToListAsync(ct))
        {
            var want = teamOf.TryGetValue(email, out var g) ? TeamId(g.Id) : null;
            foreach (var key in await gateway.KeysAsync(email, ct))
            {
                // A key some other way in a team of its own (made by hand at the gateway) is left alone.
                if (key.TeamId != want && (key.TeamId is null || key.TeamId.StartsWith(LiteLlmClient.TeamPrefix, StringComparison.Ordinal)))
                {
                    await gateway.SetKeyTeamAsync(key.Token, want, ct);
                    changed++;
                }
                else if (key.TeamId is { } kept)
                {
                    stillHolding.Add(kept);
                }
            }
        }
        // A team no group needs goes, once no key is in it: the gateway deletes a team's keys with it.
        var wanted = groups.Select(g => TeamId(g.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var team in existing.Keys.Where(t => !wanted.Contains(t) && !stillHolding.Contains(t)))
        {
            await gateway.DeleteTeamAsync(team, ct);
            changed++;
        }
        return changed;
    }
}
