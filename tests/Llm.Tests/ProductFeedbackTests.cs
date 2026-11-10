using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Send feedback: what people say about Arena, kept for the admins with feedback.manage, who mark it and answer it; the answer reaches the bell.</summary>
[Collection(nameof(AppCollection))]
public sealed class ProductFeedbackTests(AppFixture app)
{
    [Fact]
    public async Task Feedback_is_sent_with_its_page_read_marked_and_answered_by_the_admins_and_the_answer_reaches_the_bell()
    {
        var f = app.Factory;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "fb" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await person.PostAsync("/api/feedback", new { kind = "rant", text = "x" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await person.PostAsync("/api/feedback", new { kind = "idea", text = "  " }));
        await StatusAssert.Is(HttpStatusCode.Created, await person.PostAsync("/api/feedback", new { kind = "idea", text = "Let me pin chats.", page = "/chat/abc" }));
        // Not for them to read others' or to answer.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await person.GetAsync("/api/admin/feedback"));

        var list = await admin.JsonAsync(await admin.GetAsync("/api/admin/feedback?status=new"));
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("person").GetProperty("userName").GetString() == name);
        Assert.Equal(("idea", "Let me pin chats.", "/chat/abc"), (item.GetProperty("kind").GetString(), item.GetProperty("text").GetString(), item.GetProperty("page").GetString()));
        Assert.True(list.GetProperty("counts").GetProperty("new").GetInt32() >= 1);

        var id = item.GetProperty("id").GetGuid();
        var answered = await admin.JsonAsync(await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/feedback/{id}", UriKind.Relative), new { answer = "Pinning comes next month." }));
        Assert.Equal("read", answered.GetProperty("status").GetString());
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/feedback/{id}", UriKind.Relative), new { status = "done" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/feedback/{id}", UriKind.Relative), new { status = "lost" }));

        // Theirs, with the answer; and the answer in their bell.
        var mine = (await person.JsonAsync(await person.GetAsync("/api/feedback/mine"))).EnumerateArray().Single();
        Assert.Equal(("done", "Pinning comes next month."), (mine.GetProperty("status").GetString(), mine.GetProperty("answer").GetString()));
        var news = (await person.JsonAsync(await person.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray();
        Assert.Contains(news, n => n.GetProperty("kind").GetString() == "feedback" && n.GetProperty("title").GetString() == "Your feedback was answered");
    }

    [Fact]
    public async Task Code_arena_sends_feedback_with_the_persons_key_and_a_person_sends_at_most_twenty_a_day()
    {
        var f = app.Factory;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "fk" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var key = made.GetProperty("apiKey").GetString()!;
        var codeArena = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
        codeArena.DefaultRequestHeaders.Authorization = new("Bearer", key);
        codeArena.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        for (var i = 0; i < Llm.Api.Quality.ProductFeedbackEndpoints.PerDay; i++)
        {
            await StatusAssert.Is(HttpStatusCode.Created, await codeArena.PostAsJsonAsync("/api/code-arena/feedback", new { kind = "problem", text = $"grep fails {i}", page = "the IDE" }));
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await codeArena.PostAsJsonAsync("/api/code-arena/feedback", new { kind = "problem", text = "once more" })).StatusCode);
        var list = await admin.JsonAsync(await admin.GetAsync("/api/admin/feedback?status=all"));
        var theirs = list.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("person").GetProperty("userName").GetString() == name).ToList();
        Assert.Equal(Llm.Api.Quality.ProductFeedbackEndpoints.PerDay, theirs.Count);
        Assert.All(theirs, i => Assert.Equal("Code Arena: the IDE", i.GetProperty("page").GetString()));
    }
    [Fact]
    public async Task Screenshots_go_with_feedback_checked_by_their_bytes_and_only_the_sender_and_the_admins_see_them()
    {
        var f = app.Factory;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        string Person() => "fs" + Guid.NewGuid().ToString("N")[..8];
        var (name, other) = (Person(), Person());
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var otherMade = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = other, email = $"{other}@example.test" }));
        var person = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        var stranger = await new TestBrowser(f).SignedInAsync(other, otherMade.GetProperty("password").GetString()!);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 9, 9];
        object Shot(byte[] b) => new { data = Convert.ToBase64String(b) };

        // Not a picture, an SVG (a document that can carry script), too many, too large: refused, nothing kept.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await person.PostAsync("/api/feedback", new { kind = "problem", text = "x", images = new[] { Shot("hello"u8.ToArray()) } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await person.PostAsync("/api/feedback", new { kind = "problem", text = "x", images = new[] { Shot("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray()) } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await person.PostAsync("/api/feedback", new { kind = "problem", text = "x", images = new[] { Shot(png), Shot(png), Shot(png), Shot(png) } }));
        var big = new byte[Llm.Api.Quality.ProductFeedbackEndpoints.MaxImageBytes + 1];
        png.CopyTo(big, 0);
        await StatusAssert.Is(HttpStatusCode.RequestEntityTooLarge, await person.PostAsync("/api/feedback", new { kind = "problem", text = "x", images = new[] { Shot(big) } }));
        Assert.Empty((await person.JsonAsync(await person.GetAsync("/api/feedback/mine"))).EnumerateArray());

        await StatusAssert.Is(HttpStatusCode.Created, await person.PostAsync("/api/feedback", new { kind = "problem", text = "The chart is cut off.", page = "/usage", images = new[] { Shot(png), Shot(jpeg) } }));
        var mine = (await person.JsonAsync(await person.GetAsync("/api/feedback/mine"))).EnumerateArray().Single();
        Assert.Equal(2, mine.GetProperty("images").GetInt32());
        var id = mine.GetProperty("id").GetGuid();
        // Each as the picture it is, in its order; never run as a page.
        var first = await person.GetAsync($"/api/feedback/{id}/images/0");
        Assert.Equal("image/png", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await first.Content.ReadAsByteArrayAsync());
        Assert.Contains("sandbox", first.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", first.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("image/jpeg", (await person.GetAsync($"/api/feedback/{id}/images/1")).Content.Headers.ContentType?.MediaType);
        await StatusAssert.Is(HttpStatusCode.NotFound, await person.GetAsync($"/api/feedback/{id}/images/2"));
        // Another person sees none of it; the admins do.
        await StatusAssert.Is(HttpStatusCode.NotFound, await stranger.GetAsync($"/api/feedback/{id}/images/0"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await stranger.GetAsync($"/api/admin/feedback/{id}/images/0"));
        var item = (await admin.JsonAsync(await admin.GetAsync("/api/admin/feedback?status=all"))).GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == id);
        Assert.Equal(2, item.GetProperty("images").GetInt32());
        Assert.Equal(jpeg, await (await admin.GetAsync($"/api/admin/feedback/{id}/images/1")).Content.ReadAsByteArrayAsync());

        // Code Arena sends them as it got them, with the person's key.
        var codeArena = f.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{AppFixture.Domain}"), HandleCookies = false });
        codeArena.DefaultRequestHeaders.Authorization = new("Bearer", made.GetProperty("apiKey").GetString()!);
        codeArena.DefaultRequestHeaders.Add("X-Requested-With", "code-arena");
        await StatusAssert.Is(HttpStatusCode.Created, await codeArena.PostAsJsonAsync("/api/code-arena/feedback", new { kind = "idea", text = "Show it bigger.", page = "the IDE", images = new[] { Shot(jpeg) } }));
        var fromIde = (await person.JsonAsync(await person.GetAsync("/api/feedback/mine"))).EnumerateArray().First();
        Assert.Equal(("Code Arena: the IDE", 1), (fromIde.GetProperty("page").GetString(), fromIde.GetProperty("images").GetInt32()));
    }
}
