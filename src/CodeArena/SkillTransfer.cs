using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A skill as a folder: its folder's name, and its files by their paths in it (SKILL.md among them).</summary>
internal sealed record SkillFolder(string Folder, Dictionary<string, string> Files);

/// <summary>
/// Skills as files, as Claude Code keeps them (a folder with a SKILL.md and the files beside it), in the IDE's Skills view
/// and /skills export, /skills import in the terminal. Any skill Code Arena reads goes out as a .zip of folders (or one
/// alone as its SKILL.md); one comes in from a .zip, a SKILL.md, or a folder (one skill's, or a skills folder such as
/// .claude/skills), for this project (.arena/skills) or the person (theirs in Arena when signed in to it, as the Skills page
/// imports them, else the config folder's). A preview shows each one and what is in its way; a name taken is replaced,
/// renamed or skipped as chosen. Every file is looked at for secrets here, and by Arena's safeguards when signed in to it.
/// The limits are Arena's: 5 MB, 50 skills, 50 files of 200 KB beside each SKILL.md, 1 MB a skill, text only.
/// </summary>
internal static partial class SkillTransfer
{
    public const string SkillMd = "SKILL.md";
    public const long MaxUpload = 5 * 1024 * 1024;
    public const long MaxUnpacked = 20 * 1024 * 1024;
    public const int MaxSkills = 50;
    public const int MaxFiles = 50;
    public const int MaxFileBytes = 200_000;
    public const int MaxSkillBytes = 1_000_000;
    public const int MaxEntries = 2_000;

    /// <summary>A skill's name here, as /name is typed and its folder is called.</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex Name();

    /// <summary>
    /// The skill folders of what was chosen: files by their paths (a .zip's unpacked, a lone Markdown file a SKILL.md), each
    /// folder with a SKILL.md a skill and every file under it (not under another inside it) its own; what was left out, and why.
    /// </summary>
    public static (List<SkillFolder> Folders, List<string> LeftOut) Read(IReadOnlyList<(string Path, byte[] Bytes)> uploads)
    {
        if (uploads.Count == 0)
        {
            throw new IdeError(400, "invalid", "Choose a .zip of skills, a SKILL.md, or a skill's folder.");
        }
        if (uploads.Sum(u => (long)u.Bytes.Length) > MaxUpload)
        {
            throw new IdeError(413, "too_large", $"An import is at most {MaxUpload / 1024 / 1024} MB.");
        }
        var leftOut = new List<string>();
        var files = new List<(string Path, byte[] Bytes)>();
        foreach (var (path, bytes) in uploads)
        {
            if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".skill", StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(Unzip(bytes, leftOut));
            }
            else if (Clean(path) is { } clean)
            {
                files.Add((clean, bytes));
            }
            else
            {
                leftOut.Add($"{path}: not a path inside the folder");
            }
        }
        if (files is [var lone] && !lone.Path.EndsWith("/" + SkillMd, StringComparison.OrdinalIgnoreCase) && lone.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var stem = Path.GetFileNameWithoutExtension(lone.Path);
            files = [((string.Equals(stem, "SKILL", StringComparison.OrdinalIgnoreCase) ? "skill" : stem) + "/" + SkillMd, lone.Bytes)];
        }
        var roots = files.Where(f => string.Equals(Path.GetFileName(f.Path), SkillMd, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path.Contains('/', StringComparison.Ordinal) ? f.Path[..f.Path.LastIndexOf('/')] : "")
            .Distinct(StringComparer.Ordinal).OrderByDescending(r => r.Length).ToList();
        if (roots.Count == 0)
        {
            throw new IdeError(400, "invalid", "No skill is in it: each skill is a folder with a SKILL.md.");
        }
        var owned = roots.ToDictionary(r => r, _ => new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
        {
            var root = roots.FirstOrDefault(r => r.Length == 0 || path.StartsWith(r + "/", StringComparison.Ordinal));
            if (root is null)
            {
                leftOut.Add($"{path}: in no skill's folder");
                continue;
            }
            var inside = root.Length == 0 ? path : path[(root.Length + 1)..];
            if (string.Equals(inside, SkillMd, StringComparison.OrdinalIgnoreCase))
            {
                inside = SkillMd;
            }
            if (bytes.Length > MaxFileBytes)
            {
                leftOut.Add($"{path}: over {MaxFileBytes / 1000} KB");
            }
            else if (Text(bytes) is not { } text)
            {
                leftOut.Add($"{path}: not a text file");
            }
            else
            {
                owned[root][inside] = text;
            }
        }
        var folders = roots.Order(StringComparer.Ordinal)
            .Select(r => new SkillFolder(r.Length == 0 ? "skill" : r[(r.LastIndexOf('/') + 1)..], owned[r]))
            .Where(f => f.Files.ContainsKey(SkillMd)).ToList();
        if (folders.Count > MaxSkills)
        {
            throw new IdeError(400, "invalid", $"An import brings at most {MaxSkills} skills; this has {folders.Count}.");
        }
        return (folders, leftOut);
    }

    /// <summary>What a path on this computer holds, as an upload: a .zip or a Markdown file as it is, a folder's files by their paths in it.</summary>
    public static List<(string Path, byte[] Bytes)> FromDisk(string path)
    {
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > MaxUpload)
            {
                throw new IdeError(413, "too_large", $"An import is at most {MaxUpload / 1024 / 1024} MB.");
            }
            return [(Path.GetFileName(path), File.ReadAllBytes(path))];
        }
        if (!Directory.Exists(path))
        {
            throw new IdeError(404, "not_found", $"There is no {path}.");
        }
        var root = Path.GetFullPath(path);
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var list = new List<(string, byte[])>();
        long total = 0;
        // Links are not followed: a folder that leads elsewhere is not read.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Split('/').Any(p => p.StartsWith('.') && p != ".") || list.Count >= MaxEntries)
            {
                continue;
            }
            total += new FileInfo(file).Length;
            if (total > MaxUpload)
            {
                throw new IdeError(413, "too_large", $"An import is at most {MaxUpload / 1024 / 1024} MB.");
            }
            list.Add((name + "/" + relative, File.ReadAllBytes(file)));
        }
        return list;
    }

    private static List<(string Path, byte[] Bytes)> Unzip(byte[] bytes, List<string> leftOut)
    {
        var files = new List<(string, byte[])>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries)
            {
                throw new IdeError(400, "invalid", $"The archive has {zip.Entries.Count:N0} entries: at most {MaxEntries:N0}.");
            }
            long unpacked = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal))
                {
                    continue;
                }
                if (Clean(entry.FullName) is not { } path)
                {
                    leftOut.Add($"{entry.FullName}: not a path inside the archive");
                    continue;
                }
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    leftOut.Add($"{path}: a link");
                    continue;
                }
                if (entry.Length > MaxFileBytes)
                {
                    leftOut.Add($"{path}: over {MaxFileBytes / 1000} KB");
                    continue;
                }
                unpacked += entry.Length;
                if (unpacked > MaxUnpacked)
                {
                    throw new IdeError(400, "invalid", $"The archive unpacks to more than {MaxUnpacked / 1024 / 1024} MB.");
                }
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0 && copy.Length <= MaxFileBytes)
                {
                    copy.Write(buffer, 0, read);
                }
                if (copy.Length > MaxFileBytes)
                {
                    leftOut.Add($"{path}: over {MaxFileBytes / 1000} KB");
                    continue;
                }
                files.Add((path, copy.ToArray()));
            }
        }
        catch (InvalidDataException e)
        {
            throw new IdeError(400, "invalid", $"The archive does not read as a .zip: {e.Message}");
        }
        return files;
    }

    /// <summary>A path inside a folder, with / between its parts; null for one that leads out (.., absolute, a drive) or is empty.</summary>
    public static string? Clean(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || path.StartsWith('/') || path.StartsWith('\\') || parts[0].Contains(':', StringComparison.Ordinal)
            || parts.Any(p => p is "." or ".." || p.Any(char.IsControl)))
        {
            return null;
        }
        return string.Join('/', parts);
    }

    private static string? Text(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return null;
        }
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>A skill's folder as it is on disk: its SKILL.md and the text files beside it, within the limits (links not followed).</summary>
    public static Dictionary<string, string> FolderOf(Skill skill)
    {
        var root = Path.GetFullPath(skill.Folder);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes || total + info.Length > MaxSkillBytes || files.Count > MaxFiles || Text(File.ReadAllBytes(file)) is not { } text)
            {
                continue;
            }
            files[string.Equals(relative, SkillMd, StringComparison.OrdinalIgnoreCase) ? SkillMd : relative] = text;
            total += info.Length;
        }
        return files;
    }

    /// <summary>Skills as a .zip: each its folder (by its name) with its SKILL.md and its files, as Claude Code's skills folder has them.</summary>
    public static byte[] Zip(IEnumerable<Skill> skills)
    {
        using var m = new MemoryStream();
        using (var zip = new ZipArchive(m, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var skill in skills)
            {
                foreach (var (path, text) in FolderOf(skill).OrderBy(f => f.Key == SkillMd ? "" : f.Key, StringComparer.Ordinal))
                {
                    using var w = zip.CreateEntry($"{skill.Name}/{path}", CompressionLevel.Optimal).Open();
                    w.Write(Encoding.UTF8.GetBytes(text));
                }
            }
        }
        return m.ToArray();
    }

    /// <summary>The skills of these names, as the agent reads them now; an unknown name is refused, with those there are.</summary>
    public static async Task<List<Skill>> NamedAsync(Runtime rt, IEnumerable<string> names, CancellationToken ct)
    {
        var x = await rt.ReloadExtensionsAsync(false, ct);
        var wanted = names.Select(n => n.Trim().TrimStart('/')).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
        {
            throw new IdeError(400, "invalid", "Say which skills to export.");
        }
        var found = new List<Skill>();
        foreach (var name in wanted)
        {
            found.Add(x.Skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new IdeError(404, "not_found", $"There is no skill /{name}: /skills lists them."));
        }
        return found;
    }

    /// <summary>Where a skill imported for this scope goes: the project's .arena/skills, or the config folder's (the person's, not signed in to Arena).</summary>
    private static string RootFor(Runtime rt, string scope) => scope switch
    {
        "project" when rt.ProjectRoot.Length > 0 => Path.Combine(rt.ProjectRoot, ".arena", "skills"),
        "project" => throw new IdeError(400, "invalid", "There is no project folder here to import skills into."),
        "person" => Path.Combine(rt.Env.Paths.ConfigDir, "skills"),
        _ => throw new IdeError(400, "invalid", "Skills are imported for the project (project) or for you, in every project (person)."),
    };

    /// <summary>Whether this scope's skills are kept in Arena: the person's, signed in to it.</summary>
    private static bool InArena(Runtime rt, string scope) => scope == "person" && rt.MineInArena;

    /// <summary>One skill as the preview shows it here (the same fields as Arena's preview).</summary>
    private sealed record Looked(SkillFolder Folder, string Name, string Title, string Description, Dictionary<string, string> Files, long Bytes, List<string> LeftOut,
        string? Problem, string? NameProblem, Skill? Clash, string? Shadows, string Suggested);

    private static List<Looked> Look(Runtime rt, IReadOnlyList<SkillFolder> folders, string scope, IReadOnlyDictionary<string, string> refused)
    {
        var skills = rt.ReloadExtensions().Skills;
        var root = RootFor(rt, scope);
        var looked = new List<Looked>();
        foreach (var folder in folders)
        {
            var fm = FrontMatter.Parse(folder.Files[SkillMd]);
            var name = (fm.Field("name") ?? folder.Folder).Trim().TrimStart('/').ToLowerInvariant();
            var leftOut = new List<string>();
            var files = new Dictionary<string, string>(StringComparer.Ordinal) { [SkillMd] = folder.Files[SkillMd] };
            long bytes = Encoding.UTF8.GetByteCount(folder.Files[SkillMd]);
            foreach (var (path, text) in folder.Files.Where(f => f.Key != SkillMd).OrderBy(f => f.Key, StringComparer.Ordinal))
            {
                var size = Encoding.UTF8.GetByteCount(text);
                if (files.Count > MaxFiles)
                {
                    leftOut.Add($"{path}: past {MaxFiles} files");
                }
                else if (bytes + size > MaxSkillBytes)
                {
                    leftOut.Add($"{path}: past {MaxSkillBytes / 1000:N0} KB for the skill");
                }
                else
                {
                    files[path] = text;
                    bytes += size;
                }
            }
            // A secret is found here whatever Arena says: a skill is text for the model, never a place for a key.
            var secret = files.FirstOrDefault(f => CodeFiles.HoldsSecret(f.Value) || CodeFiles.Sensitive(f.Key)).Key;
            var problem = refused.GetValueOrDefault(folder.Folder)
                ?? (secret is not null ? $"{secret} holds a secret (a private key, a token, or a file that keeps them): a skill never brings one." : null)
                ?? (fm.Body.Length == 0 ? "Its SKILL.md has no instructions under its front matter." : null);
            var nameProblem = InArena(rt, scope) || Name().IsMatch(name) ? null : $"\"/{name}\" is not a skill's name: lowercase letters, digits, - and _, up to 64.";
            // In the way: a skill of its name of the same scope (the person's own in Arena, when theirs are there).
            var clash = nameProblem is null ? skills.FirstOrDefault(s => s.Name == name && IdeSkills.Whose(rt, s) == scope) : null;
            var shadows = skills.FirstOrDefault(s => s.Name == name && s != clash)?.Source;
            var stem = nameProblem is null ? name : Slug(name);
            var suggested = stem;
            for (var n = 2; skills.Any(s => s.Name == suggested) || Directory.Exists(Path.Combine(root, suggested)) || looked.Any(l => l.Suggested == suggested || l.Name == suggested); n++)
            {
                var tail = "-" + n;
                suggested = (stem.Length + tail.Length > 64 ? stem[..(64 - tail.Length)] : stem) + tail;
            }
            looked.Add(new Looked(folder, name, fm.Field("title") ?? name, fm.Field("description") ?? "", files, bytes, leftOut, problem, nameProblem, clash, shadows, suggested));
        }
        return looked;
    }

    private static string Slug(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' ? ch : '-')]).Trim('-', '_');
        slug = slug.Length > 64 ? slug[..64].TrimEnd('-', '_') : slug;
        return slug.Length == 0 ? "skill" : slug;
    }

    private static JsonArray Sent(IReadOnlyList<SkillFolder> folders) =>
        [.. folders.Select(f => (JsonNode)new JsonObject { ["folder"] = f.Folder, ["files"] = new JsonObject([.. f.Files.Select(x => KeyValuePair.Create(x.Key, (JsonNode?)x.Value))]) })];

    /// <summary>
    /// Arena's safeguards' word on each folder, when signed in to it: those it refused, and why (its preview, which keeps
    /// nothing). Not signed in, or Arena not answering: none, and the secrets are looked for here only.
    /// </summary>
    private static async Task<(Dictionary<string, string> Refused, string? Note)> SafeguardsAsync(Runtime rt, IReadOnlyList<SkillFolder> folders, CancellationToken ct)
    {
        if (!rt.MineInArena)
        {
            return ([], "Not signed in to Arena: only looked for secrets here, not through Arena's safeguards.");
        }
        try
        {
            var answer = await IdeSkills.ArenaAsync(rt, HttpMethod.Post, "/api/code-arena/skills/import", new JsonObject { ["skills"] = Sent(folders), ["preview"] = true }, ct, 60);
            return ((answer?["skills"] as JsonArray ?? []).OfType<JsonObject>().Where(s => s.Str("problem")?.StartsWith("The safeguards", StringComparison.Ordinal) == true)
                .ToDictionary(s => s.Str("folder") ?? "", s => s.Str("problem")!, StringComparer.Ordinal), null);
        }
        catch (IdeError e) when (e.Status is 403)
        {
            throw new IdeError(403, "feature", e.Message);
        }
        catch (IdeError e)
        {
            return ([], $"Arena's safeguards could not look at it ({e.Message}): only looked for secrets here.");
        }
    }

    /// <summary>
    /// What an import would do, for the preview: for the person's skills in Arena, Arena's own preview (its checks, its
    /// names); else each skill as it would go into the project's or the config folder, Arena's safeguards asked first.
    /// </summary>
    public static async Task<JsonObject> PreviewAsync(Runtime rt, IReadOnlyList<SkillFolder> folders, IReadOnlyList<string> leftOut, string scope, CancellationToken ct)
    {
        if (InArena(rt, scope))
        {
            var answer = await IdeSkills.ArenaAsync(rt, HttpMethod.Post, "/api/code-arena/skills/import", new JsonObject { ["skills"] = Sent(folders), ["preview"] = true }, ct, 60)
                ?? new JsonObject();
            answer["leftOut"] = new JsonArray([.. leftOut.Select(l => (JsonNode)l)]);
            answer["inArena"] = true;
            return answer;
        }
        var (refused, note) = await SafeguardsAsync(rt, folders, ct);
        return new JsonObject
        {
            ["skills"] = new JsonArray([.. Look(rt, folders, scope, refused).Select(l => (JsonNode)new JsonObject
            {
                ["folder"] = l.Folder.Folder, ["name"] = l.Name, ["title"] = l.Title, ["description"] = l.Description,
                ["files"] = new JsonArray([.. l.Files.Where(f => f.Key != SkillMd).Select(f => (JsonNode)new JsonObject { ["path"] = f.Key, ["bytes"] = Encoding.UTF8.GetByteCount(f.Value) })]),
                ["bytes"] = l.Bytes, ["leftOut"] = new JsonArray([.. l.LeftOut.Select(x => (JsonNode)x)]), ["problem"] = l.Problem, ["nameProblem"] = l.NameProblem,
                ["clash"] = l.Clash is { } c ? new JsonObject { ["title"] = c.Name, ["origin"] = c.Origin } : null, ["shadows"] = l.Shadows, ["suggested"] = l.Suggested,
                ["masked"] = false,
            })]),
            ["leftOut"] = new JsonArray([.. leftOut.Select(l => (JsonNode)l)]),
            ["note"] = note,
            ["inArena"] = false,
            ["where"] = rt.Workspace.Show(RootFor(rt, scope)),
        };
    }

    /// <summary>
    /// The import as chosen (each folder: import, replace, rename with name, or skip; one in the way and not chosen for is
    /// left): the person's in Arena through Arena; else written as folders of the project's or the config folder's skills.
    /// </summary>
    public static async Task<JsonObject> ImportAsync(Runtime rt, IReadOnlyList<SkillFolder> folders, string scope, JsonArray choices, CancellationToken ct)
    {
        if (InArena(rt, scope))
        {
            var answer = await IdeSkills.ArenaAsync(rt, HttpMethod.Post, "/api/code-arena/skills/import", new JsonObject { ["skills"] = Sent(folders), ["choices"] = choices.DeepClone() }, ct, 120)
                ?? new JsonObject();
            await rt.ReloadExtensionsAsync(true, ct);
            return answer;
        }
        var (refused, _) = await SafeguardsAsync(rt, folders, ct);
        var root = RootFor(rt, scope);
        var imported = new JsonArray();
        var skipped = new JsonArray();
        var made = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in Look(rt, folders, scope, refused))
        {
            var choice = choices.OfType<JsonObject>().FirstOrDefault(c => c.Str("folder") == l.Folder.Folder);
            var action = choice?.Str("action") ?? (l.Clash is null && l.NameProblem is null ? "import" : "skip");
            void Skip(string why) => skipped.Add(new JsonObject { ["folder"] = l.Folder.Folder, ["name"] = l.Name, ["reason"] = why });
            if (action == "skip")
            {
                Skip(l.Clash is not null ? $"There is a /{l.Name} already." : l.NameProblem ?? "Skipped.");
                continue;
            }
            if (l.Problem is { } problem)
            {
                Skip(problem);
                continue;
            }
            var name = action == "rename" ? choice?.Str("name")?.Trim().TrimStart('/').ToLowerInvariant() ?? l.Suggested : l.Name;
            if (!Name().IsMatch(name))
            {
                Skip($"\"/{name}\" is not a skill's name: lowercase letters, digits, - and _, up to 64.");
                continue;
            }
            if (action is not ("import" or "rename" or "replace"))
            {
                Skip($"\"{action}\" is not a choice: import, replace, rename or skip.");
                continue;
            }
            if (action == "replace" && l.Clash is null)
            {
                Skip($"There is no /{l.Name} to replace.");
                continue;
            }
            // Replaced in its own folder (wherever it was read from: .claude/skills too); a new one in the scope's.
            var folder = action == "replace" ? l.Clash!.Folder : Path.Combine(root, name);
            var taken = action == "import" && l.Clash is not null
                || action != "replace" && (Directory.Exists(folder) || rt.Extensions.Skills.Any(s => s.Name == name && IdeSkills.Whose(rt, s) == scope));
            if (taken || made.Contains(name))
            {
                Skip($"There is a /{name} already: replace it, import it under another name, or skip it.");
                continue;
            }
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
            foreach (var (path, text) in l.Files)
            {
                var file = Path.GetFullPath(Path.Combine(folder, path));
                if (!file.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllTextAsync(file, path == SkillMd && name != l.Name ? Renamed(text, name) : text, ct);
            }
            made.Add(name);
            imported.Add(new JsonObject { ["folder"] = l.Folder.Folder, ["name"] = name, ["replaced"] = action == "replace", ["path"] = rt.Workspace.Show(Path.Combine(folder, SkillMd)) });
        }
        rt.ReloadExtensions();
        return new JsonObject { ["imported"] = imported, ["skipped"] = skipped };
    }

    /// <summary>A SKILL.md under another name: its front matter's name changed (added when it had none).</summary>
    private static string Renamed(string text, string name)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n').ToList();
        var end = lines.Count < 2 || lines[0].Trim() != "---" ? -1 : lines.FindIndex(1, l => l.Trim() == "---");
        if (end < 0)
        {
            return $"---\nname: {name}\n---\n{text}";
        }
        var at = lines.FindIndex(1, end - 1, l => l.StartsWith("name:", StringComparison.Ordinal));
        if (at > 0)
        {
            lines[at] = $"name: {name}";
        }
        else
        {
            lines.Insert(1, $"name: {name}");
        }
        return string.Join('\n', lines);
    }
}
