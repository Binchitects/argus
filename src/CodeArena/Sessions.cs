using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// A saved session as it is listed: which, where, when, how it began, the chat in Arena it is kept in step with (null:
/// none), whether that chat was deleted there (it is not kept in step any more), and how many of its messages Arena lacks.
/// </summary>
internal sealed record SessionSummary(string Id, string File, string? Cwd, DateTime Updated, string Preview, int Messages, string? Conversation = null,
    bool Unlinked = false, int Waiting = 0)
{
    /// <summary>Its plan as it last stood.</summary>
    public WorkPlan Plan { get; init; } = WorkPlan.None;

    /// <summary>Arena lacks some of it: never sent, or sent in part (its chat not deleted there).</summary>
    public bool Unsent => Waiting > 0 && !Unlinked;
}

/// <summary>
/// A message of the session as it goes to its chat in Arena, or came from it, in the chat's order: each with Arena's id
/// for it once Arena has it.
/// </summary>
internal sealed class SyncEntry
{
    /// <summary>The message in the model's own shape.</summary>
    public required JsonObject Message { get; init; }
    /// <summary>Arena's id for it; null: not sent yet; empty: Arena has it, but its id was never known (a session from before ids).</summary>
    public string? Id { get; set; }
    /// <summary>Taken in from the web chat.</summary>
    public bool Web { get; init; }
    /// <summary>Its number among the session's messages: Arena knows it as session:number (its ref), so a message sent twice is one.</summary>
    public int Key { get; init; }
    public DateTimeOffset At { get; init; }
    /// <summary>How long it took: an answer, its request to the model; a tool result, the tool's run. Null: not measured.</summary>
    public long? Ms { get; init; }
    /// <summary>The model that wrote it (an answer).</summary>
    public string? Model { get; init; }
    /// <summary>An answer's tokens, when the gateway said.</summary>
    public TokenUsage? Usage { get; init; }
    /// <summary>"note": a message of Code Arena's own to the model (the connection dropped), not the person's words.</summary>
    public string? Kind { get; init; }
    /// <summary>How an answer from the web ended, when not as it should: "stopped", "failed", "declined".</summary>
    public string? Status { get; init; }
    /// <summary>A tool result's sub-agents' work (task), as the page shows it: kept beside the message, never sent to the model.</summary>
    public JsonArray? Agents { get; init; }
    /// <summary>What filled the request an answer was written for, by kind in characters (<see cref="Agent.Measure"/>): the page's context gauge.</summary>
    public JsonObject? Context { get; init; }
    /// <summary>The person's rating of this answer, as Arena kept it (thumbs in the IDE); null: none.</summary>
    public JsonObject? Rating { get; set; }

    /// <summary>A tool result's tool: the call it answers, in the messages before it.</summary>
    public static string? ToolName(IReadOnlyList<SyncEntry> stream, int index)
    {
        if (stream[index].Message.Str("tool_call_id") is not { } id)
        {
            return null;
        }
        for (var i = index - 1; i >= 0; i--)
        {
            if (stream[i].Message["tool_calls"] is JsonArray calls && calls.OfType<JsonObject>().FirstOrDefault(c => c.Str("id") == id) is { } call)
            {
                return call["function"].Str("name");
            }
        }
        return stream[index].Message.Str("name");
    }
}

/// <summary>A saved session read back: its messages (after the last compaction), model, plan and tokens.</summary>
internal sealed class SessionData
{
    public string Id { get; init; } = "";
    public string? Cwd { get; set; }
    public string? Model { get; set; }
    /// <summary>What the model reads: a summary of what came before <see cref="From"/>, if any, then <see cref="Stream"/> from there.</summary>
    public List<JsonObject> Messages { get; } = [];
    /// <summary>The agent's plan; how many times it changed, and the change Arena has (its chat's plan is sent once Arena has the messages).</summary>
    public WorkPlan Plan { get; set; } = WorkPlan.None;
    public int PlanVersion { get; set; }
    public int PlanSent { get; set; }
    /// <summary>The Arena assistant the session works as (its instructions, model, tools and skills as picked); null: none.</summary>
    public JsonObject? Assistant { get; set; }
    /// <summary>The session's goal (goal mode) as it stands, the last line of it counts; null: none. Arena is sent it with the chat.</summary>
    public JsonObject? Goal { get; set; }
    public int GoalVersion { get; set; }
    public int GoalSent { get; set; }
    /// <summary>The groups of remote tools the person turned off in the session, and those the model loaded (<see cref="ToolDemand"/>).</summary>
    public IReadOnlyList<string> ToolsOff { get; set; } = [];
    public IReadOnlyList<string> ToolsLoaded { get; set; } = [];
    public long Prompt { get; set; }
    public long Cached { get; set; }
    public long Completion { get; set; }
    /// <summary>Each answer's (by its place in Messages) model and tokens, for the web interface.</summary>
    public Dictionary<int, (string? Model, TokenUsage? Usage)> Answers { get; } = [];
    /// <summary>Each tool result's (by its place in Messages) sub-agents' work, for the web interface.</summary>
    public Dictionary<int, JsonArray> Agents { get; } = [];
    /// <summary>The chat in Arena this session is kept in step with; null: none yet (or it was deleted there).</summary>
    public string? Conversation { get; set; }
    /// <summary>The chat in Arena was deleted: the session is no longer kept in step.</summary>
    public bool Unlinked { get; set; }
    /// <summary>The project in Arena its requests count to (chosen at its start, by /project, or its repository's); null: none.</summary>
    public WorkProject? WorkProject { get; set; }
    /// <summary>
    /// Every message of the conversation in the order of its chat in Arena, compactions aside (the chat keeps what was
    /// summarized): those this session added, and those taken in from the web.
    /// </summary>
    public List<SyncEntry> Stream { get; } = [];
    /// <summary>Where in <see cref="Stream"/> what the model reads word for word starts (before it: a summary, or nothing).</summary>
    public int From { get; set; }
    /// <summary>
    /// Each compaction's summary, by the place in <see cref="Stream"/> of the last message it covers: the IDE shows the whole
    /// conversation, marked where it was compacted (only what the model reads is compacted).
    /// </summary>
    public Dictionary<int, string> Marks { get; } = [];
    /// <summary>The session went back to before a turn Arena has (/rewind): what it adds next goes on a branch of its own there.</summary>
    public bool Branch { get; set; }
    /// <summary>A compaction's summary Arena has not had: of <see cref="Stream"/> up to UpTo.</summary>
    public (int UpTo, string Text)? Summary { get; set; }
    /// <summary>The web's summary last taken in (the id of the last message it covers): not taken in again.</summary>
    public string? WebSummary { get; set; }
    /// <summary>How many message lines the file has (the next one's number).</summary>
    public int Keys { get; set; }
    /// <summary>The last answer's tokens since the last compaction, and how many of <see cref="Messages"/> its request and answer covered; null: none counted.</summary>
    public (TokenUsage Usage, int Count)? Known { get; set; }
    /// <summary>The characters a token held when the session last compacted (<see cref="Agent.CharsPerToken"/>): resumed with no count since, it estimates as before.</summary>
    public double? CharsPerToken { get; set; }

    /// <summary>A message line's body as the list keeps it: none (only what its line says before the message counts there).</summary>
    private static readonly JsonObject Unread = [];

    private static List<string> Names(JsonNode? list) => list is JsonArray a ? [.. a.Select(n => n?.ToString()).OfType<string>()] : [];

    /// <summary>A copy of what Arena has of the session and where the model's history starts (the scan's, carried on with what is added).</summary>
    internal SessionData Copy()
    {
        var copy = new SessionData
        {
            Id = Id, Cwd = Cwd, Model = Model, Conversation = Conversation, Unlinked = Unlinked, From = From, Branch = Branch, Summary = Summary,
            WebSummary = WebSummary, Keys = Keys, _pending = _pending, ToolsOff = ToolsOff, ToolsLoaded = ToolsLoaded, Plan = Plan, PlanVersion = PlanVersion,
            PlanSent = PlanSent, CharsPerToken = CharsPerToken, Assistant = Assistant, Goal = Goal, GoalVersion = GoalVersion, GoalSent = GoalSent,
            WorkProject = WorkProject,
        };
        copy.Stream.AddRange(Stream.Select(e => new SyncEntry
        {
            Message = e.Message, Id = e.Id, Web = e.Web, Key = e.Key, At = e.At, Model = e.Model, Usage = e.Usage, Kind = e.Kind, Status = e.Status,
        }));
        return copy;
    }

    /// <summary>Where the messages Arena lacks start in <see cref="Stream"/>: the last ones, with no id.</summary>
    public int FirstUnsent
    {
        get
        {
            var i = Stream.Count;
            while (i > 0 && Stream[i - 1].Id is null)
            {
                i--;
            }
            return i;
        }
    }

    /// <summary>How many messages Arena lacks.</summary>
    public int Waiting => Stream.Count - FirstUnsent;

    /// <summary>Arena's id of the last message both have; null: none (the chat is new to this session, or its ids were never known).</summary>
    public string? LastSent => Stream.Take(FirstUnsent).LastOrDefault()?.Id is { Length: > 0 } id ? id : null;

    /// <summary>How many of <see cref="Messages"/> are the summary that stands for what came before <see cref="From"/>.</summary>
    public int Prefix => Math.Max(0, Messages.Count - Math.Max(0, Stream.Count - From));

    // An answer's tokens are written just before the answer itself.
    private TokenUsage? _pending;

    /// <summary>
    /// One line of the session's file, read back. <paramref name="light"/>: only what Arena has of the session and where the
    /// model's history starts (the list's scan), from what a message's or a compaction's line says before its messages.
    /// </summary>
    internal void Apply(JsonObject entry, DateTimeOffset written, bool light = false)
    {
        switch (entry.Str("type"))
        {
            case "session":
                Cwd = entry.Str("cwd");
                Model = entry.Str("model");
                break;
            case "message" when entry["message"] is JsonObject || light:
                var m = entry["message"] as JsonObject ?? Unread;
                var web = entry.Str("from") == "web";
                var model = entry.Str("model") ?? Model;
                var usage = entry["usage"] is JsonObject u ? new TokenUsage(u.Long("prompt") ?? 0, u.Long("cached") ?? 0, u.Long("completion") ?? 0)
                    : m.Str("role") == "assistant" || (light && entry["message"] is null) ? _pending : null;
                if (!light)
                {
                    if (m.Str("role") == "assistant")
                    {
                        Answers[Messages.Count] = (model, usage);
                        if (usage is not null)
                        {
                            Known = (usage, Messages.Count + 1);
                        }
                    }
                    if (entry["agents"] is JsonArray agents)
                    {
                        Agents[Messages.Count] = (JsonArray)agents.DeepClone();
                    }
                    Messages.Add(m.Clone());
                }
                _pending = null;
                Stream.Add(new SyncEntry
                {
                    Message = light ? m : m.Clone(),
                    // A message from the web without one is from a session before ids: Arena has it.
                    Id = entry.Str("id") ?? (web ? "" : null),
                    Web = web,
                    Key = entry.Int("n") ?? Keys,
                    At = DateTimeOffset.TryParse(entry.Str("at"), out var at) ? at : written,
                    Ms = entry.Long("ms"),
                    Model = model,
                    Usage = usage,
                    Kind = entry.Str("kind"),
                    Status = entry.Str("status"),
                    Agents = light ? null : entry["agents"]?.DeepClone() as JsonArray,
                    Context = light ? null : entry["context"]?.DeepClone() as JsonObject,
                });
                Keys = Math.Max(Keys, (entry.Int("n") ?? Keys) + 1);
                break;
            case "sync":
                Conversation = entry.Str("conversation");
                Unlinked = Conversation is null && entry.Bool("unlinked") != false;
                if (entry.Long("pushed") is { } pushed)
                {
                    // A session from before ids: Arena has the first so many of those it added, by count; their ids are not known.
                    var known = 0;
                    foreach (var e in Stream.Where(e => !e.Web))
                    {
                        if (known++ >= pushed)
                        {
                            break;
                        }
                        e.Id ??= "";
                    }
                }
                break;
            case "rated":
                // The rating of the answer of this number, as Arena kept it (none: taken back).
                if (entry.Int("key") is { } rated && Stream.FindLastIndex(e => e.Key == rated) is var ratedAt and >= 0)
                {
                    Stream[ratedAt].Rating = entry["rating"] as JsonObject is { } r ? (JsonObject)r.DeepClone() : null;
                }
                break;
            case "sent":
                // Each by its number: told twice (two sends of the same, their answers both in), it is the same.
                var any = false;
                foreach (var pair in (entry["sent"] as JsonArray ?? []).OfType<JsonArray>())
                {
                    if (pair is [JsonValue key, JsonValue id] && key.TryGetValue<int>(out var n) && id.TryGetValue<string>(out var arena)
                        && Stream.FindLastIndex(e => e.Key == n) is var place and >= 0 && Stream[place].Id is null)
                    {
                        Stream[place].Id = arena;
                        any = true;
                    }
                }
                if (entry.Bool("summary") == true)
                {
                    Summary = null;
                }
                if (any)
                {
                    Branch = false;
                }
                break;
            case "branch":
                Branch = true;
                break;
            case "shown":
                Branch = false;
                break;
            case "rewrite":
                var keep = Math.Clamp(entry.Int("keep") ?? Stream.Count, 0, Stream.Count);
                var sentPast = Stream.Skip(keep).Any(e => e.Id is not null);
                Stream.RemoveRange(keep, Stream.Count - keep);
                // Back to before what Arena has: a branch of its own there. Else the web's conversation followed: none.
                Branch = entry.Bool("rewind") == true ? Branch || sentPast : !sentPast && Branch;
                if (Summary is { } s && s.UpTo >= keep)
                {
                    Summary = null;
                }
                foreach (var gone in Marks.Keys.Where(k => k >= keep).ToList())
                {
                    Marks.Remove(gone);
                }
                break;
            case "summary" when entry.Int("upTo") is { } upTo && entry.Str("text") is { } text:
                Summary = (upTo, text);
                break;
            case "compact" when entry["messages"] is JsonArray || light:
                var all = entry["messages"] as JsonArray;
                if (!light && all is not null)
                {
                    Messages.Clear();
                    Answers.Clear();
                    Agents.Clear();
                    Known = null;
                    Messages.AddRange(all.OfType<JsonObject>().Select(c => c.Clone()));
                }
                _pending = null;
                CharsPerToken = entry["charsPerToken"] is JsonValue chars && chars.TryGetValue<double>(out var c) ? c : CharsPerToken;
                From = Math.Clamp(entry.Int("from") ?? Stream.Count - (all?.Count ?? 0), 0, Stream.Count);
                WebSummary = entry.Str("web") ?? WebSummary;
                if (From == 0)
                {
                    // The model reads the whole conversation again (gone back to before a compaction): no summary stands.
                    Marks.Clear();
                }
                if (!light && From > 0 && Agent.SummaryText(Messages.Take(Prefix)) is { } mark)
                {
                    Marks[From - 1] = mark;
                }
                break;
            case "results" when !light:
                SessionStore.ApplyResults(entry, Messages);
                break;
            case "model":
                Model = entry.Str("model") ?? Model;
                break;
            case "project":
                WorkProject = entry.Str("id") is { Length: > 0 } project ? new WorkProject(project, entry.Str("name") ?? "") : null;
                break;
            case "plan":
                Plan = WorkPlan.FromJson(entry["plan"] as JsonObject);
                PlanVersion++;
                break;
            case "plan_sent":
                PlanSent = Math.Max(PlanSent, entry.Int("version") ?? 0);
                break;
            case "assistant":
                Assistant = entry["assistant"] as JsonObject;
                break;
            case "goal":
                Goal = entry["goal"] as JsonObject;
                GoalVersion++;
                break;
            case "goal_sent":
                GoalSent = Math.Max(GoalSent, entry.Int("version") ?? 0);
                break;
            case "tools":
                ToolsOff = Names(entry["off"]);
                ToolsLoaded = Names(entry["loaded"]);
                break;
            case "usage":
                Prompt += entry.Long("prompt") ?? 0;
                Cached += entry.Long("cached") ?? 0;
                Completion += entry.Long("completion") ?? 0;
                _pending = new TokenUsage(entry.Long("prompt") ?? 0, entry.Long("cached") ?? 0, entry.Long("completion") ?? 0);
                break;
        }
    }
}

/// <summary>
/// A session as JSON Lines under the data folder's sessions/: a header, then
/// each message as it is added; a compaction writes the whole new history, and
/// reading takes the last one. The file is readable by its owner only. The
/// store keeps the session as its lines say (<see cref="Read{T}"/>): what Arena
/// has of it, and what the model reads.
/// </summary>
internal sealed class SessionStore
{
    private readonly object _gate = new();
    private readonly SessionData _state;

    private SessionStore(string id, string file, SessionData state)
    {
        Id = id;
        File = file;
        _state = state;
    }

    public string Id { get; }
    public string File { get; }

    /// <summary>Held while a sync of this session talks with Arena: two (the session open now, /sync all) never send or take in at once.</summary>
    internal SemaphoreSlim Busy { get; } = new(1, 1);

    public static SessionStore Create(string dir, string cwd, string model)
    {
        PrivateFiles.EnsureDirectory(dir);
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var store = new SessionStore(id, Path.Combine(dir, id + ".jsonl"), new SessionData { Id = id });
        store.Write(new JsonObject
        {
            ["type"] = "session", ["id"] = id, ["cwd"] = cwd, ["model"] = model,
            ["created"] = DateTimeOffset.Now.ToString("o"), ["version"] = Cli.Version,
        });
        lock (Opened)
        {
            Opened[Path.GetFullPath(store.File)] = new WeakReference<SessionStore>(store);
        }
        return store;
    }

    /// <summary>Carries on writing to a saved session.</summary>
    /// <remarks>
    /// One store for a file at a time in this process: the session open now and /sync all (or the sending of the sessions
    /// Arena lacks, at the start) never keep two minds about one file.
    /// </remarks>
    public static SessionStore Open(string file)
    {
        var full = Path.GetFullPath(file);
        lock (Opened)
        {
            // The same while the file is as it left it (another program, or a person, may have written to it since).
            if (Opened.TryGetValue(full, out var known) && known.TryGetTarget(out var store) && store.Length == Size(file))
            {
                return store;
            }
            store = new SessionStore(Path.GetFileNameWithoutExtension(file), file,
                System.IO.File.Exists(file) ? Load(file, whole: true) : new SessionData { Id = Path.GetFileNameWithoutExtension(file) }) { Length = Size(file) };
            foreach (var gone in Opened.Where(kv => !kv.Value.TryGetTarget(out _)).Select(kv => kv.Key).ToList())
            {
                Opened.Remove(gone);
            }
            Opened[full] = new WeakReference<SessionStore>(store);
            // An answer a Code Arena that stopped left half-written becomes the answer it got to.
            store.Recover();
            return store;
        }
    }

    private static readonly Dictionary<string, WeakReference<SessionStore>> Opened = [];

    /// <summary>How long the file was when this store last wrote to it (or read it).</summary>
    private long Length { get; set; }

    private static long Size(string file) => System.IO.File.Exists(file) ? new FileInfo(file).Length : -1;

    /// <summary>What the session's lines say now, read under its lock: Arena's sync reads it while a turn adds to it.</summary>
    public T Read<T>(Func<SessionData, T> read)
    {
        lock (_gate)
        {
            return read(_state);
        }
    }

    /// <summary>
    /// A message the session adds, with the model that wrote it and its tokens (an answer), or "note" (<paramref name="kind"/>:
    /// Code Arena's own word to the model, not the person's); Arena is sent it.
    /// </summary>
    public void Message(JsonObject message, string? model = null, TokenUsage? usage = null, string? kind = null, JsonArray? agents = null, JsonObject? context = null, long? ms = null)
    {
        Write(Line(new SyncEntry { Message = message, At = DateTimeOffset.UtcNow, Model = model, Usage = usage, Kind = kind, Agents = agents, Context = context, Ms = ms }));
        Added?.Invoke(message);
    }

    /// <summary>
    /// The answer being written, as it stands: kept every few seconds while it streams, in a file of its own beside the
    /// session's (replaced each time, gone once the answer is written), so Code Arena closed, killed or the machine off
    /// mid-answer loses at most those seconds (<see cref="Recover"/> makes it the answer at the next open).
    /// </summary>
    public void Draft(string text, string reasoning)
    {
        PrivateFiles.WriteAllText(DraftFile, Json.Line(new JsonObject { ["text"] = text, ["reasoning"] = reasoning }), durable: true);
        Drafted?.Invoke(text, reasoning);
    }

    /// <summary>Told of the answer being written as it stands (the chat in Arena shows it as it comes).</summary>
    public event Action<string, string>? Drafted;

    /// <summary>The turn ended: no answer is being written.</summary>
    public void DropDraft()
    {
        try
        {
            System.IO.File.Delete(DraftFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left: the next open finds it and keeps it as the answer it got to.
        }
    }

    /// <summary>Where the answer being written is kept.</summary>
    public string DraftFile => File + ".draft";

    /// <summary>What a recovered answer ends with: it was cut short where Code Arena stopped.</summary>
    public const string CutShort = "(Code Arena closed before this answer ended.)";

    /// <summary>A draft not written for this long was left by a Code Arena that stopped (a live one writes every few seconds).</summary>
    public static TimeSpan DraftLeft { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// An answer left half-written by a Code Arena that stopped (closed, killed, a crash, the machine off): written as the
    /// answer it got to, marked cut short, so the session and its chat in Arena keep it. True when there was one.
    /// </summary>
    public bool Recover()
    {
        var file = DraftFile;
        if (!System.IO.File.Exists(file) || DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(file) < DraftLeft)
        {
            return false;
        }
        var d = Json.ParseObject(Text(System.IO.File.ReadAllBytes(file)));
        var text = d?.Str("text") ?? "";
        var answer = new JsonObject { ["role"] = "assistant", ["content"] = text.Trim().Length > 0 ? text.TrimEnd() + "\n\n" + CutShort : CutShort };
        if (d?.Str("reasoning") is { Length: > 0 } reasoning)
        {
            answer["reasoning_content"] = reasoning;
        }
        Message(answer);
        return true;
    }

    /// <summary>Told of each message the session adds (the chat in Arena is sent it).</summary>
    public event Action<JsonObject>? Added;

    /// <summary>Told when a compaction's summary waits to be sent (Arena keeps it where the web chat keeps its own).</summary>
    public event Action? SummaryWaiting;

    /// <summary>A message the person added in Arena's web chat (with its id there), taken into this session.</summary>
    public void Remote(SyncEntry e) => Write(Line(e));

    /// <summary>A message line, with what Arena and the IDE show of it (numbered as it is written: Arena knows the session's own as session:number).</summary>
    private static JsonObject Line(SyncEntry e)
    {
        var line = new JsonObject { ["type"] = "message", ["at"] = e.At.ToString("o") };
        if (e.Web)
        {
            line["from"] = "web";
            line["id"] = e.Id;
        }
        if (e.Model is not null)
        {
            line["model"] = e.Model;
        }
        if (e.Usage is { } u)
        {
            line["usage"] = new JsonObject { ["prompt"] = u.Prompt, ["cached"] = u.Cached, ["completion"] = u.Completion };
        }
        if (e.Kind is not null)
        {
            line["kind"] = e.Kind;
        }
        if (e.Ms is { } ms)
        {
            line["ms"] = ms;
        }
        if (e.Status is not null)
        {
            line["status"] = e.Status;
        }
        // After what the line says of itself, which is read without it (the list's scan, Head, reads a line's first 4 KB only).
        line["message"] = e.Message.Clone();
        // After the message: shown by the page only, and long (a task's sub-agents), so the scan never reads them.
        if (e.Agents is not null)
        {
            line["agents"] = e.Agents.DeepClone();
        }
        if (e.Context is not null)
        {
            line["context"] = e.Context.DeepClone();
        }
        return line;
    }

    /// <summary>Arena has these messages (by their number) under these ids (and the summary waiting, when <paramref name="summary"/>).</summary>
    public void Sent(IReadOnlyList<(int Key, string Id)> sent, bool summary = false)
    {
        if (sent.Count > 0 || summary)
        {
            Write(new JsonObject
            {
                ["type"] = "sent",
                ["sent"] = new JsonArray([.. sent.Select(s => (JsonNode)new JsonArray(s.Key, s.Id))]),
                ["summary"] = summary ? true : null,
            });
        }
    }

    /// <summary>The person rated an answer (by its number), as Arena kept it; null: taken back.</summary>
    public void Rated(int key, JsonObject? rating) => Write(new JsonObject { ["type"] = "rated", ["key"] = key, ["rating"] = rating?.DeepClone() });

    /// <summary>The chat in Arena the session is kept with (null: none any more, it was deleted there).</summary>
    public void Link(string? conversation) => Write(new JsonObject { ["type"] = "sync", ["conversation"] = conversation });

    /// <summary>The project in Arena the session counts to from now on (null: none).</summary>
    public void Project(WorkProject? project) => Write(new JsonObject { ["type"] = "project", ["id"] = project?.Id, ["name"] = project?.Name });

    /// <summary>What the session adds next goes on a branch of its own in Arena (the web moved elsewhere while it was on its way).</summary>
    public void Branch() => Write(new JsonObject { ["type"] = "branch" });

    /// <summary>
    /// The model's history replaced (a compaction): <paramref name="raw"/> of its last messages are the conversation's last
    /// word for word, those before them a summary of the rest.
    /// </summary>
    /// <param name="raw">Null: as many as before (long results cut in place).</param>
    /// <param name="charsPerToken">The characters a token holds now (<see cref="Agent.CharsPerToken"/>); null: as the session last said.</param>
    public void Compacted(IEnumerable<JsonObject> messages, int? raw = null, string? webSummary = null, double? charsPerToken = null)
    {
        var all = new JsonArray([.. messages.Select(m => (JsonNode)m.Clone())]);
        int from;
        lock (_gate)
        {
            from = raw is { } r ? Math.Max(0, _state.Stream.Count - r) : _state.From;
        }
        Write(Compaction(all, from, webSummary, charsPerToken));
    }

    /// <summary>
    /// A compaction's line: where its history starts in the conversation first, its messages last (the list's scan reads
    /// it without them). The characters a token holds go with it: the session's count is gone until the next answer.
    /// </summary>
    private JsonObject Compaction(JsonArray messages, int from, string? webSummary = null, double? charsPerToken = null)
    {
        lock (_gate)
        {
            charsPerToken ??= _state.CharsPerToken;
        }
        return new() { ["type"] = "compact", ["from"] = from, ["web"] = webSummary, ["charsPerToken"] = charsPerToken, ["messages"] = messages };
    }

    /// <summary>
    /// Tool results in the model's history given new words in place (summarised once used): each by its place in the history.
    /// Lighter than a compaction's line, which writes every message again; Arena's copy keeps them whole.
    /// </summary>
    public void Condensed(IReadOnlyList<(int At, string Content)> changes)
    {
        if (changes.Count > 0)
        {
            Write(new JsonObject { ["type"] = "results", ["changes"] = new JsonArray([.. changes.Select(c => (JsonNode)new JsonArray(c.At, c.Content))]) });
        }
    }

    /// <summary>A results line's changes made to the model's history (<see cref="Condensed"/>).</summary>
    internal static void ApplyResults(JsonObject entry, List<JsonObject> messages)
    {
        foreach (var change in (entry["changes"] as JsonArray ?? []).OfType<JsonArray>())
        {
            if (change is [JsonValue at, JsonValue content] && at.TryGetValue<int>(out var i) && content.TryGetValue<string>(out var text)
                && i >= 0 && i < messages.Count && messages[i].Str("role") == "tool")
            {
                messages[i]["content"] = text;
            }
        }
    }

    /// <summary>A compaction's summary of all but the last <paramref name="raw"/> messages, for Arena to keep where its own are.</summary>
    public void Summarized(string text, int raw)
    {
        int upTo;
        lock (_gate)
        {
            upTo = _state.Stream.Count - raw - 1;
        }
        if (upTo >= 0)
        {
            Write(new JsonObject { ["type"] = "summary", ["upTo"] = upTo, ["text"] = text });
            SummaryWaiting?.Invoke();
        }
    }

    /// <summary>
    /// The conversation from <paramref name="keep"/> on is replaced by <paramref name="entries"/> (the web's conversation
    /// followed, or its messages put before those still to send), and the model's history built again from it.
    /// </summary>
    public List<JsonObject> Rewrite(int keep, IReadOnlyList<SyncEntry> entries)
    {
        List<JsonObject> summary = [];
        var from = 0;
        lock (_gate)
        {
            // The summary stands while what it covers is kept; else the model reads the whole conversation again.
            if (_state.From <= keep)
            {
                summary = [.. _state.Messages.Take(_state.Prefix).Select(m => m.Clone())];
                from = _state.From;
            }
        }
        Write(new JsonObject { ["type"] = "rewrite", ["keep"] = keep });
        foreach (var e in entries)
        {
            var line = Line(e);
            if (!e.Web)
            {
                // The session's own keep their number: Arena may have one already (its answer lost), and knows it by it.
                line["n"] = e.Key;
            }
            Write(line);
        }
        List<JsonObject> history;
        lock (_gate)
        {
            history = [.. summary, .. _state.Stream.Skip(from).Select(e => e.Message.Clone())];
        }
        Write(Compaction(new JsonArray([.. history.Select(m => (JsonNode)m.Clone())]), from));
        return history;
    }

    /// <summary>
    /// Back to the first <paramref name="place"/> messages of the whole conversation (/rewind, the IDE's Rewind to here):
    /// what came after is gone from the session, and goes on a branch of its own in Arena when Arena has it. Back to before
    /// a compaction, the model reads the whole messages again: its summary covered some that are gone. The history now.
    /// </summary>
    public List<JsonObject> Rewind(int place)
    {
        int keep;
        int from;
        List<JsonObject> history;
        lock (_gate)
        {
            keep = Math.Clamp(place, 0, _state.Stream.Count);
            if (keep >= _state.From)
            {
                // What the model reads now, up to there (a long result cut to fit stays cut).
                from = _state.From;
                history = [.. _state.Messages.Take(_state.Prefix + keep - from).Select(m => m.Clone())];
            }
            else
            {
                from = 0;
                history = [.. _state.Stream.Take(keep).Select(e => e.Message.Clone())];
            }
        }
        Write(new JsonObject { ["type"] = "rewrite", ["keep"] = keep, ["rewind"] = true });
        Write(Compaction(new JsonArray([.. history.Select(m => (JsonNode)m.Clone())]), from));
        return history;
    }

    /// <summary>Arena shows the session's branch now (it went back to before a turn there): what it adds next goes on after it.</summary>
    public void Shown() => Write(new JsonObject { ["type"] = "shown" });

    public void Model(string model) => Write(new JsonObject { ["type"] = "model", ["model"] = model });

    /// <summary>The groups of remote tools turned off in the session, and those loaded: the last line of them counts.</summary>
    public void Tools(IEnumerable<string> off, IEnumerable<string> loaded) =>
        Write(new JsonObject { ["type"] = "tools", ["off"] = new JsonArray([.. off.Select(g => (JsonNode)g)]), ["loaded"] = new JsonArray([.. loaded.Select(g => (JsonNode)g)]) });

    /// <summary>The plan as it is now: the last line of it counts; Arena is sent it with the session's chat.</summary>
    public void Plan(WorkPlan plan)
    {
        Write(new JsonObject { ["type"] = "plan", ["plan"] = plan.ToJson() });
        PlanWaiting?.Invoke();
    }

    /// <summary>The Arena assistant the session works as from now on (null: none).</summary>
    public void Assistant(JsonObject? assistant) => Write(new JsonObject { ["type"] = "assistant", ["assistant"] = assistant?.DeepClone() });

    /// <summary>The goal as it is now (null: none any more); Arena is sent it with the session's chat.</summary>
    public void Goal(JsonObject? goal)
    {
        Write(new JsonObject { ["type"] = "goal", ["goal"] = goal?.DeepClone() });
        PlanWaiting?.Invoke();
    }

    /// <summary>Arena has the goal as it was at its <paramref name="version"/>th change.</summary>
    public void GoalSent(int version) => Write(new JsonObject { ["type"] = "goal_sent", ["version"] = version });

    /// <summary>Arena has the plan as it was at its <paramref name="version"/>th change.</summary>
    public void PlanSent(int version) => Write(new JsonObject { ["type"] = "plan_sent", ["version"] = version });

    /// <summary>Told when the plan changed: the chat's sync sends it.</summary>
    public event Action? PlanWaiting;

    public void Usage(TokenUsage usage) =>
        Write(new JsonObject { ["type"] = "usage", ["prompt"] = usage.Prompt, ["cached"] = usage.Cached, ["completion"] = usage.Completion });

    private void Write(JsonObject line)
    {
        lock (_gate)
        {
            if (line.Str("type") == "message")
            {
                // Its number before the message, with what the line says of itself (the list's scan reads only that much); what
                // only the page shows, after it.
                var after = new[] { "message", "agents", "context" }.Where(line.ContainsKey).Select(k => (k, line[k])).ToList();
                foreach (var (k, _) in after)
                {
                    line.Remove(k);
                }
                if (line.Int("n") is null)
                {
                    line["n"] = _state.Keys;
                }
                foreach (var (k, v) in after)
                {
                    line[k] = v;
                }
            }
            // On the disk before it returns: a crash or a power-off loses no message the session had.
            PrivateFiles.AppendLine(File, Json.Line(line), durable: true);
            if (line.Str("type") == "message" && line["message"]?.Str("role") == "assistant")
            {
                // The answer is written whole: the draft of it goes.
                System.IO.File.Delete(DraftFile);
            }
            _state.Apply(line, DateTimeOffset.UtcNow);
            Length = Size(File);
        }
    }

    /// <summary>
    /// Reads a session back. Its messages, from its last compaction on (where <see cref="Scan"/> found it, so a long
    /// session's earlier histories are not read again), with what Arena has of it, its model, to-do list and tokens from the
    /// scan. <paramref name="whole"/>: every line is read, each message with its words: what keeping it in step with Arena
    /// needs (the store reads it so).
    /// </summary>
    public static SessionData Load(string file, bool whole = false)
    {
        if (whole)
        {
            return Read(file);
        }
        var scan = Scan(file);
        var data = scan.State.Copy();
        (data.Prompt, data.Cached, data.Completion) = (scan.Prompt, scan.Cached, scan.Completion);
        data.Model = scan.ModelAtCompact;
        TokenUsage? pending = null;
        foreach (var (_, _, bytes) in Lines(file, Math.Max(0, scan.LastCompact), scan.Length))
        {
            if (Json.ParseObject(Text(bytes)) is not { } entry)
            {
                continue;
            }
            switch (entry.Str("type"))
            {
                case "message" when entry["message"] is JsonObject m:
                    if (m.Str("role") == "assistant")
                    {
                        var usage = entry["usage"] is JsonObject u ? Usage(u) : pending;
                        data.Answers[data.Messages.Count] = (entry.Str("model") ?? data.Model, usage);
                        if (usage is not null)
                        {
                            data.Known = (usage, data.Messages.Count + 1);
                        }
                    }
                    if (entry["agents"] is JsonArray agents)
                    {
                        data.Agents[data.Messages.Count] = (JsonArray)agents.DeepClone();
                    }
                    pending = null;
                    data.Messages.Add(m.Clone());
                    break;
                case "compact" when entry["messages"] is JsonArray all:
                    data.Messages.Clear();
                    data.Answers.Clear();
                    data.Agents.Clear();
                    data.Known = null;
                    pending = null;
                    data.Messages.AddRange(all.OfType<JsonObject>().Select(m => m.Clone()));
                    break;
                case "results":
                    ApplyResults(entry, data.Messages);
                    break;
                case "model":
                    data.Model = entry.Str("model") ?? data.Model;
                    break;
                case "usage":
                    pending = Usage(entry);
                    break;
            }
        }
        data.Model = scan.Model;
        return data;
    }

    /// <summary>Every line of a session, each message with its words.</summary>
    private static SessionData Read(string file)
    {
        var data = new SessionData { Id = Path.GetFileNameWithoutExtension(file) };
        // Lines from before messages kept their time: the file's.
        var written = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        foreach (var (_, _, bytes) in Lines(file, 0, new FileInfo(file).Length))
        {
            if (Json.ParseObject(Text(bytes)) is { } entry)
            {
                data.Apply(entry, written);
            }
        }
        return data;
    }

    /// <summary>A line's text, without the zeros a power-off can leave before it.</summary>
    private static string Text(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes).TrimStart('\0');

    private static TokenUsage Usage(JsonObject entry) => new(entry.Long("prompt") ?? 0, entry.Long("cached") ?? 0, entry.Long("completion") ?? 0);

    /// <summary>
    /// What a session file holds as it is listed and read back, scanned once and kept by the file's size and time: an
    /// appended file is scanned from where the scan before stopped, and a message's or a compaction's line is read only up
    /// to its messages (what it says of itself comes first), except for the preview.
    /// </summary>
    internal sealed record SessionScan
    {
        /// <summary>The file's size and time when scanned, and how far its whole lines go.</summary>
        public long Size { get; init; }
        public DateTime Written { get; init; }
        public long Length { get; init; }
        public string? Cwd { get; init; }
        public string? Model { get; init; }
        /// <summary>The model when the last compaction was written (its answers' model, read from there on).</summary>
        public string? ModelAtCompact { get; init; }
        /// <summary>What Arena has of the session and where the model's history starts: its messages without their words.</summary>
        public SessionData State { get; init; } = new();
        /// <summary>Message lines, all of them.</summary>
        public int Messages { get; init; }
        /// <summary>When the last message was written: the session's last activity (what the sync writes later of it is not).</summary>
        public DateTimeOffset? LastMessage { get; init; }
        public string Preview { get; init; } = "";
        public long Prompt { get; init; }
        public long Cached { get; init; }
        public long Completion { get; init; }
        /// <summary>Where the last compaction's line starts (-1: none).</summary>
        public long LastCompact { get; init; } = -1;
    }

    private static readonly Dictionary<string, SessionScan> Scans = new(StringComparer.Ordinal);

    /// <summary>The session file as <see cref="SessionScan"/> says, scanning only what was added since the last time.</summary>
    internal static SessionScan Scan(string file)
    {
        var info = new FileInfo(file);
        var (size, written) = (info.Length, info.LastWriteTimeUtc);
        SessionScan? known;
        lock (Scans)
        {
            Scans.TryGetValue(file, out known);
        }
        if (known is not null && known.Size == size && known.Written == written)
        {
            return known;
        }
        // Session files are only ever appended to: a bigger one goes on from where the last scan stopped.
        var from = known is not null && size >= known.Length && size >= known.Size ? known : new SessionScan { State = new SessionData { Id = Path.GetFileNameWithoutExtension(file) } };
        var scan = Continue(file, from, size, written);
        lock (Scans)
        {
            Scans[file] = scan;
        }
        return scan;
    }

    private static readonly byte[] TypeMessage = Encoding.UTF8.GetBytes("{\"type\":\"message\"");
    private static readonly byte[] TypeCompact = Encoding.UTF8.GetBytes("{\"type\":\"compact\"");
    private static readonly byte[] MessageField = Encoding.UTF8.GetBytes(",\"message\":");
    private static readonly byte[] MessagesField = Encoding.UTF8.GetBytes(",\"messages\":");
    private static readonly byte[] FromWeb = Encoding.UTF8.GetBytes(",\"from\":\"web\"}");

    /// <summary>The most of a line kept when only what it says before its messages is read.</summary>
    private const int HeadBytes = 4096;

    /// <summary>What a message's or a compaction's line says of itself, before its messages: as JSON, without them.</summary>
    private static JsonObject? Head(byte[] line)
    {
        var span = line.AsSpan();
        var at = span.IndexOf(span.StartsWith(TypeCompact) ? MessagesField : MessageField);
        if (at < 0)
        {
            // Code Arena 7.7.0 wrote a task's sub-agents' work before the message, often past the 4 KB read: such a message
            // still counts, at its time (its number is its place; Arena's id for it comes with its "sent" line).
            return line.Length >= HeadBytes && span.StartsWith(TypeMessage)
                ? new JsonObject { ["type"] = "message", ["at"] = Regex.Match(Encoding.UTF8.GetString(span[..200]), "^\\{\"type\":\"message\",\"at\":\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : null }
                : null;
        }
        var head = Json.ParseObject(Encoding.UTF8.GetString(span[..at]) + "}");
        // A line of an older Code Arena: the message first, whether it came from the web at its end.
        if (head is not null && span.StartsWith(TypeMessage) && span.EndsWith(FromWeb))
        {
            head["from"] = "web";
        }
        return head;
    }

    private static SessionScan Continue(string file, SessionScan s, long size, DateTime written)
    {
        var (cwd, model, atCompact) = (s.Cwd, s.Model, s.ModelAtCompact);
        var (messages, preview, prompt, cached, completion, compact, length) = (s.Messages, s.Preview, s.Prompt, s.Cached, s.Completion, s.LastCompact, s.Length);
        var lastMessage = s.LastMessage;
        var state = s.State.Copy();
        var at0 = new DateTimeOffset(written, TimeSpan.Zero);
        // A message's line is read whole only while the preview (the first question) is not found.
        bool Skip(byte[] b) => b.AsSpan().StartsWith(TypeCompact) || (preview.Length > 0 && b.AsSpan().StartsWith(TypeMessage));
        foreach (var (at, bytesLong, bytes) in Lines(file, s.Length, size, Skip))
        {
            length = at + bytesLong + 1;
            var line = bytes.AsSpan();
            if (line.StartsWith(TypeCompact))
            {
                compact = at;
                atCompact = model;
                if (Head(bytes) is { } head)
                {
                    state.Apply(head, at0, light: true);
                }
                continue;
            }
            if (line.StartsWith(TypeMessage))
            {
                messages++;
                var whole = bytes.Length < HeadBytes || !Skip(bytes) ? Json.ParseObject(Text(line)) : null;
                if (preview.Length == 0 && whole?["message"] is JsonObject m && m.Str("role") == "user")
                {
                    preview = Fmt.OneLine(Quotes.Split(m.Str("content") ?? "").Text, 70);
                }
                if ((whole is not null ? Without(whole) : Head(bytes)) is { } head)
                {
                    state.Apply(head, at0, light: true);
                    if (DateTimeOffset.TryParse(head.Str("at"), out var when) && (lastMessage is null || when > lastMessage))
                    {
                        lastMessage = when;
                    }
                }
                continue;
            }
            if (Json.ParseObject(Text(line)) is not { } entry)
            {
                continue;
            }
            state.Apply(entry, at0, light: true);
            switch (entry.Str("type"))
            {
                case "session":
                    cwd = entry.Str("cwd");
                    model = entry.Str("model");
                    atCompact = compact < 0 ? model : atCompact;
                    break;
                case "model":
                    model = entry.Str("model") ?? model;
                    break;
                case "usage":
                    prompt += entry.Long("prompt") ?? 0;
                    cached += entry.Long("cached") ?? 0;
                    completion += entry.Long("completion") ?? 0;
                    break;
            }
        }
        return new SessionScan
        {
            Size = size, Written = written, Length = length, Cwd = cwd, Model = model, ModelAtCompact = atCompact, State = state,
            Messages = messages, LastMessage = lastMessage, Preview = preview, Prompt = prompt, Cached = cached, Completion = completion, LastCompact = compact,
        };

        // A message line read whole, as the scan keeps it: without its words.
        static JsonObject Without(JsonObject line)
        {
            var head = (JsonObject)line.DeepClone();
            head.Remove("message");
            return head;
        }
    }

    /// <summary>
    /// The whole lines of a file between two places, each with where it starts and its length (a last line not ended yet
    /// is left for the next read). A line <paramref name="skip"/> says so of from its start is kept only up to
    /// <see cref="HeadBytes"/> (what it says of itself before its messages).
    /// </summary>
    private static IEnumerable<(long At, long Length, byte[] Bytes)> Lines(string file, long from, long to, Func<byte[], bool>? skip = null)
    {
        // Shared: the web interface reads a session while its turn appends to it.
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        stream.Seek(from, SeekOrigin.Begin);
        var buffer = new byte[1 << 16];
        var line = new MemoryStream();
        var start = from;
        var at = from;
        var skipping = false;
        var head = Math.Max(TypeMessage.Length, TypeCompact.Length);
        while (at < to)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, to - at));
            if (read <= 0)
            {
                break;
            }
            var i = 0;
            while (i < read)
            {
                var end = Array.IndexOf(buffer, (byte)'\n', i, read - i);
                var stop = end < 0 ? read : end;
                if (!skipping || line.Length < HeadBytes)
                {
                    line.Write(buffer, i, (int)Math.Min(stop - i, skipping ? HeadBytes - line.Length : stop - i));
                    if (!skipping && skip is not null && line.Length >= head && skip(line.GetBuffer()[..head]))
                    {
                        skipping = true;
                        if (line.Length > HeadBytes)
                        {
                            line.SetLength(HeadBytes);
                        }
                    }
                }
                if (end < 0)
                {
                    break;
                }
                yield return (start, at + end - start, line.ToArray());
                line.SetLength(0);
                skipping = false;
                start = at + end + 1;
                i = end + 1;
            }
            at += read;
        }
    }

    /// <summary>
    /// Saved sessions, newest first by their last message (what the sync writes of a session later, as what Arena has of it,
    /// does not make it newer); those of this folder only when cwd is given. Each file is scanned once (<see cref="Scan"/>).
    /// </summary>
    public static List<SessionSummary> List(string dir, string? cwd = null, int max = 20)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }
        // The newest found so far, newest first, at most max. A file is never written before its last message: once the
        // last of them is newer than the next file was written, no file further on comes before it.
        var found = new List<(DateTime At, SessionSummary Session)>();
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            if (found.Count >= max && found[^1].At >= file.LastWriteTimeUtc)
            {
                break;
            }
            SessionScan scan;
            try
            {
                scan = Scan(file.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (cwd is not null && (scan.Cwd is null || !SameFolder(scan.Cwd, cwd)))
            {
                continue;
            }
            if (scan.Messages == 0)
            {
                continue;
            }
            // A message from the web chat says Arena's time: never later than the file says.
            var at = scan.LastMessage is { } last && last.UtcDateTime < file.LastWriteTimeUtc ? last.UtcDateTime : file.LastWriteTimeUtc;
            found.Add((at, new SessionSummary(Path.GetFileNameWithoutExtension(file.Name), file.FullName, scan.Cwd, at.ToLocalTime(), scan.Preview, scan.Messages,
                scan.State.Conversation, scan.State.Unlinked, scan.State.Waiting) { Plan = scan.State.Plan }));
            found.Sort((a, b) => b.At.CompareTo(a.At));
            if (found.Count > max)
            {
                found.RemoveAt(found.Count - 1);
            }
        }
        return [.. found.Select(f => f.Session)];
    }

    /// <summary>
    /// The newest session of this folder (other than <paramref name="except"/>) whose plan is in progress: a step not done
    /// yet. The next session picks it up (/plan resume, code-arena --plan).
    /// </summary>
    public static SessionSummary? PlanInProgress(string dir, string cwd, string? except) =>
        List(dir, cwd, 50).FirstOrDefault(s => s.Id != except && !s.Plan.IsEmpty && s.Plan.Steps.Any(step => step.Status != WorkStep.Done));

    private static bool SameFolder(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// The session of this folder kept in step with a chat in Arena (the newest when several are), so the chat goes on in
    /// it rather than in a new one each time; null: none.
    /// </summary>
    public static string? Linked(string dir, string conversation, string cwd)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            var scan = Scan(file.FullName);
            if (string.Equals(scan.State.Conversation, conversation, StringComparison.OrdinalIgnoreCase) && scan.Cwd is not null && SameFolder(scan.Cwd, cwd))
            {
                return file.FullName;
            }
        }
        return null;
    }

    /// <summary>A session by its id, or the start of one.</summary>
    public static string? Find(string dir, string id)
    {
        var exact = Path.Combine(dir, id + ".jsonl");
        if (System.IO.File.Exists(exact))
        {
            return exact;
        }
        if (!Directory.Exists(dir))
        {
            return null;
        }
        var matches = Directory.EnumerateFiles(dir, "*.jsonl").Where(f => Path.GetFileName(f).StartsWith(id, StringComparison.Ordinal)).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
