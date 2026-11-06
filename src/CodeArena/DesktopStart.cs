using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodeArena;

/// <summary>
/// How code-arena was started on Windows, and the console it keeps. From a terminal it talks there, as any command does.
/// Started on its own (a double-click, a shortcut, the Start menu), Windows gives it a console window of its own, which
/// the IDE does not need: it starts itself again at once with a console that has no window (CREATE_NO_WINDOW), and the
/// first one ends. The programs the IDE runs (git, the terminals' shells, language servers) share that hidden console, so
/// none of them opens a window either. When the IDE gets no window of its own (it runs in a tab), a console is shown: it
/// is the way to stop it. What the IDE says goes to its log (LogFile) all the while.
/// </summary>
internal static class DesktopStart
{
    /// <summary>Set for the IDE started again without a window; taken out of its environment at once.</summary>
    public const string Detached = "CODE_ARENA_DETACHED";

    public enum Start
    {
        /// <summary>A terminal's console, shared with the shell that started it, or output a caller reads (or not Windows).</summary>
        Terminal,

        /// <summary>A console Windows made for this process alone: nobody typed the command.</summary>
        OwnConsole,

        /// <summary>No console and nowhere to write: started detached.</summary>
        NoConsole,
    }

    /// <summary>How this process was started; Terminal off Windows.</summary>
    public static Start Find()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Start.Terminal;
        }
        var ids = new uint[4];
        return GetConsoleProcessList(ids, (uint)ids.Length) switch
        {
            // No console, and nothing else to write to either: output to a pipe or a file is a caller's, as from a terminal.
            0 => GetStdHandle(-11) is var output && (output == IntPtr.Zero || output == new IntPtr(-1)) ? Start.NoConsole : Start.Terminal,
            1 => Start.OwnConsole,
            _ => Start.Terminal,
        };
    }

    /// <summary>Whether this run starts the IDE again without a window: the IDE, started on its own, not started again already.</summary>
    public static bool Again(Start start, bool ide, bool detached) => ide && !detached && start != Start.Terminal;

    /// <summary>The same program, arguments and folder, with a console that has no window and the mark that it is the one started again.</summary>
    public static ProcessStartInfo Child(string exe, IEnumerable<string> args, string cwd)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        start.Environment[Detached] = "1";
        return start;
    }

    /// <summary>
    /// A console window to stop the IDE with (it runs in a tab): the windowless one let go, a new one made. False when it
    /// cannot be; the writers made before it write nowhere, so the caller makes new ones.
    /// </summary>
    public static bool Show()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }
        _ = FreeConsole();
        return AllocConsole();
    }

    /// <summary>What stopped an IDE that had no console to say it in: a message box, with where the log is.</summary>
    public static void Alert(string text)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = MessageBoxW(IntPtr.Zero, text, "Code Arena", 0x10); // MB_ICONERROR
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList([Out] uint[] ids, uint count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
