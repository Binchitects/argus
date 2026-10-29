using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>
/// The API with keys goes by the app alone: the model API's own paths are passed on with the caller's key, and the
/// gateway's management (keys, people, teams, models, spend) and pages are not found from outside.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ApiPassageTests(AppFixture app)
{
    private HttpClient Gateway() =>
        app.Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });

    [Fact]
    [Trait("Control", "NET-01")]
    public async Task The_model_list_and_embeddings_are_passed_on_with_the_callers_own_key()
    {
        using var client = Gateway();
        var key = "sk-passage-" + Guid.NewGuid().ToString("N")[..8];
        using var list = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/models", UriKind.Relative));
        list.Headers.Authorization = new("Bearer", key);
        var listed = await client.SendAsync(list);
        await StatusAssert.Is(HttpStatusCode.OK, listed);
        Assert.Equal("Qwen3.8-Flash-Next", JsonDocument.Parse(await listed.Content.ReadAsStringAsync()).RootElement.GetProperty("data")[0].GetProperty("id").GetString());

        using var embed = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/embeddings", UriKind.Relative))
        {
            Content = new StringContent("""{"model":"embed","input":"hello"}""", Encoding.UTF8, "application/json"),
        };
        embed.Headers.Authorization = new("Bearer", key);
        await StatusAssert.Is(HttpStatusCode.OK, await client.SendAsync(embed));
        Assert.Contains(app.Model.Passed, p => p == ("/v1/models", key));
        Assert.Contains(app.Model.Passed, p => p == ("/v1/embeddings", key));
    }

    [Theory]
    [Trait("Control", "NET-01")]
    [InlineData("POST", "/key/generate")]
    [InlineData("GET", "/key/info")]
    [InlineData("POST", "/user/new")]
    [InlineData("GET", "/spend/logs")]
    [InlineData("POST", "/model/new")]
    [InlineData("GET", "/ui")]
    [InlineData("GET", "/health")]
    [InlineData("POST", "/team/new")]
    public async Task The_gateways_own_management_is_not_found_from_outside(string method, string path)
    {
        using var client = Gateway();
        using var req = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        req.Headers.Authorization = new("Bearer", "sk-master-would-be");
        if (method == "POST")
        {
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }
        var res = await client.SendAsync(req);
        Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} {path}: {(int)res.StatusCode}");
        Assert.DoesNotContain(app.Model.Passed, p => p.Path.StartsWith(path, StringComparison.Ordinal));
    }
}
