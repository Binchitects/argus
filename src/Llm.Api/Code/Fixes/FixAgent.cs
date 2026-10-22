using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Core.Identity;

namespace Llm.Api.Code.Fixes;

/// <summary>One thing the agent did: the tool, about what, and what came of it (for the page, as it goes).</summary>
public sealed record FixStep(string Tool, string About, string Result);

/// <summary>What the agent ended with: its account, or why it stopped.</summary>
public sealed record FixOutcome(string? Summary, string? Error, string? TestsCommand, bool? TestsPassed, string? TestsOutput);

/// <summary>
/// The agent of Fix it: the model, as the person, with tools on the workspace (list, read, search, replace, write, delete,
/// run the tests in the sandbox, finish), turn by turn until it finishes or has taken its turns.
/// </summary>
public sealed class FixAgent(GatewayChat gateway, SandboxClient sandbox)
{
    public const int MostTurns = 40;

    private static readonly JsonArray Tools =
    [
        Schema.Function("list_files", "Lists the repository's files (at most 300), under a folder, or with some words in their path.",
            new JsonObject { ["under"] = Schema.Text("A folder, e.g. src/api (optional)."), ["containing"] = Schema.Text("Words the path holds, e.g. test (optional).") }),
        Schema.Function("read_file", "Reads a file's lines, numbered (at most 400 at a time).",
            new JsonObject { ["path"] = Schema.Text("The file's path."), ["from"] = new JsonObject { ["type"] = "integer" }, ["to"] = new JsonObject { ["type"] = "integer" } }, "path"),
        Schema.Function("search", "Finds the lines that hold some text, in every file (or under a folder).",
            new JsonObject { ["text"] = Schema.Text("The exact text to find."), ["under"] = Schema.Text("A folder (optional).") }, "text"),
        Schema.Function("replace", "Changes a file: the exact text 'old' (it must be there once: give lines around it to make it so) becomes 'new'.",
            new JsonObject { ["path"] = Schema.Text("The file's path."), ["old"] = Schema.Text("The text as it is now."), ["new"] = Schema.Text("What it becomes.") }, "path", "old", "new"),
        Schema.Function("write_file", "Writes a whole file: a new one, or one to rewrite entirely.",
            new JsonObject { ["path"] = Schema.Text("The file's path."), ["content"] = Schema.Text("All of its text.") }, "path", "content"),
        Schema.Function("delete_file", "Deletes a file.", new JsonObject { ["path"] = Schema.Text("The file's path.") }, "path"),
        Schema.Function("run_tests", "Runs a command in the repository as changed, in a sandbox with Python 3.13 and no network (python -m unittest, python script.py); its output and exit code.",
            new JsonObject { ["command"] = Schema.Text("The command, e.g. python -m unittest discover -s tests") }, "command"),
        Schema.Function("finish", "Ends the work: what was changed and why, and what the tests said.", new JsonObject { ["summary"] = Schema.Text("The account, in Markdown.") }, "summary"),
    ];

    private const string Instructions = """
        You fix code in a repository checked out for you, with the tools given. Work like a careful engineer:
        - Find the code first (list_files, search, read_file); read before you change.
        - Make the smallest change that does what is asked; keep the code's style. Do not change what is not asked.
        - Change files with replace (the exact text, there once) or write_file. Never invent files you have not read unless you make new ones.
        - When the project can be tested with Python here, run its tests (run_tests) after the change and fix what fails. Say so when they cannot run here.
        - When done, call finish with an account: what you changed, why, and what the tests said.
        """;

    /// <summary>The work, turn by turn; each step told as it is taken.</summary>
    public async Task<FixOutcome> RunAsync(AppUser me, string model, string task, Workspace w, Func<FixStep, Task> told, CancellationToken ct)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = Instructions },
            new JsonObject { ["role"] = "user", ["content"] = task },
        };
        string? command = null, output = null;
        bool? passed = null;
        var nudged = 0;
        for (var turn = 0; turn < MostTurns; turn++)
        {
            var request = new JsonObject
            {
                ["model"] = model, ["messages"] = messages.DeepClone(), ["tools"] = Tools.DeepClone(), ["tool_choice"] = "auto", ["stream"] = true, ["max_tokens"] = 4000,
                ["user"] = me.Email, ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false }, ["metadata"] = new JsonObject { ["tags"] = new JsonArray("fix") },
            };
            var content = new StringBuilder();
            var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
            try
            {
                await foreach (var e in gateway.StreamAsync(request, me.Email!, ct))
                {
                    if (e is ContentDelta c)
                    {
                        content.Append(c.Text);
                    }
                    else if (e is ToolCallDelta t)
                    {
                        var call = calls.TryGetValue(t.Index, out var was) ? was : (null, null, new StringBuilder());
                        call = (t.Id ?? call.Id, t.Name ?? call.Name, call.Args.Append(t.Arguments));
                        calls[t.Index] = call;
                    }
                }
            }
            catch (ChatGatewayException ex)
            {
                return new FixOutcome(null, $"The model could not answer: {ex.Message}", command, passed, output);
            }
            if (calls.Count == 0)
            {
                // Words without a tool: the end when something changed, else a nudge to use the tools.
                if (w.Changes.Count > 0 && content.Length > 0)
                {
                    return new FixOutcome(content.ToString().Trim(), null, command, passed, output);
                }
                if (++nudged > 2)
                {
                    return new FixOutcome(null, content.Length > 0 ? $"It stopped without a change: {Cut(content.ToString(), 500)}" : "It stopped without a change.", command, passed, output);
                }
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.ToString() });
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Use the tools to make the change, then call finish." });
                continue;
            }
            var asked = new JsonArray();
            foreach (var (index, call) in calls)
            {
                asked.Add(new JsonObject
                {
                    ["id"] = call.Id ?? $"call_{turn}_{index}", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Args.ToString() },
                });
            }
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.Length > 0 ? content.ToString() : null, ["tool_calls"] = asked });
            foreach (var (index, call) in calls)
            {
                JsonObject args;
                try
                {
                    args = JsonNode.Parse(call.Args.Length > 0 ? call.Args.ToString() : "{}") as JsonObject ?? [];
                }
                catch (JsonException)
                {
                    args = [];
                }
                if (call.Name == "finish")
                {
                    var summary = Schema.Str(args, "summary") ?? content.ToString();
                    await told(new FixStep("finish", "", Cut(summary, 300)));
                    return w.Changes.Count == 0
                        ? new FixOutcome(summary, "It finished without changing anything.", command, passed, output)
                        : new FixOutcome(summary, null, command, passed, output);
                }
                var (about, result) = await CallAsync(call.Name ?? "", args, w, ct);
                if (call.Name == "run_tests")
                {
                    command = Schema.Str(args, "command");
                    output = result;
                    passed = result.Contains("Exit code 0.", StringComparison.Ordinal);
                }
                await told(new FixStep(call.Name ?? "", about, Cut(result, 300)));
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id ?? $"call_{turn}_{index}", ["content"] = Cut(result, 16_000) });
            }
        }
        return new FixOutcome(w.Changes.Count > 0 ? "It took all its turns; what it changed is kept." : null,
            w.Changes.Count > 0 ? null : $"It took {MostTurns} turns without a change.", command, passed, output);
    }

    private static string Cut(string s, int most) => s.Length > most ? s[..most] + "…" : s;

    /// <summary>One tool call on the workspace: what it was about, and its result (an error in words, for the model).</summary>
    public async Task<(string About, string Result)> CallAsync(string name, JsonObject args, Workspace w, CancellationToken ct)
    {
        var path = Schema.Str(args, "path") is { } p ? Workspace.Clean(p) : "";
        try
        {
            switch (name)
            {
                case "list_files":
                {
                    var under = Schema.Str(args, "under") is { Length: > 0 } u ? Workspace.Clean(u) : null;
                    var containing = Schema.Str(args, "containing");
                    var found = w.Paths.Where(x => (under is null || x.StartsWith(under, StringComparison.Ordinal)) && (containing is null || x.Contains(containing, StringComparison.OrdinalIgnoreCase))).ToList();
                    return (under ?? containing ?? "all", found.Count == 0 ? "No files." : string.Join('\n', found.Take(300)) + (found.Count > 300 ? $"\n({found.Count - 300} more)" : ""));
                }
                case "read_file":
                {
                    var lines = w.Read(path).Split('\n');
                    var from = Math.Max(1, Number(args, "from") ?? 1);
                    var to = Math.Min(lines.Length, Math.Min(Number(args, "to") ?? from + 399, from + 399));
                    var text = new StringBuilder();
                    for (var i = from; i <= to; i++)
                    {
                        text.Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append(' ').AppendLine(lines[i - 1]);
                    }
                    return ($"{path} {from}–{to}", to < lines.Length ? $"{text}(lines {to + 1}–{lines.Length} not shown)" : text.ToString());
                }
                case "search":
                {
                    var text = Schema.Str(args, "text") ?? "";
                    if (text.Length == 0)
                    {
                        return ("", "Say what text to find.");
                    }
                    var found = w.Search(text, Schema.Str(args, "under"));
                    return (text, found.Count == 0 ? "Not found." : string.Join('\n', found));
                }
                case "replace":
                {
                    var old = Schema.Str(args, "old") ?? "";
                    var now = w.Read(path);
                    var count = old.Length == 0 ? 0 : CountOf(now, old);
                    if (count != 1)
                    {
                        return (path, count == 0 ? "That text is not in the file as given: read the file and give it exactly." : $"That text is there {count} times: give lines around it so it is there once.");
                    }
                    w.Write(path, now.Replace(old, Schema.Str(args, "new") ?? "", StringComparison.Ordinal));
                    return (path, "Changed.");
                }
                case "write_file":
                {
                    var existed = w.Exists(path);
                    w.Write(path, Schema.Str(args, "content") ?? "");
                    return (path, existed ? "Written." : "Made.");
                }
                case "delete_file":
                    w.Delete(path);
                    return (path, "Deleted.");
                case "run_tests":
                    return (Schema.Str(args, "command") ?? "", await TestAsync(Schema.Str(args, "command") ?? "", w, ct));
                default:
                    return (name, $"There is no tool {name}.");
            }
        }
        catch (WorkspaceException ex)
        {
            return (path, ex.Message);
        }
    }

    private static int CountOf(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    private static int? Number(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    /// <summary>The command, in the repository as changed, in the Python sandbox (no network): its output and exit code.</summary>
    private async Task<string> TestAsync(string command, Workspace w, CancellationToken ct)
    {
        if (command.Trim().Length == 0)
        {
            return "Say what command runs the tests.";
        }
        if (sandbox.Unavailable() is { } why)
        {
            return $"Tests cannot run here: {why}";
        }
        var code = $$"""
            import subprocess, sys, tarfile
            with tarfile.open("workspace.tar") as t:
                t.extractall("repo", filter="data")
            try:
                r = subprocess.run({{JsonSerializer.Serialize(command)}}, shell=True, cwd="repo", capture_output=True, text=True, timeout=240)
                print(r.stdout[-8000:])
                print(r.stderr[-6000:], file=sys.stderr)
                print(f"Exit code {r.returncode}.")
            except subprocess.TimeoutExpired:
                print("It ran over four minutes, and was stopped.")
            """;
        try
        {
            var result = await sandbox.RunAsync(code, [new SandboxFile("workspace.tar", w.Tar())], 300, ct);
            var text = $"{result.Stdout}\n{result.Stderr}".Trim();
            return result.Error is { } error ? $"The sandbox could not run it: {error}" : text.Length > 0 ? text : "No output.";
        }
        catch (SandboxException ex)
        {
            return $"The sandbox could not run it: {ex.Message}";
        }
    }
}
