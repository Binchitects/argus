using System.Collections.Concurrent;
using Llm.Api.Chat;
using Microsoft.Extensions.Options;

namespace Llm.Api.Gateway;

/// <summary>
/// The line each API key's requests wait in at the gateway: as many at once as Chat:ApiRequestsPerKey,
/// the rest in the order they came, each going on as one ends. A coding agent that sends several requests
/// at once (sub-agents, tools side by side) has them answered in turn instead of refused with 429, which it
/// would retry after longer and longer waits and look stuck. Only past Chat:ApiQueueTimeout is a request
/// refused. Kept by the key's hash, never the key.
/// </summary>
public sealed class KeyLine(IOptionsMonitor<ChatOptions> options, OwnKeyPlaces? own = null)
{
    // A SemaphoreSlim needs disposing only once its wait handle was asked for, which this never does.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "SemaphoreSlim's wait handle is never made")]
    private sealed class Line(int places)
    {
        public readonly SemaphoreSlim Places = new(places, places);
        public readonly int Count = places;
        public int Waiting;
    }

    private readonly ConcurrentDictionary<string, Line> _lines = new(StringComparer.Ordinal);

    /// <summary>How many of a key's requests are answered at once, and how long the others may wait.</summary>
    public int Places => options.CurrentValue.ApiRequestsPerKey;
    public TimeSpan Timeout => options.CurrentValue.ApiQueueTimeout;

    /// <summary>A place for the key's request: disposed when the request ends. Null when the wait ran out.</summary>
    public async Task<IDisposable?> EnterAsync(string key, CancellationToken ct)
    {
        var all = options.CurrentValue.ApiRequestsPerKey;
        // A key its person made with fewer at once (Account → API keys) has that many places.
        var token = AnswerCache.HashOf(key);
        var places = OwnKeys.TighterParallel(all > 0 ? all : null, own is null ? null : await own.ForAsync(token, ct)) ?? 0;
        if (places <= 0)
        {
            return Nothing.Instance;
        }
        var id = token;
        // A changed setting takes effect for the lines made after it; a busy line keeps its count until it empties.
        var line = _lines.AddOrUpdate(id, _ => new Line(places), (_, l) => l.Count != places && l.Places.CurrentCount == l.Count && l.Waiting == 0 ? new Line(places) : l);
        Interlocked.Increment(ref line.Waiting);
        try
        {
            if (!await line.Places.WaitAsync(options.CurrentValue.ApiQueueTimeout, ct))
            {
                return null;
            }
        }
        finally
        {
            Interlocked.Decrement(ref line.Waiting);
        }
        return new Place(line.Places);
    }

    /// <summary>How many of the key's requests wait now (for the 429's message).</summary>
    public int WaitingFor(string key) =>
        _lines.TryGetValue(AnswerCache.HashOf(key), out var l) ? l.Waiting : 0;

    /// <summary>How many of the key's requests are answered at once now (its own count, else everyone's).</summary>
    public int PlacesFor(string key) => _lines.TryGetValue(AnswerCache.HashOf(key), out var l) ? l.Count : Places;

    private sealed class Place(SemaphoreSlim places) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                places.Release();
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose()
        {
        }
    }
}
