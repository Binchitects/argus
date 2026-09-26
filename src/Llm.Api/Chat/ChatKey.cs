using Llm.Api.Gateway;
using Llm.Core.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// The chat's own gateway key (alias "chat"), made once and kept encrypted in the
/// app's database. Not the master key: master-key traffic is what the usage
/// dashboard lists as unaccounted, and the key that answers chat should be able
/// to do nothing else. Each request still names its person in `user`, which is
/// where the per-person budget binds.
/// </summary>
public sealed class ChatKey(IServiceScopeFactory scopes, IDataProtectionProvider protection) : IDisposable
{
    public const string Alias = "chat";
    private const string SettingKey = "chat.gateway_key";

    private readonly IDataProtector _protector = protection.CreateProtector("chat-gateway-key");
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cached;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (_cached is { } k)
        {
            return k;
        }
        await _lock.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == SettingKey, ct) is { } row)
            {
                return _cached = _protector.Unprotect(row.Value);
            }
            var key = await scope.ServiceProvider.GetRequiredService<ILiteLlm>().GenerateServiceKeyAsync(Alias, ct);
            db.Settings.Add(new Setting { Key = SettingKey, Value = _protector.Protect(key) });
            await db.SaveChangesAsync(ct);
            return _cached = key;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The gateway no longer knows the key (deleted by hand?): forget it, and the next call makes a new one.</summary>
    public async Task ForgetAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _cached = null;
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Settings.Where(s => s.Key == SettingKey).ExecuteDeleteAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
