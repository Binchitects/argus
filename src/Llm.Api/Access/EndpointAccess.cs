using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Access;

/// <summary>An endpoint needs this permission (<see cref="Core.Access.Permissions"/>) of the person signed in.</summary>
public sealed record PermissionMetadata(string Permission);

/// <summary>An endpoint for any person signed in, about their own things (their chats, their account).</summary>
public sealed record PersonOnlyMetadata;

/// <summary>An endpoint open without a session, and why (it checks a credential of its own, or shows nothing private).</summary>
public sealed record AnonymousMetadata(string Reason);

/// <summary>
/// Every route says who may call it: a permission (<see cref="RequirePermission"/>), any person signed in
/// (<see cref="PersonOnly"/>), or nobody signed in, with the reason (<see cref="AllowAnonymous{TBuilder}(TBuilder, string)"/>).
/// A test fails for a route that says none of these (EndpointAccessTests).
/// </summary>
public static class EndpointAccess
{
    public const string PolicyPrefix = "permission:";

    /// <summary>The person signed in (a session, not a key) must hold <paramref name="permission"/>; else 403.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder
    {
        if (!Core.Access.Permissions.IsKnown(permission))
        {
            throw new ArgumentException($"{permission} is not in the permission catalog.", nameof(permission));
        }
        return builder.RequireAuthorization(PolicyPrefix + permission).WithMetadata(new PermissionMetadata(permission));
    }

    /// <summary>
    /// A group's routes need <paramref name="read"/> to look (GET and HEAD) and <paramref name="write"/> to change anything,
    /// unless a route says otherwise itself (<see cref="RequirePermission"/>, <see cref="PersonOnly"/>, an anonymous reason).
    /// </summary>
    public static RouteGroupBuilder RequirePermissions(this RouteGroupBuilder group, string read, string write)
    {
        foreach (var permission in new[] { read, write }.Where(p => !Core.Access.Permissions.IsKnown(p)))
        {
            throw new ArgumentException($"{permission} is not in the permission catalog.", nameof(read));
        }
        // After each route's own conventions, so a route's own permission is the one it needs, not both.
        ((IEndpointConventionBuilder)group).Finally(b =>
        {
            if (b.Metadata.Any(m => m is PermissionMetadata or PersonOnlyMetadata or AnonymousMetadata))
            {
                return;
            }
            var methods = b.Metadata.OfType<HttpMethodMetadata>().SelectMany(m => m.HttpMethods).ToList();
            var permission = methods.Count > 0 && methods.All(m => HttpMethods.IsGet(m) || HttpMethods.IsHead(m)) ? read : write;
            b.Metadata.Add(new AuthorizeAttribute(PolicyPrefix + permission));
            b.Metadata.Add(new PermissionMetadata(permission));
        });
        return group;
    }

    /// <summary>Where a list route finds whom a role held for some groups reaches (<see cref="AccessService.Reach"/>); absent: everyone.</summary>
    public const string ReachItem = "access.reach";

    /// <summary>
    /// A role held for some groups only (People admin for the ML team: a team manager) reaches only them: a person or a
    /// group outside is not there (404); a list shows only theirs (<see cref="ReachItem"/>); anything else (making
    /// people or groups, changing who is in a group, the directory's moves) is for those who manage everyone (403).
    /// </summary>
    public static RouteGroupBuilder ScopedToGroups(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var endpoint = http.GetEndpoint();
            if (endpoint?.Metadata.GetMetadata<PermissionMetadata>()?.Permission is not { } permission
                || await http.RequestServices.GetRequiredService<UserManager<Core.Identity.AppUser>>().GetUserAsync(http.User) is not { } me
                || await http.RequestServices.GetRequiredService<AccessService>().ReachAsync(me, permission, http.RequestAborted) is not { } reach)
            {
                return await next(ctx);
            }
            var pattern = ((endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "").TrimEnd('/');
            var read = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method);
            if (http.Request.RouteValues.TryGetValue("id", out var raw) && Guid.TryParse(raw?.ToString(), out var id))
            {
                var inReach = pattern.Contains("/people/", StringComparison.Ordinal) ? reach.People.Contains(id)
                    : pattern.Contains("/groups/", StringComparison.Ordinal) && reach.Groups.Contains(id);
                if (!inReach)
                {
                    return Results.NotFound();
                }
                if (read || !pattern.Contains("/members", StringComparison.Ordinal))
                {
                    return await next(ctx);
                }
            }
            else if (read && (pattern.EndsWith("/people", StringComparison.Ordinal) || pattern.EndsWith("/groups", StringComparison.Ordinal)))
            {
                http.Items[ReachItem] = reach;
                return await next(ctx);
            }
            return Endpoints.AuthEndpoints.Problem(403, "scope", "Your role covers some groups only: this is for an admin who manages everyone.");
        });
        return group;
    }

    /// <summary>Any person signed in; what they reach is their own.</summary>
    public static TBuilder PersonOnly<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization().WithMetadata(new PersonOnlyMetadata());

    /// <summary>Open without a session; <paramref name="reason"/> says why that is safe.</summary>
    public static TBuilder AllowAnonymous<TBuilder>(this TBuilder builder, string reason) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return builder.AllowAnonymous().WithMetadata(new AnonymousMetadata(reason));
    }
}

/// <summary>Makes the policy of a permission on first use: a session (not a key or a token), and the permission held.</summary>
public sealed class PermissionPolicies(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(EndpointAccess.PolicyPrefix, StringComparison.Ordinal))
        {
            return new AuthorizationPolicyBuilder(IdentityConstants.ApplicationScheme)
                .AddRequirements(new PermissionRequirement(policyName[EndpointAccess.PolicyPrefix.Length..]))
                .Build();
        }
        return await base.GetPolicyAsync(policyName);
    }
}
