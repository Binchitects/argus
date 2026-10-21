using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools.Browser;

/// <summary>Configuration section "Browser": the browser agent's browser, and what it may open.</summary>
public sealed class BrowserOptions
{
    /// <summary>The browser's DevTools, as the app reaches it (the browser profile's container); empty: no browser.</summary>
    public string? Url { get; set; }

    /// <summary>Sites it may open, as Web:AllowedSites ("*" any public site); empty: the Web tool's.</summary>
    public string? Sites { get; set; }

    /// <summary>Sites it never opens, whatever <see cref="Sites"/> says.</summary>
    public string? BlockedSites { get; set; }

    /// <summary>The port of the app's proxy every page goes through; 0: none (no browser).</summary>
    public int ProxyPort { get; set; } = 3128;

    /// <summary>Who may use the proxy: host names or addresses, comma separated (the browser's container).</summary>
    public string ProxyClients { get; set; } = "browser";

    /// <summary>A chat's browser is closed after this long unused.</summary>
    public int IdleMinutes { get; set; } = 15;

    /// <summary>The most chats with a browser open at once; the one unused longest is closed for a new one.</summary>
    public int MostSessions { get; set; } = 6;
}

/// <summary>
/// The only way out for the browser agent's browser: an HTTP proxy (CONNECT for https, absolute URLs for http) that opens a
/// site only when the browser may open it (Browser:Sites, else Web:AllowedSites, never Browser:BlockedSites), on ports 80
/// and 443, at a public address it resolved itself (never this machine, the stack or the office network), and only for the
/// browser's container. One request a connection for plain http; a tunnel for https.
/// </summary>
public sealed partial class BrowserProxy(IOptionsMonitor<BrowserOptions> options, IOptionsMonitor<WebOptions> web, WebResolver resolver, ILogger<BrowserProxy> logger)
    : BackgroundService
{
    private const int MostHead = 16 * 1024;

    /// <summary>The sites it may open now: its own list, else the Web tool's.</summary>
    public string Sites => options.CurrentValue.Sites is { Length: > 0 } own ? own : web.CurrentValue.AllowedSites ?? "";

    /// <summary>Where a site may be reached (a public address of it), or why it may not be opened.</summary>
    public async Task<(IPAddress? Address, string? Refusal)> CheckAsync(string host, int port, CancellationToken ct)
    {
        host = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (port is not (80 or 443))
        {
            return (null, $"The browser opens sites on the usual ports only (80 and 443), not {port}.");
        }
        if (Sites.Trim().Length == 0)
        {
            return (null, "The browser may open no site yet: an admin chooses which (Settings → Tools → Sites the browser agent may open).");
        }
        if (!WebGuard.Allowed(host, Sites))
        {
            return (null, $"{host} is not among the sites the browser may open ({Sites}).");
        }
        if (options.CurrentValue.BlockedSites is { Length: > 0 } blocked && WebGuard.Allowed(host, blocked))
        {
            return (null, $"{host} is among the sites the browser never opens.");
        }
        IPAddress[] addresses;
        try
        {
            addresses = await resolver.ResolveAsync(host, ct);
        }
        catch (SocketException)
        {
            return (null, $"{host} could not be found.");
        }
        return addresses.FirstOrDefault(WebGuard.IsPublic) is { } address
            ? (address, null)
            : (null, $"{host} is inside your network (or not on the internet): the browser never opens it.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var port = options.CurrentValue.ProxyPort;
        if (port <= 0 || string.IsNullOrWhiteSpace(options.CurrentValue.Url))
        {
            return;
        }
        var listener = new TcpListener(IPAddress.Any, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            LogListen(logger, port, ex);
            return;
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => ServeAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>Whether a connection comes from the browser's container (or another of <see cref="BrowserOptions.ProxyClients"/>).</summary>
    private async Task<bool> FromBrowserAsync(IPAddress remote, CancellationToken ct)
    {
        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }
        foreach (var name in options.CurrentValue.ProxyClients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var addresses = IPAddress.TryParse(name, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(name, ct);
                if (addresses.Any(a => (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).Equals(remote)))
                {
                    return true;
                }
            }
            catch (SocketException)
            {
            }
        }
        return false;
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var stream = client.GetStream();
        try
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !await FromBrowserAsync(remote.Address, ct))
            {
                return;
            }
            var (head, rest) = await ReadHeadAsync(stream, ct);
            if (head is null)
            {
                return;
            }
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length != 3)
            {
                await RefuseAsync(stream, 400, "Not a request.", ct);
                return;
            }
            var (method, target) = (parts[0], parts[1]);
            if (method == "CONNECT")
            {
                var colon = target.LastIndexOf(':');
                var host = colon > 0 ? target[..colon] : target;
                var port = colon > 0 && int.TryParse(target[(colon + 1)..], out var p) ? p : 443;
                var (address, refusal) = await CheckAsync(host, port, ct);
                if (address is null)
                {
                    await RefuseAsync(stream, 403, refusal!, ct);
                    return;
                }
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(address, port, ct);
                await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
                await PipeAsync(stream, upstream.GetStream(), rest, ct);
                return;
            }
            if (!Uri.TryCreate(target, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttp)
            {
                await RefuseAsync(stream, 400, "The browser's proxy takes http addresses and https tunnels only.", ct);
                return;
            }
            var (to, why) = await CheckAsync(url.Host, url.Port, ct);
            if (to is null)
            {
                await RefuseAsync(stream, 403, why!, ct);
                return;
            }
            // The request as the site expects it: its path, the browser's headers but the proxy's own, one request a connection.
            var request = new StringBuilder().Append(method).Append(' ').Append(url.PathAndQuery).Append(" HTTP/1.1\r\n");
            foreach (var line in lines.Skip(1).Where(l => l.Length > 0))
            {
                var name = line.Split(':', 2)[0].Trim();
                if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                request.Append(line).Append("\r\n");
            }
            request.Append("Connection: close\r\n\r\n");
            using var site = new TcpClient();
            await site.ConnectAsync(to, url.Port, ct);
            var siteStream = site.GetStream();
            await siteStream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), ct);
            await PipeAsync(stream, siteStream, rest, ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    /// <summary>The request's head (up to its blank line), and what came after it in the same read.</summary>
    private static async Task<(string? Head, byte[] Early)> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MostHead];
        var filled = 0;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled), timeout.Token);
            if (read == 0)
            {
                return (null, []);
            }
            filled += read;
            var end = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                return (Encoding.ASCII.GetString(buffer, 0, end), buffer[(end + 4)..filled]);
            }
        }
        return (null, []);
    }

    private static async Task RefuseAsync(NetworkStream stream, int status, string why, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(why);
        var reason = status == 403 ? "Forbidden" : "Bad Request";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct);
        await stream.WriteAsync(body, ct);
    }

    private static async Task PipeAsync(NetworkStream client, NetworkStream site, byte[] early, CancellationToken ct)
    {
        if (early.Length > 0)
        {
            await site.WriteAsync(early, ct);
        }
        using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var up = client.CopyToAsync(site, done.Token);
        var down = site.CopyToAsync(client, done.Token);
        await Task.WhenAny(up, down);
        await done.CancelAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The browser's proxy could not listen on port {Port}")]
    private static partial void LogListen(ILogger logger, int port, Exception ex);
}
