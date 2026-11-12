using System.Text.Json.Nodes;
using CodeIntel.Indexing;
using CodeIntel.Packs;
using CodeIntel.Packs.Sources;

namespace CodeIntel.Tests;

/// <summary>Deterministic stand-in for the embedding server: overlapping words point the same way.</summary>
public static class FakeEmbedder
{
    public static List<double[]> Embed(IReadOnlyList<string> texts) => texts.Select(Vector).ToList();

    public static double[] Vector(string text)
    {
        var v = new double[768];
        foreach (var word in text.ToLowerInvariant().Split([' ', '\n', '.', ',', '(', ')', '-', '_', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            var h = (uint)word.GetHashCode(StringComparison.Ordinal) & 0x7fffffff;
            v[h % 768] += (h & 1) == 0 ? 1 : -1;
        }
        if (v.All(x => x == 0)) v[0] = 1;
        var norm = Math.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / norm).ToArray();
    }
}

[Collection("process-state")]
public class PackTests
{
    [Fact]
    public void Packs_installed_under_their_old_file_names_are_taken_under_the_new_ones_links_following_their_library_file()
    {
        using var dir = new TempDir();
        var dest = System.IO.Path.Combine(dir.Path, "packs");
        // A pack installed as a file, one loaded from the library (a link), whose library file the upgrade renamed, and one
        // loaded from a library file still under its old name.
        dir.File("packs/python.arguspack", "a python pack");
        var library = dir.File("library/qt.kpack", "the qt pack");
        File.CreateSymbolicLink(System.IO.Path.Combine(dest, "qt.arguspack"), System.IO.Path.Combine(dir.Path, "library", "qt.arguspack"));
        var stillOld = dir.File("library/react.arguspack", "the react pack");
        File.CreateSymbolicLink(System.IO.Path.Combine(dest, "react.arguspack"), stillOld);
        // There under both names: the new one is kept.
        dir.File("packs/cpp.arguspack", "old cpp");
        dir.File("packs/cpp.kpack", "new cpp");

        Assert.Equal(3, Registry.AdoptOldNames(dest));
        Assert.Empty(Directory.EnumerateFileSystemEntries(dest, "*.arguspack"));
        Assert.Equal("a python pack", File.ReadAllText(System.IO.Path.Combine(dest, "python.kpack")));
        Assert.Equal(library, Registry.LinkedFrom(System.IO.Path.Combine(dest, "qt.kpack")));
        Assert.Equal(stillOld, Registry.LinkedFrom(System.IO.Path.Combine(dest, "react.kpack")));
        Assert.Equal("new cpp", File.ReadAllText(System.IO.Path.Combine(dest, "cpp.kpack")));
        // Again: nothing more to take.
        Assert.Equal(0, Registry.AdoptOldNames(dest));
        Assert.Equal(0, Registry.AdoptOldNames(System.IO.Path.Combine(dir.Path, "none")));
    }

    [Fact]
    public void Quantisation_round_trips_direction()
    {
        var q = FakeEmbedder.Vector("decode a jpeg frame");
        var near = FakeEmbedder.Vector("decode a jpeg frame quickly");
        var far = FakeEmbedder.Vector("allocate kernel pool memory");
        Assert.Equal(96, Quantize.ToBits(q).Length);
        Assert.Equal(768, Quantize.ToInt8(q).Length);
        var ranked = Quantize.Rescore(q, [(1, Quantize.ToInt8(far)), (2, Quantize.ToInt8(near))]);
        Assert.Equal(2, ranked[0].Id);
        Assert.True(ranked[0].Score > ranked[1].Score);
        Assert.Equal(0.0, Quantize.Rescore(q, [(3, new byte[768])])[0].Score);
    }

    [Fact]
    public void Markdown_chunks_keep_headings_anchors_and_fences()
    {
        var md = "# Title\n\nIntro.\n\n## Usage {/*pinned*/}\n\n```\n# not a heading\n```\n\n## Usage\n\nAgain.\n";
        var chunks = Chunker.ChunkMarkdown(md);
        Assert.Equal(["Title", "Title > Usage", "Title > Usage"], chunks.Select(c => c.HeadingPath));
        Assert.Equal(["title", "pinned", "usage"], chunks.Select(c => c.Anchor));
        Assert.Contains("# not a heading", chunks[1].Body);
        Assert.Equal("Title > Usage\n" + chunks[1].Body, Chunker.EmbedText(chunks[1]));
    }

    [Fact]
    public void Rst_titles_become_atx_headings_in_order_of_first_use()
    {
        var atx = Chunker.RstToAtx("=====\nTitle\n=====\n\nSection\n-------\n\nText with :func:`os.path.join` and ``code``.\n");
        Assert.Contains("# Title", atx);
        Assert.Contains("## Section", atx);
    }

    [Fact]
    public void Html_text_extraction_drops_chrome_and_decodes_entities()
    {
        var (title, body) = HtmlText.ToText("<html><head><title> A  B </title><script>x<y</script></head><body><nav>menu</nav><h2>Big\n Head</h2><p>1 &amp; 2 &lt;3&gt;</p></body></html>");
        Assert.Equal("A B", title);
        Assert.Equal("## Big Head\n\n1 & 2 <3>", body);
    }

    [Fact]
    public void Ms_learn_front_matter_parses_lists_and_block_items()
    {
        var (meta, body) = MsLearn.ParseFrontMatter("---\nUID: NF:winuser.MessageBox\napi_name: [\"A\",\"B\"]\ntopic_type:\n - apiref\n---\nBody\n");
        Assert.Equal("NF:winuser.MessageBox", meta.Str("UID"));
        Assert.Equal(["A", "B"], meta.List("api_name"));
        Assert.Equal(["apiref"], meta.List("topic_type"));
        Assert.Equal("Body\n", body);
        Assert.Equal(("function", "winuser", "MessageBox"), MsLearn.ParseUid("NF:winuser.MessageBox"));
        Assert.Null(MsLearn.ParseUid("NN:combaseapi"));
    }

    [Fact]
    public void A_pack_is_built_installed_and_queried()
    {
        using var dir = new TempDir();
        var root = Path.Combine(AppContext.BaseDirectory, "packfixtures", "react");
        var outPath = Path.Combine(dir.Path, "out", "react.kpack");
        PackBuilder.BuildPack(new ReactDocs(), root, outPath, "1.0", FakeEmbedder.Embed, sourceCommit: "abc", log: TextWriter.Null);

        var installed = Registry.Install(outPath, Path.Combine(dir.Path, "packs"));
        Assert.Equal("react", installed.Name);
        Assert.True(installed.Compatible);
        var listed = Registry.ListInstalled(Path.Combine(dir.Path, "packs"));
        Assert.Single(listed);

        var packs = PackStore.OpenPacks(Registry.PackFiles(Path.Combine(dir.Path, "packs")));
        try
        {
            var hit = PackStore.LookupSymbol(packs, "usestate");
            Assert.NotEmpty(hit);
            Assert.Equal("react", hit[0]["source"]!.GetValue<string>());
            var docs = PackStore.SearchDocs(packs, FakeEmbedder.Vector("reset state when a prop changes"), limit: 3);
            Assert.NotEmpty(docs);
            Assert.All(docs, d => Assert.False(string.IsNullOrEmpty(d["text"]!.GetValue<string>())));
            var page = PackStore.GetDoc(packs, docs[0]["doc_path"]!.GetValue<string>());
            Assert.NotNull(page);
            Assert.False(page!["truncated"]!.GetValue<bool>());
            Assert.NotEmpty(PackStore.SearchText(packs, "state"));
        }
        finally { PackStore.ClosePacks(packs); }
    }

    [Fact]
    public void A_pack_loaded_from_the_library_moves_to_the_newest_week_of_its_name_and_a_file_installed_stays()
    {
        using var dir = new TempDir();
        var root = Path.Combine(AppContext.BaseDirectory, "packfixtures", "react");
        var library = Path.Combine(dir.Path, "library");
        var packs = Path.Combine(dir.Path, "packs");
        void Build(string file, string version) =>
            PackBuilder.BuildPack(new ReactDocs(), root, Path.Combine(library, file), version, FakeEmbedder.Embed, sourceCommit: version, log: TextWriter.Null);
        Build("react.kpack", "1.0");
        Build("react-2026.11.5.kpack", "2026.11.5");
        Registry.Load(library, "react.kpack", packs);
        Assert.Empty(Registry.FollowNewest(Path.Combine(dir.Path, "nothing"), packs));

        // A newer week came: the loaded pack follows it; a later one again, ordered by number (11.12 after 11.5).
        Assert.Equal([("react", "1.0", "2026.11.5")], Registry.FollowNewest(library, packs));
        Build("react-2026.11.12.kpack", "2026.11.12");
        Assert.Equal([("react", "2026.11.5", "2026.11.12")], Registry.FollowNewest(library, packs));
        Assert.Equal(Path.Combine(library, "react-2026.11.12.kpack"), Registry.LinkedFrom(Path.Combine(packs, "react.kpack")));
        // Nothing newer: nothing moves.
        Assert.Empty(Registry.FollowNewest(library, packs));

        // An older pack loaded on purpose by an admin is moved forward too; a pack installed as a file is never touched.
        Registry.Load(library, "react.kpack", packs);
        Assert.Single(Registry.FollowNewest(library, packs));
        File.Delete(Path.Combine(packs, "react.kpack"));
        File.Copy(Path.Combine(library, "react.kpack"), Path.Combine(packs, "react.kpack"));
        Assert.Empty(Registry.FollowNewest(library, packs));
        Assert.Null(Registry.LinkedFrom(Path.Combine(packs, "react.kpack")));
    }

    [Fact]
    public void A_pack_in_the_library_loads_as_a_link_and_unloads_without_touching_the_library()
    {
        using var dir = new TempDir();
        var root = Path.Combine(AppContext.BaseDirectory, "packfixtures", "react");
        var library = Path.Combine(dir.Path, "library");
        var packs = Path.Combine(dir.Path, "packs");
        var built = Path.Combine(library, "docs", "react-1.0.kpack");
        PackBuilder.BuildPack(new ReactDocs(), root, built, "1.0", FakeEmbedder.Embed, sourceCommit: "abc", log: TextWriter.Null);
        File.WriteAllText(Path.Combine(library, "broken.kpack"), "not a pack");

        var listed = Registry.ListLibrary(library, packs);
        Assert.Equal(["broken.kpack", "docs/react-1.0.kpack"], listed.Select(l => l.File));
        Assert.False(listed[0].Pack.Compatible);
        Assert.StartsWith("unreadable", listed[0].Pack.IncompatibleReason, StringComparison.Ordinal);
        Assert.Equal("react", listed[1].Pack.Name);
        Assert.False(listed[1].Loaded);

        // Loaded: a link under its own name, searched like any installed pack.
        Assert.Equal("react", Registry.Load(library, "docs/react-1.0.kpack", packs).Name);
        var link = new FileInfo(Path.Combine(packs, "react.kpack"));
        Assert.NotNull(link.LinkTarget);
        Assert.True(Registry.ListLibrary(library, packs)[1].Loaded);
        var opened = PackStore.OpenPacks(Registry.PackFiles(packs));
        try { Assert.NotEmpty(PackStore.LookupSymbol(opened, "usestate")); }
        finally { PackStore.ClosePacks(opened); }

        // Nothing outside the library, nothing unreadable.
        Assert.Throws<RegistryError>(() => Registry.Load(library, "../packs/react.kpack", packs));
        Assert.Throws<RegistryError>(() => Registry.Load(library, "broken.kpack", packs));
        Assert.Throws<RegistryError>(() => Registry.Load(library, "docs/missing.kpack", packs));

        // Its library file gone: left out of every search, listed so it can be unloaded.
        var moved = Path.Combine(dir.Path, "elsewhere.kpack");
        File.Move(built, moved);
        Assert.Empty(Registry.PackFiles(packs));
        Assert.Contains("library file is gone", Registry.ListInstalled(packs).Single().IncompatibleReason, StringComparison.Ordinal);
        Assert.True(Registry.Remove("react", packs));
        Assert.Equal([], Directory.GetFileSystemEntries(packs).Select(Path.GetFileName));
        Assert.True(File.Exists(moved));
    }

    [Fact]
    public void A_checksum_mismatch_refuses_the_install_and_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        var src = dir.File("x.kpack", "not a pack");
        var dest = Path.Combine(dir.Path, "packs");
        Assert.Throws<RegistryError>(() => Registry.Install(src, dest, "00"));
        Assert.Empty(Directory.GetFiles(dest));
        Assert.Throws<RegistryError>(() => Registry.Install(src, dest));
        Assert.Empty(Directory.GetFiles(dest));
        Assert.Throws<RegistryError>(() => Registry.Remove("../etc", dest));
    }

    [Fact]
    public void Verify_reports_a_contradicted_library()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "sdk");
        Directory.CreateDirectory(Path.Combine(root, "sdk-api-src", "content", "winuser"));
        File.WriteAllText(Path.Combine(root, "sdk-api-src", "content", "winuser", "nf-winuser-messageboxw.md"),
            "---\nUID: NF:winuser.MessageBoxW\ntitle: MessageBoxW function (winuser.h)\ndescription: Shows a box.\nreq.header: winuser.h\nreq.lib: User32.lib\nreq.dll: User32.dll\n---\nShows a box.\n");
        var outPath = Path.Combine(dir.Path, "win32.kpack");
        PackBuilder.BuildPack(MicrosoftApiRef.Win32Api(), root, outPath, "1", FakeEmbedder.Embed, sourceCommit: "x", log: TextWriter.Null);
        var packs = PackStore.OpenPacks([outPath]);
        try
        {
            var findings = PackStore.VerifyText(packs, "MessageBoxW links against Kernel32.lib and needs winuser.h.");
            var fields = (JsonArray)findings.Single()["fields"]!;
            Assert.Contains(fields, f => f!["field"]!.GetValue<string>() == "header" && f["status"]!.GetValue<string>() == "confirmed");
            Assert.Contains(fields, f => f!["field"]!.GetValue<string>() == "library" && f["status"]!.GetValue<string>() == "contradicted");
            Assert.Contains(fields, f => f!["field"]!.GetValue<string>() == "dll" && f["status"]!.GetValue<string>() == "unstated");
            // The adapter appends " -- <description>" to the requirement line; it is not part of any field.
            Assert.Equal("User32.dll", fields.Single(f => f!["field"]!.GetValue<string>() == "dll")!["documented"]!.GetValue<string>());
            var correction = (JsonObject)((JsonArray)findings.Single()["corrections"]!).Single()!;
            Assert.Equal("""{"field":"library","documented":"User32.lib","status":"contradicted","stated":"Kernel32.lib"}""",
                correction.ToJsonString());
            var contracts = PackStore.ApiContracts(packs, "void f() { MessageBoxW(0,0,0,0); MessageBoxW(0,0,0,0); }");
            Assert.Equal(2, contracts.Single()["mentions"]!.GetValue<int>());
        }
        finally { PackStore.ClosePacks(packs); }
    }

    [Fact]
    public void The_verify_command_blocks_a_contradicted_draft_and_passes_a_right_one()
    {
        using var dir = new TempDir();
        var root = Path.Combine(dir.Path, "sdk");
        Directory.CreateDirectory(Path.Combine(root, "sdk-api-src", "content", "winuser"));
        File.WriteAllText(Path.Combine(root, "sdk-api-src", "content", "winuser", "nf-winuser-messageboxw.md"),
            "---\nUID: NF:winuser.MessageBoxW\ntitle: MessageBoxW function (winuser.h)\ndescription: Shows a box.\nreq.header: winuser.h\nreq.lib: User32.lib\nreq.dll: User32.dll\n---\nShows a box.\n");
        var packs = Path.Combine(dir.Path, "packs");
        Directory.CreateDirectory(packs);
        PackBuilder.BuildPack(MicrosoftApiRef.Win32Api(), root, Path.Combine(packs, "win32.kpack"), "1", FakeEmbedder.Embed,
            sourceCommit: "x", log: TextWriter.Null);
        var cfg = dir.File("config.yaml", $"gitlab:\n  url: https://gl.test\n  token: t\nindex:\n  data_dir: {dir.Path}/d\n  db_path: {dir.Path}/d/i.db\npacks:\n  dir: {packs}\n");

        var (rc, stdout, stderr) = Capture(() => Program.Run(["verify", "--config", cfg, "--text", "MessageBoxW lives in shell32.dll.", "--json"]));
        Assert.Equal(Cli.Commands.ExitVerifyContradicted, rc);
        var contradicted = (JsonArray)JsonNode.Parse(stdout)!["contradicted"]!;
        Assert.Equal("""{"symbol":"MessageBoxW","source":"win32","url":"https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-messageboxw","field":"dll","documented":"User32.dll","status":"contradicted","stated":"shell32.dll"}""",
            contradicted.Single()!.ToJsonString());
        Assert.Contains("you said 'shell32.dll'; the documentation says 'User32.dll' [win32]", stderr);

        (rc, _, _) = Capture(() => Program.Run(["verify", "--config", cfg, "--text", "MessageBoxW lives in User32.dll.", "--quiet"]));
        Assert.Equal(0, rc);

        // As a Claude Code Stop hook: the payload names the transcript; the LAST assistant text is the draft.
        string Transcript(params string[] assistantTexts)
        {
            var lines = new List<string> { """{"type":"user","message":{"role":"user","content":"where is MessageBoxW?"}}""" };
            foreach (var t in assistantTexts)
                lines.Add(new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = t }) } }.ToJsonString());
            return dir.File($"t{Guid.NewGuid():n}.jsonl", string.Join("\n", lines) + "\n");
        }
        int Hook(string payload, out string err)
        {
            var oldIn = Console.In;
            Console.SetIn(new StringReader(payload));
            try
            {
                var (code, _, e) = Capture(() => Program.Run(["verify", "--config", cfg, "--claude-hook"]));
                err = e;
                return code;
            }
            finally { Console.SetIn(oldIn); }
        }
        var wrong = Transcript("Let me check.", "MessageBoxW lives in shell32.dll.");
        Assert.Equal(2, Hook(new JsonObject { ["transcript_path"] = wrong }.ToJsonString(), out var reason));
        Assert.Contains("you said 'shell32.dll'", reason);
        Assert.Equal(0, Hook(new JsonObject { ["transcript_path"] = Transcript("MessageBoxW lives in shell32.dll.", "It is in User32.dll.") }.ToJsonString(), out _));
        Assert.Equal(0, Hook("not json", out _));
        Assert.Equal(0, Hook("""{"transcript_path":"/nonexistent/x.jsonl"}""", out _));
    }

    static (int Rc, string Out, string Err) Capture(Func<int> run)
    {
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        using var o = new StringWriter();
        using var e = new StringWriter();
        Console.SetOut(o);
        Console.SetError(e);
        try { return (run(), o.ToString(), e.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }

    [Fact]
    public void A_pack_from_another_embedding_model_is_flagged_incompatible()
    {
        var meta = new Dictionary<string, string> { ["embedding_model"] = "other", ["embedding_dim"] = "768", ["pack_schema_version"] = "1" };
        var exc = Assert.Throws<PackMismatch>(() => PackFormat.RequireCompatible(meta, "nomic-embed-text", 768));
        Assert.Contains("'other'", exc.Message);
    }
}
