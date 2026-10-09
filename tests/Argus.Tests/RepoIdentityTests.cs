using System.Net;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Indexing;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Tests;

/// <summary>
/// A repository is its project in one GitLab: a new token, another account or a GitLab set
/// up anew never makes a second copy of it, and an index v5.2.0 left with copies is merged.
/// </summary>
[Collection("process-state")]
public class RepoIdentityTests
{
    const string GitLabA = "http://gitlab.test:8929";
    const string GitLabB = "https://gitlab.example.com";

    static Project P(long id, string path, string? created = null, string gitlab = GitLabA) =>
        new(id, path, "main", $"{gitlab}/{path}.git", created);

    static long Count(SqliteConnection conn, string sql, params object?[] args) => Convert.ToInt64(Sql.Scalar(conn, sql, args));

    static int Matches(SqliteConnection conn, string word) => (int)Count(conn, "SELECT COUNT(*) FROM files_fts WHERE files_fts MATCH ?", word);

    [Fact]
    public void Repositories_listed_again_under_new_ids_keep_their_index_and_choices_and_are_not_copied()
    {
        using var ix = new TestIndex();
        Choices.Record(ix.Conn, [P(1, "root/eal-core"), P(2, "root/etl-decoder")], 100, GitLabA);
        var main = ix.Repo(1, "root/eal-core");
        var file = ix.File(main, "src/decode.c", "int DecodeFrame(void) { return 0; }\n");
        ix.Symbol(main, file, "DecodeFrame");
        Writes.SetLastIndexed(ix.Conn, main, "abc123", 100);
        Assert.True(Choices.Set(ix.Conn, 2, false, ["release/*"], 110));
        Writes.UpsertAclCache(ix.Conn, Acl.Hash("tok"), 5, "alice", $"[{main}]", 100);

        // Brought up again with another token, on a GitLab set up anew: the same paths, new ids.
        var listing = Choices.Record(ix.Conn, [P(7, "root/eal-core", "2026-10-01T10:00:00Z"), P(8, "root/etl-decoder", "2026-10-01T10:00:01Z")], 200, GitLabA);

        Assert.Equal(new Listing(0, 2, 0), listing);
        Assert.Equal([7L, 8L], Choices.List(ix.Conn).Select(c => c.GitlabId).Order());
        var etl = Choices.Find(ix.Conn, 8)!;
        Assert.False(etl.Included);
        Assert.Equal(["release/*"], etl.Branches);
        Assert.Equal("2026-10-01T10:00:01Z", etl.CreatedAt);
        // The index row (its files, symbols and text search) belongs to the repository under its new id.
        Assert.Equal(7L, Count(ix.Conn, "SELECT gitlab_id FROM repos WHERE id = ?", main));
        Assert.Single(Queries.FindSymbol([main], ix.Conn, "DecodeFrame"));
        Assert.Equal(1, Matches(ix.Conn, "DecodeFrame"));
        Assert.Equal(0L, Count(ix.Conn, "SELECT COUNT(*) FROM acl_cache"));

        // Listed again as they are now: nothing moves.
        Assert.Equal(new Listing(0, 0, 0), Choices.Record(ix.Conn, [P(7, "root/eal-core", "2026-10-01T10:00:00Z"), P(8, "root/etl-decoder", "2026-10-01T10:00:01Z")], 300, GitLabA));
        Assert.Equal(2, Choices.List(ix.Conn).Count);
    }

    [Fact]
    public void A_renamed_project_keeps_its_row_and_a_new_one_at_its_old_path_is_another_repository()
    {
        using var ix = new TestIndex();
        Choices.Record(ix.Conn, [P(1, "g/a", "2026-01-01T00:00:00Z")], 100, GitLabA);
        var row = ix.Repo(1, "g/a");
        // g/a renamed to g/b, and a new project made at g/a.
        var listing = Choices.Record(ix.Conn, [P(1, "g/b", "2026-01-01T00:00:00Z"), P(2, "g/a", "2026-09-01T00:00:00Z")], 200, GitLabA);

        Assert.Equal(new Listing(1, 0, 0), listing);
        Assert.Equal("g/b", Choices.Find(ix.Conn, 1)!.Path);
        Assert.Equal("g/a", Choices.Find(ix.Conn, 2)!.Path);
        Assert.Equal(1L, Count(ix.Conn, "SELECT gitlab_id FROM repos WHERE id = ?", row));
    }

    [Fact]
    public void Another_gitlab_giving_a_known_id_to_another_project_gets_none_of_its_index()
    {
        using var ix = new TestIndex();
        Choices.Record(ix.Conn, [P(1, "root/eal-core"), P(5, "team/old-tool")], 100, GitLabA);
        var eal = ix.Repo(1, "root/eal-core");
        ix.File(eal, "src/decode.c", "int DecodeFrame(void);\n");
        var old = ix.Repo(5, "team/old-tool");
        ix.File(old, "tool.c", "int OldToolSecret;\n");

        // The other GitLab: id 1 is another project, eal-core is id 3, and id 5 is a project Argus never saw.
        var listing = Choices.Record(ix.Conn, [P(1, "root/driver-shim", gitlab: GitLabB), P(3, "root/eal-core", gitlab: GitLabB), P(5, "other/new", gitlab: GitLabB)],
            200, GitLab.Instance(GitLabB));

        Assert.Equal(new Listing(2, 1, 1), listing);
        Assert.Equal(3L, Count(ix.Conn, "SELECT gitlab_id FROM repos WHERE id = ?", eal));
        // Neither project now holding id 1 or 5 inherits an index row (or the code in it).
        Assert.Equal(0L, Count(ix.Conn, "SELECT COUNT(*) FROM repos WHERE gitlab_id IN (1, 5)"));
        // The old tool is set aside under an id no GitLab project has: kept, as one GitLab no longer lists.
        var aside = Choices.List(ix.Conn).Single(c => c.Path == "team/old-tool");
        Assert.True(aside.GitlabId < 0);
        Assert.Equal(aside.GitlabId, Count(ix.Conn, "SELECT gitlab_id FROM repos WHERE id = ?", old));
        Assert.True(aside.SeenAt < Choices.ListedAt(ix.Conn));
        Assert.Equal("https://gitlab.example.com", Choices.Find(ix.Conn, 3)!.Instance);
    }

    [Fact]
    public void Paths_are_compared_as_gitlab_compares_them_without_case()
    {
        using var ix = new TestIndex();
        Choices.Record(ix.Conn, [P(1, "Root/Eal-Core")], 100, GitLabA);
        Choices.Record(ix.Conn, [P(9, "root/eal-core")], 200, GitLabA);
        Assert.Equal("root/eal-core", Assert.Single(Choices.List(ix.Conn)).Path);
    }

    /// <summary>The users and project members a GitLab answers with, for the permission lookups.</summary>
    sealed class Members(Dictionary<long, long[]> members) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => Answer(request.RequestUri!);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Answer(request.RequestUri!));

        static HttpResponseMessage Ok(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };

        HttpResponseMessage Answer(Uri uri)
        {
            if (uri.AbsolutePath == "/api/v4/users")
                return Ok(new JsonArray(new JsonObject { ["id"] = 5, ["username"] = "alice", ["public_email"] = "alice@example.test", ["state"] = "active" }));
            var parts = uri.AbsolutePath.Split('/');
            if (parts.Length == 7 && parts[3] == "projects" && long.TryParse(parts[4], out var id))
            {
                if (!members.TryGetValue(id, out var users)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (uri.Query.Contains("page=2", StringComparison.Ordinal)) return Ok(new JsonArray());
                return Ok(new JsonArray([.. users.Select(u => (JsonNode)new JsonObject
                {
                    ["id"] = u, ["username"] = u == 5 ? "alice" : $"user{u}", ["name"] = "", ["access_level"] = 30, ["state"] = "active",
                })]));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    /// <summary>An index row as v5.2.0 wrote it: its files (with their text search), symbols, commit and last run.</summary>
    static long V520Row(SqliteConnection conn, long gitlabId, string path, string branch, string? sha, long? indexedAt, long? runAt, string? error, string word)
    {
        var id = Writes.UpsertRepo(conn, gitlabId, path, "main", $"{GitLabA}/{path}.git", branch);
        var file = Writes.UpsertFile(conn, id, $"src/{word}.c", "c", 20, "sha-" + word, $"int {word}(void);\n");
        Writes.ReplaceSymbols(conn, id, file, [new Writes.SymbolRow(word, "function", 1, 1, "(void)", null, 1, null)], "2:x");
        if (sha is not null)
        {
            Writes.SetLastIndexed(conn, id, sha, indexedAt!.Value);
            Writes.SetCommitInfo(conn, id, $"commit {sha}", indexedAt.Value);
        }
        Writes.RecordRunState(conn, id, false, false, runAt ?? 0, error);
        Writes.RecordError(conn, id, $"src/{word}.c", "read", "a test error", runAt ?? 0);
        Sql.Exec(conn, "INSERT INTO retry_attempts (repo_id, path, attempts) VALUES (?, ?, 1)", id, $"src/{word}.c");
        Sql.Exec(conn, "INSERT INTO argus_meta (key, value) VALUES (?, '6')", Worker.ContractKey(id));
        return id;
    }

    static void V520Choice(SqliteConnection conn, long gitlabId, string path, long seenAt, bool included = true, string branches = "", long? changedAt = null) =>
        Sql.Exec(conn, "INSERT INTO repo_choices (gitlab_id, path_with_namespace, default_branch, http_url, included, branches, seen_at, changed_at) VALUES (?, ?, 'main', ?, ?, ?, ?, ?)",
            gitlabId, path, $"{GitLabA}/{path}.git", included ? 1L : 0L, branches, seenAt, changedAt);

    [Fact]
    public void Upgrading_an_index_v520_left_with_every_repository_twice_merges_the_copies_and_keeps_what_was_indexed()
    {
        using var dir = new TempDir();
        var db = Path.Combine(dir.Path, "index.db");
        long ealOldMain, ealOldV2, ealNewMain, etlOld, etlNew, shimOld, shimNew;
        using (var conn = Db.Connect(db))
        {
            Assert.Equal(16, Db.Migrate(conn, upTo: 16));
            // Brought up with one token: three repositories, ids 1 to 3; an admin added v2 to eal-core.
            V520Choice(conn, 1, "root/eal-core", 1000, branches: "v2", changedAt: 1100);
            V520Choice(conn, 2, "root/etl-decoder", 1000);
            V520Choice(conn, 3, "root/driver-shim", 1000);
            ealOldMain = V520Row(conn, 1, "root/eal-core", "main", "aaa111", 1000, 1000, null, "OldEalMain");
            ealOldV2 = V520Row(conn, 1, "root/eal-core", "v2", "aaa222", 1000, 1000, null, "OldEalV2");
            etlOld = V520Row(conn, 2, "root/etl-decoder", "main", "bbb111", 1000, 1000, null, "OldEtl");
            shimOld = V520Row(conn, 3, "root/driver-shim", "main", "ccc111", 1000, 1000, null, "OldShim");
            // Then with another token, on the GitLab set up again: the same repositories under ids 11 to 13.
            V520Choice(conn, 11, "root/eal-core", 2000);
            V520Choice(conn, 12, "root/etl-decoder", 2000);
            V520Choice(conn, 13, "Root/Driver-Shim", 2000);
            ealNewMain = V520Row(conn, 11, "root/eal-core", "main", "ddd111", 2000, 2000, null, "NewEalMain");
            // The new copy of etl-decoder never got indexed: its fetch failed.
            etlNew = V520Row(conn, 12, "root/etl-decoder", "main", null, null, 2000, "git clone failed", "NewEtl");
            shimNew = V520Row(conn, 13, "Root/Driver-Shim", "main", "eee111", 2000, 2000, null, "NewShim");
            Writes.UpsertAclCache(conn, Acl.Hash("tok"), 5, "alice", $"[{ealOldMain}, {etlOld}]", 2000);
            Assert.Equal(6L, Count(conn, "SELECT COUNT(*) FROM repo_choices"));
        }

        using (var conn = Db.Open(db))
        {
            Assert.Equal(Db.Migrations[^1].Version, Convert.ToInt32(Sql.Scalar(conn, "PRAGMA user_version")));
            var choices = Choices.List(conn);
            // One row per repository, under the ids GitLab gave them last.
            Assert.Equal([11L, 12L, 13L], choices.Select(c => c.GitlabId).Order());
            var eal = choices.Single(c => c.GitlabId == 11);
            Assert.Equal(["v2"], eal.Branches);
            Assert.Equal(1100L, eal.ChangedAt);
            Assert.Equal("http://gitlab.test:8929", eal.Instance);
            Assert.Equal("", eal.Schedule);

            // Per branch, the newest good index: the new eal-core main, its old v2, the old etl-decoder (the new one failed), the new driver-shim.
            var rows = Sql.Query(conn, "SELECT id, gitlab_id, branch, path_with_namespace FROM repos ORDER BY id").ToDictionary(r => r.Long("id"));
            Assert.Equal([ealOldV2, etlOld, ealNewMain, shimNew], rows.Keys.Order());
            Assert.Equal(11L, rows[ealOldV2].Long("gitlab_id"));
            Assert.Equal(12L, rows[etlOld].Long("gitlab_id"));
            Assert.Equal("Root/Driver-Shim", rows[shimNew].Str("path_with_namespace"));
            Assert.Equal("bbb111", Sql.One(conn, "SELECT last_indexed_sha FROM repos WHERE id = ?", etlOld)!.Str("last_indexed_sha"));

            // The copies that went took their files, symbols, errors, retries and text search with them; the rest is whole.
            foreach (var gone in new[] { ealOldMain, etlNew, shimOld })
            {
                Assert.Equal(0L, Count(conn, "SELECT COUNT(*) FROM files WHERE repo_id = ?", gone));
                Assert.Equal(0L, Count(conn, "SELECT COUNT(*) FROM symbols WHERE repo_id = ?", gone));
                Assert.Equal(0L, Count(conn, "SELECT COUNT(*) FROM index_errors WHERE repo_id = ?", gone));
                Assert.Equal(0L, Count(conn, "SELECT COUNT(*) FROM retry_attempts WHERE repo_id = ?", gone));
                Assert.Equal(0L, Count(conn, "SELECT COUNT(*) FROM argus_meta WHERE key = ?", Worker.ContractKey(gone)));
            }
            Assert.Equal(0, Matches(conn, "OldEalMain"));
            Assert.Equal(0, Matches(conn, "NewEtl"));
            Assert.Equal(0, Matches(conn, "OldShim"));
            foreach (var kept in new[] { "OldEalV2", "OldEtl", "NewEalMain", "NewShim" }) Assert.Equal(1, Matches(conn, kept));
            Sql.Exec(conn, "INSERT INTO files_fts(files_fts) VALUES ('integrity-check')");
            Assert.Equal(1L, Count(conn, "SELECT COUNT(*) FROM argus_meta WHERE key = ?", Worker.ContractKey(etlOld)));
            // Permissions cached for the old rows are read again.
            Assert.Equal(0L, Count(conn, "SELECT COUNT(*) FROM acl_cache"));

            // The chat's Argus tools: a person in eal-core and etl-decoder (by GitLab's membership of
            // the ids it gives them now) reads what was indexed under either copy.
            using var http = new HttpClient(new Members(new() { [11] = [5], [12] = [5], [13] = [] }));
            var person = People.ResolvePerson(conn, new MemberDirectory(GitLabConfig.Create(GitLabA, "svc"), http), "alice@example.test");
            Assert.Equal([ealOldV2, etlOld, ealNewMain], person.AllowedRepoIds.Order());
            Assert.Single(Queries.FindSymbol(person.AllowedRepoIds, conn, "OldEtl"));
            Assert.Single(Queries.FindSymbol(person.AllowedRepoIds, conn, "OldEalV2"));
            Assert.Empty(Queries.FindSymbol(person.AllowedRepoIds, conn, "NewShim"));

            // The next listing finds every repository where it is.
            Assert.Equal(new Listing(0, 0, 0), Choices.Record(conn, [P(11, "root/eal-core"), P(12, "root/etl-decoder"), P(13, "Root/Driver-Shim")], 3000, GitLabA));
            Assert.Equal(3, Choices.List(conn).Count);
            // And the migrations, run again, change nothing.
            Assert.Equal(Db.Migrations[^1].Version, Db.Migrate(conn));
        }
    }

    [Fact]
    public void Upgrading_keeps_a_repository_in_the_index_when_any_copy_of_it_was_in()
    {
        using var dir = new TempDir();
        var db = Path.Combine(dir.Path, "index.db");
        long ealNew, etlOld;
        using (var conn = Db.Connect(db))
        {
            Db.Migrate(conn, upTo: 16);
            Sql.Exec(conn, "INSERT INTO argus_meta (key, value) VALUES ('index.new_repos', 'exclude')");
            // Cleaned up by hand on v5.2.0: the stale old copy turned off; the new copy holds the index.
            V520Choice(conn, 1, "root/eal-core", 1000, included: false, branches: "v2", changedAt: 3000);
            V520Choice(conn, 11, "root/eal-core", 2000, branches: "release/*");
            ealNew = V520Row(conn, 11, "root/eal-core", "main", "def111", 2000, 2000, null, "NewEal");
            // "New repositories: leave them out": the old copy (in since migration 016) holds the index, the policy left the new one out.
            V520Choice(conn, 2, "root/etl-decoder", 1000, branches: "develop");
            etlOld = V520Row(conn, 2, "root/etl-decoder", "main", "abc111", 1000, 1000, null, "OldEtl");
            V520Choice(conn, 12, "root/etl-decoder", 2000, included: false);
            // Both copies in, each with branches of its own: every one stays, the kept copy's first.
            V520Choice(conn, 3, "root/driver-shim", 1000, branches: "v2\nrelease/*", changedAt: 1100);
            V520Choice(conn, 13, "root/driver-shim", 2000, branches: "release/*, hotfix\r\n");
            // Both copies out: so is the repository.
            V520Choice(conn, 4, "root/retired", 1000, included: false, branches: "a", changedAt: 1500);
            V520Choice(conn, 14, "root/retired", 2000, included: false, branches: "b", changedAt: 2500);
        }
        using (var conn = Db.Open(db))
        {
            var eal = Choices.Find(conn, 11)!;
            Assert.True(eal.Included);
            // The branches of the copy turned off are not taken up.
            Assert.Equal(["release/*"], eal.Branches);
            Assert.Equal(3000L, eal.ChangedAt);
            Assert.Equal("def111", Sql.One(conn, "SELECT last_indexed_sha FROM repos WHERE id = ?", ealNew)!.Str("last_indexed_sha"));

            var etl = Choices.Find(conn, 12)!;
            Assert.True(etl.Included);
            Assert.Equal(["develop"], etl.Branches);
            Assert.Null(etl.ChangedAt);
            Assert.Equal(12L, Count(conn, "SELECT gitlab_id FROM repos WHERE id = ?", etlOld));

            Assert.Equal(["release/*", "hotfix", "v2"], Choices.Find(conn, 13)!.Branches);
            var retired = Choices.Find(conn, 14)!;
            Assert.False(retired.Included);
            Assert.Equal(["b", "a"], retired.Branches);
            Assert.Equal(2500L, retired.ChangedAt);
            Assert.Equal(4, Choices.List(conn).Count);

            // What the next pass sees: both indexed repositories are chosen, so it keeps their index.
            Assert.True(Choices.Included(conn, 11));
            Assert.True(Choices.Included(conn, 12));
            Assert.Equal(1, Matches(conn, "NewEal"));
            Assert.Equal(1, Matches(conn, "OldEtl"));
        }
    }

    [Fact]
    public void An_index_without_copies_upgrades_untouched()
    {
        using var dir = new TempDir();
        var db = Path.Combine(dir.Path, "index.db");
        long row;
        using (var conn = Db.Connect(db))
        {
            Db.Migrate(conn, upTo: 16);
            V520Choice(conn, 4, "g/solo", 1000, included: false, changedAt: 1200);
            row = V520Row(conn, 4, "g/solo", "main", "fff111", 1000, 1000, null, "Solo");
            Writes.UpsertAclCache(conn, Acl.Hash("tok"), 5, "alice", $"[{row}]", 1000);
        }
        using (var conn = Db.Open(db))
        {
            var solo = Assert.Single(Choices.List(conn));
            Assert.Equal(4L, solo.GitlabId);
            Assert.False(solo.Included);
            Assert.Equal(4L, Count(conn, "SELECT gitlab_id FROM repos WHERE id = ?", row));
            Assert.Equal(1, Matches(conn, "Solo"));
        }
    }
}
