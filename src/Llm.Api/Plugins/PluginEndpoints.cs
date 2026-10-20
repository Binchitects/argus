using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Plugins;

/// <summary>Installs a plugin: by its name from a catalog, from its zip's address (with its SHA-256), or its zip itself (base64).</summary>
/// <param name="Tls">How its server's certificate is checked (Admin → Tools' choice); null: against the system's CAs.</param>
/// <param name="TlsCa">The CA to trust (PEM), with <see cref="TlsCheck.OwnCa"/>.</param>
/// <param name="Accept">The admin grants what the plugin asks for (its permissions, shown by preview).</param>
public sealed record InstallRequest(string? Name = null, string? Url = null, string? Sha256 = null, string? Zip = null, Dictionary<string, string?>? Settings = null,
    TlsCheck? Tls = null, string? TlsCa = null, bool Accept = false);

/// <param name="Accept">The admin grants what the new version asks for beyond what was granted.</param>
/// <param name="Zip">The new version as a zip (base64), for a plugin that is in no catalog; null: from its catalog.</param>
public sealed record UpdateRequest(bool Accept = false, string? Zip = null);

public sealed record RollbackRequest(string? Version = null);

public sealed record PinRequest(bool Pinned);

public sealed record CheckRequest(string Zip);

/// <param name="Tls">How its server's certificate is checked; null: unchanged.</param>
/// <param name="TlsCa">The CA to trust (PEM), with <see cref="TlsCheck.OwnCa"/>; null: the one kept.</param>
public sealed record PluginSettingsRequest(Dictionary<string, string?> Settings, TlsCheck? Tls = null, string? TlsCa = null);

public sealed record ConnectionRequest(string Secret);

/// <summary>Admin → Plugins: what can be installed, what is, and its settings; and each person's own accounts at plugins' services.</summary>
public static class PluginEndpoints
{
    public static void MapPlugins(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/plugins").RequirePermissions(Llm.Core.Access.Permissions.PluginsManage, Llm.Core.Access.Permissions.PluginsManage, new Dictionary<string, string>
        {
            ["GET /api/admin/plugins"] = Llm.Core.Access.Permissions.PluginsRead,
            ["POST /api/admin/plugins/preview"] = Llm.Core.Access.Permissions.PluginsInstall,
            ["POST /api/admin/plugins/install"] = Llm.Core.Access.Permissions.PluginsInstall,
            ["PATCH /api/admin/plugins/{id:guid}"] = Llm.Core.Access.Permissions.PluginsConfigure,
            ["POST /api/admin/plugins/{id:guid}/update"] = Llm.Core.Access.Permissions.PluginsInstall,
            ["DELETE /api/admin/plugins/{id:guid}"] = Llm.Core.Access.Permissions.PluginsRemove,
            ["POST /api/admin/plugins/{id:guid}/rollback"] = Llm.Core.Access.Permissions.PluginsInstall,
            ["POST /api/admin/plugins/{id:guid}/pin"] = Llm.Core.Access.Permissions.PluginsInstall,
            ["GET /api/admin/plugins/{id:guid}/health"] = Llm.Core.Access.Permissions.PluginsRead,
            ["POST /api/admin/plugins/{id:guid}/check"] = Llm.Core.Access.Permissions.PluginsRead,
            ["GET /api/admin/plugins/template"] = Llm.Core.Access.Permissions.PluginsRead,
            ["POST /api/admin/plugins/check"] = Llm.Core.Access.Permissions.PluginsRead,
        });
        admin.MapGet("", ListAsync);
        admin.MapPost("/preview", PreviewAsync);
        admin.MapPost("/install", InstallAsync);
        admin.MapPatch("/{id:guid}", SettingsAsync);
        admin.MapPost("/{id:guid}/update", UpdateAsync);
        admin.MapDelete("/{id:guid}", RemoveAsync);
        admin.MapPost("/{id:guid}/rollback", RollbackAsync);
        admin.MapPost("/{id:guid}/pin", PinAsync);
        admin.MapGet("/{id:guid}/health", HealthAsync);
        admin.MapPost("/{id:guid}/check", CheckNowAsync);
        admin.MapGet("/template", () => Results.File(PluginTemplate.Zip(), "application/zip", "plugin-template.zip"));
        admin.MapPost("/check", CheckAsync);

        var me = app.MapGroup("/api/account/connections").PersonOnly();
        me.MapGet("", ConnectionsAsync);
        me.MapPut("/{toolId}", SaveKeyAsync);
        me.MapDelete("/{toolId}", DisconnectAsync);
        me.MapGet("/{toolId}/connect", ConnectAsync);
        me.MapGet("/callback", CallbackAsync);
    }

    private static async Task<IResult> ListAsync(PluginCatalog catalog, AppDbContext db, CancellationToken ct)
    {
        var installed = await db.McpServers.AsNoTracking().Where(s => s.Plugin != null).OrderBy(s => s.Name).ToListAsync(ct);
        var ids = installed.Select(s => s.Id).ToList();
        var releases = await db.PluginReleases.AsNoTracking().Where(r => ids.Contains(r.ServerId)).OrderByDescending(r => r.InstalledAt)
            .Select(r => new { r.ServerId, r.Version, r.InstalledAt }).ToListAsync(ct);
        var (entries, problem) = await catalog.ListAsync(ct);
        // A newer version in a catalog, unless the plugin is pinned at its own.
        string? Newer(McpServer s) => !s.PluginPinned && entries.FirstOrDefault(e => e.Name == s.Plugin) is { } e && PluginManifest.CompareVersions(e.Version, s.PluginVersion ?? "0") > 0 ? e.Version : null;
        return Results.Ok(new
        {
            problem,
            catalog = entries.Select(e => new
            {
                e.Name, e.Version, e.Title, e.Description, e.Source, e.PersonAuth,
                installed = installed.FirstOrDefault(s => s.Plugin == e.Name) is { } s ? new { s.Id, version = s.PluginVersion } : null,
            }),
            installed = installed.Select(s => new
            {
                s.Id, toolId = McpServerTool.Prefix + s.Id, plugin = s.Plugin, title = s.Name, version = s.PluginVersion, s.Url, personAuth = s.PersonAuth,
                writes = s.Writes, settings = PluginInstaller.View(s), tls = s.Tls, tlsCa = s.TlsCa, tlsCaNames = ServerTls.CaNames(s.TlsCa),
                permissions = s.PluginPermissions, pinned = s.PluginPinned, update = Newer(s),
                releases = releases.Where(r => r.ServerId == s.Id).Select(r => new { r.Version, r.InstalledAt }),
            }),
        });
    }

    private static async Task<(PluginPackage? Package, IResult? Problem)> PackageAsync(InstallRequest body, PluginCatalog catalog, CancellationToken ct)
    {
        try
        {
            return (body switch
            {
                { Zip: { Length: > 0 } zip } => PluginCatalog.FromZip(Convert.FromBase64String(zip)),
                { Url: { Length: > 0 } url } => await catalog.DownloadAsync(url, body.Sha256, ct),
                { Name: { Length: > 0 } name } => await catalog.GetAsync(name, ct),
                _ => throw new PluginCatalog.PluginException("Say which plugin: its name in a catalog, its zip's address, or the zip."),
            }, null);
        }
        catch (Exception ex) when (ex is PluginCatalog.PluginException or PluginManifest.ManifestException or FormatException or HttpRequestException or TaskCanceledException)
        {
            return (null, AuthEndpoints.Problem(400, "plugin", ex is FormatException ? "The zip is not base64." : ex.Message));
        }
    }

    /// <summary>What installing would ask for: the plugin, its settings, its sign-in and its writes, before anything is installed.</summary>
    private static async Task<IResult> PreviewAsync(InstallRequest body, PluginCatalog catalog, CancellationToken ct)
    {
        var (found, failed) = await PackageAsync(body, catalog, ct);
        if (found is not { } package)
        {
            return failed!;
        }
        var m = package.Manifest;
        int? operations = null;
        if (package.Spec is { } spec)
        {
            try
            {
                operations = Chat.Tools.OpenApi.Operations(Chat.Tools.OpenApi.Parse(spec), "").Count;
            }
            catch (Chat.Tools.OpenApi.SpecException ex)
            {
                return AuthEndpoints.Problem(400, "plugin", ex.Message);
            }
        }
        var permissions = PluginContent.Permissions(package);
        return Results.Ok(new
        {
            m.Name, m.Version, m.Title, m.Description, m.PersonAuth, m.Help, m.Writes, operations, mcp = m.Mcp, m.Author, m.Homepage,
            settings = m.Settings.Select(x => new { x.Key, x.Title, x.Type, x.Required, x.Help }),
            prompts = (package.Prompts ?? []).Select(x => new { x.Name, x.Title }),
            assistants = package.Assistants.Select(a => new { a.Name, a.Description }),
            rules = package.Rules.Select(r => new { r.Name, r.Detector, r.Action }),
            toolsets = m.Toolsets.Select(t => new { t.Name, t.Description }),
            permissions, needsYes = PluginContent.NeedingYes(permissions).Any(),
        });
    }

    /// <summary>Asked for a yes the admin did not give: what the plugin asks for, to show them.</summary>
    private static IResult AskYes(IEnumerable<PluginGrant> permissions, string what) =>
        Results.Json(new { status = "accept", error = $"{what} asks for more than adding tools: grant it to go on.", permissions }, statusCode: 409);

    private static async Task<IResult> InstallAsync(InstallRequest body, PluginCatalog catalog, AppDbContext db, Audit audit, IOptions<AuthOptions> auth,
        UserManager<AppUser> users, ClaimsPrincipal principal, CancellationToken ct)
    {
        var (found, failed) = await PackageAsync(body, catalog, ct);
        if (found is not { } package)
        {
            return failed!;
        }
        var permissions = PluginContent.Permissions(package);
        if (PluginContent.NeedingYes(permissions).Any() && !body.Accept)
        {
            return AskYes(permissions, package.Manifest.Title);
        }
        if (await db.McpServers.AnyAsync(s => s.Plugin == package.Manifest.Name, ct))
        {
            return AuthEndpoints.Problem(409, "installed", $"{package.Manifest.Title} is installed already: update it or change its settings.");
        }
        var server = new McpServer { Name = "", Url = "" };
        if (PluginInstaller.Apply(server, package, body.Settings ?? [], auth.Value.DataKey) is { } problem)
        {
            return AuthEndpoints.Problem(400, "settings", problem);
        }
        if (ServerTls.Choose(server, body.Tls, body.TlsCa) is { } tls)
        {
            return AuthEndpoints.Problem(400, "tls", tls);
        }
        var names = await db.McpServers.Select(s => s.Name).ToListAsync(ct);
        for (var n = 2; names.Contains(server.Name, StringComparer.OrdinalIgnoreCase); n++)
        {
            server.Name = $"{package.Manifest.Title} {n}";
        }
        server.PluginPermissions = [.. permissions.Select(p => p.Key)];
        db.McpServers.Add(server);
        await db.SaveChangesAsync(ct);
        var adminId = (await users.GetUserAsync(principal))?.Id;
        db.PluginReleases.Add(PluginContent.Release(server, package, adminId));
        await Chat.PromptLibrary.SyncAsync(db, server, package, ct);
        await PluginContent.SyncAsync(db, server, package, adminId, ct);
        await audit.WriteAsync("plugin.install", package.Manifest.Name, detail: $"{package.Manifest.Version}, {server.Url}; granted {string.Join(", ", server.PluginPermissions)}");
        await ServerTls.AuditAsync(audit, server, TlsCheck.System, null);
        return Results.Created($"/api/admin/plugins/{server.Id}", new { server.Id, toolId = McpServerTool.Prefix + server.Id });
    }

    private static async Task<IResult> SettingsAsync(Guid id, PluginSettingsRequest body, AppDbContext db, Audit audit, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id && s.Plugin != null, ct) is not { } server)
        {
            return Results.NotFound();
        }
        var package = new PluginPackage(PluginManifest.Parse(server.Manifest!), server.Manifest!, server.Spec);
        if (PluginInstaller.Apply(server, package, body.Settings, auth.Value.DataKey) is { } problem)
        {
            return AuthEndpoints.Problem(400, "settings", problem);
        }
        var (was, wasCa) = (server.Tls, server.TlsCa);
        if (ServerTls.Choose(server, body.Tls, body.TlsCa) is { } tls)
        {
            return AuthEndpoints.Problem(400, "tls", tls);
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("plugin.settings", server.Plugin);
        await ServerTls.AuditAsync(audit, server, was, wasCa);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateRequest? body, PluginCatalog catalog, AppDbContext db, Audit audit, IOptions<AuthOptions> auth,
        UserManager<AppUser> users, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id && s.Plugin != null, ct) is not { } server)
        {
            return Results.NotFound();
        }
        if (server.PluginPinned)
        {
            return AuthEndpoints.Problem(409, "pinned", $"{server.Name} is pinned at {server.PluginVersion}: unpin it to update.");
        }
        try
        {
            var package = body?.Zip is { Length: > 0 } zip ? PluginCatalog.FromZip(Convert.FromBase64String(zip)) : await catalog.GetAsync(server.Plugin!, ct);
            if (package.Manifest.Name != server.Plugin)
            {
                return AuthEndpoints.Problem(400, "plugin", $"The zip is {package.Manifest.Name}, not {server.Plugin}.");
            }
            return await InstallVersionAsync(server, package, body?.Accept == true, "plugin.update", db, audit, auth, users, principal, ct);
        }
        catch (Exception ex) when (ex is PluginCatalog.PluginException or PluginManifest.ManifestException or HttpRequestException or TaskCanceledException or FormatException)
        {
            return AuthEndpoints.Problem(400, "plugin", ex.Message);
        }
    }

    /// <summary>
    /// Another version in place of the installed one, its settings kept: what it asks for beyond what was granted needs the
    /// admin's yes; its content follows it, and the version is kept to go back to.
    /// </summary>
    private static async Task<IResult> InstallVersionAsync(McpServer server, PluginPackage package, bool accept, string action, AppDbContext db, Audit audit,
        IOptions<AuthOptions> auth, UserManager<AppUser> users, ClaimsPrincipal principal, CancellationToken ct)
    {
        var permissions = PluginContent.Permissions(package);
        var more = permissions.Where(p => !server.PluginPermissions.Contains(p.Key)).ToList();
        if (PluginContent.NeedingYes(more).Any() && !accept)
        {
            return AskYes(more, $"{package.Manifest.Title} {package.Manifest.Version}");
        }
        var was = server.PluginVersion;
        if (PluginInstaller.Apply(server, package, new Dictionary<string, string?>(), auth.Value.DataKey) is { } problem)
        {
            return AuthEndpoints.Problem(400, "settings", $"The version needs its settings: {problem}");
        }
        server.PluginPermissions = [.. permissions.Select(p => p.Key)];
        var adminId = (await users.GetUserAsync(principal))?.Id;
        db.PluginReleases.Add(PluginContent.Release(server, package, adminId));
        await db.SaveChangesAsync(ct);
        await Chat.PromptLibrary.SyncAsync(db, server, package, ct);
        await PluginContent.SyncAsync(db, server, package, adminId, ct);
        await audit.WriteAsync(action, server.Plugin, detail: $"{was} → {server.PluginVersion}{(more.Count > 0 ? $"; granted {string.Join(", ", more.Select(p => p.Key))}" : "")}");
        return Results.Ok(new { version = server.PluginVersion, granted = more });
    }

    /// <summary>Back to a version installed before (the one before this, or the one named), from the files kept with it.</summary>
    private static async Task<IResult> RollbackAsync(Guid id, RollbackRequest body, AppDbContext db, Audit audit, IOptions<AuthOptions> auth,
        UserManager<AppUser> users, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id && s.Plugin != null, ct) is not { } server)
        {
            return Results.NotFound();
        }
        var releases = await db.PluginReleases.AsNoTracking().Where(r => r.ServerId == id).OrderByDescending(r => r.InstalledAt).ToListAsync(ct);
        var target = body.Version is { Length: > 0 } v ? releases.FirstOrDefault(r => r.Version == v) : releases.FirstOrDefault(r => r.Version != server.PluginVersion);
        if (target is null)
        {
            return AuthEndpoints.Problem(404, "version", "There is no earlier version kept to go back to.");
        }
        var files = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(target.FilesJson) ?? [];
        // Going back grants nothing new: it asks only for what that version had.
        return await InstallVersionAsync(server, PluginCatalog.FromFiles(files), accept: true, "plugin.rollback", db, audit, auth, users, principal, ct);
    }

    private static async Task<IResult> PinAsync(Guid id, PinRequest body, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id && s.Plugin != null, ct) is not { } server)
        {
            return Results.NotFound();
        }
        server.PluginPinned = body.Pinned;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(body.Pinned ? "plugin.pin" : "plugin.unpin", server.Plugin, detail: server.PluginVersion);
        return Results.NoContent();
    }

    /// <summary>How the plugin's tool has been doing: calls, failures and time over a week, the last error, failures in a row, whether it is on.</summary>
    private static async Task<IResult> HealthAsync(Guid id, AppDbContext db, ToolUsage usage, CancellationToken ct)
    {
        if (await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.Plugin != null, ct) is not { } server)
        {
            return Results.NotFound();
        }
        var toolId = McpServerTool.Prefix + id;
        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var calls = await db.ToolCalls.AsNoTracking().Where(c => c.ToolId == toolId && c.At >= since).Select(c => new { c.At, c.Failed, c.DurationMs, c.Error, c.Function }).ToListAsync(ct);
        var setting = await db.ToolSettings.AsNoTracking().SingleOrDefaultAsync(s => s.ToolId == toolId, ct);
        var lastFailure = calls.Where(c => c.Failed).MaxBy(c => c.At);
        return Results.Ok(new
        {
            on = setting?.Enabled ?? true, calls = calls.Count, failed = calls.Count(c => c.Failed),
            averageMs = calls.Count > 0 ? (int)calls.Average(c => c.DurationMs) : 0,
            failuresInARow = usage.FailuresInARow(toolId),
            lastError = lastFailure is null ? null : new { lastFailure.Error, lastFailure.At, lastFailure.Function },
            lastCall = calls.Count > 0 ? calls.Max(c => c.At) : (DateTimeOffset?)null,
        });
    }

    /// <summary>Its server reached now, as the admin: how many functions it offers, or why it cannot be reached.</summary>
    private static async Task<IResult> CheckNowAsync(Guid id, ToolRegistry registry, UserManager<AppUser> users, ClaimsPrincipal principal, CancellationToken ct)
    {
        var toolId = McpServerTool.Prefix + id;
        if ((await registry.AllAsync(ct)).FirstOrDefault(t => t.Tool.Id == toolId) is not { } choice)
        {
            return Results.NotFound();
        }
        var me = (await users.GetUserAsync(principal))!;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var run = await choice.Tool.StartAsync(new ToolContext(me, me.Email ?? "", new Conversation { UserId = me.Id, Title = "Admin → Plugins" }), ct);
            return Results.Ok(new { ok = true, functions = run.Functions.Count, ms = clock.ElapsedMilliseconds });
        }
        catch (Exception ex) when (ex is Chat.McpException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return Results.Ok(new { ok = false, error = ex.Message, ms = clock.ElapsedMilliseconds });
        }
    }

    /// <summary>A plugin's zip checked as installing would, and more: its problems (it would not install) and warnings, what it brings and asks for.</summary>
    private static IResult CheckAsync(CheckRequest body)
    {
        try
        {
            return Results.Ok(PluginTemplate.Check(PluginCatalog.FromZip(Convert.FromBase64String(body.Zip))));
        }
        catch (Exception ex) when (ex is PluginCatalog.PluginException or PluginManifest.ManifestException or FormatException)
        {
            return Results.Ok(new { ok = false, problems = new[] { ex is FormatException ? "The zip is not base64." : ex.Message }, warnings = Array.Empty<string>() });
        }
    }

    private static async Task<IResult> RemoveAsync(Guid id, AppDbContext db, Audit audit, AccessService access, ClaimsPrincipal principal, UserManager<AppUser> users,
        CancellationToken ct)
    {
        if (await db.McpServers.SingleOrDefaultAsync(s => s.Id == id && s.Plugin != null, ct) is not { } server)
        {
            return Results.NotFound();
        }
        var toolId = McpServerTool.Prefix + id;
        await PluginContent.RemoveAsync(db, server.Plugin!, ct);
        db.McpServers.Remove(server);
        await db.ToolSettings.Where(s => s.ToolId == toolId).ExecuteDeleteAsync(ct);
        await db.PersonCredentials.Where(c => c.ToolId == toolId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        await access.RemoveResourceAsync(await users.GetUserAsync(principal), Llm.Core.Access.ResourceKinds.Tool, toolId, ct);
        ServerClients.Forget(id);
        await audit.WriteAsync("plugin.remove", server.Plugin);
        return Results.NoContent();
    }

    // ------------------------------------------------------------- a person's connections --

    /// <summary>The plugins this person may use that sign in as them: each with whether they have connected, and how.</summary>
    private static async Task<IResult> ConnectionsAsync(ClaimsPrincipal p, UserManager<AppUser> users, ToolRegistry registry, AccessService access, AppDbContext db, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var mine = await db.PersonCredentials.AsNoTracking().Where(c => c.UserId == me.Id).ToDictionaryAsync(c => c.ToolId, ct);
        var usable = (await registry.ForAsync(await access.MembershipAsync(me, ct), ct)).Select(t => t.Tool).OfType<IServerTool>().Where(t => t.Server.PersonAuth is not null);
        return Results.Ok(usable.Select(t =>
        {
            var manifest = PluginManifest.Parse(t.Server.Manifest!);
            var saved = mine.GetValueOrDefault(t.Id);
            return new
            {
                toolId = t.Id, title = t.Title, kind = t.Server.PersonAuth, help = manifest.Help, connected = saved is not null, account = saved?.Account, since = saved?.CreatedAt,
            };
        }));
    }

    private static async Task<(AppUser Me, McpServer Server)?> UsableAsync(string toolId, ClaimsPrincipal p, UserManager<AppUser> users, ToolRegistry registry, AccessService access,
        CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var tool = (await registry.ForAsync(await access.MembershipAsync(me, ct), ct)).Select(t => t.Tool).OfType<IServerTool>().FirstOrDefault(t => t.Id == toolId);
        return tool is { Server.PersonAuth: not null } ? (me, tool.Server) : null;
    }

    private static async Task<IResult> SaveKeyAsync(string toolId, ConnectionRequest body, ClaimsPrincipal p, UserManager<AppUser> users, ToolRegistry registry, AccessService access,
        PersonCredentials people, Audit audit, CancellationToken ct)
    {
        if (await UsableAsync(toolId, p, users, registry, access, ct) is not { } found || found.Server.PersonAuth != "api_key")
        {
            return Results.NotFound();
        }
        if (body.Secret.Trim() is not { Length: > 0 and <= 4000 } secret)
        {
            return AuthEndpoints.Problem(400, "secret", "Paste your key.");
        }
        await people.SaveAsync(found.Me.Id, toolId, secret, null, null, null, ct);
        await audit.WriteAsync("plugin.connect", found.Server.Plugin, actor: found.Me);
        return Results.NoContent();
    }

    private static async Task<IResult> DisconnectAsync(string toolId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        if (await db.PersonCredentials.Where(c => c.UserId == me.Id && c.ToolId == toolId).ExecuteDeleteAsync(ct) == 0)
        {
            return Results.NotFound();
        }
        await audit.WriteAsync("plugin.disconnect", toolId, actor: me);
        return Results.NoContent();
    }

    private static IDataProtector States(IDataProtectionProvider protection) => protection.CreateProtector("plugin-oauth-state");

    private static string CallbackUrl(AuthOptions auth) => auth.Origin + "/api/account/connections/callback";

    /// <summary>Sends the person to sign in at the plugin's service; it sends them back to the callback with a code.</summary>
    private static async Task<IResult> ConnectAsync(string toolId, ClaimsPrincipal p, UserManager<AppUser> users, ToolRegistry registry, AccessService access, PersonCredentials people,
        IDataProtectionProvider protection, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (await UsableAsync(toolId, p, users, registry, access, ct) is not { } found || found.Server.PersonAuth != "oauth2")
        {
            return Results.NotFound();
        }
        var manifest = PluginManifest.Parse(found.Server.Manifest!);
        var values = people.Settings(found.Server);
        // Whose and for what, for ten minutes, sealed: the callback trusts nothing else.
        var state = States(protection).Protect($"{found.Me.Id}|{toolId}|{DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()}");
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = values.GetValueOrDefault("client_id"), ["redirect_uri"] = CallbackUrl(auth.Value), ["response_type"] = "code",
            ["scope"] = string.Join(' ', manifest.OAuth!.Scopes), ["state"] = state,
        };
        return Results.Redirect(Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(PluginManifest.Fill(manifest.OAuth.Authorize, values), query));
    }

    private static async Task<IResult> CallbackAsync(string? code, string? state, string? error, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, PersonCredentials people,
        IDataProtectionProvider protection, IOptions<AuthOptions> auth, Audit audit, CancellationToken ct)
    {
        IResult Back(string query) => Results.Redirect("/account?" + query);
        var me = (await users.GetUserAsync(p))!;
        string[] parts;
        try
        {
            parts = States(protection).Unprotect(state ?? "").Split('|');
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Back("connection=failed");
        }
        if (parts.Length != 3 || parts[0] != me.Id.ToString() || long.Parse(parts[2], CultureInfo.InvariantCulture) < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            return Back("connection=failed");
        }
        var toolId = parts[1];
        if (!Guid.TryParse(toolId.Replace(McpServerTool.Prefix, "", StringComparison.Ordinal), out var serverId)
            || await db.McpServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serverId && s.PersonAuth == "oauth2", ct) is not { } server)
        {
            return Back("connection=failed");
        }
        if (error is not null || code is null)
        {
            return Back("connection=declined");
        }
        try
        {
            var (token, refresh, expires) = await people.ExchangeAsync(server, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = CallbackUrl(auth.Value),
            }, ct);
            await people.SaveAsync(me.Id, toolId, token, refresh, expires, null, ct);
        }
        catch (Exception ex) when (ex is PluginCatalog.PluginException or HttpRequestException or System.Text.Json.JsonException)
        {
            await audit.WriteAsync("plugin.connect", server.Plugin, success: false, detail: ex.Message, actor: me);
            return Back("connection=failed");
        }
        await audit.WriteAsync("plugin.connect", server.Plugin, actor: me);
        return Back("connected=" + Uri.EscapeDataString(server.Name));
    }
}
