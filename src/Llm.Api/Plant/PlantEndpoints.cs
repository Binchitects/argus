using System.Globalization;
using System.Security.Claims;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Plant;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Plant;

public sealed record SiteRequest(string? Name = null, string? Description = null, string? TimeZone = null, List<Guid>? GroupIds = null);

public sealed record AreaRequest(string? Name = null, string? Kind = null, Guid? ParentId = null, int? Position = null);

public sealed record AssetRequest(string? Name = null, string? Kind = null, Guid? AreaId = null, string? Vendor = null, string? Model = null, string? Serial = null,
    string? Description = null, List<string>? Tags = null, double? X = null, double? Y = null);

public sealed record PointRequest(string? Name = null, string? Description = null, string? DataType = null, string? Unit = null, string? Source = null, string? Address = null,
    double? LowLow = null, double? Low = null, double? High = null, double? HighHigh = null, double? Deadband = null, string? States = null);

/// <summary>A sheet of points, a line each: area, asset, asset kind, point, unit, type, source, address, low, high.</summary>
public sealed record PointsImport(string? Csv = null);

/// <summary>
/// The plant (v9, IoT and OT): its sites, each site's areas (one under another), assets and their points, with their
/// units, sources and limits. Seen by whoever has the plant feature, a site limited to its groups when it names some;
/// set up by those with plant.manage. A sheet of points imports a site's whole list at once.
/// </summary>
public static class PlantEndpoints
{
    public const int MostSites = 500, MostAssets = 50_000, MostPoints = 500_000, MostImportLines = 20_000;

    public static void MapPlant(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/plant").RequireFeature("plant").RequirePermissions(Core.Access.Permissions.Plant, Core.Access.Permissions.PlantManage);
        g.MapGet("", OverviewAsync);
        g.MapPost("/sites", AddSiteAsync);
        g.MapGet("/sites/{id:guid}", SiteAsync);
        g.MapPatch("/sites/{id:guid}", ChangeSiteAsync);
        g.MapDelete("/sites/{id:guid}", RemoveSiteAsync);
        g.MapPost("/sites/{id:guid}/areas", AddAreaAsync);
        g.MapPatch("/areas/{id:guid}", ChangeAreaAsync);
        g.MapDelete("/areas/{id:guid}", RemoveAreaAsync);
        g.MapPost("/sites/{id:guid}/assets", AddAssetAsync);
        g.MapGet("/assets/{id:guid}", AssetAsync);
        g.MapPatch("/assets/{id:guid}", ChangeAssetAsync);
        g.MapDelete("/assets/{id:guid}", RemoveAssetAsync);
        g.MapPost("/assets/{id:guid}/points", AddPointAsync);
        g.MapPatch("/points/{id:guid}", ChangePointAsync);
        g.MapDelete("/points/{id:guid}", RemovePointAsync);
        g.MapPost("/sites/{id:guid}/import", ImportAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static IResult Bad(string code, string message) => AuthEndpoints.Problem(400, code, message);

    /// <summary>Whether the person may see the site: one of its groups, or none named, or the plant's managers.</summary>
    private static bool Sees(PlantSite s, Membership m) => s.GroupIds.Count == 0 || s.GroupIds.Any(m.Groups.Contains) || m.Has(Core.Access.Permissions.PlantManage);

    private static async Task<PlantSite?> VisibleAsync(AppDbContext db, Guid id, Membership m, CancellationToken ct) =>
        await db.PlantSites.SingleOrDefaultAsync(s => s.Id == id, ct) is { } s && Sees(s, m) ? s : null;

    private static string? Text(string? value, int most, string what, bool required, out IResult? problem)
    {
        problem = null;
        var t = value?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            if (required)
            {
                problem = Bad(what, $"Give it a {what}.");
            }
            return null;
        }
        if (t.Length > most)
        {
            problem = Bad(what, $"A {what} has at most {most:N0} characters.");
            return null;
        }
        return t;
    }

    private static object SiteShape(PlantSite s, int assets, int points) => new
    {
        s.Id, s.Name, s.Description, s.TimeZone, s.GroupIds, s.CreatedAt, s.UpdatedAt, assets, points,
    };

    private static object AssetShape(PlantAsset a, int points) => new
    {
        a.Id, a.SiteId, a.AreaId, a.Name, a.Kind, a.Vendor, a.Model, a.Serial, a.Description, a.Tags, a.X, a.Y, a.CreatedAt, a.UpdatedAt, points,
    };

    private static object PointShape(PlantPoint p) => new
    {
        p.Id, p.AssetId, p.Name, p.Description, p.DataType, p.Unit, p.Source, p.Address, p.LowLow, p.Low, p.High, p.HighHigh, p.Deadband, p.States, p.CreatedAt,
    };

    /// <summary>The sites the person sees, with how many assets and points each has, and whether they may set the plant up.</summary>
    private static async Task<IResult> OverviewAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        var m = await access.MembershipAsync(await Me(p, users), ct);
        var sites = (await db.PlantSites.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).Where(s => Sees(s, m)).ToList();
        var ids = sites.Select(s => s.Id).ToList();
        var assets = await db.PlantAssets.AsNoTracking().Where(a => ids.Contains(a.SiteId)).GroupBy(a => a.SiteId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var points = await (from pt in db.PlantPoints.AsNoTracking()
                            join a in db.PlantAssets.AsNoTracking() on pt.AssetId equals a.Id
                            where ids.Contains(a.SiteId)
                            group pt by a.SiteId into g
                            select new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        return Results.Ok(new
        {
            sites = sites.Select(s => SiteShape(s, assets.GetValueOrDefault(s.Id), points.GetValueOrDefault(s.Id))),
            mayManage = m.Has(Core.Access.Permissions.PlantManage),
            kinds = new { areas = PlantArea.Kinds, assets = PlantAsset.Kinds, types = PlantPoint.Types, sources = PlantPoint.Sources },
        });
    }

    /// <summary>A site whole: its areas (each under its parent), its assets with how many points each has.</summary>
    private static async Task<IResult> SiteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        if (await VisibleAsync(db, id, await access.MembershipAsync(await Me(p, users), ct), ct) is not { } site)
        {
            return Results.NotFound();
        }
        var areas = await db.PlantAreas.AsNoTracking().Where(a => a.SiteId == id).OrderBy(a => a.Position).ThenBy(a => a.Name).ToListAsync(ct);
        var assets = await db.PlantAssets.AsNoTracking().Where(a => a.SiteId == id).OrderBy(a => a.Name).ToListAsync(ct);
        var assetIds = assets.Select(a => a.Id).ToList();
        var counts = await db.PlantPoints.AsNoTracking().Where(pt => assetIds.Contains(pt.AssetId)).GroupBy(pt => pt.AssetId).Select(g => new { g.Key, n = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.n, ct);
        return Results.Ok(new
        {
            site = SiteShape(site, assets.Count, counts.Values.Sum()),
            areas = areas.Select(a => new { a.Id, a.ParentId, a.Name, a.Kind, a.Position }),
            assets = assets.Select(a => AssetShape(a, counts.GetValueOrDefault(a.Id))),
        });
    }

    private static IResult? ApplySite(PlantSite s, SiteRequest body)
    {
        if (body.Name is not null)
        {
            s.Name = Text(body.Name, 200, "name", true, out var problem)!;
            if (problem is not null)
            {
                return problem;
            }
        }
        if (body.Description is not null)
        {
            s.Description = Text(body.Description, 2000, "description", false, out var problem);
            if (problem is not null)
            {
                return problem;
            }
        }
        if (body.TimeZone is { } zone)
        {
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
            {
                return Bad("timeZone", $"{zone} is not a time zone (Europe/Berlin, Asia/Tehran, UTC).");
            }
            s.TimeZone = zone;
        }
        if (body.GroupIds is { } groups)
        {
            s.GroupIds = [.. groups.Distinct()];
        }
        s.UpdatedAt = DateTimeOffset.UtcNow;
        return null;
    }

    private static async Task<IResult> AddSiteAsync(SiteRequest body, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Bad("name", "Give the site a name.");
        }
        if (await db.PlantSites.CountAsync(ct) >= MostSites)
        {
            return Bad("sites", $"At most {MostSites} sites.");
        }
        var site = new PlantSite { Name = "" };
        if (ApplySite(site, body) is { } problem)
        {
            return problem;
        }
        if (await db.PlantSites.AnyAsync(s => s.Name == site.Name, ct))
        {
            return AuthEndpoints.Problem(409, "name", $"There is a site named {site.Name} already.");
        }
        db.PlantSites.Add(site);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/plant/sites/{site.Id}", new { id = site.Id });
    }

    private static async Task<IResult> ChangeSiteAsync(Guid id, SiteRequest body, AppDbContext db, CancellationToken ct)
    {
        if (await db.PlantSites.SingleOrDefaultAsync(s => s.Id == id, ct) is not { } site)
        {
            return Results.NotFound();
        }
        if (ApplySite(site, body) is { } problem)
        {
            return problem;
        }
        if (await db.PlantSites.AnyAsync(s => s.Id != id && s.Name == site.Name, ct))
        {
            return AuthEndpoints.Problem(409, "name", $"There is a site named {site.Name} already.");
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveSiteAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        // Its areas point at one another (Restrict): the site's areas go first, then the site with its assets and points.
        await db.PlantAreas.Where(a => a.SiteId == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ParentId, (Guid?)null), ct);
        await db.PlantAreas.Where(a => a.SiteId == id).ExecuteDeleteAsync(ct);
        return await db.PlantSites.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult?> ApplyAreaAsync(AppDbContext db, PlantArea area, AreaRequest body, CancellationToken ct)
    {
        if (body.Name is not null)
        {
            area.Name = Text(body.Name, 200, "name", true, out var problem)!;
            if (problem is not null)
            {
                return problem;
            }
        }
        if (body.Kind is { } kind)
        {
            if (!PlantArea.Kinds.Contains(kind))
            {
                return Bad("kind", $"An area is one of {string.Join(", ", PlantArea.Kinds)}.");
            }
            area.Kind = kind;
        }
        if (body.ParentId is { } parent)
        {
            if (parent == Guid.Empty)
            {
                area.ParentId = null;
            }
            else
            {
                if (parent == area.Id || !await db.PlantAreas.AnyAsync(a => a.Id == parent && a.SiteId == area.SiteId, ct))
                {
                    return Bad("parent", "Its parent is another area of the same site.");
                }
                // Not under itself, however deep.
                for (Guid? at = parent; at is { } here;)
                {
                    if (here == area.Id)
                    {
                        return Bad("parent", "An area cannot be under itself.");
                    }
                    at = await db.PlantAreas.Where(a => a.Id == here).Select(a => a.ParentId).SingleOrDefaultAsync(ct);
                }
                area.ParentId = parent;
            }
        }
        if (body.Position is { } position)
        {
            area.Position = position;
        }
        return null;
    }

    private static async Task<IResult> AddAreaAsync(Guid id, AreaRequest body, AppDbContext db, CancellationToken ct)
    {
        if (!await db.PlantSites.AnyAsync(s => s.Id == id, ct))
        {
            return Results.NotFound();
        }
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Bad("name", "Give the area a name.");
        }
        var area = new PlantArea { SiteId = id, Name = "" };
        if (await ApplyAreaAsync(db, area, body, ct) is { } problem)
        {
            return problem;
        }
        db.PlantAreas.Add(area);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/plant/areas/{area.Id}", new { id = area.Id });
    }

    private static async Task<IResult> ChangeAreaAsync(Guid id, AreaRequest body, AppDbContext db, CancellationToken ct)
    {
        if (await db.PlantAreas.SingleOrDefaultAsync(a => a.Id == id, ct) is not { } area)
        {
            return Results.NotFound();
        }
        if (await ApplyAreaAsync(db, area, body, ct) is { } problem)
        {
            return problem;
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>An area removed: the areas under it come up to its parent, its assets to no area.</summary>
    private static async Task<IResult> RemoveAreaAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        if (await db.PlantAreas.SingleOrDefaultAsync(a => a.Id == id, ct) is not { } area)
        {
            return Results.NotFound();
        }
        await db.PlantAreas.Where(a => a.ParentId == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.ParentId, area.ParentId), ct);
        db.PlantAreas.Remove(area);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult?> ApplyAssetAsync(AppDbContext db, PlantAsset asset, AssetRequest body, CancellationToken ct)
    {
        IResult? problem;
        if (body.Name is not null)
        {
            asset.Name = Text(body.Name, 200, "name", true, out problem)!;
            if (problem is not null)
            {
                return problem;
            }
        }
        if (body.Kind is { } kind)
        {
            if (!PlantAsset.Kinds.Contains(kind))
            {
                return Bad("kind", $"An asset is one of {string.Join(", ", PlantAsset.Kinds)}.");
            }
            asset.Kind = kind;
        }
        if (body.AreaId is { } area)
        {
            if (area != Guid.Empty && !await db.PlantAreas.AnyAsync(a => a.Id == area && a.SiteId == asset.SiteId, ct))
            {
                return Bad("area", "That area is not this site's.");
            }
            asset.AreaId = area == Guid.Empty ? null : area;
        }
        if (body.Vendor is not null) { asset.Vendor = Text(body.Vendor, 200, "vendor", false, out problem); if (problem is not null) return problem; }
        if (body.Model is not null) { asset.Model = Text(body.Model, 200, "model", false, out problem); if (problem is not null) return problem; }
        if (body.Serial is not null) { asset.Serial = Text(body.Serial, 200, "serial", false, out problem); if (problem is not null) return problem; }
        if (body.Description is not null) { asset.Description = Text(body.Description, 2000, "description", false, out problem); if (problem is not null) return problem; }
        if (body.Tags is { } tags)
        {
            asset.Tags = [.. tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length is > 0 and <= 40).Distinct().Take(20)];
        }
        if (body.X is { } x || body.Y is not null)
        {
            if (body.X is < 0 or > 1 || body.Y is < 0 or > 1)
            {
                return Bad("place", "A place on the floor plan is a share of its width and height, 0 to 1.");
            }
            asset.X = body.X;
            asset.Y = body.Y;
        }
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        return null;
    }

    private static async Task<IResult> AddAssetAsync(Guid id, AssetRequest body, AppDbContext db, CancellationToken ct)
    {
        if (!await db.PlantSites.AnyAsync(s => s.Id == id, ct))
        {
            return Results.NotFound();
        }
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Bad("name", "Give the asset a name.");
        }
        if (await db.PlantAssets.CountAsync(ct) >= MostAssets)
        {
            return Bad("assets", $"At most {MostAssets:N0} assets.");
        }
        var asset = new PlantAsset { SiteId = id, Name = "" };
        if (await ApplyAssetAsync(db, asset, body, ct) is { } problem)
        {
            return problem;
        }
        if (await db.PlantAssets.AnyAsync(a => a.SiteId == id && a.Name == asset.Name, ct))
        {
            return AuthEndpoints.Problem(409, "name", $"The site has an asset named {asset.Name} already.");
        }
        db.PlantAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/plant/assets/{asset.Id}", new { id = asset.Id });
    }

    /// <summary>An asset with its points (to whoever sees its site).</summary>
    private static async Task<IResult> AssetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AccessService access, CancellationToken ct)
    {
        if (await db.PlantAssets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is not { } asset
            || await VisibleAsync(db, asset.SiteId, await access.MembershipAsync(await Me(p, users), ct), ct) is null)
        {
            return Results.NotFound();
        }
        var points = await db.PlantPoints.AsNoTracking().Where(pt => pt.AssetId == id).OrderBy(pt => pt.Name).ToListAsync(ct);
        return Results.Ok(new { asset = AssetShape(asset, points.Count), points = points.Select(PointShape) });
    }

    private static async Task<IResult> ChangeAssetAsync(Guid id, AssetRequest body, AppDbContext db, CancellationToken ct)
    {
        if (await db.PlantAssets.SingleOrDefaultAsync(a => a.Id == id, ct) is not { } asset)
        {
            return Results.NotFound();
        }
        if (await ApplyAssetAsync(db, asset, body, ct) is { } problem)
        {
            return problem;
        }
        if (await db.PlantAssets.AnyAsync(a => a.Id != id && a.SiteId == asset.SiteId && a.Name == asset.Name, ct))
        {
            return AuthEndpoints.Problem(409, "name", $"The site has an asset named {asset.Name} already.");
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemoveAssetAsync(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.PlantAssets.Where(a => a.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();

    private static IResult? ApplyPoint(PlantPoint point, PointRequest body)
    {
        IResult? problem;
        if (body.Name is not null)
        {
            point.Name = Text(body.Name, 200, "name", true, out problem)!;
            if (problem is not null)
            {
                return problem;
            }
        }
        if (body.Description is not null) { point.Description = Text(body.Description, 2000, "description", false, out problem); if (problem is not null) return problem; }
        if (body.DataType is { } type)
        {
            if (!PlantPoint.Types.Contains(type))
            {
                return Bad("dataType", $"A point is one of {string.Join(", ", PlantPoint.Types)}.");
            }
            point.DataType = type;
        }
        if (body.Unit is not null) { point.Unit = Text(body.Unit, 40, "unit", false, out problem); if (problem is not null) return problem; }
        if (body.Source is { } source)
        {
            if (!PlantPoint.Sources.Contains(source))
            {
                return Bad("source", $"A point is read from one of {string.Join(", ", PlantPoint.Sources)}.");
            }
            point.Source = source;
        }
        if (body.Address is not null) { point.Address = Text(body.Address, 500, "address", false, out problem); if (problem is not null) return problem; }
        if (body.States is not null) { point.States = Text(body.States, 2000, "states", false, out problem); if (problem is not null) return problem; }
        (point.LowLow, point.Low, point.High, point.HighHigh) = (body.LowLow ?? point.LowLow, body.Low ?? point.Low, body.High ?? point.High, body.HighHigh ?? point.HighHigh);
        if (body.Deadband is { } deadband)
        {
            if (deadband < 0)
            {
                return Bad("deadband", "A deadband is 0 or more.");
            }
            point.Deadband = deadband;
        }
        // The limits in their order: low-low ≤ low < high ≤ high-high, as far as they are set.
        var set = new[] { point.LowLow, point.Low, point.High, point.HighHigh }.Where(v => v is not null).Select(v => v!.Value).ToList();
        if (set.Zip(set.Skip(1)).Any(pair => pair.First > pair.Second))
        {
            return Bad("limits", "The limits go up: low-low, low, high, high-high.");
        }
        if (point.Source != PlantPoint.Manual && string.IsNullOrEmpty(point.Address))
        {
            return Bad("address", $"A point read from {point.Source} says where: its topic, node id or register.");
        }
        return null;
    }

    private static async Task<IResult> AddPointAsync(Guid id, PointRequest body, AppDbContext db, CancellationToken ct)
    {
        if (!await db.PlantAssets.AnyAsync(a => a.Id == id, ct))
        {
            return Results.NotFound();
        }
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return Bad("name", "Give the point a name.");
        }
        if (await db.PlantPoints.CountAsync(ct) >= MostPoints)
        {
            return Bad("points", $"At most {MostPoints:N0} points.");
        }
        var point = new PlantPoint { AssetId = id, Name = "" };
        if (ApplyPoint(point, body) is { } problem)
        {
            return problem;
        }
        if (await db.PlantPoints.AnyAsync(pt => pt.AssetId == id && pt.Name == point.Name, ct))
        {
            return AuthEndpoints.Problem(409, "name", $"The asset has a point named {point.Name} already.");
        }
        db.PlantPoints.Add(point);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/plant/points/{point.Id}", new { id = point.Id });
    }

    private static async Task<IResult> ChangePointAsync(Guid id, PointRequest body, AppDbContext db, CancellationToken ct)
    {
        if (await db.PlantPoints.SingleOrDefaultAsync(pt => pt.Id == id, ct) is not { } point)
        {
            return Results.NotFound();
        }
        if (ApplyPoint(point, body) is { } problem)
        {
            return problem;
        }
        if (await db.PlantPoints.AnyAsync(pt => pt.Id != id && pt.AssetId == point.AssetId && pt.Name == point.Name, ct))
        {
            return AuthEndpoints.Problem(409, "name", $"The asset has a point named {point.Name} already.");
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RemovePointAsync(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.PlantPoints.Where(pt => pt.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();

    /// <summary>
    /// A site's points from a sheet (CSV, a header line first): area, asset, asset kind, point, unit, type, source, address,
    /// low, high. Areas and assets are made as named, a point already there is changed: importing the same sheet again
    /// changes nothing. Each wrong line is said by its number; nothing is kept when any is wrong.
    /// </summary>
    private static async Task<IResult> ImportAsync(Guid id, PointsImport body, AppDbContext db, CancellationToken ct)
    {
        if (!await db.PlantSites.AnyAsync(s => s.Id == id, ct))
        {
            return Results.NotFound();
        }
        var lines = (body.Csv ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2)
        {
            return Bad("csv", "A sheet has a header line and at least one point: area,asset,kind,point,unit,type,source,address,low,high.");
        }
        if (lines.Count - 1 > MostImportLines)
        {
            return Bad("csv", $"At most {MostImportLines:N0} points a sheet.");
        }
        var header = Fields(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        if (Col("asset") < 0 || Col("point") < 0)
        {
            return Bad("csv", "The header names at least asset and point (and may name area, kind, unit, type, source, address, low, high).");
        }
        var areas = await db.PlantAreas.Where(a => a.SiteId == id).ToListAsync(ct);
        var assets = await db.PlantAssets.Where(a => a.SiteId == id).ToListAsync(ct);
        var assetIds = assets.Select(a => a.Id).ToList();
        var points = await db.PlantPoints.Where(pt => assetIds.Contains(pt.AssetId)).ToListAsync(ct);
        var wrong = new List<string>();
        int madeAreas = 0, madeAssets = 0, madePoints = 0, changed = 0;
        for (var n = 1; n < lines.Count; n++)
        {
            var f = Fields(lines[n]);
            string? At(string name) => Col(name) is var i && i >= 0 && i < f.Count && f[i].Trim() is { Length: > 0 } v ? v : null;
            double? Number(string name, out bool bad)
            {
                bad = false;
                if (At(name) is not { } text)
                {
                    return null;
                }
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    return d;
                }
                bad = true;
                return null;
            }
            var assetName = At("asset");
            var pointName = At("point");
            if (assetName is null || pointName is null)
            {
                wrong.Add($"line {n + 1}: an asset and a point are needed");
                continue;
            }
            PlantArea? area = null;
            if (At("area") is { } areaName)
            {
                area = areas.FirstOrDefault(a => string.Equals(a.Name, areaName, StringComparison.OrdinalIgnoreCase));
                if (area is null)
                {
                    area = new PlantArea { SiteId = id, Name = areaName.Length > 200 ? areaName[..200] : areaName };
                    areas.Add(area);
                    db.PlantAreas.Add(area);
                    madeAreas++;
                }
            }
            var asset = assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                var kind = At("kind") ?? PlantAsset.Machine;
                if (!PlantAsset.Kinds.Contains(kind))
                {
                    wrong.Add($"line {n + 1}: an asset is one of {string.Join(", ", PlantAsset.Kinds)}, not {kind}");
                    continue;
                }
                asset = new PlantAsset { SiteId = id, Name = assetName.Length > 200 ? assetName[..200] : assetName, Kind = kind, AreaId = area?.Id };
                assets.Add(asset);
                db.PlantAssets.Add(asset);
                madeAssets++;
            }
            else if (area is not null && asset.AreaId != area.Id)
            {
                asset.AreaId = area.Id;
            }
            var request = new PointRequest(Name: pointName, Unit: At("unit"), DataType: At("type"), Source: At("source"), Address: At("address"),
                Low: Number("low", out var badLow), High: Number("high", out var badHigh));
            if (badLow || badHigh)
            {
                wrong.Add($"line {n + 1}: low and high are numbers (1.5, not 1,5)");
                continue;
            }
            var point = points.FirstOrDefault(pt => pt.AssetId == asset.Id && string.Equals(pt.Name, pointName, StringComparison.OrdinalIgnoreCase));
            var isNew = point is null;
            point ??= new PlantPoint { AssetId = asset.Id, Name = "" };
            if (ApplyPoint(point, request) is { } problem)
            {
                wrong.Add($"line {n + 1}: {Message(problem)}");
                continue;
            }
            if (isNew)
            {
                points.Add(point);
                db.PlantPoints.Add(point);
                madePoints++;
            }
            else if (db.Entry(point).State == EntityState.Modified || db.ChangeTracker.HasChanges())
            {
                changed++;
            }
        }
        if (wrong.Count > 0)
        {
            return AuthEndpoints.Problem(400, "csv", $"{wrong.Count} line(s) are not right, so nothing was kept: " + string.Join("; ", wrong.Take(20)) + (wrong.Count > 20 ? "; …" : ""));
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { areas = madeAreas, assets = madeAssets, points = madePoints, sheetLines = lines.Count - 1 });
    }

    private static string Message(IResult problem) =>
        problem is Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<object> { Value: { } v } && v.GetType().GetProperty("error")?.GetValue(v) is string s ? s : "not right";

    /// <summary>A CSV line's fields: commas between, "double quotes" around one with commas ("" for a quote in it).</summary>
    public static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var field = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }
        fields.Add(field.ToString());
        return fields;
    }
}
