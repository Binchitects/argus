using System.Text;

namespace CodeArena;

/// <summary>The interactive session: the prompt, turns, and the slash commands.</summary>
internal sealed class Repl(Runtime rt)
{
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
        ("/jobs", "the commands running with no time limit"),
        ("/clear", "start a new session (this one stays saved)"),
        ("/resume [id]", "switch to a saved session"),
        ("/exit", "leave (also Ctrl+D, or Ctrl+C twice)"),
    ];

    public async Task<int> RunAsync(string? first, CancellationToken ct)
    {
        Banner();
        var pending = first;
        while (!ct.IsCancellationRequested)
        {
            rt.SayLater();
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
            if (input is null)
            {
                // A read cut short by Ctrl+C (Windows) is not the end of input.
                if (DateTime.UtcNow - rt.Env.Cancel.LastPress < TimeSpan.FromSeconds(1))
                {
                    continue;
                }
                break;
            }
            input = input.Trim();
            if (input.Length == 0)
            {
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
        Ui.Info("/help for commands · Ctrl+C stops a turn · end a line with \\ to go on to the next");
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
        var line = Ui.ReadLine(Ui.Cyan("› "));
        if (line is null)
        {
            return null;
        }
        var sb = new StringBuilder();
        while (line.EndsWith('\\'))
        {
            sb.Append(line[..^1]).Append('\n');
            line = Ui.ReadLine(Ui.Dim("… ")) ?? "";
        }
        return sb.Append(line).ToString();
    }

    private async Task TurnAsync(string input, CancellationToken ct)
    {
        var turn = new Spend();
        rt.Turn = turn;
        using var key = rt.Env.Cancel.BeginTurn();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, key.Token);
        try
        {
            await rt.Agent.RunAsync(input, turn, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Ui.Warn("Stopped.");
        }
        catch (Exception e) when (e is GatewayException or HttpRequestException or IOException)
        {
            Ui.Error(e.Message);
        }
        finally
        {
            rt.Env.Cancel.EndTurn();
            rt.Turn = null;
        }
        if (turn.Requests > 0)
        {
            Ui.Info(turn.Describe(Ui) + " · " + ContextUse());
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
                if (rt.Jobs.All.Count == 0)
                {
                    Ui.Info("No commands with no time limit in this session.");
                }
                foreach (var job in rt.Jobs.All)
                {
                    Ui.Line($"  job {job.Id}  {Fmt.OneLine(job.Command, 70)}  {Ui.Dim(job.Status())}");
                }
                break;
            case "/clear":
                var previous = rt.Session.Id;
                rt.NewSession();
                Ui.Line($"A new session. The last one is saved as {previous}.");
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
                Ui.Error($"There is no command {name}: /help lists them.");
                break;
        }
        return true;
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
