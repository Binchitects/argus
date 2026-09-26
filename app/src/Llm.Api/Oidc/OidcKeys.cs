using System.Security.Cryptography;
using Llm.Core.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;

namespace Llm.Api.Oidc;

/// <summary>
/// The OIDC signing and encryption keys: generated on first start, kept in the
/// database (protected by Data Protection), the same after every restart. New
/// keys would sign everyone out of every app and break outstanding tokens.
/// </summary>
public sealed class OidcKeys(IServiceScopeFactory scopes) : IConfigureOptions<OpenIddictServerOptions>
{
    private const string SigningKey = "oidc.signing_key";
    private const string EncryptionKey = "oidc.encryption_key";

    public void Configure(OpenIddictServerOptions options)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("oidc-keys");

        var rsa = RSA.Create();
        rsa.ImportFromPem(Load(db, protector, SigningKey, () =>
        {
            using var fresh = RSA.Create(3072);
            return fresh.ExportPkcs8PrivateKeyPem();
        }));
        options.SigningCredentials.Add(new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = KeyId(rsa) }, SecurityAlgorithms.RsaSha256));

        var aes = Convert.FromBase64String(Load(db, protector, EncryptionKey, () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        options.EncryptionCredentials.Add(new EncryptingCredentials(new SymmetricSecurityKey(aes), SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes256CbcHmacSha512));
    }

    private static string Load(AppDbContext db, IDataProtector protector, string key, Func<string> create)
    {
        var row = db.Settings.Find(key);
        if (row is not null)
        {
            return protector.Unprotect(row.Value);
        }
        var value = create();
        db.Settings.Add(new Setting { Key = key, Value = protector.Protect(value) });
        db.SaveChanges();
        return value;
    }

    private static string KeyId(RSA rsa) =>
        Base64UrlEncoder.Encode(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()))[..16];
}
