using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeArena.Tests;

/// <summary>The programs debug adapters and language servers run as: no API key, the end of their stderr kept, stopped with all they started.</summary>
public sealed class StdioChildTests
{
    [Fact]
    public async Task A_child_gets_no_API_key_whatever_its_environment_says_and_its_last_stderr_lines_are_kept()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var env = new Dictionary<string, string?> { ["ARENA_API_KEY"] = "sk-not-for-the-adapter", ["SAID"] = "given", ["HOME"] = null };
        await using var child = StdioChild.Start("/bin/sh", ["-c", "echo \"key=$ARENA_API_KEY said=$SAID home=$HOME\"; i=0; while [ $i -lt 250 ]; do i=$((i+1)); echo line $i >&2; done"], Path.GetTempPath(), env);
        using var output = new StreamReader(child.Reading, Encoding.UTF8);
        Assert.Equal("key= said=given home=", await output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await child.Exited.WaitAsync(TimeSpan.FromSeconds(10)));
        await Until(() => child.Said().LastOrDefault() == "line 250");
        Assert.Equal(StdioChild.KeptLines, child.Said().Length);
        Assert.Equal("line 51", child.Said()[0]);
    }

    [Fact]
    public async Task Killing_a_child_ends_what_it_started_too()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        await using var child = StdioChild.Start("/bin/sh", ["-c", "sleep 600 & echo $!; wait"], Path.GetTempPath());
        using var output = new StreamReader(child.Reading, Encoding.UTF8);
        var sleeper = int.Parse((await output.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Alive(sleeper));
        child.Kill();
        await child.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        await Until(() => !Alive(sleeper));
    }

    [Fact]
    public async Task A_child_that_listens_is_reached_on_its_port_and_one_that_ends_first_says_why()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        // Something listens there (the test, for the program): the child is reached over TCP once it answers.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepting = listener.AcceptTcpClientAsync();
            await using var child = await StdioChild.StartListeningAsync("/bin/sh", ["-c", "sleep 600"], Path.GetTempPath(), null, port, TimeSpan.FromSeconds(10), default);
            using var server = await accepting.WaitAsync(TimeSpan.FromSeconds(10));
            await child.Writing.WriteAsync("ping"u8.ToArray());
            var got = new byte[4];
            await server.GetStream().ReadExactlyAsync(got);
            Assert.Equal("ping", Encoding.ASCII.GetString(got));
            Assert.NotNull(child.Pid);
        }
        finally
        {
            listener.Stop();
        }

        var e = await Assert.ThrowsAsync<IOException>(() => StdioChild.StartListeningAsync("/bin/sh", ["-c", "echo no port for me >&2; exit 3"], Path.GetTempPath(), null, StdioChild.FreePort(), TimeSpan.FromSeconds(10), default));
        Assert.Contains("exit code 3", e.Message);
        Assert.Contains("no port for me", e.Message);

        await Assert.ThrowsAsync<IOException>(() => StdioChild.ConnectAsync(StdioChild.FreePort(), TimeSpan.FromSeconds(5), default));
    }

    private static bool Alive(int pid) => File.Exists($"/proc/{pid}/stat") && !File.ReadAllText($"/proc/{pid}/stat").Split(')')[1].TrimStart().StartsWith('Z');

    private static async Task Until(Func<bool> condition)
    {
        for (var waited = 0; waited < 10_000; waited += 50)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(50);
        }
        Assert.True(condition());
    }
}
