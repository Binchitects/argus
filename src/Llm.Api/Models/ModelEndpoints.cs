using System.Globalization;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Api.Operations;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

public sealed record LocalModelRequest(
    string? Name, string? File, string? Projector, int? Context, int? MaxOutput, string? Placement, int? GpuLayers, int? CpuMoe, string? KvType, int? Parallel,
    int? Ubatch, bool? Mtp, string? DraftHead, int? DraftMax, bool? Yarn, double? Temperature, double? TopP, int? TopK, double? MinP, double? PresencePenalty,
    string? ExtraPreset, bool? Thinking, bool? Tools, decimal? InputPerMtok, decimal? OutputPerMtok, string[]? Clear = null, string? Devices = null,
    decimal? CachedInputPerMtok = null, string? EngineKind = null, string? Engine = null, int? Replicas = null, double? GpuMemoryUtilization = null,
    string? Quantization = null, string? SpecDecode = null, int? KvCpuGb = null, bool? Sleep = null, string? Fallback = null)
{
    /// <summary>A value to empty: a missing one is left as it is.</summary>
    public bool Clears(string field) => Clear?.Contains(field, StringComparer.OrdinalIgnoreCase) == true;
}

public sealed record ModelAccessRequest(Audience Audience, Guid[]? Groups);

public sealed record KeepRequest(bool Keep);

/// <summary>A picture, video or speech model on (at the gateway, in the chat's tools) or off.</summary>

/// <summary>
/// Admin → Models: every model at the gateway; for the engine's, which to keep
/// loaded, loading and unloading them (live: llama.cpp's router), the GPUs each
/// runs on, and adding more from the library; and for each, who may use it.
/// </summary>
public static class ModelEndpoints
{
    public static void MapModels(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/models").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        MediaModelEndpoints.Map(g);
        g.MapGet("/library", LibraryAsync);
        g.MapGet("/library/snapshots", SnapshotsAsync);
        g.MapPost("/advice", AdviceAsync);
        g.MapPost("", AddAsync);
        g.MapPatch("/{name}", UpdateAsync);
        g.MapDelete("/{name}", RemoveAsync);
        g.MapPost("/{name}/load", LoadAsync);
        g.MapPost("/{name}/unload", UnloadAsync);
        g.MapPut("/{name}/keep", KeepAsync);
        g.MapPut("/{name}/access", AccessAsync);
    }

    private static async Task<IResult> ListAsync(AppDbContext db, ChatModels gatewayModels, EngineState state, ModelCatalog catalog, ModelLibrary library,
        HardwareProbe hardware, ModelHoursState hours, IOptions<EngineOptions> engine, MediaControl media, SmallModel small, PriceBook prices, ModelPolicy policy, GpuShare gpu,
        CancellationToken ct)
    {
        var e = engine.Value;
        // What each costs: its own prices, else the defaults (Settings → Prices); own says which it sets.
        var defaults = prices.Defaults;
        object Price(decimal? input, decimal? cached, decimal? output)
        {
            var p = TokenPrice.Of(input, cached, output, defaults);
            return new { p.Input, cachedInput = p.CachedInput, p.Output, own = new { input = input is not null, cachedInput = cached is not null, output = output is not null } };
        }
        object MediaPrice(MediaControl.Model m) => m.Mode switch
        {
            MediaModes.Image => new { unit = "picture", amount = m.Row.UnitPrice ?? defaults.PerImage, own = m.Row.UnitPrice is not null },
            MediaModes.Video => new { unit = "second of video", amount = m.Row.UnitPrice ?? defaults.PerVideoSecond, own = m.Row.UnitPrice is not null },
            MediaModes.Transcription => new { unit = "minute of sound", amount = m.Row.UnitPrice ?? defaults.PerAudioMinute, own = m.Row.UnitPrice is not null },
            _ => new { unit = "1,000 characters", amount = m.Row.UnitPrice ?? defaults.PerThousandCharacters, own = m.Row.UnitPrice is not null },
        };
        var files = e.Enabled ? library.List().ToDictionary(f => f.File.Path, f => f.Profile, StringComparer.Ordinal) : [];
        var local = await db.LocalModels.AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct);
        var rules = await db.ModelAccess.AsNoTracking().ToDictionaryAsync(r => r.Model, ct);
        var groups = await db.Groups.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var atGateway = await gatewayModels.AllAsync(ct);
        object Access(string model) => rules.TryGetValue(model, out var r)
            ? new { r.Audience, groups = r.Groups.Where(groups.ContainsKey).Select(x => new { id = x, name = groups[x] }) }
            : new { Audience = Audience.Everyone, groups = Enumerable.Empty<object>() };
        GatewayModel? At(string name) => atGateway.FirstOrDefault(m => m.Name == name);
        var now = state.Now;
        // Why a model is on its way in or out: what the app recorded, else (the router's own loads at its start) kept loaded.
        string? Why(string name) => state.StatusOf(name) is "loading" or "unloading"
            ? state.Reason(name)?.Why ?? (catalog.Kept().Contains(name) && state.StatusOf(name) == "loading" ? "kept loaded" : null)
            : null;
        var hoursNow = hours.Now;
        // Kept now (the working hours' in force, else the pinned), and the pinned ones (each model's switch).
        var kept = e.Enabled ? catalog.Kept() : [];
        var pinned = e.Enabled ? catalog.Pinned() : [];
        var hw = e.Enabled ? await hardware.GetAsync(ct) : null;
        var servers = await db.RemoteServers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var onServers = servers.SelectMany(s => s.Models).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var rows = new List<object>();
        foreach (var m in local)
        {
            rows.Add(new
            {
                name = m.Name, source = "local", mode = "chat",
                // Not in the engine's list although it answered: not read yet (it restarts), or its preset refused.
                status = state.StatusOf(m.Name) ?? (now is { Error: null, At: not null } ? "missing" : null), kept = pinned.Contains(m.Name), keptNow = kept.Contains(m.Name), m.Devices,
                // Failed to load: when it is tried again.
                retryAt = state.NextTry(m.Name),
                // Loading or unloading: why (an admin, a request, kept loaded, making room for another), and since when.
                why = Why(m.Name), since = state.Reason(m.Name)?.Since,
                // Loaded by an admin in place of a model that never makes room: it never makes room either until unloaded.
                instead = state.LoadedInstead.Contains(m.Name),
                // The engine unloaded it by its own choice this often lately (it holds fewer models than the app thinks).
                evicted = state.Evicted(m.Name),
                // What one conversation may hold: its slot's share of the context (the gateway says so too).
                file = m.File, m.Projector, context = m.Context, window = ModelCatalog.SlotWindow(m), m.MaxOutput, m.Placement, m.GpuLayers, m.CpuMoe, m.KvType, m.Parallel, m.Ubatch,
                m.Mtp, m.DraftHead, m.DraftMax, m.Yarn, m.Temperature, m.TopP, m.TopK, m.MinP, m.PresencePenalty,
                m.ExtraPreset, m.Thinking, m.Tools, m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, price = Price(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok),
                vision = m.Projector is { Length: > 0 }, atGateway = At(m.Name) is not null, access = Access(m.Name),
                profile = files.GetValueOrDefault(m.File),
                // What its token cache keeps, and the RAM it takes.
                cache = TokenCache.Of(m, files.GetValueOrDefault(m.File), onServers.Contains(m.Name), engine.Value.SessionCacheGb),
                // Its engine, and what only some kinds have (EngineKinds).
                engineKind = m.EngineKind, m.Engine, m.Replicas, m.GpuMemoryUtilization, m.Quantization, m.SpecDecode, m.KvCpuGb, m.Sleep, m.Fallback,
            });
        }
        foreach (var s in servers)
        {
            foreach (var m in s.Models)
            {
                rows.Add(new
                {
                    name = m.Name, source = "remote", server = s.Name, remote = m.Remote, mode = "chat", status = (string?)null, context = m.Context,
                    maxOutput = m.MaxOutput, vision = m.Vision, atGateway = At(m.Name) is not null, access = Access(m.Name),
                    price = Price(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok),
                });
            }
        }
        // The picture, video and speech models, each on its own server.
        foreach (var x in await media.ListAsync(ct))
        {
            rows.Add(new
            {
                name = x.Model.Name, source = "media", mode = x.Model.Mode, server = x.Model.Server, status = x.Now, kept = x.Kept,
                why = x.Why, since = x.Since, retryAt = x.Failure?.RetryAt,
                failure = x.Failure is { } f ? new { kind = f.Class, detail = f.Detail, tries = f.Count, since = f.Since } : null,
                keptNow = x.Kept, vision = false, atGateway = At(x.Model.Name) is not null, access = Access(x.Model.Name),
                unitPrice = MediaPrice(x.Model), idleMinutes = x.Model.Row.IdleMinutes,
                files = x.Model.Row.Files.Select(f => new { role = f.Role, path = f.Path, present = File.Exists(Path.Combine(engine.Value.LibraryDir, f.Path)) }),
                speechId = x.Model.Id,
                settings = new
                {
                    x.Model.Row.Steps, x.Model.Row.CfgScale, x.Model.Row.Guidance, x.Model.Row.FlowShift, x.Model.Row.Sampler, x.Model.Row.Scheduler,
                    x.Model.Row.NegativePrompt, x.Model.Row.Width, x.Model.Row.Height, x.Model.Row.Fps, x.Model.Row.MaxSeconds, x.Model.Row.Voices,
                    x.Model.Row.DefaultVoice, x.Model.Row.Speed, x.Model.Row.Language, x.Model.Row.Gpu, x.Model.Row.MaxVramGb, x.Model.Row.Vae,
                    x.Model.Row.VaeGpuGb, x.Model.Row.OffloadToCpu, x.Model.Row.VaeTiling, x.Model.Row.FlashAttention, x.Model.Row.ExtraArgs,
                },
            });
        }
        foreach (var m in atGateway.Where(m => local.All(l => l.Name != m.Name) && !onServers.Contains(m.Name) && media.Find(m.Name) is null))
        {
            rows.Add(new { name = m.Name, source = "gateway", mode = m.Mode, status = (string?)null, context = m.Context, vision = m.Vision, access = Access(m.Name) });
        }
        return Results.Ok(new
        {
            engine = new
            {
                enabled = e.Enabled, error = now.Error, checkedAt = now.At, kept, pinned, max = e.ModelsMax,
                hours = hoursNow.Window is { } window ? new { window.Id, window.Name, until = hoursNow.Until } : null,
                // A place left besides the kept models and those that never make room: the others load when asked for.
                onRequest = e.Enabled && policy.PlaceLeft,
                loaded = now.Models.Where(m => m.Status == "loaded").Select(m => m.Name),
                loading = now.Models.Where(m => m.Status == "loading").Select(m => m.Name),
                unloading = now.Models.Where(m => m.Status == "unloading").Select(m => m.Name),
                gpus = hw?.Devices?.Select(g => new { g.Index, g.Name, g.Total, g.Vendor, g.Backend }) ?? [],
                plan = e.Enabled ? Plan(kept, local, e, library, hw) : null,
                // The kinds of engine and what each can do: the form offers a model only its kind's settings.
                kinds = EngineKinds.All,
            },
            // The model for sub-agents and small steps (Settings → Model), and what keeps it from doing them well.
            small = small.Name is { } name ? new { name, warning = await small.WarningAsync(ct) } : null,
            // The GPU lent to a picture or video job now, and what the jobs did lately (room made, and given back).
            gpu = new
            {
                lent = state.Lent is { } lent ? new { media = lent.Media, kind = lent.Kind, since = lent.Since } : null,
                recent = gpu.Recent.Reverse().Select(h => new { at = h.At, text = h.Text }),
            },
            models = rows,
        });
    }

    private static async Task<IResult> LibraryAsync(ModelLibrary library, AppDbContext db, IOptions<EngineOptions> engine, CancellationToken ct)
    {
        var used = await db.LocalModels.AsNoTracking().Select(m => new { m.Name, m.File, m.Projector, m.DraftHead }).ToListAsync(ct);
        var media = await db.MediaModels.AsNoTracking().ToListAsync(ct);
        var e = engine.Value;
        return Results.Ok(library.List().Select(f => new
        {
            f.File.Path, f.File.Size, f.File.Parts, f.Profile,
            // Chat models and picture or video models alike (a text encoder is often both).
            usedBy = used.Where(u => u.File == f.File.Path || u.Projector == f.File.Path || u.DraftHead == f.File.Path).Select(u => u.Name)
                .Concat(media.Where(m => m.Files.Any(x => x.Path == f.File.Path)).Select(m => m.Name)),
        }));
    }

    /// <summary>The Hugging Face snapshots in the library, for vLLM models: each with its size, whether it is whole, and the models using it.</summary>
    private static async Task<IResult> SnapshotsAsync(ModelLibrary library, AppDbContext db, CancellationToken ct)
    {
        var used = await db.LocalModels.AsNoTracking().Where(m => m.EngineKind == EngineKinds.Vllm).Select(m => new { m.Name, m.File, m.DraftHead }).ToListAsync(ct);
        return Results.Ok(library.Snapshots().Select(s => new
        {
            s.Path, s.Repo, s.Revision, s.Size, s.Complete, s.Why, s.Architectures,
            usedBy = used.Where(u => u.File == s.Path || u.DraftHead == s.Path).Select(u => u.Name),
        }));
    }

    /// <summary>What the form shows as it is filled in: the file's profile, the limits of its kind, recommended settings and a memory estimate.</summary>
    private static async Task<IResult> AdviceAsync(LocalModelRequest body, ModelLibrary library, HardwareProbe hardware, AppDbContext db, CancellationToken ct)
    {
        var model = body.Name is { Length: > 0 } name && await db.LocalModels.AsNoTracking().SingleOrDefaultAsync(m => m.Name == name, ct) is { } saved
            ? saved
            : new LocalModel { Name = "draft", File = "" };
        var file = (body.File ?? model.File).Trim();
        if (library.Find(file) is not { } entry)
        {
            return AuthEndpoints.Problem(400, "file", "Choose a GGUF file from the model library.");
        }
        Merge(model, body, file);
        return Results.Ok(ModelAdvisor.Advise(model, entry, library.List(), await hardware.GetAsync(ct)));
    }

    private static async Task<IResult> AddAsync(LocalModelRequest body, AppDbContext db, ModelCatalog catalog, ModelLibrary library, HardwareProbe hardware,
        ChatModels gatewayModels, IOptions<EngineOptions> engine, EngineWatcher watcher, PriceBook prices, Audit audit, CancellationToken ct)
    {
        if (!engine.Value.Enabled)
        {
            return AuthEndpoints.Problem(400, "engine", "Models can be added only with the llama.cpp engine (the llamacpp profile).");
        }
        var name = (body.Name ?? "").Trim();
        if (ModelCatalog.CheckName(name) is { } bad)
        {
            return AuthEndpoints.Problem(400, "name", bad);
        }
        var taken = (await gatewayModels.AllAsync(ct)).Select(m => m.Name)
            .Concat(await db.LocalModels.Select(m => m.Name).ToListAsync(ct));
        if (taken.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return AuthEndpoints.Problem(409, "exists", $"There is already a model named {name}.");
        }
        var model = new LocalModel { Name = name, File = "" };
        var (problem, warning) = Apply(model, body, library, await hardware.GetAsync(ct), prices.Defaults, [.. taken]);
        if (problem is not null)
        {
            return problem;
        }
        db.LocalModels.Add(model);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("model.add", name, detail: model.File);
        return Results.Created($"/api/admin/models/{name}", await AfterChangeAsync(catalog, watcher, warning, ct));
    }

    private static async Task<IResult> UpdateAsync(string name, LocalModelRequest body, AppDbContext db, ModelCatalog catalog, ModelLibrary library, HardwareProbe hardware,
        EngineWatcher watcher, PriceBook prices, ChatModels gatewayModels, Audit audit, CancellationToken ct)
    {
        if (await db.LocalModels.SingleOrDefaultAsync(m => m.Name == name, ct) is not { } model)
        {
            return Results.NotFound();
        }
        var (problem, warning) = Apply(model, body, library, await hardware.GetAsync(ct), prices.Defaults,
            [.. (await gatewayModels.AllAsync(ct)).Select(x => x.Name), .. await db.LocalModels.Select(x => x.Name).ToListAsync(ct)]);
        if (problem is not null)
        {
            return problem;
        }
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("model.update", name);
        return Results.Ok(await AfterChangeAsync(catalog, watcher, warning, ct));
    }

    private static async Task<IResult> RemoveAsync(string name, AppDbContext db, ModelCatalog catalog, IOptions<EngineOptions> engine, EngineWatcher watcher,
        MediaControl media, Audit audit, CancellationToken ct)
    {
        // A picture, video or speech model: unloaded, then gone (its files stay in the library, as a chat model's).
        if (await db.MediaModels.SingleOrDefaultAsync(m => m.Name == name, ct) is { } row)
        {
            await media.UnloadAsync(name, ct);
            db.MediaModels.Remove(row);
            await db.ModelAccess.Where(a => a.Model == name).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            await media.ChangedAsync(ct);
            try
            {
                await catalog.SyncGatewayAsync(ct);
            }
            catch (GatewayException)
            {
                // In step within a minute (the engine watcher).
            }
            await audit.WriteAsync("model.remove", name);
            return Results.Ok(new { restarting = false, warning = (string?)null });
        }
        if (await db.LocalModels.SingleOrDefaultAsync(m => m.Name == name, ct) is not { } model)
        {
            return Results.NotFound();
        }
        db.LocalModels.Remove(model);
        await db.ModelAccess.Where(a => a.Model == name).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        if (catalog.Pinned().Contains(name))
        {
            catalog.SetKept(catalog.Pinned().Where(k => k != name));
        }
        // Working hours that kept it, or started chats on it, do without it.
        foreach (var w in await db.ModelWindows.Where(w => w.Keep.Contains(name) || w.DefaultModel == name).ToListAsync(ct))
        {
            w.Keep = [.. w.Keep.Where(k => k != name)];
            w.DefaultModel = w.DefaultModel == name ? null : w.DefaultModel;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("model.remove", name);
        return Results.Ok(await AfterChangeAsync(catalog, watcher, null, ct));
    }

    /// <summary>The engine restarts on new presets; the gateway learns of the change. A gateway that is down is tried again later.</summary>
    private static async Task<object> AfterChangeAsync(ModelCatalog catalog, EngineWatcher watcher, string? warning, CancellationToken ct)
    {
        await catalog.WritePresetsAsync(ct);
        watcher.Wake();
        try
        {
            await catalog.SyncGatewayAsync(ct);
            return new { restarting = true, warning };
        }
        catch (GatewayException ex)
        {
            return new { restarting = true, warning = $"Saved, but the gateway could not be updated yet ({ex.Message}); it is tried again within a minute." };
        }
    }

    /// <summary>
    /// Loads a model now, beside the kept ones. At the engine's limit the app makes room (never the engine, whose choice of
    /// the model used least recently may be the one everyone is on, which the watcher would then load back): an idle model
    /// that may make room unloads first. When every place is held by models that never make room, the admin is told which,
    /// and <c>?instead=true</c> loads it in place of one that is not kept (it then never makes room either, until unloaded).
    /// </summary>
    private static async Task<IResult> LoadAsync(string name, bool? instead, HttpContext http, EngineClient engine, EngineState state, EngineRoute route, ModelCatalog catalog,
        EngineWatcher watcher, ChatModels chatModels, IOptions<EngineOptions> options, MediaControl media, Audit audit, CancellationToken ct)
    {
        if (media.Find(name) is not null)
        {
            media.Load(name);
            await audit.WriteAsync("model.load", name);
            return Results.Accepted();
        }
        if (!await KnownAsync(name, engine, state, ct))
        {
            return AuthEndpoints.Problem(404, "unknown", $"The engine has no model named {name} (yet: a model just added is there once the engine has restarted).");
        }
        if (state.Lent is { } lent && state.StatusOf(name) is not ("loaded" or "loading"))
        {
            return AuthEndpoints.Problem(409, "lent", $"The GPU is making a {lent.Kind} with {lent.Media} just now (since {lent.Since:HH:mm} UTC). Load {name} once it is done: "
                + "the chat models unloaded for it load again by themselves then.");
        }
        var kept = catalog.Kept();
        if (!kept.Contains(name) && kept.Count >= options.Value.ModelsMax)
        {
            return AuthEndpoints.Problem(409, "full", FullMessage(kept.Count, options.Value.ModelsMax));
        }
        var room = await route.RoomForAdminAsync(name, kept, instead == true, ct);
        if (room.Refusal is { } refusal)
        {
            return room.Holding.Count > 0
                ? Results.Json(new { status = "held", error = refusal, holding = room.Holding }, statusCode: 409)
                : AuthEndpoints.Problem(409, "full", refusal);
        }
        if (state.StatusOf(name) == "failed")
        {
            // A try of a model that failed to load: should it fail again, the next try by itself waits longer.
            state.Tried(name);
        }
        var by = http.User.Identity?.Name is { Length: > 0 } who ? $"loaded by {who}" : "loaded by an admin";
        try
        {
            // Loaded again, though the app told it to unload a moment ago; followed up should the engine lose it.
            state.Loading(name, room.Replaced is { } r ? $"{by} instead of {r}" : by);
            state.Asked(name);
            if (room.Replaced is not null)
            {
                state.Instead(name);
            }
            await engine.LoadAsync(name, ct);
        }
        catch (EngineException ex) when (!ex.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            return AuthEndpoints.Problem(503, "engine", ex.Message);
        }
        chatModels.Forget();
        watcher.Wake();
        await audit.WriteAsync("model.load", room.Replaced is { } replaced ? $"{name} (instead of {replaced})" : name);
        return Results.Accepted();
    }

    private static async Task<IResult> UnloadAsync(string name, HttpContext http, EngineClient engine, EngineState state, EngineRoute route, ModelCatalog catalog, EngineWatcher watcher,
        ChatModels chatModels, ModelHoursState hours, MediaControl media, Audit audit, CancellationToken ct)
    {
        if (media.Find(name) is not null)
        {
            await media.UnloadAsync(name, ct);
            await audit.WriteAsync("model.unload", name);
            return Results.Accepted();
        }
        if (!await KnownAsync(name, engine, state, ct))
        {
            return Results.NotFound();
        }
        try
        {
            // Unloaded on purpose: not kept any more, or it would come straight back.
            if (hours.Now is { Window: { } window, Until: var until } && window.Keep.Contains(name))
            {
                return AuthEndpoints.Problem(409, "hours", $"The working hours \"{window.Name}\" keep {name} loaded{(until is { } u ? $" until {u:HH:mm}" : "")}. Change them under Models → Working hours.");
            }
            if (catalog.Pinned().Contains(name))
            {
                catalog.SetKept(catalog.Pinned().Where(k => k != name));
            }
            // Nor loaded again by the app as the model new chats use, nor as an admin's Load being followed up, nor held for one.
            state.Dropped(name);
            state.Forget(name);
            state.Release(name);
            // Unloading at once for every request (before the engine is told: a request let through meanwhile would reach a
            // model on its way out), though the engine lists it loaded until it has stopped.
            route.Unloaded(name, http.User.Identity?.Name is { Length: > 0 } who ? $"unloaded by {who}" : "unloaded by an admin");
            await engine.UnloadAsync(name, ct);
        }
        catch (EngineException ex)
        {
            state.NotUnloading(name);
            return AuthEndpoints.Problem(503, "engine", ex.Message);
        }
        chatModels.Forget();
        watcher.Wake();
        await audit.WriteAsync("model.unload", name);
        return Results.Accepted();
    }

    /// <summary>
    /// Keeps a model loaded (loaded now, and again after a restart or when a model loaded on
    /// request pushed it out), or stops keeping it. Refused when the engine's places are all
    /// kept already, or when the kept models would not fit the GPUs and RAM together.
    /// </summary>
    private static async Task<IResult> KeepAsync(string name, KeepRequest body, AppDbContext db, EngineClient engine, EngineState state, ModelCatalog catalog,
        ModelLibrary library, HardwareProbe hardware, EngineWatcher watcher, ChatModels chatModels, IOptions<EngineOptions> options, MediaControl media, Audit audit,
        CancellationToken ct)
    {
        if (media.Find(name) is not null)
        {
            await media.SetKeptAsync(name, body.Keep, ct);
            await audit.WriteAsync(body.Keep ? "model.keep" : "model.unkeep", name);
            return Results.Ok(new { kept = body.Keep, warning = (string?)null });
        }
        if (!await KnownAsync(name, engine, state, ct))
        {
            return AuthEndpoints.Problem(404, "unknown", $"The engine has no model named {name}.");
        }
        var e = options.Value;
        var kept = catalog.Pinned().ToList();
        string? warning = null;
        if (body.Keep && !kept.Contains(name))
        {
            if (kept.Count >= e.ModelsMax)
            {
                return AuthEndpoints.Problem(409, "full", $"The engine holds {e.ModelsMax} model{(e.ModelsMax == 1 ? "" : "s")} at once, and {(kept.Count == 1 ? "one is" : $"{kept.Count} are")} kept loaded already. Stop keeping one, or raise \"Models loaded at once\" under Settings.");
            }
            var plan = Plan([.. kept, name], await db.LocalModels.AsNoTracking().ToListAsync(ct), e, library, await hardware.GetAsync(ct));
            if (plan.FirstError is { } error)
            {
                return AuthEndpoints.Problem(409, "memory", error.Message);
            }
            warning = plan.Problems.Count > 0 ? plan.Problems[0].Message : null;
            kept.Add(name);
        }
        else if (!body.Keep)
        {
            kept.Remove(name);
        }
        catalog.SetKept(kept);
        chatModels.Forget();
        watcher.Wake();
        await audit.WriteAsync(body.Keep ? "model.keep" : "model.unkeep", name);
        return Results.Ok(new { kept, warning });
    }

    private static string FullMessage(int kept, int max) =>
        $"Every place in the engine ({max}) keeps a model loaded, so no other can load beside them. Stop keeping one, or raise \"Models loaded at once\" under Settings.";

    /// <summary>Whether these models, kept loaded together, fit the machine.</summary>
    internal static KeptPlan Plan(IReadOnlyList<string> kept, IReadOnlyList<LocalModel> local, EngineOptions e, ModelLibrary library, Hardware? hw)
    {
        var entries = library.List();
        var models = new List<(LocalModel, LibraryEntry?)>();
        foreach (var name in kept)
        {
            if (local.FirstOrDefault(m => m.Name == name) is { EngineKind: EngineKinds.LlamaCpp } m)
            {
                models.Add((m, library.Find(m.File)));
            }
        }
        return ModelAdvisor.PlanKept(models, entries, hw);
    }

    /// <summary>Whether the engine has the model: its last answer, or a fresh one when that did not know it.</summary>
    private static async Task<bool> KnownAsync(string name, EngineClient engine, EngineState state, CancellationToken ct)
    {
        if (state.StatusOf(name) is not null)
        {
            return true;
        }
        try
        {
            var asked = state.Asking();
            state.Set(await engine.ModelsAsync(ct), asked);
        }
        catch (EngineException ex)
        {
            state.Fail(ex.Message);
        }
        return state.StatusOf(name) is not null;
    }

    private static async Task<IResult> AccessAsync(string name, ModelAccessRequest body, AppDbContext db, ChatModels gatewayModels, IOptions<EngineOptions> engine,
        KeyAccessWatcher keys, Audit audit, CancellationToken ct)
    {
        var known = (await gatewayModels.AllAsync(ct)).Any(m => m.Name == name) || await db.MediaModels.AnyAsync(m => m.Name == name, ct) || await db.LocalModels.AnyAsync(m => m.Name == name, ct);
        if (!known)
        {
            return Results.NotFound();
        }
        var groups = (body.Groups ?? []).Distinct().ToList();
        if (body.Audience == Audience.Groups && groups.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Choose at least one group, or let everyone use it.");
        }
        if (await db.Groups.CountAsync(x => groups.Contains(x.Id), ct) != groups.Count)
        {
            return AuthEndpoints.Problem(400, "groups", "A group in the list does not exist.");
        }
        var rule = await db.ModelAccess.SingleOrDefaultAsync(a => a.Model == name, ct);
        if (rule is null)
        {
            rule = new ModelAccess { Model = name };
            db.ModelAccess.Add(rule);
        }
        rule.Audience = body.Audience;
        rule.Groups = body.Audience == Audience.Groups ? groups : [];
        rule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("model.access", name, detail: body.Audience.ToString().ToLowerInvariant());
        keys.Wake();
        return Results.NoContent();
    }

    /// <summary>
    /// Checks and applies a model's settings (the name is set by the caller): the file must be a
    /// language model, and every setting inside what that model and this machine allow. The
    /// warnings (slower, not wrong) come back to show.
    /// </summary>
    private static (IResult? Problem, string? Warning) Apply(LocalModel m, LocalModelRequest body, ModelLibrary library, Hardware? hardware, PriceOptions prices,
        IReadOnlyCollection<string> models)
    {
        var file = (body.File ?? m.File).Trim();
        var kind = EngineKinds.Of(body.EngineKind is { Length: > 0 } k0 ? k0.Trim() : m.EngineKind);
        if (kind is not { Managed: true })
        {
            return (AuthEndpoints.Problem(400, "engineKind", $"The engine is {string.Join(" or ", EngineKinds.All.Where(x => x.Managed).Select(x => x.Kind))}. "
                + "A server the app does not run is added under Other GPU servers."), null);
        }
        var fallback = body.Fallback ?? m.Fallback;
        if (fallback is { Length: > 0 } f && !body.Clears("fallback") && (f.Trim() == m.Name || !models.Contains(f.Trim())))
        {
            return (AuthEndpoints.Problem(400, "fallback", "The fallback is another model the gateway has."), null);
        }
        if (kind.Kind == EngineKinds.Vllm)
        {
            return ApplyVllm(m, body, file, library, hardware, prices);
        }
        if (file.Length == 0 || file.StartsWith('/') || !file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || library.Find(file) is not { } entry)
        {
            return (AuthEndpoints.Problem(400, "file", "Choose a GGUF file from the model library."), null);
        }
        if (ModelCatalog.CheckExtra(body.ExtraPreset ?? m.ExtraPreset) is { } bad)
        {
            return (AuthEndpoints.Problem(400, "extra", bad), null);
        }
        if (body.Devices is { Length: > 0 } devices)
        {
            var chosen = Hardware.ParseDevices(devices);
            if (hardware is { Gpus: 0 })
            {
                return (AuthEndpoints.Problem(400, "devices", "This machine's engine has no GPU: its models run on the CPU, so leave the GPUs empty."), null);
            }
            if (chosen is null || (hardware?.Devices is { Count: > 0 } known && chosen.Any(i => known.All(g => g.Index != i))))
            {
                var have = hardware?.Devices is { Count: > 0 } k ? string.Join(", ", k.Select(g => g.Index)) : "0";
                return (AuthEndpoints.Problem(400, "devices", $"Choose GPUs by their numbers, separated by commas: this machine has {have}."), null);
            }
        }
        var draft = Copy(m);
        Merge(draft, body, file);
        // Its prices as they will be: a value kept, set or emptied (the default).
        if (TokenPrice.Check(draft.InputPerMtok, draft.CachedInputPerMtok, draft.OutputPerMtok, prices) is { } price)
        {
            return (AuthEndpoints.Problem(400, "price", price), null);
        }
        var advice = ModelAdvisor.Advise(draft, entry, library.List(), hardware);
        if (ModelAdvisor.KindProblems(draft).Concat(advice.Problems).FirstOrDefault(p => p.Error) is { } error)
        {
            return (AuthEndpoints.Problem(400, error.Field, error.Message), null);
        }
        Merge(m, body, file);
        var warnings = advice.Problems.Where(p => !p.Error).Select(p => p.Message).ToList();
        return (null, warnings.Count > 0 ? "Saved. " + string.Join(" ", warnings) : null);
    }

    /// <summary>A vLLM model's settings: its folder a Hugging Face snapshot in the library, and only what vLLM has.</summary>
    private static (IResult? Problem, string? Warning) ApplyVllm(LocalModel m, LocalModelRequest body, string file, ModelLibrary library, Hardware? hardware, PriceOptions prices)
    {
        if (library.Snapshot(file) is not { Complete: true })
        {
            return (AuthEndpoints.Problem(400, "file", library.Snapshot(file) is { Why: { } why }
                ? $"{file} cannot be loaded yet: {why}"
                : "Choose a Hugging Face snapshot from the model library (hf/ORG/REPO@REVISION, with its config.json)."), null);
        }
        var draft = Copy(m);
        Merge(draft, body, file);
        VllmDefaults(draft, body);
        if (TokenPrice.Check(draft.InputPerMtok, draft.CachedInputPerMtok, draft.OutputPerMtok, prices) is { } price)
        {
            return (AuthEndpoints.Problem(400, "price", price), null);
        }
        if (ModelAdvisor.KindProblems(draft).FirstOrDefault(p => p.Error) is { } error)
        {
            return (AuthEndpoints.Problem(400, error.Field, error.Message), null);
        }
        _ = hardware;
        Merge(m, body, file);
        VllmDefaults(m, body);
        return (null, null);
    }

    /// <summary>A model turned to vLLM keeps none of llama.cpp's own values it did not set again: its cache type becomes vLLM's fp8.</summary>
    private static void VllmDefaults(LocalModel m, LocalModelRequest body)
    {
        if (body.KvType is null && !ModelAdvisor.VllmCacheTypes.Contains(m.KvType))
        {
            m.KvType = "fp8";
        }
    }

    private static LocalModel Copy(LocalModel m) => new()
    {
        Name = m.Name, File = m.File, Projector = m.Projector, Context = m.Context, MaxOutput = m.MaxOutput, Placement = m.Placement, GpuLayers = m.GpuLayers,
        CpuMoe = m.CpuMoe, KvType = m.KvType, Parallel = m.Parallel, Ubatch = m.Ubatch, Mtp = m.Mtp, DraftHead = m.DraftHead, DraftMax = m.DraftMax, Yarn = m.Yarn,
        Temperature = m.Temperature, TopP = m.TopP, TopK = m.TopK, MinP = m.MinP, PresencePenalty = m.PresencePenalty, ExtraPreset = m.ExtraPreset,
        Thinking = m.Thinking, Tools = m.Tools, InputPerMtok = m.InputPerMtok, CachedInputPerMtok = m.CachedInputPerMtok, OutputPerMtok = m.OutputPerMtok, Devices = m.Devices,
        EngineKind = m.EngineKind, Engine = m.Engine, Replicas = m.Replicas, GpuMemoryUtilization = m.GpuMemoryUtilization, Quantization = m.Quantization,
        SpecDecode = m.SpecDecode, KvCpuGb = m.KvCpuGb, Sleep = m.Sleep, Fallback = m.Fallback,
    };

    /// <summary>The request's values over the model's: a missing value is kept, one named in "clear" emptied.</summary>
    private static void Merge(LocalModel m, LocalModelRequest body, string file)
    {
        m.File = file;
        m.Projector = body.Projector is null ? m.Projector : body.Projector.Trim() is { Length: > 0 } p ? p : null;
        m.Context = body.Context ?? m.Context;
        m.MaxOutput = body.Clears("maxOutput") ? null : body.MaxOutput ?? m.MaxOutput;
        m.Placement = body.Placement ?? m.Placement;
        m.GpuLayers = body.GpuLayers ?? m.GpuLayers;
        // "" or cleared: every GPU.
        m.Devices = body.Clears("devices") ? null : body.Devices is { } dv ? (Hardware.ParseDevices(dv) is { } list ? string.Join(',', list) : null) : m.Devices;
        m.CpuMoe = body.CpuMoe ?? m.CpuMoe;
        m.KvType = body.KvType ?? m.KvType;
        m.Parallel = body.Parallel ?? m.Parallel;
        m.Ubatch = body.Clears("ubatch") ? null : body.Ubatch ?? m.Ubatch;
        m.Mtp = body.Mtp ?? m.Mtp;
        m.DraftHead = body.Clears("draftHead") ? null : body.DraftHead is { } h ? (h.Trim() is { Length: > 0 } head ? head : null) : m.DraftHead;
        m.DraftMax = body.DraftMax ?? m.DraftMax;
        m.Yarn = body.Yarn ?? m.Yarn;
        m.Temperature = body.Clears("temperature") ? null : body.Temperature ?? m.Temperature;
        m.TopP = body.Clears("topP") ? null : body.TopP ?? m.TopP;
        m.TopK = body.Clears("topK") ? null : body.TopK ?? m.TopK;
        m.MinP = body.Clears("minP") ? null : body.MinP ?? m.MinP;
        m.PresencePenalty = body.Clears("presencePenalty") ? null : body.PresencePenalty ?? m.PresencePenalty;
        var extra = body.ExtraPreset ?? m.ExtraPreset;
        m.ExtraPreset = string.IsNullOrWhiteSpace(extra) ? null : extra.Trim();
        m.Thinking = body.Thinking ?? m.Thinking;
        m.Tools = body.Tools ?? m.Tools;
        m.InputPerMtok = body.Clears("inputPerMtok") ? null : body.InputPerMtok ?? m.InputPerMtok;
        m.CachedInputPerMtok = body.Clears("cachedInputPerMtok") ? null : body.CachedInputPerMtok ?? m.CachedInputPerMtok;
        m.OutputPerMtok = body.Clears("outputPerMtok") ? null : body.OutputPerMtok ?? m.OutputPerMtok;
        m.EngineKind = body.EngineKind is { Length: > 0 } kind ? kind.Trim() : m.EngineKind;
        m.Engine = body.Clears("engine") ? null : body.Engine is { } en ? (en.Trim() is { Length: > 0 } engine ? engine : null) : m.Engine;
        m.Replicas = body.Replicas ?? m.Replicas;
        m.GpuMemoryUtilization = body.Clears("gpuMemoryUtilization") ? null : body.GpuMemoryUtilization ?? m.GpuMemoryUtilization;
        m.Quantization = body.Clears("quantization") ? null : body.Quantization is { } q ? (q.Trim() is { Length: > 0 } quant ? quant : null) : m.Quantization;
        m.SpecDecode = body.Clears("specDecode") ? null : body.SpecDecode is { } sd ? (sd.Trim() is { Length: > 0 } spec ? spec : null) : m.SpecDecode;
        m.KvCpuGb = body.Clears("kvCpuGb") ? null : body.KvCpuGb ?? m.KvCpuGb;
        m.Sleep = body.Sleep ?? m.Sleep;
        m.Fallback = body.Clears("fallback") ? null : body.Fallback is { } fb ? (fb.Trim() is { Length: > 0 } fallback ? fallback : null) : m.Fallback;
    }
}
