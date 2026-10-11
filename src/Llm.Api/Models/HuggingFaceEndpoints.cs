using System.Net.Http.Json;
using System.Security.Claims;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Models;

/// <param name="Models">The repository's models to download, by name (a split model's parts come together; a vision projector is a model too).</param>
/// <param name="Files">Or its files by path: a picture or video model's diffusion model, text encoders and decoder.</param>
public sealed record HfDownloadRequest(string Repo, string[]? Models = null, string[]? Files = null);

/// <summary>A speech model for the speech server to fetch: its id there (a Hugging Face repository).</summary>
public sealed record SpeechDownloadRequest(string Id);

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
        g.MapGet("/speech", SpeechRegistryAsync);
        g.MapPost("/speech", SpeechDownloadAsync);
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

    /// <param name="kind">chat (GGUF), image or video (their pipelines).</param>
    private static Task<IResult> SearchAsync(string? q, string? kind, HuggingFace hf, CancellationToken ct) => Try(async () =>
        (q ?? "").Trim() is { Length: >= 2 } words
            ? Results.Ok(await hf.SearchAsync(words, ct, kind switch { "image" => "text-to-image", "video" => "text-to-video", _ => null }))
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
            // Every file a picture or video model may be made of, with the role its name suggests.
            files = repo.Weights.Select(f => new
            {
                f.Path, f.Size, role = MediaModelEndpoints.RoleOf(f.Path), library = $"{repo.Id}/{f.Path}",
                inLibrary = downloads.PathOf(probe, f.Path) is { } p && File.Exists(p) && new FileInfo(p).Length == f.Size,
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
            d.Id, d.Kind, d.Repo, d.Revision, d.State, bytes = live?.Bytes ?? d.Bytes, d.Total, speed = live?.Speed, d.Error, d.CreatedBy, d.CreatedAt, d.FinishedAt,
            files = d.Files.Select(f => new { f.Path, f.Size, library = $"{d.Dir}/{f.Target ?? f.Path}" }),
        };
    }

    private static Task<IResult> StartAsync(HfDownloadRequest body, ClaimsPrincipal p, AppDbContext db, HuggingFace hf, ModelDownloads downloads, Audit audit,
        CancellationToken ct) => Try(async () =>
    {
        var repo = await hf.RepoAsync((body.Repo ?? "").Trim(), ct);
        var chosen = repo.Models.Where(m => (body.Models ?? []).Contains(m.Name)).ToList();
        var files = repo.Weights.Where(f => (body.Files ?? []).Contains(f.Path)).ToList();
        if (chosen.Count + files.Count == 0 || chosen.Count != (body.Models ?? []).Distinct().Count() || files.Count != (body.Files ?? []).Distinct().Count())
        {
            return AuthEndpoints.Problem(400, "models", "Choose models or files this repository has (by name or path).");
        }
        var d = new ModelDownload
        {
            Repo = repo.Id, Revision = repo.Sha, Dir = repo.Id, CreatedBy = p.Identity?.Name ?? "admin",
            Files = [.. chosen.SelectMany(m => m.Files).Concat(files).DistinctBy(f => f.Path).Select(f => new DownloadFile { Path = f.Path, Size = f.Size, Sha256 = f.Sha256 })],
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
        await audit.WriteAsync("model.download", repo.Id, detail: $"{string.Join(", ", chosen.Select(m => m.Name).Concat(files.Select(f => f.Path)))} ({Gb(d.Total)})");
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

    /// <summary>The models the speech server can fetch (its registry), of a task: text-to-speech or automatic-speech-recognition.</summary>
    private static async Task<IResult> SpeechRegistryAsync(string? task, IHttpClientFactory http, CancellationToken ct)
    {
        try
        {
            var url = $"{MediaModels.AudioUrl}/v1/registry" + (task is { Length: > 0 } t ? $"?task={Uri.EscapeDataString(t)}" : "");
            var list = await http.CreateClient(MediaControl.Client).GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>(url, ct);
            var have = await http.CreateClient(MediaControl.Client).GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>($"{MediaModels.AudioUrl}/v1/models", ct);
            var present = (have?["data"] as System.Text.Json.Nodes.JsonArray ?? []).Select(m => m?["id"]?.ToString()).OfType<string>().ToHashSet(StringComparer.Ordinal);
            return Results.Ok((list?["data"] as System.Text.Json.Nodes.JsonArray ?? []).OfType<System.Text.Json.Nodes.JsonObject>().Select(m => new
            {
                id = m["id"]?.ToString(), task = m["task"]?.ToString(),
                language = (m["language"] as System.Text.Json.Nodes.JsonArray)?.Select(x => x?.ToString()).OfType<string>().ToList(),
                downloaded = present.Contains(m["id"]?.ToString() ?? ""),
            }).Where(m => m.id is not null));
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            return AuthEndpoints.Problem(503, "speech", "The speech server did not answer: its registry cannot be listed now.");
        }
    }

    /// <summary>Asks the speech server to fetch a model: a download like the others (Admin -> Models -> Downloads), one at a time.</summary>
    private static async Task<IResult> SpeechDownloadAsync(SpeechDownloadRequest body, ClaimsPrincipal p, AppDbContext db, ModelDownloads downloads, Audit audit, CancellationToken ct)
    {
        var id = (body.Id ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9][\w.-]*/[\w.-]+$"))
        {
            return AuthEndpoints.Problem(400, "id", "A speech model's id is owner/name, as the speech server's registry lists it.");
        }
        if (await db.ModelDownloads.AnyAsync(d => d.Kind == "speech" && d.Repo == id && d.State != "done" && d.State != "failed", ct))
        {
            return AuthEndpoints.Problem(409, "exists", "This model has a download already.");
        }
        var d = new ModelDownload { Kind = "speech", Repo = id, Revision = "", Dir = "", CreatedBy = p.Identity?.Name ?? "admin" };
        db.ModelDownloads.Add(d);
        await db.SaveChangesAsync(ct);
        downloads.Wake();
        await audit.WriteAsync("model.download", id, detail: "speech server");
        return Results.Created($"/api/admin/models/hf/downloads/{d.Id}", View(d, downloads));
    }

    private static string Gb(long bytes) => $"{bytes / 1073741824.0:0.#} GB";
}
