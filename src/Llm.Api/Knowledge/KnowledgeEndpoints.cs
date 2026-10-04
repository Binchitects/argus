using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>A knowledge source as an admin sets it: what is left out stays as it is.</summary>
public sealed record SourceRequest(
    string? Name = null, string? Kind = null, string? Location = null, bool? Wiki = null, bool? Issues = null, string? Hosts = null, int? MaxPages = null,
    Audience? Audience = null, Guid[]? Groups = null);

/// <summary>Admin → Knowledge: the sources, how their syncs went, what they hold and who may read it; adding, changing, Sync now, removing.</summary>
public static class KnowledgeEndpoints
{
    public static readonly string[] Kinds = ["gitlab", "folder", "website"];

    public static void MapKnowledge(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/knowledge").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapPost("/{id:guid}/sync", SyncAsync);
        g.MapDelete("/{id:guid}", DeleteAsync);
        g.MapGet("/{id:guid}/documents", DocumentsAsync);
    }

    private static async Task<IResult> ListAsync(AppDbContext db, Embedder embedder, Schedules.GitLabBot bot, IOptionsMonitor<KnowledgeOptions> options, CancellationToken ct)
    {
        var sources = await db.KnowledgeSources.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var documents = await db.KnowledgeDocuments.AsNoTracking().Where(d => d.SourceId != null).GroupBy(d => d.SourceId)
            .Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.n, ct);
        var passages = await db.KnowledgeChunks.AsNoTracking().Join(db.KnowledgeDocuments.Where(d => d.SourceId != null), c => c.DocumentId, d => d.Id, (c, d) => d.SourceId)
            .GroupBy(s => s).Select(x => new { x.Key, n = x.Count() }).ToDictionaryAsync(x => x.Key!.Value, x => x.n, ct);
        var readers = await db.KnowledgeReaders.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
        var groupIds = sources.SelectMany(s => s.Groups).Distinct().ToList();
        var groups = await db.Groups.AsNoTracking().Where(x => groupIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var ready = await embedder.ReadyAsync(ct);
        return Results.Ok(new
        {
            embedder = ready,
            problem = ready ? null : "The embedder (the embed module) is not running: nothing can be synced or searched. It runs unless it is left out in docker-compose.override.yml.",
            gitlabBot = bot.Ready,
            folderRoot = options.CurrentValue.FolderRoot,
            syncEvery = options.CurrentValue.SyncEvery,
            sources = sources.Select(s => new
            {
                s.Id, s.Name, s.Kind, s.Location, s.Wiki, s.Issues, s.Hosts, s.MaxPages, s.Audience,
                groups = s.Groups.Where(groups.ContainsKey).Select(id => new { id, name = groups[id] }),
                s.State, s.Error, s.SyncStartedAt, s.SyncedAt,
                documents = documents.GetValueOrDefault(s.Id), passages = passages.GetValueOrDefault(s.Id),
                projects = s.Kind == "gitlab"
                    ? readers.Where(r => r.SourceId == s.Id).Select(r => new { name = r.Name, people = r.People.Count, r.FetchedAt })
                    : null,
            }),
        });
    }

    private static async Task<IResult?> CheckAsync(SourceRequest body, KnowledgeSource source, AppDbContext db, KnowledgeSyncer syncer, CancellationToken ct)
    {
        if (source.Name.Length is 0 or > 100)
        {
            return AuthEndpoints.Problem(400, "name", "Give the source a name of 1 to 100 characters.");
        }
        if (await db.KnowledgeSources.AnyAsync(s => s.Name == source.Name && s.Id != source.Id, ct))
        {
            return AuthEndpoints.Problem(409, "name", $"There is a source called {source.Name} already.");
        }
        if (!Kinds.Contains(source.Kind))
        {
            return AuthEndpoints.Problem(400, "kind", "A source is a GitLab project or group, a folder, or a website.");
        }
        if (source.Location.Length is 0 or > 2000)
        {
            return AuthEndpoints.Problem(400, "location", source.Kind switch
            {
                "gitlab" => "Say which GitLab project or group, by its path (group/project).",
                "folder" => "Say which folder.",
                _ => "Give the first page's address.",
            });
        }
        if (source.Audience == Audience.Groups && source.Groups.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Choose at least one group, or let everyone read it.");
        }
        if (body.Groups is { Length: > 0 } chosen && await db.Groups.CountAsync(x => chosen.Contains(x.Id), ct) != chosen.Distinct().Count())
        {
            return AuthEndpoints.Problem(400, "groups", "One of the groups does not exist.");
        }
        // GitLab's bot can be set later: the sync then says what is missing. A folder or a website must be right now.
        return source.Kind != "gitlab" && syncer.Connector(source.Kind)?.Check(source) is { } problem ? AuthEndpoints.Problem(400, "location", problem) : null;
    }

    private static void Apply(SourceRequest body, KnowledgeSource s)
    {
        if (body.Name is not null)
        {
            s.Name = body.Name.Trim();
        }
        if (body.Location is not null)
        {
            s.Location = body.Location.Trim();
        }
        s.Wiki = body.Wiki ?? s.Wiki;
        s.Issues = body.Issues ?? s.Issues;
        if (body.Hosts is not null)
        {
            s.Hosts = string.Join(", ", body.Hosts.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) is { Length: > 0 } h ? h : null;
        }
        s.MaxPages = body.MaxPages ?? s.MaxPages;
        s.Audience = body.Audience ?? s.Audience;
        if (body.Groups is not null || body.Audience is not null)
        {
            s.Groups = s.Audience == Audience.Groups ? [.. (body.Groups ?? [.. s.Groups]).Distinct()] : [];
        }
    }

    private static async Task<IResult> CreateAsync(SourceRequest body, AppDbContext db, KnowledgeSyncer syncer, KnowledgeSync sync, Audit audit, CancellationToken ct)
    {
        var source = new KnowledgeSource { Name = "", Kind = (body.Kind ?? "").Trim().ToLowerInvariant(), Location = "" };
        Apply(body, source);
        if (await CheckAsync(body, source, db, syncer, ct) is { } problem)
        {
            return problem;
        }
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("knowledge.add", source.Name, detail: $"{source.Kind} {source.Location}");
        sync.Now(source.Id);
        return Results.Created($"/api/admin/knowledge/{source.Id}", new { source.Id, source.Name });
    }

    private static async Task<IResult> UpdateAsync(Guid id, SourceRequest body, AppDbContext db, KnowledgeSyncer syncer, KnowledgeSync sync, Audit audit, CancellationToken ct)
    {
        if (await db.KnowledgeSources.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return Results.NotFound();
        }
        if (body.Kind is { } kind && kind != source.Kind)
        {
            return AuthEndpoints.Problem(400, "kind", "A source keeps its kind: add a new one instead.");
        }
        var before = (source.Location, source.Wiki, source.Issues, source.Hosts, source.MaxPages);
        var readersBefore = Readers.For(source.Audience, source.Groups);
        Apply(body, source);
        if (await CheckAsync(body, source, db, syncer, ct) is { } problem)
        {
            return problem;
        }
        await db.SaveChangesAsync(ct);
        // Who may read a folder or a website changes at once, for every document; GitLab's readers are its members.
        var readers = Readers.For(source.Audience, source.Groups);
        if (source.Kind != "gitlab" && !readers.SequenceEqual(readersBefore))
        {
            await db.KnowledgeDocuments.Where(d => d.SourceId == id).ExecuteUpdateAsync(u => u.SetProperty(d => d.Readers, readers), ct);
        }
        await audit.WriteAsync("knowledge.change", source.Name, detail: source.Kind == "gitlab" ? source.Location : $"{source.Location}; readers: {string.Join(", ", readers)}");
        if (before != (source.Location, source.Wiki, source.Issues, source.Hosts, source.MaxPages))
        {
            sync.Now(source.Id);
        }
        return Results.NoContent();
    }

    private static async Task<IResult> SyncAsync(Guid id, AppDbContext db, KnowledgeSync sync, Audit audit, CancellationToken ct)
    {
        if (await db.KnowledgeSources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return Results.NotFound();
        }
        sync.Now(id);
        await audit.WriteAsync("knowledge.sync", source.Name);
        return Results.Accepted();
    }

    private static async Task<IResult> DeleteAsync(Guid id, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.KnowledgeSources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return Results.NotFound();
        }
        // Its documents, passages and readers go with it.
        await db.KnowledgeSources.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
        await audit.WriteAsync("knowledge.remove", source.Name, detail: $"{source.Kind} {source.Location}");
        return Results.NoContent();
    }

    /// <summary>What a source holds: its documents (the first 500, by title), each with its link and passages.</summary>
    private static async Task<IResult> DocumentsAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        if (!await db.KnowledgeSources.AnyAsync(s => s.Id == id, ct))
        {
            return Results.NotFound();
        }
        return Results.Ok(await db.KnowledgeDocuments.AsNoTracking().Where(d => d.SourceId == id).OrderBy(d => d.Title).Take(500)
            .Select(d => new { d.Id, d.Title, d.Url, d.Key, d.SyncedAt, passages = db.KnowledgeChunks.Count(c => c.DocumentId == d.Id) }).ToListAsync(ct));
    }
}
