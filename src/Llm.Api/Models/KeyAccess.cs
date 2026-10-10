using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Api.Operations;
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

    /// <summary>Each person's keys blocked while they are disabled or have no API access, unblocked otherwise; returns how many changed.</summary>
    public async Task<int> BlocksAsync(CancellationToken ct = default)
    {
        var changed = 0;
        foreach (var user in await users.Users.AsNoTracking().Where(u => u.Email != null).ToListAsync(ct))
        {
            var blocked = user.IsDisabled || user.ApiOff;
            var wrong = (await gateway.KeysAsync(user.Email!, ct)).Where(k => k.Blocked != blocked).Select(k => k.Token).ToList();
            if (wrong.Count > 0)
            {
                await gateway.SetBlockedAsync(wrong, blocked, ct);
                changed += wrong.Count;
            }
        }
        return changed;
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

/// <summary>
/// Runs <see cref="KeyAccess.SyncAsync"/>, <see cref="Gateway.GroupTeams.SyncAsync"/> and <see cref="Gateway.RateLimits.SyncAsync"/> when access, groups or
/// the company's rate limits change, and every 10 minutes (the directory's groups change on their own). With several replicas, the one that leads runs it.
/// </summary>
public sealed partial class KeyAccessWatcher : BackgroundService
{
    private const string WakeTopic = "keys:wake";
    private readonly SemaphoreSlim _wake = new(0);
    private readonly IServiceScopeFactory scopes;
    private readonly Replicas replicas;
    private readonly ILogger<KeyAccessWatcher> logger;
    private readonly IDisposable? _onChange;
    private readonly IDisposable? _onRates;

    public KeyAccessWatcher(IServiceScopeFactory scopes, Replicas replicas, ILogger<KeyAccessWatcher> logger, IOptionsMonitor<ChatOptions> chat,
        IOptionsMonitor<Gateway.RateLimitOptions> rates)
    {
        this.scopes = scopes;
        this.replicas = replicas;
        this.logger = logger;
        replicas.On(WakeTopic, _ =>
        {
            WakeHere();
            return true;
        });
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
        // So does the company's rate limit.
        var limits = (rates.CurrentValue.RequestsPerMinute, rates.CurrentValue.TokensPerMinute);
        _onRates = rates.OnChange(o =>
        {
            if ((o.RequestsPerMinute, o.TokensPerMinute) != limits)
            {
                limits = (o.RequestsPerMinute, o.TokensPerMinute);
                Wake();
            }
        });
    }

    public void Wake()
    {
        WakeHere();
        replicas.Tell(WakeTopic);
    }

    private void WakeHere()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    public override void Dispose()
    {
        _onChange?.Dispose();
        _onRates?.Dispose();
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
                if (!replicas.IsLeader)
                {
                    wait = TimeSpan.FromMinutes(1);
                    continue;
                }
                wait = TimeSpan.FromMinutes(10);
                await using var scope = scopes.CreateAsyncScope();
                // The gateway holds no budget (the first time, the old ones become the app's credits).
                if (await scope.ServiceProvider.GetRequiredService<Gateway.CreditMove>().RunAsync(stoppingToken) is > 0 and var moved)
                {
                    LogMoved(logger, moved);
                }
                // Each key blocked or not as its person is: disabled, or without API access (a block that failed is done again).
                if (await scope.ServiceProvider.GetRequiredService<KeyAccess>().BlocksAsync(stoppingToken) is > 0 and var blocks)
                {
                    LogBlocks(logger, blocks);
                }
                var changed = await scope.ServiceProvider.GetRequiredService<KeyAccess>().SyncAsync(stoppingToken);
                if (changed > 0)
                {
                    LogChanged(logger, changed);
                }
                // The teams the app once made for groups' credit at the gateway go (the credits are the app's now).
                if (await scope.ServiceProvider.GetRequiredService<Gateway.GroupTeams>().SyncAsync(stoppingToken) is > 0 and var teams)
                {
                    LogTeams(logger, teams);
                }
                // And each key's rate limits: the person's own, their groups' or the company's.
                if (await scope.ServiceProvider.GetRequiredService<Gateway.RateLimits>().SyncAsync(stoppingToken) is > 0 and var rated)
                {
                    LogRates(logger, rated);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Groups' old teams at the gateway: {Changed} keys or teams taken away")]
    private static partial void LogTeams(ILogger logger, int changed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Credits: the gateway's budgets of {Moved} people became their credits of every kind")]
    private static partial void LogMoved(ILogger logger, int moved);

    [LoggerMessage(Level = LogLevel.Information, Message = "API keys: {Changed} keys blocked or unblocked to match their people")]
    private static partial void LogBlocks(ILogger logger, int changed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rate limits: {Changed} keys now carry their person's requests and tokens a minute")]
    private static partial void LogRates(ILogger logger, int changed);

    [LoggerMessage(Level = LogLevel.Error, Message = "API keys: the sync failed; trying again later")]
    private static partial void LogError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "API keys could not be brought in step with model access: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
