using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// A model moved to another engine at runtime (ModelMove): llama.cpp to vLLM or SGLang and back, vLLM to SGLang and back,
/// with the stack running. An admin's save begins it (<see cref="Begin"/>); the watcher, on the replica that leads, carries
/// it on a step each pass (<see cref="StepAsync"/>). Its old engine keeps it until its answers in flight there have ended
/// (no busy slot, no request running in it), then lets it go. Its new engine loads it at once when they share no GPU (or
/// the old one did not have it loaded), else once the old one has let it go; meanwhile its requests wait (EngineRoute).
/// Ready there, the move is over. Should it fail to start there (or not start within Engine:VllmStartWait), it is put back
/// on its old engine as it was, loaded again if it was, and the move says why.
/// </summary>
public sealed partial class ModelMoves(AppDbContext db, ModelCatalog catalog, EngineClient engines, EngineState state, EngineRoute route, VllmBackends vllm,
    IOptions<EngineOptions> options, Audit audit, TimeProvider clock, ILogger<ModelMoves> logger)
{
    /// <summary>How long its answers on its old engine may go on before the old engine lets it go all the same.</summary>
    public static readonly TimeSpan Drain = TimeSpan.FromMinutes(15);

    /// <summary>How long its old engine may take to stop it once told (llama.cpp kills one within 10 seconds, vLLM within its drain).</summary>
    private static readonly TimeSpan StopWait = TimeSpan.FromMinutes(2);

    /// <summary>A failure its new engine lists this soon after it was asked to load it may be its last load's: looked at again.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>The engine a model's settings put it on, by name: llama.cpp's, its vLLM or SGLang engine; null: none of this stack.</summary>
    public static string? HomeOf(LocalModel m, VllmBackends vllm) =>
        EngineKinds.ServerPerModel(m.EngineKind) ? vllm.For(m.Engine, m.EngineKind)?.Name : m.EngineKind == EngineKinds.LlamaCpp ? EngineKinds.LlamaCpp : null;

    /// <summary>A kind's name for people: llama.cpp, vLLM, SGLang.</summary>
    public static string Label(string kind) => EngineKinds.Of(kind)?.Label ?? kind;

    /// <summary>A model's settings on the engine it leaves, while that engine still has it; null: none.</summary>
    public static LocalModel? Leaving(LocalModel m) =>
        m.Move is { Phase: not ModelMove.Failed, Old: not ModelMove.Gone, From: { } from } ? JsonSerializer.Deserialize<LocalModel>(from) : null;

    /// <summary>
    /// Whether two settings of a model share a GPU: one that names none runs on every GPU (llama.cpp's way), as on a machine
    /// whose GPUs are not known.
    /// </summary>
    public static bool Overlap(LocalModel a, LocalModel b, Hardware? hw)
    {
        if (hw?.Devices is not { Count: > 0 } devices)
        {
            return true;
        }
        IReadOnlyList<int> all = [.. devices.Select(d => d.Index)];
        return (Hardware.ParseDevices(a.Devices) ?? all).Intersect(Hardware.ParseDevices(b.Devices) ?? all).Any();
    }

    /// <summary>
    /// Why <paramref name="m"/> cannot move to its new engine now (it does not run, or its feature is off), or null. A move to an
    /// engine that cannot load it would only fail there.
    /// </summary>
    public static string? Refusal(LocalModel m, VllmBackends vllm, EngineOptions options, Operations.Features features)
    {
        if (!EngineKinds.ServerPerModel(m.EngineKind))
        {
            if (!options.LlamaCpp)
            {
                return "llama.cpp does not run on this stack.";
            }
            return features.Runs(EngineKinds.LlamaCpp) || Operations.Features.All.FirstOrDefault(f => f.Services.Contains(EngineKinds.LlamaCpp)) is not { } feature
                ? null
                : $"llama.cpp is off on this stack (its feature, {feature.Id}): the installer turns it on with ./arenactl repair --with {feature.Id} from the checkout, or sh arena-{AppInfo.Current.Version}-offline.run repair --with {feature.Id} with the offline installer. It cannot move there before it runs.";
        }
        if (vllm.For(m.Engine, m.EngineKind) is not { } backend)
        {
            return $"There is no {Label(m.EngineKind)} engine {(m.Engine is { } name ? $"named {name} " : "")}on this stack.";
        }
        return backend.Down() is { } why ? $"{why} It cannot move there before it runs." : null;
    }

    /// <summary>
    /// The move of a model from <paramref name="before"/> (its settings on its old engine, whose last word of it was
    /// <paramref name="status"/>) to its new settings, <paramref name="after"/>: its answers there end first when the two
    /// share a GPU and it was loaded there, else it loads on its new engine at once.
    /// </summary>
    public static ModelMove Begin(LocalModel before, LocalModel after, string? status, Hardware? hw, VllmBackends vllm, string? by, DateTimeOffset now)
    {
        var there = status is "loaded" or "loading" or "sleeping" or "unloading";
        return new ModelMove
        {
            From = JsonSerializer.Serialize(before), FromKind = before.EngineKind, FromEngine = HomeOf(before, vllm) ?? before.EngineKind, ToKind = after.EngineKind,
            ToEngine = HomeOf(after, vllm) ?? after.EngineKind, Phase = there && Overlap(before, after, hw) ? ModelMove.Draining : ModelMove.Loading,
            Old = there ? ModelMove.Serving : ModelMove.Gone, WasLoaded = there && status != "unloading", By = by, Since = now, At = now,
        };
    }

    /// <summary>
    /// The engine each model runs on (EngineClient.Homes) and the moves underway (EngineState.Moves), from the database: every
    /// replica, each pass, so each request goes by them.
    /// </summary>
    public static async Task PublishAsync(AppDbContext db, EngineClient engines, EngineState state, VllmBackends vllm, CancellationToken ct)
    {
        var models = await db.LocalModels.AsNoTracking().ToListAsync(ct);
        engines.Homes = models.Select(m => (m.Name, Home: HomeOf(m, vllm))).Where(m => m.Home is not null)
            .ToDictionary(m => m.Name, m => m.Home!, StringComparer.Ordinal);
        state.Moves = models.Where(m => m.Move is { Phase: not ModelMove.Failed }).ToDictionary(m => m.Name, m => View(m.Move!), StringComparer.Ordinal);
    }

    /// <summary>A move as requests need it.</summary>
    public static EngineState.Moving View(ModelMove move) => new(move.Phase, move.FromKind, move.ToKind);

    /// <summary>Each move underway a step on: what its old engine and its new one say of it decides the next.</summary>
    public async Task StepAsync(CancellationToken ct)
    {
        foreach (var m in (await db.LocalModels.ToListAsync(ct)).Where(m => m.Move is { Phase: not ModelMove.Failed }))
        {
            await StepAsync(m, ct);
        }
    }

    private async Task StepAsync(LocalModel m, CancellationToken ct)
    {
        var move = m.Move!;
        var now = clock.GetUtcNow();
        var (changed, presets) = (false, false);
        var old = engines.Backend(move.FromEngine);
        // Its old engine: its answers there end, then it lets the model go, and its presets no longer have it.
        if (move.Old == ModelMove.Serving)
        {
            var there = engines.StatusOn(move.FromEngine, m.Name);
            if (old is null || there is not ("loaded" or "loading" or "sleeping") || now - move.Since >= Drain || await IdleAsync(old, m.Name, move, now, ct))
            {
                if (old is not null && there is "loaded" or "loading" or "sleeping")
                {
                    try
                    {
                        await old.UnloadAsync(m.Name, ct);
                    }
                    catch (EngineException ex)
                    {
                        // It stops with its engine all the same (one not running has it no more).
                        LogUnloadRefused(logger, m.Name, move.FromEngine, ex.Message);
                    }
                }
                (move.Old, move.OldAt, changed) = (ModelMove.Stopping, now, true);
                LogLeft(logger, m.Name, move.FromEngine);
            }
        }
        if (move.Old == ModelMove.Stopping
            && (engines.StatusOn(move.FromEngine, m.Name) is not ("loaded" or "loading" or "sleeping" or "unloading") || now - (move.OldAt ?? now) >= StopWait))
        {
            (move.Old, changed, presets) = (ModelMove.Gone, true, true);
        }
        // Its new engine: it loads there (once the old one has let it go, when they share a GPU), and is ready, or fails.
        if (move.Phase == ModelMove.Draining && move.Old == ModelMove.Gone)
        {
            (move.Phase, move.At, changed) = (ModelMove.Loading, now, true);
        }
        if (move.Phase == ModelMove.Loading)
        {
            var status = state.StatusOf(m.Name);
            if (status is "loaded" or "sleeping")
            {
                (move.Phase, move.At, changed) = (ModelMove.Ready, now, true);
            }
            else if (status == "failed" && move.LoadAt is { } asked && now - asked >= Settle)
            {
                await FailAsync(m, Why(m, move), ct);
                return;
            }
            else if (now - move.At >= options.Value.VllmStartWait)
            {
                await FailAsync(m, move.LoadAt is null
                    ? $"Its GPUs on {Label(move.ToKind)} had no room for it within {options.Value.VllmStartWait.TotalMinutes:0} minutes: the models there are answering or kept loaded."
                    : $"It did not start on {Label(move.ToKind)} within {options.Value.VllmStartWait.TotalMinutes:0} minutes.", ct);
                return;
            }
            else if (status is null)
            {
                // Not listed by its new engine yet (llama.cpp reads a new model as it starts again): asked once it is.
            }
            else if (move.LoadAt is null)
            {
                if (await route.RoomForAsync(m.Name, TimeSpan.Zero, ct))
                {
                    state.Loading(m.Name, $"moved from {Label(move.FromKind)}{(move.By is { } by ? $" by {by}" : "")}");
                    // Followed up should the engine lose the load (llama.cpp starting again).
                    state.Asked(m.Name);
                    try
                    {
                        await engines.BackendOf(m.Name).LoadAsync(m.Name, ct);
                    }
                    catch (EngineException ex) when (!ex.Message.StartsWith("The engine is not reachable", StringComparison.Ordinal)
                        && !ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
                    {
                        await FailAsync(m, $"{Label(move.ToKind)} refused it: {ex.Message}", ct);
                        return;
                    }
                    catch (EngineException)
                    {
                        // Starting again, or loading it already: the watcher asks again while it lists it unloaded.
                    }
                    (move.LoadAt, changed) = (now, true);
                }
            }
            else if (status == "unloaded" && !state.StillAsked(m.Name))
            {
                // Its load was lost (its engine started again, or this replica took over the lead): the watcher asks again.
                state.Asked(m.Name);
            }
        }
        if (move.Phase == ModelMove.Ready && move.Old == ModelMove.Gone)
        {
            m.Move = null;
            await db.SaveChangesAsync(ct);
            state.Move(m.Name, null);
            await catalog.WritePresetsAsync(ct);
            await catalog.WriteVllmPresetsAsync(ct);
            await audit.WriteAsync("model.moved", m.Name, detail: $"{Label(move.FromKind)} to {Label(move.ToKind)}");
            LogMoved(logger, m.Name, move.ToEngine);
            return;
        }
        if (changed)
        {
            await db.SaveChangesAsync(ct);
            state.Move(m.Name, View(move));
        }
        if (presets)
        {
            await catalog.WritePresetsAsync(ct);
            await catalog.WriteVllmPresetsAsync(ct);
        }
    }

    /// <summary>
    /// Whether its old engine has no answer of it in flight: no busy slot, no request running or waiting in it, as the engine
    /// says now. Not before <see cref="EngineRoute.Starting"/> has passed since the move began: a request let through just
    /// before it may not be busy there yet.
    /// </summary>
    private static async Task<bool> IdleAsync(IEngineBackend old, string model, ModelMove move, DateTimeOffset now, CancellationToken ct) =>
        now - move.Since >= EngineRoute.Starting && await old.CapacityAsync(model, TimeSpan.FromSeconds(2), ct) is { Running: 0, Waiting: 0, Busy.Count: 0 };

    /// <summary>Why its new engine did not start it: the last line of its server's log (vLLM, SGLang), else where llama.cpp's says.</summary>
    private string Why(LocalModel m, ModelMove move)
    {
        if (vllm.All.FirstOrDefault(b => b.Name == move.ToEngine) is { } backend
            && backend.Presets().FirstOrDefault(e => e.Model == m.Name) is { } entry
            && backend.StatusOf(entry.Name)?["log"] is JsonArray log
            && log.Select(l => l?.GetValue<string>()?.Trim()).LastOrDefault(l => !string.IsNullOrEmpty(l)) is { } last)
        {
            return $"{Label(move.ToKind)} could not start it: {last}";
        }
        return $"{Label(move.ToKind)} could not start it: its log says why (Admin → Logs, {move.ToEngine}).";
    }

    /// <summary>
    /// The move failed: the model is put back on its old engine as it was (loaded again there if it was), its new engine lets
    /// it go, the gateway sends its requests to its old engine again, and the move says why.
    /// </summary>
    private async Task FailAsync(LocalModel m, string why, CancellationToken ct)
    {
        var move = m.Move!;
        var from = JsonSerializer.Deserialize<LocalModel>(move.From!)!;
        ModelEndpoints.Assign(m, from);
        m.Move = new ModelMove
        {
            FromKind = move.FromKind, FromEngine = move.FromEngine, ToKind = move.ToKind, ToEngine = move.ToEngine, Phase = ModelMove.Failed, Old = ModelMove.Gone,
            WasLoaded = move.WasLoaded, Error = why, By = move.By, Since = move.Since, At = clock.GetUtcNow(),
        };
        m.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        engines.Home(m.Name, move.FromEngine);
        state.Move(m.Name, null);
        state.Forget(m.Name);
        try
        {
            await (engines.Backend(move.ToEngine)?.UnloadAsync(m.Name, ct) ?? Task.CompletedTask);
        }
        catch (EngineException)
        {
            // Not loaded there: nothing to let go.
        }
        await catalog.WritePresetsAsync(ct);
        await catalog.WriteVllmPresetsAsync(ct);
        await catalog.SyncGatewayAsync(ct);
        if (move.WasLoaded && move.Old != ModelMove.Serving)
        {
            // Loaded on its old engine again, followed up while that engine has not read it back (llama.cpp starts again).
            state.Asked(m.Name);
        }
        await audit.WriteAsync("model.move_failed", m.Name, success: false, detail: $"{Label(move.FromKind)} to {Label(move.ToKind)}: {why}");
        LogFailed(logger, m.Name, move.ToEngine, why);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine: {Model} left {Engine}, its answers there ended")]
    private static partial void LogLeft(ILogger logger, string model, string engine);

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine: {Model} moved to {Engine}")]
    private static partial void LogMoved(ILogger logger, string model, string engine);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: {Model} could not move to {Engine}, it stays where it was: {Why}")]
    private static partial void LogFailed(ILogger logger, string model, string engine, string why);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: {Engine} refused to unload {Model}, which leaves it: {Why}")]
    private static partial void LogUnloadRefused(ILogger logger, string model, string engine, string why);
}
