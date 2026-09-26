using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// Who may use which model holds for API keys too: each person's keys carry the
/// models they may call (LiteLLM refuses the rest with 403). The chat's own key
/// is not one of them; the app checks the chat itself. Fair use holds for keys
/// too: each carries how many requests it may have at once.
/// </summary>
public sealed class KeyAccess(UserManager<AppUser> users, ModelPolicy policy, ChatModels models, ILiteLlm gateway, IOptionsMonitor<ChatOptions> chat)
{
    /// <summary>Requests a key may have at once; null: no limit.</summary>
    public int? MaxParallel => chat.CurrentValue.ApiRequestsPerKey > 0 ? chat.CurrentValue.ApiRequestsPerKey : null;

    /// <summary>LiteLLM's word for "no model": an empty list would mean every model.</summary>
    public const string NoModels = "no-default-models";

    /// <summary>The list a person's keys carry: empty (every model) when nothing is kept from them.</summary>
    public async Task<IReadOnlyList<string>> ListForAsync(AppUser user, CancellationToken ct = default)
    {
        var all = (await models.AllAsync(ct)).Select(m => m.Name).ToList();
        var allowed = await policy.AllowedAsync(user, all, ct);
        return allowed.Count == all.Count ? [] : allowed.Count == 0 ? [NoModels] : [.. all.Where(allowed.Contains)];
    }

    /// <summary>Brings everyone's keys in step; returns how many keys changed.</summary>
    public async Task<int> SyncAsync(CancellationToken ct = default)
    {
        var changed = 0;
        foreach (var user in await users.Users.AsNoTracking().Where(u => u.Email != null).ToListAsync(ct))
        {
            var want = await ListForAsync(user, ct);
            foreach (var key in await gateway.KeysAsync(user.Email!, ct))
            {
                if (!(key.Models ?? []).Order(StringComparer.Ordinal).SequenceEqual(want.Order(StringComparer.Ordinal)) || key.MaxParallel != MaxParallel)
                {
                    await gateway.SetKeyAccessAsync(key.Token, want, MaxParallel, ct);
                    changed++;
                }
            }
        }
        return changed;
    }
}

/// <summary>Runs <see cref="KeyAccess.SyncAsync"/> when access or groups change, and every 10 minutes (the directory's groups change on their own).</summary>
public sealed partial class KeyAccessWatcher : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<KeyAccessWatcher> logger;
    private readonly IDisposable? _onChange;

    public KeyAccessWatcher(IServiceScopeFactory scopes, ILogger<KeyAccessWatcher> logger, IOptionsMonitor<ChatOptions> chat)
    {
        this.scopes = scopes;
        this.logger = logger;
        // A new limit of requests per key reaches every key at once, not in ten minutes.
        var perKey = chat.CurrentValue.ApiRequestsPerKey;
        _onChange = chat.OnChange(o =>
        {
            if (o.ApiRequestsPerKey != perKey)
            {
                perKey = o.ApiRequestsPerKey;
                Wake();
            }
        });
    }

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    public override void Dispose()
    {
        _onChange?.Dispose();
        _wake.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Soon after start (an upgrade may bring a new limit), then every ten minutes or when woken.
        var wait = TimeSpan.FromSeconds(15);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(wait, stoppingToken);
                wait = TimeSpan.FromMinutes(10);
                await using var scope = scopes.CreateAsyncScope();
                var changed = await scope.ServiceProvider.GetRequiredService<KeyAccess>().SyncAsync(stoppingToken);
                if (changed > 0)
                {
                    LogChanged(logger, changed);
                }
            }
            catch (GatewayException ex)
            {
                LogFailed(logger, ex.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A background failure must never stop the app.
                LogError(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "API keys: {Changed} keys now carry the models their people may use")]
    private static partial void LogChanged(ILogger logger, int changed);

    [LoggerMessage(Level = LogLevel.Error, Message = "API keys: the sync failed; trying again later")]
    private static partial void LogError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "API keys could not be brought in step with model access: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
