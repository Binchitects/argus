using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;

namespace Llm.Api.Chat.Tools;

/// <summary>What a tool knows about the answer it helps with.</summary>
public sealed record ToolContext(AppUser User, string Email, Conversation Conversation);

/// <summary>
/// A tool call's outcome: the text the model reads, whether it failed, and files
/// the tool made for the person (a picture), which the page shows.
/// </summary>
public sealed record ToolResult(string Text, bool IsError = false, IReadOnlyList<ChatAttachment>? Files = null);

/// <summary>A tool made ready for one answer: its functions, and how to run them.</summary>
public interface IToolRun
{
    /// <summary>OpenAI-style function definitions.</summary>
    JsonArray Functions { get; }

    /// <summary>Added to the system prompt while the tool is on.</summary>
    string? Instructions { get; }

    Task<ToolResult> CallAsync(string name, JsonObject arguments, CancellationToken ct);
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

    public static string? Str(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>A tool whose functions are code here, with nothing to open first.</summary>
internal sealed class LocalRun(JsonArray functions, string? instructions, Func<string, JsonObject, CancellationToken, Task<ToolResult>> call) : IToolRun
{
    public JsonArray Functions { get; } = functions;
    public string? Instructions { get; } = instructions;
    public Task<ToolResult> CallAsync(string name, JsonObject arguments, CancellationToken ct) => call(name, arguments, ct);
}

/// <summary>Argus: the organisation's code, searched with the person's own GitLab access.</summary>
public sealed class ArgusTool(ArgusMcp argus) : IChatTool
{
    public string Id => "argus";
    public string Title => "Argus";
    public string Description => "Searches the code you can read in GitLab: symbols, references, files and repositories.";
    public string Icon => "search-code";

    public Task<string?> UnavailableAsync(CancellationToken ct) =>
        Task.FromResult(argus.Enabled ? null : "Argus is not deployed here (the argus profile), or the chat has no token for it.");

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var session = await argus.ConnectAsync(context.Email, ct);
        var functions = Mcp.ToOpenAiTools(await session.ToolsAsync(ct));
        return new LocalRun(functions, session.Instructions, async (name, args, token) =>
        {
            var (text, isError) = await session.CallAsync(name, args, token);
            return new ToolResult(text, isError);
        });
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
public sealed partial class ImageTool(ChatModels models, GatewayChat gateway, AppDbContext db) : IChatTool
{
    public static readonly string[] Sizes = ["1024x1024", "1024x768", "768x1024", "768x768", "512x512"];

    public string Id => "image";
    public string Title => "Image generation";
    public string Description => "Makes pictures from a description, with the image model at the gateway.";
    public string Icon => "image";

    public async Task<string?> UnavailableAsync(CancellationToken ct) =>
        await models.ImageModelAsync(ct) is null ? "The gateway serves no image model. Turn on the image profile (IMAGEGEN_* in .env)." : null;

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var model = await models.ImageModelAsync(ct) ?? throw new McpException("The gateway serves no image model.");
        return new LocalRun(
            [Schema.Function("generate_image", "Makes a picture from a description and shows it to the person. Write the prompt in English, " +
                "concrete and visual: subject, setting, style, lighting, composition.",
                new JsonObject
                {
                    ["prompt"] = Schema.Text("What to draw, in detail"),
                    ["size"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. Sizes.Select(s => (JsonNode)s)]), ["description"] = "width x height; default 1024x1024" },
                }, "prompt")],
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
                byte[] png;
                try
                {
                    png = await gateway.GenerateImageAsync(model.Name, prompt, size, context.Email, token);
                }
                catch (ChatGatewayException ex)
                {
                    return new ToolResult(ex.Message, IsError: true);
                }
                var file = new ChatAttachment
                {
                    UserId = context.User.Id, FileName = FileName(prompt), ContentType = "image/png", Size = png.Length, Kind = "image", Data = png, Text = "",
                };
                db.ChatAttachments.Add(file);
                await db.SaveChangesAsync(token);
                return new ToolResult(new JsonObject
                {
                    ["shown_to_the_person"] = true, ["file"] = file.FileName, ["size"] = size, ["model"] = model.Name,
                }.ToJsonString(Mcp.Plain), Files: [file]);
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
