using System.Text;

namespace ArenaCode;

/// <summary>A line of a diff as the web interface shows it.</summary>
internal readonly record struct DiffLine(char Op, int Old, int New, string Text);

/// <summary>Line diffs for showing an edit: the changed lines, three lines of context around them, numbered.</summary>
internal static class Diff
{
    private enum Op { Same, Removed, Added }

    private readonly record struct Row(Op Op, string Text, int Old, int New);

    public static string[] Lines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    /// <summary>How many lines were added and removed.</summary>
    public static (int Added, int Removed) Count(string before, string after)
    {
        var rows = Rows(Lines(before), Lines(after));
        return (rows.Count(r => r.Op == Op.Added), rows.Count(r => r.Op == Op.Removed));
    }

    /// <summary>The diff as the terminal shows it; at most maxLines lines.</summary>
    public static string Render(string before, string after, Ui ui, int context = 3, int maxLines = 80)
    {
        var rows = Rows(Lines(before), Lines(after));
        var (shown, rest) = Window(rows, context, maxLines);
        if (shown.Count == 0)
        {
            return ui.Dim("(no change)");
        }
        var width = Math.Max(rows.Max(r => Math.Max(r.Old, r.New)).ToString().Length, 3);
        var sb = new StringBuilder();
        foreach (var shownRow in shown)
        {
            var line = shownRow is not { } r ? ui.Dim($"{new string(' ', width)}   ⋮") : r.Op switch
            {
                Op.Removed => ui.Red($"{r.Old.ToString().PadLeft(width)} - {r.Text}"),
                Op.Added => ui.Green($"{r.New.ToString().PadLeft(width)} + {r.Text}"),
                _ => ui.Dim($"{r.New.ToString().PadLeft(width)}   {r.Text}"),
            };
            sb.Append(line).Append('\n');
        }
        if (rest > 0)
        {
            sb.Append(ui.Dim($"{new string(' ', width)}   … {rest} more changed lines")).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// The same diff as data, for the web interface: each line's kind (' ' the
    /// same, '-' removed, '+' added, '⋮' lines left out), its old and new numbers
    /// (0 when it has none), and how many changed lines did not fit.
    /// </summary>
    public static (List<DiffLine> Lines, int More) Hunks(string before, string after, int context = 3, int maxLines = 400)
    {
        var (shown, rest) = Window(Rows(Lines(before), Lines(after)), context, maxLines);
        var lines = shown.Select(shownRow => shownRow is not { } r ? new DiffLine('⋮', 0, 0, "") : r.Op switch
        {
            Op.Removed => new DiffLine('-', r.Old, 0, r.Text),
            Op.Added => new DiffLine('+', 0, r.New, r.Text),
            _ => new DiffLine(' ', r.Old, r.New, r.Text),
        }).ToList();
        return (lines, rest);
    }

    /// <summary>The changed rows with their context (null where lines are left out between them), and how many changed rows did not fit in maxLines.</summary>
    private static (List<Row?> Shown, int After) Window(List<Row> rows, int context, int maxLines)
    {
        var shown = new List<Row?>();
        var changed = rows.Select((r, i) => (r, i)).Where(x => x.r.Op != Op.Same).Select(x => x.i).ToList();
        if (changed.Count == 0)
        {
            return (shown, 0);
        }
        var show = new bool[rows.Count];
        foreach (var i in changed)
        {
            for (var j = Math.Max(0, i - context); j <= Math.Min(rows.Count - 1, i + context); j++)
            {
                show[j] = true;
            }
        }
        var lines = 0;
        var gap = false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!show[i])
            {
                gap = true;
                continue;
            }
            if (lines >= maxLines)
            {
                return (shown, rows.Skip(i).Count(r => r.Op != Op.Same));
            }
            if (gap && shown.Count > 0)
            {
                shown.Add(null);
            }
            gap = false;
            shown.Add(rows[i]);
            lines++;
        }
        return (shown, 0);
    }

    /// <summary>The common start and end are kept as they are; the middle is diffed by longest common subsequence.</summary>
    private static List<Row> Rows(string[] a, string[] b)
    {
        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix])
        {
            suffix++;
        }
        var rows = new List<Row>();
        for (var i = 0; i < prefix; i++)
        {
            rows.Add(new Row(Op.Same, a[i], i + 1, i + 1));
        }
        var midA = a[prefix..(a.Length - suffix)];
        var midB = b[prefix..(b.Length - suffix)];
        if ((long)midA.Length * midB.Length > 4_000_000)
        {
            // Too large to align: everything removed, then everything added.
            rows.AddRange(midA.Select((t, i) => new Row(Op.Removed, t, prefix + i + 1, 0)));
            rows.AddRange(midB.Select((t, i) => new Row(Op.Added, t, 0, prefix + i + 1)));
        }
        else
        {
            var lcs = new int[midA.Length + 1, midB.Length + 1];
            for (var i = midA.Length - 1; i >= 0; i--)
            {
                for (var j = midB.Length - 1; j >= 0; j--)
                {
                    lcs[i, j] = midA[i] == midB[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
            }
            int x = 0, y = 0;
            while (x < midA.Length || y < midB.Length)
            {
                if (x < midA.Length && y < midB.Length && midA[x] == midB[y])
                {
                    rows.Add(new Row(Op.Same, midA[x], prefix + x + 1, prefix + y + 1));
                    x++;
                    y++;
                }
                else if (x < midA.Length && (y == midB.Length || lcs[x + 1, y] >= lcs[x, y + 1]))
                {
                    rows.Add(new Row(Op.Removed, midA[x], prefix + x + 1, 0));
                    x++;
                }
                else
                {
                    rows.Add(new Row(Op.Added, midB[y], 0, prefix + y + 1));
                    y++;
                }
            }
        }
        for (var i = 0; i < suffix; i++)
        {
            var oldNo = a.Length - suffix + i;
            var newNo = b.Length - suffix + i;
            rows.Add(new Row(Op.Same, a[oldNo], oldNo + 1, newNo + 1));
        }
        return rows;
    }
}
