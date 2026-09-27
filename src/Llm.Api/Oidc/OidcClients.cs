using Llm.Api.Identity;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Llm.Api.Oidc;

/// <summary>
/// Registers the apps that sign in through the app, from .env, on every start:
/// a changed secret or domain takes effect with a restart. The registered
/// clients are exactly the configured ones: any other client in the database
/// is deleted, so a client nobody configures cannot keep accepting a secret.
/// </summary>
public sealed class OidcClients(
    IOpenIddictApplicationManager apps,
    IOpenIddictScopeManager scopes,
    IOptions<OidcOptions> oidc,
    IOptions<AuthOptions> auth)
{
    public async Task RunAsync(CancellationToken ct = default)
    {
        await UpsertScopeAsync(OidcScopes.Groups, "Groups", null, ct);
        await UpsertScopeAsync(OidcScopes.Api, "The model API", OidcScopes.ApiResource, ct);

        var d = auth.Value.Domain;
        var o = oidc.Value;
        await UpsertAppAsync("langfuse", "Langfuse", o.LangfuseSecret, $"https://traces.{d}/api/auth/callback/custom", $"https://traces.{d}/", ct);
        await UpsertMachineAsync("api", "Model API (machine clients)", o.ApiSecret, ct);

        var configured = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(o.LangfuseSecret))
        {
            configured.Add("langfuse");
        }
        if (!string.IsNullOrEmpty(o.ApiSecret))
        {
            configured.Add("api");
        }
        var others = new List<object>();
        await foreach (var app in apps.ListAsync(count: null, offset: null, ct))
        {
            if (await apps.GetClientIdAsync(app, ct) is not { } id || !configured.Contains(id))
            {
                others.Add(app);
            }
        }
        foreach (var app in others)
        {
            await apps.DeleteAsync(app, ct);
        }
    }

    private async Task UpsertAppAsync(string id, string name, string? secret, string redirect, string postLogout, CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = id,
            ClientSecret = secret,
            DisplayName = name,
            ClientType = ClientTypes.Confidential,
            // First-party apps of this service: no consent screen.
            ConsentType = ConsentTypes.Implicit,
            RedirectUris = { new Uri(redirect) },
            PostLogoutRedirectUris = { new Uri(postLogout) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.EndSession,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + OidcScopes.Groups,
            },
        };
        await UpsertAsync(id, secret, descriptor, ct);
    }

    private async Task UpsertMachineAsync(string id, string name, string? secret, CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = id,
            ClientSecret = secret,
            DisplayName = name,
            ClientType = ClientTypes.Confidential,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.ClientCredentials,
                Permissions.Prefixes.Scope + OidcScopes.Api,
            },
        };
        await UpsertAsync(id, secret, descriptor, ct);
    }

    private async Task UpsertAsync(string id, string? secret, OpenIddictApplicationDescriptor descriptor, CancellationToken ct)
    {
        var existing = await apps.FindByClientIdAsync(id, ct);
        if (string.IsNullOrEmpty(secret))
        {
            // No secret configured: the client must not exist (a stale one would still accept its old secret).
            if (existing is not null)
            {
                await apps.DeleteAsync(existing, ct);
            }
            return;
        }
        if (existing is null)
        {
            await apps.CreateAsync(descriptor, ct);
        }
        else
        {
            await apps.UpdateAsync(existing, descriptor, ct);
        }
    }

    private async Task UpsertScopeAsync(string name, string display, string? resource, CancellationToken ct)
    {
        var descriptor = new OpenIddictScopeDescriptor { Name = name, DisplayName = display };
        if (resource is not null)
        {
            descriptor.Resources.Add(resource);
        }
        var existing = await scopes.FindByNameAsync(name, ct);
        if (existing is null)
        {
            await scopes.CreateAsync(descriptor, ct);
        }
        else
        {
            await scopes.UpdateAsync(existing, descriptor, ct);
        }
    }
}
