using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>The model in use, what the gateway says of it, and the thinking level asked for.</summary>
internal sealed class ModelState
{
    public required ModelInfo Info { get; set; }
    public string Name => Info.Id;
    /// <summary>off, low, medium, high, xhigh, or null for the model's own default.</summary>
    public string? Thinking { get; set; }
    /// <summary>The window in tokens: the config's, else the gateway's, else 32,768.</summary>
    public int? ContextOverride { get; set; }
    public int Context => ContextOverride ?? Info.Context ?? 32_768;

    public static readonly string[] Levels = ["off", "low", "medium", "high", "xhigh"];

    /// <summary>
    /// The level "default" stands for: the web chat's default as Arena says it (DEFAULT_THINKING), so a session on default
    /// thinks as a new chat on the web does; null (the model's own) until Arena says, or when it names no level of these.
    /// </summary>
    public string? DefaultLevel { get; set; }

    /// <summary>What reaches the model's chat template: reasoning_effort, or enable_thinking false for "off" (as the chat sends it).</summary>
    public JsonObject? TemplateKwargs() => !Info.Thinking || (Thinking is null or "" or "default" ? DefaultLevel : Thinking) is not { Length: > 0 } level ? null
        : level == "off" ? new JsonObject { ["enable_thinking"] = false }
        : new JsonObject { ["reasoning_effort"] = level };
}

/// <summary>
/// When the session compacts itself, and how much it keeps: shares of the model's window, in
/// percent. At <see cref="At"/>, the older part of the conversation is summarized and the recent
/// part kept whole within <see cref="Target"/>. At <see cref="Off"/>, only when the next request
/// would not fit; and whatever the threshold, always then.
/// </summary>
internal sealed class Compaction
{
    /// <summary>The threshold that compacts only when the next request would not fit the window (the web chat's "only when needed").</summary>
    public const int Off = 0;
    public const int DefaultAt = 80;
    public const int DefaultTarget = 25;
    public const int MinAt = 20;
    public const int MaxAt = 95;
    public const int MinTarget = 5;
    /// <summary>The target stays this far below the threshold, so a compaction does not start the next one.</summary>
    public const int Gap = 10;

    public int At { get; private set; } = DefaultAt;
    public int Target { get; private set; } = DefaultTarget;

    /// <summary>Sets both, or says why not (a sentence for the person); a target too close to the threshold is brought down to fit.</summary>
    public string? Set(int? at, int? target)
    {
        var newAt = at ?? At;
        if (newAt != Off && newAt is < MinAt or > MaxAt)
        {
            return $"The threshold is from {MinAt}% to {MaxAt}% of the model's window, or off (only when a request would not fit).";
        }
        // Off compacts near the window's end: what is kept stays as far under the highest threshold.
        var ceiling = (newAt == Off ? MaxAt : newAt) - Gap;
        var newTarget = target ?? Math.Min(Target, ceiling);
        if (newTarget < MinTarget || newTarget > ceiling)
        {
            return newAt == Off
                ? $"What is kept is from {MinTarget}% to {ceiling}% of the window."
                : $"What is kept is from {MinTarget}% to {ceiling}% of the window ({Gap} points under the threshold, {newAt}%).";
        }
        (At, Target) = (newAt, newTarget);
        return null;
    }

    /// <summary>"70", "70%", "0.7" → 70; "off" or 0 → <see cref="Off"/>; null when it is none of these.</summary>
    public static int? Percent(string? text)
    {
        var t = (text ?? "").Trim().TrimEnd('%').Trim();
        if (t.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return Off;
        }
        if (!double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0)
        {
            return null;
        }
        return (int)Math.Round(n <= 1 ? n * 100 : n);
    }

    /// <summary>"at 80%", or "only when a request would not fit".</summary>
    public string When() => At == Off ? "only when a request would not fit" : $"at {At}%";

    /// <summary>What it does; <paramref name="from"/>: where the threshold comes from ("from your Arena account"), when said.</summary>
    public string Describe(string? from = null) =>
        (At == Off ? "compacts only when a request would not fit the window" : $"compacts at {At}% of the window, and always before a request would not fit") +
        (from is null ? "" : $" ({from})") + $", keeping the recent part within {Target}%";
}

/// <summary>Tokens spent: per turn, and for the session (sub-agents included).</summary>
internal sealed class Spend
{
    public long Prompt;
    public long Cached;
    public long Completion;
    public int Requests;
    public decimal? Cost;
    private readonly object _gate = new();

    public void Add(TokenUsage u, ModelInfo model)
    {
        Interlocked.Add(ref Prompt, u.Prompt);
        Interlocked.Add(ref Cached, u.Cached);
        Interlocked.Add(ref Completion, u.Completion);
        Interlocked.Increment(ref Requests);
        if (model.InputCost is { } input && model.OutputCost is { } output)
        {
            lock (_gate)
            {
                Cost = (Cost ?? 0) + (u.Prompt - u.Cached) * input + u.Cached * (model.CachedCost ?? input) + u.Completion * output;
            }
        }
    }

    public string Describe(Ui ui)
    {
        var share = Prompt > 0 ? $" ({100.0 * Cached / Prompt:0}% cached)" : "";
        var cost = Cost is { } c && c > 0 ? $" · ≈{c:0.####}" : "";
        return $"{Fmt.Tokens(Prompt)} in{share} · {Fmt.Tokens(Completion)} out · {Requests} request{(Requests == 1 ? "" : "s")}{cost}";
    }
}

/// <summary>
/// What a front end other than the terminal hears of a turn (the web interface):
/// a call for each thing the terminal shows, as it happens.
/// </summary>
internal interface IAgentEvents
{
    /// <summary>A request to the model starts: its answer streams next.</summary>
    void Step();
    void Reasoning(string text);
    void Text(string text);
    /// <summary>The model's answer is in: its tokens, when the gateway said.</summary>
    void StepDone(TokenUsage? usage);
    /// <summary>A call the model made, before it is allowed or run.</summary>
    void ToolCall(string id, string name, ToolDef? tool, string arguments);
    /// <summary>A call's result; index is its place among the answer's calls; declined when the permissions refused it.</summary>
    void ToolResult(string id, string name, int index, ToolResult result, TimeSpan took, bool declined);
    /// <summary>The history was replaced by a summary (compaction).</summary>
    void Compacted(string notice);
    void Notice(string text);
    /// <summary>What went with the person's message (@files, the lines chosen in the IDE's editor): path, or path:first-last.</summary>
    void Attached(IReadOnlyList<string> files)
    {
    }
}

/// <summary>
/// The agent loop: ask the model, run the tools it calls (those that need no
/// question at once), give back their results, until it answers without a
/// call. Compacts the history when it nears the model's window.
/// </summary>
internal sealed class Agent
{
    public required GatewayClient Gateway { get; init; }
    public required ToolBox Tools { get; init; }
    public required Permissions Permissions { get; init; }
    public required ToolContext Context { get; init; }
    public required Ui Ui { get; init; }
    public required ModelState Model { get; init; }
    /// <summary>Rebuilt for every request: the model may have changed. What changes more often is in <see cref="Note"/>.</summary>
    public required Func<string> SystemPrompt { get; init; }
    /// <summary>
    /// Code Arena's note on the session (the mode, the date, the branch, the tools connected), rebuilt for every request and
    /// added to the conversation where it changed: the start of the requests stays the same, and the engine reads it from
    /// its cache. Null: none (a sub-agent, whose prompt says it all).
    /// </summary>
    public Func<string>? Note { get; init; }
    public SessionStore? Session { get; set; }
    public Spend Total { get; init; } = new();
    /// <summary>0 for the main agent, 1 for a sub-agent (indented, not streamed).</summary>
    public int Depth { get; init; }
    public int MaxSteps { get; init; } = 200;
    /// <summary>How often one turn carries on after a connection dropped mid-answer before it stops.</summary>
    public const int MaxInterruptions = 3;

    /// <summary>How many turns in a row the model may carry on from commands that ended, with no message from the person in between.</summary>
    public const int MaxFollowUps = 20;

    private int _followUps;

    /// <summary>The model carried on <see cref="MaxFollowUps"/> times with no message from the person: it waits for one.</summary>
    public bool FollowUpsSpent => _followUps > MaxFollowUps;
    public List<JsonObject> Messages { get; private set; } = [];
    /// <summary>The answer's text streams to the terminal (the main agent, not in a quiet run).</summary>
    public bool Stream { get; init; } = true;
    /// <summary>The web interface's ear on each turn; null in the terminal.</summary>
    public IAgentEvents? Events { get; set; }
    /// <summary>When the history is compacted, and to what.</summary>
    public Compaction Compaction { get; init; } = new();
    /// <summary>What the web chat added since the last turn, taken in as a turn starts (the main conversation only).</summary>
    public Func<CancellationToken, Task<Taken>>? BeforeTurn { get; set; }
    /// <summary>Told when a turn of the main conversation ends, however it ended (the web chat is let go).</summary>
    public Action? AfterTurn { get; set; }
    /// <summary>A checkpoint before each turn (/rewind); null: none kept (a sub-agent).</summary>
    public Checkpoints? Checkpoints { get; set; }
    /// <summary>Commits each turn's changes under this name (the main conversation only); null: no commits.</summary>
    public CommitIdentity? CommitAs { get; set; }
    /// <summary>The files turns changed but did not commit (stopped or failed), with their content then: still the harness's next turn.</summary>
    private Dictionary<string, string?> _uncommitted = new(StringComparer.Ordinal);

    private bool _parallelCalls = true;
    /// <summary>The tokens of the last request and its answer as the gateway counted them, and the messages they covered (0: not counted since a compaction).</summary>
    private long _knownTokens;
    private int _knownCount;
    /// <summary>The size of the last request sent, in characters (what its tokens are learned against).</summary>
    private long _sentChars;

    /// <summary>The notes added (<see cref="Note"/>), each before the message at its place (at the end: after the last), in order.</summary>
    private readonly List<(int At, string Text)> _notes = [];

    /// <summary>
    /// Starts from these messages (a session resumed, a rewind). <paramref name="known"/>: the last answer's tokens as the
    /// gateway counted them, and how many of the messages its request and answer covered, so the size is known at once.
    /// </summary>
    public void Load(IEnumerable<JsonObject> messages, (TokenUsage Usage, int Count)? known = null)
    {
        Messages = [.. messages];
        _notes.Clear();
        _knownTokens = 0;
        _knownCount = 0;
        _compactedAt = 0;
        _followUps = 0;
        if (known is { } k && k.Count > 0 && k.Count <= Messages.Count && k.Usage.Prompt > 0)
        {
            Learn(Chars(k.Count - 1), k.Usage.Prompt);
            _knownTokens = k.Usage.Prompt + k.Usage.Completion;
            _knownCount = k.Count;
        }
    }

    public void Clear() => Load([]);

    /// <summary>
    /// Messages the person added in the web chat: the model hears them (the session has them written already). Said with
    /// <paramref name="say"/> (the terminal's prompt: above what is being typed), else as a line of its own.
    /// </summary>
    public void TakeIn(Taken taken, Action<string>? say = null)
    {
        if (!taken.Any)
        {
            return;
        }
        var messages = taken.Added;
        if (taken.History is { } history)
        {
            // The web's conversation changed, or its messages go before some of the session's: the history as the session has it now.
            Messages = history;
            _notes.Clear();
            _knownTokens = 0;
            _knownCount = 0;
            _compactedAt = 0;
        }
        else
        {
            Messages.AddRange(messages);
        }
        var question = messages.LastOrDefault(m => m.Str("role") == "user")?.Str("content") is { } said ? Quotes.Split(said).Text : null;
        var text = taken.Said ?? $"Taken in from the web chat: {messages.Count} message{(messages.Count == 1 ? "" : "s")}" +
                   (question is { Length: > 0 } ? $", the last question \"{Fmt.OneLine(question, 80)}\"." : ".");
        if (say is null)
        {
            Ui.Info(text);
        }
        else
        {
            say(text);
        }
        Events?.Notice(text);
    }

    /// <summary>A message added to the conversation: an answer with its tokens; a note (<paramref name="kind"/>) is Code Arena's own word to the model.</summary>
    private void Add(JsonObject message, TokenUsage? usage = null, string? kind = null)
    {
        Messages.Add(message);
        Session?.Message(message, message.Str("role") == "assistant" ? Model.Name : null, usage, kind);
    }

    /// <summary>What ends an answer the person stopped, as the model reads it back.</summary>
    internal const string StoppedMark = "(stopped by the person)";

    /// <summary>
    /// One turn: the person's message in, the final answer out. Cancelling stops it and keeps the history valid.
    /// It ends when the model answers: a command started with no time limit, or in the background, keeps running
    /// (listed, stoppable), and the model hears how it ended at the next step or turn. Ctrl+C or Stop also stops
    /// the commands this turn started with no time limit; those in the background run on. With no input, the turn
    /// carries on from what is there (a command that ended while nobody was asking: <see cref="ContinueAsync"/>).
    /// </summary>
    /// <param name="takeIn">Whether the web chat's news is taken in first (<see cref="BeforeTurn"/>); the IDE takes it in itself.</param>
    /// <param name="pieces">Lines chosen in the IDE's editor, sent with the message.</param>
    /// <param name="quote">The part of an earlier answer the message replies to: the model reads it before the message (<see cref="Quotes"/>).</param>
    /// <param name="uploaded">Files attached in the IDE, as <see cref="Uploads.ReadAsync"/> read them: their text after the message, their pictures with it.</param>
    public async Task<string> RunAsync(string? input, Spend turn, CancellationToken ct, bool takeIn = true, IReadOnlyList<Mentions.Piece>? pieces = null, string? quote = null,
        (string Section, JsonArray Pictures)? uploaded = null)
    {
        try
        {
            return await RunTurnAsync(input, turn, ct, takeIn, pieces, quote, uploaded);
        }
        finally
        {
            if (Depth == 0)
            {
                AfterTurn?.Invoke();
            }
        }
    }

    private async Task<string> RunTurnAsync(string? input, Spend turn, CancellationToken ct, bool takeIn, IReadOnlyList<Mentions.Piece>? pieces, string? quote,
        (string Section, JsonArray Pictures)? uploaded)
    {
        if (input is not null)
        {
            _followUps = 0;
        }
        else if (Depth == 0 && ++_followUps > MaxFollowUps)
        {
            // The model carried on from command after command with nobody there: it waits for the person now (the commands'
            // ends are told with their next message).
            Warn($"The model carried on {MaxFollowUps} times in a row from commands that ended, with no message from you: it waits for yours now.");
            return "";
        }
        if (takeIn && BeforeTurn is { } news && Depth == 0)
        {
            TakeIn(await news(ct));
        }
        var asked = input ?? "Carry on after the commands that ended";
        if (Depth == 0)
        {
            // @path: the file goes with the message.
            if (input is not null)
            {
                var (expanded, files) = Mentions.Expand(input, Context.Workspace, pieces);
                if (files.Count > 0)
                {
                    Ui.Info($"With the message: {string.Join(", ", files)}.");
                    Events?.Attached(files);
                    input = expanded;
                }
                // The files attached in the IDE come last: their text, and their pictures as parts of the message.
                if (uploaded is { } attached)
                {
                    input += attached.Section;
                }
                // The part of an answer it replies to goes first (the checkpoint keeps what was typed).
                input = Quotes.Compose(quote, input);
            }
            Checkpoints?.Begin(Messages.Count, asked);
            Context.Turn++;
        }
        var before = await SnapshotAsync(ct);
        var done = false;
        try
        {
            var answer = await TurnAsync(input, turn, ct, Depth == 0 ? uploaded?.Pictures : null);
            done = true;
            await CommitAsync(before, asked, ct);
            return answer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && Context.Jobs is { } jobs)
        {
            await StopJobsAsync(jobs, Context.Turn);
            throw;
        }
        catch (Exception e) when (Context.Jobs is { Running.Count: > 0 })
        {
            // Only the person stops a command with no time limit: a failed request to the model does not.
            Warn($"The model cannot carry on: {Fmt.OneLine(e.Message, 300)} The commands still running go on (/jobs lists them, /jobs stop N stops one); " +
                 "the model hears how each ended when it does, or with your next message.");
            throw;
        }
        finally
        {
            if (!done)
            {
                await KeepUncommittedAsync(before);
            }
        }
    }

    /// <summary>A turn of no new message: the model carries on from the commands that ended while nobody asked (their ends are its news).</summary>
    public Task<string> ContinueAsync(Spend turn, CancellationToken ct, bool takeIn = true) => RunAsync(null, turn, ct, takeIn);

    /// <summary>The repository as the turn begins, the files earlier turns left uncommitted counted as the harness's; null: no commits.</summary>
    private async Task<TurnCommits.Snapshot?> SnapshotAsync(CancellationToken ct)
    {
        if (CommitAs is null || Depth > 0 || await TurnCommits.TakeAsync(Context.Workspace.Root, ct) is not { } snapshot)
        {
            return null;
        }
        var dirty = snapshot.Dirty.Where(kv => !(_uncommitted.TryGetValue(kv.Key, out var ours) && ours == kv.Value)).ToDictionary(StringComparer.Ordinal);
        return snapshot with { Dirty = dirty };
    }

    /// <summary>
    /// Commits what the turn changed under Code Arena's name, and says so; a turn that did not end keeps its files for the next.
    /// The repository's hooks run as for any commit: after two seconds the person is told, and Ctrl+C or Stop skips the commit.
    /// </summary>
    private async Task CommitAsync(TurnCommits.Snapshot? before, string input, CancellationToken ct)
    {
        if (before is null || CommitAs is null)
        {
            return;
        }
        if (Depth == 0 && Context.Jobs?.Running.Any(j => !j.Background && j.Turn == Context.Turn) == true)
        {
            // A command the turn started still writes (a build, an install): its files are not taken half done. The turn
            // that carries on once it ends commits them.
            await KeepUncommittedAsync(before);
            return;
        }
        try
        {
            var committing = TurnCommits.CommitAsync(before, input, CommitAs, Model.Name, Session?.Id, ct);
            if (await Task.WhenAny(committing, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None)) != committing)
            {
                Ui.Info("Committing the turn's changes (the repository's hooks run)… Ctrl+C or Stop skips it.");
                Events?.Notice("Committing the turn's changes (the repository's hooks run)… Stop skips it.");
            }
            if (await committing is { } commit)
            {
                if (commit.Hash is not null)
                {
                    _uncommitted.Clear();
                }
                else if (commit.Problem is not null)
                {
                    await KeepUncommittedAsync(before);
                }
                var text = commit.Describe();
                Ui.Info(text);
                Events?.Notice(text);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Warn("The commit was skipped: the turn's changes stay uncommitted, and the next turn that ends commits them.");
            await KeepUncommittedAsync(before);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Warn($"The turn's changes are not committed: {Fmt.OneLine(e.Message, 200)}");
        }
    }

    /// <summary>A turn stopped or failed: what it changed stays uncommitted, and still counts as the harness's at the next turn.</summary>
    private async Task KeepUncommittedAsync(TurnCommits.Snapshot? before)
    {
        if (before is not null && await TurnCommits.ChangedAsync(before, CancellationToken.None) is { } changed)
        {
            foreach (var (path, hash) in changed)
            {
                _uncommitted[path] = hash;
            }
        }
    }

    /// <summary>
    /// The person stopped the turn: the commands it started with no time limit stop with it (those in the background run on),
    /// and their ends are said before the turn's.
    /// </summary>
    private static async Task StopJobsAsync(CommandJobs jobs, int turn)
    {
        var running = jobs.Running.Where(j => j.Turn == turn && !j.Background).ToList();
        foreach (var job in running)
        {
            job.Stop("by the person");
        }
        // A stopped command ends in a moment (its output read to the end): the watchers hear of it while the turn is still theirs.
        await Task.WhenAny(Task.WhenAll(running.Select(j => j.Done)), Task.Delay(TimeSpan.FromSeconds(5)));
        foreach (var job in running)
        {
            job.Reported = true;
        }
    }

    private async Task<string> TurnAsync(string? input, Spend turn, CancellationToken ct, JsonArray? pictures = null)
    {
        JsonObject? question = null;
        if (input is not null)
        {
            // Pictures go as parts of the message, after its words (as the web chat sends them).
            question = new JsonObject
            {
                ["role"] = "user",
                ["content"] = pictures is { Count: > 0 } ? new JsonArray([new JsonObject { ["type"] = "text", ["text"] = input }, .. pictures.Select(p => p!.DeepClone())]) : input,
            };
            Add(question);
        }
        var last = "";
        var interrupted = 0;
        var overflowed = false;
        var waitedForCache = false;
        for (var step = 0; step < MaxSteps; step++)
        {
            await MaybeCompactAsync(ct);
            ReportJobs();
            var printer = new Printer(Ui, Stream && Depth == 0, Events);
            Completion answer;
            Events?.Step();
            Ui.StartSpinner(step == 0 ? "Thinking" : "Working");
            try
            {
                answer = await AskAsync(printer, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Ui.StopSpinner();
                printer.End();
                // What was said before the stop stays, so the next turn follows from it.
                Add(new JsonObject { ["role"] = "assistant", ["content"] = printer.Said.Length > 0 ? printer.Said + "\n\n" + StoppedMark : StoppedMark });
                throw;
            }
            catch (GatewayException e) when (e is { ContextExceeded: true, SharedCache: true } && !waitedForCache && printer.Said.Length == 0)
            {
                // The engine's cache, shared by the requests it runs at once, was full: other requests filled it, not this one.
                // Asked again in a moment, as it was; a second time it is compacted as any refusal for the window is.
                Ui.StopSpinner();
                printer.End();
                Events?.StepDone(null);
                waitedForCache = true;
                Warn("The model's memory for the requests it answers at once was full: asking again in a moment.");
                await Task.Delay(SharedCacheWait, ct);
                step--;
                continue;
            }
            catch (GatewayException e) when (e.ContextExceeded)
            {
                // Too big for the window (the estimate was short of the engine's count): compacted, then asked once more.
                Ui.StopSpinner();
                printer.End();
                Events?.StepDone(null);
                if (overflowed || !await OverflowAsync(e, ct))
                {
                    DropQuestion(question);
                    throw new GatewayException($"The conversation does not fit the model's window, even compacted ({Fmt.OneLine(e.Message, 300)}). " +
                        "/compact or /clear makes room; a smaller request (fewer files at once) may fit.", e.Status) { ContextExceeded = true, PromptTokens = e.PromptTokens };
                }
                overflowed = true;
                step--;
                continue;
            }
            finally
            {
                Ui.StopSpinner();
            }
            overflowed = false;
            printer.End();
            Events?.StepDone(answer.Usage);
            if (answer.Usage is { } usage)
            {
                turn.Add(usage, Model.Info);
                if (!ReferenceEquals(turn, Total))
                {
                    Total.Add(usage, Model.Info);
                }
                Session?.Usage(usage);
                _knownTokens = usage.Prompt + usage.Completion;
                Learn(_sentChars, usage.Prompt);
            }
            if (answer.FinishReason == GatewayClient.Interrupted)
            {
                // The connection dropped mid-answer: what came is kept (when it said anything), and the model carries on from it.
                if (++interrupted > MaxInterruptions)
                {
                    Warn($"The connection dropped mid-answer {interrupted} times in this turn: stopping here. Send a message to carry on.");
                    if (answer.Text.Length > 0)
                    {
                        Add(answer.Message, answer.Usage);
                    }
                    return answer.Text;
                }
                Warn("The connection dropped while the model was answering: it carries on from where it stopped.");
                if (answer.Text.Length > 0)
                {
                    Add(answer.Message, answer.Usage);
                    Add(new JsonObject { ["role"] = "user", ["content"] = "(The connection dropped while you were answering: your answer so far is above. Carry on from where it stopped, without repeating it.)" },
                        kind: "note");
                }
                continue;
            }
            if (answer.Text.Length == 0 && answer.ToolCalls.Count == 0)
            {
                // Nothing to keep: an empty answer would only be one more message that says nothing (and some models refuse it).
                Warn(answer.FinishReason == "length"
                    ? "The model spent its whole output limit before saying anything: nothing of this answer is kept."
                    : "The model said nothing: nothing of this answer is kept.");
            }
            else
            {
                Add(answer.Message, answer.Usage);
                _knownCount = Messages.Count;
            }
            last = answer.Text;
            if (answer.ToolCalls.Count == 0)
            {
                if (answer.FinishReason == "length" && answer.Text.Length > 0)
                {
                    Warn("The answer was cut at the model's output limit.");
                }
                // A command still running goes on without the turn: the model hears how it ended next time, and a
                // command that ends while nobody asks starts a turn of its own (ContinueAsync).
                if (ReportJobs())
                {
                    continue;
                }
                return last;
            }
            await RunToolsAsync(answer.ToolCalls, ct);
        }
        Warn($"Stopped after {MaxSteps} steps.");
        return last;
    }

    /// <summary>How long a request waits before it is asked again when the engine's shared cache was full.</summary>
    internal static TimeSpan SharedCacheWait { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A request the window could not hold: its size is learned (the engine's count, else at least the window), and the
    /// conversation compacted (a summary, else the older messages left out); false when nothing could be made smaller.
    /// </summary>
    private async Task<bool> OverflowAsync(GatewayException e, CancellationToken ct)
    {
        var tokens = e.PromptTokens ?? Model.Context + 1;
        if (_sentChars > 0)
        {
            CharsPerToken = Math.Clamp(Math.Min(CharsPerToken, (double)_sentChars / tokens), 1.0, 8.0);
        }
        _knownTokens = 0;
        _knownCount = 0;
        Warn($"The request was too big for the model's window ({(e.PromptTokens is { } n ? $"{Fmt.Tokens(n)} tokens" : "the gateway said so")} " +
             $"for {Fmt.Tokens(Model.Context)}): compacting the conversation, then asking again.");
        return await CompactAsync(force: false, ct);
    }

    /// <summary>The person's message of a turn that could not be asked: out of the history, so a next try does not add to it.</summary>
    private void DropQuestion(JsonObject? question)
    {
        if (question is null || Messages.Count == 0 || !ReferenceEquals(Messages[^1], question))
        {
            return;
        }
        Messages.RemoveAt(Messages.Count - 1);
        _notes.RemoveAll(n => n.At > Messages.Count);
        // Out of the session as if gone back from (/rewind): what Arena has of it stays there, on a branch of its own.
        Session?.Rewind(Messages.Count);
        Events?.Compacted("The message that did not fit is out of the conversation: send it again, smaller, or after /compact.");
    }

    /// <summary>
    /// Tells the model of the commands with no time limit that ended since it last heard, each as a
    /// command_output call of its own with the result (a tool result is data, not the person's
    /// words); false when none did.
    /// </summary>
    private bool ReportJobs()
    {
        if (Context.Jobs?.TakeEnded() is not { Count: > 0 } ended)
        {
            return false;
        }
        foreach (var job in ended)
        {
            var id = $"call_job_{job.Id}_{Guid.NewGuid():N}"[..24];
            var args = $$"""{"job":{{job.Id}}}""";
            var tool = Tools.Find("command_output");
            var result = new ToolResult(job.Notice(), job.ExitCode != 0 || job.StoppedBy is not null) { Display = Ui.Dim($"job {job.Id}: {job.Status()}") };
            Events?.Step();
            Events?.StepDone(null);
            Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "command_output", ["arguments"] = args },
                }),
            });
            ShowCall("command_output", tool, Json.ParseObject(args));
            Events?.ToolCall(id, "command_output", tool, args);
            ShowResult("command_output", result, false);
            Events?.ToolResult(id, "command_output", 0, result, (job.Ended ?? DateTime.UtcNow) - job.Started, false);
            Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result.Text });
        }
        return true;
    }

    private void Warn(string text)
    {
        Ui.Warn(text);
        Events?.Notice(text);
    }

    private async Task<Completion> AskAsync(IStreamSink sink, CancellationToken ct)
    {
        AddNote();
        var request = new JsonObject
        {
            ["model"] = Model.Name,
            ["messages"] = Conversation(Messages.Count),
        };
        // The same tools in every mode, in the same order: the start of the request is what the engine has cached.
        var tools = Model.Info.Tools ? Tools.Offered().Select(t => (JsonNode)t.Spec()).ToArray() : [];
        if (tools.Length > 0)
        {
            request["tools"] = new JsonArray(tools);
            if (_parallelCalls)
            {
                request["parallel_tool_calls"] = true;
            }
        }
        if (Model.TemplateKwargs() is { } kwargs)
        {
            request["chat_template_kwargs"] = kwargs;
        }
        _sentChars = Chars(Messages.Count);
        var uncached = Uncached(_sentChars);
        try
        {
            return await Gateway.CompleteAsync(request, sink, ct, uncached);
        }
        catch (GatewayException e) when (e.Status == 400 && _parallelCalls && e.Message.Contains("parallel_tool_calls", StringComparison.OrdinalIgnoreCase))
        {
            // A backend that does not take the option: one call at a time, then.
            _parallelCalls = false;
            request.Remove("parallel_tool_calls");
            return await Gateway.CompleteAsync(request, sink, ct, uncached);
        }
    }

    /// <summary>
    /// Adds Code Arena's note when it says something other than the last one (the mode, the date, the branch or the tools
    /// changed): before the person's message when one is last, else after the last message. Notes stay where they were
    /// added, so the requests' start does not change; a compaction or a resume adds the note anew.
    /// </summary>
    private void AddNote()
    {
        if (Note?.Invoke() is not { Length: > 0 } note)
        {
            return;
        }
        _notes.RemoveAll(n => n.At > Messages.Count);
        if (_notes.Count > 0 && _notes[^1].Text == note)
        {
            return;
        }
        var at = Messages.Count > 0 && Messages[^1].Str("role") == "user" ? Messages.Count - 1 : Messages.Count;
        // One that nothing follows yet is replaced rather than kept beside the new one.
        _notes.RemoveAll(n => n.At >= at);
        _notes.Add((at, note));
    }

    /// <summary>The notes a request of the first <paramref name="count"/> messages carries: those before them, and those at the end when it is the whole conversation.</summary>
    private IEnumerable<(int At, string Text)> NotesUpTo(int count) => _notes.Where(n => n.At < count || (n.At == count && count == Messages.Count));

    /// <summary>A request's messages: the system prompt, then the first <paramref name="count"/> of the conversation with the notes where they were added.</summary>
    private JsonArray Conversation(int count)
    {
        var list = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() } };
        var notes = NotesUpTo(count).ToList();
        var n = 0;
        for (var i = 0; i < count; i++)
        {
            while (n < notes.Count && notes[n].At == i)
            {
                list.Add(new JsonObject { ["role"] = "user", ["content"] = notes[n++].Text });
            }
            // Without the thinking kept with an answer: some providers refuse it in what they read.
            list.Add(GatewayClient.Sendable(Messages[i]));
        }
        while (n < notes.Count)
        {
            list.Add(new JsonObject { ["role"] = "user", ["content"] = notes[n++].Text });
        }
        return list;
    }

    /// <summary>Questions first, one at a time; then every allowed call at once. Every call gets a result, in order.</summary>
    private async Task RunToolsAsync(JsonArray calls, CancellationToken ct)
    {
        var planned = new List<(string Id, string Name, ToolDef? Tool, JsonObject Args, string? Refusal, bool Declined)>();
        foreach (var call in calls.OfType<JsonObject>())
        {
            var id = call.Str("id") ?? "";
            var name = call["function"].Str("name") ?? "";
            var raw = call["function"].Str("arguments");
            var args = string.IsNullOrWhiteSpace(raw) ? new JsonObject() : Json.ParseObject(raw);
            var (tool, refusal) = Tools.ForCall(name);
            if (tool is null)
            {
                refusal = $"There is no tool named {name}. The tools: {string.Join(", ", Tools.Offered().Select(t => t.Name))}.";
            }
            else if (args is null)
            {
                refusal = $"The arguments for {name} were not valid JSON: {Fmt.OneLine(raw, 200)}";
            }
            ShowCall(name, tool, args);
            Events?.ToolCall(id, name, tool, string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var declined = false;
            if (refusal is null && tool is not null && args is not null)
            {
                try
                {
                    refusal = await Permissions.CheckAsync(tool, args, ct, id);
                    declined = refusal is not null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    FinishCalls(planned.Select(p => p.Id).Concat(calls.OfType<JsonObject>().Select(c => c.Str("id") ?? "")).Distinct(), "Stopped by the person before it ran.");
                    throw;
                }
            }
            planned.Add((id, name, tool, args ?? [], refusal, declined));
        }

        var results = new ToolResult[planned.Count];
        if (planned.Any(p => p.Refusal is null))
        {
            Ui.StartSpinner("Running");
        }
        var running = planned.Select(async (p, i) =>
        {
            ToolResult result;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (p.Refusal is not null || p.Tool is null)
            {
                result = new ToolResult(p.Refusal ?? "Not run.", true) { Display = Ui.Yellow(Fmt.OneLine(p.Refusal, 140)) };
            }
            else
            {
                try
                {
                    result = await p.Tool.Run(p.Args, Context, ct);
                }
                catch (ToolError e)
                {
                    result = new ToolResult("Error: " + e.Message, true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    result = new ToolResult("Stopped by the person while it ran.", true);
                }
                catch (McpException e)
                {
                    result = new ToolResult($"Error from the {p.Tool.Server} tools: {e.Message}", true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or HttpRequestException)
                {
                    result = new ToolResult($"Error: {e.Message}", true);
                }
            }
            results[i] = result;
            ShowResult(p.Name, result, planned.Count > 1);
            Events?.ToolResult(p.Id, p.Name, i, result, clock.Elapsed, p.Declined);
        }).ToList();
        await Task.WhenAll(running);
        var limit = MaxToolChars;
        for (var i = 0; i < planned.Count; i++)
        {
            var text = results[i].Text;
            if (text.Length > limit)
            {
                text = text[..(limit * 2 / 3)] + $"\n\n… ({text.Length - limit:N0} characters cut: ask for less, e.g. a line range or a narrower search) …\n\n" + text[^(limit / 3)..];
            }
            Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = planned[i].Id, ["content"] = text });
        }
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>About a third of the window, in characters, and never more than 40,000.</summary>
    private int MaxToolChars => Math.Clamp(Model.Context * 3 / 2, 4_000, 40_000);

    private void FinishCalls(IEnumerable<string> ids, string text)
    {
        foreach (var id in ids)
        {
            Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = text });
        }
    }

    private string Prefix => Depth == 0 ? "" : new string(' ', Depth * 2) + "⎿ ";

    private void ShowCall(string name, ToolDef? tool, JsonObject? args)
    {
        var summary = tool?.Summary is { } s && args is not null ? s(args) : args is null ? "" : Fmt.OneLine(Json.Line(args), 80);
        var label = tool?.Server is { } server && server != "arena" ? $"{name} ({server})" : name;
        Ui.Line($"{Prefix}{Ui.Magenta("●")} {Ui.Bold(label)} {Ui.Dim(Fmt.OneLine(summary, 110))}");
    }

    /// <summary>A result under a ⎿; named when several calls ran at once, since they finish in any order.</summary>
    private void ShowResult(string name, ToolResult result, bool named)
    {
        var indent = new string(' ', Depth * 2) + "  ";
        string shown;
        if (result.Display is { } display)
        {
            shown = display;
        }
        else
        {
            var head = Fmt.Head(result.Text, Depth == 0 ? 4 : 1, 140);
            shown = result.Error ? Ui.Red(head) : Ui.Dim(head);
        }
        if (Depth > 0)
        {
            shown = Fmt.OneLine(shown, 120);
        }
        var lines = shown.Split('\n');
        var first = (named ? Ui.Dim(name + " · ") : "") + lines[0];
        Ui.Line(string.Join('\n', lines.Skip(1).Select(l => indent + "  " + l).Prepend(indent + Ui.Dim("⎿ ") + first)));
    }

    /// <summary>
    /// Compacts when the next request would pass the threshold (80% of the window unless the person chose otherwise, and
    /// never so late that the answer has no room). After a compaction that could not bring it under (the turn alone is that
    /// big), not again until it grows by a tenth of the window. It never fails the turn: a summary that cannot be made
    /// leaves the older messages out instead (<see cref="CompactAsync"/>).
    /// </summary>
    public async Task<bool> MaybeCompactAsync(CancellationToken ct)
    {
        var at = Threshold;
        var estimate = Estimate();
        // Never later than 95% of the window (nor past the answer's room), whatever a compaction before could not bring it under.
        if (Messages.Count < 3 || estimate < Math.Min(Math.Max(at, _compactedAt + Model.Context / 10.0), Math.Min(Model.Context * 0.95, Limit)))
        {
            return false;
        }
        if (Compaction.At == Compaction.Off || estimate < Model.Context * Compaction.At / 100.0)
        {
            // Not the person's threshold: the request would not fit otherwise.
            Warn($"Compacting: the next request (~{Fmt.Tokens(estimate)} tokens) would not fit the window ({Fmt.Tokens(Model.Context)}) with room for the answer.");
        }
        var changed = await CompactAsync(force: false, ct);
        var now = Estimate();
        _compactedAt = now >= at ? now : 0;
        return changed;
    }

    /// <summary>The size a compaction left the conversation at when it stayed over the threshold (0: it got under).</summary>
    private long _compactedAt;

    /// <summary>Where compaction starts, in tokens: the person's share of the window, within <see cref="Limit"/> (off: at it).</summary>
    private double Threshold => Compaction.At == Compaction.Off ? Limit : Math.Min(Model.Context * Compaction.At / 100.0, Limit);

    /// <summary>The most a request may hold, in tokens: the window less the room the answer needs.</summary>
    public long Limit => Model.Context - Reserve;

    /// <summary>The room left for the answer: the model's longest answer as the gateway says (4,096 when it does not), at most an eighth of the window.</summary>
    private long Reserve => Math.Min(Model.Info.MaxOutput is > 0 and var most ? most : DefaultReserve, Model.Context / 8);

    private const int DefaultReserve = 4_096;

    /// <summary>How every compaction's first message starts (the IDE shows such a message folded, as a compaction).</summary>
    private const string SummaryMark = "[The conversation so far, summarized";

    /// <summary>The summary a compaction puts first, and the model's answer to it.</summary>
    internal const string SummaryHead = SummaryMark + " to fit the model's window]";

    /// <summary>What a compaction puts first when the model's summary could not be made: the older messages left out, in brief.</summary>
    private const string OmittedHead = SummaryMark + " mechanically to fit the model's window: the model's summary could not be made]";

    /// <summary>The person's message and the earlier steps of the same turn, summarized when the turn alone fills the window.</summary>
    private const string TurnHead = SummaryMark + " to fit the model's window: the person's message, and your earlier steps of this turn]";

    private const string StepsMark = "[Your earlier steps of this turn, in brief]";

    internal const string Understood = "Understood: I have the summary and carry on from there.";

    /// <summary>How the request for a summary ends: the conversation as the model has it, then this.</summary>
    internal const string SummaryAsk = "[Code Arena asks for a summary, to fit the model's window]";

    private const string SummaryInstruction = SummaryAsk + "\n\nSummarize the conversation above so you can carry on from the summary alone: it replaces everything above. " +
        "Keep the person's goals and instructions, decisions made, files read or changed (with paths), commands run and what they showed, errors met, " +
        "the state of the work and the next steps. Be concrete and brief (at most 400 words). Invent nothing. Do not call a tool: answer with the summary alone.";

    private const string TurnInstruction = SummaryAsk + "\n\nSummarize what you did in this turn so far, since the person's last message (earlier summaries of it included), " +
        "so you can carry on from the summary alone: it replaces those steps. Keep the files read or changed (with paths), commands run and what they showed, " +
        "errors met, what is done and what is left. Be concrete and brief (at most 300 words). Invent nothing. Do not call a tool: answer with the summary alone.";

    /// <summary>The longest summary asked for, in tokens.</summary>
    private const int SummaryTokens = 2_048;

    /// <summary>
    /// The most a request for a summary sends, in tokens; null: what the window holds beside the summary. Past it, its
    /// oldest messages are left out (an earlier summary first among them is kept).
    /// </summary>
    public long? SummaryBudget { get; set; }

    /// <summary>Summaries that failed in a row, and the compactions still to do without one before the next try.</summary>
    private int _summaryFailures;
    private int _summariesSkipped;

    private static bool IsSummary(List<JsonObject> older) =>
        older.Count == 2 && older[0].Str("role") == "user" && older[0].Str("content")?.StartsWith(SummaryMark, StringComparison.Ordinal) == true;

    /// <summary>The characters a token holds in this conversation, as learned from the gateway's counts (3.5 until it counted).</summary>
    public double CharsPerToken { get; private set; } = DefaultCharsPerToken;

    private const double DefaultCharsPerToken = 3.5;

    /// <summary>Learns <see cref="CharsPerToken"/> from a request's size in characters and its tokens as the gateway or engine counted them.</summary>
    private void Learn(long chars, long tokens)
    {
        if (chars > 0 && tokens > 0)
        {
            CharsPerToken = Math.Clamp((double)chars / tokens, 1.0, 8.0);
        }
    }

    /// <summary>
    /// Tokens the next request will hold: the last count the gateway gave, plus what was added since; with no count since the
    /// last compaction or resume, the whole of it. Characters become tokens at <see cref="CharsPerToken"/>.
    /// </summary>
    public long Estimate()
    {
        var notes = _notes.Where(n => n.At <= Messages.Count).ToList();
        var added = Messages.Skip(_knownCount).Sum(Size) + notes.Where(n => n.At >= _knownCount).Sum(n => n.Text.Length + 10);
        if (_knownTokens > 0)
        {
            return _knownTokens + (long)(added / CharsPerToken);
        }
        return (long)((SystemPrompt().Length + ToolChars() + added) / CharsPerToken);
    }

    private long ToolChars() => Model.Info.Tools ? Tools.Offered().Sum(t => (long)Json.Line(t.Spec()).Length) : 0;

    /// <summary>A request's size in characters: the system prompt, the tools and the first <paramref name="count"/> messages with their notes.</summary>
    private long Chars(int count) =>
        SystemPrompt().Length + ToolChars() + Messages.Take(count).Sum(Size) + NotesUpTo(count).Sum(n => n.Text.Length + 10);

    private static int Size(JsonObject m) => (m.Str("content")?.Length ?? 0) + (m["tool_calls"] is JsonArray calls ? Json.Line(calls).Length : 0) + 10;

    /// <summary>
    /// Makes room in the window. The older part of the history is replaced with a summary the model writes, and the recent
    /// part stays whole, from a message of the person's, within the compaction's target (a quarter of the window unless the
    /// person chose otherwise). When the summary cannot be made, the older part is left out with a note of what it held
    /// (and a summary is not tried again for a few compactions). When nothing older is left (one long turn), the earlier
    /// steps of the turn are summarized; failing that, long results and call arguments are cut. Forced (/compact):
    /// everything is summarized, and a summary that fails leaves the conversation as it was (the error is thrown).
    /// False when nothing changed: then nothing is written and the page is not reset.
    /// </summary>
    public async Task<bool> CompactAsync(bool force, CancellationToken ct)
    {
        var budget = Model.Context * Compaction.Target / 100.0 * CharsPerToken;
        var keepFrom = Messages.Count;
        if (!force)
        {
            double size = 0;
            for (var i = Messages.Count - 1; i >= 0; i--)
            {
                size += Size(Messages[i]);
                if (size > budget)
                {
                    break;
                }
                if (Messages[i].Str("role") == "user")
                {
                    keepFrom = i;
                }
            }
            if (keepFrom == Messages.Count)
            {
                // The recent part alone is too big: from the last message of the person's, then.
                keepFrom = Messages.FindLastIndex(m => m.Str("role") == "user");
            }
        }
        keepFrom = Math.Max(keepFrom, 0);
        var older = Messages.Take(keepFrom).ToList();
        var kept = Messages.Skip(keepFrom).ToList();
        if (older.Count == 0 && force)
        {
            return false;
        }
        if (!force && (IsSummary(older) || older.Count < 2))
        {
            // Nothing older worth summarizing (summarizing the last summary again would only lose more): the earlier steps
            // of this turn, when it has several; else the long results and arguments are cut.
            if (await CompactTurnAsync(budget, ct))
            {
                return true;
            }
            if (!Shrink(Messages))
            {
                return false;
            }
            Saved(null, "Long tool results and call arguments were cut to fit the model's window.");
            return true;
        }
        Events?.Notice("Compacting the conversation to fit the model's window…");
        var (summary, failure) = await TrySummaryAsync(keepFrom, SummaryInstruction, force, ct);
        var text = summary ?? Digest(older, failure!);
        Messages =
        [
            new JsonObject { ["role"] = "user", ["content"] = summary is not null ? SummaryHead + "\n\n" + summary : OmittedHead + "\n\n" + text },
            new JsonObject { ["role"] = "assistant", ["content"] = Understood },
            .. kept,
        ];
        if (Estimate() > Threshold)
        {
            Shrink(Messages);
        }
        // Arena keeps the summary where the web's own compaction keeps its own: the web chat goes on from it too.
        Session?.Summarized(text, kept.Count);
        Saved(kept.Count, summary is not null
            ? $"Compacted: {older.Count} messages summarized, {kept.Count} kept."
            : $"Compacted without a summary ({failure}): {older.Count} older messages left out, in brief, and {kept.Count} kept.");
        return true;
    }

    /// <summary>
    /// One long turn (no message of the person's since): its earlier steps are summarized, the person's message and the
    /// latest steps kept (within the budget, in characters); false when it has too few steps for that.
    /// </summary>
    private async Task<bool> CompactTurnAsync(double budget, CancellationToken ct)
    {
        var q = Messages.FindLastIndex(m => m.Str("role") == "user");
        if (q < 0)
        {
            return false;
        }
        // Each step starts with the model's answer: a cut there keeps every call with its result.
        var starts = Enumerable.Range(q + 1, Messages.Count - q - 1).Where(i => Messages[i].Str("role") == "assistant").ToList();
        if (starts.Count < 2)
        {
            return false;
        }
        var cut = starts[^1];
        double size = Messages.Skip(cut).Sum(Size);
        for (var j = starts.Count - 2; j >= 1; j--)
        {
            size += Messages.Skip(starts[j]).Take(starts[j + 1] - starts[j]).Sum(Size);
            if (size > budget)
            {
                break;
            }
            cut = starts[j];
        }
        Events?.Notice("Compacting this turn's earlier steps to fit the model's window…");
        var (summary, failure) = await TrySummaryAsync(cut, TurnInstruction, false, ct);
        var head = Messages[q].Str("content") ?? "";
        var (question, before) = Split(head);
        var steps = Messages.Skip(q + 1).Take(cut - q - 1).ToList();
        var said = summary ?? (before is null ? "" : before + "\n\n") + StepsDigest(steps, failure!);
        Messages =
        [
            .. Messages.Take(q),
            new JsonObject { ["role"] = "user", ["content"] = $"{TurnHead}\n\n{question}\n\n{StepsMark}\n\n{said}" },
            .. Messages.Skip(cut),
        ];
        if (Estimate() > Threshold)
        {
            Shrink(Messages);
        }
        // What follows the turn's summary is the conversation's last word for word; what comes before it stands as a summary.
        Saved(Messages.Count - q - 1, summary is not null
            ? $"Compacted this turn: {steps.Count} messages of its earlier steps summarized, the latest {Messages.Count - q - 1} kept."
            : $"Compacted this turn without a summary ({failure}): {steps.Count} messages of its earlier steps left out, in brief.");
        return true;

        // The person's message, and the steps a summary of the turn before had: from such a summary, or the message itself.
        static (string Question, string? Steps) Split(string text)
        {
            if (!text.StartsWith(TurnHead, StringComparison.Ordinal))
            {
                return (text, null);
            }
            var rest = text[TurnHead.Length..].TrimStart('\n');
            var at = rest.LastIndexOf("\n\n" + StepsMark + "\n\n", StringComparison.Ordinal);
            return at < 0 ? (rest, null) : (rest[..at], rest[(at + StepsMark.Length + 4)..]);
        }
    }

    /// <summary>The model's summary of the conversation up to <paramref name="upTo"/>, or why there is none (and none is tried for a few compactions).</summary>
    private async Task<(string? Summary, string? Failure)> TrySummaryAsync(int upTo, string instruction, bool force, CancellationToken ct)
    {
        if (!force && _summariesSkipped > 0)
        {
            _summariesSkipped--;
            return (null, "a summary failed a short while ago, and is not tried again yet");
        }
        Ui.StartSpinner("Compacting the conversation");
        try
        {
            var summary = await SummarizeAsync(upTo, instruction, ct);
            _summaryFailures = 0;
            _summariesSkipped = 0;
            return (summary, null);
        }
        catch (Exception e) when (!force && !(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Not again at once: after 2, then 4, then 8 compactions without one.
            _summaryFailures++;
            _summariesSkipped = 1 << Math.Min(_summaryFailures, 3);
            var why = Fmt.OneLine(e.Message, 200);
            Warn($"The model's summary could not be made ({why}): the older messages are left out instead, with a note of what they held. " +
                 $"A summary is tried again after {_summariesSkipped} compactions.");
            return (null, why);
        }
        finally
        {
            Ui.StopSpinner();
        }
    }

    /// <summary>
    /// Asks the model for a summary as a continuation of the conversation it has read: the same system prompt, tools and
    /// messages up to <paramref name="upTo"/> (the engine has them cached), then the instruction. Past the budget, the
    /// oldest messages are left out, from a message of the person's.
    /// </summary>
    private async Task<string> SummarizeAsync(int upTo, string instruction, CancellationToken ct)
    {
        var messages = Conversation(upTo);
        var answer = Math.Min(SummaryTokens, Model.Context / 4);
        var budget = (long)(Math.Min(SummaryBudget ?? Model.Context - answer, Model.Context - answer) * CharsPerToken);
        var chars = Chars(upTo) + instruction.Length;
        // A window so small that the tools fill most of the budget: the summary is asked without them (the tools' start
        // is not cached for such a request anyway), so the messages have room.
        var tools = Model.Info.Tools ? Tools.Offered() : [];
        if (SystemPrompt().Length + ToolChars() > budget / 2)
        {
            chars -= ToolChars();
            tools = [];
        }
        var left = 0;
        // The oldest go first (after an earlier summary, which holds what came before them), each with what follows it up
        // to the next message of the person's, so every call keeps its result.
        var from = messages.Count > 3 && messages[1].Str("content")?.StartsWith(SummaryMark, StringComparison.Ordinal) == true ? 3 : 1;
        while (chars > budget && messages.Count > from + 1)
        {
            do
            {
                chars -= Size((JsonObject)messages[from]!);
                messages.RemoveAt(from);
                left++;
            }
            while (messages.Count > from && messages[from].Str("role") != "user");
        }
        if (left > 0)
        {
            messages.Insert(from, new JsonObject { ["role"] = "user", ["content"] = $"(The {left} oldest messages of the conversation are left out here, to fit the model's window.)" });
            messages.Insert(from + 1, new JsonObject { ["role"] = "assistant", ["content"] = "Understood." });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = instruction });
        var request = new JsonObject { ["model"] = Model.Name, ["messages"] = messages, ["max_tokens"] = answer };
        if (tools.Count > 0)
        {
            request["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)t.Spec())]);
        }
        if (Model.Info.Thinking)
        {
            request["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        }
        var summary = await Gateway.CompleteAsync(request, null, ct, Uncached(chars));
        if (summary.Usage is { } usage)
        {
            Total.Add(usage, Model.Info);
            Session?.Usage(usage);
            Learn(chars, usage.Prompt);
        }
        return summary.Text.Trim() is { Length: > 0 } s ? s : throw new InvalidOperationException("the model answered with no summary");
    }

    /// <summary>About how many tokens of a request of this many characters the engine has not cached: what was added since its last count.</summary>
    private long Uncached(long chars) => Math.Max(0, (long)(chars / CharsPerToken) - _knownTokens);

    /// <summary>The compaction is done: written to the session (as the whole new history), said, and the page shown it.</summary>
    /// <param name="raw">How many of the last messages are the conversation's own, word for word (the rest stands for what came before); null: as before.</param>
    private void Saved(int? raw, string said)
    {
        Session?.Compacted(Messages, raw);
        _knownTokens = 0;
        _knownCount = 0;
        _notes.Clear();
        Ui.Info(said);
        Events?.Compacted(said);
    }

    /// <summary>
    /// Long tool results and long arguments of the model's calls (a file written whole), except in the last two steps, cut
    /// to their start: a last resort when the recent part is too big. False when nothing was long enough to cut.
    /// </summary>
    private static bool Shrink(List<JsonObject> messages)
    {
        var changed = false;
        var results = messages.Select((m, i) => (m, i)).Where(x => x.m.Str("role") == "tool").Select(x => x.i).ToList();
        foreach (var i in results.Take(Math.Max(0, results.Count - 2)))
        {
            if (messages[i].Str("content") is { Length: > 2000 } text)
            {
                messages[i]["content"] = text[..2000] + "\n… (cut to save room)";
                changed = true;
            }
        }
        var answers = messages.Select((m, i) => (m, i)).Where(x => x.m["tool_calls"] is JsonArray { Count: > 0 }).Select(x => x.i).ToList();
        foreach (var i in answers.Take(Math.Max(0, answers.Count - 2)))
        {
            foreach (var call in ((JsonArray)messages[i]["tool_calls"]!).OfType<JsonObject>())
            {
                if (call["function"] is JsonObject f && f.Str("arguments") is { Length: > 2000 } args && ShortArguments(args) is { } shorter && shorter.Length < args.Length)
                {
                    f["arguments"] = shorter;
                    changed = true;
                }
            }
        }
        return changed;
    }

    /// <summary>A call's arguments with each long text in them cut to its start, still JSON (engines read them as such); null when they are not.</summary>
    internal static string? ShortArguments(string args)
    {
        if (Json.ParseObject(args) is not { } parsed)
        {
            return null;
        }
        Cut(parsed);
        return Json.Line(parsed);

        static void Cut(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var key in o.Select(kv => kv.Key).ToList())
                    {
                        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 1000)
                        {
                            o[key] = s[..600] + $"\n… ({s.Length - 600:N0} characters cut to save room)";
                        }
                        else
                        {
                            Cut(o[key]);
                        }
                    }
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 1000)
                        {
                            a[i] = s[..600] + $"\n… ({s.Length - 600:N0} characters cut to save room)";
                        }
                        else
                        {
                            Cut(a[i]);
                        }
                    }
                    break;
            }
        }
    }

    /// <summary>What older messages held, when the model could not summarize them: the summary before them, the person's messages, the files changed, the commands run, the last answer.</summary>
    private string Digest(List<JsonObject> older, string failure)
    {
        var sb = new StringBuilder($"{older.Count} older messages were left out here to fit the model's window (no summary was made: {failure}). In brief:\n");
        if (older.Count > 0 && older[0].Str("content") is { } first && first.StartsWith(SummaryMark, StringComparison.Ordinal))
        {
            var start = first.IndexOf("\n\n", StringComparison.Ordinal);
            sb.Append("\nThe summary before them:\n\n").Append(Fmt.Head(start < 0 ? first : first[(start + 2)..], 120, 400)).Append('\n');
        }
        var asked = older.Where(m => m.Str("role") == "user" && m.Str("content")?.StartsWith(SummaryMark, StringComparison.Ordinal) != true).Select(m => Fmt.OneLine(Mentions.Typed(Quotes.Split(m.Str("content") ?? "").Text), 300)).TakeLast(15).ToList();
        if (asked.Count > 0)
        {
            sb.Append("\nThe person's messages:\n").Append(string.Join("", asked.Select(a => $"- {a}\n")));
        }
        var (files, commands) = Touched(older);
        if (files.Count > 0)
        {
            sb.Append("\nFiles changed: ").Append(string.Join(", ", files.TakeLast(40))).Append('\n');
        }
        if (commands.Count > 0)
        {
            sb.Append("\nCommands run:\n").Append(string.Join("", commands.TakeLast(15).Select(c => $"- {c}\n")));
        }
        if (older.LastOrDefault(m => m.Str("role") == "assistant" && m.Str("content") is { Length: > 0 } && m.Str("content") != Understood) is { } answer)
        {
            sb.Append("\nYour last answer among them: ").Append(Fmt.OneLine(answer.Str("content"), 800)).Append('\n');
        }
        sb.Append("\nAsk the person, or read the files again, for what you need of it.");
        return sb.ToString();
    }

    /// <summary>The steps of a turn in brief, when the model could not summarize them: each call and how it went.</summary>
    private string StepsDigest(List<JsonObject> steps, string failure)
    {
        var lines = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in steps)
        {
            if (m.Str("role") == "assistant")
            {
                if (m.Str("content") is { Length: > 0 } text)
                {
                    lines.Add("- You said: " + Fmt.OneLine(text, 200));
                }
                foreach (var call in (m["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var name = call["function"].Str("name") ?? "";
                    names[call.Str("id") ?? ""] = name;
                    var args = Json.ParseObject(call["function"].Str("arguments"));
                    var summary = Tools.Find(name)?.Summary is { } s && args is not null ? s(args) : Fmt.OneLine(call["function"].Str("arguments"), 120);
                    lines.Add($"- You called {name} {Fmt.OneLine(summary, 150)}");
                }
            }
            else if (m.Str("role") == "tool")
            {
                lines.Add($"  → {Fmt.OneLine(m.Str("content"), 150)}");
            }
        }
        var kept = lines.TakeLast(60).ToList();
        return $"({steps.Count} messages of your earlier steps were left out to fit the model's window; no summary was made: {failure}. In brief:)\n" +
               (kept.Count < lines.Count ? $"- … ({lines.Count - kept.Count} lines before these)\n" : "") + string.Join('\n', kept);
    }

    /// <summary>The files the calls among these messages changed, and the commands they ran.</summary>
    private (List<string> Files, List<string> Commands) Touched(List<JsonObject> messages)
    {
        var files = new List<string>();
        var commands = new List<string>();
        foreach (var call in messages.SelectMany(m => (m["tool_calls"] as JsonArray ?? []).OfType<JsonObject>()))
        {
            var tool = Tools.Find(call["function"].Str("name") ?? "");
            var args = Json.ParseObject(call["function"].Str("arguments"));
            if (tool?.Kind == ToolKind.Edit && (args.Str("path") ?? args.Str("file_path")) is { Length: > 0 } path)
            {
                files.Remove(path);
                files.Add(path);
            }
            else if (tool?.Kind == ToolKind.Shell && args.Str("command") is { Length: > 0 } command)
            {
                commands.Add(Fmt.OneLine(command, 150));
            }
        }
        return (files, commands);
    }

    /// <summary>Streams the answer to the terminal: reasoning dimmed, then the text; and to the web interface when it listens.</summary>
    private sealed class Printer(Ui ui, bool show, IAgentEvents? events) : IStreamSink
    {
        private bool _reasoning;
        private bool _any;
        private readonly StringBuilder _said = new();

        public string Said => _said.ToString();

        public void Reasoning(string text)
        {
            events?.Reasoning(text);
            if (!show)
            {
                return;
            }
            if (!_reasoning)
            {
                ui.Write(ui.Dim("thinking: "));
                _reasoning = true;
            }
            ui.Write(ui.Dim(text));
            _any = true;
        }

        public void Text(string text)
        {
            _said.Append(text);
            events?.Text(text);
            if (!show)
            {
                return;
            }
            if (_reasoning)
            {
                ui.Write("\n\n");
                _reasoning = false;
            }
            ui.Write(text);
            _any = true;
        }

        public void End()
        {
            if (_any)
            {
                ui.EndLine();
            }
        }
    }
}
