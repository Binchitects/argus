using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools;

/// <summary>A tool an admin added: an MCP server, or an API by its OpenAPI document.</summary>
public interface IServerTool : IChatTool
{
    McpServer Server { get; }

    /// <summary>"Jira Cloud" -> "jira_cloud": the prefix of its functions' names.</summary>
    string Slug { get; }
}

/// <summary>An MCP server an admin added, as a tool: its functions are named "{server}__{function}".</summary>
public sealed partial class McpServerTool(McpServer server, HttpClient http, string? dataKey, TimeSpan callTimeout) : IServerTool
{
    public const string Prefix = "mcp:";

    public McpServer Server => server;
    public string Id => Prefix + server.Id;
    public string Title => server.Name;
    public string Description => server.Description ?? $"Tools from the MCP server at {server.Url}.";
    public string Icon => "plug";

    /// <summary>"Jira Cloud" -> "jira_cloud": the prefix that keeps its functions apart from other tools'.</summary>
    public string Slug => Slugify(server.Name);

    public static string Slugify(string name)
    {
        var s = NotSlug().Replace(name.ToLowerInvariant(), "_").Trim('_');
        return s.Length == 0 ? "mcp" : s[..Math.Min(20, s.Length)];
    }

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public IReadOnlyDictionary<string, string> Headers(string? email) => HeadersFor(server, dataKey, email);

    /// <summary>Each person's own account, for a plugin whose calls go as the person.</summary>
    public Plugins.PersonCredentials? People { get; init; }

    /// <summary>
    /// The headers for one person's calls: the server's, and for a plugin that signs in per person,
    /// theirs. Throws when they have not connected their account yet, or its sign-in lapsed (the chat says so, and where).
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> PersonHeadersAsync(McpServer server, string? dataKey, Plugins.PersonCredentials? people, ToolContext context,
        CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(HeadersFor(server, dataKey, context.Email), StringComparer.OrdinalIgnoreCase);
        if (server.PersonAuth is null || people is null)
        {
            return headers;
        }
        var (token, lapsed) = await people.TokenAsync(server, context.User.Id, ct);
        if (token is null)
        {
            throw new McpException(lapsed
                ? $"your {server.Name} sign-in has expired and could not be renewed: connect again, in Your account → Connections"
                : $"connect your {server.Name} account first, in Your account → Connections");
        }
        var manifest = Plugins.PluginManifest.Parse(server.Manifest!);
        headers[manifest.Header] = Plugins.PluginManifest.Fill(manifest.Value, new Dictionary<string, string> { ["token"] = token });
        return headers;
    }

    /// <summary>What every request to an admin's server carries: its key header (decrypted), and the person's email where it asks for it.</summary>
    public static IReadOnlyDictionary<string, string> HeadersFor(McpServer server, string? dataKey, string? email)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (server.HeaderName is { Length: > 0 } name && server.HeaderValueEncrypted is { } stored && SettingsCrypto.Decrypt(stored, dataKey) is { } value)
        {
            headers[name] = value;
        }
        if (server.EmailHeader is { Length: > 0 } emailHeader && email is not null)
        {
            headers[emailHeader] = email;
        }
        return headers;
    }

    /// <summary>Its tools, as the server lists them (to check a server before anyone relies on it).</summary>
    public async Task<System.Text.Json.Nodes.JsonArray> ListAsync(CancellationToken ct) =>
        await (await ConnectAsync(Headers(null), null, ct)).ToolsAsync(ct);

    /// <summary>A session; a refused certificate says what an admin can do about it.</summary>
    private async Task<McpSession> ConnectAsync(IReadOnlyDictionary<string, string> headers, TimeSpan? callTimeout, CancellationToken ct)
    {
        try
        {
            return await Mcp.ConnectAsync(http, new Uri(server.Url), headers, server.Name, ct, callTimeout);
        }
        catch (McpException ex) when (ex.Certificate)
        {
            throw new McpException(ServerTls.Untrusted(server.Name)) { Certificate = true };
        }
    }

    /// <summary>Longest one call may take: the server's own limit, or the chat's.</summary>
    public TimeSpan CallTimeout => server.CallTimeoutMinutes is { } m ? TimeSpan.FromMinutes(m) : callTimeout;

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) =>
        await StartAsync(context, await PersonHeadersAsync(server, dataKey, People, context, ct), ct);

    /// <summary>
    /// The person's sign-in read (from the database, renewed when it lapsed), and the rest of the
    /// start, the connection, as a start of its own that needs no database: Arena MCP reads the
    /// sign-ins one after another, then connects to the servers together.
    /// </summary>
    public async Task<Func<CancellationToken, Task<IToolRun>>> SignInAsync(ToolContext context, CancellationToken ct)
    {
        var headers = await PersonHeadersAsync(server, dataKey, People, context, ct);
        return token => StartAsync(context, headers, token);
    }

    private async Task<IToolRun> StartAsync(ToolContext context, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var session = await ConnectAsync(headers, CallTimeout, ct);
        var prefix = Slug + "__";
        var functions = Mcp.ToOpenAiTools(await session.ToolsAsync(ct), name => prefix + name);
        return new LocalRun(functions, session.Instructions, async (function, args, token) =>
        {
            var (text, isError) = await session.CallAsync(function.StartsWith(prefix, StringComparison.Ordinal) ? function[prefix.Length..] : function, args,
                context.Progress is { } p ? p.ReportAsync : null, token);
            return new ToolResult(text, isError);
        })
        {
            // A plugin's writes ask first, by their own names.
            AsksFirst = function => function.StartsWith(prefix, StringComparison.Ordinal) && server.Writes.Contains(function[prefix.Length..]),
        };
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();
}

/// <summary>A tool with an admin's choices for it, and why it cannot be used now (if so).</summary>
public sealed record ToolChoice(IChatTool Tool, ToolSetting Setting, string? Unavailable);

/// <summary>
/// Every tool the chat knows (built in, and the MCP servers admins added), with
/// what admins chose for each: on or off, for whom, on in new chats, ask first.
/// </summary>
public sealed class ToolRegistry(
    AppDbContext db, ArgusTool argus, ImageTool image, VectorTool vector, VideoTool video, SpeechTool speech, CalculatorTool calculator, TimeTool time, FilesTool files, CanvasTool canvas, PythonTool python, WebTool web, AskTool ask, AgentsTool agents, MemoryTool memory, SkillTool skills,
    IHttpClientFactory http, IOptions<AuthOptions> auth, IOptionsMonitor<ChatOptions> chat, Plugins.PersonCredentials people, Knowledge.KnowledgeTool knowledge, LayaTool laya,
    ResearchTool research, BlenderTool blender, Browser.BrowserTool browser, DataTool data, DeckTool decks, Plans.PlansTool plans, Code.ReviewTool reviews, GatewayChat gateway, ToolUsage usage, CodeTool code, Lab.LabTool lab,
    SandboxClient sandbox, IServiceScopeFactory scopes, IOptionsMonitor<SandboxOptions> sandboxOptions)
{
    public const string McpClient = "mcp";

    public async Task<IReadOnlyList<ToolChoice>> AllAsync(CancellationToken ct = default)
    {
        var settings = await db.ToolSettings.AsNoTracking().ToDictionaryAsync(s => s.ToolId, ct);
        var servers = await db.McpServers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var customs = await db.CustomTools.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
        IEnumerable<IChatTool> tools = [argus, knowledge, data, decks, python, code, lab, web, research, browser, image, vector, video, speech, calculator, time, files, canvas, blender, plans, reviews, ask, agents, memory, skills, laya,
            .. servers.Select(Server), .. customs.Select(t => CustomTools.For(t, http, gateway, auth.Value.DataKey))];
        var all = new List<ToolChoice>();
        foreach (var tool in tools)
        {
            all.Add(new ToolChoice(tool, settings.GetValueOrDefault(tool.Id) ?? tool.Defaults(), await tool.UnavailableAsync(ct)));
        }
        return all;
    }

    /// <summary>The server as a tool, on its own connection (its certificate checked as the admin chose: ServerClients).</summary>
    public IServerTool Server(McpServer server) => Server(server, ServerClients.For(http, server));

    /// <param name="client">The connection to use: a test's, with the form's certificate check.</param>
    public IServerTool Server(McpServer server, HttpClient client) =>
        server.Url.StartsWith(Plugins.PluginManifest.SandboxScheme, StringComparison.Ordinal) ? new SandboxServerTool(server, sandbox, scopes, auth.Value.DataKey, sandboxOptions)
        : server.Spec is { Length: > 0 }
        ? new OpenApiTool(server, client, auth.Value.DataKey, chat.CurrentValue.ToolCallTimeout) { People = people }
        : new McpServerTool(server, client, auth.Value.DataKey, chat.CurrentValue.ToolCallTimeout) { People = people };

    /// <summary>The feature permission a built-in tool also needs (Admin → Access): the web, pictures, video, speech, memory, code.</summary>
    public static readonly IReadOnlyDictionary<string, string> FeatureOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["web"] = Core.Access.Permissions.WebSearch,
        [ResearchTool.ToolId] = Core.Access.Permissions.Research,
        ["python"] = Core.Access.Permissions.Python,
        [CodeTool.ToolId] = Core.Access.Permissions.Python,
        // The lab's mathematics runs in the sandbox, as Python does.
        [Lab.LabTool.ToolId] = Core.Access.Permissions.Python,
        ["canvas"] = Core.Access.Permissions.Canvas,
        [BlenderTool.ToolId] = Core.Access.Permissions.ThreeD,
        [Browser.BrowserTool.ToolId] = Core.Access.Permissions.Browser,
        [DataTool.ToolId] = Core.Access.Permissions.Data,
        [DeckTool.ToolId] = Core.Access.Permissions.Decks,
        [Plans.PlansTool.ToolId] = Core.Access.Permissions.Plans,
        ["knowledge"] = Core.Access.Permissions.Knowledge,
        ["image"] = Core.Access.Permissions.Pictures,
        [VectorTool.ToolId] = Core.Access.Permissions.Pictures,
        ["video"] = Core.Access.Permissions.Video,
        ["speech"] = Core.Access.Permissions.Speech,
        ["memory"] = Core.Access.Permissions.Memory,
        ["argus"] = Core.Access.Permissions.CodeRead,
        [Code.ReviewTool.ToolId] = Core.Access.Permissions.ReviewRun,
    };

    /// <summary>A tool that reads or acts as the person who asks (their memory, code, knowledge, an admin's server or plugin): off in rooms.</summary>
    public static bool Personal(IChatTool tool) => tool is IServerTool || tool.Id is "memory" or SkillTool.ToolId or "argus" or "knowledge" or Code.ReviewTool.ToolId or Browser.BrowserTool.ToolId or DataTool.ToolId or DeckTool.ToolId;

    /// <summary>Whether this person may use the tool by the rules: the tool allowed to them, and its feature held.</summary>
    public static bool Allowed(Membership member, string toolId) =>
        member.MayUse(Core.Access.ResourceKinds.Tool, toolId) && (!FeatureOf.TryGetValue(toolId, out var feature) || member.Has(feature));

    /// <summary>The tools this person may use now: on, allowed to them, and available.</summary>
    /// <summary>
    /// The tools this person may use, each with their settings: the company's, and their groups' over them (on in new chats when
    /// a group says so; asking first only when every group that says asks; the most calls a day of their groups).
    /// </summary>
    public async Task<IReadOnlyList<ToolChoice>> ForAsync(Membership member, CancellationToken ct = default)
    {
        var mine = member.Groups.Count == 0 ? [] : await db.ToolGroupSettings.AsNoTracking().Where(g => member.Groups.Contains(g.GroupId)).ToListAsync(ct);
        var choices = (await AllAsync(ct)).Where(t => t.Setting.Enabled && t.Unavailable is null && Allowed(member, t.Tool.Id))
            .Select(t => mine.Where(g => g.ToolId == t.Tool.Id).ToList() is { Count: > 0 } groups ? t with { Setting = ForGroups(t.Setting, groups) } : t).ToList();
        return await WithGroupPluginsAsync(member, choices, ct);
    }

    /// <summary>The plugins the person's groups set their own values for (another key, another address): each as their groups set it.</summary>
    private async Task<IReadOnlyList<ToolChoice>> WithGroupPluginsAsync(Membership member, List<ToolChoice> choices, CancellationToken ct)
    {
        if (member.Groups.Count == 0)
        {
            return choices;
        }
        var theirs = await db.PluginGroupSettings.AsNoTracking().Where(g => member.Groups.Contains(g.GroupId))
            .Join(db.Groups.AsNoTracking(), s => s.GroupId, g => g.Id, (s, g) => new { s.ServerId, s.SettingsJson, g.Name }).ToListAsync(ct);
        if (theirs.Count == 0)
        {
            return choices;
        }
        var ids = theirs.Select(t => t.ServerId).Distinct().ToList();
        var servers = await db.McpServers.AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        return [.. choices.Select(c =>
        {
            if (!c.Tool.Id.StartsWith(McpServerTool.Prefix, StringComparison.Ordinal) || !Guid.TryParse(c.Tool.Id[McpServerTool.Prefix.Length..], out var id)
                || !servers.TryGetValue(id, out var server))
            {
                return c;
            }
            var settings = theirs.Where(t => t.ServerId == id).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => t.SettingsJson).ToList();
            return c with { Tool = Server(Plugins.PluginInstaller.ForGroups(server, settings, auth.Value.DataKey)) };
        })];
    }

    /// <summary>A tool's settings for a person in these groups.</summary>
    public static ToolSetting ForGroups(ToolSetting company, IReadOnlyList<ToolGroupSetting> groups)
    {
        var on = groups.Where(g => g.OnByDefault is not null).Select(g => g.OnByDefault!.Value).ToList();
        var ask = groups.Where(g => g.AskFirst is not null).Select(g => g.AskFirst!.Value).ToList();
        var calls = groups.Where(g => g.CallsPerDay is not null).Select(g => g.CallsPerDay!.Value).ToList();
        return new ToolSetting
        {
            ToolId = company.ToolId, Enabled = company.Enabled, Title = company.Title, Description = company.Description, Instructions = company.Instructions,
            ReplaceInstructions = company.ReplaceInstructions, DisabledFunctions = company.DisabledFunctions, AskFirstFunctions = company.AskFirstFunctions,
            FunctionsJson = company.FunctionsJson, UpdatedAt = company.UpdatedAt,
            OnByDefault = on.Count > 0 ? on.Any(x => x) : company.OnByDefault,
            AskFirst = ask.Count > 0 ? ask.All(x => x) : company.AskFirst,
            // 0 is no limit: the most of all.
            CallsPerDay = calls.Count > 0 ? (calls.Contains(0) ? 0 : calls.Max()) : company.CallsPerDay,
        };
    }

    /// <summary>
    /// The tool made ready for one answer or call, as the admin customized it (functions, descriptions, fixed arguments, asking
    /// first, calls a day), each call recorded. Every chat, sub-agent and Arena MCP call starts its tools here.
    /// </summary>
    public async Task<IToolRun> StartAsync(ToolChoice choice, ToolContext context, CancellationToken ct)
    {
        var run = await choice.Tool.StartAsync(context, ct);
        var userId = context.User.Id;
        return new CustomizedRun(run, choice.Setting, choice.Tool.Id, userId, usage, token => usage.TodayAsync(db, userId, choice.Tool.Id, token));
    }

    /// <summary>The name and description people see: the admin's, or the tool's own.</summary>
    public static string TitleOf(ToolChoice c) => c.Setting.Title is { Length: > 0 } t ? t : c.Tool.Title;

    public static string DescriptionOf(ToolChoice c) => c.Setting.Description is { Length: > 0 } d ? d : c.Tool.Description;

    /// <summary>Whether this person may use the tool now: on, allowed to them, and available.</summary>
    public async Task<bool> MayUseAsync(Membership member, string toolId, CancellationToken ct = default) => (await ForAsync(member, ct)).Any(t => t.Tool.Id == toolId);

    /// <summary>What a chat uses: its own choice, or the tools on in new chats; either way only what the person may use.</summary>
    public static IReadOnlyList<ToolChoice> Chosen(IReadOnlyList<string>? chat, IReadOnlyList<ToolChoice> allowed) =>
        chat is null ? [.. allowed.Where(t => t.Setting.OnByDefault)] : [.. allowed.Where(t => chat.Contains(t.Tool.Id))];
}

/// <summary>
/// Calls waiting for the person to allow them ("ask before running"): the answer
/// waits here until the page says yes or no, or until it gives up. With several
/// replicas, a yes or no posted to another replica is passed to the one waiting.
/// </summary>
public sealed class ToolApprovals
{
    private const string Topic = "tool:decision";
    private readonly ConcurrentDictionary<(Guid Conversation, string Call), TaskCompletionSource<bool>> _waiting = new();
    private readonly Operations.Replicas replicas;

    /// <summary>A yes or no, as passed between replicas.</summary>
    private sealed record Decision(Guid Conversation, string Call, bool Allow);

    public ToolApprovals(Operations.Replicas replicas)
    {
        this.replicas = replicas;
        replicas.On(Topic, payload => System.Text.Json.JsonSerializer.Deserialize<Decision>(payload) is { } d && Decide(d.Conversation, d.Call, d.Allow));
    }

    public async Task<bool> WaitAsync(Guid conversation, string call, TimeSpan patience, CancellationToken ct)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[(conversation, call)] = decision;
        try
        {
            return await decision.Task.WaitAsync(patience, ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            _waiting.TryRemove((conversation, call), out _);
        }
    }

    /// <returns>False when no call with that id is waiting (here).</returns>
    public bool Decide(Guid conversation, string call, bool allow) =>
        _waiting.TryGetValue((conversation, call), out var decision) && decision.TrySetResult(allow);

    /// <returns>False when no call with that id is waiting on any replica.</returns>
    public async Task<bool> DecideAnywhereAsync(Guid conversation, string call, bool allow, CancellationToken ct) =>
        Decide(conversation, call, allow) || await replicas.AskAsync(Topic, System.Text.Json.JsonSerializer.Serialize(new Decision(conversation, call, allow)), ct);
}
