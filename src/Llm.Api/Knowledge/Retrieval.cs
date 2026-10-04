using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Knowledge;

/// <summary>
/// A chat's long files and projects' files, cut into passages and embedded in the background, one at a time,
/// so answers take the passages that match each question. A file is embedded once, however often it is asked for.
/// </summary>
public sealed partial class FileIndex(IServiceScopeFactory scopes, ILogger<FileIndex> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<bool>> _waiting = new();

    /// <summary>Embeds the file when its turn comes; true once its passages are stored.</summary>
    public Task<bool> QueueAsync(Guid attachment)
    {
        var mine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = _waiting.GetOrAdd(attachment, mine);
        if (waiting == mine)
        {
            _queue.Writer.TryWrite(attachment);
        }
        return waiting.Task;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            var done = false;
            try
            {
                done = await IndexAsync(id, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, id, ex);
            }
            finally
            {
                if (_waiting.TryRemove(id, out var waiting))
                {
                    waiting.TrySetResult(done);
                }
            }
        }
    }

    private async Task<bool> IndexAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var embedder = scope.ServiceProvider.GetRequiredService<Embedder>();
        var had = await db.KnowledgeDocuments.AsNoTracking().Where(d => d.AttachmentId == id).Select(d => new { d.Id, d.Model }).FirstOrDefaultAsync(ct);
        if (had?.Model == embedder.Model)
        {
            return true;
        }
        if (!await embedder.ReadyAsync(ct) || await db.ChatAttachments.AsNoTracking().Where(a => a.Id == id && a.Kind == "text")
                .Select(a => new { a.FileName, a.Text }).SingleOrDefaultAsync(ct) is not { } file || file.Text.Length == 0)
        {
            return false;
        }
        var chunks = Chunker.Split(file.Text);
        var vectors = chunks.Count == 0 ? [] : await embedder.DocumentsAsync([.. chunks.Select(c => Chunker.ForEmbedding(file.FileName, c))], ct);
        await scope.ServiceProvider.GetRequiredService<KnowledgeStore>().SaveAsync(new KnowledgeDocument
        {
            Id = had?.Id ?? Guid.CreateVersion7(), AttachmentId = id, Key = id.ToString(), Title = file.FileName, Model = embedder.Model,
        }, chunks, vectors, ct);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "File {Attachment} could not be embedded for retrieval")]
    private static partial void LogFailed(ILogger logger, Guid attachment, Exception ex);
}

/// <summary>
/// Retrieval instead of stuffing: a chat's long files, and a project's files when they do not fit, go to
/// the model as the passages that match each question (with the file's name and lines), not as their
/// first part. Without the embedder, or for a file not embedded yet, they go in as before.
/// </summary>
public sealed class Retrieval(AppDbContext db, Embedder embedder, KnowledgeStore store, FileIndex index, IOptionsMonitor<KnowledgeOptions> options,
    IOptionsMonitor<Chat.ChatOptions> chat)
{
    /// <summary>Of a long file, what goes where it was attached: its start, so the model knows what it is.</summary>
    public const int HeadChars = 2_000;

    /// <summary>The files that go by their passages in one answer, with their names.</summary>
    public sealed record Plan(IReadOnlyDictionary<Guid, string> Files)
    {
        public static readonly Plan None = new(new Dictionary<Guid, string>());

        public bool Has(Guid id) => Files.ContainsKey(id);
    }

    /// <summary>
    /// Which files go by their passages: text attachments longer than what goes in the question, and a project's
    /// files when together they pass the room project files have (each one longer than that, always). Those not
    /// embedded yet are embedded now, waited for a little (Knowledge:IndexWait); any still not ready go as before.
    /// </summary>
    public async Task<Plan> PlanAsync(Guid? projectId, IEnumerable<ChatAttachment> attachments, CancellationToken ct)
    {
        var inline = chat.CurrentValue.InlineAttachmentChars;
        var wanted = attachments.Where(a => a.Kind == "text" && a.Text.Length > inline).ToDictionary(a => a.Id, a => a.FileName);
        if (projectId is { } pid)
        {
            var files = await db.ProjectFiles.AsNoTracking().Where(f => f.ProjectId == pid)
                .Join(db.ChatAttachments, f => f.AttachmentId, a => a.Id, (f, a) => new { a.Id, a.FileName, a.Kind, Length = a.Text.Length })
                .Where(a => a.Kind == "text" && a.Length > 0).ToListAsync(ct);
            var fit = files.Sum(f => (long)Math.Min(f.Length, inline)) <= inline * 3L;
            foreach (var f in files.Where(f => !fit || f.Length > inline))
            {
                wanted.TryAdd(f.Id, f.FileName);
            }
        }
        if (wanted.Count == 0 || !await embedder.ReadyAsync(ct))
        {
            return Plan.None;
        }
        var ready = await ReadyAsync(wanted.Keys, ct);
        if (ready.Count < wanted.Count)
        {
            try
            {
                await Task.WhenAll(wanted.Keys.Where(id => !ready.Contains(id)).Select(index.QueueAsync)).WaitAsync(options.CurrentValue.IndexWait, ct);
            }
            catch (TimeoutException)
            {
                // The rest go as before this time; they are ready for the next question.
            }
            ready = await ReadyAsync(wanted.Keys, ct);
        }
        return ready.Count == 0 ? Plan.None : new Plan(wanted.Where(w => ready.Contains(w.Key)).ToDictionary());
    }

    private async Task<HashSet<Guid>> ReadyAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        var model = embedder.Model;
        return [.. await db.KnowledgeDocuments.AsNoTracking().Where(d => d.AttachmentId != null && list.Contains(d.AttachmentId.Value) && d.Model == model)
            .Select(d => d.AttachmentId!.Value).ToListAsync(ct)];
    }

    /// <summary>A long file where it was attached: its start, and a note that its passages come with each question.</summary>
    public static string Head(ChatAttachment f, bool canReadFiles)
    {
        var end = f.Text.LastIndexOf('\n', Math.Min(HeadChars, f.Text.Length) - 1);
        var shown = f.Text[..(end > HeadChars / 2 ? end : Math.Min(HeadChars, f.Text.Length))];
        var lines = f.Text.Count(c => c == '\n') + 1;
        var how = canReadFiles ? $" Read any part with read_file (file \"{f.FileName}\"), or find words with search_file." : "";
        return shown + $"\n[This file is long ({f.Text.Length:N0} characters, {lines:N0} lines): its start is above, and the passages that match each question come with the question.{how}]";
    }

    /// <summary>
    /// The passages of the plan's files that best match the question (with the question before it, when it is
    /// short: "and the second one?"), best first, within Knowledge:PassageChars; null when there are none.
    /// </summary>
    public async Task<string?> PassagesAsync(Plan plan, IReadOnlyList<ChatMessage> path, ChatMessage question, CancellationToken ct)
    {
        if (plan.Files.Count == 0)
        {
            return null;
        }
        var asked = question.Content.Trim();
        if (asked.Length < 80 && path.LastOrDefault(m => m.Role == "user" && m.Id != question.Id) is { } before)
        {
            asked = before.Content.Trim() + "\n" + asked;
        }
        if (asked.Length == 0)
        {
            return null;
        }
        List<Passage> found;
        try
        {
            found = await store.SearchFilesAsync(await embedder.QueryAsync(asked, ct), [.. plan.Files.Keys], Math.Max(8, options.CurrentValue.PassageChars / 400), ct);
        }
        catch (EmbedderException)
        {
            return "\n\n[The passages of the long files about this question could not be found (the embedder did not answer): read them with read_file.]";
        }
        var budget = options.CurrentValue.PassageChars;
        var text = new StringBuilder();
        var used = 0;
        foreach (var p in found.Where(p => p.AttachmentId is { } a && plan.Has(a)))
        {
            if (used + p.Text.Length > budget)
            {
                continue;
            }
            used += p.Text.Length;
            text.Append("\n<passage file=\"").Append(Attr(plan.Files[p.AttachmentId!.Value])).Append("\" lines=\"")
                .Append(p.Line.ToString(CultureInfo.InvariantCulture)).Append('-').Append(p.EndLine.ToString(CultureInfo.InvariantCulture)).Append('"')
                .Append(p.Heading is { Length: > 0 } h ? $" section=\"{Attr(h)}\"" : "").Append(">\n").Append(p.Text).Append("\n</passage>");
        }
        return used == 0 ? null :
            "\n\n<passages note=\"The parts of this chat's long files and its project's files that best match this question, best first. " +
            "Answer from them, naming the file and lines; read more of a file with read_file.\">" + text + "\n</passages>";
    }

    /// <summary>The passages go at the end of the question (where the prompt cache has nothing to keep).</summary>
    public static void AddToQuestion(List<(JsonObject Turn, long Weight, ChatMessage Source)> turns, Guid question, string passages)
    {
        var i = turns.FindLastIndex(t => t.Source.Id == question);
        if (i < 0)
        {
            return;
        }
        var (turn, weight, source) = turns[i];
        if (turn["content"] is JsonArray parts && parts.OfType<JsonObject>().FirstOrDefault(p => p["type"]?.GetValue<string>() == "text") is { } part)
        {
            part["text"] = part["text"]!.GetValue<string>() + passages;
        }
        else if (turn["content"] is JsonValue v && v.TryGetValue<string>(out var said))
        {
            turn["content"] = said + passages;
        }
        turns[i] = (turn, weight + passages.Length, source);
    }

    private static string Attr(string s) => s.Replace("\"", "'", StringComparison.Ordinal);
}
