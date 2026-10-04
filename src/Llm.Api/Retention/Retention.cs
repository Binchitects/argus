using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Identity;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Retention;

/// <summary>Configuration section "Retention": how long chats and their files are kept.</summary>
public sealed class RetentionOptions
{
    /// <summary>Days a chat is kept after its last message, for people whose groups set none; null or 0: forever.</summary>
    public int? Days { get; set; }

    /// <summary>How often the sweep looks for chats past their time.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>What was removed: chats and files.</summary>
public sealed record Erased(int Chats, int Files)
{
    public static readonly Erased None = new(0, 0);

    public Erased Plus(Erased other) => new(Chats + other.Chats, Files + other.Files);
}

/// <summary>
/// How long each person's chats are kept, and their deletion: by the sweep once they are
/// older, and by the person themselves. Nothing of a person on legal hold is erased: the
/// sweep passes them by, and a chat they delete is hidden instead, until the hold ends.
/// </summary>
public sealed class Retention(AppDbContext db, AccessService access, IOptionsMonitor<RetentionOptions> options, Audit audit, TimeProvider clock)
{
    /// <summary>Days this person's chats are kept: the shortest their groups set, else the company's; null: forever.</summary>
    public async Task<int?> DaysForAsync(AppUser user, CancellationToken ct = default)
    {
        var groups = (await access.MembershipAsync(user, ct)).Groups;
        var set = await db.Groups.AsNoTracking().Where(g => groups.Contains(g.Id) && g.RetentionDays != null).Select(g => g.RetentionDays!.Value).ToListAsync(ct);
        return set.Count > 0 ? set.Min() : options.CurrentValue.Days is > 0 and var days ? days : null;
    }

    /// <summary>The person deletes chats: erased with their files, or only hidden while they are on legal hold.</summary>
    public async Task DeleteAsync(AppUser user, IReadOnlyCollection<Guid> conversations, CancellationToken ct = default)
    {
        if (user.LegalHoldSince is not null)
        {
            var now = clock.GetUtcNow();
            await db.Conversations.Where(c => c.UserId == user.Id && conversations.Contains(c.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, now), ct);
            return;
        }
        await EraseAsync(user.Id, conversations, ct);
    }

    /// <summary>Removes chats for good, with the files that none of the person's other chats, nor a project, still uses.</summary>
    public async Task<Erased> EraseAsync(Guid userId, IReadOnlyCollection<Guid> conversations, CancellationToken ct = default)
    {
        if (conversations.Count == 0)
        {
            return Erased.None;
        }
        var mine = db.Conversations.IgnoreQueryFilters().Where(c => c.UserId == userId);
        var used = await db.ChatMessages.AsNoTracking().Where(m => conversations.Contains(m.ConversationId) && m.AttachmentsJson != null)
            .Select(m => m.AttachmentsJson).ToListAsync(ct);
        var elsewhere = await db.ChatMessages.AsNoTracking()
            .Where(m => !conversations.Contains(m.ConversationId) && m.AttachmentsJson != null && mine.Any(c => c.Id == m.ConversationId))
            .Select(m => m.AttachmentsJson).ToListAsync(ct);
        var keep = elsewhere.SelectMany(ChatService.ParseIds).ToHashSet();
        var files = used.SelectMany(ChatService.ParseIds).Where(a => !keep.Contains(a)).Distinct().ToList();
        // A project's file stays with its project.
        var inProjects = await db.AssistantFiles.AsNoTracking().Where(f => files.Contains(f.AttachmentId)).Select(f => f.AttachmentId).ToListAsync(ct);
        files.RemoveAll(inProjects.Contains);
        var chats = await mine.Where(c => conversations.Contains(c.Id)).ExecuteDeleteAsync(ct);
        var removed = files.Count == 0 ? 0 : await db.ChatAttachments.Where(a => a.UserId == userId && files.Contains(a.Id)).ExecuteDeleteAsync(ct);
        return new Erased(chats, removed);
    }

    /// <summary>
    /// One pass over everyone not on hold: chats untouched for longer than their retention go,
    /// with their files, and so do files nothing uses any more that are as old (uploaded and
    /// never sent). Chats hidden under a hold that has ended go too. Each person's deletion is
    /// audited, with counts, never content.
    /// </summary>
    public async Task<Erased> SweepAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var total = Erased.None;
        foreach (var person in await db.Users.AsNoTracking().Where(u => u.LegalHoldSince == null).OrderBy(u => u.UserName).ToListAsync(ct))
        {
            var hidden = await db.Conversations.IgnoreQueryFilters().Where(c => c.UserId == person.Id && c.DeletedAt != null).Select(c => c.Id).ToListAsync(ct);
            var theirs = await EraseAsync(person.Id, hidden, ct);
            if (theirs.Chats > 0)
            {
                await audit.WriteAsync("retention.delete", person.UserName, detail: $"{theirs.Chats} chats and {theirs.Files} files they deleted while on legal hold", actor: Sweeper);
            }
            total = total.Plus(theirs);
            if (await DaysForAsync(person, ct) is not { } days)
            {
                continue;
            }
            var cutoff = now.AddDays(-days);
            var old = await db.Conversations.IgnoreQueryFilters().Where(c => c.UserId == person.Id && c.UpdatedAt < cutoff).Select(c => c.Id).ToListAsync(ct);
            var past = (await EraseAsync(person.Id, old, ct)).Plus(new Erased(0, await EraseUnusedFilesAsync(person.Id, cutoff, ct)));
            if (past.Chats + past.Files > 0)
            {
                await audit.WriteAsync("retention.delete", person.UserName, detail: $"{past.Chats} chats and {past.Files} files older than {days} days", actor: Sweeper);
            }
            total = total.Plus(past);
        }
        return total;
    }

    /// <summary>Stands in for "who did it" in the audit log when the sweep deletes.</summary>
    public static readonly AppUser Sweeper = new() { Id = Guid.Empty, UserName = "retention" };

    /// <summary>The person's files older than the cutoff that no chat of theirs (hidden ones too) and no project uses.</summary>
    private async Task<int> EraseUnusedFilesAsync(Guid userId, DateTimeOffset cutoff, CancellationToken ct)
    {
        var candidates = await db.ChatAttachments.AsNoTracking().Where(a => a.UserId == userId && a.CreatedAt < cutoff && !db.AssistantFiles.Any(f => f.AttachmentId == a.Id))
            .Select(a => a.Id).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return 0;
        }
        var lists = await db.ChatMessages.AsNoTracking()
            .Where(m => m.AttachmentsJson != null && db.Conversations.IgnoreQueryFilters().Any(c => c.Id == m.ConversationId && c.UserId == userId))
            .Select(m => m.AttachmentsJson).ToListAsync(ct);
        var used = lists.SelectMany(ChatService.ParseIds).ToHashSet();
        var unused = candidates.Where(id => !used.Contains(id)).ToList();
        return unused.Count == 0 ? 0 : await db.ChatAttachments.Where(a => a.UserId == userId && unused.Contains(a.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Puts a person on legal hold, or ends it. Ending it erases the chats they deleted meanwhile. Audited.</summary>
    public async Task<Erased> SetHoldAsync(AppUser user, bool hold, string? reason, CancellationToken ct = default)
    {
        if (hold)
        {
            var why = reason?.Trim() ?? "";
            if (why.Length is 0 or > 500)
            {
                throw new PeopleException("Say why the hold is placed (a matter or a ticket), in up to 500 characters.");
            }
            var renewed = user.LegalHoldSince is not null;
            user.LegalHoldSince ??= clock.GetUtcNow();
            user.LegalHoldReason = why;
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(renewed ? "person.legal_hold_change" : "person.legal_hold", user.UserName, detail: why);
            return Erased.None;
        }
        if (user.LegalHoldSince is null)
        {
            return Erased.None;
        }
        var since = user.LegalHoldSince.Value;
        user.LegalHoldSince = null;
        user.LegalHoldReason = null;
        await db.SaveChangesAsync(ct);
        var hidden = await db.Conversations.IgnoreQueryFilters().Where(c => c.UserId == user.Id && c.DeletedAt != null).Select(c => c.Id).ToListAsync(ct);
        var erased = await EraseAsync(user.Id, hidden, ct);
        await audit.WriteAsync("person.legal_hold_end", user.UserName,
            detail: $"held since {since:yyyy-MM-dd}; {erased.Chats} chats and {erased.Files} files they deleted meanwhile are now erased");
        return erased;
    }
}

/// <summary>Runs <see cref="Retention.SweepAsync"/> soon after start, then every <see cref="RetentionOptions.Interval"/>.</summary>
public sealed partial class RetentionSweep(IServiceScopeFactory scopes, IOptionsMonitor<RetentionOptions> options, TimeProvider clock, ILogger<RetentionSweep> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wait = TimeSpan.FromMinutes(2);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(wait, clock, stoppingToken);
                wait = options.CurrentValue.Interval > TimeSpan.Zero ? options.CurrentValue.Interval : TimeSpan.FromHours(1);
                await using var scope = scopes.CreateAsyncScope();
                var erased = await scope.ServiceProvider.GetRequiredService<Retention>().SweepAsync(stoppingToken);
                if (erased.Chats + erased.Files > 0)
                {
                    LogSwept(logger, erased.Chats, erased.Files);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A background failure must never stop the app.
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Retention: {Chats} chats and {Files} files past their time were deleted")]
    private static partial void LogSwept(ILogger logger, int chats, int files);

    [LoggerMessage(Level = LogLevel.Error, Message = "Retention: the sweep failed; trying again later")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
