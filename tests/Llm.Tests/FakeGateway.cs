using System.Collections.Concurrent;
using Llm.Api.Gateway;

namespace Llm.Tests;

/// <summary>LiteLLM in memory: records what the app asked for, so tests can check it.</summary>
public sealed class FakeGateway : ILiteLlm
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

    public Task<string> GenerateKeyAsync(string email, string keyAlias, IReadOnlyList<string>? models = null, int? maxParallel = null, KeyRate? rate = null,
        CancellationToken ct = default)
    {
        Check();
        var secret = "sk-" + Guid.NewGuid().ToString("N");
        // As LiteLLM keeps it: the key's SHA-256, in hex.
        var token = AnswerCache.HashOf(secret);
        Keys[token] = new Key { Secret = secret, Token = token, Email = email, Alias = keyAlias, Models = models ?? [], MaxParallel = maxParallel, Rate = rate ?? KeyRate.None };
        return Task.FromResult(secret);
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
        return [.. KeysOf(email).Select(k => new GatewayKey(k.Token, k.Alias, "sk-...", 0, k.Blocked, DateTimeOffset.UtcNow, k.Models, k.MaxParallel, k.TeamId, k.Rate))];
    }

    /// <summary>The keys looked up by the key itself (each one asked about), so tests can see what was cached.</summary>
    public ConcurrentQueue<string> KeyLookups { get; } = new();

    public Task<GatewayKeyInfo?> KeyInfoAsync(string key, CancellationToken ct = default)
    {
        Check();
        KeyLookups.Enqueue(key);
        var found = Keys.Values.FirstOrDefault(k => k.Secret == key);
        return Task.FromResult(found is null ? null : new GatewayKeyInfo(found.Email, found.Blocked, found.Expires, found.Models));
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
        var listed = new GatewayModel(name, modelInfo["max_input_tokens"]?.GetValue<int>(), modelInfo["max_output_tokens"]?.GetValue<int>(),
            modelInfo["supports_vision"]?.GetValue<bool>() ?? false, true, true, null, null, null);
        _listed[id] = listed;
        lock (Models)
        {
            Models.Add(listed);
        }
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
