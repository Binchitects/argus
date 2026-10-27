using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Core.Data;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// Tools as code (run_code): the model writes a short Python program that calls the chat's other tools as functions
/// (arena.argus.find_symbol(...), found with arena.tools.search), loops, filters and joins their results, and only what
/// the program prints (and the files it writes) comes back to the window. It runs in the sandbox (no network); its calls
/// go through the runner's socket to the chat, which runs each as the person, as a direct call would be.
/// </summary>
public sealed partial class CodeTool(SandboxClient sandbox, PythonTool files, AppDbContext db, IOptionsMonitor<SandboxOptions> options) : IChatTool
{
    public const string ToolId = "code";
    public const string Function = "run_code";

    /// <summary>The most calls of the chat's tools one program makes.</summary>
    public const int MostCalls = 100;

    /// <summary>
    /// Whether a program may call this tool's functions: not its own, nor those that hand work on (deep research, sub-agents) or
    /// need the sandbox the program holds a place in (Python, vector, an admin's sandboxed server), nor those whose call acts in
    /// the chat's page (a question to the person ends the answer; a plan's card, a memory's, the canvas, the browser's screen
    /// are shown there). Those that ask first are left out too.
    /// </summary>
    public static bool Callable(IChatTool tool) =>
        tool.Id is not (ToolId or "python" or Lab.LabTool.ToolId or ResearchTool.ToolId or VectorTool.ToolId or "agents" or "ask" or "canvas" or "memory" or Plans.PlansTool.ToolId or Browser.BrowserTool.ToolId)
        && tool is not SandboxServerTool;

    /// <summary>Python's keywords: a name that is one gets a _ after it, as the arena module names it.</summary>
    private static readonly HashSet<string> Keywords =
    [
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except",
        "finally", "for", "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try",
        "while", "with", "yield",
    ];

    /// <summary>A function as a program calls it, arena.&lt;group&gt;.&lt;function&gt;: the names the arena module gives them (arena/__init__.py).</summary>
    public static string PythonName(string group, string function)
    {
        var g = Identifier(group);
        return $"arena.{(g is "tools" or "call" or "ToolError" ? g + "_" : g)}.{Identifier(function)}";
    }

    /// <summary>
    /// What a model given its tools as code reads in place of their definitions: each function a program may call, as Python
    /// calls it, with its parameters (the required ones first) and the first line of what it does.
    /// </summary>
    public static string FunctionsNote(IReadOnlyList<CodeFunction> functions)
    {
        var lines = functions.Select(f =>
        {
            var properties = (f.Parameters?["properties"] as JsonObject)?.Select(p => p.Key).ToList() ?? [];
            var required = (f.Parameters?["required"] as JsonArray)?.Select(r => r?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
            var args = string.Join(", ", properties.OrderBy(p => !required.Contains(p)).Select(p => required.Contains(p) ? p : p + "=…"));
            var about = (f.Description ?? "").Split('\n')[0];
            return $"- {PythonName(f.Group, f.Name)}({args}): {(about.Length > 140 ? about[..140] + "…" : about)}";
        });
        return $"Your tools are functions to call from a {Function} program, not one by one: write the program, call them with keyword arguments, " +
            "and print what you need (help(arena.<group>.<function>) in it tells one's parameters in full). They are:\n" + string.Join('\n', lines);
    }

    private static string Identifier(string name)
    {
        var clean = NotWord().Replace(name, "_").Trim('_');
        clean = clean.Length == 0 ? "tool" : char.IsDigit(clean[0]) ? "_" + clean : clean;
        return Keywords.Contains(clean) ? clean + "_" : clean;
    }

    public string Id => ToolId;
    public string Title => "Tools as code";
    public string Description => "Writes a short Python program that calls the chat's other tools as functions: many calls, filtered and joined there, and only what it prints comes back.";
    public string Icon => "square-code";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult(sandbox.Unavailable());

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [Schema.Function(Function,
            "Runs a Python 3.13 program that calls this chat's tools as functions, and returns what it prints. In it, `import arena`: " +
            "arena.tools.search(\"words\") lists the functions that match (arena.<tool>.<function>), help(arena.argus.find_symbol) tells one's parameters, " +
            "and each call (keyword arguments) returns the tool's result: a dict or a list when it is JSON, else text; a refused call raises arena.ToolError. " +
            $"At most {MostCalls} calls a program, {options.CurrentValue.TimeoutSeconds} seconds a run, no network but those calls. The chat's files are in the working directory; " +
            "files the program writes there are given to the person. Print only what you need: the results stay in the program.",
            new JsonObject { ["code"] = Schema.Text("The whole program; print() what you need to read.") }, "code")],
        "Use run_code when a task needs many tool calls or results you only need to filter: every caller of a symbol and what each does, " +
        "searching several places and joining what they say, reading many files for a number. Write the loop and the filtering in Python and " +
        "print a short answer. For one or two simple calls, call the tool directly. Tools that ask the person first are not callable from code.",
        async (_, args, token) =>
        {
            var code = Schema.Str(args, "code") ?? "";
            if (code.Trim().Length == 0)
            {
                return new ToolResult("Give the program in 'code'.", IsError: true);
            }
            if (context.CodeFunctions is not { } functions || context.CallFromCode is not { } call)
            {
                return new ToolResult("The chat's tools cannot be called from code here.", IsError: true);
            }
            var callable = functions();
            var definitions = new JsonArray([.. callable.Select(f => (JsonNode)new JsonObject
            {
                ["name"] = f.Name, ["group"] = f.Group, ["description"] = f.Description, ["parameters"] = f.Parameters?.DeepClone(),
            })]);
            var inputs = (await files.InputsAsync(context.Conversation.Id, token)).Append(new SandboxFile(".arena-tools.json", Encoding.UTF8.GetBytes(definitions.ToJsonString()))).ToList();
            var made = new List<string>();
            // What the calls made beside their text: their files go to the person with the program's, their cost with its.
            var theirs = new List<Llm.Core.Chat.ChatAttachment>();
            decimal? spent = null;
            var calls = 0;
            SandboxResult result;
            try
            {
                result = await sandbox.RunAsync(code, inputs, options.CurrentValue.TimeoutSeconds, token, async (name, callArgs, inner) =>
                {
                    if (++calls > MostCalls)
                    {
                        return new JsonObject { ["ok"] = false, ["error"] = $"A program makes at most {MostCalls} calls." };
                    }
                    if (callable.All(f => f.Name != name))
                    {
                        return new JsonObject { ["ok"] = false, ["error"] = $"There is no function {name} to call from code (arena.tools.search finds them; tools that ask first are called directly)." };
                    }
                    if (context.Progress is { } progress)
                    {
                        await progress.ReportAsync(new McpProgress(calls, null, $"call {calls}: {name}"));
                    }
                    var outcome = await call(name, callArgs, inner);
                    made.Add(name);
                    theirs.AddRange(outcome.Files ?? []);
                    spent = outcome.Cost is { } cost ? (spent ?? 0) + cost : spent;
                    return outcome.IsError ? new JsonObject { ["ok"] = false, ["error"] = outcome.Text } : new JsonObject { ["ok"] = true, ["result"] = outcome.Text };
                });
            }
            catch (SandboxException ex)
            {
                return new ToolResult(ex.Message, IsError: true);
            }
            var kept = result.Files.Select(f => PythonTool.AsAttachment(context.User.Id, f)).ToList();
            db.ChatAttachments.AddRange(kept);
            await db.SaveChangesAsync(token);
            kept.AddRange(theirs);
            var max = options.CurrentValue.MaxOutputChars;
            var failed = result.Error is not null || result.TimedOut || result.Killed is not null || result.ExitCode is not 0;
            return new ToolResult(new JsonObject
            {
                ["exit_code"] = result.ExitCode,
                ["stdout"] = result.Stdout.Length > max ? result.Stdout[..max] + $"\n[… {result.Stdout.Length - max:N0} more characters not shown]" : result.Stdout,
                ["stderr"] = result.Stderr.Length > 0 ? (result.Stderr.Length > max ? result.Stderr[..max] : result.Stderr) : null,
                ["calls"] = made.Count == 0 ? null : string.Join(", ", made.GroupBy(n => n).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}")),
                ["timed_out"] = result.TimedOut ? $"Stopped after {options.CurrentValue.TimeoutSeconds} seconds." : null,
                ["problem"] = result.Error ?? result.Killed,
                ["files_given_to_the_person"] = kept.Count > 0 ? new JsonArray([.. kept.Select(a => (JsonNode)a.FileName)]) : null,
                ["seconds"] = Math.Round(result.DurationMs / 1000.0, 1),
            }.ToJsonString(Mcp.Plain), failed, kept) { Cost = spent };
        }));

    [GeneratedRegex(@"\W")]
    private static partial Regex NotWord();
}
