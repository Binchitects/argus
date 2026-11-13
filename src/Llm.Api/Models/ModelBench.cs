using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Endpoints;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Llm.Api.Models;

/// <summary>One test of what a model can do: what it is asked (and the tools it is offered), and the rule its answer is checked by.</summary>
public sealed record BenchTest(string Id, string Title, string Skill, Func<int, JsonArray> Messages, Func<string, List<(string Name, string Args)>, (bool Passed, string? Why)> Check,
    JsonArray? Tools = null, int MaxTokens = 512);

/// <summary>
/// The model test bench: a model's capability (a set of tests, each checked by a rule, no judge model: arithmetic, following
/// an instruction, logic, JSON, a tool call, a fact found in a long text, Persian, code) and its capacity (answers at once,
/// 1, 2, 4… raised until more do not make more, or answers fail or wait too long: the tokens a second all together and each
/// answer's, and the wait for the first token). Through the app's own gateway as the admin who runs it, as any request.
/// </summary>
public sealed partial class ModelBench(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ModelBench> logger)
{
    /// <summary>The most answers at once a capacity run goes to.</summary>
    public const int MostAtOnce = 64;

    /// <summary>A level whose first tokens wait longer than this at the 95th percentile is past what people would take.</summary>
    public static readonly TimeSpan SlowFirstToken = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    public Task Finished(Guid run) => _running.GetValueOrDefault(run) ?? Task.CompletedTask;

    /// <summary>The capability tests, the same for every model; the long text's size follows the model's context.</summary>
    public static readonly IReadOnlyList<BenchTest> Tests =
    [
        new("arithmetic", "Arithmetic", "reasoning", _ => Ask("What is 17 × 23? Answer with the number only."),
            (a, _) => Has(a, "391")),
        new("instructions", "Follows an instruction", "instructions", _ => Ask("List three primary colours of paint, as exactly three lines each starting with \"- \", and nothing else."),
            (a, _) =>
            {
                var lines = a.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                return lines.Length == 3 && lines.All(l => l.StartsWith("- ", StringComparison.Ordinal)) ? (true, null) : (false, $"{lines.Length} lines, not three \"- \" lines");
            }),
        new("logic", "Logic", "reasoning", _ => Ask("Ali is taller than Sara. Sara is taller than Reza. Who is the shortest? Answer with one name."),
            (a, _) => Has(a, "Reza")),
        new("json", "JSON as asked", "structured output", _ => Ask("Give a JSON object with the keys \"name\" (a string) and \"year\" (a number) for the first person to walk on the Moon. Answer with the JSON only."),
            (a, _) =>
            {
                try
                {
                    var o = JsonNode.Parse(Unfenced(a)) as JsonObject;
                    return o?["name"]?.ToString().Contains("Armstrong", StringComparison.OrdinalIgnoreCase) == true && o["year"]?.ToString() == "1969"
                        ? (true, null) : (false, "not Armstrong and 1969");
                }
                catch (JsonException)
                {
                    return (false, "not JSON");
                }
            }),
        new("tool", "Calls a tool", "tools", _ => Ask("What is the weather in Paris now? Use the tool."),
            (_, calls) => calls.Any(c => c.Name == "get_weather" && c.Args.Contains("Paris", StringComparison.OrdinalIgnoreCase)) ? (true, null)
                : (false, calls.Count == 0 ? "no tool call" : $"called {string.Join(", ", calls.Select(c => c.Name))}"),
            new JsonArray(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "get_weather", ["description"] = "The weather in a city now.",
                    ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("city") },
                },
            })),
        new("long", "Finds a fact in a long text", "long context", context => Ask(Haystack(context) + "\n\nWhat is the secret code said above? Answer with the number only."),
            (a, _) => Has(a, "4721"), MaxTokens: 64),
        new("persian", "Answers in Persian", "languages", _ => Ask("پایتخت ایران کجاست؟ فقط با یک کلمه به فارسی پاسخ بده."),
            (a, _) => Has(a, "تهران")),
        new("code", "Writes code", "code", _ => Ask("Write a Python function is_prime(n) that says whether n is a prime number. Answer with the code only."),
            (a, _) => a.Contains("def is_prime", StringComparison.Ordinal) && a.Contains("return", StringComparison.Ordinal) ? (true, null) : (false, "no def is_prime with a return")),
    ];

    private static JsonArray Ask(string question) => [new JsonObject { ["role"] = "user", ["content"] = question }];

    private static (bool, string?) Has(string answer, string expected) =>
        answer.Contains(expected, StringComparison.OrdinalIgnoreCase) ? (true, null) : (false, $"no \"{expected}\" in the answer");

    private static string Unfenced(string text) => Fence().Match(text) is { Success: true } m ? m.Groups[1].Value : text.Trim();

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```")]
    private static partial Regex Fence();

    /// <summary>A long text, half the model's context or 24,000 tokens at most, with the code in its middle (about 4 characters a token).</summary>
    public static string Haystack(int context)
    {
        var chars = Math.Clamp(context / 2, 1_000, 24_000) * 4;
        var lines = new StringBuilder();
        var n = 0;
        var said = false;
        while (lines.Length < chars)
        {
            n++;
            lines.Append(CultureInfo.InvariantCulture, $"Line {n}: the ship's log says the weather was calm and the crew checked the ropes and the sails at dawn.\n");
            if (!said && lines.Length >= chars / 2)
            {
                lines.Append("The secret code is 4721.\n");
                said = true;
            }
        }
        return lines.ToString();
    }

    /// <summary>Starts a run of the bench on a model; its id. It goes on in the background: its row says how it goes.</summary>
    public async Task<Guid> StartAsync(string model, string kind, AppUser by, int most, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = new ModelBenchRun { Model = model, Kind = kind, ById = by.Id, StartedAt = clock.GetUtcNow() };
        db.ModelBenchRuns.Add(run);
        await db.SaveChangesAsync(ct);
        var work = Task.Run(async () =>
        {
            try
            {
                await (kind == ModelBenchRun.Capacity ? CapacityAsync(run.Id, model, by.Email!, Math.Clamp(most, 1, MostAtOnce)) : CapabilityAsync(run.Id, model, by.Email!));
                await SaveAsync(run.Id, null, ModelBenchRun.Done, null);
            }
            catch (Exception e) when (e is ChatGatewayException or HttpRequestException or InvalidOperationException or DbUpdateException or TaskCanceledException)
            {
                LogFailed(logger, model, e);
                await SaveAsync(run.Id, null, ModelBenchRun.Failed, e.Message.Length > 2000 ? e.Message[..2000] : e.Message);
            }
        }, CancellationToken.None);
        _running[run.Id] = work;
        _ = work.ContinueWith(_ => _running.TryRemove(run.Id, out var _), TaskScheduler.Default);
        return run.Id;
    }

    private async Task SaveAsync(Guid id, JsonObject? results, string? status, string? error)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var run = await db.ModelBenchRuns.SingleAsync(r => r.Id == id);
        if (results is not null)
        {
            run.Results = results.ToJsonString();
        }
        if (status is not null)
        {
            run.Status = status;
            run.Error = error;
            run.FinishedAt = status == ModelBenchRun.Running ? null : clock.GetUtcNow();
        }
        await db.SaveChangesAsync();
    }

    /// <summary>One request through the gateway: its answer, tool calls, tokens, and when its first token came.</summary>
    private sealed record Answer(string Text, List<(string Name, string Args)> Calls, int Completion, TimeSpan FirstToken, TimeSpan Took, string? Error);

    private static async Task<Answer> AskAsync(GatewayChat gateway, JsonObject request, string email)
    {
        var clockwatch = Stopwatch.StartNew();
        TimeSpan? first = null;
        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string? Name, StringBuilder Args)>();
        var completion = 0;
        try
        {
            await foreach (var e in gateway.StreamAsync(request, email, CancellationToken.None))
            {
                switch (e)
                {
                    case ContentDelta c:
                        first ??= clockwatch.Elapsed;
                        text.Append(c.Text);
                        break;
                    case ReasoningDelta:
                        first ??= clockwatch.Elapsed;
                        break;
                    case ToolCallDelta t:
                        first ??= clockwatch.Elapsed;
                        var slot = calls.TryGetValue(t.Index, out var at) ? at : (null, new StringBuilder());
                        calls[t.Index] = (t.Name ?? slot.Name, slot.Item2.Append(t.Arguments));
                        break;
                    case UsageReport u:
                        completion = u.Completion;
                        break;
                }
            }
        }
        catch (ChatGatewayException e)
        {
            return new Answer(text.ToString(), [], completion, first ?? clockwatch.Elapsed, clockwatch.Elapsed, e.Message);
        }
        return new Answer(text.ToString(), [.. calls.Values.Select(c => (c.Name ?? "", c.Item2.ToString()))], completion, first ?? clockwatch.Elapsed, clockwatch.Elapsed, null);
    }

    private async Task<(GatewayChat Gateway, LocalModel? Model, AsyncServiceScope Scope)> ScopeAsync(string model)
    {
        var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (scope.ServiceProvider.GetRequiredService<GatewayChat>(), await db.LocalModels.AsNoTracking().SingleOrDefaultAsync(m => m.Name == model), scope);
    }

    private static JsonObject Request(string model, LocalModel? local, JsonArray messages, int maxTokens, JsonArray? tools = null)
    {
        var request = new JsonObject
        {
            ["model"] = model, ["messages"] = messages, ["stream"] = true, ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["max_tokens"] = maxTokens, ["temperature"] = 0, ["metadata"] = new JsonObject { ["tags"] = new JsonArray("model-bench") },
        };
        // A model that thinks answers the tests without: they are short, and its thinking would take the answer's room.
        if (local?.Thinking == true && ThinkingPresets.TemplateKwargs("off") is { } off)
        {
            request["chat_template_kwargs"] = off;
        }
        if (tools is not null)
        {
            request["tools"] = tools;
        }
        return request;
    }

    private async Task CapabilityAsync(Guid id, string model, string email)
    {
        var (gateway, local, scope) = await ScopeAsync(model);
        await using var _ = scope;
        var context = local?.Context ?? 8192;
        var results = new JsonArray();
        foreach (var t in Tests)
        {
            var a = await AskAsync(gateway, Request(model, local, t.Messages(context), t.MaxTokens, t.Tools?.DeepClone() as JsonArray), email);
            var (passed, why) = a.Error is null ? t.Check(a.Text, a.Calls) : (false, a.Error);
            results.Add(new JsonObject
            {
                ["id"] = t.Id, ["title"] = t.Title, ["skill"] = t.Skill, ["passed"] = passed, ["why"] = why, ["ms"] = (int)a.Took.TotalMilliseconds,
                ["tokens"] = a.Completion, ["answer"] = a.Calls.Count > 0 ? string.Join(", ", a.Calls.Select(c => $"{c.Name}({c.Args})")) : a.Text.Length > 400 ? a.Text[..400] + "…" : a.Text,
            });
            var passedCount = results.Count(r => r!["passed"]!.GetValue<bool>());
            await SaveAsync(id, new JsonObject { ["tests"] = results.DeepClone(), ["passed"] = passedCount, ["total"] = Tests.Count }, null, null);
        }
    }

    private async Task CapacityAsync(Guid id, string model, string email, int most)
    {
        var (gateway, local, scope) = await ScopeAsync(model);
        await using var _ = scope;
        var levels = new JsonArray();
        double best = 0;
        var bestAt = 0;
        var flat = 0;
        for (var n = 1; n <= most; n *= 2)
        {
            var wall = Stopwatch.StartNew();
            var answers = await Task.WhenAll(Enumerable.Range(0, n).Select(i =>
            {
                // Stories of their own: not one answer read n times from the engine's cache.
                var request = Request(model, local, Ask($"Write a story of about 200 words about a lighthouse keeper (story {i + 1})."), 256);
                request.Remove("temperature");
                return AskAsync(gateway, request, email);
            }));
            var took = wall.Elapsed.TotalSeconds;
            var ok = answers.Where(a => a.Error is null).ToList();
            var tokens = ok.Sum(a => a.Completion);
            var all = took > 0 ? tokens / took : 0;
            var each = ok.Where(a => a.Took > a.FirstToken && a.Completion > 1).Select(a => (a.Completion - 1) / (a.Took - a.FirstToken).TotalSeconds).DefaultIfEmpty(0).Average();
            var firsts = ok.Select(a => a.FirstToken.TotalSeconds).Order().ToList();
            double Pct(double p) => firsts.Count == 0 ? 0 : firsts[Math.Min(firsts.Count - 1, (int)Math.Ceiling(p * firsts.Count) - 1)];
            levels.Add(new JsonObject
            {
                ["atOnce"] = n, ["tokensPerSecond"] = Math.Round(all, 1), ["perAnswer"] = Math.Round(each, 1), ["firstTokenP50"] = Math.Round(Pct(0.5), 2),
                ["firstTokenP95"] = Math.Round(Pct(0.95), 2), ["failed"] = n - ok.Count, ["seconds"] = Math.Round(took, 1),
            });
            if (all > best * 1.1)
            {
                (best, bestAt, flat) = (all, n, 0);
            }
            else
            {
                flat++;
            }
            await SaveAsync(id, new JsonObject { ["levels"] = levels.DeepClone(), ["best"] = new JsonObject { ["atOnce"] = bestAt, ["tokensPerSecond"] = Math.Round(best, 1) } }, null, null);
            // More do not make more, answers fail, or people would wait too long for the first token: that is its capacity.
            if (flat >= 2 || ok.Count < n || Pct(0.95) > SlowFirstToken.TotalSeconds)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The test bench of {Model} failed")]
    private static partial void LogFailed(ILogger logger, string model, Exception ex);
}

public sealed record BenchRequest(string? Kind = null, int? Most = null);

/// <summary>Admin → Models → Test: a run started, the runs of a model, one run.</summary>
public static class ModelBenchEndpoints
{
    public static void MapModelBench(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/admin/models").RequirePermission(Core.Access.Permissions.ModelsManage);
        g.MapPost("/{name}/bench", StartAsync);
        g.MapGet("/{name}/bench", ListAsync);
        g.MapGet("/bench/{id:guid}", GetAsync);
    }

    private static object Shape(ModelBenchRun r) => new
    {
        r.Id, r.Model, r.Kind, r.Status, r.Error, r.StartedAt, r.FinishedAt, results = JsonNode.Parse(r.Results),
    };

    private static async Task<IResult> StartAsync(string name, BenchRequest body, System.Security.Claims.ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, ModelBench bench,
        CancellationToken ct)
    {
        var kind = body.Kind ?? ModelBenchRun.Capability;
        if (kind is not (ModelBenchRun.Capability or ModelBenchRun.Capacity))
        {
            return AuthEndpoints.Problem(400, "kind", "A test is of its capability or its capacity.");
        }
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            return Results.NotFound();
        }
        if (await db.ModelBenchRuns.AnyAsync(r => r.Model == name && r.Status == ModelBenchRun.Running, ct))
        {
            return AuthEndpoints.Problem(409, "running", "A test of this model is running: wait for it to end.");
        }
        var me = (await users.GetUserAsync(p))!;
        var id = await bench.StartAsync(name, kind, me, body.Most ?? 32, ct);
        return Results.Accepted($"/api/admin/models/bench/{id}", new { id });
    }

    private static async Task<IResult> ListAsync(string name, AppDbContext db, CancellationToken ct) =>
        Results.Ok((await db.ModelBenchRuns.AsNoTracking().Where(r => r.Model == name).OrderByDescending(r => r.StartedAt).Take(20).ToListAsync(ct)).Select(Shape));

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.ModelBenchRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct) is { } run ? Results.Ok(Shape(run)) : Results.NotFound();
}
