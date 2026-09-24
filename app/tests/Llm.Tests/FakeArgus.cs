using System.Net;
using System.Text;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>Argus's /admin surface, answering with the shapes the real one returns (src/argus/mcpsrv/server.py).</summary>
public sealed class FakeArgus : HttpMessageHandler
{
    public const string Token = "argus-admin-token-for-tests";

    public List<(string Method, string PathAndQuery, string? Token, JsonElement? Body)> Calls { get; } = [];
    public bool IndexRunning { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? (JsonElement?)null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement;
        var token = request.Headers.TryGetValues("X-Argus-Admin-Token", out var v) ? v.Single() : null;
        var path = request.RequestUri!.AbsolutePath;
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
}
