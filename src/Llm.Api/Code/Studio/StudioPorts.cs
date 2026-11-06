using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Identity;
using Llm.Core.Code;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Code.Studio;

/// <summary>A workspace's port as its Ports view shows it.</summary>
public sealed record PortView(int Port, string? Label, string Url, string Visibility, string Origin, bool Listening, bool Stopped, string? Process, int? Pid, string? Address);

/// <summary>
/// Code Studio's forwarded ports: what a workspace's programs listen on (a dev server, found by the workspaces service),
/// and those its owner forwarded by hand, each at an address of its own under Arena's domain
/// (ws-&lt;workspace&gt;-&lt;port&gt;.&lt;domain&gt;): its own origin, so the program's pages never run on Arena's, reached with the
/// person's Arena session, by its owner alone unless they share it with everyone signed in.
/// </summary>
public sealed class StudioPorts(AppDbContext db, IHttpClientFactory http, IOptionsMonitor<WorkspacesOptions> options, IOptions<AuthOptions> auth)
{
    /// <summary>The most ports one workspace keeps a setting for.</summary>
    public const int Most = 50;

    /// <summary>A forwarded port's host: ws-&lt;workspace id&gt;-&lt;port&gt; under the domain (one label: the stack's *.domain certificate covers it).</summary>
    public static string Host(Guid workspace, int port, string domain) => $"ws-{workspace:N}-{port}.{domain}";

    /// <summary>The workspace and port a request's host names, when it is a forwarded port's.</summary>
    public static bool TryParseHost(string host, string domain, out Guid workspace, out int port)
    {
        (workspace, port) = (Guid.Empty, 0);
        var suffix = "." + domain;
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var label = host[..^suffix.Length];
        // ws- (3), 32 hex digits, -, 1 to 5 digits.
        if (label.Length is < 38 or > 42 || !label.StartsWith("ws-", StringComparison.OrdinalIgnoreCase) || label[35] != '-'
            || !Guid.TryParseExact(label[3..35], "N", out workspace) || !label[36..].All(char.IsAsciiDigit)
            || !int.TryParse(label[36..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        {
            (workspace, port) = (Guid.Empty, 0);
            return false;
        }
        return true;
    }

    public string Url(Guid workspace, int port) =>
        $"https://{Host(workspace, port, auth.Value.Domain)}{(auth.Value.HttpsPort == 443 ? "" : $":{auth.Value.HttpsPort}")}/";

    private sealed record Found(int Port, string? Address, int? Pid, string? Process);

    /// <summary>What a running workspace's programs listen on, as the workspaces service sees them; none when it sleeps.</summary>
    private async Task<IReadOnlyList<Found>> ListeningAsync(StudioWorkspace workspace, CancellationToken ct)
    {
        if (workspace.Kind != StudioWorkspace.Server || workspace.EnginePort is null || options.CurrentValue is not { Url.Length: > 0 } o)
        {
            return [];
        }
        var c = http.CreateClient(StudioWorkspaces.Client);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(o.Url.TrimEnd('/') + "/"), $"v1/workspaces/{workspace.Id:N}/ports"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", o.Key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var res = await c.SendAsync(request, timeout.Token);
            if (!res.IsSuccessStatusCode || await res.Content.ReadFromJsonAsync<JsonArray>(timeout.Token) is not { } list)
            {
                return [];
            }
            return [.. list.OfType<JsonObject>().Select(p => new Found(p["port"]!.GetValue<int>(), p["address"]?.GetValue<string>(), p["pid"]?.GetValue<int>(), p["process"]?.GetValue<string>()))];
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>The workspace's ports: those listening (forwarded to its owner unless stopped), and those its owner set, by port.</summary>
    public async Task<IReadOnlyList<PortView>> ListAsync(StudioWorkspace workspace, CancellationToken ct)
    {
        var set = await db.StudioPorts.AsNoTracking().Where(p => p.WorkspaceId == workspace.Id).ToDictionaryAsync(p => p.Port, ct);
        var listening = (await ListeningAsync(workspace, ct)).ToDictionary(p => p.Port);
        return [.. set.Keys.Union(listening.Keys).Order().Select(port =>
        {
            var s = set.GetValueOrDefault(port);
            var l = listening.GetValueOrDefault(port);
            return new PortView(port, s?.Label, Url(workspace.Id, port), s?.Visibility ?? StudioPort.Private, s is { Added: true } ? "user" : "auto", l is not null, s?.Stopped ?? false,
                l?.Process, l?.Pid, l?.Address);
        })];
    }

    /// <summary>A port's setting: forwarded by hand (or again), named, shared or kept private, stopped.</summary>
    public async Task<StudioPort> SetAsync(StudioWorkspace workspace, int port, string? label, string? visibility, bool? stopped, bool added, CancellationToken ct)
    {
        var row = await db.StudioPorts.SingleOrDefaultAsync(p => p.WorkspaceId == workspace.Id && p.Port == port, ct);
        if (row is null)
        {
            if (await db.StudioPorts.CountAsync(p => p.WorkspaceId == workspace.Id, ct) >= Most)
            {
                throw new StudioException(409, $"This workspace keeps {Most} ports: remove one first.");
            }
            row = new StudioPort { WorkspaceId = workspace.Id, Port = port, Added = added };
            db.StudioPorts.Add(row);
        }
        if (label is not null)
        {
            row.Label = label.Trim() is { Length: > 0 } l ? l[..Math.Min(100, l.Length)] : null;
        }
        if (visibility is not null)
        {
            row.Visibility = visibility is StudioPort.Private or StudioPort.SignedIn ? visibility : throw new StudioException(400, "visibility: private or signed-in.");
        }
        if (stopped is { } s)
        {
            row.Stopped = s;
        }
        else if (added)
        {
            // Forwarded again by hand.
            row.Stopped = false;
        }
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>A port's setting gone: one found listening is forwarded to its owner again, one added by hand is no more.</summary>
    public async Task<bool> RemoveAsync(StudioWorkspace workspace, int port, CancellationToken ct) =>
        await db.StudioPorts.Where(p => p.WorkspaceId == workspace.Id && p.Port == port).ExecuteDeleteAsync(ct) > 0;
}

/// <summary>Why a forwarded port could not be reached: the workspaces service's answer.</summary>
public sealed class PortRelayException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>A connection to what a workspace listens on at a port.</summary>
public interface IPortRelay
{
    Task<Stream> ConnectAsync(Guid workspace, int port, CancellationToken ct);
}

/// <summary>
/// Through the workspaces service: a TCP connection to it, CONNECT &lt;workspace&gt;:&lt;port&gt; with its key, and, once it
/// answers 200, the same connection joined to the port (it checks the port is the workspace's own, not its engine).
/// </summary>
public sealed class PortRelay(IOptionsMonitor<WorkspacesOptions> options) : IPortRelay
{
    public async Task<Stream> ConnectAsync(Guid workspace, int port, CancellationToken ct)
    {
        var o = options.CurrentValue;
        if (o is not { Url.Length: > 0, Key.Length: >= 32 })
        {
            throw new PortRelayException(503, "Workspaces on the server are not set up here.");
        }
        var service = new Uri(o.Url);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(service.Host, service.Port, ct);
            var stream = new NetworkStream(socket, ownsSocket: true);
            var target = $"{workspace:N}:{port}";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\nAuthorization: Bearer {o.Key}\r\n\r\n"), ct);
            var (status, body) = await AnswerAsync(stream, ct);
            if (status != 200)
            {
                await stream.DisposeAsync();
                var said = body.Length > 0 && body.TrimStart().StartsWith('{') ? JsonNode.Parse(body)?["error"]?.GetValue<string>() : null;
                throw new PortRelayException(status, said ?? $"The workspaces service answered HTTP {status}.");
            }
            return stream;
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new PortRelayException(502, $"The workspaces service did not answer: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            socket.Dispose();
            throw new PortRelayException(502, $"The workspaces service did not answer: {ex.Message}");
        }
    }

    /// <summary>The answer's status and, when it is a refusal, its body; read a byte at a time so nothing past it is taken.</summary>
    private static async Task<(int Status, string Body)> AnswerAsync(Stream stream, CancellationToken ct)
    {
        var head = new StringBuilder();
        var one = new byte[1];
        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (head.Length > 8192 || await stream.ReadAsync(one, ct) == 0)
            {
                throw new IOException("its answer ended early");
            }
            head.Append((char)one[0]);
        }
        var lines = head.ToString().Split("\r\n");
        var status = lines[0].Split(' ') is [_, var code, ..] && int.TryParse(code, out var s) ? s : 502;
        if (status == 200)
        {
            return (200, "");
        }
        var length = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.TryParse(p[1].Trim(), out var n) ? n : 0).FirstOrDefault();
        var body = new byte[Math.Clamp(length, 0, 64 * 1024)];
        var read = 0;
        while (read < body.Length && await stream.ReadAsync(body.AsMemory(read), ct) is var n and > 0)
        {
            read += n;
        }
        return (status, Encoding.UTF8.GetString(body, 0, read));
    }
}
