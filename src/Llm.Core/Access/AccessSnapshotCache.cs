namespace Llm.Core.Access;

/// <summary>
/// The current <see cref="AccessSnapshot"/>, read again only when the stored version moved. Each request
/// asks for the version (one small read), so a change made on any replica applies from the next request
/// everywhere, while the roles, assignments and rules themselves are read once per change.
/// </summary>
public sealed class AccessSnapshotCache : IDisposable
{
    private readonly SemaphoreSlim _loading = new(1, 1);
    private volatile AccessSnapshot? _current;

    public AccessSnapshot? Current => _current;

    /// <summary>The snapshot of <paramref name="version"/>: the one held when it is that version, else a fresh read.</summary>
    public async Task<AccessSnapshot> GetAsync(long version, Func<CancellationToken, Task<AccessSnapshot>> load, CancellationToken ct)
    {
        if (_current is { } held && held.Version == version)
        {
            return held;
        }
        await _loading.WaitAsync(ct);
        try
        {
            if (_current is { } again && again.Version == version)
            {
                return again;
            }
            var fresh = await load(ct);
            // A newer version read by another request stays: never go back.
            if (_current is null || fresh.Version >= _current.Version)
            {
                _current = fresh;
            }
            return fresh;
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>Forgets the held snapshot: the next request reads it again whatever the version says.</summary>
    public void Drop() => _current = null;

    public void Dispose() => _loading.Dispose();
}
