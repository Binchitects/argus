using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>
/// VS Code extensions as the IDE takes them: .vsix files built here (a zip with extension/package.json), and a fake
/// ~/.vscode/extensions (HOME set for the run). Only what they declare is kept and served; their code never is.
/// </summary>
public sealed class EditorExtensionTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    /// <summary>An extension with a colour theme (with an include), a file icon theme, snippets, a language and its grammar, keys, configuration defaults, code, and files nothing names.</summary>
    private static (string Name, string Text)[] Night(string version = "1.2.0") =>
    [
        ("extension.vsixmanifest", "<PackageManifest/>"),
        ("extension/package.json", $$"""
            {
              "publisher": "Acme", "name": "night", "version": "{{version}}", "displayName": "%displayName%", "description": "A dark theme.",
              "main": "./out/extension.js", "icon": "images/icon.png",
              "contributes": {
                "themes": [
                  { "label": "%theme.label%", "uiTheme": "vs-dark", "path": "./themes/night.json" },
                  { "label": "Old", "uiTheme": "vs", "path": "./themes/old.tmTheme" }
                ],
                "iconThemes": [{ "id": "night-icons", "label": "Night Icons", "path": "./icons/theme.json" }],
                "snippets": [{ "language": "python", "path": "./snippets/python.code-snippets" }],
                "languages": [{ "id": "toml", "extensions": [".toml"], "aliases": ["TOML"], "configuration": "./toml.language-configuration.json" }],
                "grammars": [{ "language": "toml", "scopeName": "source.toml", "path": "./syntaxes/toml.tmLanguage.json" }],
                "commands": [{ "command": "night.own", "title": "Its own" }],
                "keybindings": [
                  { "key": "ctrl+alt+n", "mac": "cmd+alt+n", "command": "workbench.action.togglePanel", "when": "editorIsOpen" },
                  { "key": "ctrl+alt+m", "command": "night.own" }
                ],
                "configurationDefaults": { "[toml]": { "editor.tabSize": 2 } },
                "menus": { "editor/title": [] }
              }
            }
            """),
        ("extension/package.nls.json", """{ "displayName": "Night", "theme.label": { "message": "Night Sky", "comment": ["the theme"] } }"""),
        ("extension/themes/night.json", """
            {
              // VS Code's themes are JSON with comments.
              "include": "./base.json",
              "colors": { "editor.background": "#101020" },
              "tokenColors": [{ "scope": "comment", "settings": { "foreground": "#808080" } }],
            }
            """),
        ("extension/themes/base.json", """{ "colors": { "editor.foreground": "#e0e0e0" } }"""),
        ("extension/themes/old.tmTheme", "<plist/>"),
        ("extension/icons/theme.json", """
            {
              "iconDefinitions": { "_file": { "iconPath": "./file.svg" }, "_ts": { "fontCharacter": "\\E001", "fontId": "glyphs" } },
              "fonts": [{ "id": "glyphs", "src": [{ "path": "./glyphs.woff2", "format": "woff2" }] }],
              "file": "_file",
              "fileExtensions": { "ts": "_ts" }
            }
            """),
        ("extension/icons/file.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"),
        ("extension/icons/glyphs.woff2", "wOF2"),
        ("extension/icons/unused.svg", "<svg/>"),
        ("extension/images/icon.png", "PNG"),
        ("extension/snippets/python.code-snippets", """{ "Main": { "prefix": "main", "body": ["if __name__ == '__main__':", "\t$0"] } }"""),
        ("extension/toml.language-configuration.json", """{ "comments": { "lineComment": "#" }, "brackets": [["[", "]"]] }"""),
        ("extension/syntaxes/toml.tmLanguage.json", """{ "scopeName": "source.toml", "patterns": [] }"""),
        ("extension/out/extension.js", "require('child_process').exec('rm -rf ~')"),
        ("extension/README.md", "# Night"),
    ];

    private static byte[] Vsix(IEnumerable<(string Name, string Text)> files, Action<ZipArchive>? more = null)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }
            more?.Invoke(zip);
        }
        return memory.ToArray();
    }

    private static async Task<HttpResponseMessage> Install(WebRun web, byte[] vsix, string type = "application/octet-stream")
    {
        using var content = new ByteArrayContent(vsix);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        var response = await web.Http.PostAsync("/api/extensions/install", content);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    private static async Task<JsonObject> Json(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

    private static string[] Texts(JsonNode? node) => [.. node!.AsArray().Select(x => x!.GetValue<string>())];

    /// <summary>The files kept in an extension's folder, by their paths in it.</summary>
    private static string[] Kept(Harness h, string dir)
    {
        var root = Path.Combine(h.Paths.DataDir, "extensions", dir);
        return [.. Directory.GetFiles(root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task A_vsix_is_kept_with_only_the_files_its_contributions_name_and_says_what_is_not_used()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        var response = await Install(web, Vsix(Night()));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await Json(response);
        Assert.Equal("acme.night", item["id"]!.GetValue<string>());
        Assert.Equal("Night", item["displayName"]!.GetValue<string>());
        Assert.Equal("1.2.0", item["version"]!.GetValue<string>());
        Assert.Equal("Acme", item["publisher"]!.GetValue<string>());
        Assert.True(item["enabled"]!.GetValue<bool>());
        Assert.Equal("vsix", item["source"]!.GetValue<string>());
        Assert.Equal("images/icon.png", item["icon"]!.GetValue<string>());
        var contributes = item["contributes"]!;
        Assert.Equal("""[{"label":"Night Sky","uiTheme":"vs-dark","path":"themes/night.json","id":null}]""", contributes["themes"]!.ToJsonString());
        Assert.Equal("""[{"id":"night-icons","label":"Night Icons","path":"icons/theme.json"}]""", contributes["iconThemes"]!.ToJsonString());
        Assert.Equal("""[{"language":"python","path":"snippets/python.code-snippets"}]""", contributes["snippets"]!.ToJsonString());
        Assert.Equal("toml.language-configuration.json", contributes["languages"]![0]!["configuration"]!.GetValue<string>());
        Assert.Equal([".toml"], Texts(contributes["languages"]![0]!["extensions"]));
        Assert.Equal("source.toml", contributes["grammars"]![0]!["scopeName"]!.GetValue<string>());
        // Its keys for VS Code's commands, not those for its own (they need its code).
        Assert.Equal(1, contributes["keybindings"]!.GetValue<int>());
        Assert.Equal("""{"[toml]":{"editor.tabSize":2}}""", contributes["configurationDefaults"]!.ToJsonString());
        var unused = Texts(item["unused"]);
        Assert.StartsWith("its code (main: ./out/extension.js): Code Arena runs no extension's code", unused[0]);
        Assert.Contains("theme Old: .tmTheme (XML) colour themes are not read, only JSON ones", unused);
        Assert.Contains(unused, u => u.StartsWith("grammars: kept, not used yet", StringComparison.Ordinal));
        Assert.Contains("keybindings: 1 for its own commands, which need its code", unused);
        Assert.Contains("menus: needs VS Code's extension host (its code)", unused);
        Assert.Contains(unused, u => u.StartsWith("commands:", StringComparison.Ordinal));

        // Only what the contributions name: the theme and what it includes, the icons and the font the icon theme names. No code, no manifest.
        Assert.Equal(
            ["icons/file.svg", "icons/glyphs.woff2", "icons/theme.json", "images/icon.png", "snippets/python.code-snippets", "syntaxes/toml.tmLanguage.json", "themes/base.json", "themes/night.json", "toml.language-configuration.json"],
            Kept(h, "acme.night-1.2.0"));
        Assert.Equal("acme.night", (await web.GetJsonAsync("/api/extensions"))[0]!["id"]!.GetValue<string>());

        // What the page applies: the enabled extensions' contributions, each saying whose it is; the keys whole.
        var all = await web.GetJsonAsync("/api/extensions/contributions");
        Assert.Equal("acme.night", all["themes"]![0]!["extension"]!.GetValue<string>());
        Assert.Equal("""[{"command":"workbench.action.togglePanel","key":"ctrl+alt+n","mac":"cmd+alt+n","when":"editorIsOpen","extension":"acme.night","version":"1.2.0"}]""", all["keybindings"]!.ToJsonString(CodeArena.Json.Relaxed));
        Assert.Equal("""[{"extension":"acme.night","version":"1.2.0","values":{"[toml]":{"editor.tabSize":2}}}]""", all["configurationDefaults"]!.ToJsonString());

        // A file it uses: its type, never run as a page of this origin's.
        var theme = await web.Http.GetAsync("/api/extensions/file?id=acme.night&path=themes/night.json");
        Assert.Equal(HttpStatusCode.OK, theme.StatusCode);
        Assert.Equal("application/json", theme.Content.Headers.ContentType!.MediaType);
        Assert.Contains("\"include\": \"./base.json\"", await theme.Content.ReadAsStringAsync());
        var svg = await web.Http.GetAsync("/api/extensions/file?id=acme.night&path=icons/file.svg");
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType!.MediaType);
        Assert.Equal("sandbox; default-src 'none'", svg.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", svg.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("font/woff2", (await web.Http.GetAsync("/api/extensions/file?id=acme.night&path=icons/glyphs.woff2")).Content.Headers.ContentType!.MediaType);
        // Nothing else: not its code, not a file it does not use, not the list, not Code Arena's own files.
        foreach (var path in new[] { "out/extension.js", "icons/unused.svg", "package.json", "../installed.json", "../../config/config.json", "/etc/passwd", "themes/../themes/night.json", "" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync($"/api/extensions/file?id=acme.night&path={Uri.EscapeDataString(path)}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/extensions/file?id=acme.other&path=themes/night.json")).StatusCode);

        // Off: its contributions no longer apply, and it stays off through an update, which replaces the version before.
        var off = await web.PostAsync("/api/extensions/enable", new JsonObject { ["id"] = "acme.night", ["enabled"] = false });
        Assert.False((await Json(off))["enabled"]!.GetValue<bool>());
        Assert.Empty((await web.GetJsonAsync("/api/extensions/contributions"))["themes"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/extensions/enable", new JsonObject { ["id"] = "acme.night" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/extensions/enable", new JsonObject { ["id"] = "acme.nope", ["enabled"] = true })).StatusCode);
        var update = await Json(await Install(web, Vsix(Night("1.3.0"))));
        Assert.Equal("1.3.0", update["version"]!.GetValue<string>());
        Assert.False(update["enabled"]!.GetValue<bool>());
        Assert.Single((await web.GetJsonAsync("/api/extensions")).AsArray());
        Assert.False(Directory.Exists(Path.Combine(h.Paths.DataDir, "extensions", "acme.night-1.2.0")));
        Assert.True(Directory.Exists(Path.Combine(h.Paths.DataDir, "extensions", "acme.night-1.3.0")));

        // Uninstalled: its folder goes with it.
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsync("/api/extensions/uninstall", new JsonObject { ["id"] = "acme.night" })).StatusCode);
        Assert.Empty((await web.GetJsonAsync("/api/extensions")).AsArray());
        Assert.False(Directory.Exists(Path.Combine(h.Paths.DataDir, "extensions", "acme.night-1.3.0")));
        Assert.Equal(HttpStatusCode.NotFound, (await web.Http.GetAsync("/api/extensions/file?id=acme.night&path=themes/night.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/extensions/uninstall", new JsonObject { ["id"] = "acme.night" })).StatusCode);
    }

    [Fact]
    public async Task A_vsix_whose_names_lead_out_of_it_that_holds_a_link_or_is_too_large_is_refused()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        // Zip slip: a name that climbs out, wherever it is in the archive, refuses the whole .vsix.
        foreach (var evil in new[] { "extension/../../evil.txt", "../evil.txt", "/tmp/evil.txt", "extension\\..\\..\\evil.txt", "C:/evil.txt" })
        {
            var refused = await Install(web, Vsix([.. Night(), (evil, "pwned")]));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Equal("unsafe", (await Json(refused))["status"]!.GetValue<string>());
        }
        // A link, as a Unix zip stores one (S_IFLNK in the attributes' high bits).
        var link = await Install(web, Vsix(Night().Where(f => f.Name != "extension/themes/base.json"), zip =>
        {
            var entry = zip.CreateEntry("extension/themes/base.json");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var w = new StreamWriter(entry.Open());
            w.Write("/etc/passwd");
        }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, link.StatusCode);
        Assert.Contains("link", (await Json(link))["error"]!.GetValue<string>());
        // A path in package.json that leads out of the extension is not followed: that theme is left out, said why.
        var outside = Night().Select(f => f.Name == "extension/package.json" ? (f.Name, f.Text.Replace("./themes/night.json", "../../../config/config.json")) : f);
        var kept = await Json(await Install(web, Vsix(outside)));
        Assert.Empty(kept["contributes"]!["themes"]!.AsArray());
        Assert.Contains("theme Night Sky: ../../../config/config.json leads outside the extension", Texts(kept["unused"]));
        Assert.DoesNotContain(Kept(h, "acme.night-1.2.0"), f => f.Contains("config.json", StringComparison.Ordinal) && !f.Contains("language-configuration", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(h.Root, "evil.txt")));

        // A file it uses over 5 MB.
        var large = await Install(web, Vsix(Night().Select(f => f.Name == "extension/themes/base.json" ? (f.Name, new string(' ', EditorExtensions.MaxFile + 1)) : f)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
        Assert.Equal("too_large", (await Json(large))["status"]!.GetValue<string>());

        // Not a zip; a zip that is no extension; a manifest without a name.
        Assert.Equal(HttpStatusCode.BadRequest, (await Install(web, Encoding.UTF8.GetBytes("not a zip"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Install(web, Vsix([("readme.txt", "hi")]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Install(web, Vsix([("extension/package.json", """{ "name": "../x", "publisher": "a", "version": "1.0.0" }""")]))).StatusCode);

        // Its bytes only: JSON, or a form's text, is not a .vsix.
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Install(web, Vsix(Night()), "application/json")).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Install(web, Vsix(Night()), "text/plain")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Install(web, Vsix(Night()), "application/zip")).StatusCode);

        // 50 MB at most, said before the body is sent; other calls keep their 8 MB.
        async Task<HttpStatusCode> Sized(string path, int size, string type)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(new byte[size]) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
            request.Headers.ExpectContinue = true;
            using var response = await web.Http.SendAsync(request);
            return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Sized("/api/extensions/install", EditorExtensions.MaxVsix + 1, "application/octet-stream"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Sized("/api/extensions/import", HttpServer.MaxBody + 1, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, await Sized("/api/extensions/install", HttpServer.MaxBody + 1, "application/octet-stream"));
    }

    [Fact]
    public async Task A_contribution_whose_file_is_not_in_the_vsix_is_left_out_and_with_nothing_left_the_vsix_is_refused()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        // The theme's file is missing: the theme is not listed (the page would ask for it), its language's configuration neither.
        var item = await Json(await Install(web, Vsix(Night().Where(f => f.Name is not ("extension/themes/night.json" or "extension/toml.language-configuration.json")))));
        Assert.Empty(item["contributes"]!["themes"]!.AsArray());
        Assert.Null(item["contributes"]!["languages"]![0]!["configuration"]);
        Assert.Equal("Night Icons", item["contributes"]!["iconThemes"]![0]!["label"]!.GetValue<string>());
        var unused = Texts(item["unused"]);
        Assert.Contains("themes/night.json: named, but not in the extension", unused);
        Assert.Contains("toml.language-configuration.json: named, but not in the extension", unused);
        Assert.DoesNotContain("themes/night.json", Kept(h, "acme.night-1.2.0"));
        Assert.Empty((await web.GetJsonAsync("/api/extensions/contributions"))["themes"]!.AsArray());

        // A theme-only extension whose theme is not there has nothing left: refused, and nothing kept.
        var empty = await Install(web, Vsix([("extension/package.json", Theme("acme", "hollow", "1.0.0", "Hollow").Item2)]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("nothing_usable", (await Json(empty))["status"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(h.Paths.DataDir, "extensions", "acme.hollow-1.0.0")));
        Assert.Single((await web.GetJsonAsync("/api/extensions")).AsArray());
    }

    [Fact]
    public async Task An_extension_that_is_only_code_is_refused_and_says_what_it_would_need()
    {
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);

        var eslint = await Install(web, Vsix([
            ("extension/package.json", """
                { "publisher": "dbaeumer", "name": "vscode-eslint", "version": "3.0.10", "displayName": "ESLint", "main": "./client/out/extension",
                  "contributes": { "commands": [{ "command": "eslint.fixAll", "title": "Fix all" }], "keybindings": [{ "key": "ctrl+alt+f", "command": "eslint.fixAll" }], "configuration": {} } }
                """),
            ("extension/client/out/extension.js", "module.exports = {}"),
        ]));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, eslint.StatusCode);
        var said = await Json(eslint);
        Assert.Equal("nothing_usable", said["status"]!.GetValue<string>());
        Assert.StartsWith("ESLint has nothing Code Arena can use (its code).", said["error"]!.GetValue<string>());
        Assert.Contains("it runs no extension's code", said["error"]!.GetValue<string>());
        Assert.Contains("keybindings: 1 for its own commands, which need its code", Texts(said["unused"]));
        Assert.Empty((await web.GetJsonAsync("/api/extensions")).AsArray());
        Assert.False(Directory.Exists(Path.Combine(h.Paths.DataDir, "extensions", "dbaeumer.vscode-eslint-3.0.10")));
    }

    /// <summary>An extension folder as VS Code installs one: its package.json and files.</summary>
    private static string Folder(string root, string name, params (string Name, string Text)[] files)
    {
        var dir = Path.Combine(root, name);
        foreach (var (file, text) in files)
        {
            var path = Path.Combine(dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        return dir;
    }

    private static (string, string) Theme(string publisher, string name, string version, string label) =>
        ("package.json", $$"""{ "publisher": "{{publisher}}", "name": "{{name}}", "version": "{{version}}", "displayName": "{{label}}", "contributes": { "themes": [{ "label": "{{label}}", "uiTheme": "vs-dark", "path": "./themes/t.json" }] } }""");

    [Fact]
    public async Task VS_Codes_own_extensions_on_this_computer_are_listed_newest_first_and_copied_when_imported()
    {
        using var h = new Harness(_gateway, _mcp);
        var home = Path.Combine(h.Root, "home");
        h.Env["HOME"] = home;
        var code = Path.Combine(home, ".vscode", "extensions");
        foreach (var f in Night().Where(f => f.Name.StartsWith("extension/", StringComparison.Ordinal)))
        {
            Folder(code, "acme.night-1.2.0", (f.Name[10..], f.Text));
        }
        Folder(code, "acme.night-1.10.0-linux-x64", Theme("acme", "night", "1.10.0", "Night Newer"), ("themes/t.json", "{}"));
        Folder(code, "acme.gone-1.0.0", Theme("acme", "gone", "1.0.0", "Gone"), ("themes/t.json", "{}"));
        File.WriteAllText(Path.Combine(code, ".obsolete"), """{ "acme.gone-1.0.0": true }""");
        Folder(code, "ms-python.python-2025.1.0", ("package.json", """{ "publisher": "ms-python", "name": "python", "version": "2025.1.0", "displayName": "Python", "main": "./out/client/extension" }"""));
        Folder(code, "broken-1.0.0", ("package.json", "{ not json"));
        Folder(Path.Combine(home, ".vscode-oss", "extensions"), "dracula-theme.theme-dracula-2.25.1", Theme("dracula-theme", "theme-dracula", "2.25.1", "Dracula"), ("themes/t.json", """{ "colors": {} }"""));
        await using var web = await WebRun.StartAsync(h);

        var local = (await web.GetJsonAsync("/api/extensions/local")).AsArray();
        Assert.Equal(["acme.night", "broken", "dracula-theme.theme-dracula", "ms-python.python"], local.Select(x => x!["id"]!.GetValue<string>()));
        var night = local[0]!;
        Assert.Equal("1.10.0", night["version"]!.GetValue<string>());
        Assert.Equal("VS Code", night["from"]!.GetValue<string>());
        Assert.True(night["usable"]!.GetValue<bool>());
        Assert.Null(night["installed"]);
        Assert.False(local[1]!["usable"]!.GetValue<bool>());
        Assert.Contains("not JSON", local[1]!["problem"]!.GetValue<string>());
        Assert.Equal("VSCodium", local[2]!["from"]!.GetValue<string>());
        Assert.Equal("Dracula", local[2]!["displayName"]!.GetValue<string>());
        Assert.False(local[3]!["usable"]!.GetValue<bool>());
        Assert.StartsWith("Python has nothing Code Arena can use", local[3]!["problem"]!.GetValue<string>());

        // Imported: copied, the newest copy, its files read from the folder as from a .vsix.
        var imported = await web.PostAsync("/api/extensions/import", new JsonObject { ["id"] = "Dracula-Theme.Theme-Dracula" });
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var item = await Json(imported);
        Assert.Equal(("dracula-theme.theme-dracula", "vscode"), (item["id"]!.GetValue<string>(), item["source"]!.GetValue<string>()));
        Assert.Equal(["themes/t.json"], Kept(h, "dracula-theme.theme-dracula-2.25.1"));
        Assert.Equal("2.25.1", (await web.GetJsonAsync("/api/extensions/local"))[2]!["installed"]!.GetValue<string>());
        var newer = await Json(await web.PostAsync("/api/extensions/import", new JsonObject { ["id"] = "acme.night" }));
        Assert.Equal("1.10.0", newer["version"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await web.PostAsync("/api/extensions/import", new JsonObject { ["id"] = "ms-python.python" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/extensions/import", new JsonObject { ["id"] = "acme.gone" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await web.PostAsync("/api/extensions/import", new JsonObject { ["id"] = "../../etc" })).StatusCode);

        if (!OperatingSystem.IsWindows())
        {
            // A theme in a link to elsewhere is not copied: the import is refused.
            var linked = Folder(code, "evil.links-1.0.0", Theme("evil", "links", "1.0.0", "Evil"));
            Directory.CreateSymbolicLink(Path.Combine(linked, "themes"), h.Paths.ConfigDir);
            var refused = await web.PostAsync("/api/extensions/import", new JsonObject { ["id"] = "evil.links" });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            Assert.Contains("link", (await Json(refused))["error"]!.GetValue<string>());
            Assert.False(Directory.Exists(Path.Combine(h.Paths.DataDir, "extensions", "evil.links-1.0.0")));
        }
    }

    [Fact]
    public async Task The_folders_recommended_extensions_say_where_each_stands_here()
    {
        using var h = new Harness(_gateway, _mcp);
        var home = Path.Combine(h.Root, "home");
        h.Env["HOME"] = home;
        var code = Path.Combine(home, ".vscode", "extensions");
        Folder(code, "dracula-theme.theme-dracula-2.25.1", Theme("dracula-theme", "theme-dracula", "2.25.1", "Dracula"), ("themes/t.json", "{}"));
        Folder(code, "gitlab.gitlab-workflow-5.0.0", ("package.json", """{ "publisher": "GitLab", "name": "gitlab-workflow", "version": "5.0.0", "displayName": "GitLab Workflow", "main": "./out/main.js" }"""));
        await using var web = await WebRun.StartAsync(h);

        var none = await web.GetJsonAsync("/api/extensions/recommended");
        Assert.False(none["file"]!.GetValue<bool>());
        Assert.Empty(none["recommendations"]!.AsArray());

        Assert.Equal(HttpStatusCode.OK, (await Install(web, Vsix(Night()))).StatusCode);
        h.Write(".vscode/extensions.json", """
            {
              // As VS Code writes it: comments and trailing commas.
              "recommendations": [
                "esbenp.prettier-vscode",
                "dbaeumer.vscode-eslint",
                "acme.night",
                "dracula-theme.theme-dracula",
                "gitlab.gitlab-workflow",
                "ms-python.python",
                "nobody.nothing",
                "not an id",
              ],
              "unwantedRecommendations": ["ms-vscode.powershell"],
            }
            """);
        var said = await web.GetJsonAsync("/api/extensions/recommended");
        Assert.True(said["file"]!.GetValue<bool>());
        Assert.Equal(
            [
                "esbenp.prettier-vscode built-in",
                "dbaeumer.vscode-eslint unsupported",
                "acme.night installed",
                "dracula-theme.theme-dracula local",
                "gitlab.gitlab-workflow unsupported",
                "ms-python.python built-in",
                "nobody.nothing unknown",
            ],
            said["recommendations"]!.AsArray().Select(r => $"{r!["id"]} {r["status"]}"));
        var notes = said["recommendations"]!.AsArray().ToDictionary(r => r!["id"]!.GetValue<string>(), r => r!["note"]!.GetValue<string>());
        Assert.StartsWith("Formatting: Prettier", notes["esbenp.prettier-vscode"]);
        Assert.StartsWith("ESLint's extension runs code: not supported here.", notes["dbaeumer.vscode-eslint"]);
        Assert.StartsWith("GitLab Workflow has nothing Code Arena can use", notes["gitlab.gitlab-workflow"]);
        Assert.Equal("VS Code has it on this computer (2.25.1): import it.", notes["dracula-theme.theme-dracula"]);
        Assert.Equal(["ms-vscode.powershell"], Texts(said["unwanted"]));

        h.Write(".vscode/extensions.json", "{ \"recommendations\": [");
        Assert.Contains("line", (await web.GetJsonAsync("/api/extensions/recommended"))["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_extension_calls_need_this_runs_key_and_its_own_page()
    {
        using var h = new Harness(_gateway, _mcp);
        h.Env["HOME"] = Path.Combine(h.Root, "home");
        await using var web = await WebRun.StartAsync(h);
        (string Method, string Path, HttpContent? Body)[] calls =
        [
            ("GET", "/api/extensions", null),
            ("GET", "/api/extensions/contributions", null),
            ("GET", "/api/extensions/file?id=acme.night&path=themes/night.json", null),
            ("GET", "/api/extensions/local", null),
            ("GET", "/api/extensions/recommended", null),
            ("POST", "/api/extensions/install", new ByteArrayContent(Vsix(Night())) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } }),
            ("POST", "/api/extensions/import", new StringContent("""{"id":"acme.night"}""", Encoding.UTF8, "application/json")),
            ("POST", "/api/extensions/uninstall", new StringContent("""{"id":"acme.night"}""", Encoding.UTF8, "application/json")),
            ("POST", "/api/extensions/enable", new StringContent("""{"id":"acme.night","enabled":false}""", Encoding.UTF8, "application/json")),
        ];
        using var anonymous = WebRun.Client(web.Port, token: null);
        var wrong = new List<string>();
        foreach (var (method, path, body) in calls)
        {
            async Task Expect(HttpStatusCode status, string what, HttpClient client, Action<HttpRequestMessage>? change = null)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (body is not null)
                {
                    var bytes = await body.ReadAsByteArrayAsync();
                    request.Content = new ByteArrayContent(bytes);
                    request.Content.Headers.ContentType = body.Headers.ContentType;
                }
                change?.Invoke(request);
                using var response = await client.SendAsync(request);
                if (response.StatusCode != status)
                {
                    wrong.Add($"{method} {path} {what}: {(int)response.StatusCode}, not {(int)status}");
                }
            }
            await Expect(HttpStatusCode.Unauthorized, "without the key", anonymous);
            await Expect(HttpStatusCode.Forbidden, "for another host name", web.Http, r => r.Headers.Host = $"evil.example:{web.Port}");
            await Expect(HttpStatusCode.Forbidden, "from another site", web.Http, r => r.Headers.Add("Origin", "https://evil.example"));
            await Expect(HttpStatusCode.Forbidden, "said by the browser to come from elsewhere", web.Http, r => r.Headers.Add("Sec-Fetch-Site", "cross-site"));
            if (body is not null)
            {
                await Expect(HttpStatusCode.UnsupportedMediaType, "as a form", web.Http, r => r.Content!.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded"));
                await Expect(HttpStatusCode.UnsupportedMediaType, "as text", web.Http, r => r.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain"));
            }
        }
        Assert.Empty(wrong);
        Assert.Empty((await web.GetJsonAsync("/api/extensions")).AsArray());
    }
}
