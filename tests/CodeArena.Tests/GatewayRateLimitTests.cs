using System.Net;
using System.Text;

namespace CodeArena.Tests;

/// <summary>A key's rate limits at the gateway: a refusal says which limit in plain words, not LiteLLM's.</summary>
public sealed class GatewayRateLimitTests
{
    /// <summary>Answers every request with this status and body, as the gateway does.</summary>
    private sealed class Refusing(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
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
    }

    [Fact]
    public async Task Requests_at_once_say_to_wait_for_one_to_finish()
    {
        var e = await RefusedAsync(HttpStatusCode.TooManyRequests, Over("max_parallel_requests", 2));
        Assert.Equal("Your API key reached its limit of 2 requests at once. Wait for one to finish, then try again.", e.Message);
    }

    [Fact]
    public async Task Another_429_keeps_the_gateways_own_words()
    {
        var e = await RefusedAsync(HttpStatusCode.TooManyRequests, """{"error":{"message":"Budget has been exceeded! Current cost: 5, Max budget: 5"}}""");
        Assert.Equal("The gateway answered 429: Budget has been exceeded! Current cost: 5, Max budget: 5", e.Message);
    }
}
