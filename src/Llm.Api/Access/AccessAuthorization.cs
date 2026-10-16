using Llm.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace Llm.Api.Access;

/// <summary>An endpoint's need: a permission of the catalog, or (null) the Owner role.</summary>
public sealed record PermissionRequirement(string? Permission) : IAuthorizationRequirement;

/// <summary>Decides <see cref="PermissionRequirement"/> with the one access check, for the person signed in.</summary>
public sealed class PermissionHandler(UserManager<AppUser> users, AccessService access) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true || await users.GetUserAsync(context.User) is not { IsDisabled: false } user)
        {
            return;
        }
        var member = await access.MembershipAsync(user);
        if (requirement.Permission is null ? member.IsOwner : member.Has(requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}
