using System.Net;
using System.Text;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>An MCP server an admin could add: one tool, "echo", behind an API key header.</summary>
public sealed class FakeMcp : HttpMessageHandler
{
    public const string ApiKey = "mcp-key-for-tests";

    public List<(string Method, Dictionary<string, string> Headers, JsonElement? Params)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement;
        var method = body.GetProperty("method").GetString()!;
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (Calls)
        {
            Calls.Add((method, headers, body.TryGetProperty("params", out var p) ? p.Clone() : null));
        }
        if (headers.GetValueOrDefault("X-Api-Key") != ApiKey)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"bad api key"}""", Encoding.UTF8, "application/json") };
        }
        if (method == "notifications/initialized")
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
        var id = body.GetProperty("id").GetInt32();
        var result = method switch
        {
            "initialize" => """{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"echo","version":"1"}}""",
            "tools/list" => """{"tools":[{"name":"echo","description":"Says the text back","inputSchema":{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}}]}""",
            "tools/call" => JsonSerializer.Serialize(new
            {
                content = new[] { new { type = "text", text = "echo: " + body.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString() } },
                isError = false,
            }),
            _ => throw new InvalidOperationException(method),
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"jsonrpc":"2.0","id":{{id}},"result":{{result}}}""", Encoding.UTF8, "application/json"),
        };
    }
}
