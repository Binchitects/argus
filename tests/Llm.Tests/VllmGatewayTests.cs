using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// vLLM models at the gateway: each copy at its own port, never sent more at once than its answers at once, a pool of its
/// copies under the model's name and each copy under its own (in no list) for the chat to keep a conversation on one; its
/// fallback answering while its engine is down, for the chat, and named to an API key.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class VllmGatewayTests(AppFixture app) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-vllm-gw-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Its_copies_register_with_ports_limits_and_a_fallback_that_answers_while_its_engine_is_down()
    {
        var config = Path.Combine(_dir, "config");
        var status = Path.Combine(_dir, "vllm-status");
        var library = Path.Combine(_dir, "library");
        Directory.CreateDirectory(config);
        var hardware = Path.Combine(_dir, "hardware.json");
        File.WriteAllText(hardware, $$"""{"ram_bytes": {{1L << 40}}, "gpus": [{{string.Join(',', Enumerable.Range(0, 4).Select(i =>
            $$"""{"index": {{i}}, "name": "GPU", "vendor": "nvidia", "backend": "cuda", "total_bytes": {{97887L << 20}}}"""))}}]}""");
        var dir = Path.Combine(library, "hf", "org", "flash@r");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), """{"num_hidden_layers":4,"num_attention_heads":4,"num_key_value_heads":4,"head_dim":64,"max_position_embeddings":131072,"vision_config":{}}""");
        File.WriteAllText(Path.Combine(dir, "model.safetensors"), "w");
        app.Engine.Reset(Path.Combine(config, "models.ini"));
        var vllm = new FakeVllm(Path.Combine(config, "vllm"), Path.Combine(status, "vllm")) { Running = false };
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("vllmgw_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = config, ["Engine:LibraryDir"] = library,
            ["Engine:VllmStatusDir"] = status, ["Engine:HardwareFile"] = hardware, ["Replicas:Enabled"] = "false",
        }, s => s.AddHttpClient(VllmBackend.Client).ConfigurePrimaryHttpMessageHandler(() => vllm));
        using var running = new CancellationTokenSource();
        var ticking = Task.Run(async () =>
        {
            while (!running.IsCancellationRequested)
            {
                vllm.Tick();
                await Task.Delay(100);
            }
        });
        try
        {
            await using (var scope = f.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.LocalModels.Add(new LocalModel
                {
                    Name = "flash", File = "hf/org/flash@r", EngineKind = EngineKinds.Vllm, KvType = "fp8", Context = 65536, Parallel = 16, Devices = "0,1,2,3", Replicas = 2,
                    Fallback = "Qwen3.8-Flash-Next",
                });
                db.LocalModels.Add(new LocalModel
                {
                    Name = "solo", File = "hf/org/flash@r", EngineKind = EngineKinds.Vllm, KvType = "fp8", Context = 8192, Parallel = 4, Devices = "0", Replicas = 1,
                });
                await db.SaveChangesAsync();
                var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
                await catalog.WriteVllmPresetsAsync();
                await catalog.SyncGatewayAsync();
            }

            // The pool: two copies under its name, each at its port with its limit; and each under its own name, in no list.
            var pool = (await app.RoutesOfAsync(f)).Where(m => m.Name == "flash").OrderBy(m => m.Params["api_base"]!.GetValue<string>()).ToList();
            Assert.Equal(["http://vllm:8001/v1", "http://vllm:8002/v1"], pool.Select(m => m.Params["api_base"]!.GetValue<string>()));
            Assert.All(pool, m => Assert.Equal(16, m.Params["max_parallel_requests"]!.GetValue<int>()));
            Assert.All(pool, m => Assert.Equal("os.environ/ENGINE_API_KEY", m.Params["api_key"]!.GetValue<string>()));
            Assert.Equal(["openai/flash@0", "openai/flash@1"], pool.Select(m => m.Params["model"]!.GetValue<string>()));
            Assert.All(pool, m => Assert.Equal(2, m.Info["llm_app_pool"]!.GetValue<int>()));
            Assert.All(pool, m => Assert.Equal("Qwen3.8-Flash-Next", m.Info["llm_app_fallbacks"]![0]!.GetValue<string>()));
            Assert.All(pool, m => Assert.True(m.Info["supports_vision"]!.GetValue<bool>()));
            // Each request may hold the whole context (16 at once share the cache, not the window), and half of it at most is the answer.
            Assert.Equal((65536, 32768), (pool[0].Info["max_input_tokens"]!.GetValue<int>(), pool[0].Info["max_output_tokens"]!.GetValue<int>()));
            var copies = (await app.RoutesOfAsync(f)).Where(m => m.Name.StartsWith("flash@", StringComparison.Ordinal)).OrderBy(m => m.Name).ToList();
            Assert.Equal(["flash@0", "flash@1"], copies.Select(m => m.Name));
            Assert.All(copies, m => Assert.Equal("flash", m.Info["llm_app_replica_of"]!.GetValue<string>()));
            Assert.DoesNotContain(gateway.Models, m => m.Name.Contains('@', StringComparison.Ordinal));
            // The same as routes: each copy at its port with the engines' key, its limit, its fallback; and under its own name.
            List<Llm.Api.Gateway.Deployment> routes;
            await using (var scope = f.Services.CreateAsyncScope())
            {
                routes = await scope.ServiceProvider.GetRequiredService<Llm.Api.Gateway.GatewayRoutes>().BuildAsync(CancellationToken.None, others: false);
            }
            var flash = routes.Where(r => r.Name == "flash").OrderBy(r => r.BaseUrl, StringComparer.Ordinal).ToList();
            Assert.Equal([("http://vllm:8001/v1", "flash@0"), ("http://vllm:8002/v1", "flash@1")], flash.Select(r => (r.BaseUrl, r.Model)));
            Assert.All(flash, r => Assert.Equal((EngineKinds.Vllm, true, 16, 16, 2, "Qwen3.8-Flash-Next"), (r.Kind, r.EngineKey, r.MaxParallel, r.Weight, r.Copies, r.Fallback)));
            Assert.Equal([("flash@0", "flash"), ("flash@1", "flash")], routes.Where(r => r.CopyOf is not null).OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => (r.Name, r.CopyOf)));
            Assert.Equal(routes.Select(r => r.Fingerprint).Order(StringComparer.Ordinal), (await app.RoutesOfAsync(f)).Select(m => m.Fingerprint).Order(StringComparer.Ordinal));
            // In step: a second sync changes nothing.
            var before = (await app.RoutesOfAsync(f)).Select(r => r.Fingerprint).ToHashSet();
            await using (var scope = f.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ModelCatalog>().SyncGatewayAsync();
            }
            Assert.Equal(before, (await app.RoutesOfAsync(f)).Select(r => r.Fingerprint).ToHashSet());

            var state = f.Services.GetRequiredService<EngineState>();
            var watcher = f.Services.GetRequiredService<EngineWatcher>();
            var route = f.Services.GetRequiredService<EngineRoute>();
            for (var i = 0; i < 300 && !(state.StatusOf("flash") == "unloaded" && state.Places.ContainsKey("flash")); i++)
            {
                watcher.Wake();
                await Task.Delay(100);
            }
            Assert.Equal("Qwen3.8-Flash-Next", route.FallbackOf("flash"));

            // Its engine down: the chat's request goes to the fallback; an API key is told which answers.
            using (var sending = await route.TakeAsync("flash", Guid.NewGuid(), CancellationToken.None))
            {
                Assert.Equal("Qwen3.8-Flash-Next", sending.Model);
            }
            // An API key's request goes on at once: the gateway's fallback answers while flash cannot.
            Assert.Null(await route.RoomForKeyAsync("flash", CancellationToken.None));
            var built = await app.RoutesOfAsync(f);
            Assert.All(built.Where(r => r.Name == "flash"), r => Assert.Equal("Qwen3.8-Flash-Next", r.Fallback));
            Assert.All(built.Where(r => r.Name == "solo"), r => Assert.Null(r.Fallback));

            // Up again: it starts, and a conversation stays on one copy.
            vllm.Running = true;
            var backend = f.Services.GetRequiredService<VllmBackends>().All[0];
            for (var i = 0; i < 100 && backend.Down() is not null; i++)
            {
                await Task.Delay(100);
            }
            // An API key's request for a model without a fallback, not running: started, and told at once to come back.
            var quick = System.Diagnostics.Stopwatch.StartNew();
            Assert.Contains("solo is starting", await route.RoomForKeyAsync("solo", CancellationToken.None), StringComparison.Ordinal);
            Assert.True(quick.Elapsed < TimeSpan.FromSeconds(10), "the key's request waited for the model");
            Assert.Contains(backend.Desired(), e => e.Model == "solo");
            await f.Services.GetRequiredService<EngineClient>().UnloadAsync("solo");
            // Copy 0 busier than its 16 answers at once (its metrics say 20, copy 1's none): a new conversation goes to copy 1,
            // though its own is copy 0.
            var flashPorts = backend.Presets().Where(e => e.Model == "flash").Select(e => e.Port).ToList();
            vllm.Busy[flashPorts[0]] = (16, 4);
            var onZero = Ids(64).First(c => EngineRoute.Copy(c, 2) == 0);
            using (var sending = await route.TakeAsync("flash", onZero, CancellationToken.None))
            {
                Assert.Equal("flash@1", sending.Model);
            }
            vllm.Busy.Clear();
            var conversation = Ids(64).First(c => EngineRoute.Copy(c, 2) == 1);
            using (var sending = await route.TakeAsync("flash", conversation, CancellationToken.None))
            {
                Assert.Equal($"flash@{EngineRoute.Copy(conversation, 2)}", sending.Model);
            }
            using (var again = await route.TakeAsync("flash", conversation, CancellationToken.None))
            {
                Assert.Equal($"flash@{EngineRoute.Copy(conversation, 2)}", again.Model);
            }
            // Busy inside vLLM (requests waiting past a quarter of its places): an API key is told to come back; the chat is not held.
            vllm.Busy[flashPorts[0]] = (16, 12);
            Assert.Contains("flash is busy: 12 requests wait inside it", await route.RoomForKeyAsync("flash", CancellationToken.None), StringComparison.Ordinal);
            vllm.Busy.Clear();
            Assert.Null(await route.RoomForKeyAsync("flash", CancellationToken.None));
            Assert.Equal(EngineKinds.Vllm, route.KindOf("flash"));
            // A side request goes to the pool.
            using (var side = await route.TakeAsync("flash", null, CancellationToken.None))
            {
                Assert.Null(side.Model);
            }
            Assert.Equal([0, 1], Enumerable.Range(0, 64).Select(_ => EngineRoute.Copy(Guid.NewGuid(), 2)).Distinct().Order());
        }
        finally
        {
            await running.CancelAsync();
            await ticking;
        }
    }

    /// <summary>Conversations' ids as they are made (version 7: each begins with the time it was made), <paramref name="n"/> within a moment.</summary>
    private static List<Guid> Ids(int n) => [.. Enumerable.Range(0, n).Select(_ => Guid.CreateVersion7())];

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Conversations_begun_together_spread_over_the_copies(int copies)
    {
        // 400 conversations begun within the same moment: each copy gets its share of them, not one copy all.
        var counts = Ids(400).GroupBy(c => EngineRoute.Copy(c, copies)).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(copies, counts.Count);
        Assert.All(counts.Values, n => Assert.True(n >= 400 / copies / 5, $"a copy of {copies} got {n} of 400"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void A_conversation_goes_to_the_least_busy_copy_and_stays_while_its_copy_has_room(int copies)
    {
        var idle = new int[copies];
        // All idle: its own copy.
        Assert.Equal(copies - 1, EngineRoute.Choose(idle, copies - 1, null, 16));
        // The least busy, whatever its own; ties go to its own and the ones after it.
        var busy = Enumerable.Range(0, copies).Select(i => 10 + i).ToArray();
        busy[copies / 2] = 3;
        Assert.Equal(copies / 2, EngineRoute.Choose(busy, 0, null, 16));
        var two = Enumerable.Repeat(9, copies).ToArray();
        two[0] = two[copies - 1] = 1;
        Assert.Equal(0, EngineRoute.Choose(two, 0, null, 16));
        Assert.Equal(copies - 1, EngineRoute.Choose(two, 1, null, 16));
        // On a copy with room, it stays there (its prefix is cached), though another is less busy: up to its answers at once more.
        var full = Enumerable.Repeat(2, copies).ToArray();
        full[1] = 2 + 16;
        Assert.Equal(1, EngineRoute.Choose(full, 0, 1, 16));
        // Busier than that: it goes to the least busy.
        full[1] = 2 + 17;
        Assert.Equal(0, EngineRoute.Choose(full, 0, 1, 16));
        // Many conversations at once, each sent where the fewest are: every copy takes its turn.
        var sent = new int[copies];
        foreach (var c in Ids(copies * 10))
        {
            sent[EngineRoute.Choose(sent, EngineRoute.Copy(c, copies), null, 16)]++;
        }
        Assert.All(sent, n => Assert.Equal(10, n));
    }
}
