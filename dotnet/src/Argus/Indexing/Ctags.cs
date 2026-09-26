using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Argus.Store;
using Argus.Util;

namespace Argus.Indexing;

/// <summary>universal-ctags is not installed, is the wrong implementation, or timed out.</summary>
public sealed class CtagsUnavailable(string message) : Exception(message);

/// <summary>
/// The result of one ctags invocation, including what it did NOT process.
/// Every path handed in lands in exactly one of <see cref="Covered"/> and
/// <see cref="Uncovered"/>; the symbol map
/// alone cannot answer "was this path processed".
/// </summary>
public sealed record SymbolBatch(
    Dictionary<string, List<Writes.SymbolRow>> Symbols,
    HashSet<string> Covered,
    Dictionary<string, string> Uncovered,
    HashSet<string> Unattributable)
{
    public static SymbolBatch Empty() => new(new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
}

/// <summary>Symbol extraction with universal-ctags.</summary>
public static class Ctags
{
    public static readonly HashSet<string> PrivateScopes = new(StringComparer.Ordinal) { "detail", "internal", "impl", "anonymous" };
    public const int TimeoutSeconds = 600;

    /// <summary>Bump when a symbol ROW means something different; composed into the per-file stamp.</summary>
    public const string SymbolContractVersion = "3";

    public static readonly string[] Args =
    [
        "--output-format=json",
        "--fields=+nKSsefl", // l: the language, which decides whether a docstring is read
        "--kinds-c=+p",
        "--kinds-c++=+p",
        "-L", "-",
        "-f", "-",
    ];

    /// <summary>Locate an executable on PATH, as shutil.which does.</summary>
    public static string? Which(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [""];
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    static HashSet<string> PathsNamedIn(IEnumerable<string> paths, string stderr)
    {
        var blamed = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(stderr)) return blamed;
        var haystack = stderr.Replace('\\', '/');
        foreach (var p in paths)
        {
            var pattern = @"(?<![\w./-])" + Regex.Escape(p) + @"(?![\w./-])";
            if (Regex.IsMatch(haystack, pattern, RegexOptions.CultureInvariant)) blamed.Add(p);
        }
        return blamed;
    }

    public static bool IsPublicSymbol(string path, string? scope, bool fileRestricted)
    {
        if (!string.IsNullOrEmpty(scope))
        {
            var parts = scope.Replace("::", ".").Split('.').Select(p => PyStr.Strip(p));
            if (parts.Any(p => PrivateScopes.Contains(p) || p.StartsWith("__anon", StringComparison.Ordinal))) return false;
        }
        if (Filters.HeaderExtensions.Contains(Filters.Suffix(path).ToLowerInvariant())) return true;
        return !fileRestricted;
    }

    /// <summary>Run ctags over <paramref name="relPaths"/> (relative to <paramref name="root"/>).</summary>
    public static SymbolBatch ExtractSymbols(string root, IReadOnlyList<string> relPaths)
    {
        if (relPaths.Count == 0) return SymbolBatch.Empty();
        var exe = Which("ctags") ?? throw new CtagsUnavailable(
            "ctags not found on PATH — install universal-ctags on the index host");

        var existing = relPaths.Where(p => File.Exists(Path.Combine(root, p))).ToList();
        var listed = new HashSet<string>(existing, StringComparer.Ordinal);
        var uncovered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in relPaths)
            if (!listed.Contains(p)) uncovered[p] = "not a readable file on disk when ctags ran";
        if (existing.Count == 0)
            return new SymbolBatch(new(StringComparer.Ordinal), new(StringComparer.Ordinal), uncovered, new(StringComparer.Ordinal));

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in Args) psi.ArgumentList.Add(a);

        string stdout, stderr;
        int exitCode;
        using (var proc = Process.Start(psi) ?? throw new CtagsUnavailable("ctags failed: could not start process"))
        {
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            proc.StandardInput.Write(string.Join("\n", existing));
            proc.StandardInput.Close();
            if (!proc.WaitForExit(TimeSpan.FromSeconds(TimeoutSeconds)))
            {
                try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new CtagsUnavailable($"ctags timed out after {TimeoutSeconds}s");
            }
            stdout = outTask.GetAwaiter().GetResult();
            stderr = errTask.GetAwaiter().GetResult();
            exitCode = proc.ExitCode;
        }
        if (exitCode != 0 && stdout.Length == 0)
            throw new CtagsUnavailable($"ctags failed: {PyStr.Prefix(PyStr.Strip(stderr), 500)}");

        var results = new Dictionary<string, List<Writes.SymbolRow>>(StringComparer.Ordinal);
        foreach (var line in PyStr.SplitLines(stdout))
        {
            if (PyStr.Strip(line).Length == 0) continue;
            JsonElement entry;
            try { entry = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { continue; }
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("_type", out var type) || type.GetString() != "tag") continue;

            var path = NormalisePath(Str(entry, "path") ?? "");
            var scope = Str(entry, "scope");
            long? end = entry.TryGetProperty("end", out var e) && Truthy(e) ? ToLong(e) : null;
            bool fileRestricted = entry.TryGetProperty("file", out var f) && Truthy(f);
            if (!results.TryGetValue(path, out var list)) results[path] = list = [];
            list.Add(new Writes.SymbolRow(
                Name: Str(entry, "name") ?? "",
                Kind: Str(entry, "kind") ?? "unknown",
                Line: entry.TryGetProperty("line", out var l) ? ToLong(l) : 0,
                EndLine: end,
                Signature: Str(entry, "signature"),
                Scope: scope,
                IsPublic: IsPublicSymbol(path, scope, fileRestricted) ? 1 : 0,
                Doc: null,
                Language: Str(entry, "language") is { Length: > 0 } lang ? lang : null));
        }

        if (exitCode == 0)
            return new SymbolBatch(results, listed, uncovered, new(StringComparer.Ordinal));

        var detail = PyStr.Prefix(PyStr.Strip(stderr), 500);
        Console.Error.WriteLine($"ctags exited {exitCode} with partial output for {existing.Count} file(s): {detail}");
        var blamed = PathsNamedIn(existing, stderr);
        var covered = blamed.Count > 0
            ? new HashSet<string>(listed.Where(p => !blamed.Contains(p)), StringComparer.Ordinal)
            : new HashSet<string>(results.Keys.Where(listed.Contains), StringComparer.Ordinal);
        var misses = listed.Where(p => !covered.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();
        foreach (var p in misses)
            uncovered[p] = $"ctags exited {exitCode} without covering this file: {(detail.Length > 0 ? detail : "no diagnostic output")}";
        var unattributable = blamed.Count == 0 ? new HashSet<string>(misses, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        return new SymbolBatch(results, covered, uncovered, unattributable);
    }

    /// <summary>PurePosixPath(p.replace('\\','/')).as_posix(): collapse duplicate and trailing slashes and "." parts.</summary>
    static string NormalisePath(string raw)
    {
        var p = raw.Replace('\\', '/');
        bool abs = p.StartsWith('/');
        var parts = p.Split('/').Where(x => x.Length > 0 && x != ".");
        var joined = string.Join("/", parts);
        if (abs) joined = (p.StartsWith("//") && !p.StartsWith("///") ? "//" : "/") + joined;
        return joined.Length == 0 ? "." : joined;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Truthy(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => v.GetDouble() != 0,
        JsonValueKind.String => v.GetString()!.Length > 0,
        JsonValueKind.Array => v.GetArrayLength() > 0,
        JsonValueKind.Object => v.EnumerateObject().Any(),
        _ => false,
    };

    static long ToLong(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.TryGetInt64(out var n) ? n : (long)v.GetDouble(),
        JsonValueKind.String => long.Parse(v.GetString()!),
        _ => 0,
    };
}
