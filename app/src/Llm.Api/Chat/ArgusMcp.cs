using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>An MCP server refused, failed, or could not be reached; the message says which.</summary>
public sealed class McpException(string message) : Exception(message);

/// <summary>A session with one MCP server, with the headers it was opened with (a token, whose it is).</summary>
public sealed class McpSession(HttpClient http, Uri endpoint, IReadOnlyDictionary<string, string> headers, string? sessionId, string protocol, string server)
{
    private int _id = 10;

    public string? Instructions { get; init; }

    public async Task<JsonArray> ToolsAsync(CancellationToken ct)
    {
        var result = await Mcp.RequestAsync(http, endpoint, headers, sessionId, protocol, Interlocked.Increment(ref _id), "tools/list", new JsonObject(), server, ct);
        return result?["tools"] as JsonArray ?? [];
    }

    /// <returns>The tool's answer, and whether the tool reported an error.</returns>
    public async Task<(string Text, bool IsError)> CallAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        var result = await Mcp.RequestAsync(http, endpoint, headers, sessionId, protocol, Interlocked.Increment(ref _id), "tools/call",
            new JsonObject { ["name"] = name, ["arguments"] = arguments }, server, ct);
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

/// <summary>
/// The smallest MCP client that does the job: streamable HTTP, JSON-RPC,
/// answers as JSON or as SSE.
/// </summary>
public static class Mcp
{
    public const string Protocol = "2025-06-18";

    /// <summary>
    /// JSON as written for the model and the page: code keeps its &lt; &amp; + and
    /// other languages their letters, instead of \u escapes. It never goes into HTML.
    /// </summary>
    public static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="server">The server's name, for messages ("Argus refused: …").</param>
    public static async Task<McpSession> ConnectAsync(HttpClient http, Uri endpoint, IReadOnlyDictionary<string, string> headers, string server, CancellationToken ct)
    {
        using var req = Build(endpoint, headers, null, null, new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = Protocol,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "llm-app", ["version"] = AppInfo.Current.Version },
            },
        });
        using var res = await SendAsync(http, req, server, ct);
        var session = res.Headers.TryGetValues("mcp-session-id", out var ids) ? ids.First() : null;
        var result = await ReadResultAsync(res, 1, server, ct);
        var protocol = result?["protocolVersion"]?.GetValue<string>() ?? Protocol;
        using (var note = Build(endpoint, headers, session, protocol, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }))
        using (await SendAsync(http, note, server, ct))
        {
        }
        return new McpSession(http, endpoint, headers, session, protocol, server) { Instructions = result?["instructions"]?.GetValue<string>() };
    }

    internal static async Task<JsonNode?> RequestAsync(HttpClient http, Uri endpoint, IReadOnlyDictionary<string, string> headers, string? session, string protocol,
        int id, string method, JsonObject @params, string server, CancellationToken ct)
    {
        using var req = Build(endpoint, headers, session, protocol, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        using var res = await SendAsync(http, req, server, ct);
        return await ReadResultAsync(res, id, server, ct);
    }

    private static HttpRequestMessage Build(Uri endpoint, IReadOnlyDictionary<string, string> headers, string? session, string? protocol, JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        foreach (var (name, value) in headers)
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
        return req;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage req, string server, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new McpException($"{server} did not answer.");
        }
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            // A 401 from Argus names the reason (unknown person, no GitLab account): pass it on.
            var reason = TryError(body) ?? $"HTTP {(int)res.StatusCode}";
            throw new McpException($"{server} refused: {reason}");
        }
        return res;
    }

    private static async Task<JsonNode?> ReadResultAsync(HttpResponseMessage res, int id, string server, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        IEnumerable<string> messages = res.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim())
            : [text];
        foreach (var m in messages)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(m);
            }
            catch (JsonException)
            {
                continue;
            }
            if (node?["id"]?.GetValue<int>() != id)
            {
                continue;
            }
            if (node["error"] is JsonObject err)
            {
                throw new McpException(err["message"]?.GetValue<string>() ?? $"{server} reported an error.");
            }
            return node["result"];
        }
        throw new McpException($"{server} sent no answer to the request.");
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
/// email beside it, accepted only from inside the network, exactly as Open WebUI
/// calls it. Argus answers with that person's GitLab access.
/// </summary>
public sealed class ArgusMcp(HttpClient http, IOptions<ArgusOptions> argus, IOptionsMonitor<ChatOptions> chat)
{
    public const string EmailHeader = "x-openwebui-user-email";

    public bool Enabled => argus.Value.Deployed && !string.IsNullOrWhiteSpace(argus.Value.Url) && !string.IsNullOrWhiteSpace(chat.CurrentValue.ArgusChatToken);

    public Task<McpSession> ConnectAsync(string email, CancellationToken ct) =>
        Mcp.ConnectAsync(http, new Uri(argus.Value.Url.TrimEnd('/') + "/mcp"),
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + chat.CurrentValue.ArgusChatToken, [EmailHeader] = email }, "Argus", ct);

    /// <summary>Argus's words when something exists but the person cannot read it (src/argus/access.py).</summary>
    public const string NoAccessMarker = "Nothing you have access to matches this";

    public static bool IsNoAccess(string toolText) => toolText.Contains(NoAccessMarker, StringComparison.Ordinal);
}
