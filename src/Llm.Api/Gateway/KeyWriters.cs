using System.Collections.Concurrent;

namespace Llm.Api.Gateway;

/// <summary>
/// One writer at a time of a person's keys' limits at the gateway: their own keys made or changed (OwnKeys) and the syncs
/// (RateLimits, KeyAccess), which read what each key should carry and then write it. Without it a sync that read before a
/// key of theirs was made or changed wrote over that key's own limits until the next sync. Within one app: with several
/// replicas, the leader's next sync puts any such key right.
/// </summary>
public sealed class KeyWriters
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _people = new();

    /// <summary>The person's turn; disposing it ends it.</summary>
    public async Task<IDisposable> EnterAsync(Guid person, CancellationToken ct = default)
    {
        var turn = _people.GetOrAdd(person, _ => new SemaphoreSlim(1, 1));
        await turn.WaitAsync(ct);
        return new Turn(turn);
    }

    private sealed class Turn(SemaphoreSlim turn) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                turn.Release();
            }
        }
    }
}
