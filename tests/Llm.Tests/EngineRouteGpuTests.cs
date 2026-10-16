using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Room-making per GPU set (EngineRoute): a vLLM model asked for starts on its GPUs; room for it is made only by idle models
/// on the GPUs it is short on (one on other GPUs stays), never while they answer (busy from their metrics), and never at
/// llama.cpp's expense (its places are its own); and a model it cannot have room for says why.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class EngineRouteGpuTests(AppFixture app) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-route-gpu-").FullName;

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

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private static async Task UntilAsync(Func<bool> check, string what)
    {
        var end = DateTime.UtcNow + Deadline;
        while (!check())
        {
            if (DateTime.UtcNow > end)
            {
                Assert.Fail("Never: " + what);
            }
            await Task.Delay(100);
        }
    }

    /// <summary>A snapshot of a small model (4 heads, 40 GiB of weights by its index) in the library.</summary>
    private void Snapshot(string library, string name)
    {
        var dir = Path.Combine(library, "hf", "org", name + "@r");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), """{"architectures":["X"],"num_hidden_layers":4,"num_attention_heads":4,"num_key_value_heads":4,"head_dim":64,"max_position_embeddings":131072}""");
        File.WriteAllText(Path.Combine(dir, "model.safetensors.index.json"), $$$"""{"metadata":{"total_size":{{{40L << 30}}}},"weight_map":{}}""");
        File.WriteAllText(Path.Combine(dir, "model.safetensors"), "w");
    }

    [Fact]
    public async Task Room_for_a_vllm_model_is_made_only_on_its_gpus_never_from_busy_models_nor_llama_cpps_places()
    {
        var config = Path.Combine(_dir, "config");
        var status = Path.Combine(_dir, "vllm-status");
        var library = Path.Combine(_dir, "library");
        Directory.CreateDirectory(config);
        var hardware = Path.Combine(_dir, "hardware.json");
        File.WriteAllText(hardware, $$"""{"ram_bytes": {{1L << 40}}, "gpus": [{{string.Join(',', Enumerable.Range(0, 4).Select(i =>
            $$"""{"index": {{i}}, "name": "GPU", "vendor": "nvidia", "backend": "cuda", "total_bytes": {{97887L << 20}}}"""))}}]}""");
        foreach (var n in new[] { "flash", "small", "oss", "coder" })
        {
            Snapshot(library, n);
        }
        app.Engine.Reset(Path.Combine(config, "models.ini"));
        var vllm = new FakeVllm(Path.Combine(config, "vllm"), Path.Combine(status, "vllm"));
        await using var f = app.Create(app.ConnectionStringFor("routegpu_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = config, ["Engine:LibraryDir"] = library,
            ["Engine:VllmStatusDir"] = status, ["Engine:HardwareFile"] = hardware, ["Engine:ModelsMax"] = "1", ["Replicas:Enabled"] = "false",
            ["Engine:ImageReserveBytes"] = "0",
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
                LocalModel M(string name, string devices) => new()
                {
                    Name = name, File = $"hf/org/{name}@r", EngineKind = EngineKinds.Vllm, KvType = "fp8", Context = 8192, Parallel = 8, Devices = devices, GpuMemoryUtilization = 0.9,
                };
                db.LocalModels.AddRange(M("flash", "0,1"), M("small", "0,1"), M("oss", "2"), M("coder", "0,1,2,3"));
                await db.SaveChangesAsync();
                await scope.ServiceProvider.GetRequiredService<ModelCatalog>().WriteVllmPresetsAsync();
            }
            var state = f.Services.GetRequiredService<EngineState>();
            var watcher = f.Services.GetRequiredService<EngineWatcher>();
            var route = f.Services.GetRequiredService<EngineRoute>();
            var backend = f.Services.GetRequiredService<VllmBackends>().All[0];
            async Task SeenAsync(Func<bool> check, string what)
            {
                var end = DateTime.UtcNow + Deadline;
                while (!check())
                {
                    watcher.Wake();
                    if (DateTime.UtcNow > end)
                    {
                        Assert.Fail("Never: " + what);
                    }
                    await Task.Delay(200);
                }
            }
            IEnumerable<string> Desired() => backend.Desired().Select(e => e.Model).Distinct();
            int PortOf(string model) => backend.Presets().First(e => e.Model == model).Port;

            await SeenAsync(() => state.StatusOf("coder") == "unloaded" && state.Places.TryGetValue("coder", out var p) && p.PerGpu.Count == 4, "the vLLM models and their places");
            Assert.Equal("loaded", app.Engine.StatusOf("Qwen3.8-Flash-Next"));

            // flash asked for: started on GPUs 0-1, while llama.cpp's one place keeps its model.
            using (await route.TakeAsync("flash", Guid.NewGuid(), CancellationToken.None))
            {
            }
            Assert.Equal(["flash"], Desired());
            Assert.Equal("loaded", app.Engine.StatusOf("Qwen3.8-Flash-Next"));
            Assert.Equal(0, app.Engine.CallsTo("/models/unload"));
            await SeenAsync(() => state.LoadedSince("flash") is not null && state.Spare.Contains("flash"), "flash loaded and spare");

            // oss on GPU 2: room beside flash, which stays.
            using (await route.TakeAsync("oss", Guid.NewGuid(), CancellationToken.None))
            {
            }
            Assert.Equal(["flash", "oss"], Desired().Order());
            await SeenAsync(() => state.LoadedSince("oss") is not null && state.Spare.Contains("oss"), "oss loaded and spare");

            // small on GPUs 0-1 while flash answers (its metrics): no room is made, the request waits.
            vllm.Busy[PortOf("flash")] = (1, 0);
            using (var soon = new CancellationTokenSource(TimeSpan.FromSeconds(4)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => route.TakeAsync("small", Guid.NewGuid(), soon.Token));
            }
            Assert.Equal(["flash", "oss"], Desired().Order());
            // Idle again: flash makes room on GPUs 0-1; oss, on GPU 2, is not touched.
            vllm.Busy.Clear();
            using (await route.TakeAsync("small", Guid.NewGuid(), CancellationToken.None))
            {
            }
            Assert.Equal(["oss", "small"], Desired().Order());
            await SeenAsync(() => state.LoadedSince("small") is not null && state.Spare.Contains("small"), "small loaded and spare");

            // coder on all four: both on its GPUs make room.
            Assert.False(route.PlaceFor("coder"));
            Assert.True(route.Fits("coder", []).Fits);
            Assert.Equal([0, 1, 2], route.Fits("coder", ["oss", "small"]).Short);
            using (await route.TakeAsync("coder", Guid.NewGuid(), CancellationToken.None))
            {
            }
            Assert.Equal(["coder"], Desired());
            Assert.Equal("loaded", app.Engine.StatusOf("Qwen3.8-Flash-Next"));
            // Its cache as Admin → Models shows it: prefix caching, no slots pinned, its hit rate from its metrics.
            var cache = TokenCache.OfVllm(new LocalModel { Name = "coder", File = "x", Parallel = 8, KvCpuGb = 16 }, await f.Services.GetRequiredService<EngineClient>().CapacityAsync("coder", TimeSpan.FromSeconds(5)));
            Assert.True(cache is { PrefixCache: true, Pinned: false, Slots: 8, HitRate: 0.75, GpuUsage: 0.25 });
            Assert.Equal(16L << 30, cache.SessionBytes);
        }
        finally
        {
            await running.CancelAsync();
            await ticking;
        }
    }
}
