using Llm.Api.Gateway;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// The models the chat offers (and the picture and speech models its tools use): the gateway's list, cached for a minute.
/// </summary>
public sealed class ChatModels(IServiceScopeFactory scopes, IOptionsMonitor<ChatOptions> chat, IOptions<Models.EngineOptions> engine, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<GatewayModel>? _cached;
    private DateTimeOffset _at;

    /// <summary>The model new chats use: as set under Settings, else the first kept loaded.</summary>
    public string? DefaultName => chat.CurrentValue.DefaultModel is { Length: > 0 } set ? set : FirstKept();

    private string? FirstKept()
    {
        try
        {
            return File.ReadLines(Path.Combine(engine.Value.ConfigDir, Models.ModelCatalog.KeepFile)).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The models one can chat with.</summary>
    public async Task<IReadOnlyList<GatewayModel>> ListAsync(CancellationToken ct = default) =>
        [.. (await AllAsync(ct)).Where(m => m.Mode == "chat")];

    /// <summary>The gateway's picture model, if it serves one.</summary>
    public async Task<GatewayModel?> ImageModelAsync(CancellationToken ct = default) =>
        (await AllAsync(ct)).FirstOrDefault(m => m.Mode == "image_generation");

    /// <summary>The gateway's model of a kind (audio_transcription, audio_speech), by name first.</summary>
    public async Task<GatewayModel?> OfModeAsync(string mode, string? name = null, CancellationToken ct = default)
    {
        var all = (await AllAsync(ct)).Where(m => m.Mode == mode).ToList();
        return all.FirstOrDefault(m => m.Name == name) ?? all.FirstOrDefault();
    }

    /// <summary>Every model at the gateway: chat and pictures.</summary>
    public async Task<IReadOnlyList<GatewayModel>> AllAsync(CancellationToken ct = default)
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

    /// <summary>When the gateway cannot say: the default model alone, so the chat still opens and says why answers fail.</summary>
    private List<GatewayModel> Fallback() => DefaultName is { } d ? [new(d, null, null, false, true, true, null, null, null)] : [];

    /// <summary>The model to use: the one asked for if the gateway serves it, else the default.</summary>
    public async Task<GatewayModel?> ResolveAsync(string? name, CancellationToken ct = default)
    {
        var models = await ListAsync(ct);
        return models.FirstOrDefault(m => m.Name == name) ?? models.FirstOrDefault(m => m.Name == DefaultName) ?? (models.Count > 0 ? models[0] : null);
    }

    public void Forget() => _cached = null;

    public void Dispose() => _lock.Dispose();
}
