using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Calls in rooms: pages in a room's call reach one another through its events, and what each says is a caption for all, in each reader's language.</summary>
[Collection(nameof(AppCollection))]
public sealed class RoomCallTests(AppFixture app)
{
    private static readonly byte[] Opus = [0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>A page's events as they come (until the test ends).</summary>
    private sealed class Events : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _reading;
        public ConcurrentQueue<JsonElement> Seen { get; } = new();

        public Events(TestBrowser b, Guid room, Guid page)
        {
            var opened = new TaskCompletionSource();
            _reading = Task.Run(async () =>
            {
                using var res = await b.Http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/rooms/{room}/events?page={page}"), HttpCompletionOption.ResponseHeadersRead, _stop.Token);
                using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(_stop.Token));
                opened.TrySetResult();
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        Seen.Enqueue(JsonDocument.Parse(line[6..]).RootElement.Clone());
                    }
                }
            });
            Opened = opened.Task;
        }

        public Task Opened { get; }

        public async Task<JsonElement> NextAsync(string type, Func<JsonElement, bool>? which = null)
        {
            for (var i = 0; i < 200; i++)
            {
                var found = Seen.FirstOrDefault(e => e.GetProperty("type").GetString() == type && (which?.Invoke(e) ?? true));
                if (found.ValueKind != JsonValueKind.Undefined)
                {
                    return found;
                }
                await Task.Delay(25);
            }
            throw new Xunit.Sdk.XunitException($"No {type} event came; seen: {string.Join(", ", Seen.Select(e => e.GetRawText()))}");
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            try
            {
                await _reading;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
            {
                // stopped
            }
            _stop.Dispose();
        }
    }

    private async Task<(WebApplicationFactory<Program> App, TestBrowser Admin)> NewAppAsync()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel(Llm.Api.Models.MediaModels.SpeechToText, null, null, false, false, false, null, null, null, Mode: "audio_transcription"));
        var f = app.Create(app.ConnectionStringFor("call_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        return (f, await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword));
    }

    private static async Task<(Guid Id, TestBrowser Browser)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin, string name)
    {
        var user = "rc" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test", displayName = name }));
        return (made.GetProperty("id").GetGuid(), await new TestBrowser(f).SignedInAsync(user, made.GetProperty("password").GetString()!));
    }

    private static async Task<HttpResponseMessage> SayAsync(TestBrowser b, Guid room, Guid page)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Opus);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/webm;codecs=opus");
        form.Add(file, "file", "speech.webm");
        form.Add(new StringContent(page.ToString()), "page");
        return await b.Http.PostAsync(new Uri($"/api/rooms/{room}/call/captions", UriKind.Relative), form);
    }

    [Fact]
    public async Task People_in_a_rooms_call_reach_each_others_pages_and_read_what_each_says_in_their_language()
    {
        var (f, admin) = await NewAppAsync();
        await using var _f = f;
        var (_, olivia) = await PersonAsync(f, admin, "Olivia");
        var (benId, ben) = await PersonAsync(f, admin, "Ben");
        var (veraId, vera) = await PersonAsync(f, admin, "Vera");
        var (_, otto) = await PersonAsync(f, admin, "Otto");
        var room = (await olivia.JsonAsync(await olivia.PostAsync("/api/rooms", new { title = "Launch", userIds = new[] { benId } }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await olivia.PostAsync($"/api/rooms/{room}/members", new { userIds = new[] { veraId }, role = "viewer" }));
        var (oliviaPage, benPage) = (Guid.NewGuid(), Guid.NewGuid());

        // A seat is for a page that has the room open: its events are how the others reach it.
        await StatusAssert.Is(HttpStatusCode.Conflict, await olivia.PostAsync($"/api/rooms/{room}/call", new { page = oliviaPage, language = "en" }));
        await using var oliviaSees = new Events(olivia, room, oliviaPage);
        await using var benSees = new Events(ben, room, benPage);
        await Task.WhenAll(oliviaSees.Opened, benSees.Opened);
        await oliviaSees.NextAsync("presence", e => e.GetProperty("here").GetArrayLength() == 2);

        var joined = await olivia.JsonAsync(await olivia.PostAsync($"/api/rooms/{room}/call", new { page = oliviaPage, language = "en" }));
        Assert.Equal([oliviaPage], joined.GetProperty("seats").EnumerateArray().Select(s => s.GetProperty("page").GetGuid()));
        Assert.Equal(0, joined.GetProperty("iceServers").GetArrayLength());
        var withBen = await ben.JsonAsync(await ben.PostAsync($"/api/rooms/{room}/call", new { page = benPage, language = "de" }));
        Assert.Equal(2, withBen.GetProperty("seats").GetArrayLength());
        await oliviaSees.NextAsync("call", e => e.GetProperty("seats").GetArrayLength() == 2);
        // A viewer listens to the room, not its call; a stranger finds nothing.
        await StatusAssert.Is(HttpStatusCode.NotFound, await vera.PostAsync($"/api/rooms/{room}/call", new { page = Guid.NewGuid() }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await otto.PostAsync($"/api/rooms/{room}/call", new { page = Guid.NewGuid() }));

        // Ben's page calls Olivia's: the signal reaches her page only, as it was sent.
        await StatusAssert.Is(HttpStatusCode.NoContent, await ben.PostAsync($"/api/rooms/{room}/call/signal", new { page = benPage, to = oliviaPage, signal = new { description = new { type = "offer", sdp = "v=0" } } }));
        var offer = await oliviaSees.NextAsync("signal");
        Assert.Equal(benPage, offer.GetProperty("from").GetGuid());
        Assert.Equal(benId, offer.GetProperty("by").GetGuid());
        Assert.Equal("offer", offer.GetProperty("signal").GetProperty("description").GetProperty("type").GetString());
        Assert.DoesNotContain(benSees.Seen, e => e.GetProperty("type").GetString() == "signal");
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.PostAsync($"/api/rooms/{room}/call/signal", new { page = benPage, to = Guid.NewGuid(), signal = new { candidate = new { } } }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await olivia.PostAsync($"/api/rooms/{room}/call/signal", new { page = benPage, to = oliviaPage, signal = new { candidate = new { } } }));

        // What Ben says is written down and shown to the call.
        var said = await SayAsync(ben, room, benPage);
        await StatusAssert.Is(HttpStatusCode.OK, said);
        var caption = await ben.JsonAsync(said);
        Assert.Equal("What is the capital of France?", caption.GetProperty("text").GetString());
        var shown = (await oliviaSees.NextAsync("caption")).GetProperty("caption");
        Assert.Equal(caption.GetProperty("id").GetGuid(), shown.GetProperty("id").GetGuid());
        Assert.Equal(benId, shown.GetProperty("by").GetGuid());
        // Only from one's own seat.
        await StatusAssert.Is(HttpStatusCode.Conflict, await SayAsync(olivia, room, benPage));

        // Read in German: translated once, for all who read in it.
        var asked = () => app.Model.Requests.Count(r => r.Body["metadata"]?["tags"]?[0]?.GetValue<string>() == "translate" && r.Body["messages"]![1]!["content"]!.GetValue<string>().Contains("capital of France", StringComparison.Ordinal));
        var before = asked();
        var path = $"/api/rooms/{room}/call/captions/{caption.GetProperty("id").GetGuid()}/translation";
        var german = await olivia.JsonAsync(await olivia.PostAsync(path, new { language = "de" }));
        Assert.Equal("[German] What is the capital of France?", german.GetProperty("text").GetString());
        Assert.Equal("[German] What is the capital of France?", (await vera.JsonAsync(await vera.PostAsync(path, new { language = "de" }))).GetProperty("text").GetString());
        Assert.Equal(before + 1, asked());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await olivia.PostAsync(path, new { language = "xx" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await otto.PostAsync(path, new { language = "de" }));
        // Someone who joins late sees what was said.
        Assert.Single((await ben.JsonAsync(await ben.PostAsync($"/api/rooms/{room}/call", new { page = benPage, language = "de", muted = true }))).GetProperty("captions").EnumerateArray());

        // Muted: nothing Ben says is sent; leaving, his seat goes, and with nobody left what was said goes too.
        await StatusAssert.Is(HttpStatusCode.Conflict, await SayAsync(ben, room, benPage));
        await oliviaSees.NextAsync("call", e => e.GetProperty("seats").EnumerateArray().Any(s => s.GetProperty("muted").GetBoolean()));
        await StatusAssert.Is(HttpStatusCode.NoContent, await ben.Http.DeleteAsync(new Uri($"/api/rooms/{room}/call?page={benPage}", UriKind.Relative)));
        await oliviaSees.NextAsync("call", e => e.GetProperty("seats").GetArrayLength() == 1);
        await StatusAssert.Is(HttpStatusCode.NoContent, await olivia.Http.DeleteAsync(new Uri($"/api/rooms/{room}/call?page={oliviaPage}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await olivia.PostAsync(path, new { language = "fa" }));
    }

    [Fact]
    public async Task A_call_holds_as_many_as_it_may_and_a_page_just_opened_hears_who_is_in_it()
    {
        var (f, admin) = await NewAppAsync();
        await using var _f = f;
        var (_, olivia) = await PersonAsync(f, admin, "Olivia");
        var room = (await olivia.JsonAsync(await olivia.PostAsync("/api/rooms", new { title = "Standup", userIds = Array.Empty<Guid>() }))).GetProperty("id").GetGuid();
        var pages = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToList();
        var open = pages.Select(p => new Events(olivia, room, p)).ToList();
        try
        {
            await Task.WhenAll(open.Select(e => e.Opened));
            foreach (var p in pages.Take(8))
            {
                await StatusAssert.Is(HttpStatusCode.OK, await olivia.PostAsync($"/api/rooms/{room}/call", new { page = p }));
            }
            await StatusAssert.Is(HttpStatusCode.Conflict, await olivia.PostAsync($"/api/rooms/{room}/call", new { page = pages[8] }));
            await using var late = new Events(olivia, room, Guid.NewGuid());
            Assert.Equal(8, (await late.NextAsync("call")).GetProperty("seats").GetArrayLength());
        }
        finally
        {
            foreach (var e in open)
            {
                await e.DisposeAsync();
            }
        }
    }
}
