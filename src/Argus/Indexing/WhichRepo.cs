using System.Text.RegularExpressions;

namespace Argus.Indexing;

/// <summary>
/// What a developer handed <c>which_repo</c>, and the evidence in it
///. Detection order is load-bearing: a diff contains paths
/// and a stack trace contains symbols, so the most specific shape is tested first.
/// </summary>
public static class WhichRepo
{
    public static class Shape
    {
        public const string Diff = "diff";
        public const string Stack = "stack";
        public const string Symbol = "symbol";
        public const string Prose = "prose";
    }

    const RegexOptions M = RegexOptions.Multiline | RegexOptions.CultureInvariant;
    static readonly Regex DiffRe = new(@"^(diff --git |@@ .* @@|\+\+\+ |--- )", M);
    static readonly Regex FrameRe = new(@"(?:^|[\s(])([\w./\\-]+\.\w+):(\d+)", M);
    static readonly Regex AtFrameRe = new(@"^\s*(?:at|from|#\d+)\s+\S", M);
    static readonly Regex DiffPathRe = new(@"^diff --git a/(\S+) b/\S+", M);
    const string Ident = @"\b[A-Za-z_][A-Za-z0-9_]*(?:(?:::|\.)[A-Za-z_][A-Za-z0-9_]*)*\b";
    static readonly Regex IdentRe = new(Ident, RegexOptions.CultureInvariant);
    static readonly Regex IdentFull = new(@"\A(?:" + Ident + @")\z", RegexOptions.CultureInvariant);
    static readonly Regex PartSplit = new(@"::|\.", RegexOptions.CultureInvariant);

    static readonly HashSet<string> Stopwords = new(Util.PyStr.SplitWhitespace("""
        a an and add or the to for of in on with without is are be do does how what
        where when why which that this it its from into support change fix update
        make add new use using need want should would could can i we my our
        """), StringComparer.Ordinal);

    const string SourceExts =
        @"c|h|cc|cpp|cxx|c\+\+|hpp|hxx|hh|inl|ipp|s|asm" +
        @"|py|rs|go|java|js|ts|tsx|jsx|rb|php|cs|swift|kt|m|mm" +
        @"|md|rst|txt|json|ya?ml|toml|cmake|mk";

    static readonly Regex FileTokenRe = new(
        @"(?<![\w/.-])([\w+.-]+(?:/[\w+.-]+)*\.(?:" + SourceExts + @"))(?![\w])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string DetectShape(string text)
    {
        if (DiffRe.IsMatch(text)) return Shape.Diff;
        int frames = FrameRe.Matches(text).Count;
        if (frames >= 2 || (frames >= 1 && AtFrameRe.IsMatch(text))) return Shape.Stack;
        var stripped = Util.PyStr.Strip(text);
        if (IdentFull.IsMatch(stripped)) return Shape.Symbol;
        return Shape.Prose;
    }

    /// <summary>File paths named explicitly, in order, without duplicates.</summary>
    public static List<string> ExtractPaths(string text)
    {
        var found = DiffPathRe.Matches(text).Select(m => m.Groups[1].Value).ToList();
        if (found.Count == 0) found = FrameRe.Matches(text).Select(m => m.Groups[1].Value).ToList();
        if (found.Count == 0) found = FileTokenRe.Matches(text).Select(m => m.Groups[1].Value).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (var path in found)
        {
            var normalised = path.Replace('\\', '/');
            if (seen.Add(normalised)) output.Add(normalised);
        }
        return output;
    }

    /// <summary>Identifier-shaped tokens worth looking up, stopwords removed.</summary>
    public static List<string> ExtractSymbols(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        foreach (Match m in IdentRe.Matches(text))
        {
            var token = m.Value;
            var parts = PartSplit.Split(token);
            if (!parts.Any(p => p.Length >= 3 && !Stopwords.Contains(p.ToLowerInvariant()))) continue;
            if (seen.Add(token)) output.Add(token);
        }
        return output;
    }
}
