using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Llm.Api.Operations;

/// <summary>
/// Which of the stack's modules run. Every one does unless left out in docker-compose.override.yml,
/// and a module left out has no name on the stack's network. Asked again after a minute.
/// "Modules:&lt;name&gt;" in configuration (true/false) answers instead, for tests and odd networks; a module whose
/// feature is off (deploy/features) is not asked for.
/// </summary>
public sealed class Modules(IConfiguration config, Features features, TimeProvider clock)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (bool Up, DateTimeOffset At)> _seen = new(StringComparer.Ordinal);

    public async Task<bool> HasAsync(string name, CancellationToken ct = default)
    {
        if (!features.Runs(name))
        {
            return false;
        }
        if (bool.TryParse(config[$"Modules:{name}"], out var forced))
        {
            return forced;
        }
        if (_seen.TryGetValue(name, out var seen) && clock.GetUtcNow() - seen.At < Fresh)
        {
            return seen.Up;
        }
        bool up;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(2));
            up = (await Dns.GetHostAddressesAsync(name, wait.Token)).Length > 0;
        }
        catch (SocketException)
        {
            // An answer even when the request went away meanwhile: the name is not there
            up = false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            up = false;
        }
        _seen[name] = (up, clock.GetUtcNow());
        return up;
    }
}
