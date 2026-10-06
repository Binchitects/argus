using System.Collections.Concurrent;
using Llm.Api.Models;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// Where the app's request to a model of this engine goes. Room first: when the model is not loaded
/// and the engine holds all it may, one of the models not kept loaded, idle, unloads (the smallest
/// first: the quickest to load again), so the engine does not unload the model used least recently
/// to load it, which may be the big model everyone uses. With none idle, the engine unloads the
/// first model to be idle, as it would without the app. Then its slot (<see cref="SlotTable"/>): for
/// a conversation's turn on a loaded model, the engine is asked first which slots are busy with
/// requests the table did not send (API keys, another replica): those are never chosen, nor slots
/// past the engine's count (a side request goes without a slot when its slot is past it).
/// </summary>
public sealed partial class EngineRoute(SlotTable slots, EngineState engine, EngineClient client, AnswerGate gate, IOptions<EngineOptions> options,
    TimeProvider clock, ILogger<EngineRoute> logger)
{
    /// <summary>When room was last made for each model: the requests that follow within a minute wait for its load, not for more room.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _room = new(StringComparer.Ordinal);

    /// <summary>The slot for a request to <paramref name="model"/>: <paramref name="conversation"/>'s turn, or a side request when null.</summary>
    public async Task<SlotTable.Lease> TakeAsync(string? model, Guid? conversation, CancellationToken ct)
    {
        if (model is null)
        {
            return slots.Take(null, conversation);
        }
        await RoomAsync(model, ct);
        var count = slots.Count(model);
        // A slot is chosen from what the engine has now: a model whose slots were just raised keeps its old count
        // until the engine restarts, and a request sent to a slot it does not have would wait there for ever.
        if ((conversation is not null ? count >= 2 : SlotTable.SideSlot(count) is not null) && engine.StatusOf(model) == "loaded"
            && await client.SlotsAsync(model, ct) is { } now)
        {
            if (conversation is null)
            {
                return now.Count >= count ? slots.Take(model, null) : slots.Take(null, null);
            }
            return slots.Take(model, conversation, new HashSet<int>([.. now.Busy, .. Enumerable.Range(now.Count, Math.Max(0, count - now.Count))]));
        }
        return slots.Take(model, conversation);
    }

    /// <summary>Unloads an idle model not kept loaded when <paramref name="model"/> needs its place.</summary>
    private async Task RoomAsync(string model, CancellationToken ct)
    {
        var now = engine.Now;
        if (!options.Value.Enabled || now is not { Error: null, At: not null } || engine.StatusOf(model) is not ("unloaded" or "failed")
            || now.Models.Count(m => m.Status is "loaded" or "loading") < options.Value.ModelsMax
            || (_room.TryGetValue(model, out var made) && clock.GetUtcNow() - made < TimeSpan.FromMinutes(1)))
        {
            return;
        }
        foreach (var spare in engine.Spare.Where(s => s != model && engine.StatusOf(s) == "loaded"))
        {
            // Idle: no answer of this replica holds a place on it, and the engine answers nothing on it now.
            if (gate.Now(spare).Total > 0 || await client.SlotsAsync(spare, ct) is not { Busy.Count: 0 })
            {
                continue;
            }
            _room[model] = clock.GetUtcNow();
            LogRoom(logger, spare, model);
            try
            {
                await client.UnloadAsync(spare, ct);
            }
            catch (EngineException ex)
            {
                // The engine makes room itself then.
                LogRoomFailed(logger, spare, ex.Message);
            }
            return;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Unloading {Spare}, idle, to make room for {Model}")]
    private static partial void LogRoom(ILogger logger, string spare, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not unload {Spare} to make room: {Message}")]
    private static partial void LogRoomFailed(ILogger logger, string spare, string message);
}
