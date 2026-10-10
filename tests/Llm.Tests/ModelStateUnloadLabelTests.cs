using System.Net;
using System.Text;
using Llm.Api.Models;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Llm.Tests;

/// <summary>
/// "The unloading model text is wrong": every status the API gives a model from the moment an admin presses Unload until
/// the engine has stopped it, as the real router (llama.cpp b10909, measured) reports it: unloading while it stops, then
/// unloaded, never loading, and never failed when the router had to kill it (it was answering).
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ModelStateUnloadLabelTests(AppFixture app, ITestOutputHelper output) : IDisposable
{
    /// <summary>The statuses the page renders "Unloading…" and "Not loaded".</summary>
    private static readonly string?[] Unloading = ["unloading", "unloaded"];

    private readonly string _dir = Directory.CreateTempSubdirectory("llm-unload-label-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class SteppedClock(DateTimeOffset start) : TimeProvider
    {
        private readonly DateTimeOffset _start = start;

        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => (Now - _start).Ticks;
    }

    /// <summary>Answers GET /models with whatever JSON the test sets: the real router's words, verbatim.</summary>
    private sealed class Router : HttpMessageHandler
    {
        public string Json { get; set; } = """{"data":[]}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json, Encoding.UTF8, "application/json") });
    }

    /// <summary>
    /// What llama.cpp b10909's router said of a model (measured on this host, CPU, SmolLM2-135M): "loaded"; told to unload while
    /// it answers, "loaded" for 10 seconds, then force-killed: "unloaded" with failed=true, exit_code=1; a new request after
    /// that (--models-autoload): "loading", then "loaded". There is no "unloading" value.
    /// </summary>
    private static string RouterSays(string status) => status switch
    {
        "killed" => """{"data":[{"id":"big","status":{"value":"loaded"}},{"id":"tiny","status":{"value":"unloaded","failed":true,"exit_code":1}}]}""",
        _ => """{"data":[{"id":"big","status":{"value":"loaded"}},{"id":"tiny","status":{"value":"STATUS"}}]}""".Replace("STATUS", status, StringComparison.Ordinal),
    };

    [Fact]
    public async Task A_model_an_admin_unloads_is_unloading_then_unloaded_never_loading_nor_failed_until_it_is_asked_for_again()
    {
        using var router = new Router();
        using var http = new HttpClient(router);
        var engine = new EngineClient(http, Options.Create(new EngineOptions { Url = "http://router" }));
        var clock = new SteppedClock(DateTimeOffset.UtcNow);
        var state = new EngineState(clock);
        var seen = new List<string?>();

        // The watcher's round (EngineWatcher.cs:108-109); the API's "status" of a local model is state.StatusOf (ModelEndpoints.cs:97).
        async Task RoundAsync(string routerStatus)
        {
            router.Json = RouterSays(routerStatus);
            var asked = state.Asking();
            state.Set(await engine.ModelsAsync(), asked);
            seen.Add(state.StatusOf("tiny"));
        }

        await RoundAsync("loaded");
        Assert.Equal("loaded", state.StatusOf("tiny"));
        seen.Clear();

        // Unload pressed: ModelEndpoints.UnloadAsync -> route.Unloaded (EngineState.Unloading) -> engine.UnloadAsync.
        state.Dropped("tiny");
        state.Forget("tiny");
        state.Unloading("tiny", "unloaded by admin");
        seen.Add(state.StatusOf("tiny"));

        // The router lists it loaded while it stops (10 s at most), the watcher looking every 3-10 s.
        for (var s = 3; s < 10; s += 3)
        {
            clock.Now += TimeSpan.FromSeconds(3);
            await RoundAsync("loaded");
        }
        // Killed after 10 s: unloaded and failed.
        clock.Now += TimeSpan.FromSeconds(1);
        await RoundAsync("killed");
        clock.Now += TimeSpan.FromSeconds(10);
        await RoundAsync("killed");
        output.WriteLine("API status from Unload until stopped: " + string.Join(" -> ", seen));

        Assert.DoesNotContain("loading", seen);
        Assert.All(seen, s => Assert.Contains(s, Unloading));
        // Unloading while the router stops it, with why.
        Assert.Equal(["unloading", "unloading", "unloading", "unloading"], seen.Take(4));
        // Killed as it did not stop within 10 s: that was the app's stop, not a failed load.
        Assert.Equal("unloaded", seen[^1]);
        Assert.False(state.MayRetry("tiny"));
        Assert.Null(state.NextTry("tiny"));

        // "loading" appears only when something asks for the model again (a chat, a retry): the router loads it.
        await RoundAsync("loading");
        Assert.Equal("loading", state.StatusOf("tiny"));
    }

    [Fact]
    public async Task Get_api_admin_models_while_an_admin_unloads_a_model_that_stops_slowly()
    {
        var config = Path.Combine(_dir, "config");
        var library = Path.Combine(_dir, "library");
        GgufFile.Language("qwen3", name: "Flash").Write(Path.Combine(library, "flash", "Flash-Q4_K_M.gguf"));
        GgufFile.Language("qwen3", name: "Tiny", context: 40960).Write(Path.Combine(library, "tiny", "Tiny-4B-Q4_K_M.gguf"));
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "keep"), "Qwen3.8-Flash-Next\n");
        app.Engine.Reset(Path.Combine(config, "models.ini"));
        app.Engine.Max = 2;
        var gateway = new FakeGateway();
        await using var f = app.Create(app.ConnectionStringFor("unloadlabel_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = config, ["Engine:LibraryDir"] = library,
            ["Engine:ModelsMax"] = "2",
        });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models",
            new { name = "tiny-b", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 8192, maxOutput = 2048, parallel = 1 }));
        await EventuallyAsync(async () => await StatusAsync(admin, "tiny-b") == "unloaded", "the engine lists tiny-b", deadline.Token);
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/load"));
        await EventuallyAsync(async () => await StatusAsync(admin, "tiny-b") == "loaded", "tiny-b loaded", deadline.Token);

        // As the router: listed loaded until it has stopped.
        app.Engine.SlowStop = true;
        var seen = new List<string?>();
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/unload"));
        // While it stops (the watcher wakes at once, then looks every 3-10 s).
        for (var i = 0; i < 30; i++)
        {
            seen.Add(await StatusAsync(admin, "tiny-b"));
            await Task.Delay(100, deadline.Token);
        }
        app.Engine.Stop();
        for (var i = 0; i < 40; i++)
        {
            seen.Add(await StatusAsync(admin, "tiny-b"));
            await Task.Delay(300, deadline.Token);
        }
        output.WriteLine("GET /api/admin/models status of tiny-b from Unload: " + string.Join(" ", Collapse(seen)));
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-b"));
        Assert.DoesNotContain("loading", seen);
        Assert.All(seen, s => Assert.Contains(s, Unloading));
        // Unloading until the engine stopped it, then unloaded for good.
        Assert.Equal("unloading", seen[0]);
        Assert.Equal("unloaded", seen[^1]);
    }

    private static IEnumerable<string> Collapse(IEnumerable<string?> statuses)
    {
        string? last = null;
        var n = 0;
        foreach (var s in statuses.Append("<end>"))
        {
            if (s == last)
            {
                n++;
                continue;
            }
            if (n > 0)
            {
                yield return $"{last ?? "null"}x{n}";
            }
            (last, n) = (s, 1);
        }
    }

    private static async Task<string?> StatusAsync(TestBrowser admin, string name)
    {
        var list = await admin.JsonAsync(await admin.GetAsync("/api/admin/models"));
        return list.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name).GetProperty("status").GetString();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what, CancellationToken ct)
    {
        for (var i = 0; i < 150; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(100, ct);
        }
        Assert.Fail($"Never: {what}");
    }
}
