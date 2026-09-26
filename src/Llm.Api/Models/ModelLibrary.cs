using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>A GGUF file in the library: what it is, from its own header.</summary>
/// <param name="Path">Relative to the library; for a split model, its first part.</param>
/// <param name="Role">"model", "projector" (vision), "draft" (a multi-token-prediction head) or "other" (not a language model).</param>
public sealed record LibraryFile(string Path, long Size, int Parts, string Role, string? Architecture, string? Name, string? SizeLabel, int? TrainedContext);

/// <summary>
/// The GGUF files under the model library (LLAMACPP_LIBRARY_DIR), read-only.
/// Each file's header says what it is, so the Models page offers only language
/// models where a model is asked for, and fills in their trained context.
/// </summary>
public sealed partial class ModelLibrary(IOptions<EngineOptions> options)
{
    private const int MaxDepth = 3;

    public IReadOnlyList<LibraryFile> List()
    {
        var root = options.Value.LibraryDir;
        if (!Directory.Exists(root))
        {
            return [];
        }
        var files = new List<LibraryFile>();
        foreach (var path in Enumerate(root, 0).Order(StringComparer.Ordinal))
        {
            var split = SplitPart().Match(path);
            if (split.Success && split.Groups[1].Value != "00001")
            {
                continue;
            }
            var parts = split.Success ? int.Parse(split.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
            var size = split.Success
                ? Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(path)!, System.IO.Path.GetFileName(path).Replace("-00001-of-", "-*-of-", StringComparison.Ordinal)).Sum(f => new FileInfo(f).Length)
                : new FileInfo(path).Length;
            var header = Gguf.Read(path);
            var fileName = System.IO.Path.GetFileName(path);
            var role = header.Architecture == "clip" || fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase) ? "projector"
                : fileName.StartsWith("mtp-", StringComparison.OrdinalIgnoreCase) || fileName.Contains("-mtp", StringComparison.OrdinalIgnoreCase) ? "draft"
                : header.TrainedContext is not null ? "model"
                : "other";
            files.Add(new LibraryFile(System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'), size, parts, role, header.Architecture, header.Name, header.SizeLabel, header.TrainedContext));
        }
        return files;
    }

    /// <summary>Whether a relative path names a file inside the library (no way out with ..).</summary>
    public bool Contains(string relative)
    {
        var root = System.IO.Path.GetFullPath(options.Value.LibraryDir);
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        return full.StartsWith(root.TrimEnd('/') + "/", StringComparison.Ordinal) && File.Exists(full);
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

/// <summary>
/// Just enough of the GGUF format to say what a file is: its key/value header,
/// read until the few keys wanted are found, never the tensors.
/// </summary>
public static class Gguf
{
    public sealed record Header(string? Architecture, string? Name, string? SizeLabel, int? TrainedContext);

    private const int MaxKeys = 400;
    private const long MaxBytes = 16 * 1024 * 1024;

    public static Header Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var r = new BinaryReader(stream, Encoding.UTF8);
            if (r.ReadUInt32() != 0x46554747)
            {
                return new Header(null, null, null, null); // not "GGUF"
            }
            var version = r.ReadUInt32();
            if (version < 2)
            {
                return new Header(null, null, null, null);
            }
            r.ReadUInt64(); // tensors
            var count = r.ReadUInt64();
            string? arch = null, name = null, size = null;
            int? context = null;
            for (ulong i = 0; i < Math.Min(count, MaxKeys) && stream.Position < MaxBytes; i++)
            {
                var key = ReadString(r);
                var type = r.ReadUInt32();
                if (type == 8 && key is "general.architecture" or "general.name" or "general.size_label")
                {
                    var value = ReadString(r);
                    (arch, name, size) = key switch
                    {
                        "general.architecture" => (value, name, size),
                        "general.name" => (arch, value, size),
                        _ => (arch, name, value),
                    };
                }
                else if (key.EndsWith(".context_length", StringComparison.Ordinal) && type is 4 or 5 or 10 or 11)
                {
                    context = (int)Math.Min(type is 4 ? r.ReadUInt32() : type is 5 ? r.ReadInt32() : type is 10 ? (long)Math.Min(r.ReadUInt64(), int.MaxValue) : r.ReadInt64(), int.MaxValue);
                }
                else
                {
                    Skip(r, type);
                }
                if (arch is not null && name is not null && size is not null && context is not null)
                {
                    break;
                }
            }
            return new Header(arch, name, size, context);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentOutOfRangeException or OverflowException)
        {
            return new Header(null, null, null, null);
        }
    }

    private static string ReadString(BinaryReader r)
    {
        var length = r.ReadUInt64();
        if (length > 1 << 20)
        {
            throw new EndOfStreamException("a string longer than a header holds");
        }
        return Encoding.UTF8.GetString(r.ReadBytes((int)length));
    }

    private static void Skip(BinaryReader r, uint type)
    {
        switch (type)
        {
            case 0 or 1 or 7: r.BaseStream.Seek(1, SeekOrigin.Current); break;
            case 2 or 3: r.BaseStream.Seek(2, SeekOrigin.Current); break;
            case 4 or 5 or 6: r.BaseStream.Seek(4, SeekOrigin.Current); break;
            case 10 or 11 or 12: r.BaseStream.Seek(8, SeekOrigin.Current); break;
            case 8: ReadString(r); break;
            case 9:
                var inner = r.ReadUInt32();
                var n = r.ReadUInt64();
                if (inner == 8)
                {
                    for (ulong i = 0; i < n; i++)
                    {
                        var length = r.ReadUInt64();
                        r.BaseStream.Seek((long)length, SeekOrigin.Current);
                    }
                }
                else
                {
                    var width = inner switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => throw new EndOfStreamException("a nested array") };
                    r.BaseStream.Seek((long)n * width, SeekOrigin.Current);
                }
                break;
            default: throw new EndOfStreamException($"an unknown value type {type}");
        }
    }
}
