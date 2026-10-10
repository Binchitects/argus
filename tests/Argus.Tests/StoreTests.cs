using System.Text.Json.Nodes;
using Argus.Indexing;
using Argus.Store;
using Argus.Util;

namespace Argus.Tests;

public class StoreTests
{
    [Fact]
    public void Migrations_run_to_the_latest_version_and_are_idempotent()
    {
        using var ix = new TestIndex();
        var version = Convert.ToInt32(Sql.Scalar(ix.Conn, "PRAGMA user_version"));
        Assert.Equal(Db.Migrations[^1].Version, version);
        Assert.Equal(version, Db.Migrate(ix.Conn));
        Assert.Equal(21, Db.Migrations.Count);
    }

    [Fact]
    public void A_repository_left_out_is_removed_a_few_files_at_a_time_with_everything_that_hangs_on_them()
    {
        using var ix = new TestIndex();
        long Count(string sql, params object?[] args) => Convert.ToInt64(Sql.Scalar(ix.Conn, sql, args));
        var main = ix.Repo(1, "g/big");
        var dev = ix.Repo(1, "g/big", branch: "develop");
        foreach (var row in new[] { main, dev })
            for (int i = 0; i < 7; i++)
                ix.Symbol(row, ix.File(row, $"src/f{i}.c", $"int BigThing{i}(void);\n"), $"BigThing{i}");
        var other = ix.Repo(2, "g/other");
        ix.Symbol(other, ix.File(other, "o.c", "int OtherThing(void);\n"), "OtherThing");
        Writes.RecordError(ix.Conn, main, "src/f0.c", "read", "a test error", 1);

        Assert.Equal(2, Choices.DropInSteps(ix.Conn, 1, filesPerStep: 3));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM repos WHERE gitlab_id = 1"));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM files WHERE repo_id IN (?, ?)", main, dev));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM symbols WHERE repo_id IN (?, ?)", main, dev));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM index_errors WHERE repo_id = ?", main));
        Assert.Equal(0L, Count("SELECT COUNT(*) FROM files_fts WHERE files_fts MATCH 'BigThing3'"));
        Sql.Exec(ix.Conn, "INSERT INTO files_fts(files_fts) VALUES ('integrity-check')");
        // Another repository is untouched.
        Assert.Equal(1L, Count("SELECT COUNT(*) FROM files_fts WHERE files_fts MATCH 'OtherThing'"));
        Assert.Single(Queries.FindSymbol([other], ix.Conn, "OtherThing"));
        Assert.Equal(0, Choices.DropInSteps(ix.Conn, 1));
    }

    [Fact]
    public void Upserting_a_file_keeps_the_fts_index_in_step()
    {
        using var ix = new TestIndex();
        var r = ix.Repo(1, "g/a");
        ix.File(r, "a.c", "alpha beta");
        ix.File(r, "a.c", "gamma delta");
        long Count(string q) => Convert.ToInt64(Sql.Scalar(ix.Conn, "SELECT count(*) FROM files_fts WHERE files_fts MATCH ?", q));
        Assert.Equal(0, Count("alpha"));
        Assert.Equal(1, Count("gamma"));
        Writes.DeleteFile(ix.Conn, r, "a.c");
        Assert.Equal(0, Count("gamma"));
    }

    [Fact]
    public void Deleting_a_repo_removes_its_fts_rows_too()
    {
        using var ix = new TestIndex();
        var r = ix.Repo(1, "g/a");
        var f = ix.File(r, "a.c", "needle");
        ix.Symbol(r, f, "needle_fn");
        Writes.DeleteRepo(ix.Conn, r);
        Assert.Equal(0L, Convert.ToInt64(Sql.Scalar(ix.Conn, "SELECT count(*) FROM files_fts WHERE files_fts MATCH 'needle'")));
        Assert.Equal(0L, Convert.ToInt64(Sql.Scalar(ix.Conn, "SELECT count(*) FROM symbols")));
    }

    [Fact]
    public void The_retry_queue_round_trips_and_counts_packed_paths()
    {
        using var ix = new TestIndex();
        var r = ix.Repo(1, "g/a");
        Writes.EnqueueRetry(ix.Conn, r, ["b.c", "a.c", "a.c"], "read error", 1);
        Assert.Equal(["a.c", "b.c"], Writes.PeekRetryPaths(ix.Conn, r));
        Assert.Equal("{\"reason\": \"read error\", \"paths\": [\"a.c\", \"b.c\"]}", Sql.Scalar(ix.Conn, "SELECT reason FROM index_queue"));
        Assert.Equal(2, Queries.IndexStatus([r], ix.Conn)[0].Long("queued_retries"));
        Assert.Equal(["a.c", "b.c"], Writes.DrainRetryPaths(ix.Conn, r));
        Assert.Empty(Writes.PeekRetryPaths(ix.Conn, r));
        var counts = Writes.BumpRetryAttempts(ix.Conn, r, ["x.c"]);
        counts = Writes.BumpRetryAttempts(ix.Conn, r, ["x.c"]);
        Assert.Equal(2, counts["x.c"]);
    }

    [Fact]
    public void A_malformed_queue_row_does_not_break_status()
    {
        using var ix = new TestIndex();
        var r = ix.Repo(1, "g/a");
        Sql.Exec(ix.Conn, "INSERT INTO index_queue (repo_id, enqueued_at, reason) VALUES (?, 1, 'not json')", r);
        Assert.Equal(0, Queries.IndexStatus([r], ix.Conn)[0].Long("queued_retries"));
        Assert.Empty(Writes.PeekRetryPaths(ix.Conn, r));
    }

    /// <summary>Every allowlist-scoped query returns nothing for an empty allowlist and never leaks another repo.</summary>
    [Fact]
    public void Every_scoped_query_filters_by_the_allowlist()
    {
        using var ix = new TestIndex();
        var mine = ix.Repo(1, "g/mine");
        var theirs = ix.Repo(2, "g/theirs");
        var fm = ix.File(mine, "m.c", "shared_name here\n");
        var ft = ix.File(theirs, "t.c", "shared_name there\n");
        ix.Symbol(mine, fm, "shared_name");
        ix.Symbol(theirs, ft, "shared_name");

        List<long> none = [];
        Assert.Empty(Queries.FindSymbol(none, ix.Conn, "shared_name"));
        Assert.Empty(Queries.SearchCode(none, ix.Conn, "shared_name"));
        Assert.Empty(Queries.FindReferences(none, ix.Conn, "shared_name"));
        Assert.Empty(Queries.IndexStatus(none, ix.Conn));
        Assert.Empty(Queries.WhichRepo(none, ix.Conn, "shared_name"));
        Assert.Empty(Queries.RepoOverview(none, ix.Conn));
        Assert.Null(Queries.GetFile(none, ix.Conn, mine, "m.c"));

        Assert.All(Queries.FindSymbol([mine], ix.Conn, "shared_name"), r => Assert.Equal(mine, r.Long("repo_id")));
        Assert.All(Queries.SearchCode([mine], ix.Conn, "shared_name"), r => Assert.Equal(mine, r.Long("repo_id")));
        Assert.All(Queries.FindReferences([mine], ix.Conn, "shared_name"), r => Assert.Equal("g/mine", r["repo"]!.GetValue<string>()));
        Assert.Null(Queries.GetFile([mine], ix.Conn, theirs, "t.c"));
        Assert.All(Queries.WhichRepo([mine], ix.Conn, "shared_name"), r => Assert.Equal(mine, r["repo_id"]!.GetValue<long>()));
        Assert.Single((JsonArray)Queries.RepoOverview([mine], ix.Conn)["repos"]!);
    }

    [Fact]
    public void Chunking_the_allowlist_changes_nothing()
    {
        using var ix = new TestIndex();
        var ids = new List<long>();
        for (int i = 0; i < 950; i++) ids.Add(ix.Repo(1000 + i, $"g/r{i}"));
        var f = ix.File(ids[^1], "a.c", "last_one");
        ix.Symbol(ids[^1], f, "last_one");
        Assert.Single(Queries.FindSymbol(ids, ix.Conn, "last_one"));
        Assert.Single(Queries.SearchCode(ids, ix.Conn, "last_one"));
        Assert.Equal(950, Queries.IndexStatus(ids, ix.Conn).Count);
    }

    [Fact]
    public void Bad_fts_syntax_becomes_an_actionable_query_error()
    {
        using var ix = new TestIndex();
        var r = ix.Repo(1, "g/a");
        ix.File(r, "a.c", "text");
        var exc = Assert.Throws<QueryError>(() => Queries.SearchCode([r], ix.Conn, "\"unbalanced"));
        Assert.StartsWith("That search syntax is not valid (unterminated string).", exc.Message);
    }

    [Fact]
    public void Find_references_respects_identifier_boundaries_and_marks_definitions()
    {
        using var ix = new TestIndex();
        var r = ix.Repo(1, "g/a");
        var f = ix.File(r, "a.c", "int Decode(void);\nint DecodeV2(void);\n// call Decode here\n");
        Writes.ReplaceSymbols(ix.Conn, r, f, [new Writes.SymbolRow("Decode", "prototype", 1, 1, "(void)", null, 1, null)], "2:x");
        var refs = Queries.FindReferences([r], ix.Conn, "Decode");
        Assert.Equal([1L, 3L], refs.Select(x => x["line"]!.GetValue<long>()));
        Assert.True(refs[0]["is_definition"]!.GetValue<bool>());
        Assert.False(refs[1]["is_definition"]!.GetValue<bool>());
    }

    [Fact]
    public void Impact_of_walks_reverse_includes_only_through_allowed_repos()
    {
        using var ix = new TestIndex();
        var a = ix.Repo(1, "g/a");
        var hidden = ix.Repo(2, "g/hidden");
        var b = ix.Repo(3, "g/b");
        ix.File(a, "core.h", "");
        var bridge = ix.File(hidden, "bridge.h", "");
        var leaf = ix.File(b, "leaf.c", "");
        Writes.ReplaceIncludes(ix.Conn, hidden, bridge, [new("core.h", 0)]);
        Writes.ReplaceIncludes(ix.Conn, b, leaf, [new("bridge.h", 0)]);
        Resolve.ResolveIncludes(ix.Conn);
        var all = Queries.ImpactOf([a, hidden, b], ix.Conn, a, "core.h");
        Assert.Equal(2, all["affected_files"]!.GetValue<int>());
        var visible = Queries.ImpactOf([a, b], ix.Conn, a, "core.h");
        Assert.Equal(0, visible["affected_files"]!.GetValue<int>());
    }

    [Fact]
    public void Branch_scoping_narrows_and_never_widens()
    {
        using var ix = new TestIndex();
        var main = ix.Repo(1, "g/a");
        var v2 = ix.Repo(1, "g/a", branch: "v2");
        var other = ix.Repo(2, "g/b");
        Assert.Equal([main], Queries.ScopeToBranch([main, v2], ix.Conn));
        Assert.Equal([v2], Queries.ScopeToBranch([main, v2, other], ix.Conn, "v2"));
        Assert.Empty(Queries.ScopeToBranch([main], ix.Conn, "v2"));
        Assert.Equal(["main", "v2"], Queries.BranchesAvailable([main, v2], ix.Conn));
    }

    [Fact]
    public void Explore_escapes_like_metacharacters()
    {
        using var ix = new TestIndex();
        var r = ix.Repo(1, "g/a");
        ix.File(r, "unique_beta.c", "");
        ix.File(r, "uniqueXbeta.c", "");
        var rows = (JsonArray)Explore.Files(ix.Conn, "unique_beta")["rows"]!;
        Assert.Single(rows);
        Assert.Equal(50, Explore.Files(ix.Conn, limit: 0)["limit"]!.GetValue<int>());
        Assert.Equal(200, Explore.Files(ix.Conn, limit: 5000)["limit"]!.GetValue<int>());
    }
}

public class ResolveTests
{
    static Resolve.FileRow F(long id, long repo, string path) => new(id, repo, path);

    [Theory]
    [InlineData("a/b/../c.h", "a/c.h")]
    [InlineData("../x.h", "../x.h")]
    [InlineData("./a//b/./c", "a/b/c")]
    [InlineData("/../a", "/a")]
    [InlineData("//a", "//a")]
    [InlineData("", ".")]
    public void Normpath_matches_posixpath(string input, string expected) => Assert.Equal(expected, PosixPath.NormPath(input));

    [Fact]
    public void Suffixes_are_slash_aligned()
    {
        Assert.Equal(["a/b/c.h", "b/c.h", "c.h"], Resolve.PathSuffixes("a/b/c.h"));
        var index = Resolve.BuildSuffixIndex([F(1, 1, "inc/eal_thread.h"), F(2, 1, "not_eal_thread.h")]);
        Assert.Single(index["eal_thread.h"]);
    }

    [Fact]
    public void A_relative_include_resolves_locally_first()
    {
        var files = new[] { F(1, 1, "src/a.h"), F(2, 2, "a.h") };
        var index = Resolve.BuildSuffixIndex(files);
        var byRepoPath = files.ToDictionary(f => (f.RepoId, f.Path));
        var (match, state) = Resolve.ResolveOne(new(1, "a.h", false, "src/main.c"), index, byRepoPath);
        Assert.Equal("resolved", state);
        Assert.Equal(1, match!.Value.Id);
    }

    [Fact]
    public void System_headers_found_only_in_other_repos_are_external()
    {
        var files = new[] { F(1, 2, "include/stdio.h") };
        var (match, state) = Resolve.ResolveOne(new(1, "stdio.h", true, "a.c"), Resolve.BuildSuffixIndex(files), files.ToDictionary(f => (f.RepoId, f.Path)));
        Assert.Null(match);
        Assert.Equal("external", state);
    }

    [Fact]
    public void Two_equally_shallow_candidates_are_ambiguous()
    {
        var files = new[] { F(1, 2, "x/config.h"), F(2, 3, "y/config.h") };
        var (_, state) = Resolve.ResolveOne(new(1, "config.h", false, "a.c"), Resolve.BuildSuffixIndex(files), files.ToDictionary(f => (f.RepoId, f.Path)));
        Assert.Equal("ambiguous", state);
    }

    [Fact]
    public void A_header_two_projects_have_is_not_chosen_by_depth_and_a_projects_branches_count_once()
    {
        // Two projects, one with the header nearer its top: not a reason to choose it.
        var files = new[] { F(1, 2, "config.h"), F(2, 3, "include/x/config.h") };
        var (_, deep) = Resolve.ResolveOne(new(1, "config.h", false, "a.c"), Resolve.BuildSuffixIndex(files), files.ToDictionary(f => (f.RepoId, f.Path)));
        Assert.Equal("ambiguous", deep);
        // One project indexed at two branches (rows 2 and 3): its default branch's header.
        var projects = new Dictionary<long, (long Project, bool Default)> { [1] = (10, true), [2] = (20, true), [3] = (20, false) };
        var branches = new Dictionary<long, string> { [1] = "feature", [2] = "main", [3] = "dev" };
        var two = new[] { F(1, 2, "include/money.h"), F(2, 3, "include/money.h") };
        var (match, state) = Resolve.ResolveOne(new(1, "money.h", true, "a.c"), Resolve.BuildSuffixIndex(two), two.ToDictionary(f => (f.RepoId, f.Path)),
            branchOfRepo: branches, projects: projects);
        Assert.Equal("resolved", state);
        Assert.Equal(2L, match!.Value.RepoId);
    }

    [Fact]
    public void A_bundled_copy_nested_deeper_than_the_original_is_vendored()
    {
        var names = new[] { "inflate.c", "deflate.c", "zutil.c", "adler32.c", "crc32.c" };
        var rows = names.Select((n, i) => F(i, 1, n))
            .Concat(names.Select((n, i) => F(100 + i, 2, "src/gzip/" + n)))
            .Append(F(200, 2, "src/main.c"));
        var vendored = Resolve.FindVendoredDirs(rows);
        Assert.Equal([(2L, "src/gzip")], vendored.ToList());
    }

    [Fact]
    public void A_copy_is_told_by_content_not_by_conventional_names_nor_another_branch_of_the_same_project()
    {
        var names = new[] { "inflate.c", "deflate.c", "zutil.c", "adler32.c", "crc32.c" };
        Resolve.FileRow Row(long id, long repo, string path, string sha, long project) => new(id, repo, path, sha, project);
        // The same names with other content: a project of its own, not a copy.
        var other = names.Select((n, i) => Row(i, 1, n, "a" + i, 10)).Concat(names.Select((n, i) => Row(100 + i, 2, "src/gzip/" + n, "b" + i, 20)));
        Assert.Empty(Resolve.FindVendoredDirs(other));
        // Their content: a copy.
        var copy = names.Select((n, i) => Row(i, 1, n, "a" + i, 10)).Concat(names.Select((n, i) => Row(100 + i, 2, "src/gzip/" + n, "a" + i, 20)));
        Assert.Equal([(2L, "src/gzip")], Resolve.FindVendoredDirs(copy).ToList());
        // Another branch of the same project holds the same files deeper: not a copy.
        var branch = names.Select((n, i) => Row(i, 1, n, "a" + i, 10)).Concat(names.Select((n, i) => Row(100 + i, 2, "moved/" + n, "a" + i, 10)));
        Assert.Empty(Resolve.FindVendoredDirs(branch));
        // Every package's own names (an __init__.py, a README, index and main files) say nothing.
        var conventions = new[] { "__init__.py", "README.md", "index.ts", "main.go", "models.py" };
        var common = conventions.Select((n, i) => Row(i, 1, n, "c", 10)).Concat(conventions.Select((n, i) => Row(100 + i, 2, "pkg/" + n, "c", 20)));
        Assert.Empty(Resolve.FindVendoredDirs(common));
    }

    [Fact]
    public void A_directory_named_after_its_own_repo_is_not_vendored()
    {
        IReadOnlySet<string> names = new HashSet<string> { "eal", "zlib" };
        Assert.False(Resolve.IsVendoredCopy("eal/include/eal/eal_thread.h", "eal", names));
        Assert.True(Resolve.IsVendoredCopy("src/zlib/zconf.h", "libpng", names));
    }
}

/// <summary>What admins choose for the index: repositories in or out, their branches, and new ones.</summary>
[Collection("process-state")]
public class ChoicesTests
{
    static Argus.Indexing.Project P(long id, string path) => new(id, path, "main", $"http://x/{path}.git");

    [Fact]
    public void New_repositories_follow_the_policy_and_known_ones_keep_their_choice()
    {
        using var ix = new TestIndex();
        Choices.Record(ix.Conn, [P(1, "g/a"), P(2, "g/b")], 100);
        Assert.True(Choices.Find(ix.Conn, 1)!.Included);
        Assert.True(Choices.Set(ix.Conn, 2, false, ["develop", "release/*"], 110));
        Choices.SetNewReposIncluded(ix.Conn, false);
        // GitLab lists them again (one renamed) and a new one.
        Choices.Record(ix.Conn, [P(1, "g/a-renamed"), P(2, "g/b"), P(3, "g/c")], 200);
        Assert.Equal("g/a-renamed", Choices.Find(ix.Conn, 1)!.Path);
        var b = Choices.Find(ix.Conn, 2)!;
        Assert.False(b.Included);
        Assert.Equal(["develop", "release/*"], b.Branches);
        Assert.False(Choices.Find(ix.Conn, 3)!.Included);
        Assert.False(Choices.Included(ix.Conn, 99));
        Assert.False(Choices.Set(ix.Conn, 99, true, null, 300));
    }

    [Fact]
    public void A_repository_taken_out_leaves_the_index_and_its_text_search_with_it()
    {
        using var ix = new TestIndex();
        var main = ix.Repo(5, "g/gone");
        var dev = ix.Repo(5, "g/gone", branch: "develop");
        ix.File(main, "a.c", "int UniqueGoneWord;\n");
        ix.File(dev, "b.c", "int UniqueGoneWord;\n");
        var kept = ix.Repo(6, "g/kept");
        ix.File(kept, "k.c", "int KeptWord;\n");
        int Matches(string word) => Convert.ToInt32(Sql.Scalar(ix.Conn, "SELECT COUNT(*) FROM files_fts WHERE files_fts MATCH ?", word));
        Assert.Equal(2, Matches("UniqueGoneWord"));

        Assert.Equal(2, Choices.Drop(ix.Conn, 5));
        Assert.Equal(0, Matches("UniqueGoneWord"));
        Assert.Equal(1, Matches("KeptWord"));
        Assert.Equal(0L, Sql.Scalar(ix.Conn, "SELECT COUNT(*) FROM files WHERE repo_id IN (?, ?)", main, dev));
    }

    [Fact]
    public void A_runs_progress_follows_the_steps_the_index_reports()
    {
        var p = new System.Text.Json.Nodes.JsonObject { ["outcomes"] = new System.Text.Json.Nodes.JsonObject() };
        var lines = new List<string>();
        Argus.Indexing.Progress.Write = lines.Add;
        try
        {
            Argus.Indexing.Progress.Pass(["g/a", "g/b"]);
            Argus.Indexing.Progress.Fetching(1, "g/a");
            Argus.Indexing.Progress.Branch(1, "g/a", "main");
            Argus.Indexing.Progress.Files("g/a", "main", 40, 160);
            Argus.Indexing.Progress.BranchDone("g/a", "main", "ok");
            Argus.Indexing.Progress.RepoDone("g/a", "ok", "Done in 2.0 s: 1 branch, 1 updated.");
            Argus.Indexing.Progress.Fetching(2, "g/b");
            Argus.Indexing.Progress.Branch(2, "g/b", "main");
            Argus.Indexing.Progress.Finishing("embeddings");
        }
        finally
        {
            Argus.Indexing.Progress.Write = line => Console.Out.WriteLine(line);
        }
        foreach (var line in lines)
        {
            Assert.StartsWith(Argus.Indexing.Progress.Prefix, line);
            Argus.Server.Jobs.Step(p, (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse(line[Argus.Indexing.Progress.Prefix.Length..])!);
        }
        Assert.Equal(2, p["repos"]!.GetValue<int>());
        Assert.Equal(2, p["position"]!.GetValue<int>());
        Assert.Equal("g/b", p["repo"]!.GetValue<string>());
        Assert.Equal(0, p["done"]!.GetValue<int>());
        Assert.Equal("ok", p["outcomes"]!["g/a@main"]!.GetValue<string>());
        Assert.Equal("finishing", p["stage"]!.GetValue<string>());
        Assert.Equal("embeddings", p["what"]!.GetValue<string>());
        Assert.Equal("done", p["by_repo"]!["g/a"]!["state"]!.GetValue<string>());
        Assert.Equal("files", p["by_repo"]!["g/b"]!["state"]!.GetValue<string>());
    }
}
