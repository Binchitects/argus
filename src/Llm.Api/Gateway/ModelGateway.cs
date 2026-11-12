using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Data;
using Llm.Core.Gateway;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Gateway;

/// <summary>Who a call is for, as the ledger books it: the person, where in the app it came from, its tag, chat and project.</summary>
/// <param name="Booked">Told the call's line once it is written for the ledger (an API key's tokens then count to its minute).</param>
public sealed record GatewayCaller(string Person, string Surface = "chat", string? Tag = null, Guid? Conversation = null, Guid? Project = null, string? KeyHash = null,
    string? KeyAlias = null, Action<GatewayRequest>? Booked = null);

/// <summary>
/// The app's own model gateway: a chat completion sent straight to where its model is answered (llama.cpp's router, a copy
/// of a vLLM or SGLang model, another GPU server), shaped for it (each engine's own fields only), balanced over a pool's
/// copies by what each is busy with, its fallback asked when the first cannot take it, and booked in the app's ledger
/// (gateway.requests) as it ends: tokens, cost at the model's prices, the first token's time, how it ended. The body passes
/// through as it comes; the usage is read beside it.
/// </summary>
public sealed partial class ModelGateway(IServiceScopeFactory scopes, IHttpClientFactory clients, IOptions<EngineOptions> engine,
    TimeProvider clock, ILogger<ModelGateway> logger, GatewayMetrics metrics, IOptionsMonitor<GatewayTimeoutOptions> timeouts) : IDisposable
{
    /// <summary>The client that checks certificates; and the one for an https server whose certificate is not checked (VerifyTls off).</summary>
    public const string Upstream = "gateway-upstream";
    public const string UpstreamUnchecked = "gateway-upstream-unchecked";

    /// <summary>How long a route table is used before it is built again (a change rebuilds it at once: <see cref="Invalidate"/>).</summary>
    public static readonly TimeSpan RoutesFor = TimeSpan.FromSeconds(30);

    private (List<Deployment> Routes, DateTimeOffset At)? _table;
    private readonly SemaphoreSlim _building = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _busy = new(StringComparer.Ordinal);
    private readonly Channel<GatewayRequest> _ledger = Channel.CreateBounded<GatewayRequest>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private Task? _writer;
    private volatile bool _writing; // a batch read from the channel and not saved yet

    /// <summary>A line for the ledger, counted for the metrics as it goes.</summary>
    private void Write(GatewayRequest row)
    {
        metrics.Booked(row);
        _ledger.Writer.TryWrite(row);
    }

    /// <summary>The gateway's metrics as Prometheus reads them.</summary>
    public string Metrics() => metrics.Render(_busy.GroupBy(b => b.Key.Split('|')[0]).ToDictionary(g => g.Key, g => g.Sum(x => x.Value)), _ledger.Reader.Count);

    /// <summary>
    /// The model gateway's own health (Admin → Overview, Mode own): each server its routes send to asked as each kind is
    /// (llama.cpp, vLLM and SGLang: /health; another server: /v1/models): ok when every chat model's server answers.
    /// </summary>
    public async Task<(bool Ok, string Detail)> HealthAsync(CancellationToken ct)
    {
        List<Deployment> routes;
        try
        {
            routes = await TableAsync(false, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return (false, "its routes cannot be read: " + e.Message);
        }
        var servers = routes.Where(r => r.CopyOf is null || r.Mode == "chat").GroupBy(r => r.BaseUrl).Select(g => g.First()).ToList();
        var down = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(servers.Select(async d =>
        {
            var root = d.BaseUrl.EndsWith("/v1", StringComparison.Ordinal) ? d.BaseUrl[..^3] : d.BaseUrl;
            var url = d.Kind is Deployment.LlamaCpp || Llm.Core.Models.EngineKinds.Priority(d.Kind) ? root + "/health" : d.BaseUrl.TrimEnd('/') + "/models";
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(5));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if ((d.EngineKey ? engine.Value.ApiKey : d.Key) is { Length: > 0 } bearer)
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }
                using var res = await clients.CreateClient(d.VerifyTls == false ? UpstreamUnchecked : Upstream).SendAsync(req, limit.Token);
                if ((int)res.StatusCode >= 500 && d.Mode == "chat")
                {
                    lock (down)
                    {
                        down.Add(d.Name);
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (d.Mode == "chat")
                {
                    lock (down)
                    {
                        down.Add(d.Name);
                    }
                }
            }
        }));
        return down.Count == 0
            ? (true, $"in the app: {routes.Count(r => r.CopyOf is null)} models on {servers.Count} servers · {sw.ElapsedMilliseconds} ms")
            : (false, $"in the app: {string.Join(", ", down.Distinct().Order())} not answering");
    }

    public void Dispose()
    {
        _ledger.Writer.TryComplete();
        _building.Dispose();
    }

    /// <summary>A request refused before anything was sent, in the ledger with why (it counts as no request and no failure).</summary>
    public void Refused(GatewayCaller caller, string? model, int status, string reason, string message)
    {
        EnsureWriter();
        var row = Row(caller, model ?? "", null, null, 0, clock.GetUtcNow());
        (row.Status, row.HttpStatus, row.Refusal, row.Error, row.EndedAt) = (GatewayRequest.Refused, (short)status, reason.Length <= 16 ? reason : reason[..16], Short(message), row.StartedAt);
        Write(row);
    }

    /// <summary>The routes are built again at the next call (a model loaded, a server or price changed).</summary>
    public void Invalidate() => _table = null;

    /// <summary>Every route now (the models API lists them).</summary>
    public Task<List<Deployment>> AllRoutesAsync(CancellationToken ct) => TableAsync(false, ct);

    /// <summary>
    /// Embeddings: sent to the embeddings server, booked by the prompt tokens it read at the input price. Errors come back
    /// as the server sent them.
    /// </summary>
    public async Task<HttpResponseMessage> EmbedAsync(JsonObject body, Deployment d, GatewayCaller caller, CancellationToken ct)
    {
        EnsureWriter();
        var started = clock.GetUtcNow();
        var row = Row(caller, d.Name, d, null, 1, started);
        row.Call = "embedding";
        var shaped = (JsonObject)body.DeepClone();
        shaped["model"] = d.Model;
        using var req = new HttpRequestMessage(HttpMethod.Post, d.BaseUrl.TrimEnd('/') + "/embeddings") { Content = new StringContent(shaped.ToJsonString(), Encoding.UTF8, "application/json") };
        if (d.Key is { Length: > 0 } bearer)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        HttpResponseMessage res;
        try
        {
            res = await clients.CreateClient(Upstream).SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            (row.Status, row.HttpStatus, row.Error, row.EndedAt) = (GatewayRequest.Failed, 502, Short(ex.Message), clock.GetUtcNow());
            Write(row);
            throw new ChatGatewayException("The embeddings server could not be reached: " + ex.Message, 502);
        }
        var text = await res.Content.ReadAsStringAsync(ct);
        row.EndedAt = clock.GetUtcNow();
        row.DurationMs = (int)(row.EndedAt - started).TotalMilliseconds;
        if (res.IsSuccessStatusCode)
        {
            try
            {
                var answer = JsonNode.Parse(text);
                row.PromptTokens = answer?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
                if (answer?["model"] is not null && answer is JsonObject o)
                {
                    o["model"] = d.Name;
                    text = o.ToJsonString();
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                row.UsageEstimated = true;
            }
            row.Cost = (d.Price ?? new TokenPrice(0, 0, 0)).Cost(row.PromptTokens, 0, 0);
        }
        else
        {
            (row.Status, row.HttpStatus, row.Error) = (GatewayRequest.Failed, (short)res.StatusCode, Short(PromptLogWriter.ErrorOf(text) ?? text));
        }
        Write(row);
        var status = res.StatusCode;
        res.Dispose();
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }

    /// <summary>The deployments answering a name, the table built again once when none does (a model just added).</summary>
    public async Task<List<Deployment>> RoutesForAsync(string name, CancellationToken ct)
    {
        var table = await TableAsync(false, ct);
        var found = table.Where(d => d.Name == name).ToList();
        if (found.Count == 0)
        {
            found = [.. (await TableAsync(true, ct)).Where(d => d.Name == name)];
        }
        return found;
    }

    private async Task<List<Deployment>> TableAsync(bool fresh, CancellationToken ct)
    {
        if (!fresh && _table is { } t && clock.GetUtcNow() - t.At < RoutesFor)
        {
            return t.Routes;
        }
        await _building.WaitAsync(ct);
        try
        {
            if (!fresh && _table is { } again && clock.GetUtcNow() - again.At < RoutesFor)
            {
                return again.Routes;
            }
            await using var scope = scopes.CreateAsyncScope();
            var routes = await scope.ServiceProvider.GetRequiredService<GatewayRoutes>().BuildAsync(ct);
            // The embeddings server too (Knowledge:EmbedUrl), while it runs: API keys reach it by its model's name.
            var knowledge = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<Knowledge.KnowledgeOptions>>().CurrentValue;
            var embedder = scope.ServiceProvider.GetRequiredService<Knowledge.Embedder>();
            if (await embedder.ReadyAsync(ct) && routes.All(r => r.Name != knowledge.EmbedModel))
            {
                var price = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<PriceOptions>>().CurrentValue;
                routes = [.. routes, new Deployment
                {
                    Name = knowledge.EmbedModel, Kind = Deployment.Embed, BaseUrl = knowledge.EmbedUrl.TrimEnd('/') + "/v1", Model = knowledge.EmbedModel, Key = knowledge.EmbedKey,
                    Mode = "embedding", Info = new JsonObject { ["mode"] = "embedding" }, Params = [], Fingerprint = "embed:" + knowledge.EmbedModel,
                    Price = TokenPrice.Of(null, null, null, price),
                }];
            }
            _table = (routes, clock.GetUtcNow());
            return routes;
        }
        finally
        {
            _building.Release();
        }
    }

    /// <summary>
    /// The copy a request goes to: of those not at their most at once, the least busy for its share (its weight); when all
    /// are full, the least busy all the same (the engine queues it).
    /// </summary>
    public Deployment Pick(IReadOnlyList<Deployment> copies)
    {
        // A copy that just refused a connection is passed over while others are up (all down: tried all the same).
        var now = clock.GetUtcNow();
        var up = copies.Where(d => !(_down.TryGetValue(Key(d), out var until) && until > now)).ToList();
        if (up.Count > 0)
        {
            copies = up;
        }
        Deployment? best = null;
        var score = double.MaxValue;
        foreach (var full in new[] { false, true })
        {
            foreach (var d in copies)
            {
                var busy = _busy.GetValueOrDefault(Key(d));
                if (!full && d.MaxParallel is { } most && busy >= most)
                {
                    continue;
                }
                var s = (double)busy / Math.Max(1, d.Weight);
                if (s < score)
                {
                    (best, score) = (d, s);
                }
            }
            if (best is not null)
            {
                return best;
            }
        }
        return copies[0];
    }

    private static string Key(Deployment d) => d.BaseUrl + "|" + d.Model;

    /// <summary>How long a copy that refused a connection is passed over (Pick), while the pool's other copies answer.</summary>
    public static TimeSpan DownFor { get; set; } = TimeSpan.FromSeconds(10);

    // The copies that refused a connection, and until when they are passed over.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _down = new(StringComparer.Ordinal);

    private void Down(Deployment d) => _down[Key(d)] = clock.GetUtcNow() + DownFor;

    /// <summary>A wait as people read it: "16 minutes", "45 seconds".</summary>
    internal static string Said(TimeSpan t) => t.TotalMinutes >= 1 && t.Seconds == 0
        ? $"{(int)t.TotalMinutes} minute{((int)t.TotalMinutes == 1 ? "" : "s")}"
        : (int)Math.Ceiling(t.TotalSeconds) is var n && n == 1 ? "1 second" : $"{(int)Math.Ceiling(t.TotalSeconds)} seconds";

    /// <summary>Every name a request gives its longest answer by: chat and text completions, Anthropic's messages, OpenAI's newer name, the Responses API.</summary>
    public static readonly string[] AnswerLimits = ["max_tokens", "max_completion_tokens", "max_output_tokens"];

    /// <summary>A whole number however it was given (parsed, or set from code as an int or a long); null for anything else.</summary>
    private static long? Whole(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// The body as this deployment takes it: its model's name there, only the fields its engine reads, usage on every
    /// stream, and a longest answer asked above the model's (its max_output_tokens) lowered to it, by whichever name it is
    /// asked: vLLM, SGLang and other servers write as long as asked, and refuse one past their window.
    /// </summary>
    public static JsonObject Shape(JsonObject body, Deployment d)
    {
        var shaped = (JsonObject)body.DeepClone();
        shaped["model"] = d.Model;
        shaped.Remove("metadata");
        shaped.Remove("fallbacks");
        if (d.Kind != Deployment.LlamaCpp)
        {
            // Only llama.cpp has slots.
            shaped.Remove("id_slot");
        }
        if (!Llm.Core.Models.EngineKinds.Priority(d.Kind))
        {
            // Only vLLM and SGLang take a priority.
            shaped.Remove("priority");
        }
        if (shaped["stream"] is JsonValue s && s.TryGetValue<bool>(out var streams) && streams)
        {
            var so = shaped["stream_options"] as JsonObject ?? new JsonObject();
            so["include_usage"] = true;
            shaped["stream_options"] = so;
        }
        if (Whole(d.Info["max_output_tokens"]) is { } most and > 0)
        {
            foreach (var name in AnswerLimits)
            {
                if (Whole(shaped[name]) is { } asked && asked > most)
                {
                    shaped[name] = most;
                }
            }
        }
        return shaped;
    }

    /// <summary>
    /// A chat completion for the app's own caller: sent to the model's deployment (else its fallbacks', in order), its
    /// response returned as it comes, booked when its stream ends or it is disposed. A refusal throws
    /// <see cref="ChatGatewayException"/> with the engine's words, after it is booked.
    /// </summary>
    public async Task<HttpResponseMessage> ChatAsync(JsonObject body, GatewayCaller caller, IReadOnlyList<string> fallbacks, CancellationToken ct, bool passErrors = false,
        string endpoint = "/chat/completions")
    {
        EnsureWriter();
        var asked = body["model"]?.GetValue<string>() ?? throw new ChatGatewayException("The request names no model.", 400);
        var names = new List<string> { asked };
        names.AddRange(fallbacks.Where(f => f != asked));
        var started = clock.GetUtcNow();
        string? lastError = null;
        int? lastStatus = null;
        short attempts = 0;
        foreach (var name in names)
        {
            var copies = await RoutesForAsync(name, ct);
            if (copies.Count == 0)
            {
                lastError ??= $"There is no model {name}.";
                lastStatus ??= 404;
                continue;
            }
            // Each copy of the model at most once: one not reached, or that cannot take it now (502, 503, 504), passes the
            // request to another before the model's fallback is asked. Never once its answer started.
            var tried = new HashSet<string>(StringComparer.Ordinal);
            var refused = false;
            while (!refused && copies.Where(c => !tried.Contains(Key(c))).ToList() is { Count: > 0 } left)
            {
                var d = Pick(left);
                tried.Add(Key(d));
                attempts++;
                var key = Key(d);
                _busy.AddOrUpdate(key, 1, (_, n) => n + 1);
                var released = false;
                void Release()
                {
                    if (!released)
                    {
                        released = true;
                        _busy.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1));
                    }
                }
                HttpResponseMessage res;
                // The longest wait for the answer to start (its prompt read, a long one too), then the longest pause in it.
                var (firstByte, idle) = (timeouts.CurrentValue.FirstByteTimeout, timeouts.CurrentValue.IdleTimeout);
                var sent = clock.GetUtcNow();
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waiting.CancelAfter(firstByte);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, d.BaseUrl.TrimEnd('/') + endpoint)
                    {
                        Content = new StringContent(Shape(body, d).ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                    if ((d.EngineKey ? engine.Value.ApiKey : d.Key) is { Length: > 0 } bearer)
                    {
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                    }
                    res = await clients.CreateClient(d.VerifyTls == false ? UpstreamUnchecked : Upstream).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, waiting.Token);
                }
                catch (HttpRequestException ex)
                {
                    // Not reached (the engine restarting, a server down): another copy, else its fallback, when there is one.
                    Release();
                    Down(d);
                    (lastError, lastStatus) = ($"The model server could not be reached: {ex.Message}", 502);
                    continue;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // No answer in time: another copy, else its fallback (nothing has reached the caller yet).
                    Release();
                    (lastError, lastStatus) = ($"The model did not start its answer within {Said(firstByte)}.", 504);
                    continue;
                }
                catch
                {
                    Release();
                    throw;
                }
                if (res.IsSuccessStatusCode)
                {
                    var meter = new SseMeter(clock);
                    var row = Row(caller, name, d, name == asked ? null : asked, attempts, started);
                    if (endpoint != "/chat/completions")
                    {
                        row.Call = "completion";
                    }
                    var inner = new TimedStream(await res.Content.ReadAsStreamAsync(ct), meter, firstByte - (clock.GetUtcNow() - sent), firstByte, idle);
                    var metered = new MeteredStream(inner, meter, ended =>
                    {
                        Release();
                        Book(row, meter, d, ended, caller);
                    });
                    var content = new StreamContent(metered);
                    foreach (var h in res.Content.Headers)
                    {
                        content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                    res.Content = content;
                    return res;
                }
                string text;
                using (res)
                {
                    text = await res.Content.ReadAsStringAsync(ct);
                }
                Release();
                (lastError, lastStatus) = (text, (int)res.StatusCode);
                // A server that cannot take it now (restarting, overloaded, gone): another copy, else its fallback. Anything else is the request's.
                if ((int)res.StatusCode is 502 or 503 or 504)
                {
                    continue;
                }
                refused = true;
            }
            if (refused)
            {
                break;
            }
        }
        var failed = Row(caller, asked, null, null, attempts, started);
        failed.Status = GatewayRequest.Failed;
        failed.HttpStatus = (short)(lastStatus ?? 500);
        failed.Error = Short(PromptLogWriter.ErrorOf(lastError ?? "") ?? lastError);
        failed.EndedAt = clock.GetUtcNow();
        failed.DurationMs = (int)(failed.EndedAt - started).TotalMilliseconds;
        Write(failed);
        // An API caller gets the engine's own error as it came (its status and body: the context's size, its own words).
        if (passErrors && lastStatus is { } code and not 404 && lastError is { } said)
        {
            return new HttpResponseMessage((System.Net.HttpStatusCode)code) { Content = new StringContent(said, Encoding.UTF8, "application/json") };
        }
        throw lastStatus == 404 && lastError?.StartsWith("There is no model", StringComparison.Ordinal) == true
            ? new ChatGatewayException(lastError, 404)
            : ChatGatewayException.From(lastError ?? "", lastStatus);
    }

    /// <summary>
    /// A picture, a transcription or speech: sent to the model's server (the app's picture or speech server), booked at its
    /// price (per picture, per second of sound heard, per character read aloud) under its credit kind. A transcription's
    /// answer is read whole (its length prices it); speech passes on as it is made. A refusal throws
    /// <see cref="ChatGatewayException"/>, or, with <paramref name="passErrors"/>, comes back as the server sent it.
    /// </summary>
    public async Task<HttpResponseMessage> MediaAsync(string path, string model, Func<HttpContent> content, string accept, GatewayCaller caller, CancellationToken ct,
        int pictures = 0, int characters = 0, bool passErrors = false)
    {
        EnsureWriter();
        var call = path.Contains("/images/", StringComparison.Ordinal) ? "image" : path.EndsWith("/speech", StringComparison.Ordinal) ? "speech" : "transcription";
        var started = clock.GetUtcNow();
        var row = Row(caller, model, null, null, 1, started);
        row.Call = call;
        row.CreditKind = call == "image" ? "pictures" : "speech";
        var copies = await RoutesForAsync(model, ct);
        if (copies.Count == 0)
        {
            (row.Status, row.HttpStatus, row.Error, row.EndedAt) = (GatewayRequest.Failed, 404, $"There is no model {model}.", clock.GetUtcNow());
            Write(row);
            throw new ChatGatewayException($"There is no model {model}.", 404);
        }
        // A copy not reached passes it to another (the body made again for each), as a chat's does.
        Deployment d;
        HttpResponseMessage res;
        var tried = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            d = Pick([.. copies.Where(c => !tried.Contains(Key(c)))]);
            tried.Add(Key(d));
            row.Attempts = (short)tried.Count;
            row.Deployment = Uri.TryCreate(d.BaseUrl, UriKind.Absolute, out var u) ? u.Authority : d.BaseUrl;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, d.BaseUrl.TrimEnd('/') + (path.StartsWith("/v1/", StringComparison.Ordinal) ? path[3..] : path)) { Content = await RenameAsync(content(), d.Model) };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
                if ((d.EngineKey ? engine.Value.ApiKey : d.Key) is { Length: > 0 } bearer)
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }
                res = await clients.CreateClient(d.VerifyTls == false ? UpstreamUnchecked : Upstream).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                break;
            }
            catch (HttpRequestException ex)
            {
                Down(d);
                if (tried.Count < copies.Count)
                {
                    continue;
                }
                (row.Status, row.HttpStatus, row.Error, row.EndedAt) = (GatewayRequest.Failed, 502, Short(ex.Message), clock.GetUtcNow());
                Write(row);
                throw new ChatGatewayException("The model server could not be reached: " + ex.Message, 502);
            }
        }
        decimal Unit(string k) => d.Params[k] is JsonValue v && v.TryGetValue<decimal>(out var x) ? x : 0m;
        if (!res.IsSuccessStatusCode)
        {
            var said = await res.Content.ReadAsStringAsync(ct);
            res.Dispose();
            (row.Status, row.HttpStatus, row.Error, row.EndedAt) = (GatewayRequest.Failed, (short)res.StatusCode, Short(PromptLogWriter.ErrorOf(said) ?? said), clock.GetUtcNow());
            row.DurationMs = (int)(row.EndedAt - started).TotalMilliseconds;
            Write(row);
            return passErrors ? new HttpResponseMessage(res.StatusCode) { Content = new StringContent(said, Encoding.UTF8, "application/json") } : throw ChatGatewayException.From(said, (int)res.StatusCode);
        }
        if (call == "transcription")
        {
            // Read whole: its length (verbose_json's duration) is its price.
            var text = await res.Content.ReadAsStringAsync(ct);
            var type = res.Content.Headers.ContentType?.ToString() ?? "application/json";
            res.Content = new StringContent(text, Encoding.UTF8);
            res.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            try
            {
                row.Seconds = JsonNode.Parse(text)?["duration"]?.GetValue<double>();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
            {
                row.Seconds = null;
            }
            row.Cost = (decimal)(row.Seconds ?? 0) * Unit("input_cost_per_second");
        }
        else if (call == "image")
        {
            row.Pictures = Math.Max(1, pictures);
            row.Cost = row.Pictures.Value * Unit("input_cost_per_image");
        }
        else
        {
            row.Characters = characters;
            row.Cost = characters * Unit("input_cost_per_character");
        }
        row.EndedAt = clock.GetUtcNow();
        row.DurationMs = (int)(row.EndedAt - started).TotalMilliseconds;
        Write(row);
        return res;
    }

    /// <summary>The request's model named as its server knows it: a JSON body's "model", a form's model field.</summary>
    private static async Task<HttpContent> RenameAsync(HttpContent content, string model)
    {
        switch (content)
        {
            case MultipartFormDataContent form:
                var renamed = new MultipartFormDataContent();
                foreach (var part in form)
                {
                    if (part.Headers.ContentDisposition?.Name?.Trim('"') == "model")
                    {
                        renamed.Add(new StringContent(model), "model");
                        continue;
                    }
                    renamed.Add(part);
                }
                return renamed;
            case StringContent or ByteArrayContent when content.Headers.ContentType?.MediaType == "application/json":
                var text = await content.ReadAsStringAsync();
                if (JsonNode.Parse(text) is JsonObject o)
                {
                    o["model"] = model;
                    return new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json");
                }
                return content;
            default:
                return content;
        }
    }

    private static string? Short(string? s) => s is null ? null : s.Length <= 500 ? s : s[..499] + "…";

    private static GatewayRequest Row(GatewayCaller caller, string model, Deployment? d, string? fallbackFrom, short attempts, DateTimeOffset started) => new()
    {
        StartedAt = started, Surface = caller.Surface, Call = "chat", Protocol = "openai", CreditKind = caller.Surface is "api" or "studio" ? "api" : "chat",
        Person = caller.Person.Trim().ToLowerInvariant(),
        KeyHash = caller.KeyHash, KeyAlias = caller.KeyAlias ?? Chat.ChatKey.Alias, Tag = caller.Tag, ConversationId = caller.Conversation, ProjectId = caller.Project,
        Model = model, Deployment = d is null ? null : Uri.TryCreate(d.BaseUrl, UriKind.Absolute, out var u) ? u.Authority : d.BaseUrl,
        FallbackFrom = fallbackFrom, Attempts = attempts, Status = GatewayRequest.Done, HttpStatus = 200,
    };

    /// <summary>The call's line in the ledger, as its stream ended (<paramref name="whole"/>: to [DONE], else the caller went away).</summary>
    private void Book(GatewayRequest row, SseMeter meter, Deployment d, bool whole, GatewayCaller caller)
    {
        row.EndedAt = clock.GetUtcNow();
        row.DurationMs = (int)Math.Max(0, (row.EndedAt - row.StartedAt).TotalMilliseconds);
        row.FirstTokenAt = meter.FirstToken;
        row.PromptTokens = meter.Prompt;
        row.CachedTokens = meter.Cached;
        row.CompletionTokens = meter.Completion;
        row.UsageEstimated = !meter.Usage;
        if (!meter.Usage)
        {
            // No usage came (the stream cut short): its words, about four characters a token.
            row.CompletionTokens = (int)Math.Ceiling(meter.Characters / 4.0);
        }
        row.Cost = (d.Price ?? new TokenPrice(0, 0, 0)).Cost(row.PromptTokens, row.CachedTokens, row.CompletionTokens);
        if (meter.Error is { } error)
        {
            (row.Status, row.HttpStatus, row.Error) = (GatewayRequest.Failed, 500, Short(PromptLogWriter.ErrorOf(error) ?? error));
        }
        else if (!whole)
        {
            (row.Status, row.HttpStatus) = (GatewayRequest.Stopped, 499);
        }
        Write(row);
        try
        {
            caller.Booked?.Invoke(row);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            LogNotBooked(logger, 1, ex.Message);
        }
    }

    private void EnsureWriter()
    {
        if (_writer is null)
        {
            lock (_ledger)
            {
                _writer ??= Task.Run(WriteAsync);
            }
        }
    }

    /// <summary>The ledger's lines written behind the calls, a few at a time: the person and the chat's project found as LedgerImport finds them.</summary>
    private async Task WriteAsync()
    {
        var batch = new List<GatewayRequest>();
        while (await _ledger.Reader.WaitToReadAsync())
        {
            _writing = true;
            batch.Clear();
            while (batch.Count < 200 && _ledger.Reader.TryRead(out var row))
            {
                batch.Add(row);
            }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var emails = batch.Select(r => r.Person).OfType<string>().Distinct().Select(e => e.ToUpperInvariant()).ToList();
                var people = await db.Users.AsNoTracking().Where(u => u.NormalizedEmail != null && emails.Contains(u.NormalizedEmail))
                    .Select(u => new { u.Id, u.NormalizedEmail }).ToDictionaryAsync(u => u.NormalizedEmail!, u => u.Id);
                var tags = new List<(string?, string?)>();
                foreach (var r in batch)
                {
                    r.UserId = r.Person is { } p && people.TryGetValue(p.ToUpperInvariant(), out var id) ? id : null;
                    tags.Add((r.ConversationId?.ToString(), r.ProjectId?.ToString()));
                    (r.ConversationId, r.ProjectId) = (null, null);
                }
                await Projects.ProjectTags.ResolveAsync(db, scope.ServiceProvider.GetRequiredService<Access.AccessService>(), batch, tags, default);
                db.GatewayRequests.AddRange(batch);
                await db.SaveChangesAsync();
            }
            catch (Exception ex) when (ex is DbUpdateException or Npgsql.NpgsqlException or InvalidOperationException)
            {
                LogNotBooked(logger, batch.Count, ex.Message);
            }
            finally
            {
                _writing = false;
            }
        }
    }

    /// <summary>Every line booked so far is in the ledger (tests, and a replica stopping).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        for (var i = 0; i < 200 && (_writing || _ledger.Reader.Count > 0); i++)
        {
            await Task.Delay(25, ct);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The gateway could not book {Count} requests in the ledger: {Reason}")]
    private static partial void LogNotBooked(ILogger logger, int count, string reason);
}

/// <summary>
/// What an OpenAI-style stream says as it passes: its usage (the last chunk's, with the cached tokens, else llama.cpp's
/// timings), when its first words came, how many characters of words, and an error chunk. Only data lines are read; the
/// bytes go on untouched.
/// </summary>
public sealed class SseMeter(TimeProvider clock)
{
    private readonly StringBuilder _line = new();
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();

    public int Prompt { get; private set; }
    public int Cached { get; private set; }
    public int Completion { get; private set; }
    public bool Usage { get; private set; }
    public bool Done { get; private set; }
    public long Characters { get; private set; }
    public DateTimeOffset? FirstToken { get; private set; }
    public string? Error { get; private set; }

    /// <summary>The answer ended by the gateway (a wait past its limit): booked as failed, with why.</summary>
    public void Fail(string why) => Error ??= why;

    // A body that is not a stream (one JSON answer): kept, up to 4 MB, and read at its end.
    private readonly StringBuilder _whole = new();
    private bool _lines;

    /// <summary>The body's end: a whole JSON answer (no stream) is read for its usage and words.</summary>
    public void Finish()
    {
        if (_line.Length > 0)
        {
            Line(_line.ToString().TrimEnd('\r'));
            _line.Clear();
        }
        if (_lines || _whole.Length == 0)
        {
            return;
        }
        try
        {
            if (JsonNode.Parse(_whole.ToString()) is JsonObject answer)
            {
                Usage_(answer["usage"] as JsonObject);
                if (answer["error"] is JsonObject err)
                {
                    Error = err.ToJsonString();
                }
                var choice = answer["choices"]?[0];
                Characters += (choice?["message"]?["content"] as JsonValue)?.ToString().Length ?? 0;
                Characters += (choice?["text"] as JsonValue)?.ToString().Length ?? 0;
                Done = true;
            }
        }
        catch (JsonException)
        {
            // Not JSON: nothing to read.
        }
    }

    private void Usage_(JsonObject? u)
    {
        if (u?["prompt_tokens"] is null)
        {
            return;
        }
        Usage = true;
        Prompt = Int(u["prompt_tokens"]);
        Completion = Int(u["completion_tokens"]);
        Cached = Int(u["prompt_tokens_details"]?["cached_tokens"]);
    }

    public void Read(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[Math.Max(1, Encoding.UTF8.GetMaxCharCount(Math.Min(bytes.Length, 4096)))];
        while (bytes.Length > 0)
        {
            var take = Math.Min(bytes.Length, 4096);
            var n = _utf8.GetChars(bytes[..take], chars, flush: false);
            bytes = bytes[take..];
            if (!_lines && _whole.Length < 4_000_000)
            {
                _whole.Append(chars[..n]);
            }
            foreach (var c in chars[..n])
            {
                if (c == '\n')
                {
                    Line(_line.ToString().TrimEnd('\r'));
                    _line.Clear();
                }
                else if (_line.Length < 1_000_000)
                {
                    _line.Append(c);
                }
            }
        }
    }

    private void Line(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return;
        }
        if (!_lines)
        {
            _lines = true;
            _whole.Clear();
        }
        var data = line[5..].Trim();
        if (data == "[DONE]")
        {
            Done = true;
            return;
        }
        JsonNode? chunk;
        try
        {
            chunk = JsonNode.Parse(data);
        }
        catch (JsonException)
        {
            return;
        }
        if (chunk?["error"] is JsonObject err)
        {
            Error = err.ToJsonString();
            return;
        }
        Usage_(chunk?["usage"] as JsonObject);
        if (chunk?["timings"] is JsonObject tm && Cached == 0 && tm["cache_n"] is { } cacheN)
        {
            Cached = Int(cacheN);
        }
        if (chunk?["choices"] is JsonArray { Count: > 0 } legacy && legacy[0]?["text"] is JsonValue words && words.ToString().Length > 0)
        {
            FirstToken ??= clock.GetUtcNow();
            Characters += words.ToString().Length;
        }
        if (chunk?["choices"] is JsonArray { Count: > 0 } choices && choices[0]?["delta"] is JsonObject delta)
        {
            var said = (delta["content"] as JsonValue)?.ToString().Length ?? 0;
            said += (delta["reasoning_content"] as JsonValue)?.ToString().Length ?? 0;
            said += delta["tool_calls"]?.ToJsonString().Length ?? 0;
            if (said > 0)
            {
                FirstToken ??= clock.GetUtcNow();
                Characters += said;
            }
        }
    }

    private static int Int(JsonNode? n) => n is JsonValue v ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<double>(out var d) ? (int)d : 0 : 0;
}

/// <summary>The upstream's body as it is read, each piece shown to the meter; once, at its end or its disposal, <c>ended</c> is told whether it was read whole.</summary>
/// <summary>
/// Configuration section "Gateway": how long the gateway waits on a model (Settings → API keys), the app's own calls and
/// API keys' alike. Past either, the answer is stopped and booked as failed with why; before its first byte reached the
/// caller, another copy or the model's fallback is asked first.
/// </summary>
public sealed class GatewayTimeoutOptions
{
    /// <summary>Gateway:FirstByteTimeout: the longest wait for an answer to start (a long prompt read first).</summary>
    public TimeSpan FirstByteTimeout { get; set; } = TimeSpan.FromMinutes(16);

    /// <summary>Gateway:IdleTimeout: the longest pause in an answer once it started.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(16);
}

/// <summary>
/// A model's answer read with a limit on each wait: its first bytes within what is left of the first-byte wait, then no
/// pause longer than the idle wait. A wait past its limit ends the read with an error (the request is cancelled at the
/// server) and the meter says why, so the ledger books it as failed.
/// </summary>
internal sealed class TimedStream(Stream inner, SseMeter meter, TimeSpan firstLeft, TimeSpan firstByte, TimeSpan idle) : Stream
{
    private bool _started;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var limit = _started ? idle : firstLeft < TimeSpan.Zero ? TimeSpan.Zero : firstLeft;
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(limit);
        try
        {
            var n = await inner.ReadAsync(buffer, wait.Token);
            _started |= n > 0;
            return n;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var why = _started ? $"The model paused its answer for more than {ModelGateway.Said(idle)}: it was stopped."
                : $"The model did not start its answer within {ModelGateway.Said(firstByte)}.";
            meter.Fail(why);
            throw new IOException(why);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}

internal sealed class MeteredStream(Stream inner, SseMeter meter, Action<bool> ended) : Stream
{
    private int _ended;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Seen(inner.Read(buffer, offset, count), buffer.AsSpan(offset));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken);
        return Seen(n, buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Seen(int n, Span<byte> buffer)
    {
        if (n > 0)
        {
            meter.Read(buffer[..n]);
        }
        else
        {
            End(true);
        }
        return n;
    }

    private void End(bool whole)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 0)
        {
            meter.Finish();
            ended(whole || meter.Done);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            End(false);
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        End(false);
        await inner.DisposeAsync();
        await base.DisposeAsync();
    }
}
