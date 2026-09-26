using Argus.Indexing;
using Argus.Util;

namespace Argus.Packs.Sources;

public sealed record Doc(string Path, string Title, string Url, string Lang, string Body);

public sealed record ApiSymbol(string Name, string Kind, string Namespace, string DocPath, string Anchor, string Signature);

/// <summary>A documentation corpus a pack is built from.</summary>
public interface ISource
{
    string Name { get; }
    string RepoUrl { get; }
    string Branch { get; }
    string Subtree { get; }
    string License { get; }
    string LicenseUrl { get; }
    string Attribution { get; }
    /// <summary>A release archive to fetch instead of a git clone, or "".</summary>
    string ArchiveUrl => "";
    string ArchiveSha256 => "";
    IEnumerable<Doc> IterDocs(string root);
    IEnumerable<ApiSymbol> IterSymbols(string root);
    /// <summary>For a composite: (part name, checkout) pairs whose commits make up the provenance.</summary>
    IReadOnlyList<(string Name, string Checkout)>? PartCheckouts(string root) => null;
}

/// <summary>The registry of buildable sources, by the name the CLI takes.</summary>
public static class SourceCatalog
{
    public static readonly IReadOnlyDictionary<string, Func<ISource>> Sources = new Dictionary<string, Func<ISource>>(StringComparer.Ordinal)
    {
        ["python"] = () => new PythonDocs(),
        ["react"] = () => new ReactDocs(),
        ["cpp"] = () => new CppDocs(),
        ["win32"] = () => Composite.Win32WithSamples(),
        ["wdk"] = () => Composite.WdkWithSamples(),
        ["win32-docs"] = () => MicrosoftApiRef.Win32Api(),
        ["wdk-docs"] = () => MicrosoftApiRef.WdkDdi(),
        ["wdk-samples"] = () => CodeRepo.WindowsDriverSamples(),
        ["win32-samples"] = () => CodeRepo.WindowsClassicSamples(),
        ["algorithms"] = () => new AlgorithmsCpp(),
        ["system-design"] = () => new SystemDesignPrimer(),
        ["debugger"] = () => new DebuggerDocs(),
        ["sqlite"] = () => new SqliteDocs(),
        ["cppreference"] = () => new CppReference(),
        ["dotnet"] = () => new DotnetApiDocs(),
        ["scripting"] = () => Composite.ScriptingDocs(),
    };
}

/// <summary>
/// Walking a documentation tree the way pathlib does, because pack contents
/// and their ids follow this order.
///
/// <c>sorted(root.rglob(pattern))</c> compares paths component by component,
/// not as strings ("a/b" sorts before "a-c", which a string sort reverses), and
/// the recursive glob does not descend through symlinked directories. Both
/// differences would reorder a pack's documents relative to the Python build.
/// </summary>
public static class Walk
{
    /// <summary>Every file under <paramref name="root"/> whose name satisfies <paramref name="match"/>, in pathlib order.</summary>
    public static List<string> Files(string root, Func<string, bool> match)
    {
        var found = new List<string>();
        if (!Directory.Exists(root)) return found;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir); }
            catch (Exception exc) when (exc is IOException or UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                FileSystemInfo info = new DirectoryInfo(entry);
                bool isDir = info.Exists;
                if (isDir && info.LinkTarget is null) stack.Push(entry);
                if (match(name) && File.Exists(entry)) found.Add(entry);
            }
        }
        var rootFull = Path.GetFullPath(root);
        return found.OrderBy(p => Relative(rootFull, p), PathOrder.Instance).ToList();
    }

    public static string Relative(string root, string path) =>
        Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path)).Replace('\\', '/');

    /// <summary>
    /// <c>Path.read_text(encoding="utf-8", errors="replace")</c>, which opens in
    /// text mode: universal newlines turn every "\r\n" and lone "\r" into "\n".
    /// Reading the bytes without that translation changes a CRLF page's body,
    /// its content hash, and every chunk cut from it.
    /// </summary>
    public static string ReadText(string path) =>
        Utf8.DecodeReplace(File.ReadAllBytes(path)).Replace("\r\n", "\n").Replace('\r', '\n');

    public static string Stem(string path)
    {
        var name = Path.GetFileName(path);
        var suffix = Indexing.Filters.Suffix(name);
        return suffix.Length > 0 ? name[..^suffix.Length] : name;
    }
}

/// <summary>pathlib's ordering: compare the lists of path components.</summary>
public sealed class PathOrder : IComparer<string>
{
    public static readonly PathOrder Instance = new();

    public int Compare(string? a, string? b)
    {
        var pa = (a ?? "").Split('/');
        var pb = (b ?? "").Split('/');
        for (int i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            int c = string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }
}
