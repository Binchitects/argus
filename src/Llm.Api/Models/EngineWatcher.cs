using Llm.Api.Chat;
using Llm.Api.Gateway;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// Keeps the engine as admins chose: the models kept loaded loaded (again after
/// the engine restarts, or after a model loaded on request pushed one out), the
/// presets and the gateway in step with the database, and Prometheus scraping
/// whichever models are loaded. Checks every 10 seconds, every 3 while a model
/// loads. A model that fails to load is not tried again (it would fail every few
/// seconds); when every kept model failed, the .env model takes their place.
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
            await GatewayOnlyAsync(stoppingToken);
            return;
        }
        var nextSync = DateTimeOffset.MinValue;
        var loaded = "";
        string? reported = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var loading = false;
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
                // At start, then every minute: a gateway that was down, or restarted without the app's models, catches up.
                if (DateTimeOffset.UtcNow >= nextSync)
                {
                    await catalog.WritePresetsAsync(stoppingToken);
                    await catalog.SyncGatewayAsync(stoppingToken);
                    nextSync = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                var models = await engine.ModelsAsync(stoppingToken);
                state.Set(models);
                loading = models.Any(m => m.Status == "loading");
                string? Status(string name) => models.FirstOrDefault(m => m.Name == name)?.Status;
                // Never more than the engine holds at once: past that, each load would unload another kept model.
                var kept = catalog.Kept().Where(k => Status(k) is not null).Take(options.Value.ModelsMax).ToList();
                if (!loading)
                {
                    // One at a time: a load at the engine's limit first unloads the model used least recently,
                    // which may be a kept one that sat idle; it comes back on a later round.
                    if (kept.FirstOrDefault(k => Status(k) == "unloaded") is { } next)
                    {
                        LogLoading(logger, next);
                        await engine.LoadAsync(next, stoppingToken);
                        loading = true;
                    }
                    else if (kept.Count > 0 && kept.All(k => Status(k) == "failed") && !models.Any(m => m.Status == "loaded"))
                    {
                        // Every kept model failed to load (they are not tried again: each would fail every few seconds).
                        var fallback = options.Value.DefaultModel;
                        if (fallback is { Length: > 0 } && !kept.Contains(fallback) && Status(fallback) is "unloaded")
                        {
                            LogFellBack(logger, string.Join(", ", kept), fallback);
                            await engine.LoadAsync(fallback, stoppingToken);
                            loading = true;
                        }
                        else if (reported != string.Join(',', kept))
                        {
                            LogNothing(logger, string.Join(", ", kept));
                            reported = string.Join(',', kept);
                        }
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

    /// <summary>Without the llama.cpp engine, the other GPU servers' models still reach the gateway: every minute.</summary>
    private async Task GatewayOnlyAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ModelCatalog>().SyncGatewayAsync(stoppingToken);
            }
            catch (GatewayException ex)
            {
                LogGateway(logger, ex.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogFailed(logger, ex);
            }
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Engine: loading {Model}, a model kept loaded")]
    private static partial void LogLoading(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: {Model} failed to load (the engine's log says why); loading {Fallback}, the .env model, in their place")]
    private static partial void LogFellBack(ILogger logger, string model, string fallback);

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine: {Model} failed to load and there is no other to load in their place; load one from Admin -> Models")]
    private static partial void LogNothing(ILogger logger, string model);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: the gateway's model list could not be brought in step: {Reason}")]
    private static partial void LogGateway(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Engine: the check failed; trying again")]
    private static partial void LogFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Engine: cannot write to {Dir}: {Reason}")]
    private static partial void LogFiles(ILogger logger, string dir, string reason);
}
