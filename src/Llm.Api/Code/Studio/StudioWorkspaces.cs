using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Llm.Api.Code.Fixes;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Code;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Code.Studio;

/// <summary>
/// Code Studio's workspaces service (compose profile "workspaces"): where the app drives it (<see cref="Url"/>, with
/// <see cref="Key"/>, WORKSPACES_KEY), the name its engines answer at (<see cref="EngineHost"/>), and the addresses the
/// engines reach the app, the gateway and Argus at over the stack's own network (Argus checks the person's own key).
/// </summary>
public sealed class WorkspacesOptions
{
    public string? Url { get; set; }
    public string? Key { get; set; }
    public string EngineHost { get; set; } = "workspaces";
    /// <summary>The app, as the workspaces reach it (its API and the code arena's routes, with the workspace's key).</summary>
    public string ArenaUrl { get; set; } = "http://app:8080";
    public string GatewayUrl { get; set; } = "http://litellm:4000";
    public string ArgusUrl { get; set; } = "http://argus:7700/mcp";
    /// <summary>A workspace unused this long sleeps: its engine stops, its files stay.</summary>
    public TimeSpan Idle { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>The most workspaces one person keeps.</summary>
    public int MostPerPerson { get; set; } = 8;
}

/// <summary>Why a workspace could not be made or started, in words for the person.</summary>
public sealed class StudioException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Code Studio's workspaces on the server: made from a GitLab project and branch the person reads with their own token
/// (the one Fix it links), started on the workspaces service (cloned once, Code Arena's engine run for it as its own
/// user, its agent with a key of its own at the gateway), put to sleep, and deleted with their files and their key.
/// </summary>
public sealed class StudioWorkspaces(AppDbContext db, IHttpClientFactory http, IOptionsMonitor<WorkspacesOptions> options, IOptions<AuthOptions> auth,
    GitLabAsPerson gitlab, ILiteLlm gateway, Models.KeyAccess keyAccess, Gateway.RateLimits rateLimits, Operations.Modules modules, TimeProvider clock)
{
    public const string Client = "workspaces";

    /// <summary>Set up (its address and key), and running here (the workspaces module).</summary>
    public async Task<bool> EnabledAsync(CancellationToken ct) => options.CurrentValue is { Url.Length: > 0, Key.Length: >= 32 } && await modules.HasAsync("workspaces", ct);

    private HttpClient Service()
    {
        var c = http.CreateClient(Client);
        c.BaseAddress = new Uri(options.CurrentValue.Url!.TrimEnd('/') + "/");
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.CurrentValue.Key);
        return c;
    }

    private string DataKey => auth.Value.DataKey is { Length: > 0 } k ? k : throw new StudioException(400, "APP_DATA_KEY is not set, so a workspace's keys cannot be kept encrypted.");

    /// <summary>A workspace of the person's project and branch (its default branch when none), checked with GitLab as them.</summary>
    public async Task<StudioWorkspace> CreateAsync(AppUser me, string project, string? branch, string? name, CancellationToken ct)
    {
        if (!await EnabledAsync(ct))
        {
            throw new StudioException(503, "Workspaces on the server are not set up here (the workspaces module).");
        }
        if (await db.StudioWorkspaces.CountAsync(w => w.UserId == me.Id && w.Kind == StudioWorkspace.Server, ct) >= options.CurrentValue.MostPerPerson)
        {
            throw new StudioException(409, $"You have {options.CurrentValue.MostPerPerson} workspaces: delete one first.");
        }
        var token = await gitlab.TokenAsync(me.Id, ct) ?? throw new StudioException(409, "Link your GitLab token first (Code Studio → GitLab token): the workspace clones and pushes as you.");
        project = project.Trim().Trim('/');
        JsonNode repo;
        try
        {
            repo = await gitlab.GetAsync(token, GitLabAsPerson.Project(project), ct);
        }
        catch (GitLabException ex)
        {
            throw new StudioException(404, $"GitLab did not show you {project}: {ex.Message}");
        }
        var chosen = branch?.Trim() is { Length: > 0 } b ? b : repo["default_branch"]?.GetValue<string>() ?? "main";
        try
        {
            await gitlab.GetAsync(token, $"{GitLabAsPerson.Project(project)}/repository/branches/{Uri.EscapeDataString(chosen)}", ct);
        }
        catch (GitLabException)
        {
            throw new StudioException(404, $"{project} has no branch {chosen}.");
        }
        var workspace = new StudioWorkspace
        {
            UserId = me.Id, Kind = StudioWorkspace.Server, Project = repo["path_with_namespace"]?.GetValue<string>() ?? project, Branch = chosen,
            Name = name?.Trim() is { Length: > 0 } n ? n[..Math.Min(200, n.Length)] : $"{repo["name"]?.GetValue<string>() ?? project} ({chosen})",
        };
        db.StudioWorkspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        return workspace;
    }

    /// <summary>
    /// The workspace's engine running: cloned once, started (again) with the person's GitLab token and its agent's key at
    /// the gateway (made once, kept encrypted); its port and its key kept for the IDE's requests.
    /// </summary>
    public async Task StartAsync(StudioWorkspace workspace, AppUser me, CancellationToken ct)
    {
        if (!await EnabledAsync(ct))
        {
            throw new StudioException(503, "Workspaces on the server are not set up here (the workspaces module).");
        }
        var token = await gitlab.TokenAsync(me.Id, ct) ?? throw new StudioException(409, "Link your GitLab token first: the workspace clones and pushes as you.");
        if (workspace.KeyCipher is null)
        {
            var alias = $"studio:{workspace.Id:N}";
            var made = await gateway.GenerateKeyAsync(me.Email!, alias, await keyAccess.ListForAsync(me, ct), keyAccess.MaxParallel, (await rateLimits.ForAsync(me, ct)).Key, ct);
            (workspace.KeyCipher, workspace.KeyAlias) = (SettingsCrypto.Encrypt(made, DataKey), alias);
            await db.SaveChangesAsync(ct);
        }
        var o = options.CurrentValue;
        var login = await db.PersonCredentials.AsNoTracking().Where(c => c.UserId == me.Id && c.ToolId == GitLabAsPerson.ToolId).Select(c => c.Account).SingleOrDefaultAsync(ct);
        var spec = new JsonObject
        {
            ["repo"] = $"{gitlab.BaseUrl}/{workspace.Project}.git", ["branch"] = workspace.Branch, ["git_user"] = login is { Length: > 0 } ? login : "oauth2", ["git_token"] = token,
            ["arena_url"] = o.ArenaUrl, ["api_key"] = SettingsCrypto.Decrypt(workspace.KeyCipher!, DataKey), ["gateway_url"] = o.GatewayUrl,
            ["mcp_url"] = o.ArenaUrl.TrimEnd('/') + "/mcp", ["argus_url"] = o.ArgusUrl,
        };
        using var res = await Service().PutAsJsonAsync($"v1/workspaces/{workspace.Id:N}", spec, ct);
        var answer = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        if (!res.IsSuccessStatusCode)
        {
            workspace.Error = answer?["error"]?.GetValue<string>() ?? $"The workspaces service answered HTTP {(int)res.StatusCode}.";
            workspace.EnginePort = null;
            await db.SaveChangesAsync(ct);
            throw new StudioException((int)res.StatusCode is 400 or 404 or 409 or 503 ? (int)res.StatusCode : 502, workspace.Error);
        }
        workspace.EnginePort = answer!["port"]!.GetValue<int>();
        workspace.EngineKeyCipher = SettingsCrypto.Encrypt(answer["key"]!.GetValue<string>(), DataKey);
        workspace.StartedAt = workspace.UsedAt = clock.GetUtcNow();
        workspace.Error = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The workspace asleep: its engine stopped on the service (its files stay).</summary>
    public async Task StopAsync(StudioWorkspace workspace, CancellationToken ct)
    {
        if (await EnabledAsync(ct) && workspace.Kind == StudioWorkspace.Server)
        {
            using var res = await Service().PostAsync($"v1/workspaces/{workspace.Id:N}/stop", null, ct);
        }
        (workspace.EnginePort, workspace.EngineKeyCipher, workspace.StartedAt) = (null, null, null);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The workspace gone: its files on the service, its agent's key at the gateway, its row.</summary>
    public async Task DeleteAsync(StudioWorkspace workspace, CancellationToken ct)
    {
        if (await EnabledAsync(ct) && workspace.Kind == StudioWorkspace.Server)
        {
            using var res = await Service().DeleteAsync($"v1/workspaces/{workspace.Id:N}", ct);
        }
        if (workspace.KeyCipher is not null && SettingsCrypto.Decrypt(workspace.KeyCipher, auth.Value.DataKey) is { } key)
        {
            // The gateway knows a key by its token (the key's hash).
            await gateway.DeleteKeysAsync([AnswerCache.HashOf(key)], ct);
        }
        db.StudioWorkspaces.Remove(workspace);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Where the IDE's requests go: the engine's base address and key, or null when it sleeps.</summary>
    public (Uri Base, string Key)? EngineOf(StudioWorkspace workspace) =>
        workspace.EnginePort is { } port && workspace.EngineKeyCipher is { } cipher && SettingsCrypto.Decrypt(cipher, auth.Value.DataKey) is { } key
            ? (new Uri($"http://{options.CurrentValue.EngineHost}:{port}/"), key)
            : null;
}
