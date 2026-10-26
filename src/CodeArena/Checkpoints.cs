namespace CodeArena;

/// <summary>
/// A checkpoint before each turn: how long the conversation was, and what each file the turn's tools wrote held before
/// (kept the first time the turn writes it). /rewind goes back to before a turn: the conversation as it was, and those
/// files as they were (one the turn made is removed). What commands changed is not kept: git has it, or the person.
/// Kept while Code Arena runs (memory only, at most the last 50 turns).
/// </summary>
internal sealed class Checkpoints
{
    public const int Most = 50;
    /// <summary>A file bigger than this is not kept (and a turn that wrote it cannot give it back).</summary>
    public const long MostBytes = 20_000_000;

    internal sealed class Turn(int messages, string input)
    {
        public int Messages { get; } = messages;
        public string Input { get; } = input;
        public DateTime At { get; } = DateTime.Now;
        /// <summary>Each file written, as it was before the turn (null: it did not exist); too big to keep: not here, in Lost.</summary>
        public Dictionary<string, byte[]?> Before { get; } = new(StringComparer.Ordinal);
        public List<string> Lost { get; } = [];
    }

    private readonly object _gate = new();
    private readonly List<Turn> _turns = [];

    /// <summary>A checkpoint before each step of the plan as it began: what each file written since held before it (files only).</summary>
    internal sealed class Step(int number, string title)
    {
        public int Number { get; } = number;
        public string Title { get; } = title;
        public Dictionary<string, byte[]?> Before { get; } = new(StringComparer.Ordinal);
        public List<string> Lost { get; } = [];
    }

    private readonly List<Step> _steps = [];

    public IReadOnlyList<Step> Steps
    {
        get
        {
            lock (_gate)
            {
                return [.. _steps];
            }
        }
    }

    /// <summary>A step of the plan begins: what the tools write from now on is kept as it was before it, for /plan undo.</summary>
    public void BeginStep(int number, string title)
    {
        lock (_gate)
        {
            _steps.RemoveAll(s => s.Number == number);
            _steps.Add(new Step(number, title));
            if (_steps.Count > Most)
            {
                _steps.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// The files as they were before step <paramref name="number"/> began, and before every step begun after it, put back
    /// (latest first); those checkpoints go. The step numbers undone, the files put back, and those that could not be.
    /// </summary>
    public (List<int> Steps, List<string> Restored, List<string> Lost) UndoStep(int number)
    {
        lock (_gate)
        {
            var at = _steps.FindIndex(s => s.Number == number);
            if (at < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(number));
            }
            var undone = _steps.Skip(at).ToList();
            var restored = new List<string>();
            var lost = new List<string>();
            foreach (var step in Enumerable.Reverse(undone))
            {
                Restore(step.Before, restored, lost);
                lost.AddRange(step.Lost.Select(p => $"{p} (too big to keep)"));
            }
            _steps.RemoveRange(at, _steps.Count - at);
            return ([.. undone.Select(s => s.Number)], restored, lost);
        }
    }

    private static void Restore(Dictionary<string, byte[]?> before, List<string> restored, List<string> lost)
    {
        foreach (var (path, was) in before)
        {
            try
            {
                if (was is null)
                {
                    File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, was);
                }
                if (!restored.Contains(path))
                {
                    restored.Add(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lost.Add($"{path} ({e.Message})");
            }
        }
    }

    public IReadOnlyList<Turn> Turns
    {
        get
        {
            lock (_gate)
            {
                return [.. _turns];
            }
        }
    }

    /// <summary>A turn begins with this many messages before it.</summary>
    public void Begin(int messages, string input)
    {
        lock (_gate)
        {
            _turns.Add(new Turn(messages, input));
            if (_turns.Count > Most)
            {
                _turns.RemoveAt(0);
            }
        }
    }

    /// <summary>A tool is about to write this file: its content before the turn is kept, once.</summary>
    public void BeforeWrite(string full)
    {
        lock (_gate)
        {
            if (_turns.Count > 0)
            {
                Keep(_turns[^1].Before, _turns[^1].Lost, full);
            }
            if (_steps.Count > 0)
            {
                Keep(_steps[^1].Before, _steps[^1].Lost, full);
            }
        }
    }

    private static void Keep(Dictionary<string, byte[]?> before, List<string> lost, string full)
    {
        if (before.ContainsKey(full) || lost.Contains(full))
        {
            return;
        }
        var info = new FileInfo(full);
        if (info.Exists && info.Length > MostBytes)
        {
            lost.Add(full);
            return;
        }
        before[full] = info.Exists ? File.ReadAllBytes(full) : null;
    }

    /// <summary>
    /// Back to before turn <paramref name="number"/> (from 1, the oldest kept): its files and every later turn's put back,
    /// latest first. The conversation's length then, the files put back, and those that could not be.
    /// </summary>
    public (int Messages, List<string> Restored, List<string> Lost) Rewind(int number, bool files)
    {
        lock (_gate)
        {
            if (number < 1 || number > _turns.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(number));
            }
            var undone = _turns.Skip(number - 1).ToList();
            var restored = new List<string>();
            var lost = new List<string>();
            if (files)
            {
                foreach (var turn in Enumerable.Reverse(undone))
                {
                    foreach (var (path, before) in turn.Before)
                    {
                        try
                        {
                            if (before is null)
                            {
                                File.Delete(path);
                            }
                            else
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                                File.WriteAllBytes(path, before);
                            }
                            if (!restored.Contains(path))
                            {
                                restored.Add(path);
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            lost.Add($"{path} ({e.Message})");
                        }
                    }
                    lost.AddRange(turn.Lost.Select(p => $"{p} (too big to keep)"));
                }
            }
            var messages = undone[0].Messages;
            _turns.RemoveRange(number - 1, _turns.Count - number + 1);
            return (messages, restored, lost);
        }
    }
}
