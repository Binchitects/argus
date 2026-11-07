using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using Llm.Api.Identity;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Code.Studio;

/// <summary>
/// A forwarded port's own host (ws-&lt;workspace&gt;-&lt;port&gt;.&lt;domain&gt;, Traefik's ports route to the app): every request
/// there goes on to what the workspace listens on at that port, WebSockets too, for the person signed in to Arena (the
/// session cookie covers the domain's subdomains) who owns the workspace, or anyone signed in who may use Code Studio
/// when its owner shared the port. Arena's session never reaches the program: its cookies are taken out of the request,
/// and the program cannot set one for Arena's domain. Nothing of Arena's API answers on these hosts.
/// </summary>
public static class PortForwards
{
    /// <summary>The client the forwarded requests go through: each connection made through the workspaces service.</summary>
    public const string Client = "studio-ports";

    /// <summary>Hop-by-hop headers (RFC 9110 7.6.1), and those the forward sets itself.</summary>
    private static readonly HashSet<string> NotPassed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Host", "Cookie", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Original-For", "X-Original-Host", "X-Original-Proto", "X-Real-Ip", "Forwarded",
    };

    /// <summary>Arena's own cookies, which stay out of what the program is sent.</summary>
    private static bool Arenas(string name) =>
        name is "llm_session" or "arena_replica" || name.StartsWith(".AspNetCore.", StringComparison.Ordinal) || name.StartsWith("llm_", StringComparison.Ordinal);

    /// <summary>Whether a request is to a forwarded port's host (the app's own middleware leaves those alone).</summary>
    public static bool IsPortHost(HttpContext http) =>
        StudioPorts.TryParseHost(http.Request.Host.Host, http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.Domain, out _, out _);

    /// <summary>The connections to forwarded ports: each a new one through the workspaces service, to the workspace and port its host names.</summary>
    public static void AddPortForwards(this IServiceCollection services)
    {
        services.AddScoped<StudioPorts>();
        services.AddSingleton<IPortRelay, PortRelay>();
        services.AddHttpClient(Client, c => c.Timeout = Timeout.InfiniteTimeSpan).ConfigurePrimaryHttpMessageHandler(sp => Handler(sp.GetRequiredService<IPortRelay>()));
    }

    /// <summary>A handler whose connections are the relay's: the address's host is &lt;workspace&gt;-&lt;port&gt;.relay.</summary>
    internal static SocketsHttpHandler Handler(IPortRelay relay) => new()
    {
        UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseProxy = false,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectCallback = async (context, ct) =>
        {
            var (workspace, port) = Target(context.DnsEndPoint.Host);
            return await relay.ConnectAsync(workspace, port, ct);
        },
    };

    private static Uri Address(Guid workspace, int port, string scheme, string pathAndQuery) => new($"{scheme}://{workspace:N}-{port}.relay{pathAndQuery}");

    private static (Guid, int) Target(string host)
    {
        var name = host.Split('.')[0];
        return (Guid.ParseExact(name[..32], "N"), int.Parse(name[33..], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Requests to forwarded ports' hosts, answered here; the rest go on.</summary>
    public static IApplicationBuilder UsePortForwards(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var domain = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.Domain;
        if (!StudioPorts.TryParseHost(http.Request.Host.Host, domain, out var id, out var port))
        {
            await next(http);
            return;
        }
        // The program's own answers, as they are (an empty 404 of its is not turned into one of Arena's).
        if (http.Features.Get<IStatusCodePagesFeature>() is { } pages)
        {
            pages.Enabled = false;
        }
        await ForwardAsync(http, id, port, domain);
    });

    private static async Task ForwardAsync(HttpContext http, Guid id, int port, string domain)
    {
        var ct = http.RequestAborted;
        var services = http.RequestServices;
        var features = services.GetRequiredService<Operations.Features>();
        var studio = services.GetRequiredService<StudioWorkspaces>();
        if (!features.IsOn("code-studio") || !await studio.EnabledAsync(ct))
        {
            await RefuseAsync(http, 404, "Not found", "Code Studio's workspaces are not set up here.");
            return;
        }
        // A session of Arena's (a key is not a browser), its person not held back by the second factor or the notice.
        if (http.User.Identity is not { IsAuthenticated: true, AuthenticationType: var scheme } || scheme != IdentityConstants.ApplicationScheme)
        {
            if (HttpMethods.IsGet(http.Request.Method) && http.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal))
            {
                var auth = services.GetRequiredService<IOptions<AuthOptions>>().Value;
                var here = $"https://{http.Request.Host}{http.Request.PathBase}{http.Request.Path}{http.Request.QueryString}";
                http.Response.Redirect($"https://{domain}{(auth.HttpsPort == 443 ? "" : $":{auth.HttpsPort}")}/login?rd={Uri.EscapeDataString(here)}");
                return;
            }
            await RefuseAsync(http, 401, "Sign in", "Sign in to Arena first.");
            return;
        }
        if (http.User.HasClaim(c => c.Type == TwoFactorPolicy.EnrolClaim)
            || (Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) && await services.GetRequiredService<Compliance.Notice>().MustAcceptAsync(userId, ct)))
        {
            await RefuseAsync(http, 403, "Not yet", "Open Arena first: it asks you something before anything else.");
            return;
        }
        var me = await services.GetRequiredService<UserManager<AppUser>>().GetUserAsync(http.User);
        var db = services.GetRequiredService<AppDbContext>();
        var workspace = await db.StudioWorkspaces.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id && w.Kind == StudioWorkspace.Server, ct);
        var setting = workspace is null ? null : await db.StudioPorts.AsNoTracking().SingleOrDefaultAsync(p => p.WorkspaceId == id && p.Port == port, ct);
        var mine = workspace is not null && me is not null && workspace.UserId == me.Id;
        var shared = setting is { Visibility: StudioPort.SignedIn, Stopped: false };
        // Its owner, or anyone signed in when it is shared; both with Code Studio among what they may use.
        if (me is null || workspace is null || (!mine && !shared) || !await services.GetRequiredService<Access.AccessService>().MayAsync(me, Core.Access.Permissions.CodeStudio, ct))
        {
            await RefuseAsync(http, 404, "Not found", "There is no forwarded port here that you may open.");
            return;
        }
        if (setting is { Stopped: true })
        {
            await RefuseAsync(http, 404, "Not forwarded", $"Port {port} is not forwarded: forward it again in the workspace's Ports view.");
            return;
        }
        if (workspace.EnginePort is null)
        {
            await RefuseAsync(http, 503, "Asleep", $"{workspace.Name} is asleep: open it in Code Studio, then start what listens on port {port}.");
            return;
        }
        try
        {
            if (http.WebSockets.IsWebSocketRequest)
            {
                await SocketAsync(http, id, port);
            }
            else
            {
                await RequestAsync(http, id, port);
            }
        }
        catch (PortRelayException ex) when (!http.Response.HasStarted)
        {
            await RefuseAsync(http, ex.Status is 404 or 403 ? 502 : ex.Status, "Not listening", ex.Status == 404 ? $"Nothing in {workspace.Name} listens on port {port} now: start it in a terminal there." : ex.Message);
        }
        catch (HttpRequestException ex) when (!http.Response.HasStarted)
        {
            var refused = ex.InnerException as PortRelayException;
            await RefuseAsync(http, 502, "Not listening", refused?.Status == 404 ? $"Nothing in {workspace.Name} listens on port {port} now: start it in a terminal there." : refused?.Message ?? ex.Message);
        }
    }

    /// <summary>A plain page (or text) saying why: the program's own host has none of Arena's pages.</summary>
    private static async Task RefuseAsync(HttpContext http, int status, string title, string message)
    {
        http.Response.StatusCode = status;
        http.Response.Headers.CacheControl = "no-store";
        if (http.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal))
        {
            http.Response.ContentType = "text/html; charset=utf-8";
            var text = WebUtility.HtmlEncode(message);
            await http.Response.WriteAsync($"<!doctype html><meta charset=utf-8><meta name=viewport content=\"width=device-width\"><title>{WebUtility.HtmlEncode(title)}</title>" +
                $"<body style=\"font:16px system-ui,sans-serif;max-width:40rem;margin:4rem auto;padding:0 1rem;color:CanvasText;background:Canvas\"><h1 style=\"font-size:1.25rem\">{WebUtility.HtmlEncode(title)}</h1><p>{text}</p>");
            return;
        }
        http.Response.ContentType = "text/plain; charset=utf-8";
        await http.Response.WriteAsync(message);
    }

    /// <summary>The request's cookies but Arena's.</summary>
    private static string? Cookies(HttpRequest request)
    {
        var kept = request.Cookies.Where(c => !Arenas(c.Key)).Select(c => $"{c.Key}={c.Value}").ToList();
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    private static async Task RequestAsync(HttpContext http, Guid id, int port)
    {
        var ct = http.RequestAborted;
        var r = http.Request;
        using var request = new HttpRequestMessage(new HttpMethod(r.Method), Address(id, port, "http", $"{r.Path}{r.QueryString}"));
        if (r.ContentLength is > 0 || r.Headers.TransferEncoding.Count > 0)
        {
            request.Content = new StreamContent(r.Body);
        }
        foreach (var (name, value) in r.Headers)
        {
            if (NotPassed.Contains(name))
            {
                continue;
            }
            if (!request.Headers.TryAddWithoutValidation(name, value.ToArray()))
            {
                request.Content?.Headers.TryAddWithoutValidation(name, value.ToArray());
            }
        }
        Forwarded(request.Headers, http, port);
        using var response = await http.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient(Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        http.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (NotPassed.Contains(name) || name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            http.Response.Headers[name] = values.ToArray();
        }
        // The program's own cookies, for its own host only: none for Arena's domain (where it would reach Arena and every other port), none of Arena's names.
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            http.Response.Headers.SetCookie = cookies.Where(KeptCookie).ToArray();
        }
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            // As it comes: a dev server's event stream, a page sent in parts.
            await http.Response.Body.FlushAsync(ct);
        }
    }

    /// <summary>A Set-Cookie the program may give: host-only, not one of Arena's names.</summary>
    public static bool KeptCookie(string cookie)
    {
        var name = cookie.Split('=', 2)[0].Trim();
        return !Arenas(name) && !cookie.Split(';').Skip(1).Any(a => a.Trim().StartsWith("domain", StringComparison.OrdinalIgnoreCase) && a.Contains('=', StringComparison.Ordinal));
    }

    /// <summary>
    /// As a dev server expects to be reached (Host: localhost, which Vite and webpack's dev servers allow), with where the
    /// browser really is (X-Forwarded-Host and -Proto) for those that build addresses or check an Origin from them.
    /// </summary>
    private static void Forwarded(System.Net.Http.Headers.HttpRequestHeaders headers, HttpContext http, int port)
    {
        headers.Host = $"localhost:{port}";
        headers.TryAddWithoutValidation("X-Forwarded-Host", http.Request.Host.Value);
        headers.TryAddWithoutValidation("X-Forwarded-Proto", http.Request.Scheme);
        headers.TryAddWithoutValidation("X-Forwarded-For", http.Connection.RemoteIpAddress?.ToString());
        if (Cookies(http.Request) is { } cookies)
        {
            headers.TryAddWithoutValidation("Cookie", cookies);
        }
    }

    /// <summary>A WebSocket (a dev server's live reload) joined to the program's: from its own pages only (their Origin).</summary>
    private static async Task SocketAsync(HttpContext http, Guid id, int port)
    {
        var ct = http.RequestAborted;
        var origin = http.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var from) || !string.Equals(from.Authority, http.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            await RefuseAsync(http, 403, "Refused", "A WebSocket of this port opens from its own pages only.");
            return;
        }
        using var upstream = new ClientWebSocket();
        foreach (var protocol in http.WebSockets.WebSocketRequestedProtocols)
        {
            upstream.Options.AddSubProtocol(protocol);
        }
        upstream.Options.SetRequestHeader("Host", $"localhost:{port}");
        // The program sees its own origin as a dev server's checks expect (this one was checked above).
        upstream.Options.SetRequestHeader("Origin", $"http://localhost:{port}");
        upstream.Options.SetRequestHeader("X-Forwarded-Host", http.Request.Host.Value);
        upstream.Options.SetRequestHeader("X-Forwarded-Proto", http.Request.Scheme);
        if (Cookies(http.Request) is { } cookies)
        {
            upstream.Options.SetRequestHeader("Cookie", cookies);
        }
        using var invoker = new HttpMessageInvoker(Handler(http.RequestServices.GetRequiredService<IPortRelay>()));
        try
        {
            await upstream.ConnectAsync(Address(id, port, "ws", $"{http.Request.Path}{http.Request.QueryString}"), invoker, ct);
        }
        catch (WebSocketException ex) when (ex.InnerException is HttpRequestException { InnerException: PortRelayException refused })
        {
            throw refused;
        }
        catch (WebSocketException)
        {
            await RefuseAsync(http, 502, "Refused", $"The program on port {port} did not take the WebSocket.");
            return;
        }
        using var socket = await http.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        await Task.WhenAll(StudioEndpoints.PumpAsync(socket, upstream, ct), StudioEndpoints.PumpAsync(upstream, socket, ct));
    }
}
