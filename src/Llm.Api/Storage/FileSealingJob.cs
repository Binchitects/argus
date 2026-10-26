using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Llm.Api.Storage;

/// <summary>
/// Seals the files stored before they were sealed at rest (or by an app with no APP_KEY): a few at a time, read as they are and
/// saved again through the converters, which seal them. Every hour, and as the app starts.
/// </summary>
public sealed partial class FileSealingJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<FileSealingJob> logger) : BackgroundService
{
    private const int Batch = 20;

    /// <summary>Not sealed yet, per table: (query of their ids, count).</summary>
    private static readonly (string Table, string Where)[] Columns =
    [
        ("chat_attachments", """("Data" IS NOT NULL AND substring("Data" from 1 for 8) <> @magic) OR ("Sound" IS NOT NULL AND substring("Sound" from 1 for 8) <> @magic) """ +
            """OR ("Text" <> '' AND left("Text", 16) <> chr(1) || 'ARENA-SEALED-1:')"""),
        ("attachment_pages", """substring("Data" from 1 for 8) <> @magic"""),
        ("deck_templates", """substring("Data" from 1 for 8) <> @magic"""),
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await SealAsync(stoppingToken) is > 0 and var n)
                {
                    LogSealed(logger, n);
                }
            }
            catch (Exception ex) when (ex is DbUpdateException or NpgsqlException or InvalidOperationException && !stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex.Message);
            }
            try
            {
                await Task.Delay(TimeSpan.FromHours(1), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Seals every file not sealed yet; returns how many. Nothing without a key.</summary>
    public async Task<int> SealAsync(CancellationToken ct)
    {
        var done = 0;
        while (true)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (db.GetService<IDbContextOptions>().FindExtension<FileKeyExtension>()?.Key is null)
            {
                return done;
            }
            var attachments = await IdsAsync(db, """SELECT "Id" AS "Value" FROM chat_attachments WHERE """ + Columns[0].Where, ct);
            var pages = await IdsAsync(db, """SELECT DISTINCT "AttachmentId" AS "Value" FROM attachment_pages WHERE """ + Columns[1].Where, ct);
            var templates = await IdsAsync(db, """SELECT "Id" AS "Value" FROM deck_templates WHERE """ + Columns[2].Where, ct);
            if (attachments.Count + pages.Count + templates.Count == 0)
            {
                return done;
            }
            foreach (var a in await db.ChatAttachments.Where(a => attachments.Contains(a.Id)).ToListAsync(ct))
            {
                db.Entry(a).Property(x => x.Data).IsModified = a.Data is not null;
                db.Entry(a).Property(x => x.Sound).IsModified = a.Sound is not null;
                db.Entry(a).Property(x => x.Text).IsModified = a.Text.Length > 0;
                done++;
            }
            foreach (var p in await db.AttachmentPages.Where(p => pages.Contains(p.AttachmentId)).ToListAsync(ct))
            {
                db.Entry(p).Property(x => x.Data).IsModified = true;
                done++;
            }
            foreach (var t in await db.DeckTemplates.Where(t => templates.Contains(t.Id)).ToListAsync(ct))
            {
                db.Entry(t).Property(x => x.Data).IsModified = true;
                done++;
            }
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>A batch of ids the query finds (its WHERE is one of <see cref="Columns"/>, constants only).</summary>
    private static Task<List<Guid>> IdsAsync(AppDbContext db, string query, CancellationToken ct)
    {
        var sql = query + " LIMIT " + Batch.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return db.Database.SqlQueryRaw<Guid>(sql, new NpgsqlParameter("magic", FileSealer.Prefix.ToArray())).ToListAsync(ct);
    }

    /// <summary>How many files are not sealed yet, for Admin → Compliance.</summary>
    public static async Task<long> UnsealedAsync(AppDbContext db, CancellationToken ct)
    {
        long total = 0;
        foreach (var (table, where) in Columns)
        {
            var sql = "SELECT count(*) AS \"Value\" FROM " + table + " WHERE " + where;
            total += await db.Database.SqlQueryRaw<long>(sql, new NpgsqlParameter("magic", FileSealer.Prefix.ToArray())).SingleAsync(ct);
        }
        return total;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} files stored before were sealed at rest")]
    private static partial void LogSealed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sealing files at rest failed: {Reason}; trying again later")]
    private static partial void LogFailed(ILogger logger, string reason);
}
