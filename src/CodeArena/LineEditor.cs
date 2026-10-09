using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A terminal read key by key: what the prompt's line editor needs of it. Tests script one.</summary>
internal interface IKeyboard
{
    /// <summary>The next key; null when input has ended.</summary>
    ConsoleKeyInfo? ReadKey();

    /// <summary>A key is waiting already: typed keys come one at a time, a paste all at once.</summary>
    bool KeyAvailable { get; }

    /// <summary>The terminal's width, in columns.</summary>
    int Width { get; }

    /// <summary>Ctrl+C comes as a key (it clears the line) until disposed; otherwise it stops the turn.</summary>
    IDisposable CaptureCtrlC();
}

/// <summary>
/// The terminal's prompt, edited in place: the caret moves with the arrows, Home
/// and End (and Emacs's Ctrl keys), and ↑ and ↓ step through what was sent before
/// in this folder, newest first, then back to what was being typed (Esc goes
/// straight back to it). A message recalled is a copy: editing it leaves the
/// history as it was. A line ending in \ goes on to the next, as Enter does in a
/// paste; in a message of several lines ↑ and ↓ move between them first.
/// </summary>
internal sealed partial class LineEditor(IKeyboard keys, TextWriter output, Func<IReadOnlyList<string>> history)
{
    /// <summary>How a tab is shown (it is kept as a tab).</summary>
    private const string Tab = "    ";

    private readonly StringBuilder _text = new();
    private int _caret;
    /// <summary>Rows from the prompt's first to the one the terminal's cursor is on.</summary>
    private int _cursorRow;
    private string _prompt = "";
    private string _more = "";
    /// <summary>What ↑ steps through, oldest first, and which is shown: Count for what was being typed.</summary>
    private List<string> _entries = [];
    private int _at;
    private string _draft = "";
    private bool _afterReturn;

    private enum Outcome { None, Render, Send, End, Interrupt }

    /// <summary>Ctrl+C on an empty prompt: true to read on (a hint was given), false to leave.</summary>
    public Func<bool>? Interrupted { get; init; }

    /// <summary>A message, or null at the end of input (Ctrl+D on an empty prompt, or a Ctrl+C that leaves).</summary>
    public string? Read(string prompt, string more)
    {
        _prompt = prompt;
        _more = more;
        _text.Clear();
        _caret = 0;
        _cursorRow = 0;
        // The same text twice is stepped through once, where it was sent last.
        _entries = [.. history().Reverse().Distinct().Reverse()];
        _at = _entries.Count;
        _draft = "";
        using var capture = keys.CaptureCtrlC();
        Render();
        while (true)
        {
            if (keys.ReadKey() is not { } key)
            {
                Finish();
                return null;
            }
            switch (Handle(key))
            {
                case Outcome.Send:
                    Finish();
                    return _text.ToString();
                case Outcome.End:
                    Finish();
                    return null;
                case Outcome.Interrupt:
                    Finish();
                    if (Interrupted?.Invoke() != true)
                    {
                        return null;
                    }
                    _cursorRow = 0;
                    Render();
                    break;
                // Half a character (the rest comes next), or a paste: drawn once it is all in.
                case Outcome.Render when !keys.KeyAvailable && !(_caret > 0 && char.IsHighSurrogate(_text[_caret - 1])):
                    Render();
                    break;
            }
        }
    }

    private Outcome Handle(ConsoleKeyInfo key)
    {
        var afterReturn = _afterReturn;
        _afterReturn = false;
        var alt = (key.Modifiers & ConsoleModifiers.Alt) != 0;
        var ctrl = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var c = key.KeyChar;
        if (key.Key == ConsoleKey.Enter || c is '\r' or '\n')
        {
            // A paste from Windows ends its lines with \r\n: one new line.
            if (c == '\n' && afterReturn)
            {
                return Outcome.None;
            }
            _afterReturn = c == '\r';
            // Alt+Enter, and Enter with more already waiting (a paste): a new line, not the message sent.
            if (alt || (key.Modifiers & ConsoleModifiers.Shift) != 0 || keys.KeyAvailable)
            {
                return Insert("\n");
            }
            if (_caret > 0 && _text[_caret - 1] == '\\')
            {
                _text.Remove(--_caret, 1);
                return Insert("\n");
            }
            return Outcome.Send;
        }
        if (alt && !ctrl && key.Key is not (ConsoleKey.Backspace or ConsoleKey.LeftArrow or ConsoleKey.RightArrow))
        {
            // Alt+B and Alt+F move by a word, Alt+D deletes the next one; other Alt keys type nothing.
            return char.ToLowerInvariant(c) switch
            {
                'b' => Move(WordStart(_caret)),
                'f' => Move(WordEnd(_caret)),
                'd' => DeleteAfter(WordEnd(_caret)),
                _ => Outcome.None,
            };
        }
        switch (key.Key)
        {
            case ConsoleKey.Backspace:
                return DeleteBefore(alt || ctrl ? WordStart(_caret) : Prev(_caret));
            case ConsoleKey.Delete:
                return DeleteAfter(Next(_caret));
            case ConsoleKey.LeftArrow:
                return Move(alt || ctrl ? WordStart(_caret) : Prev(_caret));
            case ConsoleKey.RightArrow:
                return Move(alt || ctrl ? WordEnd(_caret) : Next(_caret));
            case ConsoleKey.Home:
                return Move(LineStart(_caret));
            case ConsoleKey.End:
                return Move(LineEnd(_caret));
            case ConsoleKey.UpArrow:
                return Up();
            case ConsoleKey.DownArrow:
                return Down();
            case ConsoleKey.Escape:
                return _at < _entries.Count ? Show(_entries.Count) : Outcome.None;
            case ConsoleKey.Tab:
                return Insert("\t");
        }
        if (c == '\x7f')
        {
            return DeleteBefore(Prev(_caret));
        }
        if (c == '\t')
        {
            return Insert("\t");
        }
        // Ctrl+letter: a control character on a Unix terminal, the letter with Ctrl on Windows.
        var letter = c is >= '\x01' and <= '\x1a' ? (char)('a' + c - 1) : ctrl && key.Key is >= ConsoleKey.A and <= ConsoleKey.Z ? (char)('a' + (key.Key - ConsoleKey.A)) : '\0';
        switch (letter)
        {
            case 'a':
                return Move(LineStart(_caret));
            case 'e':
                return Move(LineEnd(_caret));
            case 'b':
                return Move(Prev(_caret));
            case 'f':
                return Move(Next(_caret));
            case 'p':
                return Up();
            case 'n':
                return Down();
            case 'h':
                return DeleteBefore(Prev(_caret));
            case 'd':
                return _text.Length == 0 ? Outcome.End : DeleteAfter(Next(_caret));
            case 'u':
                return DeleteBefore(LineStart(_caret));
            case 'k':
                return DeleteAfter(LineEnd(_caret));
            case 'w':
                return DeleteBefore(WordStart(_caret));
            case 'l':
                output.Write("\e[H\e[2J");
                _cursorRow = 0;
                return Outcome.Render;
            case 'c':
                if (_text.Length == 0)
                {
                    return Outcome.Interrupt;
                }
                // As a shell does: the line is dropped, and ↑ starts again from the newest.
                _text.Clear();
                _caret = 0;
                _at = _entries.Count;
                return Outcome.Render;
            case not '\0':
                return Outcome.None;
        }
        return c != '\0' && !char.IsControl(c) ? Insert(c.ToString()) : Outcome.None;
    }

    /// <summary>↑: the line above, or (on the first line, or on a message recalled as it was) the one sent before.</summary>
    private Outcome Up()
    {
        var start = LineStart(_caret);
        if (start > 0 && !Unchanged())
        {
            var above = LineStart(start - 1);
            return Move(above + Math.Min(_caret - start, start - 1 - above));
        }
        if (_at == 0)
        {
            return Outcome.None;
        }
        if (_at == _entries.Count)
        {
            _draft = _text.ToString();
        }
        return Show(_at - 1);
    }

    /// <summary>↓: the line below, or (on the last line, while stepping through) the one sent after, then what was being typed.</summary>
    private Outcome Down()
    {
        var end = LineEnd(_caret);
        if (end < _text.Length)
        {
            var below = end + 1;
            return Move(below + Math.Min(_caret - LineStart(_caret), LineEnd(below) - below));
        }
        return _at < _entries.Count ? Show(_at + 1) : Outcome.None;
    }

    /// <summary>A message recalled, untouched, with the caret where it was put: ↑ goes on to the one before at once.</summary>
    private bool Unchanged() => _at < _entries.Count && _caret == _text.Length && _text.ToString() == _entries[_at];

    private Outcome Show(int at)
    {
        _at = at;
        _text.Clear().Append(at == _entries.Count ? _draft : _entries[at]);
        _caret = _text.Length;
        return Outcome.Render;
    }

    private Outcome Insert(string s)
    {
        _text.Insert(_caret, s);
        _caret += s.Length;
        return Outcome.Render;
    }

    private Outcome Move(int to)
    {
        _caret = Math.Clamp(to, 0, _text.Length);
        return Outcome.Render;
    }

    private Outcome DeleteBefore(int from)
    {
        if (from >= _caret)
        {
            return Outcome.None;
        }
        _text.Remove(from, _caret - from);
        _caret = from;
        return Outcome.Render;
    }

    private Outcome DeleteAfter(int to)
    {
        if (to <= _caret)
        {
            return Outcome.None;
        }
        _text.Remove(_caret, to - _caret);
        return Outcome.Render;
    }

    private int LineStart(int i)
    {
        while (i > 0 && _text[i - 1] != '\n')
        {
            i--;
        }
        return i;
    }

    private int LineEnd(int i)
    {
        while (i < _text.Length && _text[i] != '\n')
        {
            i++;
        }
        return i;
    }

    /// <summary>One character back, both halves of a pair at once.</summary>
    private int Prev(int i) => i >= 2 && char.IsLowSurrogate(_text[i - 1]) && char.IsHighSurrogate(_text[i - 2]) ? i - 2 : Math.Max(0, i - 1);

    private int Next(int i) => i + 1 < _text.Length && char.IsHighSurrogate(_text[i]) && char.IsLowSurrogate(_text[i + 1]) ? i + 2 : Math.Min(_text.Length, i + 1);

    private int WordStart(int i)
    {
        while (i > 0 && !char.IsLetterOrDigit(_text[i - 1]))
        {
            i--;
        }
        while (i > 0 && char.IsLetterOrDigit(_text[i - 1]))
        {
            i--;
        }
        return i;
    }

    private int WordEnd(int i)
    {
        while (i < _text.Length && !char.IsLetterOrDigit(_text[i]))
        {
            i++;
        }
        while (i < _text.Length && char.IsLetterOrDigit(_text[i]))
        {
            i++;
        }
        return i;
    }

    /// <summary>
    /// Draws the prompt and the text again from the prompt's first row, the
    /// lines after the first behind the continuation mark, each wrapped at the
    /// terminal's width, then puts the cursor at the caret.
    /// </summary>
    private void Render()
    {
        var width = Math.Max(10, keys.Width);
        var sb = new StringBuilder();
        if (_cursorRow > 0)
        {
            sb.Append($"\e[{_cursorRow}A");
        }
        sb.Append("\r\e[J");
        var lines = _text.ToString().Split('\n');
        int row = 0, offset = 0, caretRow = 0, caretCol = 0, endRow = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var lead = i == 0 ? _prompt : _more;
            var leadCells = Cells(Ansi().Replace(lead, ""));
            var line = lines[i];
            if (i > 0)
            {
                sb.Append("\r\n");
            }
            sb.Append(lead).Append(line.Replace("\t", Tab));
            var cells = leadCells + Cells(line);
            if (_caret >= offset && _caret <= offset + line.Length)
            {
                var at = leadCells + Cells(line[..(_caret - offset)]);
                (caretRow, caretCol) = (row + at / width, at % width);
            }
            if (i == lines.Length - 1)
            {
                endRow = row + cells / width;
                // A line that fills its last row leaves the cursor waiting at the edge: it goes on to the next row.
                if (cells > 0 && cells % width == 0)
                {
                    sb.Append("\r\n");
                }
            }
            row += Math.Max(1, (cells + width - 1) / width);
            offset += line.Length + 1;
        }
        if (endRow > caretRow)
        {
            sb.Append($"\e[{endRow - caretRow}A");
        }
        sb.Append('\r');
        if (caretCol > 0)
        {
            sb.Append($"\e[{caretCol}C");
        }
        _cursorRow = caretRow;
        output.Write(sb.ToString());
        output.Flush();
    }

    /// <summary>The whole text drawn, the cursor under it: what follows starts on a new line.</summary>
    private void Finish()
    {
        _caret = _text.Length;
        Render();
        output.Write("\r\n");
        output.Flush();
    }

    /// <summary>The columns a text takes: two for a wide (East Asian, emoji) character, none for a combining mark.</summary>
    internal static int Cells(string text)
    {
        var n = 0;
        foreach (var r in text.EnumerateRunes())
        {
            n += r.Value == '\t' ? Tab.Length : Cells(r);
        }
        return n;
    }

    private static int Cells(Rune r)
    {
        if (Rune.IsControl(r) || Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }
        return r.Value is (>= 0x1100 and <= 0x115F) or (>= 0x2E80 and <= 0x303E) or (>= 0x3041 and <= 0x33FF) or (>= 0x3400 and <= 0x4DBF)
            or (>= 0x4E00 and <= 0x9FFF) or (>= 0xA000 and <= 0xA4CF) or (>= 0xAC00 and <= 0xD7A3) or (>= 0xF900 and <= 0xFAFF)
            or (>= 0xFE30 and <= 0xFE4F) or (>= 0xFF00 and <= 0xFF60) or (>= 0xFFE0 and <= 0xFFE6) or (>= 0x1F300 and <= 0x1F64F)
            or (>= 0x1F900 and <= 0x1F9FF) or (>= 0x20000 and <= 0x3FFFD) ? 2 : 1;
    }

    [GeneratedRegex(@"\e\[[0-9;?]*[A-Za-z]")]
    private static partial Regex Ansi();
}
