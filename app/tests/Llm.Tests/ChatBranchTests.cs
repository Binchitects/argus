using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Gateway;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>Branches (edit, regenerate, switching), a chat's own settings, models, and pictures.</summary>
[Collection(nameof(AppCollection))]
public sealed class ChatBranchTests(AppFixture app)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4];

    private static async Task<(TestBrowser Browser, string Email)> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "b" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), $"{name}@example.test");
    }

    private static async Task<Guid> NewChatAsync(TestBrowser b, object body)
    {
        var res = await b.PostAsync("/api/chat/conversations", body);
        await StatusAssert.Is(HttpStatusCode.Created, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    private static async Task<List<JsonElement>> PostStreamAsync(TestBrowser b, string path, object body)
    {
        var res = await b.PostAsync(path, body);
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    private static async Task<JsonElement> ChatAsync(TestBrowser b, Guid id) => await b.JsonAsync(await b.GetAsync($"/api/chat/conversations/{id}"));

    private static List<JsonElement> Messages(JsonElement chat) => [.. chat.GetProperty("messages").EnumerateArray()];

    private (JsonElement Body, string[] Roles) LastRequest(string email)
    {
        var body = app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == email).Body;
        return (JsonDocument.Parse(body.ToJsonString()).RootElement, [.. body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>())]);
    }

    [Fact]
    public async Task Editing_a_question_makes_a_branch_and_both_stay()
    {
        var (b, email) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b, new { useArgus = false });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "first" });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "second" });
        var before = Messages(await ChatAsync(b, id));
        var second = before.Single(m => m.GetProperty("content").GetString() == "second");

        // The edit goes where "second" was: after the first answer.
        var events = await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "second, edited", parentId = second.GetProperty("parentId").GetGuid() });
        Assert.Equal(second.GetProperty("parentId").GetGuid(), events[0].GetProperty("parentId").GetGuid());
        var (body, roles) = LastRequest(email);
        Assert.Equal(["system", "user", "assistant", "user"], roles);
        Assert.Equal("second, edited", body.GetProperty("messages")[3].GetProperty("content").GetString());

        var chat = await ChatAsync(b, id);
        Assert.Equal(6, Messages(chat).Count); // both branches are kept
        var edited = Messages(chat).Single(m => m.GetProperty("content").GetString() == "second, edited");
        Assert.Equal(second.GetProperty("parentId").GetGuid(), edited.GetProperty("parentId").GetGuid());

        // Back to the old branch: its newest line is on screen, and the next question builds on it.
        var leaf = await b.JsonAsync(await b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{id}/leaf", UriKind.Relative), new { messageId = second.GetProperty("id").GetGuid() }));
        var oldAnswer = Messages(chat).Single(m => m.GetProperty("parentId").ValueKind != JsonValueKind.Null && m.GetProperty("parentId").GetGuid() == second.GetProperty("id").GetGuid());
        Assert.Equal(oldAnswer.GetProperty("id").GetGuid(), leaf.GetProperty("currentLeafId").GetGuid());
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "third" });
        Assert.Contains("second", LastRequest(email).Body.GetProperty("messages")[3].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(LastRequest(email).Body.GetProperty("messages").EnumerateArray(), m => m.GetProperty("content").GetString() == "second, edited");
    }

    [Fact]
    public async Task Editing_the_first_question_starts_a_new_first_question()
    {
        var (b, email) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b, new { useArgus = false });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "original" });
        var events = await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "rewritten", root = true });
        Assert.Equal(JsonValueKind.Null, events[0].GetProperty("parentId").ValueKind);
        Assert.Equal(["system", "user"], LastRequest(email).Roles);
    }

    [Fact]
    public async Task A_message_from_another_chat_cannot_be_a_parent_or_a_branch()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var one = await NewChatAsync(b, new { useArgus = false });
        var two = await NewChatAsync(b, new { useArgus = false });
        await PostStreamAsync(b, $"/api/chat/conversations/{one}/messages", new { content = "hello" });
        var foreign = Messages(await ChatAsync(b, one))[0].GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync($"/api/chat/conversations/{two}/messages", new { content = "x", parentId = foreign }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.Http.PutAsJsonAsync(new Uri($"/api/chat/conversations/{two}/leaf", UriKind.Relative), new { messageId = foreign }));
    }

    [Fact]
    public async Task A_chats_own_instructions_and_parameters_reach_the_model()
    {
        var (b, email) = await PersonAsync(app.Factory);
        var id = await NewChatAsync(b, new { useArgus = false, systemPrompt = "Answer in French.", temperature = 0.3, topP = 0.9, maxTokens = 500 });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "hi" });
        var body = LastRequest(email).Body;
        Assert.Equal(0.3, body.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, body.GetProperty("top_p").GetDouble());
        Assert.Equal(500, body.GetProperty("max_tokens").GetInt32());
        Assert.EndsWith("Answer in French.", body.GetProperty("messages")[0].GetProperty("content").GetString(), StringComparison.Ordinal);

        // Negative clears a number; the rest stays.
        await StatusAssert.Is(HttpStatusCode.NoContent, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative), new { temperature = -1 }));
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "again" });
        Assert.False(LastRequest(email).Body.TryGetProperty("temperature", out _));
        Assert.Equal(500, LastRequest(email).Body.GetProperty("max_tokens").GetInt32());

        foreach (var bad in new object[] { new { temperature = 3.0 }, new { topP = 1.5 }, new { maxTokens = 999_999 }, new { model = "no-such-model" } })
        {
            await StatusAssert.Is(HttpStatusCode.BadRequest, await b.Http.PatchAsJsonAsync(new Uri($"/api/chat/conversations/{id}", UriKind.Relative), bad));
        }
    }

    [Fact]
    public async Task Config_lists_the_models_with_what_they_can_do_and_their_prices()
    {
        var (b, _) = await PersonAsync(app.Factory);
        var config = await b.JsonAsync(await b.GetAsync("/api/chat/config"));
        var model = config.GetProperty("models")[0];
        Assert.Equal("Qwen3.8-Flash-Next", model.GetProperty("name").GetString());
        Assert.False(model.GetProperty("vision").GetBoolean());
        Assert.Equal(0.8m, model.GetProperty("prices").GetProperty("output").GetDecimal());
        Assert.Contains("image/png", config.GetProperty("imageTypes").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Pictures_go_to_a_model_that_can_see_and_by_name_to_one_that_cannot()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("Seeing-Model", 32768, 4096, Vision: true, Tools: true, Thinking: false, null, null, null));
        await using var f = app.Create(app.ConnectionStringFor("vision_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var (b, email) = await PersonAsync(f);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Png);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "diagram.png");
        var up = await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form));
        Assert.Equal("image", up.GetProperty("kind").GetString());
        Assert.Equal("image/png", up.GetProperty("contentType").GetString());
        var imageId = up.GetProperty("id").GetGuid();

        // Only its owner can fetch it, as the picture it is.
        var content = await b.GetAsync($"/api/chat/attachments/{imageId}/content");
        Assert.Equal("image/png", content.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await content.Content.ReadAsByteArrayAsync());
        var (other, _) = await PersonAsync(f);
        await StatusAssert.Is(HttpStatusCode.NotFound, await other.GetAsync($"/api/chat/attachments/{imageId}/content"));

        var seeing = await NewChatAsync(b, new { useArgus = false, model = "Seeing-Model" });
        await PostStreamAsync(b, $"/api/chat/conversations/{seeing}/messages", new { content = "what is this?", attachments = new[] { imageId } });
        var (body, _) = LastRequest(email);
        Assert.Equal("Seeing-Model", body.GetProperty("model").GetString());
        var parts = body.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", parts[1].GetProperty("image_url").GetProperty("url").GetString(), StringComparison.Ordinal);

        var blind = await NewChatAsync(b, new { useArgus = false });
        var events = await PostStreamAsync(b, $"/api/chat/conversations/{blind}/messages", new { content = "and this?", attachments = new[] { imageId } });
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "notice" && e.GetProperty("kind").GetString() == "no_vision");
        Assert.Contains("diagram.png", LastRequest(email).Body.GetProperty("messages")[1].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_svg_is_never_served_as_an_image()
    {
        var (b, _) = await PersonAsync(app.Factory);
        using var form = new MultipartFormDataContent();
        var file = new StringContent("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        file.Headers.ContentType = new MediaTypeHeaderValue("image/svg+xml");
        form.Add(file, "file", "x.svg");
        var up = await b.JsonAsync(await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form));
        Assert.Equal("text", up.GetProperty("kind").GetString());
        var content = await b.GetAsync($"/api/chat/attachments/{up.GetProperty("id").GetGuid()}/content");
        Assert.Equal("text/plain", content.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Answering_again_with_another_model_and_thinking_uses_them_once()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("Other-Model", 32768, 4096, Vision: false, Tools: true, Thinking: true, null, null, null));
        await using var f = app.Create(app.ConnectionStringFor("retry_" + Guid.NewGuid().ToString("N")[..8]), gateway);
        var (b, email) = await PersonAsync(f);
        var id = await NewChatAsync(b, new { useArgus = false });
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "hi" });
        var events = await PostStreamAsync(b, $"/api/chat/conversations/{id}/regenerate", new { model = "Other-Model", thinking = "off" });
        Assert.Equal("Other-Model", events.First(e => e.GetProperty("type").GetString() == "assistant").GetProperty("model").GetString());
        var body = LastRequest(email).Body;
        Assert.Equal("Other-Model", body.GetProperty("model").GetString());
        Assert.False(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        var answers = Messages(await ChatAsync(b, id)).Where(m => m.GetProperty("role").GetString() == "assistant").Select(m => m.GetProperty("model").GetString()).ToList();
        Assert.Equal(["Qwen3.8-Flash-Next", "Other-Model"], answers);
        // The chat itself keeps its model.
        await PostStreamAsync(b, $"/api/chat/conversations/{id}/messages", new { content = "next" });
        Assert.Equal("Qwen3.8-Flash-Next", LastRequest(email).Body.GetProperty("model").GetString());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await b.PostAsync($"/api/chat/conversations/{id}/regenerate", new { model = "Missing" }));
    }

    [Fact]
    public async Task Chats_from_before_branches_become_one_branch_each()
    {
        var cs = app.ConnectionStringFor("branches_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using (var db = new AppDbContext(options.Options))
        {
            var migrator = db.GetService<IMigrator>();
            var all = db.Database.GetMigrations().ToList();
            var before = all[all.FindIndex(m => m.EndsWith("_ChatBranches", StringComparison.Ordinal)) - 1];
            await migrator.MigrateAsync(before);
            await using (var conn = new NpgsqlConnection(cs))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","DisplayName","Source","IsDisabled","CreatedAt")
                      VALUES ('00000000-0000-0000-0000-0000000000a1','old','OLD','old@example.test','OLD@EXAMPLE.TEST',true,false,false,true,0,'Old',0,false,now());
                    INSERT INTO conversations ("Id","UserId","Title","UseArgus","CreatedAt","UpdatedAt")
                      VALUES ('00000000-0000-0000-0000-0000000000c1','00000000-0000-0000-0000-0000000000a1','Old chat',false,now(),now());
                    INSERT INTO chat_messages ("Id","ConversationId","Sequence","Role","Content","Status","CreatedAt") VALUES
                      ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-0000000000c1',1,'user','q1',0,now()),
                      ('00000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-0000000000c1',2,'assistant','a1',0,now()),
                      ('00000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-0000000000c1',3,'user','q2',0,now()),
                      ('00000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-0000000000c1',4,'assistant','a2',0,now());
                    """, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            await migrator.MigrateAsync();
            var messages = await db.ChatMessages.AsNoTracking().OrderBy(m => m.Sequence).ToListAsync();
            Assert.Null(messages[0].ParentId);
            for (var i = 1; i < messages.Count; i++)
            {
                Assert.Equal(messages[i - 1].Id, messages[i].ParentId);
            }
            var chat = await db.Conversations.AsNoTracking().SingleAsync();
            Assert.Equal(messages[^1].Id, chat.CurrentLeafId);
            // It had Argus off, so it keeps no tools (ChatTools).
            Assert.Equal([], chat.Tools);
        }
    }
}
