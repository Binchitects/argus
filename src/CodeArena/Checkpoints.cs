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
            if (_turns.Count == 0 || _turns[^1].Before.ContainsKey(full) || _turns[^1].Lost.Contains(full))
            {
                return;
            }
            var info = new FileInfo(full);
            if (info.Exists && info.Length > MostBytes)
            {
                _turns[^1].Lost.Add(full);
                return;
            }
            _turns[^1].Before[full] = info.Exists ? File.ReadAllBytes(full) : null;
        }
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
