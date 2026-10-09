using System.Net;
using System.Text;

namespace CodeArena.Tests;

/// <summary>A key's rate limits at the gateway: a refusal says which limit in plain words, not LiteLLM's.</summary>
public sealed class GatewayRateLimitTests
{
    /// <summary>Answers every request with this status and body, as the gateway does, and counts them.</summary>
    private sealed class Refusing(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var res = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            res.Headers.TryAddWithoutValidation("Retry-After", "60");
            return Task.FromResult(res);
        }
    }

    /// <summary>LiteLLM's answer past a key's limit, as the gateway sends it.</summary>
    private static string Over(string type, int limit) =>
        $$$"""{"error":{"message":"Rate limit exceeded for api_key: 61dde803. Limit type: {{{type}}}. Current limit: {{{limit}}}, Remaining: 0. Limit resets at: 2026-10-09 10:47:17 UTC","type":"throttling_error","param":null,"code":"429"}}""";

    private static async Task<GatewayException> RefusedAsync(HttpStatusCode status, string body)
    {
        using var http = new HttpClient(new Refusing(status, body));
        var client = new GatewayClient(http, "https://gateway.example.test", "sk-test");
        return await Assert.ThrowsAsync<GatewayException>(() => client.ModelIdsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Requests_or_tokens_a_minute_say_the_limit_and_when_to_try_again()
    {
        var requests = await RefusedAsync(HttpStatusCode.TooManyRequests, Over("requests", 60));
        Assert.Equal(429, requests.Status);
        Assert.Equal("Your API key reached its limit of 60 requests a minute. Try again in a minute; Your account → API key shows your limits and what you used.", requests.Message);
        var tokens = await RefusedAsync(HttpStatusCode.TooManyRequests, Over("tokens", 100000));
        Assert.StartsWith("Your API key reached its limit of 100000 tokens a minute.", tokens.Message, StringComparison.Ordinal);
        // Waiting never helps a request bigger than the limit: it says so, and what helps.
        Assert.Contains("a request bigger than the limit (its prompt and the answer it asks for) is refused every time", tokens.Message, StringComparison.Ordinal);
        Assert.Contains("/compact", tokens.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("requests", "Your API key reached its limit of 1 request a minute.")]
    [InlineData("tokens", "Your API key reached its limit of 1 token a minute.")]
    [InlineData("max_parallel_requests", "Your API key reached its limit of 1 request at once.")]
    public async Task A_limit_of_one_says_request_or_token_not_requests(string type, string start)
    {
        var e = await RefusedAsync(HttpStatusCode.TooManyRequests, Over(type, 1));
        Assert.StartsWith(start, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("requests")]
    [InlineData("tokens")]
    public async Task A_key_past_its_limit_a_minute_is_not_asked_again_within_seconds(string limit)
    {
        var gateway = new Refusing(HttpStatusCode.TooManyRequests, Over(limit, 60));
        using var http = new HttpClient(gateway);
        var client = new GatewayClient(http, "https://gateway.example.test", "sk-test");
        var started = DateTime.UtcNow;
        var e = await Assert.ThrowsAsync<GatewayException>(() => client.CompleteAsync(new System.Text.Json.Nodes.JsonObject { ["model"] = "m" }, null, CancellationToken.None));
        // Once: the minute is not over in a few seconds, and the gateway counts each try.
        Assert.Equal(1, gateway.Requests);
        Assert.True(e.PerMinute);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_busy_gateway_or_a_key_at_its_requests_at_once_is_asked_again()
    {
        Assert.True(GatewayClient.Retryable(new GatewayException("busy", 503)));
        Assert.True(GatewayClient.Retryable(new GatewayException("Your API key reached its limit of 2 requests at once.", 429)));
        Assert.True(GatewayClient.Retryable(new GatewayException("The gateway answered 429: slow down", 429)));
        Assert.False(GatewayClient.Retryable(new GatewayException("Your API key reached its limit of 60 requests a minute.", 429, perMinute: true)));
        Assert.False(GatewayClient.Retryable(new GatewayException("The gateway answered 400: bad", 400)));
    }

    [Fact]
    public async Task Requests_at_once_say_to_wait_for_one_to_finish()
    {
        var e = await RefusedAsync(HttpStatusCode.TooManyRequests, Over("max_parallel_requests", 2));
        Assert.Equal("Your API key reached its limit of 2 requests at once. Wait for one to finish, then try again.", e.Message);
        // One may finish in a few seconds: asked again.
        Assert.False(e.PerMinute);
    }

    [Fact]
    public async Task Another_429_keeps_the_gateways_own_words()
    {
        var e = await RefusedAsync(HttpStatusCode.TooManyRequests, """{"error":{"message":"Budget has been exceeded! Current cost: 5, Max budget: 5"}}""");
        Assert.Equal("The gateway answered 429: Budget has been exceeded! Current cost: 5, Max budget: 5", e.Message);
    }
}
