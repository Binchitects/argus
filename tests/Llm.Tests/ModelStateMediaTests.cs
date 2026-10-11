using System.Net;
using System.Text;
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
public sealed class ModelStateMediaTests(AppFixture app)
{
    private const string Image = MediaModels.ImageModel;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

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
    private async Task<Setup> NewAsync()
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
            },
            s =>
            {
                s.AddSingleton<TimeProvider>(clock);
                s.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => server);
                s.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => server);
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
}
