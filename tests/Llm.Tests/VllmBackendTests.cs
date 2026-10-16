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
        Assert.Equal("""{"method":"mtp","num_speculative_tokens":3}""", After("--speculative-config"));
        Assert.Contains("\"cpu_bytes_to_use\":68719476736", After("--kv-transfer-config"), StringComparison.Ordinal);
        Assert.Equal("""{"temperature":0.6}""", After("--override-generation-config"));
        Assert.DoesNotContain(args, a => a is "--api-key" or "--host" or "--port" or "--served-model-name");
        Assert.DoesNotContain("--enable-sleep-mode", ModelCatalog.VllmArgs(Model("x", sleep: false), "/library", 1));
    }

    [Fact]
    public void Its_kv_tier_is_vllms_own_cpu_tier_or_lmcache_with_ram_disk_and_a_shared_store()
    {
        var cpu = Model("flash");
        cpu.KvCpuGb = 32;
        Assert.Contains("OffloadingConnector", ModelCatalog.VllmArgs(cpu, "/library", 1)[^1], StringComparison.Ordinal);

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

    [Fact]
    public async Task The_real_supervisor_runs_the_fake_vllm_as_the_app_drives_it_in_a_container()
    {
        var port = FreePort();
        byte[] From(params string[] path) => File.ReadAllBytes(Path.Combine([Repo, .. path]));
        // The python image the stack already has; on the host's network, so the model's port is the same here.
        await using var container = new ContainerBuilder("python:3.13-slim")
            .WithResourceMapping(From("deploy", "services", "vllm", "supervisor.sh"), "/vllm/supervisor.sh")
            .WithResourceMapping(From("deploy", "services", "vllm", "vllm-helper.py"), "/vllm/vllm-helper.py")
            .WithResourceMapping(From("deploy", "services", "gpu-probe.py"), "/gpu-probe.py")
            .WithResourceMapping(From("tools", "fake-vllm", "fake_vllm.py"), "/fake-vllm/fake_vllm.py")
            .WithResourceMapping(From("tools", "fake-vllm", "vllm"), "/fake-vllm/vllm")
            .WithEnvironment("ENGINE_KEY", "key-for-the-container")
            .WithEnvironment("VLLM_PRESETS", "/presets")
            .WithEnvironment("VLLM_STATUS", "/status")
            .WithEnvironment("VLLM_BIN", "/fake-vllm/vllm")
            .WithEnvironment("VLLM_TICK", "0.3")
            .WithCreateParameterModifier(p => p.HostConfig!.NetworkMode = "host")
            .WithEntrypoint("/bin/sh", "-c", "chmod +x /fake-vllm/vllm && exec /bin/sh /vllm/supervisor.sh")
            .Build();
        await container.StartAsync();

        var o = Options("127.0.0.1");
        var status = Path.Combine(o.VllmStatusDir, "vllm");
        Directory.CreateDirectory(status);
        var backend = new VllmBackend(o.VllmEngines[0], o, new HttpClientFactoryOfOne(), TimeProvider.System);
        var entries = ModelCatalog.VllmEntries(Model("flash", "0"), "/library", new Dictionary<string, int>(), new HashSet<int>(), port);
        backend.Write(VllmBackend.PresetsFile, entries);

        // What the app writes reaches the container, and what the supervisor writes comes back (a shared volume in a stack).
        async Task SyncAsync()
        {
            var desired = Path.Combine(backend.ConfigDir, VllmBackend.DesiredFile);
            if (File.Exists(desired))
            {
                await container.CopyAsync(await File.ReadAllBytesAsync(desired), "/presets/models.json");
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
        Assert.Null(backend.Down());
        await backend.LoadAsync("flash", default);
        await UntilAsync("loaded");
        var c = await backend.CapacityAsync("flash", TimeSpan.FromSeconds(5), default);
        Assert.Equal((32, 0, 0), (c!.Count, c.Running, c.Waiting));
        await backend.SleepAsync("flash", true, default);
        await UntilAsync("sleeping");
        await backend.SleepAsync("flash", false, default);
        await UntilAsync("loaded");
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
