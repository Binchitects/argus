using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A saved session as it is listed: which, where, when, how it began, the chat in Arena it is kept in step with (null:
/// none), whether that chat was deleted there (it is not kept in step any more), and how many of its messages Arena lacks.
/// </summary>
internal sealed record SessionSummary(string Id, string File, string? Cwd, DateTime Updated, string Preview, int Messages, string? Conversation = null,
    bool Unlinked = false, int Waiting = 0)
{
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
    /// <summary>The model that wrote it (an answer).</summary>
    public string? Model { get; init; }
    /// <summary>An answer's tokens, when the gateway said.</summary>
    public TokenUsage? Usage { get; init; }
    /// <summary>"note": a message of Code Arena's own to the model (the connection dropped), not the person's words.</summary>
    public string? Kind { get; init; }
    /// <summary>How an answer from the web ended, when not as it should: "stopped", "failed", "declined".</summary>
    public string? Status { get; init; }

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

/// <summary>A saved session read back: its messages (after the last compaction), model, to-do list and tokens.</summary>
internal sealed class SessionData
{
    public string Id { get; init; } = "";
    public string? Cwd { get; set; }
    public string? Model { get; set; }
    /// <summary>What the model reads: a summary of what came before <see cref="From"/>, if any, then <see cref="Stream"/> from there.</summary>
    public List<JsonObject> Messages { get; } = [];
    public List<TodoItem> Todos { get; set; } = [];
    public long Prompt { get; set; }
    public long Cached { get; set; }
    public long Completion { get; set; }
    /// <summary>Each answer's (by its place in Messages) model and tokens, for the web interface.</summary>
    public Dictionary<int, (string? Model, TokenUsage? Usage)> Answers { get; } = [];
    /// <summary>The chat in Arena this session is kept in step with; null: none yet (or it was deleted there).</summary>
    public string? Conversation { get; set; }
    /// <summary>The chat in Arena was deleted: the session is no longer kept in step.</summary>
    public bool Unlinked { get; set; }
    /// <summary>
    /// Every message of the conversation in the order of its chat in Arena, compactions aside (the chat keeps what was
    /// summarized): those this session added, and those taken in from the web.
    /// </summary>
    public List<SyncEntry> Stream { get; } = [];
    /// <summary>Where in <see cref="Stream"/> what the model reads word for word starts (before it: a summary, or nothing).</summary>
    public int From { get; set; }
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

    /// <summary>A message line's body as the list keeps it: none (only what its line says before the message counts there).</summary>
    private static readonly JsonObject Unread = [];

    /// <summary>A copy of what Arena has of the session and where the model's history starts (the scan's, carried on with what is added).</summary>
    internal SessionData Copy()
    {
        var copy = new SessionData
        {
            Id = Id, Cwd = Cwd, Model = Model, Conversation = Conversation, Unlinked = Unlinked, From = From, Branch = Branch, Summary = Summary,
            WebSummary = WebSummary, Keys = Keys, _pending = _pending,
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
                    Model = model,
                    Usage = usage,
                    Kind = entry.Str("kind"),
                    Status = entry.Str("status"),
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
                    Known = null;
                    Messages.AddRange(all.OfType<JsonObject>().Select(c => c.Clone()));
                }
                _pending = null;
                From = Math.Clamp(entry.Int("from") ?? Stream.Count - (all?.Count ?? 0), 0, Stream.Count);
                WebSummary = entry.Str("web") ?? WebSummary;
                break;
            case "model":
                Model = entry.Str("model") ?? Model;
                break;
            case "todos" when entry["todos"] is JsonArray todos:
                Todos = [.. todos.OfType<JsonObject>().Select(t => new TodoItem(t.Str("content") ?? "", t.Str("status") ?? "pending"))];
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
    public void Message(JsonObject message, string? model = null, TokenUsage? usage = null, string? kind = null)
    {
        Write(Line(new SyncEntry { Message = message, At = DateTimeOffset.UtcNow, Model = model, Usage = usage, Kind = kind }));
        Added?.Invoke(message);
    }

    /// <summary>Told of each message the session adds (the chat in Arena is sent it).</summary>
    public event Action<JsonObject>? Added;

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
        if (e.Status is not null)
        {
            line["status"] = e.Status;
        }
        // Last: what the line says of itself is read without it (the list's scan, Head).
        line["message"] = e.Message.Clone();
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

    /// <summary>The chat in Arena the session is kept with (null: none any more, it was deleted there).</summary>
    public void Link(string? conversation) => Write(new JsonObject { ["type"] = "sync", ["conversation"] = conversation });

    /// <summary>What the session adds next goes on a branch of its own in Arena (the web moved elsewhere while it was on its way).</summary>
    public void Branch() => Write(new JsonObject { ["type"] = "branch" });

    /// <summary>
    /// The model's history replaced (a compaction): <paramref name="raw"/> of its last messages are the conversation's last
    /// word for word, those before them a summary of the rest.
    /// </summary>
    /// <param name="raw">Null: as many as before (long results cut in place).</param>
    public void Compacted(IEnumerable<JsonObject> messages, int? raw = null, string? webSummary = null)
    {
        var all = new JsonArray([.. messages.Select(m => (JsonNode)m.Clone())]);
        int from;
        lock (_gate)
        {
            from = raw is { } r ? Math.Max(0, _state.Stream.Count - r) : _state.From;
        }
        Write(Compaction(all, from, webSummary));
    }

    /// <summary>A compaction's line: where its history starts in the conversation first, its messages last (the list's scan reads it without them).</summary>
    private static JsonObject Compaction(JsonArray messages, int from, string? webSummary = null) =>
        new() { ["type"] = "compact", ["from"] = from, ["web"] = webSummary, ["messages"] = messages };

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
    /// Back to the first <paramref name="messages"/> of the model's history (/rewind): what came after is gone from the session,
    /// and goes on a branch of its own in Arena when Arena has it. The history now.
    /// </summary>
    public List<JsonObject> Rewind(int messages)
    {
        int keep;
        int raw;
        List<JsonObject> history;
        lock (_gate)
        {
            var prefix = _state.Prefix;
            messages = Math.Clamp(messages, 0, _state.Messages.Count);
            // Back to before a compaction: its summary is all there is of what it covered.
            raw = Math.Max(0, messages - prefix);
            keep = Math.Min(_state.Stream.Count, _state.From + raw);
            history = [.. _state.Messages.Take(messages).Select(m => m.Clone())];
        }
        Write(new JsonObject { ["type"] = "rewrite", ["keep"] = keep, ["rewind"] = true });
        Write(Compaction(new JsonArray([.. history.Select(m => (JsonNode)m.Clone())]), keep - raw));
        return history;
    }

    public void Model(string model) => Write(new JsonObject { ["type"] = "model", ["model"] = model });

    public void Todos(IEnumerable<TodoItem> todos) =>
        Write(new JsonObject { ["type"] = "todos", ["todos"] = new JsonArray([.. todos.Select(t => (JsonNode)new JsonObject { ["content"] = t.Content, ["status"] = t.Status })]) });

    public void Usage(TokenUsage usage) =>
        Write(new JsonObject { ["type"] = "usage", ["prompt"] = usage.Prompt, ["cached"] = usage.Cached, ["completion"] = usage.Completion });

    private void Write(JsonObject line)
    {
        lock (_gate)
        {
            if (line.Str("type") == "message")
            {
                // Its number before the message, with what the line says of itself (the list's scan reads only that much).
                var message = line["message"];
                line.Remove("message");
                if (line.Int("n") is null)
                {
                    line["n"] = _state.Keys;
                }
                line["message"] = message;
            }
            PrivateFiles.AppendLine(File, Json.Line(line));
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
        data.Todos = [.. scan.Todos];
        (data.Prompt, data.Cached, data.Completion) = (scan.Prompt, scan.Cached, scan.Completion);
        data.Model = scan.ModelAtCompact;
        TokenUsage? pending = null;
        foreach (var (_, _, bytes) in Lines(file, Math.Max(0, scan.LastCompact), scan.Length))
        {
            if (Json.ParseObject(Encoding.UTF8.GetString(bytes)) is not { } entry)
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
                    pending = null;
                    data.Messages.Add(m.Clone());
                    break;
                case "compact" when entry["messages"] is JsonArray all:
                    data.Messages.Clear();
                    data.Answers.Clear();
                    data.Known = null;
                    pending = null;
                    data.Messages.AddRange(all.OfType<JsonObject>().Select(m => m.Clone()));
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
            if (Json.ParseObject(Encoding.UTF8.GetString(bytes)) is { } entry)
            {
                data.Apply(entry, written);
            }
        }
        return data;
    }

    private static TokenUsage Usage(JsonObject entry) => new(entry.Long("prompt") ?? 0, entry.Long("cached") ?? 0, entry.Long("completion") ?? 0);

    private static List<TodoItem> Todos(JsonArray todos) => [.. todos.OfType<JsonObject>().Select(t => new TodoItem(t.Str("content") ?? "", t.Str("status") ?? "pending"))];

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
        public IReadOnlyList<TodoItem> Todos { get; init; } = [];
        /// <summary>What Arena has of the session and where the model's history starts: its messages without their words.</summary>
        public SessionData State { get; init; } = new();
        /// <summary>Message lines, all of them.</summary>
        public int Messages { get; init; }
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
            return null;
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
        var (cwd, model, atCompact, todos) = (s.Cwd, s.Model, s.ModelAtCompact, s.Todos);
        var (messages, preview, prompt, cached, completion, compact, length) = (s.Messages, s.Preview, s.Prompt, s.Cached, s.Completion, s.LastCompact, s.Length);
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
                var whole = bytes.Length < HeadBytes || !Skip(bytes) ? Json.ParseObject(Encoding.UTF8.GetString(line)) : null;
                if (preview.Length == 0 && whole?["message"] is JsonObject m && m.Str("role") == "user")
                {
                    preview = Fmt.OneLine(m.Str("content"), 70);
                }
                if ((whole is not null ? Without(whole) : Head(bytes)) is { } head)
                {
                    state.Apply(head, at0, light: true);
                }
                continue;
            }
            if (Json.ParseObject(Encoding.UTF8.GetString(line)) is not { } entry)
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
                case "todos" when entry["todos"] is JsonArray list:
                    todos = Todos(list);
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
            Size = size, Written = written, Length = length, Cwd = cwd, Model = model, ModelAtCompact = atCompact, Todos = todos, State = state,
            Messages = messages, Preview = preview, Prompt = prompt, Cached = cached, Completion = completion, LastCompact = compact,
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

    /// <summary>Saved sessions, newest first; those of this folder only when cwd is given. Each file is scanned once (<see cref="Scan"/>).</summary>
    public static List<SessionSummary> List(string dir, string? cwd = null, int max = 20)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }
        var found = new List<SessionSummary>();
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc))
        {
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
            found.Add(new SessionSummary(Path.GetFileNameWithoutExtension(file.Name), file.FullName, scan.Cwd, file.LastWriteTime, scan.Preview, scan.Messages,
                scan.State.Conversation, scan.State.Unlinked, scan.State.Waiting));
            if (found.Count >= max)
            {
                break;
            }
        }
        return found;
    }

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
