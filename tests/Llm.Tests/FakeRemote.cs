using System.Net;
using System.Text;

namespace Llm.Tests;

/// <summary>
/// Another machine's OpenAI-compatible engine, as the app sees it: GET /v1/models at
/// http://gpu-box:8000, answering only the right key, listing what <see cref="Models"/> holds.
/// </summary>
public sealed class FakeRemote : HttpMessageHandler
{
    public const string Base = "http://gpu-box:8000/v1";
    public const string Key = "remote-secret-key";

    /// <summary>The /models answer's data, as JSON objects.</summary>
    public string Models { get; set; } = """[{"id":"big-remote","object":"model","max_model_len":65536},{"id":"small-remote","object":"model","meta":{"n_ctx_train":32768}}]""";

    public bool Down { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down || request.RequestUri!.Host != "gpu-box")
        {
            throw new HttpRequestException("Connection refused (gpu-box:8000)");
        }
        if (request.Headers.Authorization?.Parameter != Key)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":"bad key"}""") });
        }
        if (request.RequestUri.AbsolutePath != "/v1/models")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"object":"list","data":{{Models}}}""", Encoding.UTF8, "application/json"),
        });
    }
}
