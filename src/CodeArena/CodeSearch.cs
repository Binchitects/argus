using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A question for the code index: what to find, where, and how much of an answer it may take.</summary>
internal sealed record CodeQuery(string Text)
{
    /// <summary>Only under this folder or file, or matching this glob.</summary>
    public string? Path { get; init; }
    /// <summary>Rank tests like other code (else below it, unless the question says test).</summary>
    public bool Tests { get; init; }
    public int Budget { get; init; } = 2000;
    public double CharsPerToken { get; init; } = 3.5;
    /// <summary>Whether the person's rules let a path be read, as read_file would; null: every path may.</summary>
    public Func<string, bool>? MayRead { get; init; }
    /// <summary>Places already shown in this context (path:a-b): cited again in one line, not shown again.</summary>
    public ISet<string>? Shown { get; init; }
    /// <summary>Files changed in the working tree: ranked a little higher.</summary>
    public IReadOnlySet<string>? Changed { get; init; }
    /// <summary>Files read or edited in this session: ranked a little higher.</summary>
    public IReadOnlySet<string>? Touched { get; init; }
}

/// <summary>A place an answer cites: its lines, its symbol, and why it was chosen (the words it holds, or "use" for a use of a name).</summary>
internal sealed record CodeHit(string Path, int Start, int End, string Kind, string Name, IReadOnlyList<string> Why);

/// <summary>An answer: by words or by name, the places it cites, whether it holds every match, how many it shows of how many, and its text.</summary>
internal sealed record CodeAnswer(string Route, IReadOnlyList<CodeHit> Items, bool Complete, int Shown, int Total, string Text);

/// <summary>
/// The code index's answers. By words: the symbols ranked by BM25 over their words, their file's BM25, how many of the words
/// their lines hold now, and how near they are (calls, containment, imports) to the best few, with tests, generated and
/// vendored code lower; then cited, each as path:start-end with its symbol and its lines as they are on disk, until the
/// budget is spent. By name: its definitions and every use in code, grouped by the symbol each is in, complete for code.
/// </summary>
internal sealed partial class CodeSearch
{
    private const float K1 = 1.2f;
    private const float B = 0.75f;
    private const int WholeLines = 40;
    private const int MostLineChars = 300;
    private const long MostReadBytes = 8 * 1024 * 1024;
    private const string SecretHidden = "«secret-like value hidden»";

    private static readonly HashSet<string> Lookup = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "is", "are", "the", "a", "an", "of", "in", "its", "all", "me", "show", "find", "who", "what", "which", "defined", "define", "definition",
        "definitions", "declared", "declaration", "used", "use", "uses", "usage", "usages", "caller", "callers", "call", "calls", "called", "reference",
        "references", "refs", "implementation", "implemented", "implements", "and", "or", "for", "to", "does", "do", "how",
    };
    private static readonly string[] Containers = ["namespace", "class", "struct", "interface", "record", "enum", "component"];

    private readonly CodeIndex _index;
    private readonly List<(IndexedFile File, IndexedSymbol Symbol, float Length)> _symbols = [];
    private readonly List<(IndexedFile File, float Length)> _files = [];
    private readonly float _symbolLength;
    private readonly float _fileLength;

    public CodeSearch(CodeIndex index, IEnumerable<IndexedFile> files)
    {
        _index = index;
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            if (f.Tags.HasFlag(CodeFileTags.Sensitive))
            {
                continue;
            }
            _files.Add((f, f.Terms.Values.Sum()));
            foreach (var s in f.Symbols)
            {
                if (s.Symbol.Kind != "namespace")
                {
                    _symbols.Add((f, s, s.Terms.Values.Sum()));
                }
            }
        }
        _symbolLength = _symbols.Count > 0 ? _symbols.Average(s => s.Length) : 1;
        _fileLength = _files.Count > 0 ? _files.Average(f => f.Length) : 1;
    }

    public int FileCount => _files.Count;

    public CodeAnswer Answer(CodeQuery q) => (Names(q.Text) is { } names ? ByName(q, names) : null) ?? ByWords(q);

    // ------------------------------------------------------------------------------------------------- which route

    /// <summary>
    /// The names a question asks about, when it asks by name: code names (x(), A::B, A.B, path#A, snake_case, an inner capital,
    /// `quoted`, or a capitalised word the index declares), at most four, with nothing else but lookup words; else null.
    /// </summary>
    internal List<string>? Names(string text)
    {
        var names = new List<string>();
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var quoted = raw.StartsWith('`') && raw.EndsWith('`') && raw.Length > 2;
            var word = raw.Trim('`', '"', '\'', '?', ',', ';', ':', '!');
            var call = word.EndsWith("()", StringComparison.Ordinal);
            var bare = (call ? word[..^2] : word).Replace("::", ".", StringComparison.Ordinal);
            if (bare.Length == 0)
            {
                continue;
            }
            var ident = Identifier().IsMatch(bare);
            var codeLike = ident && (quoted || call || word.Contains("::", StringComparison.Ordinal) || bare.Contains('#') || bare.Contains('.') || bare.Contains('_')
                || InnerCapital(bare) || (char.IsUpper(bare[0]) && _index.Graph().Find(bare).Count > 0));
            if (codeLike)
            {
                names.Add(bare);
            }
            else if (!Lookup.Contains(word))
            {
                return null;
            }
        }
        return names.Count is > 0 and <= 4 ? names : null;
    }

    [GeneratedRegex(@"^(?:[\w./-]+#)?[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*$")]
    private static partial Regex Identifier();

    private static bool InnerCapital(string name)
    {
        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && char.IsLower(name[i - 1]))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Within(string path, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter is "." or "./")
        {
            return true;
        }
        if (filter.IndexOfAny(['*', '?', '[', '{']) >= 0)
        {
            return Files.Glob(filter).IsMatch(path);
        }
        var f = filter.Replace('\\', '/').TrimEnd('/');
        f = f.StartsWith("./", StringComparison.Ordinal) ? f[2..] : f;
        return path == f || path.StartsWith(f + "/", StringComparison.Ordinal);
    }

    private static bool Allowed(CodeQuery q, IndexedFile f) => Within(f.Path, q.Path) && !f.Tags.HasFlag(CodeFileTags.Sensitive);

    // ------------------------------------------------------------------------------------------------- by words

    private sealed class Candidate(IndexedFile file, CodeSymbol symbol)
    {
        public IndexedFile File { get; } = file;
        public CodeSymbol Symbol { get; } = symbol;
        public string Id => $"{File.Path}#{Symbol.Qualified}";
        public float L;
        public float F;
        public float G;
        public float Q;
        public float P = 1;
        /// <summary>Its lines hold the question's text word for word.</summary>
        public bool Phrase;
        /// <summary>The lines that hold it: shown first.</summary>
        public SortedSet<int> PhraseLines { get; } = [];
        public double Score;
        public List<string> Why { get; } = [];
        public HashSet<string> Calls { get; } = new(StringComparer.Ordinal);
        public HashSet<string> CalledBy { get; } = new(StringComparer.Ordinal);
    }

    private CodeAnswer ByWords(CodeQuery q)
    {
        var raw = Words(q.Text);
        var terms = raw.SelectMany(Terms.Split).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0)
        {
            return new CodeAnswer("words", [], false, 0, 0, $"code_search \"{q.Text}\" · words · nothing to search for: give what the code does, or a name.");
        }
        var tests = q.Tests || terms.Contains("test") || terms.Contains("tests");
        var docs = terms.Any(t => t is "doc" or "documentation" or "readme" or "guide");

        // F: each file's BM25 over its words.
        var fileScore = new Dictionary<IndexedFile, float>();
        var fileDf = terms.ToDictionary(t => t, t => _files.Count(f => f.File.Terms.ContainsKey(t)), StringComparer.Ordinal);
        foreach (var (f, length) in _files)
        {
            if (!Allowed(q, f))
            {
                continue;
            }
            var s = Bm25(f.Terms, length, _fileLength, terms, fileDf, _files.Count);
            if (s > 0)
            {
                fileScore[f] = s;
            }
        }
        // L: each symbol's BM25 over its words.
        var df = terms.ToDictionary(t => t, t => _symbols.Count(s => s.Symbol.Terms.ContainsKey(t)), StringComparer.Ordinal);
        var all = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var hidden = 0;
        foreach (var (f, s, length) in _symbols)
        {
            if (!Allowed(q, f))
            {
                continue;
            }
            var l = Bm25(s.Terms, length, _symbolLength, terms, df, _symbols.Count);
            if (l <= 0)
            {
                continue;
            }
            if (q.MayRead is { } may && !may(f.Path))
            {
                hidden++;
                continue;
            }
            var c = new Candidate(f, s.Symbol) { L = l, F = fileScore.GetValueOrDefault(f) };
            c.Why.AddRange(terms.Where(s.Terms.ContainsKey));
            // Overloads share an id: the better one stands for them.
            if (!all.TryGetValue(c.Id, out var other) || other.L < l)
            {
                all[c.Id] = c;
            }
        }
        if (all.Count == 0)
        {
            var none = $"code_search \"{q.Text}\" · words · no symbol holds these words{(hidden > 0 ? $" ({hidden} hidden by your rules)" : "")}. Every line: {Grep(raw, q.Path)}";
            return new CodeAnswer("words", [], false, 0, 0, none);
        }
        var maxL = all.Values.Max(c => c.L);
        var maxF = Math.Max(1e-6f, all.Values.Max(c => c.F));
        foreach (var c in all.Values)
        {
            c.L /= maxL;
            c.F /= maxF;
            c.P = Prior(c.File, q, tests, docs);
        }

        var graph = _index.Graph();
        Near(graph, all, q);
        Coverage(graph, all.Values, terms);
        Phrase(graph, all, q, fileScore);
        foreach (var c in all.Values)
        {
            c.Score = (c.L + 0.5 * c.F + 0.6 * c.G + 0.3 * c.Q + (c.Phrase ? 1.5 : 0)) * c.P;
        }
        var ranked = all.Values.Where(c => c.Score > 0).OrderByDescending(c => c.Score).ThenBy(c => c.File.Path, StringComparer.Ordinal).ThenBy(c => c.Symbol.Start).ToList();
        return Pack(q, raw, ranked, hidden, graph);
    }

    /// <summary>A question's words, lookup words aside: what grep would be given.</summary>
    private static List<string> Words(string text) =>
        [.. Regex.Matches(text, @"[A-Za-z_][\w]*").Select(m => m.Value).Where(w => !Lookup.Contains(w) && Terms.Word(w) is not null).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static float Bm25(Dictionary<string, float> doc, float length, float average, List<string> terms, Dictionary<string, int> df, int n)
    {
        var score = 0f;
        foreach (var t in terms)
        {
            if (!doc.TryGetValue(t, out var tf))
            {
                continue;
            }
            var idf = MathF.Log(1 + (n - df[t] + 0.5f) / (df[t] + 0.5f));
            score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * length / average));
        }
        return score;
    }

    /// <summary>Tests, generated, vendored and docs lower (unless asked for); changed or touched files a little higher.</summary>
    private static float Prior(IndexedFile f, CodeQuery q, bool tests, bool docs)
    {
        var p = 1f;
        if (f.Tags.HasFlag(CodeFileTags.Test) && !tests)
        {
            p *= 0.6f;
        }
        if (f.Tags.HasFlag(CodeFileTags.Generated) || f.Tags.HasFlag(CodeFileTags.Vendored))
        {
            p *= 0.3f;
        }
        if (f.Tags.HasFlag(CodeFileTags.Docs) && !docs)
        {
            p *= 0.7f;
        }
        if (q.Changed?.Contains(f.Path) == true)
        {
            p *= 1.25f;
        }
        if (q.Touched?.Contains(f.Path) == true)
        {
            p *= 1.15f;
        }
        return p;
    }

    /// <summary>
    /// G: nearness to the three best by their words. What each calls and what calls it count half its score, what it holds or
    /// what holds it 0.4, symbols holding the words in a file it imports 0.2; one step out.
    /// </summary>
    private void Near(CodeGraph graph, Dictionary<string, Candidate> all, CodeQuery q)
    {
        var seeds = all.Values.OrderByDescending(c => c.L).ThenBy(c => c.Id, StringComparer.Ordinal).Take(3).ToList();
        void Boost(IndexedFile file, CodeSymbol symbol, float g, Candidate seed, bool? calls)
        {
            if (!Allowed(q, file) || (q.MayRead is { } may && !may(file.Path)) || symbol.Kind == "namespace")
            {
                return;
            }
            var id = $"{file.Path}#{symbol.Qualified}";
            if (id == seed.Id)
            {
                return;
            }
            if (!all.TryGetValue(id, out var c))
            {
                // Not holding the words, but next to the best: it may be what is asked for.
                all[id] = c = new Candidate(file, symbol) { P = Prior(file, q, q.Tests, false) };
            }
            c.G = Math.Max(c.G, g);
            if (calls == true)
            {
                seed.Calls.Add(id);
                c.CalledBy.Add(seed.Id);
            }
            else if (calls == false)
            {
                seed.CalledBy.Add(id);
                c.Calls.Add(seed.Id);
            }
        }
        foreach (var seed in seeds)
        {
            var target = new SymbolRef(seed.File, seed.Symbol);
            foreach (var e in graph.UsesFrom(target).Where(e => e.Call && e.Evidence is not Evidence.Guess))
            {
                Boost(e.To[0].File, e.To[0].Symbol, 0.5f * seed.L, seed, true);
            }
            foreach (var e in graph.UsesOf(target, mostFiles: 60).Uses.Where(e => e.From is not null && e.Evidence is not Evidence.Guess))
            {
                if (_index.FileAt(e.Path) is { } file && file.Symbols.FirstOrDefault(s => s.Symbol.Qualified == e.From!.Qualified) is { } from)
                {
                    // A reference (a type named in a declaration) is as near as a call, but not said to be one.
                    Boost(file, from.Symbol, 0.5f * seed.L, seed, e.Call ? false : null);
                }
            }
            foreach (var (s, _) in seed.File.Symbols.Where(s => s.Symbol.Parent == seed.Symbol.Qualified || s.Symbol.Qualified == seed.Symbol.Parent))
            {
                Boost(seed.File, s, 0.4f * seed.L, seed, null);
            }
            var imported = graph.Imported(seed.File).ToHashSet();
            foreach (var c in all.Values.Where(c => imported.Contains(c.File) && c.L > 0))
            {
                c.G = Math.Max(c.G, 0.2f * seed.L);
            }
        }
    }

    /// <summary>
    /// A question of several words that a file holds word for word (a message, a line of code): the symbol holding that line
    /// goes first, whatever its length. Looked for in the fifty files best by their words that hold every word.
    /// </summary>
    private void Phrase(CodeGraph graph, Dictionary<string, Candidate> all, CodeQuery q, Dictionary<IndexedFile, float> fileScore)
    {
        var phrase = Regex.Replace(q.Text.Trim(), @"\s+", " ").TrimEnd('.', '?', '!', ':', ';', ',');
        if (phrase.Split(' ').Length < 3)
        {
            return;
        }
        var terms = Terms.Of(phrase).Distinct(StringComparer.Ordinal).ToList();
        foreach (var file in fileScore.Where(f => terms.All(f.Key.Terms.ContainsKey)).OrderByDescending(f => f.Value).ThenBy(f => f.Key.Path, StringComparer.Ordinal).Take(50).Select(f => f.Key))
        {
            if ((q.MayRead is { } may && !may(file.Path)) || graph.Now(file) is not { } now)
            {
                continue;
            }
            string[]? code = null;
            for (var at = now.Text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase); at >= 0; at = now.Text.IndexOf(phrase, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var line = 1 + now.Text.AsSpan(0, at).Count('\n');
                // In a comment just above a symbol (its doc): that symbol's.
                code ??= now.Code.Split('\n');
                var comment = line <= code.Length && code[line - 1].Trim().Length == 0;
                var below = comment ? now.Scanned.Symbols.Where(s => s.Start > line && s.Start - line <= 3 && s.Kind != "namespace").MinBy(s => s.Start) : null;
                if ((below ?? now.Enclosing(line)) is not { } symbol)
                {
                    continue;
                }
                var id = $"{file.Path}#{symbol.Qualified}";
                if (!all.TryGetValue(id, out var c))
                {
                    all[id] = c = new Candidate(file, symbol) { P = Prior(file, q, q.Tests, false) };
                    c.Why.AddRange(terms);
                }
                c.Phrase = true;
                c.PhraseLines.Add(line);
            }
        }
    }

    /// <summary>Q: the share of the words a symbol's lines hold now, for the symbols of the thirty best files (at most 8 MB read).</summary>
    private static void Coverage(CodeGraph graph, IEnumerable<Candidate> all, List<string> terms)
    {
        var best = all.GroupBy(c => c.File).Select(g => (File: g.Key, Best: g.Max(c => c.L + 0.5f * c.F))).OrderByDescending(x => x.Best)
            .ThenBy(x => x.File.Path, StringComparer.Ordinal).Take(30).Select(x => x.File).ToHashSet();
        long read = 0;
        foreach (var group in all.Where(c => best.Contains(c.File)).GroupBy(c => c.File).OrderBy(g => g.Key.Path, StringComparer.Ordinal))
        {
            if (read > MostReadBytes || graph.Now(group.Key) is not { } now)
            {
                continue;
            }
            read += now.Text.Length;
            var lines = now.Lines;
            foreach (var c in group.Where(c => !Containers.Contains(c.Symbol.Kind)))
            {
                var (a, b) = RangeNow(now, c.Symbol);
                b = Math.Min(b, a + 200);
                var words = Terms.Of(string.Join('\n', lines[Math.Max(0, a - 1)..Math.Min(lines.Length, b)])).ToHashSet(StringComparer.Ordinal);
                c.Q = (float)terms.Count(words.Contains) / terms.Count;
            }
        }
    }

    /// <summary>A symbol's lines as the file is now: the same symbol nearest where the index saw it, else where it was.</summary>
    private static (int Start, int End) RangeNow(FileNow now, CodeSymbol symbol) =>
        now.Scanned.Symbols.Where(s => s.Qualified == symbol.Qualified && s.Kind == symbol.Kind).MinBy(s => Math.Abs(s.Start - symbol.Start)) is { } here
            ? (here.Start, here.End)
            : (symbol.Start, symbol.End);

    // ------------------------------------------------------------------------------------------------- packing

    /// <summary>A place cited: its lines shown (all of them from Start to End when Whole), its symbol's lines, and the secret-like values it hid.</summary>
    private sealed record Place(Candidate Candidate, int Start, int End, int SymbolStart, int SymbolEnd, List<string> Lines, bool Whole, int Secrets);

    private CodeAnswer Pack(CodeQuery q, List<string> raw, List<Candidate> ranked, int hidden, CodeGraph graph)
    {
        var room = (int)(q.Budget * q.CharsPerToken);
        var grep = Grep(raw, q.Path);
        // Room kept for the header, the coverage line and the edges named once every place is numbered.
        var reserve = 300 + q.Text.Length + grep.Length;
        var chosen = new List<Place>();
        var perFile = new Dictionary<string, int>(StringComparer.Ordinal);
        var terms = raw.SelectMany(Terms.Split).ToHashSet(StringComparer.Ordinal);
        var used = 0;

        // Inside a function already cited, or inside lines already shown whole.
        bool Covered(Candidate c) => chosen.Any(p => p.Candidate.File == c.File && ((!Containers.Contains(p.Candidate.Symbol.Kind) && p.SymbolStart <= c.Symbol.Start && c.Symbol.End <= p.SymbolEnd)
            || (p.Whole && p.Start <= c.Symbol.Start && c.Symbol.End <= p.End)));

        foreach (var pass in new[] { 2, int.MaxValue })
        {
            var misses = 0;
            foreach (var c in ranked)
            {
                if (misses >= 8 || room - used - reserve < 120)
                {
                    break;
                }
                if (chosen.Any(p => p.Candidate == c) || perFile.GetValueOrDefault(c.File.Path) >= pass || Covered(c))
                {
                    continue;
                }
                _index.Refresh(c.File.Path);
                // A type holding places already cited is shown by its outline, not again whole.
                var outline = chosen.Any(p => p.Candidate.File == c.File && c.Symbol.Start <= p.SymbolStart && p.SymbolEnd <= c.Symbol.End);
                if (graph.Now(c.File) is not { } now || Excerpt(now, c, terms, outline) is not { } place)
                {
                    continue;
                }
                var size = Render(place, chosen.Count + 1, chosen, q.Shown).Length + 24;
                if (used + size + reserve > room)
                {
                    misses++;
                    continue;
                }
                chosen.Add(place);
                used += size;
                perFile[c.File.Path] = perFile.GetValueOrDefault(c.File.Path) + 1;
            }
        }
        var rest = ranked.Where(c => !chosen.Any(p => p.Candidate == c)).Take(40)
            .Select(c => $"- {c.File.Path}:{c.Symbol.Start} · {c.Symbol.Kind} {Display(c.File, c.Symbol)} · {string.Join(", ", c.Why.Take(4))}").ToList();
        var notes = new List<string>();
        if (hidden > 0)
        {
            notes.Add($"{hidden} hidden by your rules");
        }
        var secrets = chosen.Sum(p => p.Secrets);
        if (secrets > 0)
        {
            notes.Add($"{secrets} secret-like {(secrets == 1 ? "value" : "values")} hidden");
        }

        // Written whole, then made to fit: one-line places dropped from the end, then whole places.
        var more = rest.Count;
        while (true)
        {
            var body = new StringBuilder();
            for (var i = 0; i < chosen.Count; i++)
            {
                body.Append(Render(chosen[i], i + 1, chosen, q.Shown, edges: true));
            }
            if (more > 0)
            {
                body.Append("More, one line each:\n");
                foreach (var line in rest.Take(more))
                {
                    body.Append(line).Append('\n');
                }
            }
            var listed = chosen.Count + more;
            body.Append(CultureInfo.InvariantCulture, $"Ranked, not every match: {listed} of {ranked.Count} symbols holding these words or next to the best{(notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : "")}. Every line: {grep}");
            var header = $"code_search \"{q.Text}\" · words · {chosen.Count} {(chosen.Count == 1 ? "place" : "places")} · ≈{{0}} of {q.Budget:N0} tokens · {FileCount:N0} files indexed\n";
            var text = header + body;
            text = text.Replace("{0}", ((int)Math.Ceiling(text.Length / q.CharsPerToken)).ToString("N0", CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (text.Length <= room || (chosen.Count <= 1 && more == 0))
            {
                foreach (var p in chosen)
                {
                    q.Shown?.Add($"{p.Candidate.File.Path}:{p.Start}-{p.End}");
                }
                var hits = chosen.Select(p => new CodeHit(p.Candidate.File.Path, p.Start, p.End, p.Candidate.Symbol.Kind, Display(p.Candidate.File, p.Candidate.Symbol), p.Candidate.Why)).ToList();
                return new CodeAnswer("words", hits, false, listed, ranked.Count, text);
            }
            if (more > 0)
            {
                more = Math.Max(0, more - Math.Max(1, (text.Length - room) / 80));
            }
            else
            {
                chosen.RemoveAt(chosen.Count - 1);
            }
        }
    }

    /// <summary>
    /// A place's lines as they are on disk: a symbol of forty lines or fewer whole; a longer one's first lines to its name, then
    /// three lines around each line holding the words; a type (a class, an interface...) its first lines and its members' names.
    /// </summary>
    private static Place? Excerpt(FileNow now, Candidate c, HashSet<string> terms, bool outline)
    {
        var secrets = 0;
        var (a, b) = RangeNow(now, c.Symbol);
        var lines = now.Lines;
        if (a < 1 || a > lines.Length)
        {
            return null;
        }
        b = Math.Min(b, lines.Length);
        var shown = new List<string>();
        var head = a;
        while (head < b && head - a < 6 && !lines[head - 1].Contains(c.Symbol.Name, StringComparison.Ordinal))
        {
            head++;
        }
        var container = Containers.Contains(c.Symbol.Kind);
        if (b - a + 1 <= WholeLines && !(container && outline))
        {
            for (var n = a; n <= b; n++)
            {
                shown.Add(Line(lines, n, ref secrets));
            }
            return new Place(c, a, b, a, b, shown, true, secrets);
        }
        if (container)
        {
            for (var n = a; n <= head; n++)
            {
                shown.Add(Line(lines, n, ref secrets));
            }
            var members = now.Scanned.Symbols.Where(s => s.Parent == c.Symbol.Qualified && s.Start > head)
                .Select(s => s.Kind is "method" or "function" or "constructor" ? $"{s.Name}() :{s.Start}" : $"{s.Name} :{s.Start}").Distinct().ToList();
            if (members.Count > 0)
            {
                var line = "  members: " + string.Join(", ", members);
                shown.Add(line.Length > 500 ? line[..500] + " …" : line);
            }
            return new Place(c, a, head, a, b, shown, false, secrets);
        }
        var keep = new SortedSet<int>(Enumerable.Range(a, head - a + 1));
        // The lines holding the question word for word first, then those holding its words.
        foreach (var hit in c.PhraseLines.Where(n => n > head && n <= b).Concat(Enumerable.Range(head + 1, b - head).Where(n => Terms.Of(lines[n - 1]).Any(terms.Contains))))
        {
            for (var n = Math.Max(a, hit - 3); n <= Math.Min(b, hit + 3) && keep.Count < WholeLines; n++)
            {
                keep.Add(n);
            }
        }
        var previous = 0;
        foreach (var n in keep)
        {
            if (previous > 0 && n > previous + 1)
            {
                shown.Add("  …");
            }
            shown.Add(Line(lines, n, ref secrets));
            previous = n;
        }
        return new Place(c, keep.Min, keep.Max, a, b, shown, false, secrets);
    }

    private static string Render(Place p, int n, List<Place> chosen, ISet<string>? shownBefore, bool edges = false)
    {
        var c = p.Candidate;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[{n}] {c.File.Path}:{p.Start}-{p.End} · {c.Symbol.Kind} {Display(c.File, c.Symbol)}");
        if (p.Start != p.SymbolStart || p.End != p.SymbolEnd)
        {
            sb.Append(CultureInfo.InvariantCulture, $" (lines {p.SymbolStart}-{p.SymbolEnd})");
        }
        if (c.Why.Count > 0)
        {
            sb.Append(" · why: ").Append(string.Join(", ", c.Why.Take(5)));
        }
        if (edges)
        {
            var calls = chosen.Select((x, i) => (x, i)).Where(x => c.Calls.Contains(x.x.Candidate.Id)).Select(x => $"[{x.i + 1}]").ToList();
            var by = chosen.Select((x, i) => (x, i)).Where(x => c.CalledBy.Contains(x.x.Candidate.Id)).Select(x => $"[{x.i + 1}]").ToList();
            if (calls.Count > 0)
            {
                sb.Append(" · calls ").Append(string.Join(' ', calls));
            }
            if (by.Count > 0)
            {
                sb.Append(" · called by ").Append(string.Join(' ', by));
            }
        }
        if (shownBefore?.Contains($"{c.File.Path}:{p.Start}-{p.End}") == true)
        {
            return sb.Append(" · shown above\n").ToString();
        }
        sb.Append('\n');
        foreach (var line in p.Lines)
        {
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>A line as read_file numbers it, cut at 300 characters, with values shaped like secrets hidden.</summary>
    private static string Line(string[] lines, int n, ref int secrets)
    {
        var text = lines[n - 1].TrimEnd('\r');
        if (text.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            secrets++;
            text = SecretHidden;
        }
        else if (CodeFiles.TokenShape().IsMatch(text))
        {
            secrets += CodeFiles.TokenShape().Count(text);
            text = CodeFiles.TokenShape().Replace(text, SecretHidden);
        }
        if (text.Length > MostLineChars)
        {
            text = text[..MostLineChars] + "…";
        }
        return n.ToString(CultureInfo.InvariantCulture).PadLeft(6) + "\t" + text;
    }

    /// <summary>A symbol's name as answers show it: qualified, less its file's namespace (Agent.RunToolsAsync, not CodeArena.Agent.RunToolsAsync).</summary>
    internal static string Display(IndexedFile file, CodeSymbol symbol)
    {
        var ns = file.Symbols.Where(s => s.Symbol.Kind == "namespace" && symbol.Qualified.StartsWith(s.Symbol.Qualified + ".", StringComparison.Ordinal))
            .Select(s => s.Symbol.Qualified).MaxBy(n => n.Length);
        return ns is null ? symbol.Qualified : symbol.Qualified[(ns.Length + 1)..];
    }

    private static string Grep(List<string> words, string? path)
    {
        var pattern = string.Join('|', words.Select(Regex.Escape));
        return $"grep \"{pattern}\"{(string.IsNullOrWhiteSpace(path) ? "" : $" path={path}")}";
    }

    // ------------------------------------------------------------------------------------------------- by name

    private sealed record Use(IndexedFile File, int Line, string Symbol);

    /// <summary>Definitions and every use in code of each name, grouped by the symbol each use is in; null when the folder knows none of them.</summary>
    private CodeAnswer? ByName(CodeQuery q, List<string> names)
    {
        var graph = _index.Graph();
        var defs = new List<SymbolRef>();
        var uses = new List<Use>();
        var hidden = 0;
        var unresolved = 0;
        var complete = true;
        foreach (var name in names)
        {
            var found = graph.Find(name).Where(r => Allowed(q, r.File)).ToList();
            hidden += found.RemoveAll(r => q.MayRead is { } may && !may(r.File.Path));
            defs.AddRange(found.OrderBy(r => r.File.Tags.HasFlag(CodeFileTags.Test) && !q.Tests));
            var simple = !name.Contains('.') && !name.Contains('#');
            if (simple)
            {
                // Every use of the name in code, as grep -w would find it outside comments and strings.
                foreach (var f in _files.Select(f => f.File).Where(f => f.Names.Contains(name) && Allowed(q, f)))
                {
                    if (q.MayRead is { } may && !may(f.Path))
                    {
                        hidden++;
                        continue;
                    }
                    if (graph.Now(f) is not { } now)
                    {
                        continue;
                    }
                    foreach (var line in now.Uses.Where(u => u.Name == name).Select(u => u.Line).Distinct())
                    {
                        uses.Add(new Use(f, line, now.Enclosing(line) is { } s ? Display(f, s) : "(top)"));
                    }
                }
            }
            else
            {
                foreach (var d in found)
                {
                    var (resolved, left) = graph.UsesOf(d);
                    unresolved += left;
                    foreach (var e in resolved.Where(e => Within(e.Path, q.Path) && (q.MayRead?.Invoke(e.Path) ?? true)))
                    {
                        if (_index.FileAt(e.Path) is { } f)
                        {
                            uses.Add(new Use(f, e.Line, e.From is { } s ? Display(f, s) : "(top)"));
                        }
                    }
                }
                complete &= unresolved == 0;
            }
        }
        if (defs.Count == 0 && uses.Count == 0)
        {
            return null;
        }
        uses = [.. uses.DistinctBy(u => (u.File.Path, u.Line)).OrderBy(u => u.File.Path, StringComparer.Ordinal).ThenBy(u => u.Line)];

        var room = (int)(q.Budget * q.CharsPerToken);
        var grep = names.Count == 1 ? $"grep -w {(names[0].Contains('.') ? $"\"{names[0]}\"" : names[0])}" : $"grep -wE \"{string.Join('|', names)}\"";
        var reserve = 200 + q.Text.Length + grep.Length;
        var body = new StringBuilder();
        var hits = new List<CodeHit>();
        var secrets = 0;
        var n = 0;
        foreach (var d in defs.Take(8))
        {
            var text = Definition(d, ++n, q.Shown, graph, ref secrets);
            if (body.Length + text.Length + reserve > room && n > 1)
            {
                n--;
                break;
            }
            body.Append(text);
            hits.Add(new CodeHit(d.File.Path, d.Symbol.Start, d.Symbol.End, d.Symbol.Kind, Display(d.File, d.Symbol), ["definition"]));
        }
        var byFile = uses.GroupBy(u => u.File).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Path, StringComparer.Ordinal).ToList();
        if (byFile.Count > 0)
        {
            body.Append("Uses, by the symbol they are in:\n");
        }
        var shownFiles = 0;
        foreach (var g in byFile)
        {
            var symbols = g.GroupBy(u => u.Symbol).Select(s => $"{s.Key} :{s.First().Line}{(s.Count() > 1 ? $" (×{s.Count()})" : "")}").ToList();
            var line = $"- {g.Key.Path} ({g.Count()}): {string.Join(", ", symbols)}";
            if (line.Length > 400)
            {
                line = line[..400] + " …";
            }
            if (body.Length + line.Length + 1 + reserve > room)
            {
                break;
            }
            body.Append(line).Append('\n');
            shownFiles++;
            hits.AddRange(g.Select(u => new CodeHit(u.File.Path, u.Line, u.Line, "use", u.Symbol, ["use"])));
        }
        if (shownFiles < byFile.Count)
        {
            var left = byFile.Skip(shownFiles).ToList();
            var tests = left.Count(g => g.Key.Tags.HasFlag(CodeFileTags.Test));
            var summary = $"- … {left.Count} more files{(tests > 0 ? $" ({tests} tests)" : "")}: " + string.Join(", ", left.Select(g => $"{g.Key.Path.Split('/')[^1]} ({g.Count()})"));
            body.Append(summary.Length > 300 ? summary[..300] + " …" : summary).Append('\n');
        }
        var notes = new List<string>();
        if (hidden > 0)
        {
            notes.Add($"{hidden} hidden by your rules");
        }
        if (secrets > 0)
        {
            notes.Add($"{secrets} secret-like {(secrets == 1 ? "value" : "values")} hidden");
        }
        if (unresolved > 0)
        {
            notes.Add($"{unresolved} more uses of the name could be it but are not tied to it for certain");
        }
        if (notes.Count > 0)
        {
            body.Append(string.Join("; ", notes)).Append(".\n");
        }
        body.Append("Every occurrence, comments too: ").Append(grep).Append('\n');
        var files = byFile.Count;
        var tokens = (int)((body.Length + 160 + q.Text.Length) / q.CharsPerToken);
        var coverage = complete ? "complete for code (comments and strings not counted)" : "the uses tied to it";
        var header = $"code_search \"{q.Text}\" · name · {defs.Count} {(defs.Count == 1 ? "definition" : "definitions")} · uses in {files} {(files == 1 ? "file" : "files")} · {coverage} · ≈{tokens:N0} of {q.Budget:N0} tokens\n";
        return new CodeAnswer("name", hits, complete && shownFiles == byFile.Count, hits.Count, defs.Count + uses.Count, header + body.ToString().TrimEnd('\n'));
    }

    /// <summary>A definition: a type with its members, else its lines (whole up to forty, else the first twenty).</summary>
    private string Definition(SymbolRef d, int n, ISet<string>? shownBefore, CodeGraph graph, ref int secrets)
    {
        _index.Refresh(d.File.Path);
        var now = graph.Now(d.File);
        var (a, b) = now is null ? (d.Symbol.Start, d.Symbol.End) : RangeNow(now, d.Symbol);
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[{n}] {d.File.Path}:{a}-{b} · {d.Symbol.Kind} {Display(d.File, d.Symbol)}");
        if (d.Symbol.Doc is { Length: > 0 } doc)
        {
            sb.Append(" · \"").Append(doc).Append('"');
        }
        var place = $"{d.File.Path}:{a}-{b}";
        if (shownBefore?.Contains(place) == true)
        {
            return sb.Append(" · shown above\n").ToString();
        }
        shownBefore?.Add(place);
        sb.Append('\n');
        var symbols = now?.Scanned.Symbols ?? [.. d.File.Symbols.Select(s => s.Symbol)];
        if (Containers.Contains(d.Symbol.Kind))
        {
            var members = symbols.Where(s => s.Parent == d.Symbol.Qualified).Select(s => s.Kind is "method" or "function" or "constructor" ? $"{s.Name}() :{s.Start}" : s.Name).Distinct().ToList();
            if (members.Count > 0)
            {
                var line = "  members: " + string.Join(", ", members);
                sb.Append(line.Length > 600 ? line[..600] + " …" : line).Append('\n');
            }
            return sb.ToString();
        }
        if (now is null)
        {
            return sb.ToString();
        }
        var lines = now.Lines;
        var last = b - a + 1 <= WholeLines ? b : a + 19;
        for (var i = a; i <= Math.Min(last, lines.Length); i++)
        {
            sb.Append(Line(lines, i, ref secrets)).Append('\n');
        }
        if (last < b)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  … {b - last} more lines (read_file {d.File.Path} offset {last + 1})\n");
        }
        return sb.ToString();
    }
}
