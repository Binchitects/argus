using System.Security.Claims;
using System.Text.Json;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Lab;

/// <summary>
/// The science lab's page: its fields (and whether the lab's tools are the person's), and the person's notebooks with
/// what was checked in each. A notebook is made, read and answered as any chat is (POST /api/chat/conversations with lab).
/// </summary>
public static class LabEndpoints
{
    public static void MapLab(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/lab").PersonOnly();
        g.MapGet("/fields", FieldsAsync);
        g.MapGet("/notebooks", NotebooksAsync);
    }

    private static async Task<IResult> FieldsAsync(ClaimsPrincipal p, UserManager<AppUser> users, ToolRegistry registry, AccessService access, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var allowed = (await registry.ForAsync(await access.MembershipAsync(me, ct), ct)).Select(t => t.Tool.Id).ToHashSet();
        // Without the lab's own tools there is no lab: the page says why.
        string? why = allowed.Contains(LabTool.ToolId) ? null
            : "The science lab needs its tools: the sandbox running, and Python among what you may use (an admin gives it, Admin → Access).";
        return Results.Ok(new
        {
            available = why is null,
            why,
            fields = LabFields.All.Select(f => new { f.Id, f.Title, f.About, tools = f.Tools.Where(allowed.Contains) }),
        });
    }

    private static async Task<IResult> NotebooksAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var notebooks = await db.Conversations.AsNoTracking().Where(c => c.UserId == me.Id && c.Kind == Conversation.Lab && c.ArchivedAt == null)
            .OrderByDescending(c => c.UpdatedAt).Take(200).Select(c => new { c.Id, c.Title, c.LabField, c.UpdatedAt }).ToListAsync(ct);
        var ids = notebooks.Select(n => n.Id).ToList();
        // What the lab's checks said in each: proved, refuted, or only numerically.
        var checks = await db.ChatMessages.AsNoTracking()
            .Where(m => ids.Contains(m.ConversationId) && m.Role == "tool" && Checks.Contains(m.ToolName!))
            .Select(m => new { m.ConversationId, m.ToolName, m.Content }).ToListAsync(ct);
        return Results.Ok(notebooks.Select(n =>
        {
            var mine = checks.Where(c => c.ConversationId == n.Id).Select(c => Verdict(c.ToolName!, c.Content)).ToList();
            return new
            {
                n.Id, n.Title, field = n.LabField, n.UpdatedAt,
                checks = new { proved = mine.Count(v => v == "proved"), refuted = mine.Count(v => v == "refuted"), numeric = mine.Count(v => v == "numeric") },
            };
        }));
    }

    /// <summary>The lab's functions whose result is a check: a claim, solutions put back, a reaction balanced.</summary>
    private static readonly string[] Checks = ["math_check", "math_solve", "chem_balance"];

    /// <summary>What a check's result says: proved (a balanced reaction: checked), refuted, numeric (holds only at the points tried) or other.</summary>
    public static string Verdict(string tool, string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (tool == "chem_balance")
            {
                return doc.RootElement.GetProperty("checked").GetBoolean() ? "proved" : "refuted";
            }
            if (tool == "math_solve")
            {
                var solutions = doc.RootElement.GetProperty("solutions").EnumerateArray().ToList();
                return solutions.Count > 0 && solutions.All(s => s.GetProperty("checked").GetBoolean()) ? "proved" : solutions.Count > 0 ? "refuted" : "other";
            }
            return doc.RootElement.GetProperty("verdict").GetString() switch
            {
                "proved" => "proved",
                "refuted" => "refuted",
                "holds numerically" => "numeric",
                _ => "other",
            };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "other";
        }
    }
}
