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
    string? ExtraPreset, bool? Thinking, bool? Tools, decimal? InputPerMtok, decimal? OutputPerMtok, string[]? Clear = null)
{
    /// <summary>A value to empty: a missing one is left as it is.</summary>
    public bool Clears(string field) => Clear?.Contains(field, StringComparer.OrdinalIgnoreCase) == true;
}

public sealed record ModelAccessRequest(Audience Audience, Guid[]? Groups);

/// <summary>
/// Admin → Models: every model at the gateway; for the engine's, loading and
/// unloading them (live: llama.cpp's router) and adding more from the library;
/// and for each, who may use it.
/// </summary>
public static class ModelEndpoints
{
    public static void MapModels(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/models").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapGet("/library", LibraryAsync);
        g.MapPost("/advice", AdviceAsync);
        g.MapPost("", AddAsync);
        g.MapPatch("/{name}", UpdateAsync);
        g.MapDelete("/{name}", RemoveAsync);
        g.MapPost("/{name}/load", LoadAsync);
        g.MapPost("/{name}/unload", UnloadAsync);
        g.MapPut("/{name}/access", AccessAsync);
    }

    private static async Task<IResult> ListAsync(AppDbContext db, ChatModels gatewayModels, EngineState state, ModelCatalog catalog, ModelLibrary library,
        IOptions<EngineOptions> engine, IOptions<StackOptions> stack, CancellationToken ct)
    {
        var e = engine.Value;
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
        var rows = new List<object>();
        if (e.Enabled && e.DefaultModel is { Length: > 0 } d)
        {
            rows.Add(new
            {
                name = d, source = "env", mode = "chat", status = state.StatusOf(d), file = stack.Value.ModelFile,
                context = int.TryParse(stack.Value.ModelContext, CultureInfo.InvariantCulture, out var c) ? c : (int?)null,
                vision = At(d)?.Vision ?? false, access = Access(d),
                profile = e.DefaultModelFile is { } df ? files.GetValueOrDefault(df) : null,
            });
        }
        foreach (var m in local)
        {
            rows.Add(new
            {
                name = m.Name, source = "local", mode = "chat",
                // Not in the engine's list although it answered: not read yet (it restarts), or its preset refused.
                status = state.StatusOf(m.Name) ?? (now is { Error: null, At: not null } ? "missing" : null),
                file = m.File, m.Projector, context = m.Context, m.MaxOutput, m.Placement, m.GpuLayers, m.CpuMoe, m.KvType, m.Parallel, m.Ubatch,
                m.Mtp, m.DraftHead, m.DraftMax, m.Yarn, m.Temperature, m.TopP, m.TopK, m.MinP, m.PresencePenalty,
                m.ExtraPreset, m.Thinking, m.Tools, m.InputPerMtok, m.OutputPerMtok,
                vision = m.Projector is { Length: > 0 }, atGateway = At(m.Name) is not null, access = Access(m.Name),
                profile = files.GetValueOrDefault(m.File),
            });
        }
        foreach (var m in atGateway.Where(m => m.Name != e.DefaultModel && local.All(l => l.Name != m.Name)))
        {
            rows.Add(new { name = m.Name, source = "gateway", mode = m.Mode, status = (string?)null, context = m.Context, vision = m.Vision, access = Access(m.Name) });
        }
        return Results.Ok(new
        {
            engine = new
            {
                enabled = e.Enabled, error = now.Error, checkedAt = now.At, active = e.Enabled ? catalog.Active() : null,
                loaded = now.Models.Where(m => m.Status == "loaded").Select(m => m.Name),
                loading = now.Models.Where(m => m.Status == "loading").Select(m => m.Name),
            },
            models = rows,
        });
    }

    private static async Task<IResult> LibraryAsync(ModelLibrary library, AppDbContext db, IOptions<EngineOptions> engine, CancellationToken ct)
    {
        var used = await db.LocalModels.AsNoTracking().Select(m => new { m.Name, m.File, m.Projector, m.DraftHead }).ToListAsync(ct);
        var e = engine.Value;
        return Results.Ok(library.List().Select(f => new
        {
            f.File.Path, f.File.Size, f.File.Parts, f.Profile,
            usedBy = used.Where(u => u.File == f.File.Path || u.Projector == f.File.Path || u.DraftHead == f.File.Path).Select(u => u.Name)
                .Concat(e.DefaultModelFile == f.File.Path && e.DefaultModel is { } d ? [d] : []),
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
        ChatModels gatewayModels, IOptions<EngineOptions> engine, EngineWatcher watcher, Audit audit, CancellationToken ct)
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
        var taken = (await gatewayModels.AllAsync(ct)).Select(m => m.Name).Append(engine.Value.DefaultModel ?? "")
            .Concat(await db.LocalModels.Select(m => m.Name).ToListAsync(ct));
        if (taken.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return AuthEndpoints.Problem(409, "exists", $"There is already a model named {name}.");
        }
        var model = new LocalModel { Name = name, File = "" };
        var (problem, warning) = Apply(model, body, library, await hardware.GetAsync(ct));
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
        EngineWatcher watcher, Audit audit, CancellationToken ct)
    {
        if (await db.LocalModels.SingleOrDefaultAsync(m => m.Name == name, ct) is not { } model)
        {
            return Results.NotFound();
        }
        var (problem, warning) = Apply(model, body, library, await hardware.GetAsync(ct));
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
        Audit audit, CancellationToken ct)
    {
        if (await db.LocalModels.SingleOrDefaultAsync(m => m.Name == name, ct) is not { } model)
        {
            return Results.NotFound();
        }
        db.LocalModels.Remove(model);
        await db.ModelAccess.Where(a => a.Model == name).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        if (catalog.Active() == name)
        {
            catalog.SetActive(engine.Value.DefaultModel ?? ModelCatalog.None);
        }
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

    private static async Task<IResult> LoadAsync(string name, EngineClient engine, EngineState state, ModelCatalog catalog, EngineWatcher watcher,
        ChatModels chatModels, Audit audit, CancellationToken ct)
    {
        if (!await KnownAsync(name, engine, state, ct))
        {
            return AuthEndpoints.Problem(404, "unknown", $"The engine has no model named {name} (yet: a model just added is there once the engine has restarted).");
        }
        try
        {
            catalog.SetActive(name);
            await engine.LoadAsync(name, ct);
        }
        catch (EngineException ex)
        {
            return AuthEndpoints.Problem(503, "engine", ex.Message);
        }
        chatModels.Forget();
        watcher.Wake();
        await audit.WriteAsync("model.load", name);
        return Results.Accepted();
    }

    private static async Task<IResult> UnloadAsync(string name, EngineClient engine, EngineState state, ModelCatalog catalog, EngineWatcher watcher,
        ChatModels chatModels, Audit audit, CancellationToken ct)
    {
        if (!await KnownAsync(name, engine, state, ct))
        {
            return Results.NotFound();
        }
        try
        {
            if (catalog.Active() == name)
            {
                catalog.SetActive(ModelCatalog.None);
            }
            await engine.UnloadAsync(name, ct);
        }
        catch (EngineException ex)
        {
            return AuthEndpoints.Problem(503, "engine", ex.Message);
        }
        chatModels.Forget();
        watcher.Wake();
        await audit.WriteAsync("model.unload", name);
        return Results.Accepted();
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
            state.Set(await engine.ModelsAsync(ct));
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
        var known = (await gatewayModels.AllAsync(ct)).Any(m => m.Name == name) || name == engine.Value.DefaultModel || await db.LocalModels.AnyAsync(m => m.Name == name, ct);
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
    private static (IResult? Problem, string? Warning) Apply(LocalModel m, LocalModelRequest body, ModelLibrary library, Hardware? hardware)
    {
        var file = (body.File ?? m.File).Trim();
        if (file.Length == 0 || file.StartsWith('/') || !file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || library.Find(file) is not { } entry)
        {
            return (AuthEndpoints.Problem(400, "file", "Choose a GGUF file from the model library."), null);
        }
        if ((body.InputPerMtok ?? 0) < 0 || (body.OutputPerMtok ?? 0) < 0)
        {
            return (AuthEndpoints.Problem(400, "price", "Prices cannot be negative."), null);
        }
        if (ModelCatalog.CheckExtra(body.ExtraPreset ?? m.ExtraPreset) is { } bad)
        {
            return (AuthEndpoints.Problem(400, "extra", bad), null);
        }
        var draft = Copy(m);
        Merge(draft, body, file);
        var advice = ModelAdvisor.Advise(draft, entry, library.List(), hardware);
        if (advice.FirstError is { } error)
        {
            return (AuthEndpoints.Problem(400, error.Field, error.Message), null);
        }
        Merge(m, body, file);
        var warnings = advice.Problems.Where(p => !p.Error).Select(p => p.Message).ToList();
        return (null, warnings.Count > 0 ? "Saved. " + string.Join(" ", warnings) : null);
    }

    private static LocalModel Copy(LocalModel m) => new()
    {
        Name = m.Name, File = m.File, Projector = m.Projector, Context = m.Context, MaxOutput = m.MaxOutput, Placement = m.Placement, GpuLayers = m.GpuLayers,
        CpuMoe = m.CpuMoe, KvType = m.KvType, Parallel = m.Parallel, Ubatch = m.Ubatch, Mtp = m.Mtp, DraftHead = m.DraftHead, DraftMax = m.DraftMax, Yarn = m.Yarn,
        Temperature = m.Temperature, TopP = m.TopP, TopK = m.TopK, MinP = m.MinP, PresencePenalty = m.PresencePenalty, ExtraPreset = m.ExtraPreset,
        Thinking = m.Thinking, Tools = m.Tools, InputPerMtok = m.InputPerMtok, OutputPerMtok = m.OutputPerMtok,
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
        m.OutputPerMtok = body.Clears("outputPerMtok") ? null : body.OutputPerMtok ?? m.OutputPerMtok;
    }
}
