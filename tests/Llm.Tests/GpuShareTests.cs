using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// One GPU for the chat engine and the picture and video servers (GpuShare): a video whose model is short of memory gets
/// room (the idle chat model not kept loaded unloads), its server starts again to decode on the GPU, no chat model loads
/// while it runs (a chat request waits and says why, the watcher loads none, an admin's Load is refused), the room goes
/// back after it (the video model unloads, the chat model loads again), and picture and video jobs take turns.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class GpuShareTests(AppFixture app) : IDisposable
{
    private const string Chat = "Qwen3.8-Flash-Next";
    private const string Video = MediaModels.VideoModel;
    private const string Picture = MediaModels.ImageModel;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    private readonly string _dir = Directory.CreateTempSubdirectory("gpu-share-").FullName;

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

    /// <summary>
    /// services/sd-serve.sh and its sd-server for both servers, as far as the app sees them: every few hundred milliseconds it
    /// reads the control file, runs the generation it names (deciding where it decodes as it starts, by the memory free
    /// then), writes its status, and answers /v1/models while it runs. The GPU has 24,000 MB; the chat model takes 20,500.
    /// </summary>
    private sealed class FakeServe(string control, string status, FakeEngine engine) : HttpMessageHandler
    {
        private sealed record Run(string Gen, string Phase, string Model, string Vae);

        private readonly ConcurrentDictionary<string, Run> _runs = new(StringComparer.Ordinal);

        /// <summary>The generations each server started, in order, with where each decoded.</summary>
        public ConcurrentQueue<string> Started { get; } = new();

        public long Free => FreeOf?.Invoke() ?? 24_000 - (engine.StatusOf(Chat) == "loaded" ? 20_500 : 0);

        /// <summary>The media GPU's free memory, when a test's machine says it otherwise.</summary>
        public Func<long>? FreeOf { get; set; }

        public string? PhaseOf(string server) => _runs.TryGetValue(server, out var r) ? r.Phase : null;

        public void Tick()
        {
            foreach (var server in MediaControl.FileServers)
            {
                var path = Path.Combine(control, server + ".conf");
                var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
                string Get(string key) => lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? "";
                var was = _runs.GetValueOrDefault(server) ?? new Run("", "off", "", "");
                Run now;
                if (Get("want") == "on")
                {
                    if (was.Phase == "running" && was.Gen == Get("gen"))
                    {
                        now = was;
                    }
                    else
                    {
                        var need = Get("vae_gpu_mb");
                        var vae = need.Length == 0 ? "" : Free >= long.Parse(need, CultureInfo.InvariantCulture) ? "gpu" : "cpu";
                        now = new Run(Get("gen"), "running", Get("model"), vae);
                        Started.Enqueue($"{server} {now.Gen} {now.Model} {vae}");
                    }
                }
                else
                {
                    now = was with { Phase = "off" };
                }
                _runs[server] = now;
                Directory.CreateDirectory(status);
                var json = $$"""{"gen":"{{now.Gen}}","phase":"{{now.Phase}}","since":0,"at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"free_mb":{{Free}},"exit":null,"missing":[],"vae":"{{now.Vae}}","model":"{{now.Model}}"}""";
                File.WriteAllText(Path.Combine(status, server + ".json.tmp"), json);
                File.Move(Path.Combine(status, server + ".json.tmp"), Path.Combine(status, server + ".json"), overwrite: true);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { Port: 1234, AbsolutePath: "/v1/models" } uri && PhaseOf(uri.Host) == "running")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") });
            }
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"Connection refused ({request.RequestUri?.Authority})");
        }
    }

    private sealed record Setup(WebApplicationFactory<Program> App, FakeServe Serve, CancellationTokenSource Running, Task Ticking) : IAsyncDisposable
    {
        public GpuShare Gpu => App.Services.GetRequiredService<GpuShare>();
        public MediaControl Media => App.Services.GetRequiredService<MediaControl>();
        public EngineState State => App.Services.GetRequiredService<EngineState>();
        public EngineRoute Route => App.Services.GetRequiredService<EngineRoute>();

        public async ValueTask DisposeAsync()
        {
            await Running.CancelAsync();
            await Ticking;
            await App.DisposeAsync();
            Running.Dispose();
        }
    }

    /// <summary>An app with the engine (the chat model loaded, not kept: the one new chats use), both media servers, and their script.</summary>
    private async Task<Setup> NewAsync(IDictionary<string, string?>? more = null, Action<IServiceCollection>? services = null)
    {
        var config = Path.Combine(_dir, "config");
        var status = Path.Combine(_dir, "status");
        Directory.CreateDirectory(config);
        app.Engine.Reset(Path.Combine(config, "models.ini"));
        app.Engine.Max = 2;
        var serve = new FakeServe(config, status, app.Engine);
        var settings = new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = config, ["Engine:LibraryDir"] = Path.Combine(_dir, "library"),
            ["Engine:MediaStatusDir"] = status, ["Engine:ModelsMax"] = "2", ["Modules:imagegen"] = "true", ["Modules:videogen"] = "true", ["Replicas:Enabled"] = "false",
        };
        foreach (var (k, v) in more ?? new Dictionary<string, string?>())
        {
            settings[k] = v;
        }
        var f = app.Create(app.ConnectionStringFor("gpushare_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings, s =>
        {
            s.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => serve);
            s.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => serve);
            services?.Invoke(s);
        });
        var running = new CancellationTokenSource();
        var ticking = Task.Run(async () =>
        {
            while (!running.IsCancellationRequested)
            {
                serve.Tick();
                try
                {
                    await Task.Delay(200, running.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });
        var s = new Setup(f, serve, running, ticking);
        // The engine's watcher has looked: the chat model is loaded, and the one new chats use.
        await UntilAsync(() => s.State.StatusOf(Chat) == "loaded" && s.State.Default == Chat, "the watcher sees the chat model loaded");
        return s;
    }

    private static async Task UntilAsync(Func<bool> done, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds} s: {what}");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task A_video_short_of_memory_gets_room_holds_it_while_it_runs_and_gives_it_back()
    {
        await using var s = await NewAsync();
        var media = s.Media;
        var video = media.Find(Video)!;
        using var deadline = new CancellationTokenSource(Deadline);

        // The video model decodes on the GPU with 13,312 MB free; the chat model leaves 3,500.
        Assert.Equal(13312, GpuShare.NeedMb(video.Row));
        await UntilAsync(() => media.StatusOf("videogen") is { FreeMb: 3500 }, "the video server's script says 3,500 MB are free");
        // The answer that asked for the video holds its place in the chat model's line while its tool runs: the model is idle all the same.
        using var answer = await s.App.Services.GetRequiredService<AnswerGate>().EnterAsync(Guid.NewGuid(), Chat, _ => Task.CompletedTask, deadline.Token);
        var hold = await s.Gpu.HoldAsync(video, deadline.Token);
        Assert.Equal([Chat], hold.Unloaded);
        Assert.Equal("unloaded", app.Engine.StatusOf(Chat));
        Assert.Equal((Video, "video"), (s.State.Lent?.Media, s.State.Lent?.Kind));
        Assert.Null(await media.ReadyAsync(Video, deadline.Token));
        // Its server started with the room made: it decodes on the GPU.
        Assert.Contains(s.Serve.Started, x => x.StartsWith("videogen ", StringComparison.Ordinal) && x.EndsWith($" {Video} gpu", StringComparison.Ordinal));
        Assert.Contains(s.Gpu.Recent, h => h.Text == $"Made room for {Video} (a video): unloaded {Chat}.");

        // Meanwhile a chat request for the chat model waits, and says why; the watcher does not load it; an admin's Load is refused.
        var waits = new ConcurrentQueue<string>();
        var take = Task.Run(async () =>
        {
            EngineRoute.Waiting.Value = why =>
            {
                waits.Enqueue(why);
                return Task.CompletedTask;
            };
            return await s.Route.TakeAsync(Chat, null, deadline.Token);
        });
        await Task.Delay(3000);
        Assert.False(take.IsCompleted, "a chat request went on while the GPU was lent to the video");
        Assert.Equal($"The GPU is making a video with {Video} just now: {Chat} loads again as soon as it is done.", Assert.Single(waits));
        Assert.Equal(0, app.Engine.LoadsOf(Chat));
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var refused = await admin.PostAsync($"/api/admin/models/{Chat}/load");
        await StatusAssert.Is(HttpStatusCode.Conflict, refused);
        Assert.Contains("The GPU is making a video", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var page = await admin.JsonAsync(await admin.GetAsync("/api/admin/models"));
        Assert.Equal(Video, page.GetProperty("gpu").GetProperty("lent").GetProperty("media").GetString());

        // Done: the video model unloads (it is not kept loaded), then the chat model loads again; the request goes on.
        await hold.DisposeAsync();
        Assert.Null(s.State.Lent);
        Assert.Equal("off", s.Serve.PhaseOf("videogen"));
        Assert.True(app.Engine.LoadsOf(Chat) >= 1, "the chat model was not loaded again");
        Assert.Equal("loaded", app.Engine.StatusOf(Chat));
        using var sending = await take.WaitAsync(Deadline);
        Assert.Contains(s.Gpu.Recent, h => h.Text == $"Gave the room back after {Video}: loaded {Chat} again.");
        var recent = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("gpu").GetProperty("recent").EnumerateArray()
            .Select(r => r.GetProperty("text").GetString()).ToList();
        Assert.Equal($"Gave the room back after {Video}: loaded {Chat} again.", recent[0]);
    }

    [Fact]
    public async Task Studio_work_short_of_memory_gets_room_and_the_studio_unloads_its_model_before_the_room_goes_back()
    {
        await using var s = await NewAsync();
        using var deadline = new CancellationTokenSource(Deadline);
        // The studio server says its free memory itself: 3,500 MB while the chat model is loaded.
        Task<long?> Free(CancellationToken _) => Task.FromResult<long?>(app.Engine.StatusOf(Chat) == "unloaded" ? 20000 : 3500);
        var released = 0;
        var hold = await s.Gpu.HoldStudioAsync("Chatterbox", 4500, Free, () =>
        {
            released++;
            return Task.CompletedTask;
        }, deadline.Token);
        Assert.Equal([Chat], hold.Unloaded);
        Assert.Equal(("Chatterbox", "studio piece"), (s.State.Lent?.Media, s.State.Lent?.Kind));
        Assert.Contains(s.Gpu.Recent, h => h.Text == $"Made room for Chatterbox (a studio piece): unloaded {Chat}.");

        await hold.DisposeAsync();
        Assert.Equal(1, released);
        Assert.Null(s.State.Lent);
        Assert.Equal("loaded", app.Engine.StatusOf(Chat));

        // With the room it needs already free: its turn only.
        await using (var quick = await s.Gpu.HoldStudioAsync("BiRefNet-lite", 1800, _ => Task.FromResult<long?>(20000), () => Task.CompletedTask, deadline.Token))
        {
            Assert.Empty(quick.Unloaded);
            Assert.Null(s.State.Lent);
        }
    }

    [Fact]
    public async Task A_video_server_decoding_on_the_cpu_is_started_again_on_the_gpu_once_room_is_made()
    {
        await using var s = await NewAsync();
        var media = s.Media;
        using var deadline = new CancellationTokenSource(Deadline);
        // Loaded beside the chat model, as before this: its server decides on the CPU.
        media.Load(Video);
        await UntilAsync(() => s.Serve.Started.Any(x => x.EndsWith($" {Video} cpu", StringComparison.Ordinal)), "the video server runs, decoding on the CPU");
        var started = s.Serve.Started.Count;

        await using (var hold = await s.Gpu.HoldAsync(media.Find(Video)!, deadline.Token))
        {
            Assert.Equal([Chat], hold.Unloaded);
            Assert.Null(await media.ReadyAsync(Video, deadline.Token));
            // A new generation, which its script started with the room made.
            Assert.EndsWith($" {Video} gpu", s.Serve.Started.Skip(started).Last(), StringComparison.Ordinal);
        }
        Assert.Equal("loaded", app.Engine.StatusOf(Chat));
    }

    [Fact]
    public async Task A_video_stopped_while_room_is_made_gives_back_what_was_unloaded_for_it()
    {
        await using var s = await NewAsync();
        // A second chat model, loaded and answering an API key's request all along (its slot busy): it cannot make room.
        await using (var scope = s.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            db.LocalModels.Add(new Llm.Core.Models.LocalModel { Name = "tiny", File = "tiny/Tiny.gguf", Context = 8192 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ModelCatalog>().WritePresetsAsync();
        }
        await UntilAsync(() => app.Engine.StatusOf("tiny") == "unloaded", "the engine lists tiny");
        await s.App.Services.GetRequiredService<EngineClient>().LoadAsync("tiny");
        app.Engine.BusySlots["tiny"] = [0];
        await UntilAsync(() => s.State.StatusOf("tiny") == "loaded" && s.State.StatusOf(Chat) == "loaded", "the watcher sees both loaded");
        await UntilAsync(() => s.Media.StatusOf("videogen") is { FreeMb: 3500 }, "the video server's script says 3,500 MB are free");

        // The person stops the answer once the idle chat model has unloaded, while the busy one is still waited for.
        using var stop = new CancellationTokenSource(Deadline);
        var holding = s.Gpu.HoldAsync(s.Media.Find(Video)!, stop.Token);
        await UntilAsync(() => app.Engine.StatusOf(Chat) == "unloaded", "the idle chat model unloads for the video");
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => holding);

        // What was unloaded comes back; the GPU is the chat's again.
        Assert.Contains(s.Gpu.Recent, h => h.Text == $"Gave the room back after {Video}: loaded {Chat} again.");
        Assert.Null(s.State.Lent);
        Assert.Equal("loaded", app.Engine.StatusOf(Chat));
        Assert.Equal("loaded", app.Engine.StatusOf("tiny"));
    }

    [Fact]
    public void A_chat_model_that_fits_itself_to_the_gpu_leaves_room_for_the_picture_model_kept_loaded()
    {
        static string Preset(string? devices = null, string placement = "auto", string? extra = null, (int?, int)? room = null) =>
            ModelCatalog.Preset(new Llm.Core.Models.LocalModel { Name = "a", File = "a/A.gguf", Placement = placement, Devices = devices, ExtraPreset = extra },
                "/library", pictureRoom: room ?? (null, 3072));

        // llama.cpp's own margin (1024 MiB) and FLUX's 3,072 MB on its GPU.
        Assert.Contains("fit = on\nfit-target = 4096\n", Preset(), StringComparison.Ordinal);
        Assert.Contains("fit-target = 4096,1024\n", Preset("0,1"), StringComparison.Ordinal);
        Assert.Contains("fit-target = 1024,4096\n", Preset("0,1", room: (1, 3072)), StringComparison.Ordinal);
        // Not on the picture model's GPU, placed by hand, or set by the admin's own lines: llama.cpp's own, or theirs.
        Assert.DoesNotContain("fit-target", Preset("1"), StringComparison.Ordinal);
        Assert.DoesNotContain("fit-target", Preset(placement: "manual"), StringComparison.Ordinal);
        Assert.Single(Preset(extra: "fit-target = 2048").Split('\n'), l => l.StartsWith("fit-target", StringComparison.Ordinal));
        Assert.DoesNotContain("fit-target", ModelCatalog.Preset(new Llm.Core.Models.LocalModel { Name = "a", File = "a/A.gguf" }, "/library"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_engines_presets_leave_room_for_the_picture_model_while_it_is_kept_loaded()
    {
        await using var s = await NewAsync();
        await using var scope = s.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        db.LocalModels.Add(new Llm.Core.Models.LocalModel { Name = "Flash", File = "flash/Flash.gguf", Context = 8192 });
        await db.SaveChangesAsync();
        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        var presets = Path.Combine(_dir, "config", "models.ini");

        // FLUX is kept loaded (as installed): 3,072 MB beside llama.cpp's margin.
        await catalog.WritePresetsAsync();
        Assert.Contains("fit-target = 4096\n", await File.ReadAllTextAsync(presets), StringComparison.Ordinal);
        await s.Media.SetKeptAsync(Picture, false);
        await catalog.WritePresetsAsync();
        Assert.DoesNotContain("fit-target", await File.ReadAllTextAsync(presets), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_picture_server_running_its_model_gets_room_when_a_chat_model_loaded_since_left_it_too_little()
    {
        await using var s = await NewAsync();
        var media = s.Media;
        using var deadline = new CancellationTokenSource(Deadline);
        media.Load(Picture);
        await UntilAsync(() => media.StatusOf("imagegen") is { Phase: "running" } st && st.Model == Picture, "the picture server runs its model");
        // A bigger chat model loaded since (asked for by a drawing's planning): 1,500 MB free, too little to compute in.
        s.Serve.FreeOf = () => app.Engine.StatusOf(Chat) == "loaded" ? 1_500 : 20_000;
        await UntilAsync(() => media.StatusOf("imagegen") is { FreeMb: 1_500 }, "the picture server's script says 1,500 MB are free");

        await using (var hold = await s.Gpu.HoldAsync(media.Find(Picture)!, deadline.Token))
        {
            Assert.Equal([Chat], hold.Unloaded);
        }
        Assert.Equal("loaded", app.Engine.StatusOf(Chat));
    }

    [Fact]
    public async Task Picture_and_video_jobs_take_turns_and_a_server_does_not_switch_models_under_a_job()
    {
        await using var s = await NewAsync();
        var media = s.Media;
        using var deadline = new CancellationTokenSource(Deadline);
        await UntilAsync(() => media.StatusOf("imagegen") is { FreeMb: 3500 }, "the picture server's script says 3,500 MB are free");

        // A picture fits beside the chat model (3,072 MB): no room is made for it.
        var picture = await s.Gpu.HoldAsync(media.Find(Picture)!, deadline.Token);
        Assert.Empty(picture.Unloaded);
        Assert.Null(s.State.Lent);
        // A video waits for the picture to be done.
        var video = s.Gpu.HoldAsync(media.Find(Video)!, deadline.Token);
        await Task.Delay(1500);
        Assert.False(video.IsCompleted, "a video took the GPU while a picture was being made");
        Assert.Equal("loaded", app.Engine.StatusOf(Chat));

        // Another picture model on the picture server, kept loaded meanwhile by an admin: the server keeps the job's.
        await using (var scope = s.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var flux = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.MediaModels, m => m.Name == Picture);
            db.MediaModels.Add(new Llm.Core.Models.MediaModel
            {
                Name = "FLUX-8", Mode = flux.Mode, Files = [.. flux.Files.Select(x => new Llm.Core.Models.MediaModelFile { Role = x.Role, Path = x.Path })],
                MaxVramGb = flux.MaxVramGb, FlashAttention = true, Steps = 8,
            });
            await db.SaveChangesAsync();
        }
        await media.ChangedAsync(CancellationToken.None);
        await media.SetKeptAsync("FLUX-8", true);
        media.Wake();
        await Task.Delay(1500);
        Assert.Contains($"model={Picture}", await File.ReadAllTextAsync(Path.Combine(_dir, "config", "imagegen.conf")));

        await picture.DisposeAsync();
        await using var videoHold = await video.WaitAsync(Deadline);
        Assert.Equal([Chat], videoHold.Unloaded);
        // The picture's job over, the server runs the one kept loaded.
        await UntilAsync(() => File.ReadAllText(Path.Combine(_dir, "config", "imagegen.conf")).Contains("model=FLUX-8", StringComparison.Ordinal), "the picture server runs FLUX-8");
    }

    [Fact]
    public async Task On_a_machine_of_four_gpus_only_the_media_servers_gpu_is_lent_its_vllm_model_sleeps_and_wakes_and_the_others_stay()
    {
        // Four GPUs; the video server on GPU 3, where flash (vLLM) runs; other (vLLM) on GPUs 0-1; the chat model (llama.cpp) on GPU 2.
        var hardware = Path.Combine(_dir, "hardware.json");
        File.WriteAllText(hardware, $$"""{"ram_bytes": {{1L << 40}}, "gpus": [{{string.Join(',', Enumerable.Range(0, 4).Select(i =>
            $$"""{"index": {{i}}, "name": "GPU", "vendor": "nvidia", "backend": "cuda", "total_bytes": {{97887L << 20}}}"""))}}]}""");
        var library = Path.Combine(_dir, "library");
        foreach (var n in new[] { "flash", "other" })
        {
            var dir = Path.Combine(library, "hf", "org", n + "@r");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), """{"num_hidden_layers":4,"num_attention_heads":4,"num_key_value_heads":4,"head_dim":64,"max_position_embeddings":131072}""");
            File.WriteAllText(Path.Combine(dir, "model.safetensors"), "w");
        }
        var config = Path.Combine(_dir, "config");
        var vllm = new FakeVllm(Path.Combine(config, "vllm"), Path.Combine(_dir, "vllm-status", "vllm"));
        await using var s = await NewAsync(new Dictionary<string, string?>
        {
            ["Engine:HardwareFile"] = hardware, ["Engine:VllmStatusDir"] = Path.Combine(_dir, "vllm-status"),
        }, services => services.AddHttpClient(VllmBackend.Client).ConfigurePrimaryHttpMessageHandler(() => vllm));
        // GPU 3's free memory: what flash leaves while it is awake.
        s.Serve.FreeOf = () => vllm.Phase("flash") == "ready" ? 2_000 : 80_000;
        using var ticking = new CancellationTokenSource();
        var ticks = Task.Run(async () =>
        {
            while (!ticking.IsCancellationRequested)
            {
                vllm.Tick();
                await Task.Delay(100);
            }
        });
        try
        {
            await using (var scope = s.App.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
                LocalModel V(string name, string devices) => new()
                {
                    Name = name, File = $"hf/org/{name}@r", EngineKind = EngineKinds.Vllm, KvType = "fp8", Context = 8192, Parallel = 8, Devices = devices,
                };
                db.LocalModels.AddRange(V("flash", "3"), V("other", "0,1"), new LocalModel { Name = Chat, File = "chat/Chat.gguf", Devices = "2" });
                (await db.MediaModels.SingleAsync(m => m.Name == Video)).Gpu = 3;
                await db.SaveChangesAsync();
                var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
                await catalog.WritePresetsAsync();
                await catalog.WriteVllmPresetsAsync();
            }
            await s.Media.RefreshAsync(CancellationToken.None);
            var watcher = s.App.Services.GetRequiredService<EngineWatcher>();
            var engines = s.App.Services.GetRequiredService<EngineClient>();
            await UntilAsync(() =>
            {
                watcher.Wake();
                return s.State.StatusOf("flash") == "unloaded" && s.State.Places.TryGetValue(Chat, out var p) && p.PerGpu.ContainsKey(2);
            }, "the watcher sees the vLLM models and every model's place");
            await engines.LoadAsync("flash");
            await engines.LoadAsync("other");
            await UntilAsync(() =>
            {
                watcher.Wake();
                return s.State.StatusOf("flash") == "loaded" && s.State.StatusOf("other") == "loaded" && s.State.LoadedSince("flash") is not null
                    && s.State.LoadedSince(Chat) is not null && s.Media.StatusOf("videogen") is { FreeMb: 2_000 };
            }, "both vLLM models loaded, and the video server's GPU short");

            using var deadline = new CancellationTokenSource(Deadline);
            var video = s.Media.Find(Video)!;
            Assert.Equal([3], s.Gpu.GpusOf(video));
            var hold = await s.Gpu.HoldAsync(video, deadline.Token);
            // flash, on GPU 3, sleeps; the chat model on GPU 2 and other on GPUs 0-1 are not touched.
            Assert.Equal(["flash"], hold.Slept);
            Assert.Empty(hold.Unloaded);
            await UntilAsync(() => vllm.Phase("flash") == "sleeping", "flash asleep");
            Assert.Equal("loaded", app.Engine.StatusOf(Chat));
            Assert.Equal("ready", vllm.Phase("other"));
            Assert.Equal([3], s.State.Lent!.Gpus);
            Assert.Contains(s.Gpu.Recent, h => h.Text == $"Made room for {Video} (a video) on GPU 3: put to sleep flash (asleep).");
            // A model on other GPUs loads as ever while GPU 3 is lent; flash waits.
            Assert.False(s.State.Blocks("other"));
            Assert.False(s.State.Blocks(Chat));
            Assert.True(s.State.Blocks("flash"));

            // Done: flash wakes, the same processes.
            await hold.DisposeAsync();
            await UntilAsync(() => vllm.Phase("flash") == "ready", "flash awake");
            Assert.Equal(["flash sleep", "flash wake"], vllm.Sleeps);
            Assert.Contains(s.Gpu.Recent, h => h.Text == $"Gave the room back after {Video}: woke flash.");
            Assert.Null(s.State.Lent);
        }
        finally
        {
            await ticking.CancelAsync();
            await ticks;
        }
    }
}
