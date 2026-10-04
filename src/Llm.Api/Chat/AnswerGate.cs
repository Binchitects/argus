using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// Fair use of a model that serves few people at once. A person has at most
/// <see cref="ChatOptions.AnswersPerPerson"/> answers running, the whole chat at
/// most <see cref="ChatOptions.AnswersAtOnce"/>; the rest wait in line. A free
/// place goes first to the highest priority waiting (their groups' priority,
/// Admin → Groups), then, within a priority, to whoever has waited while having
/// the fewest answers running, and among those to whoever was served longest
/// ago: one person's many questions cannot keep the others waiting. With several
/// replicas each keeps its own line, with its share of the places.
/// </summary>
public sealed class AnswerGate(IOptionsMonitor<ChatOptions> options, TimeProvider clock)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, int> _running = [];
    private readonly Dictionary<Guid, long> _lastServed = [];
    private readonly List<Waiter> _waiting = [];
    private long _tick;
    private int _total;

    private sealed class Waiter(Guid person, int priority, long order)
    {
        public Guid Person { get; } = person;
        public int Priority { get; } = priority;
        public long Order { get; } = order;
        public TaskCompletionSource<Place> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A place taken: give it back by disposing it.</summary>
    public sealed class Place(AnswerGate gate, Guid person) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release(person);
            }
        }
    }

    /// <summary>What the line looks like to one person: how many are ahead of them.</summary>
    public sealed record Line(int Ahead, int Running, int AtOnce);

    private int PerPerson => Math.Max(1, options.CurrentValue.AnswersPerPerson);

    /// <summary>What the engine's loaded models serve at once, their parallel slots together (the engine watcher keeps it).</summary>
    public int EngineSlots { get; set; }

    /// <summary>The app's replicas on the database (the answers' service keeps it): the places are shared out among them.</summary>
    public int Replicas { get; set; } = 1;

    /// <summary>As set; 0: as many as the engine serves at once; unknown, no limit. This replica's share, rounded up.</summary>
    private int AtOnce
    {
        get
        {
            var all = options.CurrentValue.AnswersAtOnce > 0 ? options.CurrentValue.AnswersAtOnce
                : Math.Max(0, options.CurrentValue.EngineSlots > 0 ? options.CurrentValue.EngineSlots : EngineSlots);
            return all == 0 || Replicas <= 1 ? all : Math.Max(1, (all + Replicas - 1) / Replicas);
        }
    }

    /// <summary>
    /// Waits for a place. <paramref name="waiting"/> is told the line now and then
    /// while it waits (not at all when a place is free at once).
    /// </summary>
    public Task<Place> EnterAsync(Guid person, Func<Line, Task> waiting, CancellationToken ct) => EnterAsync(person, 0, waiting, ct);

    /// <param name="priority">Their place in line: higher goes first (0 for most people).</param>
    public async Task<Place> EnterAsync(Guid person, int priority, Func<Line, Task> waiting, CancellationToken ct)
    {
        Waiter me;
        lock (_lock)
        {
            me = new Waiter(person, priority, ++_tick);
            _waiting.Add(me);
            Dispatch();
        }
        if (me.Granted.Task.IsCompleted)
        {
            return await me.Granted.Task;
        }
        var timeout = options.CurrentValue.QueueTimeout;
        var started = clock.GetUtcNow();
        Line? told = null;
        try
        {
            while (true)
            {
                var line = LineFor(me);
                if (line is not null && line != told)
                {
                    await waiting(line);
                    told = line;
                }
                var done = await Task.WhenAny(me.Granted.Task, Task.Delay(TimeSpan.FromSeconds(1), clock, ct));
                if (done == me.Granted.Task)
                {
                    return await me.Granted.Task;
                }
                ct.ThrowIfCancellationRequested();
                if (clock.GetUtcNow() - started > timeout)
                {
                    throw new TimeoutException($"The model has been busy for {timeout.TotalMinutes:0} minutes: nothing was sent. Try again in a while.");
                }
            }
        }
        catch
        {
            bool granted;
            lock (_lock)
            {
                granted = !_waiting.Remove(me) && me.Granted.Task.IsCompletedSuccessfully;
            }
            if (granted)
            {
                // Granted as it gave up: the place goes to the next in line.
                me.Granted.Task.Result.Dispose();
            }
            throw;
        }
    }

    /// <summary>Answers running now, and answers waiting (for Admin → Overview and tests).</summary>
    public (int Total, int Waiting) Now()
    {
        lock (_lock)
        {
            return (_total, _waiting.Count);
        }
    }

    private Line? LineFor(Waiter me)
    {
        lock (_lock)
        {
            if (!_waiting.Contains(me))
            {
                return null;
            }
            var ahead = _waiting.Count(w => w != me && Before(w, me));
            return new Line(ahead, _total, AtOnce);
        }
    }

    private void Release(Guid person)
    {
        lock (_lock)
        {
            _total--;
            if (--_running[person] == 0)
            {
                _running.Remove(person);
            }
            Dispatch();
        }
    }

    /// <summary>Who goes first: a higher priority, then fewer answers running, then served longer ago, then waiting longer.</summary>
    private bool Before(Waiter a, Waiter b)
    {
        if (a.Priority != b.Priority)
        {
            return a.Priority > b.Priority;
        }
        var (ra, rb) = (_running.GetValueOrDefault(a.Person), _running.GetValueOrDefault(b.Person));
        if (ra != rb)
        {
            return ra < rb;
        }
        var (sa, sb) = (_lastServed.GetValueOrDefault(a.Person), _lastServed.GetValueOrDefault(b.Person));
        return sa != sb ? sa < sb : a.Order < b.Order;
    }

    /// <summary>Gives free places to those first in line whose own limit allows. Called under the lock.</summary>
    private void Dispatch()
    {
        while (AtOnce == 0 || _total < AtOnce)
        {
            Waiter? next = null;
            foreach (var w in _waiting)
            {
                if (_running.GetValueOrDefault(w.Person) < PerPerson && (next is null || Before(w, next)))
                {
                    next = w;
                }
            }
            if (next is null)
            {
                return;
            }
            _waiting.Remove(next);
            _running[next.Person] = _running.GetValueOrDefault(next.Person) + 1;
            _total++;
            _lastServed[next.Person] = ++_tick;
            next.Granted.TrySetResult(new Place(this, next.Person));
        }
    }
}
