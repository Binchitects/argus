using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools;

/// <summary>Configuration section "Laya": where the decision model's server answers (the laya module).</summary>
public sealed class LayaOptions
{
    public string Url { get; set; } = "http://laya:8000";

    /// <summary>Longest one call may take: a forward pass is milliseconds, but calls wait their turn.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}

public sealed class LayaException(string message) : Exception(message);

/// <summary>
/// The laya module's server (deploy/services/laya): typed questions about a state in, a
/// probability for every option out. Whether it is ready is asked at most every half minute.
/// </summary>
public sealed class LayaClient(IHttpClientFactory http, IOptionsMonitor<LayaOptions> options, TimeProvider clock)
{
    public const string Client = "laya";
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    private (string? NotReady, DateTimeOffset At)? _health;

    /// <summary>Why it cannot answer now (still fetching or loading its checkpoints, or down), or null when it can.</summary>
    public async Task<string?> NotReadyAsync(CancellationToken ct)
    {
        if (_health is { } seen && clock.GetUtcNow() - seen.At < Fresh)
        {
            return seen.NotReady;
        }
        string? notReady;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(3));
            var health = await http.CreateClient(Client).GetFromJsonAsync<JsonObject>(options.CurrentValue.Url.TrimEnd('/') + "/health", wait.Token);
            notReady = health?["ready"] is JsonArray { Count: > 0 } ? null
                : "Laya is still fetching or loading its checkpoints (Admin → Models shows the downloads).";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            notReady = "The Laya decision model does not answer (the laya module).";
        }
        _health = (notReady, clock.GetUtcNow());
        return notReady;
    }

    /// <summary>One forward pass: Laya's answers, the checkpoint that read the state, the time it took. Throws <see cref="LayaException"/> with what went wrong.</summary>
    public async Task<JsonObject> DecideAsync(JsonObject request, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(options.CurrentValue.TimeoutSeconds));
        HttpResponseMessage res;
        try
        {
            // With its length: JSON content would go in chunks.
            using var body = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
            res = await http.CreateClient(Client).PostAsync(options.CurrentValue.Url.TrimEnd('/') + "/v1/decide", body, wait.Token);
        }
        catch (HttpRequestException ex)
        {
            _health = null;
            throw new LayaException($"Laya cannot be reached: {ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LayaException($"Laya did not answer within {options.CurrentValue.TimeoutSeconds} seconds: it may be busy. Try again.");
        }
        using (res)
        {
            JsonObject? body;
            try
            {
                body = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
            }
            catch (JsonException)
            {
                body = null;
            }
            if (!res.IsSuccessStatusCode || body is null)
            {
                throw new LayaException($"Laya refused it: {body?["error"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}"}");
            }
            return body;
        }
    }
}

/// <summary>
/// Decide (Laya): typed questions about a text, answered with probabilities in milliseconds by a
/// decision model on the CPU (it never writes text): classify, route, score, yes or no.
/// </summary>
public sealed class LayaTool(LayaClient laya, Modules modules) : IChatTool
{
    public const string Module = "laya";
    public const string Function = "decide";
    public const int MaxStateChars = 20_000;
    public const int MaxQuestions = 20;
    public const int MaxOptions = 20;
    public const int MaxLevels = 10;
    private static readonly string[] Types = ["choice", "score", "noul"];
    private static readonly string[] Checkpoints = ["auto", "english", "multilingual"];

    public string Id => "laya";
    public string Title => "Decide (Laya)";
    public string Description => "Answers typed questions about a text with probabilities, in milliseconds: classify, route, score, yes or no (the Laya decision model).";
    public string Icon => "scale";

    public async Task<string?> UnavailableAsync(CancellationToken ct) =>
        !await modules.HasAsync(Module, ct) ? "The Laya decision model does not run here (the laya module is off: COMPOSE_PROFILES=laya turns it on)."
        : await laya.NotReadyAsync(ct);

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [Definition()],
        "decide answers typed questions about a text with probabilities in milliseconds (Laya, a decision model). Use it to classify, route, triage, " +
        "score or check texts yes or no, and for many texts one call each. Act on an answer only when its probability is high (say 0.85 or more); " +
        "otherwise say it is unsure, or look yourself.",
        async (_, args, token) =>
        {
            var (request, problem) = Request(args);
            if (request is null)
            {
                return new ToolResult(problem!, IsError: true);
            }
            try
            {
                return new ToolResult(Answer(await laya.DecideAsync(request, token)).ToJsonString(Mcp.Plain));
            }
            catch (LayaException ex)
            {
                return new ToolResult(ex.Message, IsError: true);
            }
        }));

    /// <summary>The function, with the rules that make Laya's answers good (docs/chat.md, "Decide (Laya)").</summary>
    public static JsonObject Definition() => Schema.Function(Function,
        "Answers typed questions about a text with calibrated probabilities, in milliseconds, with Laya: a decision model, not a language model (it never writes text). " +
        "For classifying, routing, triage, scoring and yes/no checks: which team a ticket goes to, how urgent it is, whether a message is spam or asks for a refund. " +
        "Question types: choice picks one of a few options (give each a short description of what it covers); score places the text on an ordered scale " +
        "(levels lowest first; the answer is the expected level); noul checks a yes/no statement (the answer is the probability of yes). " +
        "Ask what the text says, not what to do about it. Put numbers and comparisons into words first (\"the order is late\", not two dates). " +
        "Keep option lists short. Keep the state short with what matters first: about 2,000 characters are read, and the answer says when the rest was cut. Ask all the questions about one text in one call. " +
        "The multilingual checkpoint is not calibrated: read its 100% and 0% as likely, not certain.",
        new JsonObject
        {
            ["state"] = Schema.Text("The text to decide about (a ticket, email, message, review, command...), short, with what matters first."),
            ["questions"] = new JsonObject
            {
                ["type"] = "array",
                ["minItems"] = 1,
                ["maxItems"] = MaxQuestions,
                ["description"] = "Every question about this text, answered in one forward pass.",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["id"] = Schema.Text("A short name for the answer, e.g. department."),
                        ["type"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("choice", "score", "noul") },
                        ["question"] = Schema.Text("What to decide, about what the text says, e.g. \"Which department should handle this request?\""),
                        ["options"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "choice: each option with a short description of what it covers, e.g. {\"billing\": \"invoices, payments, refunds\", \"technical\": \"bugs, outages\"}. " +
                                "noul: optionally what yes and no mean, {\"true\": \"...\", \"false\": \"...\"}.",
                        },
                        ["levels"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "string" },
                            ["description"] = "score: the levels, lowest first, e.g. [\"not urgent\", \"soon\", \"blocking\"].",
                        },
                    },
                    ["required"] = new JsonArray("id", "type", "question"),
                },
            },
            ["checkpoint"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("auto", "english", "multilingual"),
                ["description"] = "Which checkpoint reads the text: auto (by its script and language, the default), english (calibrated), or multilingual (100+ languages).",
            },
        }, "state", "questions");

    /// <summary>The model's call as Laya's request ({state, questions: {id: {type, instructions, criteria}}, checkpoint}), or what is wrong with it.</summary>
    public static (JsonObject? Request, string? Problem) Request(JsonObject args)
    {
        var state = (Schema.Str(args, "state") ?? "").Trim();
        if (state.Length == 0)
        {
            return (null, "Give the text to decide about in 'state'.");
        }
        if (state.Length > MaxStateChars)
        {
            return (null, $"The state is {state.Length:N0} characters: at most {MaxStateChars:N0}, and only about 2,000 are read. Shorten it, with what matters first.");
        }
        if (args["questions"] is not JsonArray { Count: > 0 } asked)
        {
            return (null, "Give at least one question in 'questions'.");
        }
        if (asked.Count > MaxQuestions)
        {
            return (null, $"At most {MaxQuestions} questions in one call.");
        }
        var questions = new JsonObject();
        foreach (var item in asked)
        {
            if (item is not JsonObject q)
            {
                return (null, "Each question is an object with id, type and question.");
            }
            var id = (Schema.Str(q, "id") ?? "").Trim();
            if (id.Length is 0 or > 64)
            {
                return (null, "Each question needs an id of 1 to 64 characters.");
            }
            if (questions.ContainsKey(id))
            {
                return (null, $"Two questions are named {id}: give each its own id.");
            }
            var type = Schema.Str(q, "type");
            if (type is null || !Types.Contains(type))
            {
                return (null, $"Question {id}: type is choice, score or noul.");
            }
            var text = (Schema.Str(q, "question") ?? "").Trim();
            if (text.Length is 0 or > 1000)
            {
                return (null, $"Question {id}: say what to decide in 'question' (at most 1,000 characters).");
            }
            var asLaya = new JsonObject { ["type"] = type, ["instructions"] = text };
            switch (type)
            {
                case "choice":
                    var options = Options(q["options"]);
                    if (options is null || options.Count is < 2 or > MaxOptions)
                    {
                        return (null, $"Question {id}: a choice needs 2 to {MaxOptions} options in 'options', each with a short description: {{\"billing\": \"invoices, payments, refunds\", ...}}.");
                    }
                    asLaya["criteria"] = options;
                    break;
                case "score":
                    var levels = (q["levels"] as JsonArray)?.Select(l => l is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").ToList();
                    if (levels is null || levels.Count is < 2 or > MaxLevels || levels.Any(l => l.Length is 0 or > 300))
                    {
                        return (null, $"Question {id}: a score needs 2 to {MaxLevels} levels in 'levels', lowest first: [\"not urgent\", \"soon\", \"blocking\"].");
                    }
                    asLaya["criteria"] = new JsonArray([.. levels.Select(l => (JsonNode)l)]);
                    break;
                default:
                    if (q["options"] is JsonObject meaning)
                    {
                        var yesNo = Options(meaning);
                        if (yesNo is null || yesNo.Any(o => o.Key is not ("true" or "false")))
                        {
                            return (null, $"Question {id}: a noul's options, when given, are {{\"true\": \"...\", \"false\": \"...\"}}.");
                        }
                        asLaya["criteria"] = yesNo;
                    }
                    break;
            }
            questions[id] = asLaya;
        }
        var checkpoint = Schema.Str(args, "checkpoint") is { Length: > 0 } c ? c : "auto";
        if (!Checkpoints.Contains(checkpoint))
        {
            return (null, "checkpoint is auto, english or multilingual.");
        }
        return (new JsonObject { ["state"] = state, ["questions"] = questions, ["checkpoint"] = checkpoint }, null);
    }

    /// <summary>{"name": "description"}, or a list of names (each its own description); null when empty, unnamed, or too long.</summary>
    private static JsonObject? Options(JsonNode? node)
    {
        var pairs = node switch
        {
            JsonObject o => o.Select(p => (p.Key.Trim(), p.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "")).ToList(),
            JsonArray a => a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? (s.Trim(), s.Trim()) : ("", "")).ToList(),
            _ => null,
        };
        if (pairs is null || pairs.Any(p => p.Item1.Length is 0 or > 300 || p.Item2.Length is 0 or > 300) || pairs.DistinctBy(p => p.Item1).Count() != pairs.Count)
        {
            return null;
        }
        var options = new JsonObject();
        foreach (var (name, description) in pairs)
        {
            options[name] = description;
        }
        return options;
    }

    /// <summary>What the model reads: each answer with its probabilities (to three places), the checkpoint, and the time.</summary>
    public static JsonObject Answer(JsonObject laya)
    {
        var answers = new JsonObject();
        foreach (var (id, answer) in laya["answers"] as JsonObject ?? new JsonObject())
        {
            if (Round(answer) is JsonObject a)
            {
                // The act-or-escalate head is Laya's own; the answer is in the rest.
                a.Remove("action");
                answers[id] = a;
            }
        }
        var calibrated = laya["calibrated"] is JsonValue c && c.TryGetValue<bool>(out var yes) && yes;
        var result = new JsonObject
        {
            ["answers"] = answers,
            ["checkpoint"] = laya["checkpoint"]?.DeepClone(),
            ["calibrated"] = calibrated,
            ["ms"] = laya["ms"]?.DeepClone(),
        };
        var notes = new List<string>();
        if (Cut(laya["usage"]) is { } cut)
        {
            // Laya reads a few hundred tokens of the state and drops the rest; it counts them, so it is said, not guessed.
            result["truncated"] = new JsonObject { ["tokens"] = cut.Tokens, ["read"] = cut.Read };
            notes.Add($"Laya read only the first {cut.Read:N0} of the state's {cut.Tokens:N0} tokens: the answers say nothing of the rest. Shorten it, with what matters first, or split it.");
        }
        if (!calibrated)
        {
            notes.Add("This checkpoint is not calibrated: read its probabilities as a ranking, not as certainty.");
        }
        if (notes.Count > 0)
        {
            result["note"] = string.Join(" ", notes);
        }
        return result;
    }

    /// <summary>The state's tokens and how many Laya read, when its usage says it cut the state short.</summary>
    private static (int Tokens, int Read)? Cut(JsonNode? usage) =>
        usage is JsonObject u && u["truncated"] is JsonValue t && t.TryGetValue<bool>(out var truncated) && truncated
        && u["state_tokens"] is JsonValue s && s.TryGetValue<int>(out var tokens)
        && u["state_tokens_dropped"] is JsonValue d && d.TryGetValue<int>(out var dropped) && dropped > 0 && dropped <= tokens
            ? (tokens, tokens - dropped) : null;

    private static JsonNode? Round(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject([.. o.Select(p => KeyValuePair.Create(p.Key, Round(p.Value)))]),
        JsonArray a => new JsonArray([.. a.Select(Round)]),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) => JsonValue.Create(Math.Round(d, 3)),
        _ => node?.DeepClone(),
    };

    /// <summary>The client, the tool and their settings (one line in IdentityWiring).</summary>
    public static void AddLaya(IServiceCollection services, IConfiguration config)
    {
        services.Configure<LayaOptions>(config.GetSection("Laya"));
        services.AddHttpClient(LayaClient.Client);
        services.AddSingleton<LayaClient>();
        services.AddSingleton<LayaTool>();
    }
}
