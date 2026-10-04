using System.Globalization;
using Llm.Core.Chat;
using Llm.Core.Data;
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

    /// <summary>
    /// A chat's files for its tools (read_file, Python): its project's first (in the order they
    /// were added), then those its questions carried, oldest first.
    /// </summary>
    public static async Task<List<Guid>> FileIdsAsync(AppDbContext db, Guid conversationId, CancellationToken ct)
    {
        var project = await db.Conversations.AsNoTracking().Where(c => c.Id == conversationId).Select(c => c.ProjectId).SingleOrDefaultAsync(ct);
        var ids = project is { } pid
            ? await db.ProjectFiles.AsNoTracking().Where(f => f.ProjectId == pid).OrderBy(f => f.AddedAt).Select(f => f.AttachmentId).ToListAsync(ct)
            : [];
        var lists = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.AttachmentsJson != null)
            .OrderBy(m => m.Sequence).Select(m => m.AttachmentsJson).ToListAsync(ct);
        return [.. ids.Concat(lists.SelectMany(ParseIds)).Distinct()];
    }

    /// <summary>
    /// A project's files as every answer of its chats reads them: text inline (cut to the
    /// same size as an attachment, the rest by read_file), the others by name. Past their
    /// room, with the embedder, by name too: their passages about each question come with it.
    /// </summary>
    private async Task<string> ProjectFilesAsync(Project project, bool canReadFiles, Knowledge.Retrieval.Plan byPassages, CancellationToken ct)
    {
        var files = await db.ProjectFiles.AsNoTracking().Where(f => f.ProjectId == project.Id).OrderBy(f => f.AddedAt)
            .Join(db.ChatAttachments, f => f.AttachmentId, a => a.Id, (f, a) => a).ToListAsync(ct);
        if (files.Count == 0)
        {
            return "";
        }
        var text = new System.Text.StringBuilder("\n\nThe project's files (every conversation of the project has them):");
        var room = chat.CurrentValue.InlineAttachmentChars * 3;
        foreach (var f in files)
        {
            if (byPassages.Has(f.Id))
            {
                text.Append("\n[project file: ").Append(f.FileName).Append(CultureInfo.InvariantCulture, $" ({f.Text.Length:N0} characters): the passages that match each question come with it")
                    .Append(canReadFiles ? "; read_file reads any part]" : "]");
                continue;
            }
            if (f.Kind == "image" || f.Kind == "file" || f.Text.Length == 0 || room <= 0)
            {
                text.Append("\n[project file: ").Append(f.FileName).Append(f.Kind == "image" ? " (a picture)" : canReadFiles ? " (read_file or run_python can open it)" : "").Append(']');
                continue;
            }
            var shown = Inline(f, Math.Min(chat.CurrentValue.InlineAttachmentChars, room), canReadFiles);
            room -= shown.Length;
            text.Append("\n<project_file name=\"").Append(f.FileName.Replace("\"", "'", StringComparison.Ordinal)).Append("\">\n").Append(shown).Append("\n</project_file>");
        }
        return text.ToString();
    }

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
