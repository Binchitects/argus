using System.Text;

namespace CodeArena;

/// <summary>The interactive session: the prompt, turns, and the slash commands.</summary>
internal sealed class Repl(Runtime rt)
{
    /// <summary>
    /// The prompt edited key by key, with what was sent before on ↑, when someone types at a terminal that can
    /// take it; lines as they come otherwise (a pipe, a dumb terminal).
    /// </summary>
    private readonly LineEditor? _editor = rt.Env.Keys is { } keys && rt.Ui.CanAsk && !rt.Ui.Quiet && rt.Env.Env("TERM") != "dumb"
        ? new LineEditor(keys, rt.Ui.Out, () => rt.History.Entries()) { Interrupted = rt.Env.Cancel.Press }
        : null;

    // What the person typed while a turn ran (whole lines, and a line begun): taken when it ends.
    private readonly Queue<string> _typed = new();
    private string _ahead = "";
    // The running turn's token: a question waiting for its answer ends when the turn is stopped.
    private CancellationToken? _turnStop;

    private Ui Ui => rt.Ui;

    public static readonly (string Name, string What)[] Commands =
    [
        ("/help", "these commands"),
        ("/model [name]", "show or switch the model"),
        ("/mode [mode]", "show or switch the mode: ask, auto-edit, plan, yolo"),
        ("/thinking [level]", "show or set thinking: default, off, low, medium, high, xhigh"),
        ("/tools", "the tools this session has"),
        ("/todo", "the to-do list"),
        ("/compact", "summarize the conversation to free the model's window"),
        ("/compact-at [% [%]]", "show or set when it compacts itself (80% of the window) and what it keeps (25%)"),
        ("/context", "how full the model's window is"),
        ("/cost", "tokens spent, and how full the window is"),
        ("/mcp [retry]", "Arena's, Argus's and your MCP servers: connected or not; retry tries now"),
        ("/jobs [stop N]", "the commands run with no time limit or in the background; stop N stops job N"),
        ("/clear", "start a new session (this one stays saved)"),
        ("/resume [id]", "switch to a saved session"),
        ("/web [N]", "your chats in Arena; /web N continues one here (what is added goes back to it)"),
        ("/memory [add|forget]", "what it remembers; add TEXT (this project), add --all TEXT (every project), forget N"),
        ("/rewind [N] [code|chat]", "the turns kept; /rewind N goes back to before turn N (its files and the conversation)"),
        ("/search WORDS", "earlier sessions in this folder that said these words"),
        ("/permissions [allow|deny|remove RULE]", "kept rules, e.g. run_shell(npm test*), edit_file, read_file(.env*)"),
        ("/sandbox", "what commands in the sandbox may do"),
        ("/commands", "your own and the project's commands, sub-agents and skills"),
        ("/sync", "where this session stands with its chat in Arena"),
        ("/exit", "leave (also Ctrl+D, or Ctrl+C twice)"),
    ];

    public async Task<int> RunAsync(string? first, CancellationToken ct)
    {
        Banner();
        if (_editor is not null)
        {
            // A command's output while the person types goes above the prompt, which is drawn again under it.
            Ui.Interject = _editor.Interject;
            // A question's answer is read by the prompt's own editor, never by a second reader of the same keys: what was
            // typed while the model worked stays for the next message, and Ctrl+C at the question stops the turn.
            Ui.AnswerReader = prompt =>
            {
                KeepTypedAhead();
                return _editor.Read(prompt, Ui.Dim("… "), wake: () => _turnStop?.IsCancellationRequested == true, answer: true);
            };
        }
        Ui.NotAnAnswer = text =>
        {
            _typed.Enqueue(text);
            Ui.Info($"Not an answer, so a no: \"{Fmt.OneLine(text, 60)}\" is kept as your next message.");
        };
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
            string? input;
            if (pending is null)
            {
                // What the servers say while the person types waits for the line: it would break into what they write.
                rt.AtPrompt = true;
                try
                {
                    input = ReadInput();
                }
                finally
                {
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
            Ui.Line($"  {Ui.Dim("resumed")} {rt.Session.Id}, {rt.Agent.Messages.Count} messages");
        }
        if (rt.Permissions.Mode == Mode.Yolo)
        {
            Ui.Warn("yolo: edits and commands run without asking. Use it in a folder you can throw away.");
        }
        Ui.Info(_editor is null
            ? "/help for commands · Ctrl+C stops a turn · end a line with \\ to go on to the next"
            : "/help for commands · ↑ for what you sent before · Ctrl+C stops a turn · end a line with \\ to go on to the next");
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
        var line = new StringBuilder(_ahead);
        while (keys.KeyAvailable && keys.ReadKey() is { } key)
        {
            if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
            {
                if (line.Length > 0 && line[^1] == '\\')
                {
                    // A line ended with \ goes on to the next, as at the prompt.
                    line.Length--;
                    line.Append('\n');
                    continue;
                }
                if (line.ToString().Trim() is { Length: > 0 } text)
                {
                    _typed.Enqueue(text);
                    Ui.Info($"Taken when this turn ends: {Fmt.OneLine(text, 60)}");
                }
                line.Clear();
            }
            else if (key.Key == ConsoleKey.Backspace)
            {
                if (line.Length > 0)
                {
                    line.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                line.Append(key.KeyChar);
            }
        }
        _ahead = line.ToString();
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
    private async Task TurnAsync(string? input, CancellationToken ct)
    {
        if (input is null)
        {
            var ended = rt.Jobs.Follow().Select(j => $"job {j.Id}").ToList();
            if (ended.Count == 0)
            {
                return;
            }
            Ui.Line();
            Ui.Info($"{string.Join(", ", ended)} ended: the model carries on from {(ended.Count == 1 ? "it" : "them")}.");
        }
        if (_ran.Count > 0)
        {
            input = string.Join("\n\n", _ran) + "\n\n" + input;
            _ran.Clear();
        }
        var turn = new Spend();
        rt.Turn = turn;
        using var key = rt.Env.Cancel.BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token);
        _turnStop = linked.Token;
        try
        {
            await rt.Agent.RunAsync(input, turn, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Ui.Warn("Stopped.");
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

    /// <summary>"context 18.2k / 131k (14%), compacts at 80%".</summary>
    private string ContextUse()
    {
        var used = rt.Agent.Estimate();
        return $"context {Fmt.Tokens(used)} / {Fmt.Tokens(rt.Model.Context)} ({100.0 * used / rt.Model.Context:0}%), compacts at {rt.Compaction.At}%";
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
                foreach (var (command, what) in Commands)
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
            case "/tools":
                var width = rt.Tools.All.Max(t => t.Name.Length);
                foreach (var group in rt.Tools.All.GroupBy(t => t.Server))
                {
                    Ui.Line(Ui.Bold(group.Key is null ? "Local" : rt.Links.FirstOrDefault(l => l.Name == group.Key)?.Title ?? group.Key));
                    foreach (var t in group)
                    {
                        var mark = t.Kind switch
                        {
                            ToolKind.Edit => Ui.Yellow("edits "),
                            ToolKind.Shell => Ui.Yellow("runs commands "),
                            ToolKind.Remote when !t.Trusted && !t.ReadOnly => Ui.Yellow("asks "),
                            _ => "",
                        };
                        Ui.Line($"  {t.Name.PadRight(width)}  {mark}{Ui.Dim(Fmt.OneLine(t.Description, 80))}");
                    }
                }
                if (!rt.ArenaConnected && rt.Config.ArenaTools)
                {
                    Ui.Info("Arena's tools are not connected in this session.");
                }
                break;
            case "/todo":
                Ui.Line(LocalTools.RenderTodos(rt.Context.Todos, Ui));
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
                    catch (Exception e) when (e is GatewayException or HttpRequestException)
                    {
                        Ui.Error(e.Message);
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
                Ui.Line($"The window: about {Fmt.Tokens(used)} of {Fmt.Tokens(rt.Model.Context)} tokens ({100.0 * used / rt.Model.Context:0}%); {rt.Compaction.Describe()}.");
                break;
            case "/context":
                Ui.Line(ContextUse() + $", keeping the recent part within {rt.Compaction.Target}%.");
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
            case "/memory":
                MemoryCommand(arg);
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
            case "/sync":
                Ui.Line(rt.Sync?.Describe() ?? "Sessions are not kept with Arena here (\"syncChats\": false in config.json, or no Arena address).");
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
                    Ui.Line($"Resumed {rt.Session.Id}: {rt.Agent.Messages.Count} messages, model {rt.Model.Name}.");
                }
                else
                {
                    Ui.Error($"No saved session {id}.");
                }
                break;
            default:
                if (rt.Extensions.Expand(input) is { } prompt)
                {
                    await TurnAsync(prompt, ct);
                    break;
                }
                Ui.Error($"There is no command {name}: /help lists them (and /commands your own).");
                break;
        }
        return true;
    }

    private void MemoryCommand(string arg)
    {
        var words = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (words is ["add", var rest])
        {
            var person = rest.StartsWith("--all ", StringComparison.Ordinal);
            var text = person ? rest[6..].Trim() : rest.Trim();
            Ui.Line(rt.Memory.Add(person, text) ? $"Remembered {(person ? "for every project" : "for this project")}: it is in the next sessions' prompt." : "Already remembered.");
            return;
        }
        if (words is ["forget", var which])
        {
            var person = which.StartsWith("--all ", StringComparison.Ordinal);
            var n = int.TryParse(person ? which[6..] : which, out var k) ? k : 0;
            Ui.Line(rt.Memory.Forget(person, n) is { } gone ? $"Forgotten: {gone}" : $"There is no memory {which}: /memory lists them.");
            return;
        }
        foreach (var (person, title) in new[] { (false, "This project"), (true, "Every project (/memory add --all, forget --all N)") })
        {
            var all = rt.Memory.Read(person);
            Ui.Line(Ui.Bold(title) + Ui.Dim($"  {rt.Memory.FileOf(person)}"));
            Ui.Line(all.Count == 0 ? Ui.Dim("  (nothing yet)") : string.Join('\n', all.Select((m, i) => $"  {i + 1,2}. {m}")));
        }
        Ui.Info("The model keeps memories with remember; /memory add TEXT adds one, /memory forget N forgets one. The files are plain Markdown to edit.");
    }

    private async Task RewindCommandAsync(string arg, CancellationToken ct)
    {
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var turns = rt.Checkpoints.Turns;
        if (words.Length == 0 || !int.TryParse(words[0], out var n))
        {
            if (turns.Count == 0)
            {
                Ui.Info("No turn kept yet: each turn leaves a checkpoint while Code Arena runs.");
                return;
            }
            for (var i = 0; i < turns.Count; i++)
            {
                Ui.Line($"  {i + 1,2}. {turns[i].At:HH:mm}  {Fmt.OneLine(turns[i].Input, 70)}  {Ui.Dim($"{turns[i].Before.Count} files")}");
            }
            Ui.Info("/rewind N goes back to before turn N: the files its tools and later turns' wrote, and the conversation. /rewind N code: only the files; /rewind N chat: only the conversation.");
            return;
        }
        var only = words.Length > 1 ? words[1].ToLowerInvariant() : null;
        Ui.Line(await rt.RewindAsync(n, files: only is null or "code", chat: only is null or "chat", ct));
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

    /// <summary>/compact-at: shows when the session compacts; "70" sets the threshold, "70 30" what is kept too, "default" both back.</summary>
    private void CompactAt(string arg)
    {
        if (arg.Length == 0)
        {
            Ui.Line($"It {rt.Compaction.Describe()}. " + ContextUse() + ".");
            Ui.Info("Set with /compact-at 70 (the threshold) or /compact-at 70 30 (and what is kept); /compact-at default for 80 and 25. Kept in config.json.");
            return;
        }
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int? at, target;
        if (words is ["default"])
        {
            (at, target) = (Compaction.DefaultAt, Compaction.DefaultTarget);
        }
        else
        {
            at = Compaction.Percent(words[0]);
            target = words.Length > 1 ? Compaction.Percent(words[1]) : null;
            if (at is null || (words.Length > 1 && target is null) || words.Length > 2)
            {
                Ui.Error("Give it as a share of the window: /compact-at 70, or /compact-at 70 30.");
                return;
            }
        }
        if (rt.SetCompaction(at, target) is { } wrong)
        {
            Ui.Error(wrong);
            return;
        }
        Ui.Line($"It {rt.Compaction.Describe()} (kept in config.json).");
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
