using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The person's own processes, for an attach configuration's ${command:pickProcess}: their id, name and command line
/// (on Linux, from /proc, the person's alone; elsewhere what .NET can say, without the command line). Never
/// Code Arena itself. What `ps` shows them; the page has the person's shell already.
/// </summary>
internal static class OwnProcesses
{
    /// <summary>The most listed.</summary>
    public const int Max = 2000;

    public static JsonArray List()
    {
        var me = Environment.ProcessId;
        var found = new List<(int Pid, string Name, string? Command)>();
        if (OperatingSystem.IsLinux())
        {
            var uid = UidOf("/proc/self/status");
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == me)
                {
                    continue;
                }
                try
                {
                    if (uid is null || UidOf(Path.Combine(dir, "status")) != uid)
                    {
                        continue;
                    }
                    var name = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                    var command = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim();
                    found.Add((pid, name, command.Length > 300 ? command[..300] + "…" : command));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Gone meanwhile, or not readable: not one to attach to.
                }
            }
        }
        else
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.Id != me)
                        {
                            found.Add((p.Id, p.ProcessName, null));
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Ended meanwhile.
                    }
                }
            }
        }
        return new JsonArray([.. found.OrderByDescending(p => p.Pid).Take(Max).Select(p => (JsonNode)new JsonObject { ["pid"] = p.Pid, ["name"] = p.Name, ["command"] = p.Command })]);
    }

    private static string? UidOf(string status)
    {
        foreach (var line in File.ReadLines(status))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                return line[4..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            }
        }
        return null;
    }
}
