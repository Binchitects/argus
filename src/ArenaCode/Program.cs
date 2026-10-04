using System.Runtime.InteropServices;
using System.Text;
using ArenaCode;

// The real console: UTF-8, colours on Windows terminals too, the key read
// without echo, and Ctrl+C stopping the turn rather than the program.
var colours = Terminal.Prepare();
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
    ReadSecret = Console.IsInputRedirected ? null : Terminal.ReadSecret,
};
Console.CancelKeyPress += (_, e) => e.Cancel = env.Cancel.Press();
return await Cli.RunAsync(args, env);

/// <summary>Console set-up that differs by system.</summary>
internal static class Terminal
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

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
