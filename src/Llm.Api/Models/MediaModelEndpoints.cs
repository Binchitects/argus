using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

public sealed record MediaFileRequest(string Role, string Path);

/// <summary>A picture, video or speech model as Admin -> Models adds or changes it; on a change, what is left out stays, and <see cref="Clear"/> empties fields.</summary>
public sealed record MediaModelRequest(
    string? Name = null, string? Mode = null, List<MediaFileRequest>? Files = null, string? SpeechId = null,
    int? Steps = null, double? CfgScale = null, double? Guidance = null, double? FlowShift = null, string? Sampler = null, string? Scheduler = null,
    string? NegativePrompt = null, int? Width = null, int? Height = null, int? Fps = null, int? MaxSeconds = null,
    List<string>? Voices = null, string? DefaultVoice = null, double? Speed = null, string? Language = null,
    int? Gpu = null, decimal? MaxVramGb = null, string? Vae = null, decimal? VaeGpuGb = null, bool? OffloadToCpu = null, bool? VaeTiling = null,
    bool? FlashAttention = null, string? ExtraArgs = null, bool? Kept = null, int? IdleMinutes = null, decimal? UnitPrice = null, List<string>? Clear = null);

/// <summary>Adding and changing picture, video and speech models (Admin -> Models), and what to pick their files and ids from.</summary>
public static partial class MediaModelEndpoints
{
    public static void Map(RouteGroupBuilder g)
    {
        g.MapPost("/media", AddAsync);
        g.MapPatch("/media/{name}", UpdateAsync);
        g.MapGet("/media/files", Files);
        g.MapGet("/media/speech", SpeechAsync);
    }

    /// <summary>The files a picture or video model can read: stable-diffusion.cpp's formats.</summary>
    private static readonly string[] Extensions = [".gguf", ".safetensors", ".ckpt", ".pth", ".pt", ".bin"];

    /// <summary>
    /// sd-server's options (its --help, b167b94) an admin may add: not the ones the app sets from the model's files and
    /// settings, nor those that read or write other files, listen elsewhere, or reach other machines.
    /// </summary>
    public static readonly IReadOnlySet<string> ExtraOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "--attn-scale", "--auto-fit", "--batch-count", "--cache-mode", "--cache-option", "--circular", "--circularx", "--circulary", "--clip-on-cpu",
        "--clip-skip", "--color", "--conditioning-cache-size", "--control-net-cpu", "--control-strength", "--diffusion-conv-direct", "--disable-image-metadata",
        "--disable-prefetch", "--disable-segmented-compute", "--eager-load", "--eta", "--fa", "--force-sdxl-vae-conv-scale", "--fps", "--height",
        "--high-noise-cfg-scale", "--high-noise-eta", "--high-noise-guidance", "--high-noise-img-cfg-scale", "--high-noise-sampling-method",
        "--high-noise-skip-layer-end", "--high-noise-skip-layers", "--high-noise-skip-layer-start", "--high-noise-slg-scale", "--high-noise-steps",
        "--hires", "--hires-denoising-strength", "--hires-height", "--hires-scale", "--hires-steps", "--hires-upscale-tile-size", "--hires-width",
        "--img-cfg-scale", "--increase-ref-index", "--linear-scale", "--log-level", "--lora-apply-mode", "--mmap", "--moe-boundary", "--negative-prompt",
        "--prediction", "--rng", "--sage-attn", "--sampler-rng", "--scm-policy", "--seed", "--sigmas", "--skip-layer-end", "--skip-layers",
        "--skip-layer-start", "--slg-scale", "--split-mode", "--strength", "--temporal-tiling", "--threads", "--timestep-shift", "--type",
        "--upscale-repeats", "--upscale-tile-size", "--vace-strength", "--vae-conv-direct", "--vae-format", "--vae-relative-tile-size",
        "--vae-tile-overlap", "--vae-tile-size", "--verbose", "--video-frames", "--width",
    };

    /// <summary>The options the model's own fields set (said so when one is given among its extra options).</summary>
    private static readonly Dictionary<string, string> Managed = new(StringComparer.Ordinal)
    {
        ["--max-vram"] = "GPU budget", ["--offload-to-cpu"] = "Weights in RAM", ["--diffusion-fa"] = "Flash attention", ["--vae-tiling"] = "Decode in tiles",
        ["--vae-on-cpu"] = "Decode on", ["--steps"] = "Steps", ["--cfg-scale"] = "CFG scale", ["--guidance"] = "Guidance", ["--sampling-method"] = "Sampler",
        ["--scheduler"] = "Scheduler", ["--flow-shift"] = "Flow shift",
    };

    [GeneratedRegex(@"^[A-Za-z0-9._+-]+(/[A-Za-z0-9._+-]+)*$")]
    private static partial Regex SafePath();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex RepoId();

    private static IResult Bad(string field, string message) => AuthEndpoints.Problem(400, field, message);

    /// <summary>The request onto the row: null when it is good, else why not (the field and what is wrong).</summary>
    internal static IResult? Apply(MediaModel m, MediaModelRequest r, string library)
    {
        var clear = (r.Clear ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        T? Pick<T>(string field, T? given, T? now) where T : struct => clear.Contains(field) ? null : given ?? now;
        string? Text(string field, string? given, string? now) => clear.Contains(field) ? null : given is null ? now : given.Trim() is { Length: > 0 } t ? t : null;

        var pictures = m.Mode is MediaModes.Image or MediaModes.Video;
        if (pictures)
        {
            if (r.Files is { } files)
            {
                if (files.Count == 0)
                {
                    return Bad("files", "A picture or video model needs its files: at least its diffusion model.");
                }
                foreach (var f in files)
                {
                    var path = (f.Path ?? "").Trim().TrimStart('/');
                    if (!SdArgs.RoleFlags.ContainsKey(f.Role ?? ""))
                    {
                        return Bad("files", $"{f.Role} is not a file's role: {string.Join(", ", SdArgs.RoleFlags.Keys)}.");
                    }
                    if (!SafePath().IsMatch(path) || path.Split('/').Any(p => p is "." or ".."))
                    {
                        return Bad("files", $"{f.Path}: a path in the library, of letters, digits and . _ + - /.");
                    }
                    if (!Extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    {
                        return Bad("files", $"{f.Path}: stable-diffusion.cpp reads {string.Join(", ", Extensions)} files.");
                    }
                }
                if (files.GroupBy(f => f.Role).FirstOrDefault(g => g.Count() > 1) is { } twice)
                {
                    return Bad("files", $"Two files have the role {twice.Key}: one each.");
                }
                if (!files.Any(f => f.Role is "diffusion" or "model"))
                {
                    return Bad("files", "One file must be its diffusion model (or the whole model).");
                }
                m.Files = [.. files.Select(f => new MediaModelFile { Role = f.Role, Path = f.Path.Trim().TrimStart('/') })];
            }
            m.Steps = Pick("steps", r.Steps, m.Steps);
            m.CfgScale = Pick("cfgScale", r.CfgScale, m.CfgScale);
            m.Guidance = Pick("guidance", r.Guidance, m.Guidance);
            m.FlowShift = Pick("flowShift", r.FlowShift, m.FlowShift);
            m.Sampler = Text("sampler", r.Sampler, m.Sampler);
            m.Scheduler = Text("scheduler", r.Scheduler, m.Scheduler);
            m.NegativePrompt = Text("negativePrompt", r.NegativePrompt, m.NegativePrompt);
            m.Width = Pick("width", r.Width, m.Width);
            m.Height = Pick("height", r.Height, m.Height);
            m.Gpu = Pick("gpu", r.Gpu, m.Gpu);
            m.MaxVramGb = Pick("maxVramGb", r.MaxVramGb, m.MaxVramGb);
            m.Vae = r.Vae ?? m.Vae;
            m.VaeGpuGb = Pick("vaeGpuGb", r.VaeGpuGb, m.VaeGpuGb);
            m.OffloadToCpu = r.OffloadToCpu ?? m.OffloadToCpu;
            m.VaeTiling = r.VaeTiling ?? m.VaeTiling;
            m.FlashAttention = r.FlashAttention ?? m.FlashAttention;
            m.ExtraArgs = Text("extraArgs", r.ExtraArgs, m.ExtraArgs);
            if (m.Mode == MediaModes.Video)
            {
                m.Fps = Pick("fps", r.Fps, m.Fps);
                m.MaxSeconds = Pick("maxSeconds", r.MaxSeconds, m.MaxSeconds);
            }
            if (m.Steps is < 1 or > 150)
            {
                return Bad("steps", "Steps: 1 to 150.");
            }
            if (m.CfgScale is < 0 or > 30 || m.Guidance is < 0 or > 30 || m.FlowShift is < 0 or > 30)
            {
                return Bad("cfgScale", "CFG scale, guidance and flow shift: 0 to 30.");
            }
            if (m.Width is { } w && (w is < 64 or > 4096 || w % 16 != 0) || m.Height is { } h && (h is < 64 or > 4096 || h % 16 != 0))
            {
                return Bad("width", "Width and height: 64 to 4096, a multiple of 16.");
            }
            if (m.Fps is < 1 or > 60 || m.MaxSeconds is < 1 or > 30)
            {
                return Bad("fps", "Frames a second: 1 to 60; the longest clip: 1 to 30 seconds.");
            }
            if (m.Gpu is < 0 or > 15)
            {
                return Bad("gpu", "The GPU, as nvidia-smi numbers them: 0 to 15.");
            }
            if (m.MaxVramGb is <= 0 or > 1024 || m.VaeGpuGb is <= 0 or > 1024)
            {
                return Bad("maxVramGb", "A GPU budget in GB, above 0.");
            }
            if (m.Vae is not ("gpu" or "cpu" or "auto"))
            {
                return Bad("vae", "Decode on: gpu, cpu, or auto (on the GPU when it has room as the model loads).");
            }
            if (m.Vae == "auto" && m.VaeGpuGb is null)
            {
                return Bad("vaeGpuGb", "Decoding where there is room needs the GPU memory decoding takes there.");
            }
            foreach (var line in (m.ExtraArgs ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var flag = line.Split(' ', 2)[0];
                if (Managed.TryGetValue(flag, out var field))
                {
                    return Bad("extraArgs", $"{flag} is the model's {field}: set it there.");
                }
                if (!ExtraOptions.Contains(flag))
                {
                    return Bad("extraArgs", $"{flag}: not an option of sd-server an admin may add (its files, where it listens and what it reaches are the app's).");
                }
            }
            if (m.ExtraArgs is { Length: > 4000 })
            {
                return Bad("extraArgs", "At most 4,000 characters of options.");
            }
        }
        else
        {
            m.SpeechId = Text("speechId", r.SpeechId, m.SpeechId);
            if (m.SpeechId is null || !RepoId().IsMatch(m.SpeechId))
            {
                return Bad("speechId", "The speech server's id for it: a Hugging Face repository (owner/name), as the speech server lists it.");
            }
            m.Language = Text("language", r.Language, m.Language);
            if (m.Mode == MediaModes.Speech)
            {
                if (r.Voices is { } voices)
                {
                    m.Voices = [.. voices.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal)];
                }
                m.DefaultVoice = Text("defaultVoice", r.DefaultVoice, m.DefaultVoice);
                m.Speed = Pick("speed", r.Speed, m.Speed);
                if (m.Speed is < VoiceCatalog.Slowest or > VoiceCatalog.Fastest)
                {
                    return Bad("speed", $"Speed: {VoiceCatalog.Slowest.ToString(CultureInfo.InvariantCulture)} to {VoiceCatalog.Fastest.ToString(CultureInfo.InvariantCulture)}.");
                }
            }
        }
        m.IdleMinutes = Pick("idleMinutes", r.IdleMinutes, m.IdleMinutes);
        m.UnitPrice = Pick("unitPrice", r.UnitPrice, m.UnitPrice);
        if (m.IdleMinutes is < 1 or > 1440)
        {
            return Bad("idleMinutes", "Unloaded after 1 to 1,440 minutes unused.");
        }
        if (m.UnitPrice is < 0)
        {
            return Bad("unitPrice", "Prices cannot be negative.");
        }
        if (r.Kept is { } kept)
        {
            m.Kept = kept;
        }
        return null;
    }

    private static async Task<IResult> AddAsync(MediaModelRequest body, AppDbContext db, ChatModels gatewayModels, MediaControl media, ModelCatalog catalog,
        IOptions<EngineOptions> engine, Audit audit, CancellationToken ct)
    {
        var name = (body.Name ?? "").Trim();
        if (ModelCatalog.CheckName(name) is { } bad)
        {
            return Bad("name", bad);
        }
        if (body.Mode is not { } mode || !MediaModes.All.Contains(mode))
        {
            return Bad("mode", $"What it does: {string.Join(", ", MediaModes.All)}.");
        }
        var taken = (await gatewayModels.AllAsync(ct)).Select(m => m.Name)
            .Concat(await db.LocalModels.Select(m => m.Name).ToListAsync(ct)).Concat(await db.MediaModels.Select(m => m.Name).ToListAsync(ct));
        if (taken.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return AuthEndpoints.Problem(409, "exists", $"There is already a model named {name}.");
        }
        var model = new MediaModel { Name = name, Mode = mode };
        if (Apply(model, body with { Files = body.Files ?? [] }, engine.Value.LibraryDir) is { } problem)
        {
            return problem;
        }
        db.MediaModels.Add(model);
        await db.SaveChangesAsync(ct);
        if (model.Kept)
        {
            await media.SetKeptAsync(name, true, ct);
        }
        await AfterAsync(media, catalog, ct);
        await audit.WriteAsync("model.add", name, detail: mode);
        return Results.Created($"/api/admin/models/{name}", new { restarting = false, warning = Missing(model, engine.Value.LibraryDir) });
    }

    private static async Task<IResult> UpdateAsync(string name, MediaModelRequest body, AppDbContext db, MediaControl media, ModelCatalog catalog,
        IOptions<EngineOptions> engine, Audit audit, CancellationToken ct)
    {
        if (await db.MediaModels.SingleOrDefaultAsync(m => m.Name == name, ct) is not { } model)
        {
            return Results.NotFound();
        }
        var kept = model.Kept;
        if (Apply(model, body, engine.Value.LibraryDir) is { } problem)
        {
            return problem;
        }
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (model.Kept && !kept)
        {
            await media.SetKeptAsync(name, true, ct);
        }
        // Changed by hand: whatever failed before is tried again with the new settings.
        media.Forgive(name);
        await AfterAsync(media, catalog, ct);
        await audit.WriteAsync("model.edit", name);
        return Results.Ok(new { restarting = false, warning = Missing(model, engine.Value.LibraryDir) });
    }

    private static async Task AfterAsync(MediaControl media, ModelCatalog catalog, CancellationToken ct)
    {
        await media.ChangedAsync(ct);
        try
        {
            await catalog.SyncGatewayAsync(ct);
        }
        catch (GatewayException)
        {
            // In step within a minute (the engine watcher).
        }
    }

    /// <summary>Its files not in the library yet: it waits for them (a download, a copy), and says so.</summary>
    private static string? Missing(MediaModel m, string library)
    {
        var missing = m.Files.Where(f => !File.Exists(Path.Combine(library, f.Path))).Select(f => f.Path).ToList();
        return missing.Count == 0 ? null : $"Not in the library yet: {string.Join(", ", missing)}. It waits for them.";
    }

    /// <summary>A file in the library a picture or video model may read: its path, size, the role its name suggests, and the models that use it.</summary>
    public sealed record LibraryFileRow(string Path, long Size, string Role, IReadOnlyList<string> UsedBy);

    private static async Task<IResult> Files(IOptions<EngineOptions> engine, AppDbContext db, CancellationToken ct)
    {
        var root = engine.Value.LibraryDir;
        if (!Directory.Exists(root))
        {
            return Results.Ok(Array.Empty<LibraryFileRow>());
        }
        var media = await db.MediaModels.AsNoTracking().ToListAsync(ct);
        var local = await db.LocalModels.AsNoTracking().ToListAsync(ct);
        var rows = new List<LibraryFileRow>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            if (!Extensions.Any(e => file.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!SafePath().IsMatch(path))
            {
                continue;
            }
            var used = media.Where(m => m.Files.Any(f => f.Path == path)).Select(m => m.Name)
                .Concat(local.Where(m => m.File == path || m.Projector == path || m.DraftHead == path).Select(m => m.Name)).ToList();
            rows.Add(new LibraryFileRow(path, new FileInfo(file).Length, RoleOf(path), used));
            if (rows.Count >= 2000)
            {
                break;
            }
        }
        return Results.Ok(rows.OrderBy(r => r.Path, StringComparer.Ordinal));
    }

    /// <summary>The role a file's name suggests (an admin picks: this is a first guess).</summary>
    public static string RoleOf(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name switch
        {
            _ when name.Contains("vae", StringComparison.Ordinal) => "vae",
            _ when name.Contains("taesd", StringComparison.Ordinal) || name.StartsWith("tae", StringComparison.Ordinal) => "taesd",
            _ when name.Contains("t5", StringComparison.Ordinal) || name.Contains("umt5", StringComparison.Ordinal) => "t5xxl",
            _ when name.Contains("clip_l", StringComparison.Ordinal) || name.Contains("clip-l", StringComparison.Ordinal) => "clip_l",
            _ when name.Contains("clip_g", StringComparison.Ordinal) || name.Contains("clip-g", StringComparison.Ordinal) => "clip_g",
            _ when name.Contains("clip_vision", StringComparison.Ordinal) => "clip_vision",
            _ when name.Contains("high_noise", StringComparison.Ordinal) || name.Contains("high-noise", StringComparison.Ordinal) => "high_noise",
            _ when name.Contains("qwen", StringComparison.Ordinal) || name.Contains("mistral", StringComparison.Ordinal) || name.Contains("gemma", StringComparison.Ordinal)
                || name.Contains("llama", StringComparison.Ordinal) => "llm",
            _ => "diffusion",
        };
    }

    /// <summary>The models the speech server has (downloaded), by task: what a speech model's id is picked from.</summary>
    private static async Task<IResult> SpeechAsync(IHttpClientFactory http, CancellationToken ct)
    {
        try
        {
            var list = await http.CreateClient(MediaControl.Client).GetFromJsonAsync<JsonObject>($"{MediaModels.AudioUrl}/v1/models", ct);
            var rows = (list?["data"] as JsonArray ?? []).OfType<JsonObject>().Select(m => new
            {
                id = m["id"]?.ToString(),
                task = m["task"]?.ToString(),
                language = (m["language"] as JsonArray)?.Select(x => x?.ToString()).OfType<string>().ToList(),
                voices = (m["voices"] as JsonArray)?.OfType<JsonObject>().Select(v => v["name"]?.ToString()).OfType<string>().ToList(),
            }).Where(m => m.id is not null);
            return Results.Ok(rows);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return AuthEndpoints.Problem(503, "speech", "The speech server did not answer: its models cannot be listed now.");
        }
    }
}
