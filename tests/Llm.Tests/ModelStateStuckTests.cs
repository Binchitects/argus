using System.Globalization;
using System.Net;
using System.Text.Json;
using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// "Sometimes stuck": EngineState's bookkeeping (an admin's Load followed up, an Unload's mark) ends with its outcome, so
/// neither the page nor the watcher keeps a state that no longer matches the router. Nothing here waits without a deadline.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ModelStateStuckTests(AppFixture app) : IDisposable
{
    private const string Big = "Qwen3.8-Flash-Next";
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-stuck-").FullName;

    private string Config => Path.Combine(_dir, "config");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A clock whose time and timestamps move only when the test moves them.</summary>
    private sealed class SteppedClock(DateTimeOffset start) : TimeProvider
    {
        private readonly DateTimeOffset _start = start;

        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => (Now - _start).Ticks;
    }

    /// <summary>
    /// Outcome "a failed load": an admin's Load whose load ends failed is still followed up for 10 minutes
    /// (EngineState._asked is cleared only when the model is seen loaded, or by an admin's Unload).
    /// </summary>
    [Fact]
    public void An_admins_Load_that_failed_is_no_longer_followed_up()
    {
        var clock = new SteppedClock(DateTimeOffset.UtcNow);
        var state = new EngineState(clock);
        state.Set([new EngineModel("big", "loaded"), new EngineModel("x", "unloaded")], state.Asking());

        // Admin -> Models -> Load x, as ModelEndpoints.LoadAsync does it: Loading, Asked, then the engine's load.
        state.Loading("x");
        state.Asked("x");
        clock.Now += TimeSpan.FromSeconds(5);
        // The load ends in an error: the router lists x failed.
        state.Set([new EngineModel("big", "loaded"), new EngineModel("x", "failed")], state.Asking());
        var askedAfterFailure = state.StillAsked("x");

        // A minute later the router restarts (an admin saved a model's settings): it lists everything unloaded, x's failure forgotten.
        clock.Now += TimeSpan.FromMinutes(1);
        state.Set([new EngineModel("big", "unloaded"), new EngineModel("x", "unloaded")], state.Asking());
        // EngineWatcher.ExecuteAsync picks models.FirstOrDefault(m => m.Status == "unloaded" && state.StillAsked(m.Name)):
        // it loads x again, without EngineState.Tried (x is "unloaded" now, not "failed"), so outside the failure back-off.
        var watcherWouldLoad = new[] { "big", "x" }.FirstOrDefault(m => state.StatusOf(m) == "unloaded" && state.StillAsked(m));

        Assert.True(!askedAfterFailure && watcherWouldLoad is null,
            $"After the router said x failed, StillAsked(x) = {askedAfterFailure}; after a restart the watcher would load {watcherWouldLoad ?? "nothing"} again " +
            "(the failed Load is followed up for EngineState.AskedFor, 10 minutes).");
    }

    /// <summary>
    /// Outcome "two admins at once": B's Unload and A's Load of the same model interleave (each request awaits the
    /// engine between its bookkeeping steps). B's last step, route.Unloaded, lands after A's Load reached the router.
    /// </summary>
    [Fact]
    public void Two_admins_at_once_a_Load_after_an_Unload_shows_the_model_loaded_and_the_watcher_leaves_it()
    {
        var clock = new SteppedClock(DateTimeOffset.UtcNow);
        var state = new EngineState(clock);
        IReadOnlyList<EngineModel> Router(string x) => [new EngineModel("big", "loaded"), new EngineModel("x", x)];
        state.Set(Router("loaded"), state.Asking());

        // B: ModelEndpoints.UnloadAsync: Dropped, Forget, the mark (route.Unloaded), then await engine.UnloadAsync...
        state.Dropped("x");
        state.Forget("x");
        state.Unloading("x", "unloaded by b");
        clock.Now += TimeSpan.FromSeconds(1);
        // ...A, meanwhile: ModelEndpoints.LoadAsync: Loading, Asked, await engine.LoadAsync; the router loads x again after
        // B's unload, so it ends loaded: A's is the last word.
        state.Loading("x", "loaded by a");
        state.Asked("x");

        // The watcher looks 10 and 20 seconds later: the router has x loaded each time.
        var shown = new List<string?>();
        for (var i = 0; i < 2; i++)
        {
            clock.Now += TimeSpan.FromSeconds(10);
            state.Set(Router("loaded"), state.Asking());
            shown.Add(state.StatusOf("x"));
        }
        // What the watcher would do: x is "unloaded" to it and still asked, so it sends /models/load for a model the
        // router runs (llama.cpp's router answers "model is already running": EngineException -> EngineState.Fail).
        var watcherWouldLoad = state.StatusOf("x") == "unloaded" && state.StillAsked("x");

        Assert.True(shown.All(s => s == "loaded") && !watcherWouldLoad,
            $"The router had x loaded; Admin -> Models showed it {string.Join(", then ", shown)} (10 s and 20 s later), " +
            $"and the watcher would load it again: {watcherWouldLoad}.");
    }

    /// <summary>
    /// Outcomes "a load the router lost in a restart" and "two admins at once", end to end with the fake router at one
    /// model at a time: admin A's Load is lost (the watcher keeps asking, EngineState.StillAsked); admin B then loads
    /// another model, which loads. A's Load, never seen loaded, is still followed up, and the watcher loads it, which
    /// unloads B's model: the two Loads fight, and the page shows B's model not loaded right after B loaded it.
    /// </summary>
    [Fact]
    public async Task At_one_model_at_a_time_a_followed_up_Load_undoes_a_later_admins_Load()
    {
        await using var f = await NewAppAsync(max: 1, keep: [], others: ["tiny-a", "tiny-b"]);
        var state = f.Services.GetRequiredService<EngineState>();
        try
        {
            await EventuallyAsync(() => state.StatusOf("tiny-a") == "unloaded" && state.StatusOf("tiny-b") == "unloaded", "the watcher lists tiny-a and tiny-b");
            var adminA = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword).WaitAsync(TimeSpan.FromSeconds(30));
            var adminB = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword).WaitAsync(TimeSpan.FromSeconds(30));

            // A loads tiny-a while the engine restarts: it answers and never loads it. The watcher asks again (StillAsked).
            app.Engine.LostLoads = 1000;
            await StatusAssert.Is(HttpStatusCode.Accepted, await adminA.PostAsync("/api/admin/models/tiny-a/load").WaitAsync(TimeSpan.FromSeconds(30)));
            await EventuallyAsync(() => app.Engine.LoadsOf("tiny-a") >= 2, "the watcher follows A's lost Load up");

            // The engine is back. B, seeing nothing load, loads tiny-b: it loads, in the one place (the big one makes room).
            app.Engine.LostLoads = 0;
            await StatusAssert.Is(HttpStatusCode.Accepted, await adminB.PostAsync("/api/admin/models/tiny-b/load").WaitAsync(TimeSpan.FromSeconds(30)));
            var stillAskedA = state.StillAsked("tiny-a");

            // B's Load is the last word. Watch the router for 15 seconds (the watcher looks every 3 to 10).
            var trail = new List<string>();
            var undone = false;
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < until && !undone)
            {
                var (a, b) = (app.Engine.StatusOf("tiny-a"), app.Engine.StatusOf("tiny-b"));
                var now = $"tiny-a {a}, tiny-b {b}";
                if (trail.Count == 0 || trail[^1] != now)
                {
                    trail.Add(now);
                }
                undone = b != "loaded" || a == "loaded";
                await Task.Delay(100);
            }
            // What the admins see once the watcher has looked again (it lags the router by 3 to 10 seconds).
            for (var i = 0; i < 150 && undone && state.StatusOf("tiny-a") != app.Engine.StatusOf("tiny-a"); i++)
            {
                await Task.Delay(100);
            }
            var page = await ModelsAsync(adminB);

            Assert.False(undone,
                $"Right after B's Load, StillAsked(tiny-a) = {stillAskedA}. The router went: {string.Join(" -> ", trail)}. " +
                $"Loads sent for tiny-a: {app.Engine.LoadsOf("tiny-a")}. Admin -> Models now shows tiny-a {Status(page, "tiny-a")}, tiny-b {Status(page, "tiny-b")}.");
        }
        finally
        {
            // The fake router is shared by the collection.
            app.Engine.LostLoads = 0;
        }
    }

    private static string? Status(JsonElement list, string name) =>
        list.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name).GetProperty("status").GetString();

    private static async Task<JsonElement> ModelsAsync(TestBrowser admin) =>
        await admin.JsonAsync(await admin.GetAsync("/api/admin/models").WaitAsync(TimeSpan.FromSeconds(30)));

    /// <summary>The watcher checks every few seconds; waits up to 30 seconds for what it should reach.</summary>
    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"Never: {what}");
    }

    /// <summary>Its own app and database, the fake router holding <paramref name="max"/> models, the big one and <paramref name="others"/> added.</summary>
    private async Task<WebApplicationFactory<Program>> NewAppAsync(int max, string[] keep, params string[] others)
    {
        var library = Path.Combine(_dir, "library");
        GgufFile.Language("qwen3", name: "Flash").Write(Path.Combine(library, "flash", "Flash-Q4_K_M.gguf"));
        for (var i = 0; i < others.Length; i++)
        {
            GgufFile.Language("qwen3", name: others[i], layerBytes: 4096L << i).Write(Path.Combine(library, others[i], "Tiny-Q4_K_M.gguf"));
        }
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, "keep"), string.Concat(keep.Select(k => k + "\n")));
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        app.Engine.Max = max;
        app.Engine.LostLoads = 0;
        var gateway = new FakeGateway();
        // The app registers its models at the gateway itself.
        gateway.Models.RemoveAll(m => m.Name == Big);
        var f = app.Create(app.ConnectionStringFor("stuck_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = library,
            ["Engine:ModelsMax"] = max.ToString(CultureInfo.InvariantCulture),
        });
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        db.LocalModels.Add(new LocalModel { Name = Big, File = "flash/Flash-Q4_K_M.gguf", Context = 8192, Parallel = 1 });
        foreach (var name in others)
        {
            db.LocalModels.Add(new LocalModel { Name = name, File = $"{name}/Tiny-Q4_K_M.gguf", Context = 8192, Parallel = 1 });
        }
        await db.SaveChangesAsync();
        // As an admin's save does: the engine's presets and the gateway at once.
        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        await catalog.WritePresetsAsync();
        await catalog.SyncGatewayAsync();
        return f;
    }
}
