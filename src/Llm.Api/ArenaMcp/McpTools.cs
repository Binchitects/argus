using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Llm.Api.ArenaMcp;

/// <summary>
/// What a person's tools serve at the moment: the MCP tool list, the notes for the agent, and
/// which tool has each function (so a call starts only that one).
/// </summary>
public sealed record McpCatalog(JsonArray Tools, string Instructions, IReadOnlyDictionary<string, string> Owners);

/// <summary>
/// The person's chat tools, served to outside agents: exactly those they may use in the chat
/// (the admins' choices, their groups), with the same function names and schemas, run as them
/// (Argus with their GitLab access, a plugin with their own account, the gateway on their
/// credit). Each call is audited (mcp.call). A tool set to ask first cannot show the app's
/// approval card here: it is marked destructive, and its description tells the client to ask.
/// </summary>
public sealed partial class McpTools(
    ToolRegistry registry, AccessService access, Audit audit, AppDbContext db, IMemoryCache cache,
    IOptions<AuthOptions> auth, IOptionsMonitor<BrandingOptions> branding, IOptionsMonitor<McpOptions> options, ILogger<McpTools> logger)
{
    /// <summary>Tools that only make sense inside a chat: its files, questions to the person, sub-agents.</summary>
    public static readonly string[] ChatOnly = ["files", "ask", "agents"];

    /// <summary>How long a person's tool list is kept: an agent lists right after it connects, and calls by it.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);

    /// <summary>Pictures and sound up to this size come inline as well as by link: the agent sees them at once.</summary>
    private const int InlineBytes = 4 * 1024 * 1024;

    internal const string AskFirstNote =
        " Ask the person before each call and wait for their yes: in the chat this tool asks them first, and this server cannot ask them itself.";

    /// <summary>The tools served to this person now: on, allowed to them, available, and not the chat's own.</summary>
    public async Task<IReadOnlyList<ToolChoice>> ServedAsync(AppUser user, CancellationToken ct) =>
        [.. (await registry.ForAsync(await access.MembershipAsync(user, ct), ct)).Where(t => !ChatOnly.Contains(t.Tool.Id))];

    /// <summary>
    /// A tool whose start is only a network round trip, which may be slow or down (Argus, an admin's
    /// MCP server without a per-person sign-in): started beside the others, not after them. The rest
    /// start one after another, as they share the request's database context.
    /// </summary>
    private static bool Remote(IChatTool tool) => tool is ArgusTool or McpServerTool { Server.PersonAuth: null };

    /// <summary>
    /// A plugin with a sign-in of each person's own: reading it uses the request's database context
    /// (and may renew it with the plugin's provider), so it is read with the others' one after
    /// another; an MCP server's connection then runs beside the remote ones.
    /// </summary>
    private static bool SignsIn(IChatTool tool) => tool is IServerTool { Server.PersonAuth: not null };

    /// <summary>
    /// Every served tool started, its functions listed; kept a minute unless <paramref name="fresh"/>.
    /// The remote ones and the plugins with a sign-in of the person's start together, first, and are
    /// waited for <see cref="McpOptions.ListWaitSeconds"/> at most, all together: one slower is listed
    /// as not available now (and the list kept only briefly), so a slow or dead server never holds an
    /// agent's connection.
    /// </summary>
    public async Task<McpCatalog> CatalogAsync(AppUser user, bool fresh, CancellationToken ct)
    {
        if (!fresh && cache.TryGetValue(CatalogKey(user), out McpCatalog? kept) && kept is not null)
        {
            return kept;
        }
        var context = Context(user, null);
        var tools = new JsonArray();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes = new List<string>();
        var unavailable = new List<string>();
        var served = await ServedAsync(user, ct);
        var wait = TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.ListWaitSeconds));
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waiting.CancelAfter(wait);
        var early = served.Where(c => Remote(c.Tool)).ToDictionary(c => c.Tool.Id, c => AttemptAsync(c, t => c.Tool.StartAsync(context, t), wait, waiting.Token, ct));
        foreach (var choice in served.Where(c => SignsIn(c.Tool)))
        {
            if (choice.Tool is McpServerTool server)
            {
                var (connect, failure) = await AttemptAsync(choice, t => server.SignInAsync(context, t), wait, waiting.Token, ct);
                early[choice.Tool.Id] = connect is null ? Task.FromResult<(IToolRun?, string?)>((null, failure)) : AttemptAsync(choice, connect, wait, waiting.Token, ct);
            }
            else
            {
                early[choice.Tool.Id] = Task.FromResult(await AttemptAsync(choice, t => choice.Tool.StartAsync(context, t), wait, waiting.Token, ct));
            }
        }
        var slow = false;
        foreach (var choice in served)
        {
            var (run, failure) = early.TryGetValue(choice.Tool.Id, out var started) ? await started : await AttemptAsync(choice, t => choice.Tool.StartAsync(context, t), wait, ct, ct);
            if (run is null)
            {
                unavailable.Add($"{choice.Tool.Title} ({failure})");
                slow |= failure == Slow(wait);
                continue;
            }
            foreach (var f in run.Functions.OfType<JsonObject>())
            {
                // As in the chat: the first tool with a name has it.
                if (f["function"] is JsonObject fn && fn["name"]?.GetValue<string>() is { Length: > 0 } name && owners.TryAdd(name, choice.Tool.Id))
                {
                    tools.Add(Definition(fn, name, choice.Setting.AskFirst || run.AsksFirst(name)));
                }
            }
            if (!string.IsNullOrWhiteSpace(run.Instructions))
            {
                notes.Add($"## {choice.Tool.Title}\n{run.Instructions.Trim()}");
            }
        }
        var catalog = new McpCatalog(tools, Instructions(user, notes, unavailable), owners);
        // Without a slow one's tools, kept briefly: the next list asks it again.
        cache.Set(CatalogKey(user), catalog, slow ? TimeSpan.FromSeconds(10) : Fresh);
        return catalog;
    }

    private static string Slow(TimeSpan wait) => $"it did not answer within {Mcp.Describe(wait)}";

    /// <summary>A tool started (or its start begun: a sign-in read), or why not. <paramref name="limit"/> is the wait for it; <paramref name="ct"/> the request's.</summary>
    private async Task<(T? Value, string? Failure)> AttemptAsync<T>(ToolChoice choice, Func<CancellationToken, Task<T>> start, TimeSpan wait, CancellationToken limit, CancellationToken ct)
        where T : class
    {
        try
        {
            return (await start(limit), null);
        }
        catch (McpException ex)
        {
            return (null, ex.Message);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return (null, Slow(wait));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStartFailed(logger, choice.Tool.Id, ex);
            return (null, "it failed to start");
        }
    }

    /// <summary>
    /// One call, as the person, audited. Null: no tool of theirs has that function. A tool that
    /// fails or cannot be reached is a result with IsError, as in the chat.
    /// </summary>
    public async Task<ToolResult?> CallAsync(AppUser user, string function, JsonObject arguments, Func<McpProgress, Task>? progress, CancellationToken ct)
    {
        var context = Context(user, progress);
        var (owner, failure) = await OwnerAsync(user, await ServedAsync(user, ct), function, context, ct);
        if (owner is not { } found)
        {
            return failure is null ? null : new ToolResult(failure, IsError: true);
        }
        ToolResult outcome;
        try
        {
            outcome = await found.Run.CallAsync(function, arguments, ct);
        }
        catch (McpException ex)
        {
            outcome = new ToolResult(ex.Message, IsError: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await audit.WriteAsync("mcp.call", Target(found.Choice.Tool), success: false, detail: $"{function}, stopped by the client", actor: user);
            throw;
        }
        catch (Exception ex)
        {
            // A tool's own fault ends that call: the agent is told, and can go on.
            LogCallFailed(logger, function, ex);
            outcome = new ToolResult($"{function} failed: {ex.Message}", IsError: true);
        }
        await audit.WriteAsync("mcp.call", Target(found.Choice.Tool), success: !outcome.IsError, detail: function, actor: user);
        return outcome;
    }

    /// <summary>
    /// The tool with the function, started. As listed when the list is still kept; otherwise an
    /// admin's server or API by its prefix, else the built-in tools, Argus last (it is a network
    /// round trip, they are not).
    /// </summary>
    private async Task<((ToolChoice Choice, IToolRun Run)? Owner, string? Failure)> OwnerAsync(AppUser user, IReadOnlyList<ToolChoice> served, string function,
        ToolContext context, CancellationToken ct)
    {
        IEnumerable<ToolChoice> candidates = cache.TryGetValue(CatalogKey(user), out McpCatalog? kept) && kept?.Owners.TryGetValue(function, out var id) == true
            ? served.Where(t => t.Tool.Id == id)
            :
            [
                .. served.Where(t => t.Tool is IServerTool s && function.StartsWith(s.Slug + "__", StringComparison.Ordinal)),
                .. served.Where(t => t.Tool is not IServerTool && t.Tool.Id != "argus"),
                .. served.Where(t => t.Tool.Id == "argus"),
            ];
        string? failure = null;
        foreach (var choice in candidates)
        {
            IToolRun run;
            try
            {
                run = await choice.Tool.StartAsync(context, ct);
            }
            catch (McpException ex)
            {
                failure = $"{choice.Tool.Title} is not available now: {ex.Message}";
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogStartFailed(logger, choice.Tool.Id, ex);
                failure = $"{choice.Tool.Title} failed to start.";
                continue;
            }
            if (run.Functions.OfType<JsonObject>().Any(f => f["function"]?["name"]?.GetValue<string>() == function))
            {
                return ((choice, run), null);
            }
        }
        return (null, failure);
    }

    /// <summary>
    /// A result as MCP content: its text; the pictures and sound it made inline (up to a size); a
    /// link to each file it made, which resources/read reads and the person opens signed in.
    /// Older clients know neither links nor sound (2025-06-18 and 2025-03-26): theirs are in the text.
    /// </summary>
    public JsonObject Content(ToolResult result, string protocol)
    {
        var text = result.Text;
        var links = string.CompareOrdinal(protocol, "2025-06-18") >= 0;
        var sound = string.CompareOrdinal(protocol, "2025-03-26") >= 0;
        var content = new JsonArray();
        var more = new List<JsonNode>();
        foreach (var f in result.Files ?? [])
        {
            if (f.Data is { Length: <= InlineBytes } bytes && (f.Kind == "image" || (f.Kind == "audio" && sound)))
            {
                more.Add(new JsonObject { ["type"] = f.Kind, ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = f.ContentType });
            }
            if (links)
            {
                more.Add(new JsonObject { ["type"] = "resource_link", ["uri"] = FileUri(f.Id), ["name"] = f.FileName, ["mimeType"] = f.ContentType, ["size"] = f.Size });
            }
            else
            {
                text += $"\n{f.FileName}: {FileUri(f.Id)}";
            }
        }
        content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var item in more)
        {
            content.Add(item);
        }
        return new JsonObject { ["content"] = content, ["isError"] = result.IsError };
    }

    /// <summary>A file of the person's by its link (resources/read), or null when it is not theirs or not there.</summary>
    public async Task<JsonObject?> ReadAsync(AppUser user, string uri, CancellationToken ct)
    {
        if (FileLink().Match(uri) is not { Success: true } m || !Guid.TryParse(m.Groups[1].Value, out var id))
        {
            return null;
        }
        var file = await db.ChatAttachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id && a.UserId == user.Id, ct);
        if (file is null)
        {
            return null;
        }
        var item = new JsonObject { ["uri"] = uri, ["mimeType"] = file.ContentType };
        if (file.Data is { } bytes)
        {
            item["blob"] = Convert.ToBase64String(bytes);
        }
        else
        {
            item["text"] = file.Text;
        }
        return new JsonObject { ["contents"] = new JsonArray(item) };
    }

    /// <summary>Where a file the tools made is: the chat's own address for it (the person opens it there, signed in).</summary>
    public string FileUri(Guid id) => $"{auth.Value.Origin}/api/chat/attachments/{id}/content";

    private static string CatalogKey(AppUser user) => "mcp-catalog:" + user.Id;

    /// <summary>
    /// What a tool needs to know about the call: the person, and where it reports how far it is. A
    /// call here belongs to no chat: a chat of none, never saved (Python finds no files in it).
    /// </summary>
    private static ToolContext Context(AppUser user, Func<McpProgress, Task>? progress) =>
        new(user, user.Email!.ToLowerInvariant(), new Conversation { UserId = user.Id, Title = "Arena MCP" }, progress is null ? null : Reporter(progress));

    /// <summary>The chat's progress reports (an event for the page), passed on as MCP progress.</summary>
    private static ToolProgress Reporter(Func<McpProgress, Task> progress) => new(e =>
    {
        var said = JsonSerializer.SerializeToNode(e);
        return said?["progress"] is JsonValue p && p.TryGetValue<double>(out var done)
            ? progress(new McpProgress(done, said["total"] is JsonValue t && t.TryGetValue<double>(out var all) ? all : null, said["message"]?.GetValue<string>()))
            : Task.CompletedTask;
    })
    { CallId = "mcp" };

    private static JsonObject Definition(JsonObject function, string name, bool asksFirst)
    {
        var schema = function["parameters"]?.DeepClone() as JsonObject ?? [];
        schema["type"] ??= "object";
        var tool = new JsonObject
        {
            ["name"] = name,
            ["description"] = (function["description"]?.GetValue<string>() ?? "") + (asksFirst ? AskFirstNote : ""),
            ["inputSchema"] = schema,
        };
        if (asksFirst)
        {
            tool["annotations"] = new JsonObject { ["readOnlyHint"] = false, ["destructiveHint"] = true };
        }
        return tool;
    }

    /// <summary>In the audit log: the built-in tool's id, or the plugin's or server's name.</summary>
    private static string Target(IChatTool tool) => tool is IServerTool s ? s.Server.Plugin ?? s.Server.Name : tool.Id;

    private string Instructions(AppUser user, List<string> notes, List<string> unavailable)
    {
        var product = string.IsNullOrWhiteSpace(branding.CurrentValue.ProductName) ? AppInfo.Current.Name : branding.CurrentValue.ProductName;
        var text = new StringBuilder(
            $"These are the tools of {product}'s chat, run as {user.DisplayName} ({user.Email}): they reach only what this person may " +
            "(Argus searches only the code they can read in GitLab), what costs is paid from their credit, and every call is audited. " +
            "A tool marked destructiveHint asks the person first in the chat: ask them before each call, as this server cannot. " +
            "Files the tools make come back as links: read one with resources/read, or the person opens it signed in. " +
            "Where a note below says the person sees a file under the answer, here it comes back to you instead: pass it on.");
        if (unavailable.Count > 0)
        {
            text.Append("\n\nNot available now: ").AppendJoin("; ", unavailable).Append('.');
        }
        foreach (var note in notes)
        {
            text.Append("\n\n").Append(note);
        }
        return text.ToString();
    }

    [GeneratedRegex("/api/chat/attachments/([0-9a-fA-F-]{36})/content$")]
    private static partial Regex FileLink();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Arena MCP: the tool {Tool} failed to start")]
    private static partial void LogStartFailed(ILogger logger, string tool, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Arena MCP: the tool function {Function} failed")]
    private static partial void LogCallFailed(ILogger logger, string function, Exception ex);
}

/// <summary>
/// The prompts Arena MCP serves (prompts/list, prompts/get): the prompt library as the person
/// sees it in the chat's / menu (theirs, their groups', the company's, their plugins'), each
/// with its {{variables}} as arguments. One name from two places is theirs first, as in the menu.
/// </summary>
public sealed class McpPrompts(AppDbContext db, AccessService access)
{
    /// <summary>MCP prompts: name, title, description, arguments.</summary>
    public async Task<JsonArray> ListAsync(AppUser user, CancellationToken ct) =>
        [.. (await UsableAsync(user, ct)).Select(p => (JsonNode)new JsonObject
        {
            ["name"] = p.Name,
            ["title"] = p.Title,
            ["description"] = p.Title,
            ["arguments"] = new JsonArray([.. PromptLibrary.Variables(p.Text).Select(v => (JsonNode)new JsonObject { ["name"] = v, ["required"] = true })]),
        })];

    /// <returns>The prompt's description and messages, filled in with the arguments; null when there is no such prompt.</returns>
    public async Task<JsonObject?> GetAsync(AppUser user, string name, JsonObject arguments, CancellationToken ct)
    {
        if ((await UsableAsync(user, ct)).FirstOrDefault(p => p.Name == name) is not { } prompt)
        {
            return null;
        }
        var values = arguments.Where(a => a.Value is JsonValue).ToDictionary(a => a.Key, a => a.Value!.ToString(), StringComparer.Ordinal);
        return new JsonObject
        {
            ["description"] = prompt.Title,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = PromptLibrary.Fill(prompt.Text, values) },
            }),
        };
    }

    /// <summary>Each slash name once: the first, as the library ranks them.</summary>
    private async Task<List<SavedPrompt>> UsableAsync(AppUser user, CancellationToken ct) =>
        [.. (await PromptEndpoints.UsableAsync(user, access, db, ct)).DistinctBy(p => p.Name, StringComparer.Ordinal)];
}
