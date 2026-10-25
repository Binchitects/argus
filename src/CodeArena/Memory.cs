using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// What the agent remembers. Signed in to Arena, Arena's memory (the same as the web chat's): the person's for every
/// project, the project's by its repository's key, the company's; the session starts with its block (pinned, the
/// company's, the newest) and each message brings what matters to it. Without Arena (offline, not signed in), the files
/// on this machine: the person's memory.md in the config folder, and this project's in the data folder (never in the
/// repository), each memory a line "- ..." the person may edit; they go to Arena when it next answers.
/// </summary>
internal sealed class MemoryStore(AppPaths paths, string projectRoot)
{
    /// <summary>Arena's memory, when Code Arena is signed in (null: the files on this machine only).</summary>
    public ArenaMemory? Arena { get; set; }

    /// <summary>Arena answered at the session's start: memory is Arena's for this session.</summary>
    public bool Online { get; private set; }

    /// <summary>The memories Arena gave at the session's start (newest first, pinned before): /memory numbers them.</summary>
    public List<ArenaMemoryItem> Listed { get; private set; } = [];

    /// <summary>This project's key in Arena: its repository's remote (host/group/name), or this folder's on this machine.</summary>
    public string ProjectKey => _projectKey ??= ProjectKeys.Of(projectRoot);
    private string? _projectKey;

    private string CacheFile => Path.Combine(paths.DataDir, "memory", Key(projectRoot) + ".arena.json");

    /// <summary>
    /// The prompt's memory for this session. With Arena: the files kept on this machine first go to it (once), then its
    /// block for this project, kept here too for a session without Arena. Without: the files, and the block kept last time.
    /// </summary>
    public async Task<string> ForPromptAsync(Action<string>? say, CancellationToken ct)
    {
        if (Arena is { } arena)
        {
            await ImportAsync(arena, say, ct);
            if (await arena.ListAsync(ProjectKey, ct) is { } list)
            {
                (Online, Listed) = (true, list.Memories);
                try
                {
                    PrivateFiles.EnsureDirectory(Path.GetDirectoryName(CacheFile)!);
                    PrivateFiles.WriteAllText(CacheFile, new JsonObject { ["note"] = list.Note }.ToJsonString());
                }
                catch (IOException)
                {
                    // Not kept: the next session without Arena has the files only.
                }
                // Nothing remembered yet: no section at all (remember is described in the prompt's way of working).
                return _prompt = list.Note is { Length: > 0 } note ? $"\n# Memory\n\n{note}\n\n{More}\n" : "";
            }
        }
        Online = false;
        return _prompt = Offline();
    }

    private string _prompt = "";

    /// <summary>The prompt's memory again (a new or resumed session): Arena's as the session started, else the files read anew.</summary>
    public string Again() => Online ? _prompt : _prompt = Offline();

    private string Offline()
    {
        var kept = File.Exists(CacheFile) ? Json.ParseObject(File.ReadAllText(CacheFile))?.Str("note") : null;
        return ForPrompt() + (kept is { Length: > 0 } ? $"\n# Memory (as Arena last gave it)\n\n{kept}\n" : "");
    }

    private const string More = "More is remembered than is shown here: the memories that matter to each message of the person come with it, " +
        "in Code Arena's note, and recall searches all of memory. Keep what a later session should know with remember.";

    /// <summary>The memory files' lines, sent to Arena; the files kept beside as ".imported" once it took them.</summary>
    private async Task ImportAsync(ArenaMemory arena, Action<string>? say, CancellationToken ct)
    {
        var items = Read(true).Select(m => new ArenaMemoryInput(m, "person", null))
            .Concat(Read(false).Select(m => new ArenaMemoryInput(m, "project", ProjectKey))).ToList();
        if (items.Count == 0 || await arena.ImportAsync(items, ct) is not { } kept)
        {
            return;
        }
        foreach (var file in new[] { PersonFile, ProjectFile }.Where(File.Exists))
        {
            var done = file + ".imported";
            PrivateFiles.WriteAllText(done, (File.Exists(done) ? File.ReadAllText(done) : "") + File.ReadAllText(file));
            File.Delete(file);
        }
        say?.Invoke($"{items.Count} memories kept on this machine went to Arena ({kept} new): it keeps Code Arena's memory now, the web chat's too.");
    }

    /// <summary>Keeps a memory: in Arena when it answered at the start, else in the files (they go to Arena next time). False: the same is kept already.</summary>
    public async Task<(bool Added, string Where)> RememberAsync(bool person, string text, string kind, CancellationToken ct)
    {
        var where = person ? "for every project" : "for this project";
        if (Online && Arena is { } arena)
        {
            var kept = await arena.KeepAsync(text, person ? "person" : "project", kind, person ? null : ProjectKey, ct);
            return (Listed.All(m => m.Id != kept.Id), where + ", in Arena");
        }
        return (Add(person, text), where + " (on this machine: it goes to Arena when Code Arena next reaches it)");
    }

    /// <summary>
    /// The memories that matter to a question, not among <paramref name="besides"/>: Arena's recall when it answered at the
    /// start; else the files' lines sharing its words. Null when nothing matters.
    /// </summary>
    public async Task<(string Note, List<string> Ids)?> RecallAsync(string query, IReadOnlyCollection<string> besides, int budget, CancellationToken ct)
    {
        if (Online && Arena is { } arena)
        {
            return await arena.RecallAsync(query, ProjectKey, besides, budget, ct);
        }
        var words = Words(query);
        var found = Read(true).Concat(Read(false)).Select(m => (Text: m, Score: Score(words, Words(m)))).Where(x => x.Score >= 0.2 && !besides.Contains(x.Text))
            .OrderByDescending(x => x.Score).Take(10).ToList();
        if (found.Count == 0)
        {
            return null;
        }
        return (RecallHeading + string.Concat(found.Select(f => "\n- " + f.Text)) + ")", [.. found.Select(f => f.Text)]);
    }

    /// <summary>How the memories found for a message start (as Arena says them).</summary>
    public const string RecallHeading = "(Memories that may matter to this question; use them where they help:";

    /// <summary>The words that carry meaning, in lower case.</summary>
    internal static HashSet<string> Words(string text) =>
        [.. System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}_-]{3,}").Select(m => m.Value).Where(w => !Common.Contains(w))];

    private static double Score(HashSet<string> a, HashSet<string> b) => a.Count == 0 || b.Count == 0 ? 0 : a.Count(b.Contains) / Math.Sqrt((double)a.Count * b.Count);

    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "what", "which", "how", "are", "was", "were", "has", "have", "its", "into", "about",
        "when", "where", "who", "why", "does", "did", "can", "not", "but", "you", "your", "they", "their", "there", "our", "all", "use", "please",
    };

    /// <summary>Forgets the memory /memory lists at this place (from 1), in Arena when it answered at the start; the one forgotten, or null.</summary>
    public async Task<string?> ForgetListedAsync(int number, CancellationToken ct)
    {
        var mine = Listed.Where(m => m.Mine).ToList();
        if (Arena is not { } arena || number < 1 || number > mine.Count)
        {
            return null;
        }
        var gone = mine[number - 1];
        if (!await arena.ForgetAsync(gone.Id, ct))
        {
            return null;
        }
        Listed.Remove(gone);
        return gone.Text;
    }

    /// <summary>The most a memory file gives the prompt.</summary>
    public const int MaxChars = 12_000;

    public string PersonFile => Path.Combine(paths.ConfigDir, "memory.md");

    public string ProjectFile => Path.Combine(paths.DataDir, "memory", Key(projectRoot) + ".md");

    /// <summary>A project's file name: its folder's name and a hash of its whole path (two folders of one name stay apart).</summary>
    internal static string Key(string root)
    {
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = new string([.. Path.GetFileName(full).Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')]);
        return $"{(name.Length == 0 ? "root" : name)}-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..10]}";
    }

    public string FileOf(bool person) => person ? PersonFile : ProjectFile;

    /// <summary>The memories, in order (the lines "- ..." of the file).</summary>
    public List<string> Read(bool person)
    {
        var file = FileOf(person);
        return File.Exists(file)
            ? [.. File.ReadAllLines(file).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..].Trim()).Where(l => l.Length > 0)]
            : [];
    }

    /// <summary>Adds a memory (one line); false when the same is there already.</summary>
    public bool Add(bool person, string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (line.Length == 0)
        {
            throw new ToolError("There is nothing to remember: give the memory as text.");
        }
        if (Read(person).Any(m => string.Equals(m, line, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var file = FileOf(person);
        PrivateFiles.EnsureDirectory(Path.GetDirectoryName(file)!);
        if (!File.Exists(file))
        {
            PrivateFiles.WriteAllText(file, person
                ? "# What Code Arena remembers about you\n\nFor every project. Edit freely: each memory is a line that starts with \"- \".\n\n"
                : $"# What Code Arena remembers about {Path.GetFileName(Path.GetFullPath(projectRoot))}\n\n{Path.GetFullPath(projectRoot)}. On this machine only. Edit freely: each memory is a line that starts with \"- \".\n\n");
        }
        PrivateFiles.AppendLine(file, "- " + line);
        return true;
    }

    /// <summary>Forgets the memory at this place (from 1); the one forgotten, or null when there is none.</summary>
    public string? Forget(bool person, int number)
    {
        var file = FileOf(person);
        if (!File.Exists(file))
        {
            return null;
        }
        var lines = File.ReadAllLines(file).ToList();
        var places = lines.Select((l, i) => (l, i)).Where(x => x.l.StartsWith("- ", StringComparison.Ordinal) && x.l[2..].Trim().Length > 0).Select(x => x.i).ToList();
        if (number < 1 || number > places.Count)
        {
            return null;
        }
        var gone = lines[places[number - 1]][2..].Trim();
        lines.RemoveAt(places[number - 1]);
        PrivateFiles.WriteAllText(file, string.Join('\n', lines) + "\n");
        return gone;
    }

    /// <summary>Both memories as the prompt gives them; empty when there are none.</summary>
    public string ForPrompt()
    {
        var sb = new StringBuilder();
        foreach (var (person, title) in new[] { (true, "What you remember about the person (every project)"), (false, "What you remember about this project") })
        {
            var all = Read(person);
            if (all.Count == 0)
            {
                continue;
            }
            var text = string.Join('\n', all.Select(m => "- " + m));
            sb.Append($"\n# {title}\n\n{(text.Length <= MaxChars ? text : text[..MaxChars] + "\n… (cut: the file is longer)")}\n");
        }
        return sb.ToString();
    }

    public static ToolDef RememberTool() => new()
    {
        Name = "remember",
        Kind = ToolKind.Read,
        Description = "Keep something for later sessions: about this project (how to build or test it, a convention, a decision and why, a lesson from something that went wrong), or about the person (a preference for every project, with scope person). One or two short sentences each; not secrets, not what is in the code already. The person sees it and can edit or forget it (/memory, and Memory in Arena).",
        Parameters = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{
              "memory":{"type":"string","description":"One or two sentences, as they should be read in a later session."},
              "scope":{"type":"string","enum":["project","person"],"description":"project (the default): this project only; person: every project."},
              "kind":{"type":"string","enum":["preference","fact","decision","howto","lesson"],"description":"preference (how the person likes things), fact (the default), decision (and why), howto (how something is done here), lesson (what went wrong once, or what the person corrected)."}},
             "required":["memory"]}
            """)!,
        Summary = a => Fmt.OneLine(a.Str("memory") ?? "", 100),
        Run = async (a, c, ct) =>
        {
            if (c.Memory is not { } memory)
            {
                throw new ToolError("Memory is not kept in this session.");
            }
            var person = a.Str("scope") == "person";
            var kind = a.Str("kind") is "preference" or "decision" or "howto" or "lesson" ? a.Str("kind")! : "fact";
            var (added, where) = await memory.RememberAsync(person, a.Str("memory") ?? "", kind, ct);
            return new ToolResult(added ? $"Remembered {where}." : $"Already remembered {where}.") { Display = c.Ui.Dim($"remembered {where}") };
        },
    };

    public static ToolDef RecallTool() => new()
    {
        Name = "recall",
        Kind = ToolKind.Read,
        Description = "Search what is remembered (the person's, this project's and the company's memories) for what bears on a question, when what came with the message is not enough: the nearest by meaning.",
        Parameters = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{"query":{"type":"string","description":"What to look for, in a few words or a question."}},"required":["query"]}
            """)!,
        Summary = a => a.Str("query") ?? "",
        Run = async (a, c, ct) =>
        {
            if (c.Memory is not { } memory)
            {
                throw new ToolError("Memory is not kept in this session.");
            }
            var found = await memory.RecallAsync(a.Str("query") ?? "", [], 6_000, ct);
            if (found is not { } f)
            {
                return new ToolResult("Nothing remembered bears on that.") { Display = c.Ui.Dim("nothing found") };
            }
            // The memories without the note's heading and its closing bracket.
            var lines = f.Note[(f.Note.IndexOf('\n', StringComparison.Ordinal) + 1)..];
            return new ToolResult(lines.EndsWith(')') ? lines[..^1] : lines) { Display = c.Ui.Dim($"{f.Ids.Count} found") };
        },
    };
}

/// <summary>Searching the saved sessions: what was said before, in this folder or all of them.</summary>
internal static class SessionSearch
{
    public sealed record Hit(string Session, DateTime When, string? Cwd, string Role, string Snippet);

    /// <summary>Messages holding every word of the query (any case), newest sessions first; at most <paramref name="most"/>.</summary>
    public static List<Hit> Find(string sessionsDir, string query, string? cwd, string? skipSession, int most = 20)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || !Directory.Exists(sessionsDir))
        {
            return [];
        }
        var hits = new List<Hit>();
        foreach (var file in new DirectoryInfo(sessionsDir).EnumerateFiles("*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            var id = Path.GetFileNameWithoutExtension(file.Name);
            if (id == skipSession)
            {
                continue;
            }
            // Its folder from the scan kept of it: the file is read whole only when it is of this folder.
            var data = SessionStore.Scan(file.FullName);
            if (cwd is not null && !string.Equals(data.Cwd, cwd, StringComparison.Ordinal))
            {
                continue;
            }
            // Every message the session had, compactions aside: its own and those taken in from the web.
            foreach (var m in Messages(file.FullName))
            {
                var text = Text(m);
                if (text.Length == 0 || !words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                var at = text.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
                var start = Math.Max(0, at - 120);
                var snippet = (start > 0 ? "…" : "") + text.Substring(start, Math.Min(text.Length - start, 300)).ReplaceLineEndings(" ") + (start + 300 < text.Length ? "…" : "");
                hits.Add(new Hit(id, file.LastWriteTime, data.Cwd, m.Str("role") ?? "", snippet));
                if (hits.Count >= most)
                {
                    return hits;
                }
            }
        }
        return hits;
    }

    private static IEnumerable<JsonObject> Messages(string file)
    {
        using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is { } line)
        {
            if (Json.ParseObject(line) is { } entry && entry.Str("type") == "message" && entry["message"] is JsonObject m)
            {
                yield return m;
            }
        }
    }

    private static string Text(JsonObject m) => m["content"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Join(' ', parts.OfType<JsonObject>().Select(p => p.Str("text") ?? "")),
        _ => "",
    };

    public static ToolDef Tool() => new()
    {
        Name = "search_sessions",
        Kind = ToolKind.Read,
        Description = "Search earlier sessions with the person (what was asked, answered, run or found), in this folder unless all_folders: messages that hold every word of the query. For \"as we did last time\", or a decision made before.",
        Parameters = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{
              "query":{"type":"string","description":"Words that must all be in the message (any case)."},
              "all_folders":{"type":"boolean","description":"Every folder's sessions, not only this one's."}},
             "required":["query"]}
            """)!,
        Summary = a => a.Str("query") ?? "",
        Run = (a, c, ct) =>
        {
            if (c.SessionsDir is not { } dir)
            {
                throw new ToolError("Earlier sessions cannot be searched here.");
            }
            var hits = Find(dir, a.Str("query") ?? "", a.Bool("all_folders") == true ? null : c.Workspace.Root, c.SessionId?.Invoke());
            var text = hits.Count == 0
                ? "No earlier session has a message with all of those words."
                : string.Join('\n', hits.Select(h => $"- {h.When:yyyy-MM-dd HH:mm} session {h.Session}{(a.Bool("all_folders") == true ? $" ({h.Cwd})" : "")}, {h.Role}: {h.Snippet}"));
            return Task.FromResult(new ToolResult(text) { Display = c.Ui.Dim($"{hits.Count} found") });
        },
    };
}

/// <summary>A memory as Arena gives it to Code Arena.</summary>
internal sealed record ArenaMemoryItem(string Id, string Text, string Scope, string Kind, string? Project, bool Pinned, bool Mine);

/// <summary>What a session starts with: the prompt's block, the memories in it, and every memory it may read.</summary>
internal sealed record ArenaMemoryList(string? Note, List<string> InNote, List<ArenaMemoryItem> Memories);

/// <summary>A memory of the machine's files, brought to Arena.</summary>
internal sealed record ArenaMemoryInput(string Text, string Scope, string? Project);

/// <summary>Arena's memory, read and written with the person's key (/api/code-arena/memories).</summary>
internal sealed class ArenaMemory(Config config, HttpClient http)
{
    /// <summary>How long one call waits for Arena: memory must not hold a session's start or a message.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(8);

    private string Url(string path) => config.Url!.TrimEnd('/') + "/api/code-arena/memories" + path;

    private async Task<JsonObject?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct, bool throwRefusal = false)
    {
        if (config.Url is not { Length: > 0 } || config.ApiKey is not { Length: > 0 } key)
        {
            return null;
        }
        try
        {
            using var req = ChatSync.Request(method, Url(path), key, body);
            using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
            within.CancelAfter(Limit);
            using var res = await http.SendAsync(req, within.Token);
            var answer = res.StatusCode == System.Net.HttpStatusCode.NoContent ? new JsonObject() : Json.ParseObject(await res.Content.ReadAsStringAsync(within.Token));
            if (!res.IsSuccessStatusCode)
            {
                return throwRefusal ? throw new ToolError($"Arena did not keep it: {answer?.Str("error") ?? answer?.Str("detail") ?? $"HTTP {(int)res.StatusCode}"}") : null;
            }
            return answer ?? new JsonObject();
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return throwRefusal ? throw new ToolError("Arena did not answer: try again in a moment.") : null;
        }
    }

    private static ArenaMemoryItem Item(JsonObject m) =>
        new(m.Str("id") ?? "", m.Str("text") ?? "", m.Str("scope") ?? "person", m.Str("kind") ?? "fact", m.Str("project"), m.Bool("pinned") == true, m.Bool("mine") != false);

    public async Task<ArenaMemoryList?> ListAsync(string project, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, "?project=" + Uri.EscapeDataString(project), null, ct) is { } a
            ? new ArenaMemoryList(a.Str("note"), [.. (a["inNote"] as JsonArray ?? []).Select(x => x?.GetValue<string>() ?? "")],
                [.. (a["memories"] as JsonArray ?? []).OfType<JsonObject>().Select(Item)])
            : null;

    public async Task<ArenaMemoryItem> KeepAsync(string text, string scope, string kind, string? project, CancellationToken ct) =>
        Item((await SendAsync(HttpMethod.Post, "", new JsonObject { ["text"] = text, ["scope"] = scope, ["kind"] = kind, ["project"] = project }, ct, throwRefusal: true))!);

    public async Task<bool> ForgetAsync(string id, CancellationToken ct) => await SendAsync(HttpMethod.Delete, "/" + Uri.EscapeDataString(id), null, ct) is not null;

    public async Task<(string Note, List<string> Ids)?> RecallAsync(string query, string project, IReadOnlyCollection<string> besides, int budget, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["query"] = query.Length > 4_000 ? query[..4_000] : query, ["project"] = project, ["budget"] = budget,
            ["besides"] = new JsonArray([.. besides.Where(b => Guid.TryParse(b, out _)).Select(b => (JsonNode)b)]),
        };
        return await SendAsync(HttpMethod.Post, "/recall", body, ct) is { } a && a.Str("note") is { Length: > 0 } note
            ? (note, [.. (a["memories"] as JsonArray ?? []).OfType<JsonObject>().Select(m => m.Str("id") ?? "")])
            : null;
    }

    /// <summary>The machine's memories brought in; how many were new, or null when Arena did not take them.</summary>
    public async Task<int?> ImportAsync(IReadOnlyList<ArenaMemoryInput> items, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, "/import", new JsonObject
        {
            ["memories"] = new JsonArray([.. items.Select(i => (JsonNode)new JsonObject { ["text"] = i.Text, ["scope"] = i.Scope, ["project"] = i.Project })]),
        }, ct) is { } a ? (int?)a.Int("kept") ?? 0 : null;
}

/// <summary>A project's key in Arena: its repository, as its origin remote names it (host/group/name), else this folder's.</summary>
internal static class ProjectKeys
{
    public static string Of(string root) => Remote(root) is { } url && FromRemote(url) is { } key ? key : "local/" + MemoryStore.Key(root);

    /// <summary>"git@gitlab.corp:payments/api.git", "https://user@gitlab.corp/payments/api", "ssh://git@gitlab.corp:2222/payments/api.git" → "gitlab.corp/payments/api".</summary>
    public static string? FromRemote(string url)
    {
        var u = url.Trim();
        string? host, path;
        if (Uri.TryCreate(u, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "ssh" or "git")
        {
            (host, path) = (parsed.Host, parsed.AbsolutePath);
        }
        else if (System.Text.RegularExpressions.Regex.Match(u, @"^(?:[^@/]+@)?([^:/]+):(.+)$") is { Success: true } scp)
        {
            (host, path) = (scp.Groups[1].Value, scp.Groups[2].Value);
        }
        else
        {
            return null;
        }
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }
        return host.Length == 0 || path.Length == 0 ? null : $"{host.ToLowerInvariant()}/{path}";
    }

    /// <summary>The origin remote's URL from the repository's config (a worktree's from its common directory).</summary>
    private static string? Remote(string root)
    {
        try
        {
            if (SystemPrompt.GitRoot(root) is not { } top)
            {
                return null;
            }
            var git = Path.Combine(top, ".git");
            if (File.Exists(git) && File.ReadAllText(git).Trim() is var pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                git = Path.GetFullPath(pointer[7..].Trim(), top);
                if (File.Exists(Path.Combine(git, "commondir")))
                {
                    git = Path.GetFullPath(File.ReadAllText(Path.Combine(git, "commondir")).Trim(), git);
                }
            }
            var inOrigin = false;
            foreach (var raw in File.ReadLines(Path.Combine(git, "config")))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    inOrigin = line.Replace(" ", "", StringComparison.Ordinal) == "[remote\"origin\"]";
                }
                else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal) && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var eq)
                {
                    return line[(eq + 1)..].Trim();
                }
            }
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

