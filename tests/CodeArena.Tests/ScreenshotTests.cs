using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>The screenshot tool: a page through a headless browser (here a stand-in), the picture to a model that sees.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ScreenshotTests : IDisposable
{
    private readonly FakeGateway _gateway = new() { Vision = true };

    /// <summary>A browser that writes a tiny PNG where --screenshot= says, and notes how it was called.</summary>
    private static string FakeBrowser(Harness h)
    {
        var script = Path.Combine(h.Root, "fake-chrome");
        File.WriteAllText(script, """
            #!/bin/sh
            for a in "$@"; do echo "$a" >> "$(dirname "$0")/browser-args"; case "$a" in --screenshot=*) printf '\211PNG\r\n\032\n' > "${a#--screenshot=}";; esac; done
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    [Fact]
    public async Task A_page_is_taken_and_its_picture_goes_to_the_model()
    {
        using var h = new Harness(_gateway);
        h.Env["CODE_ARENA_BROWSER"] = FakeBrowser(h);
        _gateway.Answer = req => FakeGateway.HasToolResults(req) ? Reply.Say("It looks fine.")
            : Reply.Call(("screenshot", """{"url":"http://localhost:5173/","width":800,"height":600}"""));
        Assert.Equal(0, await h.Run("", "-p", "how does the page look?", "--mode", "yolo"));
        var args = File.ReadAllText(Path.Combine(h.Root, "browser-args"));
        Assert.Contains("--window-size=800,600", args, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5173/", args, StringComparison.Ordinal);
        var last = _gateway.Requests.Last();
        var picture = FakeGateway.History(last).OfType<JsonObject>().Last(m => m["role"]?.ToString() == "user");
        var parts = picture["content"]!.AsArray();
        Assert.Equal("(The picture the tool took.)", parts[0]!["text"]!.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,", parts[1]!["image_url"]!["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("It looks fine.", h.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_cannot_see_is_told_so_and_a_bad_address_is_refused()
    {
        _gateway.Vision = false;
        using var h = new Harness(_gateway);
        h.Env["CODE_ARENA_BROWSER"] = FakeBrowser(h);
        var asked = 0;
        _gateway.Answer = req => ++asked switch
        {
            1 => Reply.Call(("screenshot", """{"url":"not an address"}""")),
            2 => Reply.Call(("screenshot", """{"url":"http://localhost:3000/"}""")),
            _ => Reply.Say("ok"),
        };
        Assert.Equal(0, await h.Run("", "-p", "look", "--mode", "yolo"));
        var requests = _gateway.Requests.ToList();
        Assert.Contains("Give url as an address", FakeGateway.Last(requests[1]) + string.Join("", FakeGateway.History(requests[1]).Select(m => m!["content"]?.ToString())), StringComparison.Ordinal);
        Assert.Contains("this model cannot see pictures", FakeGateway.Last(requests[2]), StringComparison.Ordinal);
    }

    public void Dispose() => _gateway.Dispose();
}
