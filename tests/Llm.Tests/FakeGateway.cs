using System.Collections.Concurrent;
using Llm.Api.Gateway;

namespace Llm.Tests;

/// <summary>
/// LiteLLM in memory: records what the app asked for, so tests can check it. The models a test lists here (not those the
/// app registered) are routes beside the app's own, answered by the fake model at engine.test.
/// </summary>
public sealed class FakeGateway : ILiteLlm, IRoutesSource
{
    public sealed class Key
    {
        public required string Secret { get; init; }
        public required string Token { get; init; }
        public required string Email { get; init; }
        public required string Alias { get; init; }
        public bool Blocked { get; set; }
        /// <summary>The models the key may call; empty: every model.</summary>
        public IReadOnlyList<string> Models { get; set; } = [];
        public int? MaxParallel { get; set; }
        public DateTimeOffset? Expires { get; set; }
        /// <summary>The team (a group's credit) the key is in, if any.</summary>
        public string? TeamId { get; set; }
        /// <summary>Its requests and tokens a minute.</summary>
        public KeyRate Rate { get; set; } = KeyRate.None;
        /// <summary>What it may spend in 30 days; null: no budget of its own.</summary>
        public decimal? MaxBudget { get; set; }
        /// <summary>When it was made: a test sets an earlier time for a key made before an upgrade.</summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Every rate the app set on a key, in order (the key's hashed token and the rate), so tests see what changed.</summary>
    public ConcurrentQueue<(string Token, KeyRate Rate)> RateChanges { get; } = new();

    /// <summary>The teams the app made for groups, by id, with the duration their budget is per.</summary>
    public ConcurrentDictionary<string, (GatewayTeam Team, string Duration)> Teams { get; } = new();

    public ConcurrentDictionary<string, decimal?> Budgets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, bool> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, Key> Keys { get; } = new();
    public bool Down { get; set; }

    public IEnumerable<Key> KeysOf(string email) => Keys.Values.Where(k => string.Equals(k.Email, email, StringComparison.OrdinalIgnoreCase));

    public Task EnsureUserAsync(string email, CancellationToken ct = default)
    {
        Check();
        Users[email] = true;
        return Task.CompletedTask;
    }

    public Task SetBudgetAsync(string email, decimal? budget, CancellationToken ct = default)
    {
        Check();
        Budgets[email] = budget;
        return Task.CompletedTask;
    }

    public Task GenerateKeyAsync(string email, string keyAlias, string key, IReadOnlyList<string>? models = null, int? maxParallel = null, KeyRate? rate = null,
        CancellationToken ct = default)
    {
        Check();
        // As LiteLLM keeps it: the key's SHA-256, in hex.
        var token = AnswerCache.HashOf(key);
        Keys[token] = new Key { Secret = key, Token = token, Email = email, Alias = keyAlias, Models = models ?? [], MaxParallel = maxParallel, Rate = rate ?? KeyRate.None };
        return Task.CompletedTask;
    }

    /// <summary>Runs before a key's rate is set (its hashed token): a test holds a sync there.</summary>
    public Func<string, Task>? BeforeRate { get; set; }

    /// <summary>Runs before a person's keys are listed (their email): a test holds a sync there.</summary>
    public Func<string, Task>? BeforeKeys { get; set; }

    public async Task SetKeyRateAsync(string token, KeyRate rate, CancellationToken ct = default)
    {
        Check();
        if (BeforeRate is { } before)
        {
            await before(token);
        }
        if (Keys.TryGetValue(token, out var key))
        {
            key.Rate = rate;
            RateChanges.Enqueue((token, rate));
        }
    }

    public Task SetKeyBudgetAsync(string token, decimal? monthly, CancellationToken ct = default)
    {
        Check();
        if (Keys.TryGetValue(token, out var key))
        {
            key.MaxBudget = monthly is > 0 ? monthly : null;
        }
        return Task.CompletedTask;
    }

    public Task SetKeyAccessAsync(string token, IReadOnlyList<string> models, int? maxParallel, CancellationToken ct = default)
    {
        Check();
        if (Keys.TryGetValue(token, out var key))
        {
            key.Models = models;
            key.MaxParallel = maxParallel;
        }
        return Task.CompletedTask;
    }

    public List<string> ServiceKeys { get; } = [];

    /// <summary>Key requests refused as unreachable before one is made (a gateway still settling).</summary>
    public int ServiceKeyFailures { get; set; }

    public Task<string> GenerateServiceKeyAsync(string keyAlias, CancellationToken ct = default)
    {
        Check();
        if (ServiceKeyFailures > 0)
        {
            ServiceKeyFailures--;
            throw new GatewayException("The gateway is unreachable (still settling).");
        }
        var secret = $"sk-{keyAlias}-" + Guid.NewGuid().ToString("N")[..12];
        lock (ServiceKeys)
        {
            ServiceKeys.Add(secret);
        }
        return Task.FromResult(secret);
    }

    public async Task<IReadOnlyList<GatewayKey>> KeysAsync(string email, CancellationToken ct = default)
    {
        Check();
        if (BeforeKeys is { } before)
        {
            await before(email);
        }
        return [.. KeysOf(email).Select(k => new GatewayKey(k.Token, k.Alias, "sk-...", 0, k.Blocked, k.CreatedAt, k.Models, k.MaxParallel, k.TeamId, k.Rate, k.MaxBudget))];
    }

    public Task DeleteKeysAsync(IEnumerable<string> tokens, CancellationToken ct = default)
    {
        Check();
        foreach (var t in tokens)
        {
            Keys.TryRemove(t, out _);
        }
        return Task.CompletedTask;
    }

    public Task SetBlockedAsync(IEnumerable<string> tokens, bool blocked, CancellationToken ct = default)
    {
        Check();
        foreach (var t in tokens)
        {
            Keys[t].Blocked = blocked;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, GatewayUser>> UsersAsync(CancellationToken ct = default)
    {
        Check();
        IReadOnlyDictionary<string, GatewayUser> all = Users.Keys.ToDictionary(e => e, e => new GatewayUser(e, 1.5m, Budgets.GetValueOrDefault(e)), StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(all);
    }

    /// <summary>What /model/info lists; tests change it to try a second model or one that can see.</summary>
    public List<GatewayModel> Models { get; } =
        [new("Qwen3.8-Flash-Next", 32768, 8192, Vision: false, Tools: true, Thinking: true, 0.20m, 0.02m, 0.80m)];

    public IEnumerable<Deployment> Routes()
    {
        List<GatewayModel> given;
        lock (Models)
        {
            given = [.. Models.Except(_listed.Values)];
        }
        foreach (var m in given)
        {
            var info = new System.Text.Json.Nodes.JsonObject
            {
                ["mode"] = m.Mode, ["supports_vision"] = m.Vision, ["supports_function_calling"] = m.Tools, ["supports_reasoning"] = m.Thinking, ["supports_audio_input"] = m.Audio,
            };
            if (m.Context is { } c)
            {
                info["max_input_tokens"] = c;
            }
            if (m.MaxOutput is { } o)
            {
                info["max_output_tokens"] = o;
            }
            var litellm = new System.Text.Json.Nodes.JsonObject { ["model"] = "openai/" + m.Name, ["api_base"] = "http://engine.test/v1", ["api_key"] = "none" };
            if (m.InputPerMtok is { } i)
            {
                litellm["input_cost_per_token"] = i / 1_000_000m;
            }
            if (m.CachedInputPerMtok is { } ci)
            {
                litellm["cache_read_input_token_cost"] = ci / 1_000_000m;
            }
            if (m.OutputPerMtok is { } op)
            {
                litellm["output_cost_per_token"] = op / 1_000_000m;
            }
            yield return new Deployment
            {
                Name = m.Name, Kind = Deployment.Remote, BaseUrl = "http://engine.test/v1", Model = m.Name, Mode = m.Mode, Info = info, LiteLlm = litellm, Fingerprint = "test:" + m.Name,
                Price = m.InputPerMtok is { } p ? new TokenPrice(p, m.CachedInputPerMtok ?? 0, m.OutputPerMtok ?? 0) : null,
            };
        }
    }

    public Task<IReadOnlyList<GatewayModel>> ModelsAsync(CancellationToken ct = default)
    {
        Check();
        return Task.FromResult<IReadOnlyList<GatewayModel>>([.. Models]);
    }

    /// <summary>Models the app added: the gateway's id, name, fingerprint and what it was added with.</summary>
    public ConcurrentDictionary<string, (string Name, string Fingerprint, System.Text.Json.Nodes.JsonObject Params, System.Text.Json.Nodes.JsonObject Info)> Managed { get; } = new();
    /// <summary>The listed model each managed deployment added: removing one deployment leaves the others of its name.</summary>
    private readonly ConcurrentDictionary<string, GatewayModel> _listed = new();

    public Task<IReadOnlyList<ManagedModel>> ManagedModelsAsync(CancellationToken ct = default)
    {
        Check();
        return Task.FromResult<IReadOnlyList<ManagedModel>>([.. Managed.Select(m => new ManagedModel(m.Key, m.Value.Name, m.Value.Fingerprint))]);
    }

    public Task AddModelAsync(string name, System.Text.Json.Nodes.JsonObject litellmParams, System.Text.Json.Nodes.JsonObject modelInfo, string fingerprint, CancellationToken ct = default)
    {
        Check();
        var id = Guid.NewGuid().ToString();
        Managed[id] = (name, fingerprint, litellmParams, modelInfo);
        if (modelInfo["llm_app_replica_of"] is not null)
        {
            // As LiteLlmClient lists them: a copy under its own name is in no list.
            return Task.CompletedTask;
        }
        var listed = new GatewayModel(name, modelInfo["max_input_tokens"]?.GetValue<int>(), modelInfo["max_output_tokens"]?.GetValue<int>(),
            modelInfo["supports_vision"]?.GetValue<bool>() ?? false, true, true, null, null, null);
        _listed[id] = listed;
        lock (Models)
        {
            Models.Add(listed);
        }
        return Task.CompletedTask;
    }

    /// <summary>The fallbacks the app set last (router_settings.fallbacks).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Fallbacks { get; private set; }

    public Task SetFallbacksAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> fallbacks, CancellationToken ct = default)
    {
        Check();
        Fallbacks = fallbacks;
        return Task.CompletedTask;
    }

    public Task DeleteModelAsync(string id, CancellationToken ct = default)
    {
        Check();
        if (Managed.TryRemove(id, out _) && _listed.TryRemove(id, out var listed))
        {
            lock (Models)
            {
                Models.Remove(listed);
            }
        }
        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(string email, CancellationToken ct = default)
    {
        Check();
        foreach (var k in KeysOf(email).ToList())
        {
            Keys.TryRemove(k.Token, out _);
        }
        Users.TryRemove(email, out _);
        Budgets.TryRemove(email, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GatewayTeam>> TeamsAsync(CancellationToken ct = default)
    {
        Check();
        return Task.FromResult<IReadOnlyList<GatewayTeam>>([.. Teams.Values.Select(t => t.Team)]);
    }

    public Task SetTeamAsync(GatewayTeam team, string duration, CancellationToken ct = default)
    {
        Check();
        Teams[team.Id] = (team, duration);
        return Task.CompletedTask;
    }

    /// <summary>As LiteLLM does: the keys still in the team go with it.</summary>
    public Task DeleteTeamAsync(string id, CancellationToken ct = default)
    {
        Check();
        Teams.TryRemove(id, out _);
        foreach (var k in Keys.Values.Where(k => k.TeamId == id).ToList())
        {
            Keys.TryRemove(k.Token, out _);
        }
        return Task.CompletedTask;
    }

    public Task SetKeyTeamAsync(string token, string? teamId, CancellationToken ct = default)
    {
        Check();
        if (Keys.TryGetValue(token, out var key))
        {
            key.TeamId = teamId;
        }
        return Task.CompletedTask;
    }

    private void Check()
    {
        if (Down)
        {
            throw new GatewayException("The gateway is unreachable (test).");
        }
    }
}

/// <summary>Holds the first call a fake gateway's hook sees for one person or key until released, and says when it holds it.</summary>
public sealed class Hold
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _once;

    public Task Held => _held.Task.WaitAsync(TimeSpan.FromSeconds(15));

    public Func<string, Task> For(string target) => async seen =>
    {
        if (seen == target && Interlocked.Exchange(ref _once, 1) == 0)
        {
            _held.SetResult();
            await _go.Task;
        }
    };

    public void Release() => _go.TrySetResult();
}
