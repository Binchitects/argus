using Llm.Api.Chat;
using Llm.Api.Gateway;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// Keeps the engine as admins chose: the chosen model loaded (again after the
/// engine restarts), the presets and the gateway in step with the database,
/// and Prometheus scraping whichever model is loaded. Checks every 10 seconds,
/// every 3 while a model loads. A model that fails to load is not tried again
/// (it would fail every few seconds): the .env model takes its place, once.
/// </summary>
public sealed partial class EngineWatcher(IServiceScopeFactory scopes, EngineClient engine, EngineState state, ChatModels chatModels,
    IOptions<EngineOptions> options, ILogger<EngineWatcher> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Check now (after an admin loads or unloads a model).</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }
        var synced = false;
        var loaded = "";
        string? reported = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var loading = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
                if (!synced)
                {
                    await catalog.WritePresetsAsync(stoppingToken);
                    await catalog.SyncGatewayAsync(stoppingToken);
                    synced = true;
                }
                var models = await engine.ModelsAsync(stoppingToken);
                state.Set(models);
                var active = catalog.Active();
                loading = models.Any(m => m.Status == "loading");
                var chosen = models.FirstOrDefault(m => m.Name == active);
                if (chosen is not null && !models.Any(m => m.Status is "loaded" or "loading"))
                {
                    var fallback = options.Value.DefaultModel;
                    if (chosen.Status != "failed")
                    {
                        LogLoading(logger, active);
                        await engine.LoadAsync(active, stoppingToken);
                        loading = true;
                    }
                    else if (fallback is { Length: > 0 } && fallback != active && models.Any(m => m.Name == fallback && m.Status != "failed"))
                    {
                        LogFellBack(logger, active, fallback);
                        catalog.SetActive(fallback);
                        await engine.LoadAsync(fallback, stoppingToken);
                        loading = true;
                    }
                    else if (reported != active)
                    {
                        LogNothing(logger, active);
                        reported = active;
                    }
                }
                var now = string.Join(',', models.Where(m => m.Status == "loaded").Select(m => m.Name).Order(StringComparer.Ordinal));
                if (now != loaded)
                {
                    catalog.WriteTargets(models.Where(m => m.Status == "loaded").Select(m => m.Name));
                    chatModels.Forget();
                    loaded = now;
                }
            }
            catch (EngineException ex)
            {
                state.Fail(ex.Message);
            }
            catch (GatewayException ex)
            {
                // Tried again next round.
                LogGateway(logger, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogFiles(logger, options.Value.ConfigDir, ex.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Whatever went wrong, the app keeps running: a background failure must never stop it.
                LogFailed(logger, ex);
            }
            try
            {
                await _wake.WaitAsync(loading ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine: loading {Model}, the model chosen to run")]
    private static partial void LogLoading(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: {Model} failed to load (the engine's log says why); loading {Fallback}, the .env model, in its place")]
    private static partial void LogFellBack(ILogger logger, string model, string fallback);

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine: {Model} failed to load and there is no other to load in its place; load one from Admin -> Models")]
    private static partial void LogNothing(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: the gateway's model list could not be brought in step: {Reason}")]
    private static partial void LogGateway(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine: the check failed; trying again")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: cannot write to {Dir}: {Reason}")]
    private static partial void LogFiles(ILogger logger, string dir, string reason);
}
