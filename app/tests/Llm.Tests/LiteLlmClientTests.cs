using System.Net;
using System.Text.Json;
using Llm.Api.Gateway;

namespace Llm.Tests;

/// <summary>The exact requests the app sends to LiteLLM.</summary>
public sealed class LiteLlmClientTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public List<(string Path, JsonElement Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement;
            Calls.Add((request.RequestUri!.PathAndQuery, body));
            var answer = request.RequestUri.AbsolutePath == "/key/generate" ? """{"key":"sk-new"}""" : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) };
        }
    }

    private static (LiteLlmClient Client, Recorder Calls) Create()
    {
        var recorder = new Recorder();
        return (new LiteLlmClient(new HttpClient(recorder) { BaseAddress = new Uri("http://litellm:4000") }), recorder);
    }

    [Fact]
    public async Task Creating_a_gateway_user_never_mints_a_hidden_key()
    {
        var (client, recorder) = Create();
        await client.EnsureUserAsync("p@example.test");
        var (path, body) = Assert.Single(recorder.Calls);
        Assert.Equal("/user/new", path);
        Assert.False(body.GetProperty("auto_create_key").GetBoolean());
        Assert.Equal("p@example.test", body.GetProperty("user_id").GetString());
    }

    [Fact]
    public async Task A_budget_binds_on_both_the_key_path_and_the_chat_path()
    {
        var (client, recorder) = Create();
        await client.SetBudgetAsync("p@example.test", 7.5m);
        Assert.Equal(["/user/update", "/end_user/update"], recorder.Calls.Select(c => c.Path));
        Assert.All(recorder.Calls, c => Assert.Equal(7.5m, c.Body.GetProperty("max_budget").GetDecimal()));
    }

    [Fact]
    public async Task A_generated_key_carries_the_persons_alias()
    {
        var (client, recorder) = Create();
        Assert.Equal("sk-new", await client.GenerateKeyAsync("p@example.test", "app-p"));
        Assert.Equal("app-p", recorder.Calls.Single().Body.GetProperty("key_alias").GetString());
    }

    private sealed class Hangs : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    public async Task A_gateway_that_does_not_answer_is_reported_as_down()
    {
        var client = new LiteLlmClient(new HttpClient(new Hangs()) { BaseAddress = new Uri("http://litellm:4000"), Timeout = TimeSpan.FromMilliseconds(200) });
        var ex = await Assert.ThrowsAsync<GatewayException>(() => client.KeysAsync("p@example.test"));
        Assert.Contains("did not answer in time", ex.Message, StringComparison.Ordinal);
    }
}
