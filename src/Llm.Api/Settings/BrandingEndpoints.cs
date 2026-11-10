using System.Security.Cryptography;
using Llm.Api.Access;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Settings;

/// <summary>
/// The app's icon an admin chose (Settings → Branding), beside its name: in the sidebar, on the sign-in page, in the browser's
/// tab, in notifications and on a phone's home screen. Without one, the icon the app ships with.
/// </summary>
public static class BrandingEndpoints
{
    public const string Icon = "icon";

    /// <summary>The most an icon may weigh: a picture for a small square, not a photograph.</summary>
    public const int MostIconBytes = 512 * 1024;

    public static void MapBranding(this IEndpointRouteBuilder app)
    {
        // Public, as the name is: the sign-in page shows it before anyone signs in.
        app.MapGet("/api/branding/icon", ReadAsync).AllowAnonymous("The app's icon: shown on the sign-in page, as its name is.");
        var admin = app.MapGroup("/api/admin/branding").RequirePermissions(Core.Access.Permissions.SettingsRead, Core.Access.Permissions.SettingsBranding);
        admin.MapPut("/icon", UploadAsync).DisableAntiforgery();
        admin.MapDelete("/icon", RemoveAsync);
    }

    /// <summary>The icon's address, which changes with it (a browser never keeps an old one); null: the app's own.</summary>
    public static async Task<(string Url, string Type)?> IconAsync(AppDbContext db, CancellationToken ct) =>
        await db.SiteImages.AsNoTracking().Where(i => i.Name == Icon).Select(i => new { i.Sha256, i.ContentType }).FirstOrDefaultAsync(ct) is { } icon
            ? ($"/api/branding/icon?v={icon.Sha256[..12]}", icon.ContentType)
            : null;

    private static async Task<IResult> ReadAsync(HttpContext http, AppDbContext db, CancellationToken ct)
    {
        if (await db.SiteImages.AsNoTracking().FirstOrDefaultAsync(i => i.Name == Icon, ct) is not { } icon)
        {
            return Results.NotFound(new { error = "No icon was chosen: the app's own is /favicon.svg." });
        }
        var etag = $"\"{icon.Sha256}\"";
        http.Response.Headers.ETag = etag;
        // Its address carries its hash: kept a year; asked for by the bare address, checked each time.
        http.Response.Headers.CacheControl = http.Request.Query.ContainsKey("v") ? "public, max-age=31536000, immutable" : "no-cache";
        http.Response.Headers.XContentTypeOptions = "nosniff";
        // An SVG opened by itself runs nothing: no script, no request, no form.
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; sandbox";
        if (http.Request.Headers.IfNoneMatch == etag)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.File(icon.Data, icon.ContentType);
    }

    private static async Task<IResult> UploadAsync(HttpRequest request, AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (!request.HasFormContentType || (await request.ReadFormAsync(ct)) is not { Files: [var upload] })
        {
            return AuthEndpoints.Problem(400, "file", "Send one picture: PNG, JPEG, WebP or SVG.");
        }
        if (upload.Length is 0 or > MostIconBytes)
        {
            return AuthEndpoints.Problem(400, "file", $"An icon is at most {MostIconBytes / 1024} KB: a picture for a small square.");
        }
        using var m = new MemoryStream();
        await upload.CopyToAsync(m, ct);
        var data = m.ToArray();
        if (TypeOf(data) is not { } type)
        {
            return AuthEndpoints.Problem(400, "file", "That is not a PNG, JPEG, WebP or SVG picture.");
        }
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var by = request.HttpContext.User.Identity?.Name;
        if (await db.SiteImages.FirstOrDefaultAsync(i => i.Name == Icon, ct) is { } icon)
        {
            (icon.Data, icon.ContentType, icon.Sha256, icon.UpdatedAt, icon.UpdatedBy) = (data, type, sha, DateTimeOffset.UtcNow, by);
        }
        else
        {
            db.SiteImages.Add(new SiteImage { Name = Icon, Data = data, ContentType = type, Sha256 = sha, UpdatedBy = by });
        }
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("branding.icon", upload.FileName, detail: $"{type}, {data.Length:N0} bytes");
        return Results.Ok(new { url = $"/api/branding/icon?v={sha[..12]}", type, size = data.Length });
    }

    private static async Task<IResult> RemoveAsync(AppDbContext db, Audit audit, CancellationToken ct)
    {
        if (await db.SiteImages.Where(i => i.Name == Icon).ExecuteDeleteAsync(ct) > 0)
        {
            await audit.WriteAsync("branding.icon.reset");
        }
        return Results.NoContent();
    }

    /// <summary>What the picture is, from its own bytes (never its name or what the browser said); null: none of the four.</summary>
    public static string? TypeOf(byte[] data)
    {
        var d = data.AsSpan();
        if (d.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }
        if (d.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }
        if (d.Length >= 12 && d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return IsSvg(data) ? "image/svg+xml" : null;
    }

    /// <summary>An SVG document: XML (no DTD, so no entities) whose root is svg.</summary>
    private static bool IsSvg(byte[] data)
    {
        try
        {
            using var reader = System.Xml.XmlReader.Create(new MemoryStream(data),
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true });
            return reader.MoveToContent() == System.Xml.XmlNodeType.Element && reader.LocalName == "svg";
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
