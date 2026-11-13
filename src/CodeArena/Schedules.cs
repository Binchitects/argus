using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// A prompt Code Arena runs on its own on a schedule, in a folder, as a session of its own (code-arena -p, synced to Arena
/// as any session is): every so often, or every day (or days) at a time. Kept in the config folder's schedules.json; run by
/// the IDE while it is open, and by "code-arena schedule due", which an OS timer calls ("code-arena schedule install").
/// </summary>
internal sealed record LocalSchedule
{
    public required string Name { get; init; }
    public required string Prompt { get; init; }
    public required string Folder { get; init; }
    /// <summary>How often: 30m, 2h, 1d, 7d (at least a minute).</summary>
    public required TimeSpan Every { get; init; }
    /// <summary>For a day or more: the time of day it runs at, local time; null: from when it was added.</summary>
    public TimeOnly? At { get; init; }
    /// <summary>The run's mode: plan (reads only), ask (each change refused: nobody is there to allow it), auto-edit or yolo.</summary>
    public string Mode { get; init; } = "plan";
    public string? Model { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTimeOffset Added { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? LastAt { get; init; }
    /// <summary>How the last run ended: ok, failed (its exit code), or running.</summary>
    public string? LastStatus { get; init; }
    /// <summary>The last run's output, a file beside the schedules.</summary>
    public string? LastLog { get; init; }

    public static readonly string[] Modes = ["plan", "ask", "auto-edit", "yolo"];

    /// <summary>When it runs next: from its last run (or when it was added), every so often, or the next day at its time.</summary>
    public DateTimeOffset NextAt()
    {
        var from = LastAt ?? Added;
        if (At is { } at && Every >= TimeSpan.FromDays(1))
        {
            var day = DateOnly.FromDateTime(from.LocalDateTime);
            var next = Local(day, at);
            if (next <= from)
            {
                next = Local(day.AddDays(1), at);
            }
            // Every few days: the days between counted from the last run.
            return LastAt is not null && Every.TotalDays > 1 ? next.AddDays((int)Every.TotalDays - 1) : next;
        }
        return from + Every;
    }

    private static DateTimeOffset Local(DateOnly day, TimeOnly at)
    {
        var when = day.ToDateTime(at);
        return new DateTimeOffset(when, TimeZoneInfo.Local.GetUtcOffset(when));
    }

    /// <summary>A run marked running this long ago ended without saying so (its process was killed): it may run again.</summary>
    public static readonly TimeSpan Stuck = TimeSpan.FromHours(6);

    public bool Due(DateTimeOffset now) => Enabled && (LastStatus != "running" || LastAt < now - Stuck) && NextAt() <= now;

    public JsonObject ToJson() => new()
    {
        ["name"] = Name, ["prompt"] = Prompt, ["folder"] = Folder, ["every"] = Runtime.EveryText(Every),
        ["at"] = At?.ToString("HH:mm", CultureInfo.InvariantCulture), ["mode"] = Mode, ["model"] = Model, ["enabled"] = Enabled,
        ["added"] = Added.ToString("o"), ["lastAt"] = LastAt?.ToString("o"), ["lastStatus"] = LastStatus, ["lastLog"] = LastLog,
    };

    public static LocalSchedule? From(JsonObject o) =>
        o.Str("name") is { Length: > 0 } name && o.Str("prompt") is { Length: > 0 } prompt && o.Str("folder") is { Length: > 0 } folder
            && Runtime.ParseEvery(o.Str("every") ?? "") is { } every
            ? new LocalSchedule
            {
                Name = name, Prompt = prompt, Folder = folder, Every = every,
                At = TimeOnly.TryParseExact(o.Str("at") ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null,
                Mode = o.Str("mode") is { } m && Modes.Contains(m) ? m : "plan", Model = o.Str("model"), Enabled = o.Bool("enabled") != false,
                Added = DateTimeOffset.TryParse(o.Str("added"), CultureInfo.InvariantCulture, out var added) ? added : DateTimeOffset.Now,
                LastAt = DateTimeOffset.TryParse(o.Str("lastAt"), CultureInfo.InvariantCulture, out var last) ? last : null,
                LastStatus = o.Str("lastStatus"), LastLog = o.Str("lastLog"),
            }
            : null;
}

/// <summary>The schedules file, and running a schedule: its own code-arena process, its output in a log beside it.</summary>
internal static class Schedules
{
    public const int Most = 50;
    private static readonly Lock Gate = new();

    public static string File(AppPaths paths) => Path.Combine(paths.ConfigDir, "schedules.json");

    public static string LogDir(AppPaths paths) => Path.Combine(paths.DataDir, "schedules");

    public static List<LocalSchedule> Load(AppPaths paths)
    {
        lock (Gate)
        {
            var text = System.IO.File.Exists(File(paths)) ? System.IO.File.ReadAllText(File(paths)) : "";
            return [.. (Json.ParseObject(text)?["schedules"] as JsonArray ?? []).OfType<JsonObject>().Select(LocalSchedule.From).OfType<LocalSchedule>()];
        }
    }

    public static void Save(AppPaths paths, IEnumerable<LocalSchedule> schedules)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(paths.ConfigDir);
            var tmp = File(paths) + ".tmp";
            System.IO.File.WriteAllText(tmp, new JsonObject { ["schedules"] = new JsonArray([.. schedules.Select(s => (JsonNode)s.ToJson())]) }.ToJsonString(Json.Indented));
            System.IO.File.Move(tmp, File(paths), overwrite: true);
        }
    }

    /// <summary>One schedule changed in the file (by its name), whatever else changed it meanwhile.</summary>
    public static void Update(AppPaths paths, string name, Func<LocalSchedule, LocalSchedule> change)
    {
        lock (Gate)
        {
            Save(paths, Load(paths).Select(s => s.Name == name ? change(s) : s).ToList());
        }
    }

    /// <summary>A new schedule, or why not.</summary>
    public static string? Add(AppPaths paths, LocalSchedule s)
    {
        var all = Load(paths);
        if (all.Any(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"There is a schedule named {s.Name} already.";
        }
        if (all.Count >= Most)
        {
            return $"At most {Most} schedules: remove one first.";
        }
        if (s.Every < Runtime.LeastEvery)
        {
            return "A schedule runs at most every minute.";
        }
        if (!Directory.Exists(s.Folder))
        {
            return $"There is no folder {s.Folder}.";
        }
        Save(paths, [.. all, s]);
        return null;
    }

    /// <summary>
    /// Runs it now, in its folder, as its own session (code-arena -C FOLDER -p PROMPT --mode MODE): its output kept in a log,
    /// the schedule marked running and then how it ended. The exit code.
    /// </summary>
    public static async Task<int> RunAsync(AppPaths paths, LocalSchedule s, string exe, CancellationToken ct)
    {
        Directory.CreateDirectory(LogDir(paths));
        // One run at a time of each, whoever starts it (the IDE, an OS timer, the command): the others leave it.
        FileStream held;
        try
        {
            held = new FileStream(Path.Combine(LogDir(paths), Safe(s.Name) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            return -2;
        }
        await using var _ = held;
        var log = Path.Combine(LogDir(paths), $"{Safe(s.Name)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
        var started = DateTimeOffset.Now;
        Update(paths, s.Name, x => x with { LastAt = started, LastStatus = "running", LastLog = log });
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = s.Folder };
        foreach (var a in Arguments(s))
        {
            start.ArgumentList.Add(a);
        }
        var code = -1;
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("code-arena did not start.");
            await using var file = new StreamWriter(log);
            await file.WriteLineAsync($"# {s.Name}, {started:yyyy-MM-dd HH:mm}, in {s.Folder}, mode {s.Mode}\n# {s.Prompt}\n");
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await file.WriteAsync(await output);
            if ((await errors) is { Length: > 0 } err)
            {
                await file.WriteAsync("\n# errors\n" + err);
            }
            code = process.ExitCode;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            await System.IO.File.AppendAllTextAsync(log, $"\n# It did not run: {e.Message}\n", CancellationToken.None);
        }
        finally
        {
            Update(paths, s.Name, x => x with { LastStatus = code == 0 ? "ok" : $"failed ({code})" });
        }
        return code;
    }

    /// <summary>What the run is started with: its folder, its prompt one-shot, its mode and model.</summary>
    public static List<string> Arguments(LocalSchedule s)
    {
        List<string> args = ["-C", s.Folder, "--mode", s.Mode];
        if (s.Model is { Length: > 0 } model)
        {
            args.AddRange(["-m", model]);
        }
        args.AddRange(["-p", s.Prompt]);
        return args;
    }

    /// <summary>While the IDE is open: the schedules due run, looked at every minute.</summary>
    public static async Task WatchAsync(AppPaths paths, string exe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunDueAsync(paths, exe, ct);
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // A schedules file being written, or not readable now: looked at again in a minute.
                await Task.Delay(TimeSpan.FromMinutes(1), ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    /// <summary>Runs every schedule that is due, one after another; how many ran.</summary>
    public static async Task<int> RunDueAsync(AppPaths paths, string exe, CancellationToken ct)
    {
        var ran = 0;
        foreach (var s in Load(paths).Where(s => s.Due(DateTimeOffset.Now)))
        {
            await RunAsync(paths, s, exe, ct);
            ran++;
        }
        return ran;
    }

    private static string Safe(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    /// <summary>A command line split into words as a shell does: spaces between, "double quotes" around one with spaces.</summary>
    public static List<string> Words(string line)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    any = false;
                }
            }
            else
            {
                word.Append(c);
                any = true;
            }
        }
        if (any)
        {
            words.Add(word.ToString());
        }
        return words;
    }

    /// <summary>"every 2h", "every 1d at 09:00": how it reads.</summary>
    public static string When(LocalSchedule s) => $"every {Runtime.EveryText(s.Every)}{(s.At is { } at && s.Every >= TimeSpan.FromDays(1) ? $" at {at:HH\\:mm}" : "")}";

    /// <summary>The line a schedule is listed with.</summary>
    public static string Line(LocalSchedule s) =>
        $"{s.Name}: {When(s)}, in {s.Folder}, mode {s.Mode}{(s.Enabled ? "" : " (off)")}; next {s.NextAt():yyyy-MM-dd HH:mm}"
        + (s.LastAt is { } last ? $"; last {last:yyyy-MM-dd HH:mm} {s.LastStatus}" : "") + $"\n    {Fmt.OneLine(s.Prompt, 120)}";

    public const string Usage = """
        code-arena schedule list
        code-arena schedule add NAME --every 1d [--at 09:00] [-C DIR] [--mode plan|ask|auto-edit|yolo] [-m MODEL] PROMPT
        code-arena schedule remove NAME | on NAME | off NAME | run NAME
        code-arena schedule due        run those due now, then end (for an OS timer)
        code-arena schedule install    an OS timer that runs "schedule due" every 5 minutes (systemd, launchd or Task Scheduler)
        """;

    /// <summary>code-arena schedule …: the person's schedules from the command line; the exit code.</summary>
    public static Task<int> CommandAsync(string[] args, CliEnv env, CancellationToken ct) =>
        CommandAsync(args, new Place(env.Paths, env.Cwd, env.Exe ?? Environment.ProcessPath ?? "code-arena", env.Out, env.Err), ct);

    /// <summary>Where a schedule command runs: its files, its folder, the program it starts, where it says what it did.</summary>
    public sealed record Place(AppPaths Paths, string Cwd, string Exe, TextWriter Out, TextWriter Err);

    public static async Task<int> CommandAsync(string[] args, Place env, CancellationToken ct)
    {
        var paths = env.Paths;
        var exe = env.Exe;
        switch (args)
        {
            case [] or ["list"]:
            {
                var all = Load(paths);
                env.Out.WriteLine(all.Count == 0 ? "No schedules yet. " + Usage : string.Join("\n", all.Select(Line)));
                return 0;
            }
            case ["add", var name, .. var rest]:
            {
                TimeSpan? every = null;
                TimeOnly? at = null;
                var folder = env.Cwd;
                var mode = "plan";
                string? model = null;
                var words = new List<string>();
                for (var i = 0; i < rest.Length; i++)
                {
                    string Next() => i + 1 < rest.Length ? rest[++i] : throw new ArgumentException($"{rest[i]} needs a value.");
                    try
                    {
                        switch (rest[i])
                        {
                            case "--every":
                                every = Runtime.ParseEvery(Next()) ?? throw new ArgumentException("--every is 30m, 2h, 1d or 7d.");
                                break;
                            case "--at":
                                at = TimeOnly.TryParseExact(Next(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : throw new ArgumentException("--at is a time of day, 09:00.");
                                break;
                            case "-C" or "--folder":
                                folder = Path.GetFullPath(Next(), env.Cwd);
                                break;
                            case "--mode":
                                mode = Next() is var m && LocalSchedule.Modes.Contains(m) ? m : throw new ArgumentException("--mode is plan, ask, auto-edit or yolo.");
                                break;
                            case "-m" or "--model":
                                model = Next();
                                break;
                            default:
                                words.Add(rest[i]);
                                break;
                        }
                    }
                    catch (ArgumentException e)
                    {
                        env.Err.WriteLine($"code-arena schedule: {e.Message}");
                        return 2;
                    }
                }
                if (every is null || words.Count == 0)
                {
                    env.Err.WriteLine("code-arena schedule add: say how often (--every 1d) and what to do.\n" + Usage);
                    return 2;
                }
                var s = new LocalSchedule { Name = name, Prompt = string.Join(' ', words), Folder = folder, Every = every.Value, At = at, Mode = mode, Model = model };
                if (Add(paths, s) is { } wrong)
                {
                    env.Err.WriteLine($"code-arena schedule: {wrong}");
                    return 1;
                }
                env.Out.WriteLine($"Added. {Line(Load(paths).Single(x => x.Name == name))}");
                env.Out.WriteLine("It runs while the IDE is open, and from an OS timer once code-arena schedule install has set one up.");
                return 0;
            }
            case ["remove" or "on" or "off" or "run", var name]:
            {
                if (Load(paths).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } s)
                {
                    env.Err.WriteLine($"code-arena schedule: there is no schedule named {name}.");
                    return 1;
                }
                switch (args[0])
                {
                    case "remove":
                        Save(paths, Load(paths).Where(x => x.Name != s.Name).ToList());
                        env.Out.WriteLine($"Removed {s.Name}.");
                        return 0;
                    case "on" or "off":
                        Update(paths, s.Name, x => x with { Enabled = args[0] == "on" });
                        env.Out.WriteLine($"{s.Name} is {args[0]}.");
                        return 0;
                    default:
                        var code = await RunAsync(paths, s, exe, ct);
                        env.Out.WriteLine($"{s.Name} ran: {(code == 0 ? "ok" : $"failed ({code})")}. Its output: {Load(paths).Single(x => x.Name == s.Name).LastLog}");
                        return code == 0 ? 0 : 1;
                }
            }
            case ["due"]:
                env.Out.WriteLine($"{await RunDueAsync(paths, exe, ct)} ran.");
                return 0;
            case ["install"]:
                return Install(env, exe);
            case ["help"]:
                env.Out.WriteLine(Usage);
                return 0;
            default:
                env.Err.WriteLine(Usage);
                return 2;
        }
    }

    /// <summary>An OS timer for "code-arena schedule due" every 5 minutes: a systemd user timer, a launchd agent, or a Task Scheduler task.</summary>
    private static int Install(Place env, string exe)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var p = Process.Start(new ProcessStartInfo("schtasks", ["/Create", "/F", "/SC", "MINUTE", "/MO", "5", "/TN", "Code Arena schedules", "/TR", $"\"{exe}\" schedule due"])
                    { UseShellExecute = false });
                p?.WaitForExit();
                env.Out.WriteLine(p?.ExitCode == 0 ? "Task Scheduler runs \"code-arena schedule due\" every 5 minutes (task \"Code Arena schedules\")." : "schtasks did not make the task.");
                return p?.ExitCode == 0 ? 0 : 1;
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsMacOS())
            {
                var plist = Path.Combine(home, "Library", "LaunchAgents", "dev.code-arena.schedules.plist");
                Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
                System.IO.File.WriteAllText(plist, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0"><dict>
                      <key>Label</key><string>dev.code-arena.schedules</string>
                      <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(exe)}</string><string>schedule</string><string>due</string></array>
                      <key>StartInterval</key><integer>300</integer>
                    </dict></plist>
                    """);
                env.Out.WriteLine($"Written {plist}. Load it: launchctl load {plist}");
                return 0;
            }
            var units = Path.Combine(home, ".config", "systemd", "user");
            Directory.CreateDirectory(units);
            System.IO.File.WriteAllText(Path.Combine(units, "code-arena-schedules.service"), $"""
                [Unit]
                Description=Code Arena's schedules: those due now

                [Service]
                Type=oneshot
                ExecStart="{exe}" schedule due
                """);
            System.IO.File.WriteAllText(Path.Combine(units, "code-arena-schedules.timer"), """
                [Unit]
                Description=Code Arena's schedules, every 5 minutes

                [Timer]
                OnBootSec=2min
                OnUnitActiveSec=5min

                [Install]
                WantedBy=timers.target
                """);
            env.Out.WriteLine($"Written {units}/code-arena-schedules.timer and .service. Start it: systemctl --user daemon-reload && systemctl --user enable --now code-arena-schedules.timer");
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            env.Err.WriteLine($"code-arena schedule install: {e.Message}");
            return 1;
        }
    }
}
