using System.Runtime.InteropServices;
using System.Text;
using CodeArena;

// The start of an IDE terminal's shell where posix_spawn cannot do it all (macOS): first,
// before anything touches the console, the config or the signals.
if (args is [PtyHelper.Flag, ..])
{
    return PtyHelper.Run(args[1..]);
}

// The real console: UTF-8, colours on Windows terminals too, the key read
// without echo, and Ctrl+C stopping the turn rather than the program.
var colours = ConsoleSetup.Prepare();
var env = new CliEnv
{
    In = Console.In,
    Out = Console.Out,
    Err = Console.Error,
    Env = Environment.GetEnvironmentVariable,
    Cwd = Environment.CurrentDirectory,
    Paths = AppPaths.From(Environment.GetEnvironmentVariable),
    InTerminal = !Console.IsInputRedirected,
    OutTerminal = !Console.IsOutputRedirected && colours,
    ErrTerminal = !Console.IsErrorRedirected && colours,
    ReadSecret = Console.IsInputRedirected ? null : ConsoleSetup.ReadSecret,
    // The prompt's line editor draws with escape codes: a terminal both ways, one that takes them.
    Keys = Console.IsInputRedirected || Console.IsOutputRedirected || !colours ? null : new ConsoleSetup.Keyboard(),
};
Console.CancelKeyPress += (_, e) => e.Cancel = env.Cancel.Press();
return await Cli.RunAsync(args, env);

/// <summary>Console set-up that differs by system.</summary>
internal static class ConsoleSetup
{
    /// <summary>UTF-8 in and out, and ANSI escapes switched on in a Windows console; false when they cannot be.</summary>
    public static bool Prepare()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (!Console.IsInputRedirected)
        {
            Console.InputEncoding = new UTF8Encoding(false);
        }
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }
        var ok = true;
        foreach (var which in new[] { -11, -12 }) // STD_OUTPUT_HANDLE, STD_ERROR_HANDLE
        {
            var handle = GetStdHandle(which);
            ok &= GetConsoleMode(handle, out var mode) && SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
        }
        return ok;
    }

    /// <summary>Reads a line showing a • for each character typed.</summary>
    public static string? ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return sb.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Error.Write("\b \b");
                }
            }
            else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
                Console.Error.Write('•');
            }
        }
    }

    /// <summary>The real terminal, key by key, for the prompt's line editor.</summary>
    internal sealed class Keyboard : IKeyboard
    {
        public ConsoleKeyInfo? ReadKey()
        {
            try
            {
                return Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
                return null; // input ended, or is no longer a terminal
            }
        }

        public bool KeyAvailable
        {
            get
            {
                try
                {
                    return Console.KeyAvailable;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
        }

        public int Width
        {
            get
            {
                try
                {
                    return Console.WindowWidth is > 0 and var w ? w : 80;
                }
                catch (IOException)
                {
                    return 80;
                }
            }
        }

        public int Height
        {
            get
            {
                try
                {
                    return Console.WindowHeight is > 0 and var h ? h : 24;
                }
                catch (IOException)
                {
                    return 24;
                }
            }
        }

        public IDisposable CaptureCtrlC()
        {
            var before = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;
            return new Restore(() => Console.TreatControlCAsInput = before);
        }

        private sealed class Restore(Action undo) : IDisposable
        {
            public void Dispose() => undo();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
