namespace Llm.Api.Chat;

/// <summary>
/// The engine's slots, per model, and which conversation each holds, so the engine reads a turn's
/// prompt from its cache. A conversation (or a sub-agent's run) goes back to the slot it used last
/// while that slot still holds it and is idle: only the new turn is read. Otherwise it takes the
/// idle slot used least recently, which holds another conversation's start, so the shared system
/// prompt is read from there. A busy slot is never chosen: with none idle, the request goes without
/// a slot and the engine gives it the first that frees. Side requests (titles, the safeguards'
/// check, compaction summaries, Auto's choice) keep to the last slot of a model with 3 or more, so
/// they never push a conversation out of its slot nor take an answer's place; with fewer, the
/// engine places them. A model with one slot, or with copies on other GPU servers (the gateway
/// chooses the copy), gets no slot from here. With several replicas each keeps its own table (and
/// asks the engine which slots are busy before choosing).
/// </summary>
public sealed class SlotTable(TimeProvider clock)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Model> _models = new(StringComparer.Ordinal);
    private HashSet<string> _loaded = new(StringComparer.Ordinal);

    /// <summary>A model's slots: who each holds, when each was last used, and whether it is answering now.</summary>
    private sealed class Model(int count)
    {
        public int Count { get; } = count;
        public Guid?[] Holder { get; } = new Guid?[count];
        public long[] Used { get; } = new long[count];
        public int[] Busy { get; } = new int[count];
    }

    /// <summary>A slot taken for one request (or none: <see cref="Slot"/> null); give it back by disposing it.</summary>
    public sealed class Lease(SlotTable table, string? model, int? slot) : IDisposable
    {
        private int _released;

        /// <summary>The slot to send as id_slot; null: send none, the engine chooses.</summary>
        public int? Slot { get; } = slot;

        public void Dispose()
        {
            if (Slot is { } s && Interlocked.Exchange(ref _released, 1) == 0)
            {
                table.Release(model!, s);
            }
        }
    }

    /// <summary>The slot kept for side requests: the last, on a model with 3 or more; else none.</summary>
    public static int? SideSlot(int slots) => slots >= 3 ? slots - 1 : null;

    /// <summary>The answers a model of this many slots runs at once: all but the side requests' slot.</summary>
    public static int Places(int slots) => SideSlot(slots) is null ? slots : slots - 1;

    /// <summary>
    /// The models whose slots are chosen here (those on this engine alone), with their slots. A model
    /// whose count changed, or that was unloaded since the last call (the engine emptied its slots),
    /// starts empty; a model not named is forgotten.
    /// </summary>
    public void SetModels(IReadOnlyDictionary<string, int> slots, IReadOnlySet<string> loaded)
    {
        lock (_lock)
        {
            foreach (var name in _models.Keys.Where(n => !slots.ContainsKey(n)).ToList())
            {
                _models.Remove(name);
            }
            foreach (var (name, count) in slots)
            {
                if (!_models.TryGetValue(name, out var m) || m.Count != count || (_loaded.Contains(name) && !loaded.Contains(name)))
                {
                    // Requests still running keep their slot as busy: a new table starts with them.
                    var fresh = new Model(Math.Max(0, count));
                    if (m is not null)
                    {
                        for (var i = 0; i < Math.Min(m.Count, fresh.Count); i++)
                        {
                            fresh.Busy[i] = m.Busy[i];
                        }
                    }
                    _models[name] = fresh;
                }
            }
            _loaded = new HashSet<string>(loaded, StringComparer.Ordinal);
        }
    }

    /// <summary>The slots of a model chosen here; 0: none (the engine or the gateway chooses).</summary>
    public int Count(string model)
    {
        lock (_lock)
        {
            return _models.GetValueOrDefault(model)?.Count ?? 0;
        }
    }

    /// <summary>
    /// A slot for one request to <paramref name="model"/>: for <paramref name="conversation"/>'s turn, or a side
    /// request when it is null. <paramref name="busyElsewhere"/>: slots the engine says are busy with requests
    /// not sent from here (API keys, another replica).
    /// </summary>
    public Lease Take(string? model, Guid? conversation, IReadOnlySet<int>? busyElsewhere = null)
    {
        lock (_lock)
        {
            if (model is null || !_models.TryGetValue(model, out var m) || m.Count < 2)
            {
                return new Lease(this, model, null);
            }
            var side = SideSlot(m.Count);
            int? slot;
            if (conversation is not { } c)
            {
                // A side request: its own slot, waiting there for the one before; none on a model of two slots.
                slot = side;
            }
            else
            {
                bool Idle(int i) => i != side && m.Busy[i] == 0 && busyElsewhere?.Contains(i) != true;
                var own = Array.IndexOf(m.Holder, c);
                slot = own >= 0 && Idle(own) ? own
                    : Enumerable.Range(0, m.Count).Where(Idle).OrderBy(i => m.Used[i]).Cast<int?>().FirstOrDefault();
                if (slot is { } s && own != s)
                {
                    if (own >= 0)
                    {
                        // It moves: what it left there is another conversation's start now, as far as choosing goes.
                        m.Holder[own] = null;
                    }
                    m.Holder[s] = c;
                }
            }
            if (slot is { } taken)
            {
                m.Busy[taken]++;
                m.Used[taken] = clock.GetTimestamp();
            }
            return new Lease(this, model, slot);
        }
    }

    private void Release(string model, int slot)
    {
        lock (_lock)
        {
            if (_models.TryGetValue(model, out var m) && slot < m.Count && m.Busy[slot] > 0)
            {
                m.Busy[slot]--;
                m.Used[slot] = clock.GetTimestamp();
            }
        }
    }
}
