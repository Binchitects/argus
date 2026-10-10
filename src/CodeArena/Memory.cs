using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// What the agent remembers: the person's own memory (memory.md in the config folder, for every project) and this
/// project's (in the data folder, by the project's path: on this machine only, never in the repository). Each memory
/// is a line "- ..."; the files are plain Markdown the person may edit. The prompt carries them as a session starts.
/// </summary>
internal sealed class MemoryStore(AppPaths paths, string projectRoot)
{
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
        Description = "Keep something for later sessions: a fact about this project (how to build or test it, a convention, a decision), or about the person (a preference for every project, with scope person). One short line each; not secrets, not what is in the code already. The person sees it and can edit or forget it (/memory).",
        Parameters = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{
              "memory":{"type":"string","description":"One line, as it should be read in a later session."},
              "scope":{"type":"string","enum":["project","person"],"description":"project (the default): this project only; person: every project."}},
             "required":["memory"]}
            """)!,
        Summary = a => Fmt.OneLine(a.Str("memory") ?? "", 100),
        Run = (a, c, ct) =>
        {
            if (c.Memory is not { } memory)
            {
                throw new ToolError("Memory is not kept in this session.");
            }
            var person = a.Str("scope") == "person";
            var text = a.Str("memory") ?? "";
            var added = memory.Add(person, text);
            var where = person ? "for every project" : "for this project";
            return Task.FromResult(new ToolResult(added ? $"Remembered {where}." : $"Already remembered {where}.") { Display = c.Ui.Dim($"remembered {where}") });
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
            var data = SessionStore.Load(file.FullName);
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
