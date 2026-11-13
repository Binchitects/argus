namespace CodeArena.Tests;

/// <summary>Code Arena's own schedules: when each runs next, the command that keeps them, and a run as a program of its own.</summary>
public sealed class ScheduleTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("schedules-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AppPaths Paths => new(Path.Combine(_root, "config"), Path.Combine(_root, "data"));

    private (Schedules.Place Place, StringWriter Out) Place()
    {
        var output = new StringWriter();
        return (new Schedules.Place(Paths, _root, Path.Combine(_root, "fake-code-arena"), output, output), output);
    }

    [Fact]
    public void A_schedule_runs_next_every_so_often_from_its_last_run_or_at_its_time_of_day()
    {
        var added = new DateTimeOffset(2026, 11, 13, 10, 30, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 13, 10, 30, 0)));
        var hourly = new LocalSchedule { Name = "h", Prompt = "p", Folder = _root, Every = TimeSpan.FromHours(2), Added = added };
        Assert.Equal(added.AddHours(2), hourly.NextAt());
        Assert.Equal(added.AddHours(5), (hourly with { LastAt = added.AddHours(3) }).NextAt());
        // A day's at its time: after 10:30, 09:00 is tomorrow's; after a run, the next day's.
        var daily = new LocalSchedule { Name = "d", Prompt = "p", Folder = _root, Every = TimeSpan.FromDays(1), At = new TimeOnly(9, 0), Added = added };
        Assert.Equal((new DateTime(2026, 11, 14, 9, 0, 0), true), (daily.NextAt().LocalDateTime, daily.Due(added.AddDays(1).AddHours(-2)) is false));
        Assert.True(daily.Due(daily.NextAt()));
        Assert.Equal(new DateTime(2026, 11, 15, 9, 0, 0), (daily with { LastAt = daily.NextAt() }).NextAt().LocalDateTime);
        // Every 3 days at 9: three days after its last run.
        var third = daily with { Every = TimeSpan.FromDays(3), LastAt = new DateTimeOffset(new DateTime(2026, 11, 14, 9, 0, 0)) };
        Assert.Equal(new DateTime(2026, 11, 17, 9, 0, 0), third.NextAt().LocalDateTime);
        // One running is not run again, unless it was marked so long ago that its run died.
        var running = hourly with { LastAt = added, LastStatus = "running" };
        Assert.False(running.Due(added.AddHours(3)));
        Assert.True(running.Due(added + LocalSchedule.Stuck + TimeSpan.FromMinutes(1)));
        Assert.False((hourly with { Enabled = false }).Due(added.AddDays(1)));
    }

    [Fact]
    public void A_command_line_is_split_into_words_with_quoted_ones_whole()
    {
        Assert.Equal(["add", "nightly", "--every", "1d", "Run the tests", "now"], Schedules.Words("""add nightly --every 1d "Run the tests" now"""));
        Assert.Empty(Schedules.Words("   "));
    }

    [Fact]
    public async Task Schedules_are_added_listed_turned_off_and_removed_and_a_wrong_one_says_why()
    {
        var (place, output) = Place();
        Assert.Equal(0, await Schedules.CommandAsync(["add", "nightly", "--every", "1d", "--at", "02:00", "--mode", "auto-edit", "Run", "the", "tests"], place, CancellationToken.None));
        Assert.Contains("nightly: every 1d at 02:00, in " + _root + ", mode auto-edit", output.ToString(), StringComparison.Ordinal);
        var s = Assert.Single(Schedules.Load(Paths));
        Assert.Equal(("Run the tests", "auto-edit", new TimeOnly(2, 0)), (s.Prompt, s.Mode, s.At));
        Assert.Equal(["-C", _root, "--mode", "auto-edit", "-p", "Run the tests"], Schedules.Arguments(s));

        Assert.Equal(1, await Schedules.CommandAsync(["add", "nightly", "--every", "1h", "again"], place, CancellationToken.None));
        Assert.Contains("There is a schedule named nightly already.", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, await Schedules.CommandAsync(["add", "x", "--every", "soon", "y"], place, CancellationToken.None));
        Assert.Equal(1, await Schedules.CommandAsync(["add", "x", "--every", "1h", "-C", Path.Combine(_root, "nowhere"), "y"], place, CancellationToken.None));

        Assert.Equal(0, await Schedules.CommandAsync(["off", "nightly"], place, CancellationToken.None));
        Assert.False(Assert.Single(Schedules.Load(Paths)).Enabled);
        Assert.Equal(0, await Schedules.CommandAsync(["remove", "nightly"], place, CancellationToken.None));
        Assert.Empty(Schedules.Load(Paths));
    }

    [Fact]
    public async Task A_run_is_a_program_of_its_own_in_its_folder_its_output_kept_and_how_it_ended_said()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        // code-arena as the run sees it: a script that says what it was given, and fails on "fail".
        var exe = Path.Combine(_root, "fake-code-arena");
        File.WriteAllText(exe, "#!/bin/sh\necho \"in $(pwd): $*\"\ncase \"$*\" in *fail*) exit 3;; esac\n");
        File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var folder = Directory.CreateDirectory(Path.Combine(_root, "shop")).FullName;
        var ok = new LocalSchedule { Name = "ok", Prompt = "say hi", Folder = folder, Every = TimeSpan.FromHours(1), Added = DateTimeOffset.Now.AddHours(-2) };
        var bad = ok with { Name = "bad", Prompt = "fail now" };
        Schedules.Save(Paths, [ok, bad]);

        Assert.Equal(2, await Schedules.RunDueAsync(Paths, exe, CancellationToken.None));
        var after = Schedules.Load(Paths).ToDictionary(s => s.Name);
        Assert.Equal(("ok", "failed (3)"), (after["ok"].LastStatus, after["bad"].LastStatus));
        Assert.Contains($"in {folder}: -C {folder} --mode plan -p say hi", File.ReadAllText(after["ok"].LastLog!), StringComparison.Ordinal);
        // Just run: none is due again.
        Assert.Equal(0, await Schedules.RunDueAsync(Paths, exe, CancellationToken.None));

        // Held by another (the IDE, an OS timer): not run twice at once.
        using (new FileStream(Path.Combine(Schedules.LogDir(Paths), "ok.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(-2, await Schedules.RunAsync(Paths, after["ok"], exe, CancellationToken.None));
        }
    }
}

/// <summary>Checklists in Code Arena: a repository's own, and the person's written in one line.</summary>
public sealed class RepoChecklistTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("checklists-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_repositorys_checklists_are_its_markdown_files_each_named_by_its_heading_its_items_its_list_lines()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, ".arena", "checklists")).FullName;
        File.WriteAllText(Path.Combine(dir, "release.md"), "# Before a release\n\nWhat to check:\n- [ ] The changelog says what changed\n* The version is bumped\n- [x] Tests pass\n");
        File.WriteAllText(Path.Combine(dir, "review.md"), "- Read the diff\n");
        File.WriteAllText(Path.Combine(dir, "empty.md"), "# Nothing here\n");
        var found = Runtime.ReadRepoChecklists(_root);
        Assert.Equal(["Before a release", "review"], found.Select(c => c.Name));
        Assert.Equal(["The changelog says what changed", "The version is bumped", "Tests pass"], found[0].Items);
        Assert.Equal(Path.Combine(".arena", "checklists", "release.md"), found[0].File);
        Assert.Empty(Runtime.ReadRepoChecklists(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void A_checklist_written_in_one_line_is_its_name_then_its_items()
    {
        var (name, items) = Runtime.SplitList("Done means done: tests pass; docs say what changed;");
        Assert.Equal("Done means done", name);
        Assert.Equal(["tests pass", "docs say what changed"], items);
        Assert.Equal("", Runtime.SplitList("no colon here").Name);
    }
}
