using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Models;
using Llm.Core.Apps;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Apps;

/// <param name="Reach">private, groups (<paramref name="GroupIds"/>) or company.</param>
public sealed record AppRequest(string? Name = null, string? Description = null, string? Kind = null, string? Code = null, string? Reach = null, List<Guid>? GroupIds = null,
    Guid? ConversationId = null, string? Note = null);

/// <param name="Model">"small" for the model for small steps (when there is one and the person may use it); else the person's default.</param>
public sealed record AppAsk(string? Prompt, string? System = null, string? Model = null, int? MaxTokens = null);

public sealed record AppValue(JsonElement Value);

/// <summary>
/// Apps: what the chat builds (a page or a React component), published at its own link; a gallery of those a person may
/// open; versions; remixes. The app runs in the preview's sandbox and reaches the server only through window.arena, which
/// the page around it relays: ask (the model, as the person using it: their credit, their safeguards) and the store (its
/// data, everyone's or each person's own).
/// </summary>
public static class AppEndpoints
{
    public const int MostCode = 500_000;
    public const int MostAsksADay = 300;
    public const int MostKeys = 1000;
    public const int MostValue = 64 * 1024;

    public static void MapApps(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/apps").PersonOnly();
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync).RequirePermission(Core.Access.Permissions.Apps);
        g.MapPost("/ask", AskAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPut("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapGet("/{id:guid}/versions", VersionsAsync);
        g.MapPost("/{id:guid}/versions/{version:int}/restore", RestoreAsync);
        g.MapPost("/{id:guid}/remix", RemixAsync).RequirePermission(Core.Access.Permissions.Apps);
        g.MapPost("/{id:guid}/ask", AskAsync);
        g.MapGet("/{id:guid}/data", DataAsync);
        g.MapGet("/{id:guid}/data/{key}", ValueAsync);
        g.MapPut("/{id:guid}/data/{key}", SetAsync);
        g.MapDelete("/{id:guid}/data/{key}", RemoveAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    /// <summary>Whether this person may open the app: theirs, everyone's, or one of their groups'.</summary>
    private static async Task<bool> MayOpenAsync(ArenaApp a, AppUser me, AccessService access, CancellationToken ct) =>
        a.OwnerId == me.Id || a.Reach == ArenaApp.Company || (a.Reach == ArenaApp.Groups && (await access.MembershipAsync(me, ct)).Groups.Any(a.GroupIds.Contains));

    private static async Task<ArenaApp?> OpenableAsync(Guid id, AppUser me, AppDbContext db, AccessService access, CancellationToken ct) =>
        await db.Apps.SingleOrDefaultAsync(a => a.Id == id, ct) is { } a && await MayOpenAsync(a, me, access, ct) ? a : null;

    private static object Brief(ArenaApp a, string? owner, Guid me) => new
    {
        a.Id, a.Name, a.Description, a.Kind, a.Version, a.Reach, a.GroupIds, a.RemixOfId, a.ConversationId, a.Opens, a.CreatedAt, a.UpdatedAt,
        owner, mine = a.OwnerId == me,
    };

    private static async Task<Dictionary<Guid, string>> NamesAsync(AppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName == "" ? u.UserName! : u.DisplayName, ct);
    }

    /// <summary>The gallery: the apps this person may open, the most opened first.</summary>
    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        var groups = (await access.MembershipAsync(me, ct)).Groups.ToList();
        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.OwnerId == me.Id || a.Reach == ArenaApp.Company || (a.Reach == ArenaApp.Groups && a.GroupIds.Any(g => groups.Contains(g))))
            .OrderByDescending(a => a.Opens).ThenByDescending(a => a.UpdatedAt).Take(500).ToListAsync(ct);
        var names = await NamesAsync(db, apps.Select(a => a.OwnerId), ct);
        return Results.Ok(apps.Select(a => Brief(a, names.GetValueOrDefault(a.OwnerId), me.Id)));
    }

    /// <summary>The request applied to the app, or why not.</summary>
    private static async Task<IResult?> ApplyAsync(ArenaApp a, AppRequest body, AppUser me, AppDbContext db, AccessService access, CancellationToken ct)
    {
        if (body.Name is { } name)
        {
            name = name.Trim();
            if (name.Length is 0 or > 100)
            {
                return AuthEndpoints.Problem(400, "name", "An app needs a name of up to 100 characters.");
            }
            a.Name = name;
        }
        if (body.Description is { } description)
        {
            a.Description = description.Trim() is var d && d.Length > 500 ? d[..500] : d;
        }
        if (body.Kind is { } kind)
        {
            if (kind is not ("html" or "react"))
            {
                return AuthEndpoints.Problem(400, "kind", "An app is a page (html) or a React component (react).");
            }
            a.Kind = kind;
        }
        if (body.Code is { } code)
        {
            if (code.Trim().Length == 0 || code.Length > MostCode)
            {
                return AuthEndpoints.Problem(400, "code", $"An app's code is 1 to {MostCode:N0} characters.");
            }
            a.Code = code;
        }
        if (body.Reach is { } reach)
        {
            if (reach is not (ArenaApp.Private or ArenaApp.Groups or ArenaApp.Company))
            {
                return AuthEndpoints.Problem(400, "reach", "Who may open it: private, groups or company.");
            }
            a.Reach = reach;
        }
        if (body.GroupIds is { } groupIds)
        {
            var known = await db.Groups.AsNoTracking().Where(g => groupIds.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct);
            if (known.Count != groupIds.Distinct().Count())
            {
                return AuthEndpoints.Problem(400, "groups", "A group of those is not here.");
            }
            // Shared with groups the person is in, unless they may share with any (as assistants are).
            var mine = (await access.MembershipAsync(me, ct)).Groups;
            if (!await access.MayAsync(me, Core.Access.Permissions.AssistantsShare, ct) && known.Any(g => !mine.Contains(g)))
            {
                return AuthEndpoints.Problem(403, "groups", "Share it with groups you are in.");
            }
            a.GroupIds = known;
        }
        if (a.Reach == ArenaApp.Groups && a.GroupIds.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Name the groups that may open it.");
        }
        return null;
    }

    private static async Task<IResult> CreateAsync(AppRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (body.Code is null || body.Name is null)
        {
            return AuthEndpoints.Problem(400, "app", "An app needs a name and its code.");
        }
        var a = new ArenaApp { OwnerId = me.Id, Name = "", Code = "" };
        if (await ApplyAsync(a, body, me, db, access, ct) is { } problem)
        {
            return problem;
        }
        if (body.ConversationId is { } chat && await db.Conversations.AnyAsync(c => c.Id == chat && c.UserId == me.Id, ct))
        {
            a.ConversationId = chat;
        }
        db.Apps.Add(a);
        db.AppVersions.Add(new ArenaAppVersion { AppId = a.Id, Version = 1, Code = a.Code, Note = body.Note ?? "Published", ById = me.Id });
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("app.publish", a.Name, detail: a.Reach);
        return Results.Created($"/api/apps/{a.Id}", Brief(a, null, me.Id));
    }

    private static async Task<IResult> GetAsync(Guid id, bool? open, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OpenableAsync(id, me, db, access, ct) is not { } a)
        {
            return Results.NotFound();
        }
        if (open == true && a.OwnerId != me.Id)
        {
            await db.Apps.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Opens, x => x.Opens + 1), ct);
        }
        var names = await NamesAsync(db, [a.OwnerId], ct);
        return Results.Ok(new { app = Brief(a, names.GetValueOrDefault(a.OwnerId), me.Id), a.Code, viewer = me.DisplayName == "" ? me.UserName : me.DisplayName });
    }

    private static async Task<IResult> UpdateAsync(Guid id, AppRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Apps.SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == me.Id, ct) is not { } a)
        {
            return Results.NotFound();
        }
        var before = a.Code;
        if (await ApplyAsync(a, body, me, db, access, ct) is { } problem)
        {
            return problem;
        }
        if (a.Code != before)
        {
            a.Version++;
            db.AppVersions.Add(new ArenaAppVersion { AppId = a.Id, Version = a.Version, Code = a.Code, Note = body.Note, ById = me.Id });
        }
        a.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("app.update", a.Name, detail: $"version {a.Version}, {a.Reach}");
        return Results.Ok(Brief(a, null, me.Id));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Apps.SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == me.Id, ct) is not { } a)
        {
            return Results.NotFound();
        }
        db.Apps.Remove(a);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("app.delete", a.Name);
        return Results.NoContent();
    }

    private static async Task<IResult> VersionsAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Apps.AnyAsync(a => a.Id == id && a.OwnerId == me.Id, ct))
        {
            return Results.NotFound();
        }
        return Results.Ok(await db.AppVersions.AsNoTracking().Where(v => v.AppId == id).OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.Note, v.CreatedAt, chars = v.Code.Length }).ToListAsync(ct));
    }

    /// <summary>An earlier version's code, put back as a new version.</summary>
    private static async Task<IResult> RestoreAsync(Guid id, int version, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Apps.SingleOrDefaultAsync(a => a.Id == id && a.OwnerId == me.Id, ct) is not { } a
            || await db.AppVersions.AsNoTracking().SingleOrDefaultAsync(v => v.AppId == id && v.Version == version, ct) is not { } old)
        {
            return Results.NotFound();
        }
        a.Code = old.Code;
        a.Version++;
        a.UpdatedAt = DateTimeOffset.UtcNow;
        db.AppVersions.Add(new ArenaAppVersion { AppId = a.Id, Version = a.Version, Code = a.Code, Note = $"Back to version {version}", ById = me.Id });
        await db.SaveChangesAsync(ct);
        return Results.Ok(Brief(a, null, me.Id));
    }

    /// <summary>A copy of the app as the person's own, to change: private, its data not copied.</summary>
    private static async Task<IResult> RemixAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OpenableAsync(id, me, db, access, ct) is not { } a)
        {
            return Results.NotFound();
        }
        var copy = new ArenaApp { OwnerId = me.Id, Name = (a.Name + " (remix)")[..Math.Min(100, a.Name.Length + 8)], Description = a.Description, Kind = a.Kind, Code = a.Code, RemixOfId = a.Id };
        db.Apps.Add(copy);
        db.AppVersions.Add(new ArenaAppVersion { AppId = copy.Id, Version = 1, Code = copy.Code, Note = $"Remixed from {a.Name}", ById = me.Id });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/apps/{copy.Id}", Brief(copy, null, me.Id));
    }

    /// <summary>
    /// arena.ask: one question to the model as the person using the app (their default model, or the model for small steps),
    /// through their safeguards (the question's and the answer's rules), counted against the asks a day. No app: a chat's preview.
    /// </summary>
    private static async Task<IResult> AskAsync(Guid? id, AppAsk body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        Safeguards.Safeguards safeguards, GatewayChat gateway, ModelPolicy policy, ChatModels models, IOptionsMonitor<ChatOptions> chat, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (id is { } appId && await OpenableAsync(appId, me, db, access, ct) is null)
        {
            return Results.NotFound();
        }
        var prompt = body.Prompt?.Trim() ?? "";
        if (prompt.Length is 0 or > 20_000)
        {
            return AuthEndpoints.Problem(400, "prompt", "Ask something, up to 20,000 characters.");
        }
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        if (await db.AppAsks.CountAsync(x => x.UserId == me.Id && x.At > since, ct) >= MostAsksADay)
        {
            return AuthEndpoints.Problem(429, "asks", $"Apps asked the model {MostAsksADay} times for you today, the most a day.");
        }
        var mine = await policy.ForAsync(me, await models.ListAsync(ct), ct);
        var model = body.Model == "small" && chat.CurrentValue.SmallModel is { Length: > 0 } small && mine.Models.Any(m => m.Name == small) ? small : mine.Default?.Name;
        if (model is null)
        {
            return AuthEndpoints.Problem(503, "model", "No model is there for you to ask.");
        }
        if (await policy.RefusalAsync(me, model, ct) is { } refused)
        {
            return AuthEndpoints.Problem(403, "model", refused);
        }
        var verdict = await safeguards.CheckMessageAsync(me, prompt, 0, false, model, ct);
        if (!verdict.Allowed)
        {
            return Safeguards.Safeguards.Problem(verdict);
        }
        var messages = new JsonArray();
        if (body.System is { Length: > 0 } system)
        {
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system.Length > 10_000 ? system[..10_000] : system });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = verdict.Text ?? prompt });
        var request = new JsonObject
        {
            ["model"] = verdict.Model ?? model, ["messages"] = messages, ["stream"] = true, ["max_tokens"] = Math.Clamp(body.MaxTokens ?? 2000, 16, 4000), ["user"] = me.Email,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["metadata"] = new JsonObject { ["tags"] = new JsonArray("app") },
        };
        var said = new StringBuilder();
        try
        {
            await foreach (var e in gateway.StreamAsync(request, me.Email!, ct))
            {
                if (e is ContentDelta c)
                {
                    said.Append(c.Text);
                }
            }
        }
        catch (ChatGatewayException ex)
        {
            return AuthEndpoints.Problem(502, "model", ex.Message);
        }
        db.AppAsks.Add(new ArenaAppAsk { AppId = id, UserId = me.Id, Model = model });
        await db.SaveChangesAsync(ct);
        var answer = await safeguards.RunAsync(new Safeguards.RuleRun("answer", said.ToString()) { User = me, Model = model }, ct);
        return answer.Blocked
            ? AuthEndpoints.Problem(answer.Status, "safeguard", answer.Reason ?? "The answer was withheld by a rule.")
            : Results.Ok(new { text = answer.Text, model });
    }

    private static Guid Whose(bool? mine, AppUser me) => mine == true ? me.Id : Guid.Empty;

    private static async Task<IResult> DataAsync(Guid id, bool? mine, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OpenableAsync(id, me, db, access, ct) is null)
        {
            return Results.NotFound();
        }
        var whose = Whose(mine, me);
        var rows = await db.AppData.AsNoTracking().Where(d => d.AppId == id && d.UserId == whose).OrderBy(d => d.Key).Take(MostKeys).ToListAsync(ct);
        return Results.Ok(rows.Select(d => new { d.Key, value = JsonNode.Parse(d.Value), d.UpdatedAt }));
    }

    private static async Task<IResult> ValueAsync(Guid id, string key, bool? mine, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OpenableAsync(id, me, db, access, ct) is null)
        {
            return Results.NotFound();
        }
        var whose = Whose(mine, me);
        var row = await db.AppData.AsNoTracking().SingleOrDefaultAsync(d => d.AppId == id && d.UserId == whose && d.Key == key, ct);
        return Results.Ok(new { key, value = row is null ? null : JsonNode.Parse(row.Value) });
    }

    private static async Task<IResult> SetAsync(Guid id, string key, AppValue body, bool? mine, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OpenableAsync(id, me, db, access, ct) is null)
        {
            return Results.NotFound();
        }
        if (key.Length is 0 or > 200)
        {
            return AuthEndpoints.Problem(400, "key", "A key is 1 to 200 characters.");
        }
        var value = body.Value.GetRawText();
        if (Encoding.UTF8.GetByteCount(value) > MostValue)
        {
            return AuthEndpoints.Problem(413, "value", $"A value is at most {MostValue / 1024} KB.");
        }
        var whose = Whose(mine, me);
        var row = await db.AppData.SingleOrDefaultAsync(d => d.AppId == id && d.UserId == whose && d.Key == key, ct);
        if (row is null)
        {
            if (await db.AppData.CountAsync(d => d.AppId == id && d.UserId == whose, ct) >= MostKeys)
            {
                return AuthEndpoints.Problem(400, "keys", $"An app keeps at most {MostKeys} values for everyone, and as many for each person.");
            }
            db.AppData.Add(new ArenaAppData { AppId = id, UserId = whose, Key = key, Value = value });
        }
        else
        {
            row.Value = value;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveAsync(Guid id, string key, bool? mine, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OpenableAsync(id, me, db, access, ct) is null)
        {
            return Results.NotFound();
        }
        var whose = Whose(mine, me);
        await db.AppData.Where(d => d.AppId == id && d.UserId == whose && d.Key == key).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}
