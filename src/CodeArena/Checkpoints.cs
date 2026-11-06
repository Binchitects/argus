namespace CodeArena;

/// <summary>
/// A checkpoint before each turn: where in the whole conversation it began, and what each file the turn's tools wrote held
/// before (kept the first time the turn writes it). /rewind goes back to before a turn: the conversation as it was, and
/// those files as they were (one the turn made is removed). What commands changed is not kept: git has it, or the person.
/// Kept while Code Arena runs (memory only, at most the last 50 turns).
/// </summary>
internal sealed class Checkpoints
{
    public const int Most = 50;
    /// <summary>A file bigger than this is not kept (and a turn that wrote it cannot give it back).</summary>
    public const long MostBytes = 20_000_000;

    internal sealed class Turn(int place, string input)
    {
        /// <summary>
        /// How many messages the whole conversation had as the turn began: its question's place (the IDE's m{place}). A
        /// compaction does not change it, as it changes how many the model reads.
        /// </summary>
        public int Place { get; } = place;
        public string Input { get; } = input;
        public DateTime At { get; } = DateTime.Now;
        /// <summary>Each file written, as it was before the turn (null: it did not exist); too big to keep: not here, in Lost.</summary>
        public Dictionary<string, byte[]?> Before { get; } = new(StringComparer.Ordinal);
        public List<string> Lost { get; } = [];
    }

    private readonly object _gate = new();
    private readonly List<Turn> _turns = [];
    /// <summary>
    /// What the files held before the first turn kept, written by turns whose conversation alone went back (/rewind 1 chat):
    /// a rewind of the code to the first turn puts them back too.
    /// </summary>
    private readonly Turn _base = new(0, "");

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

    /// <summary>A turn begins with this many messages of the whole conversation before it.</summary>
    public void Begin(int place, string input)
    {
        lock (_gate)
        {
            _turns.Add(new Turn(place, input));
            if (_turns.Count > Most)
            {
                _turns.RemoveAt(0);
                // What came before the turn let go is let go with it.
                _base.Before.Clear();
                _base.Lost.Clear();
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
    /// What going back to before turn <paramref name="number"/> would do to the files as they are now: each one its tools or
    /// a later turn's wrote that differs from what it is put back to, and whether it is removed (a turn made it); and those
    /// too big to keep, which are not put back.
    /// </summary>
    public (List<(string Path, bool Removed)> Files, List<string> Lost) Changes(int number)
    {
        lock (_gate)
        {
            if (number < 1 || number > _turns.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(number));
            }
            // Each file as the earliest of those turns found it: what it is put back to.
            var before = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
            var lost = new List<string>();
            foreach (var turn in Undone(number))
            {
                foreach (var (path, was) in turn.Before)
                {
                    before.TryAdd(path, was);
                }
                lost.AddRange(turn.Lost.Where(p => !lost.Contains(p)));
            }
            var files = new List<(string Path, bool Removed)>();
            foreach (var (path, was) in before)
            {
                var now = File.Exists(path) ? File.ReadAllBytes(path) : null;
                if (was is null ? now is not null : now is null || !was.AsSpan().SequenceEqual(now))
                {
                    files.Add((path, was is null));
                }
            }
            return (files, lost);
        }
    }

    /// <summary>The checkpoints going back to before turn <paramref name="number"/> undoes, earliest first: before the first, what came before it too.</summary>
    private List<Turn> Undone(int number) => [.. number == 1 ? [_base] : Array.Empty<Turn>(), .. _turns.Skip(number - 1)];

    /// <summary>
    /// Back to before turn <paramref name="number"/> (from 1, the oldest kept): its files and every later turn's put back,
    /// latest first. With only the conversation going back, what those turns' files held before them goes to the turn
    /// before (or to before the first): a later rewind of the code to there or earlier puts them back too. Where in the
    /// conversation that turn began, the files put back, and those that could not be.
    /// </summary>
    public (int Place, List<string> Restored, List<string> Lost) Rewind(int number, bool files)
    {
        lock (_gate)
        {
            if (number < 1 || number > _turns.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(number));
            }
            var place = _turns[number - 1].Place;
            var restored = new List<string>();
            var lost = new List<string>();
            if (files)
            {
                foreach (var turn in Enumerable.Reverse(Undone(number)))
                {
                    Restore(turn.Before, restored, lost);
                    lost.AddRange(turn.Lost.Select(p => $"{p} (too big to keep)"));
                }
                if (number == 1)
                {
                    _base.Before.Clear();
                    _base.Lost.Clear();
                }
            }
            else
            {
                var into = number > 1 ? _turns[number - 2] : _base;
                foreach (var turn in _turns.Skip(number - 1))
                {
                    foreach (var (path, was) in turn.Before)
                    {
                        if (!into.Lost.Contains(path))
                        {
                            into.Before.TryAdd(path, was);
                        }
                    }
                    into.Lost.AddRange(turn.Lost.Where(p => !into.Before.ContainsKey(p) && !into.Lost.Contains(p)));
                }
            }
            _turns.RemoveRange(number - 1, _turns.Count - number + 1);
            return (place, restored, lost);
        }
    }
}
