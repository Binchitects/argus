using System.Globalization;
using System.Net.Sockets;
using System.Text;
using DotNet.Testcontainers.Builders;
using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>
/// The engines as one (EngineClient over IEngineBackend): llama.cpp's router and a vLLM engine side by side, each model
/// going to its own; the vLLM backend's presets, desired state, status and metrics; its arguments and ports; and, in a
/// container, the real supervisor running the fake vLLM as the app drives it.
/// </summary>
public sealed class VllmBackendTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-vllm-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private EngineOptions Options(string? host = null) => new()
    {
        ConfigDir = Path.Combine(_dir, "config"), VllmStatusDir = Path.Combine(_dir, "status"), Url = "http://llamacpp:8080", ApiKey = FakeEngine.Key,
        Vllm = [new VllmEngineOptions { Name = "vllm", Host = host }],
    };

    private static LocalModel Model(string name, string devices = "0,1", int replicas = 1, bool sleep = true) => new()
    {
        Name = name, File = $"hf/org/{name}@abc", EngineKind = EngineKinds.Vllm, KvType = "fp8", Context = 65536, Parallel = 32, Devices = devices,
        Replicas = replicas, Sleep = sleep, GpuMemoryUtilization = 0.85,
    };

    [Fact]
    public void Its_arguments_say_what_the_model_chose_and_never_a_key_host_or_port()
    {
        var m = Model("flash");
        m.Quantization = "fp8";
        m.SpecDecode = "mtp";
        m.KvCpuGb = 64;
        m.Temperature = 0.6;
        var args = ModelCatalog.VllmArgs(m, "/library", 2);
        Assert.Equal("/library/hf/org/flash@abc", args[0]);
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Equal(("2", "65536", "32", "0.85", "fp8", "fp8"),
            (After("--tensor-parallel-size"), After("--max-model-len"), After("--max-num-seqs"), After("--gpu-memory-utilization"), After("--kv-cache-dtype"), After("--quantization")));
        Assert.Contains("--enable-sleep-mode", args);
        Assert.Contains("--enable-prefix-caching", args);
        Assert.Equal("priority", After("--scheduling-policy"));
        Assert.Equal("""{"method":"mtp","num_speculative_tokens":3}""", After("--speculative-config"));
        Assert.Contains("\"cpu_bytes_to_use\":68719476736", After("--kv-transfer-config"), StringComparison.Ordinal);
        // Its longest answer (half its 65,536 when none is set): what a request without max_tokens gets, and the most any gets.
        Assert.Equal("""{"max_new_tokens":32768,"temperature":0.6}""", After("--override-generation-config"));
        Assert.DoesNotContain(args, a => a is "--api-key" or "--host" or "--port" or "--served-model-name");
        var plain = ModelCatalog.VllmArgs(Model("x", sleep: false), "/library", 1);
        Assert.DoesNotContain("--enable-sleep-mode", plain);
        Assert.Equal("""{"max_new_tokens":32768}""", plain[plain.ToList().IndexOf("--override-generation-config") + 1]);
        m.MaxOutput = 8192;
        Assert.Contains("""{"max_new_tokens":8192,""", ModelCatalog.VllmArgs(m, "/library", 2).Single(a => a.Contains("max_new_tokens", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void Sglang_holds_every_request_to_the_models_longest_answer_by_its_own_limit()
    {
        var m = Model("swift");
        m.EngineKind = EngineKinds.Sglang;
        m.MaxOutput = 8192;
        var entry = Assert.Single(ModelCatalog.VllmEntries(m, "/library", new Dictionary<string, int>(), new HashSet<int>(), 8001, "/presets/sglang"));
        // SGLang's server-wide cap (SGLANG_MAX_NEW_TOKENS_LIMIT): a request without max_tokens gets it, a longer one is cut to it.
        Assert.Equal("8192", entry.Env!["SGLANG_MAX_NEW_TOKENS_LIMIT"]);
        Assert.Equal(entry.Env, VllmEntry.From(entry.ToJson())!.Env);
        // vLLM's is in its arguments.
        Assert.Null(Assert.Single(ModelCatalog.VllmEntries(Model("flash"), "/library", new Dictionary<string, int>(), new HashSet<int>(), 8001, "/presets/vllm")).Env);
    }

    [Fact]
    public void Its_kv_tier_is_vllms_own_cpu_tier_or_lmcache_with_ram_disk_and_a_shared_store()
    {
        var cpu = Model("flash");
        cpu.KvCpuGb = 32;
        var offloaded = ModelCatalog.VllmArgs(cpu, "/library", 1);
        Assert.Contains("OffloadingConnector", offloaded[offloaded.ToList().IndexOf("--kv-transfer-config") + 1], StringComparison.Ordinal);

        var lm = Model("flash", replicas: 2, devices: "0,1");
        lm.KvTier = "lmcache";
        lm.KvCpuGb = 64;
        lm.KvDiskGb = 500;
        var args = ModelCatalog.VllmArgs(lm, "/library", 1);
        Assert.Equal("""{"kv_connector":"LMCacheConnectorV1","kv_role":"kv_both"}""", args[args.ToList().IndexOf("--kv-transfer-config") + 1]);
        var entries = ModelCatalog.VllmEntries(lm, "/library", new Dictionary<string, int>(), new HashSet<int>(), 8001, "/presets/vllm");
        Assert.Equal(["/presets/vllm/lmcache-flash@0.yaml", "/presets/vllm/lmcache-flash@1.yaml"], entries.Select(e => e.Env!["LMCACHE_CONFIG_FILE"]));
        // Through the desired state's JSON and back.
        Assert.Equal(entries[0].Env, VllmEntry.From(entries[0].ToJson())!.Env);
        var yaml = ModelCatalog.LmcacheConfig(lm, "flash@0", "redis://valkey:6379");
        Assert.Contains("local_cpu: true\nmax_local_cpu_size: 64\n", yaml, StringComparison.Ordinal);
        Assert.Contains("local_disk: \"file:///kvcache/flash@0/\"\nmax_local_disk_size: 500\n", yaml, StringComparison.Ordinal);
        Assert.Contains("remote_url: \"redis://valkey:6379\"\n", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("remote_url", ModelCatalog.LmcacheConfig(lm, "flash@0", null), StringComparison.Ordinal);

        // A disk tier without LMCache, a tier vLLM has not, a tier on llama.cpp: refused.
        var bad = Model("x");
        bad.KvDiskGb = 10;
        Assert.Contains(ModelAdvisor.KindProblems(bad), p => p.Field == "kvDiskGb" && p.Error);
        bad.KvTier = "nvme";
        Assert.Contains(ModelAdvisor.KindProblems(bad), p => p.Field == "kvTier" && p.Error);
        Assert.Contains(ModelAdvisor.KindProblems(new LocalModel { Name = "l", File = "l.gguf", KvTier = "lmcache" }), p => p.Field == "kvCpuGb" && p.Error);
    }

    [Fact]
    public void An_admins_reload_and_restart_are_files_the_supervisor_follows_and_its_heartbeat_says_which_it_has_done()
    {
        var o = Options();
        var vllm = new FakeVllm(Path.Combine(o.ConfigDir, "vllm"), Path.Combine(o.VllmStatusDir, "vllm"));
        var backend = new VllmBackend(o.VllmEngines[0], o, new OneClient(vllm), TimeProvider.System);
        Assert.Null(backend.Heartbeat());
        vllm.Tick();
        Assert.Equal((0, 0), (backend.Heartbeat()!.Value.Restart, backend.Heartbeat()!.Value.Reload));
        var started = backend.Heartbeat()!.Value.Started;

        // Each ask raises its generation, in its own file beside the desired state: what is asked, by whom, when.
        var reload = backend.Ask(VllmBackend.ReloadFile, "reload", "ada\nwith=a line of its own");
        Assert.Equal((1, "reload", "ada with=a line of its own"), (reload.Gen, reload.Action, reload.By));
        Assert.Equal("gen=1\naction=reload\nby=ada with=a line of its own\n", string.Concat(File.ReadAllLines(Path.Combine(o.ConfigDir, "vllm", "reload")).Take(3).Select(l => l + "\n")));
        Assert.Equal(2, backend.Ask(VllmBackend.ReloadFile, "reload", "grace").Gen);
        Assert.Equal(("grace", 2), (backend.Asked(VllmBackend.ReloadFile)!.By, backend.Asked(VllmBackend.ReloadFile)!.Gen));
        Assert.Null(backend.Asked(VllmBackend.RestartFile));
        vllm.Tick();
        Assert.Equal((0, 2, started), (backend.Heartbeat()!.Value.Restart, backend.Heartbeat()!.Value.Reload, backend.Heartbeat()!.Value.Started));

        // A restart: the supervisor starts again.
        Assert.Equal(1, backend.Ask(VllmBackend.RestartFile, "restart", "ada").Gen);
        vllm.Tick();
        Assert.Equal(1, backend.Heartbeat()!.Value.Restart);
        Assert.True(backend.Heartbeat()!.Value.Started > started);
        Assert.Equal(2, vllm.Restarts);
    }

    [Fact]
    public void Copies_take_their_run_of_the_gpus_and_a_model_added_never_moves_another_s_port()
    {
        var two = ModelCatalog.VllmEntries(Model("flash", "0,1,2,3", replicas: 2), "/library", new Dictionary<string, int>(), new HashSet<int>(), 8001);
        Assert.Equal([("flash@0", 8001, "0,1"), ("flash@1", 8002, "2,3")], two.Select(e => (e.Name, e.Port, string.Join(',', e.Gpus))));
        Assert.All(two, e => Assert.Equal("2", e.Args[e.Args.ToList().IndexOf("--tensor-parallel-size") + 1]));
        // The ports it had, kept; a new one takes the lowest free.
        var taken = new HashSet<int>();
        var kept = ModelCatalog.VllmEntries(Model("oss", "2"), "/library", new Dictionary<string, int> { ["oss"] = 8005 }, taken, 8001);
        var added = ModelCatalog.VllmEntries(Model("new", "3"), "/library", new Dictionary<string, int>(), taken, 8001);
        Assert.Equal((8005, 8001), (kept[0].Port, added[0].Port));
    }

    [Fact]
    public async Task A_model_loads_sleeps_wakes_and_unloads_through_the_desired_state_and_its_status_says_so()
    {
        var o = Options();
        var vllm = new FakeVllm(Path.Combine(o.ConfigDir, "vllm"), Path.Combine(o.VllmStatusDir, "vllm")) { LoadTicks = 1 };
        var backend = new VllmBackend(o.VllmEngines[0], o, new OneClient(vllm), TimeProvider.System);
        backend.Write(VllmBackend.PresetsFile, [.. ModelCatalog.VllmEntries(Model("flash", "0,1,2,3", replicas: 2), "/library", new Dictionary<string, int>(), new HashSet<int>(), 8001),
            .. ModelCatalog.VllmEntries(Model("oss", "3", sleep: false), "/library", new Dictionary<string, int>(), new HashSet<int> { 8001, 8002 }, 8001)]);

        // No supervisor: every model unloaded, and loading one says why.
        Assert.All(await backend.ListAsync(default), m => Assert.Equal("unloaded", m.Status));
        Assert.Contains("not running", backend.Down(), StringComparison.Ordinal);
        Assert.Contains("not running", (await Assert.ThrowsAsync<EngineException>(() => backend.LoadAsync("flash", default))).Message, StringComparison.Ordinal);

        vllm.Tick();
        Assert.Null(backend.Down());
        await backend.LoadAsync("flash", default);
        Assert.Equal(["flash@0", "flash@1"], backend.Desired().Select(e => e.Name));
        Status(await backend.ListAsync(default), "flash", "loading");
        vllm.Tick();
        Status(await backend.ListAsync(default), "flash", "loading");
        vllm.Tick();
        Status(await backend.ListAsync(default), "flash", "loaded");
        Status(await backend.ListAsync(default), "oss", "unloaded");

        // Busy from its metrics, over both copies: 32 answers at once each, 5 running, 2 waiting.
        vllm.Busy[8001] = (3, 2);
        vllm.Busy[8002] = (2, 0);
        var c = (await backend.CapacityAsync("flash", TimeSpan.FromSeconds(5), default))!;
        Assert.Equal((64, 5, 2, 5), (c.Count, c.Running, c.Waiting, c.Busy.Count));
        Assert.Equal(0.25, c.KvUsage);
        Assert.Equal(0.75, c.PrefixHitRate);

        await backend.SleepAsync("flash", true, default);
        vllm.Tick();
        Status(await backend.ListAsync(default), "flash", "sleeping");
        await backend.SleepAsync("flash", false, default);
        vllm.Tick();
        Status(await backend.ListAsync(default), "flash", "loaded");
        Assert.Equal(["flash@0 sleep", "flash@1 sleep", "flash@0 wake", "flash@1 wake"], vllm.Sleeps);

        // One started without sleep mode does not sleep: it unloads instead (GpuShare).
        await backend.LoadAsync("oss", default);
        Assert.Contains("unloads instead", (await Assert.ThrowsAsync<EngineException>(() => backend.SleepAsync("oss", true, default))).Message, StringComparison.Ordinal);

        await backend.UnloadAsync("flash", default);
        Status(await backend.ListAsync(default), "flash", "unloading");
        vllm.Tick();
        Status(await backend.ListAsync(default), "flash", "unloaded");

        // A process that fails: its model failed.
        vllm.Broken.Add("oss");
        vllm.Tick();
        Status(await backend.ListAsync(default), "oss", "failed");
    }

    private static void Status(IReadOnlyList<EngineModel> models, string name, string status) => Assert.Equal(status, models.Single(m => m.Name == name).Status);

    [Fact]
    public async Task The_engines_as_one_send_each_model_to_its_own_and_a_vllm_engine_down_spoils_nothing_else()
    {
        var o = Options();
        var router = new FakeEngine();
        router.Reset(null);
        var vllm = new FakeVllm(Path.Combine(o.ConfigDir, "vllm"), Path.Combine(o.VllmStatusDir, "vllm"));
        var options = Microsoft.Extensions.Options.Options.Create(o);
        var backends = new VllmBackends(options, new OneClient(vllm), TimeProvider.System);
        backends.All[0].Write(VllmBackend.PresetsFile, ModelCatalog.VllmEntries(Model("flash"), "/library", new Dictionary<string, int>(), new HashSet<int>(), 8001));
        var engines = new EngineClient(new LlamaCppBackend(new OneClient(router), options, TimeProvider.System), backends, null!, options);

        var all = await engines.ModelsAsync();
        Assert.Equal([("Qwen3.8-Flash-Next", "loaded"), ("flash", "unloaded")], all.Select(m => (m.Name, m.Status)));
        Assert.Contains("not running", engines.Problems["vllm"], StringComparison.Ordinal);
        Assert.Equal((EngineKinds.LlamaCpp, EngineKinds.Vllm), (engines.KindOf("Qwen3.8-Flash-Next"), engines.KindOf("flash")));

        vllm.Tick();
        await engines.ModelsAsync();
        Assert.Empty(engines.Problems);
        await engines.LoadAsync("flash");
        Assert.Single(backends.All[0].Desired());
        Assert.Equal(0, router.LoadsOf("flash"));
        vllm.Tick();
        Assert.Equal("loaded", (await engines.ModelsAsync()).Single(m => m.Name == "flash").Status);
        var slots = await engines.SlotsAsync("flash", TimeSpan.FromSeconds(5));
        Assert.Equal(32, slots!.Value.Count);
        await engines.UnloadAsync("Qwen3.8-Flash-Next");
        Assert.Equal("unloaded", router.StatusOf("Qwen3.8-Flash-Next"));
        await Assert.ThrowsAsync<EngineException>(() => engines.SleepAsync("Qwen3.8-Flash-Next", true));

        // Without llama.cpp (vllm only), the vLLM models alone.
        var only = new EngineClient(new LlamaCppBackend(new OneClient(router), options, TimeProvider.System), backends, null!,
            Microsoft.Extensions.Options.Options.Create(new EngineOptions { ConfigDir = o.ConfigDir, VllmStatusDir = o.VllmStatusDir, LlamaCpp = false, Vllm = o.Vllm }));
        Assert.Equal(["flash"], (await only.ModelsAsync()).Select(m => m.Name));
    }

    [Fact]
    public void Sglangs_arguments_say_what_the_model_chose_in_its_own_flags_and_never_a_key_host_or_port()
    {
        var m = Model("swift");
        m.EngineKind = EngineKinds.Sglang;
        m.Quantization = "fp8";
        m.SpecDecode = "eagle3";
        m.DraftHead = "hf/org/swift-eagle@abc";
        m.DraftMax = 3;
        m.KvCpuGb = 64;
        m.TopP = 0.9;
        var args = ModelCatalog.ServerArgs(m, "/library", 2);
        string After(string flag) => args[args.ToList().IndexOf(flag) + 1];
        Assert.Equal(("/library/hf/org/swift@abc", "2", "65536", "32", "0.85", "fp8_e4m3", "fp8"),
            (After("--model-path"), After("--tp-size"), After("--context-length"), After("--max-running-requests"), After("--mem-fraction-static"), After("--kv-cache-dtype"),
                After("--quantization")));
        Assert.Contains("--enable-memory-saver", args);
        Assert.Contains("--enable-metrics", args);
        Assert.Contains("--enable-priority-scheduling", args);
        Assert.Contains("--schedule-low-priority-values-first", args);
        Assert.Equal(("EAGLE3", "3", "1", "4", "/library/hf/org/swift-eagle@abc"),
            (After("--speculative-algorithm"), After("--speculative-num-steps"), After("--speculative-eagle-topk"), After("--speculative-num-draft-tokens"),
                After("--speculative-draft-model-path")));
        Assert.Contains("--enable-hierarchical-cache", args);
        Assert.Equal("64", After("--hicache-size"));
        Assert.Equal("""{"top_p":0.9}""", After("--preferred-sampling-params"));
        // vLLM's own flags are not SGLang's.
        Assert.DoesNotContain(args, a => a is "--api-key" or "--host" or "--port" or "--served-model-name" or "--max-num-seqs" or "--enable-sleep-mode" or "--enable-prefix-caching");
        m.SpecDecode = "mtp";
        Assert.Equal("NEXTN", ModelCatalog.ServerArgs(m, "/library", 2).SkipWhile(a => a != "--speculative-algorithm").ElementAt(1));
        Assert.Equal("/library/hf/org/x@abc", ModelCatalog.ServerArgs(Model("x"), "/library", 1)[0]);
    }

    [Fact]
    public async Task An_sglang_engine_counts_its_totals_of_its_first_rank_and_sleeps_by_its_memory_saver()
    {
        var o = Options();
        o.Vllm = [new VllmEngineOptions { Name = "sglang", Kind = EngineKinds.Sglang, FirstPort = 8101 }];
        var m = Model("swift", "0,1");
        m.EngineKind = EngineKinds.Sglang;
        // SGLang as it writes them with priority scheduling: totals under priority="", one per priority, for each of two ranks.
        var text = new StringBuilder();
        foreach (var rank in new[] { "0", "1" })
        {
            foreach (var (name, total) in new[] { ("sglang:num_running_reqs", 6), ("sglang:num_queue_reqs", 2) })
            {
                text.Append(CultureInfo.InvariantCulture, $"{name}{{model_name=\"swift\",tp_rank=\"{rank}\",priority=\"\"}} {total}\n");
                text.Append(CultureInfo.InvariantCulture, $"{name}{{model_name=\"swift\",tp_rank=\"{rank}\",priority=\"-1\"}} {total}\n");
            }
            text.Append(CultureInfo.InvariantCulture, $"sglang:token_usage{{model_name=\"swift\",tp_rank=\"{rank}\",priority=\"\"}} 0.4\n");
            text.Append(CultureInfo.InvariantCulture, $"sglang:cache_hit_rate{{model_name=\"swift\",tp_rank=\"{rank}\",priority=\"\"}} 0.6\n");
            text.Append(CultureInfo.InvariantCulture, $"sglang:prompt_tokens_total{{model_name=\"swift\",tp_rank=\"{rank}\"}} 1000\n");
        }
        var backend = new VllmBackends(Microsoft.Extensions.Options.Options.Create(o), new OneClient(new Answer(text.ToString())), TimeProvider.System).All.Single();
        Assert.Equal((EngineKinds.Sglang, "SGLang"), (backend.Kind, backend.Label));
        backend.Write(VllmBackend.PresetsFile, ModelCatalog.VllmEntries(m, "/library", new Dictionary<string, int>(), new HashSet<int>(), backend.FirstPort));
        Assert.Contains("--enable-memory-saver", backend.Presets().Single().Args);
        Directory.CreateDirectory(backend.StatusDir);
        File.WriteAllText(Path.Combine(backend.StatusDir, "supervisor.json"), $$"""{"at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}""");
        await backend.LoadAsync("swift", default);
        var c = (await backend.CapacityAsync("swift", TimeSpan.FromSeconds(5), default))!;
        // Rank 0's totals alone: neither rank 1's nor the per-priority series count twice.
        Assert.Equal((32, 6, 2), (c.Count, c.Running, c.Waiting));
        Assert.Equal(0.4, c.KvUsage);
        Assert.Equal(0.6, c.PrefixHitRate!.Value, 6);
        await backend.SleepAsync("swift", true, default);
        Assert.True(backend.Desired().Single().Sleep);
        // Its own messages name it.
        File.Delete(Path.Combine(backend.StatusDir, "supervisor.json"));
        Assert.Contains("The SGLang engine sglang is not running", backend.Down(), StringComparison.Ordinal);
        Assert.Contains("COMPOSE_PROFILES=sglang", backend.Down(), StringComparison.Ordinal);
    }

    [Fact]
    public void Lmdeploys_and_stratas_arguments_are_their_own_and_never_a_key_host_or_port()
    {
        var m = Model("turbo");
        m.EngineKind = EngineKinds.Lmdeploy;
        var args = ModelCatalog.ServerArgs(m, "/library", 2);
        string After(IReadOnlyList<string> a, string flag) => a[a.ToList().IndexOf(flag) + 1];
        Assert.Equal("/library/hf/org/turbo@abc", args[0]);
        Assert.Equal(("2", "65536", "32", "0.85", "8"),
            (After(args, "--tp"), After(args, "--session-len"), After(args, "--max-batch-size"), After(args, "--cache-max-entry-count"), After(args, "--quant-policy")));
        Assert.Contains("--enable-prefix-caching", args);
        var strata = Model("flash");
        (strata.EngineKind, strata.File, strata.Parallel) = (EngineKinds.Strata, "unsloth/Flash-UD-IQ4_XS-00001-of-00003.gguf", 12);
        var s = ModelCatalog.ServerArgs(strata, "/library", 1);
        Assert.Equal("/library/unsloth/Flash-UD-IQ4_XS-00001-of-00003.gguf", s[0]);
        // Strata's batch is 1 to 8; its fp8 cache is its int8 (the same byte a value).
        Assert.Equal(("65536", "8", "int8"), (After(s, "--context"), After(s, "--parallel"), After(s, "--kv")));
        foreach (var a in new[] { args, s })
        {
            Assert.DoesNotContain(a, x => x is "--api-key" or "--api-keys" or "--host" or "--port" or "--server-port" or "--served-model-name" or "--enable-sleep-mode");
        }
    }

    [Fact]
    public async Task An_lmdeploy_engine_counts_its_own_metrics_and_a_strata_one_is_asked_with_its_key_for_prometheus_text()
    {
        var o = Options();
        o.Vllm = [new VllmEngineOptions { Name = "lmdeploy", Kind = EngineKinds.Lmdeploy }, new VllmEngineOptions { Name = "strata", Kind = EngineKinds.Strata }];
        var asked = new List<HttpRequestMessage>();
        var answer = new Asked(asked, """
            lmdeploy:num_requests_running{model_name="turbo"} 5
            lmdeploy:num_requests_waiting{model_name="turbo"} 1
            lmdeploy:gpu_cache_usage_perc{model_name="turbo"} 0.3
            lmdeploy:prefix_cache_hit_rate{model_name="turbo"} 0.5
            lmdeploy:prompt_tokens_total{model_name="turbo"} 100
            vllm:num_requests_running{model_name="flash"} 2
            vllm:num_requests_waiting{model_name="flash"} 0
            vllm:kv_cache_usage_perc{model_name="flash"} 0.7
            """);
        var all = new VllmBackends(Microsoft.Extensions.Options.Options.Create(o), new OneClient(answer), TimeProvider.System);
        Assert.Equal([(EngineKinds.Lmdeploy, "LMDeploy"), (EngineKinds.Strata, "Strata")], all.All.Select(b => (b.Kind, b.Label)));
        var turbo = Model("turbo", "0", sleep: false);
        turbo.EngineKind = EngineKinds.Lmdeploy;
        var flash = Model("flash", "1", sleep: false);
        (flash.EngineKind, flash.File, flash.Parallel) = (EngineKinds.Strata, "q/flash.gguf", 4);
        foreach (var (b, m) in new[] { (all.All[0], turbo), (all.All[1], flash) })
        {
            b.Write(VllmBackend.PresetsFile, ModelCatalog.VllmEntries(m, "/library", new Dictionary<string, int>(), new HashSet<int>(), b.FirstPort));
            Directory.CreateDirectory(b.StatusDir);
            File.WriteAllText(Path.Combine(b.StatusDir, "supervisor.json"), $$"""{"at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}""");
            await b.LoadAsync(m.Name, default);
        }
        var lmdeploy = (await all.All[0].CapacityAsync("turbo", TimeSpan.FromSeconds(5), default))!;
        Assert.Equal((32, 5, 1, 0.3), (lmdeploy.Count, lmdeploy.Running, lmdeploy.Waiting, lmdeploy.KvUsage));
        Assert.Equal(0.5, lmdeploy.PrefixHitRate!.Value, 6);
        var strata = (await all.All[1].CapacityAsync("flash", TimeSpan.FromSeconds(5), default))!;
        Assert.Equal((4, 2, 0, 0.7), (strata.Count, strata.Running, strata.Waiting, strata.KvUsage));
        // Strata answers its metrics as Prometheus text only when asked for it, and only with the key.
        Assert.Null(asked[0].Headers.Authorization);
        Assert.Equal(("Bearer", FakeEngine.Key), (asked[1].Headers.Authorization!.Scheme, asked[1].Headers.Authorization!.Parameter));
        Assert.Contains("text/plain", asked[1].Headers.Accept.ToString(), StringComparison.Ordinal);
        // Neither is put to sleep by the app.
        await Assert.ThrowsAsync<EngineException>(() => all.All[0].SleepAsync("turbo", true, default));
    }

    /// <summary>Answers every request with the same Prometheus text, keeping what was asked.</summary>
    private sealed class Asked(List<HttpRequestMessage> asked, string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            asked.Add(request);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(text) });
        }
    }

    [Fact]
    public void This_stacks_engines_are_one_of_each_kind_and_a_model_goes_to_the_first_of_its_kind_or_the_one_it_names()
    {
        var o = new EngineOptions();
        Assert.Equal([("vllm", EngineKinds.Vllm), ("sglang", EngineKinds.Sglang), ("lmdeploy", EngineKinds.Lmdeploy), ("strata", EngineKinds.Strata)],
            o.VllmEngines.Select(e => (e.Name, e.Kind)));
        o.Vllm = [new VllmEngineOptions { Name = "vllm" }, new VllmEngineOptions { Name = "sglang-a", Kind = EngineKinds.Sglang }, new VllmEngineOptions { Name = "sglang-b", Kind = EngineKinds.Sglang }];
        var all = new VllmBackends(Microsoft.Extensions.Options.Options.Create(o), new OneClient(new Answer("")), TimeProvider.System);
        Assert.Equal("sglang-a", all.For(null, EngineKinds.Sglang)!.Name);
        Assert.Equal("sglang-b", all.For("sglang-b", EngineKinds.Sglang)!.Name);
        Assert.Equal("vllm", all.For(null, EngineKinds.Vllm)!.Name);
        // A model never runs on an engine of another kind.
        Assert.Null(all.For("vllm", EngineKinds.Sglang));
        Assert.Equal(2, all.OfKind(EngineKinds.Sglang).Count());
    }

    /// <summary>Answers every request with the same Prometheus text.</summary>
    private sealed class Answer(string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(text) });
    }

    [Fact]
    public void Prometheus_text_is_summed_by_metric()
    {
        var m = VllmBackend.Metrics("# HELP x\nvllm:num_requests_running{a=\"1\"} 2\nvllm:num_requests_running{a=\"2\"} 3.0\nvllm:kv_cache_usage_perc 0.5\nbad line\n");
        Assert.Equal(5, m["vllm:num_requests_running"]);
        Assert.Equal(0.5, m["vllm:kv_cache_usage_perc"]);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }

    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppFixture.DashboardsPath, "..", "..", "..", ".."));

    [Theory]
    [Trait("Control", "IAM-08")]
    [InlineData(EngineKinds.Vllm)]
    [InlineData(EngineKinds.Sglang)]
    [InlineData(EngineKinds.Lmdeploy)]
    [InlineData(EngineKinds.Strata)]
    public async Task The_real_supervisor_runs_the_fake_vllm_as_the_app_drives_it_in_a_container(string kind)
    {
        var port = FreePort();
        byte[] From(params string[] path) => File.ReadAllBytes(Path.Combine([Repo, .. path]));
        // The python image the stack already has; on the host's network, so the model's port is the same here. SGLang runs
        // through its launcher (services/sglang/serve.py), the fake sglang package answering, as fake-gpu.yml runs it.
        await using var container = new ContainerBuilder("python:3.13-slim")
            .WithResourceMapping(From("deploy", "services", "vllm", "supervisor.sh"), "/vllm/supervisor.sh")
            .WithResourceMapping(From("deploy", "services", "vllm", "vllm-helper.py"), "/vllm/vllm-helper.py")
            .WithResourceMapping(From("deploy", "services", "gpu-probe.py"), "/gpu-probe.py")
            .WithResourceMapping(From("tools", "fake-vllm", "fake_vllm.py"), "/fake-vllm/fake_vllm.py")
            .WithResourceMapping(From("tools", "fake-vllm", "vllm"), "/fake-vllm/vllm")
            .WithResourceMapping(From("deploy", "services", "sglang", "serve.py"), "/sglang/serve.py")
            .WithResourceMapping(From("tools", "fake-vllm", "sglang-module", "sglang", "__init__.py"), "/fake-vllm/sglang-module/sglang/__init__.py")
            .WithResourceMapping(From("tools", "fake-vllm", "sglang-module", "sglang", "launch_server.py"), "/fake-vllm/sglang-module/sglang/launch_server.py")
            // LMDeploy through its launcher and the fake lmdeploy package; Strata through its launcher and the fake strata command.
            .WithResourceMapping(From("deploy", "services", "lmdeploy", "serve.py"), "/lmdeploy/serve.py")
            .WithResourceMapping(From("tools", "fake-vllm", "lmdeploy-module", "lmdeploy", "__init__.py"), "/fake-vllm/lmdeploy-module/lmdeploy/__init__.py")
            .WithResourceMapping(From("tools", "fake-vllm", "lmdeploy-module", "lmdeploy", "cli", "__init__.py"), "/fake-vllm/lmdeploy-module/lmdeploy/cli/__init__.py")
            .WithResourceMapping(From("tools", "fake-vllm", "lmdeploy-module", "lmdeploy", "cli", "entrypoint.py"), "/fake-vllm/lmdeploy-module/lmdeploy/cli/entrypoint.py")
            .WithResourceMapping(From("deploy", "services", "strata", "serve.py"), "/strata/serve.py")
            .WithResourceMapping(From("tools", "fake-vllm", "strata"), "/fake-vllm/strata")
            .WithResourceMapping(Encoding.UTF8.GetBytes("GGUF"), "/library/flash/flash.gguf")
            .WithEnvironment("ENGINE_KEY", "key-for-the-container")
            .WithEnvironment("ENGINE_SERVER", kind)
            .WithEnvironment("VLLM_PRESETS", "/presets")
            .WithEnvironment("VLLM_STATUS", "/status")
            .WithEnvironment("VLLM_BIN", kind switch
            {
                EngineKinds.Sglang => "env PYTHONPATH=/fake-vllm/sglang-module python3 /sglang/serve.py",
                EngineKinds.Lmdeploy => "env PYTHONPATH=/fake-vllm/lmdeploy-module python3 /lmdeploy/serve.py",
                EngineKinds.Strata => "python3 /strata/serve.py",
                _ => "/fake-vllm/vllm",
            })
            .WithEnvironment("STRATA_CLI", "/fake-vllm/strata")
            .WithEnvironment("VLLM_TICK", "0.3")
            .WithCreateParameterModifier(p => p.HostConfig!.NetworkMode = "host")
            .WithEntrypoint("/bin/sh", "-c", "chmod +x /fake-vllm/vllm /fake-vllm/strata && exec /bin/sh /vllm/supervisor.sh")
            .Build();
        await container.StartAsync();

        var o = Options("127.0.0.1");
        o.Vllm = [new VllmEngineOptions { Name = kind, Kind = kind, Host = "127.0.0.1" }];
        var status = Path.Combine(o.VllmStatusDir, kind);
        Directory.CreateDirectory(status);
        var backend = new VllmBackend(o.VllmEngines[0], o, new HttpClientFactoryOfOne(), TimeProvider.System);
        var model = Model("flash", "0", sleep: EngineKinds.Of(kind)!.Sleep);
        model.EngineKind = kind;
        if (kind == EngineKinds.Strata)
        {
            // Strata runs a GGUF file, 1 to 8 at once.
            (model.File, model.Parallel) = ("flash/flash.gguf", 8);
        }
        var entries = ModelCatalog.VllmEntries(model, "/library", new Dictionary<string, int>(), new HashSet<int>(), port);
        backend.Write(VllmBackend.PresetsFile, entries);

        // What the app writes reaches the container, and what the supervisor writes comes back (a shared volume in a stack).
        // As the app writes it: whole, renamed into place, and only when it changes (a copy is written in place, and the
        // supervisor may read it half written).
        var copied = "";
        async Task SyncAsync()
        {
            var desired = Path.Combine(backend.ConfigDir, VllmBackend.DesiredFile);
            if (File.Exists(desired) && await File.ReadAllTextAsync(desired) is var text && text != copied)
            {
                await container.CopyAsync(Encoding.UTF8.GetBytes(text), "/presets/models.json.tmp");
                Assert.Equal(0, (await container.ExecAsync(["mv", "-f", "/presets/models.json.tmp", "/presets/models.json"])).ExitCode);
                copied = text;
            }
            foreach (var file in new[] { "supervisor.json", "flash.json" })
            {
                try
                {
                    await File.WriteAllBytesAsync(Path.Combine(status, file), await container.ReadFileAsync("/status/" + file));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    File.Delete(Path.Combine(status, file));
                }
            }
        }
        async Task UntilAsync(string want)
        {
            for (var i = 0; i < 100; i++)
            {
                await SyncAsync();
                if ((await backend.ListAsync(default)).Single().Status == want)
                {
                    return;
                }
                await Task.Delay(200);
            }
            Assert.Fail($"flash never {want}: {(await container.GetLogsAsync()).Stdout}");
        }

        await UntilAsync("unloaded");
        // "unloaded" is also what a model reads before the supervisor has said anything: its heartbeat back first.
        for (var i = 0; i < 100 && backend.Down() is not null; i++)
        {
            await SyncAsync();
            await Task.Delay(200);
        }
        Assert.Null(backend.Down());
        await backend.LoadAsync("flash", default);
        await UntilAsync("loaded");
        var c = await backend.CapacityAsync("flash", TimeSpan.FromSeconds(5), default);
        Assert.Equal((model.Parallel, 0, 0), (c!.Count, c.Running, c.Waiting));
        if (model.Sleep)
        {
            await backend.SleepAsync("flash", true, default);
            await UntilAsync("sleeping");
            await backend.SleepAsync("flash", false, default);
            await UntilAsync("loaded");
        }
        await backend.UnloadAsync("flash", default);
        await UntilAsync("unloaded");
        // The key went by the environment: never on the server's command line.
        Assert.DoesNotContain("key-for-the-container", (await container.ExecAsync(["sh", "-c", "cat /proc/[0-9]*/cmdline 2>/dev/null | tr '\\0' ' '"])).Stdout, StringComparison.Ordinal);
    }

    /// <summary>A real HTTP client, for a server on this machine's network.</summary>
    private sealed class HttpClientFactoryOfOne : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
