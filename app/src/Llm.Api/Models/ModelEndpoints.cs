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
    string? Name, string? File, string? Projector, int? Context, int? MaxOutput, int? GpuLayers, int? CpuMoe, string? KvType, int? Parallel,
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
    private static readonly string[] KvTypes = ["f16", "bf16", "q8_0", "q5_1", "q5_0", "q4_1", "q4_0", "iq4_nl"];

    public static void MapModels(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/models").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapGet("/library", (ModelLibrary library, AppDbContext db) => LibraryAsync(library, db));
        g.MapPost("", AddAsync);
        g.MapPatch("/{name}", UpdateAsync);
        g.MapDelete("/{name}", RemoveAsync);
        g.MapPost("/{name}/load", LoadAsync);
        g.MapPost("/{name}/unload", UnloadAsync);
        g.MapPut("/{name}/access", AccessAsync);
    }

    private static async Task<IResult> ListAsync(AppDbContext db, ChatModels gatewayModels, EngineState state, ModelCatalog catalog,
        IOptions<EngineOptions> engine, IOptions<StackOptions> stack, CancellationToken ct)
    {
        var e = engine.Value;
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
            });
        }
        foreach (var m in local)
        {
            rows.Add(new
            {
                name = m.Name, source = "local", mode = "chat", status = state.StatusOf(m.Name), file = m.File, m.Projector, context = m.Context, m.MaxOutput,
                m.GpuLayers, m.CpuMoe, m.KvType, m.Parallel, m.ExtraPreset, m.Thinking, m.Tools, m.InputPerMtok, m.OutputPerMtok,
                vision = m.Projector is { Length: > 0 }, atGateway = At(m.Name) is not null, access = Access(m.Name),
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

    private static async Task<IResult> LibraryAsync(ModelLibrary library, AppDbContext db)
    {
        var used = await db.LocalModels.AsNoTracking().Select(m => new { m.Name, m.File, m.Projector }).ToListAsync();
        return Results.Ok(library.List().Select(f => new
        {
            f.Path, f.Size, f.Parts, f.Role, f.Architecture, name = f.Name, f.SizeLabel, f.TrainedContext,
            usedBy = used.Where(u => u.File == f.Path || u.Projector == f.Path).Select(u => u.Name),
        }));
    }

    private static async Task<IResult> AddAsync(LocalModelRequest body, AppDbContext db, ModelCatalog catalog, ModelLibrary library, ChatModels gatewayModels,
        IOptions<EngineOptions> engine, EngineWatcher watcher, Audit audit, CancellationToken ct)
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
        if (Apply(model, body, library) is { } problem)
        {
            return problem;
        }
        db.LocalModels.Add(model);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("model.add", name, detail: model.File);
        return Results.Created($"/api/admin/models/{name}", await AfterChangeAsync(catalog, watcher, ct));
    }

    private static async Task<IResult> UpdateAsync(string name, LocalModelRequest body, AppDbContext db, ModelCatalog catalog, ModelLibrary library, EngineWatcher watcher,
        Audit audit, CancellationToken ct)
    {
        if (await db.LocalModels.SingleOrDefaultAsync(m => m.Name == name, ct) is not { } model)
        {
            return Results.NotFound();
        }
        if (Apply(model, body, library) is { } problem)
        {
            return problem;
        }
        model.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("model.update", name);
        return Results.Ok(await AfterChangeAsync(catalog, watcher, ct));
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
        return Results.Ok(await AfterChangeAsync(catalog, watcher, ct));
    }

    /// <summary>The engine restarts on new presets; the gateway learns of the change. A gateway that is down is tried again later.</summary>
    private static async Task<object> AfterChangeAsync(ModelCatalog catalog, EngineWatcher watcher, CancellationToken ct)
    {
        await catalog.WritePresetsAsync(ct);
        watcher.Wake();
        try
        {
            await catalog.SyncGatewayAsync(ct);
            return new { restarting = true, warning = (string?)null };
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

    /// <summary>Checks and applies a model's settings; the name is set by the caller. Null when all is well.</summary>
    private static IResult? Apply(LocalModel m, LocalModelRequest body, ModelLibrary library)
    {
        var file = (body.File ?? m.File).Trim();
        if (file.Length == 0 || file.StartsWith('/') || !file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || !library.Contains(file))
        {
            return AuthEndpoints.Problem(400, "file", "Choose a GGUF file from the model library.");
        }
        var projector = body.Projector is null ? m.Projector : body.Projector.Trim() is { Length: > 0 } p ? p : null;
        if (projector is not null && (!projector.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) || !library.Contains(projector)))
        {
            return AuthEndpoints.Problem(400, "projector", "The vision projector must be a GGUF file in the model library.");
        }
        var context = body.Context ?? m.Context;
        if (context is < 512 or > 1_048_576)
        {
            return AuthEndpoints.Problem(400, "context", "The context is 512 to 1,048,576 tokens.");
        }
        var maxOutput = body.Clears("maxOutput") ? null : body.MaxOutput ?? m.MaxOutput;
        if (maxOutput is < 1 || maxOutput > context)
        {
            return AuthEndpoints.Problem(400, "max_output", "The longest answer is at most the context.");
        }
        var kv = body.KvType ?? m.KvType;
        if (!KvTypes.Contains(kv))
        {
            return AuthEndpoints.Problem(400, "kv_type", $"The cache type is one of {string.Join(", ", KvTypes)}.");
        }
        if ((body.GpuLayers ?? m.GpuLayers) is < 0 or > 999 || (body.CpuMoe ?? m.CpuMoe) is < 0 or > 999 || (body.Parallel ?? m.Parallel) is < 1 or > 32)
        {
            return AuthEndpoints.Problem(400, "numbers", "GPU layers and MoE layers on the CPU are 0 to 999; parallel answers 1 to 32.");
        }
        if ((body.InputPerMtok ?? 0) < 0 || (body.OutputPerMtok ?? 0) < 0)
        {
            return AuthEndpoints.Problem(400, "price", "Prices cannot be negative.");
        }
        var extra = body.ExtraPreset ?? m.ExtraPreset;
        if (ModelCatalog.CheckExtra(extra) is { } bad)
        {
            return AuthEndpoints.Problem(400, "extra", bad);
        }
        m.File = file;
        m.Projector = projector;
        m.Context = context;
        m.MaxOutput = maxOutput;
        m.GpuLayers = body.GpuLayers ?? m.GpuLayers;
        m.CpuMoe = body.CpuMoe ?? m.CpuMoe;
        m.KvType = kv;
        m.Parallel = body.Parallel ?? m.Parallel;
        m.ExtraPreset = string.IsNullOrWhiteSpace(extra) ? null : extra.Trim();
        m.Thinking = body.Thinking ?? m.Thinking;
        m.Tools = body.Tools ?? m.Tools;
        m.InputPerMtok = body.Clears("inputPerMtok") ? null : body.InputPerMtok ?? m.InputPerMtok;
        m.OutputPerMtok = body.Clears("outputPerMtok") ? null : body.OutputPerMtok ?? m.OutputPerMtok;
        return null;
    }
}
