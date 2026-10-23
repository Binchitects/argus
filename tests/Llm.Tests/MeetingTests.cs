using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Api.Media;
using Llm.Api.Meetings;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Meetings: a recording heard part by part, who spoke each told apart, the minutes written (summary, decisions, action
/// items with whose they are and when due, the speakers' names the meeting said), the speakers told which person they
/// are (their items theirs, reminded on the day due), the items made a plan, the meeting asked about in a chat, and all of
/// it its owner's alone.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class MeetingTests(AppFixture app)
{
    /// <summary>The studio server's voice encoder: the parts' speakers taking turns (or, failing, a 503), what was asked kept.</summary>
    private sealed class FakeVoices : HttpMessageHandler
    {
        public readonly ConcurrentQueue<JsonObject> Asked = new();
        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != "/v1/speakers")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"models": {"voice": true}}""") };
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Asked.Enqueue(body);
            if (Fail)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("""{"error": "The voice model is not in the library yet."}""") };
            }
            var labels = new JsonArray([.. body["segments"]!.AsArray().Select((_, i) => (JsonNode)(i % 2))]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["speakers"] = labels, ["count"] = 2 }.ToJsonString()) };
        }
    }

    /// <summary>Its own app and database, with speech to text at the gateway and the sandbox's ffmpeg (a fake one).</summary>
    private WebApplicationFactory<Program> NewApp(FakeVoices voices, FakeSandbox sandbox)
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel(Llm.Api.Models.MediaModels.SpeechToText, null, null, false, false, false, null, null, null, Mode: "audio_transcription"));
        return app.Create(app.ConnectionStringFor("meetings_" + Guid.NewGuid().ToString("N")[..8]), gateway,
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-meeting-tests", ["Sandbox:Dir"] = sandbox.Dir },
            s => s.AddHttpClient(StudioServer.Client).ConfigurePrimaryHttpMessageHandler(() => voices));
    }

    private static async Task<(TestBrowser Browser, Guid Id)> PersonAsync(WebApplicationFactory<Program> f, string? display = null)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "m" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = display ?? name }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid());
    }

    /// <summary>A recording sent as the page sends one (an MP3 here: ID3 and a frame).</summary>
    private static async Task<HttpResponseMessage> UploadAsync(TestBrowser b, string? title = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent([0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0, 0xFF, 0xFB, 0x90, 0x44, 0, 0, 0, 0]);
        file.Headers.ContentType = new("audio/mpeg");
        form.Add(file, "file", "standup.mp3");
        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }
        return await b.Http.PostAsync(new Uri("/api/meetings", UriKind.Relative), form);
    }

    /// <summary>The meeting once it is no longer being made (the engine runs in the background).</summary>
    private static async Task<JsonElement> MadeAsync(TestBrowser b, string id)
    {
        JsonElement got = default;
        for (var i = 0; i < 400; i++)
        {
            var res = await b.GetAsync($"/api/meetings/{id}");
            Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
            got = await b.JsonAsync(res);
            if (got.GetProperty("meeting").GetProperty("status").GetString() != "processing")
            {
                return got;
            }
            await Task.Delay(50);
        }
        return got;
    }

    [Fact]
    public async Task A_recording_becomes_a_transcript_with_its_speakers_and_minutes_with_action_items()
    {
        var voices = new FakeVoices();
        await using var sandbox = new FakeSandbox();
        await using var f = NewApp(voices, sandbox);
        var (ana, _) = await PersonAsync(f);
        var made = await UploadAsync(ana);
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var id = (await ana.JsonAsync(made)).GetProperty("id").GetString()!;
        var got = await MadeAsync(ana, id);
        var meeting = got.GetProperty("meeting");
        Assert.True(meeting.GetProperty("status").GetString() == "ready", meeting.ToString());
        Assert.Equal(("Checkout launch", 40.0), (meeting.GetProperty("title").GetString(), meeting.GetProperty("seconds").GetDouble()));
        Assert.Equal(JsonValueKind.Null, meeting.GetProperty("error").ValueKind);

        // Heard part by part, with their times; the speakers told apart, named as the meeting named them.
        Assert.Contains(app.Model.Transcriptions, t => t.Contains("timestamp_granularities", StringComparison.Ordinal));
        var lines = got.GetProperty("transcript").EnumerateArray().ToList();
        Assert.Equal([0, 1, 0, 1], lines.Select(l => l.GetProperty("speaker").GetInt32()));
        Assert.Equal(("Yes, if the payment tests pass. I'll fix the flaky one.", 9.5), (lines[1].GetProperty("text").GetString(), lines[1].GetProperty("start").GetDouble()));
        Assert.Equal(4, Assert.Single(voices.Asked)["segments"]!.AsArray().Count);
        Assert.Equal(["Ana", "Ben"], got.GetProperty("speakers").EnumerateArray().Select(s => s.GetProperty("name").GetString()));
        Assert.Equal(["Ship the new checkout on Monday."], got.GetProperty("decisions").EnumerateArray().Select(d => d.GetString()));

        // Whose each item is: a speaker by number or by the name the meeting said, else the name given; a date only when it is one.
        var actions = got.GetProperty("actions").EnumerateArray().ToList();
        Assert.Equal([("Fix the flaky payment test", "Ben", null), ("Write the release notes", "Ana", "2026-10-30"), ("Tell support", "Dana from support", null)],
            actions.Select(a => (a.GetProperty("text").GetString(), a.GetProperty("owner").GetString(), a.GetProperty("due").GetString())));
        Assert.Equal([1, 0], actions.Take(2).Select(a => a.GetProperty("speaker").GetInt32()));
        Assert.Contains(app.Model.Requests, r => r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() == "meetings");
        var bell = (await ana.JsonAsync(await ana.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray();
        Assert.Contains(bell, n => n.GetProperty("title").GetString() == "Minutes ready: Checkout launch");

        // Its sound plays from any line (a range of it).
        using var range = new HttpRequestMessage(HttpMethod.Get, $"/api/meetings/{id}/audio");
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 3);
        var part = await ana.Http.SendAsync(range);
        Assert.Equal((HttpStatusCode.PartialContent, "audio/mpeg"), (part.StatusCode, part.Content.Headers.ContentType?.MediaType));

        // Listed, and nobody else's.
        Assert.Contains((await ana.JsonAsync(await ana.GetAsync("/api/meetings"))).EnumerateArray(), m => m.GetProperty("id").GetString() == id && m.GetProperty("open").GetInt32() == 3);
        var (other, _) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/meetings/{id}"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/meetings/{id}/audio"));
        Assert.Empty((await other.JsonAsync(await other.GetAsync("/api/meetings"))).EnumerateArray());
    }

    [Fact]
    public async Task A_speaker_told_as_a_person_gets_their_items_now_and_a_reminder_on_the_day_due()
    {
        var voices = new FakeVoices();
        await using var sandbox = new FakeSandbox();
        await using var f = NewApp(voices, sandbox);
        var (ana, _) = await PersonAsync(f);
        var (ben, benId) = await PersonAsync(f, "Ben Builder");
        var id = (await ana.JsonAsync(await UploadAsync(ana, "Standup"))).GetProperty("id").GetString()!;
        var got = await MadeAsync(ana, id);
        // Its owner's title stays.
        Assert.Equal("Standup", got.GetProperty("meeting").GetProperty("title").GetString());
        var fix = got.GetProperty("actions").EnumerateArray().First().GetProperty("id").GetString()!;

        var named = await ana.JsonAsync(await ana.Http.PutAsJsonAsync(new Uri($"/api/meetings/{id}/speakers/1", UriKind.Relative), new { name = "Ben B.", personId = benId }));
        var speaker = named.GetProperty("speakers").EnumerateArray().Single(s => s.GetProperty("speaker").GetInt32() == 1);
        Assert.Equal(("Ben B.", "Ben Builder"), (speaker.GetProperty("name").GetString(), speaker.GetProperty("person").GetString()));
        var item = named.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("id").GetString() == fix);
        Assert.Equal(("Ben B.", "Ben Builder"), (item.GetProperty("owner").GetString(), item.GetProperty("person").GetString()));
        var told = (await ben.JsonAsync(await ben.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(told, n => n.GetProperty("title").GetString() == "1 action item from Standup");
        // Not the meeting itself: it is Ana's.
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/meetings/{id}"));

        // Due today: reminded once, at the next look.
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await StatusAssert.Is(HttpStatusCode.OK, await ana.Http.PutAsJsonAsync(new Uri($"/api/meetings/{id}/actions/{fix}", UriKind.Relative), new { due = today }));
        var reminders = f.Services.GetRequiredService<MeetingReminders>();
        Assert.Equal(1, await reminders.RemindAsync(CancellationToken.None));
        Assert.Equal(0, await reminders.RemindAsync(CancellationToken.None));
        Assert.Contains((await ben.JsonAsync(await ben.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray(),
            n => n.GetProperty("title").GetString() == "Due today: Fix the flaky payment test");
        // Done: not reminded again, even when its day moves.
        await ana.Http.PutAsJsonAsync(new Uri($"/api/meetings/{id}/actions/{fix}", UriKind.Relative), new { done = true, due = "2020-01-01" });
        Assert.Equal(0, await reminders.RemindAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Items_are_changed_made_a_plan_and_the_meeting_is_asked_about_and_deleted_with_its_recording()
    {
        var voices = new FakeVoices();
        await using var sandbox = new FakeSandbox();
        await using var f = NewApp(voices, sandbox);
        var (ana, _) = await PersonAsync(f);
        var id = (await ana.JsonAsync(await UploadAsync(ana))).GetProperty("id").GetString()!;
        await MadeAsync(ana, id);

        var added = await ana.JsonAsync(await ana.PostAsync($"/api/meetings/{id}/actions", new { text = "Book the launch call", speaker = 0, due = "2026-10-26" }));
        Assert.Equal(4, added.GetProperty("actions").GetArrayLength());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.PostAsync($"/api/meetings/{id}/actions", new { text = "x", due = "next week" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.PostAsync($"/api/meetings/{id}/actions", new { text = "x", speaker = 7 }));
        var tell = added.GetProperty("actions").EnumerateArray().Single(a => a.GetProperty("text").GetString() == "Tell support").GetProperty("id").GetString()!;
        var left = await ana.JsonAsync(await ana.Http.DeleteAsync(new Uri($"/api/meetings/{id}/actions/{tell}", UriKind.Relative)));
        Assert.Equal(3, left.GetProperty("actions").GetArrayLength());

        // A plan: a step each, whose and when.
        var plan = (await ana.JsonAsync(await ana.PostAsync($"/api/meetings/{id}/plan"))).GetProperty("id").GetString()!;
        var steps = (await ana.JsonAsync(await ana.GetAsync($"/api/plans/{plan}"))).GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal([("Fix the flaky payment test", "Ben"), ("Write the release notes", "Ana"), ("Book the launch call", "Ana")],
            steps.Select(s => (s.GetProperty("title").GetString(), s.GetProperty("owner").GetString())));

        // Asked about: a chat whose question carries the minutes and the transcript.
        var ask = await ana.JsonAsync(await ana.PostAsync($"/api/meetings/{id}/ask", new { question = "Who fixes the tests?" }));
        Assert.EndsWith("Who fixes the tests?", ask.GetProperty("message").GetString(), StringComparison.Ordinal);
        var file = ask.GetProperty("attachment").GetProperty("id").GetString()!;
        var text = await (await ana.GetAsync($"/api/chat/attachments/{file}/content")).Content.ReadAsStringAsync();
        Assert.Contains("## Transcript", text, StringComparison.Ordinal);
        Assert.Contains("[00:09] Ben: Yes, if the payment tests pass.", text, StringComparison.Ordinal);
        Assert.Contains("- Fix the flaky payment test — Ben", text, StringComparison.Ordinal);

        // Deleted: the meeting and its recording.
        await StatusAssert.Is(HttpStatusCode.NoContent, await ana.Http.DeleteAsync(new Uri($"/api/meetings/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ana.GetAsync($"/api/meetings/{id}"));
    }

    [Fact]
    public async Task Without_the_studios_voice_model_a_meeting_is_still_made_with_one_voice_and_says_so()
    {
        var voices = new FakeVoices { Fail = true };
        await using var sandbox = new FakeSandbox();
        await using var f = NewApp(voices, sandbox);
        var (ana, _) = await PersonAsync(f);
        var id = (await ana.JsonAsync(await UploadAsync(ana))).GetProperty("id").GetString()!;
        var got = await MadeAsync(ana, id);
        Assert.Equal("ready", got.GetProperty("meeting").GetProperty("status").GetString());
        Assert.Equal("Who spoke is not told: the studio's voice model is not running here.", got.GetProperty("meeting").GetProperty("error").GetString());
        Assert.All(got.GetProperty("transcript").EnumerateArray(), l => Assert.Equal(0, l.GetProperty("speaker").GetInt32()));
        // Made again on asking (the studio back).
        voices.Fail = false;
        await StatusAssert.Is(HttpStatusCode.OK, await ana.PostAsync($"/api/meetings/{id}/again"));
        Assert.Equal([0, 1, 0, 1], (await MadeAsync(ana, id)).GetProperty("transcript").EnumerateArray().Select(l => l.GetProperty("speaker").GetInt32()));
    }
}

