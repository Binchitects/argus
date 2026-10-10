using System.Text.Json;
using System.Text.RegularExpressions;

namespace Argus.Indexing;

/// <summary>
/// What a file declares about the estate: what it provides (a package by its name, a module, a namespace, a .proto
/// file) and what it uses (a package reference, an import, a submodule, a CI include, an image). Read from the text the
/// index already stores, by the conventions of each ecosystem; no build is run. Resolving uses against provides links
/// repositories (Graph.RebuildLinks).
/// </summary>
public static partial class Links
{
    public const string Provides = "provides";
    public const string Uses = "uses";

    /// <summary>One declaration: provides or uses, its kind (nuget, npm, pypi, go, maven, cargo, cs, java, py, proto, repo, image), the name, its line.</summary>
    public readonly record struct Decl(string Role, string Kind, string Name, long Line);

    /// <summary>The languages that are manifests, not code: their declarations are read, no symbols.</summary>
    public static readonly HashSet<string> ManifestLangs = new(StringComparer.Ordinal)
    {
        "msbuild", "npm", "gomod", "maven", "gradle", "cargo", "toml", "yaml", "gitmodules", "dockerfile", "ini", "nugetconfig",
    };

    public static List<Decl> Extract(string path, string? lang, string content)
    {
        var output = new List<Decl>();
        var name = path[(path.LastIndexOf('/') + 1)..];
        try
        {
            switch (lang)
            {
                case "msbuild": MsBuild(name, content, output); break;
                case "nugetconfig": Matches(PackagesConfig(), content, "nuget", output, lower: true); break;
                case "npm": PackageJson(content, output); break;
                case "gomod": GoMod(content, output); break;
                case "maven": Pom(content, output); break;
                case "gradle": Matches(GradleDep(), content, "maven", output, lower: true, join: m => $"{m.Groups["g"].Value}:{m.Groups["a"].Value}"); break;
                case "cargo": Cargo(content, output); break;
                case "toml" when name == "pyproject.toml": PyProject(content, output); break;
                case "ini" when name == "setup.cfg": SetupCfg(content, output); break;
                case "text" when IsRequirements(path, name): Requirements(content, output); break;
                case "gitmodules": Matches(SubmoduleUrl(), content, "repo", output, lower: true, join: m => RepoPathOf(m.Groups["url"].Value)); break;
                case "yaml": Yaml(name, content, output); break;
                case "dockerfile": Matches(DockerFrom(), content, "image", output, lower: true, join: m => ImagePath(m.Groups["image"].Value)); break;
                case "csharp": CSharp(Blank(content, Code.CSharp), output); break;
                case "python": Python(path, Blank(content, Code.Python), output); break;
                case "typescript" or "javascript": JavaScript(content, output); break;
                case "go": Go(Blank(content, Code.Go), output); break;
                case "java" or "kotlin": Java(Blank(content, Code.Java), output); break;
                case "rust": Matches(RustUse(), content, "cargo", output, lower: true, join: m => m.Groups["crate"].Value.Replace('_', '-')); break;
                case "proto": Proto(path, content, output); break;
            }
        }
        catch (Exception e) when (e is JsonException or RegexMatchTimeoutException or ArgumentException)
        {
            // A file this cannot read declares nothing: the rest of the estate is still linked.
        }
        return [.. output.Where(d => d.Name.Length is > 0 and <= 300).Distinct()];
    }

    /// <summary>A language's comments and strings, as Blank reads them.</summary>
    enum Code { CSharp, Java, Go, Python }

    /// <summary>
    /// The text with its comments and string literals blanked (spaces; line breaks kept, so lines stay where they are): code
    /// inside a string (an analyzer's test, a generator's template, a docstring) declares nothing. Go's quoted strings stay
    /// (its imports are strings); its raw strings go.
    /// </summary>
    static string Blank(string text, Code code)
    {
        var b = new System.Text.StringBuilder(text.Length);
        var i = 0;
        void Skip(int to)
        {
            for (; i < to && i < text.Length; i++) b.Append(text[i] == '\n' ? '\n' : ' ');
        }
        while (i < text.Length)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (code != Code.Python && c == '/' && next == '/')
            {
                Skip(text.IndexOf('\n', i) is var nl and >= 0 ? nl : text.Length);
            }
            else if (code != Code.Python && c == '/' && next == '*')
            {
                Skip(text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 2 : text.Length);
            }
            else if (code == Code.Python && c == '#')
            {
                Skip(text.IndexOf('\n', i) is var nl and >= 0 ? nl : text.Length);
            }
            else if (code is Code.Python or Code.CSharp or Code.Java && (text.AsSpan(i).StartsWith("\"\"\"") || (code == Code.Python && text.AsSpan(i).StartsWith("\'\'\'"))))
            {
                // Triple quotes: Python's, C#'s raw strings (three or more), Java's and Kotlin's text blocks.
                var quote = text[i];
                var run = 0;
                while (i + run < text.Length && text[i + run] == quote) run++;
                var close = new string(quote, code == Code.CSharp ? run : 3);
                Skip(text.IndexOf(close, i + run, StringComparison.Ordinal) is var end and >= 0 ? end + close.Length : text.Length);
            }
            else if (code == Code.CSharp && (c == '@' || c == '$') && (next == '"' || (next is '@' or '$' && i + 2 < text.Length && text[i + 2] == '"')))
            {
                // Verbatim (@"…", "" inside) or interpolated strings.
                var verbatim = c == '@' || next == '@';
                var start = text.IndexOf('"', i) + 1;
                var j = start;
                while (j < text.Length)
                {
                    if (verbatim && text[j] == '"' && j + 1 < text.Length && text[j + 1] == '"') j += 2;
                    else if (!verbatim && text[j] == '\\') j += 2;
                    else if (text[j] == '"') break;
                    else j++;
                }
                Skip(Math.Min(text.Length, j + 1));
            }
            else if ((c == '"' && code != Code.Go) || (c == '\'' && code is Code.Python))
            {
                var j = i + 1;
                while (j < text.Length && text[j] != c && text[j] != '\n') j += text[j] == '\\' ? 2 : 1;
                Skip(Math.Min(text.Length, j + 1));
            }
            else if (c == '\'' && code is Code.CSharp or Code.Java && i + 2 < text.Length)
            {
                // A character literal: 'x', '\n', '\''.
                var end = text[i + 1] == '\\' ? text.IndexOf('\'', i + 3) : i + 2;
                if (end > i && end < text.Length && text[end] == '\'' && end - i <= 8) Skip(end + 1);
                else
                {
                    b.Append(c);
                    i++;
                }
            }
            else if (c == '`' && code == Code.Go)
            {
                Skip(text.IndexOf('`', i + 1) is var end and >= 0 ? end + 1 : text.Length);
            }
            else
            {
                b.Append(c);
                i++;
            }
        }
        return b.ToString();
    }

    [ThreadStatic] static string? _linesOf;
    [ThreadStatic] static List<int>? _lineStarts;

    /// <summary>The line (from 1) of a place in the text: the line starts are found once per text.</summary>
    static long LineAt(string content, int index)
    {
        if (!ReferenceEquals(_linesOf, content) || _lineStarts is null)
        {
            _lineStarts = [0];
            for (var i = 0; i < content.Length; i++)
                if (content[i] == '\n') _lineStarts.Add(i + 1);
            _linesOf = content;
        }
        var at = _lineStarts.BinarySearch(index);
        return at >= 0 ? at + 1 : ~at;
    }

    static void Matches(Regex re, string content, string kind, List<Decl> output, bool lower, Func<Match, string>? join = null, string role = Uses)
    {
        foreach (Match m in re.Matches(content))
        {
            var value = join is null ? m.Groups["name"].Value : join(m);
            if (value.Length == 0) continue;
            output.Add(new Decl(role, kind, lower ? value.ToLowerInvariant() : value, LineAt(content, m.Index)));
        }
    }

    // --- .NET -------------------------------------------------------------------------

    static void MsBuild(string fileName, string content, List<Decl> output)
    {
        Matches(PackageReference(), content, "nuget", output, lower: true);
        if (!fileName.EndsWith("proj", StringComparison.OrdinalIgnoreCase)) return;
        // The package it makes: its PackageId, else its assembly's name, else the project file's.
        var id = PackageId().Match(content) is { Success: true } p ? p : AssemblyName().Match(content);
        var name = id.Success ? id.Groups["name"].Value : fileName[..fileName.LastIndexOf('.')];
        if (!name.Contains("$(", StringComparison.Ordinal))
            output.Add(new Decl(Provides, "nuget", name.ToLowerInvariant(), id.Success ? LineAt(content, id.Index) : 1));
    }

    /// <summary>
    /// A C# file's namespaces, whole (a namespace inside another is Outer.Inner), and its usings: a namespace (cs, matched
    /// exactly), or a type or a namespace (cs-type: `using static`, an alias), matched as it is or one segment up.
    /// </summary>
    static void CSharp(string content, List<Decl> output)
    {
        var open = new Stack<(string Name, int Depth)>();
        var depth = 0;
        string? fileScoped = null;
        foreach (Match m in CsToken().Matches(content))
        {
            switch (m.Value)
            {
                case "{":
                    depth++;
                    break;
                case "}":
                    depth--;
                    while (open.Count > 0 && open.Peek().Depth > depth) open.Pop();
                    break;
                default:
                    var name = m.Groups["ns"].Value;
                    var outer = open.Count > 0 ? string.Join('.', open.Reverse().Select(o => o.Name)) + "." : fileScoped is null ? "" : fileScoped + ".";
                    output.Add(new Decl(Provides, "cs", outer + name, LineAt(content, m.Index)));
                    if (m.Groups["end"].Value == ";") fileScoped = name;
                    else
                    {
                        depth++;
                        open.Push((name, depth));
                    }
                    break;
            }
        }
        foreach (Match m in CsUsing().Matches(content))
            output.Add(new Decl(Uses, m.Groups["static"].Success || m.Groups["alias"].Success ? "cs-type" : "cs", m.Groups["name"].Value, LineAt(content, m.Index)));
    }

    // --- JavaScript and TypeScript ----------------------------------------------------

    static void PackageJson(string content, List<Decl> output)
    {
        using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name)
            output.Add(new Decl(Provides, "npm", name.ToLowerInvariant(), Line(content, "\"name\"")));
        foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
        {
            if (!root.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
            foreach (var dep in deps.EnumerateObject())
                output.Add(new Decl(Uses, "npm", dep.Name.ToLowerInvariant(), Line(content, $"\"{dep.Name}\"")));
        }
    }

    static long Line(string content, string text) => content.IndexOf(text, StringComparison.Ordinal) is var i and >= 0 ? LineAt(content, i) : 1;

    static void JavaScript(string content, List<Decl> output)
    {
        foreach (Match m in JsImport().Matches(content))
        {
            var spec = m.Groups["spec"].Value;
            if (spec.StartsWith('.') || spec.StartsWith('/') || spec.Contains(':', StringComparison.Ordinal)) continue;
            // The package a bare specifier names: @scope/name, or its first part.
            var parts = spec.Split('/');
            var package = spec.StartsWith('@') && parts.Length > 1 ? parts[0] + "/" + parts[1] : parts[0];
            output.Add(new Decl(Uses, "npm", package.ToLowerInvariant(), LineAt(content, m.Index)));
        }
    }

    // --- Python -----------------------------------------------------------------------

    /// <summary>PEP 503: a distribution's name compares lower-case, runs of - _ . as one -.</summary>
    public static string PyName(string name) => PyNorm().Replace(name.Trim().ToLowerInvariant(), "-");

    static void PyProject(string content, List<Decl> output)
    {
        if (TomlName().Match(content) is { Success: true } n)
            output.Add(new Decl(Provides, "pypi", PyName(n.Groups["name"].Value), LineAt(content, n.Index)));
        // dependencies = ["a>=1", "b"] (PEP 621) and [tool.poetry.dependencies] a = "^1".
        if (TomlDependencies().Match(content) is { Success: true } list)
            foreach (Match d in QuotedRequirement().Matches(list.Groups["list"].Value))
                output.Add(new Decl(Uses, "pypi", PyName(d.Groups["name"].Value), LineAt(content, list.Index)));
        if (PoetryDependencies().Match(content) is { Success: true } poetry)
            foreach (Match d in TomlKey().Matches(poetry.Groups["body"].Value))
                if (d.Groups["name"].Value != "python")
                    output.Add(new Decl(Uses, "pypi", PyName(d.Groups["name"].Value), LineAt(content, poetry.Index)));
    }

    static void SetupCfg(string content, List<Decl> output)
    {
        if (IniName().Match(content) is { Success: true } n)
            output.Add(new Decl(Provides, "pypi", PyName(n.Groups["name"].Value), LineAt(content, n.Index)));
        if (IniInstallRequires().Match(content) is { Success: true } list)
            foreach (var line in list.Groups["body"].Value.Split('\n'))
                if (RequirementName().Match(line.Trim()) is { Success: true } d)
                    output.Add(new Decl(Uses, "pypi", PyName(d.Groups["name"].Value), LineAt(content, list.Index)));
    }

    /// <summary>requirements*.txt, constraints*.txt, and any .txt in a requirements/ folder (requirements/base.txt).</summary>
    static bool IsRequirements(string path, string name) =>
        name.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) || name.StartsWith("constraints", StringComparison.OrdinalIgnoreCase)
        || path.Contains("requirements/", StringComparison.OrdinalIgnoreCase);

    static void Requirements(string content, List<Decl> output)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Split('#')[0].Trim();
            if (line.Length == 0 || line.StartsWith('-')) continue;
            // git+https://gitlab.example.com/group/lib.git@v1#egg=lib: the repository itself.
            if (line.StartsWith("git+", StringComparison.Ordinal) || line.Contains("://", StringComparison.Ordinal))
            {
                if (RepoPathOf(line.Split('@', '#')[0].Replace("git+", "", StringComparison.Ordinal)) is { Length: > 0 } repo)
                    output.Add(new Decl(Uses, "repo", repo.ToLowerInvariant(), i + 1));
                continue;
            }
            if (RequirementName().Match(line) is { Success: true } m)
                output.Add(new Decl(Uses, "pypi", PyName(m.Groups["name"].Value), i + 1));
        }
    }

    static void Python(string path, string content, List<Decl> output)
    {
        // The module a file is: named from its import root, which only the repository's other files tell (the top of the
        // chain of package folders); resolved with them (Graph.PythonModules).
        if (path.EndsWith(".py", StringComparison.Ordinal))
            output.Add(new Decl(Provides, "py-file", path, 1));
        foreach (Match m in PyImport().Matches(content))
        {
            var module = m.Groups["from"].Success ? m.Groups["from"].Value : m.Groups["import"].Value;
            if (module.StartsWith('.')) continue;
            foreach (var one in module.Split(','))
                if (one.Trim().Split(' ')[0] is { Length: > 0 } dotted)
                    output.Add(new Decl(Uses, "py", dotted, LineAt(content, m.Index)));
        }
        if (SetupPyName().Match(content) is { Success: true } n && path.EndsWith("setup.py", StringComparison.Ordinal))
            output.Add(new Decl(Provides, "pypi", PyName(n.Groups["name"].Value), LineAt(content, n.Index)));
        if (path.EndsWith("setup.py", StringComparison.Ordinal) && SetupInstallRequires().Match(content) is { Success: true } list)
            foreach (Match d in QuotedRequirement().Matches(list.Groups["list"].Value))
                output.Add(new Decl(Uses, "pypi", PyName(d.Groups["name"].Value), LineAt(content, list.Index)));
    }

    // --- Go, Java, Rust, .proto ---------------------------------------------------------

    static void GoMod(string content, List<Decl> output)
    {
        if (GoModule().Match(content) is { Success: true } m)
            output.Add(new Decl(Provides, "go", m.Groups["name"].Value, LineAt(content, m.Index)));
        foreach (Match r in GoRequire().Matches(content))
        {
            // What the module needs only through another (// indirect) is not its own use.
            var line = content[r.Index..(content.IndexOf('\n', r.Index) is var nl and >= 0 ? nl : content.Length)];
            if (!line.Contains("// indirect", StringComparison.Ordinal))
                output.Add(new Decl(Uses, "go", r.Groups["name"].Value, LineAt(content, r.Index)));
        }
        // replace a => estate.example.com/team/a-fork v1: the fork is what it builds against.
        foreach (Match r in GoReplace().Matches(content))
            output.Add(new Decl(Uses, "go", r.Groups["to"].Value, LineAt(content, r.Index)));
    }

    /// <summary>A Go file's imports: in import ( … ) blocks and import lines only, with or without an alias (., _, a name).</summary>
    static void Go(string content, List<Decl> output)
    {
        foreach (Match block in GoImportBlock().Matches(content))
        {
            var body = block.Groups["body"];
            foreach (Match m in GoImportSpec().Matches(body.Value))
                output.Add(new Decl(Uses, "go", m.Groups["name"].Value, LineAt(content, body.Index + m.Index)));
        }
    }

    static void Pom(string content, List<Decl> output)
    {
        // The project's own coordinates: outside <parent>, <dependencies> and <build>.
        var own = PomSections().Replace(content, m => new string('\n', m.Value.Count(c => c == '\n')));
        var group = PomGroup().Match(own) is { Success: true } g ? g.Groups["v"].Value
            : PomParentGroup().Match(content) is { Success: true } pg ? pg.Groups["v"].Value : "";
        if (PomArtifact().Match(own) is { Success: true } a && group.Length > 0)
            output.Add(new Decl(Provides, "maven", $"{group}:{a.Groups["v"].Value}".ToLowerInvariant(), LineAt(content, a.Index)));
        // A <dependencyManagement> entry only pins a version, and build plugins are tools: neither is a use.
        var used = PomManaged().Replace(content, m => new string('\n', m.Value.Count(c => c == '\n')));
        foreach (Match d in PomDependency().Matches(used))
            output.Add(new Decl(Uses, "maven", $"{d.Groups["g"].Value}:{d.Groups["a"].Value}".ToLowerInvariant(), LineAt(content, d.Index)));
    }

    static void Java(string content, List<Decl> output)
    {
        if (JavaPackage().Match(content) is { Success: true } p)
            output.Add(new Decl(Provides, "java", p.Groups["name"].Value, LineAt(content, p.Index)));
        foreach (Match m in JavaImport().Matches(content))
        {
            // import a.b.*: the package a.b (the greedy name takes the dot before the star).
            var name = m.Groups["name"].Value;
            output.Add(new Decl(Uses, name.EndsWith('.') ? "java-package" : "java", name.TrimEnd('.'), LineAt(content, m.Index)));
        }
    }

    static void Cargo(string content, List<Decl> output)
    {
        if (CargoPackage().Match(content) is { Success: true } p && TomlName().Match(p.Groups["body"].Value) is { Success: true } n)
            output.Add(new Decl(Provides, "cargo", n.Groups["name"].Value.ToLowerInvariant().Replace('_', '-'), LineAt(content, p.Index)));
        foreach (Match section in CargoDependencies().Matches(content))
            foreach (Match d in TomlKey().Matches(section.Groups["body"].Value))
                output.Add(new Decl(Uses, "cargo", d.Groups["name"].Value.ToLowerInvariant().Replace('_', '-'), LineAt(content, section.Index)));
    }

    static void Proto(string path, string content, List<Decl> output)
    {
        output.Add(new Decl(Provides, "proto", path, 1));
        foreach (Match m in ProtoImport().Matches(content))
            output.Add(new Decl(Uses, "proto", m.Groups["name"].Value, LineAt(content, m.Index)));
    }

    // --- Repositories by path: submodules, CI, images ------------------------------------

    static void Yaml(string fileName, string content, List<Decl> output)
    {
        if (fileName.EndsWith("gitlab-ci.yml", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith("gitlab-ci.yaml", StringComparison.OrdinalIgnoreCase))
            Matches(CiProject(), content, "repo", output, lower: true, join: m => m.Groups["name"].Value.Trim('\'', '"', '/'));
        // compose files, Kubernetes manifests, Helm values: image: group/name:tag and repository: group/name. A chart's
        // repository: is where its dependencies' charts come from, not an image.
        if (fileName is "Chart.yaml" or "Chart.lock" or "requirements.yaml") return;
        Matches(YamlImage(), content, "image", output, lower: true, join: m => ImagePath(m.Groups["image"].Value));
    }

    /// <summary>group/sub/name from a git URL (https://host/group/name.git, git@host:group/name.git); empty when it is not one.</summary>
    public static string RepoPathOf(string url)
    {
        var u = url.Trim().Trim('"', '\'');
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        if (u.StartsWith("git@", StringComparison.Ordinal) && u.IndexOf(':') is var colon and > 0) return u[(colon + 1)..].Trim('/');
        var scheme = u.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return "";
        var rest = u[(scheme + 3)..];
        var slash = rest.IndexOf('/');
        return slash < 0 ? "" : rest[(slash + 1)..].Trim('/');
    }

    /// <summary>An image's path in its registry, without the registry's host or the tag (registry.example.com:5000/group/app:1.2 is group/app).</summary>
    public static string ImagePath(string image)
    {
        var i = image.Trim().Trim('"', '\'');
        if (i.Contains('$', StringComparison.Ordinal)) return "";
        var at = i.IndexOf('@');
        if (at >= 0) i = i[..at];
        var parts = i.Split('/').ToList();
        // A first part with a dot or a port, or "localhost", is a registry's host.
        if (parts.Count > 1 && (parts[0].Contains('.') || parts[0].Contains(':') || parts[0] == "localhost")) parts.RemoveAt(0);
        var last = parts[^1];
        var colon = last.LastIndexOf(':');
        if (colon > 0) parts[^1] = last[..colon];
        // One part is a public image's name (nginx, python): never this estate's.
        return parts.Count >= 2 ? string.Join('/', parts) : "";
    }

    // --- Patterns -----------------------------------------------------------------------

    [GeneratedRegex(@"<PackageReference\b[^>]*?\sInclude\s*=\s*""(?<name>[^""$]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReference();
    [GeneratedRegex("""<PackageId>\s*(?<name>[^<\s]+)\s*</PackageId>""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageId();
    [GeneratedRegex("""<AssemblyName>\s*(?<name>[^<\s]+)\s*</AssemblyName>""", RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyName();
    [GeneratedRegex(@"<package\s+id\s*=\s*""(?<name>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PackagesConfig();
    /// <summary>A namespace's start (its name, and { or ; after it), or a brace: what nests one namespace in another.</summary>
    [GeneratedRegex(@"\bnamespace\s+(?<ns>[A-Za-z_][\w.]*)\s*(?<end>[{;])|[{}]")]
    private static partial Regex CsToken();
    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?:(?<static>static)\s+)?(?:(?<alias>\w+)\s*=\s*)?(?:global::)?(?<name>[A-Za-z_][\w.]*)\s*;", RegexOptions.Multiline)]
    private static partial Regex CsUsing();
    [GeneratedRegex("""(?:\bimport\s+(?:[\w*{}\s,]+\s+from\s+)?|\bexport\s+[\w*{}\s,]+\s+from\s+|\brequire\s*\(\s*|\bimport\s*\(\s*)['"](?<spec>[^'"\s]+)['"]""")]
    private static partial Regex JsImport();
    [GeneratedRegex(@"[-_.]+")]
    private static partial Regex PyNorm();
    [GeneratedRegex(@"^\s*name\s*=\s*['""](?<name>[^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex TomlName();
    /// <summary>dependencies = [ … ]: quoted items (a ] inside one, as in extras, does not end the list).</summary>
    [GeneratedRegex(@"^\s*dependencies\s*=\s*\[(?<list>(?:""[^""]*""|'[^']*'|[^\]""'])*)\]", RegexOptions.Multiline)]
    private static partial Regex TomlDependencies();
    [GeneratedRegex(@"^\[tool\.poetry\.dependencies\]\s*\n(?<body>(?:(?!\[).*\n?)*)", RegexOptions.Multiline)]
    private static partial Regex PoetryDependencies();
    [GeneratedRegex(@"^\s*(?<name>[A-Za-z0-9][\w.-]*)\s*=", RegexOptions.Multiline)]
    private static partial Regex TomlKey();
    [GeneratedRegex("""['"](?<name>[A-Za-z0-9][\w.-]*)""")]
    private static partial Regex QuotedRequirement();
    [GeneratedRegex(@"^(?<name>[A-Za-z0-9][\w.-]*)")]
    private static partial Regex RequirementName();
    [GeneratedRegex(@"^\s*name\s*=\s*(?<name>\S+)", RegexOptions.Multiline)]
    private static partial Regex IniName();
    [GeneratedRegex(@"^\s*install_requires\s*=\s*\n(?<body>(?:[ \t]+\S.*\n?)*)", RegexOptions.Multiline)]
    private static partial Regex IniInstallRequires();
    [GeneratedRegex(@"^(?:from\s+(?<from>[\w.]+)\s+import|import\s+(?<import>[\w.]+(?:\s+as\s+\w+)?(?:\s*,\s*[\w.]+(?:\s+as\s+\w+)?)*))", RegexOptions.Multiline)]
    private static partial Regex PyImport();
    [GeneratedRegex("""\bname\s*=\s*['"](?<name>[^'"]+)['"]""")]
    private static partial Regex SetupPyName();
    [GeneratedRegex(@"\binstall_requires\s*=\s*\[(?<list>(?:""[^""]*""|'[^']*'|[^\]""'])*)\]")]
    private static partial Regex SetupInstallRequires();
    [GeneratedRegex(@"^module\s+(?<name>\S+)", RegexOptions.Multiline)]
    private static partial Regex GoModule();
    [GeneratedRegex(@"^\s*(?:require\s+)?(?<name>[a-z0-9][\w.\-]*\.[a-z]{2,}/[\w./\-]+)\s+v\d", RegexOptions.Multiline)]
    private static partial Regex GoRequire();
    /// <summary>import "x" (a line), or import ( … ): the specs.</summary>
    [GeneratedRegex(@"^\s*import\s*(?:\((?<body>[^)]*)\)|(?<body>(?:[\w.]+\s+)?""[^""\n]*""))", RegexOptions.Multiline)]
    private static partial Regex GoImportBlock();
    [GeneratedRegex(@"(?:^|\n)\s*(?:[\w.]+\s+)?""(?<name>[a-z0-9][\w.\-]*\.[a-z]{2,}/[\w./\-]+)""")]
    private static partial Regex GoImportSpec();
    [GeneratedRegex(@"^\s*(?:replace\s+)?[^\s=]+(?:\s+v\S+)?\s*=>\s*(?<to>[a-z0-9][\w.\-]*\.[a-z]{2,}/[\w./\-]+)\s+v\d", RegexOptions.Multiline)]
    private static partial Regex GoReplace();
    [GeneratedRegex(@"<(?<tag>parent|dependencies|dependencyManagement|build|profiles|plugins)>[\s\S]*?</\k<tag>>")]
    private static partial Regex PomSections();
    [GeneratedRegex(@"<(?<tag>dependencyManagement|build|plugins)>[\s\S]*?</\k<tag>>")]
    private static partial Regex PomManaged();
    [GeneratedRegex(@"<groupId>\s*(?<v>[^<\s]+)\s*</groupId>")]
    private static partial Regex PomGroup();
    [GeneratedRegex(@"<parent>[\s\S]*?<groupId>\s*(?<v>[^<\s]+)\s*</groupId>")]
    private static partial Regex PomParentGroup();
    [GeneratedRegex(@"<artifactId>\s*(?<v>[^<\s]+)\s*</artifactId>")]
    private static partial Regex PomArtifact();
    [GeneratedRegex(@"<dependency>\s*(?:<!--[\s\S]*?-->\s*)*<groupId>\s*(?<g>[^<\s]+)\s*</groupId>\s*<artifactId>\s*(?<a>[^<\s]+)\s*</artifactId>")]
    private static partial Regex PomDependency();
    [GeneratedRegex("""\b(?:implementation|api|compile|compileOnly|runtimeOnly|testImplementation)\s*\(?\s*['"](?<g>[\w.\-]+):(?<a>[\w.\-]+)(?::[^'"]*)?['"]""")]
    private static partial Regex GradleDep();
    [GeneratedRegex(@"^\s*package\s+(?<name>[a-z_][\w.]*)\s*;?", RegexOptions.Multiline)]
    private static partial Regex JavaPackage();
    [GeneratedRegex(@"^\s*import\s+(?:static\s+)?(?<name>[a-z_][\w.]*)(?:\.\*)?\s*;?", RegexOptions.Multiline)]
    private static partial Regex JavaImport();
    [GeneratedRegex(@"^\[package\]\s*\n(?<body>(?:(?!\[).*\n?)*)", RegexOptions.Multiline)]
    private static partial Regex CargoPackage();
    [GeneratedRegex(@"^\[(?:dev-|build-)?dependencies\]\s*\n(?<body>(?:(?!\[).*\n?)*)", RegexOptions.Multiline)]
    private static partial Regex CargoDependencies();
    [GeneratedRegex(@"^\s*(?:pub\s+)?(?:use|extern\s+crate)\s+(?<crate>[a-z_][a-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex RustUse();
    [GeneratedRegex(@"^\s*import\s+(?:public\s+|weak\s+)?""(?<name>[^""]+\.proto)""", RegexOptions.Multiline)]
    private static partial Regex ProtoImport();
    [GeneratedRegex(@"^\s*url\s*=\s*(?<url>\S+)", RegexOptions.Multiline)]
    private static partial Regex SubmoduleUrl();
    [GeneratedRegex(@"^\s*-?\s*project\s*:\s*(?<name>['""]?[\w.\-/]+['""]?)\s*$", RegexOptions.Multiline)]
    private static partial Regex CiProject();
    /// <summary>image: x, repository: x, and the extended forms (image:\n  name: x; kustomize's newName: x).</summary>
    [GeneratedRegex(@"^\s*-?\s*(?:image|repository|newName)\s*:\s*['""]?(?<image>[\w.\-/:@${}]+)['""]?\s*$|^\s*image\s*:\s*\n\s+name\s*:\s*['""]?(?<image>[\w.\-/:@${}]+)['""]?\s*$", RegexOptions.Multiline)]
    private static partial Regex YamlImage();
    [GeneratedRegex(@"^\s*FROM\s+(?:--platform=\S+\s+)?(?<image>\S+)", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex DockerFrom();
}
