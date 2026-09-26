using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Llm.Api.Chat.Tools;

namespace Llm.Tests;

/// <summary>
/// The web, as the chat's Web tool sees it: pages by address, and a search engine.
/// Names resolve through <see cref="Resolver"/>: *.test is public, internal.test is not.
/// </summary>
public sealed class FakeWeb : HttpMessageHandler
{
    public ConcurrentDictionary<string, Func<HttpResponseMessage>> Pages { get; } = new();
    public ConcurrentQueue<Uri> Requests { get; } = new();
    public FakeResolver Resolver { get; } = new();

    public void Html(string url, string html) => Pages[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    public void Redirect(string from, string to) => Pages[from] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        return Task.FromResult(Pages.TryGetValue(request.RequestUri!.GetLeftPart(UriPartial.Path), out var page)
            ? page()
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not here") });
    }

    public sealed class FakeResolver : WebResolver
    {
        public override Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => host switch
        {
            "internal.test" => Task.FromResult<IPAddress[]>([IPAddress.Parse("10.0.0.5")]),
            "rebind.test" => Task.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34"), IPAddress.Parse("127.0.0.1")]),
            _ when host.EndsWith(".test", StringComparison.Ordinal) => Task.FromResult<IPAddress[]>([IPAddress.Parse("93.184.216.34")]),
            _ => base.ResolveAsync(host, ct),
        };
    }
}
