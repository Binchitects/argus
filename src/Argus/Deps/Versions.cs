using System.Globalization;
using System.Text.RegularExpressions;

namespace Argus.Deps;

/// <summary>
/// Versions compared as each ecosystem orders them, for matching a locked version against an advisory's ranges with no
/// model: SemVer (npm, NuGet, Go, crates.io, Packagist, Hex, Pub: numbers, then a pre-release below its release), PEP 440
/// (PyPI: epoch, release, a/b/rc, post, dev), Maven's (alpha &lt; beta &lt; milestone &lt; rc &lt; snapshot &lt; release &lt; sp)
/// and RubyGems' (a letter makes a pre-release). A version that does not read as one is not compared: null.
/// </summary>
public static partial class Versions
{
    /// <summary>A package's name as matched: lower case, and PyPI's runs of -, _ and . as one - (PEP 503).</summary>
    public static string PackageKey(string ecosystem, string name)
    {
        var key = name.Trim().ToLowerInvariant();
        return ecosystem == "PyPI" ? PyName().Replace(key, "-") : key;
    }

    [GeneratedRegex(@"[-_.]+")]
    private static partial Regex PyName();

    /// <summary>Whether the version lies in [introduced, fixed) or [introduced, last affected]; null when a version does not read.</summary>
    public static bool? InRange(string ecosystem, string version, string? introduced, string? fixedIn, string? lastAffected)
    {
        if (introduced is not (null or "0"))
        {
            if (Compare(ecosystem, version, introduced) is not { } c) return null;
            if (c < 0) return false;
        }
        if (fixedIn is not null)
            return Compare(ecosystem, version, fixedIn) is { } f ? f < 0 : null;
        if (lastAffected is not null)
            return Compare(ecosystem, version, lastAffected) is { } l ? l <= 0 : null;
        return true;
    }

    /// <summary>Negative, zero or positive as <paramref name="a"/> comes before, with or after <paramref name="b"/>; null when either does not read.</summary>
    public static int? Compare(string ecosystem, string a, string b) => ecosystem switch
    {
        "PyPI" => Pep440(a) is { } x && Pep440(b) is { } y ? CompareKeys(x, y) : null,
        "Maven" => Maven(a) is { } x && Maven(b) is { } y ? CompareKeys(x, y) : null,
        "RubyGems" => Gem(a) is { } x && Gem(b) is { } y ? CompareKeys(x, y) : null,
        _ => Semver(a) is { } x && Semver(b) is { } y ? CompareKeys(x, y) : null,
    };

    /// <summary>A comparable key: numbers as numbers (padded with zeros), strings by ordinal; a shorter key is padded with its own filler.</summary>
    sealed record Key(List<IComparable> Parts, IComparable Filler);

    static int CompareKeys(Key a, Key b)
    {
        var n = Math.Max(a.Parts.Count, b.Parts.Count);
        for (var i = 0; i < n; i++)
        {
            var x = i < a.Parts.Count ? a.Parts[i] : a.Filler;
            var y = i < b.Parts.Count ? b.Parts[i] : b.Filler;
            var c = Cmp(x, y);
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>Numbers before strings (a numeric part ranks above a word, as SemVer's and Gem's pre-releases do), then by value.</summary>
    static int Cmp(IComparable x, IComparable y) => (x, y) switch
    {
        (long l, long r) => l.CompareTo(r),
        (string l, string r) => string.CompareOrdinal(l, r),
        (Rank l, Rank r) => l.Value.CompareTo(r.Value),
        (long, _) => 1,
        (_, long) => -1,
        (Rank l, _) => l.Value.CompareTo(0),
        (_, Rank r) => -r.Value.CompareTo(0),
        _ => string.CompareOrdinal(x.ToString(), y.ToString()),
    };

    /// <summary>A part that orders by a rank (a pre-release's place, a qualifier's).</summary>
    sealed record Rank(int Value) : IComparable
    {
        public int CompareTo(object? obj) => obj is Rank r ? Value.CompareTo(r.Value) : 0;
    }

    [GeneratedRegex(@"^v?(\d+(?:\.\d+)*)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemverRe();

    /// <summary>SemVer, with any count of numbers (NuGet has four): a pre-release (after -) ranks below its release.</summary>
    static Key? Semver(string v)
    {
        var m = SemverRe().Match(v.Trim());
        if (!m.Success) return null;
        var parts = new List<IComparable>();
        var numbers = m.Groups[1].Value.Split('.').Select(s => (IComparable)long.Parse(s, CultureInfo.InvariantCulture)).ToList();
        while (numbers.Count < 4) numbers.Add(0L);
        parts.AddRange(numbers.Take(4));
        if (m.Groups[2].Success)
        {
            parts.Add(new Rank(-1));
            // Each identifier: a numeric one below a word (SemVer 11.4.3), then by value.
            foreach (var s in m.Groups[2].Value.Split('.'))
            {
                if (long.TryParse(s, out var n)) parts.AddRange([new Rank(0), n]);
                else parts.AddRange([new Rank(1), s]);
            }
        }
        else parts.Add(new Rank(0));
        return new Key(parts, new Rank(-2));
    }

    [GeneratedRegex(@"^v?(?:(\d+)!)?(\d+(?:\.\d+)*)(?:[-_.]?(a|b|c|rc|alpha|beta|pre|preview)[-_.]?(\d*))?(?:(?:-(\d+))|(?:[-_.]?(?:post|rev|r)[-_.]?(\d*)))?(?:[-_.]?dev[-_.]?(\d*))?(?:\+[a-z0-9.]+)?$")]
    private static partial Regex Pep440Re();

    /// <summary>PEP 440: epoch, release, then pre (a &lt; b &lt; rc), post and dev in their order (dev first of all).</summary>
    static Key? Pep440(string v)
    {
        var m = Pep440Re().Match(v.Trim().ToLowerInvariant());
        if (!m.Success) return null;
        var parts = new List<IComparable> { m.Groups[1].Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0L };
        var release = m.Groups[2].Value.Split('.').Select(s => (IComparable)long.Parse(s, CultureInfo.InvariantCulture)).ToList();
        while (release.Count < 6) release.Add(0L);
        parts.AddRange(release.Take(6));
        var pre = m.Groups[3].Success ? m.Groups[3].Value switch { "a" or "alpha" => 1, "b" or "beta" => 2, _ => 3 } : (int?)null;
        var preN = m.Groups[4].Value.Length > 0 ? long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0L;
        var post = m.Groups[5].Success ? long.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture)
            : m.Groups[6].Success ? (m.Groups[6].Value.Length > 0 ? long.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0L) : (long?)null;
        var dev = m.Groups[7].Success ? (m.Groups[7].Value.Length > 0 ? long.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture) : 0L) : (long?)null;
        // A dev release of the release itself (1.0.dev1) comes before its pre-releases.
        parts.Add(new Rank(pre ?? (dev is not null && post is null ? 0 : 4)));
        parts.Add(preN);
        parts.Add(post is null ? new Rank(-1) : post.Value);
        parts.Add(dev is null ? new Rank(1) : new Rank(-1));
        parts.Add(dev ?? 0L);
        return new Key(parts, 0L);
    }

    static readonly Dictionary<string, int> MavenQualifiers = new(StringComparer.Ordinal)
    {
        ["alpha"] = -5, ["a"] = -5, ["beta"] = -4, ["b"] = -4, ["milestone"] = -3, ["m"] = -3, ["rc"] = -2, ["cr"] = -2,
        ["snapshot"] = -1, [""] = 0, ["ga"] = 0, ["final"] = 0, ["release"] = 0, ["sp"] = 1,
    };

    [GeneratedRegex(@"\d+|[a-z]+")]
    private static partial Regex Tokens();

    /// <summary>
    /// Maven's order: up to four numbers (missing ones 0), then the qualifier by its rank (none: a release; an unknown one
    /// after the known ones, by name), then what follows it (beta-2's 2).
    /// </summary>
    static Key? Maven(string v)
    {
        var tokens = Tokens().Matches(v.Trim().ToLowerInvariant()).Select(m => m.Value).ToList();
        if (tokens.Count == 0 || !char.IsDigit(tokens[0][0])) return null;
        var numbers = tokens.TakeWhile(t => char.IsDigit(t[0])).Take(4).Select(t => (IComparable)long.Parse(t, CultureInfo.InvariantCulture)).ToList();
        var rest = tokens.Skip(numbers.Count).ToList();
        while (numbers.Count < 4) numbers.Add(0L);
        var parts = new List<IComparable>(numbers);
        var qualifier = rest.FirstOrDefault(t => !char.IsDigit(t[0])) ?? "";
        if (MavenQualifiers.TryGetValue(qualifier, out var rank)) parts.Add(new Rank(rank));
        else parts.AddRange([new Rank(2), qualifier]);
        parts.AddRange(rest.Where(t => t != qualifier).Select(t => char.IsDigit(t[0]) ? (IComparable)long.Parse(t, CultureInfo.InvariantCulture) : t));
        return new Key(parts, 0L);
    }

    [GeneratedRegex(@"^\d+[0-9a-z.]*$")]
    private static partial Regex GemRe();

    /// <summary>RubyGems: segments split at dots and between digits and letters; a letter makes a pre-release, below its release.</summary>
    static Key? Gem(string v)
    {
        var s = v.Trim().ToLowerInvariant().Replace('-', '.');
        if (!GemRe().IsMatch(s)) return null;
        var parts = Tokens().Matches(s).Select(m => char.IsDigit(m.Value[0]) ? (IComparable)long.Parse(m.Value, CultureInfo.InvariantCulture) : m.Value).ToList();
        while (parts.Count > 1 && parts[^1] is 0L) parts.RemoveAt(parts.Count - 1);
        return new Key(parts, 0L);
    }
}
