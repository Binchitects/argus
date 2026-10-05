using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeArena;

/// <summary>
/// A pseudo-terminal with a program on its other side, as a terminal window
/// runs a shell: what the program writes is read here, what is written here is
/// its keyboard, and it knows its window's size. openpty and posix_spawn on
/// Linux and macOS, ConPTY on Windows, both through the system's own C API (no
/// package).
/// </summary>
internal interface IPty : IDisposable
{
    int Pid { get; }

    /// <summary>Waits for output: how many bytes came, 0 once the program's side is closed or Stop was called.</summary>
    int Read(byte[] buffer);

    /// <summary>Keys typed, or pasted text.</summary>
    void Write(ReadOnlySpan<byte> data);

    void Resize(int cols, int rows);

    /// <summary>Waits for the program to end: its exit code (128 + the signal when a signal ended it).</summary>
    int WaitForExit();

    /// <summary>Ends the program, and what it runs in its terminal.</summary>
    void Kill();

    /// <summary>Makes Read return 0: the rest of the output is not wanted.</summary>
    void Stop();
}

/// <summary>Starts a program in a new pseudo-terminal on this system.</summary>
internal static class Pty
{
    public const int MaxCols = 800;
    public const int MaxRows = 200;

    /// <summary>
    /// The program in a new terminal of this size, in this folder, with this
    /// environment. <paramref name="helper"/>: the command that starts it through
    /// code-arena's own --pty-helper (macOS always does; null: as this system needs).
    /// </summary>
    public static IPty Start(string program, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string> env, int cols, int rows, IReadOnlyList<string>? helper = null)
    {
        cols = Math.Clamp(cols, 2, MaxCols);
        rows = Math.Clamp(rows, 2, MaxRows);
        return OperatingSystem.IsWindows()
            ? WindowsPty.Start(program, args, cwd, env, cols, rows)
            : UnixPty.Start(program, args, cwd, env, cols, rows, helper ?? (OperatingSystem.IsMacOS() ? PtyHelper.Command() : null));
    }
}

/// <summary>
/// openpty makes the terminal; posix_spawn starts the program on its other
/// side, in a session of its own, with the terminal as its keyboard and screen
/// and as its controlling terminal (so job control and Ctrl+C work): opened by
/// name once the new session exists, which makes it the session's terminal. Nothing of
/// this program runs in the child between fork and exec (posix_spawn does it
/// all in the C library), which matters: the runtime's code pages are shared
/// with a forked child, so a child running managed code can break its parent.
/// macOS opens the file before it makes the session, so there the program
/// starts through code-arena --pty-helper, which opens the terminal and then
/// becomes the program.
/// </summary>
internal sealed unsafe class UnixPty : IPty
{
    private const int EINTR = 4;
    private const short POLLIN = 1;
    private const int O_RDWR = 2;
    private const int SIGHUP = 1, SIGKILL = 9;

    private readonly int _master;
    private readonly object _gate = new();
    private int _stopped;
    private bool _disposed;
    private int? _exit;

    private UnixPty(int pid, int master)
    {
        Pid = pid;
        _master = master;
    }

    public int Pid { get; }

    public static UnixPty Start(string program, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string> env, int cols, int rows, IReadOnlyList<string>? helper)
    {
        var c = Calls.Find();
        using var native = new NativeMemory();
        var size = (ushort*)native.Alloc(4 * sizeof(ushort));
        size[0] = (ushort)rows;
        size[1] = (ushort)cols;
        int master, slave;
        if (((delegate* unmanaged[Cdecl]<int*, int*, byte*, void*, ushort*, int>)c.OpenPty)(&master, &slave, null, null, size) != 0)
        {
            throw new IOException($"Could not make a terminal: {Marshal.GetPInvokeErrorMessage(Marshal.GetLastSystemError())}");
        }
        // Neither end for the programs started later, nor for this one (it opens the terminal by name).
        CloseOnExec(master);
        CloseOnExec(slave);
        try
        {
            var name = (byte*)native.Alloc(512);
            var error = ttyname_r(slave, name, 512);
            if (error != 0)
            {
                throw new IOException($"Could not make a terminal: {Marshal.GetPInvokeErrorMessage(error)}");
            }
            var tty = Marshal.PtrToStringUTF8((nint)name)!;
            string file;
            List<string> argv;
            if (helper is { Count: > 0 })
            {
                (file, argv) = (helper[0], [.. helper, "--pty-helper", tty, cwd, program, .. args]);
            }
            else if (c.AddChdir == 0)
            {
                // A C library without posix_spawn_file_actions_addchdir_np (glibc before 2.29): sh changes the folder.
                (file, argv) = ("/bin/sh", ["/bin/sh", "-c", "cd -- \"$0\" && exec \"$@\"", cwd, program, .. args]);
            }
            else
            {
                (file, argv) = (program, [program, .. args]);
            }
            var actions = (void*)native.Alloc(1024);
            var attributes = (void*)native.Alloc(1024);
            Check(posix_spawn_file_actions_init(actions), program);
            Check(posix_spawnattr_init(attributes), program);
            try
            {
                if (helper is { Count: > 0 })
                {
                    // The helper opens the terminal itself, in its new session.
                    for (var fd = 0; fd <= 2; fd++)
                    {
                        Check(posix_spawn_file_actions_adddup2(actions, slave, fd), program);
                    }
                }
                else
                {
                    // In the new session (glibc makes it first): the terminal it opens becomes its controlling terminal.
                    Check(posix_spawn_file_actions_addopen(actions, 0, native.String(tty), O_RDWR, 0), program);
                    Check(posix_spawn_file_actions_adddup2(actions, 0, 1), program);
                    Check(posix_spawn_file_actions_adddup2(actions, 0, 2), program);
                    if (c.AddChdir != 0)
                    {
                        Check(((delegate* unmanaged[Cdecl]<void*, byte*, int>)c.AddChdir)(actions, native.String(cwd)), program);
                    }
                }
                // Every signal at its default and none blocked: the runtime ignores SIGPIPE, which a shell must not inherit.
                var all = native.Alloc(256);
                var none = native.Alloc(256);
                sigfillset(all);
                sigemptyset(none);
                Check(posix_spawnattr_setsigdefault(attributes, all), program);
                Check(posix_spawnattr_setsigmask(attributes, none), program);
                // SETSID | SETSIGDEF | SETSIGMASK, and on macOS CLOEXEC_DEFAULT: no other descriptor of this program leaks to the shell.
                var flags = OperatingSystem.IsMacOS() ? 0x0400 | 0x0004 | 0x0008 | 0x4000 : 0x80 | 0x04 | 0x08;
                Check(posix_spawnattr_setflags(attributes, (short)flags), program);
                int pid;
                var started = posix_spawn(&pid, native.String(file), actions, attributes, (byte**)native.Strings(argv), (byte**)native.Strings([.. env.Select(e => $"{e.Key}={e.Value}")]));
                if (started != 0)
                {
                    throw new IOException($"Could not start {program}: {Marshal.GetPInvokeErrorMessage(started)}");
                }
                return new UnixPty(pid, master);
            }
            finally
            {
                posix_spawn_file_actions_destroy(actions);
                posix_spawnattr_destroy(attributes);
            }
        }
        catch
        {
            close(master);
            throw;
        }
        finally
        {
            close(slave);
        }
    }

    private static void Check(int error, string program)
    {
        if (error != 0)
        {
            throw new IOException($"Could not start {program}: {Marshal.GetPInvokeErrorMessage(error)}");
        }
    }

    // ioctl(fd, FIOCLEX) has no third argument, which keeps it clear of how each system passes variadic ones.
    private static void CloseOnExec(int fd) => ioctl(fd, OperatingSystem.IsMacOS() ? 0x20006601u : 0x5451u);

    public int Read(byte[] buffer)
    {
        fixed (byte* b = buffer)
        {
            while (Volatile.Read(ref _stopped) == 0)
            {
                var p = new PollFd { Fd = _master, Events = POLLIN };
                var ready = poll(&p, 1, 200);
                if (ready == 0 || ready < 0 && Marshal.GetLastPInvokeError() == EINTR)
                {
                    continue;
                }
                if (ready < 0)
                {
                    return 0;
                }
                var n = read(_master, b, buffer.Length);
                if (n > 0)
                {
                    return (int)n;
                }
                if (n < 0 && Marshal.GetLastPInvokeError() == EINTR)
                {
                    continue;
                }
                // EIO (Linux) or 0 (macOS): every program on the other side is gone.
                return 0;
            }
            return 0;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            fixed (byte* p = data)
            {
                var done = 0;
                while (done < data.Length)
                {
                    var n = write(_master, p + done, data.Length - done);
                    if (n < 0)
                    {
                        if (Marshal.GetLastPInvokeError() == EINTR)
                        {
                            continue;
                        }
                        return;
                    }
                    done += (int)n;
                }
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            var size = stackalloc ushort[4];
            size[0] = (ushort)Math.Clamp(rows, 2, Pty.MaxRows);
            size[1] = (ushort)Math.Clamp(cols, 2, Pty.MaxCols);
            size[2] = size[3] = 0;
            // TIOCSWINSZ; the kernel tells the program with SIGWINCH.
            if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            {
                // Apple's ARM64 passes variadic arguments on the stack: past the eight registers, as here.
                ioctl(_master, 0x80087467u, 0, 0, 0, 0, 0, 0, size);
            }
            else
            {
                ioctl(_master, OperatingSystem.IsMacOS() ? 0x80087467u : 0x5414u, size);
            }
        }
    }

    public int WaitForExit()
    {
        int status;
        while (true)
        {
            var r = waitpid(Pid, &status, 0);
            if (r == Pid)
            {
                break;
            }
            if (r < 0 && Marshal.GetLastPInvokeError() == EINTR)
            {
                continue;
            }
            // Reaped by someone else (a parent that ignores SIGCHLD makes the runtime reap every child).
            return _exit ??= -1;
        }
        var signal = status & 0x7f;
        return (_exit = signal == 0 ? (status >> 8) & 0xff : 128 + signal).Value;
    }

    public void Kill()
    {
        // The shell leads its own session and process group (setsid): the whole group hears it.
        kill(-Pid, SIGHUP);
        kill(Pid, SIGHUP);
        var pid = Pid;
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
        {
            if (_exit is null)
            {
                kill(-pid, SIGKILL);
                kill(pid, SIGKILL);
            }
        }, TaskScheduler.Default);
    }

    public void Stop() => Volatile.Write(ref _stopped, 1);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            Volatile.Write(ref _stopped, 1);
            close(_master);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    /// <summary>The C functions that are not where DllImport looks on every system: openpty (libutil in older glibc) and the optional chdir action.</summary>
    private sealed class Calls
    {
        private static readonly Lazy<Calls> Found = new(() => new Calls());

        public nint OpenPty { get; }
        /// <summary>posix_spawn_file_actions_addchdir_np; 0 where the C library has none.</summary>
        public nint AddChdir { get; }

        private Calls()
        {
            string[] names = OperatingSystem.IsMacOS() ? ["/usr/lib/libSystem.B.dylib", "libc"] : ["libc", "libc.so.6", "libutil.so.1"];
            var libraries = names.Select(n => NativeLibrary.TryLoad(n, out var h) ? h : 0).Where(h => h != 0).ToList();
            nint Of(string name) => libraries.Select(l => NativeLibrary.TryGetExport(l, name, out var f) ? f : 0).FirstOrDefault(f => f != 0);
            OpenPty = Of("openpty") is not 0 and var openpty ? openpty : throw new PlatformNotSupportedException("This system's C library has no openpty: no terminal here.");
            AddChdir = Of("posix_spawn_file_actions_addchdir_np");
        }

        public static Calls Find() => Found.Value;
    }

    /// <summary>Native memory for the child's arguments: UTF-8 strings and NULL-ended arrays of them, freed together.</summary>
    private sealed class NativeMemory : IDisposable
    {
        private readonly List<nint> _blocks = [];

        public nint Alloc(int bytes)
        {
            var p = Marshal.AllocHGlobal(bytes);
            _blocks.Add(p);
            new Span<byte>((void*)p, bytes).Clear();
            return p;
        }

        public byte* String(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            var p = Alloc(bytes.Length + 1);
            bytes.CopyTo(new Span<byte>((void*)p, bytes.Length));
            return (byte*)p;
        }

        public nint Strings(IReadOnlyList<string> items)
        {
            var p = (nint*)Alloc((items.Count + 1) * sizeof(nint));
            for (var i = 0; i < items.Count; i++)
            {
                p[i] = (nint)String(items[i]);
            }
            return (nint)p;
        }

        public void Dispose()
        {
            foreach (var p in _blocks)
            {
                Marshal.FreeHGlobal(p);
            }
            _blocks.Clear();
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte* buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte* buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(PollFd* fds, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc", SetLastError = true)]
    private static extern int waitpid(int pid, int* status, int options);

    [DllImport("libc")]
    private static extern int ttyname_r(int fd, byte* buffer, nuint size);

    [DllImport("libc")]
    private static extern int sigfillset(nint set);

    [DllImport("libc")]
    private static extern int sigemptyset(nint set);

    [DllImport("libc")]
    private static extern int posix_spawn(int* pid, byte* path, void* actions, void* attributes, byte** argv, byte** envp);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_init(void* actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_destroy(void* actions);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_addopen(void* actions, int fd, byte* path, int flags, uint mode);

    [DllImport("libc")]
    private static extern int posix_spawn_file_actions_adddup2(void* actions, int fd, int to);

    [DllImport("libc")]
    private static extern int posix_spawnattr_init(void* attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_destroy(void* attributes);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setflags(void* attributes, short flags);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigdefault(void* attributes, nint set);

    [DllImport("libc")]
    private static extern int posix_spawnattr_setsigmask(void* attributes, nint set);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, void* arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, nuint request, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, void* arg);
}

/// <summary>
/// code-arena --pty-helper TTY DIR PROGRAM [ARGS]: the first moments of a
/// terminal's program where the system cannot do them in posix_spawn (macOS).
/// It starts as a session leader with the terminal on its standard handles;
/// opening the terminal by name makes it the session's controlling terminal;
/// then it moves to the folder and becomes the program.
/// </summary>
internal static unsafe class PtyHelper
{
    public const string Flag = "--pty-helper";

    /// <summary>How this program runs itself: its own file, or dotnet and its assembly.</summary>
    public static IReadOnlyList<string> Command()
    {
        var self = Environment.ProcessPath ?? throw new PlatformNotSupportedException("Code Arena cannot find its own program to start a terminal with.");
        return Path.GetFileNameWithoutExtension(self) == "dotnet" ? [self, typeof(PtyHelper).Assembly.Location] : [self];
    }

    public static int Run(string[] args)
    {
        if (args.Length < 3 || OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("code-arena --pty-helper TTY DIR PROGRAM [ARGS]: only for the IDE's terminals.");
            return 2;
        }
        var (tty, dir, program) = (args[0], args[1], args[2]);
        fixed (byte* path = Encoding.UTF8.GetBytes(tty + "\0"))
        {
            var fd = open(path, 2); // O_RDWR, not O_NOCTTY
            if (fd >= 0)
            {
                close(fd);
            }
        }
        try
        {
            Directory.SetCurrentDirectory(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"code-arena: cannot open {dir}: {e.Message}");
        }
        // As a new process expects them: what a shell may find ignored back at its default (the runtime ignores SIGPIPE), none blocked.
        int[] signals = OperatingSystem.IsMacOS() ? [1, 2, 3, 13, 15, 18, 21, 22] : [1, 2, 3, 13, 15, 20, 21, 22];
        foreach (var signal in signals)
        {
            Signal(signal, 0); // SIG_DFL
        }
        var none = stackalloc byte[256];
        sigprocmask(OperatingSystem.IsMacOS() ? 3 : 2, none, null);
        var argv = args[2..];
        var envp = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(e => $"{e.Key}={e.Value}").ToList();
        execve(Bytes(program), Pointers(argv), Pointers(envp));
        Console.Error.WriteLine($"code-arena: cannot start {program}: {Marshal.GetLastPInvokeErrorMessage()}");
        return 127;
    }

    private static byte* Bytes(string s) => (byte*)Marshal.StringToCoTaskMemUTF8(s);

    private static byte** Pointers(IReadOnlyList<string> items)
    {
        var p = (byte**)Marshal.AllocHGlobal((items.Count + 1) * sizeof(nint));
        for (var i = 0; i < items.Count; i++)
        {
            p[i] = Bytes(items[i]);
        }
        p[items.Count] = null;
        return p;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(byte* path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", EntryPoint = "signal")]
    private static extern nint Signal(int signal, nint handler);

    [DllImport("libc")]
    private static extern int sigprocmask(int how, byte* set, byte* old);

    [DllImport("libc", SetLastError = true)]
    private static extern int execve(byte* path, byte** argv, byte** envp);
}

/// <summary>
/// ConPTY: Windows' pseudo console (CreatePseudoConsole, Windows 10 1809 and
/// later). The program starts attached to it; its output comes through one pipe
/// as text with VT sequences, its keyboard goes through another.
/// </summary>
internal sealed unsafe class WindowsPty : IPty
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const nint PseudoConsoleAttribute = 0x00020016; // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE

    private readonly object _gate = new();
    private readonly nint _console;
    private readonly nint _process;
    private readonly FileStream _output;
    private readonly FileStream _input;
    private bool _closed;
    private bool _disposed;

    private WindowsPty(int pid, nint console, nint process, FileStream output, FileStream input)
    {
        Pid = pid;
        _console = console;
        _process = process;
        _output = output;
        _input = input;
    }

    public int Pid { get; }

    public static WindowsPty Start(string program, IReadOnlyList<string> args, string cwd, IReadOnlyDictionary<string, string> env, int cols, int rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, 0, 0))
        {
            throw new Win32Exception();
        }
        if (!CreatePipe(out var outputRead, out var outputWrite, 0, 0))
        {
            var error = new Win32Exception();
            inputRead.Dispose();
            inputWrite.Dispose();
            throw error;
        }
        var hr = CreatePseudoConsole(new Coord((short)cols, (short)rows), inputRead, outputWrite, 0, out var console);
        // The console holds its own copies of its ends.
        inputRead.Dispose();
        outputWrite.Dispose();
        if (hr != 0)
        {
            inputWrite.Dispose();
            outputRead.Dispose();
            throw new IOException($"Could not make a pseudo console (0x{hr:x8}): Windows 10 1809 or later is needed.");
        }
        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var attributes = Marshal.AllocHGlobal(size);
        var initialized = false;
        try
        {
            initialized = InitializeProcThreadAttributeList(attributes, 1, 0, ref size);
            if (!initialized || !UpdateProcThreadAttribute(attributes, 0, PseudoConsoleAttribute, console, sizeof(nint), 0, 0))
            {
                throw new Win32Exception();
            }
            var info = new StartupInfoEx { Attributes = attributes };
            info.StartupInfo.Size = sizeof(StartupInfoEx);
            // No handles of ours: the program's are the console's, even when this program's own are redirected.
            info.StartupInfo.Flags = STARTF_USESTDHANDLES;
            var line = new StringBuilder(Quote(program));
            foreach (var a in args)
            {
                line.Append(' ').Append(Quote(a));
            }
            var block = new StringBuilder();
            foreach (var (name, value) in env.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                block.Append(name).Append('=').Append(value).Append('\0');
            }
            block.Append('\0');
            ProcessInformation pi;
            fixed (char* environment = block.ToString())
            {
                if (!CreateProcessW(null, line, 0, 0, false, EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, environment, cwd, ref info, out pi))
                {
                    throw new IOException($"Could not start {program}: {new Win32Exception().Message}");
                }
            }
            CloseHandle(pi.Thread);
            return new WindowsPty(pi.ProcessId, console, pi.Process, new FileStream(outputRead, FileAccess.Read, 0), new FileStream(inputWrite, FileAccess.Write, 0));
        }
        catch
        {
            ClosePseudoConsole(console);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            if (initialized)
            {
                DeleteProcThreadAttributeList(attributes);
            }
            Marshal.FreeHGlobal(attributes);
        }
    }

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    public int Read(byte[] buffer)
    {
        try
        {
            return _output.Read(buffer, 0, buffer.Length);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            return 0;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                _input.Write(data);
                _input.Flush();
            }
            catch (IOException)
            {
                // The console is gone.
            }
        }
    }

    public void Resize(int cols, int rows)
    {
        lock (_gate)
        {
            if (!_closed)
            {
                ResizePseudoConsole(_console, new Coord((short)Math.Clamp(cols, 2, Pty.MaxCols), (short)Math.Clamp(rows, 2, Pty.MaxRows)));
            }
        }
    }

    public int WaitForExit()
    {
        WaitForSingleObject(_process, uint.MaxValue);
        return GetExitCodeProcess(_process, out var code) ? (int)code : -1;
    }

    public void Kill() => TerminateProcess(_process, 1);

    /// <summary>Closing the console ends its output pipe, so Read returns 0 (and the programs still attached are told to close).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
        }
        // It may wait for the output to be read: not under the lock, and never on the reader's thread.
        ClosePseudoConsole(_console);
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _input.Dispose();
            _output.Dispose();
            CloseHandle(_process);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(nint console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, char* environment, string currentDirectory, ref StartupInfoEx info, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint code);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
