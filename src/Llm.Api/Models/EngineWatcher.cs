using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Api.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// Keeps the engine as admins chose: the models kept loaded loaded (again after
/// the engine restarts, or after a model loaded on request pushed one out), and
/// with 2 or more at once the model new chats use too (after the engine unloaded it
/// by its own choice to load another), and the model for small steps (in its place,
/// or in the one it shares, once that is free or idle a while); the presets and the gateway in step with
/// the database, and Prometheus scraping whichever models are loaded. Checks every
/// 10 seconds, every 3 while a model loads. A model that fails to load is tried
/// again after a minute, then less and less often (not every few seconds); when
/// every kept model failed, the .env model takes their place.
/// With several replicas, only the one that leads changes anything; the others
/// only read what the engine has loaded (their lines, slot table and model list need it).
/// </summary>
public sealed partial class EngineWatcher : BackgroundService
{
    private const string WakeTopic = "engine:wake";
    private readonly SemaphoreSlim _wake = new(0);
    /// <summary>The loaded and held models <see cref="EngineState.Spare"/> was worked out for.</summary>
    private string? _spareFor;
    private readonly IServiceScopeFactory scopes;
    private readonly EngineClient engine;
    private readonly EngineState state;
    private readonly ChatModels chatModels;
    private readonly AnswerGate gate;
    private readonly SlotTable slotTable;
    private readonly EngineRoute route;
    private readonly Replicas replicas;
    private readonly IOptions<EngineOptions> options;
    private readonly ILogger<EngineWatcher> logger;

    public EngineWatcher(IServiceScopeFactory scopes, EngineClient engine, EngineState state, ChatModels chatModels, AnswerGate gate, SlotTable slotTable, EngineRoute route,
        Replicas replicas, IOptions<EngineOptions> options, ILogger<EngineWatcher> logger)
    {
        (this.scopes, this.engine, this.state, this.chatModels, this.gate, this.slotTable, this.route, this.replicas, this.options, this.logger) =
            (scopes, engine, state, chatModels, gate, slotTable, route, replicas, options, logger);
        replicas.On(WakeTopic, _ =>
        {
            WakeHere();
            return true;
        });
    }

    /// <summary>Check now (after an admin loads or unloads a model), on whichever replica leads.</summary>
    public void Wake()
    {
        WakeHere();
        replicas.Tell(WakeTopic);
    }

    private void WakeHere()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            await GatewayOnlyAsync(stoppingToken);
            return;
        }
        var nextSync = DateTimeOffset.MinValue;
        var loaded = "";
        string? reported = null;
        // The working hours in force last round, and what they kept: at a change, what is no longer kept unloads.
        (Guid? Window, IReadOnlyList<string> Kept)? before = null;
        var led = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var loading = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
                var hours = await scope.ServiceProvider.GetRequiredService<ModelHours>().RefreshAsync(stoppingToken);
                var lead = replicas.IsLeader;
                if (lead && !led)
                {
                    // Taking over: the files and the gateway are written again now, as at a start.
                    (nextSync, loaded) = (DateTimeOffset.MinValue, "");
                }
                led = lead;
                // At start, then every minute: a gateway that was down, or restarted without the app's models, catches up.
                if (lead && DateTimeOffset.UtcNow >= nextSync)
                {
                    catalog.WriteMax();
                    await catalog.WritePresetsAsync(stoppingToken);
                    await catalog.SyncGatewayAsync(stoppingToken);
                    nextSync = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                var asked = state.Asking();
                state.Set(await engine.ModelsAsync(stoppingToken), asked);
                // As the engine said, with the models the app told to unload unloaded (the engine lists them loaded until they stop).
                var models = state.Now.Models;
                loading = models.Any(m => m.Status == "loading");
                string? Status(string name) => models.FirstOrDefault(m => m.Name == name)?.Status;
                // Never more than the engine holds at once: past that, each load would unload another kept model.
                var kept = catalog.Kept().Where(k => Status(k) is not null).Take(options.Value.ModelsMax).ToList();
                if (lead && before is { } was && (was.Window != hours.Window?.Id || (hours.Window is not null && !was.Kept.SequenceEqual(kept))))
                {
                    // Working hours began, ended or changed: the models only they kept make room (not on the app's
                    // first round, when nothing is known of what kept them).
                    foreach (var gone in was.Kept.Where(k => !kept.Contains(k) && Status(k) == "loaded"))
                    {
                        LogHoursUnload(logger, gone, hours.Window?.Name ?? "the pinned models");
                        // Unloading for every request before the engine is told.
                        route.Unloaded(gone, hours.Window is { } w ? $"working hours \"{w.Name}\" do not keep it" : "working hours ended");
                        try
                        {
                            await engine.UnloadAsync(gone, stoppingToken);
                        }
                        catch (EngineException)
                        {
                            state.NotUnloading(gone);
                            throw;
                        }
                    }
                    chatModels.Forget();
                }
                before = (hours.Window?.Id, kept);
                // The model new chats use, and those that never make room for another (EngineRoute).
                var small = scope.ServiceProvider.GetRequiredService<SmallModel>().Name;
                var usual = await UsualAsync(scope.ServiceProvider, hours.Window?.DefaultModel, small, Status, stoppingToken);
                var held = Held(kept, usual, small, options.Value.ModelsMax, Status, state.LoadedInstead);
                state.Default = usual;
                state.Small = small;
                var names = models.Where(m => m.Status == "loaded").Select(m => m.Name).ToList();
                // What each model serves at once: its line's places, and the slots conversations keep; and which may make room.
                await CapacityAsync(scope.ServiceProvider, true, names, held, stoppingToken);
                if (lead && !loading)
                {
                    // One at a time, each through the app's room-making (an idle model that may make room unloads first), never
                    // by the engine's own choice of the model used least recently, which may be another that never makes room.
                    // One that failed is tried again once its wait is over: the router also marks failed a model it had to
                    // kill while it was being stopped, which is not broken (EngineState.MayRetry). One the engine keeps
                    // unloading by its own choice to load another waits too (EngineState.MayReload): it holds fewer models
                    // than Models loaded at once says, and two would push each other out for ever.
                    var keptNext = kept.FirstOrDefault(k => Status(k) == "unloaded" && Reload(k)) ?? kept.FirstOrDefault(state.MayRetry);
                    var askedNext = models.FirstOrDefault(m => m.Status == "unloaded" && state.StillAsked(m.Name) && Reload(m.Name))?.Name;
                    if (keptNext is { } next)
                    {
                        if (await route.RoomForAsync(next, TimeSpan.Zero, stoppingToken))
                        {
                            LogLoading(logger, next);
                            loading = await LoadAsync(next, "kept loaded", stoppingToken);
                        }
                    }
                    else if (askedNext is { } asked2 && await route.RoomForAsync(asked2, TimeSpan.Zero, stoppingToken))
                    {
                        // An admin's Load the engine lost (it restarted under it).
                        LogAsked(logger, asked2);
                        loading = await LoadAsync(asked2, "loaded by an admin, again after the engine restarted", stoppingToken);
                    }
                    else if (options.Value.ModelsMax >= 2 && usual is { } back && held.Contains(back) && !state.WasDropped(back)
                        && ((Status(back) == "unloaded" && Reload(back)) || state.MayRetry(back))
                        && await route.RoomForAsync(back, EngineRoute.Quiet, stoppingToken))
                    {
                        // The model new chats use, unloaded by the engine to load another (a request that did not ask the app
                        // first): back, in a place left free or made by a model that may make room, idle a minute.
                        LogBack(logger, back);
                        loading = await LoadAsync(back, "the model new chats use", stoppingToken);
                    }
                    else if (options.Value.ModelsMax >= 2 && small is { } helper && helper != usual && !state.WasDropped(helper)
                        && ((Status(helper) == "unloaded" && Reload(helper)) || state.MayRetry(helper))
                        && await route.RoomForAsync(helper, held.Contains(helper) ? EngineRoute.Quiet : SmallQuiet, stoppingToken))
                    {
                        // The model for small steps: in the place kept for it, or, when it shares the last place with the models
                        // loaded on request, in that place while it is free, or once the model there has been idle a while.
                        LogSmall(logger, helper);
                        loading = await LoadAsync(helper, "the model for small steps", stoppingToken);
                    }
                    else if (kept.Count > 0 && kept.All(k => Status(k) == "failed") && !models.Any(m => m.Status == "loaded"))
                    {
                        // Every kept model failed to load (each is tried again after a while, not every few seconds).
                        if (reported != string.Join(',', kept))
                        {
                            LogNothing(logger, string.Join(", ", kept));
                            reported = string.Join(',', kept);
                        }
                    }
                }
                var now = string.Join(',', names.Order(StringComparer.Ordinal));
                if (now != loaded)
                {
                    if (lead)
                    {
                        catalog.WriteTargets(names);
                    }
                    chatModels.Forget();
                    loaded = now;
                }
            }
            catch (EngineException ex)
            {
                state.Fail(ex.Message);
            }
            catch (GatewayException ex)
            {
                // Tried again next round.
                LogGateway(logger, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFiles(logger, options.Value.ConfigDir, ex.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the app keeps running: a background failure must never stop it.
                LogFailed(logger, ex);
            }
            try
            {
                await _wake.WaitAsync(loading ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Whether the watcher may load <paramref name="model"/> again now (EngineState.MayReload), said once when it may not.</summary>
    private bool Reload(string model)
    {
        if (state.MayReload(model))
        {
            _flapping.Remove(model);
            return true;
        }
        if (_flapping.Add(model))
        {
            LogFlapping(logger, model, state.Evicted(model), options.Value.ModelsMax);
        }
        return false;
    }

    /// <summary>The models <see cref="Reload"/> said wait.</summary>
    private readonly HashSet<string> _flapping = new(StringComparer.Ordinal);

    /// <summary>
    /// Has the engine load <paramref name="model"/> (why: for Admin → Models): true when it does. The router answering that it
    /// runs it already is no failure; any other refusal is logged and tried again later, without marking the engine down.
    /// </summary>
    private async Task<bool> LoadAsync(string model, string why, CancellationToken ct)
    {
        if (state.StatusOf(model) == "failed")
        {
            state.Tried(model);
        }
        state.Loading(model, why);
        try
        {
            await engine.LoadAsync(model, ct);
        }
        catch (EngineException ex) when (ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            // Loaded or loading already: what it was asked for.
        }
        catch (EngineException ex) when (!ex.Message.StartsWith("The engine is not reachable", StringComparison.Ordinal))
        {
            LogLoadRefused(logger, model, ex.Message);
            return false;
        }
        return true;
    }

    /// <summary>
    /// What each chat model serves at once, for the answers' lines (AnswerGate) and the engine's slots
    /// (SlotTable): a model of this engine alone, its parallel slots; a model with copies on other GPU
    /// servers, the slots of all its copies (the gateway spreads the requests among them), unless a copy
    /// does not say how many it serves; any other, no limit.
    /// </summary>
    public static (Dictionary<string, int> Places, Dictionary<string, int> Slots) Capacity(IEnumerable<(string Name, int Parallel)> local, IEnumerable<(string Name, int? Parallel)> remote)
    {
        var (places, slots) = (new Dictionary<string, int>(StringComparer.Ordinal), new Dictionary<string, int>(StringComparer.Ordinal));
        var copies = remote.GroupBy(r => r.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(r => r.Parallel).ToList(), StringComparer.Ordinal);
        foreach (var (name, parallel) in local)
        {
            var here = Math.Max(1, parallel);
            if (!copies.Remove(name, out var elsewhere))
            {
                slots[name] = here;
                places[name] = here;
            }
            else if (elsewhere.All(p => p > 0))
            {
                places[name] = here + elsewhere.Sum(p => p!.Value);
            }
        }
        foreach (var (name, elsewhere) in copies.Where(c => c.Value.All(p => p > 0)))
        {
            places[name] = elsewhere.Sum(p => p!.Value);
        }
        return (places, slots);
    }

    /// <summary>
    /// The loaded models that may make room for another (EngineRoute), the quickest to load again first: not
    /// <paramref name="held"/>, smallest file first whatever the size of the one asked for (a model the app did
    /// not add, the .env one, last).
    /// </summary>
    public static IReadOnlyList<string> SpareOf(IEnumerable<string> loaded, IReadOnlyCollection<string> held, IReadOnlyDictionary<string, long> sizes) =>
        [.. loaded.Where(n => !held.Contains(n)).OrderBy(n => sizes.TryGetValue(n, out var size) ? size : long.MaxValue).ThenBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// The models that never make room for another (EngineState.Held), at most as many as the engine holds: those
    /// <paramref name="kept"/> loaded, those an admin loaded <paramref name="instead"/> of one of these, then the one new
    /// chats use (<paramref name="usual"/>), each the engine's own
    /// (<paramref name="status"/> knows it); then the model for small steps (<paramref name="small"/>), only while a
    /// place is left beside it for the others: else it shares that place with them, making room once idle, and comes
    /// back to it once free (or once the model there has been idle a while). With one model at a time, only the kept.
    /// </summary>
    public static IReadOnlyList<string> Held(IReadOnlyList<string> kept, string? usual, string? small, int max, Func<string, string?> status,
        IReadOnlyList<string>? instead = null)
    {
        // An admin's Load in place of one of them comes before the model new chats use and the one for small steps.
        var first = kept.Concat((instead ?? []).Where(n => status(n) is not null)).Distinct(StringComparer.Ordinal).ToList();
        if (max < 2)
        {
            return [.. first.Take(max)];
        }
        var held = first.Concat(new[] { usual }.OfType<string>().Where(n => status(n) is not null)).Distinct(StringComparer.Ordinal).Take(max).ToList();
        if (small is not null && status(small) is not null && !held.Contains(small) && held.Count + 1 < max)
        {
            held.Add(small);
        }
        return held;
    }

    /// <summary>
    /// How long the model loaded in the place the model for small steps shares with the others must have been idle before
    /// it makes room for it: longer than for the model new chats use, as small steps meanwhile use the answer's own model.
    /// </summary>
    public static readonly TimeSpan SmallQuiet = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The engine model new chats use (EngineState.Default): the working hours' default, the admin's (Settings), the
    /// first kept; with none of them (or Auto with nothing kept), the one new chats have been getting while it stays on
    /// the engine (an admin's Unload ends that), else the one they get now while it is loaded. Never the model for
    /// small steps: with nothing else loaded new chats get it, but only until another loads. Null: none of the engine's.
    /// </summary>
    private async Task<string?> UsualAsync(IServiceProvider services, string? hoursDefault, string? small, Func<string, string?> status, CancellationToken ct)
    {
        if ((hoursDefault ?? chatModels.DefaultName) is { } named)
        {
            return status(named) is not null ? named : null;
        }
        if (state.Default is { } was && was != small && status(was) is not null && !state.WasDropped(was))
        {
            return was;
        }
        var policy = services.GetRequiredService<ModelPolicy>();
        var onEngine = await policy.OnEngineAsync(ct);
        return policy.DefaultOf(await chatModels.ListAsync(ct), onEngine)?.Name is { } now && now != small && onEngine.Contains(now) && status(now) == "loaded" ? now : null;
    }

    /// <summary>
    /// Each model's places and slots from the database, on every replica (each keeps its own lines and table),
    /// and the loaded models that may make room for another (<paramref name="held"/>: those that never do).
    /// </summary>
    private async Task CapacityAsync(IServiceProvider services, bool onEngine, IReadOnlyList<string> loaded, IReadOnlyCollection<string> held, CancellationToken ct)
    {
        var db = services.GetRequiredService<Llm.Core.Data.AppDbContext>();
        var models = onEngine ? await db.LocalModels.AsNoTracking().Select(m => new { m.Name, m.Parallel, m.File }).ToListAsync(ct) : [];
        var remote = (await db.RemoteServers.AsNoTracking().ToListAsync(ct)).SelectMany(s => s.Models).Select(m => (m.Name, m.Parallel));
        var (places, slots) = Capacity(models.Select(m => (m.Name, m.Parallel)), remote);
        gate.SetPlaces(places);
        slotTable.SetModels(slots, loaded.ToHashSet(StringComparer.Ordinal));
        // Read again only when the models, what is loaded or what is held changed: the library's files are listed for their sizes.
        var spareFor = string.Join(',', loaded.Order(StringComparer.Ordinal)) + "|" + string.Join(',', held.Order(StringComparer.Ordinal)) + "|"
            + string.Join(',', models.Select(m => m.Name + "=" + m.File).Order(StringComparer.Ordinal));
        if (onEngine && spareFor != _spareFor)
        {
            var files = services.GetRequiredService<ModelLibrary>().List().ToDictionary(e => e.File.Path, e => e.File.Size, StringComparer.Ordinal);
            var sizes = models.Where(m => files.ContainsKey(m.File)).ToDictionary(m => m.Name, m => files[m.File], StringComparer.Ordinal);
            state.Held = held;
            state.Spare = SpareOf(loaded, held, sizes);
            _spareFor = spareFor;
        }
    }

    /// <summary>Without the llama.cpp engine, the other GPU servers' models still reach the gateway: every minute.</summary>
    private async Task GatewayOnlyAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (replicas.IsLeader)
                {
                    await scope.ServiceProvider.GetRequiredService<ModelCatalog>().SyncGatewayAsync(stoppingToken);
                }
                await CapacityAsync(scope.ServiceProvider, false, [], [], stoppingToken);
            }
            catch (GatewayException ex)
            {
                LogGateway(logger, ex.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Working hours: {Model} unloads, {Now} take over")]
    private static partial void LogHoursUnload(ILogger logger, string model, string now);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine: loading {Model}, a model kept loaded")]
    private static partial void LogLoading(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine: loading {Model} again, the model new chats use")]
    private static partial void LogBack(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine: loading {Model} again, the model for small steps")]
    private static partial void LogSmall(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine: loading {Model} again, an admin's Load the engine lost")]
    private static partial void LogAsked(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: the engine refused to load {Model}: {Reason}")]
    private static partial void LogLoadRefused(ILogger logger, string model, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: the engine unloaded {Model} by its own choice {Count} times lately, to load another: it holds fewer than the {Max} models at once the app was told (check --models-max and the GPU memory); it waits before it is loaded again")]
    private static partial void LogFlapping(ILogger logger, string model, int count, int max);

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine: {Model} failed to load (the engine's log says why); load another from Admin -> Models")]
    private static partial void LogNothing(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: the gateway's model list could not be brought in step: {Reason}")]
    private static partial void LogGateway(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine: the check failed; trying again")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: cannot write to {Dir}: {Reason}")]
    private static partial void LogFiles(ILogger logger, string dir, string reason);
}
