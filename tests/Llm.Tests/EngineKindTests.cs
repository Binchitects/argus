using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Each model's engine kind (llama.cpp, vLLM or SGLang): the settings each kind has and refuses, llama.cpp's presets holding
/// its models alone and unchanged, a vLLM or SGLang model from a snapshot folder on its own engine, and a fallback that must
/// be a model the gateway has.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class EngineKindTests(AppFixture app) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-kinds-").FullName;

    private string Config => Path.Combine(_dir, "config");
    private string Library => Path.Combine(_dir, "library");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Each_kind_has_only_its_own_settings()
    {
        Assert.Empty(ModelAdvisor.KindProblems(new LocalModel { Name = "l", File = "l.gguf" }));
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "l", File = "l.gguf", Replicas = 2 }), p => p.Field == "replicas" && p.Error);
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "l", File = "l.gguf", GpuMemoryUtilization = 0.9 }), p => p.Field == "gpuMemoryUtilization");
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "l", File = "l.gguf", SpecDecode = "eagle3" }), p => p.Field == "specDecode");

        LocalModel V() => new() { Name = "v", File = "hf/a/b@c", EngineKind = EngineKinds.Vllm, KvType = "fp8" };
        Assert.Empty(ModelAdvisor.KindProblems(V()));
        var two = V();
        two.Replicas = 2;
        two.GpuMemoryUtilization = 0.85;
        two.SpecDecode = "mtp";
        Assert.Empty(ModelAdvisor.KindProblems(two));
        foreach (var (change, field) in new (Action<LocalModel>, string)[]
        {
            (m => m.Placement = "manual", "placement"), (m => m.CpuMoe = 4, "cpuMoe"), (m => m.Ubatch = 1024, "ubatch"), (m => m.ExtraPreset = "x = 1", "extra"),
            (m => m.KvType = "q8_0", "kvType"), (m => m.GpuMemoryUtilization = 0.99, "gpuMemoryUtilization"), (m => m.SpecDecode = "eagle3", "draftHead"),
            (m => m.SpecDecode = "ngram", "specDecode"), (m => m.Replicas = 9, "replicas"), (m => m.Mtp = true, "mtp"), (m => m.Projector = "p.gguf", "projector"),
        })
        {
            var m = V();
            change(m);
            Assert.Contains(ModelAdvisor.KindProblems(m), p => p.Field == field && p.Error);
        }
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "x", File = "x", EngineKind = "tgi" }), p => p.Field == "engineKind");

        // SGLang: what vLLM has, but LMCache's tiers (its own hierarchical cache is in RAM).
        LocalModel S() => new() { Name = "s", File = "hf/a/b@c", EngineKind = EngineKinds.Sglang, KvType = "fp8", Replicas = 2, SpecDecode = "mtp", KvTier = "cpu", KvCpuGb = 32 };
        Assert.Empty(ModelAdvisor.KindProblems(S()));
        foreach (var (change, field) in new (Action<LocalModel>, string)[]
        {
            (m => m.KvTier = "lmcache", "kvTier"), (m => m.KvDiskGb = 100, "kvDiskGb"), (m => m.CpuMoe = 4, "cpuMoe"), (m => m.KvType = "q8_0", "kvType"),
        })
        {
            var m = S();
            change(m);
            Assert.Contains(ModelAdvisor.KindProblems(m), p => p.Field == field && p.Error);
        }
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "s", File = "x", EngineKind = EngineKinds.Sglang, KvType = "q8_0" }),
            p => p.Message.StartsWith("SGLang's token cache", StringComparison.Ordinal));
        Assert.True(EngineKinds.SglangCapabilities is { Managed: true, TensorParallel: true, Sleep: true, PrefixCache: true, Replicas: true, SlotPinning: false });
        Assert.Equal(["cpu"], EngineKinds.SglangCapabilities.KvTiers);
        Assert.True(EngineKinds.ServerPerModel(EngineKinds.Sglang) && EngineKinds.ServerPerModel(EngineKinds.Vllm) && !EngineKinds.ServerPerModel(EngineKinds.LlamaCpp));
        // What each kind can do, for the form.
        Assert.True(EngineKinds.VllmCapabilities is { TensorParallel: true, Sleep: true, PrefixCache: true, SlotPinning: false });
        Assert.True(EngineKinds.LlamaCppCapabilities is { TensorParallel: false, SpillToRam: true, SlotPinning: true });
        Assert.Contains("rocm", EngineKinds.LlamaCppCapabilities.Backends);
        Assert.False(EngineKinds.Managed(EngineKinds.External));
    }

    [Fact]
    public void A_move_waits_for_its_answers_on_the_old_engine_only_on_the_same_gpus()
    {
        var hw = new Hardware("GPU", 2, 2L << 40, 0, 1L << 40, 0, Devices: [new GpuDevice(0, "GPU", 1L << 40, 0), new GpuDevice(1, "GPU", 1L << 40, 0)]);
        var vllm = new VllmBackends(Microsoft.Extensions.Options.Options.Create(new EngineOptions()), null!, TimeProvider.System);
        LocalModel M(string kind, string? devices) => new() { Name = "m", File = "f", EngineKind = kind, Devices = devices };
        // llama.cpp naming no GPU is on all of them; a machine whose GPUs are not known shares them all.
        Assert.True(ModelMoves.Overlap(M(EngineKinds.LlamaCpp, null), M(EngineKinds.Vllm, "1"), hw));
        Assert.False(ModelMoves.Overlap(M(EngineKinds.Vllm, "0"), M(EngineKinds.Sglang, "1"), hw));
        Assert.True(ModelMoves.Overlap(M(EngineKinds.Vllm, "0"), M(EngineKinds.Sglang, "1"), null));

        var now = DateTimeOffset.UtcNow;
        var draining = ModelMoves.Begin(M(EngineKinds.LlamaCpp, null), M(EngineKinds.Vllm, "1"), "loaded", hw, vllm, "ada", now);
        Assert.Equal((ModelMove.Draining, ModelMove.Serving, true, "llamacpp", "vllm"), (draining.Phase, draining.Old, draining.WasLoaded, draining.FromEngine, draining.ToEngine));
        // On other GPUs, or not loaded where it was: it loads on its new engine at once.
        Assert.Equal((ModelMove.Loading, ModelMove.Serving), (ModelMoves.Begin(M(EngineKinds.Vllm, "0"), M(EngineKinds.Sglang, "1"), "loaded", hw, vllm, null, now) is var b ? (b.Phase, b.Old) : default));
        Assert.Equal((ModelMove.Loading, ModelMove.Gone, false), (ModelMoves.Begin(M(EngineKinds.LlamaCpp, null), M(EngineKinds.Vllm, "1"), "unloaded", hw, vllm, null, now) is var c
            ? (c.Phase, c.Old, c.WasLoaded) : default));
        // Its old settings go with it, to put it back should it fail.
        Assert.Equal(EngineKinds.LlamaCpp, System.Text.Json.JsonSerializer.Deserialize<LocalModel>(draining.From!)!.EngineKind);
    }

    /// <summary>An app with a GGUF file and two snapshot folders in its library; <paramref name="features"/>: only those on (Features:On).</summary>
    private WebApplicationFactory<Program> NewApp(IEnumerable<string>? features = null)
    {
        GgufFile.Language("qwen3", name: "Tiny", context: 40960).Write(Path.Combine(Library, "tiny", "Tiny-4B-Q4_K_M.gguf"));
        Directory.CreateDirectory(Path.Combine(Library, "hf", "acme", "Flash@abc123"));
        File.WriteAllText(Path.Combine(Library, "hf", "acme", "Flash@abc123", "config.json"), """{"architectures":["Qwen3ForCausalLM"],"max_position_embeddings":131072,"num_hidden_layers":2,"num_attention_heads":4,"num_key_value_heads":2,"head_dim":64}""");
        File.WriteAllText(Path.Combine(Library, "hf", "acme", "Flash@abc123", "model.safetensors"), "weights");
        Directory.CreateDirectory(Path.Combine(Library, "hf", "acme", "Empty@abc123"));
        Directory.CreateDirectory(Config);
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        return app.Create(app.ConnectionStringFor("kinds_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = Library,
            ["Engine:VllmStatusDir"] = Path.Combine(_dir, "vllm-status"), ["Features:On"] = features is null ? null : string.Join(',', features),
        });
    }

    [Fact]
    public async Task A_move_to_llama_cpp_is_refused_while_its_feature_is_off_as_the_engine_field_says_it()
    {
        await using var f = NewApp(Llm.Api.Operations.Features.All.Select(x => x.Id).Where(id => id != "chat-models"));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new { name = "flash", engineKind = "vllm", file = "hf/acme/Flash@abc123", context = 32768 }));
        var models = await admin.JsonAsync(await admin.GetAsync("/api/admin/models"));
        // The Engine field shows llama.cpp off by its instance.
        Assert.False(models.GetProperty("engine").GetProperty("instances").EnumerateArray().Single(i => i.GetProperty("name").GetString() == "llamacpp").GetProperty("on").GetBoolean());

        // A move there would only fail: refused, with what turns it on; the model stays on vLLM.
        using var refused = await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/flash", UriKind.Relative), new { engineKind = "llamacpp", file = "tiny/Tiny-4B-Q4_K_M.gguf" });
        await StatusAssert.Is(HttpStatusCode.Conflict, refused);
        var why = (await admin.JsonAsync(refused)).GetProperty("error").GetString();
        Assert.Contains("./arenactl repair --with chat-models", why, StringComparison.Ordinal);
        // On a host with no network, the offline installer of this release does it.
        Assert.Contains($"sh arena-{Llm.Api.AppInfo.Current.Version}-offline.run repair --with chat-models", why, StringComparison.Ordinal);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "flash");
        Assert.Equal(("vllm", "hf/acme/Flash@abc123", JsonValueKind.Null), (row.GetProperty("engineKind").GetString(), row.GetProperty("file").GetString(), row.GetProperty("move").ValueKind));
    }

    [Fact]
    public async Task The_engine_fields_request_moves_a_vllm_model_to_sglang_on_its_snapshot_with_one_engine_of_each()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new
        {
            name = "flash", engineKind = "vllm", file = "hf/acme/Flash@abc123", context = 32768, kvTier = "lmcache", kvCpuGb = 8, kvDiskGb = 50,
            toolParser = "hermes", reasoningParser = "qwen3",
        }));
        foreach (var engine in new[] { "vllm", "sglang" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, "vllm-status", engine));
            File.WriteAllText(Path.Combine(_dir, "vllm-status", engine, "supervisor.json"), $$"""{"at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"started":0,"restart":0,"reload":0}""");
        }

        // What the dialog sends with SGLang chosen in its Engine field: its whole form, the first SGLang engine (none named), LMCache cleared.
        using var moved = await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/flash", UriKind.Relative), new
        {
            engineKind = "sglang", file = "hf/acme/Flash@abc123", replicas = 1, kvType = "fp8", context = 32768, parallel = 16, kvCpuGb = 8, sleep = true,
            clear = new[] { "devices", "gpuMemoryUtilization", "quantization", "maxOutput", "specDecode", "draftHead", "kvTier", "fallback", "engine" },
        });
        await StatusAssert.Is(HttpStatusCode.OK, moved);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "flash");
        Assert.Equal(("sglang", "hf/acme/Flash@abc123", "vllm", "sglang"), (row.GetProperty("engineKind").GetString(), row.GetProperty("file").GetString(),
            row.GetProperty("move").GetProperty("fromEngine").GetString(), row.GetProperty("move").GetProperty("toEngine").GetString()));
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null), (row.GetProperty("engine").ValueKind, row.GetProperty("kvTier").ValueKind, row.GetProperty("kvDiskGb").ValueKind));
        Assert.Equal(8, row.GetProperty("kvCpuGb").GetInt32());
        // Its parsers go with it: SGLang has both by the same names.
        Assert.Equal(("hermes", "qwen3"), (row.GetProperty("toolParser").GetString(), row.GetProperty("reasoningParser").GetString()));
    }

    [Fact]
    public async Task Its_parsers_are_saved_refused_when_its_engine_has_not_them_and_kept_for_a_move_back()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // LMDeploy's name on vLLM: refused, saying vLLM's.
        var refused = await admin.PostAsync("/api/admin/models", new { name = "flash", engineKind = "vllm", file = "hf/acme/Flash@abc123", context = 32768, toolParser = "qwen2d5" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("toolParser", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new
        {
            name = "flash", engineKind = "vllm", file = "hf/acme/Flash@abc123", context = 32768, toolParser = "qwen3_xml", reasoningParser = "none",
        }));
        var args = Args("vllm");
        Assert.Equal("qwen3_xml", args[args.IndexOf("--tool-call-parser") + 1]);
        Assert.Contains("--enable-auto-tool-choice", args);
        Assert.DoesNotContain("--reasoning-parser", args);
        var advice = await admin.JsonAsync(await admin.PostAsync("/api/admin/models/advice", new { name = "flash", engineKind = "vllm", file = "hf/acme/Flash@abc123" }));
        var parsers = advice.GetProperty("parsers");
        Assert.Equal(("qwen3", "qwen3_xml", "hermes"), (parsers.GetProperty("family").GetString(), parsers.GetProperty("tool").GetProperty("chosen").GetString(),
            parsers.GetProperty("tool").GetProperty("automatic").GetString()));

        foreach (var engine in new[] { "vllm", "lmdeploy" })
        {
            Directory.CreateDirectory(Path.Combine(_dir, "vllm-status", engine));
            File.WriteAllText(Path.Combine(_dir, "vllm-status", engine, "supervisor.json"), $$"""{"at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"started":0,"restart":0,"reload":0}""");
        }
        // To LMDeploy, which has no qwen3_xml: its own for Qwen3 (automatic); "none" it has too.
        using (var moved = await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/flash", UriKind.Relative), new
        {
            engineKind = "lmdeploy", file = "hf/acme/Flash@abc123", kvType = "fp8", context = 32768, parallel = 16, sleep = false,
        }))
        {
            await StatusAssert.Is(HttpStatusCode.OK, moved);
        }
        var row = await Row(admin, "flash");
        Assert.Equal((JsonValueKind.Null, "none"), (row.GetProperty("toolParser").ValueKind, row.GetProperty("reasoningParser").GetString()));
        Assert.Equal("qwen3_xml", row.GetProperty("left").GetProperty("toolParser").GetString());
        await Moved(f, "flash");
        // Back to vLLM: its own choice there again.
        using (var back = await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/flash", UriKind.Relative), new
        {
            engineKind = "vllm", file = "hf/acme/Flash@abc123", kvType = "fp8", context = 32768, parallel = 16,
        }))
        {
            await StatusAssert.Is(HttpStatusCode.OK, back);
        }
        await Moved(f, "flash");
        row = await Row(admin, "flash");
        Assert.Equal(("qwen3_xml", "none"), (row.GetProperty("toolParser").GetString(), row.GetProperty("reasoningParser").GetString()));
        // Cleared: automatic again, by its family.
        using (var cleared = await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/flash", UriKind.Relative), new { clear = new[] { "toolParser", "reasoningParser" } }))
        {
            await StatusAssert.Is(HttpStatusCode.OK, cleared);
        }
        row = await Row(admin, "flash");
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (row.GetProperty("toolParser").ValueKind, row.GetProperty("reasoningParser").ValueKind));
    }

    /// <summary>Its move as done: on its new engine alone (ModelMoves' own steps need the engines to answer).</summary>
    private static async Task Moved(WebApplicationFactory<Program> f, string name)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        (await db.LocalModels.SingleAsync(m => m.Name == name)).Move = null;
        await db.SaveChangesAsync();
    }

    private List<string> Args(string engine) =>
        [.. System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(Config, engine, VllmBackend.PresetsFile)))!["models"]!.AsArray().Single()!["args"]!.AsArray()
            .Select(a => a!.GetValue<string>())];

    private static async Task<JsonElement> Row(TestBrowser admin, string name) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);

    [Fact]
    public async Task A_vllm_model_without_gpus_gets_those_proposed_for_the_machine_its_engine_reports()
    {
        // Four 96 GB GPUs as an engine's probe writes them (services/gpu-probe.py), with gpt-oss-120b's real config.
        var hardware = Path.Combine(_dir, "hardware.json");
        File.WriteAllText(hardware, $$"""{"ram_bytes": {{1L << 40}}, "source": "nvidia-smi", "gpus": [{{string.Join(',', Enumerable.Range(0, 4).Select(i =>
            $$"""{"index": {{i}}, "name": "NVIDIA RTX PRO 6000 Blackwell Server Edition", "vendor": "nvidia", "backend": "cuda", "total_bytes": {{97887L << 20}}}"""))}}]}""");
        var oss = Path.Combine(Library, "hf", "openai", "gpt-oss-120b@main");
        Directory.CreateDirectory(oss);
        foreach (var f in new[] { "config.json", "model.safetensors.index.json" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "vllm", "gpt-oss-120b", f), Path.Combine(oss, f));
        }
        File.WriteAllText(Path.Combine(oss, "model-00000-of-00014.safetensors"), "weights");
        GgufFile.Language("qwen3", name: "Tiny", context: 40960).Write(Path.Combine(Library, "tiny", "Tiny-4B-Q4_K_M.gguf"));
        Directory.CreateDirectory(Config);
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        await using var f2 = app.Create(app.ConnectionStringFor("kinds_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = Library, ["Engine:HardwareFile"] = hardware,
        });
        var admin = await new TestBrowser(f2).SignedInAsync("admin", AppFixture.AdminPassword);
        var advice = await admin.JsonAsync(await admin.PostAsync("/api/admin/models/advice", new { engineKind = "vllm", file = "hf/openai/gpt-oss-120b@main", context = 131072 }));
        // GPU 0 has the picture server: one GPU of the others, with the cache the design counts.
        Assert.Equal("1", advice.GetProperty("recommended").GetProperty("devices").GetString());
        Assert.Equal("gpu", advice.GetProperty("estimate").GetProperty("fit").GetString());
        var saved = await admin.PostAsync("/api/admin/models", new { name = "oss", engineKind = "vllm", file = "hf/openai/gpt-oss-120b@main", context = 131072 });
        await StatusAssert.Is(HttpStatusCode.Created, saved);
        Assert.Contains("GPUs 1, proposed", (await admin.JsonAsync(saved)).GetProperty("warning").GetString(), StringComparison.Ordinal);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "oss");
        Assert.Equal("1", row.GetProperty("devices").GetString());
        // On GPUs it does not fit, or a split its heads refuse: refused.
        var refused = await admin.PostAsync("/api/admin/models", new { name = "oss3", engineKind = "vllm", file = "hf/openai/gpt-oss-120b@main", context = 131072, devices = "0,1,2" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
    }

    /// <summary>
    /// An app on <paramref name="gpus"/> 96 GB GPUs as their probe writes them (GPU i with i + 1 GiB in use), with gpt-oss-120b's
    /// real config, a small GGUF and its projector in its library.
    /// </summary>
    private WebApplicationFactory<Program> OnGpus(int gpus)
    {
        var hardware = Path.Combine(_dir, "hardware.json");
        File.WriteAllText(hardware, $$"""{"ram_bytes": {{1L << 41}}, "source": "nvidia-smi", "gpus": [{{string.Join(',', Enumerable.Range(0, gpus).Select(i =>
            $$"""{"index": {{i}}, "name": "NVIDIA RTX PRO 6000 Blackwell Server Edition", "vendor": "nvidia", "backend": "cuda", "total_bytes": {{97887L << 20}}, "free_bytes": {{(97887L << 20) - ((i + 1L) << 30)}}}"""))}}]}""");
        var oss = Path.Combine(Library, "hf", "openai", "gpt-oss-120b@main");
        Directory.CreateDirectory(oss);
        foreach (var f in new[] { "config.json", "model.safetensors.index.json" })
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Resources", "vllm", "gpt-oss-120b", f), Path.Combine(oss, f), overwrite: true);
        }
        File.WriteAllText(Path.Combine(oss, "model-00000-of-00014.safetensors"), "weights");
        GgufFile.Language("qwen3", name: "Tiny", context: 40960).Write(Path.Combine(Library, "tiny", "Tiny-4B-Q4_K_M.gguf"));
        new GgufFile().Text("general.architecture", "clip").Bool("clip.has_vision_encoder", true).U32("clip.vision.projection_dim", 64)
            .Tensor("v.blk.0.attn_q.weight", 2048).Write(Path.Combine(Library, "tiny", "mmproj-Tiny-F16.gguf"));
        Directory.CreateDirectory(Config);
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        return app.Create(app.ConnectionStringFor("kinds_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = Library, ["Engine:HardwareFile"] = hardware,
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Models_saved_without_gpus_go_on_the_least_placed_gpus_one_after_another_not_all_on_one(int gpus)
    {
        await using var f = OnGpus(gpus);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<string> AddAsync(string name, string? devices = null)
        {
            var saved = await admin.PostAsync("/api/admin/models", new { name, engineKind = "vllm", file = "hf/openai/gpt-oss-120b@main", context = 131072, devices });
            await StatusAssert.Is(HttpStatusCode.Created, saved);
            var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);
            return row.GetProperty("devices").GetString()!;
        }
        // None kept loaded, one GPU each: GPU 0 has the picture server, so the first goes on GPU 1, the next on GPU 2... and
        // once every GPU has one, GPU 0 (the picture server's 2 GiB is less than a model's share), then round again.
        var placed = new List<string>();
        for (var i = 0; i <= gpus; i++)
        {
            placed.Add(await AddAsync($"oss{i}"));
        }
        Assert.Equal([.. Enumerable.Range(1, gpus - 1).Select(g => g.ToString(System.Globalization.CultureInfo.InvariantCulture)), "0", "1"], placed);
        // An admin's own choice is kept, however placed that GPU is.
        Assert.Equal("1", await AddAsync("mine", "1"));
        // Admin → Models shows what each GPU holds (none loaded) and the memory in use there.
        var holdings = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("engine").GetProperty("holdings").EnumerateArray().ToList();
        Assert.Equal(gpus, holdings.Count);
        string[] Holds(int gpu) => [.. holdings[gpu].GetProperty("holds").EnumerateArray().Select(h => h.GetProperty("name").GetString()!)];
        string N(int i) => i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(["oss" + N(gpus - 1), "the picture server"], Holds(0));
        Assert.Equal(["mine", "oss0", "oss" + N(gpus)], Holds(1));
        Assert.All(Enumerable.Range(2, gpus - 2), g => Assert.Equal(["oss" + N(g - 1)], Holds(g)));
        Assert.All(holdings, h => Assert.All(h.GetProperty("holds").EnumerateArray(), x => Assert.False(x.GetProperty("loaded").GetBoolean())));
        Assert.Equal([.. Enumerable.Range(1, gpus).Select(g => (long)g << 30)], holdings.Select(h => h.GetProperty("used").GetInt64()));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task Vision_models_on_several_gpus_read_their_images_on_different_gpus_not_all_on_the_first(int gpus)
    {
        await using var f = OnGpus(gpus);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task AddAsync(string name, string? devices = null, string? extra = null) => await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models",
            new { name, file = "tiny/Tiny-4B-Q4_K_M.gguf", projector = "tiny/mmproj-Tiny-F16.gguf", context = 8192, devices, extraPreset = extra }));
        // One more than there are GPUs, on all of them; one on the last two; one whose extra lines name its own; one on a single GPU.
        var names = Enumerable.Range(0, gpus + 1).Select(i => $"see{i}").ToList();
        foreach (var name in names)
        {
            await AddAsync(name);
        }
        await AddAsync("two", $"{gpus - 2},{gpus - 1}");
        await AddAsync("own", extra: "mmproj-device = CUDA0");
        await AddAsync("one", "0");
        var ini = await File.ReadAllTextAsync(Path.Combine(Config, "models.ini"));
        string? ProjectorOf(string model) => ini.Split("\n[").Single(x => x.TrimStart('[').StartsWith(model + "]", StringComparison.Ordinal)).Split('\n')
            .Where(l => l.StartsWith("mmproj-device = ", StringComparison.Ordinal)).Select(l => l["mmproj-device = ".Length..]).SingleOrDefault();
        // own and one have theirs on GPU 0 already, and count there. Then by name: see0 on GPU 1, see1 on GPU 2... each on
        // its GPU with the fewest so far; two too.
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        var count = new int[gpus];
        count[0] = 2;
        foreach (var (name, mine) in names.Append("two").Order(StringComparer.Ordinal)
            .Select(n => (n, n == "two" ? new[] { gpus - 2, gpus - 1 } : [.. Enumerable.Range(0, gpus)])))
        {
            var gpu = mine.OrderBy(g => count[g]).ThenBy(g => g).First();
            count[gpu]++;
            expected[name] = $"CUDA{gpu.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
        Assert.All(expected, e => Assert.Equal(e.Value, ProjectorOf(e.Key)));
        // Every projector on each GPU, the admin's and the one-GPU model's too: no GPU has two more than another.
        var total = new int[gpus];
        foreach (var gpu in names.Append("two").Append("own").Select(ProjectorOf).Append("CUDA0"))
        {
            total[int.Parse(gpu![4..], System.Globalization.CultureInfo.InvariantCulture)]++;
        }
        Assert.True(total.Max() - total.Min() <= 1, $"projectors on each GPU: {string.Join(", ", total)}");
        // An admin's own line is kept as it is (once), and a model on one GPU has its projector there already.
        Assert.Equal("CUDA0", ProjectorOf("own"));
        Assert.Null(ProjectorOf("one"));
    }

    [Fact]
    public async Task A_llama_cpp_model_on_its_own_gpu_is_counted_where_a_vllm_model_goes()
    {
        await using var f = OnGpus(4);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new { name = "tiny", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192, devices = "1" }));
        var advice = await admin.JsonAsync(await admin.PostAsync("/api/admin/models/advice", new { engineKind = "vllm", file = "hf/openai/gpt-oss-120b@main", context = 131072 }));
        // GPU 0 has the picture server, GPU 1 the llama.cpp model: GPU 2.
        Assert.Equal("2", advice.GetProperty("recommended").GetProperty("devices").GetString());
    }

    [Fact]
    public async Task A_llama_cpp_model_moves_to_vllm_once_it_runs_keeping_its_name_access_and_prices_and_leaves_llama_cpp()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new
        {
            name = "tiny", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192, ubatch = 512, yarn = false, extra = "flash-attn = on", inputPerMtok = 0.2, outputPerMtok = 0.8,
        }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/models/tiny/access", UriKind.Relative), new { audience = "Admins", groups = Array.Empty<string>() }));
        Assert.Contains("[tiny]", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);

        // To vLLM while vLLM does not run: refused, as it would only fail there; it stays as it was.
        using var refused = await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "vllm", file = "hf/acme/Flash@abc123", context = 32768 });
        await StatusAssert.Is(HttpStatusCode.Conflict, refused);
        Assert.Contains("is not running", (await admin.JsonAsync(refused)).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("[tiny]", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);

        // Its snapshot instead of its GGUF file, on vLLM, once vLLM runs: llama.cpp's own settings go with the move.
        Directory.CreateDirectory(Path.Combine(_dir, "vllm-status", "vllm"));
        File.WriteAllText(Path.Combine(_dir, "vllm-status", "vllm", "supervisor.json"), $$"""{"at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"started":0,"restart":0,"reload":0}""");
        using var moved = await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny", UriKind.Relative), new { engineKind = "vllm", file = "hf/acme/Flash@abc123", context = 32768 });
        await StatusAssert.Is(HttpStatusCode.OK, moved);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "tiny");
        Assert.Equal(("vllm", "hf/acme/Flash@abc123", "fp8"), (row.GetProperty("engineKind").GetString(), row.GetProperty("file").GetString(), row.GetProperty("kvType").GetString()));
        // Not loaded on llama.cpp: nothing to wait for there, it loads on vLLM at once.
        Assert.Equal(("loading", "llamacpp", "vllm"), (row.GetProperty("move").GetProperty("phase").GetString(), row.GetProperty("move").GetProperty("from").GetString(),
            row.GetProperty("move").GetProperty("to").GetString()));
        Assert.True(row.GetProperty("ubatch").ValueKind == JsonValueKind.Null);
        Assert.Equal(0.8m, row.GetProperty("outputPerMtok").GetDecimal());
        Assert.Equal("Admins", row.GetProperty("access").GetProperty("audience").GetString());
        Assert.DoesNotContain("[tiny]", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vllm_model_is_kept_out_of_llama_cpps_presets_which_stay_as_they_were()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new { name = "tiny", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192 }));
        var before = await File.ReadAllTextAsync(Path.Combine(Config, "models.ini"));

        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new
        {
            name = "flash", engineKind = "vllm", file = "hf/acme/Flash@abc123", context = 65536, parallel = 64, replicas = 2, gpuMemoryUtilization = 0.85, fallback = "tiny",
        }));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")));
        Assert.Contains("[tiny]\nmodel = /library/tiny/Tiny-4B-Q4_K_M.gguf\nctx-size = 8192\nparallel = 1\n", before, StringComparison.Ordinal);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "flash");
        Assert.Equal("vllm", row.GetProperty("engineKind").GetString());
        Assert.Equal("fp8", row.GetProperty("kvType").GetString());
        Assert.Equal(2, row.GetProperty("replicas").GetInt32());
        Assert.Equal("tiny", row.GetProperty("fallback").GetString());

        // What the kind lacks, a folder that is no snapshot, and a fallback that is not a model: refused.
        async Task Refused(object body, string field)
        {
            var res = await admin.PostAsync("/api/admin/models", body);
            await StatusAssert.Is(HttpStatusCode.BadRequest, res);
            Assert.Contains(field, await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        await Refused(new { name = "x1", engineKind = "vllm", file = "hf/acme/Empty@abc123" }, "file");
        await Refused(new { name = "x2", engineKind = "vllm", file = "tiny/Gone-Q4_K_M.gguf" }, "file");
        await Refused(new { name = "x3", engineKind = "vllm", file = "hf/acme/Flash@abc123", placement = "manual" }, "placement");
        await Refused(new { name = "x4", file = "tiny/Tiny-4B-Q4_K_M.gguf", replicas = 2 }, "replicas");
        await Refused(new { name = "x5", file = "tiny/Tiny-4B-Q4_K_M.gguf", fallback = "nothing-of-the-sort" }, "fallback");
        await Refused(new { name = "x6", engineKind = "external", file = "tiny/Tiny-4B-Q4_K_M.gguf" }, "engineKind");
        await Refused(new { name = "x7", engineKind = "vllm", file = "hf/acme/../../tiny" }, "file");
    }

    [Fact]
    public async Task A_vllm_or_sglang_model_runs_a_gguf_file_from_the_library_named_in_its_servers_arguments()
    {
        // A model split in two parts (its header in the first), and an embedding model.
        GgufFile.Language("qwen3", name: "Big").Write(Path.Combine(Library, "big", "Big-Q4_K_M-00001-of-00002.gguf"));
        new GgufFile().Text("general.architecture", "qwen3").Tensor("blk.9.ffn_up.weight", 4096, 64, 64).Write(Path.Combine(Library, "big", "Big-Q4_K_M-00002-of-00002.gguf"));
        new GgufFile().Text("general.architecture", "nomic-bert").U32("nomic-bert.pooling_type", 1).Write(Path.Combine(Library, "embed", "nomic-embed-text-v1.5.f16.gguf"));
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

        // What the form shows for its GGUF file on vLLM: the file's own profile and limits, from its header.
        var advice = await admin.JsonAsync(await admin.PostAsync("/api/admin/models/advice", new { engineKind = "vllm", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 32768 }));
        Assert.Equal(("Q4_K_M", "qwen3", 40960), (advice.GetProperty("profile").GetProperty("quant").GetString(), advice.GetProperty("profile").GetProperty("architecture").GetString(),
            advice.GetProperty("limits").GetProperty("context").GetProperty("max").GetInt32()));

        // Saved on vLLM, its server is started on the file itself: no snapshot, no quantisation of its own (vLLM's launcher names its tokenizer).
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new { name = "tiny", engineKind = "vllm", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 32768 }));
        // Split, on vLLM: its first part, which names the others.
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new { name = "big", engineKind = "vllm", file = "big/Big-Q4_K_M-00001-of-00002.gguf", context = 8192 }));
        var presets = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Config, "vllm", VllmBackend.PresetsFile)))!["models"]!.AsArray();
        List<string> ArgsOf(string name) => [.. presets.Single(e => e!["name"]!.GetValue<string>() == name)!["args"]!.AsArray().Select(a => a!.GetValue<string>())];
        Assert.Equal(["/library/tiny/Tiny-4B-Q4_K_M.gguf", "--tensor-parallel-size", "1", "--max-model-len", "32768"], ArgsOf("tiny").Take(5));
        Assert.DoesNotContain("--quantization", ArgsOf("tiny"));
        Assert.Equal("/library/big/Big-Q4_K_M-00001-of-00002.gguf", ArgsOf("big")[0]);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "tiny");
        Assert.Equal(("vllm", "tiny/Tiny-4B-Q4_K_M.gguf", "fp8"), (row.GetProperty("engineKind").GetString(), row.GetProperty("file").GetString(), row.GetProperty("kvType").GetString()));
        Assert.Equal("Q4_K_M", row.GetProperty("profile").GetProperty("quant").GetString());

        // On SGLang, one file: its arguments name it; a split one is refused, with why and how to merge it.
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new { name = "swift", engineKind = "sglang", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192 }));
        var sglang = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Config, "sglang", VllmBackend.PresetsFile)))!["models"]!.AsArray();
        Assert.Equal(["--model-path", "/library/tiny/Tiny-4B-Q4_K_M.gguf"], sglang.Single()!["args"]!.AsArray().Select(a => a!.GetValue<string>()).Take(2));
        var split = await admin.PostAsync("/api/admin/models", new { name = "big2", engineKind = "sglang", file = "big/Big-Q4_K_M-00001-of-00002.gguf", context = 8192 });
        await StatusAssert.Is(HttpStatusCode.BadRequest, split);
        var why = (await admin.JsonAsync(split)).GetProperty("error").GetString();
        Assert.Contains("split in 2 parts", why, StringComparison.Ordinal);
        Assert.Contains("llama-gguf-split --merge", why, StringComparison.Ordinal);

        // A quantisation of its own, and a GGUF file that is no chat model: refused.
        var quantised = await admin.PostAsync("/api/admin/models", new { name = "q", engineKind = "vllm", file = "tiny/Tiny-4B-Q4_K_M.gguf", quantization = "fp8" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, quantised);
        Assert.Contains("quantised already (Q4_K_M)", (await admin.JsonAsync(quantised)).GetProperty("error").GetString(), StringComparison.Ordinal);
        var embedding = await admin.PostAsync("/api/admin/models", new { name = "e", engineKind = "vllm", file = "embed/nomic-embed-text-v1.5.f16.gguf" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, embedding);
        Assert.Contains("cannot run as a chat model: An embedding model", (await admin.JsonAsync(embedding)).GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_sglang_model_goes_to_the_sglang_engine_with_sglangs_arguments_and_to_the_gateway_at_its_port()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new
        {
            name = "swift", engineKind = "sglang", file = "hf/acme/Flash@abc123", context = 65536, parallel = 48, devices = "0", kvCpuGb = 16, temperature = 0.7,
        }));
        // This stack's two engines (Engine:Vllm unset): vllm's presets have none of it, sglang's have it.
        var presets = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Config, "sglang", VllmBackend.PresetsFile)))!["models"]!.AsArray();
        var entry = Assert.Single(presets)!;
        Assert.Equal(("swift", 8001), (entry["name"]!.GetValue<string>(), entry["port"]!.GetValue<int>()));
        var args = entry["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        string After(string flag) => args[args.IndexOf(flag) + 1];
        Assert.Equal(("/library/hf/acme/Flash@abc123", "65536", "48", "fp8_e4m3", "16"),
            (After("--model-path"), After("--context-length"), After("--max-running-requests"), After("--kv-cache-dtype"), After("--hicache-size")));
        Assert.Contains("--schedule-low-priority-values-first", args);
        // A request with no priority of its own (SGLang's own warm-up) as an API key's: without it SGLang gives it the largest
        // number there is, which its radix cache then overflows on as the request ends, and the server dies.
        Assert.Equal("0", After("--default-priority-value"));
        Assert.Equal("""{"temperature":0.7}""", After("--preferred-sampling-params"));
        // Its parsers by its family, from its config.json (Qwen3ForCausalLM) as the library has it: SGLang's names for Qwen3's.
        Assert.Equal(("qwen", "qwen3"), (After("--tool-call-parser"), After("--reasoning-parser")));
        Assert.False(File.Exists(Path.Combine(Config, "vllm", VllmBackend.PresetsFile)));
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "swift");
        Assert.Equal("sglang", row.GetProperty("engineKind").GetString());
        // At the gateway: its own port on the sglang engine, never more at once than its people at once.
        var registered = (await app.RoutesOfAsync(f)).Single(m => m.Name == "swift");
        Assert.Equal("http://sglang:8001/v1", registered.Params["api_base"]!.GetValue<string>());
        Assert.Equal(48, registered.Params["max_parallel_requests"]!.GetValue<int>());
        // Loaded, Prometheus scrapes it at its port, labelled with its GPUs for the dashboards' figures by GPU.
        using (var scope = f.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ModelCatalog>().WriteTargetsAsync(["swift"]);
        }
        var target = Assert.Single(System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Config, ModelCatalog.TargetsFile)))!.AsArray())!;
        Assert.Equal(("sglang:8001", "swift", "sglang", "0"), (target["targets"]![0]!.GetValue<string>(), target["labels"]!["model"]!.GetValue<string>(),
            target["labels"]!["kind"]!.GetValue<string>(), target["labels"]!["gpus"]!.GetValue<string>()));

        // LMCache is vLLM's: refused.
        var refused = await admin.PostAsync("/api/admin/models", new { name = "swift2", engineKind = "sglang", file = "hf/acme/Flash@abc123", kvTier = "lmcache" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("kvTier", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_lmdeploy_model_runs_a_snapshot_and_a_strata_model_a_gguf_file_each_on_its_own_engine_and_at_the_gateway()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new
        {
            name = "turbo", engineKind = "lmdeploy", file = "hf/acme/Flash@abc123", context = 32768, parallel = 24, devices = "0",
        }));
        var entry = Assert.Single(System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Config, "lmdeploy", VllmBackend.PresetsFile)))!["models"]!.AsArray())!;
        var args = entry["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal(("/library/hf/acme/Flash@abc123", "32768", "24"), (args[0], args[args.IndexOf("--session-len") + 1], args[args.IndexOf("--max-batch-size") + 1]));
        // LMDeploy's names for Qwen3's parsers: without a tool-call parser it refuses every request with tools.
        Assert.Equal(("qwen3", "default"), (args[args.IndexOf("--tool-call-parser") + 1], args[args.IndexOf("--reasoning-parser") + 1]));
        // It was saved without sleep: the app puts no LMDeploy model to sleep.
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "turbo");
        Assert.Equal(("lmdeploy", false), (row.GetProperty("engineKind").GetString(), row.GetProperty("sleep").GetBoolean()));
        Assert.Equal("http://lmdeploy:8001/v1", (await app.RoutesOfAsync(f)).Single(m => m.Name == "turbo").Params["api_base"]!.GetValue<string>());

        // Strata runs the library's GGUF file, not a snapshot; and nothing of what it has none of.
        var snapshot = await admin.PostAsync("/api/admin/models", new { name = "flash", engineKind = "strata", file = "hf/acme/Flash@abc123", context = 32768, parallel = 2 });
        await StatusAssert.Is(HttpStatusCode.BadRequest, snapshot);
        var spec = await admin.PostAsync("/api/admin/models", new { name = "flash", engineKind = "strata", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 32768, parallel = 2, specDecode = "mtp" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, spec);
        Assert.Contains("specDecode", await spec.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
