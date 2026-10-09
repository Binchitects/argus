using System.Collections.Concurrent;
using Llm.Api.Models;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// Where the app's request to a model of this engine goes. Room first: when the model is not loaded
/// and the engine holds all it may, a loaded model that may make room (not kept loaded, not the one
/// new chats use, and no bigger than the model asked for: <see cref="EngineState.Spare"/>) and is
/// idle unloads, the smallest first (the quickest to load again). With none, the request is not
/// sent: the engine would unload the model used least recently to load it, which may be the big one
/// everyone is on. An answer waits a minute for one to be idle, then says the engine is full; a side
/// request says so at once. Then its slot (<see cref="SlotTable"/>), by what the engine said of the
/// model's slots: how many it has, and which are busy with requests not sent from here (API keys',
/// other replicas'). What it said within the last second is used as it is; else it is asked, and the
/// request waits half a second for its word. llama-server answers between two batches of its work,
/// so while it reads a long prompt it says nothing for seconds: then what it said within the last 5
/// seconds goes, else the request goes without a slot (the engine gives it an idle one, never a busy
/// one), and while one ask is still unanswered the requests that follow do not wait for it.
/// </summary>
public sealed partial class EngineRoute(SlotTable slots, EngineState engine, EngineClient client, AnswerGate gate, IOptions<EngineOptions> options,
    TimeProvider clock, ILogger<EngineRoute> logger)
{
    /// <summary>What the engine said of a model's slots within this long is used as it is.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1);

    /// <summary>How long a request waits for the engine's word on the slots.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(500);

    /// <summary>The oldest word of the engine a slot is chosen by.</summary>
    private static readonly TimeSpan Recent = TimeSpan.FromSeconds(5);

    /// <summary>How long the asking goes on in the background (for the requests that follow).</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>How long an answer waits for room on a full engine before it says so.</summary>
    public static readonly TimeSpan RoomWait = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What the engine said of a model's slots, and when it was asked (a <see cref="TimeProvider.GetTimestamp"/>);
    /// <see cref="Seen"/> null: it says nothing of them (no /slots), so the table goes by itself.
    /// </summary>
    private sealed record View(SlotTable.Seen? Seen, long At);

    /// <summary>An ask of the engine about a model's slots, and when it began; its answer is null when the engine was too busy to give one.</summary>
    private sealed record Asking(Task<View?> Answer, long Began);

    private readonly ConcurrentDictionary<string, View> _views = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Asking> _asking = new(StringComparer.Ordinal);

    /// <summary>When room was last made for each model: the requests that follow within a minute wait for its load, not for more room.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _room = new(StringComparer.Ordinal);

    /// <summary>The slot for a request to <paramref name="model"/>: <paramref name="conversation"/>'s turn, or a side request when null.</summary>
    public async Task<SlotTable.Lease> TakeAsync(string? model, Guid? conversation, CancellationToken ct)
    {
        if (model is null || !options.Value.Enabled || engine.StatusOf(model) is null)
        {
            // Not this engine's: the gateway's, or another GPU server's.
            return slots.Take(null, conversation);
        }
        // A model that failed to load is refused until its wait is over, before any room is made for it.
        Refuse(model);
        await RoomAsync(model, wait: conversation is not null, ct);
        if (engine.StatusOf(model) == "failed")
        {
            Refuse(model);
            // This request has the engine load it again: a try, after which the next wait is longer.
            engine.Tried(model);
        }
        if (!slots.Chooses(model, side: conversation is null))
        {
            return slots.Take(null, conversation);
        }
        if (engine.StatusOf(model) != "loaded")
        {
            // It loads for this request: its slots start empty (and the table with them), nothing to ask yet.
            return slots.Take(model, conversation);
        }
        return await ViewAsync(model, ct) is { } view ? slots.Take(model, conversation, view.Seen) : slots.Take(null, conversation);
    }

    /// <summary>Says so when <paramref name="model"/> failed to load and its wait for the next try is not over (EngineState.MayAsk).</summary>
    private void Refuse(string model)
    {
        if (engine.StatusOf(model) == "failed" && !engine.MayAsk(model))
        {
            throw new ChatGatewayException($"{model} could not be loaded just now. Try again in a few minutes, or choose another model.", 503);
        }
    }

    /// <summary>What the engine said of a model's slots, lately; null: nothing recent (it is too busy to say now).</summary>
    private async Task<View?> ViewAsync(string model, CancellationToken ct)
    {
        if (_views.TryGetValue(model, out var known) && clock.GetElapsedTime(known.At) < Fresh)
        {
            return known;
        }
        var asking = _asking.AddOrUpdate(model, _ => new Asking(AskAsync(model), clock.GetTimestamp()),
            (_, was) => was.Answer.IsCompleted ? new Asking(AskAsync(model), clock.GetTimestamp()) : was);
        // An ask still unanswered after the wait: the engine is busy, and this request does not wait for it again.
        var left = Quick - clock.GetElapsedTime(asking.Began);
        if (left > TimeSpan.Zero && await Task.WhenAny(asking.Answer, Task.Delay(left, clock, ct)) == asking.Answer && await asking.Answer is { } said)
        {
            return said;
        }
        ct.ThrowIfCancellationRequested();
        return _views.TryGetValue(model, out var last) && clock.GetElapsedTime(last.At) < Recent ? last : null;
    }

    /// <summary>
    /// Asks the engine about a model's slots; its word serves this request and those of the next second. Null when it
    /// did not say within <see cref="Patience"/>: too busy, which says nothing of whether it has /slots.
    /// </summary>
    private async Task<View?> AskAsync(string model)
    {
        var asked = clock.GetTimestamp();
        var said = await client.SlotsAsync(model, Patience, CancellationToken.None);
        if (said is null && clock.GetElapsedTime(asked) >= Patience)
        {
            return null;
        }
        var view = new View(said is { } s ? new SlotTable.Seen(s.Count, s.Busy, asked) : null, asked);
        _views[model] = view;
        return view;
    }

    /// <summary>
    /// Unloads an idle model that may make room when <paramref name="model"/> needs its place; with none, waits for
    /// one (<paramref name="wait"/>: an answer) or says the engine is full (a <see cref="ChatGatewayException"/>).
    /// </summary>
    private async Task RoomAsync(string model, bool wait, CancellationToken ct)
    {
        // Whether the engine is full is asked of it below: the watcher's list may be seconds old.
        if (engine.StatusOf(model) is not ("unloaded" or "failed") || engine.Now is not { Error: null, At: not null }
            || (_room.TryGetValue(model, out var made) && clock.GetUtcNow() - made < TimeSpan.FromMinutes(1)))
        {
            return;
        }
        var until = clock.GetUtcNow() + (wait ? RoomWait : TimeSpan.Zero);
        var size = engine.Sizes.GetValueOrDefault(model, long.MaxValue);
        while (true)
        {
            // The engine's list as it is now.
            IReadOnlyList<EngineModel> models;
            try
            {
                models = await client.ModelsAsync(ct);
            }
            catch (EngineException)
            {
                // The request says why itself.
                return;
            }
            if (models.FirstOrDefault(m => m.Name == model)?.Status is "loaded" or "loading"
                || models.Count(m => m.Status is "loaded" or "loading") < options.Value.ModelsMax)
            {
                return;
            }
            foreach (var spare in engine.Spare.Where(s => s != model && engine.Sizes.GetValueOrDefault(s, long.MaxValue) <= size
                && models.Any(m => m.Name == s && m.Status == "loaded")))
            {
                if (!await IdleAsync(spare, ct))
                {
                    continue;
                }
                LogRoom(logger, spare, model);
                try
                {
                    await client.UnloadAsync(spare, ct);
                }
                catch (EngineException ex)
                {
                    LogRoomFailed(logger, spare, ex.Message);
                    continue;
                }
                _room[model] = clock.GetUtcNow();
                return;
            }
            if (clock.GetUtcNow() >= until)
            {
                LogFull(logger, model);
                throw new ChatGatewayException($"{model} cannot be loaded now: the engine holds all the models it may, and those loaded are in use, "
                    + "kept loaded or bigger. Try again in a few minutes, or choose a model that is loaded.", 503);
            }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }
    }

    /// <summary>Idle: no answer of this replica holds a place on it or answers in a slot of it, and the engine says none of its slots is busy.</summary>
    private async Task<bool> IdleAsync(string model, CancellationToken ct) =>
        gate.Now(model).Total == 0 && !slots.Answering(model) && await ViewAsync(model, ct) is { } view && view.Seen is not { Busy.Count: > 0 };

    [LoggerMessage(Level = LogLevel.Information, Message = "Unloading {Spare}, idle, to make room for {Model}")]
    private static partial void LogRoom(ILogger logger, string spare, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not unload {Spare} to make room: {Message}")]
    private static partial void LogRoomFailed(ILogger logger, string spare, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine is full: {Model} was not loaded, as no model that may make room is idle")]
    private static partial void LogFull(ILogger logger, string model);
}
