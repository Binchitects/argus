using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Api.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// Keeps the engine as admins chose: the models kept loaded loaded (again after
/// the engine restarts, or after a model loaded on request pushed one out), the
/// presets and the gateway in step with the database, and Prometheus scraping
/// whichever models are loaded. Checks every 10 seconds, every 3 while a model
/// loads. A model that fails to load is tried again after a minute, then less and
/// less often (not every few seconds); when every kept model failed, the .env model
/// takes their place.
/// With several replicas, only the one that leads changes anything; the others
/// only read what the engine has loaded (their lines, slot table and model list need it).
/// </summary>
public sealed partial class EngineWatcher : BackgroundService
{
    private const string WakeTopic = "engine:wake";
    private readonly SemaphoreSlim _wake = new(0);
    /// <summary>The loaded, kept and default models <see cref="EngineState.Spare"/> was worked out for.</summary>
    private string? _spareFor;
    private readonly IServiceScopeFactory scopes;
    private readonly EngineClient engine;
    private readonly EngineState state;
    private readonly ChatModels chatModels;
    private readonly AnswerGate gate;
    private readonly SlotTable slotTable;
    private readonly Replicas replicas;
    private readonly IOptions<EngineOptions> options;
    private readonly ILogger<EngineWatcher> logger;

    public EngineWatcher(IServiceScopeFactory scopes, EngineClient engine, EngineState state, ChatModels chatModels, AnswerGate gate, SlotTable slotTable, Replicas replicas,
        IOptions<EngineOptions> options, ILogger<EngineWatcher> logger)
    {
        (this.scopes, this.engine, this.state, this.chatModels, this.gate, this.slotTable, this.replicas, this.options, this.logger) =
            (scopes, engine, state, chatModels, gate, slotTable, replicas, options, logger);
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
                var models = await engine.ModelsAsync(stoppingToken);
                state.Set(models);
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
                        await engine.UnloadAsync(gone, stoppingToken);
                    }
                    chatModels.Forget();
                }
                before = (hours.Window?.Id, kept);
                if (lead && !loading)
                {
                    // One at a time: a load at the engine's limit first unloads the model used least recently,
                    // which may be a kept one that sat idle; it comes back on a later round.
                    // One that failed is tried again once its wait is over: the router also marks failed a model it
                    // had to kill while it was being stopped, which is not broken (EngineState.MayRetry).
                    if ((kept.FirstOrDefault(k => Status(k) == "unloaded") ?? kept.FirstOrDefault(state.MayRetry)) is { } next)
                    {
                        LogLoading(logger, next);
                        if (Status(next) == "failed")
                        {
                            state.Tried(next);
                        }
                        await engine.LoadAsync(next, stoppingToken);
                        loading = true;
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
                var names = models.Where(m => m.Status == "loaded").Select(m => m.Name).ToList();
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
                // What each model serves at once: its line's places, and the slots conversations keep; and which may make room
                // (never a kept one, nor the one new chats use: the big model everyone is on).
                await CapacityAsync(scope.ServiceProvider, true, names,
                    [.. catalog.Kept(), .. new[] { chatModels.DefaultName, hours.Window?.DefaultModel }.OfType<string>()], stoppingToken);
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
    /// <paramref name="kept"/> (kept loaded, or the chat's default), smallest file first (a model the app did not
    /// add, the .env one, last).
    /// </summary>
    public static IReadOnlyList<string> SpareOf(IEnumerable<string> loaded, IReadOnlyCollection<string> kept, IReadOnlyDictionary<string, long> sizes) =>
        [.. loaded.Where(n => !kept.Contains(n)).OrderBy(n => sizes.TryGetValue(n, out var size) ? size : long.MaxValue).ThenBy(n => n, StringComparer.Ordinal)];

    /// <summary>
    /// Each model's places and slots from the database, on every replica (each keeps its own lines and table),
    /// and the loaded models that may make room for another (<paramref name="kept"/>: those that never do).
    /// </summary>
    private async Task CapacityAsync(IServiceProvider services, bool onEngine, IReadOnlyList<string> loaded, IReadOnlyCollection<string> kept, CancellationToken ct)
    {
        var db = services.GetRequiredService<Llm.Core.Data.AppDbContext>();
        var models = onEngine ? await db.LocalModels.AsNoTracking().Select(m => new { m.Name, m.Parallel, m.File }).ToListAsync(ct) : [];
        var remote = (await db.RemoteServers.AsNoTracking().ToListAsync(ct)).SelectMany(s => s.Models).Select(m => (m.Name, m.Parallel));
        var (places, slots) = Capacity(models.Select(m => (m.Name, m.Parallel)), remote);
        gate.SetPlaces(places);
        slotTable.SetModels(slots, loaded.ToHashSet(StringComparer.Ordinal));
        // Read again only when the models, what is loaded or what is kept changed: the library's files are listed for their sizes.
        var spareFor = string.Join(',', loaded.Order(StringComparer.Ordinal)) + "|" + string.Join(',', kept.Order(StringComparer.Ordinal)) + "|"
            + string.Join(',', models.Select(m => m.Name + "=" + m.File).Order(StringComparer.Ordinal));
        if (onEngine && spareFor != _spareFor)
        {
            var files = services.GetRequiredService<ModelLibrary>().List().ToDictionary(e => e.File.Path, e => e.File.Size, StringComparer.Ordinal);
            var sizes = models.Where(m => files.ContainsKey(m.File)).ToDictionary(m => m.Name, m => files[m.File], StringComparer.Ordinal);
            state.Sizes = sizes;
            state.Spare = SpareOf(loaded, kept, sizes);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine: {Model} failed to load (the engine's log says why); load another from Admin -> Models")]
    private static partial void LogNothing(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: the gateway's model list could not be brought in step: {Reason}")]
    private static partial void LogGateway(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine: the check failed; trying again")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: cannot write to {Dir}: {Reason}")]
    private static partial void LogFiles(ILogger logger, string dir, string reason);
}
