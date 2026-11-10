using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The app's own gateway for its own calls (Gateway:Mode chat): the chat's answer straight from the engine, the body shaped
/// for each engine, a pool balanced, the fallback asked when a server is down, and every call booked in the ledger with its
/// person, chat, tokens and cost.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class OwnGatewayTests(AppFixture app)
{
    private static Deployment Route(string name, string kind, string baseUrl = "http://engine.test/v1", int? most = null, int weight = 1) => new()
    {
        Name = name, Kind = kind, BaseUrl = baseUrl, Model = name + "-upstream", Mode = "chat", Info = [], LiteLlm = [], Fingerprint = name + baseUrl, MaxParallel = most,
        Weight = weight, Price = new TokenPrice(1, 0.5m, 2),
    };

    private sealed class Given(params Deployment[] given) : IRoutesSource
    {
        public IEnumerable<Deployment> Routes() => given;
    }

    [Fact]
    public void The_body_carries_only_what_each_engine_reads_and_a_stream_always_asks_for_its_usage()
    {
        var body = new JsonObject
        {
            ["model"] = "m", ["messages"] = new JsonArray(), ["stream"] = true, ["id_slot"] = 2, ["priority"] = -1,
            ["metadata"] = new JsonObject { ["tags"] = new JsonArray("chat:1") }, ["fallbacks"] = new JsonArray("n"),
        };
        var llama = ModelGateway.Shape(body, Route("m", Deployment.LlamaCpp));
        Assert.Equal(("m-upstream", 2, null), (llama["model"]!.GetValue<string>(), llama["id_slot"]!.GetValue<int>(), llama["priority"]));
        Assert.Null(llama["metadata"]);
        Assert.Null(llama["fallbacks"]);
        Assert.True(llama["stream_options"]!["include_usage"]!.GetValue<bool>());
        var vllm = ModelGateway.Shape(body, Route("m", "vllm"));
        Assert.Equal((-1, null), (vllm["priority"]!.GetValue<int>(), vllm["id_slot"]));
        var remote = ModelGateway.Shape(body, Route("m", Deployment.Remote));
        Assert.Equal((null, null), (remote["priority"], remote["id_slot"]));
        // The caller's body is left as it was.
        Assert.Equal(2, body["id_slot"]!.GetValue<int>());
        Assert.Null(ModelGateway.Shape(new JsonObject { ["model"] = "m" }, Route("m", Deployment.Remote))["stream_options"]);
    }

    [Fact]
    public void The_meter_reads_the_usage_the_cached_tokens_the_first_words_and_an_error()
    {
        var meter = new SseMeter(TimeProvider.System);
        meter.Read(Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hm\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"Hel"));
        Assert.NotNull(meter.FirstToken);
        meter.Read(Encoding.UTF8.GetBytes("lo\"}}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3},\"timings\":{\"cache_n\":8}}\n\ndata: [DONE]\n\n"));
        Assert.Equal((12, 8, 3, true, true, 7L), (meter.Prompt, meter.Cached, meter.Completion, meter.Usage, meter.Done, meter.Characters));
        var failed = new SseMeter(TimeProvider.System);
        failed.Read(Encoding.UTF8.GetBytes("data: {\"error\":{\"message\":\"the cache is full\"}}\n"));
        Assert.Contains("the cache is full", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pools_copies_are_chosen_by_how_busy_each_is_for_its_share_and_a_full_one_is_passed_over()
    {
        await using var f = app.Create(app.ConnectionStringFor("owngw_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway());
        var gateway = f.Services.GetRequiredService<ModelGateway>();
        var small = Route("pool", "vllm", "http://a.test/v1", most: 1);
        var big = Route("pool", "vllm", "http://b.test/v1", weight: 3);
        Assert.Same(small, gateway.Pick([small, big]));
    }

    [Fact]
    public async Task The_chat_is_answered_by_the_engine_itself_and_booked_in_the_ledger_with_its_person_chat_tokens_and_cost()
    {
        var cs = app.ConnectionStringFor("owngw_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway(), new Dictionary<string, string?> { ["Gateway:Mode"] = "chat" },
            s => s.AddSingleton<IRoutesSource>(new Given(Route("down-model", Deployment.Remote, $"http://{FakeModel.DownHost}/v1"))));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "og" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var before = app.Model.Requests.Count;

        var chat = (await person.JsonAsync(await person.PostAsync("/api/chat/conversations", new { }))).GetProperty("id").GetGuid();
        var answer = await (await person.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "Which port does Postgres use?" })).Content.ReadAsStringAsync();
        Assert.Contains("Answer to:", answer, StringComparison.Ordinal);
        // Sent to the engine as it reads it: no metadata, its usage asked for.
        var sent = app.Model.Requests.Skip(before).Select(r => r.Body).Last(b => b["messages"]!.ToJsonString().Contains("Postgres", StringComparison.Ordinal));
        Assert.Null(sent["metadata"]);
        Assert.True(sent["stream_options"]!["include_usage"]!.GetValue<bool>());

        var gateway = f.Services.GetRequiredService<ModelGateway>();
        Llm.Core.Gateway.GatewayRequest? row = null;
        for (var i = 0; i < 100 && row is null; i++)
        {
            await gateway.FlushAsync(default);
            await using var scope = f.Services.CreateAsyncScope();
            row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GatewayRequests.AsNoTracking()
                .Where(r => r.Person == $"{name}@example.test" && r.ConversationId == chat).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync();
            if (row is null)
            {
                await Task.Delay(50);
            }
        }
        Assert.NotNull(row);
        Assert.Equal((Llm.Core.Gateway.GatewayRequest.Done, "chat", 100, 40), (row.Status, row.Surface, row.PromptTokens, row.CachedTokens));
        Assert.True(row.CompletionTokens > 0);
        Assert.Equal(made.GetProperty("id").GetGuid(), row.UserId);
        Assert.Equal("engine.test", row.Deployment);

        // A server that is down: its fallback answers, and the ledger says which answered and for which.
        var res = await gateway.ChatAsync(new JsonObject
        {
            ["model"] = "down-model", ["stream"] = true, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "fall back" }),
        }, new GatewayCaller($"{name}@example.test", Tag: "test"), ["Qwen3.8-Flash-Next"], default);
        var text = await res.Content.ReadAsStringAsync();
        res.Dispose();
        Assert.Contains("\"content\":\"fall back\"", text, StringComparison.Ordinal);
        Llm.Core.Gateway.GatewayRequest? fell = null;
        for (var i = 0; i < 100 && fell is null; i++)
        {
            await gateway.FlushAsync(default);
            await using var scope = f.Services.CreateAsyncScope();
            fell = await scope.ServiceProvider.GetRequiredService<AppDbContext>().GatewayRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Tag == "test" && r.Person == $"{name}@example.test");
            if (fell is null)
            {
                await Task.Delay(50);
            }
        }
        Assert.NotNull(fell);
        Assert.Equal(("Qwen3.8-Flash-Next", "down-model", (short)2), (fell.Model, fell.FallbackFrom, fell.Attempts));

        // No such model: said, and booked as failed.
        var none = await Assert.ThrowsAsync<ChatGatewayException>(() => gateway.ChatAsync(new JsonObject { ["model"] = "no-such-model", ["messages"] = new JsonArray() },
            new GatewayCaller($"{name}@example.test"), [], default));
        Assert.Equal(404, none.Status);
    }
}
