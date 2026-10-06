using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Api.Gateway;
using Llm.Api.Models;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Each person's voice: the language they speak, a voice per language from those the speech models offer (asked of the
/// speech server), the speed and reading aloud; the company's defaults; and every speech path using them.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class VoiceTests(AppFixture app)
{
    private sealed record Setup(WebApplicationFactory<Program> App, FakeAudio Audio) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }

    /// <summary>An app with the speech models at the gateway and a speech server to ask (or none: the audio module left out).</summary>
    private Setup NewApp(bool audio = true, bool down = false)
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel(MediaModels.SpeechToText, null, null, false, false, false, null, null, null, Mode: "audio_transcription"));
        gateway.Models.Add(new GatewayModel(MediaModels.TextToSpeech, null, null, false, false, false, null, null, null, Mode: "audio_speech"));
        gateway.Models.Add(new GatewayModel(MediaModels.TextToSpeechPersian, null, null, false, false, false, null, null, null, Mode: "audio_speech"));
        var fake = new FakeAudio { Down = down };
        var f = app.Create(app.ConnectionStringFor("voice_" + Guid.NewGuid().ToString("N")[..8]), gateway,
            new Dictionary<string, string?> { ["Modules:audio"] = audio ? "true" : "false" }, s =>
            {
                s.AddHttpClient(VoiceCatalog.Client).ConfigurePrimaryHttpMessageHandler(() => fake);
                s.AddHttpClient(MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => fake);
                s.AddHttpClient(Provisioning.Client).ConfigurePrimaryHttpMessageHandler(() => fake);
                s.AddHttpClient(KeySpeech.Client).ConfigurePrimaryHttpMessageHandler(() => app.Model);
            });
        return new Setup(f, fake);
    }

    /// <summary>A person, signed in, with their API key and email.</summary>
    private static async Task<(TestBrowser Browser, string Key, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "v" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var browser = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        return (browser, made.GetProperty("apiKey").GetString()!, $"{name}@example.test");
    }

    private static Task<HttpResponseMessage> ChooseAsync(TestBrowser b, object choices) =>
        b.Http.PutAsJsonAsync(new Uri("/api/account/voice", UriKind.Relative), choices);

    /// <summary>What text to speech was last asked for this person.</summary>
    private JsonObject Spoken(string email) => app.Model.SpeechRequests.Last(r => r["user"]?.GetValue<string>() == email);

    /// <summary>What speech to text was last sent for this person (the form, as text).</summary>
    private string Heard(string email) => app.Model.Transcriptions.Last(t => t.Contains(email, StringComparison.Ordinal));

    private static async Task<HttpResponseMessage> TranscribeAsync(TestBrowser b)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8]);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/webm");
        form.Add(file, "file", "talk.webm");
        return await b.Http.PostAsync(new Uri("/api/chat/transcribe", UriKind.Relative), form);
    }

    [Fact]
    public async Task The_voices_the_speech_server_offers_are_listed_by_language_with_the_companys_defaults()
    {
        await using var s = NewApp();
        var (b, _, _) = await PersonAsync(s.App);
        var mine = await b.JsonAsync(await b.GetAsync("/api/account/voice"));
        Assert.True(mine.GetProperty("known").GetBoolean());
        Assert.True(mine.GetProperty("hears").GetBoolean());
        var voices = mine.GetProperty("voices").EnumerateArray().ToDictionary(v => v.GetProperty("id").GetString()!);
        // The app's models only (not the server's German Piper), Kokoro's first, each with its language, accent and gender.
        Assert.Equal(["kokoro/af_heart", "kokoro/am_adam", "kokoro/bf_emma", "kokoro/ef_dora", "kokoro/ff_siwis", "kokoro/jf_alpha", "piper-fa/gyro"], voices.Keys);
        Assert.Equal(("en", "en-gb", "female"), (voices["kokoro/bf_emma"].GetProperty("language").GetString(), voices["kokoro/bf_emma"].GetProperty("accent").GetString(),
            voices["kokoro/bf_emma"].GetProperty("gender").GetString()));
        Assert.Equal("male", voices["kokoro/am_adam"].GetProperty("gender").GetString());
        Assert.Equal(("fa", "gyro"), (voices["piper-fa/gyro"].GetProperty("language").GetString(), voices["piper-fa/gyro"].GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, voices["piper-fa/gyro"].GetProperty("gender").ValueKind);
        Assert.Equal(["en", "fa", "de", "fr", "es", "ja"], mine.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));

        // The company's: its voices, and the first offered for a language it names none for.
        var company = mine.GetProperty("company");
        Assert.Equal("auto", company.GetProperty("language").GetString());
        Assert.Equal(1.0, company.GetProperty("speed").GetDouble());
        Assert.True(company.GetProperty("readAloud").GetBoolean());
        var defaults = company.GetProperty("voices").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal("kokoro/af_heart", defaults["en"]);
        Assert.Equal("piper-fa/gyro", defaults["fa"]);
        Assert.Equal("kokoro/ef_dora", defaults["es"]);
        Assert.Equal("kokoro/jf_alpha", defaults["ja"]);

        // Nothing chosen yet.
        var chosen = mine.GetProperty("chosen");
        Assert.Equal(JsonValueKind.Null, chosen.GetProperty("language").ValueKind);
        Assert.Empty(chosen.GetProperty("voices").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, chosen.GetProperty("speed").ValueKind);
        Assert.Equal(JsonValueKind.Null, chosen.GetProperty("readAloud").ValueKind);
        // Asked once for both lists, then remembered.
        await b.GetAsync("/api/account/voice");
        Assert.Single(s.Audio.Asked, a => a.Contains("task=text-to-speech", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_persons_choices_are_saved_and_read_aloud_and_Talk_use_them_until_they_go_back_to_the_companys()
    {
        await using var s = NewApp();
        var (b, _, email) = await PersonAsync(s.App);
        var saved = await ChooseAsync(b, new { language = "fa", voices = new Dictionary<string, string> { ["en"] = "kokoro/am_adam" }, speed = 1.25, readAloud = false });
        await StatusAssert.Is(HttpStatusCode.OK, saved);
        var chosen = (await b.JsonAsync(await b.GetAsync("/api/account/voice"))).GetProperty("chosen");
        Assert.Equal("fa", chosen.GetProperty("language").GetString());
        Assert.Equal("kokoro/am_adam", chosen.GetProperty("voices").GetProperty("en").GetString());
        Assert.Equal(1.25, chosen.GetProperty("speed").GetDouble());
        Assert.False(chosen.GetProperty("readAloud").GetBoolean());

        // Read aloud: English in their English voice, Persian in the company's Persian one; both at their speed.
        await StatusAssert.Is(HttpStatusCode.OK, await b.PostAsync("/api/chat/speech", new { text = "Paris is the capital of France." }));
        Assert.Equal(("kokoro", "am_adam", 1.25), (Spoken(email)["model"]!.GetValue<string>(), Spoken(email)["voice"]!.GetValue<string>(), Spoken(email)["speed"]!.GetValue<double>()));
        await StatusAssert.Is(HttpStatusCode.OK, await b.PostAsync("/api/chat/speech", new { text = "پاریس پایتخت فرانسه است." }));
        Assert.Equal(("piper-fa", "gyro"), (Spoken(email)["model"]!.GetValue<string>(), Spoken(email)["voice"]!.GetValue<string>()));

        // Talk writes down what they say in the language they speak.
        await StatusAssert.Is(HttpStatusCode.OK, await TranscribeAsync(b));
        Assert.Contains("name=language\r\n\r\nfa", Heard(email), StringComparison.Ordinal);

        // Back to the company's: nothing kept on them, Kokoro's default voice at its own pace, and Whisper hears the language.
        await StatusAssert.Is(HttpStatusCode.OK, await ChooseAsync(b, new { }));
        using (var scope = s.App.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Null(await db.Users.Where(u => u.Email == email).Select(u => u.Voice).SingleAsync());
        }
        await b.PostAsync("/api/chat/speech", new { text = "Hello again." });
        Assert.Equal("af_heart", Spoken(email)["voice"]!.GetValue<string>());
        Assert.Null(Spoken(email)["speed"]);
        await TranscribeAsync(b);
        Assert.DoesNotContain("name=language", Heard(email), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_voice_offered_for_its_language_a_language_speech_to_text_knows_and_a_speed_from_half_to_twice_are_taken()
    {
        await using var s = NewApp();
        var (b, _, _) = await PersonAsync(s.App);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ChooseAsync(b, new { voices = new Dictionary<string, string> { ["fa"] = "kokoro/am_adam" } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ChooseAsync(b, new { voices = new Dictionary<string, string> { ["en"] = "kokoro/nobody" } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ChooseAsync(b, new { voices = new Dictionary<string, string> { ["de"] = "piper-de/thorsten" } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ChooseAsync(b, new { language = "xx" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ChooseAsync(b, new { speed = 3 }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ChooseAsync(b, new { speed = 0.4 }));
        await StatusAssert.Is(HttpStatusCode.OK, await ChooseAsync(b, new { language = "auto", speed = 2, voices = new Dictionary<string, string> { ["es"] = "kokoro/ef_dora" } }));
    }

    [Fact]
    public async Task Try_it_reads_a_sample_in_the_voice_and_at_the_speed_tried_without_saving_them()
    {
        await using var s = NewApp();
        var (b, _, email) = await PersonAsync(s.App);
        var res = await b.PostAsync("/api/account/voice/try", new { voice = "kokoro/ef_dora", speed = 0.8 });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        Assert.Equal("audio/mpeg", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("ef_dora", Spoken(email)["voice"]!.GetValue<string>());
        Assert.StartsWith("¡Hola!", Spoken(email)["input"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0.8, Spoken(email)["speed"]!.GetValue<double>());

        // No voice: theirs (the company's here) for the language, in that language.
        await StatusAssert.Is(HttpStatusCode.OK, await b.PostAsync("/api/account/voice/try", new { language = "fa" }));
        Assert.Equal(("piper-fa", "gyro"), (Spoken(email)["model"]!.GetValue<string>(), Spoken(email)["voice"]!.GetValue<string>()));
        Assert.StartsWith("سلام", Spoken(email)["input"]!.GetValue<string>(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.OK, await b.PostAsync("/api/account/voice/try", new { }));
        Assert.Equal("af_heart", Spoken(email)["voice"]!.GetValue<string>());

        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/account/voice/try", new { voice = "kokoro/nobody" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync("/api/account/voice/try", new { voice = "kokoro/am_adam", speed = 5 }));
        Assert.Empty((await b.JsonAsync(await b.GetAsync("/api/account/voice"))).GetProperty("chosen").GetProperty("voices").EnumerateObject());
    }

    /// <summary>A call as a tool makes it at gateway.DOMAIN: the key, a JSON body, no cookies.</summary>
    private static async Task<HttpResponseMessage> KeySpeakAsync(WebApplicationFactory<Program> f, string key, object body, string path = "/v1/audio/speech")
    {
        var client = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://gateway.{AppFixture.Domain}"), HandleCookies = false });
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", key);
        return await client.SendAsync(req);
    }

    private JsonObject SpokenText(string text) => app.Model.SpeechRequests.Last(r => r["input"]?.GetValue<string>() == text);

    [Fact]
    public async Task An_API_keys_speech_that_names_no_voice_is_read_in_its_persons_voice_for_the_texts_language()
    {
        await using var s = NewApp();
        var (b, key, _) = await PersonAsync(s.App);
        await StatusAssert.Is(HttpStatusCode.OK, await ChooseAsync(b, new { voices = new Dictionary<string, string> { ["en"] = "kokoro/am_adam" }, speed = 1.5 }));
        // Digits: they belong to no language.
        var marker = Random.Shared.Next(100_000, 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var english = $"Good morning {marker}.";
        var res = await KeySpeakAsync(s.App, key, new { model = "kokoro", input = english });
        await StatusAssert.Is(HttpStatusCode.OK, res);
        Assert.Equal("audio/mpeg", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal(("kokoro", "am_adam", 1.5), (SpokenText(english)["model"]!.GetValue<string>(), SpokenText(english)["voice"]!.GetValue<string>(), SpokenText(english)["speed"]!.GetValue<double>()));

        // No model either: the voice of the text's language, and its model.
        var persian = $"صبح بخیر {marker}";
        await StatusAssert.Is(HttpStatusCode.OK, await KeySpeakAsync(s.App, key, new { input = persian }, "/audio/speech"));
        Assert.Equal(("piper-fa", "gyro"), (SpokenText(persian)["model"]!.GetValue<string>(), SpokenText(persian)["voice"]!.GetValue<string>()));

        // A model named: only its voices (Kokoro has no Persian one, so their English voice).
        var named = $"شب بخیر {marker}";
        await KeySpeakAsync(s.App, key, new { model = "kokoro", input = named });
        Assert.Equal(("kokoro", "am_adam"), (SpokenText(named)["model"]!.GetValue<string>(), SpokenText(named)["voice"]!.GetValue<string>()));

        // A voice and a speed named are kept as they are.
        var own = $"Hello {marker}.";
        await KeySpeakAsync(s.App, key, new { model = "kokoro", input = own, voice = "af_bella", speed = 1.0 });
        Assert.Equal(("af_bella", 1.0), (SpokenText(own)["voice"]!.GetValue<string>(), SpokenText(own)["speed"]!.GetValue<double>()));

        // A key the gateway does not know: the request goes on as it came, and the gateway answers for it.
        var stranger = $"Who am I {marker}?";
        await KeySpeakAsync(s.App, "sk-not-a-key", new { model = "kokoro", input = stranger });
        Assert.Null(SpokenText(stranger)["voice"]);
    }

    [Fact]
    public async Task The_companys_voices_language_speed_and_reading_aloud_are_everyones_until_they_choose()
    {
        await using var s = NewApp();
        var (b, _, email) = await PersonAsync(s.App);
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative), new
        {
            changes = new[]
            {
                new { key = "Speech:Voices", value = "en:kokoro/bf_emma, fa:piper-fa/gyro" },
                new { key = "Speech:Speed", value = "1.5" },
                new { key = "Speech:Language", value = "fa" },
                new { key = "Speech:ReadAloud", value = "false" },
            },
        }));
        var company = (await b.JsonAsync(await b.GetAsync("/api/account/voice"))).GetProperty("company");
        Assert.Equal("kokoro/bf_emma", company.GetProperty("voices").GetProperty("en").GetString());
        Assert.Equal(1.5, company.GetProperty("speed").GetDouble());
        Assert.Equal("fa", company.GetProperty("language").GetString());
        Assert.False(company.GetProperty("readAloud").GetBoolean());
        await b.PostAsync("/api/chat/speech", new { text = "Hello there." });
        Assert.Equal(("bf_emma", 1.5), (Spoken(email)["voice"]!.GetValue<string>(), Spoken(email)["speed"]!.GetValue<double>()));
        await TranscribeAsync(b);
        Assert.Contains("name=language\r\n\r\nfa", Heard(email), StringComparison.Ordinal);

        // Their own choice wins: auto lets Whisper hear the language.
        await StatusAssert.Is(HttpStatusCode.OK, await ChooseAsync(b, new { language = "auto" }));
        await TranscribeAsync(b);
        Assert.DoesNotContain("name=language", Heard(email), StringComparison.Ordinal);

        // A value that is not pairs of a language and a voice is refused.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Speech:Voices", value = "kokoro" } } }));
    }

    [Fact]
    public async Task While_the_speech_server_cannot_be_asked_the_voices_chosen_are_believed()
    {
        await using var s = NewApp(down: true);
        var (b, _, email) = await PersonAsync(s.App);
        var mine = await b.JsonAsync(await b.GetAsync("/api/account/voice"));
        Assert.False(mine.GetProperty("known").GetBoolean());
        Assert.Equal(["kokoro/af_heart", "piper-fa/gyro"], mine.GetProperty("voices").EnumerateArray().Select(v => v.GetProperty("id").GetString()));
        Assert.Equal(VoiceCatalog.WhisperLanguages, mine.GetProperty("languages").EnumerateArray().Select(l => l.GetString()));
        // A voice of a model at the gateway is believed; one of no model there is not.
        await StatusAssert.Is(HttpStatusCode.OK, await ChooseAsync(b, new { voices = new Dictionary<string, string> { ["en"] = "kokoro/am_adam" } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ChooseAsync(b, new { voices = new Dictionary<string, string> { ["en"] = "elsewhere/voice" } }));
        await b.PostAsync("/api/chat/speech", new { text = "Hello there." });
        Assert.Equal("am_adam", Spoken(email)["voice"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Paris is the capital of France.", "en")]
    [InlineData("پاریس پایتخت فرانسه است.", "fa")]
    [InlineData("¿Dónde está la estación de tren?", "es")]
    [InlineData("Je ne sais pas où est la gare, mais je vais la trouver.", "fr")]
    [InlineData("Il treno per Roma non è ancora arrivato.", "it")]
    [InlineData("Você não sabe onde fica a estação?", "pt")]
    [InlineData("東京は日本の首都です。", "ja")]
    [InlineData("北京是中国的首都。", "zh")]
    [InlineData("दिल्ली भारत की राजधानी है।", "hi")]
    [InlineData("Version 2.0 of Kubernetes", "en")]
    [InlineData("Il Divo and Le Monde", "en")]
    [InlineData("", "en")]
    public void A_texts_language_is_told_by_its_script_and_its_small_words(string text, string language) =>
        Assert.Equal(language, Voices.LanguageOf(text));

    [Fact]
    public void A_language_with_no_voice_is_not_told_apart()
    {
        Assert.Equal("en", Voices.LanguageOf("¿Dónde está la estación de tren?", ["en", "fa"]));
        Assert.Equal("es", Voices.LanguageOf("¿Dónde está la estación de tren?", ["en", "es"]));
    }

    private static OfferedVoice Voice(string id, string language, string? accent = null) => OfferedVoice.Assumed(id, language)! with { Accent = accent ?? language };

    private static readonly SpeechOffer Offer = new(
        [Voice("kokoro/af_heart", "en", "en-us"), Voice("kokoro/am_adam", "en", "en-us"), Voice("kokoro/ef_dora", "es"), Voice("piper-fa/gyro", "fa")],
        VoiceCatalog.WhisperLanguages, new HashSet<string> { "kokoro", "piper-fa" }, Hears: true, Known: true);

    [Fact]
    public void A_texts_language_picks_the_persons_voice_for_it_and_a_language_with_no_voice_falls_back()
    {
        var english = new PersonalSpeech(new VoiceChoices { Voices = new() { ["en"] = "kokoro/am_adam" } }, new SpeechOptions(), Offer);
        Assert.Equal("kokoro/am_adam", english.For("Good morning, how are you?")!.Id);
        // Persian text in the Persian voice, though their voice is English.
        Assert.Equal("piper-fa/gyro", english.For("صبح بخیر، حالتان چطور است؟")!.Id);
        Assert.Equal("kokoro/ef_dora", english.For("¿Qué tal estás? Muy bien, gracias.")!.Id);
        // No Japanese voice: the language they speak (none chosen), then English.
        Assert.Equal("kokoro/am_adam", english.For("こんにちは")!.Id);
        var persian = new PersonalSpeech(new VoiceChoices { Language = "fa" }, new SpeechOptions(), Offer);
        Assert.Equal("piper-fa/gyro", persian.For("こんにちは")!.Id);
        Assert.Equal("fa", persian.Language);
        // A model named: its voices only.
        Assert.Equal("kokoro/am_adam", english.For("صبح بخیر", "kokoro")!.Id);
        Assert.Null(english.For("Hello", "whisper"));

        // The Persian model off at the gateway: Persian falls back to the company's English voice.
        var noPersian = new PersonalSpeech(new VoiceChoices(), new SpeechOptions(), Offer with { Voices = [.. Offer.Voices.Where(v => v.Model == "kokoro")], Models = new HashSet<string> { "kokoro" } });
        Assert.Equal("kokoro/af_heart", noPersian.For("صبح بخیر")!.Id);
        // No text to speech at all: nothing to read with.
        Assert.Null(new PersonalSpeech(new VoiceChoices(), new SpeechOptions(), new SpeechOffer([], [], new HashSet<string>(), false, false)).For("Hello"));

        // A voice chosen that is no longer offered gives way to the company's.
        var gone = new PersonalSpeech(new VoiceChoices { Voices = new() { ["en"] = "kokoro/af_bella" } }, new SpeechOptions(), Offer);
        Assert.Equal("kokoro/af_heart", gone.For("Hello")!.Id);
        // Speed within half and twice; reading aloud the company's unless chosen.
        Assert.Equal(2, new PersonalSpeech(new VoiceChoices { Speed = 9 }, new SpeechOptions(), Offer).Speed);
        Assert.True(english.ReadAloud);
        Assert.False(new PersonalSpeech(new VoiceChoices(), new SpeechOptions { ReadAloud = false }, Offer).ReadAloud);
    }

    [Fact]
    public void Choices_are_kept_as_JSON_and_nothing_chosen_keeps_nothing()
    {
        Assert.Null(new VoiceChoices().ToJson());
        Assert.Null(new VoiceChoices { Voices = [] }.ToJson());
        var json = new VoiceChoices { Language = "fa", Voices = new() { ["en"] = "kokoro/am_adam" }, Speed = 1.25, ReadAloud = false }.ToJson();
        Assert.Equal("""{"language":"fa","voices":{"en":"kokoro/am_adam"},"speed":1.25,"readAloud":false}""", json);
        var back = VoiceChoices.Of(json);
        Assert.Equal(("fa", "kokoro/am_adam", 1.25, false), (back.Language, back.Voices!["en"], back.Speed, back.ReadAloud));
        // What cannot be read is the company's every time.
        Assert.Null(VoiceChoices.Of("not json").Language);
    }

    [Fact]
    public async Task A_person_from_v5_2_0_keeps_everything_and_is_read_to_in_the_companys_voice()
    {
        var cs = app.ConnectionStringFor("voice_upgrade_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        // v5.2.0's last migration.
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_Voice", StringComparison.Ordinal)) - 1]);
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","DisplayName","Source","IsDisabled","CreatedAt","AnswerLength","CacheApiAnswers","MemoryOff")
                  VALUES ('00000000-0000-0000-0000-0000000000b1','old','OLD','old@example.test','OLD@EXAMPLE.TEST',true,false,false,true,0,'Old',0,false,now(),'short',true,true);
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await migrator.MigrateAsync();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.UserName == "old");
        Assert.Null(user.Voice);
        Assert.Equal(("short", true, true), (user.AnswerLength, user.CacheApiAnswers, user.MemoryOff));
        var speech = new PersonalSpeech(VoiceChoices.Of(user.Voice), new SpeechOptions(), Offer);
        Assert.Equal("kokoro/af_heart", speech.For("Hello")!.Id);
        Assert.Equal("piper-fa/gyro", speech.For("سلام")!.Id);
        Assert.Null(speech.Language);
        Assert.Equal((1.0, true), (speech.Speed, speech.ReadAloud));
    }

    [Fact]
    public void Traefik_and_the_chart_send_keys_speech_by_the_app()
    {
        var deploy = Path.Combine(AppFixture.PluginsPath, "..", "deploy");
        var routes = File.ReadAllText(Path.Combine(deploy, "config", "traefik", "routes.yml"));
        Assert.Contains("Path(`/v1/audio/speech`) || Path(`/audio/speech`)", routes, StringComparison.Ordinal);
        Assert.Contains("failover: { service: app-speech, fallback: litellm }", routes, StringComparison.Ordinal);
        var ingress = File.ReadAllText(Path.Combine(deploy, "helm", "argus-arena", "templates", "ingress.yaml"));
        Assert.Contains("\"/v1/audio/speech\" \"/audio/speech\"", ingress, StringComparison.Ordinal);
    }
}
