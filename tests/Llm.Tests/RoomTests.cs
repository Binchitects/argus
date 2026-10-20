using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>Rooms: chats several people share. Whoever asks pays and their rules apply; every message says who wrote it.</summary>
[Collection(nameof(AppCollection))]
public sealed class RoomTests(AppFixture app)
{
    private Task<TestBrowser> AdminAsync() => new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);

    private async Task<(Guid Id, string Email, TestBrowser Browser)> PersonAsync(TestBrowser admin, string name)
    {
        var user = "rm" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test", displayName = name }));
        return (made.GetProperty("id").GetGuid(), $"{user}@example.test", await new TestBrowser(app.Factory).SignedInAsync(user, made.GetProperty("password").GetString()!));
    }

    private static async Task SendAsync(TestBrowser b, Guid chat, object message)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", message);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
    }

    private static async Task<List<JsonElement>> MessagesAsync(TestBrowser b, Guid chat) =>
        [.. (await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{chat}"))).GetProperty("messages").EnumerateArray()];

    private static async Task EventuallyAsync(Func<Task<bool>> done)
    {
        for (var i = 0; i < 100 && !await done(); i++)
        {
            await Task.Delay(50);
        }
        Assert.True(await done());
    }

    [Fact]
    public async Task A_member_asks_in_a_room_as_themselves_a_viewer_reads_and_others_find_nothing()
    {
        var admin = await AdminAsync();
        var (_, _, owner) = await PersonAsync(admin, "Olivia");
        var (memberId, memberEmail, member) = await PersonAsync(admin, "Ben");
        var (viewerId, _, viewer) = await PersonAsync(admin, "Vera");
        var (_, _, outsider) = await PersonAsync(admin, "Otto");

        var res = await owner.PostAsync("/api/rooms", new { title = "Launch", userIds = new[] { memberId } });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        var room = (await owner.JsonAsync(res)).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await owner.PostAsync($"/api/rooms/{room}/members", new { userIds = new[] { viewerId }, role = "viewer" }));
        // Only the owner invites, unless they let members.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await member.PostAsync($"/api/rooms/{room}/members", new { userIds = new[] { viewerId } }));

        // Said to the people: not answered. Named @Arena: answered, as the one who asked.
        await StatusAssert.Is(HttpStatusCode.OK, await owner.PostAsync($"/api/chat/conversations/{room}/queue", new { content = "Shall we launch Friday?" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await member.PostAsync($"/api/chat/conversations/{room}/messages", new { content = "Not to the model" }));
        await SendAsync(member, room, new { content = "@Arena what could go wrong?" });
        // The member's question goes as theirs: their email to the gateway, their name in the room's history.
        var asked = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == memberEmail).Body["messages"]!.AsArray();
        Assert.Contains("room several people share", asked[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("You are answering Ben", asked[0]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("[Olivia]: Shall we launch Friday?", asked[^1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("[Ben]: @Arena what could go wrong?", asked[^1]!["content"]!.GetValue<string>(), StringComparison.Ordinal);

        var seen = await MessagesAsync(viewer, room);
        Assert.Equal(["Olivia", "Ben", "Ben"], seen.Select(m => m.GetProperty("author").GetProperty("name").GetString()));
        var shape = await viewer.JsonAsync(await viewer.GetAsync($"/api/chat/conversations/{room}"));
        Assert.Equal("viewer", shape.GetProperty("room").GetProperty("role").GetString());
        Assert.Equal(3, shape.GetProperty("room").GetProperty("members").GetArrayLength());
        // A viewer reads, and writes nothing.
        await StatusAssert.Is(HttpStatusCode.NotFound, await viewer.PostAsync($"/api/chat/conversations/{room}/messages", new { content = "Me too" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await viewer.PostAsync($"/api/chat/conversations/{room}/queue", new { content = "Me too" }));
        // A member manages nothing.
        await StatusAssert.Is(HttpStatusCode.NotFound, await member.Http.DeleteAsync(new Uri($"/api/chat/conversations/{room}", UriKind.Relative)));
        // The room is in its people's lists.
        var list = (await member.JsonAsync(await member.GetAsync("/api/chat/conversations"))).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == room);
        Assert.True(list.GetProperty("isRoom").GetBoolean());
        Assert.False(list.GetProperty("mine").GetBoolean());

        foreach (var path in new[] { $"/api/chat/conversations/{room}", $"/api/chat/conversations/{room}/stamp", $"/api/rooms/{room}", $"/api/rooms/{room}/events" })
        {
            await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.GetAsync(path));
        }

        // Taken out: at once nothing.
        await StatusAssert.Is(HttpStatusCode.NoContent, await owner.Http.DeleteAsync(new Uri($"/api/rooms/{room}/members/{memberId}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await member.GetAsync($"/api/chat/conversations/{room}"));
        // What they wrote stays.
        Assert.Contains(await MessagesAsync(owner, room), m => m.GetProperty("content").GetString() == "@Arena what could go wrong?");
    }

    [Fact]
    public async Task The_model_answers_a_mention_or_a_reply_to_it_and_people_hear_their_mentions_and_replies()
    {
        var admin = await AdminAsync();
        var (_, _, owner) = await PersonAsync(admin, "Ola");
        var (memberId, memberEmail, member) = await PersonAsync(admin, "Bo");
        var room = (await owner.JsonAsync(await owner.PostAsync("/api/rooms", new { title = "Ops", userIds = new[] { memberId } }))).GetProperty("id").GetGuid();
        var asked = () => app.Model.Requests.Count(r => r.Body["user"]!.GetValue<string>() == memberEmail);

        await StatusAssert.Is(HttpStatusCode.OK, await owner.PostAsync($"/api/chat/conversations/{room}/queue", new { content = "Coffee first, @Bo?", mentions = new[] { memberId } }));
        await EventuallyAsync(async () => (await MessagesAsync(owner, room)).Any(m => m.GetProperty("content").GetString() == "Coffee first, @Bo?"));
        var coffee = (await MessagesAsync(owner, room)).Single(m => m.GetProperty("content").GetString() == "Coffee first, @Bo?");
        Assert.True(coffee.GetProperty("forPeople").GetBoolean());
        Assert.Contains("Ola mentioned you in Ops", await (await member.GetAsync("/api/notifications")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A reply to a person: for the people, and it tells them.
        var before = asked();
        await StatusAssert.Is(HttpStatusCode.OK, await member.PostAsync($"/api/chat/conversations/{room}/queue", new { content = "Yes please", replyTo = coffee.GetProperty("id").GetGuid() }));
        await EventuallyAsync(async () => (await MessagesAsync(owner, room)).Any(m => m.GetProperty("content").GetString() == "Yes please"));
        var bell = await (await owner.GetAsync("/api/notifications")).Content.ReadAsStringAsync();
        Assert.True(bell.Contains("Bo replied to you in Ops", StringComparison.Ordinal), bell);

        // @Arena: answered.
        await StatusAssert.Is(HttpStatusCode.OK, await member.PostAsync($"/api/chat/conversations/{room}/queue", new { content = "@Arena what time is it?" }));
        await EventuallyAsync(async () => (await MessagesAsync(owner, room)).Any(m => m.GetProperty("role").GetString() == "assistant"));
        Assert.Equal(before + 1, asked());
        var answer = (await MessagesAsync(owner, room)).Last(m => m.GetProperty("role").GetString() == "assistant");
        Assert.Equal("Yes please", (await MessagesAsync(owner, room)).Single(m => m.GetProperty("content").GetString() == "Yes please").GetProperty("content").GetString());

        // A reply to the model's answer: a question to it, with the answer it replies to.
        await StatusAssert.Is(HttpStatusCode.OK, await member.PostAsync($"/api/chat/conversations/{room}/queue", new { content = "Say it shorter", replyTo = answer.GetProperty("id").GetGuid() }));
        await EventuallyAsync(() => Task.FromResult(asked() == before + 2));
        var last = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == memberEmail).Body["messages"]!.AsArray();
        var question = last[^1]!["content"]!.GetValue<string>();
        Assert.Contains("Say it shorter", question, StringComparison.Ordinal);
        Assert.Contains(answer.GetProperty("content").GetString()![..10], question, StringComparison.Ordinal);
        var reply = (await MessagesAsync(owner, room)).Single(m => m.GetProperty("content").GetString() == "Say it shorter");
        Assert.Equal(answer.GetProperty("id").GetGuid(), reply.GetProperty("replyToId").GetGuid());
        Assert.False(reply.GetProperty("forPeople").GetBoolean());
    }

    [Fact]
    public async Task A_room_goes_to_its_longest_member_when_its_owner_is_deleted_and_hands_over_on_request()
    {
        var admin = await AdminAsync();
        var (ownerId, _, owner) = await PersonAsync(admin, "Ann");
        var (aId, _, a) = await PersonAsync(admin, "Al");
        var (bId, _, _) = await PersonAsync(admin, "Bea");
        var room = (await owner.JsonAsync(await owner.PostAsync("/api/rooms", new { title = "Team", userIds = new[] { aId } }))).GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/rooms/{room}/members", new { userIds = new[] { bId } });
        await SendAsync(owner, room, new { content = "@Arena hello all" });
        // The owner cannot just leave.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await owner.Http.DeleteAsync(new Uri($"/api/rooms/{room}/members/{ownerId}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/people/{ownerId}", UriKind.Relative)));
        var shape = await a.JsonAsync(await a.GetAsync($"/api/chat/conversations/{room}"));
        Assert.Equal("owner", shape.GetProperty("room").GetProperty("role").GetString());
        // What they wrote stays, nobody's now (the page says "Former member").
        Assert.Equal(JsonValueKind.Null, shape.GetProperty("messages")[0].GetProperty("author").ValueKind);
        await StatusAssert.Is(HttpStatusCode.NoContent, await a.Http.PutAsJsonAsync(new Uri($"/api/rooms/{room}/owner", UriKind.Relative), new { userId = bId }));
        Assert.Equal("member", (await a.JsonAsync(await a.GetAsync($"/api/chat/conversations/{room}"))).GetProperty("room").GetProperty("role").GetString());
    }
}
