using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Settings;
using Llm.Core.Access;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>A knowledge source as an admin sets it: what is left out stays as it is. The secret is never sent back; empty, it stays as it was.</summary>
public sealed record SourceRequest(
    string? Name = null, string? Kind = null, string? Location = null, bool? Wiki = null, bool? Issues = null, string? Hosts = null, int? MaxPages = null,
    Audience? Audience = null, Guid[]? Groups = null, string? Spaces = null, bool? BlogPosts = null, bool? SitePages = null, string? Account = null,
    string? Tenant = null, string? Secret = null);

/// <summary>Admin → Knowledge: the sources, how their syncs went, what they hold and who may read it; adding, changing, testing, Sync now, removing.</summary>
public static class KnowledgeEndpoints
{
    public static readonly string[] Kinds = ["gitlab", "folder", "website", "confluence", "sharepoint"];

    /// <summary>The kinds whose readers are their own permissions, mirrored; the admin's choice is for what those cannot tell.</summary>
    public static bool Mirrors(string kind) => kind is "confluence" or "sharepoint";

    public static void MapKnowledge(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/knowledge").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("", ListAsync);
        g.MapPost("", CreateAsync);
        g.MapPatch("/{id:guid}", UpdateAsync);
        g.MapPost("/{id:guid}/sync", SyncAsync);
        g.MapPost("/test", TestAsync);
        g.MapPost("/{id:guid}/test", TestSavedAsync);
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
                s.Spaces, s.BlogPosts, s.SitePages, s.Account, s.Tenant, secretSet = s.SecretEncrypted != null, s.Mirror,
                s.State, s.Error, s.SyncStartedAt, s.SyncedAt,
                documents = documents.GetValueOrDefault(s.Id), passages = passages.GetValueOrDefault(s.Id),
                projects = s.Kind == "gitlab"
                    ? readers.Where(r => r.SourceId == s.Id).Select(r => new { name = r.Name, people = r.People.Count, r.FetchedAt })
                    : null,
                // A Confluence source's spaces, with how many people may view each (its restricted pages' readers are not listed).
                viewers = s.Kind == "confluence"
                    ? readers.Where(r => r.SourceId == s.Id && !r.Key.Contains('/', StringComparison.Ordinal)).Select(r => new { name = r.Name, people = r.People.Count, r.FetchedAt })
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
                "confluence" => "Give the Confluence site's address.",
                "sharepoint" => "Give the sites or libraries to read, by their addresses, one a line.",
                _ => "Give the first page's address.",
            });
        }
        if (source.Account?.Length > 320 || source.Tenant?.Length > 100 || source.Spaces?.Length > 1000)
        {
            return AuthEndpoints.Problem(400, source.Account?.Length > 320 ? "account" : source.Tenant?.Length > 100 ? "tenant" : "spaces", "That is too long.");
        }
        if (source.Audience == Audience.Groups && source.Groups.Count == 0)
        {
            return AuthEndpoints.Problem(400, "groups", "Choose at least one group, or let everyone read it.");
        }
        if (body.Groups is { Length: > 0 } chosen && await db.Groups.CountAsync(x => chosen.Contains(x.Id), ct) != chosen.Distinct().Count())
        {
            return AuthEndpoints.Problem(400, "groups", "One of the groups does not exist.");
        }
        // GitLab's bot can be set later: the sync then says what is missing. The others must be right now.
        return source.Kind != "gitlab" && syncer.Connector(source.Kind)?.Check(source) is { } problem ? AuthEndpoints.Problem(400, Field(source.Kind, problem), problem) : null;
    }

    /// <summary>Which field a connector's complaint is about, for the form.</summary>
    private static string Field(string kind, string problem) =>
        !Mirrors(kind) ? "location"
        : problem.Contains("secret", StringComparison.Ordinal) || problem.Contains("token", StringComparison.Ordinal) ? "secret"
        : problem.Contains("tenant", StringComparison.Ordinal) ? "tenant"
        : problem.Contains("client (application) ID", StringComparison.Ordinal) || problem.Contains("email", StringComparison.Ordinal) ? "account"
        : problem.Contains("space key", StringComparison.Ordinal) ? "spaces"
        : "location";

    /// <summary>A secret given that cannot be stored encrypted (APP_DATA_KEY is not set), or one too long.</summary>
    private static IResult? SecretProblem(SourceRequest body, string? dataKey) =>
        body.Secret is not { Length: > 0 } secret ? null
        : secret.Trim().Length > 4000 ? AuthEndpoints.Problem(400, "secret", "That token or secret is too long.")
        : string.IsNullOrEmpty(dataKey) ? AuthEndpoints.Problem(400, "data_key", "APP_DATA_KEY is not set, so a token or secret cannot be stored encrypted.")
        : null;

    private static void Apply(SourceRequest body, KnowledgeSource s, string? dataKey)
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
        if (body.Spaces is not null)
        {
            s.Spaces = string.Join(", ", body.Spaces.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()) is { Length: > 0 } keys ? keys : null;
        }
        s.BlogPosts = body.BlogPosts ?? s.BlogPosts;
        s.SitePages = body.SitePages ?? s.SitePages;
        if (body.Account is not null)
        {
            s.Account = body.Account.Trim() is { Length: > 0 } account ? account : null;
        }
        if (body.Tenant is not null)
        {
            s.Tenant = body.Tenant.Trim() is { Length: > 0 } tenant ? tenant : null;
        }
        if (body.Secret?.Trim() is { Length: > 0 } secret && !string.IsNullOrEmpty(dataKey))
        {
            s.SecretEncrypted = SettingsCrypto.Encrypt(secret, dataKey);
        }
        s.Audience = body.Audience ?? s.Audience;
        if (body.Groups is not null || body.Audience is not null)
        {
            s.Groups = s.Audience == Audience.Groups ? [.. (body.Groups ?? [.. s.Groups]).Distinct()] : [];
        }
    }

    private static async Task<IResult> CreateAsync(SourceRequest body, AppDbContext db, KnowledgeSyncer syncer, KnowledgeSync sync, Audit audit, IOptions<AuthOptions> auth,
        CancellationToken ct)
    {
        if (SecretProblem(body, auth.Value.DataKey) is { } refused)
        {
            return refused;
        }
        var source = new KnowledgeSource { Name = "", Kind = (body.Kind ?? "").Trim().ToLowerInvariant(), Location = "" };
        // Where Confluence's or SharePoint's permissions cannot tell, admins only, unless the admin says otherwise.
        if (Mirrors(source.Kind) && body.Audience is null)
        {
            source.Audience = Audience.Admins;
        }
        Apply(body, source, auth.Value.DataKey);
        if (await CheckAsync(body, source, db, syncer, ct) is { } problem)
        {
            return problem;
        }
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("knowledge.add", source.Name, detail: $"{source.Kind} {Where(source)}");
        sync.Now(source.Id);
        return Results.Created($"/api/admin/knowledge/{source.Id}", new { source.Id, source.Name });
    }

    /// <summary>A source's place, for the audit log: never its secret.</summary>
    private static string Where(KnowledgeSource s) => s.Kind switch
    {
        "confluence" => $"{s.Location}; spaces: {s.Spaces ?? "all"}",
        "sharepoint" => $"{s.Location.Replace('\n', ' ')}; tenant {s.Tenant}",
        _ => s.Location,
    };

    private static async Task<IResult> UpdateAsync(Guid id, SourceRequest body, AppDbContext db, KnowledgeSyncer syncer, KnowledgeSync sync, Audit audit, IOptions<AuthOptions> auth,
        CancellationToken ct)
    {
        if (await db.KnowledgeSources.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } source)
        {
            return Results.NotFound();
        }
        if (body.Kind is { } kind && kind != source.Kind)
        {
            return AuthEndpoints.Problem(400, "kind", "A source keeps its kind: add a new one instead.");
        }
        if (SecretProblem(body, auth.Value.DataKey) is { } refused)
        {
            return refused;
        }
        var before = Read(source);
        var readersBefore = Readers.For(source.Audience, source.Groups);
        Apply(body, source, auth.Value.DataKey);
        if (await CheckAsync(body, source, db, syncer, ct) is { } problem)
        {
            return problem;
        }
        await db.SaveChangesAsync(ct);
        // Who may read a folder or a website changes at once, for every document; GitLab's readers are its members. Confluence's and
        // SharePoint's are their own permissions, and whom the admin chose for the rest is decided at each search: at once too.
        var readers = Readers.For(source.Audience, source.Groups);
        if (source.Kind is "folder" or "website" && !readers.SequenceEqual(readersBefore))
        {
            await db.KnowledgeDocuments.Where(d => d.SourceId == id).ExecuteUpdateAsync(u => u.SetProperty(d => d.Readers, readers), ct);
        }
        await audit.WriteAsync("knowledge.change", source.Name, detail: source.Kind == "gitlab" ? source.Location : $"{Where(source)}; readers: {string.Join(", ", readers)}");
        if (before != Read(source))
        {
            sync.Now(source.Id);
        }
        return Results.NoContent();
    }

    /// <summary>What a source reads and how: a change to any of it syncs it again at once.</summary>
    private static string Read(KnowledgeSource s) =>
        string.Join('\u001f', s.Location, s.Wiki, s.Issues, s.Hosts, s.MaxPages, s.Spaces, s.BlogPosts, s.SitePages, s.Account, s.Tenant, s.SecretEncrypted);

    /// <summary>Tries a new source's settings (Test connection): what it reached, or why it cannot.</summary>
    private static Task<IResult> TestAsync(SourceRequest body, KnowledgeSyncer syncer, IOptions<AuthOptions> auth, CancellationToken ct) =>
        TryAsync(new KnowledgeSource { Name = "test", Kind = (body.Kind ?? "").Trim().ToLowerInvariant(), Location = "" }, body, syncer, auth, ct);

    /// <summary>Tries a saved source's settings with what the form changed (its saved secret when none is given).</summary>
    private static async Task<IResult> TestSavedAsync(Guid id, SourceRequest body, AppDbContext db, KnowledgeSyncer syncer, IOptions<AuthOptions> auth, CancellationToken ct) =>
        await db.KnowledgeSources.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) is { } source
            ? await TryAsync(source, body with { Kind = null }, syncer, auth, ct)
            : Results.NotFound();

    private static async Task<IResult> TryAsync(KnowledgeSource source, SourceRequest body, KnowledgeSyncer syncer, IOptions<AuthOptions> auth, CancellationToken ct)
    {
        if (SecretProblem(body, auth.Value.DataKey) is { } refused)
        {
            return refused;
        }
        Apply(body, source, auth.Value.DataKey);
        if (syncer.Connector(source.Kind) is not IConnectionTest connector)
        {
            return AuthEndpoints.Problem(400, "kind", "Only Confluence and SharePoint sources have a connection to test.");
        }
        if (((IKnowledgeConnector)connector).Check(source) is { } problem)
        {
            return AuthEndpoints.Problem(400, Field(source.Kind, problem), problem);
        }
        try
        {
            return Results.Ok(new { message = await connector.TestAsync(source, ct) });
        }
        catch (KnowledgeException ex)
        {
            return AuthEndpoints.Problem(400, "connection", ex.Message);
        }
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
        await audit.WriteAsync("knowledge.remove", source.Name, detail: $"{source.Kind} {Where(source)}");
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
