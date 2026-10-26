using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A session put together: the config, the gateway and its models, the tools (local and MCP), the agent.</summary>
internal sealed partial class Runtime : IAsyncDisposable
{
    /// <summary>How long a session waits, as it starts, for its MCP servers' first answers: the rest join in the background.</summary>
    public static TimeSpan StartWait { get; set; } = TimeSpan.FromSeconds(3);

    private readonly List<ServerLink> _links = [];
    private readonly object _serversGate = new();
    private IReadOnlyList<(string Server, string Text)> _instructions = [];

    private Runtime(Config config, Ui ui, CliEnv env, HttpClient http, GatewayClient gateway)
    {
        Config = config;
        Ui = ui;
        Env = env;
        Http = http;
        Gateway = gateway;
    }

    public Config Config { get; }
    public Ui Ui { get; }
    public CliEnv Env { get; }
    public HttpClient Http { get; }
    public GatewayClient Gateway { get; }
    public List<ModelInfo> Models { get; private set; } = [];
    public ModelState Model { get; private set; } = null!;
    public Permissions Permissions { get; private set; } = null!;
    public ToolBox Tools { get; private set; } = null!;
    public ToolContext Context { get; private set; } = null!;
    public Agent Agent { get; private set; } = null!;
    public Workspace Workspace { get; private set; } = null!;
    public SessionStore Session { get; private set; } = null!;
    /// <summary>The person's and the project's own commands, sub-agents and skills.</summary>
    public Extensions Extensions { get; private set; } = new();
    /// <summary>What the agent remembers; the prompt has it as the session started.</summary>
    public MemoryStore Memory { get; private set; } = null!;
    private string _memory = "";

    /// <summary>A newer Code Arena the Arena serves, as said at the start (the IDE's status bar shows it); null: none.</summary>
    public string? UpdateNotice { get; private set; }

    /// <summary>The project's long memory (.remember/) as the session starts: read anew for each new or resumed session.</summary>
    private string LongMemory() => Config.ProjectMemory ? ProjectMemory.ForPrompt(ProjectMemory.Root(Workspace.Root)) : "";
    /// <summary>A checkpoint before each turn, for /rewind.</summary>
    public Checkpoints Checkpoints { get; } = new();
    /// <summary>The sandbox the agent's commands run in.</summary>
    public Sandbox Sandbox { get; private set; } = Sandbox.None;
    /// <summary>The session kept in step with its chat in Arena; null when chats are not synced.</summary>
    public ChatSync? Sync { get; private set; }
    /// <summary>Whose chats in Arena the sessions are kept with (the key's account), once Arena said; null until then or without sync.</summary>
    public string? Account { get; private set; }

    private Action? _webNews;

    /// <summary>
    /// Told when the web chat has news while the session is kept with it (asked every few seconds): the terminal's
    /// prompt and the IDE take it in at once when nothing runs. Null (a one-shot run): it comes at the next turn.
    /// </summary>
    public Action? WebNews
    {
        get => _webNews;
        set
        {
            _webNews = value;
            if (Sync is not null)
            {
                Sync.OnNews = value;
            }
        }
    }
    /// <summary>What was sent in this folder, in the terminal and the IDE's chat alike.</summary>
    public InputHistory History { get; private set; } = null!;
    public Spend Total { get; } = new();
    /// <summary>The turn running now, so a sub-agent's tokens count in it.</summary>
    public Spend? Turn { get; set; }
    /// <summary>The commands running with no time limit, and those that ended.</summary>
    public CommandJobs Jobs { get; } = new();

    /// <summary>
    /// Nobody types a next message (a one-shot run, the end of piped input): the commands with no time limit the model
    /// started are waited for, and it carries on from each as it ends, until none runs (or it carried on as often as it may
    /// with no message). The last answer it gave, or null.
    /// </summary>
    public async Task<string?> FinishJobsAsync(Spend turn, CancellationToken ct)
    {
        string? answer = null;
        while (Jobs.Waiting.Count > 0 || Jobs.HasNews)
        {
            if (Jobs.HasNews)
            {
                Jobs.Follow();
                var next = await Agent.ContinueAsync(turn, ct);
                if (Agent.FollowUpsSpent)
                {
                    break;
                }
                answer = next;
                continue;
            }
            var waiting = Jobs.Waiting;
            Ui.Info($"Waiting for {string.Join(", ", waiting.Select(j => $"job {j.Id} ({Fmt.OneLine(j.Command, 60)})"))} to end: no time limit, Ctrl+C stops {(waiting.Count == 1 ? "it" : "them")}.");
            await Jobs.WaitAnyAsync(ct);
        }
        return answer;
    }
    /// <summary>When the session compacts itself.</summary>
    public Compaction Compaction { get; } = new();

    /// <summary>Where the threshold comes from: "flag" (--compact-at), "config" (config.json), "account" (the person's choice in Arena), "arena" (Arena's default), "default".</summary>
    public string CompactAtSource { get; private set; } = "default";

    /// <summary>The threshold the Arena account gives (the person's own, else Arena's default), when Arena said at the start.</summary>
    public AccountCompaction? AccountCompactAt { get; private set; }

    /// <summary>Where the threshold comes from, as /compact-at and /context say it; null for the built-in default.</summary>
    public string? CompactAtFrom => CompactAtSource switch
    {
        "flag" => "set with --compact-at",
        "config" => "set in config.json",
        "account" => "from your Arena account",
        "arena" => "Arena's default",
        _ => null,
    };

    /// <summary>
    /// What the web chat offers the person, as Arena says it with the key (/api/code-arena/config): which models are loaded and
    /// see pictures, the thinking levels and their default, GitLab's address. Read at the start in the background; null
    /// until then, and when there is no Arena or it does not say.
    /// </summary>
    public JsonObject? ArenaChat { get; private set; }

    /// <summary>Whether a model sees pictures: as the gateway says, or as Arena's chat config does.</summary>
    public bool Sees(ModelInfo model) => model.Vision
        || ArenaChat?["models"] is JsonArray offered && offered.OfType<JsonObject>().Any(m => m.Str("name") == model.Id && m.Bool("vision") == true);

    /// <summary>The session's choice of remote tools (the groups turned off), and those loaded on demand.</summary>
    public ToolDemand Demand { get; } = new();

    /// <summary>
    /// How the model is given its tools now (it follows the model): config.json's toolMode, else Arena's word for the model
    /// (Admin → Models → Tools, in its chat config), else both.
    /// </summary>
    public string ToolMode => Config.ToolMode
        ?? ((ArenaChat?["models"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(m => m.Str("name") == Model?.Info.Id)?.Str("toolMode") is { } said
            && ToolBox.Modes.Contains(said) ? said : "both");

    /// <summary>Arena's word on one of its tools (title, description, icon), from its chat config; null when it has not said.</summary>
    private JsonObject? ArenaTool(string group) => (ArenaChat?["tools"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => t.Str("id") == group);

    /// <summary>A group of remote tools by name, for the person and the model: Arena's title for its tool, else the server's.</summary>
    public string ToolTitle(string group) => ArenaTool(group)?.Str("title")
        ?? (group.StartsWith("mcp:", StringComparison.Ordinal) ? group[4..] : group == ArgusName ? "Argus" : group == ArenaName ? "Arena" : group);

    /// <summary>What a group of remote tools is for: Arena's description of its tool, else which server's tools they are.</summary>
    public string ToolAbout(string group) => ArenaTool(group)?.Str("description")
        ?? (group.StartsWith("mcp:", StringComparison.Ordinal) ? $"The tools of the MCP server {group[4..]}." : $"{ToolTitle(group)}'s tools.");

    /// <summary>The icon Arena's chat shows for a group (search-code, globe...): a plug for a server of the person's.</summary>
    public string ToolIcon(string group) => ArenaTool(group)?.Str("icon") ?? "plug";

    /// <summary>Code Arena's note's list of the remote tools to load, a line per group; empty when every tool on is in the requests.</summary>
    private string ToolsToLoad()
    {
        var waiting = Tools.ToLoad();
        if (waiting.Count == 0)
        {
            return "";
        }
        return $"Call {ToolDemand.Function} with a tool's name (in brackets) or its functions' names, then call them. Load only what the task needs.\n" +
            string.Join("\n", waiting.GroupBy(t => t.Group!).Select(g =>
            {
                var about = ToolAbout(g.Key);
                about = about.Length <= 200 ? about : about[..200] + "…";
                return $"- {ToolTitle(g.Key)} [{g.Key}]: {about} Functions: {string.Join(", ", g.Select(t => t.Name))}.";
            }));
    }

    /// <summary>load_tools: the groups named join the requests from the next step on (and stay for the session).</summary>
    private ToolResult LoadTools(JsonObject args)
    {
        var names = args["names"] switch
        {
            JsonArray a => a.Select(n => n?.ToString() ?? ""),
            JsonValue v => v.ToString().Split(',', StringSplitOptions.TrimEntries),
            _ => [],
        };
        var (groups, unknown) = ToolDemand.Named(names, Tools.ToLoad(), ToolTitle);
        // A name already in the requests is no mistake: it is loaded.
        var offered = Tools.Offered();
        unknown.RemoveAll(n => offered.Any(t => t.Name == n.Trim() || t.Group == n.Trim() || (t.Group is { } g && string.Equals(ToolTitle(g), n.Trim(), StringComparison.OrdinalIgnoreCase))));
        var added = Demand.Load(groups);
        var functions = Tools.All.Where(t => t.Group is { } g && added.Contains(g)).Select(t => t.Name).ToList();
        var said = functions.Count > 0 ? $"Loaded: {string.Join(", ", functions)}. Call them from now on." : "Nothing new was loaded: what you asked for is already loaded.";
        if (unknown.Count > 0)
        {
            said += $" Not found: {string.Join(", ", unknown)}. The names are in the list under \"Tools to load\".";
        }
        return new ToolResult(said, functions.Count == 0 && unknown.Count > 0);
    }

    /// <summary>How long the start waits for Arena to say when the person's chats compact.</summary>
    private static readonly TimeSpan AccountWait = TimeSpan.FromSeconds(5);
    /// <summary>The session's MCP servers: Arena's, Argus's and the person's own, each connected in the background.</summary>
    public IReadOnlyList<ServerLink> Links => _links;
    /// <summary>Whether Arena's MCP endpoint answered and its tools are among the session's (set once they are, not before).</summary>
    public bool ArenaConnected => Volatile.Read(ref _arenaServing);
    private bool _arenaServing;
    /// <summary>Told when a server connects or is lost (on a background thread): the terminal says so at its next prompt, the IDE's page reads it.</summary>
    public Action<ServerLink>? ServersChanged { get; set; }

    public const string ArenaName = "arena";
    public const string ArgusName = "argus";

    /// <summary>Thrown for what stops a session before it starts, with what to do about it.</summary>
    public sealed class StartException(string message) : Exception(message);

    public static async Task<Runtime> StartAsync(Options o, CliEnv env, Ui ui, CancellationToken ct)
    {
        Config config;
        try
        {
            config = Config.Load(env.Paths.ConfigFile);
        }
        catch (InvalidOperationException e)
        {
            throw new StartException(e.Message);
        }
        config.ApplyEnvironment(env.Env);
        if (config.GatewayUrl is null || string.IsNullOrEmpty(config.ApiKey))
        {
            throw new StartException("Not signed in to an Arena yet. Run: code-arena login");
        }
        // Said now, not found later as a server that never connects.
        if (config.WrongAddress(env.Paths.ConfigFile) is { } wrong)
        {
            throw new StartException(wrong);
        }
        HttpClient http;
        try
        {
            http = Net.Client(Net.CaFile(o.Ca, config.Ca, env.Env));
        }
        catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException)
        {
            throw new StartException(e.Message);
        }
        var rt = new Runtime(config, ui, env, http, new GatewayClient(http, config.GatewayUrl, config.ApiKey));
        rt.Gateway.Retrying = said =>
        {
            rt.Notice(true, said);
            rt.Agent?.Events?.Notice(said);
        };
        try
        {
            await rt.InitAsync(o, ct);
        }
        catch
        {
            await rt.DisposeAsync();
            throw;
        }
        return rt;
    }

    private async Task InitAsync(Options o, CancellationToken ct)
    {
        Workspace = new Workspace(Env.Cwd, Config.AllowedPaths.Concat(o.AddDirs));
        History = new InputHistory(Env.Paths.HistoryDir, Workspace.Root);
        var mode = Modes.Parse(o.Mode ?? Config.Mode);
        if ((o.Mode ?? Config.Mode) is { } m && mode is null)
        {
            throw new StartException($"There is no mode {m}. The modes: {string.Join(", ", Modes.Names)}.");
        }
        // When it compacts: the flag, else config.json, else what the Arena account says (the web chat's own choice), else 80%.
        // Asked at every start (/compact-at account and the IDE say it); waited for only when it decides.
        var asking = ChatSync.CompactAtAsync(Config, Http, AccountWait, ct);
        var offered = ChatSync.ChatConfigAsync(Config, Http, AccountWait, ct);
        if (o.CompactAt is null && Config.CompactAt is null)
        {
            AccountCompactAt = await asking;
        }
        else
        {
            _ = asking.ContinueWith(t => AccountCompactAt = t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
        }
        (var at, CompactAtSource) = o.CompactAt is { } flag ? (flag, "flag")
            : Config.CompactAt is { } kept ? (kept, "config")
            : AccountCompactAt is { } account ? (account.At, account.Own ? "account" : "arena")
            : (Compaction.DefaultAt, "default");
        if (CompactAtSource is "account" or "arena")
        {
            // The account's threshold is no mistake of the person's here: what is kept comes down with it when too close, and
            // a threshold this version cannot take leaves the default.
            if (Compaction.Set(null, o.CompactTarget ?? Config.CompactTarget) is { } wrongTarget)
            {
                throw new StartException($"Compaction: {wrongTarget} (--compact-to, or compactTarget in {Env.Paths.ConfigFile})");
            }
            if (Compaction.Set(at, null) is not null)
            {
                CompactAtSource = "default";
            }
        }
        else if (Compaction.Set(at, o.CompactTarget ?? Config.CompactTarget) is { } wrong)
        {
            throw new StartException($"Compaction: {wrong} (--compact-at, --compact-to, or compactAt and compactTarget in {Env.Paths.ConfigFile})");
        }
        Permissions = new Permissions(Ui, mode ?? Mode.Ask);
        var project = SystemPrompt.GitRoot(Workspace.Root) ?? Workspace.Root;
        Permissions.Rules = PermissionRules.Of(Config, project);
        Permissions.Workspace = Workspace;
        Memory = new MemoryStore(Env.Paths, project) { Arena = Config.Url is { Length: > 0 } && Config.ApiKey is { Length: > 0 } ? new ArenaMemory(Config, Http) : null };
        _memory = await Memory.ForPromptAsync(Ui.Info, ct) + LongMemory();
        // A newer Code Arena on the Arena signed in to: looked for in the background, once a day, and said (not for -p).
        if (!o.Print && Config.UpdateCheck)
        {
            _ = Task.Run(async () =>
            {
                if (await Updates.NoticeAsync(Config, Http, Env.Paths, DateTimeOffset.UtcNow, ct) is { } said)
                {
                    UpdateNotice = said;
                    Ui.Info(said);
                }
            }, ct);
        }
        // The skills Arena's plugins bring, kept with the person's (the ones kept before when Arena does not answer); and
        // Arena's own, less those its admins turned off.
        var brought = await ChatSync.PluginSkillsAsync(Config, Http, AccountWait, ct);
        try
        {
            BuiltInSkills.Keep(Env.Paths, [.. (brought?["off"] as JsonArray ?? []).Select(n => n?.GetValue<string>() ?? "")], Config.BuiltInSkills);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Ui.Warn($"Arena's own skills could not be kept in {BuiltInSkills.Folder(Env.Paths)}: {e.Message}");
        }
        if (brought is not null)
        {
            try
            {
                foreach (var problem in PluginSkills.Keep(Env.Paths, brought))
                {
                    Ui.Warn(problem);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Ui.Warn($"The plugins' skills could not be kept in {PluginSkills.Folder(Env.Paths)}: {e.Message}");
            }
        }
        Extensions = Extensions.Load(Env.Paths, project);
        foreach (var problem in Extensions.Problems)
        {
            Ui.Warn(problem);
        }
        Sandbox = Sandbox.Choose(Config, Workspace, Env.Paths, Env.Env, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        if (Sandbox.Kind == Sandbox.Kinds.Off && Sandbox.Why is { } noSandbox)
        {
            Ui.Info($"Commands run without a sandbox here: {noSandbox}.");
        }
        // The terminal watches the commands with no time limit: their output as it comes, and how they ended.
        var printer = new JobPrinter(Ui);
        Jobs.Session = () => Session?.Id;
        Jobs.Started += printer.Started;
        Jobs.Output += printer.Output;
        Jobs.Ended += printer.Ended;
        var tools = LocalTools.All(Config.Shell);
        tools.Add(LocalTools.SubAgentTool());
        tools.Add(LocalTools.RunStepsTool());
        tools.Add(RunCodeTool(this));
        tools.Add(Screenshots.Tool(Env.Env));
        if (ProviderAgents.Tool(Config, Env.Env) is { } agents)
        {
            tools.Add(agents);
        }
        tools.Add(MemoryStore.RememberTool());
        tools.Add(MemoryStore.RecallTool());
        tools.AddRange(MemoryStore.HistoryTools());
        tools.Add(KeptResults.Tool());
        if (Config.ProjectMemory)
        {
            tools.Add(ProjectMemory.Tool());
        }
        tools.Add(SkillSave.Tool());
        tools.Add(SessionSearch.Tool());
        tools.AddRange(SessionHistory.Tools());
        if (Extensions.Skills.Count > 0)
        {
            tools.Add(Extensions.SkillTool());
        }
        Demand.Budget = Config.ToolChars ?? ToolDemand.DefaultBudget;
        Demand.Loader = LoadTools;
        Demand.Changed = (off, loaded) => Session?.Tools(off, loaded);
        Tools = new ToolBox(tools) { Demand = Demand, Mode = () => ToolMode };

        // The servers connect in the background; the session starts when the gateway has said which models there are.
        var models = Gateway.ModelsAsync(ct);
        AddLinks();
        var arena = _links.FirstOrDefault(l => l.Name == ArenaName);
        // A command waits for Arena's first answer, so Laya looks at it whenever Arena offers decide.
        Permissions.GuardReady = arena?.FirstTry ?? Task.CompletedTask;
        foreach (var link in _links)
        {
            link.Start();
        }
        try
        {
            Models = await models;
        }
        catch (Exception e) when (e is HttpRequestException or GatewayException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new StartException(e.Message);
        }
        if (Models.Count == 0)
        {
            throw new StartException("The gateway lists no models for your key. Ask your admin which models you may use.");
        }
        // A few seconds more for the servers (a run that cannot wait for them later, -p, gives them their whole handshake).
        var firstTries = Task.WhenAll(_links.Select(l => l.FirstTry));
        await Task.WhenAny(firstTries, Task.Delay(o.Print ? TimeSpan.FromSeconds(25) : StartWait, ct));
        foreach (var link in _links.Where(l => l.State == LinkState.Connecting))
        {
            Ui.Info($"{link.Title}'s tools are still connecting: they join the session when they answer.");
        }

        // A resumed session keeps its history, model and to-do list.
        SessionData? resumed = null;
        string? resumedFile = null;
        if (o.Resume || o.Continue)
        {
            resumedFile = o.ResumeId is { } id ? SessionStore.Find(Env.Paths.SessionsDir, id)
                : SessionStore.List(Env.Paths.SessionsDir, Workspace.Root, 1).FirstOrDefault()?.File;
            if (resumedFile is null)
            {
                if (o.ResumeId is not null)
                {
                    throw new StartException($"No saved session {o.ResumeId}. code-arena chat --resume lists them.");
                }
                Ui.Info("No earlier session in this folder: starting a new one.");
            }
            else
            {
                resumed = SessionStore.Load(resumedFile, whole: true);
            }
        }

        var wanted = o.Model ?? resumed?.Model ?? Config.Model;
        var info = Models.FirstOrDefault(x => x.Id == wanted);
        if (info is null)
        {
            if (o.Model is not null)
            {
                throw new StartException($"The gateway has no model {o.Model}. Yours: {string.Join(", ", Models.Select(x => x.Id))}");
            }
            // No model of its own: the one a new chat in Arena starts with, as Arena MCP says, else the gateway's first.
            var fallback = Models.FirstOrDefault(x => x.Id == arena?.Client?.DefaultModel) ?? Models[0];
            if (wanted is not null)
            {
                Ui.Warn($"{wanted} is not offered any more: using {fallback.Id}.");
            }
            info = fallback;
        }
        Model = new ModelState { Info = info, Thinking = o.Thinking ?? Config.Thinking, ContextOverride = Config.Context };
        // The web chat's offer, when Arena gives it: "default" thinking becomes the web's default level.
        _ = offered.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } chat)
            {
                Model.DefaultLevel = chat.Str("defaultThinking") is { } level && ModelState.Levels.Contains(level) ? level : null;
                ArenaChat = chat;
            }
        }, TaskScheduler.Default);

        var inputs = new SystemPrompt.Inputs
        {
            Workspace = Workspace,
            Paths = Env.Paths,
            Mode = () => Permissions.Mode,
            Model = () => Model.Name,
            Shell = Config.Shell,
            ArenaUrl = Config.Url,
            Ide = o.Ide,
            ServerInstructions = () => Volatile.Read(ref _instructions),
            HasArenaTools = () => ArenaConnected,
            ToolsToLoad = ToolsToLoad,
            ToolsInCode = ToolsInCode,
            Memory = () => _memory,
            Skills = () => [.. Extensions.Skills.Select(s => (s.Name, s.Description))],
            Agents = () => [.. Extensions.Agents.Select(a => (a.Name, a.Description))],
        };
        Context = new ToolContext
        {
            Workspace = Workspace, Ui = Ui, Shell = Config.Shell, Jobs = Jobs, Memory = Memory, SessionsDir = Env.Paths.SessionsDir, Sandbox = Sandbox,
            BeforeWrite = Checkpoints.BeforeWrite, Planning = () => Permissions.Mode == Mode.Plan,
            StepBegan = Checkpoints.BeginStep,
            // A step's check that is a command (its first word a program here) is run before the step is done; its run is
            // recorded on the step.
            IsCommand = check => IsCommand(check, Workspace.Root),
        };
        Context.CommandRan = (command, passed, output) =>
        {
            var plan = Context.Plan.Ran(command, passed, output, DateTimeOffset.UtcNow);
            if (!ReferenceEquals(plan, Context.Plan))
            {
                SetPlan(plan);
            }
        };
        // Whatever the agent commits is by Code Arena (its author), the person staying the committer.
        var harness = CommitIdentity.From(Config);
        Proc.Author = harness;
        Agent = new Agent
        {
            CommitAs = Config.AutoCommit ? harness : null,
            Checkpoints = Checkpoints,
            Gateway = Gateway,
            Tools = Tools,
            Permissions = Permissions,
            Context = Context,
            Ui = Ui,
            Model = Model,
            SystemPrompt = () => SystemPrompt.Build(inputs),
            MemoryChars = () => _memory.Length,
            Note = () => SystemPrompt.Note(inputs),
            Sees = () => Sees(Model.Info),
            Total = Total,
            Stream = !Ui.Quiet,
            Compaction = Compaction,
            // What is remembered that matters to each message (a few hundred tokens at most).
            Recall = (message, given, token) => Memory.RecallAsync(message, given, 1_500, token),
            // After a long turn: keep what it worked out, as a skill or in the project's memory.
            Nudge = "If it worked out a way of doing something worth doing again, keep it as a skill with skill_save" +
                (Config.ProjectMemory ? "; a lasting fact about this project, with remember_project." : "."),
        };
        Context.SubAgent = (description, prompt, agent, token) => RunSubAgentAsync(inputs, description, prompt, agent, token);
        Context.RunSteps = RunStepsAsync;
        Agent.Critique = Config.PlanCritique ? (plan, token) => CritiqueAsync(inputs, plan, token) : null;
        Agent.Ground = GroundAsync;
        _inputs = inputs;
        Context.Progress = said =>
        {
            Notice(false, said);
            Agent?.Events?.Notice(said);
        };
        Context.SessionId = () => Session.Id;
        Context.Store = () => Session;

        if (resumed is not null && resumedFile is not null)
        {
            Session = SessionStore.Open(resumedFile);
            Agent.Load(resumed.Messages, resumed.Known);
            Context.Plan = resumed.Plan;
            Demand.Restore(resumed.ToolsOff, resumed.ToolsLoaded);
            Total.Prompt = resumed.Prompt;
            Total.Cached = resumed.Cached;
            Total.Completion = resumed.Completion;
            if (resumed.Model != Model.Name)
            {
                Session.Model(Model.Name);
            }
        }
        else
        {
            Session = SessionStore.Create(Env.Paths.SessionsDir, Workspace.Root, Model.Name);
        }
        Agent.Session = Session;
        Context.PlanChanged = plan => Session.Plan(plan);
        if (o.Plan && !o.Resume && !o.Continue)
        {
            // code-arena --plan: the plan an earlier session left in progress, carried on in this one.
            try
            {
                Ui.Info(ResumePlan());
            }
            catch (PlanError e)
            {
                Ui.Warn(e.Message);
            }
        }
        Agent.BeforeTurn = TakeInForTurnAsync;
        Agent.AfterTurn = () => Sync?.EndTurn();
        StartSync();
        if (o.WebChat is { } web && await ContinueWebChatAsync(web, ct) is { } said)
        {
            Ui.Info(said);
        }
    }

    /// <summary>
    /// A turn starts: the web chat is this session's until it ends (the web queues the person's messages meanwhile), and
    /// what was added there comes in first.
    /// </summary>
    public async Task<Taken> TakeInForTurnAsync(CancellationToken ct)
    {
        if (Sync is not { } sync)
        {
            return Taken.None;
        }
        await sync.BeginTurnAsync(ct);
        return await sync.TakeInAsync(ct);
    }

    /// <summary>Keeps the session now in use in step with its chat in Arena (the last one's sync ends, sending what it still has).</summary>
    private void StartSync()
    {
        var previous = Sync;
        Sync = ChatSync.Start(Config, Http, Session, Workspace.Root, () => Model.Name, (warn, text) =>
        {
            Notice(warn, text);
            Agent?.Events?.Notice(text);
        }, () => Sees(Model.Info));
        if (Sync is not null)
        {
            Sync.OnNews = _webNews;
            if (Account is null)
            {
                _ = Task.Run(async () => Account = await ChatSync.WhoAsync(Config, Http, CancellationToken.None));
            }
        }
        if (previous is not null)
        {
            _ = previous.DisposeAsync().AsTask();
        }
    }

    /// <summary>The chats last listed (/web): /web N continues the Nth of them.</summary>
    private List<WebChat>? _webList;

    /// <summary>
    /// Continues a chat from Arena here: by its id, or its number in the list last shown. Empty: the 30 newest are
    /// listed; "all": every one; other words: those whose title has them. Its messages come in now; what this session
    /// adds goes back to it.
    /// </summary>
    public async Task<string?> ContinueWebChatAsync(string which, CancellationToken ct)
    {
        if (!Config.SyncChats || Config.Url is not { Length: > 0 })
        {
            return "Chats are not kept with Arena here (\"syncChats\": false in config.json, or no Arena address).";
        }
        which = which.Trim();
        var number = int.TryParse(which, out var n) ? n : 0;
        var id = Guid.TryParse(which, out _) ? which : null;
        List<WebChat> chats;
        if (number > 0 && _webList is { } shown)
        {
            // A number is in the list last shown: past its end, there is no such chat (not another list's).
            if (number > shown.Count)
            {
                return $"There is no chat {which}: the list is above.";
            }
            chats = shown;
        }
        else
        {
            try
            {
                chats = await ChatSync.ListAsync(Config, Http, ct, which.Length == 0 || number > 0 ? 30 : ChatSync.MostListed);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return $"Arena's chats cannot be read now: {Fmt.OneLine(e.Message, 200)}";
            }
        }
        var chat = number >= 1 && number <= chats.Count ? chats[number - 1] : chats.FirstOrDefault(c => c.Id.Equals(which, StringComparison.OrdinalIgnoreCase));
        if (chat is not null || id is not null)
        {
            // An id not among the newest: continued all the same, when Arena has it.
            try
            {
                return await OpenWebChatAsync(chat?.Id ?? id!, ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return $"Arena cannot say now whether it has that chat: {Fmt.OneLine(e.Message, 200)}";
            }
        }
        if (which.Length > 0 && number == 0 && !which.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var words = which.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            chats = [.. chats.Where(c => words.All(w => c.Title.Contains(w, StringComparison.OrdinalIgnoreCase) || (c.Place?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)))];
            if (chats.Count == 0)
            {
                return $"No chat in Arena has \"{which}\" in its title.";
            }
        }
        _webList = chats;
        var linked = SessionStore.List(Env.Paths.SessionsDir, Workspace.Root, 1000).Where(s => s.Conversation is not null)
            .GroupBy(s => s.Conversation!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < chats.Count; i++)
        {
            var c = chats[i];
            var where = c.Origin == ChatSyncOrigin ? $" · Code Arena in {c.Place}" : "";
            var here = linked.TryGetValue(c.Id, out var session) ? $" · session {session} here" : "";
            Ui.Line($"{i + 1,3}. {c.Updated.ToLocalTime():yyyy-MM-dd HH:mm}  {c.Title}  {Ui.Dim($"{c.Messages} messages{where}{here} · {c.Id}")}");
        }
        if (chats.Count == 0)
        {
            return "You have no chats in Arena yet.";
        }
        var more = which.Length == 0 && chats.Count == 30 ? " /web all lists every one, /web WORDS those with the words in their title." : "";
        return number > 0 ? $"There is no chat {which}: the list is above." : $"/web N continues one here (or code-arena --web ID).{more}";
    }

    /// <summary>
    /// A chat in Arena, here: the session of this folder kept in step with it when there is one, with what was added on
    /// the web since; else a new session that is, with the chat's history. An empty session left for it is not kept.
    /// Why it was not opened (Arena has no such chat of the person's: the session stays as it was); null: opened. Throws
    /// when Arena cannot say whether it has the chat.
    /// </summary>
    public async Task<string?> OpenWebChatAsync(string chat, CancellationToken ct)
    {
        if (!string.Equals(Sync?.Conversation, chat, StringComparison.OrdinalIgnoreCase))
        {
            var file = SessionStore.Linked(Env.Paths.SessionsDir, chat, Workspace.Root);
            if (file is null && !await ChatSync.ExistsAsync(Config, Http, chat, ct))
            {
                return $"Arena has no chat {chat} of yours (deleted, or the id is mistyped).";
            }
            var empty = Agent.Messages.Count == 0 ? Session.File : null;
            if (file is not null)
            {
                Resume(file);
            }
            else
            {
                NewSession();
                Sync?.Link(chat);
            }
            if (empty is not null && empty != Session.File)
            {
                TryDelete(empty);
            }
        }
        // Continued here by the person: archived on the web, it comes back to the list there (once: what this session adds leaves it be).
        if (Sync is not null)
        {
            await ChatSync.ContinuedAsync(Config, Http, chat, ct);
        }
        Agent.TakeIn(Sync is null ? Taken.None : await Sync.TakeInAsync(ct));
        return null;
    }

    /// <summary>
    /// Sends to Arena what it lacks of each saved session of this folder: those never sent (made with "syncChats" off, or
    /// before it was kept), and those sent in part. Each becomes a chat there, as the session open now is, the oldest
    /// first (Arena lists them as they came); one whose chat was deleted there stays as it is. The sessions keep their
    /// place here (newest first, --continue). How many were sent, and what kept the others back.
    /// </summary>
    /// <param name="everyFolder">Every folder's sessions, not only this one's (sent in the background as Code Arena starts).</param>
    public async Task<(int Sent, List<string> Failed)> SendAllAsync(Action<string>? progress, CancellationToken ct, bool everyFolder = false)
    {
        var failed = new List<string>();
        var sent = 0;
        if (!Config.SyncChats || Config.Url is not { Length: > 0 } || Config.ApiKey is not { Length: > 0 })
        {
            failed.Add("Chats are not kept with Arena here (\"syncChats\": false in config.json, or no Arena address or key).");
            return (0, failed);
        }
        var waiting = SessionStore.List(Env.Paths.SessionsDir, everyFolder ? null : Workspace.Root, 10_000).Where(s => s.Unsent && s.File != Session.File).Reverse().ToList();
        for (var i = 0; i < waiting.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var s = waiting[i];
            if (s.File == Session.File)
            {
                // Opened meanwhile: its own sync sends it.
                continue;
            }
            progress?.Invoke($"{i + 1}/{waiting.Count} {s.Preview}");
            var written = File.GetLastWriteTimeUtc(s.File);
            var store = SessionStore.Open(s.File);
            var (cwd, model) = store.Read(d => (d.Cwd, d.Model));
            var sync = ChatSync.Start(Config, Http, store, cwd ?? Workspace.Root, () => model ?? Model.Name, (_, _) => { });
            if (sync is null)
            {
                break;
            }
            string? trouble;
            await using (sync)
            {
                // Its chat is made, then its messages go 200 at a time, tried again while Arena is out of reach.
                await sync.FlushAsync(TimeSpan.FromSeconds(60), patient: true, ct);
                trouble = sync.InStep ? null : sync.Trouble ?? "Arena did not take it all within a minute";
            }
            try
            {
                // Its place among this folder's sessions stays where it was.
                File.SetLastWriteTimeUtc(s.File, written);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Listed as just changed: nothing else.
            }
            if (trouble is null)
            {
                sent++;
                continue;
            }
            failed.Add($"{s.Id}: {trouble}");
            if (sync.Trouble is not null && i < waiting.Count - 1)
            {
                // Arena out of reach, or refusing: the rest wait for the next /sync all.
                failed.Add($"{waiting.Count - i - 1} more not tried: /sync all again later.");
                break;
            }
        }
        return (sent, failed);
    }

    /// <summary>The sending of what Arena lacks, in the background as Code Arena starts; null: none runs.</summary>
    private CancellationTokenSource? _sendingLater;

    /// <summary>How long after Code Arena starts the sessions Arena lacks are sent (the person's own work goes first).</summary>
    public static TimeSpan SendLaterAfter { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Sends Arena, in the background and a while after the start, what it lacks of every folder's sessions (made before
    /// signing in, with "syncChats" off, or closed while Arena was out of reach): one summary notice when it is done. The
    /// session open now is left to its own sync. Started once (the terminal's chat, the IDE).
    /// </summary>
    public void SendLaterInBackground()
    {
        if (_sendingLater is not null || !Config.SyncChats || Config.Url is not { Length: > 0 } || Config.ApiKey is not { Length: > 0 })
        {
            return;
        }
        var stop = _sendingLater = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SendLaterAfter, stop.Token);
                var (sent, failed) = await SendAllAsync(null, stop.Token, everyFolder: true);
                if (sent > 0)
                {
                    Notice(false, $"Sent to Arena in the background: {sent} earlier session{(sent == 1 ? "" : "s")} it did not have.");
                    Agent?.Events?.Notice($"Sent to Arena in the background: {sent} earlier session{(sent == 1 ? "" : "s")} it did not have.");
                }
                if (failed.Count > 0)
                {
                    var why = $"Not every earlier session reached Arena: {failed[0]} /sync all tries again.";
                    Notice(true, why);
                    Agent?.Events?.Notice(why);
                }
            }
            catch (OperationCanceledException)
            {
                // Code Arena is closing.
            }
            catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                Notice(true, $"The earlier sessions Arena lacks were not sent: {Fmt.OneLine(e.Message, 200)} (/sync all tries again).");
            }
        });
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind: an empty session is not listed anyway.
        }
    }

    private const string ChatSyncOrigin = "code-arena";

    /// <summary>/permissions allow, deny or remove a rule: kept in config.json, in force at once.</summary>
    public string KeepRule(string verb, string rule)
    {
        var rules = Permissions.Rules;
        rules.Allow.Remove(rule);
        rules.Deny.Remove(rule);
        if (verb == "allow")
        {
            rules.Allow.Add(rule);
        }
        else if (verb == "deny")
        {
            rules.Deny.Add(rule);
        }
        try
        {
            var config = Config.Load(Env.Paths.ConfigFile);
            config.Allow = [.. rules.Allow];
            config.Deny = [.. rules.Deny];
            config.Save(Env.Paths.ConfigFile);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return $"In force for this session, but not kept: {e.Message}";
        }
        return verb == "remove" ? $"Removed {rule}." : $"{(verb == "allow" ? "Allowed without asking" : "Never allowed")}: {rule}. Kept in config.json.";
    }

    /// <summary>What is sent for what was typed: a command of the person's or the project's own (/name args) is its prompt.</summary>
    public string Prepare(string input) => Extensions.Expand(input) ?? input;

    /// <summary>
    /// Back to before turn <paramref name="number"/> of the checkpoints: the files its tools (and later turns' tools) wrote as
    /// they were, and the conversation as it was; what it says. The files put back are committed as Code Arena's when turns are.
    /// </summary>
    public async Task<string> RewindAsync(int number, bool files, bool chat, CancellationToken ct)
    {
        var turns = Checkpoints.Turns;
        if (number < 1 || number > turns.Count)
        {
            return turns.Count == 0 ? "No turn to go back to yet (checkpoints are kept while Code Arena runs)." : $"There is no turn {number}: /rewind lists them (1 to {turns.Count}).";
        }
        var subject = turns[number - 1].Input;
        var before = files && Agent.CommitAs is not null ? await TurnCommits.TakeAsync(Workspace.Root, ct) : null;
        var (messages, restored, lost) = Checkpoints.Rewind(number, files);
        if (chat)
        {
            // The session file reads back as it is now; the chat in Arena keeps what was said, and goes on from there on a branch of its own.
            Agent.Load(Sync is { } sync ? await sync.RewindAsync(messages, ct) : Session.Rewind(messages));
        }
        var said = new List<string>
        {
            $"Back to before \"{Fmt.OneLine(subject, 60)}\"" + (chat ? $": the conversation has {Agent.Messages.Count} messages" : ": the conversation stays") +
            (files ? $", {restored.Count} file{(restored.Count == 1 ? "" : "s")} put back" : ", the files stay") + ".",
        };
        if (lost.Count > 0)
        {
            said.Add($"Not put back: {string.Join(", ", lost.Take(5))}.");
        }
        if (files)
        {
            said.Add("What commands changed is not put back: git has it (git status, git diff).");
        }
        if (before is not null && restored.Count > 0 && Agent.CommitAs is { } who
            && await TurnCommits.CommitAsync(before, $"Rewind: back to before \"{Fmt.OneLine(subject, 50)}\"", who, Model.Name, Session.Id, ct) is { } commit)
        {
            said.Add(commit.Describe());
        }
        return string.Join('\n', said);
    }

    /// <summary>
    /// The conversation back to its first <paramref name="messages"/> (before a question asked again), and with
    /// <paramref name="files"/> the files the turns from there wrote put back, when this run kept that turn's checkpoint
    /// (/rewind's). The chat in Arena keeps what was said, on a branch of its own from there. What it says (null: nothing).
    /// </summary>
    public async Task<string?> BackToAsync(int messages, bool files, CancellationToken ct)
    {
        var number = Checkpoints.Turns.ToList().FindLastIndex(t => t.Messages == messages) + 1;
        if (number > 0)
        {
            var said = await RewindAsync(number, files, chat: true, ct);
            return files ? said : null;
        }
        Agent.Load(Sync is { } sync ? await sync.RewindAsync(messages, ct) : Session.Rewind(messages));
        return files ? "The files stay as they are: Code Arena kept no checkpoint of that turn (it was before this run, or too long ago)." : null;
    }

    /// <summary>
    /// The session's servers, each in the background: Arena's MCP endpoint and Argus's (with the
    /// person's key, unless the config turns them off), then the person's own.
    /// </summary>
    private void AddLinks()
    {
        var auth = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Config.ApiKey };
        if (Config.ArenaTools && Config.ArenaMcpUrl is { } arenaUrl)
        {
            Link(new ServerLink(ArenaName, "Arena", arenaUrl, ct => McpClient.ConnectAsync(ArenaName, new HttpMcpTransport(Http, arenaUrl, auth), ct)));
        }
        if (Config.ArgusTools && Config.ArgusMcpUrl is { } argusUrl)
        {
            Link(new ServerLink(ArgusName, "Argus", argusUrl, async ct =>
            {
                try
                {
                    return await McpClient.ConnectAsync(ArgusName, new HttpMcpTransport(Http, argusUrl, auth), ct);
                }
                catch (McpException e) when (e.NoSuchHost && Config.ArgusUrl is not { Length: > 0 })
                {
                    // Found by its name beside the Arena's (argus.DOMAIN): an Arena without Argus has no such name. Not an error.
                    throw new McpUnavailableException($"{argusUrl} does not resolve: this Arena has no Argus at argus.DOMAIN (\"argusUrl\" in config.json gives its address).");
                }
            }));
        }
        foreach (var server in Config.McpServers.Where(s => !s.Disabled))
        {
            if (server.Url is not { Length: > 0 } && server.Command is not { Length: > 0 })
            {
                Ui.Warn($"MCP server {server.Name} has neither a command nor a url: skipped.");
                continue;
            }
            if (server.Url is { Length: > 0 } address && !Config.IsWebAddress(address))
            {
                Ui.Warn($"MCP server {server.Name}: {address} is not an http or https address: skipped.");
                continue;
            }
            Link(new ServerLink(server.Name, server.Name, server.Url ?? server.Command, ct =>
            {
                IMcpTransport transport = server.Url is { Length: > 0 } url
                    ? new HttpMcpTransport(Http, url, server.Headers.ToDictionary(h => h.Key, h => StdioMcpTransport.Expand(h.Value)))
                    : StdioMcpTransport.Start(server, Workspace.Root);
                return McpClient.ConnectAsync(server.Name, transport, ct);
            }));
        }
    }

    private void Link(ServerLink link)
    {
        link.Changed = OnServerChanged;
        _links.Add(link);
    }

    /// <summary>A server connected (its tools and instructions join), or was lost (they go); said in the terminal once per change.</summary>
    private void OnServerChanged(ServerLink link, McpClient? client, McpClient? gone)
    {
        lock (_serversGate)
        {
            var (remote, notes) = Served();
            // The servers' tools keep their places as others come and go: the requests' start stays what the engine has cached.
            Tools.Update(all => all.Where(t => t.Server is null).Concat(ToolBox.InPlace(all.Where(t => t.Server is not null), remote)));
            Volatile.Write(ref _instructions, notes);
            if (link.Name == ArenaName)
            {
                Volatile.Write(ref _arenaServing, client is not null);
                Permissions.Guard = LayaGuard.For(client, Workspace, Ui);
                if (Permissions.Guard is { } guard)
                {
                    // Its one warning reaches the IDE's page too, as the agent's own do.
                    guard.Notify = said => Agent?.Events?.Notice(said);
                }
            }
        }
        Said(link, client, gone);
        ServersChanged?.Invoke(link);
    }

    /// <summary>
    /// The servers' tools and instructions, from those connected now. Arena's: renamed arena_… where
    /// a local tool has the name. Argus's: those Arena serves already (its Argus tools) are left to
    /// Arena, and Argus's instructions with them; when Arena is not connected, Argus's own are used.
    /// The person's own: mcp__server__tool.
    /// </summary>
    private (List<ToolDef> Tools, List<(string Server, string Text)> Notes) Served()
    {
        var local = Tools.All.Where(t => t.Server is null).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var tools = new List<ToolDef>();
        var notes = new List<(string Server, string Text)>();
        foreach (var link in _links.OrderBy(l => l.Name == ArenaName ? 0 : 1))
        {
            if (link.Client is not { } client)
            {
                continue;
            }
            var own = link.Name is ArenaName or ArgusName ? null : Config.McpServers.FirstOrDefault(s => s.Name == link.Name);
            var added = 0;
            foreach (var t in client.Tools)
            {
                var name = t.Str("name")!;
                if (own is not null)
                {
                    tools.Add(Remote(client, t, OwnToolName(client.Name, name), own.Trust, t["annotations"].Bool("readOnlyHint") == true, link, "mcp:" + link.Name));
                }
                else if (link.Name == ArgusName && tools.Any(x => x.Server == ArenaName && x.RemoteName == name))
                {
                    continue;
                }
                else
                {
                    // Arena (and Argus) decide who may call what; a tool marked as changing things still asks here first.
                    var changes = t["annotations"].Bool("destructiveHint") == true && t["annotations"].Bool("readOnlyHint") != true;
                    // Arena's tools by the tool of its chat each is a function of (as its menu has them); Argus's own as Argus.
                    var group = link.Name == ArenaName ? t["_meta"].Str("arena/tool") ?? ArenaName : link.Name;
                    tools.Add(Remote(client, t, local.Contains(name) ? $"{link.Name}_{name}" : name, trusted: !changes, changesNothing: !changes, link, group));
                }
                added++;
            }
            if (client.Instructions is { Length: > 0 } text && (added > 0 || client.Tools.Count == 0))
            {
                notes.Add((link.Title, text));
            }
        }
        return (tools, notes);
    }

    /// <summary>
    /// What the terminal says of a change: when a server connects after the session started, and
    /// once when it fails (again when it was connected and is lost), not at every try.
    /// </summary>
    private void Said(ServerLink link, McpClient? client, McpClient? gone)
    {
        lock (_said)
        {
            if (client is not null)
            {
                _said.Remove(link.Name);
                if (Agent is not null)
                {
                    Notice(false, $"{link.Title}'s tools connected: {Tools.All.Count(t => t.Server == link.Name)}.");
                }
                return;
            }
            if (!_said.Add(link.Name) && gone is null)
            {
                return;
            }
        }
        var again = link.NextTry is { } next ? $" Tried again at {next.ToLocalTime():HH:mm:ss} (/mcp retry tries now)." : "";
        if (link.State == LinkState.Unavailable)
        {
            Notice(false, link.Name switch
            {
                ArenaName => $"Arena's tools are not available here (no MCP endpoint at {link.Url}): carrying on with the local tools.",
                ArgusName => $"Argus's tools are not available here: {link.Error}",
                _ => $"MCP server {link.Name} has no MCP endpoint at {link.Url}.",
            });
            return;
        }
        Notice(true, link.Name switch
        {
            ArenaName => $"Arena's tools did not connect: {link.Error} Carrying on with the local tools meanwhile.{again}",
            ArgusName => $"Argus's tools did not connect: {link.Error}{again}",
            _ => $"MCP server {link.Name} did not connect: {link.Error}{again}",
        });
    }

    // The servers whose failure was said, until they connect.
    private readonly HashSet<string> _said = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<(bool Warn, string Text)> _later = new();

    /// <summary>The terminal is waiting for the person to type: what servers say waits for the next line (<see cref="SayLater"/>).</summary>
    public bool AtPrompt { get; set; }

    internal void Notice(bool warn, string text)
    {
        if (AtPrompt)
        {
            _later.Enqueue((warn, text));
            return;
        }
        // Not into a question waiting for its answer, nor the middle of the model's line.
        Ui.Background(warn ? Ui.Yellow("! " + text) : Ui.Dim(text));
    }

    /// <summary>What the servers said while the terminal waited at its prompt.</summary>
    public void SayLater()
    {
        while (_later.TryDequeue(out var said))
        {
            if (said.Warn)
            {
                Ui.Warn(said.Text);
            }
            else
            {
                Ui.Info(said.Text);
            }
        }
    }

    /// <summary>A new, empty session (/clear): the same tools and model.</summary>
    public void NewSession()
    {
        _memory = Memory.Again() + LongMemory();
        Session = SessionStore.Create(Env.Paths.SessionsDir, Workspace.Root, Model.Name);
        Agent.Session = Session;
        Agent.Clear();
        Context.Plan = WorkPlan.None;
        Demand.Restore([], []);
        Context.PlanChanged = plan => Session.Plan(plan);
        StartSync();
    }

    /// <summary>The ways the person answers a proposed plan (the terminal's /plan, the IDE's Plan view).</summary>
    public static readonly string[] Reviews = ["approve", "approve-ask", "change", "another", "reject"];

    /// <summary>
    /// The person's review of the plan the agent proposed in plan mode: approved (to carry out with edits running, or
    /// asking before each), a step to change, another way, or rejected. The plan and the mode change here; what comes back
    /// is said to the agent as the person's next message.
    /// </summary>
    public string ReviewPlan(string action, int? step = null, string? text = null)
    {
        var plan = Context.Plan;
        if (plan.Review != WorkPlan.Proposed || plan.IsEmpty)
        {
            throw new PlanError("There is no plan waiting for its review: in plan mode (/mode plan) the agent proposes one.");
        }
        text = text?.Trim() is { Length: > 0 } t ? t : null;
        // The person's words, as a sentence of their own.
        var why = text is null ? "." : ": " + text + (text[^1] is '.' or '!' or '?' ? "" : ".");
        switch (action)
        {
            case "approve" or "approve-ask":
                Permissions.Mode = action == "approve" ? Mode.AutoEdit : Mode.Ask;
                SetPlan(plan with { Review = WorkPlan.Approved });
                return "I approve the plan. Carry it out now, step by step: mark a step doing as you start it, run its check when it is done, "
                    + "mark it done only once the check passed, and change the plan (saying why) when something turns out otherwise."
                    + (text is null ? "" : $"\n\n{text}");
            case "change":
                if (step is not { } n || plan.Steps.All(s => s.Number != n))
                {
                    throw new PlanError($"Say which step to change: one of {string.Join(", ", plan.Steps.Select(s => s.Number))}.");
                }
                return text is null
                    ? throw new PlanError($"Say what should change in step {n}.")
                    : $"Change step {n} of the plan ({plan.Steps.First(s => s.Number == n).Title}): {text}\n\nThen propose the plan again.";
            case "another":
                return "Propose another way to do this" + why + " Make the plan again with the plan tool.";
            case "reject":
                SetPlan(WorkPlan.None);
                return "I reject the plan" + why + " Do not carry it out; I will say what to do next.";
            default:
                throw new PlanError($"A plan's review is one of {string.Join(", ", Reviews)}.");
        }
    }

    /// <summary>
    /// /plan undo N: the files as they were before step N began (and before every step begun after it), those steps to do
    /// again; the agent is told. What commands changed is not undone.
    /// </summary>
    public string UndoStep(int number)
    {
        List<int> steps;
        List<string> restored, lost;
        try
        {
            (steps, restored, lost) = Checkpoints.UndoStep(number);
        }
        catch (ArgumentOutOfRangeException)
        {
            var begun = Checkpoints.Steps.Select(s => s.Number).ToList();
            throw new PlanError(begun.Count == 0 ? "No step has begun in this run: there is nothing to undo." : $"Step {number} has not begun in this run: steps {string.Join(", ", begun)} have.");
        }
        var plan = Context.Plan;
        SetPlan(plan with { Steps = [.. plan.Steps.Select(s => steps.Contains(s.Number) ? s with { Status = WorkStep.Todo, CheckResult = null, CheckOutput = null, CheckedAt = null } : s)] });
        var files = restored.Select(f => Path.GetRelativePath(Workspace.Root, f)).ToList();
        var said = $"I undid step{(steps.Count == 1 ? "" : "s")} {string.Join(", ", steps)} of the plan: "
            + (files.Count == 0 ? "no file had been written since it began." : $"{string.Join(", ", files)} {(files.Count == 1 ? "is" : "are")} as before it began.")
            + (lost.Count == 0 ? "" : $" Not put back: {string.Join(", ", lost)}.")
            + " What commands changed is not undone. The steps are to do again.";
        Agent.Remember(said);
        return said;
    }

    /// <summary>A plan an earlier session of this folder left in progress, when this session has none of its own.</summary>
    public SessionSummary? PlanToResume() =>
        Context.Plan.IsEmpty ? SessionStore.PlanInProgress(Env.Paths.SessionsDir, Workspace.Root, Session.Id) : null;

    /// <summary>
    /// /plan resume, code-arena --plan: the plan an earlier session left in progress carried on here, with its progress and
    /// notes; the agent is told where it stands and to see what that session changed before going on.
    /// </summary>
    public string ResumePlan()
    {
        if (PlanToResume() is not { } from)
        {
            throw new PlanError(Context.Plan.IsEmpty ? "No earlier session of this folder left a plan in progress." : "This session has a plan of its own: /plan shows it.");
        }
        SetPlan(from.Plan);
        var done = from.Plan.Steps.Count(s => s.Status == WorkStep.Done);
        Agent.Remember($"The plan an earlier session of this folder left in progress ({from.Updated:yyyy-MM-dd HH:mm}) is carried on here, "
            + $"{done} of {from.Plan.Steps.Count} steps done:\n\n{from.Plan.Text()}\n\n"
            + "Before going on, see what that session changed (git status, git log, the files of the step that was doing), then carry on from the step that stands next.");
        return $"Carried on: \"{from.Plan.Title}\" from the session of {from.Updated:yyyy-MM-dd HH:mm}, {done} of {from.Plan.Steps.Count} steps done.";
    }

    /// <summary>Whether a check is a command: its first word a program on the PATH, or a script in the project.</summary>
    internal static bool IsCommand(string check, string root)
    {
        var first = check.Trim().Trim('`').Split((char[])[' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null || first.Length == 0 || !char.IsAsciiLetterOrDigit(first[0]) && first[0] is not ('.' or '/'))
        {
            return false;
        }
        if (first.Contains('/') || first.Contains('\\'))
        {
            return File.Exists(Path.IsPathRooted(first) ? first : Path.Combine(root, first));
        }
        var exts = OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [""];
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => exts.Any(ext => File.Exists(Path.Combine(dir, first + ext))));
    }

    private void SetPlan(WorkPlan plan)
    {
        Context.Plan = plan;
        Context.PlanChanged?.Invoke(plan);
    }

    /// <summary>Switches to a saved session (/resume).</summary>
    public void Resume(string file)
    {
        var data = SessionStore.Load(file, whole: true);
        _memory = Memory.Again() + LongMemory();
        Session = SessionStore.Open(file);
        Agent.Session = Session;
        Agent.Load(data.Messages, data.Known);
        Context.Plan = data.Plan;
        Demand.Restore(data.ToolsOff, data.ToolsLoaded);
        Context.PlanChanged = plan => Session.Plan(plan);
        if (data.Model is not null && data.Model != Model.Name && Models.FirstOrDefault(m => m.Id == data.Model) is { } info)
        {
            Model.Info = info;
        }
        StartSync();
    }

    public void SwitchModel(ModelInfo info)
    {
        Model.Info = info;
        Session.Model(info.Id);
    }

    /// <summary>
    /// Sets when the session compacts (percent of the window, <see cref="Compaction.Off"/> for only when a request would not
    /// fit; null keeps the current) and keeps it in config.json for the next sessions; <paramref name="follow"/>: the
    /// threshold is the Arena account's again (else 80%), and out of config.json. Null when done, else why not.
    /// </summary>
    public string? SetCompaction(int? at, int? target, bool follow = false)
    {
        if (follow)
        {
            at = AccountCompactAt?.At ?? Compaction.DefaultAt;
        }
        if (Compaction.Set(at, target) is { } wrong)
        {
            return wrong;
        }
        if (follow)
        {
            CompactAtSource = AccountCompactAt is { } account ? (account.Own ? "account" : "arena") : "default";
        }
        else if (at is not null)
        {
            CompactAtSource = "config";
        }
        try
        {
            // The file as it is (not with this run's environment overrides), with the two values changed: a threshold
            // chosen is kept as chosen (the account's may differ), one that follows the account is left out.
            var file = Config.Load(Env.Paths.ConfigFile);
            file.CompactAt = follow ? null : at is null ? file.CompactAt : Compaction.At;
            file.CompactTarget = Compaction.Target == Compaction.DefaultTarget ? null : Compaction.Target;
            file.Save(Env.Paths.ConfigFile);
            Config.CompactAt = file.CompactAt;
            Config.CompactTarget = file.CompactTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Ui.Warn($"Set for this session, but not saved: {e.Message}");
        }
        return null;
    }

    /// <summary>"Arena: 14 tools", "Argus: its tools come through Arena", "Argus: not connected (…)".</summary>
    public string Describe(ServerLink link)
    {
        if (link.State == LinkState.Connected)
        {
            var count = Tools.All.Count(t => t.Server == link.Name);
            return count == 0 && link.Client?.Tools.Count > 0
                ? $"{link.Title}: connected, its tools come through Arena"
                : $"{link.Title}: {count} tool{(count == 1 ? "" : "s")}";
        }
        return link.Describe() + (link.State == LinkState.Failed && link.Error is { } error ? $" ({Fmt.OneLine(error, 120)})" : "");
    }

    private SystemPrompt.Inputs? _inputs;

    /// <summary>
    /// A review of the changes (the IDE's Review view): a fresh agent with the reading tools and <paramref name="report"/>,
    /// as a sub-agent is, its steps told to <paramref name="events"/>, its tokens the session's; what it said at the end.
    /// </summary>
    internal async Task<string> ReviewAsync(string prompt, ToolDef report, IAgentEvents events, CancellationToken ct)
    {
        var inputs = _inputs ?? throw new InvalidOperationException("Code Arena has not started yet.");
        var reviewer = new Agent
        {
            Gateway = Gateway,
            Tools = new ToolBox([.. Tools.ForSubAgent().All, report]),
            Permissions = Permissions,
            Context = new ToolContext { Workspace = Workspace, Ui = Ui, Shell = Config.Shell, Sandbox = Sandbox },
            Ui = Ui,
            Model = Model,
            SystemPrompt = () => SystemPrompt.Build(inputs, subAgent: true),
            Total = Total,
            Depth = 1,
            Stream = false,
            MaxSteps = 80,
            Compaction = Compaction,
            Events = events,
        };
        return await reviewer.RunAsync(prompt, new Spend(), ct);
    }

    /// <summary>The most symbols looked up for one plan: each is a call to Argus.</summary>
    public const int MostGrounded = 12;

    /// <summary>
    /// What each step's symbols reach, from Argus's change_impact (when Argus is connected and the project is a repository it
    /// knows): the repositories that depend on this one, the lines that use each name, and the tests among them. Steps
    /// Argus cannot say anything of keep no impact; the plan is otherwise as it was.
    /// </summary>
    private async Task<WorkPlan> GroundAsync(WorkPlan plan, CancellationToken ct)
    {
        if (Tools.Find("change_impact") is not { Server: ArgusName } impact || ProjectKeys.Of(Workspace.Root) is var key && key.StartsWith("local/", StringComparison.Ordinal))
        {
            return plan;
        }
        // Argus names a repository by its path in GitLab: the project's key without its host.
        var repo = key[(key.IndexOf('/') + 1)..];
        var looked = 0;
        var steps = new List<WorkStep>();
        foreach (var step in plan.Steps)
        {
            if (step.Symbols.Count == 0 || step.Impact is not null || looked >= MostGrounded)
            {
                steps.Add(step);
                continue;
            }
            var said = new List<string>();
            foreach (var symbol in step.Symbols.Take(MostGrounded - looked))
            {
                looked++;
                try
                {
                    var result = await impact.Run(new JsonObject { ["repo"] = repo, ["symbol"] = symbol, ["depth"] = 2 }, Context, ct);
                    if (!result.Error && Json.ParseObject(result.Text) is { } found)
                    {
                        said.Add(Impact(symbol, found));
                    }
                }
                catch (Exception e) when (e is ToolError or McpException or HttpRequestException or IOException)
                {
                    // Argus out of reach: the plan goes on without what it would have said.
                }
            }
            steps.Add(said.Count == 0 ? step : step with { Impact = string.Join("\n", said) });
        }
        return plan with { Steps = steps };
    }

    /// <summary>What change_impact said of a symbol, in a line: who depends on the repository, which lines use the name, which of them are tests.</summary>
    internal static string Impact(string symbol, JsonObject found)
    {
        if (found.Str("error") is { } error)
        {
            return $"{symbol}: {error}";
        }
        var uses = (found["references"] as JsonArray ?? []).OfType<JsonObject>().Where(r => r.Bool("is_definition") != true).ToList();
        var byRepo = uses.GroupBy(r => r.Str("repo") ?? "").Select(g => $"{g.Key} ({g.Count()})").Take(4).ToList();
        var tests = uses.Select(r => r.Str("path") ?? "").Where(path => path.Contains("test", StringComparison.OrdinalIgnoreCase) || path.Contains("spec", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal).Take(5).ToList();
        var dependents = found.Int("dependents") ?? 0;
        var text = $"{symbol}: {(uses.Count == 0 ? "no line found that uses it" : $"{uses.Count}{(found.Bool("references_truncated") == true ? "+" : "")} lines use it, in {string.Join(", ", byRepo)}")}";
        text += dependents == 0 ? "; no other repository depends on this one" : $"; {dependents} {(dependents == 1 ? "repository depends" : "repositories depend")} on this one";
        return tests.Count == 0 ? text + "; no test names it." : text + $"; tests that name it: {string.Join(", ", tests)}.";
    }

    /// <summary>The words a second reading answers with when the plan holds as it is.</summary>
    public const string PlanHolds = "The plan holds.";

    /// <summary>
    /// A proposed plan read again by a sub-agent in a context of its own (the same model, fresh eyes, the reading tools):
    /// what is missing, what is risky, which step has no check or one that would not show it is done; null when it holds.
    /// </summary>
    private async Task<string?> CritiqueAsync(SystemPrompt.Inputs inputs, WorkPlan plan, CancellationToken ct)
    {
        var asked = Agent.Messages.LastOrDefault(m => m.Str("role") == "user" && m.Str("content") is { Length: > 0 } c && !c.StartsWith('('))?.Str("content");
        var report = await RunSubAgentAsync(inputs, "a second reading of the plan",
            "Read this plan with fresh eyes before it is carried out. Look at the code where it helps; change nothing.\n\n"
            + (asked is null ? "" : $"What the person asked:\n{Fmt.OneLine(Quotes.Split(asked).Text, 2000)}\n\n")
            + plan.Text() + "\n\n"
            + "Answer with short notes only, a line each: what is missing, what is risky, which step has no check or a check that would not "
            + $"show it is done, what is in the wrong order. If the plan holds as it is, answer exactly: {PlanHolds}", null, ct);
        var notes = report.Trim();
        return notes.Length == 0 || notes.StartsWith(PlanHolds, StringComparison.OrdinalIgnoreCase) ? null : notes.Length <= 4000 ? notes : notes[..4000];
    }

    private async Task<string> RunSubAgentAsync(SystemPrompt.Inputs inputs, string description, string prompt, string? agent, CancellationToken ct)
    {
        CustomAgent? own = null;
        if (agent is { Length: > 0 } && (own = Extensions.Agents.FirstOrDefault(a => string.Equals(a.Name, agent, StringComparison.OrdinalIgnoreCase))) is null)
        {
            throw new ToolError($"There is no sub-agent {agent}{(Extensions.Agents.Count > 0 ? $": yours are {string.Join(", ", Extensions.Agents.Select(a => a.Name))}" : "")}.");
        }
        var tools = Tools.ForSubAgent();
        if (own is { Tools.Count: > 0 })
        {
            // Its own list narrows the reading tools; it never adds one that changes something.
            tools = new ToolBox(tools.All.Where(t => own.Tools.Any(n => PermissionRules.Glob(n, t.Name))));
        }
        var model = own?.Model is { } name && Models.FirstOrDefault(m => m.Id == name) is { } info ? new ModelState { Info = info, Thinking = Model.Thinking, DefaultLevel = Model.DefaultLevel, ContextOverride = Config.Context } : Model;
        var sub = new Agent
        {
            Gateway = Gateway,
            Tools = tools,
            Permissions = Permissions,
            Context = new ToolContext { Workspace = Workspace, Ui = Ui, Shell = Config.Shell, Sandbox = Sandbox },
            Ui = Ui,
            Model = model,
            SystemPrompt = () => own is null ? SystemPrompt.Build(inputs, subAgent: true) : own.Prompt + "\n\n" + SystemPrompt.Build(inputs, subAgent: true),
            Total = Total,
            Depth = 1,
            Stream = false,
            MaxSteps = 60,
            Compaction = Compaction,
        };
        var report = await sub.RunAsync(prompt, Turn ?? new Spend(), ct);
        return report.Trim().Length > 0 ? report : $"The sub-agent ({description}) returned no report.";
    }

    /// <summary>An MCP tool as the model sees it, calling the server when run; a call that finds the server gone has it connect again.</summary>
    public static ToolDef Remote(McpClient client, JsonObject tool, string name, bool trusted, bool changesNothing, ServerLink? link = null, string? group = null)
    {
        var remoteName = tool.Str("name")!;
        var schema = tool["inputSchema"] is JsonObject s ? s.Clone() : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        if (schema.Str("type") is null)
        {
            schema["type"] = "object";
        }
        return new ToolDef
        {
            Name = name,
            RemoteName = remoteName,
            Description = tool.Str("description") ?? tool.Str("title") ?? remoteName,
            Parameters = schema,
            Kind = ToolKind.Remote,
            Server = client.Name,
            Group = group,
            Trusted = trusted,
            ChangesNothing = changesNothing,
            Summary = a => Fmt.OneLine(Json.Line(a), 100),
            Run = async (args, context, ct) =>
            {
                try
                {
                    // Within the tool's time (progress reports count it anew), its progress told, its pictures, sound and video saved.
                    var result = await client.CallAsync(remoteName, args, ct, progress: context.Progress, media: McpMedia.For(context.Workspace.Root));
                    return new ToolResult(result.Text, result.IsError);
                }
                catch (McpException e) when (e.Lost && link is not null)
                {
                    link.Lost(e.Message);
                    throw;
                }
            },
        };
    }

    /// <summary>mcp__server__tool, as Claude Code names them: letters, digits, _ and - only, at most 64 characters.</summary>
    public static string OwnToolName(string server, string tool)
    {
        var name = $"mcp__{Unsafe().Replace(server, "_")}__{Unsafe().Replace(tool, "_")}";
        return name.Length <= 64 ? name : name[..64];
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex Unsafe();

    public async ValueTask DisposeAsync()
    {
        Jobs.StopAll("code-arena stopped");
        Sandbox.Clean();
        // The earlier sessions not sent yet go at the next start.
        _sendingLater?.Cancel();
        if (Sync is not null)
        {
            // What is still to send goes now, for a few seconds at most; the rest goes when the session is next opened.
            await Sync.DisposeAsync();
        }
        foreach (var link in _links)
        {
            await link.DisposeAsync();
        }
        Http.Dispose();
    }
}
