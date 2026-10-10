using System.Diagnostics;
using System.Text;

namespace CodeArena;

/// <summary>
/// The sandbox the agent's commands run in, so a command cannot harm the person's system: everything may be read but
/// what holds secrets (keys, tokens, Code Arena's own key and sessions), only the working directory (and the folders
/// allowed besides it, the build tools' caches and a /tmp of the session's own) may be written, and Docker's socket is
/// out of reach. Linux: bubblewrap; macOS: sandbox-exec. Commands the person runs in the IDE's terminals are theirs: no sandbox.
/// </summary>
internal sealed class Sandbox
{
    public enum Kinds { Off, Bubblewrap, Seatbelt }

    public Kinds Kind { get; }
    /// <summary>Why it is off when asked for ("auto" on a system without one), said once.</summary>
    public string? Why { get; }
    public bool Network { get; }

    private readonly IReadOnlyList<string> _writable;
    private readonly IReadOnlyList<string> _hidden;
    private readonly string _tmp;

    private Sandbox(Kinds kind, string? why, bool network, IReadOnlyList<string> writable, IReadOnlyList<string> hidden, string tmp)
    {
        Kind = kind;
        Why = why;
        Network = network;
        _writable = writable;
        _hidden = hidden;
        _tmp = tmp;
    }

    public static readonly Sandbox None = new(Kinds.Off, null, true, [], [], "");

    /// <summary>What holds secrets in a home folder: never readable by a command in the sandbox.</summary>
    internal static readonly string[] Secrets =
    [
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".netrc", ".git-credentials", ".npmrc", ".pypirc", ".pgpass",
        ".password-store", ".vault-token", ".config/gh", ".config/gcloud", ".config/hub", ".local/share/keyrings",
        ".mozilla", ".config/google-chrome", ".config/chromium", ".config/BraveSoftware", "Library/Keychains",
    ];

    /// <summary>The build tools' caches, writable so builds and installs work.</summary>
    internal static readonly string[] Caches =
    [
        ".cache", ".npm", ".yarn", ".pnpm-store", ".nuget", ".dotnet", ".cargo/registry", ".cargo/git", ".rustup", "go/pkg",
        ".m2", ".gradle", ".ivy2", ".sbt", ".local/share/pnpm", "Library/Caches",
    ];

    /// <summary>Sockets that would give a command the whole machine (Docker's and Podman's).</summary>
    internal static readonly string[] Sockets = ["/var/run/docker.sock", "/run/docker.sock", "/run/podman/podman.sock"];

    /// <summary>
    /// The sandbox the config asks for: "auto" (the default) uses one when the system has it, "on" refuses to start
    /// without one, "off" runs commands as they are.
    /// </summary>
    public static Sandbox Choose(Config config, Workspace workspace, AppPaths paths, Func<string, string?> env, string session)
    {
        var mode = (config.Sandbox ?? "auto").Trim().ToLowerInvariant();
        if (mode == "off")
        {
            return None;
        }
        var kind = OperatingSystem.IsLinux() && Which("bwrap", env) is not null && Works()
            ? Kinds.Bubblewrap
            : OperatingSystem.IsMacOS() && File.Exists("/usr/bin/sandbox-exec") ? Kinds.Seatbelt : Kinds.Off;
        if (kind == Kinds.Off)
        {
            var why = OperatingSystem.IsLinux()
                ? (Which("bwrap", env) is null ? "bubblewrap is not installed (apt install bubblewrap, dnf install bubblewrap)" : "bubblewrap cannot make a sandbox here (user namespaces are off?)")
                : OperatingSystem.IsWindows() ? "Windows has no sandbox Code Arena can use" : "this system has no sandbox Code Arena can use";
            if (mode == "on")
            {
                throw new Runtime.StartException($"\"sandbox\": \"on\" in config.json, but {why}.");
            }
            return new Sandbox(Kinds.Off, why, true, [], [], "");
        }
        var home = env("HOME") is { Length: > 0 } h ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var writable = workspace.Roots.Concat(config.SandboxWritable.Select(p => Path.GetFullPath(Workspace.HomePath(p, home))))
            .Concat(Caches.Select(c => Path.Combine(home, c)).Where(Directory.Exists)).Distinct().ToList();
        var hidden = Secrets.Select(s => Path.Combine(home, s))
            .Append(paths.ConfigDir).Append(paths.DataDir)
            .Where(p => Directory.Exists(p) || File.Exists(p))
            // A secret folder that is the working directory itself (or holds it) is the person's choice: it stays.
            .Where(p => !workspace.Roots.Any(r => Within(r, p)))
            .Concat(Sockets.Where(File.Exists))
            .Concat(env("XDG_RUNTIME_DIR") is { Length: > 0 } run ? [Path.Combine(run, "docker.sock"), Path.Combine(run, "podman", "podman.sock")] : [])
            .Where(p => Directory.Exists(p) || File.Exists(p))
            // Mounted where they really are: /var/run is a link to /run on most systems, and a mount cannot go through a link.
            .Select(Real)
            .Distinct().ToList();
        writable = [.. writable.Select(Real).Distinct()];
        var tmp = Path.Combine(paths.DataDir, "sandbox", session);
        PrivateFiles.EnsureDirectory(tmp);
        return new Sandbox(kind, null, config.SandboxNetwork, writable, hidden, tmp);
    }

    /// <summary>A path with every link on the way followed (realpath); the path as it is when it cannot be.</summary>
    internal static string Real(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full)!;
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Path.Combine(current, part);
                for (var hops = 0; hops < 40 && new FileInfo(next).LinkTarget is { } target; hops++)
                {
                    next = Path.GetFullPath(target, current);
                }
                current = next;
            }
            return current;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return path;
        }
    }

    private static bool Within(string path, string folder) =>
        string.Equals(path, folder, StringComparison.Ordinal) || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>Whether bubblewrap can make a sandbox here (it cannot where user namespaces are off).</summary>
    private static bool Works()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("bwrap", ["--ro-bind", "/", "/", "--dev", "/dev", "--unshare-pid", "true"])
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            });
            if (p is null)
            {
                return false;
            }
            return p.WaitForExit(5000) && p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal static string? Which(string name, Func<string, string?> env) =>
        (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    /// <summary>The session's own /tmp goes when Code Arena ends.</summary>
    public void Clean()
    {
        if (_tmp.Length == 0)
        {
            return;
        }
        try
        {
            Directory.Delete(_tmp, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Something a command left running still holds it: the next start of the same name finds it, nothing more.
        }
    }

    /// <summary>What a command in the sandbox may do, in words (/sandbox, the IDE).</summary>
    public string Describe() => Kind switch
    {
        Kinds.Off when Why is not null => $"Off: {Why}. Commands run as you; \"sandbox\": \"off\" in config.json says so for good.",
        Kinds.Off => "Off (\"sandbox\": \"off\" in config.json): commands run as you.",
        _ => $"On ({(Kind == Kinds.Bubblewrap ? "bubblewrap" : "sandbox-exec")}): commands may write only in {string.Join(", ", _writable.Take(3))}{(_writable.Count > 3 ? $" and {_writable.Count - 3} more (build caches)" : "")}" +
             $" and their own /tmp; {_hidden.Count} places with secrets (and Docker's socket) are out of reach; the network is {(Network ? "on" : "off")}.",
    };

    /// <summary>The command as it runs in the sandbox (the same command when there is none).</summary>
    public ProcessStartInfo Wrap(ProcessStartInfo psi)
    {
        if (Kind == Kinds.Off)
        {
            return psi;
        }
        var inner = new List<string> { psi.FileName };
        inner.AddRange(psi.ArgumentList.Count > 0 ? psi.ArgumentList : SplitArguments(psi.Arguments));
        var wrapped = new ProcessStartInfo(Kind == Kinds.Bubblewrap ? "bwrap" : "/usr/bin/sandbox-exec")
        {
            WorkingDirectory = psi.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = psi.RedirectStandardInput,
            RedirectStandardOutput = psi.RedirectStandardOutput,
            RedirectStandardError = psi.RedirectStandardError,
            StandardOutputEncoding = psi.StandardOutputEncoding,
            StandardErrorEncoding = psi.StandardErrorEncoding,
        };
        foreach (var (key, value) in psi.Environment)
        {
            wrapped.Environment[key] = value;
        }
        wrapped.Environment["TMPDIR"] = Kind == Kinds.Bubblewrap ? "/tmp" : _tmp;
        foreach (var arg in Kind == Kinds.Bubblewrap ? BubblewrapArguments(psi.WorkingDirectory) : SeatbeltArguments())
        {
            wrapped.ArgumentList.Add(arg);
        }
        foreach (var arg in inner)
        {
            wrapped.ArgumentList.Add(arg);
        }
        return wrapped;
    }

    internal List<string> BubblewrapArguments(string cwd)
    {
        var a = new List<string> { "--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--bind", _tmp, "/tmp" };
        foreach (var path in _writable.Where(Directory.Exists))
        {
            a.AddRange(["--bind", path, path]);
        }
        foreach (var path in _hidden)
        {
            if (Directory.Exists(path))
            {
                a.AddRange(["--tmpfs", path]);
            }
            else
            {
                a.AddRange(["--ro-bind", "/dev/null", path]);
            }
        }
        a.AddRange(["--unshare-pid", "--unshare-ipc", "--unshare-uts", "--unshare-cgroup-try", "--die-with-parent", "--new-session"]);
        if (!Network)
        {
            a.Add("--unshare-net");
        }
        if (cwd.Length > 0)
        {
            a.AddRange(["--chdir", cwd]);
        }
        a.Add("--");
        return a;
    }

    internal List<string> SeatbeltArguments() => ["-p", SeatbeltProfile(), "--"];

    internal string SeatbeltProfile()
    {
        static string Quote(string p) => "\"" + p.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var sb = new StringBuilder("(version 1)\n(allow default)\n(deny file-write*)\n(allow file-write*\n");
        foreach (var path in _writable.Append(_tmp).Append("/private/tmp").Append("/private/var/folders"))
        {
            sb.Append("  (subpath ").Append(Quote(path)).Append(")\n");
        }
        sb.Append("  (literal \"/dev/null\") (literal \"/dev/tty\") (regex #\"^/dev/fd/\"))\n");
        if (_hidden.Count > 0)
        {
            sb.Append("(deny file-read* file-write*\n");
            foreach (var path in _hidden)
            {
                sb.Append(Directory.Exists(path) ? "  (subpath " : "  (literal ").Append(Quote(path)).Append(")\n");
            }
            sb.Append(")\n");
        }
        if (!Network)
        {
            sb.Append("(deny network*)\n(allow network* (local unix))\n");
        }
        return sb.ToString();
    }

    /// <summary>An argument line split as the runtime splits it (only cmd.exe's commands use one, and Windows has no sandbox).</summary>
    private static IEnumerable<string> SplitArguments(string line) => line.Length == 0 ? [] : line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
