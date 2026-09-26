using System.Net;
using System.Text;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>Argus's /admin surface, answering with the shapes the real one returns (src/argus/mcpsrv/server.py).</summary>
public sealed class FakeArgus : HttpMessageHandler
{
    public const string Token = "argus-admin-token-for-tests";
    public const string ChatToken = "argus-chat-token-for-tests";
    public const string Instructions = "Look things up in the organisation's code before answering.";
    public bool McpDown { get; set; }

    public List<(string Method, string PathAndQuery, string? Token, JsonElement? Body)> Calls { get; } = [];
    public bool IndexRunning { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? (JsonElement?)null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement;
        var token = request.Headers.TryGetValues("X-Argus-Admin-Token", out var v) ? v.Single() : null;
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/mcp")
        {
            return Mcp(request, body);
        }
        Calls.Add((request.Method.Method, request.RequestUri.PathAndQuery, token, body));
        if (token != Token)
        {
            return Json(HttpStatusCode.Forbidden, """{"error":"forbidden"}""");
        }
        return (request.Method.Method, path) switch
        {
            ("GET", "/admin/index/status") => Json(HttpStatusCode.OK, """
                {"job":{"state":"idle","branches":["main"],"started":1790000000,"finished":1790000100,"returncode":0,"tail":["done"],"trigger":"schedule"},
                 "repos":[{"repo":"group/app","branch":"main","default_branch":"main","last_run_at":1790000090,"timed_out":false,"symbols_failed":0}],
                 "index":{"repos":1,"stale":0,"errored":0,"stale_after":86400,"version":"2.9.0","never_run":0,"files":12,"symbols":340,"stale_names":[]},
                 "interval":900,"webhook":false,"pending":[]}
                """),
            ("POST", "/admin/index") when IndexRunning => Json(HttpStatusCode.Conflict, """{"error":"an index run is already in progress","started":1790000000}"""),
            ("POST", "/admin/index") => Json(HttpStatusCode.OK, """{"status":"started","branches":[],"allow_partial":false}"""),
            ("GET", "/admin/packs") => Json(HttpStatusCode.OK, """
                {"packs":[{"name":"dotnet-docs","version":"1.2.0","model":"nomic-embed-text","dim":768,"size_bytes":1048576,"license":"MIT","commit":"abc","compatible":true,"incompatible_reason":null}],
                 "job":{"state":"idle","action":null,"target":null,"started":null,"finished":null,"returncode":null,"tail":[]},
                 "index_url":null,"packs_dir":"/var/lib/argus/packs"}
                """),
            ("POST", "/admin/packs/remove") => body?.GetProperty("name").GetString() == "dotnet-docs"
                ? Json(HttpStatusCode.OK, """{"status":"removed","name":"dotnet-docs"}""")
                : Json(HttpStatusCode.NotFound, """{"error":"no installed pack named 'x'"}"""),
            ("GET", "/admin/explore") => Json(HttpStatusCode.OK, """
                {"repos":[{"repo_id":1,"path_with_namespace":"group/app","branch":"main","files":12,"symbols":340,"public_symbols":80}],
                 "symbols":{"rows":[{"name":"ParseHeader","kind":"function","path":"src/parse.c","line":10,"path_with_namespace":"group/app","branch":"main"}],"capped":false,"limit":50},
                 "files":{"rows":[],"capped":false,"limit":50},"query":"Parse","repo":""}
                """),
            _ => Json(HttpStatusCode.NotFound, """{"error":"not found"}"""),
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public List<(string Method, string? Email, string? Session, JsonElement? Params)> McpCalls { get; } = [];

    /// <summary>Argus's MCP endpoint, as the chat sees it: the chat token, the person's email, a session.</summary>
    private HttpResponseMessage Mcp(HttpRequestMessage request, JsonElement? body)
    {
        if (McpDown)
        {
            throw new HttpRequestException("connection refused (test)");
        }
        var auth = request.Headers.Authorization?.Parameter;
        var email = request.Headers.TryGetValues("x-openwebui-user-email", out var e) ? e.Single() : null;
        var session = request.Headers.TryGetValues("mcp-session-id", out var s) ? s.Single() : null;
        var method = body!.Value.GetProperty("method").GetString()!;
        McpCalls.Add((method, email, session, body.Value.TryGetProperty("params", out var p) ? p : null));
        if (auth != ChatToken)
        {
            return Json(HttpStatusCode.Unauthorized, """{"error":"unknown token"}""");
        }
        if (method == "notifications/initialized")
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
        var id = body.Value.GetProperty("id").GetInt32();
        string result = method switch
        {
            "initialize" => $$$"""{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"argus","version":"2.9.0"},"instructions":"{{{Instructions}}}"}""",
            "tools/list" => """{"tools":[{"name":"find_symbol","description":"Find where a symbol is defined","inputSchema":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}}]}""",
            "tools/call" => p.GetProperty("arguments").GetProperty("name").GetString() == "SecretThing"
                ? """{"content":[{"type":"text","text":"Nothing you have access to matches this, but it does exist in 1 repository you cannot read:\n- secret/vault (2 matches) -- maintainers: alice\nTell the person asking that they do not have access, and that they can ask a maintainer listed above to add them in GitLab with at least Reporter access. Argus picks the change up within 10 minutes."}],"isError":false}"""
                // As FastMCP answers a list: one text block per row, and the list itself as structuredContent.
                : """
                  {"content":[{"type":"text","text":"{\n  \"path\": \"src/parse.c\"\n}"},{"type":"text","text":"{\n  \"path\": \"include/parse.h\"\n}"}],
                   "structuredContent":{"result":[
                     {"repo_id":1,"path_with_namespace":"group/app","path":"src/parse.c","name":"ParseHeader","kind":"function","line":10,"end_line":24,"signature":"int ParseHeader(const char *buf)","scope":null,"is_public":1,"doc":null},
                     {"repo_id":1,"path_with_namespace":"group/app","path":"include/parse.h","name":"ParseHeader","kind":"prototype","line":3,"end_line":3,"signature":"int ParseHeader(const char *buf);","scope":null,"is_public":1,"doc":null}]},
                   "isError":false}
                  """,
            _ => throw new InvalidOperationException(method),
        };
        // Answer as SSE, as streamable HTTP servers may.
        var res = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"event: message\ndata: {{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result.ReplaceLineEndings("")}}}\n\n", Encoding.UTF8, "text/event-stream"),
        };
        if (method == "initialize")
        {
            res.Headers.Add("mcp-session-id", "session-" + email);
        }
        return res;
    }
}
