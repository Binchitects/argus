using System.Net;
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
/// The app's own gateway for its own calls: the chat's answer straight from the engine, the body shaped
/// for each engine, a pool balanced, the fallback asked when a server is down, and every call booked in the ledger with its
/// person, chat, tokens and cost.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class OwnGatewayTests(AppFixture app)
{
    private static Deployment Route(string name, string kind, string baseUrl = "http://engine.test/v1", int? most = null, int weight = 1) => new()
    {
        Name = name, Kind = kind, BaseUrl = baseUrl, Model = name + "-upstream", Mode = "chat", Info = [], Params = [], Fingerprint = name + baseUrl, MaxParallel = most,
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
    public void A_longest_answer_asked_above_the_models_is_lowered_to_it_by_whichever_name_it_is_asked()
    {
        var route = Route("m", "vllm") with { Info = new JsonObject { ["max_output_tokens"] = 4096 } };
        var asked = new JsonObject { ["model"] = "m", ["max_tokens"] = 100_000, ["max_completion_tokens"] = 8192, ["max_output_tokens"] = 1000 };
        var shaped = ModelGateway.Shape(asked, route);
        Assert.Equal("""{"model":"m-upstream","max_tokens":4096,"max_completion_tokens":4096,"max_output_tokens":1000}""", shaped.ToJsonString());
        // None asked stays none (the engine's own default); a model that says no longest answer takes what is asked.
        Assert.Null(ModelGateway.Shape(new JsonObject { ["model"] = "m" }, route)["max_tokens"]);
        Assert.Equal("100000", ModelGateway.Shape(asked, Route("m", "vllm"))["max_tokens"]!.ToJsonString());
        Assert.Equal(100_000, asked["max_tokens"]!.GetValue<int>());
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
    public async Task A_copy_that_is_down_passes_the_request_to_the_pools_other_copy_and_is_passed_over_for_a_while()
    {
        var cs = app.ConnectionStringFor("owngw_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway(), null,
            s => s.AddSingleton<IRoutesSource>(new Given(Route("pool2", Deployment.Remote, $"http://{FakeModel.DownHost}/v1", weight: 4), Route("pool2", Deployment.Remote, weight: 4))));
        var gateway = f.Services.GetRequiredService<ModelGateway>();
        async Task<Llm.Core.Gateway.GatewayRequest> AskAsync(string tag)
        {
            var res = await gateway.ChatAsync(new JsonObject
            {
                ["model"] = "pool2", ["stream"] = true, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = tag }),
            }, new GatewayCaller("pool@example.test", Tag: tag), [], default);
            Assert.Contains($"\"content\":\"{tag}\"", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            res.Dispose();
            await gateway.FlushAsync(default);
            await using var scope = f.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().GatewayRequests.AsNoTracking().SingleAsync(r => r.Tag == tag);
        }
        // The down copy is the first chosen (both idle): the other answers, the ledger says it took two tries, with no fallback.
        var first = await AskAsync("first");
        Assert.Equal((Llm.Core.Gateway.GatewayRequest.Done, (short)2, null, "engine.test"), (first.Status, first.Attempts, first.FallbackFrom, first.Deployment));
        // It is passed over while it is down: the next goes straight to the one that answers.
        var next = await AskAsync("next");
        Assert.Equal(((short)1, "engine.test"), (next.Attempts, next.Deployment));
    }

    /// <summary>
    /// A model server that is slow to start its answer (slow.test), one that stops halfway (stall.test), and one that
    /// answers (engine.test).
    /// </summary>
    private sealed class SlowServers : HttpMessageHandler
    {
        public const string Answer = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":1}}\n\ndata: [DONE]\n\n";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "slow.test")
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            }
            Stream body = request.RequestUri.Host == "stall.test" ? new Stalling() : new MemoryStream(Encoding.UTF8.GetBytes(Answer));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) { Headers = { ContentType = new("text/event-stream") } } };
        }

        /// <summary>Its first words, then nothing until it is given up on.</summary>
        private sealed class Stalling : Stream
        {
            private bool _said;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_said)
                {
                    _said = true;
                    var first = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"Once\"}}]}\n\n");
                    first.CopyTo(buffer);
                    return first.Length;
                }
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }
        }
    }

    [Fact]
    public async Task An_answer_that_does_not_start_in_time_goes_to_another_copy_else_fails_and_one_that_stops_halfway_is_ended_and_booked_failed()
    {
        var cs = app.ConnectionStringFor("owngw_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway(), new Dictionary<string, string?> { ["Gateway:FirstByteTimeout"] = "00:00:01", ["Gateway:IdleTimeout"] = "00:00:01" },
            s =>
            {
                s.AddSingleton<IRoutesSource>(new Given(Route("pool", Deployment.Remote, "http://slow.test/v1"), Route("pool", Deployment.Remote),
                    Route("slow", Deployment.Remote, "http://slow.test/v1"), Route("stall", Deployment.Remote, "http://stall.test/v1")));
                s.AddHttpClient(ModelGateway.Upstream).ConfigurePrimaryHttpMessageHandler(() => new SlowServers());
            });
        var gateway = f.Services.GetRequiredService<ModelGateway>();
        JsonObject Ask(string model, string tag) => new()
        {
            ["model"] = model, ["stream"] = true, ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = tag }),
        };
        async Task<Llm.Core.Gateway.GatewayRequest> RowAsync(string tag)
        {
            await gateway.FlushAsync(default);
            await using var scope = f.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().GatewayRequests.AsNoTracking().SingleAsync(r => r.Tag == tag);
        }

        // The slow copy gives up its turn: the other copy answers, in two tries.
        using (var res = await gateway.ChatAsync(Ask("pool", "pooled"), new GatewayCaller("slow@example.test", Tag: "pooled"), [], default))
        {
            Assert.Contains("\"content\":\"hi\"", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var pooled = await RowAsync("pooled");
        Assert.Equal((Llm.Core.Gateway.GatewayRequest.Done, "engine.test"), (pooled.Status, pooled.Deployment));
        Assert.True(pooled.Attempts >= 1);

        // Its only copy slow: it fails, and says why.
        var slow = await Assert.ThrowsAsync<ChatGatewayException>(() => gateway.ChatAsync(Ask("slow", "slow"), new GatewayCaller("slow@example.test", Tag: "slow"), [], default));
        Assert.Equal(504, slow.Status);
        Assert.Contains("did not start its answer within 1 second", slow.Message, StringComparison.Ordinal);
        Assert.Equal(Llm.Core.Gateway.GatewayRequest.Failed, (await RowAsync("slow")).Status);

        // Started, then silent: the read ends with why, and the ledger books it failed with its first words counted.
        var res2 = await gateway.ChatAsync(Ask("stall", "stall"), new GatewayCaller("slow@example.test", Tag: "stall"), [], default);
        var cut = await Assert.ThrowsAnyAsync<Exception>(() => res2.Content.ReadAsStringAsync());
        Assert.Contains("paused its answer for more than 1 second", cut.ToString(), StringComparison.Ordinal);
        res2.Dispose();
        var stalled = await RowAsync("stall");
        Assert.Equal((Llm.Core.Gateway.GatewayRequest.Failed, true), (stalled.Status, stalled.Error!.Contains("paused its answer", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_chat_is_answered_by_the_engine_itself_and_booked_in_the_ledger_with_its_person_chat_tokens_and_cost()
    {
        var cs = app.ConnectionStringFor("owngw_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(cs, new FakeGateway(), null,
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
