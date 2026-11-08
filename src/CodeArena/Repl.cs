using System.Text;
using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace CodeArena;

/// <summary>The interactive session: the prompt, turns, and the slash commands.</summary>
internal sealed partial class Repl(Runtime rt)
{
    /// <summary>
    /// The prompt edited key by key, with what was sent before on ↑, when someone types at a terminal that can
    /// take it; lines as they come otherwise (a pipe, a dumb terminal).
    /// </summary>
    private readonly LineEditor? _editor = rt.Env.Keys is { } keys && rt.Ui.CanAsk && !rt.Ui.Quiet && rt.Env.Env("TERM") != "dumb"
        ? new LineEditor(keys, rt.Ui.Out, () => rt.History.Entries()) { Interrupted = rt.Env.Cancel.Press }
        : null;

    // What the person typed while a turn ran (whole lines, and a line begun): a line is taken in at the turn's next step (a
    // command, when the turn ends); the line begun waits for the prompt.
    private readonly Queue<string> _typed = new();
    private string _ahead = "";
    private int _typedIds;
    // The running turn's token: a question waiting for its answer ends when the turn is stopped.
    private CancellationToken? _turnStop;
    // Free only while the prompt waits for the person: the web chat's news is taken in then, and never into a turn or a command.
    private readonly SemaphoreSlim _atPrompt = new(0, 1);

    private Ui Ui => rt.Ui;

    public static readonly (string Name, string What)[] Commands =
    [
        ("/help", "these commands"),
        ("/model [name]", "show or switch the model"),
        ("/providers [add NAME [URL] | remove NAME]", "models from other providers with your own key: Anthropic, OpenAI, DeepSeek, OpenRouter, Mistral, any OpenAI-compatible API"),
        ("/mode [mode]", "show or switch the mode: ask, auto-edit, plan, yolo"),
        ("/thinking [level]", "show or set thinking: default, off, low, medium, high, xhigh (Tab while it thinks skips it for that turn)"),
        ("/tools [on|off NAME]", "the tools this session has; turn a group of Arena's or a server's tools on or off in it"),
        ("/tools set NAME off|on|ask|allow|default [--here]", "keep a tool's setting: off, asking before each call, never asking; for every project, or this one"),
        ("/tools enable NAME", "enable the project's own tool or MCP server (its tools/*.json, .mcp.json) here, as it is defined now"),
        ("/plan", "the plan, as it stands; a proposed one: approve [ask], change N what, another why, reject why; undo N; resume"),
        ("/checklist", "the checklist on, and yours (kept in Arena); N or NAME turns one on; off; tick N, untick N"),
        ("/assistant [N|NAME|off]", "your assistants in Arena; N or NAME works as one (its instructions, model, tools and skills); off as none"),
        ("/tasks [N|stop N|new TASK]", "the sub-agents and background tasks: N shows one, stop N stops it, new [--edits] [--assistant NAME] TASK starts one"),
        ("/goal [TEXT|pause|resume|cancel|next|drop N]", "work until a goal is met, then the next: TEXT [--check COMMAND | --until CONDITION] [--steps N] [--minutes N] [--tokens N] [--until-met]"),
        ("/loop [EVERY [--edits] PROMPT|stop N]", "a prompt run as a background agent every 30m, 2h or 1d while the session is open"),
        ("/agent [NAME TASK]", "your own coding agents (Claude Code, Codex, Gemini CLI: your subscriptions), or hand one a task"),
        ("/review [BASE]", "review the changes not yet committed, or this branch's over BASE (main...): findings by file and line, and a verdict"),
        ("/compact", "summarize the conversation to free the model's window"),
        ("/compact-at [% [%]]", "show or set when it compacts itself (80% of the window, off, or your Arena account's) and what it keeps (25%)"),
        ("/context", "what fills the model's window, piece by piece"),
        ("/cost", "tokens spent, and how full the window is"),
        ("/mcp [retry]", "Arena's, Argus's and your MCP servers: connected or not; retry tries now"),
        ("/jobs [stop N]", "the commands run with no time limit or in the background; stop N stops job N"),
        ("/clear", "start a new session (this one stays saved)"),
        ("/resume [id]", "switch to a saved session"),
        ("/web [N|all|WORDS]", "your chats in Arena (all, or those with WORDS in the title); /web N continues one here, kept in step both ways"),
        ("/memory [add|forget]", "what it remembers; add TEXT (this project), add --all TEXT (every project), forget N"),
        ("/remember", "the project's long memory (.remember/MEMORY.md, in the repository): what every session reads at its start"),
        ("/rewind [N] [code|chat]", "your questions; /rewind N goes back to before question N (its files and the conversation)"),
        ("/search WORDS", "earlier sessions in this folder that said these words"),
        ("/index [rebuild|off|on]", "the code index, when on: its files, symbols and the files skipped as secrets; rebuild; off or on for this session"),
        ("/permissions [allow|deny|remove RULE]", "kept rules, e.g. run_shell(npm test*), edit_file, read_file(.env*)"),
        ("/sandbox", "what commands in the sandbox may do"),
        ("/skills [new NAME [--mine]]", "the skills, by whose they are; new NAME makes one for the project, with --mine one of yours (in Arena when signed in)"),
        ("/skills export NAME… [--to FILE]", "the skills as a .zip of their folders (SKILL.md and their files), as Claude Code keeps them"),
        ("/skills import PATH [--mine]", "skills from a .zip, a SKILL.md or a folder, for the project (or yours); a name taken: --replace, --rename or --skip, else asked"),
        ("/NAME [words]", "use a skill, or a command of yours or the project's, with your words"),
        ("/commands", "your own and the project's commands, sub-agents and skills"),
        ("/project [N|NAME|none]", "the session's project in Arena: its last 7 days and its open goals; N or NAME puts it (and this folder's next sessions) in one"),
        ("/project new NAME", "makes a project in Arena (when you may) and puts this session in it"),
        ("/projects", "the projects you see in Arena, those you may add to numbered"),
        ("/sync [all]", "where this session stands with its chat in Arena; all sends this folder's sessions Arena never had"),
        ("/exit", "leave (also Ctrl+D, or Ctrl+C twice)"),
    ];

    /// <summary>
    /// /project: the session's project in Arena, its numbers for the last days and its open goals; /project N, NAME or none
    /// chooses; /project new NAME makes one (when Arena lets the person) and puts the session in it.
    /// </summary>
    private async Task ProjectCommandAsync(string arg, CancellationToken ct)
    {
        if (arg.Equals("new", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("new ", StringComparison.OrdinalIgnoreCase))
        {
            Ui.StartSpinner("Making the project in Arena");
            (bool Done, string Said) made;
            try
            {
                made = await rt.NewProjectAsync(arg[3..], ct);
            }
            finally
            {
                Ui.StopSpinner();
            }
            if (made.Done)
            {
                Ui.Line(made.Said);
            }
            else
            {
                Ui.Error(made.Said);
            }
            return;
        }
        if (arg.Length > 0)
        {
            var (done, said) = await rt.ChooseProjectAsync(arg, ct);
            if (done)
            {
                Ui.Line(said);
            }
            else
            {
                Ui.Error(said);
            }
            return;
        }
        if (rt.WorkProject is not { } now)
        {
            Ui.Line("Project: none.");
            Ui.Info("/projects lists those you see; /project N or NAME puts this session in one, /project new NAME makes one.");
            return;
        }
        Ui.StartSpinner("Asking Arena for the project");
        (ProjectSummary? Summary, string? Problem) asked;
        try
        {
            asked = await rt.ProjectSummaryAsync(ct);
        }
        finally
        {
            Ui.StopSpinner();
        }
        if (asked.Summary is not { } summary)
        {
            Ui.Line($"Project: {now.Name} — what this session uses counts to it.");
            Ui.Warn(asked.Problem ?? "Arena did not say more.");
            return;
        }
        foreach (var line in ArenaProjects.Describe(summary))
        {
            Ui.Line(line);
        }
        Ui.Info("/projects lists the others; /project none takes this session out.");
    }

    /// <summary>
    /// /projects: every project the person sees in Arena; those they may add to numbered, as /project N takes them, the
    /// session's and the repository's marked.
    /// </summary>
    private async Task ProjectsCommandAsync(CancellationToken ct)
    {
        Ui.StartSpinner("Asking Arena for its projects");
        (WorkProjectList? List, string? Problem) open;
        (List<JsonObject>? Projects, string? Problem) seen;
        try
        {
            var choices = rt.ProjectsAsync(ct);
            var all = ArenaProjects.ListAsync(rt.Config, rt.Http, ct);
            open = await choices;
            seen = await all;
        }
        finally
        {
            Ui.StopSpinner();
        }
        if (open.List is not { } list)
        {
            Ui.Warn(open.Problem!);
            return;
        }
        Ui.Line(rt.WorkProject is { } now ? $"Project: {now.Name} — what this session uses counts to it." : "Project: none.");
        if (list.Projects.Count == 0 && seen.Projects is not { Count: > 0 })
        {
            Ui.Info("You see no project in Arena yet: /project new NAME makes one (when you may), or ask a project's owner to add you.");
            return;
        }
        for (var i = 0; i < list.Projects.Count; i++)
        {
            var p = list.Projects[i];
            var row = seen.Projects?.FirstOrDefault(x => x.Str("id") == p.Id);
            var marks = (p.Id == rt.WorkProject?.Id ? " (this session's)" : "") + (p.Id == list.Suggested ? " (this repository's)" : "")
                + (row is null ? "" : $"  {row.Str("role")}{(row["global"]?.GetValue<bool>() == true ? ", global" : "")}, {ArenaProjects.Money(row["spentThisMonth"]?.GetValue<decimal>() ?? 0)} this month");
            Ui.Line($"  {i + 1,3}  {p.Name}{Ui.Dim(marks)}");
        }
        // The others they see: archived ones, and those they only look at.
        foreach (var x in (seen.Projects ?? []).Where(x => list.Projects.All(p => p.Id != x.Str("id"))))
        {
            Ui.Line($"       {x.Str("name")}{Ui.Dim($"  {(x.Str("status") == "archived" ? "archived" : x.Str("role"))}: you see it, nothing goes in it from here")}");
        }
        if (seen.Problem is { } why)
        {
            Ui.Warn(why);
        }
        Ui.Info("/project N or NAME puts this session, and the next ones in this folder, in it; /project none in none; /project new NAME makes one.");
    }

    /// <summary>/index: the code index's state; rebuild builds it again; off and on for this session.</summary>
    private async Task IndexCommandAsync(string arg, CancellationToken ct)
    {
        if (rt.CodeIndex is not { } host)
        {
            Ui.Info("The code index is not on: \"codeIndex\": true in config.json turns it and its tools (code_search, code_symbol, code_outline) on.");
            return;
        }
        switch (arg)
        {
            case "off":
                host.Off = true;
                Ui.Info("The code index is off for this session: its tools say so, and grep is used.");
                return;
            case "on":
                host.Off = false;
                Ui.Info("The code index is on again.");
                return;
            case "rebuild":
                Ui.Info("Building the code index again…");
                var built = await host.RebuildAsync();
                foreach (var line in Cli.IndexStatus(built, host.LastSync, host.Built))
                {
                    Ui.Line(line);
                }
                return;
            case "":
                if (await host.GetAsync(TimeSpan.FromSeconds(2), ct) is not { } index)
                {
                    Ui.Info("The code index is still being built (the first time in this folder).");
                    return;
                }
                foreach (var line in Cli.IndexStatus(index, host.LastSync, host.Built))
                {
                    Ui.Line(line);
                }
                if (host.Off)
                {
                    Ui.Info("It is off for this session (/index on turns it on again).");
                }
                return;
            default:
                Ui.Warn("/index, /index rebuild, /index off or /index on.");
                return;
        }
    }

    public async Task<int> RunAsync(string? first, CancellationToken ct)
    {
        Banner();
        if (_editor is not null)
        {
            // A command's output while the person types goes above the prompt, which is drawn again under it.
            Ui.Interject = _editor.Interject;
            // A question's answer is read by the prompt's own editor, never by a second reader of the same keys: what was
            // typed while the model worked stays for the next message, and Ctrl+C at the question stops the turn.
            Ui.AnswerReader = prompt => Answer(prompt, secret: false);
            // A password the same way, drawn as dots.
            Ui.SecretAnswerReader = prompt => Answer(prompt, secret: true);
        }
        string? Answer(string prompt, bool secret)
        {
            // The turn's watch of the keys leaves them to the question meanwhile.
            lock (_keysGate)
            {
                _asking = true;
                KeepTypedAhead();
            }
            try
            {
                return _editor!.Read(prompt, Ui.Dim("… "), wake: () => _turnStop?.IsCancellationRequested == true, answer: true, secret: secret);
            }
            finally
            {
                _asking = false;
            }
        }
        Ui.NotAnAnswer = text =>
        {
            lock (_keysGate)
            {
                _typed.Enqueue(text);
            }
            Ui.Info($"Not an answer, so a no: \"{Fmt.OneLine(text, 60)}\" is kept as your next message.");
        };
        // What the person adds in the web chat while the prompt waits comes in at once, said above it.
        rt.WebNews = () => _ = TakeInWhileWaitingAsync(ct);
        // A line typed while the model works goes into the turn at its next step, as in Claude Code.
        if (_editor is not null)
        {
            rt.Agent.SentMeanwhile = _ => Task.FromResult<IReadOnlyList<Meanwhile>>(TakeTyped());
        }
        // A background task of the person's that ended is said above the prompt.
        rt.Tasks.Changed += TaskEnded;
        // What Arena lacks of the earlier sessions (every folder's) goes in the background.
        rt.SendLaterInBackground();
        var pending = first;
        while (!ct.IsCancellationRequested)
        {
            rt.SayLater();
            if (pending is null && _typed.TryDequeue(out var typed))
            {
                Ui.Line();
                Ui.Line(Ui.Cyan("› ") + typed);
                pending = typed;
            }
            if (pending is null && rt.GoalPrompt() is { } goalTurn)
            {
                // A goal is being worked on: its next turn (what was typed meanwhile went first).
                await GoalTurnAsync(goalTurn, ct);
                continue;
            }
            string? input;
            if (pending is null)
            {
                // What the servers say while the person types waits for the line: it would break into what they write.
                rt.AtPrompt = true;
                _atPrompt.Release();
                try
                {
                    input = ReadInput();
                }
                finally
                {
                    // The web chat's news being taken in ends first: the line goes after it.
                    await _atPrompt.WaitAsync(CancellationToken.None);
                    rt.AtPrompt = false;
                }
                rt.SayLater();
            }
            else
            {
                input = pending;
            }
            pending = null;
            if (input == LineEditor.Woken)
            {
                // A command with no time limit ended while nobody was typing: the model carries on from it.
                await TurnAsync(null, ct);
                continue;
            }
            if (input is null)
            {
                // A read cut short by Ctrl+C (Windows) is not the end of input. The line editor reads Ctrl+C itself.
                if (_editor is null && DateTime.UtcNow - rt.Env.Cancel.LastPress < TimeSpan.FromSeconds(1))
                {
                    continue;
                }
                if (_editor is null && (rt.Jobs.Waiting.Count > 0 || rt.Jobs.HasNews))
                {
                    // The end of piped input: nobody types next, so the commands still running are waited for, and the
                    // model carries on from each, before the session ends.
                    await FinishAsync(ct);
                }
                break;
            }
            input = input.Trim();
            if (input.Length == 0)
            {
                continue;
            }
            // Kept for ↑, as a shell keeps its history: what someone typed here, not a pipe's lines, nor leaving.
            if (rt.Env.InTerminal && !Leaves(input))
            {
                rt.History.Add(input);
            }
            if (input.StartsWith('!') && input.Length > 1)
            {
                await ShellAsync(input[1..].Trim(), ct);
                continue;
            }
            // A skill or command added or changed since the last line works now, Arena's too (/skills lists it).
            await rt.ReloadExtensionsAsync(false, ct);
            if (input.StartsWith('/'))
            {
                if (!await CommandAsync(input, ct))
                {
                    break;
                }
                continue;
            }
            await TurnAsync(input, ct);
        }
        if (rt.Agent.Messages.Count == 0)
        {
            // Nothing was said: no session worth keeping.
            File.Delete(rt.Session.File);
            return 0;
        }
        Ui.Info($"Saved as {rt.Session.Id}: code-arena chat --resume {rt.Session.Id}");
        return 0;
    }

    /// <summary>The web chat's news, taken in while the prompt waits (not while a turn or a command runs: it comes then at the next turn).</summary>
    private async Task TakeInWhileWaitingAsync(CancellationToken ct)
    {
        if (!await _atPrompt.WaitAsync(0, CancellationToken.None))
        {
            return;
        }
        try
        {
            if (rt.Sync is { } sync && !ct.IsCancellationRequested)
            {
                rt.Agent.TakeIn(await sync.TakeInAsync(ct), text => Ui.Background(Ui.Dim(text)));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leaving.
        }
        finally
        {
            _atPrompt.Release();
        }
    }

    private void Banner()
    {
        var local = rt.Tools.All.Count(t => t.Server is null);
        var groups = rt.Links.Select(l => l.State == LinkState.Connected
            ? $"{rt.Tools.All.Count(t => t.Server == l.Name)} from {l.Title}"
            : $"{l.Title} {(l.State == LinkState.Connecting ? "connecting" : "not connected")}");
        var git = SystemPrompt.GitRoot(rt.Workspace.Root) is { } root && SystemPrompt.GitBranch(root) is { } branch ? $" ({branch})" : "";
        Ui.Line($"{Ui.Bold("Code Arena")} {Ui.Dim(Cli.Version)}");
        Ui.Line($"  {Ui.Dim("model ")}  {rt.Model.Name} {Ui.Dim($"({Fmt.Tokens(rt.Model.Context)} tokens{(rt.Model.Info.Thinking ? $", thinking {rt.Model.Thinking ?? "default"}" : "")})")}");
        Ui.Line($"  {Ui.Dim("mode  ")}  {ModeLabel(rt.Permissions.Mode)} {Ui.Dim("— " + rt.Permissions.Mode.Describe())}");
        Ui.Line($"  {Ui.Dim("folder")}  {rt.Workspace.Root}{git}");
        Ui.Line($"  {Ui.Dim("tools ")}  {string.Join(" · ", groups.Prepend($"{local} local"))}");
        if (rt.Agent.Messages.Count > 0)
        {
            Ui.Line($"  {Ui.Dim("resumed")} {Resumed()}");
        }
        if (rt.Permissions.Mode == Mode.Yolo)
        {
            Ui.Warn("yolo: edits and commands run without asking. Use it in a folder you can throw away.");
        }
        if (rt.PlanToResume() is { } left)
        {
            // A long task goes on across sessions: an earlier one left its plan in progress here.
            Ui.Info($"An earlier session's plan is in progress here: \"{left.Plan.Title}\", {left.Plan.Steps.Count(s => s.Status == WorkStep.Done)} of {left.Plan.Steps.Count} steps done ({left.Updated:yyyy-MM-dd HH:mm}). /plan resume carries it on.");
        }
        Ui.Info(_editor is null
            ? "/help for commands · Ctrl+C stops a turn · end a line with \\ to go on to the next"
            : "/help for commands · ↑ for what you sent before · Ctrl+C stops a turn · Tab skips the thinking · end a line with \\ to go on to the next");
        if (rt.Agent.Messages.Count > 0)
        {
            LastTurns();
        }
    }

    /// <summary>
    /// A resumed session as the banner and /resume say it: its id, how many messages it has (all of them: a compaction
    /// changes only what the model reads), and, compacted, how many of them the model reads as a summary.
    /// </summary>
    private string Resumed()
    {
        var (count, from) = rt.Session.Read(d => (d.Stream.Count, d.From));
        return $"{rt.Session.Id}, {count} messages" + (from > 0 ? $" (compacted: the model reads a summary of the first {from})" : "");
    }

    /// <summary>How many of a resumed session's last turns are shown.</summary>
    private const int TurnsShown = 2;

    /// <summary>
    /// A resumed session's last turns, as they were said: the person's message, the tools the turn used, and its last
    /// answer's start. The conversation is seen to go on; the IDE shows the whole of it.
    /// </summary>
    private void LastTurns()
    {
        var messages = rt.Session.Read(d => History.Messages(d)).OfType<JsonObject>().ToList();
        // The person's messages, not Code Arena's own notes to the model.
        var asked = Enumerable.Range(0, messages.Count).Where(i => messages[i].Str("role") == "user" && messages[i]["details"]?["note"] is null).ToList();
        if (asked.Count == 0)
        {
            return;
        }
        Ui.Line();
        if (asked.Count > TurnsShown)
        {
            var earlier = asked.Count - TurnsShown;
            Ui.Info($"… {earlier} earlier {(earlier == 1 ? "turn" : "turns")} (the IDE shows them all)");
        }
        for (var t = Math.Max(0, asked.Count - TurnsShown); t < asked.Count; t++)
        {
            var turn = messages.Skip(asked[t] + 1).Take((t + 1 < asked.Count ? asked[t + 1] : messages.Count) - asked[t] - 1).ToList();
            Ui.Line(Ui.Cyan("› ") + Fmt.OneLine(messages[asked[t]].Str("content"), 200));
            var tools = turn.Where(m => m.Str("role") == "tool").GroupBy(m => m.Str("toolName") ?? "a tool").Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}").ToList();
            if (tools.Count > 0)
            {
                Ui.Info("  " + string.Join(", ", tools));
            }
            if (turn.LastOrDefault(m => m.Str("role") == "assistant" && m.Str("content") is { Length: > 0 }) is { } answer)
            {
                Ui.Line(Fmt.Indent(Fmt.Head(answer.Str("content")!, 6), "  "));
            }
        }
        Ui.Line();
    }

    /// <summary>The commands that leave: not kept in the history.</summary>
    private static bool Leaves(string input) => input.ToLowerInvariant() is "/exit" or "/quit" or "/q";

    /// <summary>
    /// A command only the terminal has (all but /compact and /clear, which the IDE's chat has too): the IDE's ↑
    /// leaves them out of what it offers again.
    /// </summary>
    public static bool TerminalOnly(string input)
    {
        if (!input.StartsWith('/'))
        {
            return false;
        }
        var name = input.Split([' ', '\n'], 2)[0].ToLowerInvariant();
        return name is not ("/compact" or "/clear") && (name is "/quit" or "/q" or "/?" || Commands.Any(c => c.Name.Split(' ')[0] == name));
    }

    private string ModeLabel(Mode mode) => mode switch
    {
        Mode.Yolo => Ui.Red("yolo"),
        Mode.Plan => Ui.Blue("plan"),
        Mode.AutoEdit => Ui.Yellow("auto-edit"),
        _ => "ask",
    };

    /// <summary>A line, or several when each but the last ends with a backslash.</summary>
    private string? ReadInput()
    {
        Ui.Line();
        if (_editor is not null)
        {
            var ahead = _ahead;
            _ahead = "";
            return _editor.Read(Ui.Cyan("› "), Ui.Dim("… "), wake: () => rt.Jobs.HasNews, initial: ahead);
        }
        var line = Ui.ReadLine(Ui.Cyan("› "), () => rt.Jobs.HasNews);
        if (line is null or LineEditor.Woken)
        {
            return line;
        }
        var sb = new StringBuilder();
        while (line.EndsWith('\\'))
        {
            sb.Append(line[..^1]).Append('\n');
            line = Ui.ReadLine(Ui.Dim("… ")) ?? "";
        }
        return sb.Append(line).ToString();
    }

    /// <summary>What the person ran with !: it goes with their next message, so the model sees it.</summary>
    private readonly List<string> _ran = [];

    /// <summary>!command: run as the agent's commands run (in the sandbox), shown, and kept for the next message.</summary>
    private async Task ShellAsync(string command, CancellationToken ct)
    {
        using var key = rt.Env.Cancel.BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token);
        try
        {
            var result = await Proc.RunAsync(LocalTools.ShellCommand(command, rt.Context), TimeSpan.FromMinutes(10), linked.Token);
            Ui.Line(result.Output.TrimEnd());
            Ui.Info(result.TimedOut ? "Stopped after 10 minutes." : $"Exit {result.ExitCode}. It goes with your next message.");
            _ran.Add($"I ran `{command}` (exit {(result.TimedOut ? "-, stopped after 10 minutes" : result.ExitCode)}):\n```\n{result.Output.TrimEnd()}\n```");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Ui.Warn("Stopped.");
        }
        catch (ToolError e)
        {
            Ui.Error(e.Message);
        }
        finally
        {
            rt.Env.Cancel.EndTurn();
        }
    }

    /// <summary>
    /// The keys typed while the model worked and no one read them: each whole line kept as a next message (said so), a
    /// line begun kept for the next prompt. A question then reads only what is typed after it is asked.
    /// </summary>
    private void KeepTypedAhead()
    {
        if (rt.Env.Keys is not { } keys)
        {
            return;
        }
        lock (_keysGate)
        {
            while (keys.KeyAvailable && keys.ReadKey() is { } key)
            {
                Typed(key);
            }
        }
    }

    /// <summary>A key typed while the model worked: a line's end keeps it as a next message, the rest goes on the line begun.</summary>
    private void Typed(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
        {
            if (_ahead.EndsWith('\\'))
            {
                // A line ended with \ goes on to the next, as at the prompt.
                _ahead = _ahead[..^1] + "\n";
                return;
            }
            if (_ahead.Trim() is { Length: > 0 } text)
            {
                _typed.Enqueue(text);
                Ui.Info(Command(text)
                    ? $"Run when this turn ends: {Fmt.OneLine(text, 60)}"
                    : $"Read at the model's next step: {Fmt.OneLine(text, 60)} (↑ takes it back)");
            }
            _ahead = "";
        }
        else if (key.Key == ConsoleKey.UpArrow && _ahead.Length == 0 && _typed.Count > 0)
        {
            // Taken back before the model read it: on the prompt when the turn ends, to change, send or clear.
            var all = _typed.ToList();
            _typed.Clear();
            foreach (var line in all[..^1])
            {
                _typed.Enqueue(line);
            }
            _ahead = all[^1];
            Ui.Info($"Taken back: {Fmt.OneLine(_ahead, 60)} waits on the prompt when this turn ends.");
        }
        else if (key.Key == ConsoleKey.Backspace)
        {
            if (_ahead.Length > 0)
            {
                _ahead = _ahead[..^1];
            }
        }
        else if (!char.IsControl(key.KeyChar))
        {
            _ahead += key.KeyChar;
        }
    }

    /// <summary>A command (/name) or a shell line (!command): run by the session, never read by the model in the turn.</summary>
    private static bool Command(string line) => line.StartsWith('/') || (line.StartsWith('!') && line.Length > 1);

    /// <summary>
    /// The lines typed while the turn worked, in order, for its next step (<see cref="Agent.SentMeanwhile"/>): out of the queue and
    /// into the history, as a message sent at the prompt. A command waits for the turn to end, and the lines after it with it.
    /// </summary>
    private List<Meanwhile> TakeTyped()
    {
        lock (_keysGate)
        {
            var taken = new List<Meanwhile>();
            while (_typed.TryPeek(out var line) && !Command(line))
            {
                _typed.Dequeue();
                rt.History.Add(line);
                taken.Add(new Meanwhile($"t{++_typedIds}", line));
            }
            return taken;
        }
    }

    /// <summary>Held while keys typed during a turn are read: the turn's watch, a question's start, what is kept for after it.</summary>
    private readonly object _keysGate = new();

    /// <summary>A question of the turn reads the keys now: the watch leaves them to it.</summary>
    private volatile bool _asking;

    /// <summary>How often the keys are looked at while a turn runs.</summary>
    private static readonly TimeSpan KeysEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// While a turn runs: Tab skips the model's thinking (it answers at once, and thinks no more in this turn), and the
    /// other keys are kept as they would be after the turn. Only with the prompt's line editor (the keys one by one).
    /// </summary>
    private async Task WatchKeysAsync(CancellationToken ct)
    {
        if (_editor is null || rt.Env.Keys is not { } keys)
        {
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(KeysEvery, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (_keysGate)
            {
                while (!_asking && !ct.IsCancellationRequested && keys.KeyAvailable && keys.ReadKey() is { } key)
                {
                    if (key.Key == ConsoleKey.Tab || key.KeyChar == '\t')
                    {
                        if (!rt.Agent.SkipThinking())
                        {
                            Ui.Info(rt.Agent.ThinkingSkipped ? "The thinking is skipped already in this turn." : "Nothing is thinking now.");
                        }
                        continue;
                    }
                    Typed(key);
                }
            }
        }
    }

    /// <summary>The commands with no time limit waited for at the end of input, as a turn (Ctrl+C stops them and it).</summary>
    private async Task FinishAsync(CancellationToken ct)
    {
        var turn = new Spend();
        rt.Turn = turn;
        using var key = rt.Env.Cancel.BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token);
        try
        {
            await rt.FinishJobsAsync(turn, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            foreach (var job in rt.Jobs.Waiting)
            {
                job.Stop("by the person");
            }
            Ui.Warn("Stopped.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Ui.Error(e.Message);
        }
        finally
        {
            rt.Env.Cancel.EndTurn();
            rt.Turn = null;
        }
    }

    /// <summary>A turn: the person's message, or (null) the model carrying on after a command that ended.</summary>
    /// <summary>
    /// A turn: the person's message (null: carrying on after commands that ended). <paramref name="turn"/> counts what it
    /// spends, <paramref name="limit"/> also stops it (a goal's budget). Whether it was stopped (Ctrl+C, or the limit).
    /// </summary>
    private async Task<bool> TurnAsync(string? input, CancellationToken ct, Spend? turn = null, CancellationToken limit = default)
    {
        if (input is null)
        {
            var ended = rt.Jobs.Follow().Select(j => $"job {j.Id}").ToList();
            if (ended.Count == 0)
            {
                return false;
            }
            Ui.Line();
            Ui.Info($"{string.Join(", ", ended)} ended: the model carries on from {(ended.Count == 1 ? "it" : "them")}.");
        }
        if (_ran.Count > 0)
        {
            input = string.Join("\n\n", _ran) + "\n\n" + input;
            _ran.Clear();
        }
        turn ??= new Spend();
        rt.Turn = turn;
        var planBefore = rt.Context.Plan;
        var stopped = false;
        using var key = rt.Env.Cancel.BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token, limit);
        _turnStop = linked.Token;
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var watch = WatchKeysAsync(watching.Token);
        try
        {
            try
            {
                await rt.Agent.RunAsync(input, turn, linked.Token);
            }
            finally
            {
                await watching.CancelAsync();
                await watch;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            stopped = true;
            Ui.Warn(limit.IsCancellationRequested && !key.Token.IsCancellationRequested ? "Stopped: the goal's budget is spent." : "Stopped.");
            if (_typed.Count > 0)
            {
                // What was typed for after the turn may no longer apply: it waits in the prompt, to send, change or clear.
                var kept = string.Join("\n", _typed.Append(_ahead).Where(t => t.Length > 0));
                _typed.Clear();
                if (_editor is not null)
                {
                    _ahead = kept;
                    Ui.Info("What you typed meanwhile is back in the prompt: Enter sends it.");
                }
                else
                {
                    Ui.Info($"Not sent, as the turn was stopped: {Fmt.OneLine(kept, 120)}");
                }
            }
        }
        catch (Exception e) when (e is GatewayException or HttpRequestException or IOException)
        {
            Ui.Error(e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Whatever goes wrong in a turn ends that turn, never the conversation.
            Ui.Error($"The turn failed: {e.Message}");
        }
        finally
        {
            _turnStop = null;
            rt.Env.Cancel.EndTurn();
            rt.Turn = null;
        }
        if (turn.Requests > 0)
        {
            Ui.Info(turn.Describe(Ui) + " · " + ContextUse());
        }
        if (rt.Context.Plan.Review == WorkPlan.Proposed && !ReferenceEquals(rt.Context.Plan, planBefore))
        {
            // The agent proposed its plan (plan mode): the person reviews it.
            Ui.Line();
            Ui.Line(rt.Context.Plan.Render(Ui));
            Ui.Info("Review the plan: /plan approve (carry it out, edits run) · /plan approve ask (asking before each change) · "
                + "/plan change N what · /plan another why · /plan reject why · or say more.");
        }
        return stopped;
    }

    /// <summary>/plan approve | approve ask | change N what | another why | reject why: the person's review of a proposed plan; /plan undo N.</summary>
    private async Task ReviewPlanAsync(string arg, CancellationToken ct)
    {
        if (arg.Equals("resume", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Ui.Line(rt.ResumePlan());
                Ui.Line(rt.Context.Plan.Render(Ui));
                Ui.Info("Say \"go on\" (or what to do first) and the agent carries it on.");
            }
            catch (PlanError e)
            {
                Ui.Error(e.Message);
            }
            return;
        }
        if (arg.StartsWith("undo", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(arg[4..].Trim().TrimStart('#'), out var undo))
            {
                Ui.Error("Say which step to undo: /plan undo 2.");
                return;
            }
            try
            {
                Ui.Line(rt.UndoStep(undo));
                Ui.Line(rt.Context.Plan.Render(Ui));
            }
            catch (PlanError e)
            {
                Ui.Error(e.Message);
            }
            return;
        }
        var (word, rest) = arg.IndexOf(' ') is var at and > 0 ? (arg[..at].ToLowerInvariant(), arg[(at + 1)..].Trim()) : (arg.ToLowerInvariant(), "");
        int? step = null;
        if (word == "approve" && rest.Equals("ask", StringComparison.OrdinalIgnoreCase))
        {
            (word, rest) = ("approve-ask", "");
        }
        else if (word == "change" && rest.Split(' ', 2) is [var n, ..] && int.TryParse(n.TrimStart('#'), out var number))
        {
            step = number;
            rest = rest.Length > n.Length ? rest[(n.Length + 1)..].Trim() : "";
        }
        string said;
        try
        {
            said = rt.ReviewPlan(word, step, rest);
        }
        catch (PlanError e)
        {
            Ui.Error(e.Message);
            return;
        }
        if (word.StartsWith("approve", StringComparison.Ordinal))
        {
            Ui.Line($"Mode: {ModeLabel(rt.Permissions.Mode)} — {rt.Permissions.Mode.Describe()}");
        }
        await TurnAsync(said, ct);
    }

    /// <summary>/jobs: the commands with no time limit and how each is; /jobs stop N stops one.</summary>
    private async Task JobsAsync(string arg)
    {
        if (arg.StartsWith("stop", StringComparison.OrdinalIgnoreCase))
        {
            await StopJobAsync(arg[4..].Trim().TrimStart('#'));
            return;
        }
        if (rt.Jobs.All.Count == 0)
        {
            Ui.Info("No commands with no time limit in this session.");
        }
        foreach (var job in rt.Jobs.All)
        {
            Ui.Line($"  job {job.Id}  {Fmt.OneLine(job.Command, 70)}  {Ui.Dim(job.Status())}");
        }
    }

    /// <summary>/jobs stop N: the person stops a command they let run with no time limit (the model is told how it ended).</summary>
    private async Task StopJobAsync(string id)
    {
        if (!int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) || rt.Jobs.Find(n) is not { } job)
        {
            Ui.Warn(id.Length == 0 ? "Which job? /jobs stop N (/jobs lists them)." : $"There is no job {id}: /jobs lists them.");
            return;
        }
        if (!job.Running)
        {
            Ui.Info($"job {job.Id} has ended: {job.Status()}.");
            return;
        }
        job.Stop("by the person, with /jobs stop");
        // Its end is said by the watcher as it comes.
        if (await Task.WhenAny(job.Done, Task.Delay(TimeSpan.FromSeconds(5))) != job.Done)
        {
            Ui.Warn($"job {job.Id} is being stopped.");
        }
    }

    /// <summary>
    /// "context 18.2k / 131k (14%), compacts at 80%" (or "only when a request would not fit"). With no count since the last
    /// compaction or resume, an estimate as the IDE says it: "context about 16.4k / 32.8k (50%, the next answer counts it)".
    /// </summary>
    private string ContextUse()
    {
        var used = rt.Agent.Estimate();
        var share = $"{100.0 * used / rt.Model.Context:0}%";
        return rt.Agent.Counted
            ? $"context {Fmt.Tokens(used)} / {Fmt.Tokens(rt.Model.Context)} ({share}), compacts {rt.Compaction.When()}"
            : $"context about {Fmt.Tokens(used)} / {Fmt.Tokens(rt.Model.Context)} ({share}, the next answer counts it), compacts {rt.Compaction.When()}";
    }

    /// <summary>
    /// What fills the window, as Claude Code's /context and the web chat's context view show it: a bar of the whole window,
    /// each part of the next request with its tokens and share and its biggest pieces (each tool's definition, each skill,
    /// file and message), the room kept for the answer and what is free, adding up to the window; then what could be freed.
    /// </summary>
    private async Task WindowCommandAsync(CancellationToken ct)
    {
        var lines = ContextWindow.Lines(await ContextView.WindowAsync(rt, ct)).ToList();
        Ui.Line(Ui.Blue(lines[0]));
        Ui.Line(Ui.Dim(lines[1]));
        foreach (var line in lines.Skip(2))
        {
            Ui.Line(line.StartsWith("      ", StringComparison.Ordinal) ? Ui.Dim(line) : line == "Could be freed:" ? Ui.Bold(line) : line);
        }
        Ui.Info("Big results are kept aside (read_result reads them); before compacting, earlier results are cleared to their note. /compact summarizes now; /tools off turns a server's tools off.");
    }

    /// <summary>Runs a slash command; false to leave.</summary>
    private async Task<bool> CommandAsync(string input, CancellationToken ct)
    {
        var space = input.IndexOf(' ');
        var name = (space < 0 ? input : input[..space]).ToLowerInvariant();
        var arg = space < 0 ? "" : input[(space + 1)..].Trim();
        switch (name)
        {
            case "/exit" or "/quit" or "/q":
                return false;
            case "/help" or "/?":
                // Memory denied in Arena: /memory is not offered.
                foreach (var (command, what) in Commands.Where(c => !(rt.Memory.Denied && c.Name.StartsWith("/memory", StringComparison.Ordinal))))
                {
                    Ui.Line($"  {command,-20} {Ui.Dim(what)}");
                }
                if (_editor is not null)
                {
                    Ui.Info("Keys: ↑ and ↓ step through what you sent in this folder, Esc goes back to what you were typing; a line ending in \\ goes on to the next.");
                }
                Ui.Info("Files: ARENA.md in the repository (and in your config folder) is read into every session.");
                break;
            case "/model":
                ChooseModel(arg);
                break;
            case "/providers":
                await ProvidersAsync(arg, ct);
                break;
            case "/mode":
                if (arg.Length == 0)
                {
                    Ui.Line($"Mode: {ModeLabel(rt.Permissions.Mode)} — {rt.Permissions.Mode.Describe()}");
                    Ui.Info($"Switch with /mode {string.Join(" | ", Modes.Names)}");
                }
                else if (Modes.Parse(arg) is { } mode)
                {
                    rt.Permissions.Mode = mode;
                    Ui.Line($"Mode: {ModeLabel(mode)} — {mode.Describe()}");
                    if (mode == Mode.Yolo)
                    {
                        Ui.Warn("yolo: edits and commands run without asking.");
                    }
                }
                else
                {
                    Ui.Error($"There is no mode {arg}: {string.Join(", ", Modes.Names)}.");
                }
                break;
            case "/thinking":
                if (arg.Length == 0)
                {
                    Ui.Line(rt.Model.Info.Thinking ? $"Thinking: {rt.Model.Thinking ?? "default"}" : $"{rt.Model.Name} does not think.");
                    Ui.Info($"Set with /thinking default | {string.Join(" | ", ModelState.Levels)}");
                }
                else if (arg == "default" || ModelState.Levels.Contains(arg))
                {
                    rt.Model.Thinking = arg == "default" ? null : arg;
                    Ui.Line($"Thinking: {arg}" + (rt.Model.Info.Thinking ? "" : $" (but {rt.Model.Name} does not think)"));
                }
                else
                {
                    Ui.Error($"Thinking is one of: default, {string.Join(", ", ModelState.Levels)}.");
                }
                break;
            case "/tools" when arg.Split(' ', 2, StringSplitOptions.TrimEntries) is [var verb, { Length: > 0 } which] && verb is "enable" or "disable":
            {
                // The project's own tool or MCP server, by its name: enabled for this project on this computer.
                var key = rt.OwnToolFiles.Any(t => t.Name == which) ? "tool:" + which : "mcp:" + which;
                var (done, enabled) = rt.EnableOwn(key, verb == "enable");
                if (done)
                {
                    Ui.Line(enabled);
                }
                else
                {
                    Ui.Error(enabled);
                }
                break;
            }
            case "/tools" when arg.Split(' ', StringSplitOptions.RemoveEmptyEntries) is ["set", var toolName, var how, ..] words:
            {
                // /tools set NAME on|off|ask|allow|default [--here]: kept for every project, or for this one.
                var here = words.Contains("--here");
                var tool = rt.Tools.Every.FirstOrDefault(t => t.Name == toolName);
                if (tool is null || how is not ("on" or "off" or "ask" or "allow" or "default"))
                {
                    Ui.Error(tool is null ? $"There is no tool {toolName}: /tools lists them." : "/tools set NAME on, off, ask (every call), allow (never asks) or default [--here].");
                    break;
                }
                var (person, inProject, _) = rt.ToolSettings.Of(toolName);
                var was = here ? inProject : person;
                var choice = how switch
                {
                    "on" => was with { Enabled = null },
                    "off" => was with { Enabled = false },
                    "default" => was with { Approval = null },
                    _ => was with { Approval = how },
                };
                if (rt.ToolSettings.Save(here ? "here" : "person", toolName, choice, tool) is { } refused)
                {
                    Ui.Error(refused);
                    break;
                }
                Ui.Line($"{toolName}: {how switch { "on" => "on", "off" => "off", "ask" => "asks before each call", "allow" => "runs without asking (but in plan mode)", _ => "asks as its kind does" }}" +
                    $"{(here ? ", in this project (kept on this computer)" : ", in every project (config.json)")}.");
                break;
            }
            case "/tools" when arg.Split(' ', 2, StringSplitOptions.TrimEntries) is [var verb, { Length: > 0 } which] && verb is "on" or "off":
                // A group by its id or title (argus, Web search...), or by one of its functions.
                var found = rt.Tools.All.Where(t => ToolBox.SwitchOf(t) is { } g && (g == which || t.Name == which || string.Equals(rt.ToolTitle(g), which, StringComparison.OrdinalIgnoreCase)))
                    .Select(t => ToolBox.SwitchOf(t)!).Distinct().ToList();
                if (found.Count == 0)
                {
                    Ui.Error($"There are no tools {which} here to turn on or off: /tools lists them (the files, commands and git are always on).");
                    break;
                }
                rt.Demand.SetOff(verb == "off" ? rt.Demand.Off.Union(found) : rt.Demand.Off.Except(found));
                Ui.Info($"{string.Join(", ", found.Select(rt.ToolTitle))} {(verb == "off" ? "off" : "on")} in this session.");
                break;
            case "/tools":
                var width = rt.Tools.Every.Max(t => t.Name.Length);
                var waiting = rt.Tools.ToLoad().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                var inForce = rt.Tools.All.ToDictionary(t => t.Name, StringComparer.Ordinal);
                foreach (var group in rt.Tools.Every.GroupBy(t => t.Server is null ? t.Origin is null ? "" : "own" : t.Server))
                {
                    Ui.Line(Ui.Bold(group.Key switch { "" => "Local", "own" => "Your own and the project's", var s => rt.Links.FirstOrDefault(l => l.Name == s)?.Title ?? s }));
                    foreach (var own in group)
                    {
                        if (!inForce.TryGetValue(own.Name, out var t))
                        {
                            Ui.Line(Ui.Dim($"  {own.Name.PadRight(width)}  off (your tool settings, or the project's)"));
                            continue;
                        }
                        if (ToolBox.SwitchOf(t) is { } g && rt.Demand.IsOff(g))
                        {
                            Ui.Line(Ui.Dim($"  {t.Name.PadRight(width)}  off in this session ({rt.ToolTitle(g)})"));
                            continue;
                        }
                        var mark = t.Approval switch
                        {
                            "ask" => Ui.Yellow("asks each call "),
                            "allow" => Ui.Yellow("never asks "),
                            _ => t.Kind switch
                            {
                                ToolKind.Edit => Ui.Yellow("edits "),
                                ToolKind.Shell => Ui.Yellow("runs commands "),
                                ToolKind.Machine => Ui.Yellow("changes VMs "),
                                ToolKind.Remote when !t.Trusted && !t.ReadOnly => Ui.Yellow("asks "),
                                _ => "",
                            },
                        };
                        var where = t.Origin is { } o ? Ui.Dim($"({o}) ") : "";
                        Ui.Line($"  {t.Name.PadRight(width)}  {mark}{where}{(waiting.Contains(t.Name) ? Ui.Dim("loaded when needed ") : "")}{Ui.Dim(Fmt.OneLine(t.Description, 80))}");
                    }
                }
                var notYet = rt.OwnToolFiles.Where(f => !rt.IsEnabled(f)).Select(f => $"{f.Name} ({f.Origin}: {(f.Argv is { } argv ? string.Join(' ', argv) : f.Shell)})")
                    .Concat(rt.ProjectServers.Where(s => !rt.IsEnabled(s)).Select(s => $"{s.Server.Name} (MCP server, {Path.GetFileName(s.File)})")).ToList();
                if (notYet.Count > 0)
                {
                    Ui.Line(Ui.Bold("The project's, not enabled here"));
                    foreach (var line in notYet)
                    {
                        Ui.Line($"  {line}");
                    }
                    Ui.Info("/tools enable NAME enables one for this project, as it is defined now (a change to its command needs enabling again).");
                }
                Ui.Info("/tools set NAME off|on|ask|allow|default [--here] keeps a tool's setting for every project (or this one); the IDE's Configure Tools does more.");
                if (waiting.Count > 0)
                {
                    Ui.Info($"Tools loaded when needed are listed for the model, which loads them with {ToolDemand.Function} (\"toolChars\" in config.json sets when).");
                }
                if (!rt.ArenaConnected && rt.Config.ArenaTools)
                {
                    Ui.Info("Arena's tools are not connected in this session.");
                }
                break;
            case "/checklist":
                Ui.Line(await rt.ChecklistCommandAsync(arg, ct));
                break;
            case "/plan" when arg.Length == 0:
                Ui.Line(rt.Context.Plan.Render(Ui));
                break;
            case "/plan":
                await ReviewPlanAsync(arg, ct);
                break;
            case "/agent":
                await AgentCommandAsync(arg, ct);
                break;
            case "/assistant":
                Ui.Line(await rt.AssistantCommandAsync(arg, ct));
                break;
            case "/tasks":
                await TasksCommandAsync(arg, ct);
                break;
            case "/goal":
                GoalCommand(arg);
                break;
            case "/loop":
                LoopCommand(arg);
                break;
            // The project's or the person's own /review command, when there is one, goes first (default: below).
            case "/review" when rt.Extensions.Expand(input) is null:
                if (arg.Length > 0 && (arg.StartsWith('-') || arg.Any(char.IsWhiteSpace)))
                {
                    Ui.Error("/review takes one branch or commit to compare with, e.g. /review main.");
                    break;
                }
                await TurnAsync(ReviewPrompt(arg), ct);
                break;
            case "/compact":
                using (var key = rt.Env.Cancel.BeginTurn())
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token))
                {
                    try
                    {
                        if (rt.Agent.Messages.Count < 2)
                        {
                            Ui.Info("Nothing to compact yet.");
                        }
                        else
                        {
                            await rt.Agent.CompactAsync(force: true, linked.Token);
                        }
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        Ui.Warn("Stopped: the conversation is as it was.");
                    }
                    catch (Exception e) when (e is GatewayException or HttpRequestException or IOException)
                    {
                        Ui.Error(e.Message);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        // Whatever goes wrong in a compaction ends it, never the conversation.
                        Ui.Error($"The compaction failed: {e.Message}");
                    }
                    finally
                    {
                        rt.Env.Cancel.EndTurn();
                    }
                }
                break;
            case "/cost":
                Ui.Line($"This session: {rt.Total.Describe(Ui)}");
                var used = rt.Agent.Estimate();
                Ui.Line($"The window: about {Fmt.Tokens(used)} of {Fmt.Tokens(rt.Model.Context)} tokens ({100.0 * used / rt.Model.Context:0}%); {rt.Compaction.Describe(rt.CompactAtFrom)}.");
                break;
            case "/context":
                Ui.Line(ContextUse() + (rt.CompactAtFrom is { } from ? $" ({from})" : "") + (rt.Compaction.At == Compaction.Off ? "" : ", and always before a request would not fit") +
                    $", keeping the recent part within {rt.Compaction.Target}%.");
                await WindowCommandAsync(ct);
                break;
            case "/compact-at":
                CompactAt(arg);
                break;
            case "/mcp":
                if (arg.StartsWith("retry", StringComparison.OrdinalIgnoreCase))
                {
                    var which = arg[5..].Trim();
                    var links = rt.Links.Where(l => which.Length == 0 ? l.State != LinkState.Connected : l.Name.Equals(which, StringComparison.OrdinalIgnoreCase) || l.Title.Equals(which, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (links.Count == 0)
                    {
                        Ui.Info(which.Length == 0 ? "Every server is connected." : $"There is no server {which}: /mcp lists them.");
                    }
                    foreach (var link in links)
                    {
                        link.Retry();
                        Ui.Info($"Trying {link.Title} again: it says when it is connected.");
                    }
                    break;
                }
                if (rt.Links.Count == 0)
                {
                    Ui.Info("No MCP servers: Arena's and Argus's are off in config.json (arenaTools, argusTools), and none of your own is set up (mcpServers).");
                }
                foreach (var link in rt.Links)
                {
                    var mark = link.State switch
                    {
                        LinkState.Connected => Ui.Green("●"),
                        LinkState.Connecting => Ui.Yellow("◌"),
                        LinkState.Unavailable => Ui.Dim("○"),
                        _ => Ui.Red("●"),
                    };
                    Ui.Line($"  {mark} {rt.Describe(link)} {Ui.Dim(link.Url ?? "")}");
                }
                if (rt.Links.Any(l => l.State != LinkState.Connected))
                {
                    Ui.Info("/mcp retry tries those not connected now (or /mcp retry NAME). Turn Arena's or Argus's off with \"arenaTools\": false or \"argusTools\": false in config.json.");
                }
                break;
            case "/jobs":
                await JobsAsync(arg);
                break;
            case "/index":
                await IndexCommandAsync(arg, ct);
                break;
            case "/clear":
                var previous = rt.Session.Id;
                rt.NewSession();
                Ui.Line($"A new session. The last one is saved as {previous}.");
                break;
            case "/web":
                if (await rt.ContinueWebChatAsync(arg, ct) is { } said)
                {
                    Ui.Info(said);
                }
                else
                {
                    Ui.Line($"Continuing the chat from Arena in session {rt.Session.Id}: {rt.Agent.Messages.Count} messages.");
                }
                break;
            case "/remember":
                RememberCommand();
                break;
            case "/memory":
                await MemoryCommandAsync(arg, ct);
                break;
            case "/rewind":
                await RewindCommandAsync(arg, ct);
                break;
            case "/search":
                SearchCommand(arg);
                break;
            case "/permissions":
                PermissionsCommand(arg);
                break;
            case "/sandbox":
                Ui.Line(rt.Sandbox.Describe());
                break;
            case "/commands":
                ExtensionsCommand();
                break;
            case "/skills":
                await SkillsCommandAsync(arg, ct);
                break;
            case "/project":
                await ProjectCommandAsync(arg, ct);
                break;
            case "/projects":
                await ProjectsCommandAsync(ct);
                break;
            case "/sync" when arg.Equals("all", StringComparison.OrdinalIgnoreCase):
                Ui.StartSpinner("Sending this folder's sessions to Arena");
                List<string> failed;
                int sent;
                try
                {
                    (sent, failed) = await rt.SendAllAsync(null, ct);
                }
                finally
                {
                    Ui.StopSpinner();
                }
                Ui.Line(sent == 0 && failed.Count == 0
                    ? "Arena has every session of this folder already."
                    : $"Sent to Arena: {sent} session{(sent == 1 ? "" : "s")}.");
                foreach (var why in failed)
                {
                    Ui.Warn(why);
                }
                break;
            case "/sync":
                Ui.Line(rt.Sync is { } sync
                    ? (rt.Account is { } account ? $"Kept with {account}'s chats in Arena (the key's account). " : "") + sync.Describe()
                    : "Sessions are not kept with Arena here (\"syncChats\": false in config.json, or no Arena address).");
                break;
            case "/resume":
                var id = arg.Length > 0 ? arg : Cli.PickSession(rt.Env, Ui, rt.Workspace.Root);
                if (id is null)
                {
                    break;
                }
                if (SessionStore.Find(rt.Env.Paths.SessionsDir, id) is { } file)
                {
                    rt.Resume(file);
                    Ui.Line($"Resumed {Resumed()}, model {rt.Model.Name}.");
                    LastTurns();
                }
                else
                {
                    Ui.Error($"No saved session {id}.");
                }
                break;
            default:
                if (await rt.ExpandAsync(input, ct) is { } prompt)
                {
                    await TurnAsync(prompt, ct);
                    break;
                }
                Ui.Error($"There is no command {name}: /help lists them, /skills the skills and /commands your own.");
                break;
        }
        return true;
    }

    /// <summary>
    /// /review: a turn that reviews the changes with the git tool, as the Code page's reviews do: findings on lines, each
    /// one the model tried to refute first, and a verdict; nothing changed. With <paramref name="reportTool"/> (the IDE's
    /// Review view), the findings are reported with report_review instead of written out.
    /// </summary>
    public static string ReviewPrompt(string baseRef, bool reportTool = false)
    {
        var what = baseRef.Length == 0
            ? "the changes not yet committed (staged and not). Read them with the git tool: git status, then git diff HEAD"
            : $"what this branch has over {baseRef}, and what is not yet committed. Read them with the git tool: git log --oneline {baseRef}..HEAD, git diff {baseRef}...HEAD, then git diff HEAD";
        return $"Review {what}. " +
            "For each changed file, read enough of the file around each change (and the code it calls or is called by) to judge it. " +
            "Look for bugs, security problems, broken edge cases, races, missing error handling, performance traps and missing tests; not style a formatter would fix. " +
            "Before you report a finding, try to refute it by reading the code it depends on, and leave out what you refuted. " +
            (reportTool
                ? "Read each changed file with read_file (its lines are numbered) and take each finding's line from there, not from the diff. " +
                  "When you are done, call report_review once: the verdict (looks good, comments, or changes needed), a summary, and every finding with its " +
                  "file (relative to the working directory), its line in the file as it is now, its severity (critical, major, minor, nit), what is wrong, why it " +
                  "matters and the fix. For a small fix, also give old (the exact lines in the file now, unique in it) and new (what replaces them). Do not change any file."
                : "Report the findings by severity (critical, major, minor, nit), each as file:line, what is wrong, why it matters, and the fix. " +
                  "End with a verdict: looks good, comments, or changes needed. Do not change any file.");
    }

    /// <summary>
    /// /agent: the person's own coding agents here; /agent NAME TASK runs one on the task, its output shown as it comes,
    /// and what it said goes into the conversation, so the model knows it.
    /// </summary>
    private async Task AgentCommandAsync(string arg, CancellationToken ct)
    {
        var agents = ProviderAgents.Available(rt.Config, rt.Env.Env);
        var space = arg.IndexOf(' ', StringComparison.Ordinal);
        if (arg.Trim().Length == 0 || space < 0)
        {
            if (agents.Count == 0)
            {
                Ui.Info("No coding agent of yours is here: install Claude Code (claude), Codex CLI (codex) or Gemini CLI (gemini), sign in with your subscription, and they show here.");
                return;
            }
            foreach (var (name, line) in agents)
            {
                Ui.Line($"{name}: {(ProviderAgents.About.TryGetValue(name, out var about) ? about : string.Join(' ', line))}");
            }
            Ui.Info("/agent NAME TASK hands one a task; the model may too (ask_agent).");
            return;
        }
        var (agent, task) = (arg[..space].Trim(), arg[(space + 1)..].Trim());
        using var key = rt.Env.Cancel.BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token);
        try
        {
            var result = await ProviderAgents.RunAsync(agents, agent, task, rt.Context.Workspace.Root, linked.Token, l => Ui.Line(Ui.Dim(l)));
            if (result.Error)
            {
                Ui.Error(Fmt.Head(result.Text, 6, 300));
            }
            rt.Agent.Remember($"(The person had {agent} do: {task})\n\n{result.Text}");
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Ui.Warn($"Stopped {agent}.");
        }
        finally
        {
            rt.Env.Cancel.EndTurn();
        }
    }

    /// <summary>The project's long memory: where it is, how big, its sections and topic files; or how it starts.</summary>
    private void RememberCommand()
    {
        if (!rt.Config.ProjectMemory)
        {
            Ui.Line("The project's long memory is off (\"projectMemory\": false in config.json).");
            return;
        }
        var root = ProjectMemory.Root(rt.Workspace.Root);
        var main = Path.Combine(ProjectMemory.Folder(root), ProjectMemory.MainName);
        if (!File.Exists(main))
        {
            Ui.Line($"No long memory yet: the agent starts {ProjectMemory.FolderName}/{ProjectMemory.MainName} (with remember_project) when it learns what a later session should know, or write it yourself.");
            return;
        }
        var lines = File.ReadAllLines(main);
        Ui.Line($"{Path.GetRelativePath(rt.Workspace.Root, main)} ({Files.Size(new FileInfo(main).Length)}, {lines.Length} lines), read at every session's start. Its sections:");
        foreach (var heading in lines.Where(l => l.StartsWith("## ", StringComparison.Ordinal)))
        {
            Ui.Line("  " + heading[3..]);
        }
        var topics = Directory.EnumerateFiles(ProjectMemory.Folder(root), "*.md", SearchOption.AllDirectories).Where(f => f != main).ToList();
        if (topics.Count > 0)
        {
            Ui.Line("Topic files: " + string.Join(", ", topics.Select(f => Path.GetRelativePath(ProjectMemory.Folder(root), f))));
        }
    }

    private async Task MemoryCommandAsync(string arg, CancellationToken ct)
    {
        var words = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var memory = rt.Memory;
        if (memory.Denied)
        {
            Ui.Warn(MemoryStore.DeniedText);
            return;
        }
        if (words is ["add", var rest])
        {
            var person = rest.StartsWith("--all ", StringComparison.Ordinal);
            var text = person ? rest[6..].Trim() : rest.Trim();
            try
            {
                var (added, where) = await memory.RememberAsync(person, text, "fact", ct);
                Ui.Line(added ? $"Remembered {where}." : $"Already remembered {where}.");
            }
            catch (ToolError e)
            {
                Ui.Warn(e.Message);
            }
            return;
        }
        if (words is ["forget", var which])
        {
            if (memory.Online)
            {
                Ui.Line(int.TryParse(which, out var number) && await memory.ForgetListedAsync(number, ct) is { } forgotten
                    ? $"Forgotten: {forgotten}"
                    : $"There is no memory {which} of yours: /memory lists them.");
                return;
            }
            var person = which.StartsWith("--all ", StringComparison.Ordinal);
            var n = int.TryParse(person ? which[6..] : which, out var k) ? k : 0;
            Ui.Line(memory.Forget(person, n) is { } gone ? $"Forgotten: {gone}" : $"There is no memory {which}: /memory lists them.");
            return;
        }
        if (memory.Online)
        {
            // Arena's: the person's, this project's and the company's, as the session started (yours numbered, to forget).
            var mine = memory.Listed.Where(m => m.Mine).ToList();
            foreach (var (title, list) in new[]
            {
                ("This project", mine.Where(m => m.Scope == "project").ToList()),
                ("Every project", mine.Where(m => m.Scope == "person").ToList()),
                ("The company's", memory.Listed.Where(m => !m.Mine).ToList()),
            })
            {
                Ui.Line(Ui.Bold(title) + (title == "This project" ? Ui.Dim($"  {memory.ProjectKey}") : ""));
                Ui.Line(list.Count == 0 ? Ui.Dim("  (nothing yet)") : string.Join('\n', list.Select(m =>
                    $"  {(m.Mine ? $"{mine.IndexOf(m) + 1,2}." : "  -")} {m.Text}{(m.Kind == "fact" ? "" : Ui.Dim($" ({(m.Kind == "howto" ? "how to" : m.Kind)})"))}{(m.Pinned ? Ui.Dim(" (pinned)") : "")}")));
            }
            Ui.Info("Arena keeps Code Arena's memory, the web chat's too (Memory in Arena edits it). The model keeps memories with remember and searches them with recall; " +
                "/memory add [--all] TEXT adds one, /memory forget N forgets one.");
            return;
        }
        foreach (var (person, title) in new[] { (false, "This project"), (true, "Every project (/memory add --all, forget --all N)") })
        {
            var all = memory.Read(person);
            Ui.Line(Ui.Bold(title) + Ui.Dim($"  {memory.FileOf(person)}"));
            Ui.Line(all.Count == 0 ? Ui.Dim("  (nothing yet)") : string.Join('\n', all.Select((m, i) => $"  {i + 1,2}. {m}")));
        }
        Ui.Info("Kept on this machine (Arena did not answer as the session started: they go to it next time). The model keeps memories with remember; " +
            "/memory add TEXT adds one, /memory forget N forgets one. The files are plain Markdown to edit.");
    }

    private async Task RewindCommandAsync(string arg, CancellationToken ct)
    {
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // The person's questions in the whole conversation, as the IDE's Rewind to here has them: before this run too (resumed).
        var questions = rt.Session.Read(d => d.Stream.Select((e, place) => (e, place)).Where(q => q.e.Message.Str("role") == "user" && q.e.Kind is null)
            .Select(q => (Place: q.place, Text: Mentions.Typed(Quotes.Split(q.e.Message.Str("content") ?? "").Text))).ToList());
        if (words.Length == 0 || !int.TryParse(words[0], out var n))
        {
            if (questions.Count == 0)
            {
                Ui.Info("No question to go back to yet.");
                return;
            }
            var turns = rt.Checkpoints.Turns;
            for (var i = 0; i < questions.Count; i++)
            {
                // Its checkpoint, when this run kept one: then its code can go back too.
                var turn = rt.TurnAt(questions[i].Place) is var t and > 0 ? turns[t - 1] : null;
                Ui.Line($"  {i + 1,2}. {(turn is null ? "     " : $"{turn.At:HH:mm}")}  {Fmt.OneLine(questions[i].Text, 70)}  " +
                    Ui.Dim(turn is null ? "(conversation only)" : $"{turn.Before.Count} files"));
            }
            Ui.Info("/rewind N goes back to before question N: the files its tools and later turns' wrote, and the conversation. /rewind N code: only the files; /rewind N chat: only the conversation. " +
                "A question with no checkpoint (asked before this run, or too long ago) goes back in the conversation only.");
            return;
        }
        if (n < 1 || n > questions.Count)
        {
            Ui.Error(questions.Count == 0 ? "No question to go back to yet." : $"There is no question {n}: /rewind lists them (1 to {questions.Count}).");
            return;
        }
        var only = words.Length > 1 ? words[1].ToLowerInvariant() : null;
        Ui.Line(await rt.BackToAsync(questions[n - 1].Place, files: only is null or "code", chat: only is null or "chat", ct));
    }

    private void SearchCommand(string arg)
    {
        if (arg.Length == 0)
        {
            Ui.Error("Give the words: /search parser timeout");
            return;
        }
        var hits = SessionSearch.Find(rt.Env.Paths.SessionsDir, arg, rt.Workspace.Root, null);
        Ui.Line(hits.Count == 0 ? "No session in this folder said all of that." : string.Join('\n', hits.Select(h => $"  {h.When:yyyy-MM-dd HH:mm}  {Ui.Dim(h.Session)}  {h.Role}: {h.Snippet}")));
        if (hits.Count > 0)
        {
            Ui.Info("/resume ID opens one.");
        }
    }

    private void PermissionsCommand(string arg)
    {
        var words = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var rules = rt.Permissions.Rules;
        if (words is [var verb, var rule] && verb is "allow" or "deny" or "remove")
        {
            rule = rule.Trim();
            if (verb != "remove" && !PermissionRules.Valid(rule))
            {
                Ui.Error("A rule is a tool's name, with a pattern in brackets if any: run_shell(npm test*), edit_file, read_file(.env*), mcp__tickets__*.");
                return;
            }
            var said = rt.KeepRule(verb, rule);
            Ui.Line(said);
            return;
        }
        Ui.Line(Ui.Bold("Allowed without asking") + (rules.Allow.Count == 0 ? Ui.Dim("  (none)") : "\n" + string.Join('\n', rules.Allow.Select(r => "  " + r))));
        Ui.Line(Ui.Bold("Never") + (rules.Deny.Count + rules.ProjectDeny.Count == 0 ? Ui.Dim("  (none)") : "\n" + string.Join('\n', rules.Deny.Select(r => "  " + r).Concat(rules.ProjectDeny.Select(r => "  " + r + Ui.Dim("  (the project's .arena/settings.json)"))))));
        Ui.Info("/permissions allow RULE, deny RULE or remove RULE: kept in config.json. A command Laya flags still asks.");
    }

    /// <summary>The groups /skills lists, as the IDE's Skills view has them.</summary>
    private static readonly (string Source, string Title)[] SkillGroups = [("project", "This project"), ("person", "Yours"), ("arena", "From Arena"), ("builtin", "Built in")];

    /// <summary>
    /// /skills: every skill the agent reads, by whose it is, as the IDE's Skills view groups them (Arena's asked for at once);
    /// /skills new NAME makes one for the project, /skills new NAME --mine one of yours (in Arena when signed in to it, else
    /// in the config folder), from a few lines to fill in; /skills export and /skills import move them as files.
    /// </summary>
    private async Task SkillsCommandAsync(string arg, CancellationToken ct)
    {
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words is ["export", ..] or ["import", ..])
        {
            try
            {
                await (words[0] == "export" ? ExportSkillsAsync(words[1..], ct) : ImportSkillsAsync(words[1..], ct));
            }
            catch (IdeError e)
            {
                Ui.Error(e.Message);
            }
            return;
        }
        if (words is ["new", ..])
        {
            var mine = words.Contains("--mine");
            var name = string.Join(' ', words.Skip(1).Where(w => w != "--mine"));
            try
            {
                await IdeSkills.CreateAsync(rt, new JsonObject { ["name"] = name, ["scope"] = mine ? "person" : "project" }, ct);
            }
            catch (IdeError e)
            {
                Ui.Error(e.Message);
                return;
            }
            Ui.Line(!mine ? $"Made /{name}: write its steps in {rt.Workspace.Show(Path.Combine(rt.ProjectRoot, ".arena", "skills", name, "SKILL.md"))} (commit it to share it with the team)."
                : rt.MineInArena ? $"/{name} is one of your skills in Arena now (the web chat and Code Arena everywhere use it): write its steps on Arena's Skills page ({rt.Config.Url!.TrimEnd('/')}/skills) or in the IDE's Skills view."
                : $"Made /{name}, yours in every project on this computer: write its steps in {Path.Combine(rt.Env.Paths.ConfigDir, "skills", name, "SKILL.md")}.");
            return;
        }
        if (words.Length > 0)
        {
            Ui.Warn("/skills lists them; /skills new NAME makes one for the project, /skills new NAME --mine one of yours; " +
                "/skills export NAME… [--to FILE] and /skills import PATH [--mine] [--replace|--rename|--skip] move them as files.");
            return;
        }
        var skills = (await rt.ReloadExtensionsAsync(true, ct)).Skills;
        if (skills.Count == 0)
        {
            Ui.Info("No skills yet: /skills new NAME makes one (or .arena/skills/NAME/SKILL.md by hand); the agent keeps one it worked out with skill_save.");
            return;
        }
        foreach (var (source, title) in SkillGroups)
        {
            var these = skills.Where(s => IdeSkills.Whose(rt, s) == source).ToList();
            if (these.Count == 0)
            {
                continue;
            }
            Ui.Line(Ui.Bold(title));
            foreach (var s in these)
            {
                var from = s.Origin is { } o && o is not ("arena" or "builtin" or "config") ? Ui.Dim($" ({o})") : "";
                Ui.Line($"  /{s.Name}{from}  {Ui.Dim(s.Description)}");
            }
        }
        Ui.Info("/NAME and your words uses one (the model also loads one when a task needs it); /skills new NAME makes one; /skills export and /skills import move them as files.");
    }

    /// <summary>A path the person typed: ~ their home, relative to the working directory.</summary>
    private string Typed(string path) =>
        Path.GetFullPath(path.StartsWith('~') && rt.Env.Env("HOME") is { Length: > 0 } home ? home + path[1..] : path, rt.Workspace.Root);

    /// <summary>/skills export NAME… [--to FILE]: the skills as a .zip of their folders (NAME.zip or skills.zip in the working directory).</summary>
    private async Task ExportSkillsAsync(string[] words, CancellationToken ct)
    {
        var at = Array.IndexOf(words, "--to");
        var to = at >= 0 && at + 1 < words.Length ? words[at + 1] : null;
        var names = words.Where((w, i) => w != "--to" && (at < 0 || i != at + 1)).ToList();
        var skills = await SkillTransfer.NamedAsync(rt, names, ct);
        var file = Typed(to ?? (skills is [var one] ? one.Name : "skills") + ".zip");
        if (Directory.Exists(file))
        {
            file = Path.Combine(file, (skills is [var only] ? only.Name : "skills") + ".zip");
        }
        await File.WriteAllBytesAsync(file, SkillTransfer.Zip(skills), ct);
        Ui.Line($"Exported {string.Join(", ", skills.Select(s => "/" + s.Name))} to {rt.Workspace.Show(file)}: each a folder with its SKILL.md, as Claude Code keeps them.");
    }

    /// <summary>
    /// /skills import PATH [--mine] [--replace|--rename|--skip]: a .zip, a SKILL.md or a folder, for the project (or for you),
    /// each skill shown first; one whose name is taken is asked about (or done as the flag says; skipped when it cannot ask).
    /// </summary>
    private async Task ImportSkillsAsync(string[] words, CancellationToken ct)
    {
        var flags = words.Where(w => w.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var path = string.Join(' ', words.Where(w => !w.StartsWith("--", StringComparison.Ordinal)));
        if (path.Length == 0)
        {
            Ui.Error("/skills import PATH: a .zip of skills, a SKILL.md, or a folder with skills in it.");
            return;
        }
        var scope = flags.Contains("--mine") ? "person" : "project";
        var (folders, leftOut) = SkillTransfer.Read(SkillTransfer.FromDisk(Typed(path)));
        var preview = await SkillTransfer.PreviewAsync(rt, folders, leftOut, scope, ct);
        var choices = new JsonArray();
        foreach (var s in (preview["skills"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var (name, folder) = (s.Str("name") ?? "", s.Str("folder") ?? "");
            var files = (s["files"] as JsonArray)?.Count ?? 0;
            Ui.Line($"  /{name}{(files > 0 ? Ui.Dim($" (with {files} file{(files == 1 ? "" : "s")})") : "")}  {Ui.Dim(s.Str("description") ?? "")}");
            if (s.Str("problem") is { } problem)
            {
                Ui.Warn($"    left out: {problem}");
                continue;
            }
            if (s["clash"] is null && s.Str("nameProblem") is null)
            {
                continue;
            }
            var why = s.Str("nameProblem") ?? $"There is a /{name} already.";
            var action = flags.Contains("--replace") && s["clash"] is not null ? "replace" : flags.Contains("--rename") ? "rename" : flags.Contains("--skip") ? "skip" : null;
            if (action is null && Ui.CanAsk)
            {
                var said = Ui.ReadLine($"    {why} {(s["clash"] is not null ? "[r]eplace, re[n]ame or [s]kip" : "re[n]ame or [s]kip")}? ")?.Trim().ToLowerInvariant();
                action = said is "r" or "replace" && s["clash"] is not null ? "replace" : said is "n" or "rename" ? "rename" : "skip";
            }
            choices.Add(new JsonObject { ["folder"] = folder, ["action"] = action ?? "skip", ["name"] = action == "rename" ? s.Str("suggested") : null });
        }
        if (preview.Str("note") is { } note)
        {
            Ui.Info(note);
        }
        var done = await SkillTransfer.ImportAsync(rt, folders, scope, choices, ct);
        var imported = (done["imported"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        foreach (var s in (done["skipped"] as JsonArray ?? []).OfType<JsonObject>())
        {
            Ui.Info($"/{s.Str("name")} not imported: {s.Str("reason")}");
        }
        if (imported.Count == 0)
        {
            Ui.Info("Nothing was imported.");
            return;
        }
        var where = scope == "project" ? "for this project (.arena/skills: commit them to share them)" : rt.MineInArena ? "as yours in Arena (the web chat and every Code Arena)" : "as yours on this computer";
        Ui.Line($"Imported {string.Join(", ", imported.Select(i => "/" + i.Str("name") + (i.Bool("replaced") == true ? " (replaced)" : "")))} {where}.");
    }

    private void ExtensionsCommand()
    {
        var x = rt.Extensions;
        Ui.Line(Ui.Bold("Commands") + (x.Commands.Count == 0 ? Ui.Dim("  (none: .arena/commands/NAME.md, or in your config folder)") : ""));
        foreach (var c in x.Commands)
        {
            Ui.Line($"  /{c.Name}{(c.ArgumentHint is { } hint ? " " + hint : "")}  {Ui.Dim(c.Description)}");
        }
        Ui.Line(Ui.Bold("Sub-agents") + (x.Agents.Count == 0 ? Ui.Dim("  (none: .arena/agents/NAME.md)") : ""));
        foreach (var a in x.Agents)
        {
            Ui.Line($"  {a.Name}  {Ui.Dim(a.Description)}");
        }
        Ui.Line(Ui.Bold("Skills") + (x.Skills.Count == 0 ? Ui.Dim("  (none: .arena/skills/NAME/SKILL.md)") : ""));
        foreach (var s in x.Skills)
        {
            Ui.Line($"  {s.Name}  {Ui.Dim(s.Description)}");
        }
    }

    /// <summary>
    /// /compact-at: shows when the session compacts; "70" sets the threshold, "70 30" what is kept too, "off" only when a
    /// request would not fit; "account" follows the Arena account's again, "default" that and 25% kept.
    /// </summary>
    private void CompactAt(string arg)
    {
        if (arg.Length == 0)
        {
            Ui.Line($"It {rt.Compaction.Describe(rt.CompactAtFrom)}. " + ContextUse() + ".");
            Ui.Info("Set with /compact-at 70 (the threshold), /compact-at 70 30 (and what is kept) or /compact-at off (only when a request would not fit); " +
                "/compact-at account for your Arena account's (Your account → Answers), /compact-at default for that and 25. Kept in config.json.");
            return;
        }
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? at, target;
        var follow = false;
        if (words is ["default"])
        {
            (at, target, follow) = (null, Compaction.DefaultTarget, true);
        }
        else if (words is ["account"])
        {
            (at, target, follow) = (null, null, true);
        }
        else
        {
            at = Compaction.Percent(words[0]);
            target = words.Length > 1 ? Compaction.Percent(words[1]) : null;
            if (at is null || (words.Length > 1 && target is null) || words.Length > 2)
            {
                Ui.Error("Give it as a share of the window: /compact-at 70, /compact-at 70 30, or /compact-at off.");
                return;
            }
        }
        if (rt.SetCompaction(at, target, follow) is { } wrong)
        {
            Ui.Error(wrong);
            return;
        }
        Ui.Line($"It {rt.Compaction.Describe(follow ? rt.CompactAtFrom : null)} ({(follow ? "config.json no longer sets when" : "kept in config.json")}).");
    }

    /// <summary>/providers: those beside Arena's gateway (list), add NAME [URL] [anthropic|openai] (the key asked, hidden), remove NAME.</summary>
    private async Task ProvidersAsync(string arg, CancellationToken ct)
    {
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (words)
        {
            case [] or ["list"]:
                if (rt.ProvidersRefused)
                {
                    Ui.Info("Arena's admins do not let Code Arena use other providers (Admin → Settings → Code Arena).");
                }
                foreach (var p in rt.Config.Providers)
                {
                    var keyAt = Providers.KeyOf(p, rt.Env.Env) is null ? Ui.Yellow("no key") : p.ApiKey is { Length: > 0 } ? "key in config.json" : $"key from {p.KeyEnv}";
                    var n = rt.Models.Count(m => m.Id.StartsWith(p.Name + ":", StringComparison.Ordinal));
                    Ui.Line($"  {p.Name,-12} {p.Kind,-9} {p.Url}  {keyAt}  {n} model{(n == 1 ? "" : "s")}");
                }
                if (rt.Config.Providers.Count == 0)
                {
                    Ui.Info("None: Arena's models only.");
                }
                Ui.Info("/providers add NAME [URL] [anthropic|openai] adds one (known: " + string.Join(", ", Providers.Known.Keys) + "); its models are NAME:model in /model.");
                break;
            case ["add", var name, .. var rest]:
                var url = rest.FirstOrDefault(w => w.Contains("://", StringComparison.Ordinal));
                var kind = rest.FirstOrDefault(w => w is "anthropic" or "openai");
                var known = Providers.Known.TryGetValue(name, out var k) ? k : default;
                string? key = null;
                if (known.KeyEnv is not { } env || rt.Env.Env(env) is not { Length: > 0 })
                {
                    key = Ui.ReadSecret($"{name}'s API key (kept in config.json, readable by you only; Enter: none{(known.KeyEnv is { } e2 ? $", then set {e2}" : "")}): ");
                }
                try
                {
                    Ui.Info(await rt.AddProviderAsync(name, url, kind, key, ct));
                }
                catch (InvalidOperationException e)
                {
                    Ui.Error(e.Message);
                }
                break;
            case ["remove", var gone]:
                if (rt.RemoveProvider(gone))
                {
                    Ui.Info($"{gone} is taken out (its key with it).");
                }
                else
                {
                    Ui.Error($"There is no provider {gone}: /providers lists them.");
                }
                break;
            default:
                Ui.Error("/providers, /providers add NAME [URL] [anthropic|openai], /providers remove NAME.");
                break;
        }
    }

    private void ChooseModel(string arg)
    {
        if (arg.Length == 0)
        {
            for (var i = 0; i < rt.Models.Count; i++)
            {
                var m = rt.Models[i];
                var current = m.Id == rt.Model.Name ? Ui.Green(" ◀ now") : "";
                Ui.Line($"{i + 1,3}. {m.Id} {Ui.Dim(m.Context is { } c ? $"{Fmt.Tokens(c)} tokens" : "")}{current}");
            }
            arg = Ui.ReadLine("Which one (number or name, Enter keeps it)? ")?.Trim() ?? "";
            if (arg.Length == 0)
            {
                return;
            }
        }
        var chosen = int.TryParse(arg, out var n) && n >= 1 && n <= rt.Models.Count ? rt.Models[n - 1]
            : rt.Models.FirstOrDefault(m => string.Equals(m.Id, arg, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            Ui.Error($"There is no model {arg}: /model lists them.");
            return;
        }
        rt.SwitchModel(chosen);
        Ui.Line($"Model: {chosen.Id} ({Fmt.Tokens(rt.Model.Context)} tokens).");
    }
}
