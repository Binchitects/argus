using Llm.Core.Access;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Gateway;

/// <summary>
/// On the replica that leads, every ten minutes: the gateway holds no budget, so only the app's credits hold (a gateway budget
/// left would refuse a person's keys on its own: one from before v5.5, or one LiteLLM gives a new person when an edited
/// config/litellm.yaml still has max_internal_user_budget). The first time, each person's budget there becomes their credit
/// of every kind in the app (unless they have credits there already); a setting says that was done. A gateway out of reach
/// is tried again at the next round.
/// </summary>
public sealed class CreditMove(AppDbContext db, ILiteLlm gateway)
{
    public const string Done = "credits.moved";

    /// <summary>The people whose gateway budget was cleared (the first time, after it became their credits here).</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var first = !await db.Settings.AnyAsync(s => s.Key == Done, ct);
        var budgets = await gateway.UsersAsync(ct);
        var people = await db.Users.Where(u => u.Email != null).ToListAsync(ct);
        if (first)
        {
            // Saved here before any budget is cleared there: a gateway that fails half way loses nobody's credit.
            foreach (var u in people)
            {
                if (budgets.TryGetValue(u.Email!, out var g) && g.Budget is { } budget && Credits.Kinds.All(k => Credits.Of(u, k) is null))
                {
                    foreach (var k in Credits.Kinds)
                    {
                        Credits.Set(u, k, budget);
                    }
                }
            }
            db.Settings.Add(new Setting { Key = Done, Value = DateTimeOffset.UtcNow.ToString("O") });
            await db.SaveChangesAsync(ct);
        }
        var cleared = 0;
        foreach (var u in people)
        {
            if (budgets.TryGetValue(u.Email!, out var g) && g.Budget is not null)
            {
                await gateway.SetBudgetAsync(u.Email!, null, ct);
                cleared++;
            }
        }
        return cleared;
    }
}
