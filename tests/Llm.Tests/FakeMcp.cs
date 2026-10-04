using System.Net;
using System.Text;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>
/// An MCP server an admin could add: one tool, "echo", behind an API key header. Asked
/// to echo "slow" with a progress token, it answers as events, saying how far it is first.
/// </summary>
public sealed class FakeMcp : HttpMessageHandler
{
    public const string ApiKey = "mcp-key-for-tests";

    public List<(string Method, Dictionary<string, string> Headers, JsonElement? Params)> Calls { get; } = [];

    /// <summary>While set, "initialize" waits for it: a server slow to answer.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>A pet store at https://pets.test/v1 (an API by its OpenAPI document, <see cref="PetsSpec"/>): what it was asked, and its pets.</summary>
    public List<(string Method, string PathAndQuery, string? Key, string? Body)> PetCalls { get; } = [];
    public List<string> Pets { get; } = ["Rex", "Tom"];

    public const string PetsSpec = """
        openapi: 3.0.3
        info: { title: Pets, version: "1" }
        servers: [{ url: "https://pets.test/v1" }]
        paths:
          /pets:
            get:
              operationId: listPets
              summary: Lists the pets
              parameters:
                - { name: limit, in: query, required: false, schema: { type: integer }, description: How many at most }
            post:
              operationId: addPet
              summary: Adds a pet
              requestBody:
                required: true
                content:
                  application/json:
                    schema: { $ref: "#/components/schemas/Pet" }
          /pets/{petId}:
            parameters:
              - { name: petId, in: path, required: true, schema: { type: string } }
            delete:
              summary: Removes a pet
        components:
          schemas:
            Pet:
              type: object
              required: [name]
              properties:
                name: { type: string, example: Rex }
                tag: { type: string }
        """;

    private HttpResponseMessage PetStore(HttpRequestMessage request, string? body)
    {
        lock (PetCalls)
        {
            PetCalls.Add((request.Method.Method, request.RequestUri!.PathAndQuery, request.Headers.TryGetValues("X-Api-Key", out var k) ? k.Single() : null, body));
        }
        var path = request.RequestUri!.AbsolutePath;
        string json;
        switch (request.Method.Method, path)
        {
            case ("GET", "/v1/pets"):
                var limit = int.TryParse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["limit"], out var l) ? l : 100;
                json = JsonSerializer.Serialize(Pets.Take(limit).Select(n => new { name = n }));
                break;
            case ("POST", "/v1/pets"):
                Pets.Add(JsonDocument.Parse(body!).RootElement.GetProperty("name").GetString()!);
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json") };
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"no such pet"}""", Encoding.UTF8, "application/json") };
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>A GitLab at https://gitlab.test for the gitlab-issues plugin: OAuth (code "good-code") and a few issue calls, as the token's owner.</summary>
    public List<(string Method, string PathAndQuery, string? Authorization, string? Body)> GitLabCalls { get; } = [];

    private HttpResponseMessage GitLab(HttpRequestMessage request, string? body)
    {
        var auth = request.Headers.Authorization?.ToString();
        lock (GitLabCalls)
        {
            GitLabCalls.Add((request.Method.Method, request.RequestUri!.PathAndQuery, auth, body));
        }
        static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path == "/oauth/token")
        {
            var form = System.Web.HttpUtility.ParseQueryString(body ?? "");
            if (form["client_id"] != "app-id" || form["client_secret"] != "app-secret")
            {
                return Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
            }
            return form["grant_type"] switch
            {
                "authorization_code" when form["code"] == "good-code" && form["redirect_uri"] == "https://llm.test/api/account/connections/callback" =>
                    Json(HttpStatusCode.OK, """{"access_token":"token-1","refresh_token":"refresh-1","expires_in":7200,"token_type":"Bearer"}"""),
                "refresh_token" when form["refresh_token"] == "refresh-1" => Json(HttpStatusCode.OK, """{"access_token":"token-2","refresh_token":"refresh-2","expires_in":7200}"""),
                _ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""),
            };
        }
        // The app's GitLab bot (tasks run by GitLab's events): reads a merge request's changes and a job's log, comments.
        if (request.Headers.TryGetValues("PRIVATE-TOKEN", out var bot) && bot.Single() == "bot-token")
        {
            return (request.Method.Method, path) switch
            {
                ("GET", "/api/v4/projects/7/merge_requests/3/changes") => Json(HttpStatusCode.OK,
                    """{"changes":[{"old_path":"src/decode.c","new_path":"src/decode.c","diff":"@@ -1 +1 @@\n-int DecodeFrame(int x);\n+int DecodeFrame(long x);"}]}"""),
                ("GET", "/api/v4/projects/7/jobs/41/trace") => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("compiling...\nerror: undefined reference to DecodeFrame\n") },
                ("POST", "/api/v4/projects/7/merge_requests/3/notes") or ("POST", "/api/v4/projects/7/repository/commits/abc123def/comments") =>
                    Json(HttpStatusCode.Created, """{"id":99}"""),
                _ => Json(HttpStatusCode.NotFound, """{"message":"404 Not Found"}"""),
            };
        }
        if (auth is not ("Bearer token-1" or "Bearer token-2"))
        {
            return Json(HttpStatusCode.Unauthorized, """{"message":"401 Unauthorized"}""");
        }
        return (request.Method.Method, path) switch
        {
            ("GET", "/api/v4/issues") => Json(HttpStatusCode.OK, """[{"iid":3,"title":"Decoder drops frames","web_url":"https://gitlab.test/group/app/-/issues/3"}]"""),
            ("POST", "/api/v4/projects/group%2Fapp/issues") => Json(HttpStatusCode.Created, """{"iid":4,"web_url":"https://gitlab.test/group/app/-/issues/4"}"""),
            _ => Json(HttpStatusCode.NotFound, """{"message":"404 Not Found"}"""),
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host == "gitlab.test")
        {
            return GitLab(request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        }
        if (request.RequestUri!.Host == "pets.test")
        {
            return PetStore(request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        }
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
        if (method == "initialize" && Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }
        if (method == "notifications/initialized")
        {
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
        var id = body.GetProperty("id").GetInt32();
        if (method == "tools/call" && body.GetProperty("params").GetProperty("arguments").GetProperty("text").GetString() == "slow"
            && body.GetProperty("params").TryGetProperty("_meta", out var meta))
        {
            var token = meta.GetProperty("progressToken").GetString();
            var events = string.Concat(
                "data: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/progress", @params = new { progressToken = token, progress = 1, total = 2, message = "Warming up" } }) + "\n\n",
                "data: " + JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = new { content = new[] { new { type = "text", text = "echo: slow" } }, isError = false } }) + "\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
        }
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
