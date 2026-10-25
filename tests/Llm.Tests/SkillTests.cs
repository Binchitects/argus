using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Plugins;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Skills, as Claude Code's: a person's own, shared with groups, the company's, and those a plugin brings; used with /name
/// and its parameters (the model reads its instructions, the transcript shows the skill), or by the model on its own when
/// a question fits one's description.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class SkillTests(AppFixture app)
{
    /// <summary>Its own app and database: plugins and groups must not leak into other tests.</summary>
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("skills_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-skill-tests" });

    private static async Task<(TestBrowser Browser, Guid Id, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "q" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = "Quinn " + name[..4] }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid(), $"{name}@example.test");
    }

    private static async Task<List<JsonElement>> SkillsAsync(TestBrowser b) =>
        [.. (await b.JsonAsync(await b.GetAsync("/api/skills"))).GetProperty("skills").EnumerateArray()];

    private static Task<HttpResponseMessage> PutAsync(TestBrowser b, string id, object body) =>
        b.Http.PutAsJsonAsync(new Uri($"/api/skills/{id}", UriKind.Relative), body);

    private static async Task<Guid> NewChatAsync(TestBrowser b)
    {
        var res = await b.PostAsync("/api/chat/conversations", new { useArgus = false });
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> ChatAsync(TestBrowser b, Guid id) => await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));

    /// <summary>The last request the model was sent for this person.</summary>
    private JsonObject LastRequestFor(string email) => app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body;

    private static string LastUser(JsonObject request) => request["messages"]!.AsArray().Last(m => m!["role"]!.GetValue<string>() == "user")!["content"] switch
    {
        JsonValue v => v.GetValue<string>(),
        JsonArray parts => string.Concat(parts.Select(p => p!["text"]?.GetValue<string>() ?? "")),
        _ => "",
    };

    private static string System(JsonObject request) => request["messages"]!.AsArray().First()!["content"]!.GetValue<string>();

    [Fact]
    public async Task A_skill_is_its_owners_shared_with_their_groups_or_the_companys_and_only_its_owner_changes_it()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (a, aId, _) = await PersonAsync(f);
        var (b, bId, _) = await PersonAsync(f);

        var made = await a.PostAsync("/api/skills", new
        {
            name = "/Review", title = "Review code", description = "Review a file for one concern.",
            instructions = "Review {{file}} for {{ focus }}, then {{file}} again.",
            parameters = new object[] { new { name = "file", description = "The file's path" }, new { name = "focus", required = false, @default = "bugs" } },
        });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var id = (await a.JsonAsync(made)).GetProperty("id").GetString()!;
        var mine = Assert.Single(await SkillsAsync(a));
        Assert.Equal(("review", "Review a file for one concern.", true), (mine.GetProperty("name").GetString(), mine.GetProperty("description").GetString(), mine.GetProperty("autoUse").GetBoolean()));
        var parameters = mine.GetProperty("parameters").EnumerateArray().ToList();
        Assert.Equal(["file", "focus"], parameters.Select(p => p.GetProperty("name").GetString()));
        Assert.Equal(("The file's path", true), (parameters[0].GetProperty("description").GetString(), parameters[0].GetProperty("required").GetBoolean()));
        Assert.Equal((false, "bugs"), (parameters[1].GetProperty("required").GetBoolean(), parameters[1].GetProperty("default").GetString()));
        Assert.Equal("mine", mine.GetProperty("source").GetString());
        Assert.True(mine.GetProperty("canEdit").GetBoolean());
        Assert.Empty(await SkillsAsync(b));

        // Checked: a slash name, instructions, parameters by their names once each, no second /review of theirs, the company is for admins.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await a.PostAsync("/api/skills", new { name = "Not a name", title = "x", instructions = "y" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await a.PostAsync("/api/skills", new { name = "empty", title = "x", instructions = "" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await a.PostAsync("/api/skills", new { name = "bad", title = "x", instructions = "y", parameters = new[] { new { name = "two words" } } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await a.PostAsync("/api/skills", new { name = "twice", title = "x", instructions = "y", parameters = new[] { new { name = "a" }, new { name = "a" } } }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await a.PostAsync("/api/skills", new { name = "review", title = "Again", instructions = "y" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await a.PostAsync("/api/skills", new { name = "all", title = "x", instructions = "y", sharing = "Company" }));

        // Shared with a group: its members use it, only the owner changes it.
        var team = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Reviewers" }))).GetProperty("id").GetGuid();
        var outsiders = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Outsiders" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{team}/members", new { userIds = new[] { aId, bId } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await PutAsync(a, id, new { sharing = "Groups", groups = new[] { outsiders } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await PutAsync(a, id, new { sharing = "Groups" }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await PutAsync(a, id, new { sharing = "Groups", groups = new[] { team } }));
        var shared = Assert.Single(await SkillsAsync(b));
        Assert.Equal("group", shared.GetProperty("source").GetString());
        Assert.False(shared.GetProperty("canEdit").GetBoolean());
        Assert.Equal("Reviewers", shared.GetProperty("groups").EnumerateArray().Single().GetProperty("name").GetString());
        Assert.StartsWith("Quinn", shared.GetProperty("from").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(b, id, new { title = "Mine now" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.Http.DeleteAsync(new Uri($"/api/skills/{id}", UriKind.Relative)));
        Assert.Empty(await SkillsAsync(admin));

        // The company's: an admin makes it, everyone uses it, any admin changes it; audited.
        var company = (await admin.JsonAsync(await admin.PostAsync("/api/skills", new { name = "standup", title = "Stand-up notes", instructions = "Write my stand-up from {{notes}}.", sharing = "Company" })))
            .GetProperty("id").GetString()!;
        Assert.Contains(await SkillsAsync(b), p => p.GetProperty("name").GetString() == "standup" && p.GetProperty("source").GetString() == "company");
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(a, company, new { instructions = "Changed" }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/skills", new { name = "standup", title = "Twice", instructions = "y", sharing = "Company" }));
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"))).EnumerateArray();
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "skill.create" && e.GetProperty("target").GetString() == "/standup");

        // Deleted by its owner: gone for the group.
        await StatusAssert.Is(HttpStatusCode.NoContent, await a.Http.DeleteAsync(new Uri($"/api/skills/{id}", UriKind.Relative)));
        Assert.DoesNotContain(await SkillsAsync(b), p => p.GetProperty("name").GetString() == "review");
    }

    [Fact]
    public async Task A_skill_used_with_its_parameters_gives_the_model_its_instructions_and_the_chat_shows_the_skill()
    {
        await using var f = NewApp();
        var (b, _, email) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/skills", new
        {
            name = "standup", title = "Stand-up notes", instructions = "Write {{team}}'s stand-up, {{tone}}, from these notes: $ARGUMENTS",
            parameters = new object[] { new { name = "team" }, new { name = "tone", required = false, @default = "brief" } },
        }));
        var id = await NewChatAsync(b);

        // A required parameter missing, a skill they do not have: refused before anything is sent.
        var missing = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "notes", skill = new { name = "standup" } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, missing);
        Assert.Contains("needs team", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "x", skill = new { name = "nothing" } }));

        var sent = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "shipped the parser; fixing the cache", skill = new { name = "/standup", args = new { team = "Payments" } } });
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        await sent.Content.ReadAsStringAsync();
        var asked = LastUser(LastRequestFor(email));
        Assert.Contains("The person used the skill /standup (Stand-up notes) with team: Payments; tone: brief", asked, StringComparison.Ordinal);
        Assert.Contains("<skill name=\"standup\">\nWrite Payments's stand-up, brief, from these notes: shipped the parser; fixing the cache\n</skill>", asked, StringComparison.Ordinal);
        // Its words were in the instructions ($ARGUMENTS): not again after them.
        Assert.DoesNotContain("Their words", asked, StringComparison.Ordinal);

        // The chat keeps the words and the skill apart: the person sees what they typed and the skill they used.
        var question = (await ChatAsync(b, id)).GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user");
        Assert.Equal("shipped the parser; fixing the cache", question.GetProperty("content").GetString());
        var skill = question.GetProperty("skill");
        Assert.Equal(("standup", "Stand-up notes", "Payments"), (skill.GetProperty("name").GetString(), skill.GetProperty("title").GetString(), skill.GetProperty("args").GetProperty("team").GetString()));

        // A skill alone (no words) is a question too, and the next answer reads it again as it was.
        var alone = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = "", skill = new { name = "standup", args = new { team = "Search", tone = "cheerful" } } });
        Assert.True(alone.IsSuccessStatusCode, await alone.Content.ReadAsStringAsync());
        await alone.Content.ReadAsStringAsync();
        var history = LastRequestFor(email)["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "user").Select(m => m!["content"]!.ToJsonString()).ToList();
        Assert.Contains(history, h => h.Contains("Write Payments", StringComparison.Ordinal));
        Assert.Contains("Write Search's stand-up, cheerful, from these notes: \n</skill>", LastUser(LastRequestFor(email)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_model_uses_a_skill_on_its_own_when_the_question_fits_its_description()
    {
        await using var f = NewApp();
        var (b, _, email) = await PersonAsync(f);
        var (c, _, otherEmail) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/skills", new
        {
            name = "release-notes", title = "Release notes", description = "Write release notes for a version from its changes.",
            instructions = "Write release notes for {{version}} in our house style: what is new first, fixes after. $ARGUMENTS",
        }));
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/skills", new { name = "only-slash", title = "Only by /", description = "Never on its own.", instructions = "x", autoUse = false }));
        await StatusAssert.Is(HttpStatusCode.Created, await b.PostAsync("/api/skills", new { name = "no-description", title = "No description", instructions = "y" }));

        var id = await NewChatAsync(b);
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = """Notes for 2.4 please [call use_skill {"name":"release-notes","parameters":{"version":"2.4"},"request":"for the mobile app"}]""" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        var first = app.Model.Requests.First(r => r.Body["user"]!.GetValue<string>() == email && r.Body["tools"] is not null).Body;
        Assert.Contains("use_skill", first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()));
        var system = System(first);
        Assert.Contains("- release-notes: Write release notes for a version from its changes. (parameters: version)", system, StringComparison.Ordinal);
        Assert.DoesNotContain("only-slash", system, StringComparison.Ordinal);
        Assert.DoesNotContain("no-description", system, StringComparison.Ordinal);

        var tool = (await ChatAsync(b, id)).GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains("Write release notes for 2.4 in our house style: what is new first, fixes after. for the mobile app", tool.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("release-notes", tool.GetProperty("details").GetProperty("skill").GetProperty("name").GetString());

        // Someone with no skills that say when they are for: no use_skill.
        var other = await NewChatAsync(c);
        var plain = await c.PostAsync($"/api/chat/conversations/{other}/messages", new { content = "hello" });
        await plain.Content.ReadAsStringAsync();
        Assert.DoesNotContain("use_skill", (LastRequestFor(otherEmail)["tools"]?.AsArray() ?? []).Select(t => t!["function"]!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_plugin_brings_its_skills_to_whoever_may_use_it_and_takes_them_when_removed()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var preview = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/preview", new { name = "gitlab-issues" }));
        Assert.Equal("triage", preview.GetProperty("skills").EnumerateArray().Single().GetProperty("name").GetString());
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install",
            new { accept = true, name = "gitlab-issues", settings = new { gitlab_url = "https://gitlab.test", client_id = "app-id", client_secret = "app-secret" } }));
        var toolId = made.GetProperty("toolId").GetString()!;

        var (b, _, _) = await PersonAsync(f);
        var triage = Assert.Single(await SkillsAsync(b));
        Assert.Equal(("triage", "plugin", "GitLab issues"), (triage.GetProperty("name").GetString(), triage.GetProperty("source").GetString(), triage.GetProperty("from").GetString()));
        Assert.StartsWith("Sort a GitLab issue", triage.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal(["issue", "project"], triage.GetProperty("parameters").EnumerateArray().Select(v => v.GetProperty("name").GetString()));
        Assert.Equal("The issue's number, like 42", triage.GetProperty("parameters")[0].GetProperty("description").GetString());
        Assert.False(triage.GetProperty("canEdit").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(admin, triage.GetProperty("id").GetString()!, new { title = "Changed" }));

        // For admins only: the person no longer sees it.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/tools/{toolId}", UriKind.Relative),
            new { enabled = true, audience = "Admins", onByDefault = true, askFirst = false }));
        Assert.Empty(await SkillsAsync(b));
        Assert.Single(await SkillsAsync(admin));

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/plugins/{made.GetProperty("id").GetGuid()}", UriKind.Relative)));
        Assert.Empty(await SkillsAsync(admin));
    }

    [Fact]
    public void A_plugins_skill_is_its_SKILL_md_its_front_matter_giving_a_title_and_parameters()
    {
        var s = SkillLibrary.FromSkill("skills/review", "---\r\nname: review\ntitle: Review a file\nparameters: [{ name: file, description: The path }]\n---\nReview {{file}}.\n", "review", "Reviews one file.")!;
        Assert.Equal(("review", "Review a file", "Reviews one file.", "Review {{file}}."), (s.Name, s.Title, s.Description, s.Instructions));
        Assert.Equal([new SkillParameter("file", "The path")], s.Parameters);
        // No front matter: its name for a title; a Code Arena skill whose name is no slash name, or with nothing to say, is not the chat's.
        Assert.Equal("plain", SkillLibrary.FromSkill("skills/plain", "Do the plain thing.", "plain", "Plain.")!.Title);
        Assert.Null(SkillLibrary.FromSkill("skills/long", "Text", new string('a', 50), "Long."));
        Assert.Null(SkillLibrary.FromSkill("skills/empty", "---\nname: empty\n---\n", "empty", "Empty."));
        Assert.Throws<PluginCatalog.PluginException>(() => SkillLibrary.FromSkill("skills/bad", "---\nparameters: [{ name: two words }]\n---\nText", "bad", "Bad."));
        Assert.Throws<PluginManifest.ManifestException>(() => PluginManifest.Parse("name: escape\ntools: { mcp: https://x.test/mcp }\nskills: [../secret]"));
        Assert.Equal(["a", "b_2"], SkillLibrary.Variables("{{a}} {{ b_2 }} {{a}} {{ 9 }} {single}"));
        // Used: a default for one not given, a required one missing named, the words where $ARGUMENTS is.
        var skill = new Llm.Core.Chat.Skill { Name = "s", Title = "S", Instructions = "For {{who}} ({{tone}}): $ARGUMENTS", Parameters = SkillLibrary.Write([new("who"), new("tone", Required: false, Default: "short")]) };
        Assert.Equal("/s needs who.", SkillLibrary.Use(skill, null, "x").Problem);
        var call = SkillLibrary.Use(skill, new Dictionary<string, string> { ["who"] = "Ana" }, "the plan").Call!;
        Assert.Equal(("For Ana (short): the plan", true), (call.Instructions, call.TookWords));
        var kept = SkillLibrary.Kept(SkillLibrary.Keep(call))!;
        Assert.Equal((call.Name, call.Title, call.Instructions, call.TookWords), (kept.Name, kept.Title, kept.Instructions, kept.TookWords));
        Assert.Equal(call.Args, kept.Args);
    }

    [Fact]
    public async Task Arenas_own_skills_are_everyones_an_admin_turns_one_off_and_the_companys_of_the_same_name_wins()
    {
        await using var f = app.Create(app.ConnectionStringFor("skills_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-skill-tests", ["Skills:BuiltIn"] = "true" });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (b, _, email) = await PersonAsync(f);
        var all = await SkillsAsync(b);
        Assert.Equal(Llm.Api.Chat.BuiltInSkills.All.Count, all.Count);
        Assert.True(all.Count >= 20, $"{all.Count} built in");
        var review = all.Single(s => s.GetProperty("name").GetString() == "code-review");
        Assert.Equal(("builtin", false, true), (review.GetProperty("source").GetString(), review.GetProperty("canEdit").GetBoolean(), review.GetProperty("autoUse").GetBoolean()));
        var what = review.GetProperty("parameters")[0];
        Assert.Equal(("what", false, "the code or change in this conversation"), (what.GetProperty("name").GetString(), what.GetProperty("required").GetBoolean(), what.GetProperty("default").GetString()));
        // Used as any skill: its instructions, filled in.
        var chat = await NewChatAsync(b);
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "", skill = new { name = "code-review", args = new { what = "src/parser.cs" } } });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Contains("Review src/parser.cs, with correctness first", LastUser(LastRequestFor(email)), StringComparison.Ordinal);

        // Nobody edits Arena's own: the company makes its own of the same name, which wins.
        var id = review.GetProperty("id").GetString()!;
        await StatusAssert.Is(HttpStatusCode.Forbidden, await PutAsync(admin, id, new { name = "code-review", title = "Mine", instructions = "x", sharing = "Company" }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await admin.Http.DeleteAsync(new Uri($"/api/skills/{id}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/skills", new { name = "code-review", title = "Our review", instructions = "Review {{what}} our way.", sharing = "Company" }));
        res = await b.PostAsync($"/api/chat/conversations/{await NewChatAsync(b)}/messages", new { content = "", skill = new { name = "code-review", args = new { what = "app.ts" } } });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Contains("Review app.ts our way.", LastUser(LastRequestFor(email)), StringComparison.Ordinal);

        // Turned off by an admin: gone for everyone (Code Arena hears so too), still listed for admins to turn on.
        var debug = all.Single(s => s.GetProperty("name").GetString() == "debug").GetProperty("id").GetString()!;
        await StatusAssert.Is(HttpStatusCode.Forbidden, await b.Http.PutAsJsonAsync(new Uri($"/api/skills/{debug}/off", UriKind.Relative), new { off = true }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/skills/{debug}/off", UriKind.Relative), new { off = true }));
        Assert.DoesNotContain(await SkillsAsync(b), s => s.GetProperty("name").GetString() == "debug");
        Assert.True((await SkillsAsync(admin)).Single(s => s.GetProperty("name").GetString() == "debug").GetProperty("off").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync($"/api/chat/conversations/{await NewChatAsync(b)}/messages", new { content = "x", skill = new { name = "debug" } }));

        // Written again at the next start: still once each, still off.
        await f.Services.GetRequiredService<Llm.Api.Chat.BuiltInSkills>().SyncAsync(CancellationToken.None);
        Assert.True((await SkillsAsync(admin)).Single(s => s.GetProperty("name").GetString() == "debug").GetProperty("off").GetBoolean());
        Assert.Single(await SkillsAsync(admin), s => s.GetProperty("name").GetString() == "summarize");
    }
}
