using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Storage;

/// <summary>Files to delete, by id.</summary>
public sealed record DeleteFilesRequest(Guid[] Ids);

/// <summary>A person's own room for files in megabytes (0: no limit); null gives them the company's again.</summary>
public sealed record QuotaRequest(int? Megabytes);

/// <summary>A clean-up's age (days) or the backups it keeps, as previewed; <paramref name="Only"/> the models chosen.</summary>
public sealed record CleanupRequest(int? Days = null, int? Keep = null, string[]? Only = null);

/// <summary>Admin → Storage: what takes room and where, the chat's files to browse, download and delete, rooms per person, and the clean-ups.</summary>
public static class StorageEndpoints
{
    /// <summary>The most files one list sends: the totals count them all.</summary>
    public const int MaxRows = 1000;

    public static void AddStorage(IServiceCollection services, IConfiguration config)
    {
        services.Configure<StorageOptions>(config.GetSection("Storage"));
        services.AddSingleton<StorageCache>();
        services.AddScoped<StorageDisks>();
        services.AddScoped<StorageFiles>();
        services.AddScoped<StorageQuotas>();
        services.AddScoped<StoragePlaces>();
        services.AddScoped<StorageReport>();
        services.AddScoped<StorageCleanups>();
        services.AddSingleton<StorageWatch>();
        services.AddHostedService(sp => sp.GetRequiredService<StorageWatch>());
    }

    public static void MapStorage(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/storage").RequireAuthorization(AdminEndpoints.Policy);
        g.MapGet("/", async (StorageReport report, CancellationToken ct) => Results.Ok(await report.BuildAsync(ct)));
        g.MapGet("/files", ListAsync);
        g.MapGet("/files/{id:guid}/download", DownloadAsync);
        g.MapPost("/files/delete", DeleteAsync);
        g.MapGet("/people", async (StorageFiles files, StorageReport report, CancellationToken ct) =>
            Results.Ok(new { people = await files.PeopleAsync(ct), personMegabytes = report.Settings.PersonMegabytes }));
        g.MapPut("/people/{id:guid}/quota", QuotaAsync);
        g.MapGet("/cleanups/{kind}", async (string kind, int? days, int? keep, StorageCleanups cleanups, CancellationToken ct) =>
            await cleanups.PlanAsync(kind, days, keep, ct) is { } plan ? Results.Ok(plan) : Results.NotFound());
        g.MapPost("/cleanups/{kind}", RunAsync);
    }

    private static async Task<IResult> ListAsync(HttpRequest request, StorageFiles files, CancellationToken ct)
    {
        var q = request.Query;
        string? Word(string key, IReadOnlySet<string>? allowed = null) =>
            q[key].ToString().Trim() is { Length: > 0 and <= 200 } v && (allowed is null || allowed.Contains(v)) ? v : null;
        DateTimeOffset? Time(string key) => DateTimeOffset.TryParse(q[key], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
        var filter = new FileFilter(
            Person: Guid.TryParse(q["person"], out var person) ? person : null,
            Kind: Word("kind"),
            Origin: Word("origin", StorageFiles.Origins),
            State: Word("state", StorageFiles.States),
            Search: Word("q"),
            From: Time("from"),
            Before: Time("before"),
            MinBytes: long.TryParse(q["min"], out var min) ? min : null,
            Sort: Word("sort") is "new" or "old" ? q["sort"].ToString() : "size",
            Take: MaxRows);
        var (rows, total) = await files.ListAsync(filter, ct);
        return Results.Ok(new { rows, total, capped = total.Count > rows.Count });
    }

    /// <summary>The file itself, to save, never to render. Audited: an admin read someone's file.</summary>
    private static async Task<IResult> DownloadAsync(Guid id, AppDbContext db, Audit audit, HttpContext http)
    {
        var a = await db.ChatAttachments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, http.RequestAborted);
        if (a is null)
        {
            return Results.NotFound();
        }
        var owner = await db.Users.AsNoTracking().Where(u => u.Id == a.UserId).Select(u => u.UserName).FirstOrDefaultAsync(http.RequestAborted);
        await audit.WriteAsync("storage.download", owner, detail: $"file {a.Id} ({a.Kind}, {StorageDisks.Size(a.Data?.LongLength ?? a.Text.Length)})");
        http.Response.Headers.XContentTypeOptions = "nosniff";
        http.Response.Headers.CacheControl = "no-store";
        return a.Data is not null
            ? Results.File(a.Data, "application/octet-stream", a.FileName)
            : Results.File(System.Text.Encoding.UTF8.GetBytes(a.Text), "text/plain; charset=utf-8", a.FileName);
    }

    /// <summary>Deletes the files chosen, never one of a person on legal hold. Audited per person, with counts and sizes.</summary>
    private static async Task<IResult> DeleteAsync(DeleteFilesRequest body, StorageFiles files, StorageCache cache, AppDbContext db, Audit audit, CancellationToken ct)
    {
        var ids = (body.Ids ?? []).Distinct().ToList();
        if (ids.Count is 0 or > 10_000)
        {
            return AuthEndpoints.Problem(400, "ids", "Choose between 1 and 10,000 files.");
        }
        var (deleted, held) = await files.DeleteAsync(ids, ct);
        var people = deleted.Keys.ToList();
        var names = await db.Users.AsNoTracking().Where(u => people.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
        foreach (var (person, amount) in deleted)
        {
            await audit.WriteAsync("storage.delete", names.GetValueOrDefault(person), detail: $"{amount.Count} file{(amount.Count == 1 ? "" : "s")} ({StorageDisks.Size(amount.Bytes)}), chosen on the storage page");
        }
        cache.Forget();
        var total = deleted.Values.Aggregate(Amount.None, (a, b) => a.Plus(b));
        return Results.Ok(new { deleted = total, held });
    }

    private static async Task<IResult> QuotaAsync(Guid id, QuotaRequest body, PeopleService people, StorageQuotas quotas, Audit audit, CancellationToken ct)
    {
        if (await people.FindAsync(id) is not { } user)
        {
            return Results.NotFound();
        }
        if (body.Megabytes is < 0 or > 100_000_000)
        {
            return AuthEndpoints.Problem(400, "megabytes", "A room is 0 (no limit) to 100,000,000 MB.");
        }
        await quotas.SetAsync(id, body.Megabytes, ct);
        await audit.WriteAsync("storage.quota", user.UserName,
            detail: body.Megabytes switch { null => "the company's room again", 0 => "no limit", { } mb => $"{mb:N0} MB" });
        return Results.NoContent();
    }

    private static async Task<IResult> RunAsync(string kind, CleanupRequest body, StorageCleanups cleanups, CancellationToken ct)
    {
        if (!StorageCleanups.Kinds.Contains(kind))
        {
            return Results.NotFound();
        }
        try
        {
            return Results.Ok(await cleanups.RunAsync(kind, body.Days, body.Keep, body.Only, ct));
        }
        catch (CleanupException ex)
        {
            return AuthEndpoints.Problem(409, "cleanup", ex.Message);
        }
    }
}
