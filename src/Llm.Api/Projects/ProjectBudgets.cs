using System.Globalization;
using Llm.Api.Notifications;
using Llm.Core.Data;
using Llm.Core.Gateway;
using Llm.Core.Projects;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Projects;

/// <summary>
/// A project's monthly budget, as a group's credit works: what its requests spent this calendar month (UTC) against it, its
/// owners told at its alert share and when it is all spent (each once a month and budget), and, when it stops there, its
/// chats' answers refused until the month ends or the budget is raised.
/// </summary>
public static class ProjectBudgets
{
    public static DateTimeOffset MonthStart(DateTimeOffset now) => new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>What the projects' requests spent this month, by project.</summary>
    public static async Task<Dictionary<Guid, decimal>> SpentAsync(AppDbContext db, IReadOnlyCollection<Guid> projects, DateTimeOffset now, CancellationToken ct)
    {
        var since = MonthStart(now);
        return await db.GatewayRequests.AsNoTracking()
            .Where(r => r.ProjectId != null && projects.Contains(r.ProjectId.Value) && r.StartedAt >= since && r.Status != GatewayRequest.Refused)
            .GroupBy(r => r.ProjectId!.Value).Select(g => new { g.Key, Cost = g.Sum(r => r.Cost) }).ToDictionaryAsync(x => x.Key, x => x.Cost, ct);
    }

    /// <summary>Why a chat of this project gets no answer now (it stops at its budget, and spent it this month), or null.</summary>
    public static async Task<string?> RefusalAsync(AppDbContext db, Guid? projectId, CancellationToken ct)
    {
        if (projectId is not { } id || await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) is not { StopAtBudget: true, MonthlyBudget: { } budget } p)
        {
            return null;
        }
        var spent = (await SpentAsync(db, [id], DateTimeOffset.UtcNow, ct)).GetValueOrDefault(id);
        return spent < budget ? null
            : $"The project {p.Name} has spent its budget for this month ({Money(spent)} of {Money(budget)}). Its owners can raise it, or take this chat out of the project.";
    }

    /// <summary>
    /// Projects with a budget at their alert share or past it: their owners are told, each once a month for each budget (a
    /// raised budget says it again when reached).
    /// </summary>
    public static async Task CheckAsync(AppDbContext db, NewsDelivery news, DateTimeOffset now, CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking().Where(p => p.MonthlyBudget > 0 && p.Status == Project.Active).ToListAsync(ct);
        if (projects.Count == 0)
        {
            return;
        }
        var spent = await SpentAsync(db, [.. projects.Select(p => p.Id)], now, ct);
        var month = now.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        foreach (var p in projects)
        {
            var budget = p.MonthlyBudget!.Value;
            var used = spent.GetValueOrDefault(p.Id);
            var share = Math.Clamp(p.AlertPercent, 1, 100);
            if (used < budget * share / 100m)
            {
                continue;
            }
            var all = used >= budget;
            var key = $"project:{p.Id}:{(all ? 100 : share)}:{month}:{budget.ToString("0.####", CultureInfo.InvariantCulture)}";
            var of = $"{Money(used)} of {Money(budget)}";
            var title = all ? $"{p.Name} has spent its budget" : $"{Math.Floor(100 * used / budget)}% of {p.Name}'s budget is spent";
            var body = all
                ? $"It spent {of} this month. {(p.StopAtBudget ? "Its chats get no answers" : "Its chats still answer")} until the month ends or the budget is raised."
                : $"It spent {of} this month.";
            foreach (var owner in await OwnersAsync(db, p, ct))
            {
                await news.SendAsync(owner, new News("usage", title, body, $"/projects/{p.Id}", key), ct);
            }
        }
    }

    /// <summary>The people who run the project: its maker and the people in it as owners (not the groups').</summary>
    public static async Task<List<Core.Identity.AppUser>> OwnersAsync(AppDbContext db, Project p, CancellationToken ct)
    {
        var ids = await db.ProjectMembers.AsNoTracking().Where(m => m.ProjectId == p.Id && m.Role == ProjectMember.Owner && m.UserId != null)
            .Select(m => m.UserId!.Value).ToListAsync(ct);
        ids.Add(p.OwnerId);
        return await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && !u.IsDisabled).ToListAsync(ct);
    }

    public static string Money(decimal v) => "$" + v.ToString(v is > 0 and < 0.01m ? "0.####" : "0.00", CultureInfo.InvariantCulture);
}
