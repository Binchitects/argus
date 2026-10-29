using System.Globalization;
using System.Text;

namespace CodeArena;

/// <summary>A question about one symbol: its source, its callers, its callees, or what reaches it (impact), steps out.</summary>
internal sealed record SymbolQuery(string Name)
{
    /// <summary>source (its code, its members and who calls it), callers, callees, or impact.</summary>
    public string Show { get; init; } = "source";
    public int Depth { get; init; } = 2;
    public int Budget { get; init; } = 1500;
    public double CharsPerToken { get; init; } = 3.5;
    public Func<string, bool>? MayRead { get; init; }
    public ISet<string>? Shown { get; init; }
}

internal sealed partial class CodeSearch
{
    private const int MostImpact = 200;

    /// <summary>One symbol's source, callers, callees or impact; its candidates (path#Qualified) when the name is not one symbol's.</summary>
    public CodeAnswer Symbol(SymbolQuery q)
    {
        var graph = _index.Graph();
        var found = graph.Find(q.Name).Where(r => !r.File.Tags.HasFlag(CodeFileTags.Sensitive)).ToList();
        var hidden = found.RemoveAll(r => q.MayRead is { } may && !may(r.File.Path));
        var head = $"code_symbol {q.Name}";
        if (found.Count == 0)
        {
            return Said($"{head} · no symbol of that name in the index{(hidden > 0 ? $" ({hidden} hidden by your rules)" : "")}. code_search finds code by words; grep finds text.");
        }
        if (found.Count > 1)
        {
            var list = string.Join('\n', found.Take(20).Select(r => $"- {r.Id} · {r.Symbol.Kind} · {r.File.Path}:{r.Symbol.Start}"));
            return new CodeAnswer("symbol", [.. found.Select(r => new CodeHit(r.File.Path, r.Symbol.Start, r.Symbol.End, r.Symbol.Kind, Display(r.File, r.Symbol), ["candidate"]))], false, Math.Min(20, found.Count), found.Count,
                $"{head} · {found.Count} symbols have that name: name one as path#Qualified.\n{list}{(found.Count > 20 ? $"\n- … {found.Count - 20} more" : "")}");
        }
        var target = found[0];
        var room = (int)(q.Budget * q.CharsPerToken);
        var body = new StringBuilder();
        var hits = new List<CodeHit>();
        var asked = q.Name.Contains('#') ? q.Name[(q.Name.IndexOf('#') + 1)..] : q.Name.Trim().TrimEnd('(', ')');
        var grep = $"grep -w {(asked.Contains('.') ? $"\"{asked}\"" : asked)}";
        string show;
        switch (q.Show)
        {
            case "callers":
            {
                show = "callers";
                var (uses, unresolved) = graph.UsesOf(target);
                uses = [.. uses.Where(u => q.MayRead?.Invoke(u.Path) ?? true)];
                foreach (var g in uses.GroupBy(u => (u.Path, u.From?.Qualified)).OrderBy(g => g.Key.Path, StringComparer.Ordinal).ThenBy(g => g.First().Line))
                {
                    var first = g.First();
                    var at = string.Join(", ", g.Select(u => $":{u.Line}").Distinct().Take(6));
                    var line = $"- {Where(first.Path, first.From)} · calls it at {at} ({EvidenceOf(first.Evidence)})\n";
                    if (body.Length + line.Length + 300 > room)
                    {
                        body.Append(CultureInfo.InvariantCulture, $"- … and more: {uses.Count} uses in all\n");
                        break;
                    }
                    body.Append(line);
                    hits.Add(new CodeHit(first.Path, first.From?.Start ?? first.Line, first.From?.End ?? first.Line, first.From?.Kind ?? "file", first.From is { } f ? DisplayAt(first.Path, f) : "(top)", ["caller"]));
                }
                if (uses.Count == 0)
                {
                    body.Append("No use in code is tied to it.\n");
                }
                body.Append(Unresolved(unresolved, target.Symbol.Name));
                break;
            }
            case "callees":
            {
                show = "callees";
                var edges = graph.UsesFrom(target).Where(e => e.To.All(t => q.MayRead?.Invoke(t.File.Path) ?? true)).ToList();
                foreach (var g in edges.GroupBy(e => e.To[0].Id).OrderBy(g => g.First().Line))
                {
                    var to = g.First().To[0];
                    var also = g.First().To.Count > 1 ? $" or {string.Join(", ", g.First().To.Skip(1).Select(t => Display(t.File, t.Symbol)))}" : "";
                    var line = $"- {to.File.Path}:{to.Symbol.Start}-{to.Symbol.End} · {to.Symbol.Kind} {Display(to.File, to.Symbol)}{also} · at {string.Join(", ", g.Select(e => $":{e.Line}").Distinct().Take(6))} ({EvidenceOf(g.First().Evidence)})\n";
                    if (body.Length + line.Length + 300 > room)
                    {
                        body.Append("- … and more\n");
                        break;
                    }
                    body.Append(line);
                    hits.Add(new CodeHit(to.File.Path, to.Symbol.Start, to.Symbol.End, to.Symbol.Kind, Display(to.File, to.Symbol), ["callee"]));
                }
                if (edges.Count == 0)
                {
                    body.Append("It uses nothing else declared in the folder.\n");
                }
                break;
            }
            case "impact":
            {
                var depth = Math.Clamp(q.Depth, 1, 3);
                show = $"impact, {depth} {(depth == 1 ? "step" : "steps")}";
                var seen = new HashSet<string>(StringComparer.Ordinal) { target.Id };
                var frontier = new List<SymbolRef> { target };
                var tests = new List<string>();
                var unresolved = 0;
                var total = 0;
                var files = new HashSet<string>(StringComparer.Ordinal);
                for (var step = 1; step <= depth && frontier.Count > 0 && total < MostImpact; step++)
                {
                    var next = new List<SymbolRef>();
                    var lines = new StringBuilder();
                    foreach (var s in frontier)
                    {
                        var (uses, left) = graph.UsesOf(s);
                        unresolved += left;
                        foreach (var u in uses.Where(u => u.From is not null && (q.MayRead?.Invoke(u.Path) ?? true)))
                        {
                            var file = _index.FileAt(u.Path);
                            if (file is null || !seen.Add($"{u.Path}#{u.From!.Qualified}") || total >= MostImpact)
                            {
                                continue;
                            }
                            total++;
                            files.Add(u.Path);
                            var from = new SymbolRef(file, u.From);
                            next.Add(from);
                            var name = Display(file, u.From);
                            if (file.Tags.HasFlag(CodeFileTags.Test))
                            {
                                tests.Add(name);
                            }
                            lines.Append(CultureInfo.InvariantCulture, $"- {Where(u.Path, u.From)} · calls {(step == 1 ? "it" : s.Symbol.Name)} at :{u.Line} ({EvidenceOf(u.Evidence)})\n");
                            hits.Add(new CodeHit(u.Path, u.From.Start, u.From.End, u.From.Kind, name, [$"step {step}"]));
                        }
                    }
                    if (lines.Length > 0)
                    {
                        if (body.Length + lines.Length + 300 > room)
                        {
                            body.Append(CultureInfo.InvariantCulture, $"Step {step}: {next.Count} symbols (more than the budget shows)\n");
                            break;
                        }
                        body.Append(CultureInfo.InvariantCulture, $"Step {step}\n").Append(lines);
                    }
                    frontier = next;
                }
                if (total == 0)
                {
                    body.Append("Nothing in the folder's code is tied to it.\n");
                }
                body.Append(CultureInfo.InvariantCulture, $"Tests within {depth} {(depth == 1 ? "step" : "steps")}: {(tests.Count > 0 ? string.Join(", ", tests.Distinct().Take(10)) : "none")}.\n");
                body.Append(Unresolved(unresolved, target.Symbol.Name));
                head += $" · {show} · {total} symbols in {files.Count} {(files.Count == 1 ? "file" : "files")}";
                show = "";
                break;
            }
            default:
            {
                show = "source";
                var secrets = 0;
                body.Append(Definition(target, 1, q.Shown, graph, ref secrets));
                hits.Add(new CodeHit(target.File.Path, target.Symbol.Start, target.Symbol.End, target.Symbol.Kind, Display(target.File, target.Symbol), ["definition"]));
                var (uses, unresolved) = graph.UsesOf(target);
                var callers = uses.Where(u => q.MayRead?.Invoke(u.Path) ?? true).GroupBy(u => (u.Path, u.From?.Qualified)).Select(g => g.First()).ToList();
                if (callers.Count > 0)
                {
                    var line = "Called by: " + string.Join(", ", callers.Take(12).Select(u => $"{(u.From is { } f ? DisplayAt(u.Path, f) : u.Path)} :{u.Line}"))
                        + (callers.Count > 12 ? $", … {callers.Count - 12} more (show: callers)" : "") + "\n";
                    body.Append(line);
                }
                else
                {
                    body.Append("Called by: nothing in the folder's code tied to it.\n");
                }
                body.Append(Unresolved(unresolved, target.Symbol.Name));
                if (secrets > 0)
                {
                    body.Append(CultureInfo.InvariantCulture, $"{secrets} secret-like {(secrets == 1 ? "value" : "values")} hidden.\n");
                }
                break;
            }
        }
        body.Append("Every line naming it: ").Append(grep);
        if (show.Length > 0)
        {
            head += $" · {show}";
        }
        var text = $"{head} · ≈{{0}} of {q.Budget:N0} tokens\n{body}";
        text = text.Replace("{0}", ((int)Math.Ceiling(text.Length / q.CharsPerToken)).ToString("N0", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return new CodeAnswer("symbol", hits, false, hits.Count, hits.Count, text);
    }

    private static CodeAnswer Said(string text) => new("symbol", [], false, 0, 0, text);

    private static string EvidenceOf(Evidence? e) => e?.ToString().ToLowerInvariant() ?? "unresolved";

    private static string Unresolved(int n, string name) => n > 0
        ? $"Unresolved: {n} more {(n == 1 ? "use" : "uses")} named {name} could be it but are not tied to it for certain.\n"
        : $"Unresolved: no other use named {name} is left unresolved in these files.\n";

    private string Where(string path, CodeSymbol? symbol) => symbol is null ? $"{path} (top of the file)" : $"{path}:{symbol.Start}-{symbol.End} · {symbol.Kind} {DisplayAt(path, symbol)}";

    private string DisplayAt(string path, CodeSymbol symbol) => _index.FileAt(path) is { } file ? Display(file, symbol) : symbol.Qualified;

    // ------------------------------------------------------------------------------------------------- outlines

    /// <summary>
    /// A file's symbols, nested, each with its lines and its first line; or a folder's: its folders with their files and
    /// symbols, then the files most used from elsewhere, each with its main types and functions; up to the budget.
    /// </summary>
    public CodeAnswer Outline(string? path, int budget = 1200, double charsPerToken = 3.5, Func<string, bool>? mayRead = null)
    {
        var room = (int)(budget * charsPerToken);
        var target = string.IsNullOrWhiteSpace(path) || path is "." or "./" ? "" : path.Replace('\\', '/').Trim('/');
        target = target.StartsWith("./", StringComparison.Ordinal) ? target[2..] : target;
        var body = new StringBuilder();
        var hits = new List<CodeHit>();
        string head;
        if (_index.FileAt(target) is { } file)
        {
            if (file.Tags.HasFlag(CodeFileTags.Sensitive) || (mayRead is { } may && !may(file.Path)))
            {
                return new CodeAnswer("outline", [], false, 0, 0, $"code_outline {target} · {(file.Tags.HasFlag(CodeFileTags.Sensitive) ? "a file that may hold secrets: never read into the index" : "hidden by your rules")}.");
            }
            _index.Refresh(file.Path);
            var symbols = _index.Graph().Now(file)?.Scanned.Symbols ?? [.. file.Symbols.Select(s => s.Symbol)];
            var depthOf = new Dictionary<string, int>(StringComparer.Ordinal);
            var shown = 0;
            foreach (var s in symbols)
            {
                var depth = s.Parent is { } p && depthOf.TryGetValue(p, out var d) ? d + 1 : 0;
                depthOf.TryAdd(s.Qualified, depth);
                var signature = s.Signature.Length > 120 ? s.Signature[..120] + "…" : s.Signature;
                var line = $"{new string(' ', depth * 2)}{(s.Start == s.End ? $"{s.Start}" : $"{s.Start}-{s.End}")} {signature}\n";
                if (body.Length + line.Length + 200 > room)
                {
                    body.Append(CultureInfo.InvariantCulture, $"… {symbols.Count - shown} more symbols (code_outline with a bigger budget, or read_file)\n");
                    break;
                }
                body.Append(line);
                shown++;
                hits.Add(new CodeHit(file.Path, s.Start, s.End, s.Kind, Display(file, s), ["outline"]));
            }
            if (symbols.Count == 0)
            {
                body.Append(file.Lang is null ? "Text only: no symbols are read in it.\n" : "No symbols.\n");
            }
            head = $"code_outline {file.Path} · {file.Lines:N0} lines · {symbols.Count} symbols";
        }
        else
        {
            var under = _files.Select(f => f.File).Where(f => target.Length == 0 || f.Path.StartsWith(target + "/", StringComparison.Ordinal)).ToList();
            if (under.Count == 0)
            {
                return new CodeAnswer("outline", [], false, 0, 0, $"code_outline {target} · no file of the index is there. glob lists files; the index covers what grep reads.");
            }
            var hiddenFiles = under.RemoveAll(f => mayRead is { } may && !may(f.Path));
            var depth = target.Length == 0 ? 0 : target.Count(c => c == '/') + 1;
            var folders = under.Where(f => f.Path.Count(c => c == '/') > depth).GroupBy(f => f.Path.Split('/')[depth])
                .Select(g => $"{g.Key}/ ({g.Count()} files, {g.Sum(f => f.Symbols.Count):N0} symbols)").ToList();
            if (folders.Count > 0)
            {
                var line = "Folders: " + string.Join(", ", folders);
                body.Append(line.Length > 800 ? line[..800] + " …" : line).Append('\n');
            }
            // The files most used from elsewhere: how many other files use the names they declare at their top.
            var users = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in _files.Select(f => f.File))
            {
                foreach (var n in f.Names)
                {
                    users[n] = users.GetValueOrDefault(n) + 1;
                }
            }
            var ranked = under.Where(f => f.Symbols.Count > 0 && !f.Tags.HasFlag(CodeFileTags.Sensitive))
                .Select(f => (File: f, Used: Tops(f).Sum(s => Math.Max(0, users.GetValueOrDefault(s.Name) - (f.Names.Contains(s.Name) ? 1 : 0)))))
                .OrderByDescending(x => x.Used).ThenBy(x => x.File.Path, StringComparer.Ordinal).ToList();
            body.Append("Files most used from elsewhere:\n");
            var listed = 0;
            foreach (var (f, _) in ranked)
            {
                var main = Tops(f).Take(8).Select(s => Containers.Contains(s.Kind)
                    ? $"{s.Kind} {s.Name}{(f.Symbols.Count(m => m.Symbol.Parent == s.Qualified) is var members and > 0 ? $" ({members} {(members == 1 ? "member" : "members")})" : "")}"
                    : $"{s.Kind} {s.Name}");
                var line = $"- {f.Path} ({f.Lines:N0} lines) · {string.Join(", ", main)}\n";
                if (body.Length + line.Length + 200 > room)
                {
                    break;
                }
                body.Append(line);
                listed++;
                hits.Add(new CodeHit(f.Path, 1, f.Lines, "file", f.Path, ["outline"]));
            }
            if (listed < ranked.Count)
            {
                body.Append(CultureInfo.InvariantCulture, $"… {ranked.Count - listed} more files (a deeper folder, or a bigger budget)\n");
            }
            if (hiddenFiles > 0)
            {
                body.Append(CultureInfo.InvariantCulture, $"{hiddenFiles} files hidden by your rules.\n");
            }
            head = $"code_outline {(target.Length == 0 ? "." : target)} · folder · {under.Count:N0} files, {under.Sum(f => f.Symbols.Count):N0} symbols";
        }
        var text = $"{head} · ≈{{0}} of {budget:N0} tokens\n{body.ToString().TrimEnd('\n')}";
        text = text.Replace("{0}", ((int)Math.Ceiling(text.Length / charsPerToken)).ToString("N0", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return new CodeAnswer("outline", hits, true, hits.Count, hits.Count, text);
    }

    /// <summary>A file's own top-level symbols (its namespaces' members, not the namespaces).</summary>
    private static IEnumerable<CodeSymbol> Tops(IndexedFile f)
    {
        var namespaces = f.Symbols.Where(s => s.Symbol.Kind == "namespace").Select(s => s.Symbol.Qualified).ToHashSet(StringComparer.Ordinal);
        return f.Symbols.Select(s => s.Symbol).Where(s => s.Kind is not ("namespace" or "heading") && (s.Parent is null || namespaces.Contains(s.Parent)));
    }
}

/// <summary>A symbol a name search found, for the IDE's Go to Symbol: what it is, what holds it, and where.</summary>
internal sealed record SymbolMatch(string Name, string Kind, string? Container, string Path, int Line, int End);

internal sealed partial class CodeSearch
{
    private static readonly string[] Unlisted = ["namespace", "heading", "main"];

    /// <summary>
    /// Symbols by name, best first: the name itself, then those it starts, then those whose parts it abbreviates (rta, RunTA:
    /// RunToolsAsync), then those holding it; code before tests and generated code, then the shorter path. With a path, that
    /// file's symbols as it is now, in order, those matching the query if one is given.
    /// </summary>
    public List<SymbolMatch> Symbols(string query, string? path = null, int limit = 100)
    {
        var q = query.Trim();
        if (path is not null)
        {
            if (_index.FileAt(path) is not { } file || file.Tags.HasFlag(CodeFileTags.Sensitive))
            {
                return [];
            }
            _index.Refresh(path);
            var symbols = _index.Graph().Now(file)?.Scanned.Symbols ?? [.. file.Symbols.Select(s => s.Symbol)];
            return [.. symbols.Where(s => !Unlisted.Contains(s.Kind) && (q.Length == 0 || NameScore(s.Name, q) > 0)).Take(limit)
                .Select(s => new SymbolMatch(s.Name, s.Kind, s.Parent is { } p ? Display(file, s with { Qualified = p }) : null, file.Path, s.Start, s.End))];
        }
        if (q.Length == 0)
        {
            return [];
        }
        var found = new List<(int Score, IndexedFile File, CodeSymbol Symbol)>();
        foreach (var (file, s, _) in _symbols)
        {
            if (Unlisted.Contains(s.Symbol.Kind) || NameScore(s.Symbol.Name, q) is not (> 0 and var score))
            {
                continue;
            }
            found.Add((score, file, s.Symbol));
        }
        return [.. found.OrderByDescending(f => f.Score)
            .ThenBy(f => f.File.Tags.HasFlag(CodeFileTags.Test) || f.File.Tags.HasFlag(CodeFileTags.Generated) || f.File.Tags.HasFlag(CodeFileTags.Vendored))
            .ThenBy(f => f.File.Path.Length).ThenBy(f => f.File.Path, StringComparer.Ordinal).ThenBy(f => f.Symbol.Start)
            .DistinctBy(f => (f.File.Path, f.Symbol.Qualified)).Take(limit)
            .Select(f => new SymbolMatch(f.Symbol.Name, f.Symbol.Kind, f.Symbol.Parent is { } p ? Display(f.File, f.Symbol with { Qualified = p }) : null, f.File.Path, f.Symbol.Start, f.Symbol.End))];
    }

    /// <summary>How well a name matches what was typed: 100 the name, 80 its start, 60 its parts abbreviated, 40 inside it; 0 not at all.</summary>
    internal static int NameScore(string name, string typed)
    {
        if (name.Equals(typed, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }
        if (name.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }
        if (typed.Length <= 24 && Humps(Parts(name), 0, typed.ToLowerInvariant(), 0))
        {
            return 60;
        }
        return name.Contains(typed, StringComparison.OrdinalIgnoreCase) ? 40 : 0;
    }

    /// <summary>A name's parts, lower case, where its case changes, an underscore or a digit: RunToolsAsync is run, tools, async.</summary>
    private static List<string> Parts(string name)
    {
        var parts = new List<string>();
        var b = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var boundary = c is '_' or '-' or '$' || (i > 0 && char.IsUpper(c) && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                || (i > 0 && char.IsDigit(c) != char.IsDigit(name[i - 1]));
            if (boundary && b.Length > 0)
            {
                parts.Add(b.ToString());
                b.Clear();
            }
            if (c is not ('_' or '-' or '$'))
            {
                b.Append(char.ToLowerInvariant(c));
            }
        }
        if (b.Length > 0)
        {
            parts.Add(b.ToString());
        }
        return parts;
    }

    /// <summary>Whether the typed letters are the starts of the parts, in order, the first part's from the first letter: rta, runta, rtasync.</summary>
    private static bool Humps(List<string> parts, int part, string typed, int at)
    {
        if (at == typed.Length)
        {
            return true;
        }
        if (part == parts.Count)
        {
            return false;
        }
        for (var n = Math.Min(parts[part].Length, typed.Length - at); n >= 1; n--)
        {
            if (string.CompareOrdinal(parts[part], 0, typed, at, n) == 0 && Humps(parts, part + 1, typed, at + n))
            {
                return true;
            }
        }
        // A part left out (rasync: Run…Async), but never the first.
        return part > 0 && Humps(parts, part + 1, typed, at);
    }
}
