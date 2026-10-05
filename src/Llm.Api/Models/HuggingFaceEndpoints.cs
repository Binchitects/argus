using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Models;

/// <param name="Models">The repository's models to download, by name (a split model's parts come together; a vision projector is a model too).</param>
public sealed record HfDownloadRequest(string Repo, string[] Models);

/// <summary>Admin → Models → Hugging Face: find GGUF models, see their files and whether they fit, download them into the library.</summary>
public static class HuggingFaceEndpoints
{
    public static void MapHuggingFace(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/models/hf").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("/search", SearchAsync);
        g.MapGet("/repo", RepoAsync);
        g.MapGet("/downloads", ListAsync);
        g.MapPost("/downloads", StartAsync);
        g.MapPost("/downloads/{id:guid}/pause", PauseAsync);
        g.MapPost("/downloads/{id:guid}/resume", ResumeAsync);
        g.MapDelete("/downloads/{id:guid}", RemoveAsync);
    }

    private static async Task<IResult> Try(Func<Task<IResult>> call)
    {
        try
        {
            return await call();
        }
        catch (HuggingFaceException ex)
        {
            return AuthEndpoints.Problem(ex.Status, "huggingface", ex.Message);
        }
    }

    private static Task<IResult> SearchAsync(string? q, HuggingFace hf, CancellationToken ct) => Try(async () =>
        (q ?? "").Trim() is { Length: >= 2 } words
            ? Results.Ok(await hf.SearchAsync(words, ct))
            : AuthEndpoints.Problem(400, "query", "Search for two letters or more, e.g. qwen3 or llama."));

    /// <summary>A repository's GGUF models, each with whether it is in the library already, and the machine's room for models to judge the fit by.</summary>
    private static Task<IResult> RepoAsync(string? id, HuggingFace hf, ModelDownloads downloads, HardwareProbe hardware, CancellationToken ct) => Try(async () =>
    {
        var repo = await hf.RepoAsync((id ?? "").Trim(), ct);
        var hw = await hardware.GetAsync(ct);
        var probe = new ModelDownload { Repo = repo.Id, Revision = repo.Sha, Dir = repo.Id, CreatedBy = "" };
        return Results.Ok(new
        {
            repo.Id, repo.Sha, repo.Gated, repo.License, repo.Architecture, repo.Parameters, repo.Context, repo.Downloads, repo.Likes, repo.LastModified,
            gpuForModels = hw?.GpuForModels, ramForModels = hw?.RamForModels,
            models = repo.Models.Select(m => new
            {
                m.Name, m.Quant, m.Size, m.Projector, parts = m.Files.Count,
                inLibrary = m.Files.All(f => downloads.PathOf(probe, f.Path) is { } p && File.Exists(p) && new FileInfo(p).Length == f.Size),
                path = $"{repo.Id}/{m.Files[0].Path}",
            }),
        });
    });

    private static async Task<IResult> ListAsync(AppDbContext db, ModelDownloads downloads, CancellationToken ct)
    {
        var rows = await db.ModelDownloads.AsNoTracking().OrderByDescending(d => d.CreatedAt).Take(50).ToListAsync(ct);
        return Results.Ok(rows.Select(d => View(d, downloads)));
    }

    private static object View(ModelDownload d, ModelDownloads downloads)
    {
        var live = downloads.Now(d.Id);
        return new
        {
            d.Id, d.Repo, d.Revision, d.State, bytes = live?.Bytes ?? d.Bytes, d.Total, speed = live?.Speed, d.Error, d.CreatedBy, d.CreatedAt, d.FinishedAt,
            files = d.Files.Select(f => new { f.Path, f.Size, library = $"{d.Dir}/{f.Target ?? f.Path}" }),
        };
    }

    private static Task<IResult> StartAsync(HfDownloadRequest body, ClaimsPrincipal p, AppDbContext db, HuggingFace hf, ModelDownloads downloads, Audit audit,
        CancellationToken ct) => Try(async () =>
    {
        var repo = await hf.RepoAsync((body.Repo ?? "").Trim(), ct);
        var chosen = repo.Models.Where(m => (body.Models ?? []).Contains(m.Name)).ToList();
        if (chosen.Count == 0 || chosen.Count != (body.Models ?? []).Distinct().Count())
        {
            return AuthEndpoints.Problem(400, "models", "Choose models this repository has (by name).");
        }
        var d = new ModelDownload
        {
            Repo = repo.Id, Revision = repo.Sha, Dir = repo.Id, CreatedBy = p.Identity?.Name ?? "admin",
            Files = [.. chosen.SelectMany(m => m.Files).Select(f => new DownloadFile { Path = f.Path, Size = f.Size, Sha256 = f.Sha256 })],
        };
        if (d.Files.Any(f => downloads.PathOf(d, f) is null))
        {
            return AuthEndpoints.Problem(400, "path", "A file of this repository would land outside the model library.");
        }
        d.Total = d.Files.Sum(f => f.Size);
        var open = await db.ModelDownloads.Where(x => x.Repo == d.Repo && x.State != "done").ToListAsync(ct);
        if (open.Any(x => x.Files.Select(f => f.Path).Intersect(d.Files.Select(f => f.Path)).Any()))
        {
            return AuthEndpoints.Problem(409, "exists", "These files have a download already: let it run, resume it, or remove it first.");
        }
        // Whole files present already are not counted; the disk must keep room to spare.
        var needed = d.Files.Sum(f => downloads.PathOf(d, f) is { } path && File.Exists(path) && new FileInfo(path).Length == f.Size ? 0 : f.Size);
        Directory.CreateDirectory(downloads.Root);
        var free = new DriveInfo(Path.GetFullPath(downloads.Root)).AvailableFreeSpace;
        if (needed + ModelDownloads.SpareBytes > free)
        {
            return AuthEndpoints.Problem(409, "space", $"The library's disk has {Gb(free)} free, and this needs {Gb(needed)} (and {Gb(ModelDownloads.SpareBytes)} to spare).");
        }
        db.ModelDownloads.Add(d);
        await db.SaveChangesAsync(ct);
        downloads.Wake();
        await audit.WriteAsync("model.download", repo.Id, detail: $"{string.Join(", ", chosen.Select(m => m.Name))} ({Gb(d.Total)})");
        return Results.Created($"/api/admin/models/hf/downloads/{d.Id}", View(d, downloads));
    });

    private static async Task<IResult> PauseAsync(Guid id, AppDbContext db, ModelDownloads downloads, CancellationToken ct)
    {
        if (await db.ModelDownloads.SingleOrDefaultAsync(d => d.Id == id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        if (d.State is not ("queued" or "running"))
        {
            return AuthEndpoints.Problem(409, "state", "Only a download waiting or running can be paused.");
        }
        d.State = "paused";
        await db.SaveChangesAsync(ct);
        downloads.Stop(id);
        return Results.NoContent();
    }

    private static async Task<IResult> ResumeAsync(Guid id, AppDbContext db, ModelDownloads downloads, CancellationToken ct)
    {
        if (await db.ModelDownloads.SingleOrDefaultAsync(d => d.Id == id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        if (d.State is not ("paused" or "failed"))
        {
            return AuthEndpoints.Problem(409, "state", "Only a paused or failed download goes on.");
        }
        d.State = "queued";
        d.Error = null;
        await db.SaveChangesAsync(ct);
        downloads.Wake();
        return Results.NoContent();
    }

    /// <summary>Forgets a download; what it left half done goes, the files it finished stay in the library.</summary>
    private static async Task<IResult> RemoveAsync(Guid id, AppDbContext db, ModelDownloads downloads, Audit audit, CancellationToken ct)
    {
        if (await db.ModelDownloads.SingleOrDefaultAsync(d => d.Id == id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        db.ModelDownloads.Remove(d);
        await db.SaveChangesAsync(ct);
        downloads.Stop(id);
        // The run lets go of its files once stopped.
        await Task.Delay(200, ct);
        downloads.Discard(d);
        await audit.WriteAsync("model.download_remove", d.Repo, detail: d.State);
        return Results.NoContent();
    }

    private static string Gb(long bytes) => $"{bytes / 1073741824.0:0.#} GB";
}
