using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Web;

namespace Llm.Tests;

/// <summary>
/// Prometheus, Loki and Alertmanager as the app reads them: an answer per API
/// path (empty results unless a test sets one), and every request kept with
/// its parameters, so a test can check what was asked.
/// </summary>
public sealed class FakeObserve : HttpMessageHandler
{
    public sealed record Asked(string Path, NameValueCollection Args);

    public ConcurrentQueue<Asked> Requests { get; } = new();

    /// <summary>Answers by path, e.g. "/loki/api/v1/query_range": the body for the request's parameters.</summary>
    public ConcurrentDictionary<string, Func<NameValueCollection, string>> Answers { get; } = new();

    /// <summary>Paths that do not answer at all (the service is down).</summary>
    public ConcurrentDictionary<string, bool> Down { get; } = new();

    public void Reset()
    {
        Requests.Clear();
        Answers.Clear();
        Down.Clear();
    }

    public IEnumerable<Asked> To(string path) => Requests.Where(r => r.Path == path);

    private static readonly Dictionary<string, string> Empty = new()
    {
        ["/api/v1/query_range"] = """{"status":"success","data":{"resultType":"matrix","result":[]}}""",
        ["/api/v1/query"] = """{"status":"success","data":{"resultType":"vector","result":[]}}""",
        ["/api/v1/rules"] = """{"status":"success","data":{"groups":[]}}""",
        ["/loki/api/v1/query_range"] = """{"status":"success","data":{"resultType":"streams","result":[]}}""",
        ["/loki/api/v1/query"] = """{"status":"success","data":{"resultType":"vector","result":[]}}""",
        ["/api/v2/alerts"] = "[]",
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var args = HttpUtility.ParseQueryString(request.RequestUri.Query);
        if (request.Content is not null)
        {
            args.Add(HttpUtility.ParseQueryString(await request.Content.ReadAsStringAsync(cancellationToken)));
        }
        Requests.Enqueue(new Asked(path, args));
        if (Down.ContainsKey(path))
        {
            throw new HttpRequestException("Connection refused");
        }
        var body = Answers.TryGetValue(path, out var answer) ? answer(args)
            : Empty.TryGetValue(path, out var empty) ? empty
            : path.Contains("/label/", StringComparison.Ordinal) ? """{"status":"success","data":["app","web"]}"""
            : null;
        return body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("404 page not found") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
