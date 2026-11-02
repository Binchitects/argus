using System.Security.Claims;
using Llm.Api.Chat.Tools;
using Llm.Api.Endpoints;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

/// <summary>
/// Back to just before a question of the branch on screen (its words go back in the box), or with <paramref name="Keep"/> to
/// just after its answer. <paramref name="Forget"/>: the memories written from what goes are forgotten too.
/// </summary>
public sealed record RewindRequest(Guid MessageId, bool Keep = false, bool Forget = false);

/// <summary>
/// A chat rewound to one of its questions, as Claude Code's rewind goes back to a message: what came after it leaves the branch
/// on screen, so the model reads neither those messages nor a summary that covered them. They stay in the chat as another
/// branch, as an edited question's do (the version arrows show them once something else is asked there), so Undo puts the
/// branch's end back where it was.
/// </summary>
public static class ChatRewind
{
    /// <summary>
    /// Where rewinding the branch <paramref name="path"/> to its question <paramref name="question"/> leaves it: its new last
    /// message (null: nothing is left) and the messages that go, in order; null when that is no question of the branch.
    /// </summary>
    public static (Guid? Leaf, List<ChatMessage> Gone)? Cut(List<ChatMessage> path, Guid question, bool keep)
    {
        var at = path.FindIndex(m => m.Id == question);
        if (at < 0 || path[at].Role != "user")
        {
            return null;
        }
        // Kept: its answer too, up to the next message of a person's.
        var end = !keep ? at : path.FindIndex(at + 1, m => m.Role == "user") is var next and >= 0 ? next : path.Count;
        return (end == 0 ? null : path[end - 1].Id, path[end..]);
    }

    /// <summary>The memories of <paramref name="person"/>'s that a chat wrote from <paramref name="since"/> on: said in it, or proposed from it as it went quiet.</summary>
    public static IQueryable<Memory> Written(AppDbContext db, Guid person, Guid chat, DateTimeOffset since) =>
        db.Memories.Where(m => m.UserId == person && m.SourceId == chat && (m.Source == Memory.FromChat || m.Source == Memory.Reflected) && m.CreatedAt >= since);
}

public static partial class ChatEndpoints
{
    /// <summary>
    /// The chat when <paramref name="me"/> may rewind it now: its owner (a room goes back for everyone in it: its owner), while
    /// no answer is written in it, here or by Code Arena.
    /// </summary>
    private static async Task<(Conversation? Chat, IResult? Refusal)> RewindableAsync(AppDbContext db, Guid id, AppUser me, AnswerJobs jobs)
    {
        if (await Owned(db, id, me, Need.Read) is not { } c)
        {
            return (null, Results.NotFound());
        }
        if (c.UserId != me.Id)
        {
            return (null, AuthEndpoints.Problem(403, "room", "Only the room's owner rewinds it: it goes back for everyone in it."));
        }
        if (jobs.IsAnswering(id))
        {
            return (null, AuthEndpoints.Problem(409, "answering", "An answer is being written: stop it, or wait for it, to rewind."));
        }
        return CodeArenaSync.Answering(c)
            ? (null, AuthEndpoints.Problem(409, "code_arena", "Code Arena is answering in this chat: rewind once its turn ends."))
            : (c, null);
    }

    /// <summary>
    /// What rewinding to a question would do, for the confirmation: how many messages of the branch go, where the branch would
    /// end, and the memories written from them (the person may forget them too).
    /// </summary>
    private static async Task<IResult> RewindPreviewAsync(Guid id, Guid messageId, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs,
        CancellationToken ct, bool keep = false)
    {
        var me = await Me(p, users);
        var (c, refusal) = await RewindableAsync(db, id, me, jobs);
        if (c is null)
        {
            return refusal!;
        }
        if (ChatRewind.Cut(await ChatHistoryTool.BranchAsync(db, c, ct), messageId, keep) is not { } cut)
        {
            return AuthEndpoints.Problem(400, "message", "Rewind to a question of the branch on screen.");
        }
        var memories = cut.Gone.Count == 0 ? []
            : await ChatRewind.Written(db, me.Id, c.Id, cut.Gone[0].CreatedAt).OrderBy(m => m.CreatedAt).Select(m => new { m.Id, m.Text, m.Proposed }).ToListAsync(ct);
        return Results.Ok(new { gone = cut.Gone.Count, currentLeafId = cut.Leaf, memories });
    }

    /// <summary>
    /// Rewinds the chat to a question: the branch on screen ends just before it (or after its answer, kept), so the next answer
    /// reads the chat from there (the whole messages, where a summary covered some that go). A digest that read what goes is
    /// written anew; with Forget, the memories written from it go too. Undo: PUT …/leaf with the previous end, exact.
    /// </summary>
    private static async Task<IResult> RewindAsync(Guid id, RewindRequest body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, AnswerJobs jobs,
        RoomHub rooms, CancellationToken ct)
    {
        var me = await Me(p, users);
        var (c, refusal) = await RewindableAsync(db, id, me, jobs);
        if (c is null)
        {
            return refusal!;
        }
        if (ChatRewind.Cut(await ChatHistoryTool.BranchAsync(db, c, ct), body.MessageId, body.Keep) is not { } cut)
        {
            return AuthEndpoints.Problem(400, "message", "Rewind to a question of the branch on screen.");
        }
        if (cut.Gone.Count == 0)
        {
            return AuthEndpoints.Problem(400, "nothing", "Nothing comes after its answer: there is nothing to rewind.");
        }
        var previous = c.CurrentLeafId;
        c.CurrentLeafId = cut.Leaf;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        if (cut.Gone.Any(m => m.Sequence <= c.DigestedSequence))
        {
            // What it is about, for finding it again (search_history), is written anew from the branch as it is now.
            (c.Digest, c.DigestEmbedding, c.DigestedSequence) = (null, null, 0);
        }
        var forgotten = 0;
        if (body.Forget)
        {
            var written = await ChatRewind.Written(db, me.Id, c.Id, cut.Gone[0].CreatedAt).Select(m => m.Id).ToListAsync(ct);
            // What they replaced is read again, as when one is forgotten.
            await db.Memories.Where(m => m.ReplacedBy != null && written.Contains(m.ReplacedBy.Value)).ExecuteUpdateAsync(s => s.SetProperty(m => m.ReplacedBy, (Guid?)null), ct);
            forgotten = await db.Memories.Where(m => written.Contains(m.Id)).ExecuteDeleteAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        if (c.IsRoom)
        {
            rooms.Changed(c.Id, "rewound");
        }
        return Results.Ok(new { currentLeafId = cut.Leaf, previousLeafId = previous, gone = cut.Gone.Count, forgotten });
    }
}
