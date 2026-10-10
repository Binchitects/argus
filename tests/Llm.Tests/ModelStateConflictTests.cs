using System.Net;
using System.Net.Http.Json;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Llm.Tests;

/// <summary>
/// "Two models conflict": a kept model, the model new chats use and an admin's Load of a third never evict each other in a
/// loop: the app makes room (never the engine's own choice of the model used least recently), an admin who loads a model in
/// place of one that never makes room says so (Load instead), and a model the engine keeps pushing out waits. Each test
/// samples the engine's states and counts the loads.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ModelStateConflictTests(AppFixture app, ITestOutputHelper output) : IDisposable
{
    private const string Big = "Qwen3.8-Flash-Next";
    private const string Kept = "tiny-k";
    private const string Third = "tiny-x";
    private static readonly string[] All = [Big, Kept, Third];
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-conflict-").FullName;

    private string Config => Path.Combine(_dir, "config");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Its own app: Big (the model new chats use, Chat:DefaultModel in the fixture), tiny-k and tiny-x, <paramref name="keep"/>
    /// kept loaded, the app told <paramref name="appMax"/> at once and the engine holding <paramref name="engineMax"/>.
    /// </summary>
    private WebApplicationFactory<Program> NewApp(int appMax, int engineMax, string[] keep, MovableClock clock)
    {
        var library = Path.Combine(_dir, "library");
        GgufFile.Language("qwen3", name: "Flash", layerBytes: 1 << 16).Write(Path.Combine(library, "flash", "Flash-Q4_K_M.gguf"));
        GgufFile.Language("qwen3", name: Kept, layerBytes: 4096).Write(Path.Combine(library, Kept, "Tiny-Q4_K_M.gguf"));
        GgufFile.Language("qwen3", name: Third, layerBytes: 8192).Write(Path.Combine(library, Third, "Tiny-Q4_K_M.gguf"));
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, "keep"), string.Concat(keep.Select(k => k + "\n")));
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        app.Engine.Max = engineMax;
        var gateway = new FakeGateway();
        gateway.Models.RemoveAll(m => m.Name == Big);
        var f = app.Create(app.ConnectionStringFor("conflict_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = library,
            ["Engine:ModelsMax"] = appMax.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, s => s.AddSingleton<TimeProvider>(clock));
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        db.LocalModels.Add(new LocalModel { Name = Big, File = "flash/Flash-Q4_K_M.gguf", Context = 8192, Parallel = 1 });
        db.LocalModels.Add(new LocalModel { Name = Kept, File = $"{Kept}/Tiny-Q4_K_M.gguf", Context = 8192, Parallel = 1 });
        db.LocalModels.Add(new LocalModel { Name = Third, File = $"{Third}/Tiny-Q4_K_M.gguf", Context = 8192, Parallel = 1 });
        db.SaveChanges();
        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        catalog.WritePresetsAsync().GetAwaiter().GetResult();
        catalog.SyncGatewayAsync().GetAwaiter().GetResult();
        return f;
    }

    /// <summary>Samples the fake engine's states every 25 ms and keeps each change, as "model: from -> to".</summary>
    private sealed class Timeline : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        private readonly List<string> _changes = [];

        public Timeline(FakeEngine engine)
        {
            var last = All.ToDictionary(m => m, engine.StatusOf);
            _run = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    foreach (var m in All)
                    {
                        var now = engine.StatusOf(m);
                        if (now != last[m])
                        {
                            lock (_changes)
                            {
                                _changes.Add($"{m}: {last[m]} -> {now}");
                            }
                            last[m] = now;
                        }
                    }
                    try
                    {
                        await Task.Delay(25, _stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
        }

        public List<string> Changes
        {
            get
            {
                lock (_changes)
                {
                    return [.. _changes];
                }
            }
        }

        /// <summary>How many times <paramref name="model"/> went from loaded to unloaded.</summary>
        public int Evictions(string model) => Changes.Count(c => c == $"{model}: loaded -> unloaded");

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
            _stop.Dispose();
        }
    }

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

    /// <summary>The watcher looks a few times (as it would every few seconds), the clock still.</summary>
    private static async Task LooksAsync(EngineWatcher watcher, int times = 6)
    {
        for (var i = 0; i < times; i++)
        {
            watcher.Wake();
            await Task.Delay(400);
        }
    }

    private void Report(string title, Timeline timeline, Dictionary<string, int> loadsBefore)
    {
        output.WriteLine($"== {title}");
        foreach (var c in timeline.Changes)
        {
            output.WriteLine("  " + c);
        }
        foreach (var m in All)
        {
            output.WriteLine($"  {m}: loads sent since the admin's Load = {app.Engine.LoadsOf(m) - loadsBefore[m]}, evictions seen = {timeline.Evictions(m)}, now {app.Engine.StatusOf(m)}");
        }
        output.WriteLine($"  unload calls: {app.Engine.CallsTo("/models/unload")}");
    }

    /// <summary>
    /// Two at once (app and engine agree), tiny-k kept, Big the model new chats use (held too): every place is held. The
    /// admin's Load of tiny-x is refused, naming Big as the one it may take the place of; loaded instead of Big, it stays.
    /// Watched over five minutes (the clock moved past EngineRoute.Quiet five times): no loop of loads and unloads.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_at_once_a_kept_model_the_default_and_an_admins_Load_do_not_evict_each_other(bool keptUsedLeastRecently)
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = NewApp(appMax: 2, engineMax: 2, keep: [Kept], clock);
        var state = f.Services.GetRequiredService<EngineState>();
        var watcher = f.Services.GetRequiredService<EngineWatcher>();
        var engine = f.Services.GetRequiredService<EngineClient>();
        await EventuallyAsync(() => state.Held.SequenceEqual([Kept, Big]) && app.Engine.StatusOf(Kept) == "loaded" && app.Engine.StatusOf(Big) == "loaded",
            "the kept model and the default both loaded, both held");
        if (keptUsedLeastRecently)
        {
            // Someone's chat on Big a moment ago: the kept model is the one used least recently.
            await engine.LoadAsync(Big);
        }
        var loadsBefore = All.ToDictionary(m => m, app.Engine.LoadsOf);
        await using var timeline = new Timeline(app.Engine);

        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var held = await admin.PostAsync($"/api/admin/models/{Third}/load").WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        var refusal = await admin.JsonAsync(held);
        Assert.Equal("held", refusal.GetProperty("status").GetString());
        Assert.Equal([Big], refusal.GetProperty("holding").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal("loaded", app.Engine.StatusOf(Big));
        var load = await admin.PostAsync($"/api/admin/models/{Third}/load?instead=true").WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine($"admin Load {Third} instead of {Big}: {(int)load.StatusCode}");
        await LooksAsync(watcher);
        output.WriteLine($"after the Load (clock still): {string.Join(", ", All.Select(m => $"{m}={app.Engine.StatusOf(m)}"))}");
        for (var window = 0; window < 5; window++)
        {
            clock.Now += EngineRoute.Quiet + TimeSpan.FromSeconds(1);
            await LooksAsync(watcher);
            output.WriteLine($"after Quiet window {window + 1}: {string.Join(", ", All.Select(m => $"{m}={app.Engine.StatusOf(m)}"))}");
        }
        Report($"two at once, kept used least recently = {keptUsedLeastRecently}", timeline, loadsBefore);

        // No loop: each model is loaded again at most once after the admin's Load, and none is evicted twice.
        foreach (var m in All)
        {
            Assert.True(app.Engine.LoadsOf(m) - loadsBefore[m] <= 1, $"{m} was loaded {app.Engine.LoadsOf(m) - loadsBefore[m]} times: a loop");
            Assert.True(timeline.Evictions(m) <= 1, $"{m} was evicted {timeline.Evictions(m)} times: a loop");
        }
        // The admin's Load was accepted; what it loaded is not taken away again by the app (the conflict the user sees), and
        // the kept model was never touched.
        Assert.Equal(HttpStatusCode.Accepted, load.StatusCode);
        Assert.Equal("loaded", app.Engine.StatusOf(Third));
        Assert.Equal("loaded", app.Engine.StatusOf(Kept));
        Assert.Equal(0, timeline.Evictions(Kept));
        Assert.Equal(0, app.Engine.LoadsOf(Big) - loadsBefore[Big]);
    }

    /// <summary>
    /// One at a time (app and engine agree), nothing kept, Big the default: the admin loads tiny-x. Then with tiny-k
    /// kept, the admin's Load is refused. No loop either way.
    /// </summary>
    [Fact]
    public async Task One_at_once_an_admins_Load_beside_the_default_settles_without_a_loop()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = NewApp(appMax: 1, engineMax: 1, keep: [], clock);
        var state = f.Services.GetRequiredService<EngineState>();
        var watcher = f.Services.GetRequiredService<EngineWatcher>();
        await EventuallyAsync(() => state.Default == Big && app.Engine.StatusOf(Big) == "loaded", "Big loaded, the model new chats use");
        var loadsBefore = All.ToDictionary(m => m, app.Engine.LoadsOf);
        await using var timeline = new Timeline(app.Engine);

        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var load = await admin.PostAsync($"/api/admin/models/{Third}/load").WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine($"admin Load {Third}: {(int)load.StatusCode}");
        await LooksAsync(watcher);
        for (var window = 0; window < 5; window++)
        {
            clock.Now += EngineRoute.Quiet + TimeSpan.FromSeconds(1);
            await LooksAsync(watcher);
        }
        Report("one at once, nothing kept", timeline, loadsBefore);
        foreach (var m in All)
        {
            Assert.True(app.Engine.LoadsOf(m) - loadsBefore[m] <= 1, $"{m} was loaded {app.Engine.LoadsOf(m) - loadsBefore[m]} times: a loop");
            Assert.True(timeline.Evictions(m) <= 1, $"{m} was evicted {timeline.Evictions(m)} times: a loop");
        }
        Assert.Equal(HttpStatusCode.Accepted, load.StatusCode);
        Assert.Equal("loaded", app.Engine.StatusOf(Third));

        // Kept now: the one place is the kept model's, and the admin's Load of another is refused at once.
        var keep = await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/models/{Kept}/keep", UriKind.Relative), new { keep = true })
            .WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine($"keep {Kept}: {(int)keep.StatusCode}");
        await EventuallyAsync(() => app.Engine.StatusOf(Kept) == "loaded", "the kept model loaded");
        var refused = await admin.PostAsync($"/api/admin/models/{Third}/load").WaitAsync(TimeSpan.FromSeconds(30));
        output.WriteLine($"admin Load {Third} with {Kept} kept: {(int)refused.StatusCode}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// The app told 2 at once while the engine holds 1 (the window between a changed "Models loaded at once" and the
    /// engine's restart, or an engine started with another --models-max): the kept model and the default evict each other.
    /// </summary>
    [Fact]
    public async Task When_the_engine_holds_fewer_than_the_app_thinks_the_kept_model_and_the_default_evict_each_other()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = NewApp(appMax: 2, engineMax: 1, keep: [Kept], clock);
        var watcher = f.Services.GetRequiredService<EngineWatcher>();
        var loadsBefore = All.ToDictionary(m => m, _ => 0);
        await using var timeline = new Timeline(app.Engine);
        // No admin action at all: the watcher alone, for about ten seconds.
        await LooksAsync(watcher, 25);
        Report("app 2 at once, engine 1", timeline, loadsBefore);
        Assert.True(app.Engine.LoadsOf(Kept) <= 2, $"{Kept} was loaded {app.Engine.LoadsOf(Kept)} times in ten seconds: a loop");
        Assert.True(app.Engine.LoadsOf(Big) <= 2, $"{Big} was loaded {app.Engine.LoadsOf(Big)} times in ten seconds: a loop");
    }
}
