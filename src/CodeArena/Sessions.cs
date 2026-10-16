using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A saved session as it is listed: which, where, when, how it began, the chat in Arena it is kept in step with (null:
/// none), whether it ever was (false: it was never sent to Arena; true with no chat: its chat was deleted there), and
/// how many of the messages it added itself Arena has of how many there are.
/// </summary>
internal sealed record SessionSummary(string Id, string File, string? Cwd, DateTime Updated, string Preview, int Messages, string? Conversation = null, bool Synced = false,
    int Pushed = 0, int Local = 0)
{
    /// <summary>Arena lacks some of it: never sent, or sent in part (its chat not deleted there).</summary>
    public bool Unsent => Local > 0 && (!Synced || (Conversation is not null && Pushed < Local));
}

/// <summary>A saved session read back: its messages (after the last compaction), model, to-do list and tokens.</summary>
internal sealed class SessionData
{
    public string Id { get; init; } = "";
    public string? Cwd { get; set; }
    public string? Model { get; set; }
    public List<JsonObject> Messages { get; } = [];
    public List<TodoItem> Todos { get; set; } = [];
    public long Prompt { get; set; }
    public long Cached { get; set; }
    public long Completion { get; set; }
    /// <summary>Each answer's (by its place in Messages) model and tokens, for the web interface.</summary>
    public Dictionary<int, (string? Model, TokenUsage? Usage)> Answers { get; } = [];
    /// <summary>The chat in Arena this session is kept in step with; null: none yet (or it was deleted there).</summary>
    public string? Conversation { get; set; }
    /// <summary>The Arena chat's messages as last seen in step with it.</summary>
    public int ServerCount { get; set; }
    /// <summary>How many of <see cref="Local"/> Arena has.</summary>
    public int Pushed { get; set; }
    /// <summary>The chat in Arena was deleted: the session is no longer kept in step.</summary>
    public bool Unlinked { get; set; }
    /// <summary>Every message this session added itself (not those taken in from the web), compactions aside, in order.</summary>
    public List<JsonObject> Local { get; } = [];
    /// <summary>The last answer's tokens since the last compaction, and how many of <see cref="Messages"/> its request and answer covered; null: none counted.</summary>
    public (TokenUsage Usage, int Count)? Known { get; set; }
}

/// <summary>
/// A session as JSON Lines under the data folder's sessions/: a header, then
/// each message as it is added; a compaction writes the whole new history, and
/// reading takes the last one. The file is readable by its owner only.
/// </summary>
internal sealed class SessionStore
{
    private readonly object _gate = new();

    private SessionStore(string id, string file)
    {
        Id = id;
        File = file;
    }

    public string Id { get; }
    public string File { get; }

    public static SessionStore Create(string dir, string cwd, string model)
    {
        PrivateFiles.EnsureDirectory(dir);
        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var store = new SessionStore(id, Path.Combine(dir, id + ".jsonl"));
        store.Write(new JsonObject
        {
            ["type"] = "session", ["id"] = id, ["cwd"] = cwd, ["model"] = model,
            ["created"] = DateTimeOffset.Now.ToString("o"), ["version"] = Cli.Version,
        });
        return store;
    }

    /// <summary>Carries on writing to a saved session.</summary>
    public static SessionStore Open(string file) => new(Path.GetFileNameWithoutExtension(file), file);

    public void Message(JsonObject message)
    {
        Write(new JsonObject { ["type"] = "message", ["message"] = message.Clone() });
        Added?.Invoke(message);
    }

    /// <summary>Told of each message the session adds (the chat in Arena is sent it).</summary>
    public event Action<JsonObject>? Added;

    /// <summary>A message the person added in Arena's web chat, taken into this session.</summary>
    public void Remote(JsonObject message) => Write(new JsonObject { ["type"] = "message", ["message"] = message.Clone(), ["from"] = "web" });

    /// <summary>Where the session stands with its chat in Arena: which chat (null: none any more), its count, how many of ours it has.</summary>
    public void Sync(string? conversation, int server, int pushed) =>
        Write(new JsonObject { ["type"] = "sync", ["conversation"] = conversation, ["server"] = server, ["pushed"] = pushed });

    public void Compacted(IEnumerable<JsonObject> messages) =>
        Write(new JsonObject { ["type"] = "compact", ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)m.Clone())]) });

    public void Model(string model) => Write(new JsonObject { ["type"] = "model", ["model"] = model });

    public void Todos(IEnumerable<TodoItem> todos) =>
        Write(new JsonObject { ["type"] = "todos", ["todos"] = new JsonArray([.. todos.Select(t => (JsonNode)new JsonObject { ["content"] = t.Content, ["status"] = t.Status })]) });

    public void Usage(TokenUsage usage) =>
        Write(new JsonObject { ["type"] = "usage", ["prompt"] = usage.Prompt, ["cached"] = usage.Cached, ["completion"] = usage.Completion });

    private void Write(JsonObject line)
    {
        lock (_gate)
        {
            PrivateFiles.AppendLine(File, Json.Line(line));
        }
    }

    /// <summary>
    /// Reads a session back. Its messages, from its last compaction on (where <see cref="Scan"/> found it, so a long
    /// session's earlier histories are not read again); its model, to-do list, tokens and where it stands with Arena from
    /// the scan. <paramref name="whole"/>: every line is read, for <see cref="SessionData.Local"/> (the messages it added
    /// itself, compactions aside) that keeping it in step with Arena needs.
    /// </summary>
    public static SessionData Load(string file, bool whole = false)
    {
        if (whole)
        {
            return LoadWhole(file);
        }
        var scan = Scan(file);
        var data = new SessionData
        {
            Id = Path.GetFileNameWithoutExtension(file), Cwd = scan.Cwd, Model = scan.ModelAtCompact, Todos = [.. scan.Todos],
            Prompt = scan.Prompt, Cached = scan.Cached, Completion = scan.Completion,
            Conversation = scan.Conversation, Unlinked = scan.Synced && scan.Conversation is null, ServerCount = scan.ServerCount, Pushed = scan.Pushed,
        };
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
                    Answer(data, m, ref pending);
                    data.Messages.Add(m.Clone());
                    break;
                case "compact" when entry["messages"] is JsonArray all:
                    Compact(data, all, ref pending);
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

    private static SessionData LoadWhole(string file)
    {
        var data = new SessionData { Id = Path.GetFileNameWithoutExtension(file) };
        // An answer's tokens are written just before the answer itself.
        TokenUsage? pending = null;
        // Shared: the web interface reads a session while its turn appends to it.
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            if (Json.ParseObject(line) is not { } entry)
            {
                continue; // a line cut short when the program was stopped mid-write
            }
            switch (entry.Str("type"))
            {
                case "session":
                    data.Cwd = entry.Str("cwd");
                    data.Model = entry.Str("model");
                    break;
                case "message" when entry["message"] is JsonObject m:
                    Answer(data, m, ref pending);
                    data.Messages.Add(m.Clone());
                    if (entry.Str("from") != "web")
                    {
                        data.Local.Add(m.Clone());
                    }
                    break;
                case "sync":
                    data.Conversation = entry.Str("conversation");
                    data.Unlinked = data.Conversation is null;
                    data.ServerCount = (int)(entry.Long("server") ?? 0);
                    data.Pushed = (int)(entry.Long("pushed") ?? 0);
                    break;
                case "compact" when entry["messages"] is JsonArray all:
                    Compact(data, all, ref pending);
                    break;
                case "model":
                    data.Model = entry.Str("model") ?? data.Model;
                    break;
                case "todos" when entry["todos"] is JsonArray todos:
                    data.Todos = Todos(todos);
                    break;
                case "usage":
                    pending = Usage(entry);
                    data.Prompt += pending.Prompt;
                    data.Cached += pending.Cached;
                    data.Completion += pending.Completion;
                    break;
            }
        }
        return data;
    }

    /// <summary>An answer's model and tokens (written just before it), by its place among the messages.</summary>
    private static void Answer(SessionData data, JsonObject m, ref TokenUsage? pending)
    {
        if (m.Str("role") == "assistant")
        {
            data.Answers[data.Messages.Count] = (data.Model, pending);
            if (pending is not null)
            {
                data.Known = (pending, data.Messages.Count + 1);
            }
        }
        pending = null;
    }

    private static void Compact(SessionData data, JsonArray all, ref TokenUsage? pending)
    {
        data.Messages.Clear();
        data.Answers.Clear();
        data.Known = null;
        pending = null;
        data.Messages.AddRange(all.OfType<JsonObject>().Select(m => m.Clone()));
    }

    private static TokenUsage Usage(JsonObject entry) => new(entry.Long("prompt") ?? 0, entry.Long("cached") ?? 0, entry.Long("completion") ?? 0);

    private static List<TodoItem> Todos(JsonArray todos) => [.. todos.OfType<JsonObject>().Select(t => new TodoItem(t.Str("content") ?? "", t.Str("status") ?? "pending"))];

    /// <summary>
    /// What a session file holds as it is listed and read back, scanned once and kept by the file's size and time: an
    /// appended file is scanned from where the scan before stopped, and only the lines that say little are parsed (a
    /// message is counted, a compaction's place noted).
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
        public string? Conversation { get; init; }
        /// <summary>Its sync state was ever written (false: never sent to Arena).</summary>
        public bool Synced { get; init; }
        public int ServerCount { get; init; }
        public int Pushed { get; init; }
        /// <summary>Message lines: all of them, and those it added itself (not taken in from the web).</summary>
        public int Messages { get; init; }
        public int Local { get; init; }
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
        var from = known is not null && size >= known.Length && size >= known.Size ? known : new SessionScan();
        var scan = Continue(file, from, size, written);
        lock (Scans)
        {
            Scans[file] = scan;
        }
        return scan;
    }

    private static readonly byte[] TypeMessage = Encoding.UTF8.GetBytes("{\"type\":\"message\"");
    private static readonly byte[] TypeCompact = Encoding.UTF8.GetBytes("{\"type\":\"compact\"");
    private static readonly byte[] FromWeb = Encoding.UTF8.GetBytes(",\"from\":\"web\"}");

    private static SessionScan Continue(string file, SessionScan s, long size, DateTime written)
    {
        var (cwd, model, atCompact, todos, conversation, synced, server, pushed) = (s.Cwd, s.Model, s.ModelAtCompact, s.Todos, s.Conversation, s.Synced, s.ServerCount, s.Pushed);
        var (messages, local, preview, prompt, cached, completion, compact, length) = (s.Messages, s.Local, s.Preview, s.Prompt, s.Cached, s.Completion, s.LastCompact, s.Length);
        foreach (var (at, bytesLong, bytes) in Lines(file, s.Length, size, skip: b => b.AsSpan().StartsWith(TypeCompact)))
        {
            length = at + bytesLong + 1;
            var line = bytes.AsSpan();
            if (line.StartsWith(TypeCompact))
            {
                compact = at;
                atCompact = model;
                continue;
            }
            if (line.StartsWith(TypeMessage))
            {
                messages++;
                if (!line.EndsWith(FromWeb))
                {
                    local++;
                }
                if (preview.Length == 0 && Json.ParseObject(Encoding.UTF8.GetString(line))?["message"] is JsonObject m && m.Str("role") == "user")
                {
                    preview = Fmt.OneLine(m.Str("content"), 70);
                }
                continue;
            }
            if (Json.ParseObject(Encoding.UTF8.GetString(line)) is not { } entry)
            {
                continue;
            }
            switch (entry.Str("type"))
            {
                case "session":
                    cwd = entry.Str("cwd");
                    model = entry.Str("model");
                    atCompact = compact < 0 ? model : atCompact;
                    break;
                case "sync":
                    conversation = entry.Str("conversation");
                    synced = true;
                    server = (int)(entry.Long("server") ?? 0);
                    pushed = (int)(entry.Long("pushed") ?? 0);
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
            Size = size, Written = written, Length = length, Cwd = cwd, Model = model, ModelAtCompact = atCompact, Todos = todos, Conversation = conversation,
            Synced = synced, ServerCount = server, Pushed = pushed, Messages = messages, Local = local, Preview = preview, Prompt = prompt, Cached = cached,
            Completion = completion, LastCompact = compact,
        };
    }

    /// <summary>
    /// The whole lines of a file between two places, each with where it starts and its length (a last line not ended yet
    /// is left for the next read). A line <paramref name="skip"/> says so of from its start is passed over, its bytes not
    /// kept: given as its start alone.
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
                if (!skipping)
                {
                    line.Write(buffer, i, stop - i);
                    if (skip is not null && line.Length >= head && skip(line.GetBuffer()[..head]))
                    {
                        skipping = true;
                    }
                }
                if (end < 0)
                {
                    break;
                }
                yield return (start, at + end - start, skipping ? line.GetBuffer()[..head] : line.ToArray());
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
            var scan = Scan(file.FullName);
            if (cwd is not null && (scan.Cwd is null || !SameFolder(scan.Cwd, cwd)))
            {
                continue;
            }
            if (scan.Messages == 0)
            {
                continue;
            }
            found.Add(new SessionSummary(Path.GetFileNameWithoutExtension(file.Name), file.FullName, scan.Cwd, file.LastWriteTime, scan.Preview, scan.Messages,
                scan.Conversation, scan.Synced, scan.Pushed, scan.Local));
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
            if (string.Equals(scan.Conversation, conversation, StringComparison.OrdinalIgnoreCase) && scan.Cwd is not null && SameFolder(scan.Cwd, cwd))
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
