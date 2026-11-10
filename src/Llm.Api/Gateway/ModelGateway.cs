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

/// <summary>The modes of Gateway:Mode: LiteLLM answers all (litellm); the app answers its own chat (chat); the app answers all it serves (own).</summary>
public static class GatewayModes
{
    public const string LiteLlm = "litellm";
    public const string Chat = "chat";
    public const string Own = "own";

    public static readonly string[] All = [LiteLlm, Chat, Own];
}

/// <summary>
/// The app's own model gateway: a chat completion sent straight to where its model is answered (llama.cpp's router, a copy
/// of a vLLM or SGLang model, another GPU server), shaped for it (each engine's own fields only), balanced over a pool's
/// copies by what each is busy with, its fallback asked when the first cannot take it, and booked in the app's ledger
/// (gateway.requests) as it ends: tokens, cost at the model's prices, the first token's time, how it ended. The body passes
/// through as it comes; the usage is read beside it.
/// </summary>
public sealed partial class ModelGateway(IServiceScopeFactory scopes, IHttpClientFactory clients, IOptions<EngineOptions> engine, IOptionsMonitor<LiteLlmOptions> options,
    TimeProvider clock, ILogger<ModelGateway> logger) : IDisposable
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

    public void Dispose()
    {
        _ledger.Writer.TryComplete();
        _building.Dispose();
    }

    /// <summary>Whether the app answers its own chat calls itself (Gateway:Mode chat or own).</summary>
    public bool ForChat => options.CurrentValue.Mode is GatewayModes.Chat or GatewayModes.Own;

    /// <summary>Whether the app answers the API keys' requests itself too (Gateway:Mode own).</summary>
    public bool ForApi => options.CurrentValue.Mode == GatewayModes.Own;

    /// <summary>A request refused before anything was sent, in the ledger with why (it counts as no request and no failure).</summary>
    public void Refused(GatewayCaller caller, string? model, int status, string reason, string message)
    {
        EnsureWriter();
        var row = Row(caller, model ?? "", null, null, 0, clock.GetUtcNow());
        (row.Status, row.HttpStatus, row.Refusal, row.Error, row.EndedAt) = (GatewayRequest.Refused, (short)status, reason.Length <= 16 ? reason : reason[..16], Short(message), row.StartedAt);
        _ledger.Writer.TryWrite(row);
    }

    /// <summary>The routes are built again at the next call (a model loaded, a server or price changed).</summary>
    public void Invalidate() => _table = null;

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

    /// <summary>The body as this deployment takes it: its model's name there, only the fields its engine reads, usage on every stream.</summary>
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
        return shaped;
    }

    /// <summary>
    /// A chat completion for the app's own caller: sent to the model's deployment (else its fallbacks', in order), its
    /// response returned as it comes, booked when its stream ends or it is disposed. A refusal throws
    /// <see cref="ChatGatewayException"/> with the engine's words, after it is booked.
    /// </summary>
    public async Task<HttpResponseMessage> ChatAsync(JsonObject body, GatewayCaller caller, IReadOnlyList<string> fallbacks, CancellationToken ct, bool passErrors = false)
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
            var d = Pick(copies);
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
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, d.BaseUrl.TrimEnd('/') + "/chat/completions")
                {
                    Content = new StringContent(Shape(body, d).ToJsonString(), Encoding.UTF8, "application/json"),
                };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if ((d.EngineKey ? engine.Value.ApiKey : d.Key) is { Length: > 0 } bearer)
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }
                res = await clients.CreateClient(d.VerifyTls == false ? UpstreamUnchecked : Upstream).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex)
            {
                // Not reached (the engine restarting, a server down): its fallback, when there is one.
                Release();
                (lastError, lastStatus) = ($"The model server could not be reached: {ex.Message}", 502);
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
                var inner = await res.Content.ReadAsStreamAsync(ct);
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
            // A server that cannot take it now (restarting, overloaded, gone): its fallback. Anything else is the request's.
            if ((int)res.StatusCode is 502 or 503 or 504)
            {
                continue;
            }
            break;
        }
        var failed = Row(caller, asked, null, null, attempts, started);
        failed.Status = GatewayRequest.Failed;
        failed.HttpStatus = (short)(lastStatus ?? 500);
        failed.Error = Short(PromptLogWriter.ErrorOf(lastError ?? "") ?? lastError);
        failed.EndedAt = clock.GetUtcNow();
        failed.DurationMs = (int)(failed.EndedAt - started).TotalMilliseconds;
        _ledger.Writer.TryWrite(failed);
        // An API caller gets the engine's own error as it came (its status and body: the context's size, its own words).
        if (passErrors && lastStatus is { } code and not 404 && lastError is { } said)
        {
            return new HttpResponseMessage((System.Net.HttpStatusCode)code) { Content = new StringContent(said, Encoding.UTF8, "application/json") };
        }
        throw lastStatus == 404 && lastError?.StartsWith("There is no model", StringComparison.Ordinal) == true
            ? new ChatGatewayException(lastError, 404)
            : ChatGatewayException.From(lastError ?? "", lastStatus);
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
        _ledger.Writer.TryWrite(row);
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
        }
    }

    /// <summary>Every line booked so far is in the ledger (tests, and a replica stopping).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        for (var i = 0; i < 200 && _ledger.Reader.Count > 0; i++)
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

    public void Read(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[Math.Max(1, Encoding.UTF8.GetMaxCharCount(Math.Min(bytes.Length, 4096)))];
        while (bytes.Length > 0)
        {
            var take = Math.Min(bytes.Length, 4096);
            var n = _utf8.GetChars(bytes[..take], chars, flush: false);
            bytes = bytes[take..];
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
        if (chunk?["usage"] is JsonObject u && u["prompt_tokens"] is not null)
        {
            Usage = true;
            Prompt = Int(u["prompt_tokens"]);
            Completion = Int(u["completion_tokens"]);
            Cached = Int(u["prompt_tokens_details"]?["cached_tokens"]);
        }
        if (chunk?["timings"] is JsonObject tm && Cached == 0 && tm["cache_n"] is { } cacheN)
        {
            Cached = Int(cacheN);
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
