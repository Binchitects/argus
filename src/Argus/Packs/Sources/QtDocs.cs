using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Argus.Packs.Sources;

/// <summary>
/// The Qt reference, Qt 4.8 through Qt 6, as one pack.
///
/// The work directory holds the three offline documentation sets as Qt ships
/// them: <c>qt4/</c> (one flat folder), <c>qt5/</c> and <c>qt6/</c> (a folder
/// per module). Each set is the last release of its major version, and each
/// says when an API arrived and keeps its deprecated members on separate
/// pages, so together they cover every Qt 4, 5 and 6 release.
///
/// A document is one page of one version, linked to that version on
/// doc.qt.io. Symbols come from qdoc's own index files (the .index beside each
/// module's pages), which name every class, function, enum, property and macro
/// with its status and the version it arrived in; the pages supply the
/// declaration as documented and its first sentence. A symbol has a row per
/// version that documents it, newest first, and every row leads with the same
/// merged line: which versions document the name, whether it is obsolete
/// there, and the module it moved to.
/// </summary>
public sealed class QtDocs : ISource
{
    public string Name => "qt";
    public string RepoUrl => "https://doc.qt.io/";
    public string Branch => "";
    public string Subtree => "";
    public string License => "GFDL-1.3-only";
    public string LicenseUrl => "https://www.gnu.org/licenses/fdl-1.3.html";
    public string Attribution =>
        "Qt documentation (Qt 4.8, 5.15 and 6). Copyright (c) The Qt Company Ltd.; documentation contributions included herein are the copyrights of their respective owners. " +
        "Used under the GNU Free Documentation License version 1.3 as published by the Free Software Foundation. Qt is a trademark of The Qt Company Ltd.";

    /// <summary>lang="qt4", "qt5" or "qt6" keeps a search to that version's pages.</summary>
    public IReadOnlyList<(string Name, string Prefix)> Facets => [("qt4", Prefix(4)), ("qt5", Prefix(5)), ("qt6", Prefix(6))];

    /// <summary>Not a git checkout: the versions of the three documentation sets are the provenance.</summary>
    public string? Provenance(string root)
    {
        var releases = Load(root).OrderBy(r => r.Major).Select(r => $"qt{r.Major}={r.Version}").ToList();
        return releases.Count > 0 ? string.Join(",", releases) : null;
    }

    static readonly int[] Majors = [6, 5, 4];
    static string Prefix(int major) => major == 4 ? "qt-4.8/" : $"qt-{major}/";
    static string UrlBase(int major) => major == 4 ? "https://doc.qt.io/archives/qt-4.8/" : $"https://doc.qt.io/qt-{major}/";

    static readonly HashSet<string> Listed = new(StringComparer.Ordinal) { "active", "commendable", "main", "obsolete", "deprecated", "compat", "preliminary", "" };
    static readonly HashSet<string> ClassKinds = new(StringComparer.Ordinal) { "class", "struct", "union", "namespace", "header", "qml-type", "module", "qml-module" };
    static readonly HashSet<string> Leaves = new(StringComparer.Ordinal) { "function", "enum", "typedef", "property", "variable", "qmlproperty", "qmlmethod", "qmlsignal" };
    static readonly Regex HeaderNameRe = new(@"<[A-Za-z0-9_./]+>", RegexOptions.CultureInvariant);

    /// <summary>One documentation set: a major version, its release, and its pages by file name.</summary>
    sealed class Release(int major)
    {
        public int Major { get; } = major;
        public string Version { get; set; } = "";
        public string Short { get; set; } = "";
        public string Prefix => QtDocs.Prefix(Major);
        public string Suffix => $" (Qt {Short})";
        public Dictionary<string, string> Pages { get; } = new(StringComparer.Ordinal);
        public List<Entry> Entries { get; } = [];
    }

    /// <summary>A name qdoc's index documents, where, and in what state.</summary>
    sealed record Entry(string Name, string Kind, string Scope, string Page, string Anchor, string Status, string Since,
        string Module, string Signature, string ParamKey, string Brief, string Value, string OwnerPage);

    sealed record Frame(string Scope, string Module, string Page, string QmlType);

    List<Release>? _releases;
    string? _root;
    // What each page says, kept from the documents pass for the symbols pass (bodies dropped).
    readonly Dictionary<(int, string), QDocPage> _facts = [];

    /// <summary>The documentation sets under <paramref name="root"/>, newest first, read once.</summary>
    List<Release> Load(string root)
    {
        var full = Path.GetFullPath(root);
        if (_releases is not null && _root == full) return _releases;
        _facts.Clear();
        var releases = new List<Release>();
        foreach (var major in Majors)
        {
            var dir = Path.Combine(full, $"qt{major}");
            if (!Directory.Exists(dir)) continue;
            var release = new Release(major);
            var skipped = new HashSet<string>(StringComparer.Ordinal);
            var versions = new Dictionary<string, int>(StringComparer.Ordinal);
            var modules = new List<string>();
            foreach (var index in Walk.Files(dir, n => n.EndsWith(".index", StringComparison.Ordinal)))
            {
                try
                {
                    var (version, project) = IndexHeader(index);
                    if (version.Length > 0) versions[version] = versions.GetValueOrDefault(version) + 1;
                    ReadIndex(index, project, release.Entries, skipped);
                }
                catch (XmlException exc)
                {
                    // A module whose symbols vanished without a word would build a pack that looks complete.
                    throw new BuildError($"cannot read the qdoc index {index}: {exc.Message}", exc);
                }
                modules.Add(Path.GetDirectoryName(index)!);
            }
            if (versions.Count == 0) continue;
            release.Version = versions.OrderByDescending(v => v.Value).ThenBy(v => v.Key, StringComparer.Ordinal).First().Key;
            release.Short = string.Join(".", release.Version.Split('.').Take(2));
            foreach (var module in modules.Distinct().OrderBy(m => Walk.Relative(dir, m), PathOrder.Instance))
                foreach (var page in Directory.EnumerateFiles(module, "*.html").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (page!.EndsWith("-members.html", StringComparison.Ordinal) || skipped.Contains(page)) continue;
                    release.Pages.TryAdd(page, Path.Combine(module, page));
                }
            releases.Add(release);
        }
        _root = full;
        return _releases = releases;
    }

    static (string Version, string Project) IndexHeader(string path)
    {
        using var reader = XmlReader.Create(path, Settings());
        reader.MoveToContent();
        return (reader.GetAttribute("version") ?? "", reader.GetAttribute("project") ?? "");
    }

    static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true, IgnoreComments = true,
    };

    static string Join(string scope, string name) => scope.Length > 0 ? $"{scope}::{name}" : name;

    static (string Page, string Anchor) SplitHref(string href)
    {
        int hash = href.IndexOf('#');
        var page = hash < 0 ? href : href[..hash];
        var anchor = hash < 0 ? "" : href[(hash + 1)..];
        int slash = page.LastIndexOf('/');
        return (slash < 0 ? page : page[(slash + 1)..], anchor);
    }

    static string SinceText(string since) =>
        since.Length == 0 ? "" : char.IsAsciiDigit(since[0]) ? $"Qt {since}" : since;

    static bool Wanted(string status, string access, string href) =>
        Listed.Contains(status) && access != "private" && href.Length > 0;

    /// <summary>Stream one qdoc index into entries; containers push a scope, leaves are read whole.</summary>
    static void ReadIndex(string path, string project, List<Entry> into, HashSet<string> skipped)
    {
        var start = into.Count;
        var getters = new HashSet<(string, string, string)>();
        using var reader = XmlReader.Create(path, Settings());
        var frames = new Stack<Frame>();
        frames.Push(new Frame("", project, "", ""));
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (Leaves.Contains(reader.LocalName))
                {
                    var leaf = (XElement)XNode.ReadFrom(reader);
                    Leaf(leaf, frames.Peek(), into, getters);
                    continue;
                }
                var frame = Container(reader, frames.Peek(), into, skipped);
                if (!reader.IsEmptyElement) frames.Push(frame);
            }
            else if (reader.NodeType == XmlNodeType.EndElement && frames.Count > 1) frames.Pop();
            reader.Read();
        }
        // A property's getter shares its name and its anchor; the property row is the one to keep.
        if (getters.Count > 0)
        {
            var kept = into.Skip(start).Where(e => !(e.Kind is "function" && getters.Contains((e.Name, e.Page, e.Anchor)))).ToList();
            into.RemoveRange(start, into.Count - start);
            into.AddRange(kept);
        }
    }

    static Frame Container(XmlReader r, Frame parent, List<Entry> into, HashSet<string> skipped)
    {
        string A(string name) => r.GetAttribute(name) ?? "";
        var element = r.LocalName;
        var name = A("name");
        var href = A("href");
        var (page, _) = SplitHref(href);
        bool wanted = Wanted(A("status"), A("access"), href);
        var module = A("module") is { Length: > 0 } m && element != "namespace" ? m : parent.Module;
        void Add(string symbol, string kind, string scope = "") =>
            into.Add(new Entry(symbol, kind, scope, page, "", A("status"), SinceText(A("since")), module, "", "", A("brief"), "", ""));
        switch (element)
        {
            case "namespace" when name.Length > 0:
            {
                var qualified = A("fullname") is { Length: > 0 } f ? f : Join(parent.Scope, name);
                if (wanted) Add(qualified, "namespace");
                return new Frame(qualified, A("module") is { Length: > 0 } nm ? nm : parent.Module, page.Length > 0 ? page : parent.Page, "");
            }
            case "class" or "struct" or "union":
            {
                var qualified = A("fullname") is { Length: > 0 } f ? f : Join(parent.Scope, name);
                if (wanted) Add(qualified, element);
                return new Frame(qualified, module, page, "");
            }
            case "header":
                if (wanted) AddHeader(name, Add);
                return new Frame("", module, page, "");
            case "qmlclass" or "qmltype" or "qmlvaluetype" or "qmlbasictype":
                if (wanted && name.Length > 0) Add(name, "qml-type");
                return new Frame("", module, page, name);
            case "qmlmodule":
                if (wanted && name.Length > 0) Add(name, "qml-module");
                return parent;
            case "module":
                if (wanted && name.Length > 0 && !name.EndsWith("Private", StringComparison.Ordinal)) Add(name, "module");
                return parent;
            case "page":
                switch (A("subtype"))
                {
                    case "file" or "image":
                        if (page.Length > 0) skipped.Add(page);
                        return parent;
                    case "qmlclass" when name.Length > 0:
                        if (wanted) Add(name, "qml-type");
                        return new Frame("", module, page, name);
                    case "module":
                    {
                        var title = A("title");
                        var moduleName = title.EndsWith(" Module", StringComparison.Ordinal) ? title[..^7] : "";
                        if (wanted && moduleName.Length > 0 && !moduleName.Contains(' ')) Add(moduleName, "module");
                        return parent;
                    }
                    case "header":
                        if (wanted && HeaderNameRe.Match(A("title")) is { Success: true } h) AddHeader(h.Value, Add);
                        return new Frame("", module, page, "");
                }
                return parent with { Page = page.Length > 0 ? page : parent.Page };
        }
        return parent;
    }

    static void AddHeader(string name, Action<string, string, string> add)
    {
        if (name.Length == 0) return;
        add(name, "header", "");
        var bare = name.Trim('<', '>');
        if (bare != name && bare.Length > 0) add(bare, "header", "");
    }

    static void Leaf(XElement e, Frame frame, List<Entry> into, HashSet<(string, string, string)> getters)
    {
        string A(string name) => (string?)e.Attribute(name) ?? "";
        var href = A("href");
        if (!Wanted(A("status"), A("access"), href)) return;
        var (page, anchor) = SplitHref(href);
        var name = A("name");
        if (name.Length == 0) return;
        var status = A("status");
        var since = SinceText(A("since"));
        var owner = frame.Page;
        void Add(string symbol, string kind, string scope, string signature = "", string paramKey = "", string value = "", string valueSince = "") =>
            into.Add(new Entry(symbol, kind, scope, page, anchor, status, valueSince.Length > 0 ? valueSince : since, frame.Module,
                signature, paramKey, A("brief"), value, owner));
        switch (e.Name.LocalName)
        {
            case "function":
            {
                var meta = A("meta");
                if (meta is "qmlmethod" or "qmlsignal")
                {
                    if (frame.QmlType.Length == 0) return;
                    Add($"{frame.QmlType}.{name}", meta == "qmlmethod" ? "qml-method" : "qml-signal", frame.QmlType, IndexSignature(e, name));
                    return;
                }
                if (meta.StartsWith("macro", StringComparison.Ordinal))
                {
                    Add(name, "macro", "", IndexSignature(e, name));
                    return;
                }
                string qualified, scope;
                if (A("fullname") is { Length: > 0 } full)
                {
                    qualified = full;
                    int cut = full.LastIndexOf("::", StringComparison.Ordinal);
                    scope = cut < 0 ? "" : full[..cut];
                }
                else if (frame.Scope.Length == 0 || A("related").Length > 0 || A("relates").Length > 0)
                {
                    // A related non-member: documented on a class's page, declared at namespace scope.
                    if (name.StartsWith("operator", StringComparison.Ordinal)) return;
                    (qualified, scope) = (name, "");
                }
                else (qualified, scope) = (Join(frame.Scope, name), frame.Scope);
                var kind = meta switch
                {
                    "signal" => "signal",
                    "slot" => "slot",
                    "constructor" or "copy-constructor" or "move-constructor" => "constructor",
                    "destructor" => "destructor",
                    _ => "function",
                };
                Add(qualified, kind, scope, IndexSignature(e, qualified), ParamKey(e, qualified));
                return;
            }
            case "enum":
            {
                var qualified = A("fullname") is { Length: > 0 } f ? f : Join(frame.Scope, name);
                Add(qualified, "enum", frame.Scope, $"enum {qualified}");
                var valueScope = A("scoped") == "true" ? qualified : frame.Scope;
                foreach (var v in e.Elements("value"))
                {
                    var valueName = (string?)v.Attribute("name") ?? "";
                    if (valueName.Length == 0) continue;
                    Add(Join(valueScope, valueName), "enum-value", valueScope, "", "", (string?)v.Attribute("value") ?? "",
                        SinceText((string?)v.Attribute("since") ?? ""));
                }
                if (A("typedef") is { Length: > 0 } flags) Add(flags, "flags", frame.Scope, $"flags {flags}");
                return;
            }
            case "typedef":
            {
                var qualified = A("fullname") is { Length: > 0 } f ? f : Join(frame.Scope, name);
                var flags = A("enum").Length > 0;
                Add(qualified, flags ? "flags" : "typedef", frame.Scope, $"{(flags ? "flags" : "typedef")} {qualified}");
                return;
            }
            case "property":
            {
                var qualified = A("fullname") is { Length: > 0 } f ? f : Join(frame.Scope, name);
                getters.Add((qualified, page, anchor));
                Add(qualified, "property", frame.Scope, $"{qualified} : {A("type")}".TrimEnd(' ', ':'));
                return;
            }
            case "variable":
            {
                var qualified = A("fullname") is { Length: > 0 } f ? f : Join(frame.Scope, name);
                Add(qualified, "variable", frame.Scope, $"{A("type")} {qualified}".Trim());
                return;
            }
            case "qmlproperty":
                if (frame.QmlType.Length == 0) return;
                Add($"{frame.QmlType}.{name}", "qml-property", frame.QmlType, $"{name} : {A("type")}".TrimEnd(' ', ':'));
                return;
            case "qmlmethod" or "qmlsignal":
                if (frame.QmlType.Length == 0) return;
                Add($"{frame.QmlType}.{name}", e.Name.LocalName == "qmlmethod" ? "qml-method" : "qml-signal", frame.QmlType, IndexSignature(e, name));
                return;
        }
    }

    static string ParamType(XElement p)
    {
        var type = (string?)p.Attribute("type");
        if (type is not null) return type.Trim();
        return $"{(string?)p.Attribute("left") ?? ""}{(string?)p.Attribute("right") ?? ""}".Trim();
    }

    /// <summary>A declaration from the index alone, for members whose anchor heads someone else's text (a property's setter).</summary>
    static string IndexSignature(XElement e, string shown)
    {
        string A(string name) => (string?)e.Attribute(name) ?? "";
        var parameters = e.Elements("parameter").Select(p =>
        {
            var type = ParamType(p);
            var name = (string?)p.Attribute("name") ?? "";
            var text = type.Length == 0 ? name : name.Length == 0 ? type : type.EndsWith('&') || type.EndsWith('*') ? type + name : $"{type} {name}";
            var dflt = (string?)p.Attribute("default") ?? "";
            return dflt.Length > 0 ? $"{text} = {dflt}" : text;
        }).ToList();
        if (A("meta") == "macrowithoutparams") return shown;
        var type = A("type");
        var signature = $"{(type.Length > 0 ? type + " " : "")}{shown}({string.Join(", ", parameters)}){(A("const") == "true" ? " const" : "")}";
        return A("static") == "true" ? $"[static] {signature}" : signature;
    }

    /// <summary>Name and parameter types without spacing: the same overload in two versions has the same key.</summary>
    static string ParamKey(XElement e, string name) =>
        $"{name}({string.Join(",", e.Elements("parameter").Select(p => ParamType(p).Replace(" ", "")))}){((string?)e.Attribute("const") == "true" ? "c" : "")}";

    QDocPage Facts(Release release, string page)
    {
        if (_facts.TryGetValue((release.Major, page), out var cached)) return cached;
        var facts = release.Pages.TryGetValue(page, out var path) ? QDocHtml.Read(Walk.ReadText(path), release.Suffix) : new QDocPage();
        facts.Body = "";
        _facts[(release.Major, page)] = facts;
        return facts;
    }

    public IEnumerable<Doc> IterDocs(string root)
    {
        var releases = Load(root);
        var presence = new Presence();
        foreach (var release in releases)
            foreach (var e in release.Entries)
                if (ClassKinds.Contains(e.Kind) && e.Anchor.Length == 0 && release.Pages.ContainsKey(e.Page)) presence.Add(release, e);
        foreach (var release in releases)
        {
            var main = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var entry in release.Entries)
                if (ClassKinds.Contains(entry.Kind) && entry.Anchor.Length == 0 && release.Pages.ContainsKey(entry.Page))
                    main.TryAdd(entry.Page, entry);
            foreach (var (file, path) in release.Pages)
            {
                var page = QDocHtml.Read(Walk.ReadText(path), release.Suffix);
                var body = page.Body;
                if (main.TryGetValue(file, out var entry))
                {
                    var line = $"Documented in: {Versions(releases, presence, release, entry, null)}.";
                    int eol = body.IndexOf('\n');
                    body = eol < 0 ? $"{body}\n\n{line}" : $"{body[..eol]}\n\n{line}{body[eol..]}";
                }
                page.Body = "";
                _facts[(release.Major, file)] = page;
                yield return new Doc(release.Prefix + file, page.Title + release.Suffix, UrlBase(release.Major) + file, "md", body);
            }
        }
    }

    public IEnumerable<ApiSymbol> IterSymbols(string root)
    {
        var releases = Load(root);
        // Only what the pages document: an anchor qdoc indexed but never wrote is an undocumented member.
        var documented = new Dictionary<Release, List<Entry>>();
        foreach (var release in releases)
            documented[release] = release.Entries.Where(e =>
                release.Pages.ContainsKey(e.Page) && (e.Anchor.Length == 0 || Facts(release, e.Page).Anchors.Contains(e.Anchor))).ToList();
        var presence = new Presence();
        foreach (var (release, entries) in documented)
            foreach (var e in entries) presence.Add(release, e);
        foreach (var release in releases)
        {
            var seen = new HashSet<(string, string, string, string)>();
            foreach (var e in documented[release])
            {
                if (!seen.Add((e.Name, e.Kind, e.Page, e.Anchor))) continue;
                yield return new ApiSymbol(e.Name, e.Kind, e.Scope, release.Prefix + e.Page, e.Anchor, Contract(releases, presence, release, e));
            }
        }
    }

    /// <summary>
    /// Which versions document a name, keyed by the name and its family: the
    /// C++ module QtCore and Qt 6's QML module QtCore are different things.
    /// </summary>
    sealed class Presence
    {
        readonly Dictionary<(string, string), Dictionary<int, List<Entry>>> _byName = [];

        static string Family(string kind) => kind switch
        {
            "module" or "qml-module" or "header" => kind,
            _ => kind.StartsWith("qml", StringComparison.Ordinal) ? "qml" : "cpp",
        };

        public void Add(Release release, Entry e)
        {
            var key = (e.Name, Family(e.Kind));
            if (!_byName.TryGetValue(key, out var byMajor)) _byName[key] = byMajor = [];
            if (!byMajor.TryGetValue(release.Major, out var list)) byMajor[release.Major] = list = [];
            list.Add(e);
        }

        /// <summary>The entries for a name per major version, or null when no version documents it.</summary>
        public Dictionary<int, List<Entry>>? Get(string name, string kind) => _byName.GetValueOrDefault((name, Family(kind)));
    }

    /// <summary>What the index and the page name say about an entry's state in its version.</summary>
    static string IndexStatus(Entry e, Release release)
    {
        var label = e.Status switch
        {
            "obsolete" => "obsolete",
            "deprecated" => "deprecated",
            "compat" => "Qt 3 support",
            "preliminary" => "preliminary",
            _ => "",
        };
        if (label.Length > 0) return label;
        if (e.Page.EndsWith("-obsolete.html", StringComparison.Ordinal)) return release.Major >= 6 ? "deprecated" : "obsolete";
        if (e.Page.EndsWith("-qt3.html", StringComparison.Ordinal)) return "Qt 3 support";
        return "";
    }

    /// <summary>The row's own state, sharpened by its page: "deprecated since 6.4".</summary>
    string OwnStatus(Entry e, Release release)
    {
        var label = IndexStatus(e, release);
        var facts = Facts(release, e.Page);
        string? detail = null;
        if (e.Anchor.Length > 0 && facts.Members.TryGetValue(e.Anchor, out var member) && member.Deprecated.Length > 0)
            detail = member.Deprecated;
        else if (e.Anchor.Length == 0 && facts.Status.Length > 0)
            detail = facts.Status.StartsWith("Deprecated since ", StringComparison.Ordinal) ? facts.Status[17..]
                : facts.Status.ToLowerInvariant() is "deprecated" or "obsolete" or "preliminary" or "technical preview" ? facts.Status.ToLowerInvariant() : null;
        if (detail is null) return label;
        if (char.IsAsciiDigit(detail[0])) return $"deprecated since {detail}";
        return label.Length > 0 ? label : detail;
    }

    /// <summary>"Qt 4.8, Qt 5.15 (obsolete), not in Qt 6": the versions that document a name, its state and its module in each.</summary>
    static string Versions(List<Release> releases, Presence presence, Release row, Entry own, Func<Entry, Release, string>? ownStatus)
    {
        var byMajor = presence.Get(own.Name, own.Kind) ?? new() { [row.Major] = [own] };
        var parts = new List<string>();
        var missing = new List<string>();
        string module = "";
        bool seen = false;
        foreach (var release in releases.OrderBy(r => r.Major))
        {
            if (!byMajor.TryGetValue(release.Major, out var entries) || entries.Count == 0)
            {
                if (seen) missing.Add($"Qt {release.Major}");
                continue;
            }
            seen = true;
            var label = $"Qt {release.Short}";
            var subject = release == row ? own : entries[0];
            if (module.Length > 0 && subject.Module.Length > 0 && subject.Module != module) label += $" in {subject.Module}";
            if (subject.Module.Length > 0) module = subject.Module;
            string status;
            if (release == row) status = ownStatus?.Invoke(own, release) ?? IndexStatus(own, release);
            else
            {
                var labels = entries.Select(e => IndexStatus(e, release)).ToList();
                status = labels.All(l => l.Length > 0) ? labels[0] : "";
            }
            if (status.Length > 0) label += $" ({status})";
            parts.Add(label);
        }
        if (missing.Count > 0) parts.Add($"not in {string.Join(", ", missing)}");
        return string.Join(", ", parts);
    }

    string OwnSince(Entry e, Release release)
    {
        if (e.Since.Length > 0) return e.Since;
        var facts = Facts(release, e.Page);
        if (e.Anchor.Length > 0) return facts.Members.TryGetValue(e.Anchor, out var m) ? m.Since : "";
        return ClassKinds.Contains(e.Kind) ? facts.Since : "";
    }

    /// <summary>The earliest version's statement of when this overload arrived, then its class's, if they arrived together.</summary>
    string Since(List<Release> releases, Presence presence, Release row, Entry e)
    {
        var ascending = releases.OrderBy(r => r.Major).ToList();
        var byMajor = presence.Get(e.Name, e.Kind);
        if (byMajor is not null)
            foreach (var release in ascending)
                if (byMajor.TryGetValue(release.Major, out var entries))
                    foreach (var other in entries)
                        if (other.ParamKey == e.ParamKey && other.Kind == e.Kind && OwnSince(other, release) is { Length: > 0 } s)
                            return s;
        var own = OwnSince(e, row);
        if (own.Length > 0 || e.Scope.Length == 0 || byMajor is null) return own;
        var owner = presence.Get(e.Scope, e.Kind.StartsWith("qml", StringComparison.Ordinal) ? "qml-type" : "class");
        if (owner is null) return "";
        int first = ascending.First(r => byMajor.ContainsKey(r.Major)).Major;
        int ownerFirst = ascending.FirstOrDefault(r => owner.ContainsKey(r.Major))?.Major ?? -1;
        if (first != ownerFirst) return "";
        var ownerRelease = ascending.First(r => r.Major == ownerFirst);
        var classEntry = owner[ownerFirst].FirstOrDefault(c => ClassKinds.Contains(c.Kind));
        return classEntry is null ? "" : OwnSince(classEntry, ownerRelease);
    }

    string Contract(List<Release> releases, Presence presence, Release release, Entry e)
    {
        var facts = Facts(release, e.Page);
        var fields = new List<string> { Versions(releases, presence, release, e, OwnStatus) };
        bool qml = e.Kind.StartsWith("qml", StringComparison.Ordinal);
        if (qml)
        {
            var import = (e.OwnerPage.Length > 0 ? Facts(release, e.OwnerPage) : facts).Import;
            if (import.Length > 0) fields.Add($"Import: {import}");
        }
        else
        {
            var header = e.OwnerPage.Length > 0 && Facts(release, e.OwnerPage).Header is { Length: > 0 } h ? h : facts.Header;
            if (header.Length > 0 && e.Kind is not ("module" or "header")) fields.Add($"Header: {header}");
        }
        if (Since(releases, presence, release, e) is { Length: > 0 } since) fields.Add($"Since: {since}");
        if (e.Module.Length > 0 && !qml) fields.Add($"Module: {e.Module}");
        if (ClassKinds.Contains(e.Kind) && facts.Inherits.Length > 0) fields.Add($"Inherits: {facts.Inherits}");

        QDocMember? member = null;
        if (e.Anchor.Length > 0) facts.Members.TryGetValue(e.Anchor, out member);
        // A property's setter and notifier link to the property's own heading; their declaration is the index's.
        bool borrowed = member is not null && e.Kind is "function" or "signal" or "slot" && e.Anchor.EndsWith("-prop", StringComparison.Ordinal);
        string signature, brief;
        switch (e.Kind)
        {
            case "enum-value":
                signature = e.Value.Length > 0 ? $"{e.Name} = {e.Value}" : e.Name;
                brief = facts.Values.GetValueOrDefault(e.Name, "");
                break;
            case "class" or "struct" or "union" or "namespace":
                signature = $"{e.Kind} {e.Name}";
                brief = facts.Brief.Length > 0 ? facts.Brief : e.Brief;
                break;
            case "header":
                signature = $"#include {(e.Name.StartsWith('<') ? e.Name : $"<{e.Name}>")}";
                brief = facts.Brief.Length > 0 ? facts.Brief : e.Brief;
                break;
            case "module" or "qml-module" or "qml-type":
                signature = facts.Title.Length > 0 ? facts.Title : e.Name;
                brief = facts.Brief.Length > 0 ? facts.Brief : e.Brief;
                break;
            default:
                signature = member is not null && !borrowed ? member.Display : e.Signature.Length > 0 ? e.Signature : e.Name;
                brief = member is not null && !borrowed ? member.Brief : e.Brief;
                break;
        }
        var text = $"{string.Join("; ", fields)} -- {signature}";
        return brief.Length > 0 ? $"{text} -- {brief}" : text;
    }
}
