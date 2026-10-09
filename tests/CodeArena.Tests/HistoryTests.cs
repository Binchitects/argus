using System.Text;

namespace CodeArena.Tests;

/// <summary>
/// Keys as a terminal sends them, typed in order; a paste's are all waiting at
/// once, as are all of them with Pasting.
/// </summary>
internal sealed class ScriptedKeys : IKeyboard
{
    /// <summary>Each key, and the paste it is part of (0: typed).</summary>
    private readonly Queue<(ConsoleKeyInfo Key, int Paste)> _keys = new();
    private int _pastes;
    /// <summary>The paste keys are added to now, and the one the key read last was part of.</summary>
    private int _adding;
    private int _read;

    public int Width { get; set; } = 80;
    public int Height { get; set; } = 1000;
    public bool Pasting { get; set; }
    public bool KeyAvailable => _keys.Count > 0 && (Pasting || (_read != 0 && _keys.Peek().Paste == _read));
    public int Captured { get; private set; }

    public ConsoleKeyInfo? ReadKey()
    {
        if (!_keys.TryDequeue(out var next))
        {
            return null;
        }
        _read = next.Paste;
        return next.Key;
    }

    public IDisposable CaptureCtrlC()
    {
        Captured++;
        return new Release(this);
    }

    /// <summary>Text pasted: once its first key is read, the rest are waiting.</summary>
    public ScriptedKeys Paste(string text)
    {
        _adding = ++_pastes;
        Type(text);
        _adding = 0;
        return this;
    }

    /// <summary>Each character as its key; \n as Enter.</summary>
    public ScriptedKeys Type(string text)
    {
        foreach (var c in text)
        {
            if (c == '\n')
            {
                Enter();
            }
            else
            {
                _keys.Enqueue((new ConsoleKeyInfo(c, char.IsAsciiLetter(c) ? ConsoleKey.A + (char.ToUpperInvariant(c) - 'A') : 0, char.IsAsciiLetterUpper(c), false, false), _adding));
            }
        }
        return this;
    }

    public ScriptedKeys Press(ConsoleKey key, char c = '\0', bool alt = false)
    {
        _keys.Enqueue((new ConsoleKeyInfo(c, key, false, alt, false), _adding));
        return this;
    }

    /// <summary>Ctrl and a letter, as a Unix terminal sends it: the control character.</summary>
    public ScriptedKeys Ctrl(char letter)
    {
        _keys.Enqueue((new ConsoleKeyInfo((char)(letter - 'a' + 1), ConsoleKey.A + (letter - 'a'), false, false, true), _adding));
        return this;
    }

    public ScriptedKeys Enter() => Press(ConsoleKey.Enter, '\r');
    public ScriptedKeys Up() => Press(ConsoleKey.UpArrow);
    public ScriptedKeys Down() => Press(ConsoleKey.DownArrow);
    public ScriptedKeys Left() => Press(ConsoleKey.LeftArrow);
    public ScriptedKeys Home() => Press(ConsoleKey.Home);
    public ScriptedKeys Escape() => Press(ConsoleKey.Escape, '\e');
    public ScriptedKeys Backspace() => Press(ConsoleKey.Backspace, '\x7f');

    private sealed class Release(ScriptedKeys keys) : IDisposable
    {
        public void Dispose() => keys.Captured--;
    }
}

/// <summary>
/// Enough of a VT100 to see what the line editor draws: printing with the wrap
/// a terminal does (the cursor waits at the right edge until the next character),
/// carriage return, line feed (on the bottom row the screen scrolls: its top row
/// goes into the scrollback, out of reach), cursor up (no higher than the
/// screen's top) and right, and erasing below.
/// </summary>
internal sealed class Screen(int width, int height = 1000)
{
    /// <summary>The scrollback, then the screen.</summary>
    private readonly List<char[]> _rows = [];
    /// <summary>Where the screen starts among the rows: how many went into the scrollback.</summary>
    private int _top;
    private bool _pending;

    /// <summary>The cursor's row on the screen.</summary>
    public int Row { get; private set; }
    public int Col { get; private set; }

    /// <summary>The row the cursor is on, without trailing blanks.</summary>
    public string Current => new string(RowAt(_top + Row)).TrimEnd();

    public void Feed(string output)
    {
        for (var i = 0; i < output.Length; i++)
        {
            var c = output[i];
            if (c == '\e' && i + 1 < output.Length && output[i + 1] == '[')
            {
                var end = i + 2;
                while (end < output.Length && !char.IsAsciiLetter(output[end]))
                {
                    end++;
                }
                var arg = output[(i + 2)..end];
                var n = int.TryParse(arg, out var x) ? x : 1;
                switch (output[end])
                {
                    case 'A':
                        Row = Math.Max(0, Row - n);
                        _pending = false;
                        break;
                    case 'C':
                        Col = Math.Min(width - 1, Col + n);
                        _pending = false;
                        break;
                    case 'J':
                        Clear(arg == "2" ? 0 : Row, arg == "2" ? 0 : Col);
                        break;
                    case 'H':
                        Row = Col = 0;
                        _pending = false;
                        break;
                }
                i = end;
                continue;
            }
            switch (c)
            {
                case '\r':
                    Col = 0;
                    _pending = false;
                    break;
                case '\n':
                    LineFeed();
                    _pending = false;
                    break;
                default:
                    if (_pending)
                    {
                        LineFeed();
                        Col = 0;
                        _pending = false;
                    }
                    RowAt(_top + Row)[Col] = c;
                    if (Col == width - 1)
                    {
                        _pending = true;
                    }
                    else
                    {
                        Col++;
                    }
                    break;
            }
        }
    }

    /// <summary>The rows written, the scrollback's and the screen's, without trailing blanks.</summary>
    public List<string> Lines => [.. _rows.Select(r => new string(r).TrimEnd()).Reverse().SkipWhile(l => l.Length == 0).Reverse()];

    private void LineFeed()
    {
        if (Row == height - 1)
        {
            _top++;
        }
        else
        {
            Row++;
        }
    }

    private char[] RowAt(int row)
    {
        while (_rows.Count <= row)
        {
            _rows.Add(Enumerable.Repeat(' ', width).ToArray());
        }
        return _rows[row];
    }

    /// <summary>Blanks the screen from this place on; the scrollback stays.</summary>
    private void Clear(int row, int col)
    {
        if (_top + row < _rows.Count)
        {
            Array.Fill(_rows[_top + row], ' ', col, width - col);
        }
        for (var r = _top + row + 1; r < _rows.Count; r++)
        {
            Array.Fill(_rows[r], ' ');
        }
    }
}

/// <summary>
/// The terminal's prompt: ↑ and ↓ step through what was sent in this folder
/// (newest first), the draft is kept, Esc goes back to it, a recalled message is
/// a copy; several lines, Ctrl+C and Ctrl+D; and the history kept per folder.
/// </summary>
public sealed class HistoryTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("code-arena-history-").FullName;

    private static (LineEditor Editor, ScriptedKeys Keys, StringWriter Output) Editor(List<string> history, int width = 80)
    {
        var keys = new ScriptedKeys { Width = width };
        var output = new StringWriter();
        return (new LineEditor(keys, output, () => history), keys, output);
    }

    [Fact]
    public void Up_shows_the_newest_then_older_and_down_comes_back_to_the_draft()
    {
        var (editor, keys, _) = Editor(["first", "second"]);
        keys.Type("half").Up().Enter();
        Assert.Equal("second", editor.Read("› ", "… "));
        keys.Type("half").Up().Up().Up().Enter();
        Assert.Equal("first", editor.Read("› ", "… "));
        // Down goes back toward the newest, and finally to what was being typed: kept.
        keys.Type("half").Up().Up().Down().Down().Enter();
        Assert.Equal("half", editor.Read("› ", "… "));
        // Down with nothing recalled is nothing.
        keys.Type("x").Down().Enter();
        Assert.Equal("x", editor.Read("› ", "… "));
        // The same text sent twice is stepped through once.
        var (twice, k2, _) = Editor(["same", "other", "same"]);
        k2.Up().Up().Up().Enter();
        Assert.Equal("other", twice.Read("› ", "… "));
    }

    [Fact]
    public void Escape_goes_back_to_the_draft_and_a_recalled_message_is_a_copy()
    {
        List<string> history = ["fix the build"];
        var (editor, keys, _) = Editor(history);
        keys.Type("draft").Up().Escape().Type("!").Enter();
        Assert.Equal("draft!", editor.Read("› ", "… "));

        keys.Up().Type(" now").Enter();
        Assert.Equal("fix the build now", editor.Read("› ", "… "));
        Assert.Equal(["fix the build"], history);
        keys.Up().Enter();
        Assert.Equal("fix the build", editor.Read("› ", "… "));
        // Escape with nothing recalled leaves the line alone.
        keys.Type("abc").Escape().Enter();
        Assert.Equal("abc", editor.Read("› ", "… "));
    }

    [Fact]
    public void In_several_lines_up_and_down_move_between_them_before_they_step_through_the_history()
    {
        var (editor, keys, _) = Editor(["older", "one\ntwo"]);
        // A line ending in \ goes on to the next; Up moves to the line above at the same column.
        keys.Type("a\\").Enter().Type("bc").Up().Type("X").Enter();
        Assert.Equal("aX\nbc", editor.Read("› ", "… "));
        // On the first line, Up recalls.
        keys.Type("a\\").Enter().Type("b").Up().Up().Enter();
        Assert.Equal("one\ntwo", editor.Read("› ", "… "));
        // A recalled message of several lines too: Up moves up its lines first, then on to the one before.
        keys.Up().Up().Type("!").Enter();
        Assert.Equal("one!\ntwo", editor.Read("› ", "… "));
        keys.Up().Up().Up().Enter();
        Assert.Equal("older", editor.Read("› ", "… "));
        keys.Up().Left().Up().Type("!").Down().Enter();
        Assert.Equal("on!e\ntwo", editor.Read("› ", "… "));
        // Down moves down its lines first, then on toward the draft, kept.
        keys.Type("draft").Up().Up().Down().Type("?").Enter();
        Assert.Equal("one\ntwo?", editor.Read("› ", "… "));
        keys.Type("draft").Up().Up().Down().Down().Enter();
        Assert.Equal("draft", editor.Read("› ", "… "));
    }

    [Fact]
    public void Enter_in_a_paste_is_a_new_line_and_alt_enter_too()
    {
        var (editor, keys, _) = Editor([]);
        keys.Pasting = true;
        keys.Type("first line\nsecond line\n");
        Assert.Equal("first line\nsecond line", editor.Read("› ", "… "));
        keys.Pasting = false;
        keys.Type("x").Press(ConsoleKey.Enter, '\r', alt: true).Type("y").Enter();
        Assert.Equal("x\ny", editor.Read("› ", "… "));
    }

    [Fact]
    public void Ctrl_c_clears_the_line_then_asks_to_leave_and_ctrl_d_ends_on_an_empty_line()
    {
        var asked = 0;
        var keys = new ScriptedKeys();
        var editor = new LineEditor(keys, new StringWriter(), () => ["sent"]) { Interrupted = () => ++asked < 2 };
        keys.Type("typed").Ctrl('c').Type("ok").Enter();
        Assert.Equal("ok", editor.Read("› ", "… "));
        Assert.Equal(0, asked);
        keys.Ctrl('c').Type("still here").Enter();
        Assert.Equal("still here", editor.Read("› ", "… "));
        Assert.Equal(1, asked);
        keys.Ctrl('c');
        Assert.Null(editor.Read("› ", "… "));
        Assert.Equal(2, asked);
        // Ctrl+C was a key only while reading.
        Assert.Equal(0, keys.Captured);

        keys.Type("ab").Ctrl('d').Ctrl('a').Ctrl('d').Enter();
        Assert.Equal("b", editor.Read("› ", "… "));
        keys.Ctrl('d');
        Assert.Null(editor.Read("› ", "… "));
        // Keys run out: the end of input.
        Assert.Null(editor.Read("› ", "… "));
    }

    [Fact]
    public void Editing_keys_move_by_character_word_and_line()
    {
        var (editor, keys, _) = Editor([]);
        keys.Type("hello world").Home().Type(">").Ctrl('e').Backspace().Ctrl('w').Type("there").Enter();
        Assert.Equal(">hello there", editor.Read("› ", "… "));
        keys.Type("one two three").Press(ConsoleKey.LeftArrow, '\0', alt: true).Ctrl('k').Ctrl('a').Press(ConsoleKey.Delete).Enter();
        Assert.Equal("ne two ", editor.Read("› ", "… "));
        keys.Type("😀x").Left().Left().Backspace().Ctrl('u').Type("ok").Enter();
        Assert.Equal("ok😀x", editor.Read("› ", "… "));
    }

    [Fact]
    public void What_is_drawn_wraps_at_the_terminals_width_with_the_cursor_at_the_caret()
    {
        var keys = new ScriptedKeys { Width = 10 };
        var output = new ScreenWriter(new Screen(10));
        var editor = new LineEditor(keys, output, () => ["one\ntwo"]);

        // Twelve letters after a two-column prompt (its colour codes take none): a row and a half. Home puts the cursor after the prompt.
        keys.Type("abcdefghijkl").Home().Enter();
        Assert.Equal("abcdefghijkl", editor.Read("\e[36m› \e[0m", "… "));
        // The last two flushes are the send's: before them, Home's.
        var home = output.Seen[^3];
        Assert.Equal(["› abcdefgh", "ijkl"], home.Lines);
        Assert.Equal((0, 2), (home.Row, home.Col));
        // Once sent, the cursor is under the message: what follows starts on a fresh row.
        Assert.Equal((2, 0), (output.Screen.Row, output.Screen.Col));

        // A line that fills its row exactly: the cursor goes on to the next row, and comes back when a letter is taken away.
        // Sent as it is drawn, it is not drawn again, and what follows starts on that next row.
        output.Screen = new Screen(10);
        keys.Type("abcdefgh");
        keys.Type("i").Backspace().Enter();
        Assert.Equal("abcdefgh", editor.Read("› ", "… "));
        Assert.Equal((1, 0), (output.Seen[^4].Row, output.Seen[^4].Col));
        Assert.Equal(["› abcdefgh", "i"], output.Seen[^3].Lines);
        Assert.Equal((1, 1), (output.Seen[^3].Row, output.Seen[^3].Col));
        Assert.Equal(["› abcdefgh"], output.Seen[^2].Lines);
        Assert.Equal((1, 0), (output.Seen[^2].Row, output.Seen[^2].Col));
        Assert.Equal((1, 0), (output.Screen.Row, output.Screen.Col));

        // A wide character that does not fit on the row goes on to the next, the cursor after it.
        output.Screen = new Screen(10);
        keys.Type("abcdefg漢").Enter();
        Assert.Equal("abcdefg漢", editor.Read("› ", "… "));
        Assert.Equal(["› abcdefg", "漢"], output.Seen[^2].Lines);
        Assert.Equal((1, 2), (output.Seen[^2].Row, output.Seen[^2].Col));

        // A recalled message of two lines takes the wrapped one's place: its second behind the continuation mark, the rows below cleared.
        output.Screen = new Screen(10);
        keys.Type("abcdefghijklmnop").Up().Enter();
        Assert.Equal("one\ntwo", editor.Read("› ", "… "));
        Assert.Equal(["› one", "… two"], output.Screen.Lines);
        Assert.Equal((2, 0), (output.Screen.Row, output.Screen.Col));

        // Up a line inside it: the cursor follows.
        output.Screen = new Screen(10);
        keys.Up().Left().Up().Enter();
        Assert.Equal("one\ntwo", editor.Read("› ", "… "));
        Assert.Equal((0, 4), (output.Seen[^3].Row, output.Seen[^3].Col));
    }

    [Fact]
    public void A_message_taller_than_the_screen_shows_the_rows_round_the_caret_and_is_sent_whole_once()
    {
        var keys = new ScriptedKeys { Width = 40, Height = 10 };
        var output = new ScreenWriter(new Screen(40, 10));
        List<string> history = ["before"];
        var editor = new LineEditor(keys, output, () => history);
        var trace = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"at frame {i}"));
        string[] whole = ["› " + trace.Split('\n')[0], .. trace.Split('\n').Skip(1).Select(l => "… " + l)];

        // Something printed before the prompt, on a screen part full.
        output.Write("earlier output\r\n");
        output.Flush();
        // A paste of thirty lines, then a question typed after it: each key draws the last ten rows again, in place.
        keys.Paste(trace).Type(" why?");
        // Up through the lines, further than a screen: the rows shown follow the caret.
        for (var i = 0; i < 25; i++)
        {
            keys.Up();
        }
        keys.Type("!").Enter();
        var read = editor.Read("› ", "… ");
        Assert.Equal(trace.Replace("at frame 30", "at frame 30 why?").Replace("at frame 5\n", "at frame 5!\n"), read);

        // While typing, the screen held the last ten rows and nothing of the message went past its top.
        var typed = output.Seen.FindIndex(s => s.Lines.Contains("… at frame 30 why?"));
        Assert.Equal(["earlier output", .. whole[20..].Select(l => l == "… at frame 30" ? "… at frame 30 why?" : l)], output.Seen[typed].Lines);
        Assert.Equal((9, 18), (output.Seen[typed].Row, output.Seen[typed].Col));
        // With the caret five lines from the top, the cursor is on that line, at the top of the screen.
        var up = output.Seen.FindIndex(s => s.Lines.Contains("… at frame 5!"));
        Assert.Equal((0, 13), (output.Seen[up].Row, output.Seen[up].Col));
        Assert.Equal(whole[4..14].Select(l => l == "… at frame 5" ? "… at frame 5!" : l), output.Seen[up].Lines.Skip(1));
        // Sent: the whole message once, in the scrollback and on the screen, and the cursor under it.
        string[] sent = [.. whole.Select(l => l switch { "… at frame 30" => "… at frame 30 why?", "… at frame 5" => "… at frame 5!", _ => l })];
        Assert.Equal(["earlier output", .. sent], output.Screen.Lines);
        Assert.Equal((9, 0), (output.Screen.Row, output.Screen.Col));
        Assert.Equal("", output.Screen.Current);

        // Brought back with ↑ and sent again: the rows round the caret while it is edited, then the whole of it, once.
        history.Add(read!);
        output.Screen = new Screen(40, 10);
        keys.Up().Type("?").Enter();
        Assert.Equal(read + "?", editor.Read("› ", "… "));
        Assert.Equal([.. sent[..^1], sent[^1] + "?"], output.Screen.Lines);
    }

    /// <summary>Feeds a screen what the editor writes, and keeps how it looked at each flush (the editor flushes once a drawing).</summary>
    private sealed class ScreenWriter(Screen screen) : StringWriter
    {
        public Screen Screen { get; set; } = screen;
        public List<(int Row, int Col, List<string> Lines)> Seen { get; } = [];

        public override void Flush()
        {
            Screen.Feed(GetStringBuilder().ToString());
            GetStringBuilder().Clear();
            Seen.Add((Screen.Row, Screen.Col, Screen.Lines));
        }
    }

    [Fact]
    public void Wide_characters_take_two_columns_and_combining_marks_none()
    {
        Assert.Equal(4, LineEditor.Cells("漢字"));
        Assert.Equal(2, LineEditor.Cells("😀"));
        Assert.Equal(1, LineEditor.Cells("é"));
        Assert.Equal(5, LineEditor.Cells("سلام!"));
        Assert.Equal(4, LineEditor.Cells("\t"));
    }

    [Fact]
    public void The_history_is_kept_per_folder_privately_and_cut_back_when_it_grows()
    {
        var shop = new InputHistory(_dir, "/home/ada/shop");
        var other = new InputHistory(_dir, "/home/ada/other/");
        shop.Add("  build it  ");
        shop.Add("build it");
        shop.Add("test it");
        shop.Add("   ");
        Assert.Equal(["build it", "test it"], shop.Entries());
        Assert.Empty(other.Entries());
        Assert.Equal(["build it", "test it"], new InputHistory(_dir, "/home/ada/shop/").Entries());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(shop.File));
        }
        // A line cut short is passed over.
        File.AppendAllText(shop.File, "{\"text\":\"half");
        shop.Add("after");
        Assert.Equal(["build it", "test it", "after"], shop.Entries());

        // Cut back to the newest once it holds twice what it keeps.
        var small = new InputHistory(_dir, "/home/ada/small", max: 10);
        for (var i = 0; i < 25; i++)
        {
            small.Add($"message {i}");
        }
        var kept = small.Entries();
        Assert.InRange(kept.Count, 10, 19);
        Assert.Equal("message 24", kept[^1]);
        Assert.Equal(kept.Count, File.ReadAllLines(small.File).Length);
        Assert.Equal(1000, InputHistory.Max);
    }

    [Fact]
    public async Task The_chat_prompt_recalls_what_was_sent_in_this_folder_in_this_run_and_the_next()
    {
        using var h = new Harness(_gateway);
        _gateway.Answer = req => Reply.Say("ok: " + FakeGateway.Last(req));
        h.Keys = new ScriptedKeys().Type("say hi\n").Up().Enter().Type("/exit\n");
        Assert.Equal(0, await h.Run("", "chat"));
        Assert.Equal(["say hi", "say hi"], _gateway.Requests.Select(FakeGateway.Last));
        Assert.Contains("↑ for what you sent before", h.Out);

        // The next run in this folder has it; an edit of it is a new message, and the history keeps both.
        h.Keys = new ScriptedKeys().Up().Type(" again\n").Up().Up().Enter().Type("/exit\n");
        Assert.Equal(0, await h.Run("", "chat"));
        Assert.Equal(["say hi again", "say hi"], _gateway.Requests.Skip(2).Select(FakeGateway.Last));
        var history = new InputHistory(h.Paths.HistoryDir, h.Work);
        Assert.Equal(["say hi", "say hi again", "say hi"], history.Entries());

        // Another folder has its own.
        h.Cwd = Directory.CreateDirectory(Path.Combine(h.Root, "elsewhere")).FullName;
        h.Keys = new ScriptedKeys().Up().Type("fresh\n").Type("/exit\n");
        Assert.Equal(0, await h.Run("", "chat"));
        Assert.Equal("fresh", FakeGateway.Last(_gateway.Requests[^1]));

        // Lines from a pipe are not kept, nor is leaving.
        h.Cwd = null;
        h.Keys = null;
        Assert.Equal(0, await h.Run("from a pipe\n", terminal: false, "chat"));
        Assert.Equal(0, await h.Run("typed by hand\n/exit\n", "chat"));
        Assert.Equal(["say hi", "say hi again", "say hi", "typed by hand"], history.Entries());
    }

    public void Dispose()
    {
        _gateway.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
