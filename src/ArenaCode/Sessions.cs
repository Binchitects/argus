using System.Text.Json.Nodes;

namespace ArenaCode;

/// <summary>A saved session as it is listed: which, where, when, and how it began.</summary>
internal sealed record SessionSummary(string Id, string File, string? Cwd, DateTime Updated, string Preview, int Messages);

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

    public void Message(JsonObject message) => Write(new JsonObject { ["type"] = "message", ["message"] = message.Clone() });

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

    public static SessionData Load(string file)
    {
        var data = new SessionData { Id = Path.GetFileNameWithoutExtension(file) };
        foreach (var line in System.IO.File.ReadLines(file))
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
                    data.Messages.Add(m.Clone());
                    break;
                case "compact" when entry["messages"] is JsonArray all:
                    data.Messages.Clear();
                    data.Messages.AddRange(all.OfType<JsonObject>().Select(m => m.Clone()));
                    break;
                case "model":
                    data.Model = entry.Str("model") ?? data.Model;
                    break;
                case "todos" when entry["todos"] is JsonArray todos:
                    data.Todos = [.. todos.OfType<JsonObject>().Select(t => new TodoItem(t.Str("content") ?? "", t.Str("status") ?? "pending"))];
                    break;
                case "usage":
                    data.Prompt += entry.Long("prompt") ?? 0;
                    data.Cached += entry.Long("cached") ?? 0;
                    data.Completion += entry.Long("completion") ?? 0;
                    break;
            }
        }
        return data;
    }

    /// <summary>Saved sessions, newest first; those of this folder only when cwd is given.</summary>
    public static List<SessionSummary> List(string dir, string? cwd = null, int max = 20)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }
        var found = new List<SessionSummary>();
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            string? sessionCwd = null;
            var preview = "";
            var messages = 0;
            foreach (var line in System.IO.File.ReadLines(file.FullName))
            {
                var entry = Json.ParseObject(line);
                if (entry.Str("type") == "session")
                {
                    sessionCwd = entry.Str("cwd");
                }
                else if (entry.Str("type") == "message")
                {
                    messages++;
                    if (preview.Length == 0 && entry?["message"].Str("role") == "user")
                    {
                        preview = Fmt.OneLine(entry["message"].Str("content"), 70);
                    }
                }
            }
            if (cwd is not null && !string.Equals(sessionCwd, cwd, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                continue;
            }
            if (messages == 0)
            {
                continue;
            }
            found.Add(new SessionSummary(Path.GetFileNameWithoutExtension(file.Name), file.FullName, sessionCwd, file.LastWriteTime, preview, messages));
            if (found.Count >= max)
            {
                break;
            }
        }
        return found;
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
