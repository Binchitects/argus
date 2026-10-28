using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Chat;
using Llm.Core.Code;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Code.Fixes;

/// <summary>
/// Fixes at work, in the background: the branch read with the person's token, the agent run as them, its steps kept as they
/// go, the change and its diff kept at the end. Stoppable.
/// </summary>
public sealed partial class FixRuns(IServiceScopeFactory scopes, TimeProvider clock, ILogger<FixRuns> logger)
{
    private readonly ConcurrentDictionary<Guid, (Task Task, CancellationTokenSource Stop)> _running = new();

    public bool IsRunning(Guid fix) => _running.ContainsKey(fix);

    public Task Finished(Guid fix) => _running.TryGetValue(fix, out var r) ? r.Task : Task.CompletedTask;

    public bool Stop(Guid fix)
    {
        if (!_running.TryGetValue(fix, out var r))
        {
            return false;
        }
        r.Stop.Cancel();
        return true;
    }

    /// <summary>Works on the fix (again, with more to do: <paramref name="more"/>), in the background.</summary>
    public void Go(Guid fix, string? more = null)
    {
        var stop = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var task = Task.Run(async () =>
        {
            try
            {
                await WorkAsync(fix, more, stop.Token);
            }
            catch (Exception ex)
            {
                FixFailed(logger, fix, ex);
            }
            finally
            {
                _running.TryRemove(fix, out _);
                stop.Dispose();
            }
        });
        _running[fix] = (task, stop);
    }

    private async Task WorkAsync(Guid id, string? more, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<AppDbContext>();
        var fix = await db.CodeFixes.SingleAsync(f => f.Id == id, ct);
        var log = JsonNode.Parse(fix.Log) as JsonArray ?? [];
        async Task Step(string step)
        {
            fix.Step = step;
            fix.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
        }
        async Task End(string status, string? error)
        {
            fix.Status = status;
            fix.Error = error is { Length: > 2000 } tooLong ? tooLong[..2000] : error;
            fix.Step = null;
            fix.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
        }
        try
        {
            var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == fix.UserId, ct);
            var gitlab = services.GetRequiredService<GitLabAsPerson>();
            if (await gitlab.TokenAsync(me.Id, ct) is not { } token)
            {
                await End(CodeFix.Failed, "Link your GitLab token for Fix it first (Account → Code hosts).");
                return;
            }
            // The model: the person's default, as their chats use it.
            var policy = services.GetRequiredService<ModelPolicy>();
            var mine = await policy.ForAsync(me, await services.GetRequiredService<ChatModels>().ListAsync(ct), ct);
            if (mine.Default?.Name is not { } model)
            {
                await End(CodeFix.Failed, "No model is there for you to use.");
                return;
            }
            if (await policy.RefusalAsync(me, model, ct) is { } refused)
            {
                await End(CodeFix.Failed, refused);
                return;
            }
            var safeguards = services.GetRequiredService<Safeguards.Safeguards>();
            var verdict = await safeguards.CheckMessageAsync(me, more ?? fix.Ask, 0, false, model, ct);
            if (!verdict.Allowed)
            {
                await End(CodeFix.Failed, verdict.Reason ?? "A safeguard rule stopped it.");
                return;
            }
            fix.Model = model;
            await Step("Reading the branch");
            if (fix.BaseSha is null)
            {
                var branch = await gitlab.GetAsync(token, $"{GitLabAsPerson.Project(fix.Project)}/repository/branches/{Uri.EscapeDataString(fix.Branch)}", ct);
                fix.BaseSha = branch["commit"]?["id"]?.GetValue<string>() ?? throw new GitLabException("GitLab did not say the branch's last commit.");
            }
            var w = Workspace.FromArchive(await gitlab.ArchiveAsync(token, fix.Project, fix.BaseSha, ct));
            // Another round: what it changed before, then what more to do.
            var before = JsonSerializer.Deserialize<Dictionary<string, string?>>(fix.Changes) ?? [];
            w.Apply(before);
            await Step("Working on it");
            var task = more is null
                ? $"Repository {fix.Project}, branch {fix.Branch}. {fix.Ask}"
                : $"Repository {fix.Project}, branch {fix.Branch}. You were asked: {fix.Ask}\n\nYou changed {string.Join(", ", before.Keys)}. Now: {more}";
            var agent = services.GetRequiredService<FixAgent>();
            var outcome = await agent.RunAsync(me, model, verdict.Text ?? task, w, async s =>
            {
                log.Add(new JsonObject { ["tool"] = s.Tool, ["about"] = s.About, ["result"] = s.Result });
                fix.Log = log.ToJsonString();
                fix.Changes = JsonSerializer.Serialize(w.Changes);
                await Step(s.Tool switch { "run_tests" => "Running the tests", "replace" or "write_file" or "delete_file" => "Changing the code", _ => "Reading the code" });
            }, ct);
            fix.Changes = JsonSerializer.Serialize(w.Changes);
            fix.Diff = w.Diff();
            fix.Summary = outcome.Summary ?? fix.Summary;
            if (outcome.TestsCommand is not null)
            {
                fix.Tests = new JsonObject { ["command"] = outcome.TestsCommand, ["passed"] = outcome.TestsPassed, ["output"] = outcome.TestsOutput is { Length: > 8000 } o ? o[^8000..] : outcome.TestsOutput }.ToJsonString();
            }
            if (outcome.Error is null && w.Changes.Count > 0 && fix.Source == "security")
            {
                await Step("Checking the change");
                fix.Checks = (await FixChecks.SecurityAsync(services, fix, w, me, ct))?.ToJsonString();
            }
            await End(outcome.Error is null && w.Changes.Count > 0 ? CodeFix.Ready : CodeFix.Failed, outcome.Error ?? (w.Changes.Count == 0 ? "Nothing was changed." : null));
            db.Notifications.Add(new Notification
            {
                UserId = fix.UserId, Kind = "fix", Title = fix.Status == CodeFix.Ready ? $"A fix is ready: {fix.Title}" : $"A fix failed: {fix.Title}",
                Body = fix.Error, Link = $"/code/fixes/{fix.Id}", CreatedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await End(CodeFix.Stopped, "Stopped.");
        }
        catch (Exception ex) when (ex is GitLabException or WorkspaceException or InvalidDataException or FormatException)
        {
            await End(CodeFix.Failed, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fix {Fix} failed")]
    private static partial void FixFailed(ILogger logger, Guid fix, Exception ex);
}
