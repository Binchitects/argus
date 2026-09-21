using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Identity;

/// <summary>
/// zxcvbn score 3 or more, the same bar Authelia enforced. It scores realistically
/// (rejects "Password1!", accepts a long passphrase) instead of counting character
/// classes. The person's own names count against the password.
/// </summary>
public sealed class StrongPasswordValidator : IPasswordValidator<AppUser>
{
    public const int MinScore = 3;

    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "PasswordEmpty", Description = "Enter a password." }));
        }
        var hints = new[] { user.UserName, user.Email, user.DisplayName }.Where(s => !string.IsNullOrEmpty(s)).Cast<string>();
        var result = Zxcvbn.Core.EvaluatePassword(password, hints);
        return Task.FromResult(result.Score >= MinScore
            ? IdentityResult.Success
            : IdentityResult.Failed(new IdentityError
            {
                Code = "PasswordTooWeak",
                Description = "That password is too easy to guess. A few unrelated words make a strong one.",
            }));
    }
}
