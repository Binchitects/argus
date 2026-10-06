using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// Fair use of models that serve few people at once. Each model has its own places: as many
/// answers as it serves at once (its slots, here and on other GPU servers; Admin → Models),
/// so people on one model never wait for another's. A person has at most
/// <see cref="ChatOptions.AnswersPerPerson"/> answers running, on any models; when
/// <see cref="ChatOptions.AnswersAtOnce"/> is set, the whole chat at most that many. The rest
/// wait in their model's line. A free place goes first to the highest priority waiting (their
/// groups' priority, Admin → Groups), then, within a priority, to whoever has waited while having
/// the fewest answers running, and among those to whoever was served longest ago: one person's
/// many questions cannot keep the others waiting. With several replicas each keeps its own
/// lines, with its share of the places.
/// </summary>
public sealed class AnswerGate(IOptionsMonitor<ChatOptions> options, TimeProvider clock)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, int> _running = [];
    private readonly Dictionary<string, int> _runningOn = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, long> _lastServed = [];
    private readonly List<Waiter> _waiting = [];
    private IReadOnlyDictionary<string, int> _places = new Dictionary<string, int>();
    private long _tick;
    private int _total;
    private int _replicas = 1;

    private sealed class Waiter(Guid person, int priority, string model, long order)
    {
        public Guid Person { get; } = person;
        public int Priority { get; } = priority;
        public string Model { get; } = model;
        public long Order { get; } = order;
        public TaskCompletionSource<Place> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A place taken: give it back by disposing it.</summary>
    public sealed class Place(AnswerGate gate, Guid person, string model) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release(person, model);
            }
        }
    }

    /// <summary>
    /// What the line looks like to one person: the model they wait for (null when it is the whole
    /// chat's limit that holds them), how many are ahead of them, how many answers it runs, of how
    /// many, and whether it is their own answers running that hold them (Chat:AnswersPerPerson).
    /// </summary>
    public sealed record Line(string? Model, int Ahead, int Running, int AtOnce, bool Yours = false);

    /// <summary>Thrown when an answer has waited longer than <see cref="ChatOptions.QueueTimeout"/>: nothing was sent.</summary>
    public sealed class TooLongException(string message) : TimeoutException(message);

    private int PerPerson => Math.Max(1, options.CurrentValue.AnswersPerPerson);

    /// <summary>The app's replicas on the database (the answers' service keeps it): the places are shared out among them.</summary>
    public int Replicas
    {
        get => _replicas;
        set
        {
            lock (_lock)
            {
                _replicas = Math.Max(1, value);
                Dispatch();
            }
        }
    }

    /// <summary>
    /// The answers each model runs at once, by name (the engine watcher keeps it: its slots, less the one
    /// kept for side requests; see <see cref="SlotTable"/>). A model not named has no limit of its own.
    /// </summary>
    public void SetPlaces(IReadOnlyDictionary<string, int> places)
    {
        lock (_lock)
        {
            _places = new Dictionary<string, int>(places, StringComparer.Ordinal);
            Dispatch();
        }
    }

    /// <summary>The answers a model runs at once, on all replicas together; 0: unknown (no limit of its own).</summary>
    public int PlacesOf(string model)
    {
        lock (_lock)
        {
            return _places.GetValueOrDefault(model);
        }
    }

    /// <summary>This replica's share of a number of places, rounded up; 0 stays 0 (no limit).</summary>
    private int Share(int all) => all <= 0 || _replicas <= 1 ? Math.Max(0, all) : Math.Max(1, (all + _replicas - 1) / _replicas);

    /// <summary>The model's places here; 0: no limit.</summary>
    private int PlacesHere(string model) => Share(_places.GetValueOrDefault(model));

    /// <summary>The whole chat's places here, when Chat:AnswersAtOnce sets them; 0: no limit.</summary>
    private int AtOnce => Share(options.CurrentValue.AnswersAtOnce);

    /// <summary>
    /// Waits for a place on <paramref name="model"/>. <paramref name="waiting"/> is told the line now and then
    /// while it waits (not at all when a place is free at once).
    /// </summary>
    public Task<Place> EnterAsync(Guid person, string model, Func<Line, Task> waiting, CancellationToken ct) => EnterAsync(person, 0, model, waiting, ct);

    /// <param name="priority">Their place in line: higher goes first (0 for most people).</param>
    public async Task<Place> EnterAsync(Guid person, int priority, string model, Func<Line, Task> waiting, CancellationToken ct)
    {
        Waiter me;
        lock (_lock)
        {
            me = new Waiter(person, priority, model, ++_tick);
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
                    throw new TooLongException($"{model} has been busy for {timeout.TotalMinutes:0} minutes: nothing was sent. Try again in a while, or choose another model.");
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

    /// <summary>Answers running now, and answers waiting, on every model (for Admin → Overview and tests).</summary>
    public (int Total, int Waiting) Now()
    {
        lock (_lock)
        {
            return (_total, _waiting.Count);
        }
    }

    /// <summary>Answers running now, and waiting, on one model.</summary>
    public (int Total, int Waiting) Now(string model)
    {
        lock (_lock)
        {
            return (_runningOn.GetValueOrDefault(model), _waiting.Count(w => w.Model == model));
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
            var places = PlacesHere(me.Model);
            var yours = _running.GetValueOrDefault(me.Person) >= PerPerson;
            // Their model is full, or only their own answers hold them: its line. Else the whole chat's limit does.
            if ((places > 0 && _runningOn.GetValueOrDefault(me.Model) >= places) || AtOnce == 0 || _total < AtOnce)
            {
                return new Line(me.Model, _waiting.Count(w => w != me && w.Model == me.Model && Before(w, me)), _runningOn.GetValueOrDefault(me.Model), places, yours);
            }
            return new Line(null, _waiting.Count(w => w != me && Before(w, me)), _total, AtOnce, yours);
        }
    }

    private void Release(Guid person, string model)
    {
        lock (_lock)
        {
            _total--;
            if (--_running[person] == 0)
            {
                _running.Remove(person);
            }
            if (--_runningOn[model] == 0)
            {
                _runningOn.Remove(model);
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

    /// <summary>Gives free places to those first in their model's line whose own limit allows. Called under the lock.</summary>
    private void Dispatch()
    {
        while (AtOnce == 0 || _total < AtOnce)
        {
            Waiter? next = null;
            foreach (var w in _waiting)
            {
                var places = PlacesHere(w.Model);
                if (_running.GetValueOrDefault(w.Person) < PerPerson && (places == 0 || _runningOn.GetValueOrDefault(w.Model) < places)
                    && (next is null || Before(w, next)))
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
            _runningOn[next.Model] = _runningOn.GetValueOrDefault(next.Model) + 1;
            _total++;
            _lastServed[next.Person] = ++_tick;
            next.Granted.TrySetResult(new Place(this, next.Person, next.Model));
        }
    }
}
