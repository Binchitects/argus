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
    }

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

    public Task<string> GenerateKeyAsync(string email, string keyAlias, CancellationToken ct = default)
    {
        Check();
        var secret = "sk-" + Guid.NewGuid().ToString("N");
        var token = "hash-" + Guid.NewGuid().ToString("N");
        Keys[token] = new Key { Secret = secret, Token = token, Email = email, Alias = keyAlias };
        return Task.FromResult(secret);
    }

    public Task<IReadOnlyList<GatewayKey>> KeysAsync(string email, CancellationToken ct = default)
    {
        Check();
        IReadOnlyList<GatewayKey> list = [.. KeysOf(email).Select(k => new GatewayKey(k.Token, k.Alias, "sk-...", 0, k.Blocked, DateTimeOffset.UtcNow))];
        return Task.FromResult(list);
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

    private void Check()
    {
        if (Down)
        {
            throw new GatewayException("The gateway is unreachable (test).");
        }
    }
}
