using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat.Tools;

/// <summary>What a tool knows about the answer it helps with, and where a long call reports how far it is.</summary>
public sealed record ToolContext(AppUser User, string Email, Conversation Conversation, ToolProgress? Progress = null)
{
    /// <summary>Runs parts of a task by sub-agents with the answer's model and tools (the Sub-agents tool).</summary>
    public Func<IReadOnlyList<AgentTask>, CancellationToken, Task<ToolResult>>? Agents { get; init; }

    /// <summary>Runs deep research on a question and returns its report (the Deep research tool, at Arena MCP; in a chat the answer itself becomes the research).</summary>
    public Func<string, CancellationToken, Task<ToolResult>>? Research { get; init; }

    /// <summary>Why deep research cannot run here; the tool is then not offered, and this says why.</summary>
    public string? NoResearch { get; init; }

    /// <summary>The functions of this answer's other tools a program may call (run_code): those that ask first are not among them.</summary>
    public Func<IReadOnlyList<CodeFunction>>? CodeFunctions { get; init; }

    /// <summary>Calls one of them for a program (run_code), as the person: the safeguards on what it sends and brings back, the audit.</summary>
    public Func<string, JsonObject, CancellationToken, Task<ToolResult>>? CallFromCode { get; init; }
}

/// <summary>A function a program may call (run_code): its name, the tool it is of (its group in Python), what it does, its parameters.</summary>
public sealed record CodeFunction(string Name, string Group, string? Description, JsonObject? Parameters);

/// <summary>
/// Where a long tool call says how far it is (MCP progress notifications): the chat
/// shows it beside the call. An answer runs its calls one at a time, so this reports
/// for the call running now. At most one report a second, unless its words change.
/// </summary>
public sealed class ToolProgress(Func<object, Task> emit)
{
    private DateTimeOffset _last;
    private string? _said;

    /// <summary>The call running now (set by the chat before each call).</summary>
    public string? CallId { get; set; }

    public Task ReportAsync(McpProgress p)
    {
        var now = DateTimeOffset.UtcNow;
        if (CallId is not { } id || (now - _last < TimeSpan.FromSeconds(1) && p.Message == _said))
        {
            return Task.CompletedTask;
        }
        (_last, _said) = (now, p.Message);
        return emit(new { type = "tool_progress", id, progress = p.Progress, total = p.Total, message = p.Message });
    }
}

/// <summary>
/// A tool call's outcome: the text the model reads, whether it failed, and files
/// the tool made for the person (a picture), which the page shows. EndsAnswer: the
/// answer stops after this call (questions for the person: their reply comes next).
/// </summary>
public sealed record ToolResult(string Text, bool IsError = false, IReadOnlyList<ChatAttachment>? Files = null, bool EndsAnswer = false)
{
    /// <summary>What the page shows beyond the text the model reads (sub-agents' work), kept with the call.</summary>
    public System.Text.Json.Nodes.JsonNode? Details { get; init; }

    /// <summary>What the call spent, in dollars: the pictures, video or speech it made, its sub-agents' tokens. Null: nothing.</summary>
    public decimal? Cost { get; init; }

    /// <summary>Its pictures go to a model that sees, after the step's results (the browser's screen): it reads them as the person sees them.</summary>
    public bool SeeImages { get; init; }

    /// <summary>A delegate call: its sub-agents' tokens together (their model is in <see cref="Details"/>).</summary>
    public UsageReport? Usage { get; init; }
}

/// <summary>A tool made ready for one answer: its functions, and how to run them.</summary>
public interface IToolRun
{
    /// <summary>OpenAI-style function definitions.</summary>
    JsonArray Functions { get; }

    /// <summary>Added to the system prompt while the tool is on.</summary>
    string? Instructions { get; }

    Task<ToolResult> CallAsync(string name, JsonObject arguments, CancellationToken ct);

    /// <summary>Whether a call of this function always waits for the person to allow it (an API's writes), whatever the tool's setting.</summary>
    bool AsksFirst(string name) => false;
}

/// <summary>A tool the chat can use: built in, or an MCP server an admin added.</summary>
public interface IChatTool
{
    /// <summary>"argus", "image", "calculator", "time", or "mcp:{id}".</summary>
    string Id { get; }
    string Title { get; }
    string Description { get; }
    /// <summary>A hint for the page's icon: search-code, image, calculator, clock, plug.</summary>
    string Icon { get; }

    /// <summary>Why it cannot be used at the moment, or null when it can.</summary>
    Task<string?> UnavailableAsync(CancellationToken ct);

    /// <summary>Ready for one answer. Throws <see cref="McpException"/> when its server cannot be reached.</summary>
    Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct);

    /// <summary>How it is set until an admin changes it: on, for everyone, in new chats.</summary>
    ToolSetting Defaults() => new() { ToolId = Id };
}

/// <summary>Builds the JSON schema for a function's parameters.</summary>
internal static class Schema
{
    public static JsonObject Function(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray([.. required.Select(r => (JsonNode)r)]),
            },
        },
    };

    public static JsonObject Text(string description) => new() { ["type"] = "string", ["description"] = description };

    /// <summary>A string that is one of these.</summary>
    public static JsonObject Choice(string description, IEnumerable<string> values) =>
        new() { ["type"] = "string", ["enum"] = new JsonArray([.. values.Select(v => (JsonNode)v)]), ["description"] = description };

    public static string? Str(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>A tool whose functions are code here, with nothing to open first.</summary>
internal sealed class LocalRun(JsonArray functions, string? instructions, Func<string, JsonObject, CancellationToken, Task<ToolResult>> call) : IToolRun
{
    public JsonArray Functions { get; } = functions;
    public string? Instructions { get; } = instructions;
    public Func<string, bool>? AsksFirst { get; init; }
    public Task<ToolResult> CallAsync(string name, JsonObject arguments, CancellationToken ct) => call(name, arguments, ct);
    bool IToolRun.AsksFirst(string name) => AsksFirst?.Invoke(name) == true;
}

/// <summary>
/// Argus: the organisation's code, searched with the person's own GitLab access, and the business behind it. With the
/// business map (Code Studio → Business map), business_map lists the systems the person may read and what each does for
/// the business, and business_explain finds the capabilities, entities, rules, processes and terms that answer a
/// question, with their sources, for the model to confirm in the code, the issues and the wiki before it answers as the
/// company's senior technical expert.
/// </summary>
/// <remarks>
/// It reads the database with a context of its own: it starts beside other tools (a plugin's sign-in reads the request's
/// context meanwhile), and one context takes one operation at a time.
/// </remarks>
public sealed class ArgusTool(ArgusMcp argus, IServiceScopeFactory scopes, Code.Business.BusinessMap map) : IChatTool
{
    public const string MapFunction = "business_map", ExplainFunction = "business_explain";

    /// <summary>The most items business_explain gives.</summary>
    public const int MostFound = 12;

    public string Id => "argus";
    public string Title => "Argus";
    public string Description => "Knows the code you can read in GitLab and the business behind it: symbols, references, files, repositories, issues, the wiki, and what each system does for the business.";
    public string Icon => "search-code";

    /// <summary>How Argus answers about the business, in every answer it is on for.</summary>
    public const string Expert =
        "You are also the company's technical mind: a senior engineer who knows the code and the business it serves. When a question is about the business " +
        "(what happens when a customer does something, why a rule is the way it is, who uses a system, what a word means here), start with business_explain " +
        "(and business_map for the systems), then confirm it in the code (semantic_search, find_symbol, get_file) and in the issues and the wiki (issue_list, " +
        "issue_get, wiki_get) before you answer. Answer in business words first, then how the code does it, citing the files and lines, issues and wiki pages " +
        "you read. Say plainly what you could not confirm.";

    public Task<string?> UnavailableAsync(CancellationToken ct) =>
        Task.FromResult(argus.Enabled ? null : "Argus is not deployed here (the argus profile), or the chat has no token for it.");

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var session = await argus.ConnectAsync(context.User.Id, context.Email, ct);
        var functions = Mcp.ToOpenAiTools(await session.ToolsAsync(ct));
        bool mapped;
        await using (var scope = scopes.CreateAsyncScope())
        {
            mapped = await scope.ServiceProvider.GetRequiredService<AppDbContext>().BusinessSystems.AnyAsync(b => b.Status == Core.Code.BusinessSystem.Ready, ct);
        }
        if (mapped)
        {
            functions.Add(Schema.Function(MapFunction,
                "The business map: each system you may read, what it does for the business (its domain and summary) and what it lets the business do.",
                new JsonObject()));
            functions.Add(Schema.Function(ExplainFunction,
                "Finds what the business map knows about a question: the capabilities, entities, rules, processes, integrations and terms that answer it, " +
                "each with its system and the sources it was read from (files and lines, documents, issues, merge requests, wiki pages). Confirm them in the code before you answer.",
                new JsonObject { ["question"] = Schema.Text("The question, in the person's words.") }, "question"));
        }
        var instructions = string.Join("\n\n", new[] { session.Instructions, mapped ? Expert : null }.Where(x => x is { Length: > 0 }));
        return new LocalRun(functions, instructions, async (name, args, token) =>
        {
            if (name == MapFunction)
            {
                return await BusinessMapAsync(context.User, token);
            }
            if (name == ExplainFunction)
            {
                return await ExplainAsync(context.User, Schema.Str(args, "question") ?? "", token);
            }
            var (text, isError) = await session.CallAsync(name, args, context.Progress is { } p ? p.ReportAsync : null, token);
            return new ToolResult(text, isError);
        });
    }

    private async Task<List<Core.Code.BusinessSystem>> ReadyAsync(AppUser me, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return [.. (await map.VisibleAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), argus, me, ct)).Where(b => b.Status == Core.Code.BusinessSystem.Ready)];
    }

    private async Task<ToolResult> BusinessMapAsync(AppUser me, CancellationToken ct)
    {
        var systems = await ReadyAsync(me, ct);
        if (systems.Count == 0)
        {
            return new ToolResult("No system you may read is on the business map yet (Code Studio → Business map maps one).");
        }
        return new ToolResult(new JsonArray([.. systems.Select(b => (JsonNode)new JsonObject
        {
            ["system"] = b.Name, ["project"] = b.Project, ["domain"] = b.Domain, ["summary"] = b.Summary,
            ["capabilities"] = new JsonArray([.. Code.Business.BusinessProfile.Read(b.Profile).Capabilities.Select(c => (JsonNode)c.Name)]),
        })]).ToJsonString());
    }

    private async Task<ToolResult> ExplainAsync(AppUser me, string question, CancellationToken ct)
    {
        if (question.Trim().Length == 0)
        {
            return new ToolResult("Give the question.", IsError: true);
        }
        var found = (await ReadyAsync(me, ct))
            .SelectMany(b => Code.Business.BusinessProfile.Read(b.Profile).Matches(question).Select(m => (System: b, m.Part, m.Title, m.Text, m.Sources, m.Score)))
            .OrderByDescending(m => m.Score).Take(MostFound).ToList();
        if (found.Count == 0)
        {
            return new ToolResult("The business map has nothing on that: look in the code (semantic_search, search_code), the issues (issue_list) and the wiki (wiki_list) instead.");
        }
        return new ToolResult(new JsonArray([.. found.Select(f => (JsonNode)new JsonObject
        {
            ["system"] = f.System.Name, ["project"] = f.System.Project, ["kind"] = f.Part, ["title"] = f.Title, ["text"] = f.Text,
            ["sources"] = new JsonArray([.. f.Sources.Select(s => (JsonNode)$"{s.Kind} {s.Ref}")]),
        })]).ToJsonString());
    }
}

/// <summary>Arithmetic the model would otherwise guess at.</summary>
public sealed class CalculatorTool : IChatTool
{
    public string Id => "calculator";
    public string Title => "Calculator";
    public string Description => "Exact arithmetic: sums, percentages, powers, roots, logarithms and trigonometry.";
    public string Icon => "calculator";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [Schema.Function("calculate", "Evaluates an arithmetic expression exactly. Use it for any calculation instead of working it out yourself. " +
            "Operators: + - * / % ^ and brackets; functions: sqrt, abs, round(x[, digits]), floor, ceil, min, max, ln, log(x[, base]), log2, exp, sin, cos, tan, asin, acos, atan (radians); constants: pi, e.",
            new JsonObject { ["expression"] = Schema.Text("For example: (1250 * 0.18) + sqrt(2) ^ 3") }, "expression")],
        null,
        (_, args, _) =>
        {
            var expression = Schema.Str(args, "expression") ?? "";
            try
            {
                var value = Calculator.Evaluate(expression);
                return Task.FromResult(new ToolResult(new JsonObject { ["expression"] = expression, ["result"] = Calculator.Format(value) }.ToJsonString(Mcp.Plain)));
            }
            catch (CalculatorException ex)
            {
                return Task.FromResult(new ToolResult(ex.Message, IsError: true));
            }
        }));
}

/// <summary>The current date and time anywhere, and the days between dates.</summary>
public sealed class TimeTool(TimeProvider clock) : IChatTool
{
    public string Id => "time";
    public string Title => "Date and time";
    public string Description => "The current date and time in any time zone, and the days between two dates.";
    public string Icon => "clock";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            Schema.Function("current_time", "The current date and time, in a time zone (IANA name, e.g. Europe/Berlin; default UTC).",
                new JsonObject { ["time_zone"] = Schema.Text("An IANA time zone, e.g. Asia/Tehran or America/New_York") }),
            Schema.Function("days_between", "Calendar days, weeks and working days (Monday to Friday) between two dates (YYYY-MM-DD).",
                new JsonObject { ["start"] = Schema.Text("YYYY-MM-DD"), ["end"] = Schema.Text("YYYY-MM-DD") }, "start", "end"),
        ],
        null,
        (function, args, _) => Task.FromResult(function switch
        {
            "current_time" => Now(Schema.Str(args, "time_zone")),
            "days_between" => Between(Schema.Str(args, "start"), Schema.Str(args, "end")),
            _ => new ToolResult($"There is no function {function}.", IsError: true),
        })));

    private ToolResult Now(string? zone)
    {
        TimeZoneInfo tz;
        try
        {
            tz = string.IsNullOrWhiteSpace(zone) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(zone.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return new ToolResult($"Unknown time zone '{zone}'. Use an IANA name such as Europe/London.", IsError: true);
        }
        var now = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz);
        return new ToolResult(new JsonObject
        {
            ["time_zone"] = tz.Id,
            ["local"] = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ["weekday"] = now.DayOfWeek.ToString(),
            ["utc"] = clock.GetUtcNow().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        }.ToJsonString(Mcp.Plain));
    }

    private static ToolResult Between(string? start, string? end)
    {
        if (!DateOnly.TryParseExact(start, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var a) ||
            !DateOnly.TryParseExact(end, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var b))
        {
            return new ToolResult("Both dates must be YYYY-MM-DD.", IsError: true);
        }
        var days = b.DayNumber - a.DayNumber;
        var (from, to) = days >= 0 ? (a, b) : (b, a);
        var working = 0;
        for (var d = from; d < to; d = d.AddDays(1))
        {
            working += d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0 : 1;
        }
        return new ToolResult(new JsonObject
        {
            ["days"] = days,
            ["weeks"] = Math.Round(days / 7.0, 2),
            ["working_days"] = days >= 0 ? working : -working,
        }.ToJsonString(Mcp.Plain));
    }
}

/// <summary>Pictures from the gateway's image model, kept as the person's files.</summary>
public sealed partial class ImageTool(ChatModels models, GatewayChat gateway, AppDbContext db, Safeguards.Safeguards safeguards, Models.MediaControl media,
    Gateway.PriceBook prices, Storage.StorageQuotas quotas, Gateway.Credit credit, Models.ModelPolicy policy, Operations.Modules modules, Models.GpuShare gpu) : IChatTool
{
    public static readonly string[] Sizes = ["1024x1024", "1024x768", "768x1024", "768x768", "512x512"];

    public string Id => "image";
    public string Title => "Image generation";
    public string Description => "Makes pictures from a description, with the image model at the gateway.";
    public string Icon => "image";

    public async Task<string?> UnavailableAsync(CancellationToken ct) =>
        (await OursAsync(ct)).Count == 0 && await models.ImageModelAsync(ct) is null
            ? "There is no picture model: add one under Admin -> Models (the imagegen module runs it)." : null;

    /// <summary>This stack's picture models, while its picture server runs (the imagegen module).</summary>
    private async Task<List<string>> OursAsync(CancellationToken ct) =>
        await modules.HasAsync("imagegen", ct) ? [.. media.All.Where(m => m.Mode == Llm.Core.Models.MediaModes.Image).Select(m => m.Name)] : [];

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        // This stack's picture models the person may use (the models' access rules), the chat's own first; else the gateway's.
        var ours = await OursAsync(ct);
        var allowed = ours.Count > 0 ? await policy.AllowedAsync(context.User, ours, ct) : [];
        var chosen = media.Pick(Llm.Core.Models.MediaModes.Image, null, allowed)?.Name
            ?? (ours.Count == 0 ? (await models.ImageModelAsync(ct))?.Name : null)
            ?? throw new McpException("There is no picture model you may use (Admin -> Models).");
        var choices = ours.Where(allowed.Contains).ToList();
        var parameters = new JsonObject
        {
            ["prompt"] = Schema.Text("What to draw, in detail"),
            ["size"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Sizes.Select(s => (JsonNode)s)]), ["description"] = "width x height; default 1024x1024" },
        };
        if (choices.Count > 1)
        {
            parameters["model"] = new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray([.. choices.Select(c => (JsonNode)c)]),
                ["description"] = $"the picture model, when the person names one; default {chosen}",
            };
        }
        return new LocalRun(
            [Schema.Function("generate_image", "Makes a picture from a description and shows it to the person. Write the prompt in English, " +
                "concrete and visual: subject, setting, style, lighting, composition.", parameters, "prompt")],
            "When the person asks for a picture, call generate_image. The picture is shown to them under your answer: " +
            "do not repeat it, link it or write its data; say in a sentence what you made.",
            async (_, args, token) =>
            {
                var prompt = (Schema.Str(args, "prompt") ?? "").Trim();
                if (prompt.Length == 0)
                {
                    return new ToolResult("Say what to draw in 'prompt'.", IsError: true);
                }
                var size = Schema.Str(args, "size") is { } s && Sizes.Contains(s) ? s : Sizes[0];
                var model = new { Name = Schema.Str(args, "model") is { } named && choices.Contains(named) ? named : chosen };
                if (await quotas.ToolRefusalAsync(context.User.Id, "picture", token) is { } full)
                {
                    return new ToolResult(full, IsError: true);
                }
                if (await credit.RefusalAsync(context.User, Llm.Core.Access.CreditKind.Pictures, token) is { } spent)
                {
                    return new ToolResult($"No picture was made. Tell the person: {spent}", IsError: true);
                }
                if (await safeguards.TakeImageAsync(context.User.Id, token) is { } limit)
                {
                    return new ToolResult(limit, IsError: true);
                }
                // Its turn on the GPU (one picture or video at a time), with room made for it when it is short; loaded when it is
                // not (a model not kept loaded unloads when unused).
                await using var hold = media.Find(model.Name) is { } local ? await gpu.HoldAsync(local, token) : null;
                if (hold is not null && await media.ReadyAsync(model.Name, token) is { } notReady)
                {
                    return new ToolResult(notReady, IsError: true);
                }
                byte[] png;
                try
                {
                    png = await gateway.GenerateImageAsync(model.Name, prompt, size, context.Email, token);
                    media.Touch(model.Name);
                }
                catch (ChatGatewayException ex)
                {
                    return new ToolResult(ex.Message, IsError: true);
                }
                var file = new ChatAttachment
                {
                    UserId = context.User.Id, FileName = FileName(prompt), ContentType = "image/png", Size = png.Length, Kind = "image", Data = png, Text = "", Origin = "picture",
                };
                db.ChatAttachments.Add(file);
                await db.SaveChangesAsync(token);
                return new ToolResult(new JsonObject
                {
                    ["shown_to_the_person"] = true, ["file"] = file.FileName, ["size"] = size, ["model"] = model.Name,
                }.ToJsonString(Mcp.Plain), Files: [file]) { Cost = prices.Images(1) };
            });
    }

    /// <summary>"a red fox in the snow" -> "a-red-fox-in-the-snow.png".</summary>
    public static string FileName(string prompt)
    {
        var words = NotWord().Replace(prompt.ToLowerInvariant(), "-").Trim('-');
        var name = words.Length > 48 ? words[..48].TrimEnd('-') : words;
        return (name.Length == 0 ? "image" : name) + ".png";
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotWord();
}
