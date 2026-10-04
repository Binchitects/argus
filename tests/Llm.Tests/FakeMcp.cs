using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            if (request.Method == HttpMethod.Get && Knowledge(path) is { } read)
            {
                return Json(HttpStatusCode.OK, read.ToJsonString());
            }
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

    /// <summary>A GitLab project as the bot reads it for company knowledge: its members, last activity, wiki pages and issues.</summary>
    public sealed class KnowledgeProject
    {
        public required long Id { get; init; }
        public required string Path { get; init; }
        public string Activity { get; set; } = "2026-10-01T10:00:00Z";
        public List<(string Username, int Level, string State)> Members { get; } = [];
        public List<(string Slug, string Title, string Content)> Wiki { get; } = [];
        public List<(long Iid, string Title, string Description, string UpdatedAt, bool Confidential, string[] Notes)> Issues { get; } = [];
    }

    /// <summary>The group "group": group/app (7) and group/secret (8). Tests change them and sync again.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<long, KnowledgeProject> Projects { get; } = new();

    public FakeMcp() => ResetKnowledge();

    /// <summary>
    /// group/app: alice (Developer), Dave (Guest), erin (blocked) and frank (minimal access); its wiki says how to roll back a deploy,
    /// issue 12 is about dropped frames, issue 13 is confidential. group/secret: only bob; its wiki names the acquisition.
    /// </summary>
    public void ResetKnowledge()
    {
        Projects.Clear();
        var app = new KnowledgeProject { Id = 7, Path = "group/app" };
        app.Members.AddRange([("alice", 30, "active"), ("Dave", 10, "active"), ("erin", 30, "blocked"), ("frank", 5, "active")]);
        app.Wiki.Add(("deploy", "Deploy", "## Rollback\n\nTo roll back a deploy, run make rollback and pick the release tag. The previous release stays warm for an hour.\n\n## Releases\n\nReleases go out on Tuesdays after the change review."));
        app.Wiki.Add(("onboarding", "Onboarding", "New people get their laptop and accounts on their first day."));
        app.Issues.Add((12, "Decoder drops frames", "The decoder drops every tenth frame on 4K input.", "2026-10-01T09:00:00Z", false, ["Fixed by widening the ring buffer to 64 frames."]));
        app.Issues.Add((13, "Salary review", "Salaries of the codec team.", "2026-10-01T09:30:00Z", true, []));
        Projects[7] = app;
        var secret = new KnowledgeProject { Id = 8, Path = "group/secret" };
        secret.Members.Add(("bob", 40, "active"));
        secret.Wiki.Add(("plan", "Plan", "The acquisition codename is Bluebird and it closes in March."));
        Projects[8] = secret;
    }

    private static JsonObject ProjectJson(KnowledgeProject p) => new()
    {
        ["id"] = p.Id, ["path_with_namespace"] = p.Path, ["web_url"] = $"https://gitlab.test/{p.Path}", ["last_activity_at"] = p.Activity,
        ["wiki_access_level"] = "enabled", ["issues_access_level"] = "enabled",
    };

    /// <summary>The bot's reads for company knowledge, or null for another path.</summary>
    private JsonNode? Knowledge(string path)
    {
        if (path.StartsWith("/api/v4/projects/group%2F", StringComparison.Ordinal))
        {
            var name = "group/" + path["/api/v4/projects/group%2F".Length..];
            return Projects.Values.FirstOrDefault(p => p.Path == name) is { } one ? ProjectJson(one) : null;
        }
        if (path == "/api/v4/groups/group/projects")
        {
            return new JsonArray([.. Projects.Values.OrderBy(p => p.Id).Select(p => (JsonNode)ProjectJson(p))]);
        }
        var m = System.Text.RegularExpressions.Regex.Match(path, @"^/api/v4/projects/(\d+)/(members/all|wikis|issues|issues/(\d+)/notes)$");
        if (!m.Success || !Projects.TryGetValue(long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), out var project))
        {
            return null;
        }
        return m.Groups[2].Value switch
        {
            "members/all" => new JsonArray([.. project.Members.Select(x => (JsonNode)new JsonObject { ["username"] = x.Username, ["access_level"] = x.Level, ["state"] = x.State })]),
            "wikis" => new JsonArray([.. project.Wiki.Select(x => (JsonNode)new JsonObject { ["slug"] = x.Slug, ["title"] = x.Title, ["content"] = x.Content, ["format"] = "markdown" })]),
            "issues" => new JsonArray([.. project.Issues.Select(x => (JsonNode)new JsonObject
            {
                ["iid"] = x.Iid, ["title"] = x.Title, ["description"] = x.Description, ["updated_at"] = x.UpdatedAt, ["created_at"] = "2026-09-30T08:00:00Z",
                ["confidential"] = x.Confidential, ["state"] = "opened", ["labels"] = new JsonArray("bug"), ["author"] = new JsonObject { ["username"] = "alice" },
                ["web_url"] = $"https://gitlab.test/{project.Path}/-/issues/{x.Iid}",
            })]),
            _ => new JsonArray([.. project.Issues.Single(x => x.Iid == long.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)).Notes
                .Select(n => (JsonNode)new JsonObject { ["body"] = n, ["system"] = false, ["author"] = new JsonObject { ["username"] = "dave" }, ["created_at"] = "2026-10-01T08:00:00Z" })
                .Prepend(new JsonObject { ["body"] = "changed the description", ["system"] = true, ["author"] = new JsonObject { ["username"] = "alice" }, ["created_at"] = "2026-10-01T07:00:00Z" })]),
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
