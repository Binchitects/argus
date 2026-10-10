using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Argus.Access;
using Argus.Configuration;
using Argus.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Argus.Tests;

/// <summary>
/// Coding agents connecting to Argus while GitLab is slow or down. Argus used to resolve the
/// person's repositories (a user lookup, then one member list per indexed project, one after
/// another) inside the gate of every request, initialize too: with 200 projects and a GitLab that
/// takes 200 ms, connecting took 40 s, and Claude Code, Qwen Code and Code Arena gave up on it.
/// </summary>
[Collection("process-state")]
public sealed class SlowGitLabTests : IAsyncLifetime
{
    const int Projects = 24;
    static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(400);
    readonly FakePlatform _platform = new();
    readonly TestIndex _ix = new();
    readonly StringWriter _log = new();
    EnvScope _env = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _env = new EnvScope(("ARGUS_KEY_CHECK_URL", _platform.KeyCheckUrl), ("ARGUS_CHAT_CLIENT_TOKEN", FakePlatform.Credential),
            ("ARGUS_APP", "off"), ("ARGUS_ACCESS_NOTICES", "0"), ("ARGUS_AUDIT_LOG", "1"), ("ARGUS_INDEX_INTERVAL", "0"), ("ARGUS_USERS_FILE", null));
        AuditLog.Writer = _log;
        for (var i = 0; i < Projects; i++)
        {
            var repo = _ix.Repo(100 + i, $"corp/service-{i:00}");
            _platform.Members[100 + i] = [(5, "alice", "Alice", 30)];
            if (i == 0) _ix.Symbol(repo, _ix.File(repo, "src/decode.c", "int DecodeFrame(int x) { return x; }\n"), "DecodeFrame");
        }
        _platform.GitLabDelay = Delay;
        var cfg = _ix.Config() with { GitLab = GitLabConfig.Create(_platform.Url, "svc") };
        _app = ArgusServer.Create(cfg, configure: b => b.WebHost.UseTestServer());
        await _app.StartAsync();
        _http = _app.GetTestClient();
        _http.BaseAddress = new Uri("http://localhost:7700");
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        AuditLog.Writer = null;
        _env.Dispose();
        _ix.Dispose();
        await _platform.DisposeAsync();
    }

    async Task<JsonNode> ListTools(string session)
    {
        var resp = await _http.SendAsync(McpCalls.Request(new { jsonrpc = "2.0", id = 3, method = "tools/list" }, "sk-alice", session));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return McpCalls.Rpc(await resp.Content.ReadAsStringAsync())["result"]!;
    }

    [Fact]
    public async Task Connecting_waits_for_neither_gitlab_nor_its_projects_and_the_first_code_tool_asks_gitlab_in_parallel_once()
    {
        // Four agents connect at once (an IDE and three terminals, or one agent's several sessions).
        var clock = Stopwatch.StartNew();
        var sessions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => McpCalls.Session(_http, "sk-alice")));
        await Task.WhenAll(sessions.Select(ListTools));
        // Before: (1 + 24 projects) × 400 ms = 10 s for each request, initialize included.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"connecting took {clock.Elapsed.TotalSeconds:0.0} s");

        clock.Restart();
        var found = await Task.WhenAll(sessions.Select(s => McpCalls.Call(_http, "sk-alice", s, "find_symbol", new { name = "DecodeFrame" })));
        foreach (var f in found)
        {
            Assert.False(f["isError"]!.GetValue<bool>(), f.ToJsonString());
            Assert.Equal("DecodeFrame", f["structuredContent"]!["result"]![0]!["name"]!.GetValue<string>());
        }
        // The user looked up (by email, then by username) and one member list per project, for all of them together, several at a time.
        Assert.Equal(2 + Projects, _platform.GitLabCalls);
        Assert.InRange(_platform.GitLabMostAtOnce, 4, MemberDirectory.Parallel);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"the first call took {clock.Elapsed.TotalSeconds:0.0} s");
        // And the app was asked about the key once.
        Assert.Equal(1, _platform.ChecksOf("sk-alice"));

        // Later calls find the answer kept.
        clock.Restart();
        Assert.False((await McpCalls.Call(_http, "sk-alice", sessions[0], "find_symbol", new { name = "DecodeFrame" }))["isError"]!.GetValue<bool>());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(2 + Projects, _platform.GitLabCalls);
    }

    [Fact]
    public async Task With_gitlab_down_the_agent_still_connects_and_a_code_tool_says_why_it_cannot_answer()
    {
        _platform.GitLabDown = true;
        _platform.GitLabDelay = TimeSpan.Zero;
        var session = await McpCalls.Session(_http, "sk-alice");
        Assert.Equal(20, (await ListTools(session))["tools"]!.AsArray().Count);

        var refused = await McpCalls.Call(_http, "sk-alice", session, "find_symbol", new { name = "DecodeFrame" });
        Assert.True(refused["isError"]!.GetValue<bool>());
        Assert.Contains("Cannot verify your GitLab access right now", refused["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("\"reason\": \"access_unresolved\"", _log.ToString(), StringComparison.Ordinal);

        // GitLab back: the next call is answered, no reconnecting needed.
        _platform.GitLabDown = false;
        Assert.False((await McpCalls.Call(_http, "sk-alice", session, "find_symbol", new { name = "DecodeFrame" }))["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_key_the_app_refuses_is_still_refused_at_the_door()
    {
        var resp = await McpCalls.Initialize(_http, "sk-nobody");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(0, _platform.GitLabCalls);
    }
}

/// <summary>A person's repositories, kept and refreshed apart from the requests.</summary>
public sealed class PersonAccessTests
{
    /// <summary>Repository rows whose id is their GitLab project's.</summary>
    static readonly IReadOnlyList<(long Id, long GitlabId)> Rows = [(1, 1), (2, 2), (3, 3), (7, 7)];

    /// <summary>Alice's access to the projects asked about: those of <paramref name="readable"/> among them.</summary>
    static Grant Alice(IReadOnlyCollection<long> projects, params long[] readable) =>
        new(5, "alice", projects.ToHashSet(), readable.Where(projects.Contains).ToHashSet());

    [Fact]
    public async Task Kept_ten_minutes_then_served_while_refreshed_and_resolved_once_for_everyone_asking_at_once()
    {
        double now = 1000;
        var calls = 0;
        long[] answer = [1, 2];
        var gate = new TaskCompletionSource();
        var access = new PersonAccess((_, _, projects) =>
        {
            Interlocked.Increment(ref calls);
            gate.Task.Wait();
            return Alice(projects, answer);
        }, () => Rows, () => now);

        var asking = Enumerable.Range(0, 10).Select(_ => Task.Run(() => access.Resolve("Alice@corp.example", "alice"))).ToList();
        await Task.Delay(200);
        gate.SetResult();
        foreach (var identity in await Task.WhenAll(asking)) Assert.Equal([1L, 2L], identity.AllowedRepoIds);
        Assert.Equal(1, calls);

        now += Acl.TtlSeconds - 1;
        Assert.Equal([1L, 2L], access.Resolve("alice@corp.example ", "alice").AllowedRepoIds);
        Assert.Equal(1, calls);

        // Past ten minutes: the last answer at once, a fresh one fetched behind it.
        now += 2;
        answer = [1, 2, 3];
        Assert.Equal([1L, 2L], access.Resolve("alice@corp.example", "alice").AllowedRepoIds);
        await Until(() => access.Resolve("alice@corp.example", "alice").AllowedRepoIds.Count == 3);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_definite_no_is_not_served_from_before_but_gitlab_being_unwell_is()
    {
        double now = 1000;
        Func<IReadOnlyCollection<long>, Grant> next = projects => Alice(projects, 1);
        var access = new PersonAccess((_, _, projects) => next(projects), () => Rows, () => now);
        Assert.Single(access.Resolve("alice@corp.example", null).AllowedRepoIds);

        // GitLab unwell: the last answer stays, within the hour.
        now += Acl.TtlSeconds + 1;
        next = _ => throw new AclDenied("Cannot verify", new GitLabUnavailable("503"));
        access.Resolve("alice@corp.example", null);
        await Task.Delay(300);
        Assert.Single(access.Resolve("alice@corp.example", null).AllowedRepoIds);

        // The account gone in GitLab: refused from then on.
        next = _ => throw new AclDenied("No GitLab account matches alice@corp.example");
        now += 1;
        access.Resolve("alice@corp.example", null);
        await Until(() =>
        {
            try { access.Resolve("alice@corp.example", null); return false; }
            catch (AclDenied exc) { return exc.Message.StartsWith("No GitLab account", StringComparison.Ordinal); }
        });
    }

    [Fact]
    public async Task Warming_starts_the_resolution_without_waiting_for_it()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var access = new PersonAccess((_, _, projects) =>
        {
            started.TrySetResult();
            release.Task.Wait();
            return Alice(projects, 7);
        }, () => Rows);
        var clock = Stopwatch.StartNew();
        access.Warm("alice@corp.example", "alice");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        Assert.Equal([7L], access.Resolve("alice@corp.example", "alice").AllowedRepoIds);
    }

    [Fact]
    public void What_is_kept_follows_the_repositories_not_their_row_ids_and_one_indexed_since_is_seen_at_once()
    {
        // Alice may read GitLab projects 200 (corp/payments) and 300; not 999 (corp/hr-secrets).
        IReadOnlyList<(long Id, long GitlabId)> rows = [(1, 100), (2, 200)];
        var asked = new List<long[]>();
        var access = new PersonAccess((_, _, projects) =>
        {
            lock (asked) asked.Add([.. projects.Order()]);
            return Alice(projects, 200, 300);
        }, () => rows, () => 1000);
        Assert.Equal([2L], access.Resolve("alice@corp.example", "alice").AllowedRepoIds);

        // corp/payments taken out, and corp/hr-secrets indexed into the row id it left (SQLite gives the highest one again).
        rows = [(1, 100)];
        Assert.Empty(access.Resolve("alice@corp.example", "alice").AllowedRepoIds);
        rows = [(1, 100), (2, 999)];
        Assert.Empty(access.Resolve("alice@corp.example", "alice").AllowedRepoIds);

        // A repository she may read, indexed since: hers at the next request, not ten minutes later.
        rows = [(1, 100), (2, 999), (3, 300)];
        Assert.Equal([3L], access.Resolve("alice@corp.example", "alice").AllowedRepoIds);
        Assert.Equal([3L], access.Resolve("alice@corp.example", "alice").AllowedRepoIds);
        // Each project asked about once: all of them first, then each newly indexed one alone.
        Assert.Equal(["100,200", "999", "300"], asked.Select(a => string.Join(",", a)));
    }

    [Fact]
    public void A_repository_indexed_while_gitlab_is_unwell_stays_closed_and_is_asked_about_again()
    {
        IReadOnlyList<(long Id, long GitlabId)> rows = [(1, 1)];
        var down = false;
        var access = new PersonAccess((_, _, projects) =>
            down ? throw new AclDenied("Cannot verify", new GitLabUnavailable("503")) : Alice(projects, 1, 2), () => rows, () => 1000);
        Assert.Equal([1L], access.Resolve("alice@corp.example", null).AllowedRepoIds);

        rows = [(1, 1), (2, 2)];
        down = true;
        Assert.Equal([1L], access.Resolve("alice@corp.example", null).AllowedRepoIds);
        down = false;
        Assert.Equal([1L, 2L], access.Resolve("alice@corp.example", null).AllowedRepoIds);
    }

    static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++) await Task.Delay(50);
        Assert.True(done());
    }
}

/// <summary>GitLab asked for a bounded number of member lists at once, however many people ask.</summary>
public sealed class MemberDirectoryTests : IAsyncLifetime
{
    readonly FakePlatform _platform = new() { GitLabDelay = TimeSpan.FromMilliseconds(150) };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _platform.DisposeAsync();

    [Fact]
    public async Task People_resolving_at_the_same_moment_ask_gitlab_for_at_most_eight_member_lists_at_once()
    {
        var directory = new MemberDirectory(GitLabConfig.Create(_platform.Url, "svc"));
        // Four people's agents reconnect together after a restart, each with 16 projects of their own: 64 lists to fetch.
        var people = Enumerable.Range(0, 4).Select(p => (IReadOnlyCollection<long>)[.. Enumerable.Range(0, 16).Select(i => 1000L + p * 100 + i)]).ToList();
        await Task.WhenAll(people.Select(ids => Task.Run(() => directory.Prefetch(ids)))).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(64, _platform.GitLabCalls);
        // Each person's are fetched in parallel, but not eight for each of them: eight for all of them.
        Assert.InRange(_platform.GitLabMostAtOnce, 2, MemberDirectory.Parallel);
    }
}

/// <summary>The key check asked once for requests that bring the same key together.</summary>
public sealed class ArenaKeySingleFlightTests : IAsyncLifetime
{
    readonly FakePlatform _platform = new() { KeyDelay = TimeSpan.FromMilliseconds(300) };

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _platform.DisposeAsync();

    [Fact]
    public async Task Requests_at_the_same_moment_share_one_check_and_a_refusal_too()
    {
        var keys = new ArenaKeys(_platform.KeyCheckUrl, FakePlatform.Credential);
        var people = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => keys.CheckAsync("sk-alice")));
        Assert.All(people, p => Assert.Equal("alice", p.Username));
        Assert.Equal(1, _platform.ChecksOf("sk-alice"));

        var refused = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => await Record.ExceptionAsync(() => keys.CheckAsync("sk-nobody"))));
        Assert.All(refused, e => Assert.Equal(ArenaKeys.Refused, Assert.IsType<AclDenied>(e).Message));
        Assert.Equal(1, _platform.ChecksOf("sk-nobody"));

        // One request giving up does not fail the others waiting on the same answer.
        using var gone = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var quitter = keys.CheckAsync("sk-carol-unknown", gone.Token);
        var stayer = keys.CheckAsync("sk-carol-unknown");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quitter);
        Assert.Equal(ArenaKeys.Refused, (await Assert.ThrowsAsync<AclDenied>(() => stayer)).Message);
        Assert.Equal(1, _platform.ChecksOf("sk-carol-unknown"));
    }
}
