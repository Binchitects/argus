using System.Text;

namespace CodeArena;

/// <summary>
/// The IDE's log: what code-arena says while the IDE runs, each line with its time, in code-arena.log beside the program
/// (in the data folder's logs/ when that folder cannot be written). Started on its own (a double-click on Windows), the IDE
/// has no console: this file is where its lines go. It rotates at <see cref="Max"/> bytes, keeping <see cref="Kept"/> older
/// files (code-arena.1.log is the newest of them). The IDE's key never reaches it: <see cref="Hide"/> blanks it.
/// </summary>
internal sealed partial class LogFile : TextWriter
{
    public const string Name = "code-arena.log";
    public const long Max = 5 * 1024 * 1024;
    public const int Kept = 3;

    private readonly Lock _lock = new();
    private readonly StringBuilder _line = new();
    private readonly List<string> _secrets = [];
    private readonly long _max;
    private FileStream? _file;

    private LogFile(string path, FileStream file, long max)
    {
        Path = path;
        _file = file;
        _max = max;
    }

    public string Path { get; }

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>Where the log is: beside the program when its folder can be written, else the data folder's logs/.</summary>
    public static string Where(string? exe, string dataDir) =>
        Candidates(exe, dataDir).FirstOrDefault(Writable) ?? System.IO.Path.Combine(dataDir, "logs", Name);

    /// <summary>The log opened for appending (rotated first if it is full); null when neither place can be written.</summary>
    public static LogFile? Open(string? exe, string dataDir, long max = Max)
    {
        foreach (var path in Candidates(exe, dataDir))
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                if (new FileInfo(path) is { Exists: true, Length: var length } && length >= max)
                {
                    Rotate(path);
                }
                return new LogFile(path, Append(path), max);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not here: the next place.
            }
        }
        return null;
    }

    /// <summary>A secret (the IDE's key) blanked in every line from now on.</summary>
    public void Hide(string secret)
    {
        if (secret.Length > 0)
        {
            lock (_lock)
            {
                _secrets.Add(secret);
            }
        }
    }

    public override void Write(char value)
    {
        lock (_lock)
        {
            if (value == '\n')
            {
                End();
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }
        lock (_lock)
        {
            foreach (var c in value)
            {
                if (c == '\n')
                {
                    End();
                }
                else if (c != '\r')
                {
                    _line.Append(c);
                }
            }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            Write(value);
            End();
        }
    }

    public override void Flush()
    {
        lock (_lock)
        {
            _file?.Flush();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_lock)
            {
                if (_line.Length > 0)
                {
                    End();
                }
                _file?.Dispose();
                _file = null;
            }
        }
        base.Dispose(disposing);
    }

    private void End()
    {
        // The terminal's colours are not the file's.
        var text = Colours().Replace(_line.ToString(), "");
        _line.Clear();
        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, "…", StringComparison.Ordinal);
        }
        if (_file is null)
        {
            return;
        }
        try
        {
            var bytes = Encoding.UTF8.GetBytes($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {text}\n");
            _file.Write(bytes);
            _file.Flush();
            if (_file.Length >= _max)
            {
                _file.Dispose();
                Rotate(Path);
                _file = Append(Path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A full disk, or the file taken away: the IDE runs on without its log.
            _file?.Dispose();
            _file = null;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]")]
    internal static partial System.Text.RegularExpressions.Regex Colours();

    private static IEnumerable<string> Candidates(string? exe, string dataDir)
    {
        if (exe is { Length: > 0 } && System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(exe)) is { Length: > 0 } beside)
        {
            yield return System.IO.Path.Combine(beside, Name);
        }
        yield return System.IO.Path.Combine(dataDir, "logs", Name);
    }

    /// <summary>Whether the log can be written there; one made to find out is taken away again.</summary>
    private static bool Writable(string path)
    {
        var existed = File.Exists(path);
        try
        {
            using (Append(path))
            {
            }
            if (!existed)
            {
                File.Delete(path);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Shared with another code-arena writing beside the same program; the person's alone where modes exist.</summary>
    private static FileStream Append(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(path, options);
    }

    /// <summary>code-arena.log to code-arena.1.log, .1 to .2 and so on; the oldest beyond <see cref="Kept"/> goes.</summary>
    private static void Rotate(string path)
    {
        string Old(int n) => System.IO.Path.ChangeExtension(path, $"{n}.log");
        try
        {
            File.Delete(Old(Kept));
            for (var n = Kept - 1; n >= 1; n--)
            {
                if (File.Exists(Old(n)))
                {
                    File.Move(Old(n), Old(n + 1));
                }
            }
            File.Move(path, Old(1));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Another code-arena rotates it at the same time, or holds it: this one appends to what is there.
        }
    }
}

/// <summary>
/// One writer to two: the console (which a Windows IDE started on its own can leave, <see cref="Screen"/> then null) and the
/// log. It keeps the last line that started with an error mark, for a message box when there was no console to see it.
/// </summary>
internal sealed class Tee(TextWriter screen, TextWriter log) : TextWriter
{
    private readonly Lock _lock = new();
    private readonly StringBuilder _line = new();

    public TextWriter Screen { get; set; } = screen;

    public string? LastError { get; private set; }

    public override Encoding Encoding => Screen.Encoding;

    public override void Write(char value)
    {
        lock (_lock)
        {
            Screen.Write(value);
            log.Write(value);
            Note(value.ToString());
        }
    }

    public override void Write(string? value)
    {
        lock (_lock)
        {
            Screen.Write(value);
            log.Write(value);
            Note(value);
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock)
        {
            Screen.WriteLine(value);
            log.WriteLine(value);
            Note(value + "\n");
        }
    }

    public override void Flush()
    {
        lock (_lock)
        {
            Screen.Flush();
            log.Flush();
        }
    }

    private void Note(string? text)
    {
        foreach (var c in text ?? "")
        {
            if (c != '\n')
            {
                _line.Append(c);
                continue;
            }
            // Ui.Error's mark, its colour codes taken off.
            var plain = LogFile.Colours().Replace(_line.ToString(), "").Trim();
            if (plain.StartsWith('✗'))
            {
                LastError = plain.TrimStart('✗').Trim();
            }
            _line.Clear();
        }
    }
}
