using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>
/// The LiteLLM teams (ids "group-...") the app once made to hold API keys to a group's credit at the gateway. Credits are
/// the app's now, kind by kind (<see cref="Credit"/>, checked for each API request by the guardrail): the keys leave those
/// teams, and the teams go. A key some other way in a team of its own (made by hand at the gateway) is left alone.
/// </summary>
public sealed class GroupTeams(AppDbContext db, ILiteLlm gateway)
{
    public static string TeamId(Guid group) => LiteLlmClient.TeamPrefix + group.ToString("N");

    /// <summary>Takes everyone's keys out of the app's teams and deletes the teams; returns how many things changed.</summary>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        var teams = (await gateway.TeamsAsync(ct)).Select(t => t.Id).Where(t => t.StartsWith(LiteLlmClient.TeamPrefix, StringComparison.Ordinal)).ToList();
        if (teams.Count == 0)
        {
            return 0;
        }
        var changed = 0;
        foreach (var email in await db.Users.AsNoTracking().Where(u => u.Email != null).Select(u => u.Email!).ToListAsync(ct))
        {
            foreach (var key in await gateway.KeysAsync(email, ct))
            {
                if (key.TeamId is { } team && team.StartsWith(LiteLlmClient.TeamPrefix, StringComparison.Ordinal))
                {
                    await gateway.SetKeyTeamAsync(key.Token, null, ct);
                    changed++;
                }
            }
        }
        // The gateway deletes a team's keys with it: only once none is in it.
        foreach (var team in teams)
        {
            await gateway.DeleteTeamAsync(team, ct);
            changed++;
        }
        return changed;
    }
}
