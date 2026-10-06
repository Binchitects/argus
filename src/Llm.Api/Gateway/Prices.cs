using Llm.Api.Models;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Gateway;

/// <summary>
/// Configuration section "Prices" (Settings → Prices), in dollars: what a chat model costs when it
/// has no price of its own (Admin → Models), and what pictures, video and speech cost.
/// </summary>
public sealed class PriceOptions
{
    /// <summary>Per million prompt tokens the engine reads.</summary>
    public decimal InputPerMtok { get; set; } = 0.20m;

    /// <summary>Per million prompt tokens read from the engine's cache (a conversation sent again).</summary>
    public decimal CachedInputPerMtok { get; set; } = 0.02m;

    /// <summary>Per million tokens written, thinking included.</summary>
    public decimal OutputPerMtok { get; set; } = 0.80m;

    public decimal PerImage { get; set; } = 0.01m;

    public decimal PerVideoSecond { get; set; } = 0.05m;

    /// <summary>Per minute of sound turned into text.</summary>
    public decimal PerAudioMinute { get; set; } = 0.006m;

    /// <summary>Per 1,000 characters read aloud.</summary>
    public decimal PerThousandCharacters { get; set; } = 0.015m;
}

/// <summary>A chat model's prices, in dollars per million tokens: prompt tokens read, prompt tokens from the cache, tokens written.</summary>
public sealed record TokenPrice(decimal Input, decimal CachedInput, decimal Output)
{
    /// <summary>What a request cost: its prompt tokens from the cache at the cached price, the rest of its prompt at the input price.</summary>
    public decimal Cost(long prompt, long cached, long completion)
    {
        var hit = Math.Clamp(cached, 0, Math.Max(prompt, 0));
        return ((Math.Max(prompt, 0) - hit) * Input + hit * CachedInput + Math.Max(completion, 0) * Output) / 1_000_000m;
    }

    /// <summary>A model's own prices, and the defaults for those it does not set. Cached input is never priced above its input.</summary>
    public static TokenPrice Of(decimal? input, decimal? cached, decimal? output, PriceOptions defaults)
    {
        var i = input ?? defaults.InputPerMtok;
        return new(i, cached ?? Math.Min(defaults.CachedInputPerMtok, i), output ?? defaults.OutputPerMtok);
    }
}

/// <summary>
/// What everything costs now: each chat model at its own prices (Admin → Models, an engine model's
/// first, then its copy on another GPU server), else the defaults; pictures, video and speech at
/// theirs (Settings → Prices). The gateway is given the same prices (ModelCatalog), so what it
/// books for a request and what the chat keeps for an answer agree. Read once per scope, by one
/// caller at a time: an answer's sub-agents ask at once.
/// </summary>
public sealed class PriceBook(AppDbContext db, IOptionsMonitor<PriceOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Dictionary<string, TokenPrice>? _models;

    public PriceOptions Defaults => options.CurrentValue;

    /// <summary>A model without prices of its own.</summary>
    public TokenPrice Default => TokenPrice.Of(null, null, null, Defaults);

    /// <summary>Every chat model the app registers, by name.</summary>
    public async Task<IReadOnlyDictionary<string, TokenPrice>> ModelsAsync(CancellationToken ct = default)
    {
        if (_models is { } known)
        {
            return known;
        }
        await _lock.WaitAsync(ct);
        try
        {
            if (_models is { } again)
            {
                return again;
            }
            var d = Defaults;
            var models = new Dictionary<string, TokenPrice>(StringComparer.Ordinal);
            foreach (var m in await db.LocalModels.AsNoTracking().ToListAsync(ct))
            {
                models[m.Name] = TokenPrice.Of(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, d);
            }
            foreach (var m in (await db.RemoteServers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct)).SelectMany(s => s.Models))
            {
                models.TryAdd(m.Name, TokenPrice.Of(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, d));
            }
            return _models = models;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>A chat model's prices: its own, else the defaults.</summary>
    public async Task<TokenPrice> ForAsync(string? model, CancellationToken ct = default) =>
        model is not null && (await ModelsAsync(ct)).TryGetValue(model, out var price) ? price : Default;

    /// <summary>What these tokens of this model cost.</summary>
    public async Task<decimal> CostAsync(string? model, long prompt, long cached, long completion, CancellationToken ct = default) =>
        (await ForAsync(model, ct)).Cost(prompt, cached, completion);

    public decimal Images(int count) => Defaults.PerImage * count;

    public decimal Video(double seconds) => Defaults.PerVideoSecond * (decimal)seconds;

    public decimal Transcription(double seconds) => Defaults.PerAudioMinute * (decimal)seconds / 60m;

    public decimal Speech(int characters) => Defaults.PerThousandCharacters * characters / 1000m;

    public void Dispose() => _lock.Dispose();

    /// <summary>New default prices reach the gateway at once; one it cannot reach now is brought in step within a minute (EngineWatcher).</summary>
    public static async Task RegisterAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            await services.GetRequiredService<ModelCatalog>().SyncGatewayAsync(ct);
        }
        catch (GatewayException)
        {
            // Saved all the same: the replica that leads registers them again within a minute.
        }
    }

    /// <summary>The unit a picture, video or speech model is priced by, or null for a chat model.</summary>
    public static string? UnitOf(string model) => model switch
    {
        MediaModels.ImageModel => "image",
        MediaModels.VideoModel => "second",
        MediaModels.SpeechToText => "minute",
        MediaModels.TextToSpeech or MediaModels.TextToSpeechPersian => "characters",
        _ => null,
    };
}
