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
            var pool = gateway.Managed.Values.Where(m => m.Name == "flash").OrderBy(m => m.Params["api_base"]!.GetValue<string>()).ToList();
            Assert.Equal(["http://vllm:8001/v1", "http://vllm:8002/v1"], pool.Select(m => m.Params["api_base"]!.GetValue<string>()));
            Assert.All(pool, m => Assert.Equal(16, m.Params["max_parallel_requests"]!.GetValue<int>()));
            Assert.All(pool, m => Assert.Equal("os.environ/ENGINE_API_KEY", m.Params["api_key"]!.GetValue<string>()));
            Assert.Equal(["openai/flash@0", "openai/flash@1"], pool.Select(m => m.Params["model"]!.GetValue<string>()));
            Assert.All(pool, m => Assert.Equal(2, m.Info["llm_app_pool"]!.GetValue<int>()));
            Assert.All(pool, m => Assert.Equal("Qwen3.8-Flash-Next", m.Info["llm_app_fallbacks"]![0]!.GetValue<string>()));
            Assert.All(pool, m => Assert.True(m.Info["supports_vision"]!.GetValue<bool>()));
            Assert.Equal(65536, pool[0].Info["max_input_tokens"]!.GetValue<int>());
            var copies = gateway.Managed.Values.Where(m => m.Name.StartsWith("flash@", StringComparison.Ordinal)).OrderBy(m => m.Name).ToList();
            Assert.Equal(["flash@0", "flash@1"], copies.Select(m => m.Name));
            Assert.All(copies, m => Assert.Equal("flash", m.Info["llm_app_replica_of"]!.GetValue<string>()));
            Assert.DoesNotContain(gateway.Models, m => m.Name.Contains('@', StringComparison.Ordinal));
            // In step: a second sync changes nothing.
            var before = gateway.Managed.Keys.ToHashSet();
            await using (var scope = f.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<ModelCatalog>().SyncGatewayAsync();
            }
            Assert.Equal(before, gateway.Managed.Keys.ToHashSet());

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
            // An API key's request goes on at once: the gateway's own fallbacks (set from the app) answer while flash cannot.
            Assert.Null(await route.RoomForKeyAsync("flash", CancellationToken.None));
            Assert.Equal(["Qwen3.8-Flash-Next"], gateway.Fallbacks!["flash"]);
            Assert.False(gateway.Fallbacks.ContainsKey("solo"));

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
            var conversation = Guid.NewGuid();
            using (var sending = await route.TakeAsync("flash", conversation, CancellationToken.None))
            {
                Assert.Equal($"flash@{EngineRoute.Copy(conversation, 2)}", sending.Model);
            }
            using (var again = await route.TakeAsync("flash", conversation, CancellationToken.None))
            {
                Assert.Equal($"flash@{EngineRoute.Copy(conversation, 2)}", again.Model);
            }
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
}
