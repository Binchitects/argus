using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>The files a workspace's folders hold, as the code index covers them (Code Arena's own: code-intel lists a checkout's).</summary>
internal static partial class CodeFiles
{
    /// <summary>
    /// The files under the working folder (and each folder added to it) the index covers, as grep sees them: binary files,
    /// those past <see cref="MostTextBytes"/>, and links that lead outside are left out; at most <see cref="MostFiles"/>.
    /// </summary>
    public static List<CodeFile> List(Workspace workspace, IEnumerable<string> roots, CancellationToken ct)
    {
        var files = new List<CodeFile>();
        foreach (var root in roots)
        {
            foreach (var full in workspace.FilesUnder(root, ct))
            {
                ct.ThrowIfCancellationRequested();
                FileInfo info;
                try
                {
                    info = new FileInfo(full);
                    if (!info.Exists || info.Length > MostTextBytes)
                    {
                        continue;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                var path = workspace.Show(full).Replace('\\', '/');
                var tags = TagsOf(path);
                // A secret's file is listed (so the index can say it was skipped) without being opened, binary or not.
                if (!tags.HasFlag(CodeFileTags.Sensitive) && Files.LooksBinary(full))
                {
                    continue;
                }
                files.Add(new CodeFile(path, full, LanguageOf(path), info.Length, info.LastWriteTimeUtc, tags));
                if (files.Count >= MostFiles)
                {
                    return files;
                }
            }
        }
        return files;
    }
}
