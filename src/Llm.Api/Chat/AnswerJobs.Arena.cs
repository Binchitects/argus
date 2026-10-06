using System.Text.Json;
using Llm.Api.Quality;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

public sealed partial class AnswerJobs
{
    /// <summary>
    /// Arena: the question answered by each model of the match, one after the other, each in its
    /// model's line (two at once could swap an engine that holds few models back and forth).
    /// Every event is blind: "Model A" and "Model B", never the names, until the
    /// person votes. The chat then shows Model A's answer; the next question follows it.
    /// </summary>
    public void StartArena(Job job, Guid questionId, Guid matchId) =>
        Start(job, async (services, user, conversation, ct) =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var question = await db.ChatMessages.SingleOrDefaultAsync(m => m.Id == questionId && m.ConversationId == conversation.Id, CancellationToken.None);
            var match = await db.ArenaMatches.AsNoTracking().SingleOrDefaultAsync(m => m.Id == matchId, CancellationToken.None);
            if (question is null || match is null)
            {
                job.Emit(new { type = "error", message = "The question is gone." });
                return;
            }
            var chat = services.GetRequiredService<ChatService>();
            Guid? endA = null;
            foreach (var (side, model, step) in new[] { ("a", match.ModelA, 1), ("b", match.ModelB, 2) })
            {
                job.Emit(new { type = "arena", id = match.Id, questionId, side, step, of = 2 });
                var first = (Guid?)null;
                var stopped = false;
                async Task EmitAsync(object e)
                {
                    var node = JsonSerializer.SerializeToNode(e, Json)!.AsObject();
                    var type = node["type"]?.GetValue<string>();
                    // The arena ends once, after both answers.
                    if (type == "done")
                    {
                        return;
                    }
                    stopped |= type == "stopped";
                    if (type == "assistant")
                    {
                        node["side"] = side;
                        if (first is null && node["parentId"]?.GetValue<Guid>() == questionId && node["id"]?.GetValue<Guid>() is { } id)
                        {
                            // Kept at once: a page that loads the chat meanwhile hides this answer's model too.
                            first = id;
                            await (side == "a"
                                ? db.ArenaMatches.Where(m => m.Id == match.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.AnswerA, id), CancellationToken.None)
                                : db.ArenaMatches.Where(m => m.Id == match.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.AnswerB, id), CancellationToken.None));
                        }
                    }
                    job.Emit(Arena.Blind(node, match.ModelA, match.ModelB)!);
                }
                await chat.AnswerAsync(user, conversation, question, new AnswerOverrides(model, Hurry: job.Hurry, Line: (m, token) => PlaceAsync(job, m, blind: true, token)), EmitAsync, ct);
                if (side == "a")
                {
                    endA = conversation.CurrentLeafId;
                }
                if (stopped || ct.IsCancellationRequested)
                {
                    // Stopped between the two: B never starts, and every page hears so.
                    if (!stopped)
                    {
                        job.Emit(new { type = "stopped", id = (Guid?)null });
                    }
                    return;
                }
            }
            conversation.CurrentLeafId = endA;
            await db.SaveChangesAsync(CancellationToken.None);
            job.Emit(new { type = "done", id = endA });
        });
}
