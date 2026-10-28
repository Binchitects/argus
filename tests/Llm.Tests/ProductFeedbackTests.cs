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
}
