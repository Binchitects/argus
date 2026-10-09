using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Access;
using Llm.Api.Chat.Tools;
using Llm.Api.Gateway;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>For one answer only: another model or thinking level than the chat's (retry with…).</summary>
/// <param name="Hurry">"Answer now", when the person asks for it while the model thinks.</param>
/// <param name="Research">Deep research: the web and sub-agents on for this answer, a plan, and a sourced report.</param>
/// <param name="Again">Said with the question for this answer only: answer again shorter or longer (AnswerLengths.Again), or answer in spoken sentences (Talk.Note).</param>
/// <param name="Titled">The chat's first question: the model for small steps writes its title beside the answer (ChatTitles).</param>
/// <param name="Unattended">Nobody watches the answer (a bot's thread, a scheduled task): deep research is not offered while it asks first (nobody would press Allow).</param>
/// <param name="Compare">One of Compare's two answers: the model starts no deep research (that is one model's report).</param>
/// <param name="Line">Waits for a place in the line of the model that answers (AnswerGate), saying so meanwhile; null: no line (AnswerJobs gives one).</param>
public sealed record AnswerOverrides(string? Model = null, string? Thinking = null, Hurry? Hurry = null, bool Research = false, string? Again = null, bool Titled = false,
    bool Unattended = false, bool Compare = false, Func<string, CancellationToken, Task<IDisposable>>? Line = null);

/// <summary>
/// "Answer now" (as in ChatGPT and Gemini): the person asked the answer to stop thinking.
/// The model's thinking so far is kept; it is asked again without thinking, and the rest
/// of the answer (its later rounds after tool calls) thinks no more.
/// </summary>
public sealed class Hurry
{
    private int _asked;

    public bool Asked => Volatile.Read(ref _asked) == 1;

    /// <returns>False when asked already.</returns>
    public bool Ask() => Interlocked.Exchange(ref _asked, 1) == 0;
}

/// <summary>
/// One answer to a question in a conversation. The conversation is a tree: the
/// model reads the path from the first message to the question, streams, calls
/// the chat's tools (Argus, pictures, MCP servers...) as the person when it wants
/// to, and every step is saved as a child of the one before, so the answer is its
/// own branch. Events go to the browser as they happen (see ChatEndpoints).
/// </summary>
public sealed partial class ChatService(
    AppDbContext db,
    GatewayChat gateway,
    Safeguards.Safeguards safeguards,
    ToolRegistry registry,
    ToolApprovals approvals,
    AccessService access,
    Models.ModelPolicy policy,
    ChatModels models,
    SmallModel small,
    AutoModel auto,
    Media media,
    AnswerGate gate,
    Identity.Audit audit,
    Memories memories,
    IOptionsMonitor<ChatOptions> chat,
    Knowledge.Retrieval retrieval,
    PriceBook prices,
    IServiceScopeFactory scopes,
    ILogger<ChatService> logger)
{
    /// <summary>Deep research: rounds of tool calls an answer may take (plan, sub-agents, gaps, report).</summary>
    private const int ResearchRounds = 16;

    /// <summary>
    /// Deep research with sub-agents: in how many rounds the answer may delegate (the research, then
    /// its gaps) before it writes the report. The delegate calls of one round are one step, and a call
    /// whose parts never ran (refused for its arguments, or declined) is none. Measured: the answer's
    /// own model reading pages to fill gaps took 20 minutes of a 27-minute answer, its sub-agents (on
    /// the small model) under two.
    /// </summary>
    internal const int ResearchDelegations = 2;

    /// <summary>Deep research's steps, for the answer only. <paramref name="byAgents"/>: its sub-agents research and read the web, and it delegates and writes.</summary>
    internal static string ResearchNote(bool byAgents) =>
        "Deep research: the person asked for a thorough, sourced report, and waits for it. Work in steps.\n" +
        "1. Plan: break the question into 2 to 4 research questions that cover its angles (facts, recent changes, numbers, " +
        "opposing views). Say the plan in one short line.\n" +
        (byAgents
            ? "2. Research: call delegate once, one part per question: the parts run side by side, each with its own tools. Tell each " +
              "part to search the web (web_search), open at most two of the best sources (fetch_page, with a focus), and bring back findings with each " +
              "source's title and URL, briefly.\n" +
              "3. Fill gaps: if something important is missing or sources disagree, call delegate once more with just those questions " +
              "(twice in all at most). The parts read the web, not you.\n"
            : "2. Research each question with the tools you have: search the web (web_search) and open at most two of the best sources " +
              "(fetch_page, with a focus).\n" +
              "3. Fill gaps: if something important is missing or sources disagree, research that too.\n") +
        "4. Report: a title; a short summary of the answer; sections by theme; a table when it compares things; what is uncertain " +
        "or disputed; and numbered citations [1] in the text, listed under a Sources heading at the end with their URLs (always). Prefer primary, " +
        "recent sources. Cite only what you opened; never make up a source or a URL.";

    /// <summary>An image counts as this many characters of the context budget (roughly 1,000 tokens).</summary>
    private const int ImageWeight = 3_500;

    /// <summary>What the chat can run, so the model writes code that previews (src/web/src/preview).</summary>
    internal const string PreviewNote =
        "The chat shows a live preview of code blocks fenced as html, svg, mermaid, jsx or tsx. " +
        "Previews have no network: use no CDN or API calls. A page may use Tailwind (its CDN script is served locally). " +
        "A React component (jsx or tsx) is shown from its default export and may import only react, react-dom and lucide-react; style it with Tailwind classes. " +
        "A mermaid block is drawn as a diagram in the answer itself: use one for a workflow, an architecture, a sequence, a data flow or states. " +
        "In Mermaid, quote any label with punctuation (A[\"parse(input)\"]) and give each diagram an accTitle line; " +
        "a pie shows its values with \"pie showData\" (there is no donut), and a state diagram's choice is \"state Name <<choice>>\".";

    /// <summary>The answer's clock, for its trace (one answer per scope).</summary>
    private AnswerClock? _answerClock;

    public async Task AnswerAsync(AppUser user, Conversation conversation, ChatMessage question, AnswerOverrides overrides, Func<object, Task> emit, CancellationToken ct)
    {
        _answerClock = new AnswerClock();
        var email = user.Email!.ToLowerInvariant();
        // The model for sub-agents and small steps, when one is set and this person may use it now.
        var helper = await small.ForAsync(user, ct);
        var (model, modelName, refusal) = await ModelForAsync(user, conversation, overrides.Model, ct);
        // Auto: the small model answers an easy question itself, and hands the rest to the chat's main model.
        var route = auto.IsAuto(conversation, overrides) ? await auto.RouteAsync(user, question, overrides, helper, modelName, ct) : null;
        if (route is { Small: true })
        {
            (model, modelName, refusal) = (helper, helper!.Name, null);
        }
        if (refusal is not null)
        {
            var sequence = (await db.ChatMessages.Where(m => m.ConversationId == conversation.Id).MaxAsync(m => (int?)m.Sequence, ct) ?? 0) + 1;
            var refused = new ChatMessage
            {
                ConversationId = conversation.Id, ParentId = question.Id, Role = "assistant", Sequence = sequence, Model = modelName,
                Status = MessageStatus.Failed, Error = refusal,
            };
            refused.AnswerId = refused.Id;
            db.ChatMessages.Add(refused);
            conversation.CurrentLeafId = refused.Id;
            await emit(new { type = "assistant", id = refused.Id, parentId = question.Id, model = modelName });
            await FinishAsync(conversation, ct);
            await emit(new { type = "error", message = refusal });
            return;
        }
        var thinking = overrides.Thinking ?? route?.Thinking ?? conversation.Thinking;

        // Fair use: a place among the few this model serves at once, in turn (AnswerGate); other models' lines are not this one's.
        var inLine = Stopwatch.StartNew();
        using var place = overrides.Line is { } line ? await line(modelName, ct) : null;
        _answerClock.Placed((int)inLine.ElapsedMilliseconds);

        // The chat's tools that this person may use, each made ready for this answer.
        var runs = new Dictionary<string, (ToolChoice Choice, IToolRun Run)>();
        var progress = new ToolProgress(emit);
        var tools = new JsonArray();
        var instructions = new List<(string Tool, string Text)>();
        // Sub-agents get the answer's tools, filled in below before any call, and the small model without thinking
        // when there is one that calls tools (else the answer's model and thinking).
        var (agentModel, agentThinking) = helper is { Tools: true } ? (helper.Name, SmallModel.NoThinking(helper)) : (modelName, model?.Thinking == false ? null : thinking);
        var kit = new AgentKit(agentModel, agentThinking, email, runs, tools, instructions, progress, emit, user, conversation);

        // A tool made ready for this answer: its functions offered and its notes kept. One whose server cannot be reached says so.
        async Task StartToolAsync(ToolChoice choice)
        {
            IToolRun run;
            try
            {
                run = await choice.Tool.StartAsync(new ToolContext(user, email, conversation, progress) { Agents = (parts, token) => AgentsAsync(parts, kit, token) }, ct);
            }
            catch (McpException ex)
            {
                await emit(new
                {
                    type = "notice", kind = choice.Tool.Id == "argus" ? "argus_unavailable" : "tool_unavailable",
                    text = $"{choice.Tool.Title} is not available for this answer: {ex.Message}",
                });
                return;
            }
            foreach (var f in run.Functions.OfType<JsonObject>())
            {
                if (f["function"]?["name"]?.GetValue<string>() is { } fname && runs.TryAdd(fname, (choice, run)))
                {
                    tools.Add(f.DeepClone());
                }
            }
            if (!string.IsNullOrWhiteSpace(run.Instructions))
            {
                instructions.Add((choice.Tool.Id, run.Instructions.Trim()));
            }
        }

        // Deep research: asked for with the composer's switch, or (below) started by the model with the Deep research tool.
        var research = overrides.Research;
        IReadOnlyList<ToolChoice> allowed = model?.Tools != false || research ? await registry.ForAsync(await access.MembershipAsync(user, ct), ct) : [];
        if (research && allowed.All(t => t.Tool.Id != ResearchTool.ToolId))
        {
            // Asked for before an admin took deep research away from this person (a message queued then): a plain answer.
            research = false;
            await emit(new { type = "notice", kind = "research_off", text = "Deep research is not available to you any more (an admin decides who may use it): this is a plain answer." });
        }
        if (model?.Tools != false)
        {
            // The model starts no deep research while this answer is one, while Auto's small model answers, in Compare (deep research is
            // one model's report), nor, while it asks first, in an answer nobody watches: nobody would press Allow, and it would wait.
            var chosen = ToolRegistry.Chosen(conversation.Tools, allowed)
                .Where(t => t.Tool.Id != ResearchTool.ToolId || !(research || route is { Small: true } || overrides.Compare || (overrides.Unattended && t.Setting.AskFirst)))
                .ToList();
            if (research)
            {
                // Deep research needs the web and sub-agents, for this answer, when the person may use them.
                chosen.AddRange(allowed.Where(t => t.Tool.Id is "web" or "agents" && chosen.All(c => c.Tool.Id != t.Tool.Id)));
                if (chosen.All(c => c.Tool.Id != "web"))
                {
                    await emit(NoWebForResearch);
                }
            }
            foreach (var choice in chosen)
            {
                await StartToolAsync(choice);
            }
        }

        // Deep research with sub-agents: the parts research and the web is theirs, and the answer delegates at most
        // twice, then writes. Not when the web asks before each call: a part has nobody there to allow it, so the
        // answer reads the web itself, as the person allows.
        var researchByAgents = research && ByAgents(runs);
        // The answer's notes: in deep research its steps, and not the web's while its functions are the parts'.
        // Sub-agents get the tools' own notes (kit.Instructions), the web's among them.
        List<(string Tool, string Text)> answerNotes = [.. instructions];
        if (research)
        {
            answerNotes = [.. instructions.Where(i => !(researchByAgents && i.Tool == "web")), ("research", ResearchNote(researchByAgents))];
            // The page says which step deep research is on.
            await emit(new { type = "research" });
        }
        // Past the budget, the tools go on demand: those the chat loaded whole, a line for each other.
        var demand = new OnDemandTools(tools, runs, answerNotes, conversation.LoadedTools, chat.CurrentValue.ToolTextChars);
        if (research && demand.Load(["web", "agents"]).Added.Count > 0)
        {
            conversation.LoadedTools = demand.Loaded;
        }
        kit = kit with { Demand = demand };
        var (messages, imagesDropped, systemParts) = await BuildHistoryAsync(conversation, question, model, modelName, helper, email, demand.Notes(), runs.ContainsKey("read_file"),
            AnswerLengths.Note(user.AnswerLength), await memories.NoteAsync(user, ct), emit, ct);
        // Said on the person's turn (models follow it more closely there), at the prompt's end (the
        // cache keeps the rest); the question kept stays as written.
        if (research)
        {
            ToQuestion(messages, researchByAgents
                ? "\n\n(Deep research: plan the research questions, call delegate once with one part per question, " +
                  "delegate once more only for real gaps, then write the report with numbered citations and a Sources list of the pages opened.)"
                : "\n\n(Deep research: plan the research questions, research each with your tools, " +
                  "then write the report with numbered citations and a Sources list of the pages opened.)");
        }
        if (overrides.Again is { Length: > 0 } again)
        {
            ToQuestion(messages, "\n\n" + again);
        }
        if (imagesDropped)
        {
            await emit(new { type = "notice", kind = "no_vision", text = $"{modelName} cannot see images, so it got their names only. Choose a model that can see to ask about them." });
        }
        var next = await db.ChatMessages.Where(m => m.ConversationId == conversation.Id).MaxAsync(m => (int?)m.Sequence, ct) ?? 0;
        var parent = question.Id;
        // Deep research with sub-agents: the rounds in which the answer delegated (ResearchDelegations).
        var delegations = 0;
        // Each round's cost at the model's prices now (the gateway was given the same); every round and tool call names the answer.
        var price = await prices.ForAsync(modelName, ct);
        Guid? answerId = null;

        // The model started deep research (the Deep research tool, allowed): from its next round this answer is one, as if asked
        // with the composer's switch. The web and sub-agents join it when the person may use them; its steps are the call's
        // result, at the prompt's end (the cache keeps the rest), not in the system prompt.
        async Task<ToolResult> ResearchNowAsync(string topic)
        {
            if (await safeguards.TakeResearchAsync(user.Id, ct) is { } refused)
            {
                return new ToolResult(refused, IsError: true);
            }
            var (functionsBefore, notesBefore) = (tools.Count, instructions.Count);
            foreach (var choice in allowed.Where(t => t.Tool.Id is "web" or "agents" && runs.Values.All(r => r.Choice.Tool.Id != t.Tool.Id)))
            {
                await StartToolAsync(choice);
            }
            foreach (var f in tools.Skip(functionsBefore).OfType<JsonObject>())
            {
                demand.Add(runs[f["function"]!["name"]!.GetValue<string>()].Choice.Tool, f);
            }
            if (runs.Values.All(r => r.Choice.Tool.Id != "web"))
            {
                await emit(NoWebForResearch);
            }
            research = true;
            researchByAgents = ByAgents(runs);
            answerNotes.AddRange(instructions.Skip(notesBefore));
            if (researchByAgents)
            {
                answerNotes.RemoveAll(i => i.Tool == "web");
            }
            demand.Load(["web", "agents"]);
            await emit(new { type = "research" });
            return new ToolResult($"Deep research is on for this answer, on: {topic}\n\n{ResearchNote(researchByAgents)}")
            {
                Details = new JsonObject { ["research"] = topic },
            };
        }

        for (var round = 0; ; round++)
        {
            var msg = new ChatMessage { ConversationId = conversation.Id, ParentId = parent, Role = "assistant", Sequence = ++next, Model = modelName };
            answerId ??= msg.Id;
            msg.AnswerId = answerId;
            db.ChatMessages.Add(msg);
            conversation.CurrentLeafId = msg.Id;
            await emit(new { type = "assistant", id = msg.Id, parentId = parent, model = modelName });
            if (round == 0 && route is not null)
            {
                // Who answers on Auto, and why: kept with the answer, said under it.
                msg.DetailsJson = new JsonObject { ["route"] = route.ToJson() }.ToJsonString();
                await emit(new { type = "route", id = msg.Id, route = route.ToJson() });
            }

            var request = new JsonObject
            {
                ["model"] = modelName,
                ["messages"] = messages.DeepClone(),
                ["stream"] = true,
                ["stream_options"] = new JsonObject { ["include_usage"] = true },
                // Enforcement: the end-user budget binds on this field (the header only attributes).
                ["user"] = email,
            };
            if (conversation.Temperature is { } temperature)
            {
                request["temperature"] = temperature;
            }
            if (conversation.TopP is { } topP)
            {
                request["top_p"] = topP;
            }
            if (conversation.MaxTokens is { } maxTokens)
            {
                request["max_tokens"] = maxTokens;
            }
            // A model that does not think gets no thinking switches: its template may not know them (Qwen3-Omni's answers nothing).
            if (model?.Thinking != false && ThinkingPresets.TemplateKwargs(overrides.Hurry?.Asked == true ? "off" : round > 0 ? ThinkingPresets.Between(thinking, chat.CurrentValue) : thinking) is { } kwargs)
            {
                request["chat_template_kwargs"] = kwargs;
            }
            if (tools.Count > 0 && chat.CurrentValue.MaxToolRounds > 0)
            {
                Tools(request, researchByAgents ? WithoutWeb(demand.Request(), runs) : demand.Request(),
                    last: round >= (research ? Math.Max(ResearchRounds, chat.CurrentValue.MaxToolRounds) : chat.CurrentValue.MaxToolRounds)
                        || (researchByAgents && delegations >= ResearchDelegations));
            }

            // What fills this request, for the context gauge (scaled to the prompt tokens the model reports).
            var filled = ContextParts.Measure(request, systemParts, ImageWeight);
            msg.ContextJson = filled.ToJsonString();
            var content = new StringBuilder();
            var reasoning = new StringBuilder();
            var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
            var clock = Stopwatch.StartNew();
            var timing = _answerClock.Round(msg);
            TimeSpan? thoughtFrom = null;
            void EndThinking()
            {
                if (thoughtFrom is { } from && msg.ThinkingMs is null)
                {
                    msg.ThinkingMs = (int)(clock.Elapsed - from).TotalMilliseconds;
                }
            }
            void Keep(MessageStatus status)
            {
                EndThinking();
                msg.Content = content.ToString();
                msg.Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null;
                msg.Status = status;
                msg.DurationMs = (int)clock.Elapsed.TotalMilliseconds;
                msg.TraceJson = _answerClock.Trace(timing, msg);
            }
            try
            {
                for (var pass = 0; ; pass++)
                {
                var cut = false;
                await foreach (var e in gateway.StreamAsync(request, email, conversation.Id, ct))
                {
                    timing.Saw(e);
                    switch (e)
                    {
                        // Answer now: still thinking (no word, no tool call yet), it stops here.
                        case ReasoningDelta when pass == 0 && overrides.Hurry?.Asked == true && content.Length == 0 && calls.Count == 0:
                            cut = true;
                            break;
                        case ReasoningDelta r:
                            thoughtFrom ??= clock.Elapsed;
                            reasoning.Append(r.Text);
                            await emit(new { type = "reasoning", text = r.Text });
                            break;
                        case ContentDelta c:
                            if (thoughtFrom is not null && msg.ThinkingMs is null)
                            {
                                EndThinking();
                                await emit(new { type = "thought", ms = msg.ThinkingMs });
                            }
                            content.Append(c.Text);
                            await emit(new { type = "content", text = c.Text });
                            break;
                        case ToolCallDelta t:
                            EndThinking();
                            var slot = calls.TryGetValue(t.Index, out var existing) ? existing : (null, null, new StringBuilder());
                            calls[t.Index] = (t.Id ?? slot.Id, t.Name ?? slot.Name, slot.Args.Append(t.Arguments));
                            break;
                        case UsageReport u:
                            (msg.PromptTokens, msg.CachedTokens, msg.CompletionTokens) = (u.Prompt, u.Cached, u.Completion);
                            msg.Cost = price.Cost(u.Prompt, u.Cached, u.Completion);
                            break;
                    }
                    if (cut)
                    {
                        break;
                    }
                }
                if (!cut)
                {
                    break;
                }
                // The same request again, without thinking: the model answers with what it has.
                EndThinking();
                msg.CutShort = true;
                await emit(new { type = "thought", ms = msg.ThinkingMs, cutShort = true });
                request["chat_template_kwargs"] = ThinkingPresets.TemplateKwargs("off");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Stop: keep what arrived. The stop cancelled the token, so save without it.
                Keep(MessageStatus.Stopped);
                await FinishAsync(conversation, CancellationToken.None);
                await emit(new { type = "stopped", id = msg.Id });
                return;
            }
            catch (ChatGatewayException ex)
            {
                Keep(MessageStatus.Failed);
                msg.Error = ex.Message;
                await FinishAsync(conversation, CancellationToken.None);
                await emit(new { type = "error", message = ex.Message });
                return;
            }

            Keep(MessageStatus.Complete);
            await emit(new
            {
                type = "usage", prompt = msg.PromptTokens, cached = msg.CachedTokens, completion = msg.CompletionTokens, cost = msg.Cost,
                thinkingMs = msg.ThinkingMs, durationMs = msg.DurationMs, context = filled,
            });

            if (calls.Count == 0 || runs.Count == 0)
            {
                // Written in full: a stop now must not lose it.
                await FinishAsync(conversation, CancellationToken.None);
                await emit(new { type = "done", id = msg.Id });
                return;
            }

            // The model asked for tools: record the request, run each as the person, feed the answers back.
            var toolCalls = new JsonArray([.. calls.Select(kv => (JsonNode)new JsonObject
            {
                ["id"] = kv.Value.Id ?? $"call_{round}_{kv.Key}",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = kv.Value.Name ?? "", ["arguments"] = kv.Value.Args.ToString() },
            })]);
            msg.ToolCallsJson = toolCalls.ToJsonString();
            await db.SaveChangesAsync(ct);
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = msg.Content, ["tool_calls"] = toolCalls.DeepClone() });
            parent = msg.Id;

            // Questions for the person end the answer: their reply comes as their next message.
            var ends = false;
            var delegated = false;
            var loadedBefore = demand.Loaded.Count;
            foreach (var call in toolCalls.OfType<JsonObject>())
            {
                var id = call["id"]!.GetValue<string>();
                var name = call["function"]!["name"]!.GetValue<string>();
                var rawArgs = call["function"]!["arguments"]!.GetValue<string>();
                var known = runs.TryGetValue(name, out var target);
                var loading = demand.Active && name == OnDemandTools.Function;
                if (known && !demand.IsLoaded(name))
                {
                    // Called straight from the list: it is loaded now.
                    demand.Load([name]);
                }
                await emit(new { type = "tool_call", id, name, arguments = rawArgs, tool = known ? target.Choice.Tool.Id : null });
                ToolResult outcome;
                var declined = false;
                var took = Stopwatch.StartNew();
                try
                {
                    var args = Arguments(rawArgs);
                    if (loading)
                    {
                        outcome = LoadTools(demand, args);
                    }
                    else if (!known)
                    {
                        outcome = new ToolResult($"There is no tool named {name}.", IsError: true);
                    }
                    else if (researchByAgents && target.Choice.Tool.Id == "web")
                    {
                        // Named from memory or from the list: the parts read the web in deep research.
                        outcome = new ToolResult("In deep research the parts read the web: call delegate with what is missing, or write the report.", IsError: true);
                    }
                    else if (target.Choice.Tool.Id == ResearchTool.ToolId && (research || ResearchTool.Question(args) is null))
                    {
                        // Nothing to ask the person about: this answer researches already, or the call says not what.
                        outcome = research ? new ToolResult("This answer is deep research already: go on with its steps.") : ResearchTool.NoQuestion;
                    }
                    else if ((target.Choice.Setting.AskFirst || target.Run.AsksFirst(name)) && !await AskAsync(conversation, id, name, rawArgs, target.Choice.Tool, emit, ct))
                    {
                        declined = true;
                        outcome = new ToolResult($"The person did not allow {target.Choice.Tool.Title} to run this call. Do not try it again unless they ask.", IsError: true);
                    }
                    else if (target.Choice.Tool.Id == ResearchTool.ToolId)
                    {
                        took.Restart();
                        outcome = await ResearchNowAsync(ResearchTool.Question(args)!);
                    }
                    else
                    {
                        took.Restart();
                        progress.CallId = id;
                        outcome = await target.Run.CallAsync(name, args, ct);
                    }
                }
                catch (JsonException)
                {
                    outcome = new ToolResult($"The arguments were not valid JSON: {rawArgs}", IsError: true);
                }
                catch (McpException ex)
                {
                    outcome = new ToolResult(ex.Message, IsError: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A tool's own fault ends that call, not the answer: the model is told, and can go on.
                    LogToolFailed(logger, name, ex);
                    outcome = new ToolResult($"{name} failed: {ex.Message}", IsError: true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await FinishAsync(conversation, CancellationToken.None);
                    await emit(new { type = "stopped", id = (Guid?)null });
                    return;
                }
                await AuditPluginAsync(known ? target.Choice.Tool : null, name, outcome, user, declined);
                // A delegation counts when its parts ran (their work is in the details), not when it was refused or declined.
                delegated |= researchByAgents && name == AgentsTool.Function && outcome.Details?["agents"] is not null;
                if (!outcome.IsError && Oversized(name, outcome.Text, chat.CurrentValue.ToolResultChars, runs.ContainsKey("read_file"), user.Id) is { } kept)
                {
                    db.ChatAttachments.Add(kept.File);
                    outcome = outcome with { Text = kept.Text, Files = [.. outcome.Files ?? [], kept.File] };
                }
                var (text, isError) = (outcome.Text, outcome.IsError);
                ends |= outcome.EndsAnswer;
                var result = new ChatMessage
                {
                    ConversationId = conversation.Id, ParentId = parent, Role = "tool", Sequence = ++next, ToolCallId = id, ToolName = name,
                    Content = text, Status = declined ? MessageStatus.Declined : isError ? MessageStatus.Failed : MessageStatus.Complete,
                    DurationMs = (int)took.Elapsed.TotalMilliseconds,
                    AttachmentsJson = outcome.Files is { Count: > 0 } made ? JsonSerializer.Serialize(made.Select(f => f.Id)) : null,
                    DetailsJson = outcome.Details?.ToJsonString(),
                    // What the call spent: its pictures, video or speech, or its sub-agents' tokens.
                    AnswerId = answerId, Cost = outcome.Cost,
                    PromptTokens = outcome.Usage?.Prompt, CachedTokens = outcome.Usage?.Cached, CompletionTokens = outcome.Usage?.Completion,
                };
                db.ChatMessages.Add(result);
                conversation.CurrentLeafId = result.Id;
                parent = result.Id;
                await db.SaveChangesAsync(ct);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = safeguards.Untrusted(name, text) });
                await emit(new
                {
                    type = "tool_result", id, messageId = result.Id, name, text, isError, declined, noAccess = ArgusMcp.IsNoAccess(text), durationMs = result.DurationMs, cost = result.Cost,
                    details = outcome.Details,
                    attachments = (outcome.Files ?? []).Select(f => new { f.Id, f.FileName, f.Size, f.Truncated, f.Kind, f.ContentType, original = f.Kind != "image" && f.Data != null }),
                });
            }
            if (delegated)
            {
                delegations++;
            }
            if (demand.Loaded.Count != loadedBefore)
            {
                // Loaded tools stay with the chat; their notes join the system prompt, the list loses them.
                conversation.LoadedTools = demand.Loaded;
                await db.SaveChangesAsync(ct);
                var system = messages[0]!["content"]!.GetValue<string>();
                var notes = demand.Notes();
                var spliced = system[..systemParts.Base] + (notes.Length > 0 ? "\n\n" + notes : "") + system[(systemParts.Base + systemParts.ToolNotes)..];
                messages[0]!["content"] = spliced;
                systemParts = systemParts with { ToolNotes = systemParts.ToolNotes + spliced.Length - system.Length };
            }
            if (ends)
            {
                await FinishAsync(conversation, CancellationToken.None);
                await emit(new { type = "done", id = msg.Id });
                return;
            }
        }
    }

    /// <summary>
    /// A tool's result past the budget (Chat:ToolResultChars): the model reads its start, at a line's
    /// end, and the whole of it is a file in the chat that read_file reads on from there. Every later
    /// round and turn reads the result again, so one huge result would fill each of them.
    /// </summary>
    public static (string Text, ChatAttachment File)? Oversized(string tool, string text, int budget, bool canRead, Guid userId)
    {
        if (budget <= 0 || text.Length <= budget)
        {
            return null;
        }
        var cut = text.LastIndexOf('\n', budget);
        var atLineEnd = cut >= budget / 2;
        cut = atLineEnd ? cut : budget;
        var name = $"{tool}-result-{Guid.CreateVersion7().ToString("N")[^6..]}.txt";
        var file = new ChatAttachment { UserId = userId, FileName = name, ContentType = "text/plain", Size = Encoding.UTF8.GetByteCount(text), Kind = "text", Text = text, Origin = "tool" };
        // The first line not read whole.
        var lines = text.AsSpan(0, cut).Count('\n') + (atLineEnd ? 2 : 1);
        var said = canRead
            ? $"[The result is {text.Length:N0} characters; above are its first {cut:N0}. All of it is the file {name} in this chat: read_file reads on from line {lines}, search_file finds lines in it.]"
            : $"[The result is {text.Length:N0} characters; above are its first {cut:N0}. The rest is in the file {name}, which the person can open.]";
        return (text[..cut] + "\n\n" + said, file);
    }

    /// <summary>
    /// A call's arguments as the model wrote them. A key written twice (models do) keeps its last
    /// value, at every depth: JsonObject would otherwise throw on the first read.
    /// </summary>
    public static JsonObject Arguments(string raw)
    {
        using var doc = JsonDocument.Parse(raw.Trim().Length == 0 ? "{}" : raw);
        return Node(doc.RootElement) as JsonObject ?? [];

        static JsonNode? Node(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().Aggregate(new JsonObject(), (o, p) =>
            {
                o[p.Name] = Node(p.Value);
                return o;
            }),
            JsonValueKind.Array => new JsonArray([.. e.EnumerateArray().Select(Node)]),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => JsonNode.Parse(e.GetRawText()),
        };
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The tool function {Function} failed")]
    private static partial void LogToolFailed(ILogger logger, string function, Exception ex);

    /// <summary>Every call of a plugin's tool, in the audit log: who, which function, whether it went through.</summary>
    private Task AuditPluginAsync(IChatTool? tool, string function, ToolResult outcome, AppUser user, bool declined) =>
        tool is IServerTool { Server.Plugin: { } plugin }
            ? audit.WriteAsync("plugin.call", plugin, success: !outcome.IsError, detail: declined ? $"{function}, not allowed by the person" : function, actor: user)
            : Task.CompletedTask;

    /// <summary>load_tools: the functions asked for join the request from the next step on.</summary>
    private static ToolResult LoadTools(OnDemandTools demand, JsonObject args)
    {
        var names = args["names"] switch
        {
            JsonArray a => a.Select(n => n?.ToString() ?? ""),
            JsonValue v => v.ToString().Split(',', StringSplitOptions.TrimEntries),
            _ => [],
        };
        var (added, unknown) = demand.Load(names);
        var said = added.Count > 0 ? $"Loaded: {string.Join(", ", added)}. Call them from now on." : "Nothing new was loaded: what you asked for is already loaded.";
        if (unknown.Count > 0)
        {
            said += $" Not found: {string.Join(", ", unknown)}. The names are in the list under \"Tools to load\".";
        }
        return new ToolResult(said, IsError: added.Count == 0 && unknown.Count > 0);
    }

    /// <summary>What sub-agents of an answer work with: its model, thinking, person, tools and their instructions, and where events go.</summary>
    private sealed record AgentKit(string Model, string? Thinking, string Email, Dictionary<string, (ToolChoice Choice, IToolRun Run)> Runs, JsonArray Tools,
        List<(string Tool, string Text)> Instructions, ToolProgress Progress, Func<object, Task> Emit, AppUser User, Conversation Conversation)
    {
        /// <summary>The answer's tools on demand: sub-agents start with what it has loaded.</summary>
        public OnDemandTools? Demand { get; init; }
    }

    /// <summary>
    /// A sub-agent's own tools: the answer's tools made ready again in a scope of the sub-agent's
    /// own, so sub-agents running side by side never share a database context (a picture or a
    /// Python run each save their files). By function name; a tool whose server cannot be
    /// reached now is left out.
    /// </summary>
    private static async Task<Dictionary<string, IToolRun>> AgentToolsAsync(IServiceProvider services, AgentKit kit, HashSet<string> toolIds, CancellationToken ct)
    {
        var all = await services.GetRequiredService<ToolRegistry>().AllAsync(ct);
        var runs = new Dictionary<string, IToolRun>(StringComparer.Ordinal);
        foreach (var tool in all.Where(t => toolIds.Contains(t.Tool.Id)).Select(t => t.Tool))
        {
            try
            {
                var run = await tool.StartAsync(new ToolContext(kit.User, kit.Email, kit.Conversation, kit.Progress), ct);
                foreach (var name in run.Functions.OfType<JsonObject>().Select(f => f["function"]?["name"]?.GetValue<string>()).OfType<string>())
                {
                    runs.TryAdd(name, run);
                }
            }
            catch (McpException)
            {
                // Its server is down for this sub-agent: the others' tools still serve.
            }
        }
        return runs;
    }

    /// <summary>What a sub-agent's result may take of the answer's context.</summary>
    private const int AgentResultChars = 12_000;

    /// <summary>What of a sub-agent's tool results and thinking is kept for the page (the model got them whole).</summary>
    private const int AgentShownChars = 4_000;

    /// <summary>
    /// A sub-agent's rounds of tool calls, and what of one tool result it keeps in its context:
    /// each round reads its whole context again, so a few whole pages make every later call slow.
    /// </summary>
    private const int AgentRounds = 6;

    /// <summary>A sub-agent's tool calls in all: past them it answers with what it has (measured: unbounded, they opened 12 pages each).</summary>
    private const int AgentCalls = 5;

    /// <summary>The longest a sub-agent's step may write: its reply is for the assistant, and every token takes time.</summary>
    private const int AgentMaxTokens = 1_200;
    private const int AgentToolChars = 12_000;

    /// <summary>Text added to the newest question, in the request only.</summary>
    private static void ToQuestion(JsonArray messages, string text)
    {
        if (messages.OfType<JsonObject>().LastOrDefault(m => m["role"]?.GetValue<string>() == "user") is not { } asked)
        {
            return;
        }
        if (asked["content"] is JsonArray parts && parts.OfType<JsonObject>().FirstOrDefault(x => x["type"]?.GetValue<string>() == "text") is { } textPart)
        {
            textPart["text"] = textPart["text"]!.GetValue<string>() + text;
        }
        else if (asked["content"] is JsonValue)
        {
            asked["content"] = asked["content"]!.GetValue<string>() + text;
        }
    }

    /// <summary>
    /// The tools, in every round: the engine reuses the cache of a prompt's unchanged start, and
    /// the template writes the tools at the very start, so a round without them reads the whole
    /// conversation again. On the last allowed round they stay, calling them is switched off, and
    /// the newest tool result says so (a model that still sees its tools may write a call as text).
    /// </summary>
    public static void Tools(JsonObject request, JsonArray tools, bool last)
    {
        request["tools"] = tools.DeepClone();
        if (!last)
        {
            return;
        }
        request["tool_choice"] = "none";
        if (request["messages"] is JsonArray { Count: > 0 } messages && messages[^1] is JsonObject { } newest && newest["role"]?.GetValue<string>() == "tool")
        {
            newest["content"] = newest["content"]?.GetValue<string>() + "\n\n" + LastRoundNote;
        }
    }

    public const string LastRoundNote = "(No more tool calls are possible in this answer: answer now with what you have, and say what is still open.)";

    /// <summary>The functions without the web's: in deep research with sub-agents, they read the web and the answer writes.</summary>
    private static JsonArray WithoutWeb(JsonArray functions, Dictionary<string, (ToolChoice Choice, IToolRun Run)> runs) =>
        [.. functions.Where(f => !(f?["function"]?["name"]?.GetValue<string>() is { } n && runs.TryGetValue(n, out var r) && r.Choice.Tool.Id == "web"))
            .Select(f => f!.DeepClone())];

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "\n[cut to fit]";

    /// <summary>
    /// Whether sub-agents get a function: not delegating again, deep research, questions for the person
    /// or memory, nor a tool that asks before each call (nobody is there to allow it).
    /// </summary>
    private static bool ForAgents(string function, (ToolChoice Choice, IToolRun Run) tool) =>
        function is not AgentsTool.Function and not ResearchTool.Function and not AskTool.Function and not MemoryTool.Function
        && !tool.Choice.Setting.AskFirst && !tool.Run.AsksFirst(function);

    /// <summary>Whether deep research goes by sub-agents with these functions: they can delegate, and the web (if any) does not ask before each call.</summary>
    private static bool ByAgents(Dictionary<string, (ToolChoice Choice, IToolRun Run)> runs) =>
        runs.ContainsKey(AgentsTool.Function) && runs.All(kv => kv.Value.Choice.Tool.Id != "web" || ForAgents(kv.Key, kv.Value));

    /// <summary>Said when deep research has no web to read.</summary>
    private static readonly object NoWebForResearch = new
    {
        type = "notice", kind = "research_no_web", text = "Deep research works best with the web tool, which is not available to you: this answer uses what is.",
    };

    /// <summary>
    /// Sub-agents: each part of a task is asked of the answer's model on its own (a clean
    /// context: only its instructions), with the answer's tools except delegating again,
    /// questions for the person and tools that ask before each call (nobody is there to
    /// allow them). Up to Chat:AgentsAtOnce run at once, inside the answer's place in line.
    /// Each one's work streams to the page as it happens ("agent" events: its thinking,
    /// words, tool calls and their results) and is kept with the call (Details); the model
    /// reads only their results, in order.
    /// </summary>
    private async Task<ToolResult> AgentsAsync(IReadOnlyList<AgentTask> parts, AgentKit kit, CancellationToken ct)
    {
        var callId = kit.Progress.CallId;
        var usable = kit.Tools.OfType<JsonObject>()
            .Where(f => f["function"]?["name"]?.GetValue<string>() is { } n && kit.Runs.TryGetValue(n, out var r) && ForAgents(n, r))
            .Select(f => (JsonNode)f.DeepClone())
            .ToList();
        var notes = kit.Instructions.Where(i => i.Tool is not "agents" and not "ask" and not "memory").ToList();
        var toolIds = usable.Select(f => kit.Runs[f["function"]!["name"]!.GetValue<string>()].Choice.Tool.Id).ToHashSet(StringComparer.Ordinal);
        // No more at once than their model serves at once (its places): one more would push another's cache out, and it
        // would read its whole context again. Each keeps an engine slot of its own for its steps (SlotTable), as a chat does.
        var places = gate.PlacesOf(kit.Model);
        var atOnce = Math.Max(1, places > 0 ? Math.Min(chat.CurrentValue.AgentsAtOnce, places) : chat.CurrentValue.AgentsAtOnce);
        using var turns = new SemaphoreSlim(atOnce);
        var done = 0;
        async Task<(JsonObject Result, JsonObject Shown, List<ChatAttachment> Files, AgentRun Run)> RunAsync(AgentTask part, int index)
        {
            Task Say(object e) => callId is null ? Task.CompletedTask : kit.Emit(e);
            await turns.WaitAsync(ct);
            try
            {
                await Say(new { type = "agent", id = callId, index, @event = "start", title = part.Title, instructions = part.Instructions });
                await kit.Progress.ReportAsync(new McpProgress(done, parts.Count, $"{part.Title}: started"));
                var took = Stopwatch.StartNew();
                await using var scope = scopes.CreateAsyncScope();
                var own = await AgentToolsAsync(scope.ServiceProvider, kit, toolIds, ct);
                // Its own tools on demand: what the answer has loaded, and load_tools for the rest.
                var demand = new OnDemandTools(new JsonArray([.. usable.Select(u => u.DeepClone())]), kit.Runs, notes, kit.Demand?.Loaded, chat.CurrentValue.ToolTextChars);
                var run = await AgentAsync(part, kit, own, demand,
                    doing => kit.Progress.ReportAsync(new McpProgress(done, parts.Count, $"{part.Title}: {doing}")),
                    e => Say(new { type = "agent", id = callId, index, e.Event, e.Text, e.Call, e.IsError, e.Files }), ct);
                var finished = Interlocked.Increment(ref done);
                var ms = (int)took.Elapsed.TotalMilliseconds;
                var usage = new JsonObject { ["prompt"] = run.Usage.Prompt, ["cached"] = run.Usage.Cached, ["completion"] = run.Usage.Completion };
                await Say(new { type = "agent", id = callId, index, @event = "done", error = run.Error, ms, model = kit.Model, usage });
                await kit.Progress.ReportAsync(new McpProgress(finished, parts.Count, $"{part.Title}: {(run.Error is null ? "done" : "failed")}"));
                var result = new JsonObject { ["title"] = part.Title, ["result"] = Cut(run.Text, AgentResultChars), ["tool_calls"] = run.Steps.Count };
                if (run.Error is not null)
                {
                    result["error"] = run.Error;
                }
                var shown = new JsonObject
                {
                    ["title"] = part.Title, ["instructions"] = part.Instructions, ["text"] = run.Text, ["reasoning"] = Cut(run.Reasoning, AgentShownChars * 5),
                    ["steps"] = new JsonArray([.. run.Steps]), ["error"] = run.Error, ["ms"] = ms, ["model"] = kit.Model, ["usage"] = usage.DeepClone(),
                    ["speed"] = run.Timing.ToJson(),
                };
                return (result, shown, run.Files, run);
            }
            finally
            {
                turns.Release();
            }
        }
        var outcomes = await Task.WhenAll(parts.Select((p, i) => RunAsync(p, i)));
        var failed = outcomes.Count(o => o.Result.ContainsKey("error"));
        // What the sub-agents made (pictures, a Python run's files) is the call's: in the chat and its Files panel.
        return new ToolResult(new JsonArray([.. outcomes.Select(o => (JsonNode)o.Result)]).ToJsonString(Mcp.Plain), IsError: failed == outcomes.Length,
            Files: [.. outcomes.SelectMany(o => o.Files)])
        {
            Details = new JsonObject { ["agents"] = new JsonArray([.. outcomes.Select(o => (JsonNode)o.Shown)]) },
            // The call carries its sub-agents' tokens and cost: the answer's cost counts them.
            Usage = new UsageReport(outcomes.Sum(o => o.Run.Usage.Prompt), outcomes.Sum(o => o.Run.Usage.Cached), outcomes.Sum(o => o.Run.Usage.Completion)),
            Cost = outcomes.Sum(o => o.Run.Cost),
        };
    }

    /// <summary>A step of a sub-agent's work, for the page: its thinking or words as they come, a tool call, a tool's result.</summary>
    private sealed record AgentStep(string Event, string? Text = null, JsonObject? Call = null, bool? IsError = null, JsonArray? Files = null);

    /// <summary>What a sub-agent did: its last words, its thinking, its tool calls (each with its result), and why it stopped short, if so; what it made, the tokens it used, and the model's time.</summary>
    private sealed record AgentRun(string Text, string Reasoning, List<JsonNode> Steps, string? Error, List<ChatAttachment> Files, UsageReport Usage, TimingTotal Timing)
    {
        /// <summary>Its tokens at its model's prices, and the pictures, video or speech its tools made.</summary>
        public decimal Cost { get; init; }
    }

    /// <summary>One sub-agent: its own little answer loop, kept out of the chat (only its result goes to the model).</summary>
    private async Task<AgentRun> AgentAsync(AgentTask part, AgentKit kit, Dictionary<string, IToolRun> own, OnDemandTools demand, Func<string, Task> say,
        Func<AgentStep, Task> step, CancellationToken ct)
    {
        var preamble = $"Today is {DateTimeOffset.UtcNow.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)} (UTC).\n\n" +
            "You are a sub-agent: an assistant gave you one part of a larger task, and does the other parts elsewhere. Do only this part, with " +
            "your tools when they help, and reply with its result: the facts, names, file paths and links you found, as short bullets, in at most " +
            "about 200 words (every word you write takes time), for the assistant to put together with the other parts. Nobody else reads your " +
            "reply, and you cannot ask questions.";
        string System() => demand.Notes() is { Length: > 0 } notes ? preamble + "\n\n" + notes : preamble;
        var messages = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = System() }, new JsonObject { ["role"] = "user", ["content"] = part.Instructions });
        var tools = demand.Request();
        var steps = new List<JsonNode>();
        var made = new List<ChatAttachment>();
        var reasoning = new StringBuilder();
        var text = new StringBuilder();
        // Every round's tokens: the answer's cost counts its sub-agents' too. And its time, for the answer's trace.
        var used = new UsageReport(0, 0, 0);
        var timed = new TimingTotal();
        var price = await prices.ForAsync(kit.Model, ct);
        // What its tools spent (pictures, video, speech).
        var spent = 0m;
        decimal Cost() => price.Cost(used.Prompt, used.Cached, used.Completion) + spent;
        // Its steps go back to the engine slot that holds its start, as a chat's turns do.
        var holder = Guid.NewGuid();
        for (var round = 0; ; round++)
        {
            var request = new JsonObject
            {
                ["model"] = kit.Model, ["messages"] = messages.DeepClone(), ["stream"] = true,
                ["stream_options"] = new JsonObject { ["include_usage"] = true }, ["user"] = kit.Email,
            };
            // Without thinking, a cap on each step's words; a model that thinks needs its room.
            if (kit.Thinking is null or "off")
            {
                request["max_tokens"] = AgentMaxTokens;
            }
            if (ThinkingPresets.TemplateKwargs(kit.Thinking) is { } kwargs)
            {
                request["chat_template_kwargs"] = kwargs;
            }
            if (tools.Count > 0 && chat.CurrentValue.MaxToolRounds > 0)
            {
                Tools(request, demand.Request(), last: round >= Math.Min(chat.CurrentValue.MaxToolRounds, AgentRounds) || steps.Count >= AgentCalls);
            }
            text.Clear();
            var pending = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Args)>();
            var timing = new ModelTiming();
            UsageReport? roundUsage = null;
            try
            {
                await foreach (var e in gateway.StreamAsync(request, kit.Email, holder, ct))
                {
                    timing.Saw(e);
                    switch (e)
                    {
                        case ReasoningDelta r:
                            reasoning.Append(r.Text);
                            await step(new AgentStep("reasoning", r.Text));
                            break;
                        case ContentDelta c:
                            text.Append(c.Text);
                            await step(new AgentStep("content", c.Text));
                            break;
                        case ToolCallDelta t:
                            var slot = pending.TryGetValue(t.Index, out var existing) ? existing : (null, null, new StringBuilder());
                            pending[t.Index] = (t.Id ?? slot.Id, t.Name ?? slot.Name, slot.Args.Append(t.Arguments));
                            break;
                        case UsageReport u:
                            used = new UsageReport(used.Prompt + u.Prompt, used.Cached + u.Cached, used.Completion + u.Completion);
                            roundUsage = u;
                            break;
                    }
                }
            }
            catch (ChatGatewayException ex)
            {
                return new AgentRun(text.ToString().Trim(), reasoning.ToString(), steps, ex.Message, made, used, timed) { Cost = Cost() };
            }
            timed.Add(timing, roundUsage?.Prompt, roundUsage?.Cached, roundUsage?.Completion);
            if (pending.Count == 0)
            {
                return new AgentRun(text.ToString().Trim(), reasoning.ToString(), steps, null, made, used, timed) { Cost = Cost() };
            }
            var toolCalls = new JsonArray([.. pending.Select(kv => (JsonNode)new JsonObject
            {
                ["id"] = kv.Value.Id ?? $"agent_{round}_{kv.Key}",
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = kv.Value.Name ?? "", ["arguments"] = kv.Value.Args.ToString() },
            })]);
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = text.ToString(), ["tool_calls"] = toolCalls });
            var loadedBefore = demand.Loaded.Count;
            foreach (var call in toolCalls.OfType<JsonObject>())
            {
                var id = call["id"]!.GetValue<string>();
                var name = call["function"]!["name"]!.GetValue<string>();
                var raw = call["function"]!["arguments"]!.GetValue<string>();
                var shownCall = new JsonObject { ["id"] = id, ["name"] = name, ["arguments"] = raw };
                await step(new AgentStep("tool_call", Call: shownCall));
                await say(name.Replace('_', ' '));
                string result;
                var isError = false;
                JsonArray? files = null;
                var took = Stopwatch.StartNew();
                try
                {
                    if (demand.Active && name == OnDemandTools.Function)
                    {
                        var loaded = LoadTools(demand, Arguments(raw));
                        (result, isError) = (loaded.Text, loaded.IsError);
                    }
                    else if (!own.TryGetValue(name, out var target))
                    {
                        (result, isError) = ($"There is no tool named {name}.", true);
                    }
                    else
                    {
                        demand.Load([name]);
                        var outcome = await target.CallAsync(name, Arguments(raw), ct);
                        await AuditPluginAsync(kit.Runs.TryGetValue(name, out var chosen) ? chosen.Choice.Tool : null, name, outcome, kit.User, false);
                        (result, isError) = (outcome.Text, outcome.IsError);
                        spent += outcome.Cost ?? 0;
                        if (outcome.Files is { Count: > 0 } got)
                        {
                            made.AddRange(got);
                            files = new JsonArray([.. got.Select(f => (JsonNode)new JsonObject
                            {
                                ["id"] = f.Id, ["fileName"] = f.FileName, ["size"] = f.Size, ["truncated"] = f.Truncated, ["kind"] = f.Kind,
                                ["contentType"] = f.ContentType, ["original"] = f.Kind != "image" && f.Data != null,
                            })]);
                        }
                    }
                }
                catch (JsonException)
                {
                    (result, isError) = ($"The arguments were not valid JSON: {raw}", true);
                }
                catch (McpException ex)
                {
                    (result, isError) = (ex.Message, true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogToolFailed(logger, name, ex);
                    (result, isError) = ($"{name} failed: {ex.Message}", true);
                }
                var shown = Cut(result, AgentShownChars);
                await step(new AgentStep("tool_result", shown, new JsonObject { ["id"] = id }, isError, files));
                steps.Add(new JsonObject
                {
                    ["id"] = id, ["name"] = name, ["arguments"] = raw, ["result"] = shown, ["isError"] = isError, ["files"] = files?.DeepClone(), ["ms"] = (int)took.ElapsedMilliseconds,
                });
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = safeguards.Untrusted(name, Cut(result, AgentToolChars)) });
            }
            if (demand.Loaded.Count != loadedBefore)
            {
                messages[0]!["content"] = System();
            }
        }
    }

    /// <summary>The model an answer uses (a chat that chose none: the first loaded this person may use), and why it may not, if so.</summary>
    private async Task<(GatewayModel? Model, string Name, string? Refusal)> ModelForAsync(AppUser user, Conversation conversation, string? instead, CancellationToken ct)
    {
        // Auto's main model is the default one.
        var requested = (instead ?? conversation.Model) is { } chosen && chosen != SmallModel.Auto ? chosen : null;
        var model = requested is null
            ? (await policy.ForAsync(user, await models.ListAsync(ct), ct)).Default
            : await models.ResolveAsync(requested, ct);
        var refusal = model is null ? null : await policy.RefusalAsync(user, model.Name, ct);
        return (model, model?.Name ?? "default", refusal);
    }

    /// <summary>"Ask before running": the page shows the call, and the answer waits for yes or no (ten minutes at most).</summary>
    private async Task<bool> AskAsync(Conversation conversation, string callId, string name, string rawArgs, IChatTool tool, Func<object, Task> emit, CancellationToken ct)
    {
        var waiting = approvals.WaitAsync(conversation.Id, callId, TimeSpan.FromMinutes(10), ct);
        await emit(new { type = "approval", id = callId, name, arguments = rawArgs, tool = tool.Id, title = tool.Title });
        return await waiting;
    }

    private async Task FinishAsync(Conversation conversation, CancellationToken ct)
    {
        _answerClock?.End();
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The messages from the first one down to <paramref name="leaf"/>, following parents.</summary>
    public static List<ChatMessage> PathTo(IReadOnlyDictionary<Guid, ChatMessage> byId, Guid? leaf)
    {
        var path = new List<ChatMessage>();
        var seen = new HashSet<Guid>();
        for (var id = leaf; id is { } current && byId.TryGetValue(current, out var m) && seen.Add(current); id = m.ParentId)
        {
            path.Add(m);
        }
        path.Reverse();
        return path;
    }

    /// <summary>
    /// A sound or video in the question. A model that hears gets the sound itself (an input_audio part); one
    /// that does not, its transcript. A video's frames go as pictures to a model that can see. Returns what the
    /// parts weigh against the context budget.
    /// </summary>
    private async Task<long> MediaAsync(ChatAttachment f, StringBuilder text, List<JsonObject> parts, bool vision, bool hears, string email, CancellationToken ct)
    {
        var name = f.FileName.Replace("\"", "'", StringComparison.Ordinal);
        var length = f.Seconds is { } sec ? string.Create(CultureInfo.InvariantCulture, $" ({sec:0} s)") : "";
        var weight = 0L;
        if (f.Kind == "video")
        {
            text.Append("\n\n[video attached: ").Append(name).Append(length);
            if (vision)
            {
                var frames = await media.FramesAsync(f, ct);
                text.Append(": ").Append(frames.Count).Append(" frames below, taken at ").Append(string.Join(", ", frames.Select(x => string.Create(CultureInfo.InvariantCulture, $"{x.At:0.#} s"))));
                foreach (var (_, jpeg) in frames)
                {
                    parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) } });
                    weight += ImageWeight;
                }
            }
            else
            {
                text.Append(" (this model cannot see its pictures)");
            }
            text.Append(']');
        }
        var sound = f.Kind == "audio" ? f.Data : f.Sound;
        if (sound is null)
        {
            text.Append(f.Kind == "audio" ? $"\n\n[sound attached: {name} (empty)]" : "");
            return weight;
        }
        if (hears)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n\n[{(f.Kind == "audio" ? "sound attached" : "its sound")}: {name}{length}, below]");
            parts.Add(new JsonObject { ["type"] = "input_audio", ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(sound), ["format"] = "mp3" } });
            // Roughly 25 tokens a second of sound.
            return weight + (long)((f.Seconds ?? 30) * 90);
        }
        string said;
        try
        {
            said = await media.TranscriptAsync(f, email, ct);
        }
        catch (Exception ex) when (ex is MediaException or ChatGatewayException)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n\n[{(f.Kind == "audio" ? "sound" : "its sound")}: {name}{length} could not be written down: {ex.Message}]");
            return weight;
        }
        text.Append(CultureInfo.InvariantCulture, $"\n\n<transcript of=\"{name}\"{(f.Seconds is { } s ? string.Create(CultureInfo.InvariantCulture, $" seconds=\"{s:0}\"") : "")}>\n")
            .Append(said.Length == 0 ? "(no speech)" : (await safeguards.MaskerAsync(email, ct))(said)).Append("\n</transcript>");
        return weight;
    }

    /// <summary>
    /// An attachment as it goes into the question: whole when it is short; otherwise
    /// its start, cut at a line, and a note saying how long it is and how to read on.
    /// </summary>
    public static string Inline(ChatAttachment f, int budget, bool canReadFiles)
    {
        var cutWhenAttached = f.Truncated ? " (the file itself was longer: it was cut when it was attached)" : "";
        if (f.Text.Length <= budget)
        {
            return f.Truncated ? f.Text + $"\n[end of what was kept{cutWhenAttached}]" : f.Text;
        }
        var end = f.Text.LastIndexOf('\n', Math.Max(0, budget - 1));
        var shown = f.Text[..(end > budget / 2 ? end : budget)];
        var shownLines = shown.Count(c => c == '\n') + 1;
        var totalLines = f.Text.Count(c => c == '\n') + 1;
        var how = canReadFiles
            ? $"Read on with read_file (file \"{f.FileName}\", from_line {shownLines + 1}), or find parts with search_file."
            : "The rest is not included here.";
        return shown + $"\n[This file goes on: lines 1 to {shownLines} of {totalLines} are above ({shown.Length:N0} of {f.Text.Length:N0} characters){cutWhenAttached}. {how}]";
    }

    /// <summary>
    /// The branch as the model reads it: from its newest summary on (Compaction.cs),
    /// compacted first when it fills too much of the context, and trimmed from the
    /// oldest end as a last resort. Images go as pictures to a model that can see,
    /// and as their names to one that cannot.
    /// </summary>
    private async Task<(JsonArray Messages, bool ImagesDropped, SystemParts System)> BuildHistoryAsync(Conversation conversation, ChatMessage question, GatewayModel? model, string modelName,
        GatewayModel? summarizer, string email, string? toolInstructions, bool canReadFiles, string? lengthNote, string? memory, Func<object, Task> emit, CancellationToken ct)
    {
        var all = await db.ChatMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id).ToDictionaryAsync(m => m.Id, ct);
        all[question.Id] = question;
        var stored = PathTo(all, question.Id);
        // What a summary covers goes as the summary.
        var (summary, from) = LastSummary(stored, stored.Count - 1);
        // Only questions' files go to the model; pictures a tool made are for the person.
        var attachmentIds = stored.Where(m => m.Role == "user").SelectMany(m => ParseIds(m.AttachmentsJson)).ToHashSet();
        var files = await db.ChatAttachments.AsNoTracking().Where(a => attachmentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        // Long files, and a project's files past their room, go as their passages about the question when the embedder holds them.
        var byPassages = await retrieval.PlanAsync(conversation.AssistantId, files.Values, ct);
        var vision = model?.Vision == true;
        var hears = model?.Audio == true;
        var imagesDropped = false;
        var mask = await safeguards.MaskerAsync(email, ct);

        var turns = new List<(JsonObject Turn, long Weight, ChatMessage Source)>();
        foreach (var m in stored.Skip(from))
        {
            switch (m.Role)
            {
                case "user":
                    var text = new StringBuilder(mask(m.Content));
                    var images = new List<ChatAttachment>();
                    var heard = new List<JsonObject>();
                    var mediaWeight = 0L;
                    foreach (var id in ParseIds(m.AttachmentsJson))
                    {
                        if (!files.TryGetValue(id, out var f))
                        {
                            continue;
                        }
                        if (f.Kind is "audio" or "video")
                        {
                            mediaWeight += await MediaAsync(f, text, heard, vision, hears, email, ct);
                            imagesDropped |= f.Kind == "video" && !vision && m.Id == question.Id;
                            continue;
                        }
                        if (f.Kind == "image")
                        {
                            if (vision && f.Data is not null)
                            {
                                images.Add(f);
                            }
                            else
                            {
                                text.Append("\n\n[image attached: ").Append(f.FileName).Append(" (this model cannot see images)]");
                                imagesDropped |= m.Id == question.Id;
                            }
                            continue;
                        }
                        if (f.Kind == "file")
                        {
                            text.Append("\n\n[file attached: ").Append(f.FileName).Append(" (not text; run_python can open it)]");
                            continue;
                        }
                        text.Append("\n\n<attachment name=\"").Append(f.FileName.Replace("\"", "'", StringComparison.Ordinal)).Append("\">\n")
                            .Append(byPassages.Has(f.Id) ? Knowledge.Retrieval.Head(f, canReadFiles) : Inline(f, chat.CurrentValue.InlineAttachmentChars, canReadFiles))
                            .Append("\n</attachment>");
                    }
                    if (images.Count == 0 && heard.Count == 0)
                    {
                        turns.Add((new JsonObject { ["role"] = "user", ["content"] = text.ToString() }, text.Length, m));
                    }
                    else
                    {
                        var parts = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.ToString() });
                        foreach (var img in images)
                        {
                            parts.Add(new JsonObject
                            {
                                ["type"] = "image_url",
                                ["image_url"] = new JsonObject { ["url"] = $"data:{img.ContentType};base64,{Convert.ToBase64String(img.Data!)}" },
                            });
                        }
                        foreach (var part in heard)
                        {
                            parts.Add(part);
                        }
                        turns.Add((new JsonObject { ["role"] = "user", ["content"] = parts }, text.Length + (long)images.Count * ImageWeight + mediaWeight, m));
                    }
                    break;
                case "assistant" when m.ToolCallsJson is not null:
                    var withCalls = new JsonObject { ["role"] = "assistant", ["content"] = m.Content, ["tool_calls"] = JsonNode.Parse(m.ToolCallsJson) };
                    turns.Add((withCalls, withCalls.ToJsonString().Length, m));
                    break;
                case "assistant" when m.Content.Length > 0:
                    turns.Add((new JsonObject { ["role"] = "assistant", ["content"] = m.Content }, m.Content.Length, m));
                    break;
                case "tool":
                    turns.Add((new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId, ["content"] = safeguards.Untrusted(m.ToolName ?? "", m.Content) }, m.Content.Length, m));
                    break;
            }
        }

        var system = $"Today is {DateTimeOffset.UtcNow.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)} (UTC)." + "\n\n" + PreviewNote +
            (lengthNote is null ? "" : "\n\n" + lengthNote);
        var baseLength = system.Length;
        if (!string.IsNullOrWhiteSpace(toolInstructions))
        {
            system += "\n\n" + toolInstructions;
        }
        var toolNotes = system.Length - baseLength;
        // What the person asked to be remembered: after the fixed notes, so a new memory leaves their cache as it was.
        if (memory is not null)
        {
            system += "\n\n" + memory;
        }
        // An assistant's instructions, then the chat's own; then the assistant's files.
        var assistant = conversation.AssistantId is { } aid ? await db.Assistants.AsNoTracking().SingleOrDefaultAsync(x => x.Id == aid, ct) : null;
        if (!string.IsNullOrWhiteSpace(assistant?.Instructions))
        {
            system += $"\n\nThis conversation is with the assistant \"{assistant.Name}\". Its instructions:\n" + assistant.Instructions.Trim();
        }
        if (!string.IsNullOrWhiteSpace(conversation.SystemPrompt))
        {
            system += "\n\nThe person's instructions for this conversation:\n" + conversation.SystemPrompt.Trim();
        }
        var person = system.Length - baseLength - toolNotes;
        var beforeFiles = system.Length;
        if (assistant is not null)
        {
            system += await AssistantFilesAsync(assistant, canReadFiles, byPassages, ct);
        }
        var assistantFiles = system.Length - beforeFiles;
        if (await retrieval.PassagesAsync(byPassages, stored, question, ct) is { } passages)
        {
            Knowledge.Retrieval.AddToQuestion(turns, question.Id, passages);
        }

        // Rough, and on the safe side: ~3.5 characters a token for English and code.
        var context = model?.Context ?? 32768;
        var output = conversation.MaxTokens ?? model?.MaxOutput ?? 8192;
        var budgetChars = (long)Math.Max(4096, context - output - 1024) * 7 / 2 - system.Length;
        // The summary is the small model's work when there is one.
        if (await AutoCompactAsync(turns, stored, from, summary, budgetChars, summarizer ?? model, summarizer?.Name ?? modelName, email, emit, ct) is { } compacted)
        {
            summary = compacted.Summary;
            turns.RemoveRange(0, compacted.Cut);
        }
        var beforeSummary = system.Length;
        if (summary is not null)
        {
            system += "\n\nThe earlier part of this conversation was compacted: its messages are not shown, this summary stands for them.\n<summary>\n" + summary + "\n</summary>";
            budgetChars -= summary.Length + 150;
        }
        var dropped = TrimOldest(turns, budgetChars);
        if (dropped > 0)
        {
            LogTrimmed(logger, conversation.Id, dropped);
        }

        return ([new JsonObject { ["role"] = "system", ["content"] = system }, .. turns.Select(t => t.Turn)], imagesDropped,
            new SystemParts(baseLength, toolNotes, person, system.Length - beforeSummary, assistantFiles));
    }

    /// <summary>
    /// Over the room, the oldest turns go in steps of a quarter of it, not one by one: the start
    /// stays the same for the next turns (the engine's cache holds it) until the next step. Never
    /// starts on a tool answer or a tool request whose answers were cut. How many turns went.
    /// </summary>
    public static int TrimOldest(List<(JsonObject Turn, long Weight, ChatMessage Source)> turns, long budgetChars)
    {
        var dropped = 0;
        var over = turns.Sum(t => t.Weight) - budgetChars;
        var step = Math.Max(1, budgetChars / 4);
        var drop = over > 0 ? (over + step - 1) / step * step : 0;
        while (turns.Count > 1 && drop > 0)
        {
            drop -= turns[0].Weight;
            turns.RemoveAt(0);
            dropped++;
            while (turns.Count > 1 && turns[0].Turn["role"]!.GetValue<string>() != "user")
            {
                drop -= turns[0].Weight;
                turns.RemoveAt(0);
                dropped++;
            }
        }
        return dropped;
    }

    public static IEnumerable<Guid> ParseIds(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<Guid[]>(json) ?? [];

    /// <summary>A title from the first question: its first line, cut at a word, at most 60 characters.</summary>
    public static string TitleFrom(string text)
    {
        var line = text.Trim().Split('\n')[0].Trim();
        if (line.Length <= 60)
        {
            return line.Length == 0 ? "New chat" : line;
        }
        var cut = line[..60];
        var space = cut.LastIndexOf(' ');
        return (space > 30 ? cut[..space] : cut).TrimEnd('.', ',', ';', ':') + "…";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Conversation {Conversation}: {Dropped} oldest messages left out to fit the model's context")]
    private static partial void LogTrimmed(ILogger logger, Guid conversation, int dropped);
}
