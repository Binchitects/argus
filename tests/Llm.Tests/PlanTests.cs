using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Plans;
using Llm.Core.Plans;

namespace Llm.Tests;

/// <summary>Plans: made and kept by the chat's Plans tool, read and moved along on the page; a room's are its people's.</summary>
[Collection(nameof(AppCollection))]
public sealed class PlanTests(AppFixture app)
{
    private async Task<(Guid Id, TestBrowser Browser)> PersonAsync(TestBrowser admin)
    {
        var user = "pl" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = user, email = $"{user}@example.test" }));
        return (made.GetProperty("id").GetGuid(), await new TestBrowser(app.Factory).SignedInAsync(user, made.GetProperty("password").GetString()!));
    }

    private static async Task<JsonElement> CallAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });
        var events = (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement).ToList();
        return events.Single(e => e.GetProperty("type").GetString() == "tool_result");
    }

    [Fact]
    public async Task The_chat_makes_a_plan_keeps_it_up_to_date_and_its_room_moves_it_along()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, owner) = await PersonAsync(admin);
        var (otherId, other) = await PersonAsync(admin);
        var (_, outsider) = await PersonAsync(admin);
        var room = (await owner.JsonAsync(await owner.PostAsync("/api/rooms", new { title = "Launch", userIds = new[] { otherId } }))).GetProperty("id").GetGuid();
        await owner.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{room}", UriKind.Relative), new { tools = new[] { "plans" } });

        var made = await CallAsync(owner, room, """@Arena plan it [call plan_make {"title": "Launch", "goal": "Live by December", "steps": [{"title": "Design", "owner": "Ann", "start": "2026-11-01", "due": "2026-11-10"}, {"title": "Build", "owner": "Ben", "start": "2026-11-05", "due": "2026-11-25", "depends_on": [1]}]}]""");
        Assert.False(made.GetProperty("isError").GetBoolean(), made.GetProperty("text").GetString());
        var plan = JsonDocument.Parse(made.GetProperty("text").GetString()!).RootElement;
        var id = plan.GetProperty("id").GetGuid();
        Assert.Equal(["Design", "Build"], plan.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("title").GetString()));
        // Build starts before Design, which it waits for, is due.
        Assert.Contains(plan.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("Step 2 starts 2026-11-05, before step 1", StringComparison.Ordinal));

        // The room's people see it and move it along; nobody else does.
        var seen = await other.JsonAsync(await other.GetAsync($"/api/plans/{id}"));
        Assert.Equal("Live by December", seen.GetProperty("goal").GetString());
        await StatusAssert.Is(HttpStatusCode.OK, await other.Http.PatchAsJsonAsync(new Uri($"/api/plans/{id}/steps/1", UriKind.Relative), new { status = "done" }));
        await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.GetAsync($"/api/plans/{id}"));
        Assert.Contains((await other.JsonAsync(await other.GetAsync("/api/plans"))).EnumerateArray(), p => p.GetProperty("id").GetGuid() == id && p.GetProperty("done").GetInt32() == 1);

        // The chat reads it as people left it, and changes it; a circle is refused.
        var shown = JsonDocument.Parse((await CallAsync(owner, room, """@Arena where are we [call plan_show {}]""")).GetProperty("text").GetString()!).RootElement;
        Assert.Equal("done", shown.GetProperty("steps")[0].GetProperty("status").GetString());
        var circle = await CallAsync(owner, room, """@Arena fix [call plan_change {"steps": [{"number": 1, "depends_on": [2]}]}]""");
        Assert.True(circle.GetProperty("isError").GetBoolean());
        Assert.Contains("in a circle", circle.GetProperty("text").GetString(), StringComparison.Ordinal);
        // Refused, nothing of it is kept.
        Assert.Empty((await owner.JsonAsync(await owner.GetAsync($"/api/plans/{id}"))).GetProperty("steps")[0].GetProperty("dependsOn").EnumerateArray());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await other.Http.PatchAsJsonAsync(new Uri($"/api/plans/{id}/steps/2", UriKind.Relative), new { due = "2026-10-01" }));

        // A step added to the plan as it is kept, on the board and by the chat.
        var added = await other.PostAsync($"/api/plans/{id}/steps", new { title = "Launch party" });
        await StatusAssert.Is(HttpStatusCode.OK, added);
        Assert.Equal([1, 2, 3], (await other.JsonAsync(added)).GetProperty("steps").EnumerateArray().Select(x => x.GetProperty("number").GetInt32()));
        var more = await CallAsync(owner, room, """@Arena add it [call plan_change {"steps": [{"title": "Press release", "check": "Published on the site", "depends_on": [3]}]}]""");
        Assert.False(more.GetProperty("isError").GetBoolean(), more.GetProperty("text").GetString());
        var four = (await owner.JsonAsync(await owner.GetAsync($"/api/plans/{id}"))).GetProperty("steps")[3];
        Assert.Equal(("Press release", "Published on the site"), (four.GetProperty("title").GetString(), four.GetProperty("check").GetString()));
    }

    [Fact]
    public async Task The_chat_proposes_a_plan_of_its_own_work_and_the_person_approves_it_changes_a_step_or_rejects_it()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        var (_, owner) = await PersonAsync(admin);
        var (_, outsider) = await PersonAsync(admin);
        var chat = (await owner.JsonAsync(await owner.PostAsync("/api/chat/conversations", new { tools = new[] { "plans" } }))).GetProperty("id").GetGuid();
        var made = await CallAsync(owner, chat, """research it [call plan_make {"title": "Market study", "propose": true, "steps": [{"title": "Find the competitors", "check": "Five named, with prices"}, {"title": "Write the summary"}]}]""");
        Assert.False(made.GetProperty("isError").GetBoolean(), made.GetProperty("text").GetString());
        var id = JsonDocument.Parse(made.GetProperty("text").GetString()!).RootElement.GetProperty("id").GetGuid();
        Assert.Equal("proposed", (await owner.JsonAsync(await owner.GetAsync($"/api/plans/{id}"))).GetProperty("review").GetString());

        // Someone who may not open the chat reviews nothing; a change needs the step and what.
        await StatusAssert.Is(HttpStatusCode.NotFound, await outsider.PostAsync($"/api/plans/{id}/review", new { action = "approve" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await owner.PostAsync($"/api/plans/{id}/review", new { action = "change", step = 1 }));
        var change = await owner.JsonAsync(await owner.PostAsync($"/api/plans/{id}/review", new { action = "change", step = 1, text = "only those selling in Europe" }));
        Assert.Equal("Change step 1 of the plan (Find the competitors): only those selling in Europe. Then propose the plan again.", change.GetProperty("message").GetString());

        var approved = await owner.JsonAsync(await owner.PostAsync($"/api/plans/{id}/review", new { action = "approve" }));
        Assert.StartsWith("I approve the plan \"Market study\". Carry it out now, step by step", approved.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal("approved", (await owner.JsonAsync(await owner.GetAsync($"/api/plans/{id}"))).GetProperty("review").GetString());
        await StatusAssert.Is(HttpStatusCode.Conflict, await owner.PostAsync($"/api/plans/{id}/review", new { action = "approve" }));

        // Rejected, a proposed plan goes.
        var other = JsonDocument.Parse((await CallAsync(owner, chat, """another [call plan_make {"title": "Draft", "propose": true, "steps": [{"title": "Outline"}]}]""")).GetProperty("text").GetString()!).RootElement.GetProperty("id").GetGuid();
        var rejected = await owner.JsonAsync(await owner.PostAsync($"/api/plans/{other}/review", new { action = "reject", text = "not now" }));
        Assert.Equal("I reject the plan \"Draft\": not now. Do not carry it out.", rejected.GetProperty("message").GetString());
        await StatusAssert.Is(HttpStatusCode.NotFound, await owner.GetAsync($"/api/plans/{other}"));
    }

    [Fact]
    public void Steps_are_numbered_checked_and_removed_with_what_waited_for_them()
    {
        var plan = new Plan { Title = "T" };
        PlanBook.Apply(plan, [new StepChange(Title: "A"), new StepChange(Title: "B", DependsOn: [1]), new StepChange(Title: "C", DependsOn: [1, 2])]);
        Assert.Equal([1, 2, 3], plan.Steps.Select(s => s.Number));
        PlanBook.Apply(plan, [new StepChange(Number: 1, Remove: true)]);
        Assert.Equal([2], plan.Steps.Single(s => s.Number == 3).DependsOn);
        Assert.Throws<PlanException>(() => PlanBook.Apply(plan, [new StepChange(Number: 2, Start: "next week")]));
        Assert.Throws<PlanException>(() => PlanBook.Apply(plan, [new StepChange(Number: 2, DependsOn: [9])]));
        Assert.Throws<PlanException>(() => PlanBook.Apply(plan, [new StepChange(Title: "")]));
    }
}
