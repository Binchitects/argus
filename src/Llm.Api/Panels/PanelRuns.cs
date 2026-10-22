using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Panels;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Panels;

/// <summary>Who sits on a panel: a name, the side they take, and the model they answer with (an assistant's, or the person's default).</summary>
public sealed record Panelist(string Name, string Role, string? Model = null, Guid? AssistantId = null);

public sealed record PanelTurn(int Round, string Speaker, string Text);

/// <summary>
/// Panels at work, in the background: each panelist speaks in turn, round by round, having read what was said before; the
/// moderator then writes the verdict with who said what; the discussion and the verdict go into the chat or room. Each turn
/// is asked as the person (their credit and safeguards) and kept as it comes, for the page to show.
/// </summary>
public sealed partial class PanelRuns(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PanelRuns> logger)
{
    public const int MostWords = 120;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public Task Finished(Guid panel) => _running.TryGetValue(panel, out var t) ? t : Task.CompletedTask;

    public void Go(Guid panel)
    {
        _running[panel] = Task.Run(async () =>
        {
            try
            {
                await RunAsync(panel, CancellationToken.None);
            }
            catch (Exception ex)
            {
                PanelFailed(logger, panel, ex);
            }
            finally
            {
                _running.TryRemove(panel, out _);
            }
        });
    }

    private static string Said(IEnumerable<PanelTurn> turns) =>
        string.Join("\n\n", turns.Select(t => $"{t.Speaker} (round {t.Round}): {t.Text}"));

    private static async Task<string> Why(IResult problem)
    {
        await Task.CompletedTask;
        return problem is IValueHttpResult { Value: { } v } && JsonNode.Parse(JsonSerializer.Serialize(v))?["error"]?.GetValue<string>() is { } e ? e : "The model could not be asked.";
    }

    private async Task RunAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var one = scope.ServiceProvider.GetRequiredService<OneQuestion>();
        var panel = await db.Panels.SingleAsync(p => p.Id == id, ct);
        var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == panel.UserId, ct);
        var panelists = JsonSerializer.Deserialize<List<Panelist>>(panel.Panelists, JsonSerializerOptions.Web) ?? [];
        var turns = new List<PanelTurn>();
        string? model = null;
        try
        {
            for (var round = 1; round <= panel.Rounds; round++)
            {
                foreach (var p in panelists)
                {
                    var system = $"You sit on a panel that discusses a question. You are {p.Name}: {p.Role} Speak as yourself, plainly, in at most {MostWords} words. "
                        + "Name the others when you agree or disagree with them, and add what they missed; do not repeat them.";
                    var prompt = turns.Count == 0
                        ? $"The question: {panel.Question}\n\nYou speak first."
                        : $"The question: {panel.Question}\n\nWhat was said so far:\n{Said(turns)}\n\nYour turn{(round > 1 ? ", in round " + round : "")}.";
                    var asked = await one.AskAsync(me, prompt, system, p.Model, 600, "panel", ct);
                    if (asked.Problem is not null)
                    {
                        throw new InvalidOperationException($"{p.Name} could not answer: {await Why(asked.Problem)}");
                    }
                    turns.Add(new PanelTurn(round, p.Name, (asked.Text ?? "").Trim()));
                    model ??= asked.Model;
                    panel.Turns = JsonSerializer.Serialize(turns, JsonSerializerOptions.Web);
                    await db.SaveChangesAsync(ct);
                }
            }
            var verdict = await one.AskAsync(me, $"The question: {panel.Question}\n\nThe discussion:\n{Said(turns)}",
                "You moderate a panel. Write, in Markdown: the verdict, the answer the panel comes to in two to four sentences; then **Who said what**, a list " +
                "with each panelist's main point in a line, by name; then **Where they disagree**, if they do. Say only what the panel said.", null, 1200, "panel", ct);
            if (verdict.Problem is not null)
            {
                throw new InvalidOperationException($"The moderator could not answer: {await Why(verdict.Problem)}");
            }
            panel.Verdict = (verdict.Text ?? "").Trim();
            panel.Status = Panel.Done;
            await PostAsync(db, scope.ServiceProvider.GetRequiredService<RoomHub>(), panel, panelists, turns, verdict.Model ?? model, ct);
        }
        catch (InvalidOperationException ex)
        {
            panel.Status = Panel.Failed;
            panel.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
        }
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The question and the panel's answer (the discussion, then the verdict) as the chat's next messages.</summary>
    private async Task PostAsync(AppDbContext db, RoomHub hub, Panel panel, List<Panelist> panelists, List<PanelTurn> turns, string? model, CancellationToken ct)
    {
        var c = await db.Conversations.SingleAsync(x => x.Id == panel.ConversationId, ct);
        var w = new StringBuilder();
        w.Append("**A panel of ").Append(string.Join(", ", panelists.Select(p => p.Name))).Append("** discussed it.\n\n");
        foreach (var round in turns.GroupBy(t => t.Round))
        {
            if (panel.Rounds > 1)
            {
                w.Append("### Round ").Append(round.Key).Append("\n\n");
            }
            foreach (var t in round)
            {
                w.Append("**").Append(t.Speaker).Append(":** ").Append(t.Text).Append("\n\n");
            }
        }
        w.Append("## The verdict\n\n").Append(panel.Verdict);
        var last = await db.ChatMessages.Where(m => m.ConversationId == c.Id).MaxAsync(m => (int?)m.Sequence, ct) ?? 0;
        var question = new ChatMessage
        {
            ConversationId = c.Id, ParentId = c.CurrentLeafId, Role = "user", Sequence = last + 1, Content = $"Panel: {panel.Question}", AuthorId = panel.UserId,
        };
        var answer = new ChatMessage { ConversationId = c.Id, ParentId = question.Id, Role = "assistant", Sequence = last + 2, Content = w.ToString(), Model = model };
        db.ChatMessages.AddRange(question, answer);
        c.CurrentLeafId = answer.Id;
        c.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        if (c.IsRoom)
        {
            hub.Said(c.Id, answer.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Panel {Panel} failed")]
    private static partial void PanelFailed(ILogger logger, Guid panel, Exception ex);
}
