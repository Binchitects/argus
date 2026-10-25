using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The project's long memory, kept in the repository: .remember/MEMORY.md (what the project is, how it builds, tests and
/// runs, its conventions, the decisions and why, where the work stands, the lessons), with topic files beside it for the
/// details. Read at each session's start (MEMORY.md whole, up to a size; the topic files listed), kept current by the agent
/// with remember_project as it works, and shared with the team when the folder is committed: what one session learned,
/// the next starts from. Beside Arena's memories (short facts, found by relevance), it is the project's own notebook.
/// </summary>
internal static class ProjectMemory
{
    public const string FolderName = ".remember", MainName = "MEMORY.md";

    /// <summary>What of MEMORY.md the prompt takes, and how many topic files it lists.</summary>
    public const int PromptChars = 24_000, MostListed = 40;

    /// <summary>The most one section of a memory file may hold, and a whole file.</summary>
    public const int SectionChars = 20_000, FileChars = 200_000;

    public static string Folder(string root) => Path.Combine(root, FolderName);

    /// <summary>Where a folder's project memory is: at its repository's root (a session in a subfolder shares it), else the folder.</summary>
    public static string Root(string folder) => SystemPrompt.GitRoot(folder) ?? folder;

    private const string Instructions =
        "This project keeps its long memory in .remember/, in the repository: what a session learns, the next one starts from. MEMORY.md is " +
        "read at every session's start. Keep it current with remember_project as you work, not at the end: how to build, test and run it, " +
        "conventions, decisions and why, where the work stands and what is next, lessons (what went wrong, what the person corrected). Keep " +
        "MEMORY.md short, an index of a few hundred lines: put details in topic files (.remember/<topic>.md, e.g. architecture.md) and name " +
        "them there. Write facts, not a diary; replace what is out of date (replace: true). Never secrets, keys or passwords.";

    private const string NoneYet =
        "This project has no long memory yet. When you learn what a later session should know (how it builds and tests, a decision and " +
        "why, where the work stands), keep it with remember_project: it starts .remember/MEMORY.md, in the repository.";

    /// <summary>The prompt's part: MEMORY.md as it is now (cut past a size) and the topic files, or how to start it.</summary>
    public static string ForPrompt(string root)
    {
        var folder = Folder(root);
        var main = Path.Combine(folder, MainName);
        string text;
        try
        {
            if (!File.Exists(main))
            {
                return $"\n# Project memory ({FolderName}/)\n\n{NoneYet}\n";
            }
            text = File.ReadAllText(main).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
        if (text.Length > PromptChars)
        {
            text = text[..PromptChars] + $"\n\n[… MEMORY.md goes on ({text.Length:N0} characters): read_file {FolderName}/{MainName} for the rest, and keep it shorter: move details into topic files.]";
        }
        var md = new StringBuilder($"\n# Project memory ({FolderName}/)\n\n{Instructions}\n\n## {FolderName}/{MainName}, as this session started\n\n{text}\n");
        var topics = Topics(folder, main).Take(MostListed + 1).ToList();
        if (topics.Count > 0)
        {
            md.Append("\n## Its topic files (read_file one when it matters)\n");
            foreach (var t in topics.Take(MostListed))
            {
                md.Append("- ").Append(t).Append('\n');
            }
            if (topics.Count > MostListed)
            {
                md.Append("- … and more (list_dir .remember).\n");
            }
        }
        return md.ToString();
    }

    /// <summary>The topic files: each with its size and its first line.</summary>
    private static IEnumerable<string> Topics(string folder, string main)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories).Where(f => f != main).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var f in files)
        {
            var first = File.ReadLines(f).Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            yield return $"{FolderName}/{Path.GetRelativePath(folder, f).Replace('\\', '/')} ({Files.Size(new FileInfo(f).Length)}): {Fmt.OneLine(first, 100)}";
        }
    }

    /// <summary>A memory file's path inside .remember (MEMORY.md, or a topic file), or why it is not one.</summary>
    internal static (string? Path, string? Problem) FileOf(string root, string? name)
    {
        var file = string.IsNullOrWhiteSpace(name) ? MainName : name.Trim().Replace('\\', '/');
        if (file.StartsWith(FolderName + "/", StringComparison.Ordinal))
        {
            file = file[(FolderName.Length + 1)..];
        }
        if (!file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || file.Split('/').Any(p => p is "" or "." or ".."))
        {
            return (null, $"A memory file is a Markdown file inside {FolderName}/ (e.g. MEMORY.md, architecture.md).");
        }
        var folder = Path.GetFullPath(Folder(root));
        var path = Path.GetFullPath(Path.Combine(folder, file));
        return path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? (path, null) : (null, $"Only files inside {FolderName}/.");
    }

    /// <summary>
    /// A section of a memory file, added to (or replaced): the file and the section made when they are not there yet. The
    /// file's text after.
    /// </summary>
    internal static string Keep(string existing, string fileName, string section, string text, bool replace)
    {
        var heading = "## " + section.Trim().TrimStart('#').Trim();
        var lines = existing.Length == 0
            ? fileName == MainName
                ? ["# Project memory", "", "What sessions of Code Arena learned about this project, for the next ones (kept with remember_project; edit it freely)."]
                : new List<string> { "# " + Path.GetFileNameWithoutExtension(fileName).Replace('-', ' ') }
            : existing.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').ToList();
        var at = lines.FindIndex(l => l.Trim().Equals(heading, StringComparison.OrdinalIgnoreCase));
        var body = text.ReplaceLineEndings("\n").Trim('\n').Split('\n');
        if (at < 0)
        {
            lines.AddRange(["", heading, "", .. body]);
            return string.Join('\n', lines) + "\n";
        }
        // The section runs to the next heading of its level or above.
        var end = lines.FindIndex(at + 1, l => l.StartsWith("# ", StringComparison.Ordinal) || l.StartsWith("## ", StringComparison.Ordinal));
        end = end < 0 ? lines.Count : end;
        if (replace)
        {
            lines.RemoveRange(at + 1, end - at - 1);
            lines.InsertRange(at + 1, ["", .. body, ""]);
        }
        else
        {
            // After the section's last line with words, so blank lines stay where they were.
            var last = end - 1;
            while (last > at && lines[last].Trim().Length == 0)
            {
                last--;
            }
            lines.InsertRange(last + 1, last == at ? ["", .. body] : body);
        }
        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }

    public static ToolDef Tool() => new()
    {
        Name = "remember_project",
        Kind = ToolKind.Read,
        Description = "Keep something in the project's long memory (.remember/, in the repository), which every later session reads at its start: " +
            "add to a section of MEMORY.md (or of a topic file), or replace the section when what it says is out of date. For how to build, test and " +
            "run; conventions; decisions and why; where the work stands and what is next; lessons. Short facts about the person go to remember instead.",
        Parameters = (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{
              "section":{"type":"string","description":"The section's heading, e.g. \"How to build, test and run\", \"Decisions\", \"State and next steps\", \"Lessons\"."},
              "text":{"type":"string","description":"Markdown to add (or the section's whole new text, with replace)."},
              "replace":{"type":"boolean","description":"Replace what the section says (it was out of date). Default: add to it."},
              "file":{"type":"string","description":"A topic file inside .remember/ (e.g. architecture.md) instead of MEMORY.md."}},
             "required":["section","text"]}
            """)!,
        Summary = a => $"{a.Str("file") ?? MainName} § {a.Str("section")}",
        Run = (a, c, ct) =>
        {
            var section = a.Str("section")?.Trim() ?? "";
            var text = a.Str("text")?.Trim() ?? "";
            if (section.Length == 0 || text.Length == 0)
            {
                throw new ToolError("Give the section's heading and the text to keep.");
            }
            if (text.Length > SectionChars)
            {
                throw new ToolError($"That is {text.Length:N0} characters: keep a section under {SectionChars:N0}, and the details in a topic file.");
            }
            var root = Root(c.Workspace.Root);
            var (path, problem) = FileOf(root, a.Str("file"));
            if (path is null)
            {
                throw new ToolError(problem!);
            }
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var after = Keep(existing, Path.GetFileName(path), section, text, a.Bool("replace") == true);
            if (after.Length > FileChars)
            {
                throw new ToolError($"{Path.GetFileName(path)} would be {after.Length:N0} characters: tighten it (replace what is out of date) or start a topic file.");
            }
            c.BeforeWrite?.Invoke(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, after);
            var shown = $"{FolderName}/{Path.GetRelativePath(Folder(root), path).Replace('\\', '/')}";
            return Task.FromResult(new ToolResult($"Kept in {shown}, under \"## {section.TrimStart('#').Trim()}\" ({(a.Bool("replace") == true ? "replaced" : "added")}). Later sessions read it at their start.")
            {
                Display = c.Ui.Dim($"{shown} § {section}"),
            });
        },
    };
}
