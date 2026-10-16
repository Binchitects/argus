using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// VS Code extensions as the IDE's page uses them: only what they declare (colour and file icon themes, snippets,
/// languages, grammars, keybindings, configuration defaults), never their code. They come from a .vsix the person sends,
/// or from VS Code's own installed extensions on this computer (copied). Each is kept in the data folder
/// (<c>extensions/&lt;publisher.name&gt;-&lt;version&gt;/</c>) with only the files its contributions name, and listed in
/// <c>extensions/installed.json</c> with whether it is on. The name keeps it apart from <see cref="Extensions"/>, which
/// loads the person's commands, agents and skills.
/// </summary>
internal sealed partial class EditorExtensions(string dataDir, Func<string, string?> env)
{
    /// <summary>The largest .vsix taken.</summary>
    public const int MaxVsix = 50 * 1024 * 1024;

    /// <summary>The largest file kept from an extension.</summary>
    public const int MaxFile = 5 * 1024 * 1024;

    /// <summary>The most an extension's files kept hold together, and how many they are.</summary>
    public const long MaxTotal = 50L * 1024 * 1024;
    public const int MaxFiles = 10_000;

    /// <summary>The list's file in the extensions' folder.</summary>
    public const string ListFile = "installed.json";

    private readonly string _root = Path.Combine(dataDir, "extensions");
    private readonly object _gate = new();
    private List<JsonObject>? _list;
    private (DateTime, long) _stamp;

    /// <summary>The kinds of files kept, by extension: what the page reads (JSON) or shows (images, fonts), and grammars.</summary>
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "application/json; charset=utf-8",
        [".jsonc"] = "application/json; charset=utf-8",
        [".code-snippets"] = "application/json; charset=utf-8",
        [".tmlanguage"] = "text/plain; charset=utf-8",
        [".plist"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
    };

    /// <summary>The contributions Code Arena uses (grammars only kept, for now).</summary>
    private static readonly HashSet<string> Used = ["themes", "iconThemes", "snippets", "languages", "grammars", "keybindings", "configurationDefaults"];

    /// <summary>What a contribution Code Arena does not use needs, by its name.</summary>
    private static readonly Dictionary<string, string> Why = new(StringComparer.Ordinal)
    {
        ["configuration"] = "its own settings, which its code reads",
        ["jsonValidation"] = "JSON schemas for files are not read yet",
        ["productIconThemes"] = "product icon themes are not supported",
        ["colors"] = "colours of its own views",
        ["semanticTokenTypes"] = "semantic tokens come from language servers, not supported here",
        ["semanticTokenModifiers"] = "semantic tokens come from language servers, not supported here",
        ["semanticTokenScopes"] = "semantic tokens come from language servers, not supported here",
        ["icons"] = "product icons are not supported",
    };

    // ------------------------------------------------------------------ installed

    /// <summary>The extensions kept, in the order they came: each with what it brings and what is not used here.</summary>
    public JsonArray List()
    {
        lock (_gate)
        {
            return new JsonArray([.. Current().Select(e => (JsonNode)Item(e))]);
        }
    }

    /// <summary>
    /// What the enabled extensions bring, merged for the page to apply: each entry says its extension and version (whose
    /// files it names). Keybindings whole here (the list counts them).
    /// </summary>
    public JsonObject Contributions()
    {
        var all = new JsonObject
        {
            ["themes"] = new JsonArray(),
            ["iconThemes"] = new JsonArray(),
            ["snippets"] = new JsonArray(),
            ["languages"] = new JsonArray(),
            ["keybindings"] = new JsonArray(),
            ["configurationDefaults"] = new JsonArray(),
        };
        lock (_gate)
        {
            foreach (var e in Current().Where(e => e.Bool("enabled") != false))
            {
                var id = e.Str("id");
                var version = e.Str("version");
                var contributes = e["info"]?["contributes"] as JsonObject ?? [];
                foreach (var kind in new[] { "themes", "iconThemes", "snippets", "languages", "keybindings" })
                {
                    foreach (var c in (contributes[kind] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var copy = c.Clone();
                        copy["extension"] = id;
                        copy["version"] = version;
                        ((JsonArray)all[kind]!).Add(copy);
                    }
                }
                if (contributes["configurationDefaults"] is JsonObject { Count: > 0 } defaults)
                {
                    ((JsonArray)all["configurationDefaults"]!).Add(new JsonObject { ["extension"] = id, ["version"] = version, ["values"] = defaults.Clone() });
                }
            }
        }
        return all;
    }

    /// <summary>A .vsix the person sent: read, its usable files kept, listed (in place of another version of it).</summary>
    public JsonObject Install(byte[] vsix)
    {
        if (vsix.Length > MaxVsix)
        {
            throw new IdeError(413, "too_large", $"A .vsix is taken up to {MaxVsix / 1024 / 1024} MB.");
        }
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(new MemoryStream(vsix), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            throw new IdeError(400, "invalid", "That is not a .vsix: a VS Code extension is a zip file.");
        }
        using (zip)
        {
            var source = new VsixSource(zip);
            return Store(source, ReadManifest(source, deep: true), "vsix");
        }
    }

    /// <summary>One of VS Code's own extensions on this computer (its newest copy), copied as a .vsix's would be.</summary>
    public JsonObject Import(string? id)
    {
        var wanted = (id ?? "").Trim().ToLowerInvariant();
        var found = LocalCopies().Where(c => c.Id == wanted).OrderByDescending(c => c.Version, VersionOrder.Instance).FirstOrDefault()
            ?? throw new IdeError(404, "not_found", $"VS Code has no extension {id} on this computer.");
        var source = new FolderSource(found.Dir);
        return Store(source, ReadManifest(source, deep: true), "vscode");
    }

    /// <summary>Takes an extension away, its files with it.</summary>
    public void Uninstall(string? id)
    {
        lock (_gate)
        {
            var all = Load();
            var e = Find(all, id);
            all.Remove(e);
            Save(all);
            Remove(e.Str("dir"));
        }
    }

    /// <summary>Turns an extension on or off: its contributions apply, or not.</summary>
    public JsonObject Enable(string? id, bool? enabled)
    {
        if (enabled is not { } on)
        {
            throw new IdeError(400, "invalid", "Say whether it is enabled: {\"id\", \"enabled\": true or false}.");
        }
        lock (_gate)
        {
            var all = Load();
            var e = Find(all, id);
            e["enabled"] = on;
            Save(all);
            return Item(e);
        }
    }

    /// <summary>One of an extension's files its contributions name, and its type; any other is 404.</summary>
    public (byte[] Bytes, string Type) File(string? id, string? path)
    {
        string full;
        lock (_gate)
        {
            var e = Find(Current(), id);
            if (path is null || !(e["files"] as JsonArray ?? []).Any(f => f?.GetValue<string>() == path))
            {
                throw new IdeError(404, "not_found", $"{id} has no such file it uses.");
            }
            var dir = Path.GetFullPath(Path.Combine(_root, e.Str("dir")!));
            full = Path.GetFullPath(Path.Combine(dir, path));
            if (!Workspace.Within(full, dir) || full == dir)
            {
                throw new IdeError(404, "not_found", $"{id} has no such file it uses.");
            }
        }
        var bytes = ReadCapped(full) ?? throw new IdeError(404, "not_found", $"{path} of {id} is gone.");
        return (bytes, TypeOf(path) ?? "application/octet-stream");
    }

    // ------------------------------------------------------------- VS Code's own

    /// <summary>VS Code's own folders of extensions on this computer: Code's, VSCodium's and Insiders'.</summary>
    private IEnumerable<(string Dir, string From)> LocalRoots()
    {
        var home = env("HOME") is { Length: > 0 } h ? h : env("USERPROFILE") is { Length: > 0 } u ? u : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return (Path.Combine(home, ".vscode", "extensions"), "VS Code");
        yield return (Path.Combine(home, ".vscode-oss", "extensions"), "VSCodium");
        yield return (Path.Combine(home, ".vscode-insiders", "extensions"), "VS Code - Insiders");
        if (OperatingSystem.IsWindows() && env("USERPROFILE") is { Length: > 0 } profile && !string.Equals(profile, home, StringComparison.OrdinalIgnoreCase))
        {
            yield return (Path.Combine(profile, ".vscode", "extensions"), "VS Code");
        }
    }

    private sealed record LocalCopy(string Id, string Version, string Dir, string From, Manifest? Manifest, string? Problem);

    /// <summary>Each extension folder of VS Code's on this computer, its package.json read (not its other files).</summary>
    private List<LocalCopy> LocalCopies()
    {
        var copies = new List<LocalCopy>();
        foreach (var (root, from) in LocalRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            // VS Code lists the folders it removed but could not delete yet here.
            var obsolete = Json.ParseObject(ReadText(Path.Combine(root, ".obsolete"))) ?? [];
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var dir in dirs.Order(StringComparer.Ordinal).Take(2000))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || obsolete.ContainsKey(name) || !System.IO.File.Exists(Path.Combine(dir, "package.json")))
                {
                    continue;
                }
                try
                {
                    var manifest = ReadManifest(new FolderSource(dir), deep: false);
                    copies.Add(new LocalCopy(manifest.Id, manifest.Version, dir, from, manifest, null));
                }
                catch (IdeError e)
                {
                    var guess = FolderName().Match(name);
                    copies.Add(new LocalCopy(guess.Success ? guess.Groups[1].Value.ToLowerInvariant() : name.ToLowerInvariant(), guess.Success ? guess.Groups[2].Value : "", dir, from, null, e.Message));
                }
            }
        }
        return copies;
    }

    /// <summary>
    /// VS Code's own extensions on this computer, the newest copy of each: whether it brings something Code Arena uses
    /// (what, and what not), and the version kept here already, if any.
    /// </summary>
    public JsonArray Local()
    {
        Dictionary<string, string> kept;
        lock (_gate)
        {
            kept = Current().ToDictionary(e => e.Str("id")!.ToLowerInvariant(), e => e.Str("version")!, StringComparer.Ordinal);
        }
        var list = new JsonArray();
        foreach (var group in LocalCopies().GroupBy(c => c.Id).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var c = group.OrderByDescending(x => x.Version, VersionOrder.Instance).First();
            var item = c.Manifest is { } m ? Summary(m) : new JsonObject { ["id"] = c.Id, ["name"] = c.Id, ["displayName"] = c.Id, ["version"] = c.Version };
            item["from"] = c.From;
            item["path"] = c.Dir;
            item["usable"] = c.Manifest is { Usable: true };
            item["problem"] = c.Problem ?? (c.Manifest is { Usable: false } bare ? NothingUsable(bare) : null);
            item["installed"] = kept.GetValueOrDefault(c.Id);
            list.Add(item);
        }
        return list;
    }

    // ------------------------------------------------------------ recommended

    /// <summary>VS Code extensions Code Arena does the work of itself, and how.</summary>
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.Ordinal)
    {
        ["esbenp.prettier-vscode"] = "Formatting: Prettier from the project's node_modules (Format Document, format on save).",
        ["prettier.prettier-vscode"] = "Formatting: Prettier from the project's node_modules (Format Document, format on save).",
        ["charliermarsh.ruff"] = "Formatting: ruff, from the project's .venv or PATH.",
        ["ms-python.black-formatter"] = "Formatting: black, from the project's .venv or PATH.",
        ["ms-python.python"] = "Run and Debug: debugpy (python configurations); formatting: ruff or black.",
        ["ms-python.debugpy"] = "Run and Debug: debugpy, when it is installed.",
        ["ms-vscode.cpptools"] = "Run and Debug: gdb or lldb-dap (cppdbg configurations); formatting: clang-format.",
        ["ms-vscode.cpptools-extension-pack"] = "Run and Debug: gdb or lldb-dap (cppdbg configurations); formatting: clang-format.",
        ["vadimcn.vscode-lldb"] = "Run and Debug: lldb-dap (lldb configurations).",
        ["llvm-vs-code-extensions.lldb-dap"] = "Run and Debug: lldb-dap (lldb-dap configurations).",
        ["ms-dotnettools.csharp"] = "Run and Debug: netcoredbg (coreclr configurations); formatting: dotnet format.",
        ["ms-dotnettools.csdevkit"] = "Run and Debug: netcoredbg (coreclr configurations); formatting: dotnet format.",
        ["golang.go"] = "Run and Debug: Delve (go configurations); formatting: gofmt.",
        ["rust-lang.rust-analyzer"] = "Formatting: rustfmt.",
        ["xaver.clang-format"] = "Formatting: clang-format.",
        ["ms-vscode.js-debug"] = "Run and Debug: js-debug (node configurations), when it is installed.",
        ["ms-vscode.js-debug-nightly"] = "Run and Debug: js-debug (node configurations), when it is installed.",
    };

    /// <summary>VS Code extensions that are their code, and what to do instead here.</summary>
    private static readonly Dictionary<string, string> CodeOnly = new(StringComparer.Ordinal)
    {
        ["dbaeumer.vscode-eslint"] = "ESLint's extension runs code: not supported here. Run ESLint as the project's check in Problems (\"checkCommand\": \"npx eslint .\" in config.json) or as a task.",
        ["stylelint.vscode-stylelint"] = "Stylelint's extension runs code: not supported here. Run it as the project's check, or as a task.",
        ["eamodio.gitlens"] = "GitLens runs code: not supported here. Source control shows the changes, a file's history and compares with any revision.",
        ["ms-python.vscode-pylance"] = "Pylance is licensed for Microsoft's products only.",
        ["github.copilot"] = "Copilot runs code: Code Arena's own completion and agent do this work.",
        ["github.copilot-chat"] = "Copilot runs code: Code Arena's own chat and agent do this work.",
        ["ms-toolsai.jupyter"] = "Notebooks are not supported here.",
        ["ms-azuretools.vscode-docker"] = "Docker's extension runs code: not supported here. Use the terminal.",
        ["ms-vscode-remote.remote-ssh"] = "Remote development is not supported here: run code-arena on that machine.",
        ["ms-vscode-remote.remote-containers"] = "Remote development is not supported here: run code-arena in the container.",
        ["ms-vscode-remote.remote-wsl"] = "Remote development is not supported here: run code-arena in WSL.",
        ["editorconfig.editorconfig"] = "EditorConfig's extension runs code: .editorconfig is not read yet (dotnet format reads it).",
    };

    /// <summary>
    /// The folder's .vscode/extensions.json (JSON with comments): each recommended extension and where it stands here:
    /// <c>installed</c>, <c>built-in</c> (Code Arena does its work), <c>unsupported</c> (it is code), <c>local</c> (VS Code
    /// has it on this computer: it can be imported) or <c>unknown</c> (install it from a .vsix).
    /// </summary>
    public JsonObject Recommended(IdeFiles files)
    {
        string? text;
        try
        {
            text = files.Read(".vscode/extensions.json").Str("text");
        }
        catch (IdeError e) when (e.Status == 404)
        {
            return new JsonObject { ["file"] = false, ["recommendations"] = new JsonArray(), ["unwanted"] = new JsonArray() };
        }
        var node = Json.ParseJsonc(text, out var error);
        if (node is not JsonObject json)
        {
            return new JsonObject { ["file"] = true, ["error"] = error ?? ".vscode/extensions.json holds no object.", ["recommendations"] = new JsonArray(), ["unwanted"] = new JsonArray() };
        }
        Dictionary<string, string> kept;
        lock (_gate)
        {
            kept = Current().ToDictionary(e => e.Str("id")!.ToLowerInvariant(), e => e.Str("version")!, StringComparer.Ordinal);
        }
        Dictionary<string, LocalCopy>? local = null;
        var list = new JsonArray();
        foreach (var raw in Ids(json["recommendations"]).Take(200))
        {
            var id = raw.ToLowerInvariant();
            var item = new JsonObject { ["id"] = raw };
            if (kept.TryGetValue(id, out var version))
            {
                (item["status"], item["note"]) = ("installed", $"Installed here ({version}).");
            }
            else if (BuiltIn.TryGetValue(id, out var how))
            {
                (item["status"], item["note"]) = ("built-in", how);
            }
            else if (CodeOnly.TryGetValue(id, out var instead))
            {
                (item["status"], item["note"]) = ("unsupported", instead);
            }
            else
            {
                local ??= LocalCopies().GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Version, VersionOrder.Instance).First(), StringComparer.Ordinal);
                if (local.TryGetValue(id, out var copy) && copy.Manifest is { } m)
                {
                    item["name"] = m.DisplayName;
                    if (m.Usable)
                    {
                        (item["status"], item["note"]) = ("local", $"{copy.From} has it on this computer ({m.Version}): import it.");
                    }
                    else
                    {
                        (item["status"], item["note"]) = ("unsupported", NothingUsable(m));
                    }
                }
                else
                {
                    (item["status"], item["note"]) = ("unknown", "Not on this computer: install it from its .vsix (Open VSX or the Marketplace's page has one).");
                }
            }
            list.Add(item);
        }
        return new JsonObject { ["file"] = true, ["recommendations"] = list, ["unwanted"] = new JsonArray([.. Ids(json["unwantedRecommendations"]).Select(x => (JsonNode)x)]) };
    }

    private static IEnumerable<string> Ids(JsonNode? node) =>
        (node as JsonArray ?? []).Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null).OfType<string>().Where(s => ExtensionId().IsMatch(s)).Distinct(StringComparer.OrdinalIgnoreCase);

    // -------------------------------------------------------------- the manifest

    /// <summary>
    /// An extension's package.json as Code Arena takes it: who it is, what it contributes that is used here (in the
    /// page's words), what is not, and the files that takes.
    /// </summary>
    private sealed record Manifest(string Id, string Name, string Publisher, string Version, string DisplayName, string? Description, string? Icon, JsonObject Contributes, List<string> Unused, SortedSet<string> Files, bool Usable, string Has);

    /// <summary>Where an extension's files are read from: a .vsix, or a folder of VS Code's.</summary>
    private interface ISource
    {
        /// <summary>A file by its path in the extension ("themes/dark.json"); null when it is not there. Throws IdeError for one that is a link or too large.</summary>
        byte[]? Read(string path);
    }

    /// <summary>A .vsix: its files under extension/, refused whole when a name leads out of it or one is a link.</summary>
    private sealed class VsixSource : ISource
    {
        private readonly Dictionary<string, ZipArchiveEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public VsixSource(ZipArchive zip)
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var parts = name.Split('/');
                if (name.StartsWith('/') || name.Contains('\0') || parts[0].Contains(':') || parts.Any(p => p == ".."))
                {
                    throw new IdeError(422, "unsafe", $"The .vsix holds a file whose name leads out of it ({Fmt.OneLine(entry.FullName, 120)}): it is refused.");
                }
                // A link's type is in the high bits of a Unix entry's attributes (S_IFLNK).
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    throw new IdeError(422, "unsafe", $"The .vsix holds a link ({Fmt.OneLine(entry.FullName, 120)}): links are not taken.");
                }
                if (name.StartsWith("extension/", StringComparison.OrdinalIgnoreCase) && !name.EndsWith('/'))
                {
                    _entries.TryAdd(name[10..], entry);
                }
            }
        }

        public byte[]? Read(string path)
        {
            if (!_entries.TryGetValue(path, out var entry))
            {
                return null;
            }
            if (entry.Length > MaxFile)
            {
                throw new IdeError(413, "too_large", $"{path} is larger than {MaxFile / 1024 / 1024} MB.");
            }
            using var stream = entry.Open();
            return Capped(stream, path);
        }
    }

    /// <summary>A folder of VS Code's: files inside it only, none through a link.</summary>
    private sealed class FolderSource(string dir) : ISource
    {
        private readonly string _dir = Path.GetFullPath(dir);

        public byte[]? Read(string path)
        {
            var full = Path.GetFullPath(Path.Combine(_dir, path));
            if (!Workspace.Within(full, _dir) || full == _dir)
            {
                return null;
            }
            for (var at = full; at.Length > _dir.Length; at = Path.GetDirectoryName(at)!)
            {
                var info = new FileInfo(at);
                if (info.Exists || Directory.Exists(at))
                {
                    if (info.LinkTarget is not null)
                    {
                        throw new IdeError(422, "unsafe", $"{path} is a link (or in one): links are not taken.");
                    }
                }
            }
            if (!System.IO.File.Exists(full))
            {
                return null;
            }
            if (!Files.Regular(full))
            {
                throw new IdeError(422, "unsafe", $"{path} is not a regular file.");
            }
            if (new FileInfo(full).Length > MaxFile)
            {
                throw new IdeError(413, "too_large", $"{path} is larger than {MaxFile / 1024 / 1024} MB.");
            }
            using var stream = System.IO.File.OpenRead(full);
            return Capped(stream, path);
        }
    }

    /// <summary>A stream's bytes, refused past <see cref="MaxFile"/> (what a header said is not trusted).</summary>
    private static byte[] Capped(Stream stream, string path)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            copy.Write(buffer, 0, n);
            if (copy.Length > MaxFile)
            {
                throw new IdeError(413, "too_large", $"{path} is larger than {MaxFile / 1024 / 1024} MB.");
            }
        }
        return copy.ToArray();
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,127}\.[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex ExtensionId();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,127}$")]
    private static partial Regex PublisherName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex PackageName();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}$")]
    private static partial Regex VersionName();

    /// <summary>A folder of VS Code's extensions: publisher.name-version, then the platform it is for, if any.</summary>
    [GeneratedRegex(@"^(.+?)-(\d[^-]*)(-.*)?$")]
    private static partial Regex FolderName();

    [GeneratedRegex(@"^%([^%]+)%$")]
    private static partial Regex Placeholder();

    /// <summary>
    /// package.json read, its %placeholders% filled from package.nls.json; with <paramref name="deep"/>, the theme and
    /// icon theme files too, for the files they name in turn (includes, icons, fonts).
    /// </summary>
    private static Manifest ReadManifest(ISource source, bool deep)
    {
        var bytes = source.Read("package.json") ?? throw new IdeError(400, "invalid", "There is no extension/package.json: this is not a VS Code extension.");
        if (Json.ParseJsonc(Encoding.UTF8.GetString(bytes), out var error) is not JsonObject raw)
        {
            throw new IdeError(400, "invalid", $"Its package.json is not JSON: {error ?? "it holds no object"}.");
        }
        var nls = source.Read("package.nls.json") is { } n ? Json.ParseJsonc(Encoding.UTF8.GetString(n), out _) as JsonObject : null;
        var json = Localized(raw, nls ?? []) as JsonObject ?? [];
        var publisher = json.Str("publisher") ?? "";
        var name = json.Str("name") ?? "";
        var version = json.Str("version") ?? "";
        if (!PublisherName().IsMatch(publisher) || !PackageName().IsMatch(name))
        {
            throw new IdeError(400, "invalid", "Its package.json does not name its publisher and name as VS Code's do (letters, digits and -).");
        }
        if (!VersionName().IsMatch(version))
        {
            throw new IdeError(400, "invalid", "Its package.json does not give a version as VS Code's do (1.2.3).");
        }
        var id = $"{publisher}.{name}".ToLowerInvariant();
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var unused = new List<string>();
        var given = json["contributes"] as JsonObject ?? [];
        var contributes = new JsonObject();

        // Paths a contribution names, relative to the extension (or to the file that names them), kept when they stay inside it.
        string? Keep(string? at, string? path, string what, bool known = true)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            var inside = Inside(at ?? "", path);
            if (inside is null)
            {
                unused.Add($"{what}: {Fmt.OneLine(path, 80)} leads outside the extension");
                return null;
            }
            if (known && TypeOf(inside) is null)
            {
                unused.Add($"{what}: {Fmt.OneLine(path, 80)} is not a kind of file Code Arena reads");
                return null;
            }
            files.Add(inside);
            return inside;
        }

        var themes = new JsonArray();
        foreach (var t in Objects(given["themes"]))
        {
            var label = t.Str("label") ?? t.Str("id") ?? Path.GetFileNameWithoutExtension(t.Str("path") ?? "theme");
            var path = t.Str("path") ?? "";
            if (path.EndsWith(".tmTheme", StringComparison.OrdinalIgnoreCase))
            {
                unused.Add($"theme {label}: .tmTheme (XML) colour themes are not read, only JSON ones");
                continue;
            }
            if (Keep(null, path, $"theme {label}") is not { } kept)
            {
                continue;
            }
            var ui = t.Str("uiTheme") is "vs" or "vs-dark" or "hc-black" or "hc-light" ? t.Str("uiTheme") : "vs-dark";
            themes.Add(new JsonObject { ["label"] = label, ["uiTheme"] = ui, ["path"] = kept, ["id"] = t.Str("id") });
            if (deep)
            {
                ThemeFiles(source, kept, label, unused, Keep, [kept]);
            }
        }
        contributes["themes"] = themes;

        var iconThemes = new JsonArray();
        foreach (var t in Objects(given["iconThemes"]))
        {
            var themeId = t.Str("id") ?? "";
            var label = t.Str("label") ?? themeId;
            if (themeId.Length == 0 || Keep(null, t.Str("path"), $"icon theme {label}") is not { } kept)
            {
                continue;
            }
            iconThemes.Add(new JsonObject { ["id"] = themeId, ["label"] = label, ["path"] = kept });
            if (deep && source.Read(kept) is { } icons && Json.ParseJsonc(Encoding.UTF8.GetString(icons), out _) is JsonObject iconTheme)
            {
                var dir = Dir(kept);
                foreach (var (_, def) in iconTheme["iconDefinitions"] as JsonObject ?? [])
                {
                    Keep(dir, def.Str("iconPath"), $"icon theme {label}");
                }
                foreach (var font in Objects(iconTheme["fonts"]))
                {
                    foreach (var src in Objects(font["src"]))
                    {
                        Keep(dir, src.Str("path"), $"icon theme {label}");
                    }
                }
            }
        }
        contributes["iconThemes"] = iconThemes;

        var snippets = new JsonArray();
        foreach (var s in Objects(given["snippets"]))
        {
            if (Keep(null, s.Str("path"), $"snippets for {s.Str("language") ?? "every language"}") is { } kept)
            {
                snippets.Add(new JsonObject { ["language"] = s.Str("language"), ["path"] = kept });
            }
        }
        contributes["snippets"] = snippets;

        var languages = new JsonArray();
        foreach (var l in Objects(given["languages"]))
        {
            if (l.Str("id") is not { Length: > 0 } lang)
            {
                continue;
            }
            var language = new JsonObject { ["id"] = lang };
            foreach (var key in new[] { "extensions", "filenames", "filenamePatterns", "aliases", "mimetypes" })
            {
                language[key] = new JsonArray([.. Strings(l[key]).Select(x => (JsonNode)x)]);
            }
            language["firstLine"] = l.Str("firstLine");
            language["configuration"] = Keep(null, l.Str("configuration"), $"language {lang}'s configuration");
            languages.Add(language);
        }
        contributes["languages"] = languages;

        var grammars = new JsonArray();
        foreach (var g in Objects(given["grammars"]))
        {
            if (Keep(null, g.Str("path"), $"grammar {g.Str("scopeName")}") is { } kept)
            {
                grammars.Add(new JsonObject { ["language"] = g.Str("language"), ["scopeName"] = g.Str("scopeName"), ["path"] = kept });
            }
        }
        contributes["grammars"] = grammars;
        if (grammars.Count > 0)
        {
            unused.Add("grammars: kept, not used yet (TextMate highlighting comes later; the editor's own highlighting is used)");
        }

        // A keybinding for one of its own commands needs its code; one for VS Code's (a keymap's) may be Code Arena's too.
        var own = Objects(given["commands"]).Select(c => c.Str("command")).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var keys = new JsonArray();
        var ownKeys = 0;
        foreach (var k in given["keybindings"] is JsonObject single ? [single] : Objects(given["keybindings"]))
        {
            var command = k.Str("command");
            if (string.IsNullOrEmpty(command) || command.StartsWith('-'))
            {
                continue;
            }
            if (own.Contains(command))
            {
                ownKeys++;
                continue;
            }
            var rule = new JsonObject { ["command"] = command };
            foreach (var key in new[] { "key", "mac", "linux", "win", "when" })
            {
                if (k.Str(key) is { Length: > 0 } v)
                {
                    rule[key] = v;
                }
            }
            if (k["args"] is { } args)
            {
                rule["args"] = args.DeepClone();
            }
            if (rule["key"] is not null)
            {
                keys.Add(rule);
            }
        }
        contributes["keybindings"] = keys;
        if (ownKeys > 0)
        {
            unused.Add($"keybindings: {ownKeys} for its own commands, which need its code");
        }

        contributes["configurationDefaults"] = given["configurationDefaults"] is JsonObject defaults ? defaults.Clone() : new JsonObject();

        foreach (var (key, _) in given)
        {
            if (!Used.Contains(key))
            {
                unused.Add($"{key}: {Why.GetValueOrDefault(key, "needs VS Code's extension host (its code)")}");
            }
        }
        var code = new[] { "main", "browser" }.Select(k => json.Str(k) is { } m ? $"{k}: {m}" : null).OfType<string>().ToList();
        if (code.Count > 0)
        {
            unused.Insert(0, $"its code ({string.Join(", ", code)}): Code Arena runs no extension's code");
        }

        var icon = json.Str("icon") is { } i && Inside("", i) is { } iconPath && Path.GetExtension(iconPath).ToLowerInvariant() is ".png" or ".svg" or ".jpg" or ".jpeg" ? iconPath : null;
        if (icon is not null)
        {
            files.Add(icon);
        }
        var usable = Brings(contributes);
        var has = code.Count > 0 ? "its code" : given.Count > 0 ? string.Join(", ", given.Select(p => p.Key)) : "nothing it contributes";
        return new Manifest(id, name, publisher, version, json.Str("displayName") ?? name, json.Str("description"), icon, contributes, unused, files, usable, has);
    }

    /// <summary>A colour theme's file and what it takes in: its "include" (and that one's), and "tokenColors" given as a file.</summary>
    private static void ThemeFiles(ISource source, string path, string label, List<string> unused, Func<string?, string?, string, bool, string?> keep, HashSet<string> seen)
    {
        if (seen.Count > 10 || source.Read(path) is not { } bytes || Json.ParseJsonc(Encoding.UTF8.GetString(bytes), out _) is not JsonObject theme)
        {
            return;
        }
        var dir = Dir(path);
        if (theme.Str("include") is { } include && keep(dir, include, $"theme {label}", true) is { } included && seen.Add(included))
        {
            ThemeFiles(source, included, label, unused, keep, seen);
        }
        if (theme.Str("tokenColors") is { } tokens)
        {
            if (tokens.EndsWith(".tmTheme", StringComparison.OrdinalIgnoreCase))
            {
                unused.Add($"theme {label}: its tokenColors are a .tmTheme (XML), which is not read");
            }
            else
            {
                keep(dir, tokens, $"theme {label}", true);
            }
        }
    }

    /// <summary>A path a manifest gives ("./themes/a.json"), from a folder of the extension's, as its path in it; null when it leads out.</summary>
    internal static string? Inside(string from, string path)
    {
        if (path.Length == 0 || path.Length > 1024 || path.Contains('\0') || path.Contains(':') || path.Contains('?') || path.Contains('#'))
        {
            return null;
        }
        var p = path.Replace('\\', '/');
        if (p.StartsWith('/'))
        {
            return null;
        }
        var parts = from.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var part in p.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }
            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string? TypeOf(string path)
    {
        var name = path.ToLowerInvariant();
        if (name.EndsWith(".tmlanguage.json", StringComparison.Ordinal))
        {
            return Types[".json"];
        }
        return Types.GetValueOrDefault(Path.GetExtension(name));
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node) => (node as JsonArray ?? []).OfType<JsonObject>();

    private static IEnumerable<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().Take(500);

    /// <summary>Every "%key%" text replaced by package.nls.json's message for it.</summary>
    private static JsonNode? Localized(JsonNode? node, JsonObject nls)
    {
        switch (node)
        {
            case JsonObject o:
            {
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                {
                    copy[k] = Localized(v, nls);
                }
                return copy;
            }
            case JsonArray a:
                return new JsonArray([.. a.Select(x => Localized(x, nls))]);
            case JsonValue v when v.TryGetValue<string>(out var s) && Placeholder().Match(s) is { Success: true } m:
            {
                var said = nls[m.Groups[1].Value];
                return said is JsonObject message ? message.Str("message") ?? s : said is JsonValue text && text.TryGetValue<string>(out var t) ? t : s;
            }
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>Whether contributions as the page reads them hold anything Code Arena uses.</summary>
    private static bool Brings(JsonObject contributes) =>
        new[] { "themes", "iconThemes", "snippets", "languages", "grammars", "keybindings" }.Any(k => contributes[k] is JsonArray { Count: > 0 })
        || contributes["configurationDefaults"] is JsonObject { Count: > 0 };

    /// <summary>Contributions whose file is not in the extension left out (a language keeps its names, without its configuration).</summary>
    private static void Prune(JsonObject contributes, HashSet<string> missing)
    {
        foreach (var kind in new[] { "themes", "iconThemes", "snippets", "grammars" })
        {
            if (contributes[kind] is JsonArray list)
            {
                foreach (var gone in list.OfType<JsonObject>().Where(c => c.Str("path") is { } p && missing.Contains(p)).ToList())
                {
                    list.Remove(gone);
                }
            }
        }
        foreach (var language in (contributes["languages"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (language.Str("configuration") is { } p && missing.Contains(p))
            {
                language["configuration"] = null;
            }
        }
    }

    /// <summary>Why an extension is refused: it has nothing Code Arena uses.</summary>
    private static string NothingUsable(Manifest m) =>
        $"{m.DisplayName} has nothing Code Arena can use ({m.Has}). Code Arena takes only what extensions declare: colour and file icon themes, snippets, languages, grammars, keybindings and configuration defaults; it runs no extension's code.";

    // --------------------------------------------------------------- keeping

    /// <summary>The extension's files copied into its folder, in place of any other version of it, and listed.</summary>
    private JsonObject Store(ISource source, Manifest m, string from)
    {
        if (!m.Usable)
        {
            throw new IdeError(422, "nothing_usable", NothingUsable(m), new JsonObject { ["unused"] = new JsonArray([.. m.Unused.Select(u => (JsonNode)u)]) });
        }
        var wanted = m.Files.ToList();
        if (wanted.Count > MaxFiles)
        {
            throw new IdeError(413, "too_large", $"{m.DisplayName} names {wanted.Count} files: {MaxFiles} are kept at most.");
        }
        Directory.CreateDirectory(_root);
        var temp = Path.Combine(_root, $".new-{Guid.NewGuid():N}");
        var kept = new List<string>();
        try
        {
            long total = 0;
            foreach (var file in wanted)
            {
                if (source.Read(file) is not { } bytes)
                {
                    m.Unused.Add($"{file}: named, but not in the extension");
                    continue;
                }
                total += bytes.Length;
                if (total > MaxTotal)
                {
                    throw new IdeError(413, "too_large", $"{m.DisplayName}'s files hold more than {MaxTotal / 1024 / 1024} MB.");
                }
                var to = Path.Combine(temp, file);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    System.IO.File.WriteAllBytes(to, bytes);
                }
                catch (IOException e)
                {
                    // A name that is both a file and a folder of it ("a" and "a/b"), or one this computer's files cannot have.
                    throw new IdeError(422, "invalid", $"{file} cannot be kept: {e.Message}");
                }
                kept.Add(file);
            }
            Directory.CreateDirectory(temp);
            var contributes = m.Contributes.Clone();
            if (kept.Count < wanted.Count)
            {
                Prune(contributes, [.. wanted.Except(kept)]);
                if (!Brings(contributes))
                {
                    throw new IdeError(422, "nothing_usable", NothingUsable(m), new JsonObject { ["unused"] = new JsonArray([.. m.Unused.Select(u => (JsonNode)u)]) });
                }
            }
            var dir = $"{m.Id}-{m.Version}";
            var info = new JsonObject
            {
                ["name"] = m.Name,
                ["publisher"] = m.Publisher,
                ["displayName"] = m.DisplayName,
                ["description"] = m.Description,
                ["icon"] = m.Icon is { } icon && kept.Contains(icon) ? icon : null,
                ["contributes"] = contributes,
                ["unused"] = new JsonArray([.. m.Unused.Select(u => (JsonNode)u)]),
            };
            lock (_gate)
            {
                var all = Load();
                var before = all.FirstOrDefault(e => e.Str("id") == m.Id);
                var entry = new JsonObject
                {
                    ["id"] = m.Id,
                    ["version"] = m.Version,
                    ["dir"] = dir,
                    ["enabled"] = before?.Bool("enabled") ?? true,
                    ["source"] = from,
                    ["installed"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["files"] = new JsonArray([.. kept.Select(f => (JsonNode)f)]),
                    ["info"] = info,
                };
                if (before is not null)
                {
                    Remove(before.Str("dir"));
                    all[all.IndexOf(before)] = entry;
                }
                else
                {
                    all.Add(entry);
                }
                Remove(dir);
                Directory.Move(temp, Path.Combine(_root, dir));
                Save(all);
                return Item(entry);
            }
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    /// <summary>An extension's folder taken away (one of the list's only: never anything else of the data folder).</summary>
    private void Remove(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || dir.Contains('/') || dir.Contains('\\') || dir.StartsWith('.'))
        {
            return;
        }
        var full = Path.Combine(_root, dir);
        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another program holds a file in it: the list no longer names it, and a later install replaces it.
        }
    }

    /// <summary>An entry of the list as the page reads it.</summary>
    private static JsonObject Item(JsonObject e)
    {
        var info = e["info"] as JsonObject ?? [];
        var contributes = (info["contributes"] as JsonObject ?? []).Clone();
        contributes["keybindings"] = (contributes["keybindings"] as JsonArray)?.Count ?? 0;
        return new JsonObject
        {
            ["id"] = e.Str("id"),
            ["name"] = info.Str("name"),
            ["displayName"] = info.Str("displayName"),
            ["version"] = e.Str("version"),
            ["publisher"] = info.Str("publisher"),
            ["description"] = info.Str("description"),
            ["icon"] = info.Str("icon"),
            ["enabled"] = e.Bool("enabled") != false,
            ["source"] = e.Str("source"),
            ["contributes"] = contributes,
            ["unused"] = info["unused"]?.DeepClone() ?? new JsonArray(),
        };
    }

    /// <summary>A manifest as /local lists it: the list's shape, without what only kept ones have.</summary>
    private static JsonObject Summary(Manifest m)
    {
        var contributes = m.Contributes.Clone();
        contributes["keybindings"] = (contributes["keybindings"] as JsonArray)?.Count ?? 0;
        return new JsonObject
        {
            ["id"] = m.Id,
            ["name"] = m.Name,
            ["displayName"] = m.DisplayName,
            ["version"] = m.Version,
            ["publisher"] = m.Publisher,
            ["description"] = m.Description,
            ["contributes"] = contributes,
            ["unused"] = new JsonArray([.. m.Unused.Select(u => (JsonNode)u)]),
        };
    }

    private static JsonObject Find(List<JsonObject> all, string? id) =>
        all.FirstOrDefault(e => string.Equals(e.Str("id"), id?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new IdeError(404, "not_found", $"There is no extension {id} here.");

    /// <summary>The list as kept (not to be changed: <see cref="Load"/> gives copies), read again when its file changed. Under the gate.</summary>
    private List<JsonObject> Current()
    {
        var info = new FileInfo(Path.Combine(_root, ListFile));
        var stamp = info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1L);
        if (_list is null || stamp != _stamp)
        {
            _list = [.. (Json.Parse(ReadText(info.FullName)) as JsonArray ?? []).OfType<JsonObject>()
                .Where(e => e.Str("id") is not null && e.Str("version") is not null && e.Str("dir") is { } d && !d.Contains('/') && !d.Contains('\\') && !d.StartsWith('.'))
                .DistinctBy(e => e.Str("id"), StringComparer.OrdinalIgnoreCase)];
            _stamp = stamp;
        }
        return _list;
    }

    private List<JsonObject> Load() => [.. Current().Select(e => e.Clone())];

    private void Save(List<JsonObject> all)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, ListFile);
        // Whole or not at all: another run of code-arena may read it meanwhile.
        var temp = $"{file}.{Environment.ProcessId}.tmp";
        System.IO.File.WriteAllText(temp, new JsonArray([.. all.Select(e => (JsonNode)e.Clone())]).ToJsonString(Json.Indented));
        System.IO.File.Move(temp, file, overwrite: true);
        _list = null;
    }

    private static string? ReadText(string file)
    {
        try
        {
            return System.IO.File.Exists(file) && new FileInfo(file).Length <= MaxFile ? System.IO.File.ReadAllText(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A file of the extensions' folder, read no further than <see cref="MaxFile"/>; null when it is not there.</summary>
    private static byte[]? ReadCapped(string full)
    {
        try
        {
            if (!System.IO.File.Exists(full) || new FileInfo(full).LinkTarget is not null)
            {
                return null;
            }
            using var stream = System.IO.File.OpenRead(full);
            return Capped(stream, Path.GetFileName(full));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Versions as VS Code compares them: their numbers first (1.10 after 1.9), then as text.</summary>
    private sealed class VersionOrder : IComparer<string>
    {
        public static readonly VersionOrder Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Numbers(x);
            var b = Numbers(y);
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
                if (c != 0)
                {
                    return c;
                }
            }
            return string.CompareOrdinal(x, y);
        }

        private static long[] Numbers(string? v) =>
            [.. (v ?? "").Split('-', '+')[0].Split('.').Select(p => long.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0)];
    }
}
