using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Llm.Api.Models;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// Where the app's request to a model of this engine goes. Room first: when the model is not loaded
/// and the engine holds all it may, a loaded model that may make room (not kept loaded, not the one
/// new chats use, not the model for small steps while it keeps a place: <see cref="EngineState.Spare"/>,
/// whatever its size) and is idle unloads, the smallest first (the quickest to load again). While
/// those that may are busy, the request is not sent: the engine would unload the model used least
/// recently to load it, which may be the big one everyone is on. It waits a minute for one to be idle
/// (an answer or a side request alike), then says the engine is full; with none that may (every loaded
/// one never makes room), it says so at once. The place found or made is the model's for a few seconds,
/// until the engine loads it. Whether the model is loaded is what the engine said within the last second
/// (the watcher's list may be 10 seconds old), and a model the app told to unload counts as unloaded at
/// once, on every replica (<see cref="EngineState.Unloading"/>). A model is idle only when no request is
/// on its way to it: this replica's until they end, an API key's (or another replica's) for a few
/// seconds after it was let through (<see cref="Starting"/>), until the engine says its slot is busy. An
/// API key's request gets room the same way, when the gateway asks the app first
/// (<see cref="RoomForKeyAsync"/>). Then its slot (<see cref="SlotTable"/>), by what the engine said of the
/// model's slots: how many it has, and which are busy with requests not sent from here (API keys',
/// other replicas'). What it said within the last second is used as it is; else it is asked, and the
/// request waits half a second for its word. llama-server answers between two batches of its work,
/// so while it reads a long prompt it says nothing for seconds: then what it said within the last 5
/// seconds goes, else the request goes without a slot (the engine gives it an idle one, never a busy
/// one), and while one ask is still unanswered the requests that follow do not wait for it.
/// </summary>
public sealed partial class EngineRoute
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
    /// How long a request let through to a model it does not follow to the end (an API key's, another replica's) counts the
    /// model as in use, from when it was let through, or from when the model loaded for it: its way to the engine, and the
    /// age of what the engine said of the slots (<see cref="Recent"/>). By then the engine says its slot is busy.
    /// </summary>
    public static readonly TimeSpan Starting = TimeSpan.FromSeconds(10);

    /// <summary>How often this replica tells the others that a model that may make room was asked for (<see cref="Starting"/>).</summary>
    private static readonly TimeSpan TellEvery = TimeSpan.FromSeconds(2);

    private const string UnloadingTopic = "engine:unloading";
    private const string WantedTopic = "engine:wanted";

    private readonly SlotTable slots;
    private readonly EngineState engine;
    private readonly EngineClient client;
    private readonly AnswerGate gate;
    private readonly Replicas replicas;
    private readonly IOptions<EngineOptions> options;
    private readonly TimeProvider clock;
    private readonly ILogger<EngineRoute> logger;

    public EngineRoute(SlotTable slots, EngineState engine, EngineClient client, AnswerGate gate, Replicas replicas, IOptions<EngineOptions> options,
        TimeProvider clock, ILogger<EngineRoute> logger)
    {
        (this.slots, this.engine, this.client, this.gate, this.replicas, this.options, this.clock, this.logger) =
            (slots, engine, client, gate, replicas, options, clock, logger);
        // Another replica unloaded a model, or let a request for one through: the same here.
        replicas.On(UnloadingTopic, name =>
        {
            engine.Unloading(name);
            return true;
        });
        replicas.On(WantedTopic, name =>
        {
            _wanted[name] = clock.GetUtcNow();
            return true;
        });
    }

    /// <summary>
    /// What the engine said of a model's slots, and when it was asked (a <see cref="TimeProvider.GetTimestamp"/>);
    /// <see cref="Seen"/> null: it says nothing of them (no /slots), so the table goes by itself.
    /// </summary>
    private sealed record View(SlotTable.Seen? Seen, long At);

    /// <summary>An ask of the engine about a model's slots, and when it began; its answer is null when the engine was too busy to give one.</summary>
    private sealed record Asking(Task<View?> Answer, long Began);

    /// <summary>An ask of the engine for its models, and when it was asked (<see cref="EngineState.Asking"/>); null: no answer.</summary>
    private sealed record Listing(Task<IReadOnlyList<EngineModel>?> Models, DateTimeOffset Asked);

    private readonly ConcurrentDictionary<string, View> _views = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Asking> _asking = new(StringComparer.Ordinal);
    private readonly Lock _listing = new();
    private Listing? _listed;

    /// <summary>Since when each loaded model has been seen idle, each time the watcher looked for room (<see cref="Quiet"/>).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _quiet = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a place found or made for a model stays its own while its request is on the way to the engine (which then
    /// loads it): another model asked for meanwhile does not take it, nor have the engine unload one to load both.
    /// </summary>
    private static readonly TimeSpan OnItsWay = TimeSpan.FromSeconds(15);

    /// <summary>The models a place was found or made for, and when (<see cref="OnItsWay"/>).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _coming = new(StringComparer.Ordinal);

    /// <summary>This replica's requests on their way to each model, or answering (<see cref="TakeAsync"/>, until they end).</summary>
    private readonly ConcurrentDictionary<string, int> _sending = new(StringComparer.Ordinal);

    /// <summary>When a request for each model was last let through that this replica does not follow to the end: an API key's, another replica's (<see cref="Starting"/>).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _wanted = new(StringComparer.Ordinal);

    /// <summary>When this replica last told the others that each model was asked for (<see cref="TellEvery"/>).</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _told = new(StringComparer.Ordinal);

    /// <summary>What a look for room found: a place, or one made; models that may make room, all busy; none that may.</summary>
    private enum Room { Free, Made, Busy, None }

    /// <summary>A request to a model on its way, then answering: its slot (<see cref="Slot"/>; null: the engine chooses). Dispose it when the request ends.</summary>
    public sealed class Sending(Action? done) : IDisposable
    {
        private int _ended;

        internal SlotTable.Lease? Lease { get; set; }

        /// <summary>The slot to send as id_slot; null: send none.</summary>
        public int? Slot => Lease?.Slot;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Lease?.Dispose();
                done?.Invoke();
            }
        }
    }

    /// <summary>
    /// The way for a request to <paramref name="model"/>: <paramref name="conversation"/>'s turn, or a side request when
    /// null. The model is in use from now until the request ends (it is disposed): a look for room for another does not
    /// unload it meanwhile.
    /// </summary>
    public async Task<Sending> TakeAsync(string? model, Guid? conversation, CancellationToken ct)
    {
        if (!Ours(model))
        {
            // Not this engine's: the gateway's, or another GPU server's.
            return new Sending(null) { Lease = slots.Take(null, conversation) };
        }
        _sending.AddOrUpdate(model, 1, (_, n) => n + 1);
        var sending = new Sending(() => _sending.AddOrUpdate(model, 0, (_, n) => n - 1));
        try
        {
            // Counted before anything is looked at: a look for room either sees it, or this request sees the model unloading.
            Interlocked.MemoryBarrier();
            await ReadyAsync(model, ct);
            Tell(model);
            sending.Lease = await LeaseAsync(model, conversation, ct);
            return sending;
        }
        catch
        {
            sending.Dispose();
            throw;
        }
    }

    /// <summary>The slot for a request to <paramref name="model"/>, a model of this engine ready for it.</summary>
    private async Task<SlotTable.Lease> LeaseAsync(string model, Guid? conversation, CancellationToken ct)
    {
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
    /// may be the big one everyone is on). Null: the request may go, and its model counts as in use for a few seconds
    /// (<see cref="Starting"/>); else why not (the engine is full, or the model failed to load and waits for its next try).
    /// </summary>
    public async Task<string?> RoomForKeyAsync(string? model, CancellationToken ct)
    {
        if (!Ours(model))
        {
            return null;
        }
        try
        {
            // Wanted before anything is looked at, as TakeAsync counts its own; and again once let through.
            Want(model);
            await ReadyAsync(model, ct);
            Want(model);
            Tell(model);
            return null;
        }
        catch (ChatGatewayException ex) when (ex.NotLoaded)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The engine was told to unload <paramref name="model"/> (an admin's Unload, working hours that ended): it counts as
    /// unloaded at once, on every replica, until the engine has stopped it (<see cref="EngineState.Unloading"/>).
    /// </summary>
    public void Unloaded(string model)
    {
        engine.Unloading(model);
        replicas.Tell(UnloadingTopic, model);
    }

    /// <summary>Whether <paramref name="model"/> is this engine's (not the gateway's, nor only another GPU server's).</summary>
    private bool Ours([NotNullWhen(true)] string? model) => model is not null && options.Value.Enabled && engine.StatusOf(model) is not null;

    /// <summary>A request that this replica does not follow to the end was let through to <paramref name="model"/> (<see cref="Starting"/>).</summary>
    private void Want(string model)
    {
        _wanted[model] = clock.GetUtcNow();
        Interlocked.MemoryBarrier();
    }

    /// <summary>Tells the other replicas that <paramref name="model"/>, one that may make room, was asked for: they do not unload it for a few seconds.</summary>
    private void Tell(string model)
    {
        var now = clock.GetUtcNow();
        if (engine.Held.Contains(model) || (_told.TryGetValue(model, out var told) && now - told < TellEvery))
        {
            return;
        }
        _told[model] = now;
        replicas.Tell(WantedTopic, model);
    }

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
    /// Whether <paramref name="model"/> is loaded (or loading) now: what the watcher saw when it is under a second old,
    /// else what the engine said within the last second (asked again when older; the requests of that second share one
    /// ask). A model the app told to unload is unloaded (<see cref="EngineState.Unloading"/>).
    /// </summary>
    private async Task<string?> StatusAsync(string model, CancellationToken ct)
    {
        if (engine.Now.At is { } at && clock.GetUtcNow() - at < Fresh)
        {
            return engine.StatusOf(model);
        }
        Listing listed;
        lock (_listing)
        {
            if (_listed is not { } was || (was.Models.IsCompleted && clock.GetUtcNow() - was.Asked >= Fresh))
            {
                var asked = engine.Asking();
                _listed = new Listing(ListAsync(), asked);
            }
            listed = _listed;
        }
        return await listed.Models.WaitAsync(ct) is { } models
            ? engine.Seen(models, listed.Asked).FirstOrDefault(m => m.Name == model)?.Status
            : engine.StatusOf(model);
    }

    /// <summary>The engine's models; null when it cannot be asked (the request says why itself).</summary>
    private async Task<IReadOnlyList<EngineModel>?> ListAsync()
    {
        try
        {
            return await client.ModelsAsync(CancellationToken.None);
        }
        catch (EngineException)
        {
            return null;
        }
    }

    /// <summary>
    /// Unloads an idle model that may make room when <paramref name="model"/> needs its place; while those that may
    /// are busy, waits a minute for one; then, or at once when none may, says the engine is full (a
    /// <see cref="ChatGatewayException"/>, <see cref="ChatGatewayException.NotLoaded"/>).
    /// </summary>
    private async Task RoomAsync(string model, CancellationToken ct)
    {
        // Whether the engine is full is asked of it below.
        if (engine.Now is not { Error: null, At: not null } || await StatusAsync(model, ct) is not ("unloaded" or "failed"))
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
    /// by its own choice to load another; the model for small steps): true when the engine has a place for it, or a model
    /// that may make room has been idle each time it looked over the last <paramref name="quiet"/> and was unloaded.
    /// </summary>
    public async Task<bool> RoomForAsync(string model, TimeSpan quiet, CancellationToken ct) => await LookAsync(model, quiet, ct) is Room.Free or Room.Made;

    /// <summary>
    /// Whether a request for <paramref name="model"/> would go now without another model making room for it, by what the
    /// watcher saw last: it is loaded or loading, or not this engine's, or the engine has a place for it. The model for
    /// small steps is used only then: else each small step uses the answer's own model rather than wait.
    /// </summary>
    public bool PlaceFor(string model)
    {
        if (!Ours(model) || engine.StatusOf(model) is "loaded" or "loading")
        {
            return true;
        }
        var now = clock.GetUtcNow();
        var models = engine.Now.Models;
        bool Up(string name) => models.FirstOrDefault(m => m.Name == name)?.Status is "loaded" or "loading";
        var held = engine.Held;
        var up = models.Count(m => m.Status is "loaded" or "loading" && m.Name != model);
        var reserved = held.Count(h => h != model && !Up(h) && !engine.WasDropped(h) && models.Any(m => m.Name == h));
        var coming = _coming.Count(c => c.Key != model && now - c.Value < OnItsWay && !Up(c.Key) && !held.Contains(c.Key));
        return up + reserved + coming < options.Value.ModelsMax;
    }

    /// <summary>
    /// One look at the engine as it is now: a place for <paramref name="model"/>, or one made by unloading an idle model
    /// that may make room (seen idle for <paramref name="quiet"/> at least); else whether any loaded model may.
    /// </summary>
    private async Task<Room> LookAsync(string model, TimeSpan quiet, CancellationToken ct)
    {
        IReadOnlyList<EngineModel> models;
        try
        {
            var asked = engine.Asking();
            models = engine.Seen(await client.ModelsAsync(ct), asked);
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
        foreach (var (name, at) in _wanted.Where(w => now - w.Value >= Quiet))
        {
            _wanted.TryRemove(new KeyValuePair<string, DateTimeOffset>(name, at));
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
            Coming(model);
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
            // Unloaded from now on for every request; one that came for it meanwhile keeps it.
            engine.Unloading(spare);
            Interlocked.MemoryBarrier();
            if (InUse(spare))
            {
                engine.NotUnloading(spare);
                _quiet.TryRemove(spare, out _);
                continue;
            }
            LogRoom(logger, spare, model);
            try
            {
                await client.UnloadAsync(spare, ct);
            }
            catch (Exception ex) when (ex is EngineException or OperationCanceledException)
            {
                engine.NotUnloading(spare);
                if (ex is OperationCanceledException)
                {
                    throw;
                }
                LogRoomFailed(logger, spare, ex.Message);
                continue;
            }
            replicas.Tell(UnloadingTopic, spare);
            _quiet.TryRemove(spare, out _);
            if (--needed == 0)
            {
                Coming(model);
                return Room.Made;
            }
        }
        return Room.Busy;
    }

    /// <summary>A place was found or made for <paramref name="model"/>: its own until the engine loads it, and loaded from now on, though the app told it to unload a moment ago.</summary>
    private void Coming(string model)
    {
        _coming[model] = clock.GetUtcNow();
        engine.Loading(model);
    }

    /// <summary>
    /// Idle: no answer of this replica holds a place on it or waits for one, no request is on its way to it
    /// (<see cref="InUse"/>), and the engine says none of its slots is busy.
    /// </summary>
    private async Task<bool> IdleAsync(string model, CancellationToken ct) =>
        gate.Now(model).Total == 0 && !InUse(model) && await ViewAsync(model, ct) is { } view && view.Seen is not { Busy.Count: > 0 };

    /// <summary>
    /// In use by a request the engine may not be answering yet: one of this replica's, on its way or answering; one let
    /// through that this replica does not follow (an API key's, another replica's) within <see cref="Starting"/>, counted
    /// from when the model loaded for it when that is later. One loaded since the watcher last looked counts as in use
    /// too: the request it loaded for may not have reached it yet.
    /// </summary>
    private bool InUse(string model)
    {
        if (_sending.TryGetValue(model, out var running) && running > 0)
        {
            return true;
        }
        if (engine.LoadedSince(model) is not { } since)
        {
            return true;
        }
        return _wanted.TryGetValue(model, out var at) && clock.GetUtcNow() - (at > since ? at : since) < Starting;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Unloading {Spare}, idle, to make room for {Model}")]
    private static partial void LogRoom(ILogger logger, string spare, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not unload {Spare} to make room: {Message}")]
    private static partial void LogRoomFailed(ILogger logger, string spare, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The engine is full: {Model} was not loaded, as no model that may make room is idle")]
    private static partial void LogFull(ILogger logger, string model);
}
