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
        await StatusAssert.Is(HttpStatusCode.BadRequest, await other.Http.PatchAsJsonAsync(new Uri($"/api/plans/{id}/steps/2", UriKind.Relative), new { due = "2026-10-01" }));
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
