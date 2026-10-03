using Llm.Core.Chat;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Chat;

public sealed partial class ChatService
{
    internal const string ReaderSummaryPrompt =
        "You summarize a conversation between a person and an AI assistant for a reader who was not there.\n" +
        "Start with a title line (# ...). Then, in short headed sections with bullet points: what was asked; what was found, decided " +
        "or made; the answers, figures and code that matter (code short, in fenced blocks); the sources, files, repositories and links " +
        "named; and what is left open. Keep names, numbers and links exact. Leave out greetings, repetition and dead ends. " +
        "Write in the language the person writes in, at most about 600 words. Write only the summary.";

    /// <summary>The branch on screen summarized for a reader (an export): made now, kept nowhere.</summary>
    public async Task<string> SummaryForReaderAsync(AppUser user, Conversation conversation, CancellationToken ct)
    {
        var email = user.Email!.ToLowerInvariant();
        var (model, modelName, refusal) = await ModelForAsync(user, conversation, null, ct);
        if (refusal is not null)
        {
            throw new ChatGatewayException(refusal);
        }
        var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id).ToDictionaryAsync(m => m.Id, ct);
        var path = PathTo(all, conversation.CurrentLeafId);
        if (path.Count == 0)
        {
            throw new ChatGatewayException("the chat has nothing to summarize yet");
        }
        return await SummarizeAsync(null, path, model, modelName, email, ct, ReaderSummaryPrompt);
    }
}
