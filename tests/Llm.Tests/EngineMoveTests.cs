using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// A model moved to another engine at runtime (Admin → Models → Edit), with the stack running: llama.cpp to vLLM and
/// back, vLLM to SGLang and back. It keeps its name, who may use it and its prices; its answers in flight finish on its
/// old engine, new requests wait for it on the new one (beside the old one when its GPUs are others), a failure there
/// leaves it on its old engine and says why, and the old engine listing it a while longer changes nothing.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class EngineMoveTests(AppFixture app) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-move-").FullName;

    private string Config => Path.Combine(_dir, "config");
    private string Status => Path.Combine(_dir, "vllm-status");
    private string Library => Path.Combine(_dir, "library");

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

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    /// <summary>vLLM's and SGLang's supervisors, each with its servers' metrics by its host.</summary>
    private sealed class Hosts(FakeVllm vllm, FakeVllm sglang) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            new HttpMessageInvoker(request.RequestUri!.Host == "sglang" ? sglang : vllm, disposeHandler: false).SendAsync(request, ct);
    }

    private sealed record Stack(WebApplicationFactory<Program> App, FakeVllm Vllm, FakeVllm Sglang, FakeGateway Gateway, TestBrowser Admin, Func<Task> Stop)
    {
        public EngineState State => App.Services.GetRequiredService<EngineState>();
        public EngineRoute Route => App.Services.GetRequiredService<EngineRoute>();
        public VllmBackend Backend(string name) => App.Services.GetRequiredService<VllmBackends>().All.Single(b => b.Name == name);
        public IEnumerable<string> Desired(string engine) => Backend(engine).Desired().Select(e => e.Model).Distinct();
    }

    /// <summary>Two GPUs, a GGUF file and a snapshot in the library, llama.cpp, and vLLM and SGLang running.</summary>
    private async Task<Stack> StartAsync()
    {
        Directory.CreateDirectory(Config);
        var hardware = Path.Combine(_dir, "hardware.json");
        File.WriteAllText(hardware, $$"""{"ram_bytes": {{1L << 40}}, "gpus": [{{string.Join(',', Enumerable.Range(0, 2).Select(i =>
            $$"""{"index": {{i}}, "name": "GPU", "vendor": "nvidia", "backend": "cuda", "total_bytes": {{97887L << 20}}}"""))}}]}""");
        GgufFile.Language("qwen3", name: "Tiny", context: 40960).Write(Path.Combine(Library, "tiny", "Tiny-4B-Q4_K_M.gguf"));
        var snapshot = Path.Combine(Library, "hf", "org", "flash@r");
        Directory.CreateDirectory(snapshot);
        File.WriteAllText(Path.Combine(snapshot, "config.json"), """{"architectures":["X"],"num_hidden_layers":4,"num_attention_heads":4,"num_key_value_heads":4,"head_dim":64,"max_position_embeddings":131072}""");
        File.WriteAllText(Path.Combine(snapshot, "model.safetensors.index.json"), $$$"""{"metadata":{"total_size":{{{8L << 30}}}},"weight_map":{}}""");
        File.WriteAllText(Path.Combine(snapshot, "model.safetensors"), "w");
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        app.Engine.Max = 2;
        var vllm = new FakeVllm(Path.Combine(Config, "vllm"), Path.Combine(Status, "vllm"));
        var sglang = new FakeVllm(Path.Combine(Config, "sglang"), Path.Combine(Status, "sglang"));
        var gateway = new FakeGateway();
        var f = app.Create(app.ConnectionStringFor("move_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = Library,
            ["Engine:VllmStatusDir"] = Status, ["Engine:HardwareFile"] = hardware, ["Engine:ModelsMax"] = "2", ["Replicas:Enabled"] = "false",
            ["Engine:ImageReserveBytes"] = "0",
        }, s => s.AddHttpClient(VllmBackend.Client).ConfigurePrimaryHttpMessageHandler(() => new Hosts(vllm, sglang)));
        var running = new CancellationTokenSource();
        var ticking = Task.Run(async () =>
        {
            while (!running.IsCancellationRequested)
            {
                vllm.Tick();
                sglang.Tick();
                await Task.Delay(100);
            }
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        return new Stack(f, vllm, sglang, gateway, admin, async () =>
        {
            await running.CancelAsync();
            await ticking;
            running.Dispose();
            await f.DisposeAsync();
        });
    }

    /// <summary>Waits for <paramref name="check"/>, the watcher woken each time.</summary>
    private static async Task UntilAsync(Stack s, Func<Task<bool>> check, string what)
    {
        var end = DateTime.UtcNow + Deadline;
        while (!await check())
        {
            if (DateTime.UtcNow > end)
            {
                Assert.Fail("Never: " + what);
            }
            s.App.Services.GetRequiredService<EngineWatcher>().Wake();
            await Task.Delay(200);
        }
    }

    private static async Task<JsonElement> RowAsync(Stack s, string name) =>
        (await s.Admin.JsonAsync(await s.Admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);

    private static async Task<string?> PhaseAsync(Stack s, string name) =>
        (await RowAsync(s, name)).GetProperty("move") is { ValueKind: JsonValueKind.Object } move ? move.GetProperty("phase").GetString() : null;

    [Fact]
    public async Task A_llama_cpp_model_moves_to_vllm_at_runtime_its_answer_in_flight_finishing_on_llama_cpp_first()
    {
        var s = await StartAsync();
        try
        {
            await StatusAssert.Is(HttpStatusCode.Created, await s.Admin.PostAsync("/api/admin/models", new
            {
                name = "tiny", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192, ubatch = 512, extra = "flash-attn = on", inputPerMtok = 0.2, outputPerMtok = 0.8,
            }));
            await StatusAssert.Is(HttpStatusCode.NoContent, await s.Admin.Http.PutAsJsonAsync(new Uri("/api/admin/models/tiny/access", UriKind.Relative), new { audience = "Admins", groups = Array.Empty<string>() }));
            await UntilAsync(s, () => Task.FromResult(s.State.StatusOf("tiny") == "unloaded"), "llama.cpp lists tiny");
            await StatusAssert.Is(HttpStatusCode.Accepted, await s.Admin.PostAsync("/api/admin/models/tiny/load"));
            await UntilAsync(s, () => Task.FromResult(app.Engine.StatusOf("tiny") == "loaded" && s.State.StatusOf("tiny") == "loaded"), "tiny loaded on llama.cpp");
            // An answer in flight on llama.cpp; and llama.cpp lists its models as they were until it starts again (it reads its presets then).
            app.Engine.BusySlots["tiny"] = [0];
            var stale = Path.Combine(_dir, "router.ini");
            File.Copy(Path.Combine(Config, "models.ini"), stale);
            app.Engine.PresetsFile = stale;

            using var moved = await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "vllm", file = "hf/org/flash@r", context = 32768 });
            await StatusAssert.Is(HttpStatusCode.OK, moved);
            var row = await RowAsync(s, "tiny");
            Assert.Equal(("vllm", "hf/org/flash@r", "fp8"), (row.GetProperty("engineKind").GetString(), row.GetProperty("file").GetString(), row.GetProperty("kvType").GetString()));
            var move = row.GetProperty("move");
            // On the same GPUs: its answers on llama.cpp finish first, then it loads on vLLM.
            Assert.Equal(("draining", "llamacpp", "vllm"), (move.GetProperty("phase").GetString(), move.GetProperty("from").GetString(), move.GetProperty("to").GetString()));
            // Editing it again while it moves is refused; so is loading it.
            await StatusAssert.Is(HttpStatusCode.Conflict, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { context = 16384 }));

            // A request meanwhile waits for it on vLLM: nothing is loaded for it on either engine while llama.cpp answers.
            using (var soon = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Route.TakeAsync("tiny", Guid.NewGuid(), soon.Token));
            }
            var end = DateTime.UtcNow + EngineRoute.Starting + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < end)
            {
                s.App.Services.GetRequiredService<EngineWatcher>().Wake();
                await Task.Delay(300);
            }
            Assert.Equal(0, app.Engine.CallsTo("/models/unload", "tiny"));
            Assert.Equal(1, app.Engine.LoadsOf("tiny"));
            Assert.Equal("loaded", app.Engine.StatusOf("tiny"));
            Assert.DoesNotContain("tiny", s.Desired("vllm"));
            Assert.Equal("draining", await PhaseAsync(s, "tiny"));

            // Its answer done: unloaded from llama.cpp, then loaded on vLLM; once ready it is there, and the move is over.
            app.Engine.BusySlots.Remove("tiny");
            await UntilAsync(s, async () => await PhaseAsync(s, "tiny") is null && s.State.StatusOf("tiny") == "loaded", "tiny ready on vLLM");
            Assert.Equal(1, app.Engine.CallsTo("/models/unload", "tiny"));
            Assert.Equal(1, app.Engine.LoadsOf("tiny"));
            Assert.Contains("tiny", s.Desired("vllm"));
            Assert.Equal("ready", s.Vllm.Phase("tiny"));
            Assert.DoesNotContain("[tiny]", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);
            Assert.Equal(["http://vllm:8001/v1"], s.Gateway.Managed.Values.Where(m => m.Name == "tiny").Select(m => m.Params["api_base"]!.GetValue<string>()));
            row = await RowAsync(s, "tiny");
            Assert.Equal(0.8m, row.GetProperty("outputPerMtok").GetDecimal());
            Assert.Equal("Admins", row.GetProperty("access").GetProperty("audience").GetString());
            Assert.True(row.GetProperty("ubatch").ValueKind == JsonValueKind.Null);

            // Asked for now, it answers from vLLM: llama.cpp, which still lists it, is not asked to load it.
            using (var sending = await s.Route.TakeAsync("tiny", Guid.NewGuid(), CancellationToken.None))
            {
                Assert.Equal(EngineKinds.Vllm, s.Route.KindOf("tiny"));
            }
            Assert.Equal(1, app.Engine.LoadsOf("tiny"));
            var audit = string.Join('\n', (await s.Admin.JsonAsync(await s.Admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray()
                .Select(e => e.GetProperty("action").GetString() + " " + e.GetProperty("target").GetString()));
            Assert.Contains("model.move tiny", audit, StringComparison.Ordinal);
            Assert.Contains("model.moved tiny", audit, StringComparison.Ordinal);
        }
        finally
        {
            await s.Stop();
        }
    }

    [Fact]
    public async Task A_llama_cpp_model_moves_to_vllm_on_its_own_gguf_file_and_back_the_file_the_same()
    {
        var s = await StartAsync();
        try
        {
            await StatusAssert.Is(HttpStatusCode.Created, await s.Admin.PostAsync("/api/admin/models", new { name = "tiny", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192, outputPerMtok = 0.8 }));
            await UntilAsync(s, () => Task.FromResult(s.State.StatusOf("tiny") == "unloaded"), "llama.cpp lists tiny");

            // To vLLM with nothing but the engine: its GGUF file goes with it, no snapshot needed; vLLM's server is started on that file.
            await StatusAssert.Is(HttpStatusCode.OK, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "vllm" }));
            await UntilAsync(s, async () => await PhaseAsync(s, "tiny") is null && s.State.StatusOf("tiny") == "loaded", "tiny ready on vLLM");
            var row = await RowAsync(s, "tiny");
            Assert.Equal(("vllm", "tiny/Tiny-4B-Q4_K_M.gguf", "fp8"), (row.GetProperty("engineKind").GetString(), row.GetProperty("file").GetString(), row.GetProperty("kvType").GetString()));
            Assert.Equal("/library/tiny/Tiny-4B-Q4_K_M.gguf", s.Backend("vllm").Desired().Single(e => e.Model == "tiny").Args[0]);
            Assert.Equal("ready", s.Vllm.Phase("tiny"));
            Assert.DoesNotContain("[tiny]", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);

            // And back to llama.cpp the same way: the same file, in llama.cpp's presets again.
            await StatusAssert.Is(HttpStatusCode.OK, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "llamacpp" }));
            await UntilAsync(s, async () => await PhaseAsync(s, "tiny") is null && app.Engine.StatusOf("tiny") == "loaded" && s.State.StatusOf("tiny") == "loaded", "tiny back on llama.cpp");
            row = await RowAsync(s, "tiny");
            Assert.Equal(("llamacpp", "tiny/Tiny-4B-Q4_K_M.gguf", "q8_0"), (row.GetProperty("engineKind").GetString(), row.GetProperty("file").GetString(), row.GetProperty("kvType").GetString()));
            Assert.Contains("[tiny]\nmodel = /library/tiny/Tiny-4B-Q4_K_M.gguf\n", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);
            Assert.DoesNotContain("tiny", s.Desired("vllm"));
            Assert.Equal(0.8m, row.GetProperty("outputPerMtok").GetDecimal());
        }
        finally
        {
            await s.Stop();
        }
    }

    private static async Task<(string Kind, int Parallel, int? MaxOutput, string KvType, int? Ubatch)> SettingsAsync(Stack s, string name)
    {
        var row = await RowAsync(s, name);
        return (row.GetProperty("engineKind").GetString()!, row.GetProperty("parallel").GetInt32(),
            row.GetProperty("maxOutput") is { ValueKind: JsonValueKind.Number } o ? o.GetInt32() : null, row.GetProperty("kvType").GetString()!,
            row.GetProperty("ubatch") is { ValueKind: JsonValueKind.Number } u ? u.GetInt32() : null);
    }

    [Fact]
    public async Task A_round_trip_llama_cpp_to_vllm_and_back_leaves_the_model_as_it_was_its_slots_and_longest_answer_its_own()
    {
        var s = await StartAsync();
        try
        {
            await StatusAssert.Is(HttpStatusCode.Created, await s.Admin.PostAsync("/api/admin/models", new
            {
                name = "tiny", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192, parallel = 4, maxOutput = 1024, kvType = "f16", ubatch = 512,
            }));
            await UntilAsync(s, () => Task.FromResult(s.State.StatusOf("tiny") == "unloaded"), "llama.cpp lists tiny");

            // To vLLM as the dialog sends it: 16 answers at once there, and a longer answer, which vLLM's whole context holds.
            await StatusAssert.Is(HttpStatusCode.OK, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "vllm", parallel = 16, maxOutput = 4096 }));
            await UntilAsync(s, async () => await PhaseAsync(s, "tiny") is null && s.State.StatusOf("tiny") == "loaded", "tiny ready on vLLM");
            Assert.Equal(("vllm", 16, 4096, "fp8", null), await SettingsAsync(s, "tiny"));
            // Its llama.cpp settings are shown with it, for the dialog's move back.
            var left = (await RowAsync(s, "tiny")).GetProperty("left");
            Assert.Equal(("llamacpp", 4, 1024), (left.GetProperty("engineKind").GetString(), left.GetProperty("parallel").GetInt32(), left.GetProperty("maxOutput").GetInt32()));

            // Back with nothing but the engine: its 4 slots, its own longest answer, its cache type and prompt step, not vLLM's.
            await StatusAssert.Is(HttpStatusCode.OK, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "llamacpp" }));
            await UntilAsync(s, async () => await PhaseAsync(s, "tiny") is null && app.Engine.StatusOf("tiny") == "loaded", "tiny back on llama.cpp");
            Assert.Equal(("llamacpp", 4, 1024, "f16", 512), await SettingsAsync(s, "tiny"));
            Assert.Contains("[tiny]\nmodel = /library/tiny/Tiny-4B-Q4_K_M.gguf\n", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);

            // And to vLLM again: its 16 answers at once and its longer answer there come back too.
            await StatusAssert.Is(HttpStatusCode.OK, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "vllm" }));
            await UntilAsync(s, async () => await PhaseAsync(s, "tiny") is null && s.State.StatusOf("tiny") == "loaded", "tiny on vLLM again");
            Assert.Equal(("vllm", 16, 4096, "fp8", null), await SettingsAsync(s, "tiny"));
        }
        finally
        {
            await s.Stop();
        }
    }

    [Fact]
    public async Task A_model_moved_back_with_a_longest_answer_its_window_no_longer_holds_has_it_cut_to_fit_and_is_told_why()
    {
        var s = await StartAsync();
        try
        {
            await StatusAssert.Is(HttpStatusCode.Created, await s.Admin.PostAsync("/api/admin/models", new { name = "tiny", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192, parallel = 4 }));
            // Saved before the window rules: 4,096 tokens, more than its 4 slots of 2,048 leave (as SmolLM2's on the live stack).
            using (var scope = s.App.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
                (await db.LocalModels.FindAsync("tiny"))!.MaxOutput = 4096;
                await db.SaveChangesAsync();
            }
            await UntilAsync(s, () => Task.FromResult(s.State.StatusOf("tiny") == "unloaded"), "llama.cpp lists tiny");
            await StatusAssert.Is(HttpStatusCode.OK, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "vllm", parallel = 16, maxOutput = 4096 }));
            await UntilAsync(s, async () => await PhaseAsync(s, "tiny") is null && s.State.StatusOf("tiny") == "loaded", "tiny ready on vLLM");

            // The dialog's advice for the move back says what it becomes, as a warning, not a refusal.
            var advice = await s.Admin.JsonAsync(await s.Admin.PostAsync("/api/admin/models/advice", new { name = "tiny", engineKind = "llamacpp", file = "tiny/Tiny-4B-Q4_K_M.gguf", maxOutput = 4096, parallel = 4 }));
            var said = advice.GetProperty("problems").EnumerateArray().Single(p => p.GetProperty("field").GetString() == "maxOutput");
            Assert.False(said.GetProperty("error").GetBoolean());
            Assert.Contains("4,096", said.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("1,024", said.GetProperty("message").GetString(), StringComparison.Ordinal);

            // Moved back as the dialog sends it: saved with the longest answer that fits, and the save says why.
            using var back = await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "llamacpp", maxOutput = 4096, parallel = 4 });
            await StatusAssert.Is(HttpStatusCode.OK, back);
            Assert.Contains("1,024", (await s.Admin.JsonAsync(back)).GetProperty("warning").GetString(), StringComparison.Ordinal);
            await UntilAsync(s, async () => await PhaseAsync(s, "tiny") is null && app.Engine.StatusOf("tiny") == "loaded", "tiny back on llama.cpp");
            Assert.Equal(("llamacpp", 4, 1024), (await SettingsAsync(s, "tiny")) is var (kind, parallel, output, _, _) ? (kind, parallel, output) : default);
            // Not on a save that stays on its engine: there a longest answer that does not fit is refused, as before.
            await StatusAssert.Is(HttpStatusCode.BadRequest, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { maxOutput = 4096 }));
        }
        finally
        {
            await s.Stop();
        }
    }

    [Fact]
    public async Task A_vllm_model_moves_to_llama_cpp_a_failure_there_leaving_it_on_vllm_and_the_next_try_moving_it()
    {
        var s = await StartAsync();
        try
        {
            await StatusAssert.Is(HttpStatusCode.Created, await s.Admin.PostAsync("/api/admin/models", new
            {
                name = "flash", engineKind = "vllm", file = "hf/org/flash@r", context = 32768, devices = "0", gpuMemoryUtilization = 0.85, kvCpuGb = 8, outputPerMtok = 0.9,
            }));
            await StatusAssert.Is(HttpStatusCode.Accepted, await s.Admin.PostAsync("/api/admin/models/flash/load"));
            await UntilAsync(s, () => Task.FromResult(s.State.StatusOf("flash") == "loaded"), "flash loaded on vLLM");
            var port = s.Backend("vllm").Presets().Single(e => e.Model == "flash").Port;
            s.Vllm.Busy[port] = (1, 0);
            app.Engine.Broken.Add("flash");

            // To llama.cpp, whose cache type and settings it takes: vLLM's own (its share of the GPU, its CPU tier) go with the move.
            using var moved = await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/flash", UriKind.Relative), new { engineKind = "llamacpp", file = "tiny/Tiny-4B-Q4_K_M.gguf" });
            await StatusAssert.Is(HttpStatusCode.OK, moved);
            var row = await RowAsync(s, "flash");
            Assert.Equal(("llamacpp", "q8_0"), (row.GetProperty("engineKind").GetString(), row.GetProperty("kvType").GetString()));
            Assert.True(row.GetProperty("gpuMemoryUtilization").ValueKind == JsonValueKind.Null);
            Assert.Equal("draining", row.GetProperty("move").GetProperty("phase").GetString());
            // Its answer in flight on vLLM: it stays there.
            var end = DateTime.UtcNow + EngineRoute.Starting + TimeSpan.FromSeconds(3);
            while (DateTime.UtcNow < end)
            {
                s.App.Services.GetRequiredService<EngineWatcher>().Wake();
                await Task.Delay(300);
            }
            Assert.Contains("flash", s.Desired("vllm"));
            Assert.Equal(0, app.Engine.LoadsOf("flash"));

            // Done there: it leaves vLLM and fails to load on llama.cpp; it is back on vLLM, loaded again, and says why.
            s.Vllm.Busy.Clear();
            await UntilAsync(s, async () => await PhaseAsync(s, "flash") == "failed" && s.State.StatusOf("flash") == "loaded", "flash back on vLLM");
            Assert.Equal(1, app.Engine.LoadsOf("flash"));
            row = await RowAsync(s, "flash");
            Assert.Equal(("vllm", "hf/org/flash@r", "fp8", 0.85), (row.GetProperty("engineKind").GetString(), row.GetProperty("file").GetString(), row.GetProperty("kvType").GetString(),
                row.GetProperty("gpuMemoryUtilization").GetDouble()));
            Assert.Contains("llama.cpp", row.GetProperty("move").GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.Contains("flash", s.Desired("vllm"));
            Assert.DoesNotContain("[flash]", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);
            Assert.Equal(["http://vllm:8001/v1"], s.Gateway.Managed.Values.Where(m => m.Name == "flash").Select(m => m.Params["api_base"]!.GetValue<string>()));

            // The failure put away, and tried again once llama.cpp can load it: it moves.
            await StatusAssert.Is(HttpStatusCode.NoContent, await s.Admin.Http.DeleteAsync(new Uri("/api/admin/models/flash/move", UriKind.Relative)));
            Assert.Null(await PhaseAsync(s, "flash"));
            app.Engine.Broken.Remove("flash");
            await StatusAssert.Is(HttpStatusCode.OK, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/flash", UriKind.Relative), new { engineKind = "llamacpp", file = "tiny/Tiny-4B-Q4_K_M.gguf" }));
            await UntilAsync(s, async () => await PhaseAsync(s, "flash") is null && app.Engine.StatusOf("flash") == "loaded" && s.State.StatusOf("flash") == "loaded", "flash ready on llama.cpp");
            Assert.DoesNotContain("flash", s.Desired("vllm"));
            Assert.DoesNotContain(s.Backend("vllm").Presets(), e => e.Model == "flash");
            Assert.Equal(0.9m, (await RowAsync(s, "flash")).GetProperty("outputPerMtok").GetDecimal());
            Assert.Equal(EngineKinds.LlamaCpp, s.Route.KindOf("flash"));
        }
        finally
        {
            await s.Stop();
        }
    }

    [Fact]
    public async Task A_vllm_model_moves_to_sglang_on_other_gpus_beside_it_and_back_on_the_same_ones()
    {
        var s = await StartAsync();
        try
        {
            await StatusAssert.Is(HttpStatusCode.Created, await s.Admin.PostAsync("/api/admin/models", new
            {
                name = "flash", engineKind = "vllm", file = "hf/org/flash@r", context = 32768, devices = "0", kvTier = "lmcache", kvCpuGb = 8, kvDiskGb = 50,
            }));
            await StatusAssert.Is(HttpStatusCode.Accepted, await s.Admin.PostAsync("/api/admin/models/flash/load"));
            await UntilAsync(s, () => Task.FromResult(s.State.StatusOf("flash") == "loaded"), "flash loaded on vLLM");
            var port = s.Backend("vllm").Presets().Single(e => e.Model == "flash").Port;
            s.Vllm.Busy[port] = (2, 0);

            // To SGLang on GPU 1: it loads there at once, beside its copy on vLLM, which ends its answers; LMCache is vLLM's alone.
            await StatusAssert.Is(HttpStatusCode.OK, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/flash", UriKind.Relative), new { engineKind = "sglang", devices = "1" }));
            var row = await RowAsync(s, "flash");
            Assert.Equal(("sglang", "loading"), (row.GetProperty("engineKind").GetString(), row.GetProperty("move").GetProperty("phase").GetString()));
            Assert.True(row.GetProperty("kvTier").ValueKind == JsonValueKind.Null);
            await UntilAsync(s, () => Task.FromResult(s.Sglang.Phase("flash") == "ready"), "flash ready on SGLang");
            await UntilAsync(s, async () => await PhaseAsync(s, "flash") == "ready", "the move waiting for vLLM's answers");
            Assert.Contains("flash", s.Desired("vllm"));
            Assert.Equal(["http://sglang:8001/v1"], s.Gateway.Managed.Values.Where(m => m.Name == "flash").Select(m => m.Params["api_base"]!.GetValue<string>()));
            s.Vllm.Busy.Clear();
            await UntilAsync(s, async () => await PhaseAsync(s, "flash") is null, "the move over");
            Assert.DoesNotContain("flash", s.Desired("vllm"));
            Assert.DoesNotContain(s.Backend("vllm").Presets(), e => e.Model == "flash");
            Assert.Equal("loaded", s.State.StatusOf("flash"));

            // Back to vLLM on GPU 1, where SGLang has it: SGLang lets it go first, then vLLM loads it.
            await StatusAssert.Is(HttpStatusCode.OK, await s.Admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/flash", UriKind.Relative), new { engineKind = "vllm" }));
            Assert.Equal("draining", await PhaseAsync(s, "flash"));
            await UntilAsync(s, async () => await PhaseAsync(s, "flash") is null && s.State.StatusOf("flash") == "loaded", "flash back on vLLM");
            Assert.DoesNotContain("flash", s.Desired("sglang"));
            Assert.Equal(["1"], s.Backend("vllm").Desired().Where(e => e.Model == "flash").Select(e => string.Join(',', e.Gpus)));
        }
        finally
        {
            await s.Stop();
        }
    }
}
