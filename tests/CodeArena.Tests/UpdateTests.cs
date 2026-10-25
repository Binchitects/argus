namespace CodeArena.Tests;

/// <summary>Code Arena's updates from its Arena: a newer build said at the start (once a day), and code-arena update putting it in place.</summary>
public sealed class UpdateTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    [Fact]
    public void Versions_compare_by_their_numbers_and_a_pre_release_comes_before_its_release()
    {
        Assert.True(Updates.Newer("7.0.0", "6.9.0"));
        Assert.True(Updates.Newer("6.10.0", "6.9.3"));
        Assert.False(Updates.Newer("6.9.0", "6.9.0"));
        Assert.False(Updates.Newer("6.8.9", "6.9.0"));
        Assert.True(Updates.Newer("7.0.0", "7.0.0-rc.1"));
        Assert.False(Updates.Newer("7.0.0-rc.1", "7.0.0"));
        Assert.False(Updates.Newer("not a version", "6.9.0"));
    }

    [Fact]
    public async Task Update_downloads_the_newer_build_checks_it_and_puts_it_in_place()
    {
        using var h = new Harness(_gateway, _mcp);
        var exe = Path.Combine(h.Root, "bin", OperatingSystem.IsWindows() ? "code-arena.exe" : "code-arena");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "the old one");
        _mcp.Build = ("99.0.0", "the new one"u8.ToArray(), null);
        Assert.Equal(0, await h.RunUpdate(exe));
        Assert.Contains("→ 99.0.0: the next start runs it.", h.Out, StringComparison.Ordinal);
        Assert.Equal("the new one", File.ReadAllText(exe));
        Assert.False(File.Exists(exe + ".new"));

        // Not what Arena lists: nothing changed.
        _mcp.Build = ("99.0.1", "tampered"u8.ToArray(), new string('0', 64));
        Assert.Equal(1, await h.RunUpdate(exe));
        Assert.Contains("did not match what Arena lists", h.Err, StringComparison.Ordinal);
        Assert.Equal("the new one", File.ReadAllText(exe));

        // Nothing newer: said so.
        _mcp.Build = ("0.0.1", "older"u8.ToArray(), null);
        Assert.Equal(0, await h.RunUpdate(exe));
        Assert.Contains("is the newest this Arena has", h.Out, StringComparison.Ordinal);

        // Run from its source: not replaced.
        Assert.Equal(1, await h.RunUpdate(Path.Combine(h.Root, "dotnet")));
        Assert.Contains("runs from its source", h.Err, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_says_a_newer_build_once_a_day_and_not_for_a_one_shot_answer()
    {
        using var h = new Harness(_gateway, _mcp);
        _mcp.Build = ("99.0.0", "new"u8.ToArray(), null);
        var config = new Config { Url = _mcp.BaseUrl };
        using var http = new HttpClient();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal($"Code Arena 99.0.0 is available from Arena (this is {Cli.Version}): code-arena update installs it.",
            await Updates.NoticeAsync(config, http, h.Paths, now, CancellationToken.None));
        // Within the day: said again without asking Arena (which now has nothing newer).
        _mcp.Build = ("0.0.1", "old"u8.ToArray(), null);
        Assert.NotNull(await Updates.NoticeAsync(config, http, h.Paths, now.AddHours(2), CancellationToken.None));
        Assert.Null(await Updates.NoticeAsync(config, http, h.Paths, now.AddDays(1.1), CancellationToken.None));

        _gateway.Answer = _ => Reply.Say("ok");
        _mcp.Build = ("99.0.0", "new"u8.ToArray(), null);
        File.Delete(Path.Combine(h.Paths.DataDir, "update-check.json"));
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        Assert.DoesNotContain("is available from Arena", h.Out + h.Err, StringComparison.Ordinal);
    }
}
