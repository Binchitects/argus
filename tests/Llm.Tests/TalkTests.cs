using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Talk: what is said is written down, the answer is asked for in spoken sentences, and each sentence is read aloud.</summary>
[Collection(nameof(AppCollection))]
public sealed class TalkTests(AppFixture app)
{
    private async Task<(WebApplicationFactory<Program> App, TestBrowser Browser, string Email)> NewAppAsync(bool speech = true)
    {
        var gateway = new FakeGateway();
        if (speech)
        {
            gateway.Models.Add(new GatewayModel(Llm.Api.Models.MediaModels.SpeechToText, null, null, false, false, false, null, null, null, Mode: "audio_transcription"));
            gateway.Models.Add(new GatewayModel(Llm.Api.Models.MediaModels.TextToSpeech, null, null, false, false, false, null, null, null, Mode: "audio_speech"));
            gateway.Models.Add(new GatewayModel(Llm.Api.Models.MediaModels.TextToSpeechPersian, null, null, false, false, false, null, null, null, Mode: "audio_speech"));
        }
        var f = app.Create(app.ConnectionStringFor("talk_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "t" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (f, await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<HttpResponseMessage> TranscribeAsync(TestBrowser b, string name, string type, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        form.Add(file, "file", name);
        return await b.Http.PostAsync(new Uri("/api/chat/transcribe", UriKind.Relative), form);
    }

    private static readonly byte[] Opus = [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8];

    [Fact]
    public async Task What_is_said_is_written_down_by_the_speech_server_in_the_persons_name()
    {
        var (f, b, email) = await NewAppAsync();
        await using var _f = f;
        var res = await TranscribeAsync(b, "talk.webm", "audio/webm;codecs=opus", Opus);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        Assert.Equal("What is the capital of France?", (await b.JsonAsync(res)).GetProperty("text").GetString());
        var sent = app.Model.Transcriptions.Last(t => t.Contains(email, StringComparison.Ordinal));
        // The recording goes as the browser made it (no conversion first: every moment counts), to the speech to text model.
        Assert.Contains("filename=speech.webm", sent, StringComparison.Ordinal);
        Assert.Contains("audio/webm", sent, StringComparison.Ordinal);
        Assert.Contains(Llm.Api.Models.MediaModels.SpeechToText, sent, StringComparison.Ordinal);
        // Asked with its length (verbose_json): the gateway prices a transcription by it.
        Assert.Matches(@"name=""?response_format""?\r\n(?:[^\r\n]+\r\n)*\r\nverbose_json\r\n", sent);
    }

    [Fact]
    public async Task Talk_takes_only_recordings_and_says_when_there_is_no_speech_server()
    {
        var (f, b, _) = await NewAppAsync();
        await using var _f = f;
        await StatusAssert.Is(HttpStatusCode.BadRequest, await TranscribeAsync(b, "notes.txt", "text/plain", "hello"u8.ToArray()));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await TranscribeAsync(b, "talk.webm", "audio/webm", []));

        var (g, quiet, _) = await NewAppAsync(speech: false);
        await using var _g = g;
        var res = await TranscribeAsync(quiet, "talk.webm", "audio/webm", Opus);
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, res);
        Assert.Contains("speech to text", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_spoken_question_asks_for_spoken_sentences_for_its_answer_only()
    {
        var (f, b, _) = await NewAppAsync();
        await using var _f = f;
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();
        var spoken = "spoken-" + Guid.NewGuid().ToString("N")[..8];
        await (await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = spoken, spoken = true })).Content.ReadAsStringAsync();
        var asked = app.Model.Requests.Last(r => r.Body.ToJsonString().Contains(spoken, StringComparison.Ordinal)).Body;
        var question = LastUser(asked);
        Assert.StartsWith(spoken, question, StringComparison.Ordinal);
        Assert.EndsWith(Talk.Note, question, StringComparison.Ordinal);

        // The chat keeps the question as said, and the next typed one has no note.
        var saved = await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"));
        Assert.Contains(saved.GetProperty("messages").EnumerateArray(), m => m.GetProperty("content").GetString() == spoken);
        var typed = "typed-" + Guid.NewGuid().ToString("N")[..8];
        await (await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = typed })).Content.ReadAsStringAsync();
        var next = app.Model.Requests.Last(r => r.Body.ToJsonString().Contains(typed, StringComparison.Ordinal)).Body;
        Assert.DoesNotContain("voice conversation", LastUser(next), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_sentence_is_read_aloud_in_the_voice_of_its_language_and_streams_through()
    {
        var (f, b, email) = await NewAppAsync();
        await using var _f = f;
        var english = await b.PostAsync("/api/chat/speech", new { text = "Paris is the capital of France." });
        await StatusAssert.Is(HttpStatusCode.OK, english);
        Assert.Equal("audio/mpeg", english.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 0x49, 0x44, 0x33, 4, 0 }, await english.Content.ReadAsByteArrayAsync());
        var persian = await b.PostAsync("/api/chat/speech", new { text = "پاریس پایتخت فرانسه است." });
        await StatusAssert.Is(HttpStatusCode.OK, persian);
        var asked = app.Model.SpeechRequests.Last(r => r["user"]?.GetValue<string>() == email);
        Assert.Equal(Llm.Api.Models.MediaModels.TextToSpeechPersian, asked["model"]!.GetValue<string>());
        Assert.Equal("gyro", asked["voice"]!.GetValue<string>());
    }

    private static string LastUser(JsonObject request)
    {
        var content = request["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!;
        return content is JsonArray parts
            ? string.Concat(parts.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "text").Select(p => p["text"]!.GetValue<string>()))
            : content.GetValue<string>();
    }
}
