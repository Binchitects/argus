using System.Text.Json;
using Llm.Api.Identity;
using Llm.Api.Storage;
using Llm.Core.Compliance;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Llm.Api.Compliance;

public sealed class DataRequestException(string message) : Exception(message);

/// <summary>What an erasure did, kept with the request and given to the person.</summary>
/// <param name="Erased">Each kind of their data, and how many were erased.</param>
/// <param name="Cleared">What stays for others, with them taken out of it.</param>
/// <param name="Kept">What is kept, and why.</param>
/// <param name="Backups">When the backups taken before let go of the rest.</param>
public sealed record ErasureCertificate(Guid Request, string Person, string? Email, DateTimeOffset ErasedAt, string ErasedBy,
    IReadOnlyList<ErasedPart> Erased, IReadOnlyList<ErasedPart> Cleared, IReadOnlyList<string> Kept, string Backups);

public sealed record ErasedPart(string What, long Count);

/// <summary>
/// People's requests about their data (GDPR Articles 15–22): each taken in with its month, answered and audited. A copy (access,
/// portability) is their export; a correction is made on their profile; restriction and objection stop everything that learns from
/// their data; erasure removes everything of theirs, clears them out of what others keep, and certifies what is kept and why.
/// </summary>
public sealed partial class DataRequests(AppDbContext db, PeopleService people, Audit audit, Gateway.SpendLog spend, StoragePlaces places,
    IOptionsMonitor<StorageOptions> storage, Notifications.Notifier notifier, TimeProvider clock, ILogger<DataRequests> logger)
{
    /// <summary>How long the company has to answer (Article 12(3): one month).</summary>
    public static readonly TimeSpan Month = TimeSpan.FromDays(30);

    public async Task<DataRequest> TakeAsync(AppUser person, string kind, string? details, string by, CancellationToken ct)
    {
        if (!DataRequest.Kinds.Contains(kind))
        {
            throw new DataRequestException($"A request is one of: {string.Join(", ", DataRequest.Kinds)}.");
        }
        if (details?.Trim() is { Length: > 4000 })
        {
            throw new DataRequestException("Say it in 4,000 characters or fewer.");
        }
        var now = clock.GetUtcNow();
        var request = new DataRequest
        {
            UserId = person.Id, Person = person.UserName!, Email = person.Email, Kind = kind, Details = string.IsNullOrWhiteSpace(details) ? null : details.Trim(),
            ReceivedAt = now, DueAt = now + Month, ReceivedBy = by,
        };
        db.DataRequests.Add(request);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("privacy.request", person.UserName, detail: $"{kind}, due {request.DueAt:yyyy-MM-dd}, taken in by {by}");
        await notifier.ToAdminsAsync(new Notifications.News("compliance", $"{person.UserName} asked for {Words(kind)}",
            $"To be answered by {request.DueAt:yyyy-MM-dd}.", "/admin/compliance?tab=requests", $"data-request:{request.Id}"), ct);
        return request;
    }

    /// <summary>What a request asks for, in words.</summary>
    public static string Words(string kind) => kind switch
    {
        DataRequest.Access => "a copy of their data",
        DataRequest.Portability => "their data to take elsewhere",
        DataRequest.Rectification => "a correction",
        DataRequest.Restriction => "their data's processing restricted",
        DataRequest.Objection => "no more processing of their data (objection)",
        DataRequest.Erasure => "their data erased",
        _ => kind,
    };

    private async Task<DataRequest> OpenAsync(Guid id, CancellationToken ct)
    {
        var request = await db.DataRequests.SingleOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException();
        return request.Status == DataRequest.Open ? request : throw new DataRequestException("It was answered already.");
    }

    /// <summary>Answered: what was done, in words (their export given, their profile corrected...).</summary>
    public async Task<DataRequest> CompleteAsync(Guid id, string? outcome, AppUser actor, CancellationToken ct)
    {
        var request = await OpenAsync(id, ct);
        if (request.Kind == DataRequest.Erasure)
        {
            throw new DataRequestException("An erasure is answered by erasing: Erase everything.");
        }
        if (string.IsNullOrWhiteSpace(outcome))
        {
            throw new DataRequestException("Say what was done.");
        }
        return await CloseAsync(request, DataRequest.Done, outcome.Trim(), actor, ct);
    }

    /// <summary>Refused, with the reason the law allows (manifestly unfounded, or the data must be kept): they are told.</summary>
    public async Task<DataRequest> RefuseAsync(Guid id, string? reason, AppUser actor, CancellationToken ct)
    {
        var request = await OpenAsync(id, ct);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DataRequestException("Say why: the person is told, and may complain to the supervisory authority.");
        }
        return await CloseAsync(request, DataRequest.Refused, reason.Trim(), actor, ct);
    }

    private async Task<DataRequest> CloseAsync(DataRequest request, string status, string outcome, AppUser actor, CancellationToken ct)
    {
        request.Status = status;
        request.Outcome = outcome.Length > 4000 ? outcome[..4000] : outcome;
        request.DoneAt = clock.GetUtcNow();
        request.DoneBy = actor.UserName;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(status == DataRequest.Done ? "privacy.done" : "privacy.refuse", request.Person, detail: $"{request.Kind}: {request.Outcome}");
        if (request.UserId is { } who)
        {
            await notifier.SendAsync(who, new Notifications.News("compliance", status == DataRequest.Done ? $"Your request for {Words(request.Kind)} was answered" : $"Your request for {Words(request.Kind)} was refused",
                request.Outcome, "/account#your-data-rights"), ct);
        }
        return request;
    }

    /// <summary>Restricts (or lets go of the restriction on) the processing of their data: nothing learns from it while it holds.</summary>
    public async Task SetRestrictedAsync(AppUser person, bool restricted, string why, CancellationToken ct)
    {
        if ((person.ProcessingRestrictedAt is not null) == restricted)
        {
            return;
        }
        person.ProcessingRestrictedAt = restricted ? clock.GetUtcNow() : null;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(restricted ? "privacy.restrict" : "privacy.lift", person.UserName, detail: why);
    }

    /// <summary>Answers a restriction or an objection by restricting their processing.</summary>
    public async Task<DataRequest> RestrictAsync(Guid id, AppUser actor, CancellationToken ct)
    {
        var request = await OpenAsync(id, ct);
        if (request.Kind is not (DataRequest.Restriction or DataRequest.Objection))
        {
            throw new DataRequestException("Only a restriction or an objection is answered by restricting.");
        }
        var person = await db.Users.SingleOrDefaultAsync(u => u.Id == request.UserId, ct) ?? throw new DataRequestException("The person is gone.");
        await SetRestrictedAsync(person, true, $"their request ({request.Kind})", ct);
        return await CloseAsync(request, DataRequest.Done,
            "Their data is kept, and nothing learns from it any more: no memories, digests, reflection or skills learned from their work, until the restriction is lifted.", actor, ct);
    }

    /// <summary>What is counted and erased with the person (their rows go with them), table by table.</summary>
    private static readonly (string Table, string Column, string What)[] Theirs =
    [
        ("conversations", "UserId", "chats and rooms of theirs, with every message, branch, summary and digest"),
        ("chat_attachments", "UserId", "files"),
        ("memories", "UserId", "memories"),
        ("memory_changes", "UserId", "memory changes kept for Undo"),
        ("skills", "UserId", "skills"),
        ("skill_changes", "UserId", "skill changes kept for Undo"),
        ("meetings", "UserId", "meetings, with their recordings and minutes"),
        ("media_items", "OwnerId", "pictures, videos and sounds"),
        ("media_jobs", "OwnerId", "studio jobs"),
        ("media_stories", "OwnerId", "video stories"),
        ("apps", "OwnerId", "apps"),
        ("decks", "OwnerId", "decks"),
        ("workflows", "OwnerId", "workflows"),
        ("scheduled_tasks", "UserId", "scheduled tasks"),
        ("plans", "UserId", "plans"),
        ("projects", "UserId", "projects"),
        ("panels", "UserId", "panel discussions"),
        ("notifications", "UserId", "notifications"),
        ("data_queries", "UserId", "questions asked of the company's data"),
        ("data_boards", "OwnerId", "data boards"),
        ("code_reviews", "UserId", "code reviews"),
        ("code_fixes", "UserId", "fixes"),
        ("studio_workspaces", "UserId", "Code Studio workspaces"),
        ("answer_feedback", "UserId", "ratings of answers"),
        ("arena_matches", "UserId", "model comparisons"),
        ("person_credentials", "UserId", "connected accounts (plugins, GitLab)"),
        ("push_subscriptions", "UserId", "devices notified"),
        ("safeguard_events", "UserId", "safeguard events"),
        ("access_requests", "UserId", "access requests"),
        ("tool_calls", "UserId", "tool calls of their chats"),
        ("prompt_log", "UserId", "questions asked with their API keys, with the answers (the prompt log)"),
        ("app_data", "UserId", "what apps kept for them"),
    ];

    /// <summary>
    /// Erases everything of theirs: counted first for the certificate, then their tool calls and apps' data (which would otherwise
    /// stay without them), their words in others' rooms cleared, their name taken off others' meeting actions, their requests in the
    /// gateway's log made no longer theirs, and the person deleted with all that is theirs. Refused under legal hold.
    /// </summary>
    public async Task<DataRequest> EraseAsync(Guid id, AppUser actor, CancellationToken ct)
    {
        var request = await OpenAsync(id, ct);
        if (request.Kind != DataRequest.Erasure)
        {
            throw new DataRequestException("Only an erasure request erases.");
        }
        var person = await db.Users.SingleOrDefaultAsync(u => u.Id == request.UserId, ct) ?? throw new DataRequestException("The person is gone already.");
        if (person.LegalHoldSince is not null)
        {
            throw new DataRequestException($"{person.UserName} is on legal hold: their data must be kept (Article 17(3)(e)). Refuse the request with that reason, or end the hold first.");
        }
        var userId = person.Id;
        var erased = new List<ErasedPart>();
        foreach (var (table, column, what) in Theirs)
        {
            var n = await CountAsync($"""SELECT count(*) FROM "{table}" WHERE "{column}" = @id""", userId, ct);
            if (n > 0)
            {
                erased.Add(new ErasedPart(what, n));
            }
        }
        var messages = await CountAsync("""SELECT count(*) FROM chat_messages m JOIN conversations c ON c."Id" = m."ConversationId" WHERE c."UserId" = @id""", userId, ct);
        if (messages > 0)
        {
            erased.Insert(1, new ErasedPart("messages in those chats", messages));
        }

        // What would stay without them, gone; what others keep, with them taken out.
        var cleared = new List<ErasedPart>();
        await ExecAsync("""DELETE FROM tool_calls WHERE "UserId" = @id""", userId, ct);
        await ExecAsync("""DELETE FROM app_data WHERE "UserId" = @id""", userId, ct);
        var words = await ExecAsync("""
            UPDATE chat_messages m SET "Content" = '(erased at their request)', "Quote" = NULL, "AuthorId" = NULL
            FROM conversations c WHERE c."Id" = m."ConversationId" AND m."AuthorId" = @id AND c."UserId" <> @id
            """, userId, ct);
        if (words > 0)
        {
            cleared.Add(new ErasedPart("their messages in other people's rooms: the words erased, the place kept", words));
        }
        var actions = await ExecAsync("""UPDATE meeting_actions SET "PersonId" = NULL WHERE "PersonId" = @id""", userId, ct);
        if (actions > 0)
        {
            cleared.Add(new ErasedPart("action items given to them in other people's meetings: no longer theirs", actions));
        }
        var kept = new List<string>();
        // The app's own ledger of model requests: kept for the company's totals, no longer theirs.
        var email0 = person.Email?.Trim().ToLowerInvariant();
        var ledger = await db.GatewayRequests.Where(r => r.UserId == userId || (email0 != null && r.Person == email0))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Person, "erased").SetProperty(r => r.UserId, (Guid?)null).SetProperty(r => r.KeyAlias, (string?)null)
                .SetProperty(r => r.ConversationId, (Guid?)null), ct);
        if (ledger > 0)
        {
            cleared.Add(new ErasedPart("their requests in the app's ledger of model requests: kept for the company's totals, no longer theirs", ledger));
        }
        try
        {
            if (person.Email is { } email && await spend.ForgetAsync(email, ct) is > 0 and var rows)
            {
                cleared.Add(new ErasedPart("their requests in the gateway's log: kept for the company's totals, with who, their address and what was sent and answered cleared", rows));
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            LogGatewayLog(logger, ex.Message);
            kept.Add($"Their requests in the gateway's log could not be cleared ({ex.Message}): clear them by hand, where \"user\" is {person.Email}.");
        }
        var audited = await db.AuditEvents.CountAsync(e => e.ActorId == userId || e.Target == person.UserName, ct);
        kept.Add($"{audited} entries of the audit log that name them: the law requires them kept (GDPR 17(3)(b) and (e); HIPAA keeps such records six years), and the audit log's hash chain forbids changing them.");
        kept.Add("This request and its certificate, naming them: the proof that the request was answered.");

        await people.DeleteAsync(actor, person);
        request.UserId = null;
        var now = clock.GetUtcNow();
        var keep = Math.Max(1, storage.CurrentValue.BackupsKept);
        var backups = places.Backups();
        var newest = backups.Backups.Where(b => b.At is not null).MaxBy(b => b.At)?.At;
        var backupWords = newest is null
            ? "No backup was found that holds their data."
            : $"Backups taken before {now:yyyy-MM-dd} still hold their data, untouched and unread; scripts/backup.sh keeps the newest {keep}, so the last of them is gone by about {now.AddDays(keep):yyyy-MM-dd}. Restoring one of them would bring them back: erase them again after a restore.";
        var certificate = new ErasureCertificate(request.Id, request.Person, request.Email, now, actor.UserName!, erased, cleared, kept, backupWords);
        request.Certificate = JsonSerializer.Serialize(certificate, Json);
        await audit.WriteAsync("privacy.erase", request.Person, detail: string.Join("; ", erased.Select(e => $"{e.Count} {e.What}")));
        return await CloseAsync(request, DataRequest.Done, $"Erased: {string.Join(", ", erased.Select(e => $"{e.Count} {e.What}"))}. The certificate says what is kept and why.", actor, ct);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The certificate as a page to give the person.</summary>
    public static string CertificateText(ErasureCertificate c) =>
        $"""
        # Certificate of erasure

        {c.Person}{(c.Email is null ? "" : $" ({c.Email})")} asked for their data to be erased. It was erased on {c.ErasedAt:yyyy-MM-dd HH:mm} UTC by {c.ErasedBy}
        (request {c.Request}).

        ## Erased

        {string.Join("\n", c.Erased.Select(e => $"- {e.Count} {e.What}"))}

        ## Taken out of what others keep

        {(c.Cleared.Count == 0 ? "- Nothing" : string.Join("\n", c.Cleared.Select(e => $"- {e.Count} {e.What}")))}

        ## Kept, and why

        {string.Join("\n", c.Kept.Select(k => $"- {k}"))}

        ## Backups

        {c.Backups}
        """;

    private async Task<long> CountAsync(string sql, Guid id, CancellationToken ct) =>
        await db.Database.SqlQueryRaw<long>(sql.Replace("count(*)", "count(*) AS \"Value\"", StringComparison.Ordinal), new NpgsqlParameter("id", id)).SingleAsync(ct);

    private async Task<int> ExecAsync(string sql, Guid id, CancellationToken ct) =>
        await db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter("id", id)], ct);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An erasure could not clear the gateway's log: {Reason}")]
    private static partial void LogGatewayLog(ILogger logger, string reason);
}
