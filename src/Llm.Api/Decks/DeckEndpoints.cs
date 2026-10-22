using System.Security.Claims;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Api.Insights;
using Llm.Core.Data;
using Llm.Core.Decks;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Decks;

public sealed record NewDeck(string? About, Guid? TemplateId = null);

/// <param name="Slide">The slide the person is looking at (from 0): "this slide".</param>
public sealed record DeckChange(string? Change, int? Slide = null);

/// <param name="TemplateId">Guid.Empty: the built-in look.</param>
public sealed record DeckEdit(string? Title = null, List<DeckSlide>? Slides = null, Guid? TemplateId = null, string? Note = null);

public sealed record TemplateChange(string? Name = null, bool? IsDefault = null);

/// <summary>
/// Decks: made from a person's words by the model, changed by asking or by hand (every version kept), their charts' numbers
/// asked of the data, their pictures made one by one, downloaded as PowerPoint in the company's template. Each person's own.
/// </summary>
public static class DeckEndpoints
{
    public const int MostTemplateBytes = 30 * 1024 * 1024;

    public static void MapDecks(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/decks").PersonOnly().RequirePermission(Core.Access.Permissions.Decks);
        g.MapGet("", ListAsync);
        g.MapGet("/config", ConfigAsync);
        g.MapPost("", MakeAsync);
        g.MapGet("/{id:guid}", GetAsync);
        g.MapPut("/{id:guid}", EditAsync);
        g.MapDelete("/{id:guid}", RemoveAsync);
        g.MapPost("/{id:guid}/ask", ChangeAsync);
        g.MapPost("/{id:guid}/refresh", RefreshAsync);
        g.MapPost("/{id:guid}/slides/{index:int}/picture", PictureAsync);
        g.MapGet("/{id:guid}/versions", VersionsAsync);
        g.MapPost("/{id:guid}/versions/{version:int}/restore", RestoreAsync);
        g.MapGet("/{id:guid}/pptx", PptxAsync);

        var admin = app.MapGroup("/api/admin/deck-templates").RequirePermissions(Core.Access.Permissions.SettingsRead, Core.Access.Permissions.SettingsBranding);
        admin.MapGet("", TemplatesAsync);
        admin.MapPost("", UploadAsync).DisableAntiforgery();
        admin.MapPatch("/{id:guid}", ChangeTemplateAsync);
        admin.MapDelete("/{id:guid}", RemoveTemplateAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    private static object Brief(Deck d) => new { d.Id, d.Title, d.TemplateId, d.Version, d.CreatedAt, d.UpdatedAt };

    private static object Full(Deck d, List<DeckSlide> slides) => new { d.Id, d.Title, d.TemplateId, d.Version, d.CreatedAt, d.UpdatedAt, slides };

    private static async Task<IResult> ListAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        var decks = await db.Decks.AsNoTracking().Where(d => d.OwnerId == me.Id).OrderByDescending(d => d.UpdatedAt).Take(DeckMaker.MostDecks).ToListAsync(ct);
        // The first slide of each, for its card.
        return Results.Ok(decks.Select(d => DeckModel.Read(d.Slides) is var s ? new { d.Id, d.Title, d.TemplateId, d.Version, d.UpdatedAt, slides = s.Count, first = s.FirstOrDefault() } : null));
    }

    /// <summary>What a deck can have here: pictures (a picture model this person may use), the company's templates, the databases they may ask.</summary>
    private static async Task<IResult> ConfigAsync(ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ToolRegistry registry, AccessService access, ImageTool images,
        DataQuestions data, CancellationToken ct)
    {
        var me = await Me(p, users);
        var templates = await db.DeckTemplates.AsNoTracking().OrderByDescending(t => t.IsDefault).ThenBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsDefault, t.Theme }).ToListAsync(ct);
        return Results.Ok(new
        {
            pictures = await PicturesAsync(me, registry, access, images, ct),
            templates = templates.Select(t => new { t.Id, t.Name, t.IsDefault, theme = JsonNode.Parse(t.Theme) }),
            sources = (await data.SourcesForAsync(me, ct)).Select(s => s.Name),
        });
    }

    private static async Task<bool> PicturesAsync(AppUser me, ToolRegistry registry, AccessService access, ImageTool images, CancellationToken ct) =>
        await images.UnavailableAsync(ct) is null && await registry.MayUseAsync(await access.MembershipAsync(me, ct), images.Id, ct);

    private static async Task<IResult> MakeAsync(NewDeck body, ClaimsPrincipal p, UserManager<AppUser> users, DeckMaker maker, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (deck, slides, problem) = await maker.CreateAsync(me, body.About ?? "", body.TemplateId, ct);
        return problem ?? Results.Created($"/api/decks/{deck!.Id}", Full(deck, slides));
    }

    private static string Cut(string s, int most) => s.Length > most ? s[..(most - 1)] + "…" : s;

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await db.Decks.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is { } d ? Results.Ok(Full(d, DeckModel.Read(d.Slides))) : Results.NotFound();
    }

    /// <summary>A new version of the deck: its slides, title or template changed.</summary>
    private static void Save(Deck d, AppDbContext db, string title, List<DeckSlide> slides, string note)
    {
        d.Title = title;
        d.Slides = DeckModel.Write(slides);
        d.Version++;
        d.UpdatedAt = DateTimeOffset.UtcNow;
        db.DeckVersions.Add(new DeckVersion { DeckId = d.Id, Version = d.Version, Title = d.Title, Slides = d.Slides, Note = Cut(note, 500) });
    }

    private static async Task<IResult> EditAsync(Guid id, DeckEdit body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        if (body.TemplateId is { } template)
        {
            if (template != Guid.Empty && !await db.DeckTemplates.AnyAsync(t => t.Id == template, ct))
            {
                return AuthEndpoints.Problem(400, "template", "That template is not here.");
            }
            d.TemplateId = template == Guid.Empty ? null : template;
            d.UpdatedAt = DateTimeOffset.UtcNow;
        }
        var title = body.Title?.Trim() is { Length: > 0 } t ? Cut(t, 200) : d.Title;
        if (body.Slides is { } sent)
        {
            if (sent.Count is 0 or > DeckModel.MostSlides)
            {
                return AuthEndpoints.Problem(400, "slides", $"A deck has 1 to {DeckModel.MostSlides} slides.");
            }
            var slides = DeckModel.Clean(sent);
            await maker.CheckFilesAsync(slides, me, ct);
            // A chart given a question by hand gets its numbers now.
            await maker.FillChartsAsync(me, slides, false, ct);
            Save(d, db, title, slides, body.Note ?? "Edited");
        }
        else if (title != d.Title)
        {
            Save(d, db, title, DeckModel.Read(d.Slides), body.Note ?? "Renamed");
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(Full(d, DeckModel.Read(d.Slides)));
    }

    private static async Task<IResult> RemoveAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        db.Decks.Remove(d);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Changed as the person asks: pictures kept where their prompt is the same, new charts' numbers asked of the data.</summary>
    private static async Task<IResult> ChangeAsync(Guid id, DeckChange body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        var change = body.Change?.Trim() ?? "";
        if (change.Length is 0 or > 4000)
        {
            return AuthEndpoints.Problem(400, "change", "Say what to change, up to 4,000 characters.");
        }
        var before = DeckModel.Read(d.Slides);
        var looking = body.Slide is { } n && n >= 0 && n < before.Count ? n : (int?)null;
        var made = await maker.ChangeAsync(me, d.Title, before, change, looking, ct);
        if (made.Problem is not null)
        {
            return made.Problem;
        }
        var pictures = before.Select(s => s.Picture).OfType<DeckPicture>().Where(x => x.FileId is not null).GroupBy(x => x.Prompt).ToDictionary(g => g.Key, g => g.First().FileId);
        foreach (var picture in made.Slides.Select(s => s.Picture).OfType<DeckPicture>().Where(x => x.FileId is null && pictures.ContainsKey(x.Prompt)))
        {
            picture.FileId = pictures[picture.Prompt];
        }
        // A chart the model kept but whose numbers it dropped gets them back from the version before.
        var charts = before.Select(s => s.Chart).OfType<DeckChart>().Where(c => c.Ask is not null && c.Categories.Count > 0).GroupBy(c => c.Ask!.Question).ToDictionary(g => g.Key, g => g.First());
        foreach (var chart in made.Slides.Select(s => s.Chart).OfType<DeckChart>().Where(c => c.Ask is not null && c.Categories.Count == 0 && charts.ContainsKey(c.Ask.Question)))
        {
            var old = charts[chart.Ask!.Question];
            (chart.Categories, chart.Series, chart.Note) = (old.Categories, old.Series, old.Note);
        }
        await maker.CheckFilesAsync(made.Slides, me, ct);
        await maker.FillChartsAsync(me, made.Slides, false, ct);
        Save(d, db, made.Title, made.Slides, change);
        await db.SaveChangesAsync(ct);
        return Results.Ok(Full(d, made.Slides));
    }

    /// <summary>Every chart that asks a database asked again: today's numbers.</summary>
    private static async Task<IResult> RefreshAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        var slides = DeckModel.Read(d.Slides);
        await maker.FillChartsAsync(me, slides, true, ct);
        Save(d, db, d.Title, slides, "Numbers asked again");
        await db.SaveChangesAsync(ct);
        return Results.Ok(Full(d, slides));
    }

    /// <summary>A slide's picture made from its prompt (once: a slide that has one keeps it), kept in the deck as it is.</summary>
    private static async Task<IResult> PictureAsync(Guid id, int index, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, ToolRegistry registry,
        AccessService access, ImageTool images, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        var slides = DeckModel.Read(d.Slides);
        if (index < 0 || index >= slides.Count || slides[index].Picture is not { Prompt.Length: > 0 } picture)
        {
            return AuthEndpoints.Problem(400, "picture", "That slide asks for no picture.");
        }
        if (picture.FileId is not null)
        {
            return Results.Ok(Full(d, slides));
        }
        if (!await PicturesAsync(me, registry, access, images, ct))
        {
            return AuthEndpoints.Problem(409, "picture", "There is no picture model you may use: an admin adds one, or lets you use it.");
        }
        var (file, problem) = await maker.PictureAsync(me, picture.Prompt, ct);
        if (file is null)
        {
            return AuthEndpoints.Problem(502, "picture", problem ?? "The picture could not be made.");
        }
        // The deck as it is now (it may have changed while the picture was made): the picture goes to the slides that ask for it.
        var now = await db.Decks.SingleAsync(x => x.Id == id, ct);
        var current = DeckModel.Read(now.Slides);
        foreach (var asking in current.Select(s => s.Picture).OfType<DeckPicture>().Where(x => x.FileId is null && x.Prompt == picture.Prompt))
        {
            asking.FileId = file;
        }
        now.Slides = DeckModel.Write(current);
        now.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(Full(now, current));
    }

    private static async Task<IResult> VersionsAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Decks.AnyAsync(d => d.Id == id && d.OwnerId == me.Id, ct))
        {
            return Results.NotFound();
        }
        return Results.Ok(await db.DeckVersions.AsNoTracking().Where(v => v.DeckId == id).OrderByDescending(v => v.Version).Take(200)
            .Select(v => new { v.Version, v.Title, v.Note, v.CreatedAt }).ToListAsync(ct));
    }

    private static async Task<IResult> RestoreAsync(Guid id, int version, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, DeckMaker maker, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        if (await db.DeckVersions.AsNoTracking().SingleOrDefaultAsync(v => v.DeckId == id && v.Version == version, ct) is not { } old)
        {
            return Results.NotFound();
        }
        var slides = DeckModel.Read(old.Slides);
        await maker.CheckFilesAsync(slides, me, ct);
        Save(d, db, old.Title, slides, $"Version {version} put back");
        await db.SaveChangesAsync(ct);
        return Results.Ok(Full(d, slides));
    }

    /// <summary>The deck as PowerPoint, in its template (or the built-in look).</summary>
    private static async Task<IResult> PptxAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await db.Decks.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.OwnerId == me.Id, ct) is not { } d)
        {
            return Results.NotFound();
        }
        var slides = DeckModel.Read(d.Slides);
        var ids = slides.Select(s => s.Picture?.FileId).OfType<Guid>().Distinct().ToList();
        var images = await db.ChatAttachments.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.UserId == me.Id && a.Data != null && (a.ContentType == "image/png" || a.ContentType == "image/jpeg" || a.ContentType == "image/gif"))
            .ToDictionaryAsync(a => a.Id, a => new DeckImage(a.Data!, a.ContentType), ct);
        var template = d.TemplateId is { } t ? await db.DeckTemplates.AsNoTracking().Where(x => x.Id == t).Select(x => x.Data).SingleOrDefaultAsync(ct) : null;
        byte[] file;
        try
        {
            file = DeckPptx.Build(d.Title, slides, images, template, clock.GetUtcNow());
        }
        catch (DeckTemplateException ex)
        {
            return AuthEndpoints.Problem(409, "template", $"Its template cannot be used: {ex.Message} Choose another, or the built-in look.");
        }
        var name = string.Concat(d.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '"' ? '-' : c)).Trim();
        return Results.File(file, "application/vnd.openxmlformats-officedocument.presentationml.presentation", (name.Length > 0 ? name : "deck") + ".pptx");
    }

    private static async Task<IResult> TemplatesAsync(AppDbContext db, CancellationToken ct)
    {
        var templates = await db.DeckTemplates.AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsDefault, t.Theme, t.CreatedAt, size = t.Data.Length }).ToListAsync(ct);
        return Results.Ok(templates.Select(t => new { t.Id, t.Name, t.IsDefault, theme = JsonNode.Parse(t.Theme), t.CreatedAt, t.size }));
    }

    /// <summary>A company template (.pptx or .potx): read now (its masters, layouts and theme), so a broken one is found here.</summary>
    private static async Task<IResult> UploadAsync(HttpRequest request, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (request.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MostTemplateBytes + 1024 * 1024;
        }
        if (!request.HasFormContentType || (await request.ReadFormAsync(ct)) is not { Files: [var upload] } form)
        {
            return AuthEndpoints.Problem(400, "file", "Send one .pptx or .potx file.");
        }
        if (upload.Length is 0 or > MostTemplateBytes)
        {
            return AuthEndpoints.Problem(400, "file", "A template is at most 30 MB.");
        }
        using var m = new MemoryStream();
        await upload.CopyToAsync(m, ct);
        var data = m.ToArray();
        string theme;
        try
        {
            theme = DeckPptx.Theme(data);
            // Built once with a slide of each kind: a template that cannot carry a deck is refused now.
            DeckPptx.Build("Check", [new DeckSlide { Layout = "title", Title = "Check" }, new DeckSlide { Layout = "bullets", Title = "Check", Bullets = ["Check"] }], new Dictionary<Guid, DeckImage>(), data, DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is DeckTemplateException or System.Xml.XmlException)
        {
            return AuthEndpoints.Problem(400, "file", ex is DeckTemplateException ? ex.Message : $"The template's XML does not read: {ex.Message}");
        }
        var name = (form["name"].ToString().Trim() is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(upload.FileName)).Trim();
        name = name.Length > 100 ? name[..100] : name.Length == 0 ? "Template" : name;
        if (await db.DeckTemplates.AnyAsync(t => t.Name == name, ct))
        {
            return AuthEndpoints.Problem(409, "name", "Another template has that name.");
        }
        var first = !await db.DeckTemplates.AnyAsync(ct);
        var template = new DeckTemplate { Name = name, Data = data, Theme = theme, IsDefault = first };
        db.DeckTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("deck.template.add", name);
        return Results.Created($"/api/admin/deck-templates/{template.Id}", new { template.Id, template.Name, template.IsDefault, theme = JsonNode.Parse(theme), template.CreatedAt, size = data.Length });
    }

    private static async Task<IResult> ChangeTemplateAsync(Guid id, TemplateChange body, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.DeckTemplates.SingleOrDefaultAsync(t => t.Id == id, ct) is not { } template)
        {
            return Results.NotFound();
        }
        if (body.Name?.Trim() is { Length: > 0 } name)
        {
            if (await db.DeckTemplates.AnyAsync(t => t.Name == name && t.Id != id, ct))
            {
                return AuthEndpoints.Problem(409, "name", "Another template has that name.");
            }
            template.Name = name.Length > 100 ? name[..100] : name;
        }
        if (body.IsDefault is { } isDefault)
        {
            if (isDefault)
            {
                await db.DeckTemplates.Where(t => t.IsDefault && t.Id != id).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsDefault, false), ct);
            }
            template.IsDefault = isDefault;
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("deck.template.update", template.Name);
        return Results.Ok(new { template.Id, template.Name, template.IsDefault, theme = JsonNode.Parse(template.Theme), template.CreatedAt, size = template.Data.Length });
    }

    private static async Task<IResult> RemoveTemplateAsync(Guid id, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.DeckTemplates.SingleOrDefaultAsync(t => t.Id == id, ct) is not { } template)
        {
            return Results.NotFound();
        }
        db.DeckTemplates.Remove(template);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("deck.template.remove", template.Name);
        return Results.NoContent();
    }
}
