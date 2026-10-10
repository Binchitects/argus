using System.Net;
using System.Text;
using Llm.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
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
        public string File => System.IO.File.Exists(Control) ? System.IO.File.ReadAllText(Control).Trim() : "(none)";

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
        return new Setup(f, media, server, clock, Path.Combine(config, "imagegen"), dir);
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
            await s.Media.SetAsync(Image);
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
        await s.Media.SetAsync(Image, kept: false);
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
        await s.Media.SetAsync(Image);
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
        await s.Media.SetAsync(Image, kept: false);
        await UntilFileAsync(s, "off", "the app turns the picture server off");
        await UntilStatusAsync(s, "unloaded", "Admin -> Models says Not loaded");

        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var services = await admin.JsonAsync(await admin.GetAsync("/api/admin/services"));
        var pictures = services.EnumerateArray().Single(p => p.GetProperty("name").GetString() == "Pictures");

        Assert.True(pictures.GetProperty("ok").GetBoolean(),
            $"Admin -> Models says {Image} is unloaded (idle, as meant), but the Overview counts the picture server down: \"{pictures.GetProperty("detail").GetString()}\".");
    }
}
