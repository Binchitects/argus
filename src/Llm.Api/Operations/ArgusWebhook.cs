using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Llm.Api.Operations;

/// <summary>
/// The GitLab webhook that brings a repository's index up to date on a push or a merge. A new
/// secret is made here and shown once, for the admin to paste into GitLab; Argus keeps only its
/// SHA-256, through its admin API. Nothing goes through .env, and the app keeps no copy.
/// </summary>
public sealed class ArgusWebhook(ArgusAdmin argus, IOptions<Identity.AuthOptions> auth)
{
    public const string Header = "X-Gitlab-Token";

    /// <summary>Where GitLab sends its events: https://argus.DOMAIN/hook/gitlab.</summary>
    public string Url => auth.Value.HttpsPort == 443
        ? $"https://argus.{auth.Value.Domain}/hook/gitlab"
        : $"https://argus.{auth.Value.Domain}:{auth.Value.HttpsPort}/hook/gitlab";

    /// <summary>A new secret (the old one stops working); only its hash leaves this method for Argus.</summary>
    public async Task<string> RotateAsync(CancellationToken ct)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        await argus.PutAsync("webhook", new JsonObject { ["token_sha256"] = Hash(token) }, ct);
        return token;
    }

    public Task DisableAsync(CancellationToken ct) => argus.PutAsync("webhook", new JsonObject { ["token_sha256"] = "" }, ct);

    /// <summary>What the Indexing page shows: on or off, where to point GitLab, the last deliveries. Never the secret.</summary>
    public async Task<JsonObject> ViewAsync(CancellationToken ct)
    {
        var state = await argus.GetAsync("webhook", ct) as JsonObject ?? [];
        return new JsonObject
        {
            ["enabled"] = state["enabled"]?.GetValue<bool>() == true, ["fromEnv"] = state["from_env"]?.GetValue<bool>() == true,
            ["url"] = Url, ["header"] = Header,
            ["deliveries"] = state["deliveries"]?.DeepClone() ?? new JsonArray(),
        };
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
