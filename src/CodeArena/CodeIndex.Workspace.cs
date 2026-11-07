using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace CodeArena;

/// <summary>The code index of a Code Arena workspace: opened and synced with its folders, and searched by words (Code Arena's own).</summary>
internal sealed partial class CodeIndex
{
    private CodeSearch? _search;

    partial void Invalidated() => _search = null;

    /// <summary>
    /// The folder's index as it was kept, brought in step with the folder (built whole when there is none, it cannot be read,
    /// or a rebuild is asked for), and written back when anything changed.
    /// </summary>
    public static (CodeIndex Index, IndexSync Sync, bool Built) Open(string dataDir, Workspace workspace, CancellationToken ct, bool rebuild = false)
    {
        var file = FileFor(dataDir, workspace.Root);
        var index = rebuild ? null : Load(workspace.Root, file);
        var built = index is null;
        index ??= new CodeIndex(workspace.Root, file);
        var sync = index.Sync(workspace, workspace.Roots, ct);
        if (built || sync.Read > 0 || sync.Removed > 0 || sync.Skipped > 0)
        {
            index.Save();
        }
        return (index, sync, built);
    }

    /// <summary>The answers by words and by name over the files as the index has them now; made again after a sync changes them.</summary>
    public CodeSearch Search()
    {
        lock (_gate)
        {
            return _search ??= new CodeSearch(this, _files.Values);
        }
    }

    /// <summary>Brings the index in step with the workspace's folders (<see cref="Sync(IReadOnlyList{CodeFile}, CancellationToken, int)"/>).</summary>
    public IndexSync Sync(Workspace workspace, IEnumerable<string> roots, CancellationToken ct, int threads = 0) =>
        Sync(CodeFiles.List(workspace, roots, ct), ct, threads);
}
