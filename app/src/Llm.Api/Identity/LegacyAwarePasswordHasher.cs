using Isopoh.Cryptography.Argon2;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Identity;

/// <summary>
/// ASP.NET Identity's hasher, plus one-way compatibility with the argon2id
/// hashes Authelia wrote. An imported person signs in with their old password
/// and the hash is replaced by the current format on that first sign-in.
/// </summary>
public sealed class LegacyAwarePasswordHasher : IPasswordHasher<AppUser>
{
    private readonly PasswordHasher<AppUser> _inner = new();

    public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);

    public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
    {
        if (hashedPassword.StartsWith("$argon2", StringComparison.Ordinal))
        {
            return Argon2.Verify(hashedPassword, providedPassword)
                ? PasswordVerificationResult.SuccessRehashNeeded
                : PasswordVerificationResult.Failed;
        }
        return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
    }
}
