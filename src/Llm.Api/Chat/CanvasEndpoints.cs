using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>A canvas the person starts in a chat.</summary>
public sealed record NewCanvas(string? Title = null, string? Kind = null, string? Language = null, string? Content = null);

/// <summary>
/// The person's save. <see cref="BaseVersion"/> is the version they edited: when the canvas
/// moved on since (the model changed it), the save is refused, and nothing is lost on either side.
/// </summary>
public sealed record CanvasSave(int BaseVersion, string? Content = null, string? Title = null, string? Language = null, string? Summary = null);

/// <summary>A chat's canvases for the page: list, open, save, history, restore, export.</summary>
public static partial class CanvasEndpoints
{
    public static void MapCanvases(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/chat").RequireAuthorization();
        g.MapGet("/conversations/{id:guid}/canvases", ListAsync);
        g.MapPost("/conversations/{id:guid}/canvases", CreateAsync);
        g.MapGet("/canvases/{id:guid}", GetAsync);
        g.MapPut("/canvases/{id:guid}", SaveAsync);
        g.MapDelete("/canvases/{id:guid}", DeleteAsync);
        g.MapGet("/canvases/{id:guid}/versions", VersionsAsync);
        g.MapGet("/canvases/{id:guid}/versions/{number:int}", VersionAsync);
        g.MapPost("/canvases/{id:guid}/versions/{number:int}/restore", RestoreAsync);
        g.MapGet("/canvases/{id:guid}/export", ExportAsync);
    }

    private static async Task<AppUser> Me(ClaimsPrincipal p, UserManager<AppUser> users) => (await users.GetUserAsync(p))!;

    /// <summary>A canvas of one of the person's chats, tracked; null when it is not theirs.</summary>
    private static Task<Canvas?> OwnedAsync(AppDbContext db, Guid id, AppUser me, CancellationToken ct) =>
        db.Canvases.SingleOrDefaultAsync(c => c.Id == id && db.Conversations.Any(x => x.Id == c.ConversationId && x.UserId == me.Id), ct);

    private static object Shape(Canvas c, bool content) => new
    {
        c.Id, c.ConversationId, c.Title, c.Kind, c.Language, c.Version, c.CreatedAt, c.UpdatedAt,
        lines = Canvases.Split(c.Content).Length,
        content = content ? c.Content : null,
    };

    private static async Task<IResult> ListAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Canvases canvases, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, ct))
        {
            return Results.NotFound();
        }
        return Results.Ok((await canvases.ListAsync(id, ct)).Select(c => Shape(c, false)));
    }

    private static async Task<IResult> CreateAsync(Guid id, NewCanvas body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Canvases canvases, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (!await db.Conversations.AnyAsync(c => c.Id == id && c.UserId == me.Id, ct))
        {
            return Results.NotFound();
        }
        try
        {
            var canvas = await canvases.CreateAsync(id, body.Title, body.Kind, body.Language, body.Content, Canvases.Person, ct);
            return Results.Created($"/api/chat/canvases/{canvas.Id}", Shape(canvas, true));
        }
        catch (CanvasException ex)
        {
            return AuthEndpoints.Problem(400, "canvas", ex.Message);
        }
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        return await OwnedAsync(db, id, me, ct) is { } c ? Results.Ok(Shape(c, true)) : Results.NotFound();
    }

    private static async Task<IResult> SaveAsync(Guid id, CanvasSave body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Canvases canvases, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OwnedAsync(db, id, me, ct) is not { } canvas)
        {
            return Results.NotFound();
        }
        try
        {
            if (body.Language is not null && canvas.Kind == "code")
            {
                canvas.Language = Canvases.LanguageOf(body.Language);
            }
            if (!await canvases.ChangeAsync(canvas, body.Content ?? canvas.Content, body.Title, Canvases.Person, body.Summary, body.BaseVersion, ct))
            {
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(Shape(canvas, true));
        }
        catch (CanvasConflictException ex)
        {
            return Results.Json(new
            {
                status = "changed", error = "The model changed this canvas while you edited it. Your text is still in the editor: copy what you need, then load its version.",
                version = ex.Version,
            }, statusCode: 409);
        }
        catch (CanvasException ex)
        {
            return AuthEndpoints.Problem(400, "canvas", ex.Message);
        }
    }

    private static async Task<IResult> DeleteAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OwnedAsync(db, id, me, ct) is not { } canvas)
        {
            return Results.NotFound();
        }
        db.Canvases.Remove(canvas);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Every version, newest first, without their text.</summary>
    private static async Task<IResult> VersionsAsync(Guid id, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OwnedAsync(db, id, me, ct) is null)
        {
            return Results.NotFound();
        }
        var list = await db.CanvasVersions.AsNoTracking().Where(v => v.CanvasId == id).OrderByDescending(v => v.Number)
            .Select(v => new { v.Number, v.Title, v.Author, v.Summary, v.CreatedAt }).ToListAsync(ct);
        return Results.Ok(list);
    }

    private static async Task<IResult> VersionAsync(Guid id, int number, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OwnedAsync(db, id, me, ct) is null)
        {
            return Results.NotFound();
        }
        var v = await db.CanvasVersions.AsNoTracking().SingleOrDefaultAsync(v => v.CanvasId == id && v.Number == number, ct);
        return v is null ? Results.NotFound() : Results.Ok(new { v.Number, v.Title, v.Author, v.Summary, v.CreatedAt, v.Content });
    }

    private static async Task<IResult> RestoreAsync(Guid id, int number, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, Canvases canvases, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OwnedAsync(db, id, me, ct) is not { } canvas)
        {
            return Results.NotFound();
        }
        try
        {
            return await canvases.RestoreAsync(canvas, number, ct) is null ? Results.NotFound() : Results.Ok(Shape(canvas, true));
        }
        catch (CanvasConflictException)
        {
            return AuthEndpoints.Problem(409, "changed", "The canvas changed at the same moment. Try again.");
        }
    }

    /// <summary>
    /// The canvas as a file: format=md (Markdown), docx (Word, built here), pdf (that Word file
    /// through LibreOffice in the sandbox) or file (a code canvas as its file).
    /// </summary>
    private static async Task<IResult> ExportAsync(Guid id, string? format, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, SandboxClient sandbox,
        TimeProvider clock, CancellationToken ct)
    {
        var me = await Me(p, users);
        if (await OwnedAsync(db, id, me, ct) is not { } canvas)
        {
            return Results.NotFound();
        }
        var name = FileBase(canvas.Title);
        switch (format)
        {
            case "md":
                return Results.File(Encoding.UTF8.GetBytes(canvas.Content), "text/markdown; charset=utf-8", name + ".md");
            case "file" or null:
                return Results.File(Encoding.UTF8.GetBytes(canvas.Content), "text/plain; charset=utf-8", FileName(canvas));
            case "docx":
                return Results.File(CanvasDocx.Build(canvas.Title, canvas.Content, clock.GetUtcNow()),
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document", name + ".docx");
            case "pdf":
                if (sandbox.Unavailable() is { } why)
                {
                    return AuthEndpoints.Problem(503, "no_sandbox", why.Replace("run code", "make PDFs", StringComparison.Ordinal));
                }
                try
                {
                    return Results.File(await PdfAsync(sandbox, CanvasDocx.Build(canvas.Title, canvas.Content, clock.GetUtcNow()), ct), "application/pdf", name + ".pdf");
                }
                catch (SandboxException ex)
                {
                    return AuthEndpoints.Problem(502, "pdf", ex.Message);
                }
            default:
                return AuthEndpoints.Problem(400, "format", "Export as md, docx, pdf or file.");
        }
    }

    /// <summary>
    /// The first line marks the job for the fake sandbox in tests. LibreOffice as for a
    /// document's pages (DocumentPages): its own profile, its pipe beside the job.
    /// </summary>
    private const string PdfScript = """
        # canvas pdf
        import json, os, subprocess
        tmp = os.path.abspath(os.environ.get('TMPDIR', '.tmp'))
        os.makedirs(tmp, exist_ok=True)
        env = dict(os.environ, OSL_SOCKET_PATH=tmp)
        cmd = ['/usr/lib/libreoffice/program/soffice.bin', '-env:UserInstallation=file://' + os.path.abspath('.lo'),
               '--headless', '--norestore', '--convert-to', 'pdf', '--outdir', '.', 'canvas.docx']
        for _ in range(3):
            r = subprocess.run(cmd, capture_output=True, text=True, timeout=110, env=env)
            if r.returncode != 81:
                break
        print(json.dumps({'ok': os.path.exists('canvas.pdf'), 'error': (r.stderr or r.stdout).strip()[-200:]}))
        """;

    private static async Task<byte[]> PdfAsync(SandboxClient sandbox, byte[] docx, CancellationToken ct)
    {
        var run = await sandbox.RunAsync(PdfScript, [new SandboxFile("canvas.docx", docx)], 150, ct);
        return run.Files.FirstOrDefault(f => f.Name == "canvas.pdf")?.Bytes
            ?? throw new SandboxException(run.Killed ?? run.Error ?? "LibreOffice could not make the PDF.");
    }

    /// <summary>A code canvas's file name: its title when that is one ("parser.py"), else the title with its language's extension.</summary>
    public static string FileName(Canvas canvas)
    {
        var name = FileBase(canvas.Title);
        if (canvas.Kind != "code")
        {
            return name + ".md";
        }
        if (Path.HasExtension(name) && Path.GetExtension(name).Length <= 6)
        {
            return name;
        }
        return name + "." + Extension(canvas.Language);
    }

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = "py", ["py"] = "py", ["javascript"] = "js", ["js"] = "js", ["jsx"] = "jsx", ["typescript"] = "ts", ["ts"] = "ts", ["tsx"] = "tsx",
        ["csharp"] = "cs", ["c#"] = "cs", ["cs"] = "cs", ["java"] = "java", ["kotlin"] = "kt", ["go"] = "go", ["rust"] = "rs", ["ruby"] = "rb", ["php"] = "php",
        ["swift"] = "swift", ["c"] = "c", ["cpp"] = "cpp", ["c++"] = "cpp", ["sql"] = "sql", ["bash"] = "sh", ["shell"] = "sh", ["sh"] = "sh", ["powershell"] = "ps1",
        ["html"] = "html", ["css"] = "css", ["json"] = "json", ["yaml"] = "yaml", ["yml"] = "yaml", ["xml"] = "xml", ["markdown"] = "md", ["md"] = "md",
        ["dockerfile"] = "dockerfile", ["toml"] = "toml", ["ini"] = "ini", ["r"] = "r", ["scala"] = "scala", ["lua"] = "lua", ["dart"] = "dart", ["svg"] = "svg",
        ["mermaid"] = "mmd", ["vue"] = "vue", ["graphql"] = "graphql",
    };

    private static string Extension(string? language) => language is not null && Extensions.TryGetValue(language, out var e) ? e : "txt";

    /// <summary>The title without what a file system refuses.</summary>
    private static string FileBase(string title)
    {
        var name = Unsafe().Replace(title, " ").Trim().Trim('.');
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? "canvas" : name.Length > 80 ? name[..80].TrimEnd() : name;
    }

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1f]+")]
    private static partial Regex Unsafe();
}
