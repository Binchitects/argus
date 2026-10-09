using System.Security.Claims;
using System.Text.Json.Nodes;
using Llm.Api.Endpoints;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Llm.Core.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat;

/// <summary>A voice to try: one offered (none: theirs for <paramref name="Language"/>), at a speed (none: theirs).</summary>
public sealed record VoiceTry(string? Voice = null, string? Language = null, double? Speed = null);

/// <summary>
/// A person's voice (Your account → Voice): the language they speak, the voice that reads each language to them, the
/// speed, and whether Talk reads its answers aloud. What they chose, the company's defaults (Settings → Speech), and what
/// the speech models offer; trying a voice reads a sample in it.
/// </summary>
public static class VoiceEndpoints
{
    /// <summary>What a voice says when it is tried, in its language (English for a language with no sample).</summary>
    private static readonly Dictionary<string, string> Samples = new(StringComparer.Ordinal)
    {
        ["en"] = "Hello! This is how your answers sound when I read them aloud.",
        ["fa"] = "سلام! پاسخ‌هایتان را این‌گونه برایتان بلند می‌خوانم.",
        ["es"] = "¡Hola! Así suenan tus respuestas cuando las leo en voz alta.",
        ["fr"] = "Bonjour ! Voici comment je lis vos réponses à voix haute.",
        ["it"] = "Ciao! Ecco come leggo ad alta voce le tue risposte.",
        ["pt"] = "Olá! É assim que eu leio as suas respostas em voz alta.",
        ["ja"] = "こんにちは。回答を読み上げると、このように聞こえます。",
        ["zh"] = "你好！我朗读你的回答时，听起来就是这样。",
        ["hi"] = "नमस्ते! आपके उत्तर पढ़कर सुनाए जाने पर ऐसे सुनाई देते हैं।",
    };

    /// <summary>Times a change is put on top of another change of the same person's saved meanwhile.</summary>
    private const int Tries = 5;

    public static IServiceCollection AddVoices(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SpeechOptions>(config.GetSection("Speech"));
        services.AddSingleton<VoiceCatalog>();
        services.AddSingleton<Settings.ISettingWarning>(sp => sp.GetRequiredService<VoiceCatalog>());
        services.AddHttpClient(VoiceCatalog.Client, c => c.Timeout = TimeSpan.FromSeconds(5));
        services.AddHttpClient(KeySpeech.Client, (sp, c) =>
        {
            c.BaseAddress = new Uri(sp.GetRequiredService<IOptions<LiteLlmOptions>>().Value.Url);
            // A long text is read for as long as the gateway allows (its own timeout).
            c.Timeout = Timeout.InfiniteTimeSpan;
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
        return services;
    }

    public static void MapVoices(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/account/voice").RequireAuthorization();
        me.MapGet("", MineAsync);
        me.MapPatch("", ChooseAsync);
        me.MapPost("/try", TryAsync);
        // API keys' speech at gateway.DOMAIN, sent here by Traefik while the app is up.
        foreach (var path in KeySpeech.Paths)
        {
            app.MapPost(path, KeySpeech.ProxyAsync).AllowAnonymous().DisableAntiforgery();
        }
    }

    private static async Task<IResult> MineAsync(ClaimsPrincipal p, UserManager<AppUser> users, VoiceCatalog voices, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        return Results.Ok(View(await voices.ForAsync(me, ct)));
    }

    private static object View(PersonalSpeech s)
    {
        var languages = s.Usable.Select(v => v.Language).Distinct().ToList();
        return new
        {
            chosen = new { language = s.Mine.Language, voices = s.Mine.Voices ?? [], speed = s.Mine.Speed, readAloud = s.Mine.ReadAloud },
            company = new
            {
                language = PersonalSpeech.Spoken(new VoiceChoices(), s.Company) ?? SpeechOptions.Auto,
                voices = languages.Select(l => (l, v: s.CompanyVoiceFor(l))).Where(x => x.v is not null).ToDictionary(x => x.l, x => x.v!.Id),
                speed = Math.Clamp(s.Company.Speed, VoiceCatalog.Slowest, VoiceCatalog.Fastest),
                readAloud = s.Company.ReadAloud,
            },
            voices = s.Usable.Select(v => new { id = v.Id, model = v.Model, name = v.Name, language = v.Language, accent = v.Accent, gender = v.Gender }),
            languages = s.Offer.Languages,
            hears = s.Offer.Hears,
            known = s.Offer.Known,
        };
    }

    /// <summary>
    /// The choices a request names change, and only those: null puts one back to the company's ("voices": null all the
    /// voices, a language's null its voice). A voice must be one offered for its language, unless it is the one the person
    /// has already (its model turned off since: it stays theirs, and reads again when the model is back).
    /// </summary>
    private static async Task<IResult> ChooseAsync(JsonObject body, ClaimsPrincipal p, UserManager<AppUser> users, AppDbContext db, VoiceCatalog voices,
        IOptionsMonitor<SpeechOptions> options, CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var offer = await voices.OfferAsync(ct);
        var had = VoiceChoices.Of(me.Voice);
        string? language;
        double? speed;
        bool? readAloud;
        Dictionary<string, string?>? voiceChanges = null;
        try
        {
            language = body["language"]?.GetValue<string>().Trim() is { Length: > 0 } l ? l.ToLowerInvariant() : null;
            speed = body["speed"]?.GetValue<double>();
            readAloud = body["readAloud"]?.GetValue<bool>();
            if (body["voices"] is JsonObject named)
            {
                voiceChanges = named.ToDictionary(x => x.Key.Trim().ToLowerInvariant(), x => x.Value?.GetValue<string>()?.Trim() is { Length: > 0 } id ? id : null);
            }
            else if (body["voices"] is not null)
            {
                throw new InvalidOperationException();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return AuthEndpoints.Problem(400, "choices", "Send the language as text, the voices as an object of a language and a voice, the speed as a number and reading aloud as true or false.");
        }
        if (language is not null && language != SpeechOptions.Auto && !offer.Languages.Contains(language))
        {
            return AuthEndpoints.Problem(400, "language", $"\"{language}\" is not a language speech to text knows here.");
        }
        if (speed is { } fast && (double.IsNaN(fast) || fast < VoiceCatalog.Slowest || fast > VoiceCatalog.Fastest))
        {
            return AuthEndpoints.Problem(400, "speed", $"The speed is {VoiceCatalog.Slowest} to {VoiceCatalog.Fastest}.");
        }
        if (voiceChanges is { Count: > 50 })
        {
            return AuthEndpoints.Problem(400, "voices", "Choose a voice for at most 50 languages.");
        }
        foreach (var (lang, id) in voiceChanges ?? [])
        {
            // Offered for that language; while the speech server cannot be asked, any voice of a text to speech model at the gateway.
            var fits = id is null || had.Voices?.GetValueOrDefault(lang) == id || (offer.Known
                ? offer.Voices.Any(v => v.Id == id && v.Language == lang)
                : SpeechOptions.IsLanguage(lang) && OfferedVoice.Assumed(id, lang) is { } assumed && offer.Models.Contains(assumed.Model));
            if (!fits)
            {
                return AuthEndpoints.Problem(400, "voice", $"\"{id}\" is not a voice for {lang} here.");
            }
        }

        for (var tries = 1; ; tries++)
        {
            var saved = VoiceChoices.Of(me.Voice);
            var mine = new Dictionary<string, string>(body.ContainsKey("voices") && voiceChanges is null ? [] : saved.Voices ?? [], StringComparer.Ordinal);
            foreach (var (lang, id) in voiceChanges ?? [])
            {
                if (id is null)
                {
                    mine.Remove(lang);
                }
                else
                {
                    mine[lang] = id;
                }
            }
            var choices = new VoiceChoices
            {
                Language = body.ContainsKey("language") ? language : saved.Language,
                Voices = mine,
                Speed = body.ContainsKey("speed") ? speed : saved.Speed,
                ReadAloud = body.ContainsKey("readAloud") ? readAloud : saved.ReadAloud,
            };
            me.Voice = choices.ToJson();
            var result = await users.UpdateAsync(me);
            if (result.Succeeded)
            {
                return Results.Ok(View(new PersonalSpeech(choices, options.CurrentValue, offer)));
            }
            if (tries == Tries || result.Errors.All(e => e.Code != nameof(IdentityErrorDescriber.ConcurrencyFailure)))
            {
                return AuthEndpoints.Problem(409, "busy", "Your voice could not be saved just now: try again.");
            }
            // Another change of theirs was saved meanwhile (another control, another tab): this one goes on top of it.
            await db.Entry(me).ReloadAsync(ct);
        }
    }

    /// <summary>A sample read in a voice, before (or after) choosing it.</summary>
    private static async Task<IResult> TryAsync(VoiceTry body, HttpContext http, ClaimsPrincipal p, UserManager<AppUser> users, GatewayChat gateway, VoiceCatalog voices,
        CancellationToken ct)
    {
        var me = (await users.GetUserAsync(p))!;
        var speech = await voices.ForAsync(me, ct);
        if (body.Speed is { } speed && (double.IsNaN(speed) || speed < VoiceCatalog.Slowest || speed > VoiceCatalog.Fastest))
        {
            return AuthEndpoints.Problem(400, "speed", $"The speed is {VoiceCatalog.Slowest} to {VoiceCatalog.Fastest}.");
        }
        OfferedVoice? voice;
        if (body.Voice is { Length: > 0 } id)
        {
            voice = speech.Usable.FirstOrDefault(v => v.Id == id && (body.Language is null || v.Language == body.Language));
            if (voice is null)
            {
                return AuthEndpoints.Problem(400, "voice", $"\"{id}\" is not a voice offered here.");
            }
        }
        else
        {
            voice = speech.VoiceFor(body.Language ?? speech.Language ?? "en") ?? speech.For("");
        }
        var text = Samples.GetValueOrDefault(voice?.Language ?? "en") ?? Samples["en"];
        return await ReadAloudAsync(http, gateway, voice, text, body.Speed ?? speech.Speed, me.Email!, ct);
    }

    /// <summary>A text read aloud in a voice, in the person's name: the MP3 passed on as the speech server writes it.</summary>
    public static async Task<IResult> ReadAloudAsync(HttpContext http, GatewayChat gateway, OfferedVoice? voice, string text, double speed, string email, CancellationToken ct)
    {
        if (voice is null)
        {
            return AuthEndpoints.Problem(503, "no_speech", "The gateway has no text to speech model (the audio module).");
        }
        try
        {
            var res = await gateway.OpenSpeechAsync(voice.Model, text, voice.Name, email, ct, speed);
            http.Response.RegisterForDispose(res);
            return Results.Stream(await res.Content.ReadAsStreamAsync(ct), "audio/mpeg");
        }
        catch (ChatGatewayException ex)
        {
            return AuthEndpoints.Problem(502, "gateway", ex.Message);
        }
    }
}
