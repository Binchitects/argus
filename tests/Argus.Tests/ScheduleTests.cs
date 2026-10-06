using Argus.Indexing;
using Argus.Server;
using Argus.Store;
using Argus.Util;

namespace Argus.Tests;

/// <summary>Reindexing on a schedule: each repository's own, or the default for all, run by Argus's scheduler.</summary>
[Collection("process-state")]
public class ScheduleTests
{
    static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    static long At(string utc) => DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeSeconds();

    [Theory]
    [InlineData("pass", "pass", "With each scheduled pass")]
    [InlineData(" OFF ", "off", "Off: only pushes and when asked")]
    [InlineData("hours:1", "hours:1", "Every hour")]
    [InlineData("hours:6", "hours:6", "Every 6 hours")]
    [InlineData("daily:2:30", "daily:02:30", "Every day at 02:30")]
    [InlineData("weekly:7:23:05", "weekly:7:23:05", "Sundays at 23:05")]
    public void A_schedule_reads_and_says_itself(string text, string stored, string words)
    {
        Assert.True(RepoSchedule.TryParse(text, out var s, out var problem), problem);
        Assert.Equal(stored, s.ToString());
        Assert.Equal(words, s.Words);
    }

    [Theory]
    [InlineData("hours:0")]
    [InlineData("hours:169")]
    [InlineData("daily:24:00")]
    [InlineData("weekly:8:01:00")]
    [InlineData("every day")]
    [InlineData("")]
    public void Anything_else_is_refused_with_what_would_do(string text)
    {
        Assert.False(RepoSchedule.TryParse(text, out _, out var problem));
        Assert.Contains("daily:HH:MM", problem, StringComparison.Ordinal);
        // A stored value that cannot be read is the default: with each pass.
        Assert.Equal(RepoSchedule.WithPass, RepoSchedule.Parse(text));
    }

    [Fact]
    public void A_time_of_day_comes_round_in_the_zone_across_a_clock_change()
    {
        var daily = RepoSchedule.Parse("daily:02:30");
        // Berlin goes to summer time at 02:00 on 29 March 2026: 02:30 that day does not exist, so 03:30 (01:30 UTC).
        var next = daily.Next(DateTimeOffset.FromUnixTimeSeconds(At("2026-03-28T12:00:00Z")), null, Berlin)!.Value;
        Assert.Equal(At("2026-03-29T01:30:00Z"), next.ToUnixTimeSeconds());
        // The day after: 02:30 summer time (00:30 UTC).
        Assert.Equal(At("2026-03-30T00:30:00Z"), daily.Next(next, null, Berlin)!.Value.ToUnixTimeSeconds());

        var weekly = RepoSchedule.Parse("weekly:1:07:00");
        // From a Wednesday: the next Monday, 07:00 in Berlin (winter time, UTC+1).
        Assert.Equal(At("2026-01-12T06:00:00Z"), weekly.Next(DateTimeOffset.FromUnixTimeSeconds(At("2026-01-07T09:00:00Z")), null, Berlin)!.Value.ToUnixTimeSeconds());
        // From Monday 07:00 itself: a week on.
        Assert.Equal(At("2026-01-19T06:00:00Z"), weekly.Next(DateTimeOffset.FromUnixTimeSeconds(At("2026-01-12T06:00:00Z")), null, Berlin)!.Value.ToUnixTimeSeconds());
    }

    static Project P(long id, string path) => new(id, path, "main", $"http://x/{path}.git");

    [Fact]
    public void Repositories_on_their_own_schedule_or_the_default_come_due_once_per_time()
    {
        using var ix = new TestIndex();
        var t0 = At("2026-10-07T08:00:00Z");
        Choices.Record(ix.Conn, [P(1, "g/hourly"), P(2, "g/nightly"), P(3, "g/pass"), P(4, "g/off"), P(5, "g/left-out"), P(6, "g/default")], t0);
        var hourly = ix.Repo(1, "g/hourly");
        Writes.RecordRunState(ix.Conn, hourly, false, false, t0 - 3600);
        Assert.True(Choices.SetSchedule(ix.Conn, 1, "hours:6", t0));
        Assert.True(Choices.SetSchedule(ix.Conn, 2, "daily:02:00", t0));
        Assert.True(Choices.SetSchedule(ix.Conn, 4, "off", t0));
        Assert.True(Choices.SetSchedule(ix.Conn, 5, "hours:1", t0));
        Choices.Set(ix.Conn, 5, false, null, t0);
        Choices.SetScheduleZone(ix.Conn, "Europe/Berlin");

        // Every 6 hours counts from its last check (an hour before): due at 13:00 UTC.
        Assert.Empty(Choices.Due(ix.Conn, t0 + 4 * 3600));
        Assert.Equal(["g/hourly"], Choices.Due(ix.Conn, t0 + 5 * 3600).Select(c => c.Path));

        // Daily at 02:00 in Berlin (00:00 UTC), counted from when it was set.
        var night = At("2026-10-08T00:00:00Z");
        Assert.DoesNotContain("g/nightly", Choices.Due(ix.Conn, night - 60).Select(c => c.Path));
        Assert.Contains("g/nightly", Choices.Due(ix.Conn, night).Select(c => c.Path));
        // Once started, it counts from then: the next is the night after, not again at once.
        Choices.MarkScheduled(ix.Conn, [2], night);
        Assert.DoesNotContain("g/nightly", Choices.Due(ix.Conn, night + 3600).Select(c => c.Path));
        // Argus was down for two nights: it runs once when it is back, not once per night missed.
        var back = night + 3 * 86400 + 600;
        Assert.Contains("g/nightly", Choices.Due(ix.Conn, back).Select(c => c.Path));
        Choices.MarkScheduled(ix.Conn, [2], back);
        Assert.DoesNotContain("g/nightly", Choices.Due(ix.Conn, back + 60).Select(c => c.Path));

        // The default for all, set to every 2 hours: a repository never checked runs at once.
        Choices.SetDefaultSchedule(ix.Conn, RepoSchedule.Parse("hours:2"), t0);
        var due = Choices.Due(ix.Conn, t0 + 60).Select(c => c.Path).ToList();
        Assert.Contains("g/pass", due);
        Assert.Contains("g/default", due);
        // Off, or left out of the index: never by schedule.
        Assert.DoesNotContain("g/off", Choices.Due(ix.Conn, t0 + 30 * 86400).Select(c => c.Path));
        Assert.DoesNotContain("g/left-out", Choices.Due(ix.Conn, t0 + 30 * 86400).Select(c => c.Path));
    }

    [Fact]
    public void A_repository_on_a_daily_schedule_is_not_stale_an_hour_after_its_run_but_one_off_never_is_by_age()
    {
        using var ix = new TestIndex();
        using var env = new EnvScope(("ARGUS_INDEX_STALE_AFTER", "3600"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Choices.Record(ix.Conn, [P(1, "g/daily"), P(2, "g/pass"), P(3, "g/off")], now);
        foreach (var (id, path) in new[] { (1L, "g/daily"), (2L, "g/pass"), (3L, "g/off") })
            Writes.RecordRunState(ix.Conn, ix.Repo(id, path), false, false, now - 5 * 3600);
        Choices.SetSchedule(ix.Conn, 1, "daily:03:00", now);
        Choices.SetSchedule(ix.Conn, 3, "off", now);

        var snap = Metrics.Take(ix.DbPath);
        Assert.False(snap.Repos.Single(r => r.Repo == "g/daily").Stale);
        Assert.True(snap.Repos.Single(r => r.Repo == "g/pass").Stale);
        Assert.False(snap.Repos.Single(r => r.Repo == "g/off").Stale);
        Assert.Equal(1, snap.StaleRepos);
    }

    [Fact]
    public void A_scheduled_pass_leaves_repositories_on_their_own_schedule_to_it_and_the_scheduler_never_asks_twice()
    {
        using var ix = new TestIndex();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Choices.Record(ix.Conn, [P(1, "g/a"), P(2, "g/b"), P(3, "g/c")], now);
        Choices.SetSchedule(ix.Conn, 1, "hours:1", now);
        Choices.SetSchedule(ix.Conn, 2, "hours:1", now);

        var jobs = new Jobs(ix.Config());
        var started = new List<IReadOnlyList<string>>();
        using var release = new ManualResetEventSlim();
        jobs.Runner = (argv, _) =>
        {
            lock (started) started.Add(argv);
            release.Wait(TimeSpan.FromSeconds(20));
            return 0;
        };
        // Never checked: both are due at once, and start as one run of the two.
        Assert.Equal(["g/a", "g/b"], jobs.RunDue(now).Order());
        SpinWait.SpinUntil(() => { lock (started) return started.Count == 1; }, TimeSpan.FromSeconds(10));
        var argv = started[0];
        Assert.Equal(["g/a", "g/b"], argv.Select((a, i) => (a, i)).Where(x => x.i > 0 && argv[x.i - 1] == "--repo").Select(x => x.a).Order());
        Assert.Contains("repo-schedule", argv);
        Assert.DoesNotContain("--scheduled", argv);
        // While they run: due again (an hour on, nothing checked them), but neither is asked for twice.
        Choices.MarkScheduled(ix.Conn, [1, 2], now - 7200);
        Assert.Empty(jobs.RunDue(now + 60));
        // The pass the app starts on its schedule: only the repositories that go with the passes.
        Assert.False(jobs.StartIndex([], false, "schedule"));
        release.Set();
        SpinWait.SpinUntil(() => jobs.IndexJobSnapshot()["state"]?.ToString() == "idle", TimeSpan.FromSeconds(10));
        Assert.True(jobs.StartIndex([], false, "schedule"));
        SpinWait.SpinUntil(() => { lock (started) return started.Count == 2; }, TimeSpan.FromSeconds(10));
        Assert.Contains("--scheduled", started[1]);
        Assert.DoesNotContain("--repo", started[1]);
    }
}
