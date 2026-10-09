using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Identity;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.ArenaMcp;

/// <summary>
/// Deep research for an outside agent (deep_research at Arena MCP): the question is asked in a new
/// chat of the person's own, "Deep research: …", and answered as deep research is in the chat (in
/// turn, with their model, tools and credit, its parts by sub-agents). The agent hears which step it
/// is on as progress, and gets the report with the chat's address, where the person reads its parts.
/// Stopped when the agent cancels the call; the chat keeps what was done.
/// </summary>
public sealed class McpResearch(AppDbContext db, AnswerJobs jobs, Safeguards.Safeguards safeguards, ModelPolicy policy, ChatModels models, SmallModel small,
    IOptions<AuthOptions> auth)
{
    /// <summary>The title's start, so the person finds an agent's research in their chats.</summary>
    public const string TitlePrefix = "Deep research: ";

    /// <summary>
    /// Why deep research cannot run for an agent, or null when it can: when the web asks before each call
    /// (Admin → Tools) the answer reads the web itself, and nobody is in its chat to allow the calls.
    /// </summary>
    public static string? Refusal(IReadOnlyList<ToolChoice> served) => served.Any(t => t.Tool.Id == "web" && t.Setting.AskFirst)
        ? "the web asks before each call (Admin → Tools) and nobody is in an agent's research to allow it: start deep research in the chat instead"
        : null;

    public async Task<ToolResult> RunAsync(AppUser user, string question, Func<McpProgress, Task>? progress, CancellationToken ct)
    {
        // Checked as a message sent with Deep research on: its length, the rate, the research a day, blocked words, secrets.
        var checker = (await small.ForAsync(user, ct))?.Name ?? (await policy.ForAsync(user, await models.ListAsync(ct), ct)).Default?.Name;
        var verdict = await safeguards.CheckMessageAsync(user, question, 0, research: true, checker, ct);
        if (!verdict.Allowed)
        {
            return new ToolResult(verdict.Reason!, IsError: true);
        }
        await safeguards.MarkResearchAsync(user.Id, ct);
        var text = verdict.Text ?? question;
        var chat = new Conversation { UserId = user.Id, Title = TitlePrefix + ChatService.TitleFrom(text) };
        var asked = new ChatMessage { ConversationId = chat.Id, Role = "user", Sequence = 1, Content = text };
        chat.CurrentLeafId = asked.Id;
        db.Conversations.Add(chat);
        db.ChatMessages.Add(asked);
        await db.SaveChangesAsync(ct);

        // A new chat: nothing answers in it yet. The agent hears how it ended, not the bell.
        var job = jobs.Reserve(chat.Id, user.Id)!;
        job.Notify = false;
        job.Emit(new { type = "question", id = asked.Id, parentId = (Guid?)null });
        jobs.Start(job, asked.Id, new AnswerOverrides(Research: true));
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var link = $"{auth.Value.Origin}/chat/{chat.Id}";
        var reporting = progress is null ? Task.CompletedTask : ReportAsync(job, link, progress, watching.Token);
        try
        {
            await job.Running.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            jobs.Stop(chat.Id);
            throw;
        }
        finally
        {
            await watching.CancelAsync();
            try
            {
                await reporting;
            }
            catch (OperationCanceledException)
            {
                // It stopped with the answer, as it should.
            }
        }

        // The report: the answer's last words, or why there are none.
        var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == chat.Id).ToDictionaryAsync(m => m.Id, CancellationToken.None);
        var leaf = await db.Conversations.AsNoTracking().Where(c => c.Id == chat.Id).Select(c => c.CurrentLeafId).SingleAsync(CancellationToken.None);
        var answer = ChatService.PathTo(messages, leaf).Where(m => m.Role == "assistant").ToList();
        var last = answer.LastOrDefault(m => m.Content.Trim().Length > 0) ?? answer.LastOrDefault();
        if (last is null || last.Status is MessageStatus.Failed or MessageStatus.Stopped || last.Content.Trim().Length == 0)
        {
            var why = last?.Error ?? (last?.Status == MessageStatus.Stopped ? "It was stopped." : "No report came.");
            return new ToolResult($"The deep research did not finish: {why} What it did is in the person's chat: {link}", IsError: true);
        }
        return new ToolResult($"{last.Content.Trim()}\n\n(The research, with its parts, is in the person's chat: {link})");
    }

    /// <summary>
    /// The research's steps as the agent's progress, from the answer's events: waiting in line, planning,
    /// the parts (each one's own progress), a call waiting for the person, writing the report.
    /// </summary>
    private static async Task ReportAsync(AnswerJobs.Job job, string link, Func<McpProgress, Task> progress, CancellationToken ct)
    {
        // MCP's progress only goes up: each step is one more.
        var step = 0;
        string? said = null;
        Task SayAsync(string message)
        {
            if (message == said)
            {
                return Task.CompletedTask;
            }
            said = message;
            return progress(new McpProgress(++step, null, message));
        }
        var (calls, delegations) = (0, 0);
        await foreach (var line in job.WatchAsync(TimeSpan.FromSeconds(15), ct))
        {
            if (line.Length == 0 || JsonNode.Parse(line) is not JsonObject e)
            {
                continue;
            }
            switch (e["type"]?.GetValue<string>())
            {
                case "queued":
                    await SayAsync($"Waiting in line: {e["ahead"]} ahead");
                    break;
                case "research":
                    await SayAsync("Planning the research");
                    break;
                case "tool_call":
                    calls++;
                    if (e["name"]?.GetValue<string>() == AgentsTool.Function)
                    {
                        delegations++;
                        await SayAsync(delegations == 1 ? "Researching its parts" : "Filling gaps");
                    }
                    break;
                case "tool_progress" when e["message"]?.GetValue<string>() is { Length: > 0 } message:
                    await SayAsync(delegations > 1 ? $"Filling gaps: {message}" : $"Researching: {message}");
                    break;
                case "approval":
                    await SayAsync($"Waiting for the person to allow {e["title"]} in the chat: {link}");
                    break;
                case "content" when calls > 0:
                    await SayAsync("Writing the report");
                    break;
            }
        }
    }
}
