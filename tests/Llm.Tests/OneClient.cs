namespace Llm.Tests;

/// <summary>An HTTP client factory whose every client goes to one handler (the handler outlives them).</summary>
public sealed class OneClient(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
