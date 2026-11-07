using System.Net;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>A session counted to a project in Arena: chosen at the start, with /project or in the IDE, or its repository's.</summary>
public sealed class WorkProjectTests : IDisposable
{
    private readonly FakeGateway _gateway = new();
    private readonly FakeMcp _mcp = new();

    public WorkProjectTests()
    {
        _gateway.Answer = req => Reply.Say($"answer to {FakeGateway.Last(req)}");
        _mcp.Chats.Projects.Add(("p-pay", "Payments"));
        _mcp.Chats.Projects.Add(("p-wiki", "Company wiki"));
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _mcp.Dispose();
    }

    private static List<string> Tags(JsonObject request) =>
        [.. (request["metadata"]?["tags"] as JsonArray ?? []).Select(t => t!.GetValue<string>())];

    [Fact]
    public async Task A_project_chosen_at_the_start_tags_each_request_puts_the_chat_in_it_and_stays_for_the_folder()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "first", "--project", "payments"));
        Assert.Contains("In Payments", h.Err + h.Out);
        Assert.Contains("project:p-pay", Tags(_gateway.Requests[^1]));
        var chat = Assert.Single(_mcp.Chats.All);
        Assert.Equal("p-pay", chat.ProjectId);

        // Carried on, each request names its chat too: the ledger counts it where the chat is.
        Assert.Equal(0, await h.Run("", "-p", "second", "--continue"));
        Assert.Equal(["chat:" + chat.Id, "project:p-pay"], Tags(_gateway.Requests[^1]));

        // A new session in the folder starts in the project chosen there last.
        Assert.Equal(0, await h.Run("", "-p", "another"));
        Assert.Contains("project:p-pay", Tags(_gateway.Requests[^1]));
        Assert.Equal(2, _mcp.Chats.All.Count);
        Assert.All(_mcp.Chats.All, c => Assert.Equal("p-pay", c.ProjectId));
    }

    [Fact]
    public async Task A_session_goes_in_its_repositorys_project_when_none_was_chosen()
    {
        _mcp.Chats.RepositoryProject = "p-wiki";
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "first"));
        Assert.Equal("p-wiki", Assert.Single(_mcp.Chats.All).ProjectId);
        Assert.Contains("In the project Company wiki", h.Err + h.Out);

        Assert.Equal(0, await h.Run("", "-p", "second", "--continue"));
        Assert.Contains("project:p-wiki", Tags(_gateway.Requests[^1]));
    }

    [Fact]
    public async Task Projects_lists_those_the_person_may_add_to_and_project_moves_the_chat_with_the_session()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        var chat = Assert.Single(_mcp.Chats.All);
        Assert.Equal(0, await h.Run("/projects\n/project 2\n/project nothing-like-it\n/project none\n/exit\n", "chat", "--continue"));
        Assert.Contains("Project: none.", h.Out);
        Assert.Contains("1  Payments", h.Out);
        Assert.Contains("2  Company wiki", h.Out);
        Assert.Contains("In Company wiki", h.Out);
        Assert.Contains("You may add to no project named nothing-like-it", h.Out + h.Err);
        Assert.Contains("In no project", h.Out);
        Assert.Equal([(chat.Id, (string?)"p-wiki"), (chat.Id, null)], _mcp.Chats.Moves);
        Assert.Null(chat.ProjectId);
    }

    [Fact]
    public async Task Project_alone_shows_the_sessions_project_its_last_seven_days_and_its_open_goals()
    {
        _mcp.Chats.AddProject("p-shop", "Shop", "owner", ("Checkout", "doing"), ("Old idea", "dropped"), ("Launch", "done"), ("Refunds", "planned"));
        _mcp.Chats.Used["p-shop"] = new JsonObject
        {
            ["requests"] = 120, ["input"] = 40_000, ["cached"] = 2_000, ["output"] = 5_000, ["cost"] = 1.5, ["agentSeconds"] = 3_900, ["activeSeconds"] = 600, ["people"] = 2, ["messages"] = 30,
        };
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("/project\n/project shop\n/project\n/exit\n", "chat"));
        Assert.Contains("Project: none.", h.Out);
        Assert.Contains("Project: Shop (owner)", h.Out);
        Assert.Contains("Last 7 days: $1.50 · 47k tokens · 120 requests · 2 people · 10 min active · 1 h 05 min the models'", h.Out);
        Assert.Contains("Models: qwen $0.25", h.Out);
        Assert.Contains("Goals: 1 of 3 done; open:", h.Out);
        Assert.Contains("◦ Checkout (doing)", h.Out);
        Assert.Contains("◦ Refunds (planned)", h.Out);
        Assert.DoesNotContain("Old idea", h.Out);
        // The report asked for is the last seven days, with the key.
        var report = _mcp.Chats.Relayed.Last(r => r.Path.Contains("/report", StringComparison.Ordinal));
        Assert.StartsWith("/api/projects/p-shop/report?from=", report.Path);
        Assert.Equal("Bearer " + FakeGateway.Key, report.Authorization);
    }

    [Fact]
    public async Task Project_new_makes_one_when_Arena_lets_the_person_and_puts_the_session_in_it()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "hello"));
        var chat = Assert.Single(_mcp.Chats.All);
        Assert.Equal(0, await h.Run("/project new Billing v2\n/projects\n/exit\n", "chat", "--continue"));
        Assert.Contains("Made the project Billing v2 in Arena", h.Out);
        var made = _mcp.Chats.ArenaProjects.Values.Single(p => p.Str("name") == "Billing v2").Str("id");
        Assert.Equal(made, chat.ProjectId);
        Assert.Contains("3  Billing v2 (this session's)  owner, $0.00 this month", h.Out);

        _mcp.Chats.MayCreate = false;
        Assert.Equal(0, await h.Run("/project new Another\n/exit\n", "chat", "--continue"));
        Assert.Contains("Making projects is not among what you may do in Arena", h.Out + h.Err);
        Assert.DoesNotContain(_mcp.Chats.ArenaProjects.Values, p => p.Str("name") == "Another");
    }

    [Fact]
    public async Task An_unknown_project_at_the_start_is_said_and_the_session_goes_on_in_none()
    {
        using var h = new Harness(_gateway, _mcp);
        Assert.Equal(0, await h.Run("", "-p", "first", "--project", "Nope"));
        Assert.Contains("You may add to no project named Nope", h.Err + h.Out);
        Assert.DoesNotContain(Tags(_gateway.Requests[^1]), t => t.StartsWith("project:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_IDE_lists_the_projects_and_chooses_one_for_the_session()
    {
        _mcp.Chats.RepositoryProject = "p-pay";
        using var h = new Harness(_gateway, _mcp);
        await using var web = await WebRun.StartAsync(h);
        Assert.Null((await web.GetJsonAsync("/api/state"))["workProject"]);
        var listed = await web.GetJsonAsync("/api/project");
        Assert.Equal(["Payments", "Company wiki"], listed["projects"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));

        var chose = await web.PostAsync("/api/project", new JsonObject { ["project"] = "p-wiki" });
        Assert.Equal(HttpStatusCode.OK, chose.StatusCode);
        Assert.Equal("Company wiki", (await web.GetJsonAsync("/api/state"))["workProject"]!["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await web.PostAsync("/api/project", new JsonObject { ["project"] = "Nope" })).StatusCode);

        await (await web.SendAsync("hi")).RestAsync();
        Assert.Contains("project:p-wiki", Tags(_gateway.Requests[^1]));
    }
}
