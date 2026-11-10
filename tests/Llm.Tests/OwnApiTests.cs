using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The models API answered by the app's own gateway (Gateway:Mode own): the models a key may use, a copy under its own
/// name kept out, /v1/model/info in the shape Code Arena reads, embeddings by the embeddings server, and a plain 404 for
/// what nothing here serves.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class OwnApiTests(AppFixture app)
{
    private sealed class Given(params Deployment[] given) : IRoutesSource
    {
        public IEnumerable<Deployment> Routes() => given;
    }

    [Fact]
    public async Task The_gateway_is_chosen_in_the_settings_and_the_next_request_goes_the_new_way()
    {
        await using var f = app.Create(app.ConnectionStringFor("gwmode_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var gateway = f.Services.GetRequiredService<ModelGateway>();
        Assert.False(gateway.ForChat);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var saved = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = new object[] { new { key = "Gateway:Mode", value = "own" } } });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        for (var i = 0; i < 100 && !gateway.ForApi; i++)
        {
            await Task.Delay(50);
        }
        Assert.True(gateway.ForApi);
        var refused = await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new { changes = new object[] { new { key = "Gateway:Mode", value = "both" } } });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_key_lists_its_models_and_their_info_and_embeds_and_nothing_else_is_found()
    {
        var copy = new Deployment
        {
            Name = "Qwen3.8-Flash-Next@1", CopyOf = "Qwen3.8-Flash-Next", Kind = "vllm", BaseUrl = "http://engine.test/v1", Model = "q", Mode = "chat", Info = [], LiteLlm = [], Fingerprint = "copy",
        };
        var embed = new Deployment
        {
            Name = "test-embed", Kind = Deployment.Embed, BaseUrl = "http://engine.test/v1", Model = "nomic", Mode = "embedding", Info = new JsonObject { ["mode"] = "embedding" },
            LiteLlm = [], Fingerprint = "embed", Price = new TokenPrice(1, 0, 0),
        };
        await using var f = app.Create(app.ConnectionStringFor("ownapi_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Gateway:Mode"] = "own" },
            s => s.AddSingleton<IRoutesSource>(new Given(copy, embed)));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "oa" + Guid.NewGuid().ToString("N")[..8];
        var key = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("apiKey").GetString()!;
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        async Task<(HttpStatusCode, JsonNode?)> SendAsync(HttpMethod method, string path, string? body = null, string? bearer = null)
        {
            using var req = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
            if (body is not null)
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer ?? key);
            var res = await client.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            return (res.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
        }

        var (status, models) = await SendAsync(HttpMethod.Get, "/v1/models");
        Assert.Equal(HttpStatusCode.OK, status);
        var ids = models!["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ToList();
        Assert.Contains("Qwen3.8-Flash-Next", ids);
        Assert.Contains("test-embed", ids);
        Assert.DoesNotContain("Qwen3.8-Flash-Next@1", ids);
        Assert.Equal("Qwen3.8-Flash-Next", (await SendAsync(HttpMethod.Get, "/models/Qwen3.8-Flash-Next")).Item2!["id"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, "/v1/models/nothing")).Item1);

        // As Code Arena reads it: the window, the longest answer, what it does, its prices a token.
        var info = (await SendAsync(HttpMethod.Get, "/v1/model/info")).Item2!["data"]!.AsArray().Single(m => m!["model_name"]!.GetValue<string>() == "Qwen3.8-Flash-Next")!["model_info"]!;
        Assert.Equal((32768, 8192, true), (info["max_input_tokens"]!.GetValue<int>(), info["max_output_tokens"]!.GetValue<int>(), info["supports_function_calling"]!.GetValue<bool>()));
        Assert.Equal(0.0000002m, info["input_cost_per_token"]!.GetValue<decimal>());

        var (embedded, vectors) = await SendAsync(HttpMethod.Post, "/v1/embeddings", """{"model":"test-embed","input":"hello"}""");
        Assert.Equal(HttpStatusCode.OK, embedded);
        Assert.Equal(2, vectors!["data"]![0]!["embedding"]!.AsArray().Count);
        Assert.Contains(app.Model.Passed, p => p.Path.EndsWith("/embeddings", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Post, "/v1/rerank", "{}")).Item1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/v1/models", bearer: "sk-nobody")).Item1);
    }
}
