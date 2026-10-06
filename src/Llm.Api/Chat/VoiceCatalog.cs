using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Llm.Api.Models;
using Llm.Core.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>The company's speech choices (Settings → Speech): each person's until they choose their own (Your account → Voice).</summary>
public sealed partial class SpeechOptions
{
    public const string Auto = "auto";

    /// <summary>The language people speak, for speech to text: auto (Whisper hears which) or its code (en, fa).</summary>
    public string Language { get; set; } = Auto;
    /// <summary>The voice that reads each language: language:model/voice pairs, comma-separated.</summary>
    public string Voices { get; set; } = "en:kokoro/af_heart,fa:piper-fa/gyro";
    /// <summary>How fast texts are read: 0.5 to 2, 1 as the voice speaks.</summary>
    public double Speed { get; set; } = 1;
    /// <summary>Talk reads its answers aloud, and so is the answer to a voice message.</summary>
    public bool ReadAloud { get; set; } = true;

    /// <summary>The voices by language ("en" → "kokoro/af_heart"); a pair not in that form is left out.</summary>
    public IReadOnlyDictionary<string, string> VoiceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Voices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = pair.IndexOf(':', StringComparison.Ordinal);
            if (at > 0 && pair[..at].Trim().ToLowerInvariant() is var language && IsLanguage(language) && pair[(at + 1)..].Trim() is var id && IsVoice(id))
            {
                map.TryAdd(language, id);
            }
        }
        return map;
    }

    /// <summary>A language's code as Whisper and the voices name it: en, fa, haw.</summary>
    public static bool IsLanguage(string? code) => code is not null && LanguageCode().IsMatch(code);

    /// <summary>A voice as the app names it: the gateway's model, a slash, the speech server's voice (kokoro/af_heart).</summary>
    public static bool IsVoice(string? id) => id is not null && VoiceId().IsMatch(id);

    [GeneratedRegex("^[a-z]{2,3}$")]
    private static partial Regex LanguageCode();

    [GeneratedRegex(@"^[^/\s,:]+/[^/\s,:]+$")]
    private static partial Regex VoiceId();
}

/// <summary>A person's own speech choices (Your account → Voice), kept as JSON on them. Each one null (a language left out): the company's.</summary>
public sealed record VoiceChoices
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>auto, or the code of the language they speak (for speech to text).</summary>
    public string? Language { get; init; }
    /// <summary>The voice that reads each language to them: "en" → "kokoro/am_adam".</summary>
    public Dictionary<string, string>? Voices { get; init; }
    public double? Speed { get; init; }
    /// <summary>Talk reads its answers aloud to them, and the answer to their voice message.</summary>
    public bool? ReadAloud { get; init; }

    /// <summary>What a person chose; nothing (or what cannot be read) leaves every choice to the company.</summary>
    public static VoiceChoices Of(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new();
        }
        try
        {
            return JsonSerializer.Deserialize<VoiceChoices>(json, Json) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>As kept on the person: null when every choice is the company's.</summary>
    public string? ToJson() => Language is null && Voices is not { Count: > 0 } && Speed is null && ReadAloud is null
        ? null
        : JsonSerializer.Serialize(this with { Voices = Voices is { Count: > 0 } ? Voices : null }, Json);
}

/// <summary>A voice of a text to speech model at the gateway: its id ("kokoro/af_heart"), the language it reads ("en"), its accent ("en-us") and, when the speech server says, "female" or "male".</summary>
public sealed record OfferedVoice(string Id, string Model, string Name, string Language, string Accent, string? Gender)
{
    /// <summary>A voice by its id, reading <paramref name="language"/> as far as the app knows (the speech server could not say).</summary>
    public static OfferedVoice? Assumed(string id, string language)
    {
        if (!SpeechOptions.IsVoice(id))
        {
            return null;
        }
        var slash = id.IndexOf('/', StringComparison.Ordinal);
        return new OfferedVoice(id, id[..slash], id[(slash + 1)..], language, language, null);
    }
}

/// <summary>
/// What the speech models offer: the voices of the text to speech models the gateway serves (<paramref name="Models"/>),
/// and the languages speech to text knows. Known: the speech server said so; else no voice is listed, and the ones the
/// person and the company chose are believed.
/// </summary>
public sealed record SpeechOffer(IReadOnlyList<OfferedVoice> Voices, IReadOnlyList<string> Languages, IReadOnlySet<string> Models, bool Hears, bool Known);

/// <summary>How one person hears and is read to: their choices over the company's, within what the speech models offer.</summary>
public sealed class PersonalSpeech(VoiceChoices mine, SpeechOptions company, SpeechOffer offer)
{
    public VoiceChoices Mine => mine;
    public SpeechOptions Company => company;
    public SpeechOffer Offer => offer;

    /// <summary>The language for speech to text; null lets Whisper hear which.</summary>
    public string? Language => Spoken(mine, company);

    public double Speed => Math.Clamp(mine.Speed ?? company.Speed, VoiceCatalog.Slowest, VoiceCatalog.Fastest);

    public bool ReadAloud => mine.ReadAloud ?? company.ReadAloud;

    /// <summary>
    /// The voices that may read: those offered. While the speech server cannot be asked, the ones the person and the
    /// company chose instead, of a model the gateway serves, each reading the language it was chosen for.
    /// </summary>
    public IReadOnlyList<OfferedVoice> Usable { get; } = offer.Known ? offer.Voices :
        [.. (mine.Voices ?? []).Concat(company.VoiceMap())
            .Select(c => OfferedVoice.Assumed(c.Value, c.Key)).OfType<OfferedVoice>()
            .Where(v => offer.Models.Contains(v.Model))
            .DistinctBy(v => (v.Id, v.Language))];

    /// <summary>The language a person speaks: theirs, else the company's; null for auto (or a code that is not one).</summary>
    public static string? Spoken(VoiceChoices mine, SpeechOptions company) =>
        (mine.Language ?? company.Language) is var l && l != SpeechOptions.Auto && SpeechOptions.IsLanguage(l) ? l : null;

    /// <summary>The voice for a language, of <paramref name="model"/> only when given: theirs, else the company's, else the first offered for it.</summary>
    public OfferedVoice? VoiceFor(string language, string? model = null) => Pick(language, model, mine.Voices?.GetValueOrDefault(language));

    /// <summary>The voice a language gets when the person leaves it to the company.</summary>
    public OfferedVoice? CompanyVoiceFor(string language) => Pick(language, null, null);

    private OfferedVoice? Pick(string language, string? model, string? chosen)
    {
        var usable = Usable.Where(v => v.Language == language && (model is null || v.Model == model)).ToList();
        var theirs = company.VoiceMap().GetValueOrDefault(language);
        return usable.FirstOrDefault(v => v.Id == chosen) ?? usable.FirstOrDefault(v => v.Id == theirs) ?? usable.FirstOrDefault();
    }

    /// <summary>
    /// The voice a text is read in: the voice of its language (Persian text in the Persian voice, whatever the person
    /// speaks). A language no voice reads goes to the voice of the language they speak, then English's, then any.
    /// Null: no text to speech model at the gateway (of <paramref name="model"/>, when given).
    /// </summary>
    public OfferedVoice? For(string text, string? model = null)
    {
        var usable = Usable.Where(v => model is null || v.Model == model).ToList();
        var language = Tools.Voices.LanguageOf(text, usable.Select(v => v.Language).ToHashSet(StringComparer.Ordinal));
        return VoiceFor(language, model) ?? (Language is { } spoken ? VoiceFor(spoken, model) : null) ?? VoiceFor("en", model) ?? usable.FirstOrDefault();
    }
}

/// <summary>
/// The voices and languages the speech models offer, asked of the speech server: its models' list gives each text to
/// speech model's voices, with their language (and, for Kokoro, a woman's or a man's), and the languages its speech to
/// text knows. Asked again every ten minutes; while it cannot be asked (the audio module left out, the server down), the
/// last answer stays, or the voices chosen are believed.
/// </summary>
public sealed partial class VoiceCatalog(IHttpClientFactory http, Operations.Modules modules, ChatModels models, IOptionsMonitor<SpeechOptions> options,
    TimeProvider clock, ILogger<VoiceCatalog> logger) : IDisposable
{
    public const string Client = "voices";
    public const double Slowest = 0.5;
    public const double Fastest = 2;
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Retry = TimeSpan.FromMinutes(1);

    /// <summary>The languages Whisper knows, for a speech to text model whose card lists none.</summary>
    public static readonly IReadOnlyList<string> WhisperLanguages =
    [
        "en", "zh", "de", "es", "ru", "ko", "fr", "ja", "pt", "tr", "pl", "ca", "nl", "ar", "sv", "it", "id", "hi", "fi", "vi", "he", "uk", "el", "ms", "cs",
        "ro", "da", "hu", "ta", "no", "th", "ur", "hr", "bg", "lt", "la", "mi", "ml", "cy", "sk", "te", "fa", "lv", "bn", "sr", "az", "sl", "kn", "et", "mk",
        "br", "eu", "is", "hy", "ne", "mn", "bs", "kk", "sq", "sw", "gl", "mr", "pa", "si", "km", "sn", "yo", "so", "af", "oc", "ka", "be", "tg", "sd", "gu",
        "am", "yi", "lo", "uz", "fo", "ht", "ps", "tk", "nn", "mt", "sa", "lb", "my", "bo", "tl", "mg", "as", "tt", "haw", "ln", "ha", "ba", "jw", "su", "yue",
    ];

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile Asked _asked = new(null, DateTimeOffset.MinValue, false);

    private sealed record Heard(IReadOnlyList<OfferedVoice> Voices, IReadOnlyList<string> Languages);

    /// <summary>The server's last answer (kept while it cannot be asked), when it was last asked, and whether it answered then.</summary>
    private sealed record Asked(Heard? Heard, DateTimeOffset At, bool Answered)
    {
        public bool Fresh(DateTimeOffset now) => now - At < (Answered ? VoiceCatalog.Fresh : Retry);
    }

    /// <summary>What the speech models at the gateway offer now.</summary>
    public async Task<SpeechOffer> OfferAsync(CancellationToken ct = default)
    {
        var heard = await HeardAsync(ct);
        var all = await models.AllAsync(ct);
        var speaking = all.Where(m => m.Mode == "audio_speech").Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var hears = all.Any(m => m.Mode == "audio_transcription");
        return heard is null
            ? new SpeechOffer([], WhisperLanguages, speaking, hears, Known: false)
            : new SpeechOffer([.. heard.Voices.Where(v => speaking.Contains(v.Model))], heard.Languages, speaking, hears, Known: true);
    }

    /// <summary>How a person hears and is read to.</summary>
    public async Task<PersonalSpeech> ForAsync(AppUser person, CancellationToken ct = default) =>
        new(VoiceChoices.Of(person.Voice), options.CurrentValue, await OfferAsync(ct));

    /// <summary>The language a person speaks, for speech to text (the speech server is not asked); null: Whisper hears which.</summary>
    public string? LanguageOf(AppUser person) => PersonalSpeech.Spoken(VoiceChoices.Of(person.Voice), options.CurrentValue);

    private async Task<Heard?> HeardAsync(CancellationToken ct)
    {
        if (_asked is var known && known.Fresh(clock.GetUtcNow()))
        {
            return known.Heard;
        }
        await _lock.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (_asked.Fresh(now))
            {
                return _asked.Heard;
            }
            if (!await modules.HasAsync("audio", ct))
            {
                _asked = new(null, now, false);
                return null;
            }
            try
            {
                _asked = new(await AskAsync(ct), now, true);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                // The last answer stays; the server is asked again in a minute.
                LogUnheard(logger, ex.Message);
                _asked = _asked with { At = now, Answered = false };
            }
            return _asked.Heard;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The speech server's models (those the app runs), their voices, and the languages its speech to text knows.</summary>
    private async Task<Heard> AskAsync(CancellationToken ct)
    {
        var client = http.CreateClient(Client);
        var voices = new List<OfferedVoice>();
        foreach (var m in Data(await client.GetFromJsonAsync<JsonObject>($"{MediaModels.AudioUrl}/v1/models?task=text-to-speech", ct)))
        {
            var id = Str(m, "id");
            if (MediaModels.Speech.Where(s => s.Id == id && s.Mode == "audio_speech").Select(s => s.Name).FirstOrDefault() is not { } model)
            {
                continue;
            }
            var modelLanguage = (m["language"] as JsonArray)?.FirstOrDefault() is JsonValue l && l.TryGetValue<string>(out var ml) ? ml : null;
            foreach (var v in (m["voices"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var accent = (Str(v, "language") ?? modelLanguage ?? "").ToLowerInvariant().Replace('_', '-');
                if (Str(v, "name") is { } name && SpeechOptions.IsVoice($"{model}/{name}") && Base(accent) is { } language)
                {
                    voices.Add(new OfferedVoice($"{model}/{name}", model, name, language, accent, Str(v, "gender")));
                }
            }
        }
        var whisper = MediaModels.Speech.First(s => s.Mode == "audio_transcription").Id;
        var hearing = Data(await client.GetFromJsonAsync<JsonObject>($"{MediaModels.AudioUrl}/v1/models?task=automatic-speech-recognition", ct))
            .FirstOrDefault(m => Str(m, "id") == whisper);
        List<string> languages = [.. (hearing?["language"] as JsonArray ?? []).Select(x => x is JsonValue s && s.TryGetValue<string>(out var code) ? Base(code) : null)
            .OfType<string>().Distinct()];
        // In the app's order of the models; each model's voices as the server lists them.
        return new Heard([.. voices.OrderBy(v => MediaModels.Speech.Select(s => s.Name).ToList().IndexOf(v.Model))], languages.Count > 0 ? languages : WhisperLanguages);
    }

    private static IEnumerable<JsonObject> Data(JsonObject? list) => (list?["data"] as JsonArray ?? []).OfType<JsonObject>();

    private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>A language tag's language: "en-us" → "en", "fa_IR" → "fa"; null when it is not one.</summary>
    private static string? Base(string tag) => tag.Split('-', '_')[0].ToLowerInvariant() is var code && SpeechOptions.IsLanguage(code) ? code : null;

    public void Dispose() => _lock.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Voices: the speech server could not be asked: {Error}")]
    private static partial void LogUnheard(ILogger logger, string error);
}
