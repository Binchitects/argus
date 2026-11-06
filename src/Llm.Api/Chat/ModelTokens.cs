using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Models;
using Llm.Core.Chat;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>
/// Counts texts in a model's own tokens, where its engine has a tokenizer the app can ask: llama.cpp's router (/tokenize,
/// and /apply-template for a whole request), vLLM's and SGLang's servers (/tokenize). Only for a model that is loaded: asking
/// the router for another would load it. What it counted is kept (by the model and the text's hash), so a request that
/// starts like the last (the system prompt, the tools, the earlier messages) costs only what is new. A model on a server
/// the app does not run, or an engine that does not answer, is estimated from characters, and the window says why.
/// </summary>
public sealed class ModelTokens(EngineClient engines, EngineState state, IHttpClientFactory http, IOptions<EngineOptions> options)
{
    /// <summary>The most counts kept; past it the cache starts again.</summary>
    private const int MostKept = 50_000;

    /// <summary>How many texts are counted at once.</summary>
    private static readonly SemaphoreSlim Lanes = new(8, 8);

    private readonly ConcurrentDictionary<(string Model, string Hash), int> _kept = new();

    /// <summary>A model's tokenizer: what counts its texts, a whole request's tokens (null: it cannot), and its engine's name.</summary>
    public sealed record Tokenizer(TokenCounter Count, Func<JsonObject, CancellationToken, Task<long?>> Total, string By);

    /// <summary>The tokenizer of <paramref name="model"/>, or why there is none (said with the estimate).</summary>
    public (Tokenizer? Tokenizer, string Why) For(string model)
    {
        if (!options.Value.Enabled)
        {
            return (null, "no engine of this Arena's own");
        }
        if (state.Now.Models.FirstOrDefault(m => m.Name == model)?.Status != "loaded")
        {
            return (null, "the model is not loaded on an engine of this Arena");
        }
        switch (engines.BackendOf(model))
        {
            case LlamaCppBackend:
                var url = options.Value.Url.TrimEnd('/');
                return (new Tokenizer(
                    (texts, ct) => CountAsync(model, texts, LlamaCppBackend.Client, url, t => new JsonObject { ["model"] = model, ["content"] = t, ["add_special"] = false }, ct),
                    (request, ct) => LlamaTotalAsync(model, url, request, ct), "llama.cpp"), "");
            case VllmBackend v when v.Desired().FirstOrDefault(e => e.Model == model) is { } entry:
                var at = $"http://{v.Host}:{entry.Port}";
                return (new Tokenizer(
                    (texts, ct) => CountAsync(model, texts, VllmBackend.Client, at, t => new JsonObject { ["model"] = entry.Name, ["prompt"] = t, ["add_special_tokens"] = false }, ct),
                    (request, ct) => VllmTotalAsync(entry.Name, at, request, ct), v.Label), "");
            default:
                return (null, "its engine has no tokenizer the app can ask");
        }
    }

    /// <summary>Each text's tokens; null when the engine did not count one (it is not reachable, or refused).</summary>
    private async Task<IReadOnlyList<int>?> CountAsync(string model, IReadOnlyList<string> texts, string client, string url, Func<string, JsonObject> body, CancellationToken ct)
    {
        if (_kept.Count > MostKept)
        {
            _kept.Clear();
        }
        var counts = new int[texts.Count];
        var failed = false;
        await Task.WhenAll(texts.Select(async (text, i) =>
        {
            if (text.Length == 0)
            {
                return;
            }
            var key = (model, Hash(text));
            if (_kept.TryGetValue(key, out var known))
            {
                counts[i] = known;
                return;
            }
            await Lanes.WaitAsync(ct);
            try
            {
                if (await PostAsync(client, url + "/tokenize", body(text), ct) is { } said && Tokens(said) is { } n)
                {
                    counts[i] = _kept[key] = n;
                }
                else
                {
                    failed = true;
                }
            }
            finally
            {
                Lanes.Release();
            }
        }));
        return failed ? null : counts;
    }

    /// <summary>A whole request on llama.cpp: its chat template applied (the tools, the thinking switches), then counted with its special tokens.</summary>
    private async Task<long?> LlamaTotalAsync(string model, string url, JsonObject request, CancellationToken ct)
    {
        var asked = new JsonObject { ["model"] = model, ["messages"] = request["messages"]?.DeepClone() };
        foreach (var key in (string[])["tools", "chat_template_kwargs"])
        {
            if (request[key] is { } value)
            {
                asked[key] = value.DeepClone();
            }
        }
        if (await PostAsync(LlamaCppBackend.Client, url + "/apply-template", asked, ct) is not { } applied || applied["prompt"] is not JsonValue p || !p.TryGetValue<string>(out var prompt))
        {
            return null;
        }
        return await PostAsync(LlamaCppBackend.Client, url + "/tokenize", new JsonObject { ["model"] = model, ["content"] = prompt, ["add_special"] = true }, ct) is { } said ? Tokens(said) : null;
    }

    /// <summary>A whole request on vLLM: its /tokenize takes the messages and tools as a chat request (SGLang's does not: null).</summary>
    private async Task<long?> VllmTotalAsync(string served, string url, JsonObject request, CancellationToken ct)
    {
        var asked = new JsonObject { ["model"] = served, ["messages"] = request["messages"]?.DeepClone(), ["add_generation_prompt"] = true };
        foreach (var key in (string[])["tools", "chat_template_kwargs"])
        {
            if (request[key] is { } value)
            {
                asked[key] = value.DeepClone();
            }
        }
        return await PostAsync(VllmBackend.Client, url + "/tokenize", asked, ct) is { } said ? Tokens(said) : null;
    }

    /// <summary>The tokens an engine's /tokenize counted: vLLM's and SGLang's count, else its tokens.</summary>
    private static int? Tokens(JsonNode said) =>
        said["count"] is JsonValue c && c.TryGetValue<int>(out var count) ? count : said["tokens"] is JsonArray tokens ? tokens.Count : null;

    private async Task<JsonNode?> PostAsync(string client, string url, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(url)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(options.Value.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.ApiKey);
        }
        try
        {
            using var res = await http.CreateClient(client).SendAsync(req, ct);
            return res.IsSuccessStatusCode ? JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 16);

    /// <summary>What the tokenizer counted of a request: each piece's tokens (null: estimated, and <see cref="By"/> says why), and the whole request's when asked.</summary>
    public sealed record Counted(IReadOnlyList<int>? Counts, long? Total, string By);

    /// <summary>
    /// A request's pieces counted with the model's tokenizer when there is one, within <paramref name="patience"/>; with
    /// <paramref name="whole"/>, the whole request too (its chat format included), where the engine can.
    /// </summary>
    public async Task<Counted> CountAsync(string model, JsonObject request, IReadOnlyList<WindowPiece> pieces, bool whole, TimeSpan patience, CancellationToken ct)
    {
        var (tokenizer, why) = For(model);
        if (tokenizer is null)
        {
            return new Counted(null, null, why);
        }
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(patience);
        try
        {
            var counted = await tokenizer.Count([.. pieces.Select(p => p.Weight > 0 ? "" : p.Text)], quick.Token);
            if (counted is null)
            {
                return new Counted(null, null, "its engine did not count");
            }
            return new Counted(counted, whole ? await tokenizer.Total(request, quick.Token) : null, tokenizer.By);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Counted(null, null, "its engine did not count in time");
        }
    }

    /// <summary>
    /// The window of a request in pieces, as the views show it: counted (or estimated at the characters a token held in it),
    /// adding up to <paramref name="total"/> (the engine's count of the request, when it said; else the tokenizer's).
    /// </summary>
    public static JsonObject Window(IReadOnlyList<WindowPiece> pieces, Counted counted, long? total, long limit, long reserve, JsonArray? freeable,
        Func<long[], JsonArray>? freeableOf = null)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(counted);
        var all = total ?? counted.Total;
        var chars = pieces.Sum(p => p.Chars);
        var perToken = all is > 0 && chars > 0 ? (double)chars / all.Value : PromptBudget.DefaultCharsPerToken;
        var (tokens, format) = ContextWindow.Count(pieces, counted.Counts, all, perToken);
        return ContextWindow.Shape(pieces, tokens, format, limit, reserve, counted.Counts is not null, counted.By, freeable ?? freeableOf?.Invoke(tokens));
    }
}
