using System.Security.Claims;
using System.Text;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Media;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Vector;

/// <param name="Still">The drawing as it stands still (written by hand); null: as it is.</param>
/// <param name="Motions">Its motions; null: as they are.</param>
public sealed record VectorChange(string? Still, IReadOnlyList<Motion>? Motions);

public sealed record VectorUpload(string? Svg, string? Name);

/// <summary>
/// The media studio's vector drawings: what a drawing is (its still drawing, motions, how long it plays, versions), changed
/// by hand (its code, its motions), uploaded, its parts measured, and its file served as an SVG that runs nothing.
/// </summary>
public static class VectorEndpoints
{
    public static void MapVectors(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/media").RequireFeature("media").PersonOnly().RequirePermission(Core.Access.Permissions.Pictures);
        g.MapGet("/vectors/config", ConfigAsync);
        g.MapPost("/vectors", UploadAsync);
        g.MapGet("/vectors/{id:guid}", GetAsync);
        g.MapPut("/vectors/{id:guid}", ChangeAsync);
        g.MapGet("/vectors/{id:guid}/parts", PartsAsync);
        g.MapGet("/vectors/{id:guid}/versions", VersionsAsync);
        g.MapPost("/vectors/{id:guid}/versions/{version:int}/restore", RestoreAsync);
        g.MapGet("/items/{id:guid}/svg", SvgAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static async Task<(MediaItem Item, MediaVector Vector)?> FindAsync(AppDbContext db, Guid id, AppUser me, bool tracked, CancellationToken ct)
    {
        var items = tracked ? db.MediaItems : db.MediaItems.AsNoTracking();
        var item = await items.SingleOrDefaultAsync(i => i.Id == id && i.OwnerId == me.Id && i.Kind == MediaItem.Vector, ct);
        var vectors = tracked ? db.MediaVectors : db.MediaVectors.AsNoTracking();
        var vector = item is null ? null : await vectors.SingleOrDefaultAsync(v => v.ItemId == item.Id, ct);
        return item is not null && vector is not null ? (item, vector) : null;
    }

    private static async Task<IResult> ConfigAsync(SvgRender render, Chat.Tools.SandboxClient sandbox, Operations.Modules modules, CancellationToken ct) => Results.Ok(new
    {
        styles = SvgDraw.Styles.Select(s => new { name = s.Name, about = s.About }),
        traces = VectorSandbox.Styles.Select(s => new { name = s.Name, about = s.About }),
        kinds = SvgMotion.Kinds.Select(k => new { kind = k.Kind, about = k.About }),
        formats = VectorJobs.Formats,
        // Animations are recorded by the browser; pictures are traced (and drawn without it) in the sandbox; words drawn by the picture server.
        moves = await render.MovesAsync(ct),
        tracesPictures = sandbox.Unavailable() is null,
        drawsPictures = await modules.HasAsync("imagegen", ct),
    });

    private static object View(MediaItem item, MediaVector v, string markup) => new
    {
        id = item.Id, item.Prompt, item.Width, item.Height, v.Still, motions = VectorStore.MotionsOf(v.Motions), markup, v.Seconds, v.Loops, v.Source, v.Style, v.Version,
        v.UpdatedAt,
    };

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, id, me, false, ct) is not { } found)
        {
            return Results.NotFound();
        }
        var data = await db.ChatAttachments.AsNoTracking().Where(a => a.Id == found.Item.AttachmentId).Select(a => a.Data).SingleAsync(ct);
        return Results.Ok(View(found.Item, found.Vector, Encoding.UTF8.GetString(data ?? [])));
    }

    /// <summary>Its still drawing or its motions changed by hand: compiled, kept as a version; what of the motions could not be done is said.</summary>
    private static async Task<IResult> ChangeAsync(Guid id, VectorChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, VectorStore store,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, id, me, true, ct) is not { } found)
        {
            return Results.NotFound();
        }
        if (body.Motions is { Count: > 200 })
        {
            return AuthEndpoints.Problem(400, "motions", "At most 200 motions.");
        }
        IReadOnlyList<string> problems;
        try
        {
            var change = body.Still is not null && body.Motions is not null ? "by hand" : body.Still is not null ? "its code by hand" : "its motions by hand";
            problems = await store.ChangeAsync(found.Item, found.Vector, body.Still ?? found.Vector.Still, body.Motions ?? VectorStore.MotionsOf(found.Vector.Motions), change, ct);
        }
        catch (SvgException ex)
        {
            return AuthEndpoints.Problem(400, "still", ex.Message);
        }
        var data = await db.ChatAttachments.AsNoTracking().Where(a => a.Id == found.Item.AttachmentId).Select(a => a.Data).SingleAsync(ct);
        return Results.Ok(new { drawing = View(found.Item, found.Vector, Encoding.UTF8.GetString(data ?? [])), problems });
    }

    /// <summary>An SVG of the person's own into the studio (made safe: what draws stays, what runs or reaches out goes, and they are told).</summary>
    private static async Task<IResult> UploadAsync(VectorUpload body, ClaimsPrincipal p, UserManager<AppUser> users, VectorStore store, Storage.StorageQuotas quotas,
        CancellationToken ct)
    {
        var me = await Me(p, users);
        SafeSvg svg;
        try
        {
            svg = SvgSafe.Clean(body.Svg ?? "");
        }
        catch (SvgException ex)
        {
            return AuthEndpoints.Problem(400, "svg", ex.Message);
        }
        if (await quotas.RefusalAsync(me.Id, Encoding.UTF8.GetByteCount(svg.Markup), ct) is { } full)
        {
            return AuthEndpoints.Problem(413, "svg", full);
        }
        var name = Path.GetFileNameWithoutExtension(body.Name ?? "") is { Length: > 0 } n ? n : "drawing";
        var item = await store.AddAsync(me, svg, "upload", name, "uploaded", null, null, null, ct);
        return Results.Ok(new { item.Id, changed = svg.Changed });
    }

    private static async Task<IResult> PartsAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, SvgRender render, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, id, me, false, ct) is not { } found)
        {
            return Results.NotFound();
        }
        return Results.Ok(await render.PartsAsync(found.Vector.Still, ct));
    }

    private static async Task<IResult> VersionsAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, id, me, false, ct) is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(await db.MediaVectorVersions.AsNoTracking().Where(v => v.ItemId == id).OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.Change, v.CreatedAt }).ToListAsync(ct));
    }

    /// <summary>An old version back, as a new version (nothing in between is lost).</summary>
    private static async Task<IResult> RestoreAsync(Guid id, int version, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, VectorStore store, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await FindAsync(db, id, me, true, ct) is not { } found)
        {
            return Results.NotFound();
        }
        if (await db.MediaVectorVersions.AsNoTracking().SingleOrDefaultAsync(v => v.ItemId == id && v.Version == version, ct) is not { } old)
        {
            return Results.NotFound();
        }
        await store.ChangeAsync(found.Item, found.Vector, old.Still, VectorStore.MotionsOf(old.Motions), $"version {version} back", ct);
        return Results.NoContent();
    }

    /// <summary>
    /// The drawing's file as an SVG (for an &lt;img&gt;, and to save): made safe when it was kept, and served so that even
    /// opened on its own it runs nothing and reaches nothing (a sandboxing content policy).
    /// </summary>
    private static async Task<IResult> SvgAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var me = await Me(p, users);
        var file = await db.MediaItems.AsNoTracking().Where(i => i.Id == id && i.OwnerId == me.Id && i.Kind == MediaItem.Vector)
            .Join(db.ChatAttachments, i => i.AttachmentId, a => a.Id, (i, a) => new { a.Data, a.FileName }).SingleOrDefaultAsync(ct);
        if (file?.Data is null)
        {
            return Results.NotFound();
        }
        http.Response.Headers.ContentSecurityPolicy = SvgSafe.Policy;
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "private, no-cache";
        return http.Request.Query.ContainsKey("download")
            ? Results.File(file.Data, "image/svg+xml", file.FileName)
            : Results.File(file.Data, "image/svg+xml");
    }
}
