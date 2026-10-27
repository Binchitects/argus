using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Argus.Deps;

/// <summary>
/// One package a repository uses: its ecosystem (OSV's names: npm, NuGet, PyPI, Maven, Go, crates.io, RubyGems,
/// Packagist), name and version (null when a manifest gives a range, not a version), whether the repository asks for it
/// itself or it comes in through another (<see cref="Via"/>), and the file and line that say so.
/// </summary>
public sealed record Dependency(string Ecosystem, string Name, string? Version, bool Direct, string? Via, string File, int Line);

/// <summary>
/// The lock files and manifests deps_check reads, and what each says: npm's package-lock.json, yarn.lock and
/// pnpm-lock.yaml; NuGet's packages.lock.json and PackageReference/PackageVersion; pip requirements, poetry.lock, uv.lock
/// and Pipfile.lock; go.mod and go.sum; Cargo.lock; Maven's pom.xml and Gradle's build files; Gemfile.lock;
/// composer.lock. A lock file is preferred to its manifest (its versions are the ones built); a manifest's version is
/// taken only when it is exact.
/// </summary>
public static partial class Manifests
{
    /// <summary>Whether deps_check reads the file, by its name.</summary>
    public static bool Reads(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name is "package-lock.json" or "npm-shrinkwrap.json" or "yarn.lock" or "pnpm-lock.yaml" or "packages.lock.json" or "Directory.Packages.props"
                   or "poetry.lock" or "uv.lock" or "Pipfile.lock" or "go.mod" or "go.sum" or "Cargo.lock" or "pom.xml" or "build.gradle" or "build.gradle.kts"
                   or "Gemfile.lock" or "composer.lock"
               || name.EndsWith(".csproj", StringComparison.Ordinal) || name.EndsWith(".fsproj", StringComparison.Ordinal)
               || (name.StartsWith("requirements", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.Ordinal));
    }

    /// <summary>Third-party code checked in (node_modules, vendor): its manifests are not the repository's.</summary>
    [GeneratedRegex(@"(^|/)(node_modules|vendor|third_party|bower_components|\.git)/")]
    public static partial Regex Vendored();

    public static IEnumerable<Dependency> Read(string path, string text)
    {
        var name = System.IO.Path.GetFileName(path);
        try
        {
            return name switch
            {
                "package-lock.json" or "npm-shrinkwrap.json" => NpmLock(path, text),
                "yarn.lock" => YarnLock(path, text),
                "pnpm-lock.yaml" => PnpmLock(path, text),
                "packages.lock.json" => NuGetLock(path, text),
                "poetry.lock" or "uv.lock" or "Cargo.lock" => TomlPackages(path, text, name == "Cargo.lock" ? "crates.io" : "PyPI"),
                "Pipfile.lock" => PipfileLock(path, text),
                "go.mod" => GoMod(path, text),
                "go.sum" => GoSum(path, text),
                "pom.xml" => Pom(path, text),
                "build.gradle" or "build.gradle.kts" => Gradle(path, text),
                "Gemfile.lock" => GemfileLock(path, text),
                "composer.lock" => ComposerLock(path, text),
                _ when name.StartsWith("requirements", StringComparison.Ordinal) => Requirements(path, text),
                _ => MsBuild(path, text),
            };
        }
        catch (Exception exc) when (exc is JsonException or System.Xml.XmlException or InvalidOperationException or FormatException)
        {
            // A file that does not read as its kind says nothing; the others still count.
            return [];
        }
    }

    static int LineOf(string text, int index) => index < 0 ? 0 : text.AsSpan(0, Math.Min(index, text.Length)).Count('\n') + 1;

    static int LineOf(string text, string needle) => LineOf(text, text.IndexOf(needle, StringComparison.Ordinal));

    /// <summary>package-lock.json v2/v3 (its "packages", by node_modules path) and v1 (its nested "dependencies").</summary>
    static IEnumerable<Dependency> NpmLock(string path, string text)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new FormatException();
        var direct = new HashSet<string>(StringComparer.Ordinal);
        if (root["packages"]?[""] is JsonObject self)
            foreach (var group in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
                foreach (var d in self[group] as JsonObject ?? [])
                    direct.Add(d.Key);
        var found = new List<Dependency>();
        if (root["packages"] is JsonObject packages)
        {
            // Who requires whom: a package's own "dependencies" names those it brings in.
            var requiredBy = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in packages)
            {
                var owner = NodeName(key);
                foreach (var d in value?["dependencies"] as JsonObject ?? [])
                    if (owner is not null) requiredBy.TryAdd(d.Key, owner);
            }
            foreach (var (key, value) in packages)
            {
                if (NodeName(key) is not { } pkg || value?["version"]?.ToString() is not { } version || value["link"]?.GetValue<bool>() == true) continue;
                var isDirect = direct.Contains(pkg) && !key.Contains("/node_modules/", StringComparison.Ordinal);
                found.Add(new Dependency("npm", pkg, version, isDirect, isDirect ? null : requiredBy.GetValueOrDefault(pkg), path, LineOf(text, $"\"{key}\"")));
            }
            return found;
        }
        void Walk(JsonObject? deps, string? parent)
        {
            foreach (var (pkg, value) in deps ?? [])
            {
                if (value?["version"]?.ToString() is { } version)
                    found.Add(new Dependency("npm", pkg, version, parent is null, parent, path, LineOf(text, $"\"{pkg}\"")));
                Walk(value?["dependencies"] as JsonObject, pkg);
            }
        }
        Walk(root["dependencies"] as JsonObject, null);
        return found;
    }

    /// <summary>The package a node_modules path names (the last node_modules/ part, with its @scope); null for the root.</summary>
    static string? NodeName(string key)
    {
        var at = key.LastIndexOf("node_modules/", StringComparison.Ordinal);
        return at < 0 ? null : key[(at + "node_modules/".Length)..];
    }

    [GeneratedRegex(@"^""?((?:@[^@/\s""]+/)?[^@\s""]+)@[^\n]*:\s*\n(?:[ \t]+[^\n]*\n)*?[ \t]+version:?\s+""?([^""\s]+)""?", RegexOptions.Multiline)]
    private static partial Regex YarnEntry();

    /// <summary>yarn.lock, classic (version "1.2.3") and Berry (version: 1.2.3).</summary>
    static IEnumerable<Dependency> YarnLock(string path, string text) =>
        YarnEntry().Matches(text).Select(m => new Dependency("npm", m.Groups[1].Value, m.Groups[2].Value, false, null, path, LineOf(text, m.Index)))
            .DistinctBy(d => (d.Name, d.Version));

    [GeneratedRegex(@"^  ['""]?/?((?:@[^@/\s'""]+/)?[^@/\s'""(]+)[@/](\d[^\s'""():(]*)", RegexOptions.Multiline)]
    private static partial Regex PnpmEntry();

    /// <summary>pnpm-lock.yaml: its packages (/name/1.2.3 or name@1.2.3, maybe with a (peer) suffix).</summary>
    static IEnumerable<Dependency> PnpmLock(string path, string text)
    {
        var at = text.IndexOf("\npackages:", StringComparison.Ordinal);
        var end = at < 0 ? -1 : text.IndexOf("\nsnapshots:", at + 1, StringComparison.Ordinal);
        var section = at < 0 ? "" : end < 0 ? text[at..] : text[at..end];
        return PnpmEntry().Matches(section).Select(m => new Dependency("npm", m.Groups[1].Value, m.Groups[2].Value, false, null, path, LineOf(text, at + m.Index)))
            .DistinctBy(d => (d.Name, d.Version));
    }

    /// <summary>NuGet's packages.lock.json: each framework's packages, Direct or Transitive, resolved.</summary>
    static IEnumerable<Dependency> NuGetLock(string path, string text)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new FormatException();
        var found = new Dictionary<string, Dependency>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, framework) in root["dependencies"] as JsonObject ?? [])
        {
            var requiredBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (pkg, value) in framework as JsonObject ?? [])
                foreach (var d in value?["dependencies"] as JsonObject ?? [])
                    requiredBy.TryAdd(d.Key, pkg);
            foreach (var (pkg, value) in framework as JsonObject ?? [])
            {
                var type = value?["type"]?.ToString();
                if (type == "Project" || value?["resolved"]?.ToString() is not { } version) continue;
                var direct = type == "Direct";
                found.TryAdd($"{pkg}@{version}", new Dependency("NuGet", pkg, version, direct, direct ? null : requiredBy.GetValueOrDefault(pkg), path, LineOf(text, $"\"{pkg}\"")));
            }
        }
        return found.Values;
    }

    /// <summary>A project file's PackageReference, or Directory.Packages.props's PackageVersion, with an exact version.</summary>
    static IEnumerable<Dependency> MsBuild(string path, string text)
    {
        var doc = XDocument.Parse(text);
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName is "PackageReference" or "PackageVersion"))
        {
            var pkg = (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update");
            var version = (string?)e.Attribute("Version") ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value;
            if (pkg is null) continue;
            yield return new Dependency("NuGet", pkg, Exact(version?.Trim('[', ']')), true, null, path, LineOf(text, $"\"{pkg}\""));
        }
    }

    [GeneratedRegex(@"^\s*([A-Za-z0-9][A-Za-z0-9._-]*)(?:\[[^\]]*\])?\s*(==|===|>=|~=|<=|>|<|!=)?\s*([^\s;#,]+)?", RegexOptions.Multiline)]
    private static partial Regex Requirement();

    /// <summary>pip requirements: a version only when pinned (==).</summary>
    static IEnumerable<Dependency> Requirements(string path, string text)
    {
        foreach (Match m in Requirement().Matches(text))
        {
            var line = text[m.Index..].Split('\n')[0].TrimStart();
            if (line.StartsWith('-') || line.StartsWith('#') || line.Contains("://", StringComparison.Ordinal)) continue;
            var pinned = m.Groups[2].Value is "==" or "===" ? m.Groups[3].Value : null;
            yield return new Dependency("PyPI", m.Groups[1].Value, pinned, true, null, path, LineOf(text, m.Index));
        }
    }

    [GeneratedRegex(@"\[\[package\]\]\s*\nname\s*=\s*""([^""]+)""\s*\nversion\s*=\s*""([^""]+)""")]
    private static partial Regex TomlPackage();

    /// <summary>poetry.lock, uv.lock and Cargo.lock: their [[package]] tables' name and version.</summary>
    static IEnumerable<Dependency> TomlPackages(string path, string text, string ecosystem) =>
        TomlPackage().Matches(text.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Select(m => new Dependency(ecosystem, m.Groups[1].Value, m.Groups[2].Value, false, null, path, LineOf(text, m.Index)));

    /// <summary>Pipfile.lock: its default and develop packages, pinned (==1.2.3).</summary>
    static IEnumerable<Dependency> PipfileLock(string path, string text)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new FormatException();
        foreach (var group in new[] { "default", "develop" })
            foreach (var (pkg, value) in root[group] as JsonObject ?? [])
                if (value?["version"]?.ToString() is { } v && v.StartsWith("==", StringComparison.Ordinal))
                    yield return new Dependency("PyPI", pkg, v[2..], true, null, path, LineOf(text, $"\"{pkg}\""));
    }

    [GeneratedRegex(@"^\s*(?:require\s+)?([a-z0-9][^\s]*\.[^\s]+)\s+(v[0-9][^\s]*)(\s*//\s*indirect)?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoRequire();

    /// <summary>go.mod's requirements: direct, or marked // indirect.</summary>
    static IEnumerable<Dependency> GoMod(string path, string text) =>
        GoRequire().Matches(text).Where(m => !text[..m.Index].TrimEnd().EndsWith("replace", StringComparison.Ordinal))
            .Select(m => new Dependency("Go", m.Groups[1].Value, m.Groups[2].Value, !m.Groups[3].Success, null, path, LineOf(text, m.Index)));

    /// <summary>go.sum: every module version the build may use (its /go.mod lines folded in).</summary>
    static IEnumerable<Dependency> GoSum(string path, string text) =>
        text.Split('\n').Select((l, i) => (Parts: l.Split(' ', StringSplitOptions.RemoveEmptyEntries), Line: i + 1)).Where(x => x.Parts.Length >= 2)
            .Select(x => new Dependency("Go", x.Parts[0], x.Parts[1].Replace("/go.mod", "", StringComparison.Ordinal), false, null, path, x.Line))
            .DistinctBy(d => (d.Name, d.Version));

    /// <summary>pom.xml's dependencies (groupId:artifactId), their version exact or a property the pom sets.</summary>
    static IEnumerable<Dependency> Pom(string path, string text)
    {
        var doc = XDocument.Parse(text);
        var properties = doc.Descendants().Where(e => e.Name.LocalName == "properties").SelectMany(e => e.Elements())
            .GroupBy(e => e.Name.LocalName).ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.Ordinal);
        foreach (var d in doc.Descendants().Where(e => e.Name.LocalName == "dependency"))
        {
            string? Child(string n) => d.Elements().FirstOrDefault(x => x.Name.LocalName == n)?.Value.Trim();
            if (Child("groupId") is not { } group || Child("artifactId") is not { } artifact) continue;
            var version = Child("version");
            if (version is not null && PomProperty().Match(version) is { Success: true } m) version = properties.GetValueOrDefault(m.Groups[1].Value);
            yield return new Dependency("Maven", $"{group}:{artifact}", Exact(version), Child("scope") != "import", null, path, LineOf(text, $"<artifactId>{artifact}</artifactId>"));
        }
    }

    [GeneratedRegex(@"^\$\{([^}]+)\}$")]
    private static partial Regex PomProperty();

    [GeneratedRegex(@"[""']([\w.-]+):([\w.-]+):([\w.+-]+)[""']")]
    private static partial Regex GradleCoordinate();

    /// <summary>Gradle's build files: each "group:name:version" coordinate.</summary>
    static IEnumerable<Dependency> Gradle(string path, string text) =>
        GradleCoordinate().Matches(text).Select(m => new Dependency("Maven", $"{m.Groups[1].Value}:{m.Groups[2].Value}", Exact(m.Groups[3].Value), true, null, path, LineOf(text, m.Index)));

    [GeneratedRegex(@"^    ([A-Za-z0-9_.-]+) \(([^)\s]+)\)\s*$", RegexOptions.Multiline)]
    private static partial Regex GemSpec();

    /// <summary>Gemfile.lock: its specs (four spaces in: a gem and its version).</summary>
    static IEnumerable<Dependency> GemfileLock(string path, string text)
    {
        var direct = new HashSet<string>(StringComparer.Ordinal);
        var deps = text.IndexOf("\nDEPENDENCIES", StringComparison.Ordinal);
        if (deps >= 0)
            foreach (var line in text[(deps + 1)..].Split('\n').Skip(1).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal)))
                direct.Add(line.Trim().Split(' ', '!')[0]);
        return GemSpec().Matches(text).Select(m => new Dependency("RubyGems", m.Groups[1].Value, m.Groups[2].Value.Split('-')[0], direct.Contains(m.Groups[1].Value), null, path, LineOf(text, m.Index)))
            .DistinctBy(d => (d.Name, d.Version));
    }

    /// <summary>composer.lock: its packages and dev packages.</summary>
    static IEnumerable<Dependency> ComposerLock(string path, string text)
    {
        var root = JsonNode.Parse(text) as JsonObject ?? throw new FormatException();
        foreach (var group in new[] { "packages", "packages-dev" })
            foreach (var p in root[group] as JsonArray ?? [])
                if (p?["name"]?.ToString() is { } pkg && p["version"]?.ToString() is { } version)
                    yield return new Dependency("Packagist", pkg, version.TrimStart('v'), false, null, path, LineOf(text, $"\"{pkg}\""));
    }

    [GeneratedRegex(@"^v?\d+(\.\d+)*([-.+][0-9A-Za-z.+-]+)?$")]
    private static partial Regex ExactVersion();

    /// <summary>A version that is one version, not a range or a variable.</summary>
    static string? Exact(string? version) => version is not null && ExactVersion().IsMatch(version.Trim()) ? version.Trim() : null;
}
