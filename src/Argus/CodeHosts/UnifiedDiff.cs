namespace Argus.CodeHosts;

/// <summary>A unified diff as git writes it ("diff --git a/x b/y" for each file), cut into one <see cref="FileDiff"/> per file.</summary>
public static class UnifiedDiff
{
    public static List<FileDiff> Parse(string text)
    {
        var files = new List<FileDiff>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }
            var (oldPath, newPath) = Paths(lines[i]);
            bool added = false, deleted = false, renamed = false, binary = false;
            var hunks = new List<string>();
            for (i++; i < lines.Length && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal); i++)
            {
                var line = lines[i];
                if (hunks.Count > 0 || line.StartsWith("@@", StringComparison.Ordinal))
                {
                    hunks.Add(line);
                }
                else if (line.StartsWith("new file mode", StringComparison.Ordinal)) added = true;
                else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) deleted = true;
                else if (line.StartsWith("rename from ", StringComparison.Ordinal)) { oldPath = line["rename from ".Length..]; renamed = true; }
                else if (line.StartsWith("rename to ", StringComparison.Ordinal)) { newPath = line["rename to ".Length..]; renamed = true; }
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal)) binary = true;
                else if (line.StartsWith("--- ", StringComparison.Ordinal) && line != "--- /dev/null") oldPath = Strip(line[4..], "a/");
                else if (line.StartsWith("+++ ", StringComparison.Ordinal) && line != "+++ /dev/null") newPath = Strip(line[4..], "b/");
            }
            // The text ends with a newline, as GitLab's does; the split's empty last piece is not a line.
            while (hunks.Count > 0 && hunks[^1].Length == 0) hunks.RemoveAt(hunks.Count - 1);
            files.Add(new FileDiff(oldPath, newPath, added, deleted, renamed, hunks.Count == 0 ? "" : string.Join("\n", hunks) + "\n") { Binary = binary });
        }
        return files;
    }

    /// <summary>The paths in "diff --git a/x b/y" (quoted ones unquoted): good enough until ---/+++ or a rename says better.</summary>
    static (string Old, string New) Paths(string header)
    {
        var rest = header["diff --git ".Length..];
        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            var first = close > 0 ? rest[1..close] : rest;
            var second = close > 0 ? rest[(close + 1)..].Trim().Trim('"') : rest;
            return (Strip(first, "a/"), Strip(second, "b/"));
        }
        // a/x b/x: the two halves are the same length when the file was not renamed.
        var mid = rest.Length / 2;
        if (rest.Length % 2 == 1 && rest[mid] == ' ' && rest[..mid][2..] == rest[(mid + 1)..][2..])
            return (Strip(rest[..mid], "a/"), Strip(rest[(mid + 1)..], "b/"));
        var split = rest.IndexOf(" b/", StringComparison.Ordinal);
        return split > 0 ? (Strip(rest[..split], "a/"), Strip(rest[(split + 1)..], "b/")) : (rest, rest);
    }

    static string Strip(string path, string prefix)
    {
        path = path.TrimEnd('\t').Trim('"');
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }
}
