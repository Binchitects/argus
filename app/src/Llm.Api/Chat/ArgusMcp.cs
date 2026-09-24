using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

public sealed class ArgusToolException(string message) : Exception(message);

/// <summary>A session with Argus's MCP server as one person: their GitLab access, their answers.</summary>
public sealed class ArgusSession(HttpClient http, Uri endpoint, string token, string email, string? sessionId, string protocol)
{
    private int _id = 10;

    public string? Instructions { get; init; }

    public async Task<JsonArray> ToolsAsync(CancellationToken ct)
    {
        var result = await ArgusMcp.RequestAsync(http, endpoint, token, email, sessionId, protocol, Interlocked.Increment(ref _id), "tools/list", new JsonObject(), ct);
        return result?["tools"] as JsonArray ?? [];
    }

    /// <returns>The tool's text, and whether the tool reported an error.</returns>
    public async Task<(string Text, bool IsError)> CallAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        var result = await ArgusMcp.RequestAsync(http, endpoint, token, email, sessionId, protocol, Interlocked.Increment(ref _id), "tools/call",
            new JsonObject { ["name"] = name, ["arguments"] = arguments }, ct);
        var text = string.Join("\n", (result?["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => c["type"]?.GetValue<string>() == "text")
            .Select(c => c["text"]?.GetValue<string>() ?? ""));
        return (text, result?["isError"]?.GetValue<bool>() == true);
    }
}

/// <summary>
/// The smallest MCP client that does the job: streamable HTTP, JSON-RPC,
/// answers as JSON or as SSE. Argus accepts the chat token only from inside the
/// network, with the person's email beside it, exactly as Open WebUI calls it.
/// </summary>
public sealed class ArgusMcp(HttpClient http, IOptions<ArgusOptions> argus, IOptionsMonitor<ChatOptions> chat)
{
    public const string Protocol = "2025-06-18";
    public const string EmailHeader = "x-openwebui-user-email";

    public bool Enabled => argus.Value.Deployed && !string.IsNullOrWhiteSpace(argus.Value.Url) && !string.IsNullOrWhiteSpace(chat.CurrentValue.ArgusChatToken);

    public async Task<ArgusSession> ConnectAsync(string email, CancellationToken ct)
    {
        var endpoint = new Uri(argus.Value.Url.TrimEnd('/') + "/mcp");
        var token = chat.CurrentValue.ArgusChatToken!;
        using var req = Build(endpoint, token, email, null, null, new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = Protocol,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "llm-app", ["version"] = AppInfo.Current.Version },
            },
        });
        using var res = await SendAsync(http, req, ct);
        var session = res.Headers.TryGetValues("mcp-session-id", out var ids) ? ids.First() : null;
        var result = await ReadResultAsync(res, 1, ct);
        var protocol = result?["protocolVersion"]?.GetValue<string>() ?? Protocol;
        using (var note = Build(endpoint, token, email, session, protocol, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }))
        using (await SendAsync(http, note, ct))
        {
        }
        return new ArgusSession(http, endpoint, token, email, session, protocol) { Instructions = result?["instructions"]?.GetValue<string>() };
    }

    internal static async Task<JsonNode?> RequestAsync(HttpClient http, Uri endpoint, string token, string email, string? session, string protocol,
        int id, string method, JsonObject @params, CancellationToken ct)
    {
        using var req = Build(endpoint, token, email, session, protocol, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params });
        using var res = await SendAsync(http, req, ct);
        return await ReadResultAsync(res, id, ct);
    }

    private static HttpRequestMessage Build(Uri endpoint, string token, string email, string? session, string? protocol, JsonObject body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Headers.Add(EmailHeader, email);
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new ArgusToolException("Argus did not answer.");
        }
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            // A 401 from Argus names the reason (unknown person, no GitLab account): pass it on.
            var reason = TryError(body) ?? $"HTTP {(int)res.StatusCode}";
            throw new ArgusToolException("Argus refused: " + reason);
        }
        return res;
    }

    private static async Task<JsonNode?> ReadResultAsync(HttpResponseMessage res, int id, CancellationToken ct)
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
                throw new ArgusToolException(err["message"]?.GetValue<string>() ?? "Argus reported an error.");
            }
            return node["result"];
        }
        throw new ArgusToolException("Argus sent no answer to the request.");
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

    /// <summary>MCP tools, as the OpenAI tool definitions the model expects.</summary>
    public static JsonArray ToOpenAiTools(JsonArray mcpTools) =>
        [.. mcpTools.OfType<JsonObject>().Select(t => (JsonNode)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t["name"]?.DeepClone(),
                ["description"] = t["description"]?.DeepClone() ?? "",
                ["parameters"] = t["inputSchema"]?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
            },
        })];

    /// <summary>Argus's words when something exists but the person cannot read it (src/argus/access.py).</summary>
    public const string NoAccessMarker = "Nothing you have access to matches this";

    public static bool IsNoAccess(string toolText) => toolText.Contains(NoAccessMarker, StringComparison.Ordinal);
}
