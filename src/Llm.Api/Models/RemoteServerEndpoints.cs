using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Models;

/// <param name="Parallel">Requests it serves at once (its parallel slots); with other servers of the same model, the gateway sends it no more.</param>
/// <param name="InputPerMtok">Its prices per million tokens; null: the default (Settings → Prices).</param>
public sealed record RemoteModelRequest(string? Remote, string? Name, int? Context, int? MaxOutput, bool? Vision, bool? Tools, bool? Thinking,
    decimal? InputPerMtok, decimal? OutputPerMtok, int? Parallel = null, decimal? CachedInputPerMtok = null);

/// <summary>A server to add or change: a key of "" removes it, a missing one is kept.</summary>
public sealed record RemoteServerRequest(string? Name, string? BaseUrl, string? ApiKey, bool? VerifyTls, RemoteModelRequest[]? Models);

public sealed record RemoteProbeRequest(string? BaseUrl, string? ApiKey, bool? VerifyTls, Guid? Id);

/// <summary>
/// Admin → Models, other GPU servers: another machine's OpenAI-compatible engine, and
/// which of its models the gateway serves under which names. A name the gateway has
/// already is a second copy of that model, and the gateway spreads requests between them.
/// </summary>
public static class RemoteServerEndpoints
{
    public static void MapRemoteServers(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/servers").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapPost("/probe", ProbeAsync);
        g.MapPost("", AddAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapDelete("/{id:guid}", RemoveAsync);
    }

    private static async Task<IResult> ListAsync(AppDbContext db, RemoteHealth health, CancellationToken ct)
    {
        var servers = await db.RemoteServers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var statuses = await Task.WhenAll(servers.Select(s => health.StatusAsync(s, ct)));
        return Results.Ok(servers.Zip(statuses, (s, st) => new
        {
            s.Id, s.Name, baseUrl = s.BaseUrl, keySet = s.ApiKeyProtected is not null, s.VerifyTls,
            status = new { st.Up, st.Error, checkedAt = st.At, offers = st.Models },
            models = s.Models.Select(m => new
            {
                m.Remote, m.Name, m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking, m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, m.Parallel,
                // Listed by the server: a model it no longer has cannot answer.
                listed = !st.Up || st.Models.Any(o => o.Id == m.Remote),
            }),
        }));
    }

    /// <summary>What a server lists, before it is added (or with its saved key, when changing one).</summary>
    private static async Task<IResult> ProbeAsync(RemoteProbeRequest body, AppDbContext db, RemoteServerClient client, CancellationToken ct)
    {
        if (RemoteServerClient.CheckUrl(body.BaseUrl) is { } bad)
        {
            return AuthEndpoints.Problem(400, "baseUrl", bad);
        }
        var key = body.ApiKey;
        if (string.IsNullOrEmpty(key) && body.Id is { } id && await db.RemoteServers.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) is { } saved)
        {
            key = client.Unprotect(saved.ApiKeyProtected);
        }
        try
        {
            return Results.Ok(new { models = await client.ModelsAsync(body.BaseUrl!, key, body.VerifyTls ?? true, ct) });
        }
        catch (RemoteException ex)
        {
            return AuthEndpoints.Problem(400, "server", ex.Message);
        }
    }

    private static async Task<IResult> AddAsync(RemoteServerRequest body, AppDbContext db, RemoteServerClient client, RemoteHealth health, ModelCatalog catalog,
        ChatModels chatModels, KeyAccessWatcher keys, PriceBook prices, Audit audit, CancellationToken ct)
    {
        var server = new RemoteServer { Name = "", BaseUrl = "" };
        if (await ApplyAsync(server, body, db, client, prices, ct) is { } problem)
        {
            return problem;
        }
        // A new server must answer: a wrong address or key is found now, not when someone asks.
        try
        {
            await client.ModelsAsync(server.BaseUrl, client.Unprotect(server.ApiKeyProtected), server.VerifyTls, ct);
        }
        catch (RemoteException ex)
        {
            return AuthEndpoints.Problem(400, "server", ex.Message);
        }
        db.RemoteServers.Add(server);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("server.add", server.Name, detail: server.BaseUrl);
        return Results.Created($"/api/admin/servers/{server.Id}", await AfterChangeAsync(server.Id, catalog, chatModels, keys, health, ct));
    }

    private static async Task<IResult> UpdateAsync(Guid id, RemoteServerRequest body, AppDbContext db, RemoteServerClient client, RemoteHealth health, ModelCatalog catalog,
        ChatModels chatModels, KeyAccessWatcher keys, PriceBook prices, Audit audit, CancellationToken ct)
    {
        if (await db.RemoteServers.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } server)
        {
            return Results.NotFound();
        }
        if (await ApplyAsync(server, body, db, client, prices, ct) is { } problem)
        {
            return problem;
        }
        server.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("server.update", server.Name);
        return Results.Ok(await AfterChangeAsync(id, catalog, chatModels, keys, health, ct));
    }

    private static async Task<IResult> RemoveAsync(Guid id, AppDbContext db, RemoteHealth health, ModelCatalog catalog, ChatModels chatModels, KeyAccessWatcher keys,
        Audit audit, CancellationToken ct)
    {
        if (await db.RemoteServers.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } server)
        {
            return Results.NotFound();
        }
        db.RemoteServers.Remove(server);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("server.remove", server.Name);
        return Results.Ok(await AfterChangeAsync(id, catalog, chatModels, keys, health, ct));
    }

    /// <summary>Checks and applies a request over the server's values.</summary>
    private static async Task<IResult?> ApplyAsync(RemoteServer server, RemoteServerRequest body, AppDbContext db, RemoteServerClient client, PriceBook prices, CancellationToken ct)
    {
        var name = (body.Name ?? server.Name).Trim();
        if (name.Length is 0 or > 100)
        {
            return AuthEndpoints.Problem(400, "name", "Give the server a name, at most 100 characters.");
        }
        if (await db.RemoteServers.AnyAsync(s => s.Name == name && s.Id != server.Id, ct))
        {
            return AuthEndpoints.Problem(409, "name", $"There is already a server named {name}.");
        }
        var url = body.BaseUrl ?? server.BaseUrl;
        if (RemoteServerClient.CheckUrl(url) is { } bad)
        {
            return AuthEndpoints.Problem(400, "baseUrl", bad);
        }
        var models = body.Models ?? [.. server.Models.Select(m => new RemoteModelRequest(m.Remote, m.Name, m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking, m.InputPerMtok, m.OutputPerMtok, m.Parallel,
            m.CachedInputPerMtok))];
        if (models.Length == 0)
        {
            return AuthEndpoints.Problem(400, "models", "Choose at least one of its models.");
        }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chosen = new List<RemoteModel>();
        foreach (var m in models)
        {
            var remote = (m.Remote ?? "").Trim();
            var as_ = (m.Name ?? remote).Trim();
            if (remote.Length == 0)
            {
                return AuthEndpoints.Problem(400, "models", "Each model needs its id at the server.");
            }
            if (ModelCatalog.CheckName(as_) is { } nameProblem)
            {
                return AuthEndpoints.Problem(400, "models", $"{as_}: {nameProblem}");
            }
            if (!names.Add(as_))
            {
                return AuthEndpoints.Problem(400, "models", $"{as_} is there twice: give each model its own name.");
            }
            if (m.Context is < 1024 or > ModelAdvisor.MaxContext || m.MaxOutput is < 1 || (m.Context is { } c && m.MaxOutput > c))
            {
                return AuthEndpoints.Problem(400, "models", $"{as_}: the context is 1,024 to {ModelAdvisor.MaxContext:N0} tokens, and the longest answer at most the context.");
            }
            if (TokenPrice.Check(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, prices.Defaults) is { } price)
            {
                return AuthEndpoints.Problem(400, "models", $"{as_}: {char.ToLowerInvariant(price[0])}{price[1..]}");
            }
            if (m.Parallel is < 1 or > 256)
            {
                return AuthEndpoints.Problem(400, "models", $"{as_}: it serves 1 to 256 requests at once (its parallel slots), or leave it empty.");
            }
            chosen.Add(new RemoteModel
            {
                Remote = remote, Name = as_, Context = m.Context, MaxOutput = m.MaxOutput, Vision = m.Vision ?? false, Tools = m.Tools ?? true,
                Thinking = m.Thinking ?? false, InputPerMtok = m.InputPerMtok, CachedInputPerMtok = m.CachedInputPerMtok, OutputPerMtok = m.OutputPerMtok, Parallel = m.Parallel,
            });
        }
        server.Name = name;
        server.BaseUrl = RemoteServerClient.Normalize(url);
        server.VerifyTls = body.VerifyTls ?? server.VerifyTls;
        if (body.ApiKey is { } key)
        {
            server.ApiKeyProtected = client.Protect(key.Trim());
        }
        server.Models = chosen;
        return null;
    }

    /// <summary>The gateway learns of the change (a gateway that is down is brought in step within a minute).</summary>
    private static async Task<object> AfterChangeAsync(Guid id, ModelCatalog catalog, ChatModels chatModels, KeyAccessWatcher keys, RemoteHealth health, CancellationToken ct)
    {
        health.Forget(id);
        keys.Wake();
        try
        {
            await catalog.SyncGatewayAsync(ct);
            return new { id, warning = (string?)null };
        }
        catch (GatewayException ex)
        {
            chatModels.Forget();
            return new { id, warning = (string?)$"Saved, but the gateway could not be updated yet ({ex.Message}); it is tried again within a minute." };
        }
    }
}
