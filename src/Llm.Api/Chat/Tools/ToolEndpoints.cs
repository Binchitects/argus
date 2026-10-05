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
/// <param name="CallTimeoutMinutes">Longest one call may take; 0: the chat's limit (Chat:ToolCallTimeout); null: unchanged.</param>
/// <param name="Spec">An API's OpenAPI document (JSON or YAML): it is then an API, not an MCP server; "" makes it an MCP server again; null: unchanged.</param>
/// <param name="SpecUrl">Where to fetch the OpenAPI document from, now (instead of <paramref name="Spec"/>).</param>
/// <param name="Tls">How its certificate is checked; null: unchanged.</param>
/// <param name="TlsCa">The CA to trust (PEM), with <see cref="TlsCheck.OwnCa"/>; null: the one kept.</param>
public sealed record McpServerRequest(string? Name = null, string? Description = null, string? Url = null, string? HeaderName = null, string? HeaderValue = null, string? EmailHeader = null,
    int? CallTimeoutMinutes = null, string? Spec = null, string? SpecUrl = null, TlsCheck? Tls = null, string? TlsCa = null);

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
            server = t.Tool is IServerTool m ? new
            {
                m.Server.Id, m.Server.Name, m.Server.Description, m.Server.Url, m.Server.HeaderName, headerSet = m.Server.HeaderValueEncrypted is not null,
                m.Server.EmailHeader, m.Server.CallTimeoutMinutes, prefix = m.Slug + "__", kind = m is OpenApiTool ? "openapi" : "mcp",
                // A long document stays out of the list (an edit keeps it unless a new one is given).
                spec = m.Server.Spec is { Length: <= 200_000 } spec ? spec : null,
                tls = m.Server.Tls, tlsCa = m.Server.TlsCa, tlsCaNames = ServerTls.CaNames(m.Server.TlsCa),
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

    private static async Task<IResult> AddServerAsync(McpServerRequest body, AppDbContext db, Audit audit, IOptions<AuthOptions> auth, IHttpClientFactory http, CancellationToken ct)
    {
        var server = new McpServer { Name = "", Url = "" };
        if (await ApplyAsync(server, body, db, auth.Value.DataKey, http, creating: true, ct) is { } problem)
        {
            return problem;
        }
        db.McpServers.Add(server);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.server_add", server.Name, detail: server.Spec is null ? server.Url : $"API {server.Url}");
        await ServerTls.AuditAsync(audit, server, TlsCheck.System, null);
        return Results.Created($"/api/admin/tools/servers/{server.Id}", new { server.Id, toolId = McpServerTool.Prefix + server.Id });
    }

    private static async Task<IResult> UpdateServerAsync(Guid id, McpServerRequest body, AppDbContext db, Audit audit, IOptions<AuthOptions> auth, IHttpClientFactory http, CancellationToken ct)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } server)
        {
            return Results.NotFound();
        }
        var (tls, ca) = (server.Tls, server.TlsCa);
        if (await ApplyAsync(server, body, db, auth.Value.DataKey, http, creating: false, ct) is { } problem)
        {
            return problem;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("tool.server_update", server.Name, detail: body.HeaderValue is not null ? "with a new header value" : null);
        await ServerTls.AuditAsync(audit, server, tls, ca);
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
        ServerClients.Forget(id);
        await audit.WriteAsync("tool.server_remove", server.Name);
        return Results.NoContent();
    }

    /// <summary>
    /// Connects with these details (or a saved server's secret) and lists its tools, before anyone relies on it.
    /// A refused certificate comes with what it is and why (who issued it, for which names, its dates).
    /// </summary>
    private static async Task<IResult> TestAsync(McpServerRequest body, Guid? id, AppDbContext db, ToolRegistry registry, IOptions<AuthOptions> auth, IHttpClientFactory http,
        CancellationToken ct)
    {
        var saved = id is { } sid ? await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sid, ct) : null;
        // The form's certificate check (or the saved one's), on a connection of its own.
        var tls = new McpServer { Name = "", Url = "", Tls = saved?.Tls ?? TlsCheck.System, TlsCa = saved?.TlsCa };
        if (ServerTls.Choose(tls, body.Tls, body.TlsCa) is { } badTls)
        {
            return AuthEndpoints.Problem(400, "tls", badTls);
        }
        using var client = ServerClients.Once(http, tls.Tls, tls.TlsCa);
        // An API: its document read, its operations listed (what changes something marked), nothing called.
        if (body.Spec is { Length: > 0 } || body.SpecUrl is { Length: > 0 } || (body.Spec is null && saved?.Spec is not null))
        {
            var api = new McpServer { Name = string.IsNullOrWhiteSpace(body.Name) ? saved?.Name ?? "The API" : body.Name.Trim(), Url = (body.Url ?? saved?.Url ?? "").Trim() };
            if (await SpecAsync(api, body, saved?.Spec, client, ct) is ({ } bad, var untrusted))
            {
                return Results.Ok(new { ok = false, error = bad, certificate = untrusted is null ? null : await ServerTls.ProbeAsync(untrusted, tls.Tls, tls.TlsCa, ct) });
            }
            // Its calls go to its address, which the document may not have come from: no secure connection there is said now, with why.
            if (await SecureAsync(api.Url, client, ct) is ({ } insecure, var refused))
            {
                return Results.Ok(new { ok = false, error = insecure, certificate = refused is null ? null : await ServerTls.ProbeAsync(refused, tls.Tls, tls.TlsCa, ct) });
            }
            var ops = ((OpenApiTool)registry.Server(api, client)).Operations();
            return Results.Ok(new
            {
                ok = true, url = api.Url,
                tools = ops.Select(o => new { name = o.Function, description = o.Definition["function"]!["description"]!.GetValue<string>(), asksFirst = o.Writes }),
            });
        }
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
            var tools = await ((McpServerTool)registry.Server(server, client)).ListAsync(timeout.Token);
            return Results.Ok(new
            {
                ok = true,
                tools = tools.OfType<System.Text.Json.Nodes.JsonObject>().Select(t => new { name = t["name"]?.GetValue<string>(), description = t["description"]?.GetValue<string>() }),
            });
        }
        catch (McpException ex) when (ex.Certificate)
        {
            return Results.Ok(new { ok = false, error = ex.Message, certificate = await ServerTls.ProbeAsync(new Uri(server.Url), tls.Tls, tls.TlsCa, ct) });
        }
        catch (Exception ex) when (ex is McpException or UriFormatException or OperationCanceledException)
        {
            return Results.Ok(new { ok = false, error = ex is OperationCanceledException ? $"{server.Name} did not answer within 20 seconds." : ex.Message });
        }
    }

    private static bool ValidUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Why no secure connection can be set up with the API's https address (one HEAD request, nothing
    /// called): its certificate refused (and that address, to say why), or a handshake that failed for
    /// another reason. Nothing otherwise: an API that is down, slow or does not take HEAD is not what
    /// reading its document tests.
    /// </summary>
    private static async Task<(string? Problem, Uri? Untrusted)> SecureAsync(string url, HttpClient http, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var at) || at.Scheme != Uri.UriSchemeHttps)
        {
            return (null, null);
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var head = new HttpRequestMessage(HttpMethod.Head, at);
            using var answer = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return (null, null);
        }
        catch (HttpRequestException ex) when (ServerTls.IsCertificateError(ex))
        {
            return ($"The API at {at} cannot be called: its certificate is not trusted. Trust the CA that signed it, or stop checking it.", at);
        }
        catch (HttpRequestException ex) when (ServerTls.HandshakeFailed(at.Host, ex) is { } handshake)
        {
            return ($"The API at {at} cannot be called. {handshake}", null);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Puts the API's document in place (given, fetched from SpecUrl with the API's certificate check, or kept),
    /// checks it lists at least one operation, and takes the address from it when none is given. Why not
    /// (and the address whose certificate was refused, if that was why), or nothing.
    /// </summary>
    private static async Task<(string? Problem, Uri? Untrusted)> SpecAsync(McpServer api, McpServerRequest body, string? kept, HttpClient http, CancellationToken ct)
    {
        var text = body.Spec is { Length: > 0 } given ? given : kept;
        Uri? from = null;
        if (body.SpecUrl is { Length: > 0 } specUrl)
        {
            if (!Uri.TryCreate(specUrl.Trim(), UriKind.Absolute, out from) || from.Scheme is not ("http" or "https"))
            {
                return ("The document's address must be an http(s) URL.", null);
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                text = await http.GetStringAsync(from, timeout.Token);
            }
            catch (HttpRequestException ex) when (ServerTls.IsCertificateError(ex))
            {
                return ($"The document could not be fetched from {from}: its certificate is not trusted. Trust the CA that signed it, or stop checking it.", from);
            }
            catch (HttpRequestException ex) when (ServerTls.HandshakeFailed(from.Host, ex) is { } handshake)
            {
                return ($"The document could not be fetched from {from}. {handshake}", null);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                return ($"The document could not be fetched from {from}: {(ex is OperationCanceledException ? "no answer within 20 seconds" : ex.Message)}", null);
            }
        }
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxSpecChars)
        {
            return ($"Give the API's OpenAPI document (JSON or YAML, up to {MaxSpecChars / 1_000_000} MB), or the address to fetch it from.", null);
        }
        try
        {
            var doc = OpenApi.Parse(text);
            if (OpenApi.Operations(doc, "").Count == 0)
            {
                return ("The document lists no operations (paths).", null);
            }
            api.Spec = text;
            if (string.IsNullOrWhiteSpace(body.Url) && string.IsNullOrWhiteSpace(api.Url))
            {
                api.Url = OpenApi.ServerUrl(doc, from) ?? "";
            }
            return (null, null);
        }
        catch (OpenApi.SpecException ex)
        {
            return (ex.Message, null);
        }
    }

    private const int MaxSpecChars = 5_000_000;

    private static async Task<IResult?> ApplyAsync(McpServer server, McpServerRequest body, AppDbContext db, string? dataKey, IHttpClientFactory http, bool creating, CancellationToken ct)
    {
        // First, as its document is fetched with it.
        if (ServerTls.Choose(server, body.Tls, body.TlsCa) is { } tls)
        {
            return AuthEndpoints.Problem(400, "tls", tls);
        }
        if (body.Spec == "")
        {
            server.Spec = null;
        }
        else if (body.Spec is { Length: > 0 } || body.SpecUrl is { Length: > 0 })
        {
            var address = server.Url;
            server.Url = body.Url?.Trim() ?? "";
            using var client = ServerClients.Once(http, server.Tls, server.TlsCa);
            if ((await SpecAsync(server, body, null, client, ct)).Problem is { } bad)
            {
                server.Url = address;
                return AuthEndpoints.Problem(400, "spec", bad);
            }
            if (string.IsNullOrWhiteSpace(body.Url))
            {
                body = body with { Url = server.Url.Length > 0 ? server.Url : address };
            }
        }
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
            return AuthEndpoints.Problem(400, "url", server.Spec is null
                ? "The address must be an http(s) URL, e.g. https://tools.example.com/mcp."
                : "The API's address must be an http(s) URL, e.g. https://api.example.com/v1 (its document names none).");
        }
        if (body.CallTimeoutMinutes is < 0 or > 1440)
        {
            return AuthEndpoints.Problem(400, "call_timeout", "The longest call is between 1 minute and 24 hours (1440 minutes); 0 for the chat's limit.");
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
        if (body.CallTimeoutMinutes is { } minutes)
        {
            server.CallTimeoutMinutes = minutes == 0 ? null : minutes;
        }
        return null;
    }
}
