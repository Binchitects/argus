using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>A GGUF file in the library.</summary>
/// <param name="Path">Relative to the library; for a split model, its first part.</param>
public sealed record LibraryFile(string Path, long Size, int Parts);

/// <summary>
/// A Hugging Face snapshot in the library, for vLLM: its folder (hf/ORG/REPO@REVISION), repository and revision, size,
/// whether it is whole (else why not), and the architectures its config.json names.
/// </summary>
public sealed record LibrarySnapshot(string Path, string Repo, string Revision, long Size, bool Complete, string? Why, IReadOnlyList<string> Architectures);

/// <summary>A file of the library and what it is.</summary>
public sealed record LibraryEntry(LibraryFile File, ModelProfile Profile);

/// <summary>
/// The GGUF files under the model library (LLAMACPP_LIBRARY_DIR), read-only.
/// Each file's header and tensor table say what it is (<see cref="ModelProfiler"/>),
/// so the Models page offers only what the engine can serve, with the settings
/// that kind of model has. A file is read once, and again when it changes.
/// </summary>
public sealed partial class ModelLibrary(IOptions<EngineOptions> options)
{
    private const int MaxDepth = 3;
    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, ModelProfile Profile)> _profiles = new(StringComparer.Ordinal);

    public IReadOnlyList<LibraryEntry> List()
    {
        var root = options.Value.LibraryDir;
        if (!Directory.Exists(root))
        {
            return [];
        }
        var entries = new List<LibraryEntry>();
        foreach (var path in Enumerate(root, 0).Order(StringComparer.Ordinal))
        {
            var split = SplitPart().Match(path);
            if (split.Success && split.Groups[1].Value != "00001")
            {
                continue;
            }
            var parts = split.Success
                ? [.. Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path).Replace("-00001-of-", "-*-of-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)]
                : new List<string> { path };
            var size = parts.Sum(f => new FileInfo(f).Length);
            var relative = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');
            var expected = split.Success ? int.Parse(split.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            var profile = parts.Count != expected
                ? ModelProfiler.Unreadable() with { Why = $"Only {parts.Count} of its {expected} parts are in the library: the download is incomplete." }
                : ProfileOf(path, parts, size);
            entries.Add(new LibraryEntry(new LibraryFile(relative, size, expected), Annotate(relative, profile)));
        }
        return entries;
    }

    /// <summary>A file of the library by its relative path, or null.</summary>
    public LibraryEntry? Find(string relative) => Contains(relative) ? List().FirstOrDefault(e => e.File.Path == relative) : null;

    /// <summary>A path of the library as the app sees it on disk.</summary>
    public string FullPath(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(options.Value.LibraryDir, relative));

    /// <summary>Whether a relative path names a whole Hugging Face snapshot folder inside the library (<see cref="Snapshot"/>).</summary>
    public bool IsSnapshot(string relative) => Snapshot(relative) is { Complete: true };

    /// <summary>
    /// The Hugging Face snapshots in the library, for vLLM: each folder hf/ORG/REPO@REVISION (ModelDownloads puts them there),
    /// with its size and whether it is whole.
    /// </summary>
    public IReadOnlyList<LibrarySnapshot> Snapshots()
    {
        var hf = System.IO.Path.Combine(options.Value.LibraryDir, "hf");
        if (!Directory.Exists(hf))
        {
            return [];
        }
        try
        {
            return [.. Directory.EnumerateDirectories(hf).SelectMany(Directory.EnumerateDirectories)
                .Select(d => Snapshot(System.IO.Path.GetRelativePath(options.Value.LibraryDir, d).Replace('\\', '/')))
                .OfType<LibrarySnapshot>().OrderBy(s => s.Path, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// A folder of the library as a snapshot: whole when it has its config.json, safetensors weights, every shard its index
    /// names, and nothing still downloading; null for a path outside the library or a folder that is no snapshot at all.
    /// </summary>
    public LibrarySnapshot? Snapshot(string relative)
    {
        var root = System.IO.Path.GetFullPath(options.Value.LibraryDir);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (relative.Length == 0 || !full.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal) || !Directory.Exists(full))
        {
            return null;
        }
        var name = System.IO.Path.GetFileName(full);
        var at = name.LastIndexOf('@');
        var org = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(full) ?? "");
        var (repo, revision) = at > 0 ? ($"{org}/{name[..at]}", name[(at + 1)..]) : ($"{org}/{name}", "");
        List<string> files;
        try
        {
            files = [.. Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
        if (files.Count == 0)
        {
            return null;
        }
        var size = files.Sum(f => new FileInfo(f).Length);
        string? why = null;
        var config = System.IO.Path.Combine(full, "config.json");
        var architectures = new List<string>();
        if (!File.Exists(config))
        {
            why = "It has no config.json: vLLM cannot load it.";
        }
        else
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
                if (doc.RootElement.TryGetProperty("architectures", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    architectures.AddRange(a.EnumerateArray().Select(x => x.GetString()).OfType<string>());
                }
            }
            catch (System.Text.Json.JsonException)
            {
                why = "Its config.json is not JSON.";
            }
        }
        if (why is null && files.Any(f => f.EndsWith(".part", StringComparison.Ordinal)))
        {
            why = "It is still downloading.";
        }
        if (why is null && !files.Any(f => f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)))
        {
            why = "It has no safetensors weights.";
        }
        if (why is null && Missing(full) is { Count: > 0 } missing)
        {
            why = $"{missing.Count} of its weight shards are not there ({missing[0]}...): the download is incomplete.";
        }
        return new LibrarySnapshot(System.IO.Path.GetRelativePath(root, full).Replace('\\', '/'), repo, revision, size, why is null, why, architectures);
    }

    /// <summary>The shards a snapshot's index names that are not in its folder.</summary>
    private static List<string> Missing(string dir)
    {
        var missing = new List<string>();
        foreach (var index in Directory.EnumerateFiles(dir, "*.safetensors.index.json"))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(index));
                if (doc.RootElement.TryGetProperty("weight_map", out var map) && map.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    missing.AddRange(map.EnumerateObject().Select(p => p.Value.GetString()).OfType<string>().Distinct()
                        .Where(f => !File.Exists(System.IO.Path.Combine(dir, f))));
                }
            }
            catch (System.Text.Json.JsonException)
            {
                missing.Add(System.IO.Path.GetFileName(index));
            }
        }
        return missing;
    }

    /// <summary>Whether a relative path names a file inside the library (no way out with ..).</summary>
    public bool Contains(string relative)
    {
        var root = System.IO.Path.GetFullPath(options.Value.LibraryDir);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        return full.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal) && File.Exists(full);
    }

    private ModelProfile ProfileOf(string path, List<string> parts, long size)
    {
        var modified = parts.Max(f => File.GetLastWriteTimeUtc(f));
        if (_profiles.TryGetValue(path, out var known) && known.Size == size && known.Modified == modified)
        {
            return known.Profile;
        }
        var header = Gguf.Read(parts[0], parts.Skip(1).ToList());
        var profile = header is null ? ModelProfiler.Unreadable() : ModelProfiler.Profile(header, System.IO.Path.GetFileName(path));
        _profiles[path] = (size, modified, profile);
        return profile;
    }

    /// <summary>What this deployment uses a file for besides the engine: the image generator's text encoder.</summary>
    private ModelProfile Annotate(string relative, ModelProfile profile)
    {
        var o = options.Value;
        if (o.ImageModelDir is { Length: > 0 } dir && System.IO.Path.GetDirectoryName(relative)?.Replace('\\', '/') == dir.Trim('/')
            && System.IO.Path.GetFileName(relative) == o.ImageTextEncoder && profile.Kind == ModelKind.Language)
        {
            return profile with { Note = "The image generator reads it as its text encoder. It also works as a chat model of its own; the image server keeps its own copy in memory." };
        }
        return profile;
    }

    private static IEnumerable<string> Enumerate(string dir, int depth)
    {
        IEnumerable<string> here;
        try
        {
            here = [.. Directory.EnumerateFiles(dir, "*.gguf")];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }
        foreach (var f in here)
        {
            yield return f;
        }
        if (depth >= MaxDepth)
        {
            yield break;
        }
        IEnumerable<string> subdirs;
        try
        {
            subdirs = [.. Directory.EnumerateDirectories(dir).Where(d => !System.IO.Path.GetFileName(d).StartsWith('.'))];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }
        foreach (var d in subdirs)
        {
            foreach (var f in Enumerate(d, depth + 1))
            {
                yield return f;
            }
        }
    }

    [GeneratedRegex(@"-(\d{5})-of-(\d{5})\.gguf$")]
    private static partial Regex SplitPart();
}
