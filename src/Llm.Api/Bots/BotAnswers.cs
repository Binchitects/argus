using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Identity;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Bots;

/// <summary>A question asked of a bot, as the platform sent it.</summary>
/// <param name="Thread">The platform's thread (its chat goes on there), unique on the platform.</param>
/// <param name="User">The asker's id on the platform.</param>
/// <param name="Name">The asker's name there, for the replies.</param>
/// <param name="Reply">What posting in the thread needs (channel, ids, a service address): the platform's own.</param>
public sealed record BotQuestion(string Thread, string User, string Name, string Text, JsonObject Reply)
{
    /// <summary>The asker's email address, as the platform knows it: who they are here.</summary>
    public string? Email { get; init; }
}

/// <summary>A chat platform the bots answer on (or email): who asked, how to post in a thread, how it writes.</summary>
public interface IChatPlatform
{
    /// <summary>slack, mattermost, teams, email.</summary>
    string Name { get; }

    /// <summary>As people call it: Slack, Microsoft Teams.</summary>
    string Title { get; }

    /// <summary>The longest post, in characters.</summary>
    int Limit { get; }

    /// <summary>Whether it is set up (its credentials saved).</summary>
    bool Ready { get; }

    PlatformOptions Options { get; }

    /// <summary>Whether a stranger (no account here) is told so; email is not, so a forged sender gets nothing back.</summary>
    bool AnswersStrangers => true;

    /// <summary>The question with what the platform must be asked first: the asker's email, the thread's first message.</summary>
    Task<BotQuestion> ResolveAsync(BotQuestion question, CancellationToken ct);

    /// <summary>Posts in the question's thread; throws with the platform's reason when it cannot.</summary>
    Task PostAsync(BotQuestion question, string text, CancellationToken ct);

    /// <summary>An answer (Markdown) as the platform writes it.</summary>
    string Format(string markdown) => markdown;

    /// <summary>A link as the platform writes it.</summary>
    string Link(string url, string text) => $"[{text}]({url})";

    /// <summary>What ends every answer (email: the chat's link, and how to ask more).</summary>
    string Footer(string link) => "";
}

/// <summary>
/// Answers the bots' questions in the background, each with services of its own: the
/// platform's request ends at once (Slack waits three seconds at most), and the answer
/// is posted when it is written. Shutting down stops them; each keeps what it had.
/// </summary>
public sealed partial class BotAnswers(IServiceScopeFactory scopes, ILogger<BotAnswers> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <summary>Answers the question in the background; or, given <paramref name="refusal"/>, says that in its thread instead.</summary>
    public void Start(IChatPlatform platform, BotQuestion question, string? refusal = null)
    {
        var id = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        _running[id] = gate.Task;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var conversation = scope.ServiceProvider.GetRequiredService<BotConversation>();
                await (refusal is null ? conversation.AnswerAsync(platform, question, _stopping.Token) : conversation.PostAsync(platform, question, refusal, _stopping.Token));
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                // Shutting down: the answer keeps what it had.
            }
            catch (Exception ex)
            {
                LogFailed(logger, platform.Title, ex);
            }
            finally
            {
                _running.TryRemove(id, out _);
                gate.TrySetResult();
            }
        });
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await Task.WhenAll(_running.Values).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => _stopping.Dispose();

    [LoggerMessage(Level = LogLevel.Error, Message = "{Platform}: a question could not be answered")]
    private static partial void LogFailed(ILogger logger, string platform, Exception ex);
}

/// <summary>
/// One question to a bot, answered as the person who asked: found by the email the platform
/// knows them by, then asked in their chat for that thread (made on the first question) with
/// the bot's model and tools and their own rights and credit, in turn with everyone else's
/// answers. The answer goes back to the thread, cut to the platform's limit with a link to
/// the chat.
/// </summary>
public sealed partial class BotConversation(AppDbContext db, UserManager<AppUser> users, AnswerJobs jobs, Safeguards.Safeguards safeguards, ModelPolicy policy,
    ChatModels models, Audit audit, IOptionsMonitor<BotOptions> options, IOptionsMonitor<Settings.BrandingOptions> branding, IOptions<AuthOptions> auth, TimeProvider clock,
    ILogger<BotConversation> logger)
{
    /// <summary>Longest a question waits for the answer before it in the same chat.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(20);

    public async Task AnswerAsync(IChatPlatform platform, BotQuestion asked, CancellationToken ct)
    {
        BotQuestion question;
        try
        {
            question = await platform.ResolveAsync(asked, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Without the platform's word on who asked, nobody is answered.
            LogPostFailed(logger, platform.Title, $"who asked could not be read: {ex.Message}");
            return;
        }
        var person = question.Email is { Length: > 0 } email ? await users.FindByEmailAsync(email.Trim()) : null;
        if (person is null || person.IsDisabled)
        {
            LogStranger(logger, platform.Title, question.User);
            await audit.WriteAsync("bot.refused", $"{platform.Name}:{question.User}", success: false,
                detail: person is null ? $"no account has the email {question.Email ?? "(none given)"}" : "the account is disabled");
            if (platform.AnswersStrangers)
            {
                await PostAsync(platform, question, person is null
                    ? $"Sorry {question.Name}, I can only answer people who have an account on {Product} under the same email address as here" +
                      (question.Email is null ? " (I could not read yours: ask your admin to let the bot see email addresses)." : ". Ask your admin for one.")
                    : $"Sorry {question.Name}, your account on {Product} is disabled, so I cannot answer you.", ct);
            }
            return;
        }
        if (question.Text.Length == 0)
        {
            await PostAsync(platform, question, $"Ask me something after the mention, {question.Name}: I answer as you, with your access.", ct);
            return;
        }
        var o = platform.Options;
        var model = o.Model is { Length: > 0 } m ? m : (await policy.ForAsync(person, await models.ListAsync(ct), ct)).Default?.Name;
        var verdict = await safeguards.CheckMessageAsync(person, question.Text, 0, false, model, ct);
        if (!verdict.Allowed)
        {
            await PostAsync(platform, question, verdict.Reason ?? "This question was refused.", ct);
            return;
        }

        // The chat for this thread and person: carried on, or made now.
        var c = await ChatAsync(platform, question, person, ct);

        // One answer at a time per chat: a second question in the thread waits for the first.
        var job = jobs.Reserve(c.Id, person.Id);
        var since = clock.GetUtcNow();
        while (job is null && clock.GetUtcNow() - since < Patience)
        {
            if (jobs.Find(c.Id)?.Running is { IsCompleted: false } running)
            {
                await running.WaitAsync(TimeSpan.FromMinutes(1), ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
            job = jobs.Reserve(c.Id, person.Id);
        }
        if (job is null)
        {
            await PostAsync(platform, question, "I am still answering your last question in this thread: ask again when it is done.", ct);
            return;
        }
        ChatMessage message;
        try
        {
            await db.Entry(c).ReloadAsync(ct);
            var sequence = (await db.ChatMessages.Where(x => x.ConversationId == c.Id).MaxAsync(x => (int?)x.Sequence, ct) ?? 0) + 1;
            message = new ChatMessage { ConversationId = c.Id, ParentId = c.CurrentLeafId, Role = "user", Sequence = sequence, Content = question.Text };
            db.ChatMessages.Add(message);
            c.CurrentLeafId = message.Id;
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            jobs.Release(job);
            throw;
        }
        job.Emit(new { type = "question", id = message.Id, parentId = message.ParentId });
        // The answer goes to the thread; the bell is not told. Nobody watches it in the chat, to allow a call.
        job.Notify = false;
        jobs.Start(job, message.Id, new AnswerOverrides(Unattended: true));
        await job.Running.WaitAsync(ct);

        // What was answered: the words of the answer's last message, or why there are none.
        var messages = await db.ChatMessages.AsNoTracking().Where(x => x.ConversationId == c.Id).ToDictionaryAsync(x => x.Id, ct);
        var leaf = await db.Conversations.AsNoTracking().Where(x => x.Id == c.Id).Select(x => x.CurrentLeafId).SingleAsync(ct);
        var answer = ChatService.PathTo(messages, leaf).SkipWhile(x => x.Id != message.Id).Where(x => x.Role == "assistant").ToList();
        var last = answer.LastOrDefault(x => x.Content.Length > 0) ?? answer.LastOrDefault();
        var link = $"{auth.Value.Origin}/chat/{c.Id}";
        if (last is null || last.Status is MessageStatus.Failed or MessageStatus.Stopped || last.Content.Trim().Length == 0)
        {
            var why = last?.Error ?? (last?.Status == MessageStatus.Stopped ? "The answer was stopped." : "No answer came.");
            await PostAsync(platform, question, $"{why}\n\n{platform.Link(link, "Open the chat")}", ct);
            return;
        }
        var text = platform.Format(last.Content.Trim());
        await PostAsync(platform, question, BotText.Fit(text, platform.Limit, platform.Link(link, "The whole answer is in the chat")) + platform.Footer(link), ct);
    }

    private string Product => branding.CurrentValue.ProductName is { Length: > 0 } name ? name : AppInfo.Current.Name;

    /// <summary>The person's chat for the thread: carried on, or made now (once, when two first questions come at the same moment).</summary>
    private async Task<Conversation> ChatAsync(IChatPlatform platform, BotQuestion question, AppUser person, CancellationToken ct)
    {
        var o = platform.Options;
        for (var attempt = 0; ; attempt++)
        {
            var now = clock.GetUtcNow();
            var thread = await db.BotThreads.SingleOrDefaultAsync(t => t.Platform == platform.Name && t.Thread == question.Thread && t.UserId == person.Id, ct);
            var c = thread is null ? null : await db.Conversations.SingleOrDefaultAsync(x => x.Id == thread.ConversationId, ct);
            if (c is null)
            {
                c = new Conversation
                {
                    UserId = person.Id, Title = $"{platform.Title} · {ChatService.TitleFrom(question.Text)}", Model = o.Model is { Length: > 0 } ? o.Model : null,
                    Tools = o.ToolList(), SystemPrompt = options.CurrentValue.Instructions is { Length: > 0 } i ? i.Trim() : null,
                };
                db.Conversations.Add(c);
                thread ??= db.BotThreads.Add(new BotThread { Platform = platform.Name, Thread = question.Thread, UserId = person.Id, CreatedAt = now }).Entity;
                thread.ConversationId = c.Id;
            }
            thread!.LastAt = now;
            c.ArchivedAt = null;
            c.UpdatedAt = now;
            try
            {
                await db.SaveChangesAsync(ct);
                return c;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // The thread's first question twice at once: the other made its chat; this one carries it on.
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Posts in the question's thread; a failure is logged, not thrown.</summary>
    public async Task PostAsync(IChatPlatform platform, BotQuestion question, string text, CancellationToken ct)
    {
        try
        {
            await platform.PostAsync(question, text, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or System.Net.Mail.SmtpException or IOException or FormatException
            or System.Text.Json.JsonException)
        {
            LogPostFailed(logger, platform.Title, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Platform}: {User} has no account here, or a disabled one")]
    private static partial void LogStranger(ILogger logger, string platform, string user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Platform}: the answer could not be posted: {Reason}")]
    private static partial void LogPostFailed(ILogger logger, string platform, string reason);
}
