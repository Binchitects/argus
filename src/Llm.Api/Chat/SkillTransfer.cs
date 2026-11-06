using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace Llm.Api.Chat;

/// <summary>A skill as a folder, as Claude Code keeps one: its folder's name, and its files by their paths in it (SKILL.md among them).</summary>
public sealed record SkillFolder(string Folder, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// A skill read from an imported folder: its name, title, description, instructions and parameters from its SKILL.md, and
/// the files beside it; or why it cannot be imported as it is.
/// </summary>
public sealed record SkillImport(string Folder, string Name, string Title, string Description, string Instructions, IReadOnlyList<SkillParameter> Parameters,
    IReadOnlyDictionary<string, string> Files, long Bytes)
{
    /// <summary>Why it cannot be imported at all (no instructions, too large, a safeguard); null: it can.</summary>
    public string? Problem { get; init; }
    /// <summary>Its files left out (not text, or too large), each with why.</summary>
    public IReadOnlyList<string> LeftOut { get; init; } = [];
}

/// <summary>
/// Skills as files, as Claude Code's are: each a folder with a SKILL.md (front matter: name, description, and Arena's title and
/// parameters) and the files beside it. Exported one or many as a .zip of their folders, or one alone as its SKILL.md;
/// imported from a .zip, a SKILL.md, or a folder's files, each folder holding a SKILL.md being a skill. Only text files are
/// kept, within limits, every path inside its folder: what an archive says otherwise is left out.
/// </summary>
public static class SkillTransfer
{
    public const string SkillMd = "SKILL.md";
    /// <summary>The most one upload is (the .zip, or a folder's files together).</summary>
    public const long MaxUpload = 5 * 1024 * 1024;
    /// <summary>The most an archive unpacks to: a small archive of huge files is refused, not unpacked.</summary>
    public const long MaxUnpacked = 20 * 1024 * 1024;
    /// <summary>The most skills one import brings.</summary>
    public const int MaxSkills = 50;
    /// <summary>The most files beside one skill's SKILL.md.</summary>
    public const int MaxFiles = 50;
    /// <summary>The most one file is.</summary>
    public const int MaxFileBytes = 200_000;
    /// <summary>The most one skill's files are together, its SKILL.md with them.</summary>
    public const int MaxSkillBytes = 1_000_000;
    /// <summary>The most entries an archive may have.</summary>
    public const int MaxEntries = 2_000;

    /// <summary>Why an upload cannot be read at all.</summary>
    public sealed class TransferException(string message) : Exception(message);

    /// <summary>A file uploaded: its path (a folder's files keep theirs, as the browser gives them) and its bytes.</summary>
    public sealed record Upload(string Path, byte[] Bytes);

    /// <summary>
    /// The skill folders in what was uploaded: a .zip's, a lone SKILL.md (or NAME.md) as one skill, or a folder's files; and
    /// what was left out of them. The folders are found by their SKILL.md: every file under one (and not under another
    /// inside it) is its own.
    /// </summary>
    public static (List<SkillFolder> Folders, List<string> LeftOut) Read(IReadOnlyList<Upload> uploads)
    {
        if (uploads.Count == 0)
        {
            throw new TransferException("Choose a .zip of skills, a SKILL.md, or a skill's folder.");
        }
        if (uploads.Sum(u => (long)u.Bytes.Length) > MaxUpload)
        {
            throw new TransferException($"An import is at most {MaxUpload / 1024 / 1024} MB.");
        }
        var leftOut = new List<string>();
        var files = new List<(string Path, byte[] Bytes)>();
        foreach (var u in uploads)
        {
            if (u.Path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || u.Path.EndsWith(".skill", StringComparison.OrdinalIgnoreCase))
            {
                files.AddRange(Unzip(u.Bytes, leftOut));
            }
            else if (Clean(u.Path) is { } path)
            {
                files.Add((path, u.Bytes));
            }
            else
            {
                leftOut.Add($"{u.Path}: not a path inside the folder");
            }
        }
        // A lone Markdown file is a SKILL.md, whatever it is called: its front matter names it.
        if (files is [var lone] && !lone.Path.EndsWith("/" + SkillMd, StringComparison.OrdinalIgnoreCase) && lone.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(lone.Path);
            files = [((string.Equals(name, "SKILL", StringComparison.OrdinalIgnoreCase) ? "skill" : name) + "/" + SkillMd, lone.Bytes)];
        }
        else if (files is [var top] && string.Equals(top.Path, SkillMd, StringComparison.OrdinalIgnoreCase))
        {
            files = [("skill/" + SkillMd, top.Bytes)];
        }
        // The folders: those with a SKILL.md, the deepest first claims its files.
        var roots = files.Where(f => string.Equals(Path.GetFileName(f.Path), SkillMd, StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path.Contains('/', StringComparison.Ordinal) ? f.Path[..f.Path.LastIndexOf('/')] : "")
            .Distinct(StringComparer.Ordinal).OrderByDescending(r => r.Length).ToList();
        if (roots.Count == 0)
        {
            throw new TransferException("No skill is in it: each skill is a folder with a SKILL.md.");
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
            if (Text(bytes) is not { } text)
            {
                leftOut.Add($"{path}: not a text file");
                continue;
            }
            if (bytes.Length > MaxFileBytes)
            {
                leftOut.Add($"{path}: over {MaxFileBytes / 1000} KB");
                continue;
            }
            owned[root][inside] = text;
        }
        var folders = roots.OrderBy(r => r, StringComparer.Ordinal)
            .Select(r => new SkillFolder(r.Length == 0 ? "skill" : r[(r.LastIndexOf('/') + 1)..], owned[r]))
            .Where(f => f.Files.ContainsKey(SkillMd)).ToList();
        if (folders.Count > MaxSkills)
        {
            throw new TransferException($"An import brings at most {MaxSkills} skills; this has {folders.Count}.");
        }
        return (folders, leftOut);
    }

    /// <summary>A .zip's files, each path checked (inside the archive, no link out); refused when it unpacks to too much.</summary>
    private static List<(string Path, byte[] Bytes)> Unzip(byte[] bytes, List<string> leftOut)
    {
        var files = new List<(string, byte[])>();
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxEntries)
            {
                throw new TransferException($"The archive has {zip.Entries.Count:N0} entries: at most {MaxEntries:N0}.");
            }
            long unpacked = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue;
                }
                if (Clean(entry.FullName) is not { } path || path.StartsWith("__MACOSX/", StringComparison.Ordinal))
                {
                    if (!entry.FullName.StartsWith("__MACOSX/", StringComparison.Ordinal))
                    {
                        leftOut.Add($"{entry.FullName}: not a path inside the archive");
                    }
                    continue;
                }
                // A symbolic link (Unix mode 0120000) is never followed: it is left out.
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
                    throw new TransferException($"The archive unpacks to more than {MaxUnpacked / 1024 / 1024} MB.");
                }
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                // Read no more than it says it is: an entry that lies about its size is cut there and refused as too large.
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > MaxFileBytes)
                    {
                        break;
                    }
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
            throw new TransferException($"The archive does not read as a .zip: {e.Message}");
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

    /// <summary>The bytes as UTF-8 text, or null when they are not text (not UTF-8, or a NUL in them).</summary>
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

    /// <summary>
    /// A skill from its folder: the SKILL.md's front matter (name, title, description, parameters; Claude Code's other fields
    /// are left), its instructions, and the files beside it within the limits. Its name is the front matter's, else the folder's.
    /// </summary>
    public static SkillImport Parse(SkillFolder folder)
    {
        var text = folder.Files[SkillMd].Replace("\r\n", "\n", StringComparison.Ordinal);
        var front = new JsonObject();
        string? problem = null;
        if (text.StartsWith("---\n", StringComparison.Ordinal) && text.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and > 0)
        {
            try
            {
                front = Tools.OpenApi.Parse(text[4..end]);
            }
            catch (Tools.OpenApi.SpecException)
            {
                // Not YAML ("description: Use it: when..." unquoted): read line by line, as Claude Code reads its own.
                front = Lines(text[4..end]);
            }
            text = text[(end + 4)..];
            text = text.StartsWith('-') ? text.TrimStart('-') : text;
        }
        string Field(string key) => (front[key] as JsonValue)?.ToString().Trim().ReplaceLineEndings(" ") ?? "";
        var name = (Field("name") is { Length: > 0 } named ? named : folder.Folder).Trim().TrimStart('/').ToLowerInvariant();
        var title = Field("title") is { Length: > 0 } t ? t : Humanize(name);
        var description = Field("description");
        var instructions = text.Trim();
        List<SkillParameter> parameters = [];
        if (front["parameters"] is JsonArray list)
        {
            try
            {
                parameters = JsonSerializer.Deserialize<List<SkillParameter>>(list.ToJsonString(), JsonSerializerOptions.Web) ?? [];
            }
            catch (JsonException)
            {
                problem ??= "Its parameters are not written as a list of { name, description, required, default }.";
            }
        }
        var leftOut = new List<string>();
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        long bytes = Encoding.UTF8.GetByteCount(folder.Files[SkillMd]);
        foreach (var (path, content) in folder.Files.Where(f => f.Key != SkillMd).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var size = Encoding.UTF8.GetByteCount(content);
            if (files.Count >= MaxFiles)
            {
                leftOut.Add($"{path}: past {MaxFiles} files");
            }
            else if (bytes + size > MaxSkillBytes)
            {
                leftOut.Add($"{path}: past {MaxSkillBytes / 1000:N0} KB for the skill");
            }
            else
            {
                files[path] = content;
                bytes += size;
            }
        }
        problem ??= instructions.Length == 0 ? "Its SKILL.md has no instructions under its front matter."
            : instructions.Length > SkillLibrary.MaxText ? $"Its instructions are over {SkillLibrary.MaxText:N0} characters."
            : title.Length > SkillLibrary.MaxTitle ? $"Its title is over {SkillLibrary.MaxTitle} characters."
            : description.Length > SkillLibrary.MaxDescription ? $"Its description is over {SkillLibrary.MaxDescription:N0} characters."
            : SkillLibrary.ParametersProblem(parameters);
        return new SkillImport(folder.Folder, name, title, description, instructions, parameters, files, bytes) { Problem = problem, LeftOut = leftOut };
    }

    /// <summary>A front matter's top-level "key: value" lines, each value as written (its quotes taken off).</summary>
    private static JsonObject Lines(string front)
    {
        var fields = new JsonObject();
        foreach (var line in front.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0]) || fields.ContainsKey(line[..colon].Trim()))
            {
                continue;
            }
            var value = line[(colon + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                value = value[1..^1];
            }
            fields[line[..colon].Trim()] = value;
        }
        return fields;
    }

    /// <summary>"review-mr" as a title: "Review mr".</summary>
    public static string Humanize(string name)
    {
        var words = name.Replace('-', ' ').Replace('_', ' ').Trim();
        var title = words.Length == 0 ? "Skill" : char.ToUpperInvariant(words[0]) + words[1..];
        return title.Length > SkillLibrary.MaxTitle ? title[..SkillLibrary.MaxTitle] : title;
    }

    /// <summary>A skill's files beside its SKILL.md, read from what is kept (none when it does not read).</summary>
    public static Dictionary<string, string> FilesOf(Skill skill)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(skill.Files) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string WriteFiles(IReadOnlyDictionary<string, string> files) => JsonSerializer.Serialize(files.OrderBy(f => f.Key, StringComparer.Ordinal).ToDictionary());

    /// <summary>Why files will not do beside a SKILL.md (a path out of its folder, too many, too large), or null.</summary>
    public static string? FilesProblem(IReadOnlyDictionary<string, string> files)
    {
        if (files.Count > MaxFiles)
        {
            return $"A skill has at most {MaxFiles} files beside its SKILL.md.";
        }
        if (files.Keys.FirstOrDefault(p => Clean(p) != p || string.Equals(p, SkillMd, StringComparison.OrdinalIgnoreCase)) is { } bad)
        {
            return $"{bad} is not a path inside the skill's folder (beside its SKILL.md).";
        }
        return files.Sum(f => (long)Encoding.UTF8.GetByteCount(f.Value)) > MaxSkillBytes ? $"A skill's files are at most {MaxSkillBytes / 1000:N0} KB together." : null;
    }

    /// <summary>A skill's folder: its SKILL.md as Arena writes one, and its files.</summary>
    public static Dictionary<string, string> FolderOf(Skill skill)
    {
        var files = FilesOf(skill);
        files[SkillMd] = SkillLibrary.ToSkillMd(skill);
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
                    var entry = zip.CreateEntry($"{skill.Name}/{path}", CompressionLevel.Optimal);
                    entry.LastWriteTime = skill.UpdatedAt;
                    using var w = entry.Open();
                    w.Write(Encoding.UTF8.GetBytes(text));
                }
            }
        }
        return m.ToArray();
    }
}
