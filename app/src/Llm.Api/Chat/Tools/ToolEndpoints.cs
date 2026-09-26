using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools;

public sealed record ToolSettingRequest(bool Enabled, Audience Audience, Guid[]? Groups, bool OnByDefault, bool AskFirst);

/// <summary>An MCP server. HeaderValue: null keeps the stored one, "" removes it.</summary>
public sealed record McpServerRequest(string? Name = null, string? Description = null, string? Url = null, string? HeaderName = null, string? HeaderValue = null, string? EmailHeader = null);

/// <summary>Admin → Tools: which tools exist, for whom, and the MCP servers that add more.</summary>
public static class ToolEndpoints
{
    public static void MapTools(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/tools").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapPut("/{toolId}", SetAsync);
        g.MapPost("/servers", AddServerAsync);
        g.MapPatch("/servers/{id:guid}", UpdateServerAsync);
        g.MapDelete("/servers/{id:guid}", RemoveServerAsync);
        g.MapPost("/servers/test", TestAsync);
    }

    private static async Task<IResult> ListAsync(ToolRegistry registry, AppDbContext db, CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return Results.Ok((await registry.AllAsync(ct)).Select(t => new
        {
            id = t.Tool.Id, title = t.Tool.Title, description = t.Tool.Description, icon = t.Tool.Icon, unavailable = t.Unavailable,
            setting = new
            {
                t.Setting.Enabled, t.Setting.Audience, t.Setting.OnByDefault, t.Setting.AskFirst,
                groups = t.Setting.Groups.Where(groups.ContainsKey).Select(x => new { id = x, name = groups[x] }),
            },
            server = t.Tool is McpServerTool m ? new
            {
                m.Server.Id, m.Server.Name, m.Server.Description, m.Server.Url, m.Server.HeaderName, headerSet = m.Server.HeaderValueEncrypted is not null,
                m.Server.EmailHeader, prefix = m.Slug + "__",
            } : null,
        }));
    }

    private static async Task<IResult> SetAsync(string toolId, ToolSettingRequest body, ToolRegistry registry, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if ((await registry.AllAsync(ct)).All(t => t.Tool.Id != toolId))
        {
            return Results.NotFound();
        }
        var groups = (body.Groups ?? []).Distinct().ToList();
        if (body.Audience == Audience.Groups && groups.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Choose at least one group, or let everyone use it.");
        }
        if (await db.Groups.CountAsync(x => groups.Contains(x.Id), ct) != groups.Count)
        {
            return AuthEndpoints.Problem(400, "groups", "A group in the list does not exist.");
        }
        var setting = await db.ToolSettings.SingleOrDefaultAsync(s => s.ToolId == toolId, ct);
        if (setting is null)
        {
            setting = new ToolSetting { ToolId = toolId };
            db.ToolSettings.Add(setting);
        }
        setting.Enabled = body.Enabled;
        setting.Audience = body.Audience;
        setting.Groups = body.Audience == Audience.Groups ? groups : [];
        setting.OnByDefault = body.OnByDefault;
        setting.AskFirst = body.AskFirst;
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.update", toolId,
            detail: $"{(body.Enabled ? "on" : "off")}, {body.Audience.ToString().ToLowerInvariant()}{(body.AskFirst ? ", asks first" : "")}{(body.OnByDefault ? "" : ", off in new chats")}");
        return Results.NoContent();
    }

    private static async Task<IResult> AddServerAsync(McpServerRequest body, AppDbContext db, Audit audit, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var server = new McpServer { Name = "", Url = "" };
        if (await ApplyAsync(server, body, db, auth.Value.DataKey, creating: true, ct) is { } problem)
        {
            return problem;
        }
        db.McpServers.Add(server);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.server_add", server.Name, detail: server.Url);
        return Results.Created($"/api/admin/tools/servers/{server.Id}", new { server.Id, toolId = McpServerTool.Prefix + server.Id });
    }

    private static async Task<IResult> UpdateServerAsync(Guid id, McpServerRequest body, AppDbContext db, Audit audit, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } server)
        {
            return Results.NotFound();
        }
        if (await ApplyAsync(server, body, db, auth.Value.DataKey, creating: false, ct) is { } problem)
        {
            return problem;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.server_update", server.Name, detail: body.HeaderValue is not null ? "with a new header value" : null);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveServerAsync(Guid id, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } server)
        {
            return Results.NotFound();
        }
        db.McpServers.Remove(server);
        await db.ToolSettings.Where(s => s.ToolId == McpServerTool.Prefix + id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.server_remove", server.Name);
        return Results.NoContent();
    }

    /// <summary>Connects with these details (or a saved server's secret) and lists its tools, before anyone relies on it.</summary>
    private static async Task<IResult> TestAsync(McpServerRequest body, Guid? id, AppDbContext db, ToolRegistry registry, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        var saved = id is { } sid ? await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sid, ct) : null;
        var server = new McpServer
        {
            Name = string.IsNullOrWhiteSpace(body.Name) ? saved?.Name ?? "The server" : body.Name.Trim(),
            Url = (body.Url ?? saved?.Url ?? "").Trim(),
            HeaderName = body.HeaderName ?? saved?.HeaderName,
            HeaderValueEncrypted = body.HeaderValue is { Length: > 0 } v && auth.Value.DataKey is { Length: > 0 } key ? SettingsCrypto.Encrypt(v, key) : saved?.HeaderValueEncrypted,
            EmailHeader = body.EmailHeader ?? saved?.EmailHeader,
        };
        if (!ValidUrl(server.Url))
        {
            return AuthEndpoints.Problem(400, "url", "The address must be an http(s) URL, e.g. https://tools.example.com/mcp.");
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var tools = await registry.Server(server).ListAsync(timeout.Token);
            return Results.Ok(new
            {
                ok = true,
                tools = tools.OfType<System.Text.Json.Nodes.JsonObject>().Select(t => new { name = t["name"]?.GetValue<string>(), description = t["description"]?.GetValue<string>() }),
            });
        }
        catch (Exception ex) when (ex is McpException or UriFormatException or OperationCanceledException)
        {
            return Results.Ok(new { ok = false, error = ex is OperationCanceledException ? $"{server.Name} did not answer within 20 seconds." : ex.Message });
        }
    }

    private static bool ValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    private static async Task<IResult?> ApplyAsync(McpServer server, McpServerRequest body, AppDbContext db, string? dataKey, bool creating, CancellationToken ct)
    {
        var name = body.Name?.Trim() ?? server.Name;
        if (name.Length is 0 or > 100)
        {
            return AuthEndpoints.Problem(400, "name", "A server needs a name of up to 100 characters.");
        }
        if ((await db.McpServers.Where(s => s.Id != server.Id).Select(s => s.Name).ToListAsync(ct)).Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            return AuthEndpoints.Problem(409, "exists", $"There is already a server named {name}.");
        }
        var url = body.Url?.Trim() ?? server.Url;
        if (!ValidUrl(url) || url.Length > 2000)
        {
            return AuthEndpoints.Problem(400, "url", "The address must be an http(s) URL, e.g. https://tools.example.com/mcp.");
        }
        if (body.HeaderValue is { Length: > 0 } && string.IsNullOrEmpty(dataKey))
        {
            return AuthEndpoints.Problem(400, "data_key", "APP_DATA_KEY is not set, so a header value cannot be stored encrypted.");
        }
        server.Name = name;
        server.Url = url;
        if (body.Description is not null || creating)
        {
            server.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        }
        if (body.HeaderName is not null || creating)
        {
            server.HeaderName = string.IsNullOrWhiteSpace(body.HeaderName) ? null : body.HeaderName.Trim();
        }
        if (body.HeaderValue is not null)
        {
            server.HeaderValueEncrypted = body.HeaderValue.Length == 0 ? null : SettingsCrypto.Encrypt(body.HeaderValue, dataKey!);
        }
        if (body.EmailHeader is not null || creating)
        {
            server.EmailHeader = string.IsNullOrWhiteSpace(body.EmailHeader) ? null : body.EmailHeader.Trim();
        }
        return null;
    }
}
