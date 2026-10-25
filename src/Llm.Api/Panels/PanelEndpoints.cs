using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Panels;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Panels;

/// <summary>A seat on the panel: one of the roles by its name, or an assistant the person may use.</summary>
public sealed record Seat(string? Role = null, Guid? AssistantId = null);

public sealed record NewPanel(string? Question, List<Seat>? Panelists, int? Rounds = null);

/// <summary>Panels: a question discussed by assistants or roles in a chat or room, round by round, and a moderator's verdict.</summary>
public static class PanelEndpoints
{
    /// <summary>The sides one may seat, by name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Roles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["The expert"] = "you know the field deeply: answer with the facts, the numbers and the practice.",
        ["The critic"] = "you look for the weak points: what could go wrong, what is assumed, what is missing.",
        ["The one who says no"] = "you argue against the idea on the table, as well as it can be argued, even if you might agree.",
        ["The user"] = "you are the person who will live with the result: what it is like to use, what you need from it.",
        ["The accountant"] = "you weigh what it costs against what it brings: money, time, people.",
        ["The lawyer"] = "you see the rules, the contracts, the risks and who is liable.",
    };

    public static void MapPanels(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").PersonOnly().RequirePermission(Core.Access.Permissions.Chat);
        g.MapGet("/panels/roles", () => Results.Ok(Roles.Select(r => new { name = r.Key, role = r.Value })));
        g.MapPost("/chat/conversations/{id:guid}/panel", StartAsync);
        g.MapGet("/panels/{id:guid}", GetAsync);
        g.MapGet("/chat/conversations/{id:guid}/panels", ListAsync);
    }

    /// <summary>
    /// A chat's panels of the person's, the newest first (at most 10): those still working, for the chat to say how far they
    /// are after a reload, and the latest ones done.
    /// </summary>
    private static async Task<IResult> ListAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var panels = await db.Panels.AsNoTracking().Where(x => x.ConversationId == id && x.UserId == me.Id).OrderByDescending(x => x.CreatedAt).Take(10).ToListAsync(ct);
        return Results.Ok(panels.Select(x => new
        {
            x.Id, x.Question, x.Rounds, x.Status, x.CreatedAt,
            panelists = JsonNode.Parse(x.Panelists), turns = (JsonNode.Parse(x.Turns) as JsonArray)?.Count ?? 0,
        }));
    }

    private static async Task<IResult> StartAsync(Guid id, NewPanel body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, PanelRuns runs,
        CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        // The person's own chat, or a room they may write in.
        var c = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.DeletedAt == null, ct);
        if (c is null || (c.UserId != me.Id && !(c.IsRoom && await db.RoomMembers.AnyAsync(m => m.ConversationId == id && m.UserId == me.Id && m.Role == RoomMember.Member, ct))))
        {
            return Results.NotFound();
        }
        var question = body.Question?.Trim() ?? "";
        if (question.Length is 0 or > 4000)
        {
            return AuthEndpoints.Problem(400, "question", "Ask the panel something, up to 4,000 characters.");
        }
        var seats = body.Panelists ?? [];
        if (seats.Count is < 2 or > 5)
        {
            return AuthEndpoints.Problem(400, "panelists", "A panel has 2 to 5 panelists.");
        }
        var membership = await access.MembershipAsync(me, ct);
        var panelists = new List<Panelist>();
        foreach (var seat in seats)
        {
            if (seat.AssistantId is { } assistantId)
            {
                if (await Assistants.UsableAsync(db, me, membership, assistantId, ct) is not { } a)
                {
                    return AuthEndpoints.Problem(400, "panelists", "An assistant of those is not one you may use.");
                }
                var role = a.Instructions is { Length: > 0 } i ? (i.Length > 1500 ? i[..1500] : i) : a.Description ?? "an assistant.";
                panelists.Add(new Panelist(a.Name, role, a.Model, a.Id));
            }
            else if (seat.Role is { } name && Roles.TryGetValue(name, out var role))
            {
                panelists.Add(new Panelist(name, role));
            }
            else
            {
                return AuthEndpoints.Problem(400, "panelists", $"A panelist is an assistant or one of: {string.Join(", ", Roles.Keys)}.");
            }
        }
        if (panelists.Select(x => x.Name).Distinct().Count() != panelists.Count)
        {
            return AuthEndpoints.Problem(400, "panelists", "Each panelist sits once.");
        }
        var panel = new Panel
        {
            UserId = me.Id, ConversationId = id, Question = question, Panelists = JsonSerializer.Serialize(panelists, JsonSerializerOptions.Web), Rounds = Math.Clamp(body.Rounds ?? 2, 1, 3),
        };
        db.Panels.Add(panel);
        // A chat made for the panel is named after its question.
        if (c.Title == "New chat" && !await db.ChatMessages.AnyAsync(m => m.ConversationId == id, ct))
        {
            var title = question.ReplaceLineEndings(" ");
            await db.Conversations.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, title.Length > 80 ? title[..80].TrimEnd() + "…" : title), ct);
        }
        await db.SaveChangesAsync(ct);
        runs.Go(panel.Id);
        return Results.Accepted($"/api/panels/{panel.Id}", new { panel.Id });
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        if (await db.Panels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == me.Id, ct) is not { } panel)
        {
            return Results.NotFound();
        }
        return Results.Ok(new
        {
            panel.Id, panel.ConversationId, panel.Question, panel.Rounds, panel.Status, panel.Verdict, panel.Error, panel.CreatedAt,
            panelists = JsonNode.Parse(panel.Panelists), turns = JsonNode.Parse(panel.Turns),
        });
    }
}
