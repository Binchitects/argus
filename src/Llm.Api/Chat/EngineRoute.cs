using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Llm.Api.Models;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// Where the app's request to a model of this engine goes. Room first: when the model is not loaded
/// and the engine holds all it may, a loaded model that may make room (not kept loaded, not the one
/// new chats use, not the model for small steps: <see cref="EngineState.Spare"/>, whatever its size)
/// and is idle unloads, the smallest first (the quickest to load again). While those that may are
/// busy, the request is not sent: the engine would unload the model used least recently to load it,
/// which may be the big one everyone is on. It waits a minute for one to be idle (an answer or a side
/// request alike), then says the engine is full; with none that may (every loaded one never makes
/// room), it says so at once. The place found or made is the model's for a few seconds, until the
/// engine loads it. An API key's request gets room the same way, when the gateway asks the app
/// first (<see cref="RoomForKeyAsync"/>). Then its slot (<see cref="SlotTable"/>), by what the engine said of the
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

    /// <summary>How long a request waits for room on a full engine before it says so.</summary>
    public static readonly TimeSpan RoomWait = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a model must have been seen idle before it makes room for one the watcher loads again (the model new
    /// chats use): an API key's agent, idle a moment between two of its requests, is not pushed out for it.
    /// </summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What the engine said of a model's slots, and when it was asked (a <see cref="TimeProvider.GetTimestamp"/>);
    /// <see cref="Seen"/> null: it says nothing of them (no /slots), so the table goes by itself.
    /// </summary>
    private sealed record View(SlotTable.Seen? Seen, long At);

    /// <summary>An ask of the engine about a model's slots, and when it began; its answer is null when the engine was too busy to give one.</summary>
    private sealed record Asking(Task<View?> Answer, long Began);

    private readonly ConcurrentDictionary<string, View> _views = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Asking> _asking = new(StringComparer.Ordinal);

    /// <summary>Since when each loaded model has been seen idle, each time the watcher looked for room (<see cref="Quiet"/>).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _quiet = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a place found or made for a model stays its own while its request is on the way to the engine (which then
    /// loads it): another model asked for meanwhile does not take it, nor have the engine unload one to load both.
    /// </summary>
    private static readonly TimeSpan OnItsWay = TimeSpan.FromSeconds(15);

    /// <summary>The models a place was found or made for, and when (<see cref="OnItsWay"/>).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _coming = new(StringComparer.Ordinal);

    /// <summary>What a look for room found: a place, or one made; models that may make room, all busy; none that may.</summary>
    private enum Room { Free, Made, Busy, None }

    /// <summary>The slot for a request to <paramref name="model"/>: <paramref name="conversation"/>'s turn, or a side request when null.</summary>
    public async Task<SlotTable.Lease> TakeAsync(string? model, Guid? conversation, CancellationToken ct)
    {
        if (!Ours(model))
        {
            // Not this engine's: the gateway's, or another GPU server's.
            return slots.Take(null, conversation);
        }
        await ReadyAsync(model, ct);
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

    /// <summary>
    /// Before an API key's request, when the gateway asks the app first (its guardrail): room for <paramref name="model"/>
    /// as for the app's own requests, so the engine never chooses which model unloads for it (the one used least recently
    /// may be the big one everyone is on). Null: the request may go; else why not (the engine is full, or the model failed
    /// to load and waits for its next try).
    /// </summary>
    public async Task<string?> RoomForKeyAsync(string? model, CancellationToken ct)
    {
        if (!Ours(model))
        {
            return null;
        }
        try
        {
            await ReadyAsync(model, ct);
            return null;
        }
        catch (ChatGatewayException ex) when (ex.NotLoaded)
        {
            return ex.Message;
        }
    }

    /// <summary>Whether <paramref name="model"/> is this engine's (not the gateway's, nor only another GPU server's).</summary>
    private bool Ours([NotNullWhen(true)] string? model) => model is not null && options.Value.Enabled && engine.StatusOf(model) is not null;

    /// <summary>
    /// <paramref name="model"/> about to be asked for: refused while it failed to load and waits for its next try (before
    /// any room is made for it), then room for it; a request for one that failed is a try.
    /// </summary>
    private async Task ReadyAsync(string model, CancellationToken ct)
    {
        // In use: not quiet.
        _quiet.TryRemove(model, out _);
        Refuse(model);
        await RoomAsync(model, ct);
        if (engine.StatusOf(model) == "failed")
        {
            Refuse(model);
            // This request has the engine load it again: a try, after which the next wait is longer.
            engine.Tried(model);
        }
    }

    /// <summary>Says so when <paramref name="model"/> failed to load and its wait for the next try is not over (EngineState.MayAsk).</summary>
    private void Refuse(string model)
    {
        if (engine.StatusOf(model) == "failed" && !engine.MayAsk(model))
        {
            throw new ChatGatewayException($"{model} could not be loaded just now. Try again in a few minutes, or choose another model.", 503) { NotLoaded = true };
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
    /// Unloads an idle model that may make room when <paramref name="model"/> needs its place; while those that may
    /// are busy, waits a minute for one; then, or at once when none may, says the engine is full (a
    /// <see cref="ChatGatewayException"/>, <see cref="ChatGatewayException.NotLoaded"/>).
    /// </summary>
    private async Task RoomAsync(string model, CancellationToken ct)
    {
        // Whether the engine is full is asked of it below: the watcher's list may be seconds old.
        if (engine.StatusOf(model) is not ("unloaded" or "failed") || engine.Now is not { Error: null, At: not null })
        {
            return;
        }
        var until = clock.GetUtcNow() + RoomWait;
        while (true)
        {
            var room = await LookAsync(model, TimeSpan.Zero, ct);
            if (room is Room.Free or Room.Made)
            {
                return;
            }
            if (room == Room.None)
            {
                LogFull(logger, model);
                throw new ChatGatewayException($"{model} cannot be loaded now: each place in the engine is taken by, or kept for, a model kept loaded or used by "
                    + "everyone (the model new chats use, the one for small steps). Choose a model that is loaded, or ask an admin to raise Models loaded at once.", 503) { NotLoaded = true };
            }
            if (clock.GetUtcNow() >= until)
            {
                LogFull(logger, model);
                throw new ChatGatewayException($"{model} cannot be loaded now: the engine holds all the models it may, and those that may make room have been in use "
                    + "all this minute. Try again in a few minutes, or choose a model that is loaded.", 503) { NotLoaded = true };
            }
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
        }
    }

    /// <summary>
    /// Room for <paramref name="model"/>, which the watcher loads again (the model new chats use, unloaded by the engine
    /// by its own choice to load another): true when the engine has a place for it, or a model that may make room has
    /// been idle each time it looked over the last <see cref="Quiet"/> and was unloaded.
    /// </summary>
    public async Task<bool> RoomForAsync(string model, CancellationToken ct) => await LookAsync(model, Quiet, ct) is Room.Free or Room.Made;

    /// <summary>
    /// One look at the engine as it is now: a place for <paramref name="model"/>, or one made by unloading an idle model
    /// that may make room (seen idle for <paramref name="quiet"/> at least); else whether any loaded model may.
    /// </summary>
    private async Task<Room> LookAsync(string model, TimeSpan quiet, CancellationToken ct)
    {
        IReadOnlyList<EngineModel> models;
        try
        {
            models = await client.ModelsAsync(ct);
        }
        catch (EngineException)
        {
            // The request says why itself.
            return Room.Free;
        }
        bool Up(string name) => models.FirstOrDefault(m => m.Name == name)?.Status is "loaded" or "loading";
        if (Up(model))
        {
            return Room.Free;
        }
        // A place just given to another model whose request is on its way (the engine does not load it yet) is taken.
        var now = clock.GetUtcNow();
        foreach (var (name, at) in _coming.Where(c => now - c.Value >= OnItsWay || Up(c.Key)))
        {
            _coming.TryRemove(new KeyValuePair<string, DateTimeOffset>(name, at));
        }
        var held = engine.Held;
        var coming = _coming.Keys.Count(k => k != model && !held.Contains(k));
        var others = models.Where(m => m.Status is "loaded" or "loading" && m.Name != model).ToList();
        // The places of the models that never make room: taken, or kept for them while they are not loaded (each loads again:
        // the watcher's, or the next small step's), unless an admin unloaded it. The others share what is left.
        var heldUp = others.Count(m => held.Contains(m.Name));
        var reserved = held.Count(h => h != model && !Up(h) && !engine.WasDropped(h) && models.Any(m => m.Name == h));
        var open = options.Value.ModelsMax - heldUp - reserved;
        var sparesUp = others.Count - heldUp;
        if (sparesUp + coming < open)
        {
            _coming[model] = now;
            return Room.Free;
        }
        if (open <= 0)
        {
            // Each place is taken by, or kept for, a model that never makes room: waiting would not help.
            return Room.None;
        }
        var loaded = others.Where(m => m.Status == "loaded").Select(m => m.Name).ToList();
        foreach (var gone in _quiet.Keys.Where(k => !loaded.Contains(k)))
        {
            _quiet.TryRemove(gone, out _);
        }
        // Those that may make room, smallest first, as many as it takes (one, unless some sit in places kept for a model that
        // never makes room). One loaded since the watcher last looked is neither yet: it is waited for.
        var needed = sparesUp + coming - open + 1;
        foreach (var spare in engine.Spare.Where(loaded.Contains).ToList())
        {
            if (!await IdleAsync(spare, ct))
            {
                _quiet.TryRemove(spare, out _);
                continue;
            }
            if (clock.GetUtcNow() - _quiet.GetOrAdd(spare, clock.GetUtcNow()) < quiet)
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
            _quiet.TryRemove(spare, out _);
            if (--needed == 0)
            {
                _coming[model] = clock.GetUtcNow();
                return Room.Made;
            }
        }
        return Room.Busy;
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
