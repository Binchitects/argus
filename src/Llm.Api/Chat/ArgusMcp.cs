using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>An MCP server refused, failed, or could not be reached; the message says which.</summary>
public sealed class McpException(string message) : Exception(message)
{
    /// <summary>Its certificate was refused: the admin's test says why (Tools.ServerTls).</summary>
    public bool Certificate { get; init; }
}

/// <summary>How far a long tool call is, as its server reports it (MCP notifications/progress).</summary>
public sealed record McpProgress(double Progress, double? Total, string? Message);

/// <summary>A session with one MCP server, with the headers it was opened with (a token, whose it is).</summary>
public sealed class McpSession(McpEndpoint endpoint, string? sessionId, string protocol)
{
    private int _id = 10;

    public string? Instructions { get; init; }

    /// <summary>Longest one tool call may take; the server is told when it is given up on.</summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(60);

    public async Task<JsonArray> ToolsAsync(CancellationToken ct)
    {
        using var limit = Mcp.SetupLimit(ct);
        try
        {
            var result = await Mcp.RequestAsync(endpoint, sessionId, protocol, Interlocked.Increment(ref _id), "tools/list", new JsonObject(), null, limit.Token);
            return result?["tools"] as JsonArray ?? [];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"{endpoint.Server} did not list its tools within {Mcp.Describe(Mcp.SetupTimeout)}.");
        }
    }

    /// <param name="progress">Told how far the call is, when the server says (it may never).</param>
    /// <returns>The tool's answer, and whether the tool reported an error.</returns>
    public async Task<(string Text, bool IsError)> CallAsync(string name, JsonObject arguments, Func<McpProgress, Task>? progress, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _id);
        var token = $"call-{id}";
        var @params = new JsonObject { ["name"] = name, ["arguments"] = arguments };
        if (progress is not null)
        {
            @params["_meta"] = new JsonObject { ["progressToken"] = token };
        }
        Func<JsonObject, Task>? onProgress = progress is null ? null : async note =>
        {
            if (note["method"]?.GetValue<string>() == "notifications/progress" && note["params"] is JsonObject p && p["progressToken"]?.ToString() == token
                && p["progress"] is JsonValue v && v.TryGetValue<double>(out var done))
            {
                var total = p["total"] is JsonValue t && t.TryGetValue<double>(out var all) ? all : (double?)null;
                var message = p["message"] is JsonValue m && m.TryGetValue<string>(out var text) ? text[..Math.Min(300, text.Length)] : null;
                await progress(new McpProgress(done, total, message));
            }
        };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CallTimeout);
        JsonNode? result;
        try
        {
            result = await Mcp.RequestAsync(endpoint, sessionId, protocol, id, "tools/call", @params, onProgress, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await Mcp.CancelAsync(endpoint, sessionId, protocol, id, "It ran longer than the chat allows.");
            throw new McpException($"{endpoint.Server} did not finish within {Mcp.Describe(CallTimeout)}. An admin can allow longer calls (Admin → Tools).");
        }
        catch (OperationCanceledException)
        {
            await Mcp.CancelAsync(endpoint, sessionId, protocol, id, "The answer was stopped.");
            throw;
        }
        var isError = result?["isError"]?.GetValue<bool>() == true;
        return (isError ? TextOf(result) : StructuredOf(result) ?? TextOf(result), isError);
    }

    private static string TextOf(JsonNode? result) =>
        string.Join("\n", (result?["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => c["type"]?.GetValue<string>() == "text")
            .Select(c => c["text"]?.GetValue<string>() ?? ""));

    /// <summary>
    /// The answer as one JSON value. For a list, FastMCP's text is one block per
    /// row, which joined is not JSON; its structuredContent is the list, wrapped
    /// as {"result": …}. Compact, it also costs the model fewer tokens.
    /// </summary>
    private static string? StructuredOf(JsonNode? result)
    {
        if (result?["structuredContent"] is not JsonObject structured)
        {
            return null;
        }
        var value = structured.Count == 1 && structured.ContainsKey("result") ? structured["result"] : structured;
        return value?.ToJsonString(Mcp.Plain) ?? "null";
    }
}

/// <summary>Where an MCP server is, the headers every request carries, and its name for messages ("Argus refused: …").</summary>
public sealed record McpEndpoint(HttpClient Http, Uri Url, IReadOnlyDictionary<string, string> Headers, string Server);

/// <summary>
/// The smallest MCP client that does the job: streamable HTTP, JSON-RPC, answers
/// as JSON or as server-sent events read as they come. A call may run for an hour:
/// the HTTP client has no timeout of its own (each request has its limit), progress
/// is passed on, a dropped stream is resumed where it broke (Last-Event-ID), and a
/// call given up on is cancelled at the server.
/// </summary>
public static class Mcp
{
    public const string Protocol = "2025-06-18";

    /// <summary>Opening a session and listing its tools: a server that takes longer is not up.</summary>
    public static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Times a broken stream is picked up again before the call fails.</summary>
    private const int Resumes = 5;

    /// <summary>
    /// JSON as written for the model and the page: code keeps its &lt; &amp; + and
    /// other languages their letters, instead of \u escapes. It never goes into HTML.
    /// </summary>
    public static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The MCP clients' connections: no overall timeout (calls set their own), TCP keep-alive, so a
    /// firewall does not drop a connection that waits an hour for its answer, and the system's CAs
    /// deciding a server's certificate, a refusal told apart (Tools.ServerTls.Strict).
    /// </summary>
    public static SocketsHttpHandler Handler() => new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        ConnectTimeout = TimeSpan.FromSeconds(15),
        SslOptions = { RemoteCertificateValidationCallback = Tools.ServerTls.Strict },
        ConnectCallback = async (context, ct) =>
        {
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveTime, 60);
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Tcp, System.Net.Sockets.SocketOptionName.TcpKeepAliveInterval, 15);
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, ct);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    internal static CancellationTokenSource SetupLimit(CancellationToken ct)
    {
        var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(SetupTimeout);
        return limit;
    }

    /// <summary>"90 minutes", "1 minute".</summary>
    internal static string Describe(TimeSpan t) => t.TotalMinutes >= 1
        ? $"{t.TotalMinutes:0.#} minute{(Math.Abs(t.TotalMinutes - 1) < 0.01 ? "" : "s")}"
        : $"{t.TotalSeconds:0} seconds";

    /// <param name="server">The server's name, for messages ("Argus refused: …").</param>
    /// <param name="callTimeout">Longest one tool call may take (default: an hour).</param>
    public static async Task<McpSession> ConnectAsync(HttpClient http, Uri url, IReadOnlyDictionary<string, string> headers, string server, CancellationToken ct, TimeSpan? callTimeout = null)
    {
        var endpoint = new McpEndpoint(http, url, headers, server);
        using var limit = SetupLimit(ct);
        try
        {
            using var req = Build(endpoint, null, null, new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = Protocol,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "llm-app", ["version"] = AppInfo.Current.Version },
                },
            });
            using var res = await SendAsync(endpoint, req, limit.Token);
            var session = res.Headers.TryGetValues("mcp-session-id", out var ids) ? ids.First() : null;
            var (found, result) = await ReadAsync(endpoint, res, 1, session, null, null, new StreamPosition(), limit.Token);
            if (!found)
            {
                throw new McpException($"{server} sent no answer to the request.");
            }
            var protocol = result?["protocolVersion"]?.GetValue<string>() ?? Protocol;
            using (var note = Build(endpoint, session, protocol, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }))
            using (await SendAsync(endpoint, note, limit.Token))
            {
            }
            return new McpSession(endpoint, session, protocol)
            {
                Instructions = result?["instructions"]?.GetValue<string>(),
                CallTimeout = callTimeout ?? TimeSpan.FromMinutes(60),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"{server} did not answer within {Describe(SetupTimeout)}.");
        }
    }

    /// <summary>
    /// One request and its answer. Notifications on the way (progress) go to
    /// <paramref name="onNotification"/>; a stream that breaks before the answer is
    /// resumed from its last event, when the server numbers its events.
    /// </summary>
    internal static async Task<JsonNode?> RequestAsync(McpEndpoint endpoint, string? session, string protocol,
        int id, string method, JsonObject @params, Func<JsonObject, Task>? onNotification, CancellationToken ct)
    {
        using var req = Build(endpoint, session, protocol, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        var res = await SendAsync(endpoint, req, ct);
        var position = new StreamPosition();
        for (var resumed = 0; ; resumed++)
        {
            try
            {
                var (found, result) = await ReadAsync(endpoint, res, id, session, protocol, onNotification, position, ct);
                if (found)
                {
                    return result;
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !ct.IsCancellationRequested)
            {
                // The connection broke mid-stream: resumed below, if the server allows it.
            }
            finally
            {
                res.Dispose();
            }
            if (session is null || position.LastEvent is not { } lastEvent || resumed >= Resumes)
            {
                throw new McpException($"{endpoint.Server} closed the connection before it answered.");
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << resumed)), ct);
            using var again = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
            Headers(again, endpoint, session, protocol);
            again.Headers.Accept.ParseAdd("text/event-stream");
            again.Headers.Add("Last-Event-ID", lastEvent);
            res = await SendAsync(endpoint, again, ct);
        }
    }

    /// <summary>Tells the server a call is given up on, so it can stop the work (best effort: a few seconds).</summary>
    internal static async Task CancelAsync(McpEndpoint endpoint, string? session, string protocol, int id, string reason)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var req = Build(endpoint, session, protocol, new JsonObject
            {
                ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled",
                ["params"] = new JsonObject { ["requestId"] = id, ["reason"] = reason },
            });
            using var _ = await endpoint.Http.SendAsync(req, limit.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // The server is gone or slow: nothing more to tell it.
        }
    }

    private static HttpRequestMessage Build(McpEndpoint endpoint, string? session, string? protocol, JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        Headers(req, endpoint, session, protocol);
        return req;
    }

    private static void Headers(HttpRequestMessage req, McpEndpoint endpoint, string? session, string? protocol)
    {
        foreach (var (name, value) in endpoint.Headers)
        {
            if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value[7..]);
            }
            else
            {
                req.Headers.TryAddWithoutValidation(name, value);
            }
        }
        if (session is not null)
        {
            req.Headers.Add("mcp-session-id", session);
        }
        if (protocol is not null)
        {
            req.Headers.Add("MCP-Protocol-Version", protocol);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(McpEndpoint endpoint, HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await endpoint.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (Tools.ServerTls.IsCertificateError(ex))
        {
            throw new McpException($"{endpoint.Server}'s certificate is not trusted.") { Certificate = true };
        }
        catch (HttpRequestException ex) when (Tools.ServerTls.HandshakeFailed(endpoint.Server, ex) is { } handshake)
        {
            throw new McpException(handshake);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new McpException($"{endpoint.Server} did not answer.");
        }
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            // A 401 from Argus names the reason (unknown person, no GitLab account): pass it on.
            var reason = TryError(body) ?? $"HTTP {(int)res.StatusCode}";
            throw new McpException($"{endpoint.Server} refused: {reason}");
        }
        return res;
    }

    /// <summary>The last event read from a stream: where to pick it up again if it breaks.</summary>
    private sealed class StreamPosition
    {
        public string? LastEvent { get; set; }
    }

    /// <summary>
    /// Reads messages until the answer to <paramref name="id"/>: notifications go to
    /// <paramref name="onNotification"/>, a ping from the server is answered. Not found:
    /// the stream ended first; <paramref name="position"/> says where to pick it up.
    /// </summary>
    private static async Task<(bool Found, JsonNode? Result)> ReadAsync(McpEndpoint endpoint, HttpResponseMessage res, int id, string? session, string? protocol,
        Func<JsonObject, Task>? onNotification, StreamPosition position, CancellationToken ct)
    {
        await foreach (var (data, eventId) in MessagesAsync(res, ct))
        {
            position.LastEvent = eventId ?? position.LastEvent;
            JsonObject? message;
            try
            {
                message = JsonNode.Parse(data) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (message is null)
            {
                continue;
            }
            if (message["method"] is JsonValue)
            {
                if (message["id"] is { } requestId)
                {
                    await AnswerServerAsync(endpoint, session, protocol, requestId, message["method"]!.GetValue<string>(), ct);
                }
                else if (onNotification is not null)
                {
                    await onNotification(message);
                }
                continue;
            }
            if (message["id"] is not JsonValue v || !v.TryGetValue<int>(out var answered) || answered != id)
            {
                continue;
            }
            if (message["error"] is JsonObject err)
            {
                throw new McpException(err["message"]?.GetValue<string>() ?? $"{endpoint.Server} reported an error.");
            }
            return (true, message["result"]);
        }
        return (false, null);
    }

    /// <summary>A request from the server mid-call: a ping is answered (or it may give up on us); nothing else is offered.</summary>
    private static async Task AnswerServerAsync(McpEndpoint endpoint, string? session, string? protocol, JsonNode requestId, string method, CancellationToken ct)
    {
        var reply = method == "ping"
            ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(), ["result"] = new JsonObject() }
            : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"{method} is not supported." } };
        try
        {
            using var req = Build(endpoint, session, protocol, reply);
            using var _ = await endpoint.Http.SendAsync(req, ct);
        }
        catch (HttpRequestException)
        {
            // The answer to the call is what matters.
        }
    }

    /// <summary>The JSON-RPC messages of a response: its body, or its server-sent events as they arrive (with their ids).</summary>
    private static async IAsyncEnumerable<(string Data, string? EventId)> MessagesAsync(HttpResponseMessage res, [EnumeratorCancellation] CancellationToken ct)
    {
        if (res.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            yield return (await res.Content.ReadAsStringAsync(ct), null);
            yield break;
        }
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        string? id = null;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return (data.ToString(), id);
                    data.Clear();
                }
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
            }
            else if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                id = line[(line.Length > 3 && line[3] == ' ' ? 4 : 3)..];
            }
        }
        if (data.Length > 0)
        {
            yield return (data.ToString(), id);
        }
    }

    private static string? TryError(string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            return n?["error"]?.ToString() ?? n?["detail"]?.ToString();
        }
        catch (JsonException)
        {
            return body.Length > 0 ? body[..Math.Min(200, body.Length)] : null;
        }
    }

    /// <summary>MCP tools, as the OpenAI tool definitions the model expects, optionally renamed.</summary>
    public static JsonArray ToOpenAiTools(JsonArray mcpTools, Func<string, string>? rename = null) =>
        [.. mcpTools.OfType<JsonObject>().Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = rename is null ? t["name"]?.DeepClone() : rename(t["name"]?.GetValue<string>() ?? ""),
                ["description"] = t["description"]?.DeepClone() ?? "",
                ["parameters"] = t["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            },
        })];
}

/// <summary>
/// Argus's MCP server, as the chat uses it: the chat token and the person's
/// email beside it, accepted only from inside the network. Argus answers with
/// that person's GitLab access.
/// </summary>
public sealed class ArgusMcp(HttpClient http, IOptions<ArgusOptions> argus, IOptionsMonitor<ChatOptions> chat)
{
    public const string EmailHeader = "x-llm-user-email";

    /// <summary>The chat's credential at Argus: its own when set, else ARGUS_KEY (Argus takes it as both).</summary>
    private string? Token => chat.CurrentValue.ArgusChatToken is { Length: > 0 } own ? own : argus.Value.AdminToken;

    public bool Enabled => !string.IsNullOrWhiteSpace(argus.Value.Url) && !string.IsNullOrWhiteSpace(Token);

    public Task<McpSession> ConnectAsync(string email, CancellationToken ct) =>
        Mcp.ConnectAsync(http, new Uri(argus.Value.Url.TrimEnd('/') + "/mcp"),
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + Token, [EmailHeader] = email }, "Argus", ct, chat.CurrentValue.ToolCallTimeout);

    /// <summary>Argus's words when something exists but the person cannot read it (src/Argus/Access/Acl.cs).</summary>
    public const string NoAccessMarker = "Nothing you have access to matches this";

    public static bool IsNoAccess(string toolText) => toolText.Contains(NoAccessMarker, StringComparison.Ordinal);
}
