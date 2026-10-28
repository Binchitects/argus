using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>Something the model library holds on disk, and what keeps it there.</summary>
/// <param name="Path">Relative to the library: a GGUF file (a split one's first part), a snapshot's folder, or another model file.</param>
/// <param name="Kind">gguf, snapshot (Hugging Face, for vLLM) or file (a picture or video model's, as safetensors).</param>
/// <param name="UsedBy">The models in Arena that use it: it cannot be deleted while one does.</param>
/// <param name="KeptBy">A server of the stack whose own file it is (the embedder, Laya, the upscalers, the studio): never deleted here.</param>
/// <param name="Downloading">A download still writes into it.</param>
public sealed record DiskItem(string Path, string Kind, long Size, int Parts, IReadOnlyList<string> UsedBy, string? KeptBy, bool Downloading)
{
    public bool Deletable => UsedBy.Count == 0 && KeptBy is null && !Downloading;
}

/// <summary>
/// The model library on disk (Admin → Models → On disk): what it holds, how much each takes and which models use it, and
/// deleting what no model uses, for good. Removing a model from Arena leaves its files unless asked to take them too.
/// </summary>
public sealed class ModelDisk(ModelLibrary library, IOptions<EngineOptions> engine)
{
    /// <summary>The servers' own folders under the library (the files they are provisioned with), by whose they are.</summary>
    private static readonly (string Dir, string Whose)[] Servers =
    [
        .. MediaModels.Servers.SelectMany(s => s.Files.Select(f => (f.Dir, Whose(s.Server)))).Distinct(),
    ];

    private static string Whose(string server) => server switch
    {
        "imagegen" => "the picture server",
        "videogen" => "the video server",
        "embed" => "the embedder (knowledge and Argus)",
        "laya" => "Laya (decisions)",
        "studio" => "the media studio",
        _ => server,
    };

    /// <summary>Picture and video models' files that are not GGUF (those are listed as such).</summary>
    private static readonly string[] OtherFiles = [".safetensors", ".ckpt", ".pth", ".pt", ".bin"];

    private const int Most = 2000;

    private string Root => Path.GetFullPath(engine.Value.LibraryDir);

    public async Task<IReadOnlyList<DiskItem>> ListAsync(AppDbContext db, CancellationToken ct)
    {
        var local = await db.LocalModels.AsNoTracking().Select(m => new { m.Name, m.File, m.Projector, m.DraftHead }).ToListAsync(ct);
        var media = await db.MediaModels.AsNoTracking().ToListAsync(ct);
        var open = await db.ModelDownloads.AsNoTracking().Where(d => d.Kind == "files" && d.State != "done" && d.State != "failed").Select(d => d.Dir).ToListAsync(ct);
        List<string> Users(string path) =>
        [
            .. local.Where(m => m.File == path || m.Projector == path || m.DraftHead == path).Select(m => m.Name),
            .. media.Where(m => m.Files.Any(f => f.Path == path)).Select(m => m.Name),
        ];
        string? Kept(string path) => Servers.FirstOrDefault(s => path.StartsWith(s.Dir + "/", StringComparison.Ordinal)).Whose;
        bool Writing(string path) => open.Any(d => d.Length > 0 && (path.StartsWith(d + "/", StringComparison.Ordinal) || d == path || d.StartsWith(path + "/", StringComparison.Ordinal)));
        DiskItem Item(string path, string kind, long size, int parts) => new(path, kind, size, parts, Users(path), Kept(path), Writing(path));

        var items = new List<DiskItem>();
        items.AddRange(library.List().Select(e => Item(e.File.Path, "gguf", e.File.Size, e.File.Parts)));
        items.AddRange(library.Snapshots().Select(s => Item(s.Path, "snapshot", s.Size, 1)));
        if (Directory.Exists(Root))
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(Root, "*", options))
            {
                var path = Path.GetRelativePath(Root, file).Replace('\\', '/');
                if (!OtherFiles.Any(x => file.EndsWith(x, StringComparison.OrdinalIgnoreCase)) || path.StartsWith("hf/", StringComparison.Ordinal) || path.StartsWith('.') || path.Contains("/.", StringComparison.Ordinal))
                {
                    continue;
                }
                items.Add(Item(path, "file", new FileInfo(file).Length, 1));
                if (items.Count >= Most)
                {
                    break;
                }
            }
        }
        return [.. items.OrderByDescending(i => i.Size)];
    }

    /// <summary>The library disk's free and whole size, in bytes (null when the library is not there).</summary>
    public (long Free, long Total)? Room()
    {
        if (!Directory.Exists(Root))
        {
            return null;
        }
        var drive = new DriveInfo(Root);
        return (drive.AvailableFreeSpace, drive.TotalSize);
    }

    /// <summary>Deletes an item from disk (each part of a split GGUF, a snapshot's whole folder), and the folders it leaves empty; the bytes freed.</summary>
    public long Delete(DiskItem item)
    {
        var full = Path.GetFullPath(Path.Combine(Root, item.Path));
        if (!full.StartsWith(Root.TrimEnd('/') + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{item.Path} is outside the library.");
        }
        if (item.Kind == "snapshot")
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
            }
        }
        else
        {
            var files = item.Parts > 1
                ? Directory.EnumerateFiles(Path.GetDirectoryName(full)!, Path.GetFileName(full).Replace("-00001-of-", "-*-of-", StringComparison.Ordinal)).ToList()
                : [full];
            foreach (var f in files)
            {
                File.Delete(f);
                // What a download of it left half done goes with it.
                File.Delete(f + ".part");
            }
        }
        for (var dir = Path.GetDirectoryName(full); dir is not null && dir.StartsWith(Root.TrimEnd('/') + "/", StringComparison.Ordinal) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any(); dir = Path.GetDirectoryName(dir))
        {
            Directory.Delete(dir);
        }
        return item.Size;
    }

    /// <summary>Forgets the finished downloads none of whose files is left: Admin → Models → Hugging Face then offers them again.</summary>
    public async Task ForgetGoneDownloadsAsync(AppDbContext db, ModelDownloads downloads, CancellationToken ct)
    {
        var done = await db.ModelDownloads.Where(d => d.Kind == "files" && d.State == "done").ToListAsync(ct);
        var gone = done.Where(d => d.Files.Count > 0 && d.Files.All(f => downloads.PathOf(d, f) is not { } p || !File.Exists(p))).ToList();
        if (gone.Count > 0)
        {
            db.ModelDownloads.RemoveRange(gone);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Why an item cannot be deleted, or null when it can.</summary>
    public static string? WhyNot(DiskItem item) =>
        item.KeptBy is { } whose ? $"{item.Path} is {whose}'s own file: it stays."
        : item.UsedBy.Count > 0 ? $"{string.Join(", ", item.UsedBy)} {(item.UsedBy.Count == 1 ? "uses" : "use")} it: remove {(item.UsedBy.Count == 1 ? "that model" : "those models")} first (with its files, if you like)."
        : item.Downloading ? $"A download still writes into {item.Path}: let it finish, or remove the download first."
        : null;
}

/// <summary>Admin → Models → On disk: the library's items, and deleting one (models.delete).</summary>
public static class ModelDiskEndpoints
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapGet("/library/disk", ListAsync);
        g.MapDelete("/library/disk", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(ModelDisk disk, AppDbContext db, CancellationToken ct)
    {
        var items = await disk.ListAsync(db, ct);
        var room = disk.Room();
        return Results.Ok(new
        {
            items = items.Select(i => new { i.Path, i.Kind, i.Size, i.Parts, i.UsedBy, i.KeptBy, i.Downloading, whyNot = ModelDisk.WhyNot(i) }),
            used = items.Sum(i => i.Size), free = room?.Free, total = room?.Total,
        });
    }

    private static async Task<IResult> DeleteAsync(string path, ModelDisk disk, AppDbContext db, ModelDownloads downloads, Audit audit, CancellationToken ct)
    {
        if ((await disk.ListAsync(db, ct)).FirstOrDefault(i => i.Path == path) is not { } item)
        {
            return AuthEndpoints.Problem(404, "path", $"{path} is not in the model library.");
        }
        if (ModelDisk.WhyNot(item) is { } why)
        {
            return AuthEndpoints.Problem(409, "path", why);
        }
        var freed = disk.Delete(item);
        await disk.ForgetGoneDownloadsAsync(db, downloads, ct);
        await audit.WriteAsync("model.files.delete", item.Path, detail: $"{item.Kind}, {Storage.StorageDisks.Size(freed)} freed");
        return Results.Ok(new { item.Path, freed });
    }
}
