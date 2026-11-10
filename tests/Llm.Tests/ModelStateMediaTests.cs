using System.Net;
using System.Net.Http.Json;
using System.Text;
using Llm.Api.Gateway;
using Llm.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// The picture server's state as the app keeps it (MediaControl): the control file services/sd-serve.sh reads every 3 s,
/// the ten-minute idle unload, the status Admin -> Models shows, the image tool's "ready", and the Overview's probe of the
/// server: a picture asked for as the idle unload happens is not told ready until the server is on again, a server that
/// never answers is said to have failed, and an idle one is not down.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ModelStateMediaTests(AppFixture app) : IDisposable
{
    public void Dispose() => Pictures.Dispose();

    private const string Image = MediaModels.ImageModel;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    /// <summary>LiteLLM as an API key's picture reaches it, once the app has the model loaded.</summary>
    private sealed class PictureGateway : HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Paths = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Enqueue(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[{"b64_json":"AA=="}]}""", Encoding.UTF8, "application/json") });
        }
    }

    private readonly PictureGateway Pictures = new();

    /// <summary>sd-server behind sd-serve.sh: it answers /v1/models while it runs; nothing answers otherwise (the Overview's probes too).</summary>
    private sealed class FakeSdServer : HttpMessageHandler
    {
        public volatile bool Up;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Up && request.RequestUri is { Host: "imagegen", Port: 1234, AbsolutePath: "/v1/models" })
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") });
            }
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"Connection refused ({request.RequestUri?.Authority})");
        }
    }

    private sealed record Setup(WebApplicationFactory<Program> App, MediaControl Media, FakeSdServer Server, MovableClock Clock, string Control, DirectoryInfo Dir)
        : IAsyncDisposable
    {
        /// <summary>What the control file says now ("on", "off", or none yet).</summary>
        public string File => System.IO.File.Exists(Control)
            ? System.IO.File.ReadLines(Control).FirstOrDefault(l => l.StartsWith("want=", StringComparison.Ordinal))?[5..] ?? "(no want)"
            : "(none)";

        /// <summary>The status Admin -> Models shows for the picture model.</summary>
        public async Task<string> StatusAsync() => (await Media.ListAsync(CancellationToken.None)).Single(s => s.Model.Name == Image).Now;

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            try
            {
                Dir.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>An app with the imagegen module, its files in the library, a control directory of its own, one replica, and a clock the test moves.</summary>
    private async Task<Setup> NewAsync(Dictionary<string, string?>? settings = null)
    {
        var dir = Directory.CreateTempSubdirectory("model-state-media-");
        var library = Path.Combine(dir.FullName, "library");
        foreach (var file in MediaModels.Servers.First(x => x.Server == "imagegen").Files)
        {
            Directory.CreateDirectory(Path.Combine(library, file.Dir));
            await File.WriteAllTextAsync(Path.Combine(library, file.Dir, file.Name), "");
        }
        var config = Path.Combine(dir.FullName, "config");
        var server = new FakeSdServer();
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var f = app.Create(app.ConnectionStringFor("mstate_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?>
            {
                ["Modules:imagegen"] = "true",
                ["Engine:ConfigDir"] = config,
                ["Engine:MediaStatusDir"] = Path.Combine(dir.FullName, "status"),
                ["Engine:LibraryDir"] = library,
                ["Replicas:Enabled"] = "false",
            }.Concat(settings ?? []).ToDictionary(),
            s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                s.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => server);
                s.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => server);
                s.AddHttpClient(Llm.Api.Gateway.KeySpeech.Client).ConfigurePrimaryHttpMessageHandler(() => Pictures);
                s.AddHttpClient(Llm.Api.Gateway.ModelGateway.Upstream).ConfigurePrimaryHttpMessageHandler(() => Pictures);
            });
        var media = f.Services.GetRequiredService<MediaControl>();
        return new Setup(f, media, server, clock, Path.Combine(config, "imagegen.conf"), dir);
    }

    /// <summary>Until <paramref name="done"/>, waking the app's media loop each time (a save of the same state wakes it); 30 s at most.</summary>
    private static async Task UntilAsync(Setup s, Func<Task<bool>> done, string what)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!await done())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Not within {Deadline.TotalSeconds} s: {what} (control file: {s.File}, status: {await s.StatusAsync()})");
            }
            s.Media.Wake();
            await Task.Delay(200);
        }
    }

    private static Task UntilFileAsync(Setup s, string text, string what) => UntilAsync(s, () => Task.FromResult(s.File == text), what);

    private static Task UntilStatusAsync(Setup s, string status, string what) => UntilAsync(s, async () => await s.StatusAsync() == status, what);

    /// <summary>The GPU the control file names for the picture server (gpu=), or null.</summary>
    private static string? GpuIn(Setup s) =>
        File.Exists(s.Control) ? File.ReadLines(s.Control).FirstOrDefault(l => l.StartsWith("gpu=", StringComparison.Ordinal))?[4..] : null;

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public async Task The_picture_server_goes_on_the_least_placed_gpu_not_the_first_and_a_gpu_its_model_names_is_kept(int gpus)
    {
        await using var s = await NewAsync();
        var state = s.App.Services.GetRequiredService<EngineState>();
        // A vLLM model on GPU 0, llama.cpp models on the next ones but the last, and one on every GPU (which weighs on each alike).
        var places = new Dictionary<string, EngineState.ModelPlace>(StringComparer.Ordinal)
        {
            ["oss"] = new("vllm", new Dictionary<int, long> { [0] = 86 * FakeHardware.GiB }),
            ["everywhere"] = new("llamacpp", Enumerable.Range(0, gpus).ToDictionary(g => g, _ => 10 * FakeHardware.GiB)),
        };
        foreach (var g in Enumerable.Range(1, gpus - 2))
        {
            places[$"small{g}"] = new("llamacpp", new Dictionary<int, long> { [g] = (40 + g) * FakeHardware.GiB });
        }
        state.Places = places;
        // The GPUs after what is on them: no pass places the server on GPUs whose models it does not see yet. Until then it is
        // placed nowhere (its model is kept loaded from the start, so its server may be on already, on every GPU it sees).
        state.Hardware = FakeHardware.Many(gpus);
        await s.Media.SetKeptAsync(Image, false);
        s.Media.Load(Image);
        await UntilAsync(s, () => Task.FromResult(s.File == "on" && GpuIn(s) is not null), "the app turns the picture server on, on a GPU");
        // Before, no GPU was named: the server saw them all and ran on CUDA's first, GPU 0, beside the vLLM model.
        Assert.Equal((gpus - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), GpuIn(s));
        Assert.Contains(s.Media.Placed(), r => r.PerGpu.ContainsKey(gpus - 1) && r.Name.StartsWith(HardwareProbe.Picture, StringComparison.Ordinal));
        // Placed, it stays there though its GPU is the busiest now: started again, it is not moved.
        state.Places = new Dictionary<string, EngineState.ModelPlace>(StringComparer.Ordinal) { ["big"] = new("vllm", new Dictionary<int, long> { [gpus - 1] = 86 * FakeHardware.GiB }) };
        string? Gen() => File.ReadLines(s.Control).FirstOrDefault(l => l.StartsWith("gen=", StringComparison.Ordinal));
        var gen = Gen();
        s.Media.Restart("imagegen", "a test");
        await UntilAsync(s, () => Task.FromResult(Gen() != gen), "the picture server is started again");
        Assert.Equal((gpus - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), GpuIn(s));
        // The GPU its model names (an admin's choice) is kept, however placed that GPU is.
        await using (var scope = s.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            (await db.MediaModels.SingleAsync(m => m.Name == Image)).Gpu = 0;
            await db.SaveChangesAsync();
        }
        await s.Media.RefreshAsync(CancellationToken.None);
        await UntilAsync(s, () => Task.FromResult(GpuIn(s) == "0"), "the picture server goes on the GPU its model names");
    }

    [Fact]
    public async Task In_a_pod_of_its_own_the_picture_server_is_told_no_gpu_of_the_engines()
    {
        // Kubernetes: its own pod with its own GPU, index 0 there. The engines' GPUs (2 here) are not its: gpu=1 would be none.
        await using var s = await NewAsync(new() { ["Engine:MediaOwnGpus"] = "true", ["Engine:MediaGpu"] = "1" });
        var state = s.App.Services.GetRequiredService<EngineState>();
        state.Places = new Dictionary<string, EngineState.ModelPlace>(StringComparer.Ordinal) { ["oss"] = new("vllm", new Dictionary<int, long> { [0] = 86 * FakeHardware.GiB }) };
        state.Hardware = FakeHardware.Many(2);
        await s.Media.SetKeptAsync(Image, false);
        s.Media.Load(Image);
        await UntilFileAsync(s, "on", "the app turns the picture server on");
        Assert.Null(GpuIn(s));
        Assert.Empty(s.Media.Placed());
    }

    [Fact]
    public async Task A_picture_asked_for_as_the_idle_unload_happens_turns_its_server_on_again_before_it_is_told_ready()
    {
        await using var s = await NewAsync();
        // Not kept loaded: it loads when asked for, and unloads after ten minutes unused.
        await s.Media.SetKeptAsync(Image, false);
        s.Media.Load(Image);
        await UntilFileAsync(s, "on", "the app turns the picture server on");
        s.Server.Up = true; // sd-serve.sh started it, and it answers
        await UntilStatusAsync(s, "loaded", "Admin -> Models says Loaded");

        // Ten minutes unused: the app writes "off". sd-serve.sh reads the file every 3 s, so its server answers a little longer.
        s.Clock.Now += TimeSpan.FromMinutes(11);
        await UntilFileAsync(s, "off", "the idle unload turns the server off");
        var statusWhileStopping = await s.StatusAsync();

        // A person asks for a picture in that moment (ImageTool -> MediaControl.ReadyAsync).
        using var deadline = new CancellationTokenSource(Deadline);
        var notReady = await s.Media.ReadyAsync(Image, deadline.Token);
        var fileWhenReady = s.File;

        // sd-serve.sh's next look: the file says off, so it kills the server, under the picture request.
        if (fileWhenReady == "off")
        {
            s.Server.Up = false;
        }
        // The app's next round: asked for a moment ago, so on again, and the page says it is loading.
        await UntilFileAsync(s, "on", "the app turns the server on again");
        var statusAfter = await s.StatusAsync();

        Assert.True(notReady is not null || fileWhenReady == "on",
            $"ReadyAsync told the image tool {Image} is ready, but the control file said \"{fileWhenReady}\" (Admin -> Models said \"{statusWhileStopping}\" as it stopped); " +
            $"sd-serve.sh kills the server within 3 s, under the picture request, and Admin -> Models then says \"{statusAfter}\" though nobody pressed Load.");
    }

    [Fact]
    public async Task A_kept_picture_model_whose_server_never_answers_is_said_to_have_failed()
    {
        await using var s = await NewAsync();
        // Kept loaded (the picture model's default); its server never answers (sd-server dies as it loads, say with no GPU
        // memory left beside the chat models, and sd-serve.sh starts it again every 3 s).
        await UntilFileAsync(s, "on", "the app turns the picture server on");
        await UntilStatusAsync(s, "loading", "Admin -> Models says Loading");

        // The image tool waits five minutes (by the app's clock), then gives up.
        using var deadline = new CancellationTokenSource(Deadline);
        var waiting = s.Media.ReadyAsync(Image, deadline.Token);
        while (!waiting.IsCompleted)
        {
            s.Clock.Now += TimeSpan.FromMinutes(6);
            await Task.WhenAny(waiting, Task.Delay(1000, deadline.Token));
        }
        var told = await waiting;

        // A day later, after a round of the media loop.
        s.Clock.Now += TimeSpan.FromDays(1);
        s.Media.Wake();
        await Task.Delay(1500);
        var status = await s.StatusAsync();

        Assert.True(status != "loading",
            $"A day after its server stopped answering, Admin -> Models still says \"{status}\" (Loading…, with Load disabled); the image tool said: \"{told}\".");
    }

    [Fact]
    public async Task The_overview_counts_the_picture_server_down_while_its_model_is_unloaded_as_idle()
    {
        await using var s = await NewAsync();
        // Not kept loaded and not asked for: unloaded, as meant (its container runs; sd-server does not, so nothing listens on :1234).
        await s.Media.SetKeptAsync(Image, false);
        await UntilFileAsync(s, "off", "the app turns the picture server off");
        await UntilStatusAsync(s, "unloaded", "Admin -> Models says Not loaded");
        // A second picture model for the same server (an admin adds one): the Overview still answers.
        using (var scope = s.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var flux = await db.MediaModels.AsNoTracking().SingleAsync(m => m.Name == Image);
            db.MediaModels.Add(new Llm.Core.Models.MediaModel
            {
                Name = "FLUX-8", Mode = flux.Mode, Files = [.. flux.Files.Select(f => new Llm.Core.Models.MediaModelFile { Role = f.Role, Path = f.Path })],
                MaxVramGb = flux.MaxVramGb, FlashAttention = true, Steps = 8, CfgScale = 1.0,
            });
            await db.SaveChangesAsync();
        }
        await s.Media.ChangedAsync(CancellationToken.None);

        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var services = await admin.JsonAsync(await admin.GetAsync("/api/admin/services"));
        var pictures = services.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Pictures");

        Assert.True(pictures.GetProperty("ok").GetBoolean(),
            $"Admin -> Models says {Image} is unloaded (idle, as meant), but the Overview counts the picture server down: \"{pictures.GetProperty("detail").GetString()}\".");
    }

    /// <summary>The speech server's loaded models, as speaches keeps them with its own unloading off: loaded until told.</summary>
    private sealed class FakeSpeech : HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Loaded = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Calls = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "audio" && path.StartsWith("/api/ps", StringComparison.Ordinal))
            {
                var id = path.Length > "/api/ps/".Length ? path["/api/ps/".Length..] : "";
                Calls.Enqueue($"{request.Method} {id}");
                if (request.Method == HttpMethod.Post)
                {
                    Loaded[id] = true;
                }
                else if (request.Method == HttpMethod.Delete)
                {
                    Loaded.TryRemove(id, out _);
                }
                var list = string.Join(",", Loaded.Keys.Select(k => $"\"{k}\""));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"models":[{{list}}]}""", Encoding.UTF8, "application/json") });
            }
            throw new HttpRequestException(HttpRequestError.ConnectionError, $"Connection refused ({request.RequestUri.Authority})");
        }
    }

    [Fact]
    public async Task A_speech_model_used_stays_loaded_while_it_is_used_and_the_app_unloads_it_ten_minutes_after()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var speech = new FakeSpeech();
        await using var f = app.Create(app.ConnectionStringFor("mspeech_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Modules:audio"] = "true", ["Replicas:Enabled"] = "false" },
            s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                s.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => speech);
            });
        var media = f.Services.GetRequiredService<MediaControl>();
        var kokoro = MediaModels.Speech.Single(m => m.Name == MediaModels.TextToSpeech).Id;
        var whisper = MediaModels.Speech.Single(m => m.Name == MediaModels.SpeechToText).Id;
        async Task Until(Func<bool> done, string what)
        {
            var until = DateTime.UtcNow + Deadline;
            while (!done())
            {
                Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds} s: {what} (calls: {string.Join(", ", speech.Calls)})");
                media.Wake();
                await Task.Delay(200);
            }
        }

        // Kept loaded by default: speech to text. Read aloud uses kokoro (not kept): it counts as used, and is loaded.
        await Until(() => speech.Loaded.ContainsKey(whisper), "the kept speech to text model is loaded");
        media.Touch(MediaModels.TextToSpeech);
        await Until(() => speech.Loaded.ContainsKey(kokoro), "kokoro, used, is loaded");

        // Used again nine minutes on: it stays.
        clock.Now += TimeSpan.FromMinutes(9);
        media.Touch(MediaModels.TextToSpeech);
        clock.Now += TimeSpan.FromMinutes(9);
        media.Wake();
        await Task.Delay(500);
        Assert.True(speech.Loaded.ContainsKey(kokoro), "used nine minutes ago: still loaded");

        // Ten minutes and more unused: the app unloads it (speaches would not: its own unloading is off); the kept one stays.
        clock.Now += TimeSpan.FromMinutes(2);
        await Until(() => !speech.Loaded.ContainsKey(kokoro), "kokoro, unused for eleven minutes, is unloaded");
        Assert.True(speech.Loaded.ContainsKey(whisper));
        Assert.Contains($"DELETE {kokoro}", speech.Calls);
    }

    /// <summary>What services/sd-serve.sh would write: the generation it runs, its phase, since when (by the app's clock).</summary>
    private static async Task StatusAsync(Setup s, string gen, string phase, DateTimeOffset since, string missing = "", int? exit = null)
    {
        var dir = Path.Combine(s.Dir.FullName, "status");
        Directory.CreateDirectory(dir);
        var json = $$"""{"gen":"{{gen}}","phase":"{{phase}}","since":{{since.ToUnixTimeSeconds()}},"at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}},"free_mb":2600,"exit":{{(exit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null")}},"missing":[{{missing}}],"vae":"","model":"{{Image}}"}""";
        await File.WriteAllTextAsync(Path.Combine(dir, "imagegen.json"), json);
    }

    private static string Conf(Setup s, string key) =>
        File.ReadLines(s.Control).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..] ?? "";

    [Fact]
    public async Task The_control_file_names_the_model_its_files_and_flags_and_a_server_that_exits_is_started_again_after_a_while()
    {
        await using var s = await NewAsync();
        await s.Media.SetKeptAsync(Image, true);
        await UntilFileAsync(s, "on", "the app turns the picture server on");
        var text = await File.ReadAllTextAsync(s.Control);
        Assert.Contains($"model={Image}", text);
        Assert.Contains("file=image/flux2-klein-4b/flux-2-klein-4b-Q4_0.gguf", text);
        Assert.Contains("arg=--diffusion-model\narg=image/flux2-klein-4b/flux-2-klein-4b-Q4_0.gguf\n", text);
        Assert.Contains("arg=--steps\narg=4\n", text);
        var gen = Conf(s, "gen");

        // Its script says it waits for files: Admin -> Models says so.
        await StatusAsync(s, gen, "waiting", s.Clock.Now, "\"image/flux2-klein-4b/flux2-vae.safetensors\"");
        await UntilStatusAsync(s, "waiting", "the page says it waits for its files");

        // It ran and exited, out of GPU memory: failed, why, its server off while it waits, not started again yet.
        Directory.CreateDirectory(Path.Combine(s.Dir.FullName, "status"));
        await File.WriteAllTextAsync(Path.Combine(s.Dir.FullName, "status", "imagegen.log"), "loading model\n[ERROR] ggml_cuda: CUDA error: out of memory\n");
        await StatusAsync(s, gen, "exited", s.Clock.Now, exit: 1);
        await UntilStatusAsync(s, "failed", "the page says it failed");
        var failure = s.Media.FailureOf(Image)!;
        Assert.Equal(("gpu-memory", 1), (failure.Class, failure.Count));
        Assert.Contains("out of memory", failure.Detail);
        await UntilFileAsync(s, "off", "its server is off while it waits");
        Assert.Equal(gen, Conf(s, "gen"));
        Assert.Contains("could not load (gpu-memory", await s.Media.ReadyAsync(Image, CancellationToken.None));
        // A minute on (1, 2, 4… as a chat model): a new generation, which its script starts.
        s.Clock.Now += TimeSpan.FromSeconds(61);
        await UntilAsync(s, () => Task.FromResult(Conf(s, "gen") != gen), "the app raises gen to start it again");
        Assert.Equal("on", s.File);
        var next = Conf(s, "gen");
        await StatusAsync(s, next, "running", s.Clock.Now);
        s.Server.Up = true;
        await UntilStatusAsync(s, "loaded", "it answers: Loaded");

        // The old generation answering does not count as this one ready.
        await StatusAsync(s, gen, "running", s.Clock.Now);
        await UntilStatusAsync(s, "loaded", "still loaded");
        Assert.NotNull(await s.Media.ReadyAsync(Image, new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).ContinueWith(t => t.IsCanceled ? "waited" : t.Result));
    }

    [Theory]
    [InlineData("loading\nggml_cuda_init: CUDA error: out of memory\n", 1, 40, "gpu-memory")]
    [InlineData("error: unknown argument: --stepz\nusage: sd-server [options]\n", 1, 0.2, "bad-options")]
    [InlineData("loading\n", 2, 1, "bad-options")]
    [InlineData("failed to open 'image/a.gguf': No such file or directory\n", 1, 30, "missing-file")]
    [InlineData("[INFO] sampling\n", 139, 120, "crashed")]
    public void A_servers_exit_is_told_by_its_log_its_code_and_how_long_it_ran(string log, int exit, double ran, string kind) =>
        Assert.Equal(kind, MediaFailure.Classify(log, exit, ran).Class);

    [Fact]
    public void A_failure_is_tried_again_after_one_two_four_minutes_and_flags_it_refused_wait_to_be_changed()
    {
        var at = DateTimeOffset.UtcNow;
        Assert.Equal(at + TimeSpan.FromMinutes(1), new MediaFailure("crashed", "", 1, at, "1").RetryAt);
        Assert.Equal(at + TimeSpan.FromMinutes(4), new MediaFailure("gpu-memory", "", 3, at, "1").RetryAt);
        Assert.Equal(at + TimeSpan.FromMinutes(30), new MediaFailure("timeout", "", 9, at, "1").RetryAt);
        Assert.Null(new MediaFailure("bad-options", "", 1, at, "1").RetryAt);
    }

    [Fact]
    public async Task A_server_runs_one_of_its_models_at_a_time_the_kept_one_and_switching_starts_a_new_generation()
    {
        await using var s = await NewAsync();
        await s.Media.SetKeptAsync(Image, true);
        await UntilFileAsync(s, "on", "FLUX runs");
        var gen = Conf(s, "gen");
        // A second picture model from the same files, at 8 steps.
        using (var scope = s.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var flux = await db.MediaModels.AsNoTracking().SingleAsync(m => m.Name == Image);
            db.MediaModels.Add(new Llm.Core.Models.MediaModel
            {
                Name = "FLUX-8", Mode = flux.Mode, Files = [.. flux.Files.Select(f => new Llm.Core.Models.MediaModelFile { Role = f.Role, Path = f.Path })],
                MaxVramGb = flux.MaxVramGb, FlashAttention = true, Steps = 8, CfgScale = 1.0,
            });
            await db.SaveChangesAsync();
        }
        await s.Media.ChangedAsync(CancellationToken.None);
        await s.Media.SetKeptAsync("FLUX-8", true);
        // One kept a server: FLUX is not any more, and the control file names FLUX-8, a new generation its script starts.
        await UntilAsync(s, () => Task.FromResult(Conf(s, "model") == "FLUX-8"), "the server runs FLUX-8");
        Assert.NotEqual(gen, Conf(s, "gen"));
        Assert.Contains("arg=--steps\narg=8\n", await File.ReadAllTextAsync(s.Control));
        Assert.False(s.Media.Find(Image)!.Row.Kept);
        Assert.Equal("unloaded", (await s.Media.ListAsync(CancellationToken.None)).Single(x => x.Model.Name == Image).Now);
    }

    [Fact]
    public async Task An_API_keys_picture_loads_its_model_first_and_is_answered_once_it_runs()
    {
        await using var s = await NewAsync();
        await s.Media.SetKeptAsync(Image, false);
        await UntilFileAsync(s, "off", "the picture server is off");
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        var key = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("apiKey").GetString()!;
        var client = s.App.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/images/generations", UriKind.Relative))
        {
            Content = new StringContent($$"""{"model":"{{Image}}","prompt":"an apple","size":"512x512"}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", key);
        var sending = client.SendAsync(req);
        // The app turns its server on for it, and holds the request until it answers.
        await UntilFileAsync(s, "on", "the picture server is turned on for the key's request");
        Assert.False(sending.IsCompleted);
        Assert.Empty(Pictures.Paths);
        s.Server.Up = true;
        var res = await sending.WaitAsync(Deadline);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        Assert.Equal("/v1/images/generations", Assert.Single(Pictures.Paths));
    }

    /// <summary>The picture server's state on Admin → Monitoring (Engines and sandboxes).</summary>
    private static async Task<string?> RuntimeAsync(TestBrowser admin) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/runtimes"))).EnumerateArray().Single(r => r.GetProperty("id").GetString() == "imagegen").GetProperty("state").GetString();

    [Fact]
    public async Task An_admins_restart_starts_the_picture_server_again_as_a_new_generation_and_waits_until_it_runs_it()
    {
        await using var s = await NewAsync();
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        // Nothing runs on it: nothing to restart.
        await s.Media.SetKeptAsync(Image, false);
        await UntilAsync(s, async () => await RuntimeAsync(admin) == "idle", "the picture server idle");
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/runtimes/imagegen/restart"));

        await s.Media.SetKeptAsync(Image, true);
        await UntilFileAsync(s, "on", "the app turns the picture server on");
        var gen = Conf(s, "gen");
        await StatusAsync(s, gen, "running", s.Clock.Now);
        s.Server.Up = true;
        await UntilAsync(s, async () => await RuntimeAsync(admin) == "up", "up, running its model");

        // A new generation, which its script starts; until it runs it, restarting, and asked again meanwhile, refused.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/runtimes/imagegen/restart"));
        await UntilAsync(s, () => Task.FromResult(Conf(s, "gen") != gen), "the app raises gen to start it again");
        Assert.Equal("restarting", await RuntimeAsync(admin));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/runtimes/imagegen/restart"));
        Assert.Contains("restarted by admin", (await s.Media.ListAsync(CancellationToken.None)).Single(m => m.Model.Name == Image).Why, StringComparison.Ordinal);
        await StatusAsync(s, Conf(s, "gen"), "running", s.Clock.Now);
        await UntilAsync(s, async () => await RuntimeAsync(admin) == "up", "it runs the new generation: up");
        Assert.Contains($"model={Image}", await File.ReadAllLinesAsync(Path.Combine(s.Dir.FullName, "config", "imagegen.restart")));
        var audit = await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"));
        Assert.Contains(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == "runtime.restart" && e.GetProperty("target").GetString() == "imagegen"
            && e.GetProperty("detail").GetString() == Image);
    }

    [Fact]
    public async Task An_API_keys_picture_without_picture_credit_is_refused_before_its_model_loads()
    {
        await using var s = await NewAsync();
        await s.Media.SetKeptAsync(Image, false);
        await UntilFileAsync(s, "off", "the picture server is off");
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{made.GetProperty("id").GetGuid()}/credits", UriKind.Relative), new { pictures = 0 }));
        var client = s.App.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/images/generations", UriKind.Relative))
        {
            Content = new StringContent($$"""{"model":"{{Image}}","prompt":"an apple","size":"512x512"}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", made.GetProperty("apiKey").GetString()!);

        // Refused at once with who gives the credit; the model was not loaded for it, nor the gateway asked.
        var res = await client.SendAsync(req).WaitAsync(Deadline);
        await StatusAssert.Is(HttpStatusCode.BadRequest, res);
        Assert.Contains("You have no picture credit: an admin sets it under People.", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        s.Media.Wake();
        await Task.Delay(1000);
        Assert.Equal("off", s.File);
        Assert.Empty(Pictures.Paths);
    }

    [Fact]
    public async Task An_API_keys_picture_of_a_model_it_may_not_use_or_with_no_known_key_never_loads_it()
    {
        await using var s = await NewAsync();
        await s.Media.SetKeptAsync(Image, false);
        await UntilFileAsync(s, "off", "the picture server is off");
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        var key = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("apiKey").GetString()!;
        // The model given to admins only: the person's key no longer has it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(
            new Uri($"/api/admin/models/{Uri.EscapeDataString(Image)}/access", UriKind.Relative), new { audience = "Admins", groups = Array.Empty<string>() }));
        var book = s.App.Services.GetRequiredService<KeyBook>();
        await UntilAsync(s, async () => (await book.ForAsync(key, CancellationToken.None))?.Models is { Count: > 0 } m && !m.Contains(Image),
            "the person's key no longer has the picture model");
        var client = s.App.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        foreach (var bearer in new[] { key, "sk-nobody-knows-this-key" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/images/generations", UriKind.Relative))
            {
                Content = new StringContent($$"""{"model":"{{Image}}","prompt":"an apple","size":"512x512"}""", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new("Bearer", bearer);
            // On to the gateway at once, which refuses it; the picture server stays off.
            await StatusAssert.Is(HttpStatusCode.OK, await client.SendAsync(req).WaitAsync(Deadline));
        }
        Assert.Equal(2, Pictures.Paths.Count);
        s.Media.Wake();
        await Task.Delay(1000);
        Assert.Equal("off", s.File);
    }

    [Fact]
    public async Task A_model_taken_from_a_key_it_was_just_used_with_is_never_loaded_for_it_again()
    {
        await using var s = await NewAsync();
        await s.Media.SetKeptAsync(Image, false);
        await UntilFileAsync(s, "off", "the picture server is off");
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "k" + Guid.NewGuid().ToString("N")[..10];
        var key = (await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }))).GetProperty("apiKey").GetString()!;
        // The key used a moment ago: the app believes what it may call for a while (Gateway:KeyCacheSeconds).
        var book = s.App.Services.GetRequiredService<KeyBook>();
        var before = await book.ForAsync(key, CancellationToken.None);
        Assert.True(before!.Models.Count == 0 || before.Models.Contains(Image));

        // The model given to admins only: the key may not call it from that moment, with no time passing.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(
            new Uri($"/api/admin/models/{Uri.EscapeDataString(Image)}/access", UriKind.Relative), new { audience = "Admins", groups = Array.Empty<string>() }));
        var after = await book.ForAsync(key, CancellationToken.None);
        Assert.True(after!.Models.Count > 0 && !after.Models.Contains(Image), $"the key still may call: {string.Join(", ", after.Models)}");

        // So its picture goes on to the gateway at once, which refuses it; the picture server stays off.
        var client = s.App.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/images/generations", UriKind.Relative))
        {
            Content = new StringContent($$"""{"model":"{{Image}}","prompt":"an apple","size":"512x512"}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", key);
        await StatusAssert.Is(HttpStatusCode.OK, await client.SendAsync(req).WaitAsync(Deadline));
        Assert.Equal("/v1/images/generations", Assert.Single(Pictures.Paths));
        s.Media.Wake();
        await Task.Delay(1000);
        Assert.Equal("off", s.File);
    }

    [Fact]
    public async Task The_speech_servers_voice_detector_unloads_when_unused_and_an_admins_load_says_so()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var speech = new FakeSpeech();
        await using var f = app.Create(app.ConnectionStringFor("mvad_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Modules:audio"] = "true", ["Replicas:Enabled"] = "false" },
            s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                s.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => speech);
            });
        var media = f.Services.GetRequiredService<MediaControl>();
        async Task Until(Func<Task<bool>> done, string what)
        {
            var until = DateTime.UtcNow + Deadline;
            while (!await done())
            {
                Assert.True(DateTime.UtcNow < until, $"Not within {Deadline.TotalSeconds} s: {what} (calls: {string.Join(", ", speech.Calls)})");
                media.Wake();
                await Task.Delay(200);
            }
        }
        async Task<MediaControl.Status> Row(string name) => (await media.ListAsync(CancellationToken.None)).Single(x => x.Model.Name == name);

        // An admin loads kokoro: it says so while it is loaded, round after round (not "for a request").
        media.Load(MediaModels.TextToSpeech);
        await Until(async () => (await Row(MediaModels.TextToSpeech)).Now == "loaded", "kokoro is loaded");
        media.Wake();
        await Task.Delay(500);
        Assert.Equal("loaded by an admin", (await Row(MediaModels.TextToSpeech)).Why);

        // Dictation: the speech server loaded its voice detector by itself. It stays while speech to text is used.
        speech.Loaded["silero_vad_v5"] = true;
        media.Touch(MediaModels.SpeechToText);
        clock.Now += TimeSpan.FromMinutes(5);
        media.Wake();
        await Task.Delay(500);
        Assert.True(speech.Loaded.ContainsKey("silero_vad_v5"), "used five minutes ago: still loaded");
        // Ten minutes and more without speech to text: unloaded, as the models are.
        clock.Now += TimeSpan.FromMinutes(6);
        await Until(() => Task.FromResult(!speech.Loaded.ContainsKey("silero_vad_v5")), "the voice detector, unused for eleven minutes, is unloaded");
        Assert.Contains("DELETE silero_vad_v5", speech.Calls);
    }
}
