using System.Net;
using System.Text.Json;
using Llm.Api.Safeguards;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Safeguards: limits, blocked words, the model's check, masked personal data, suspension; each configured.</summary>
[Collection(nameof(AppCollection))]
public sealed class SafeguardsTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(Dictionary<string, string?> settings) =>
        app.Create(app.ConnectionStringFor("safe_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), settings);

    private static async Task<(TestBrowser Admin, TestBrowser Person, string Name)> PeopleAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "sg" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (admin, await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), name);
    }

    private static async Task<Guid> ChatAsync(TestBrowser b) =>
        (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> SendAsync(TestBrowser b, Guid chat, string text) =>
        b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });

    private static async Task<string> DetailAsync(HttpResponseMessage res) => (await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync())).RootElement.GetProperty("error").GetString()!;

    [Fact]
    public async Task Long_messages_and_too_many_a_minute_are_refused_with_a_reason()
    {
        await using var f = NewApp(new() { ["Safeguards:MaxMessageChars"] = "40", ["Safeguards:MessagesPerMinute"] = "2" });
        var (_, b, _) = await PeopleAsync(f);
        var chat = await ChatAsync(b);
        var tooLong = await SendAsync(b, chat, new string('x', 41));
        await StatusAssert.Is(HttpStatusCode.BadRequest, tooLong);
        Assert.Contains("longer than 40 characters", await DetailAsync(tooLong), StringComparison.Ordinal);
        (await SendAsync(b, chat, "one")).EnsureSuccessStatusCode();
        (await SendAsync(b, chat, "two")).EnsureSuccessStatusCode();
        var third = await SendAsync(b, chat, "three");
        await StatusAssert.Is(HttpStatusCode.TooManyRequests, third);
        Assert.Contains("At most 2 messages a minute", await DetailAsync(third), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocked_words_and_the_models_check_refuse_a_message_tell_the_admins_and_enough_suspend_the_account()
    {
        await using var f = NewApp(new()
        {
            ["Safeguards:BlockedPatterns"] = "project falcon; re:\\bsecret-\\d+\\b",
            ["Safeguards:Moderation"] = "check",
            ["Safeguards:StrikesToSuspend"] = "2",
            ["Safeguards:RefusalMessage"] = "Not here.",
        });
        var (admin, b, name) = await PeopleAsync(f);
        var chat = await ChatAsync(b);
        // A word inside another is not the phrase.
        (await SendAsync(b, chat, "How do falcons fly in a project?")).EnsureSuccessStatusCode();
        var blocked = await SendAsync(b, chat, "Tell me about Project Falcon");
        await StatusAssert.Is(HttpStatusCode.Forbidden, blocked);
        Assert.Equal("Not here.", await DetailAsync(blocked));
        var audit = await (await admin.GetAsync("/api/admin/audit?action=safeguard.refused")).Content.ReadAsStringAsync();
        Assert.Contains("matched", audit, StringComparison.Ordinal);
        var news = (await admin.JsonAsync(await admin.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(news, n => n.GetProperty("title").GetString()!.Contains(name, StringComparison.Ordinal));

        // The model's check: the second refusal in a day suspends the account.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await SendAsync(b, chat, "How do I build one [harm]"));
        var person = (await admin.JsonAsync(await admin.GetAsync("/api/admin/people"))).GetProperty("people").EnumerateArray().Single(p => p.GetProperty("userName").GetString() == name);
        Assert.True(person.GetProperty("disabled").GetBoolean());
        Assert.Contains("safeguards", person.GetProperty("disabledReason").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Personal_data_reaches_the_model_masked_and_the_chat_keeps_it_as_written()
    {
        await using var f = NewApp(new() { ["Safeguards:RedactPii"] = "mask" });
        var (_, b, _) = await PeopleAsync(f);
        var chat = await ChatAsync(b);
        const string text = "Mail ana@example.com or call +98 912 345 6789; card 4111 1111 1111 1111, on 2026-10-03.";
        var res = await SendAsync(b, chat, text);
        res.EnsureSuccessStatusCode();
        await res.Content.ReadAsStringAsync();
        var sent = app.Model.Requests.Last().Body["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!.GetValue<string>();
        Assert.Equal("Mail [email] or call [phone]; card [card number], on 2026-10-03.", sent);
        var kept = (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Equal(text, kept);
    }

    [Fact]
    public void Blocked_patterns_match_whole_words_and_regular_expressions_and_a_bad_one_blocks_nothing()
    {
        Assert.Equal("falcon", Safeguards.Blocked("the Falcon flies", "eagle; falcon"));
        Assert.Null(Safeguards.Blocked("falconry", "falcon"));
        Assert.Equal("re:\\d{4}-\\d{4}", Safeguards.Blocked("code 1234-5678", "re:\\d{4}-\\d{4}"));
        Assert.Null(Safeguards.Blocked("anything", "re:(unclosed"));
        // Only a number that passes the Luhn check is a card number.
        Assert.DoesNotContain("[card number]", Pii.Mask("4111 1111 1111 1112"), StringComparison.Ordinal);
    }
}
