using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodeArena;

/// <summary>
/// The words of code and text for searching: names split where their case or an underscore or a digit changes (RunToolsAsync:
/// run, tools, async, and runtoolsasync whole), lower case, without one-letter words, language keywords and English stop words,
/// plurals and -ing/-ed taken off.
/// </summary>
internal static class Terms
{
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "has", "have", "in", "is", "it", "its", "of", "on", "or", "that", "the", "this", "to",
        "was", "were", "will", "with", "what", "where", "which", "who", "how", "when", "do", "does", "not", "no", "but", "if", "then", "else", "we", "you", "i",
        // Code's own common words.
        "var", "let", "const", "new", "return", "public", "private", "protected", "internal", "static", "void", "int", "string", "bool", "true", "false", "null",
        "class", "def", "function", "self", "this", "async", "await", "using", "import", "export", "default", "readonly", "sealed", "override", "virtual",
    };

    /// <summary>The words of a text: each name split, and whole.</summary>
    public static IEnumerable<string> Of(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                if (start < 0)
                {
                    start = i;
                }
                continue;
            }
            if (start >= 0)
            {
                foreach (var w in Split(text[start..i]))
                {
                    yield return w;
                }
                start = -1;
            }
        }
    }

    /// <summary>One name's words: its parts, and the name whole when it has several.</summary>
    public static IEnumerable<string> Split(string name)
    {
        var parts = new List<string>();
        var b = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var boundary = c == '_' || (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                || (i > 0 && char.IsDigit(c) != char.IsDigit(name[i - 1]));
            if (boundary && b.Length > 0)
            {
                parts.Add(b.ToString());
                b.Clear();
            }
            if (c != '_')
            {
                b.Append(c);
            }
        }
        if (b.Length > 0)
        {
            parts.Add(b.ToString());
        }
        foreach (var p in parts)
        {
            if (Word(p) is { } w)
            {
                yield return w;
            }
        }
        if (parts.Count > 1 && Word(string.Concat(parts)) is { } whole)
        {
            yield return whole;
        }
    }

    /// <summary>A word as it is kept: lower case, its plural and -ing/-ed off; null for a one-letter or stop word, or a number.</summary>
    public static string? Word(string raw)
    {
        var w = raw.ToLowerInvariant();
        if (w.Length < 2 || Stop.Contains(w) || w.All(char.IsDigit))
        {
            return null;
        }
        return Stem(w);
    }

    /// <summary>A light stemmer: tools → tool, cached → cach, running → runn; enough that a word and its forms meet.</summary>
    public static string Stem(string w)
    {
        if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal))
        {
            return w[..^3] + "y";
        }
        if (w.Length > 4 && w.EndsWith("ing", StringComparison.Ordinal))
        {
            return w[..^3];
        }
        if (w.Length > 3 && w.EndsWith("ed", StringComparison.Ordinal))
        {
            return w[..^2];
        }
        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && !w.EndsWith("us", StringComparison.Ordinal))
        {
            return w[..^1];
        }
        return w;
    }
}

/// <summary>A symbol as the index keeps it: the scanned symbol, and its words with their weights.</summary>
internal sealed record IndexedSymbol(CodeSymbol Symbol, Dictionary<string, float> Terms);

/// <summary>A file as the index keeps it: what it is, a hash of its bytes, what it declares and imports, the names its code uses, and its words.</summary>
internal sealed class IndexedFile
{
    public required string Path { get; init; }
    public string? Lang { get; init; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public required string Sha { get; init; }
    public CodeFileTags Tags { get; init; }
    public int Lines { get; init; }
    public bool Approximate { get; init; }
    public List<IndexedSymbol> Symbols { get; init; } = [];
    public List<string> Imports { get; init; } = [];
    /// <summary>Every distinct name its code uses (not its comments or strings): where to look for a name's uses.</summary>
    public HashSet<string> Names { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Its words, whole-file, with their weights.</summary>
    public Dictionary<string, float> Terms { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>What a sync did: files read again, kept, removed, and how long it took.</summary>
internal sealed record IndexSync(int Read, int Kept, int Removed, int Skipped, TimeSpan Took);

/// <summary>
/// The code index of a folder: each file the index covers (CodeFiles) with what it declares and its words, kept in memory
/// and in one file in Code Arena's data folder (never in the repository). A sync reads again only the files whose size or
/// time changed and whose bytes did; a file of a newer format or another scanner's version is read again whole.
/// </summary>
internal sealed class CodeIndex
{
    public const int Format = 1;
    private static readonly byte[] Magic = "CAIX"u8.ToArray();

    private static readonly Dictionary<string, Func<ICodeScanner>> Scanners = new(StringComparer.Ordinal)
    {
        ["csharp"] = () => new CSharpScanner(),
        ["typescript"] = () => new TypeScriptScanner("typescript"),
        ["javascript"] = () => new TypeScriptScanner("javascript"),
        ["python"] = () => new PythonScanner(),
    };

    /// <summary>The scanners' versions together: a change reads every file again.</summary>
    public static string ScannerVersions => string.Join(',', Scanners.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}{s.Value().Version}"));

    private readonly object _gate = new();
    private Dictionary<string, IndexedFile> _files = new(StringComparer.Ordinal);
    private CodeGraph? _graph;
    private CodeSearch? _search;

    public CodeIndex(string root, string file)
    {
        Root = root;
        File = file;
    }

    public string Root { get; }

    /// <summary>Where it is kept: &lt;data&gt;/index/&lt;the folder's hash&gt;.bin.</summary>
    public string File { get; }

    /// <summary>How many files were scanned by the last sync (a test counts what is read again).</summary>
    public int LastScanned { get; private set; }

    /// <summary>
    /// The folder's index as it was kept, brought in step with the folder (built whole when there is none, it cannot be read,
    /// or a rebuild is asked for), and written back when anything changed.
    /// </summary>
    public static (CodeIndex Index, IndexSync Sync, bool Built) Open(string dataDir, Workspace workspace, CancellationToken ct, bool rebuild = false)
    {
        var file = FileFor(dataDir, workspace.Root);
        var index = rebuild ? null : Load(workspace.Root, file);
        var built = index is null;
        index ??= new CodeIndex(workspace.Root, file);
        var sync = index.Sync(workspace, workspace.Roots, ct);
        if (built || sync.Read > 0 || sync.Removed > 0 || sync.Skipped > 0)
        {
            index.Save();
        }
        return (index, sync, built);
    }

    public static string FileFor(string dataDir, string root) =>
        System.IO.Path.Combine(dataDir, "index", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16] + ".bin");

    public IReadOnlyCollection<IndexedFile> Files
    {
        get
        {
            lock (_gate)
            {
                return [.. _files.Values];
            }
        }
    }

    public IndexedFile? FileAt(string path)
    {
        lock (_gate)
        {
            return _files.GetValueOrDefault(path);
        }
    }

    /// <summary>A file's whole path, from its path as the index keeps it (relative to the root, or whole for an added folder outside it).</summary>
    public string FullOf(string path) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, path));

    /// <summary>Who uses what over the files as the index has them now; made again after a sync changes them.</summary>
    public CodeGraph Graph()
    {
        lock (_gate)
        {
            return _graph ??= new CodeGraph(this, _files.Values);
        }
    }

    /// <summary>The answers by words and by name over the files as the index has them now; made again after a sync changes them.</summary>
    public CodeSearch Search()
    {
        lock (_gate)
        {
            return _search ??= new CodeSearch(this, _files.Values);
        }
    }

    /// <summary>
    /// One file read again when its size or time changed and its bytes did (before it is cited); a file gone is taken out.
    /// New files come in with a sync, which decides what the index covers. True when the record changed.
    /// </summary>
    public bool Refresh(string path)
    {
        var kept = FileAt(path);
        if (kept is null || kept.Tags.HasFlag(CodeFileTags.Sensitive))
        {
            return false;
        }
        var full = FullOf(path);
        var info = new FileInfo(full);
        if (info.Exists && info.Length == kept.Size && info.LastWriteTimeUtc == kept.Modified)
        {
            return false;
        }
        IndexedFile? next = null;
        if (info.Exists && info.Length <= CodeFiles.MostTextBytes)
        {
            byte[] bytes;
            try
            {
                bytes = System.IO.File.ReadAllBytes(full);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
            if (sha == kept.Sha)
            {
                kept.Size = info.Length;
                kept.Modified = info.LastWriteTimeUtc;
                return false;
            }
            next = Read(new CodeFile(path, full, kept.Lang, info.Length, info.LastWriteTimeUtc, kept.Tags & ~CodeFileTags.Generated), Encoding.UTF8.GetString(bytes), sha);
        }
        lock (_gate)
        {
            if (next is null)
            {
                _files.Remove(path);
            }
            else
            {
                _files[path] = next;
            }
            _graph = null;
            _search = null;
        }
        return true;
    }

    /// <summary>What a language's scanner finds in a text; null for a language none reads.</summary>
    internal static ScannedFile? Scan(string? lang, string text) => lang is not null && Scanners.TryGetValue(lang, out var make) ? make().Scan(text) : null;

    /// <summary>
    /// Brings the index in step with the folder: files gone taken out, files new or changed (size or time, then bytes) read,
    /// the rest kept; at most <paramref name="threads"/> at once.
    /// </summary>
    public IndexSync Sync(Workspace workspace, IEnumerable<string> roots, CancellationToken ct, int threads = 0)
    {
        var clock = Stopwatch.StartNew();
        var listed = CodeFiles.List(workspace, roots, ct);
        Dictionary<string, IndexedFile> before;
        lock (_gate)
        {
            before = new Dictionary<string, IndexedFile>(_files, StringComparer.Ordinal);
        }
        var next = new System.Collections.Concurrent.ConcurrentDictionary<string, IndexedFile>(StringComparer.Ordinal);
        var read = 0;
        var skipped = 0;
        Parallel.ForEach(listed, new ParallelOptions { MaxDegreeOfParallelism = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount / 2), CancellationToken = ct }, f =>
        {
            if (before.TryGetValue(f.Path, out var kept) && kept.Size == f.Size && kept.Modified == f.Modified)
            {
                next[f.Path] = kept;
                return;
            }
            if (f.Tags.HasFlag(CodeFileTags.Sensitive))
            {
                // Listed so the index can say it skipped it; never opened.
                next[f.Path] = new IndexedFile { Path = f.Path, Lang = f.Lang, Size = f.Size, Modified = f.Modified, Sha = "", Tags = f.Tags };
                Interlocked.Increment(ref skipped);
                return;
            }
            byte[] bytes;
            try
            {
                bytes = System.IO.File.ReadAllBytes(f.Full);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
            if (kept is not null && kept.Sha == sha)
            {
                kept.Size = f.Size;
                kept.Modified = f.Modified;
                next[f.Path] = kept;
                return;
            }
            next[f.Path] = Read(f, Encoding.UTF8.GetString(bytes), sha);
            Interlocked.Increment(ref read);
        });
        lock (_gate)
        {
            _files = new Dictionary<string, IndexedFile>(next, StringComparer.Ordinal);
            if (read > 0 || before.Count != next.Count || before.Keys.Any(k => !next.ContainsKey(k)))
            {
                _graph = null;
                _search = null;
            }
        }
        LastScanned = read;
        var removed = before.Keys.Count(k => !next.ContainsKey(k));
        return new IndexSync(read, next.Count - read - skipped, removed, skipped, clock.Elapsed);
    }

    /// <summary>A file read into the index: its symbols (for a language it reads, up to a megabyte), its names, and its words.</summary>
    internal static IndexedFile Read(CodeFile f, string text, string sha)
    {
        ScannedFile? scanned = null;
        var head = text.Length > 2000 ? text[..2000] : text;
        var tags = f.Tags | CodeFiles.TagsOf(f.Path, head);
        if (f.Lang is { } lang && Scanners.TryGetValue(lang, out var make) && f.Size <= CodeFiles.MostSymbolBytes && !Minified(text))
        {
            scanned = make().Scan(text);
        }
        else if (f.Lang == "markdown")
        {
            scanned = Markdown(text);
        }
        var lines = text.Split('\n');
        var file = new IndexedFile
        {
            Path = f.Path, Lang = f.Lang, Size = f.Size, Modified = f.Modified, Sha = sha, Tags = tags, Lines = lines.Length, Approximate = scanned?.Approximate == true,
            Imports = scanned?.Imports ?? [],
        };
        if (scanned is not null)
        {
            foreach (var u in scanned.Uses)
            {
                file.Names.Add(u.Name);
            }
        }
        // The whole file's words: its path and its text.
        Weigh(file.Terms, Terms.Of(f.Path), 2);
        Weigh(file.Terms, Terms.Of(text), 1);
        foreach (var s in scanned?.Symbols ?? [])
        {
            var words = new Dictionary<string, float>(StringComparer.Ordinal);
            Weigh(words, Terms.Split(s.Name), 3);
            Weigh(words, Terms.Of(s.Qualified).Concat(Terms.Of(f.Path)), 2);
            Weigh(words, Terms.Of(s.Signature + " " + s.Doc), 1.5f);
            // Its body's words (its comments' and strings' too, at a little less: people search for an error's message).
            var body = string.Join('\n', lines[Math.Max(0, s.Start - 1)..Math.Min(lines.Length, s.End)]);
            Weigh(words, Terms.Of(body), s.Kind is "namespace" or "class" or "interface" or "record" or "struct" ? 0.2f : 0.85f);
            file.Symbols.Add(new IndexedSymbol(s, words));
        }
        return file;
    }

    private static void Weigh(Dictionary<string, float> into, IEnumerable<string> words, float weight)
    {
        foreach (var w in words)
        {
            into[w] = into.GetValueOrDefault(w) + weight;
        }
    }

    /// <summary>Minified code (lines longer than 300 characters on average): text only.</summary>
    private static bool Minified(string text)
    {
        var lines = text.Count(c => c == '\n') + 1;
        return text.Length / lines > 300;
    }

    /// <summary>A Markdown file's headings, each to the next heading of its level or higher.</summary>
    private static ScannedFile Markdown(string text)
    {
        var lines = text.Split('\n');
        var heads = new List<(int Level, string Name, int Line)>();
        var fence = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimEnd();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
            }
            if (!fence && t.StartsWith('#') && t.TrimStart('#').StartsWith(' '))
            {
                heads.Add((t.Length - t.TrimStart('#').Length, t.TrimStart('#').Trim(), i + 1));
            }
        }
        var symbols = new List<CodeSymbol>();
        for (var h = 0; h < heads.Count; h++)
        {
            var end = heads.Skip(h + 1).FirstOrDefault(x => x.Level <= heads[h].Level).Line is var n and > 0 ? n - 1 : lines.Length;
            symbols.Add(new CodeSymbol("heading", heads[h].Name, heads[h].Name, lines[heads[h].Line - 1].Trim(), heads[h].Line, end, null, null, ""));
        }
        return new ScannedFile(symbols, [], []);
    }

    // ------------------------------------------------------------------------------------------------- kept on disk

    /// <summary>Writes the index whole or not at all (another session in the folder may read it meanwhile), 0600 in a 0700 folder.</summary>
    public void Save(string? head = null)
    {
        var strings = new Dictionary<string, int>(StringComparer.Ordinal);
        var table = new List<string>();
        int Id(string? s)
        {
            if (s is null)
            {
                return -1;
            }
            if (!strings.TryGetValue(s, out var id))
            {
                strings[s] = id = table.Count;
                table.Add(s);
            }
            return id;
        }
        var files = Files;
        using var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(files.Count);
            foreach (var f in files)
            {
                w.Write(Id(f.Path));
                w.Write(Id(f.Lang));
                w.Write(f.Size);
                w.Write(f.Modified.Ticks);
                w.Write(Id(f.Sha));
                w.Write((int)f.Tags);
                w.Write(f.Lines);
                w.Write(f.Approximate);
                w.Write(f.Imports.Count);
                foreach (var i in f.Imports)
                {
                    w.Write(Id(i));
                }
                w.Write(f.Names.Count);
                foreach (var n in f.Names)
                {
                    w.Write(Id(n));
                }
                WriteTerms(w, f.Terms, Id);
                w.Write(f.Symbols.Count);
                foreach (var (s, terms) in f.Symbols)
                {
                    w.Write(Id(s.Kind));
                    w.Write(Id(s.Name));
                    w.Write(Id(s.Qualified));
                    w.Write(Id(s.Signature));
                    w.Write(s.Start);
                    w.Write(s.End);
                    w.Write(Id(s.Parent));
                    w.Write(Id(s.Doc));
                    w.Write(Id(s.Hash));
                    WriteTerms(w, terms, Id);
                }
            }
        }
        PrivateFiles.EnsureDirectory(System.IO.Path.GetDirectoryName(File)!);
        var temp = $"{File}.{Environment.ProcessId}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(temp, options))
        using (var zip = new System.IO.Compression.BrotliStream(stream, System.IO.Compression.CompressionLevel.Fastest))
        using (var w = new BinaryWriter(zip, Encoding.UTF8))
        {
            w.Write(Magic);
            w.Write(Format);
            w.Write(ScannerVersions);
            w.Write(Root);
            w.Write(head ?? "");
            w.Write(DateTime.UtcNow.Ticks);
            w.Write(table.Count);
            foreach (var s in table)
            {
                w.Write(s);
            }
            w.Write(body.ToArray());
        }
        System.IO.File.Move(temp, File, overwrite: true);
    }

    private static void WriteTerms(BinaryWriter w, Dictionary<string, float> terms, Func<string, int> id)
    {
        w.Write(terms.Count);
        foreach (var (t, weight) in terms)
        {
            w.Write(id(t));
            w.Write(weight);
        }
    }

    /// <summary>The index as it was kept; null when there is none, or it cannot be read, or it is of another format or scanners (it is built again).</summary>
    public static CodeIndex? Load(string root, string file)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(file);
            using var zip = new System.IO.Compression.BrotliStream(stream, System.IO.Compression.CompressionMode.Decompress);
            using var r = new BinaryReader(zip, Encoding.UTF8);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadInt32() != Format || r.ReadString() != ScannerVersions || r.ReadString() != root)
            {
                return null;
            }
            r.ReadString();
            r.ReadInt64();
            var table = new string[r.ReadInt32()];
            for (var i = 0; i < table.Length; i++)
            {
                table[i] = r.ReadString();
            }
            string? S(int id) => id < 0 ? null : table[id];
            var index = new CodeIndex(root, file);
            var count = r.ReadInt32();
            var files = new Dictionary<string, IndexedFile>(count, StringComparer.Ordinal);
            for (var n = 0; n < count; n++)
            {
                var path = S(r.ReadInt32())!;
                var lang = S(r.ReadInt32());
                var size = r.ReadInt64();
                var modified = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                var sha = S(r.ReadInt32())!;
                var tags = (CodeFileTags)r.ReadInt32();
                var lines = r.ReadInt32();
                var approximate = r.ReadBoolean();
                var imports = new List<string>(r.ReadInt32());
                for (var i = imports.Capacity; i > 0; i--)
                {
                    imports.Add(S(r.ReadInt32())!);
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (var i = r.ReadInt32(); i > 0; i--)
                {
                    names.Add(S(r.ReadInt32())!);
                }
                var file1 = new IndexedFile
                {
                    Path = path, Lang = lang, Size = size, Modified = modified, Sha = sha, Tags = tags, Lines = lines, Approximate = approximate, Imports = imports, Names = names,
                    Terms = ReadTerms(r, table),
                };
                for (var i = r.ReadInt32(); i > 0; i--)
                {
                    var symbol = new CodeSymbol(S(r.ReadInt32())!, S(r.ReadInt32())!, S(r.ReadInt32())!, S(r.ReadInt32())!, r.ReadInt32(), r.ReadInt32(), S(r.ReadInt32()),
                        S(r.ReadInt32()), S(r.ReadInt32())!);
                    file1.Symbols.Add(new IndexedSymbol(symbol, ReadTerms(r, table)));
                }
                files[path] = file1;
            }
            index._files = files;
            return index;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException or IndexOutOfRangeException or ArgumentException)
        {
            return null;
        }
    }

    private static Dictionary<string, float> ReadTerms(BinaryReader r, string[] table)
    {
        var count = r.ReadInt32();
        var terms = new Dictionary<string, float>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            terms[table[r.ReadInt32()]] = r.ReadSingle();
        }
        return terms;
    }
}
