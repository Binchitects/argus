using System.Net.WebSockets;
using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Code.Studio;

public sealed record WorkspaceRequest(string? Project, string? Branch, string? Name);

/// <summary>
/// Code Studio: the person's workspaces (made from a GitLab project and branch, started, put to sleep, deleted), and the
/// IDE's way to a workspace's engine: every request of the IDE's page under /api/studio/w/{id}/… goes on to the engine's
/// /api/… with the workspace's key (the page never has it), its answer streamed back as it comes (the agent's events),
/// and a WebSocket (a terminal, a language server, the debugger) joined end to end. Only the workspace's owner reaches it.
/// </summary>
public static class StudioEndpoints
{
    /// <summary>The request headers the engine is given (the rest, cookies among them, stay here).</summary>
    private static readonly string[] Passed = ["Accept", "Content-Type", "Range", "If-None-Match", "If-Modified-Since", "Last-Event-ID"];

    /// <summary>The answer's headers passed back to the page.</summary>
    private static readonly string[] Back = ["Content-Type", "Content-Disposition", "Content-Range", "Accept-Ranges", "ETag", "Last-Modified"];

    public static void MapStudio(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/studio").PersonOnly().RequirePermission(Core.Access.Permissions.CodeStudio);
        g.MapGet("/workspaces", ListAsync);
        g.MapPost("/workspaces", CreateAsync);
        g.MapPost("/workspaces/{id:guid}/start", StartAsync);
        g.MapPost("/workspaces/{id:guid}/stop", StopAsync);
        g.MapDelete("/workspaces/{id:guid}", DeleteAsync);
        g.Map("/w/{id:guid}/{**rest}", ProxyAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object View(StudioWorkspace w) => new
    {
        w.Id, w.Kind, w.Name, w.Project, w.Branch, state = w.EnginePort is not null ? "running" : w.Error is not null ? "failed" : "asleep", w.Error, w.CreatedAt, w.UsedAt,
    };

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, StudioWorkspaces workspaces, CancellationToken ct)
    {
        var me = await Me(p, users);
        var mine = await db.StudioWorkspaces.AsNoTracking().Where(w => w.UserId == me.Id).OrderByDescending(w => w.UsedAt).ToListAsync(ct);
        return Results.Ok(new { server = await workspaces.EnabledAsync(ct), workspaces = mine.Select(View) });
    }

    private static async Task<IResult> CreateAsync(WorkspaceRequest body, ClaimsPrincipal p, UserManager<AppUser> users, StudioWorkspaces workspaces, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (string.IsNullOrWhiteSpace(body.Project))
        {
            return AuthEndpoints.Problem(400, "project", "Choose a project (group/project).");
        }
        try
        {
            var made = await workspaces.CreateAsync(me, body.Project, body.Branch, body.Name, ct);
            await audit.WriteAsync("studio.workspace.create", $"{made.Project}@{made.Branch}", actor: me);
            return Results.Ok(View(made));
        }
        catch (StudioException ex)
        {
            return AuthEndpoints.Problem(ex.Status, "workspace", ex.Message);
        }
    }

    private static async Task<StudioWorkspace?> MineAsync(AppDbContext db, Guid id, AppUser me, CancellationToken ct) =>
        await db.StudioWorkspaces.SingleOrDefaultAsync(w => w.Id == id && w.UserId == me.Id, ct);

    private static async Task<IResult> StartAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, StudioWorkspaces workspaces, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } w)
        {
            return Results.NotFound();
        }
        try
        {
            await workspaces.StartAsync(w, me, ct);
            return Results.Ok(View(w));
        }
        catch (StudioException ex)
        {
            return AuthEndpoints.Problem(ex.Status, "workspace", ex.Message);
        }
    }

    private static async Task<IResult> StopAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, StudioWorkspaces workspaces, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } w)
        {
            return Results.NotFound();
        }
        await workspaces.StopAsync(w, ct);
        return Results.Ok(View(w));
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, StudioWorkspaces workspaces, Audit audit, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } w)
        {
            return Results.NotFound();
        }
        await workspaces.DeleteAsync(w, ct);
        await audit.WriteAsync("studio.workspace.delete", $"{w.Project}@{w.Branch}", actor: me);
        return Results.NoContent();
    }

    /// <summary>The IDE's request on to the workspace's engine, and its answer back as it comes; a WebSocket joined end to end.</summary>
    private static async Task ProxyAsync(Guid id, string? rest, HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, StudioWorkspaces workspaces,
        IHttpClientFactory clients, TimeProvider clock, IOptionsMonitor<WorkspacesOptions> options)
    {
        var ct = http.RequestAborted;
        var me = await Me(p, users);
        if (await MineAsync(db, id, me, ct) is not { } w)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (workspaces.EngineOf(w) is not { } engine)
        {
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            await http.Response.WriteAsJsonAsync(new { status = "asleep", error = "The workspace is asleep: start it." }, ct);
            return;
        }
        // Used now: a workspace the IDE reaches does not sleep (written a minute apart at most).
        var now = clock.GetUtcNow();
        if (now - w.UsedAt > TimeSpan.FromMinutes(1))
        {
            w.UsedAt = now;
            await db.SaveChangesAsync(ct);
        }
        var target = new Uri(engine.Base, $"api/{rest}{http.Request.QueryString}");
        if (http.WebSockets.IsWebSocketRequest)
        {
            await RelaySocketAsync(http, target, engine.Key, ct);
            return;
        }
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", engine.Key);
        if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0)
        {
            request.Content = new StreamContent(http.Request.Body);
        }
        foreach (var name in Passed)
        {
            if (http.Request.Headers.TryGetValue(name, out var value))
            {
                if (!request.Headers.TryAddWithoutValidation(name, value.ToArray()) && request.Content is not null)
                {
                    request.Content.Headers.TryAddWithoutValidation(name, value.ToArray());
                }
            }
        }
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(StudioWorkspaces.Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException)
        {
            // Its engine went (the service restarted, it was stopped there): asleep, to be started again.
            await workspaces.StopAsync(w, CancellationToken.None);
            http.Response.StatusCode = StatusCodes.Status409Conflict;
            await http.Response.WriteAsJsonAsync(new { status = "asleep", error = "The workspace's engine stopped: start it again." }, CancellationToken.None);
            return;
        }
        using (response)
        {
            http.Response.StatusCode = (int)response.StatusCode;
            foreach (var name in Back)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                {
                    http.Response.Headers[name] = values.ToArray();
                }
            }
            http.Response.Headers.CacheControl = "no-store";
            // As it comes: an agent's turn is a stream of events.
            var events = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
            if (events)
            {
                http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
            }
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                if (events)
                {
                    await http.Response.Body.FlushAsync(ct);
                }
            }
        }
    }

    /// <summary>
    /// A WebSocket of the IDE's (a terminal, a language server, the debugger) joined to the engine's: from this app's own
    /// page only (its Origin), each message passed on whole, either side's close passed to the other.
    /// </summary>
    private static async Task RelaySocketAsync(HttpContext http, Uri target, string key, CancellationToken ct)
    {
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var from) || !string.Equals(from.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        using var upstream = new ClientWebSocket();
        upstream.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        var builder = new UriBuilder(target) { Scheme = target.Scheme == "https" ? "wss" : "ws" };
        try
        {
            await upstream.ConnectAsync(builder.Uri, ct);
        }
        catch (WebSocketException)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        // Both ways until both are closed (each pump passes a close on, and answers it).
        await Task.WhenAll(PumpAsync(socket, upstream, ct), PumpAsync(upstream, socket, ct));
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var got = await from.ReceiveAsync(buffer, ct);
                if (got.MessageType == WebSocketMessageType.Close)
                {
                    // Closed on one side: passed to the other (or its own close answered there), and answered on this one.
                    var status = from.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
                    if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await to.CloseOutputAsync(status, from.CloseStatusDescription, CancellationToken.None);
                    }
                    if (from.State == WebSocketState.CloseReceived)
                    {
                        await from.CloseOutputAsync(status, from.CloseStatusDescription, CancellationToken.None);
                    }
                    return;
                }
                await to.SendAsync(buffer.AsMemory(0, got.Count), got.MessageType, got.EndOfMessage, ct);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            // Either side gone: the other is closed with it (which ends the other pump).
            if (to.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await to.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "gone", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }
}

/// <summary>A workspace unused a while sleeps: its engine stopped (its files kept), so the service's room goes to those in use.</summary>
public sealed partial class StudioIdle(IServiceScopeFactory scopes, IOptionsMonitor<WorkspacesOptions> options, TimeProvider clock, Operations.Replicas replicas,
    ILogger<StudioIdle> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), clock, stoppingToken);
                if (!replicas.IsLeader)
                {
                    continue;
                }
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var before = clock.GetUtcNow() - options.CurrentValue.Idle;
                var idle = await db.StudioWorkspaces.Where(w => w.EnginePort != null && w.UsedAt < before).ToListAsync(stoppingToken);
                foreach (var w in idle)
                {
                    await scope.ServiceProvider.GetRequiredService<StudioWorkspaces>().StopAsync(w, stoppingToken);
                    Slept(logger, w.Id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or DbUpdateException or InvalidOperationException)
            {
                Failed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Code Studio: workspace {Id} slept (unused)")]
    private static partial void Slept(ILogger logger, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Code Studio: putting idle workspaces to sleep failed")]
    private static partial void Failed(ILogger logger, Exception ex);
}
