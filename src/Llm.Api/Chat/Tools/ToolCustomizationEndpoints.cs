using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools;

/// <summary>A tool as the admin customizes it: what people see, what the model is told, its functions, its calls a day.</summary>
public sealed record CustomizeRequest(
    string? Title = null, string? Description = null, string? Instructions = null, bool ReplaceInstructions = false,
    List<string>? DisabledFunctions = null, List<string>? AskFirstFunctions = null, JsonObject? Functions = null, int? CallsPerDay = null);

/// <summary>One group's settings for a tool; all null removes them.</summary>
public sealed record GroupToolRequest(Guid GroupId, bool? OnByDefault = null, bool? AskFirst = null, int? CallsPerDay = null);

public sealed record ToolsetRequest(string Name, List<string> Tools, string? Description = null, List<Guid>? Groups = null, List<Guid>? DefaultFor = null);

public sealed record TryRequest(JsonObject? Arguments = null, string? Function = null);

/// <summary>
/// Admin → Tools, the customizing part: a tool's functions and the admin's settings for them, each group's settings, how
/// each tool was used (calls, failures, time), toolsets, and the admin's own tools made without code (tried before they are
/// offered). People see the toolsets meant for them.
/// </summary>
public static class ToolCustomizationEndpoints
{
    public static void MapToolCustomization(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/tools").RequirePermissions(Permissions.ToolsRead, Permissions.ToolsEdit, new Dictionary<string, string>
        {
            ["POST /api/admin/tools/custom"] = Permissions.McpServersManage,
            ["PUT /api/admin/tools/custom/{id:guid}"] = Permissions.McpServersManage,
            ["DELETE /api/admin/tools/custom/{id:guid}"] = Permissions.McpServersManage,
            ["POST /api/admin/tools/custom/try"] = Permissions.McpServersManage,
            ["POST /api/admin/tools/{toolId}/try"] = Permissions.ToolsEdit,
        });
        g.MapGet("/{toolId}/customize", CustomizationAsync);
        g.MapPut("/{toolId}/customize", CustomizeAsync);
        g.MapPut("/{toolId}/groups", GroupAsync);
        g.MapPost("/{toolId}/try", TryToolAsync);
        g.MapGet("/usage", UsageAsync);
        g.MapGet("/toolsets", ToolsetsAsync);
        g.MapPost("/toolsets", AddToolsetAsync);
        g.MapPut("/toolsets/{id:guid}", ChangeToolsetAsync);
        g.MapDelete("/toolsets/{id:guid}", RemoveToolsetAsync);
        g.MapGet("/custom", CustomsAsync);
        g.MapPost("/custom", AddCustomAsync);
        g.MapPut("/custom/{id:guid}", ChangeCustomAsync);
        g.MapDelete("/custom/{id:guid}", RemoveCustomAsync);
        g.MapPost("/custom/try", TryCustomAsync);

        app.MapGet("/api/chat/toolsets", MyToolsetsAsync).PersonOnly();
    }

    /// <summary>A tool made ready as the admin, to see its functions or try one: a chat that is not saved.</summary>
    private static ToolContext AdminContext(AppUser me) =>
        new(me, me.Email ?? "", new Conversation { UserId = me.Id, Title = "Admin → Tools" });

    /// <summary>The tool's functions as it offers them, and the admin's settings, and each group's.</summary>
    private static async Task<IResult> CustomizationAsync(string toolId, ToolRegistry registry, AppDbContext db, UserManager<AppUser> users, ClaimsPrincipal p, CancellationToken ct)
    {
        if ((await registry.AllAsync(ct)).FirstOrDefault(t => t.Tool.Id == toolId) is not { } choice)
        {
            return Results.NotFound();
        }
        JsonArray functions;
        string? problem = null;
        try
        {
            functions = (await choice.Tool.StartAsync(AdminContext((await users.GetUserAsync(p))!), ct)).Functions;
        }
        catch (Exception ex) when (ex is McpException or HttpRequestException or InvalidOperationException)
        {
            functions = [];
            problem = ex.Message;
        }
        var groups = await db.ToolGroupSettings.AsNoTracking().Where(x => x.ToolId == toolId).ToListAsync(ct);
        var names = await db.Groups.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var s = choice.Setting;
        return Results.Ok(new
        {
            id = toolId, title = choice.Tool.Title, description = choice.Tool.Description, problem,
            functions = functions.OfType<JsonObject>().Select(f => new
            {
                name = f["function"]?["name"]?.GetValue<string>(), description = f["function"]?["description"]?.GetValue<string>(),
                parameters = f["function"]?["parameters"]?["properties"] is JsonObject props ? props.Select(kv => new { name = kv.Key, description = kv.Value?["description"]?.GetValue<string>() }) : [],
            }),
            setting = new
            {
                s.Title, s.Description, s.Instructions, s.ReplaceInstructions, s.DisabledFunctions, s.AskFirstFunctions, s.CallsPerDay,
                functions = s.FunctionsJson is { Length: > 0 } fj ? JsonNode.Parse(fj) : new JsonObject(),
            },
            groups = groups.Select(x => new { x.GroupId, name = names.GetValueOrDefault(x.GroupId), x.OnByDefault, x.AskFirst, x.CallsPerDay }),
        });
    }

    private static async Task<ToolSetting> SettingAsync(AppDbContext db, string toolId, CancellationToken ct)
    {
        if (await db.ToolSettings.SingleOrDefaultAsync(s => s.ToolId == toolId, ct) is { } found)
        {
            return found;
        }
        var setting = new ToolSetting { ToolId = toolId };
        db.ToolSettings.Add(setting);
        return setting;
    }

    private static async Task<IResult> CustomizeAsync(string toolId, CustomizeRequest body, ToolRegistry registry, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if ((await registry.AllAsync(ct)).All(t => t.Tool.Id != toolId))
        {
            return Results.NotFound();
        }
        if (body.CallsPerDay is < 0 or > 100_000)
        {
            return AuthEndpoints.Problem(400, "calls", "Calls a day are 0 (no limit) to 100,000.");
        }
        if (body.Title?.Length > 100 || body.Description?.Length > 500 || body.Instructions?.Length > 4000)
        {
            return AuthEndpoints.Problem(400, "text", "A name is at most 100 characters, a description 500, the words to the model 4,000.");
        }
        if (body.Functions is { } fns && fns.Any(kv => kv.Value is not JsonObject f || (f["fixed"] is not null and not JsonObject) || (f["defaults"] is not null and not JsonObject)))
        {
            return AuthEndpoints.Problem(400, "functions", "Each function's settings are {description, fixed: {argument: value}, defaults: {argument: value}}.");
        }
        var s = await SettingAsync(db, toolId, ct);
        s.Title = Clean(body.Title);
        s.Description = Clean(body.Description);
        s.Instructions = Clean(body.Instructions);
        s.ReplaceInstructions = body.ReplaceInstructions && s.Instructions is not null;
        s.DisabledFunctions = [.. (body.DisabledFunctions ?? []).Distinct()];
        s.AskFirstFunctions = [.. (body.AskFirstFunctions ?? []).Distinct()];
        s.FunctionsJson = body.Functions is { Count: > 0 } f2 ? f2.ToJsonString() : null;
        s.CallsPerDay = body.CallsPerDay is > 0 ? body.CallsPerDay : null;
        s.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.customize", toolId,
            detail: $"{s.DisabledFunctions.Count} functions off, {s.AskFirstFunctions.Count} asking first{(s.CallsPerDay is { } c ? $", {c} calls a day" : "")}{(s.Instructions is null ? "" : ", with the admin's words")}");
        return Results.NoContent();
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static async Task<IResult> GroupAsync(string toolId, GroupToolRequest body, ToolRegistry registry, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if ((await registry.AllAsync(ct)).All(t => t.Tool.Id != toolId) || !await db.Groups.AnyAsync(x => x.Id == body.GroupId, ct))
        {
            return Results.NotFound();
        }
        if (body.CallsPerDay is < 0 or > 100_000)
        {
            return AuthEndpoints.Problem(400, "calls", "Calls a day are 0 (no limit) to 100,000.");
        }
        var row = await db.ToolGroupSettings.SingleOrDefaultAsync(x => x.ToolId == toolId && x.GroupId == body.GroupId, ct);
        if (body is { OnByDefault: null, AskFirst: null, CallsPerDay: null })
        {
            if (row is not null)
            {
                db.ToolGroupSettings.Remove(row);
            }
        }
        else
        {
            if (row is null)
            {
                row = new ToolGroupSetting { ToolId = toolId, GroupId = body.GroupId };
                db.ToolGroupSettings.Add(row);
            }
            (row.OnByDefault, row.AskFirst, row.CallsPerDay) = (body.OnByDefault, body.AskFirst, body.CallsPerDay);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.group", toolId, detail: $"group {body.GroupId}: on in new chats {body.OnByDefault?.ToString() ?? "as the company"}, asks first {body.AskFirst?.ToString() ?? "as the company"}, calls a day {body.CallsPerDay?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "as the company"}");
        return Results.NoContent();
    }

    /// <summary>One of the tool's functions, called as the admin with the admin's settings applied (fixed arguments too).</summary>
    private static async Task<IResult> TryToolAsync(string toolId, TryRequest body, ToolRegistry registry, UserManager<AppUser> users, ClaimsPrincipal p, CancellationToken ct)
    {
        if ((await registry.AllAsync(ct)).FirstOrDefault(t => t.Tool.Id == toolId) is not { } choice)
        {
            return Results.NotFound();
        }
        var run = await registry.StartAsync(choice, AdminContext((await users.GetUserAsync(p))!), ct);
        var function = body.Function ?? run.Functions.OfType<JsonObject>().Select(f => f["function"]?["name"]?.GetValue<string>()).FirstOrDefault();
        if (function is null)
        {
            return AuthEndpoints.Problem(400, "function", "The tool offers no function to try.");
        }
        var result = await run.CallAsync(function, body.Arguments ?? [], ct);
        return Results.Ok(new { function, result.Text, result.IsError });
    }

    /// <summary>Each tool's calls over the last days: how many, how many failed, how long they took, the last error; and by function.</summary>
    private static async Task<IResult> UsageAsync(AppDbContext db, int? days, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 90));
        var calls = await db.ToolCalls.AsNoTracking().Where(c => c.At >= since)
            .GroupBy(c => new { c.ToolId, c.Function })
            .Select(x => new { x.Key.ToolId, x.Key.Function, Calls = x.Count(), Failed = x.Count(c => c.Failed), Ms = x.Average(c => (double)c.DurationMs), People = x.Select(c => c.UserId).Distinct().Count() })
            .ToListAsync(ct);
        var last = await db.ToolCalls.AsNoTracking().Where(c => c.At >= since && c.Failed).GroupBy(c => c.ToolId)
            .Select(x => x.OrderByDescending(c => c.At).Select(c => new { c.ToolId, c.Error, c.At }).First()).ToListAsync(ct);
        return Results.Ok(calls.GroupBy(c => c.ToolId).Select(t => new
        {
            toolId = t.Key, calls = t.Sum(c => c.Calls), failed = t.Sum(c => c.Failed), averageMs = (int)(t.Sum(c => c.Ms * c.Calls) / Math.Max(1, t.Sum(c => c.Calls))),
            lastError = last.FirstOrDefault(l => l.ToolId == t.Key) is { } l ? new { l.Error, l.At } : null,
            functions = t.OrderByDescending(c => c.Calls).Select(c => new { name = c.Function, c.Calls, c.Failed, averageMs = (int)c.Ms }),
        }).OrderByDescending(t => t.calls));
    }

    private static object ShapeSet(Toolset t) => new { t.Id, t.Name, t.Description, t.Tools, t.Groups, t.DefaultFor, t.Plugin };

    private static async Task<IResult> ToolsetsAsync(AppDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Toolsets.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct)).Select(ShapeSet));

    private static async Task<string?> InvalidSetAsync(ToolsetRequest body, ToolRegistry registry, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 100)
        {
            return "A toolset needs a name of up to 100 characters.";
        }
        var ids = (await registry.AllAsync(ct)).Select(t => t.Tool.Id).ToHashSet();
        if (body.Tools.FirstOrDefault(t => !ids.Contains(t)) is { } unknown)
        {
            return $"There is no tool {unknown}.";
        }
        var groups = (body.Groups ?? []).Concat(body.DefaultFor ?? []).Distinct().ToList();
        return await db.Groups.CountAsync(x => groups.Contains(x.Id), ct) != groups.Count ? "A group in the list does not exist." : null;
    }

    private static async Task<IResult> AddToolsetAsync(ToolsetRequest body, ToolRegistry registry, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await InvalidSetAsync(body, registry, db, ct) is { } wrong)
        {
            return AuthEndpoints.Problem(400, "toolset", wrong);
        }
        if (await db.Toolsets.AnyAsync(t => t.Name == body.Name.Trim(), ct))
        {
            return AuthEndpoints.Problem(409, "toolset", "A toolset has that name already.");
        }
        var set = new Toolset { Name = body.Name.Trim() };
        Apply(set, body);
        db.Toolsets.Add(set);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("toolset.add", set.Name, detail: string.Join(", ", set.Tools));
        return Results.Created($"/api/admin/tools/toolsets/{set.Id}", ShapeSet(set));
    }

    private static void Apply(Toolset set, ToolsetRequest body)
    {
        set.Name = body.Name.Trim();
        set.Description = Clean(body.Description);
        set.Tools = [.. body.Tools.Distinct()];
        set.Groups = [.. (body.Groups ?? []).Distinct()];
        set.DefaultFor = [.. (body.DefaultFor ?? []).Distinct()];
    }

    private static async Task<IResult> ChangeToolsetAsync(Guid id, ToolsetRequest body, ToolRegistry registry, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.Toolsets.FindAsync([id], ct) is not { } set)
        {
            return Results.NotFound();
        }
        if (await InvalidSetAsync(body, registry, db, ct) is { } wrong)
        {
            return AuthEndpoints.Problem(400, "toolset", wrong);
        }
        Apply(set, body);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("toolset.change", set.Name, detail: string.Join(", ", set.Tools));
        return Results.Ok(ShapeSet(set));
    }

    private static async Task<IResult> RemoveToolsetAsync(Guid id, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.Toolsets.FindAsync([id], ct) is not { } set)
        {
            return Results.NotFound();
        }
        db.Toolsets.Remove(set);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("toolset.remove", set.Name);
        return Results.NoContent();
    }

    /// <summary>The toolsets for this person: for everyone or one of their groups, each with only the tools they may use.</summary>
    private static async Task<IResult> MyToolsetsAsync(ClaimsPrincipal p, UserManager<AppUser> users, AccessService access, ToolRegistry registry, AppDbContext db, CancellationToken ct)
    {
        var member = await access.MembershipAsync((await users.GetUserAsync(p))!, ct);
        var mayUse = (await registry.ForAsync(member, ct)).Select(t => t.Tool.Id).ToHashSet();
        var sets = await db.Toolsets.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        return Results.Ok(sets.Where(t => t.Groups.Count == 0 || t.Groups.Any(member.Groups.Contains))
            .Select(t => new { t.Id, t.Name, t.Description, tools = t.Tools.Where(mayUse.Contains), isDefault = t.DefaultFor.Any(member.Groups.Contains) }));
    }

    private static object ShapeCustom(CustomTool t, string? dataKey) => new
    {
        t.Id, toolId = CustomTools.Prefix + t.Id, t.Name, t.Kind, t.Title, t.Description, parameters = CustomTools.Parameters(t.ParametersJson), t.Method, t.UrlTemplate,
        // The headers' names only: their values (keys, tokens) are never shown again.
        headers = CustomTools.Headers(t, dataKey).Keys, t.BodyTemplate, t.ResultPath, t.AskFirst, t.Model, t.PromptTemplate, t.SystemPrompt, t.UpdatedAt,
    };

    private static async Task<IResult> CustomsAsync(AppDbContext db, IOptions<AuthOptions> auth, CancellationToken ct) =>
        Results.Ok((await db.CustomTools.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct)).Select(t => ShapeCustom(t, auth.Value.DataKey)));

    /// <summary>The form into the row; headers left empty keep the values saved under their names.</summary>
    private static string? Apply(CustomTool t, CustomToolRequest r, string? dataKey)
    {
        t.Name = r.Name;
        t.Kind = r.Kind;
        t.Title = r.Title?.Trim() ?? "";
        t.Description = r.Description!.Trim();
        t.ParametersJson = JsonSerializer.Serialize(r.Parameters ?? [], JsonSerializerOptions.Web);
        t.AskFirst = r.AskFirst;
        t.UpdatedAt = DateTimeOffset.UtcNow;
        if (r.Kind == CustomTool.Http)
        {
            (t.Method, t.UrlTemplate, t.BodyTemplate, t.ResultPath) = (r.Method, r.UrlTemplate!.Trim(), Clean(r.BodyTemplate), Clean(r.ResultPath));
            (t.Model, t.PromptTemplate, t.SystemPrompt) = (null, null, null);
            var saved = CustomTools.Headers(t, dataKey);
            var headers = (r.Headers ?? []).ToDictionary(h => h.Key.Trim(), h => string.IsNullOrEmpty(h.Value) ? saved.GetValueOrDefault(h.Key.Trim()) ?? "" : h.Value);
            if (headers.Count > 0 && string.IsNullOrEmpty(dataKey))
            {
                return "APP_DATA_KEY is not set, so the headers cannot be stored encrypted.";
            }
            t.HeadersEncrypted = headers.Count > 0 ? SettingsCrypto.Encrypt(JsonSerializer.Serialize(headers), dataKey!) : null;
        }
        else
        {
            (t.Model, t.PromptTemplate, t.SystemPrompt) = (r.Model!.Trim(), r.PromptTemplate!.Trim(), Clean(r.SystemPrompt));
            (t.Method, t.UrlTemplate, t.BodyTemplate, t.ResultPath, t.HeadersEncrypted) = (null, null, null, null, null);
        }
        return null;
    }

    private static async Task<IResult> AddCustomAsync(CustomToolRequest body, AppDbContext db, Audit audit, IOptions<AuthOptions> auth, UserManager<AppUser> users, ClaimsPrincipal p, CancellationToken ct)
    {
        if (CustomTools.Invalid(body) is { } wrong)
        {
            return AuthEndpoints.Problem(400, "tool", wrong);
        }
        if (await db.CustomTools.AnyAsync(t => t.Name == body.Name, ct))
        {
            return AuthEndpoints.Problem(409, "tool", "A tool has that name already.");
        }
        var tool = new CustomTool { Name = body.Name, Kind = body.Kind, CreatedById = (await users.GetUserAsync(p))?.Id };
        if (Apply(tool, body, auth.Value.DataKey) is { } problem)
        {
            return AuthEndpoints.Problem(400, "tool", problem);
        }
        db.CustomTools.Add(tool);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.custom_add", tool.Name, detail: tool.Kind == CustomTool.Http ? $"{tool.Method} {new Uri(CustomTools.Fill(tool, []).Url).Host}" : $"prompt on {tool.Model}");
        return Results.Created($"/api/admin/tools/custom/{tool.Id}", ShapeCustom(tool, auth.Value.DataKey));
    }

    private static async Task<IResult> ChangeCustomAsync(Guid id, CustomToolRequest body, AppDbContext db, Audit audit, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await db.CustomTools.FindAsync([id], ct) is not { } tool)
        {
            return Results.NotFound();
        }
        if (CustomTools.Invalid(body) is { } wrong)
        {
            return AuthEndpoints.Problem(400, "tool", wrong);
        }
        if (body.Name != tool.Name && await db.CustomTools.AnyAsync(t => t.Name == body.Name, ct))
        {
            return AuthEndpoints.Problem(409, "tool", "A tool has that name already.");
        }
        if (Apply(tool, body, auth.Value.DataKey) is { } problem)
        {
            return AuthEndpoints.Problem(400, "tool", problem);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.custom_change", tool.Name);
        return Results.Ok(ShapeCustom(tool, auth.Value.DataKey));
    }

    private static async Task<IResult> RemoveCustomAsync(Guid id, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.CustomTools.FindAsync([id], ct) is not { } tool)
        {
            return Results.NotFound();
        }
        db.CustomTools.Remove(tool);
        await db.ToolSettings.Where(s => s.ToolId == CustomTools.Prefix + id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.custom_remove", tool.Name);
        return Results.NoContent();
    }

    public sealed record TryCustomRequest(CustomToolRequest Tool, JsonObject? Arguments = null, Guid? Id = null);

    /// <summary>The form's tool, tried before it is saved (headers left empty take the saved tool's, when it has an id).</summary>
    private static async Task<IResult> TryCustomAsync(TryCustomRequest body, AppDbContext db, IHttpClientFactory http, GatewayChat gateway, IOptions<AuthOptions> auth,
        UserManager<AppUser> users, ClaimsPrincipal p, CancellationToken ct)
    {
        if (CustomTools.Invalid(body.Tool) is { } wrong)
        {
            return AuthEndpoints.Problem(400, "tool", wrong);
        }
        var saved = body.Id is { } id ? await db.CustomTools.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) : null;
        var tool = new CustomTool { Name = body.Tool.Name, Kind = body.Tool.Kind, HeadersEncrypted = saved?.HeadersEncrypted };
        if (Apply(tool, body.Tool, auth.Value.DataKey) is { } problem)
        {
            return AuthEndpoints.Problem(400, "tool", problem);
        }
        var run = await CustomTools.For(tool, http, gateway, auth.Value.DataKey).StartAsync(AdminContext((await users.GetUserAsync(p))!), ct);
        var result = await run.CallAsync(tool.Name, body.Arguments ?? [], ct);
        return Results.Ok(new { result.Text, result.IsError });
    }
}
