using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Identity;
using Llm.Api.Schedules;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Studio;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Studio;

/// <summary>Why a run ends before its last step: a step that failed, an approval turned down, or a stop step (done or failed).</summary>
public sealed class FlowEndException(string message, bool stopped = false, bool done = false) : Exception(message)
{
    public bool Stopped { get; } = stopped;

    /// <summary>A stop step ended the run well.</summary>
    public bool Done { get; } = done;
}

/// <summary>
/// Runs a workflow's steps as its owner, keeping each as it goes. A run is replayed from its start each time it goes on (after
/// an approval): a step already done gives what it gave then and is not run again, so a run goes on where it waited.
/// </summary>
public sealed class FlowRunner(AppDbContext db, OneQuestion one, AnswerJobs jobs, ToolRegistry registry, AccessService access, Mailer mailer, GitLabBot gitlab,
    QueuedMessages queue, StudioRuns runs, Audit audit, TimeProvider clock, Insights.DataQuestions questions, Knowledge.KnowledgeSearch knowledge, SandboxClient sandbox,
    WebFetcher web, IHttpClientFactory http)
{
    private const int MostBody = 1024 * 1024;
    /// <summary>The most steps one run takes (each turn of a loop counts).</summary>
    public const int MostTaken = 300;
    private const int MostOutput = 50_000;

    private sealed record Outcome(JsonObject Value, bool Waiting = false);

    private WorkflowRun _run = null!;
    private Workflow _workflow = null!;
    private AppUser _owner = null!;
    private Dictionary<string, WorkflowRunStep> _kept = [];
    private int _order;
    private int _taken;

    public async Task RunAsync(Guid runId, CancellationToken ct)
    {
        _run = await db.WorkflowRuns.SingleAsync(r => r.Id == runId, ct);
        _workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == _run.WorkflowId, ct);
        _kept = await db.WorkflowRunSteps.Where(s => s.RunId == runId).ToDictionaryAsync(s => s.Path, ct);
        _order = _kept.Count == 0 ? 0 : _kept.Values.Max(s => s.Order);
        _run.Status = WorkflowRun.Running;
        await db.SaveChangesAsync(ct);
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == _workflow.OwnerId, ct);
        string? error = null;
        var status = WorkflowRun.Done;
        if (owner is null || owner.IsDisabled)
        {
            (status, error) = (WorkflowRun.Failed, "Its owner cannot sign in (disabled).");
        }
        else
        {
            _owner = owner;
            var values = new Dictionary<string, JsonNode?>
            {
                ["input"] = JsonNode.Parse(_run.Input) ?? new JsonObject(),
                ["now"] = clock.GetUtcNow().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            };
            try
            {
                if (await StepsAsync(FlowModel.Read(_run.Flow).Steps, "", values, ct))
                {
                    status = WorkflowRun.Waiting;
                }
            }
            catch (FlowEndException end)
            {
                (status, error) = end.Done ? (WorkflowRun.Done, null) : (end.Stopped ? WorkflowRun.Stopped : WorkflowRun.Failed, end.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                (status, error) = (WorkflowRun.Stopped, "Stopped.");
            }
        }
        _run.Status = status;
        _run.Error = error is { Length: > 2000 } tooLong ? tooLong[..2000] : error;
        _run.FinishedAt = status == WorkflowRun.Waiting ? null : clock.GetUtcNow();
        // A run that went as it should says nothing (a schedule's run every hour is no news); one that failed or stopped does.
        if (status is WorkflowRun.Failed or WorkflowRun.Stopped && !(status == WorkflowRun.Stopped && error == "Stopped."))
        {
            db.Notifications.Add(new Notification
            {
                UserId = _workflow.OwnerId, Kind = "workflow", Title = $"{_workflow.Name} {status}", Body = Notifications.NewsText.Plain(error), Link = $"/studio/{_workflow.Id}?run={_run.Id}", CreatedAt = clock.GetUtcNow(),
            });
        }
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>Runs steps in order; true when one waits (an approval), and the run with it.</summary>
    private async Task<bool> StepsAsync(List<FlowStep> steps, string prefix, Dictionary<string, JsonNode?> values, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            var outcome = await StepAsync(step, prefix + step.Id, values, ct);
            if (outcome.Waiting)
            {
                return true;
            }
            values[step.Id] = outcome.Value;
        }
        return false;
    }

    private static JsonObject Value(string text, JsonNode? json = null)
    {
        var o = new JsonObject { ["text"] = text };
        if (json is not null)
        {
            o["json"] = json.DeepClone();
        }
        return o;
    }

    private static JsonObject Value(WorkflowRunStep kept) => Value(kept.Output, kept.OutputJson is { } j ? JsonNode.Parse(j) : null);

    private static string Cut(string s) => s.Length > MostOutput ? s[..MostOutput] : s;

    private async Task<Outcome> StepAsync(FlowStep step, string path, Dictionary<string, JsonNode?> values, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _kept.TryGetValue(path, out var kept);
        // Done before (the run goes on after an approval): what it gave then.
        if (kept is { Status: "done" or "skipped" } && step.Kind is not ("condition" or "loop" or "approval" or "stop"))
        {
            return new Outcome(Value(kept));
        }
        if (++_taken > MostTaken)
        {
            throw new FlowEndException($"The run took {MostTaken} steps, the most one run takes.");
        }
        switch (step.Kind)
        {
            case "condition":
            {
                var left = FlowModel.Fill(step.Left, values);
                var right = FlowModel.Fill(step.Right, values);
                var yes = kept?.OutputJson is { } was ? JsonNode.Parse(was)?["result"]?.GetValue<bool>() == true : FlowModel.Test(left, step.Op, right);
                kept ??= await KeepAsync(step, path, $"{Short(left)} {step.Op?.Replace('_', ' ')} {Short(right)}", ct);
                await DoneAsync(kept, yes ? "yes" : "no", new JsonObject { ["result"] = yes }, null, ct);
                return await StepsAsync(yes ? step.Then ?? [] : step.Else ?? [], path + "/", values, ct) ? new Outcome(new JsonObject(), true) : new Outcome(Value(yes ? "yes" : "no", new JsonObject { ["result"] = yes }));
            }
            case "loop":
            {
                var items = kept?.OutputJson is { } was && JsonNode.Parse(was)?["items"] is JsonArray seen
                    ? [.. seen.Select(i => i?.DeepClone())]
                    : FlowModel.Items(step.Over, values).Take(Math.Clamp(step.Most ?? 20, 1, FlowModel.MostTurns)).ToList();
                kept ??= await KeepAsync(step, path, $"{items.Count} {(items.Count == 1 ? "item" : "items")}", ct);
                kept.OutputJson = new JsonObject { ["items"] = new JsonArray([.. items.Select(i => i?.DeepClone())]) }.ToJsonString();
                await db.SaveChangesAsync(ct);
                var results = new JsonArray();
                for (var i = 0; i < items.Count; i++)
                {
                    var turn = new Dictionary<string, JsonNode?>(values) { ["item"] = items[i]?.DeepClone(), ["index"] = i };
                    var inner = step.Steps ?? [];
                    if (await StepsAsync(inner, $"{path}[{i}]/", turn, ct))
                    {
                        return new Outcome(new JsonObject(), true);
                    }
                    results.Add(inner.Count > 0 && turn.TryGetValue(inner[^1].Id, out var last) ? last?["text"]?.DeepClone() : null);
                }
                var text = string.Join("\n", results.Select(r => FlowModel.Show(r)));
                var json = new JsonObject { ["items"] = new JsonArray([.. items.Select(i => i?.DeepClone())]), ["results"] = results };
                await DoneAsync(kept, text, json, null, ct);
                return new Outcome(Value(text, json));
            }
            case "approval":
            {
                if (kept is { Status: "done" })
                {
                    if (JsonNode.Parse(kept.OutputJson ?? "{}")?["approved"]?.GetValue<bool>() != true)
                    {
                        throw new FlowEndException($"Turned down at {step.Name ?? step.Id}.", stopped: true);
                    }
                    return new Outcome(Value(kept));
                }
                if (kept is null)
                {
                    var question = FlowModel.Fill(step.Question ?? "Go on?", values);
                    kept = await KeepAsync(step, path, question, ct);
                    kept.Status = "waiting";
                    db.Notifications.Add(new Notification
                    {
                        UserId = _workflow.OwnerId, Kind = "workflow", Title = $"{_workflow.Name} asks you", Body = Cut(question)[..Math.Min(400, question.Length)],
                        Link = $"/studio/{_workflow.Id}?run={_run.Id}", CreatedAt = clock.GetUtcNow(),
                    });
                    await db.SaveChangesAsync(ct);
                }
                return new Outcome(new JsonObject(), true);
            }
            case "wait":
            {
                // Waits where it is kept: the runs' sweep goes on with it when its time comes, on any replica.
                if (kept is null)
                {
                    var minutes = Math.Clamp(step.Minutes ?? 1, 1, FlowModel.MostMinutes);
                    kept = await KeepAsync(step, path, $"{minutes} {(minutes == 1 ? "minute" : "minutes")}", ct);
                    kept.Status = "waiting";
                    kept.ResumeAt = clock.GetUtcNow().AddMinutes(minutes);
                    await db.SaveChangesAsync(ct);
                }
                return new Outcome(new JsonObject(), true);
            }
            case "stop":
            {
                var reason = FlowModel.Fill(step.Text, values).Trim() is { Length: > 0 } r ? r : step.Outcome == "failed" ? "It stopped here, failed." : "It ended here.";
                kept ??= await KeepAsync(step, path, reason, ct);
                await DoneAsync(kept, reason, null, null, ct);
                throw new FlowEndException(reason, done: step.Outcome != "failed");
            }
        }
        var input = Input(step, values);
        kept ??= await KeepAsync(step, path, input, ct);
        try
        {
            var (text, json, link) = await DoAsync(step, input, values, ct);
            await DoneAsync(kept, text, json, link, ct);
            return new Outcome(Value(text, json));
        }
        catch (FlowEndException end)
        {
            kept.Status = "failed";
            kept.Error = end.Message.Length > 2000 ? end.Message[..2000] : end.Message;
            kept.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            throw new FlowEndException($"Step {step.Name ?? step.Id}: {end.Message}");
        }
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] + "…" : s;

    /// <summary>What a step is given, its templates filled: the prompt, the tool's arguments, the message.</summary>
    private static string Input(FlowStep step, Dictionary<string, JsonNode?> values) => step.Kind switch
    {
        "ask" or "agent" or "knowledge" => FlowModel.Fill(step.Prompt, values),
        "data" => string.IsNullOrWhiteSpace(step.Sql) ? FlowModel.Fill(step.Prompt, values) : FlowModel.Fill(step.Sql, values),
        "http" => $"{step.Method ?? "GET"} {FlowModel.Fill(step.Url, values)}\n\n{FlowModel.Fill(step.Text, values)}".Trim(),
        "gitlab" => step.Action switch
        {
            "label" => $"Label {FlowModel.Fill(step.Target, values)} {FlowModel.Fill(step.Labels, values)}".Trim(),
            "issue" => $"{FlowModel.Fill(step.Subject, values)}\n\n{FlowModel.Fill(step.Text, values)}".Trim(),
            _ => FlowModel.Fill(step.Text, values),
        },
        "tool" => new JsonObject((step.Arguments ?? []).Select(a => KeyValuePair.Create(a.Key, Argument(FlowModel.Fill(a.Value, values))))).ToJsonString(FlowModel.Json),
        "email" => $"{FlowModel.Fill(step.Subject, values)}\n\n{FlowModel.Fill(step.Text, values)}".Trim(),
        _ => FlowModel.Fill(step.Text, values),
    };

    /// <summary>A tool's argument: a number, true or false, or JSON as such; else the words.</summary>
    private static JsonNode? Argument(string filled)
    {
        var t = filled.Trim();
        if (t.Length > 0 && (t[0] is '{' or '[' || t is "true" or "false" || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
        {
            try
            {
                return JsonNode.Parse(t);
            }
            catch (JsonException)
            {
            }
        }
        return JsonValue.Create(filled);
    }

    private async Task<WorkflowRunStep> KeepAsync(FlowStep step, string path, string input, CancellationToken ct)
    {
        var kept = new WorkflowRunStep
        {
            RunId = _run.Id, Order = ++_order, Path = path, StepId = step.Id, Kind = step.Kind, Name = step.Name ?? "", Input = Cut(input), StartedAt = clock.GetUtcNow(),
        };
        db.WorkflowRunSteps.Add(kept);
        _kept[path] = kept;
        await db.SaveChangesAsync(ct);
        return kept;
    }

    private async Task DoneAsync(WorkflowRunStep kept, string output, JsonNode? json, string? link, CancellationToken ct)
    {
        kept.Status = "done";
        kept.Output = Cut(output);
        kept.OutputJson = json?.ToJsonString();
        kept.Link = link;
        kept.FinishedAt ??= clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The step's work: its output as words, as JSON when it has any, and a link to what it made.</summary>
    private async Task<(string Text, JsonNode? Json, string? Link)> DoAsync(FlowStep step, string input, Dictionary<string, JsonNode?> values, CancellationToken ct)
    {
        switch (step.Kind)
        {
            case "ask":
            {
                var system = FlowModel.Fill(step.System, values);
                if (step.Json)
                {
                    system = (system + "\nAnswer with JSON only, no other words.").Trim();
                }
                var asked = await one.AskAsync(_owner, input, system.Length > 0 ? system : null, step.Model, 3000, "studio", ct);
                if (asked.Problem is not null)
                {
                    throw new FlowEndException(await ProblemText(asked.Problem));
                }
                var text = asked.Text ?? "";
                return (text, step.Json ? FlowModel.JsonIn(text) ?? throw new FlowEndException("The model's answer was not JSON.") : null, null);
            }
            case "agent":
                return await AgentAsync(step, input, ct);
            case "tool":
                return await ToolAsync(step, input, ct);
            case "text":
                return (input, step.Json ? FlowModel.JsonIn(input) ?? throw new FlowEndException("Its text is not JSON.") : null, null);
            case "notify":
            {
                var title = FlowModel.Fill(step.Subject, values).Trim() is { Length: > 0 } t ? t : _workflow.Name;
                db.Notifications.Add(new Notification
                {
                    UserId = _owner.Id, Kind = "workflow", Title = title.Length > 200 ? title[..200] : title, Body = Notifications.NewsText.Plain(input.Length > 2000 ? input[..2000] : input),
                    Link = $"/studio/{_workflow.Id}?run={_run.Id}", CreatedAt = clock.GetUtcNow(),
                });
                await db.SaveChangesAsync(ct);
                return ($"Told {(_owner.DisplayName is { Length: > 0 } d ? d : _owner.UserName)}: {title}", null, null);
            }
            case "code":
                return await CodeAsync(input, values, ct);
            case "knowledge":
                return await KnowledgeAsync(input, ct);
            case "data":
                return await DataAsync(step, input, ct);
            case "http":
                return await HttpAsync(step, values, ct);
            case "gitlab":
                return await GitLabAsync(step, values, ct);
            case "room":
                return await RoomAsync(step.RoomId!.Value, input, ct);
            case "email":
            {
                if (_owner.Email is not { Length: > 0 } email)
                {
                    throw new FlowEndException("Its owner has no email address.");
                }
                var subject = FlowModel.Fill(step.Subject, values) is { Length: > 0 } s ? s : _workflow.Name;
                try
                {
                    await mailer.SendAsync(email, subject, $"{FlowModel.Fill(step.Text, values)}\n\n--\nThe workflow \"{_workflow.Name}\" sent this (Agent Studio).", ct);
                }
                catch (Exception ex) when (ex is System.Net.Mail.SmtpException or InvalidOperationException or IOException or FormatException)
                {
                    throw new FlowEndException($"The email could not be sent: {ex.Message}");
                }
                return ($"Sent to {email}: {subject}", null, null);
            }
            case "comment":
            {
                if (_run.Reply is null || JsonSerializer.Deserialize<GitLabTarget>(_run.Reply, FlowModel.Json) is not { } target)
                {
                    throw new FlowEndException("Only a run GitLab started comments: there is no merge request or issue to comment on.");
                }
                try
                {
                    await gitlab.CommentAsync(target, $"{input}\n\n---\n*{_workflow.Name}, by Argus Arena for {_owner.DisplayName}*", ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
                {
                    throw new FlowEndException($"GitLab took no comment: {ex.Message}");
                }
                await audit.WriteAsync("workflow.gitlab_comment", _workflow.Name, detail: $"project {target.Project}, {target.Kind} {target.Id}", actor: _owner);
                return ("Commented in GitLab.", null, null);
            }
            case "file":
            {
                var name = FlowModel.Fill(step.FileName, values).Trim() is { Length: > 0 } n ? string.Concat(n.Where(c => !Path.GetInvalidFileNameChars().Contains(c))) : "result.md";
                var file = new ChatAttachment
                {
                    UserId = _owner.Id, FileName = name.Length > 200 ? name[..200] : name, ContentType = "text/markdown", Kind = "text", Text = input, Size = Encoding.UTF8.GetByteCount(input),
                    Origin = "workflow",
                };
                db.ChatAttachments.Add(file);
                await db.SaveChangesAsync(ct);
                return ($"Saved {file.FileName}.", null, $"/api/chat/attachments/{file.Id}/content?download");
            }
            default:
                throw new FlowEndException($"There is no step of kind {step.Kind}.");
        }
    }

    private static async Task<string> ProblemText(IResult problem)
    {
        if (problem is IValueHttpResult { Value: { } v } && JsonNode.Parse(JsonSerializer.Serialize(v))?["error"]?.GetValue<string>() is { } error)
        {
            return error;
        }
        await Task.CompletedTask;
        return "The model could not be asked.";
    }

    /// <summary>The model with its tools, as a chat of the owner's (to open from the run): the answer's last words.</summary>
    private async Task<(string, JsonNode?, string?)> AgentAsync(FlowStep step, string prompt, CancellationToken ct)
    {
        var c = new Conversation { UserId = _owner.Id, Title = $"{_workflow.Name} · {step.Name ?? step.Id}", Tools = step.Tools ?? [], Model = step.Model is "small" ? null : step.Model };
        db.Conversations.Add(c);
        var question = new ChatMessage { ConversationId = c.Id, Role = "user", Sequence = 1, Content = prompt };
        db.ChatMessages.Add(question);
        c.CurrentLeafId = question.Id;
        await db.SaveChangesAsync(ct);
        if (jobs.Reserve(c.Id, _owner.Id) is not { } job)
        {
            throw new FlowEndException("Its chat could not start.");
        }
        job.Emit(new { type = "question", id = question.Id, parentId = (Guid?)null });
        job.Notify = false;
        jobs.Start(job, question.Id, new AnswerOverrides(Unattended: true));
        await job.Running.WaitAsync(ct);
        var messages = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == c.Id).ToDictionaryAsync(m => m.Id, ct);
        var leaf = await db.Conversations.AsNoTracking().Where(x => x.Id == c.Id).Select(x => x.CurrentLeafId).SingleAsync(ct);
        var answer = ChatService.PathTo(messages, leaf).Where(m => m.Role == "assistant").ToList();
        var last = answer.LastOrDefault(m => m.Content.Length > 0) ?? answer.LastOrDefault();
        if (last is null || last.Status is MessageStatus.Failed or MessageStatus.Stopped || last.Content.Trim().Length == 0)
        {
            throw new FlowEndException(last?.Error ?? "No answer came.");
        }
        return (last.Content.Trim(), FlowModel.JsonIn(last.Content) is JsonObject or JsonArray && last.Content.TrimStart().StartsWith('{') ? FlowModel.JsonIn(last.Content) : null, $"/chat/{c.Id}");
    }

    /// <summary>One function of a tool the owner may use, with the step's arguments; one that asks a person first is not run here.</summary>
    private async Task<(string, JsonNode?, string?)> ToolAsync(FlowStep step, string arguments, CancellationToken ct)
    {
        var membership = await access.MembershipAsync(_owner, ct);
        if (!await registry.MayUseAsync(membership, step.Tool!, ct))
        {
            throw new FlowEndException($"Its owner may not use the tool {step.Tool}, or it is off.");
        }
        var choice = (await registry.AllAsync(ct)).First(t => t.Tool.Id == step.Tool);
        IToolRun run;
        try
        {
            run = await choice.Tool.StartAsync(new ToolContext(_owner, _owner.Email ?? "", new Conversation { UserId = _owner.Id, Title = _workflow.Name }), ct);
        }
        catch (McpException ex)
        {
            throw new FlowEndException($"The tool could not start: {ex.Message}");
        }
        if (!run.Functions.Any(f => f?["function"]?["name"]?.GetValue<string>() == step.Function))
        {
            throw new FlowEndException($"The tool {step.Tool} has no function {step.Function}.");
        }
        if (run.AsksFirst(step.Function!))
        {
            throw new FlowEndException($"{step.Function} asks a person first: a workflow does not run it.");
        }
        var result = await run.CallAsync(step.Function!, JsonNode.Parse(arguments) as JsonObject ?? [], ct);
        if (result.IsError)
        {
            throw new FlowEndException(result.Text);
        }
        return (result.Text, FlowModel.JsonIn(result.Text) is { } j && result.Text.TrimStart() is ['{' or '[', ..] ? j : null, null);
    }

    private static string Tail(string s) => s.Length > 1500 ? "…" + s[^1500..] : s;

    /// <summary>Python in the sandbox (no network), the run's values in input.json beside it: what it prints, and its JSON when it prints JSON.</summary>
    private async Task<(string, JsonNode?, string?)> CodeAsync(string code, Dictionary<string, JsonNode?> values, CancellationToken ct)
    {
        if (!await registry.MayUseAsync(await access.MembershipAsync(_owner, ct), "python", ct))
        {
            throw new FlowEndException("Its owner may not use Python, or it is off.");
        }
        if (sandbox.Unavailable() is { } why)
        {
            throw new FlowEndException(why);
        }
        var given = new JsonObject(values.Select(v => KeyValuePair.Create(v.Key, v.Value?.DeepClone())));
        var result = await sandbox.RunAsync(code, [new SandboxFile("input.json", Encoding.UTF8.GetBytes(given.ToJsonString()))], 120, ct);
        if (result.Error is { Length: > 0 } error)
        {
            throw new FlowEndException(error);
        }
        if (result.TimedOut)
        {
            throw new FlowEndException("The code ran longer than 2 minutes.");
        }
        if (result.ExitCode is not 0)
        {
            throw new FlowEndException($"The code failed (exit {result.ExitCode}): {Tail(result.Stderr.Trim())}");
        }
        var text = result.Stdout.Trim();
        return (text, text is ['{' or '[', ..] ? FlowModel.JsonIn(text) : null, null);
    }

    /// <summary>The company's documents the owner may read, nearest the words first: their titles, links and passages.</summary>
    private async Task<(string, JsonNode?, string?)> KnowledgeAsync(string query, CancellationToken ct)
    {
        List<Knowledge.Passage> found;
        try
        {
            found = await knowledge.SearchAsync(_owner, query, 8, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FlowEndException($"Company knowledge cannot be searched now: {ex.Message}");
        }
        var json = new JsonArray([.. found.Select(p => (JsonNode)new JsonObject { ["title"] = p.Title, ["url"] = p.Url, ["source"] = p.Source, ["text"] = p.Text })]);
        var text = found.Count == 0 ? "Nothing found." : string.Join("\n\n", found.Select(p => $"## {p.Title}{(p.Url is { Length: > 0 } u ? $" ({u})" : "")}\n\n{p.Text}"));
        return (text, json, null);
    }

    /// <summary>A data source the owner may ask, by a question in words (the model writes the SQL) or by SQL: its rows, as a table and as JSON.</summary>
    private async Task<(string, JsonNode?, string?)> DataAsync(FlowStep step, string asked, CancellationToken ct)
    {
        if (!await access.MayAsync(_owner, Core.Access.Permissions.Data, ct) || await questions.SourceForAsync(step.SourceId!.Value, _owner, ct) is not { } source)
        {
            throw new FlowEndException("Its owner may not ask that data source, or it is gone.");
        }
        Insights.DataResult result;
        string sql;
        try
        {
            if (!string.IsNullOrWhiteSpace(step.Sql))
            {
                (sql, result) = (asked, await questions.RunAsync(source, asked, _owner, "workflow", null, ct));
            }
            else
            {
                var (answer, problem) = await questions.AskAsync(source, _owner, asked, null, "workflow", ct);
                if (problem is not null)
                {
                    throw new FlowEndException(await ProblemText(problem));
                }
                if (answer!.Result is null)
                {
                    throw new FlowEndException(answer.Error ?? answer.Explanation);
                }
                (sql, result) = (answer.Sql, answer.Result);
            }
        }
        catch (Insights.DataException ex)
        {
            throw new FlowEndException(ex.Message);
        }
        var names = result.Columns.Select(c => c.Name).ToList();
        var rows = new JsonArray([.. result.Rows.Select(r => (JsonNode)new JsonObject(names.Select((n, i) => KeyValuePair.Create(n, r[i]?.DeepClone()))))]);
        var table = new StringBuilder("| ").AppendJoin(" | ", names).Append(" |\n|").AppendJoin("", names.Select(_ => " --- |")).Append('\n');
        foreach (var r in result.Rows.Take(50))
        {
            table.Append("| ").AppendJoin(" | ", r.Select(v => FlowModel.Show(v).Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" "))).Append(" |\n");
        }
        if (result.Rows.Count > 50 || result.Truncated)
        {
            table.Append(CultureInfo.InvariantCulture, $"\n({result.Rows.Count}{(result.Truncated ? "+" : "")} rows; the first 50 here)\n");
        }
        return (table.ToString(), new JsonObject { ["sql"] = sql, ["columns"] = new JsonArray([.. names.Select(n => (JsonNode)n)]), ["rows"] = rows }, null);
    }

    private static readonly string[] Unsent = ["host", "content-length", "transfer-encoding", "connection", "cookie"];

    /// <summary>
    /// A request to a public address (never the stack's or the office's, whatever a name points to; the chat's allowed
    /// sites apply), its headers and body filled: what came back, as JSON when it is.
    /// </summary>
    private async Task<(string, JsonNode?, string?)> HttpAsync(FlowStep step, Dictionary<string, JsonNode?> values, CancellationToken ct)
    {
        var method = step.Method ?? "GET";
        var address = FlowModel.Fill(step.Url, values).Trim();
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url))
        {
            throw new FlowEndException($"{address} is not an address.");
        }
        if (await web.RefusalAsync(url, ct) is { } refused)
        {
            throw new FlowEndException(refused);
        }
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        var body = FlowModel.Fill(step.Text, values);
        if (method != "GET" && body.Length > 0)
        {
            request.Content = new StringContent(body, Encoding.UTF8, body.TrimStart() is ['{' or '[', ..] ? "application/json" : "text/plain");
        }
        foreach (var (name, value) in step.Arguments ?? [])
        {
            var header = name.Trim();
            if (header.Length == 0 || Unsent.Contains(header, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            var filled = FlowModel.Fill(value, values);
            if (!request.Headers.TryAddWithoutValidation(header, filled) && request.Content is not null)
            {
                request.Content.Headers.Remove(header);
                request.Content.Headers.TryAddWithoutValidation(header, filled);
            }
        }
        await audit.WriteAsync("workflow.http", _workflow.Name, detail: $"{method} {url.GetLeftPart(UriPartial.Path)}", actor: _owner);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var res = await http.CreateClient(WebFetcher.Client).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            await using var stream = await res.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[MostBody];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), timeout.Token) is > 0 and var n)
            {
                read += n;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (!res.IsSuccessStatusCode)
            {
                throw new FlowEndException($"{url.Host} answered {(int)res.StatusCode} {res.ReasonPhrase}: {Short(text)}");
            }
            return (text, text.TrimStart() is ['{' or '[', ..] ? FlowModel.JsonIn(text) : null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or WebAccessException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new FlowEndException(ex is TaskCanceledException ? $"{url.Host} did not answer in 30 seconds." : $"{url.Host} could not be reached: {ex.Message}");
        }
    }

    /// <summary>
    /// Acts in GitLab with the app's bot: a comment, labels added, an issue opened. The project and merge request or issue
    /// are the step's, or else those of the event that started the run.
    /// </summary>
    private async Task<(string, JsonNode?, string?)> GitLabAsync(FlowStep step, Dictionary<string, JsonNode?> values, CancellationToken ct)
    {
        var reply = _run.Reply is null ? null : JsonSerializer.Deserialize<GitLabTarget>(_run.Reply, FlowModel.Json);
        var named = FlowModel.Fill(step.Project, values).Trim();
        var project = named.Length > 0 ? Uri.EscapeDataString(named)
            : reply?.Project.ToString(CultureInfo.InvariantCulture) ?? throw new FlowEndException("Name its project: no GitLab event started this run.");
        var which = FlowModel.Fill(step.Target, values).Trim();
        (string Kind, string Id)? target = which switch
        {
            ['!', .. var n] when long.TryParse(n, CultureInfo.InvariantCulture, out _) => ("merge_requests", n),
            ['#', .. var n] when long.TryParse(n, CultureInfo.InvariantCulture, out _) => ("issues", n),
            "" when named.Length == 0 && reply is not null => (reply.Kind, reply.Id),
            "" => null,
            _ => throw new FlowEndException($"{which} is neither a merge request (!12) nor an issue (#34)."),
        };
        var labels = string.Join(',', (FlowModel.Fill(step.Labels, values)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        try
        {
            switch (step.Action)
            {
                case "comment":
                {
                    if (target is not { } t)
                    {
                        throw new FlowEndException("Name the merge request (!12) or issue (#34) to comment on.");
                    }
                    await gitlab.NoteAsync(project, t.Kind, t.Id, $"{FlowModel.Fill(step.Text, values)}\n\n---\n*{_workflow.Name}, {Triggers.Signature} {_owner.DisplayName}*", ct);
                    await audit.WriteAsync("workflow.gitlab_comment", _workflow.Name, detail: $"project {(named.Length > 0 ? named : project)}, {t.Kind} {t.Id}", actor: _owner);
                    return ($"Commented on {(t.Kind == "issues" ? "#" : t.Kind == "merge_requests" ? "!" : "commit ")}{t.Id}.", null, null);
                }
                case "label":
                {
                    if (target is not { Kind: "merge_requests" or "issues" } t)
                    {
                        throw new FlowEndException("Labels go on a merge request (!12) or an issue (#34).");
                    }
                    await gitlab.SendAsync(HttpMethod.Put, $"/projects/{project}/{t.Kind}/{t.Id}", new JsonObject { ["add_labels"] = labels }, ct);
                    await audit.WriteAsync("workflow.gitlab_label", _workflow.Name, detail: $"project {(named.Length > 0 ? named : project)}, {t.Kind} {t.Id}: {labels}", actor: _owner);
                    return ($"Labelled {(t.Kind == "issues" ? "#" : "!")}{t.Id}: {labels}.", null, null);
                }
                default:
                {
                    var made = await gitlab.SendAsync(HttpMethod.Post, $"/projects/{project}/issues", new JsonObject
                    {
                        ["title"] = FlowModel.Fill(step.Subject, values).Trim(), ["description"] = $"{FlowModel.Fill(step.Text, values)}\n\n---\n*{_workflow.Name}, {Triggers.Signature} {_owner.DisplayName}*",
                        ["labels"] = labels,
                    }, ct);
                    var url = made?["web_url"]?.GetValue<string>();
                    await audit.WriteAsync("workflow.gitlab_issue", _workflow.Name, detail: $"project {(named.Length > 0 ? named : project)}: {url}", actor: _owner);
                    return ($"Opened #{made?["iid"]}: {url}", made?.DeepClone(), url);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new FlowEndException($"GitLab did not take it: {ex.Message}");
        }
    }

    /// <summary>Said in a room as the owner, for its people (not to the model): the owner may write in it.</summary>
    private async Task<(string, JsonNode?, string?)> RoomAsync(Guid room, string text, CancellationToken ct)
    {
        var c = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == room && x.Kind == RoomMember.Room, ct);
        var writes = c is not null && (c.UserId == _owner.Id || await db.RoomMembers.AnyAsync(m => m.ConversationId == room && m.UserId == _owner.Id && m.Role != RoomMember.Viewer, ct));
        if (!writes)
        {
            throw new FlowEndException("Its owner may not write in that room.");
        }
        var last = await db.QueuedMessages.Where(q => q.ConversationId == room).MaxAsync(q => (int?)q.Position, ct) ?? 0;
        db.QueuedMessages.Add(new QueuedMessage { ConversationId = room, Content = text, Position = last + 1, UserId = _owner.Id, ForPeople = true, Mentions = [] });
        await db.SaveChangesAsync(ct);
        runs.Posted(room, text);
        await queue.StartNextAsync(room);
        return ($"Said in {c!.Title}.", null, $"/chat/{room}");
    }
}
