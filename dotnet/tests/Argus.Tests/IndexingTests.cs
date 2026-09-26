using System.Diagnostics;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Store;
using Argus.Util;

namespace Argus.Tests;

/// <summary>The indexer end to end over a real git repository: mirror, worktree, diff, ctags, docs.</summary>
public class IndexingTests
{
    static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_AUTHOR_NAME"] = psi.Environment["GIT_COMMITTER_NAME"] = "t";
        psi.Environment["GIT_AUTHOR_EMAIL"] = psi.Environment["GIT_COMMITTER_EMAIL"] = "t@example.invalid";
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
    }

    [Fact]
    public void A_repository_is_indexed_then_updated_incrementally()
    {
        if (Ctags.Which("ctags") is null || Ctags.Which("git") is null) return;
        using var dir = new TempDir();
        var src = Path.Combine(dir.Path, "src");
        Directory.CreateDirectory(src);
        Git(src, "init", "-q", "-b", "main");
        dir.File("src/lib/codec.h", "#pragma once\n/** Decode one frame. */\nint decode_frame(const char *buf);\n");
        dir.File("src/lib/codec.c", "#include \"codec.h\"\n#include <stdlib.h>\n\n// Implementation.\nint decode_frame(const char *buf) { return 0; }\nstatic int helper(void) { return 1; }\n");
        dir.File("src/tool.py", "def main():\n    \"\"\"Run the tool.\"\"\"\n    return 0\n");
        dir.File("src/vendor/skip.c", "int skipped;\n");
        Git(src, "add", "-A");
        Git(src, "commit", "-q", "-m", "one");

        var index = new IndexConfig { DataDir = Path.Combine(dir.Path, "data"), DbPath = Path.Combine(dir.Path, "data", "index.db") };
        using var conn = Db.Open(index.DbPath);
        var project = new Project(7, "g/codec", "main", src);
        var repoId = Writes.UpsertRepo(conn, 7, "g/codec", "main", src);
        var mirror = Mirror.EnsureMirror(index, project, src);
        var sha = Mirror.HeadSha(mirror, "main");
        var tree = Mirror.SyncWorktree(index, 7, mirror, sha, "main");
        var result = Worker.IndexRepo(conn, index, project, mirror, tree, sha, null, repoId: repoId);

        Assert.Equal(3, result.Indexed);
        Assert.Equal(1, result.Skipped);
        Assert.False(result.TimedOut);
        Assert.False(result.SymbolsFailed);
        var symbols = Queries.FindSymbol([repoId], conn, "decode_frame");
        Assert.Equal(2, symbols.Count);
        Assert.Contains(symbols, s => s.Str("doc") == "Decode one frame.");
        Assert.Contains(symbols, s => s.Str("doc") == "Implementation.");
        // ctags reports each symbol's language (--fields=+l), which is what lets a
        // Python docstring be read at all.
        Assert.Equal("Run the tool.", Queries.FindSymbol([repoId], conn, "main")[0].Str("doc"));
        Assert.Equal(0, Queries.FindSymbol([repoId], conn, "helper")[0].Long("is_public"));
        Assert.False(Worker.ContractIsStale(conn, repoId));

        // A second commit: an edit, a deletion, an addition.
        File.AppendAllText(Path.Combine(src, "lib", "codec.c"), "int encode_frame(void) { return 2; }\n");
        File.Delete(Path.Combine(src, "tool.py"));
        dir.File("src/lib/extra.h", "int extra(void);\n");
        Git(src, "add", "-A");
        Git(src, "commit", "-q", "-m", "two");
        mirror = Mirror.EnsureMirror(index, project, src);
        var sha2 = Mirror.HeadSha(mirror, "main");
        tree = Mirror.SyncWorktree(index, 7, mirror, sha2, "main");
        var second = Worker.IndexRepo(conn, index, project, mirror, tree, sha2, sha, repoId: repoId);
        Assert.Equal(2, second.Indexed);
        Assert.Equal(1, second.Deleted);
        Assert.Single(Queries.FindSymbol([repoId], conn, "encode_frame"));
        Assert.Empty(Queries.FindSymbol([repoId], conn, "main"));

        var counts = Resolve.ResolveIncludes(conn);
        Assert.Equal(1, counts["resolved"]);
        Assert.Equal(1, counts["external"]);
    }

    [Theory]
    [InlineData("release/v1", "release%2Fv1")]
    [InlineData("release-v1", "release-v1")]
    [InlineData("a b:c", "a%20b%3Ac")]
    public void Branch_directories_are_collision_free(string branch, string expected) => Assert.Equal(expected, Mirror.BranchDir(branch));

    [Fact]
    public void The_default_branch_is_always_selected_first()
    {
        Assert.Equal(["main", "v1", "v2"], Mirror.SelectBranches(["v1", "main", "feature", "v2"], ["v*"], "main"));
        Assert.Equal(["main"], Mirror.SelectBranches(["main", "dev"], [], "main"));
        Assert.True(Fnmatch.MatchCase("release/1.0", "release/*"));
        Assert.False(Fnmatch.MatchCase("Release/1.0", "release/*"));
        Assert.True(Fnmatch.MatchCase("v2", "v[0-9]"));
        Assert.False(Fnmatch.MatchCase("v2", "v[!0-9]"));
    }

    [Fact]
    public void The_clone_url_keeps_the_path_but_uses_the_configured_origin()
    {
        var cfg = GitLabConfig.Create("http://host.docker.internal:8929", "t");
        Assert.Equal("http://host.docker.internal:8929/grp/proj.git", GitLab.CloneUrlFor(cfg, "http://localhost:8929/grp/proj.git"));
    }

    [Fact]
    public void Enumeration_health_flags_a_token_that_cannot_see_everything()
    {
        Assert.True(new EnumerationHealth(true, 1, 50).Ok);
        Assert.True(new EnumerationHealth(false, 5, 5).Ok);
        var bad = new EnumerationHealth(false, 2, 5);
        Assert.False(bad.Ok);
        Assert.Contains("At least 3 repository(ies)", bad.Problem);
    }

    [Fact]
    public void Kpis_on_an_empty_index_are_nulls_not_errors()
    {
        using var ix = new TestIndex();
        var k = Kpi.Collect(ix.Conn);
        Assert.Equal(0, k["repos"]!.GetValue<long>());
        Assert.Null(k["median_repo_age_hours"]);
        Assert.Null(k["resolved_include_rate"]);
    }

    [Fact]
    public void The_embed_text_leads_with_what_the_symbol_does()
    {
        Assert.Equal("function expire -- Remove stale keys. -- in Cache -- (int now) -- src cache util.c",
            Semantic.EmbedTextFor("expire", "function", "(int now)", "Cache", "src/cache_util.c", "Remove stale keys."));
    }
}
