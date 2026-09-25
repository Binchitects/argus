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

/// <summary>An MCP server an admin added, as a tool: its functions are named "{server}__{function}".</summary>
public sealed partial class McpServerTool(McpServer server, HttpClient http, string? dataKey) : IChatTool
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

    public IReadOnlyDictionary<string, string> Headers(string? email)
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
        await (await Mcp.ConnectAsync(http, new Uri(server.Url), Headers(null), server.Name, ct)).ToolsAsync(ct);

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var session = await Mcp.ConnectAsync(http, new Uri(server.Url), Headers(context.Email), server.Name, ct);
        var prefix = Slug + "__";
        var functions = Mcp.ToOpenAiTools(await session.ToolsAsync(ct), name => prefix + name);
        return new LocalRun(functions, session.Instructions, async (function, args, token) =>
        {
            var (text, isError) = await session.CallAsync(function.StartsWith(prefix, StringComparison.Ordinal) ? function[prefix.Length..] : function, args, token);
            return new ToolResult(text, isError);
        });
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
    AppDbContext db, ArgusTool argus, ImageTool image, CalculatorTool calculator, TimeTool time,
    IHttpClientFactory http, IOptions<AuthOptions> auth)
{
    public const string McpClient = "mcp";

    public async Task<IReadOnlyList<ToolChoice>> AllAsync(CancellationToken ct = default)
    {
        var settings = await db.ToolSettings.AsNoTracking().ToDictionaryAsync(s => s.ToolId, ct);
        var servers = await db.McpServers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        IEnumerable<IChatTool> tools = [argus, image, calculator, time, .. servers.Select(Server)];
        var all = new List<ToolChoice>();
        foreach (var tool in tools)
        {
            all.Add(new ToolChoice(tool, settings.GetValueOrDefault(tool.Id) ?? new ToolSetting { ToolId = tool.Id }, await tool.UnavailableAsync(ct)));
        }
        return all;
    }

    public McpServerTool Server(McpServer server) => new(server, http.CreateClient(McpClient), auth.Value.DataKey);

    /// <summary>The tools this person may use now: on, allowed to them, and available.</summary>
    public async Task<IReadOnlyList<ToolChoice>> ForAsync(Membership member, CancellationToken ct = default) =>
        [.. (await AllAsync(ct)).Where(t => t.Setting.Enabled && t.Unavailable is null && member.May(t.Setting.Audience, t.Setting.Groups))];

    /// <summary>What a chat uses: its own choice, or the tools on in new chats; either way only what the person may use.</summary>
    public static IReadOnlyList<ToolChoice> Chosen(IReadOnlyList<string>? chat, IReadOnlyList<ToolChoice> allowed) =>
        chat is null ? [.. allowed.Where(t => t.Setting.OnByDefault)] : [.. allowed.Where(t => chat.Contains(t.Tool.Id))];
}

/// <summary>
/// Calls waiting for the person to allow them ("ask before running"): the answer
/// waits here until the page says yes or no, or until it gives up.
/// </summary>
public sealed class ToolApprovals
{
    private readonly ConcurrentDictionary<(Guid Conversation, string Call), TaskCompletionSource<bool>> _waiting = new();

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

    /// <returns>False when no call with that id is waiting.</returns>
    public bool Decide(Guid conversation, string call, bool allow) =>
        _waiting.TryGetValue((conversation, call), out var decision) && decision.TrySetResult(allow);
}
