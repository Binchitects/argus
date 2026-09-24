using Llm.Api.Gateway;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// The models the chat offers: the gateway's list, cached for a minute. When the
/// gateway cannot say, the deployment's own model (from .env) stands in, so the
/// chat still opens and says why answers fail rather than failing to load.
/// </summary>
public sealed class ChatModels(IServiceScopeFactory scopes, IOptions<StackOptions> stack, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<GatewayModel>? _cached;
    private DateTimeOffset _at;

    public string? DefaultName => stack.Value.ModelName;

    public async Task<IReadOnlyList<GatewayModel>> ListAsync(CancellationToken ct = default)
    {
        if (_cached is { } c && clock.GetUtcNow() - _at < Fresh)
        {
            return c;
        }
        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is { } again && clock.GetUtcNow() - _at < Fresh)
            {
                return again;
            }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var models = await scope.ServiceProvider.GetRequiredService<ILiteLlm>().ModelsAsync(ct);
                if (models.Count > 0)
                {
                    // The deployment's model first: it is the default.
                    _cached = [.. models.OrderBy(m => m.Name == DefaultName ? 0 : 1)];
                    _at = clock.GetUtcNow();
                    return _cached;
                }
            }
            catch (GatewayException)
            {
                // Not cached: the next request asks again.
            }
            return Fallback();
        }
        finally
        {
            _lock.Release();
        }
    }

    private List<GatewayModel> Fallback()
    {
        var s = stack.Value;
        return string.IsNullOrEmpty(s.ModelName)
            ? []
            : [new(s.ModelName, int.TryParse(s.ModelContext, out var c) ? c : null, int.TryParse(s.ModelMaxOutput, out var o) ? o : null, false, true, true, null, null, null)];
    }

    /// <summary>The model to use: the one asked for if the gateway serves it, else the default.</summary>
    public async Task<GatewayModel?> ResolveAsync(string? name, CancellationToken ct = default)
    {
        var models = await ListAsync(ct);
        return models.FirstOrDefault(m => m.Name == name) ?? models.FirstOrDefault(m => m.Name == DefaultName) ?? (models.Count > 0 ? models[0] : null);
    }

    public void Forget() => _cached = null;

    public void Dispose() => _lock.Dispose();
}
