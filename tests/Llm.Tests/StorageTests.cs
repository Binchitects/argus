using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Gateway;
using Llm.Api.Notifications;
using Llm.Api.Storage;
using Llm.Core.Chat;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Admin → Storage: what takes room, the chat's files listed and deleted, legal hold, the clean-ups, rooms per person, and the disk alert.</summary>
[Collection(nameof(AppCollection))]
public sealed class StorageTests(AppFixture app)
{
    private sealed record Setup(WebApplicationFactory<Program> App, MovableClock Clock, string Root) : IAsyncDisposable
    {
        public string Library => Path.Combine(Root, "library");
        public string Backups => Path.Combine(Root, "backups");
        public string Sandbox => Path.Combine(Root, "sandbox");

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>An app on its own database, with a clock to move and folders of its own (none made yet).</summary>
    private Setup NewApp(Dictionary<string, string?>? settings = null, FakeGateway? gateway = null, Action<IServiceCollection>? services = null)
    {
        var root = Directory.CreateTempSubdirectory("storage-").FullName;
        var all = new Dictionary<string, string?>
        {
            ["Engine:LibraryDir"] = Path.Combine(root, "library"),
            ["Storage:BackupDir"] = Path.Combine(root, "backups"),
            ["Storage:DockerDir"] = Path.Combine(root, "docker"),
            ["Sandbox:Dir"] = Path.Combine(root, "sandbox"),
        };
        foreach (var (k, v) in settings ?? [])
        {
            all[k] = v;
        }
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var f = app.Create(app.ConnectionStringFor("st_" + Guid.NewGuid().ToString("N")[..8]), gateway ?? new FakeGateway(), all, s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            services?.Invoke(s);
        });
        return new Setup(f, clock, root);
    }

    private static async Task<(TestBrowser Browser, Guid Id, string Name)> PersonAsync(WebApplicationFactory<Program> f, TestBrowser admin)
    {
        var name = "st" + Guid.NewGuid().ToString("N")[..8];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        var b = await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
        return (b, made.GetProperty("id").GetGuid(), name);
    }

    private static async Task<Guid> ChatAsync(TestBrowser b, string text, Guid[]? attachments = null)
    {
        var id = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text, attachments });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        await res.Content.ReadAsStringAsync();
        return id;
    }

    private static async Task<HttpResponseMessage> UploadAsync(TestBrowser b, string name, byte[] bytes, string type)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue(type);
        form.Add(part, "file", name);
        return await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form);
    }

    private static async Task<Guid> UploadTextAsync(TestBrowser b, string name, string text)
    {
        var res = await UploadAsync(b, name, Encoding.UTF8.GetBytes(text), "text/plain");
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    /// <summary>A PNG of about <paramref name="bytes"/> bytes: its signature, then nothing.</summary>
    private static byte[] Png(int bytes)
    {
        var png = new byte[bytes];
        FakeModel.Png.AsSpan(0, 8).CopyTo(png);
        return png;
    }

    /// <summary>A file a tool made in a chat, as the chat keeps one: its row, and the tool's message naming it.</summary>
    private static async Task<Guid> MadeByToolAsync(WebApplicationFactory<Program> f, Guid userId, Guid chat, string tool, string kind, int bytes)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var file = new ChatAttachment
        {
            UserId = userId, FileName = tool + (kind == "image" ? ".png" : ".webm"), ContentType = kind == "image" ? "image/png" : "video/webm",
            Size = bytes, Kind = kind, Data = new byte[bytes], Text = "",
        };
        db.ChatAttachments.Add(file);
        var next = await db.ChatMessages.Where(m => m.ConversationId == chat).MaxAsync(m => m.Sequence) + 1;
        db.ChatMessages.Add(new ChatMessage
        {
            ConversationId = chat, Role = "tool", Sequence = next, ToolName = tool, ToolCallId = "call-" + next, Content = "{}",
            AttachmentsJson = JsonSerializer.Serialize(new[] { file.Id }),
        });
        await db.SaveChangesAsync();
        return file.Id;
    }

    /// <summary>Every file made that much earlier (moving the clock would end the sessions).</summary>
    private static async Task OlderAsync(WebApplicationFactory<Program> f, TimeSpan by)
    {
        await using var scope = f.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatAttachments.ExecuteUpdateAsync(x => x.SetProperty(a => a.CreatedAt, a => a.CreatedAt - by));
    }

    private static async Task HoldAsync(TestBrowser admin, Guid person, bool hold) =>
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/people/{person}/legal-hold", UriKind.Relative),
            new { hold, reason = hold ? "Matter 9" : null }));

    private static async Task<List<JsonElement>> AuditAsync(TestBrowser admin, string prefix) =>
        [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=1000"))).EnumerateArray()
            .Where(e => e.GetProperty("action").GetString()!.StartsWith(prefix, StringComparison.Ordinal))];

    private static async Task<JsonElement> FilesAsync(TestBrowser admin, string query = "")
    {
        var res = await admin.GetAsync("/api/admin/storage/files" + query);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return await admin.JsonAsync(res);
    }

    private static List<JsonElement> Rows(JsonElement list) => [.. list.GetProperty("rows").EnumerateArray()];

    [Fact]
    public async Task Files_are_listed_by_whose_where_from_and_which_chat_with_their_sizes_and_only_to_admins()
    {
        await using var s = NewApp();
        var f = s.App;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annId, annName) = await PersonAsync(f, admin);
        var (ben, benId, _) = await PersonAsync(f, admin);

        var notes = await UploadTextAsync(ann, "quarterly-notes.txt", "the quarterly numbers");
        var chat = await ChatAsync(ann, "Summarise my notes", [notes]);
        var picture = await MadeByToolAsync(f, annId, chat, "generate_image", "image", 2000);
        var video = await MadeByToolAsync(f, annId, chat, "generate_video", "video", 3000);
        var draft = await UploadTextAsync(ann, "draft.txt", "never sent");
        var photo = await UploadAsync(ben, "photo.png", Png(1500), "image/png");
        await StatusAssert.Is(HttpStatusCode.OK, photo);

        // Only admins.
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.GetAsync("/api/admin/storage/files"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.GetAsync("/api/admin/storage"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.GetAsync($"/api/admin/storage/files/{notes}/download"));

        var all = await FilesAsync(admin);
        var rows = Rows(all);
        Assert.Equal(5, all.GetProperty("total").GetProperty("count").GetInt64());
        Assert.Equal("the quarterly numbers".Length + 2000 + 3000 + "never sent".Length + 1500, all.GetProperty("total").GetProperty("bytes").GetInt64());
        Assert.False(all.GetProperty("capped").GetBoolean());
        // Largest first.
        Assert.Equal(video, rows[0].GetProperty("id").GetGuid());
        var title = await TitleAsync(f, chat);
        JsonElement Row(Guid id) => rows.Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal(("video", "chat", chat, title), (Row(video).GetProperty("origin").GetString(), Row(video).GetProperty("state").GetString(),
            Row(video).GetProperty("chatId").GetGuid(), Row(video).GetProperty("chatTitle").GetString()));
        Assert.Equal("picture", Row(picture).GetProperty("origin").GetString());
        Assert.Equal(("upload", "chat"), (Row(notes).GetProperty("origin").GetString(), Row(notes).GetProperty("state").GetString()));
        Assert.Equal(("upload", "none"), (Row(draft).GetProperty("origin").GetString(), Row(draft).GetProperty("state").GetString()));
        Assert.Equal(JsonValueKind.Null, Row(draft).GetProperty("chatId").ValueKind);
        Assert.Equal($"{annName}@example.test", Row(notes).GetProperty("email").GetString());

        // Each filter narrows, and the totals follow it.
        Assert.Equal([picture], Rows(await FilesAsync(admin, "?origin=picture")).Select(r => r.GetProperty("id").GetGuid()));
        Assert.Equal(2, Rows(await FilesAsync(admin, "?state=none")).Count);
        Assert.Single(Rows(await FilesAsync(admin, $"?person={benId}")));
        Assert.Equal(2, Rows(await FilesAsync(admin, "?kind=image")).Count);
        Assert.Equal([notes], Rows(await FilesAsync(admin, "?q=QUARTERLY")).Select(r => r.GetProperty("id").GetGuid()));
        var big = await FilesAsync(admin, "?min=1600");
        Assert.Equal(2, big.GetProperty("total").GetProperty("count").GetInt64());
        Assert.Equal(5000, big.GetProperty("total").GetProperty("bytes").GetInt64());
        Assert.Equal(notes, Rows(await FilesAsync(admin, "?sort=old"))[0].GetProperty("id").GetGuid());
        // A filter that is not one is ignored, not an error.
        Assert.Equal(5, Rows(await FilesAsync(admin, "?origin=nonsense&state=%27")).Count);

        // By person, with their room.
        var people = (await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/people"))).GetProperty("people").EnumerateArray().ToList();
        Assert.Equal(annId, people[0].GetProperty("id").GetGuid());
        Assert.Equal("the quarterly numbers".Length + 2000 + 3000 + "never sent".Length, people[0].GetProperty("bytes").GetInt64());
        Assert.Equal(4, people[0].GetProperty("count").GetInt64());

        // The file itself, to save, audited with no name and no content.
        var down = await admin.GetAsync($"/api/admin/storage/files/{notes}/download");
        await StatusAssert.Is(HttpStatusCode.OK, down);
        Assert.Equal("attachment", down.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("the quarterly numbers", await down.Content.ReadAsStringAsync());
        var audit = Assert.Single(await AuditAsync(admin, "storage.download"));
        Assert.Equal(annName, audit.GetProperty("target").GetString());
        Assert.DoesNotContain("quarterly", audit.ToString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync($"/api/admin/storage/files/{Guid.NewGuid()}/download"));
    }

    private static async Task<string> TitleAsync(WebApplicationFactory<Program> f, Guid chat)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Conversations.IgnoreQueryFilters().Where(c => c.Id == chat).Select(c => c.Title).SingleAsync();
    }

    [Fact]
    public async Task Deleting_files_never_takes_those_of_a_person_on_legal_hold_and_is_audited()
    {
        await using var s = NewApp();
        var f = s.App;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, _, annName) = await PersonAsync(f, admin);
        var (ben, benId, benName) = await PersonAsync(f, admin);
        var mine = await UploadTextAsync(ann, "a.txt", "ann's words");
        var chat = await ChatAsync(ann, "Read it", [mine]);
        var held = await UploadTextAsync(ben, "b.txt", "ben's words");
        await HoldAsync(admin, benId, true);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/storage/files/delete", new { ids = Array.Empty<Guid>() }));
        var res = await admin.JsonAsync(await admin.PostAsync("/api/admin/storage/files/delete", new { ids = new[] { mine, held, Guid.NewGuid() } }));
        Assert.Equal(1, res.GetProperty("deleted").GetProperty("count").GetInt64());
        Assert.Equal("ann's words".Length, res.GetProperty("deleted").GetProperty("bytes").GetInt64());
        Assert.Equal(1, res.GetProperty("held").GetProperty("count").GetInt64());

        await StatusAssert.Is(HttpStatusCode.NotFound, await ann.GetAsync($"/api/chat/attachments/{mine}/content"));
        await StatusAssert.Is(HttpStatusCode.OK, await ben.GetAsync($"/api/chat/attachments/{held}/content"));
        // The chat stays, without its file.
        var view = await ann.JsonAsync(await ann.GetAsync($"/api/chat/conversations/{chat}"));
        Assert.DoesNotContain(mine.ToString(), view.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Read it", view.ToString(), StringComparison.Ordinal);

        var audit = await AuditAsync(admin, "storage.delete");
        var row = Assert.Single(audit);
        Assert.Equal(annName, row.GetProperty("target").GetString());
        Assert.Equal("1 file (11 bytes), chosen on the storage page", row.GetProperty("detail").GetString());
        Assert.DoesNotContain(audit, e => e.GetProperty("target").GetString() == benName);
        Assert.True(Rows(await FilesAsync(admin)).Single().GetProperty("held").GetBoolean());
    }

    [Fact]
    public async Task Clean_ups_show_what_would_go_and_the_room_it_frees_then_take_only_that_never_what_a_hold_keeps()
    {
        await using var s = NewApp();
        var f = s.App;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annId, annName) = await PersonAsync(f, admin);
        var (ben, benId, _) = await PersonAsync(f, admin);
        var (_, cyId, cyName) = await PersonAsync(f, admin);

        var unsent = await UploadTextAsync(ann, "unsent.txt", "uploaded, never sent");
        var benUnsent = await UploadTextAsync(ben, "ben-unsent.txt", "kept by the hold");
        var chat = await ChatAsync(ann, "Draw me something");
        var picture = await MadeByToolAsync(f, annId, chat, "generate_image", "image", 4000);
        var benChat = await ChatAsync(ben, "Something Ben deletes");
        var benPicture = await MadeByToolAsync(f, benId, benChat, "generate_image", "image", 5000);
        await HoldAsync(admin, benId, true);
        await StatusAssert.Is(HttpStatusCode.NoContent, await ben.Http.DeleteAsync(new Uri($"/api/chat/conversations/{benChat}", UriKind.Relative)));

        async Task<JsonElement> Plan(string kind, string query = "")
        {
            var res = await admin.GetAsync($"/api/admin/storage/cleanups/{kind}{query}");
            await StatusAssert.Is(HttpStatusCode.OK, res);
            return await admin.JsonAsync(res);
        }
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.GetAsync("/api/admin/storage/cleanups/everything"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync("/api/admin/storage/cleanups/everything", new { }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.GetAsync("/api/admin/storage/cleanups/unused-files"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.PostAsync("/api/admin/storage/cleanups/unused-files", new { }));

        // Files in no chat: none yet (a file nobody uses may be one being written), then Ann's; Ben's is held.
        Assert.Equal(0, (await Plan("unused-files")).GetProperty("count").GetInt64());
        await OlderAsync(f, TimeSpan.FromDays(8));
        var unused = await Plan("unused-files");
        Assert.Equal(1, unused.GetProperty("count").GetInt64());
        Assert.Equal("uploaded, never sent".Length, unused.GetProperty("bytes").GetInt64());
        Assert.Equal(1, unused.GetProperty("held").GetProperty("count").GetInt64());
        Assert.Equal(7, unused.GetProperty("days").GetInt32());
        Assert.Equal(unsent.ToString(), unused.GetProperty("items")[0].GetProperty("id").GetString());
        var ran = await admin.JsonAsync(await admin.PostAsync("/api/admin/storage/cleanups/unused-files", new { }));
        Assert.Equal(1, ran.GetProperty("count").GetInt64());
        await StatusAssert.Is(HttpStatusCode.NotFound, await ann.GetAsync($"/api/chat/attachments/{unsent}/content"));
        await StatusAssert.Is(HttpStatusCode.OK, await ben.GetAsync($"/api/chat/attachments/{benUnsent}/content"));

        // Old generated media: by the age asked; the chat keeps its words.
        Assert.Equal(0, (await Plan("old-media", "?days=30")).GetProperty("count").GetInt64());
        await OlderAsync(f, TimeSpan.FromDays(30));
        var media = await Plan("old-media", "?days=30");
        Assert.Equal(1, media.GetProperty("count").GetInt64());
        Assert.Equal(4000, media.GetProperty("bytes").GetInt64());
        Assert.Equal(1, media.GetProperty("held").GetProperty("count").GetInt64());
        Assert.Contains("keep their words", media.GetProperty("warning").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, (await admin.JsonAsync(await admin.PostAsync("/api/admin/storage/cleanups/old-media", new { days = 30 }))).GetProperty("count").GetInt64());
        await StatusAssert.Is(HttpStatusCode.NotFound, await ann.GetAsync($"/api/chat/attachments/{picture}/content"));
        await StatusAssert.Is(HttpStatusCode.OK, await ann.GetAsync($"/api/chat/conversations/{chat}"));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatAttachments.AnyAsync(a => a.Id == benPicture));
        }

        // Chats deleted under a hold stay while it lasts; one whose hold is over goes (as the sweep would take it).
        var deleted = await Plan("deleted-chats");
        Assert.Equal(0, deleted.GetProperty("count").GetInt64());
        Assert.Equal(1, deleted.GetProperty("held").GetProperty("count").GetInt64());
        Assert.Contains("1 chat deleted by people still on legal hold", deleted.GetProperty("warning").GetString(), StringComparison.Ordinal);
        var cyChat = Guid.Empty;
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var c = new Conversation { UserId = cyId, Title = "Left over", DeletedAt = DateTimeOffset.UtcNow };
            db.Conversations.Add(c);
            await db.SaveChangesAsync();
            cyChat = c.Id;
        }
        var leftover = await Plan("deleted-chats");
        Assert.Equal(1, leftover.GetProperty("count").GetInt64());
        Assert.Equal("1 chat and 0 files", leftover.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(1, (await admin.JsonAsync(await admin.PostAsync("/api/admin/storage/cleanups/deleted-chats", new { }))).GetProperty("count").GetInt64());
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.Conversations.IgnoreQueryFilters().AnyAsync(c => c.Id == cyChat));
            Assert.True(await db.Conversations.IgnoreQueryFilters().AnyAsync(c => c.Id == benChat));
        }

        var audit = await AuditAsync(admin, "storage.cleanup");
        Assert.Contains(audit, e => e.GetProperty("target").GetString() == annName && e.GetProperty("detail").GetString() == "unused-files: 1 file (20 bytes) older than 7 days");
        Assert.Contains(audit, e => e.GetProperty("target").GetString() == annName && e.GetProperty("detail").GetString()!.StartsWith("old-media: 1 file", StringComparison.Ordinal));
        Assert.Contains(audit, e => e.GetProperty("target").GetString() == cyName && e.GetProperty("detail").GetString()!.StartsWith("deleted-chats: 1 chats", StringComparison.Ordinal));
        Assert.DoesNotContain("uploaded, never sent", string.Join(" ", audit), StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_disk_leftovers_and_unused_models_are_shown_then_removed_as_chosen_and_old_backups_only_shown_with_the_command()
    {
        await using var s = NewApp();
        var f = s.App;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // The library: a model of Admin -> Models, one nothing uses, a split one nothing uses, the picture server's, and downloads cut short.
        GgufFile.Language("qwen3", name: "Used").Write(Path.Combine(s.Library, "used", "Used-Q4.gguf"));
        GgufFile.Language("qwen3", name: "Spare").Write(Path.Combine(s.Library, "spare", "Spare-Q4.gguf"));
        GgufFile.Language("qwen3", name: "Split").Write(Path.Combine(s.Library, "split", "Split-00001-of-00002.gguf"));
        new GgufFile().U32("split.no", 1).Tensor("blk.1.attn_q.weight", 4096).Write(Path.Combine(s.Library, "split", "Split-00002-of-00002.gguf"));
        GgufFile.Language("qwen3", name: "Encoder").Write(Path.Combine(s.Library, "image", "flux2-klein-4b", "Qwen3-4B-Q4_K_M.gguf"));
        File.WriteAllBytes(Path.Combine(s.Library, "spare", "Orphan.gguf.part"), new byte[700]);
        Directory.CreateDirectory(Path.Combine(s.Library, "paused"));
        File.WriteAllBytes(Path.Combine(s.Library, "paused", "Paused.gguf.part"), new byte[300]);
        // The sandbox: a job left behind long ago, and one of now.
        var old = Path.Combine(s.Sandbox, "in", "old-job");
        Directory.CreateDirectory(old);
        File.WriteAllBytes(Path.Combine(old, "job.json"), new byte[50]);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));
        Directory.CreateDirectory(Path.Combine(s.Sandbox, "out", "new-job"));
        // Backups: two that ended well, two that failed (the newest is the latest).
        foreach (var (name, result) in new[] { ("2026-09-01_030000", "ok"), ("2026-09-02_030000", "ok"), ("2026-09-03_030000", "FAILED (1 problem(s))"), ("2026-09-04_030000", "FAILED (2 problem(s))") })
        {
            Directory.CreateDirectory(Path.Combine(s.Backups, name, "volumes"));
            File.WriteAllBytes(Path.Combine(s.Backups, name, "postgres.sql.gz"), new byte[1000]);
            File.WriteAllText(Path.Combine(s.Backups, name, "RESULT"), result + "\n");
        }
        File.CreateSymbolicLink(Path.Combine(s.Backups, "latest"), "2026-09-04_030000");
        File.WriteAllText(Path.Combine(s.Backups, "notes.txt"), "the admin's own");
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LocalModels.Add(new LocalModel { Name = "Used", File = "used/Used-Q4.gguf" });
            db.ModelDownloads.Add(new ModelDownload { Repo = "owner/paused", Revision = "abc", Dir = "paused", State = "paused", CreatedBy = "admin",
                Files = [new DownloadFile { Path = "Paused.gguf", Size = 1000 }] });
            db.ModelDownloads.Add(new ModelDownload { Repo = "owner/split", Revision = "abc", Dir = "split", State = "done", CreatedBy = "admin",
                Files = [new DownloadFile { Path = "Split-00001-of-00002.gguf" }, new DownloadFile { Path = "Split-00002-of-00002.gguf" }] });
            await db.SaveChangesAsync();
        }

        // The page: the library by what uses each file, the downloads cut short, the backups.
        var page = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage"));
        var library = page.GetProperty("library").GetProperty("report");
        string Use(string path) => library.GetProperty("files").EnumerateArray().Single(x => x.GetProperty("path").GetString() == path).GetProperty("use").GetString()!;
        Assert.Equal("engine", Use("used/Used-Q4.gguf"));
        Assert.Equal("unused", Use("spare/Spare-Q4.gguf"));
        Assert.Equal("pictures", Use("image/flux2-klein-4b/Qwen3-4B-Q4_K_M.gguf"));
        Assert.Contains("Used", library.GetProperty("files").EnumerateArray().Single(x => x.GetProperty("path").GetString() == "used/Used-Q4.gguf").GetProperty("models")[0].GetString(), StringComparison.Ordinal);
        Assert.Contains(library.GetProperty("partials").EnumerateArray(), p => p.GetProperty("download").GetString() == "owner/paused (paused)");
        var backups = page.GetProperty("backups");
        Assert.Equal("ok", backups.GetProperty("state").GetString());
        Assert.Equal(4, backups.GetProperty("backups").GetArrayLength());
        Assert.True(backups.GetProperty("backups")[0].GetProperty("latest").GetBoolean());
        Assert.Equal("the admin's own".Length, backups.GetProperty("otherBytes").GetInt64());

        // Leftovers on disk: the part no download owns and the old job; not the paused download's part nor the job of now.
        var orphans = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/cleanups/disk-orphans"));
        Assert.Equal(2, orphans.GetProperty("count").GetInt64());
        Assert.Contains(orphans.GetProperty("items").EnumerateArray(), i => i.GetProperty("name").GetString() == "MODELS_DIR/spare/Orphan.gguf.part");
        Assert.Equal(2, (await admin.JsonAsync(await admin.PostAsync("/api/admin/storage/cleanups/disk-orphans", new { }))).GetProperty("count").GetInt64());
        Assert.False(File.Exists(Path.Combine(s.Library, "spare", "Orphan.gguf.part")));
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(Path.Combine(s.Library, "paused", "Paused.gguf.part")));
        Assert.True(Directory.Exists(Path.Combine(s.Sandbox, "out", "new-job")));

        // Backups beyond one: never the latest, nor the newest that ended well. Only shown, with the command that removes them:
        // the app sees them read only (they hold every secret of the stack).
        var plan = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/cleanups/old-backups?keep=1"));
        Assert.Equal(new[] { "2026-09-03_030000", "2026-09-01_030000" }, plan.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()));
        Assert.Equal(2 * 1000 + "FAILED (1 problem(s))\n".Length + "ok\n".Length, plan.GetProperty("bytes").GetInt64());
        Assert.Equal("scripts/backup.sh --prune --keep 1", plan.GetProperty("command").GetString());
        Assert.Equal(JsonValueKind.Null, plan.GetProperty("problem").ValueKind);
        Assert.DoesNotContain("legal hold", plan.GetProperty("warning").GetString(), StringComparison.Ordinal);
        var refused = await admin.PostAsync("/api/admin/storage/cleanups/old-backups", new { keep = 1 });
        await StatusAssert.Is(HttpStatusCode.Conflict, refused);
        Assert.Contains("read only", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("scripts/backup.sh --prune --keep 1", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(new[] { "2026-09-01_030000", "2026-09-02_030000", "2026-09-03_030000", "2026-09-04_030000", "latest", "notes.txt" },
            Directory.EnumerateFileSystemEntries(s.Backups).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        // By default it keeps BACKUP_KEEP (14): nothing goes.
        var kept = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/cleanups/old-backups"));
        Assert.Equal((0, 14, "scripts/backup.sh --prune --keep 14"), (kept.GetProperty("count").GetInt64(), kept.GetProperty("keep").GetInt32(), kept.GetProperty("command").GetString()));
        // Someone on legal hold: backups from before it hold their data, and those are named.
        var (_, heldId, _) = await PersonAsync(f, admin);
        await HoldAsync(admin, heldId, true);
        var held = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/cleanups/old-backups?keep=1"));
        Assert.Contains("1 person is on legal hold", held.GetProperty("warning").GetString(), StringComparison.Ordinal);
        Assert.Contains("2 of these are from before it", held.GetProperty("warning").GetString(), StringComparison.Ordinal);
        Assert.Equal("FAILED (1 problem(s)) · taken before a legal hold began", held.GetProperty("items")[0].GetProperty("note").GetString());
        Assert.Contains((await admin.JsonAsync(await admin.GetAsync("/api/admin/storage"))).GetProperty("rules").EnumerateArray(),
            r => r.GetProperty("rule").GetString()!.Contains("Backups taken before a hold began", StringComparison.Ordinal));

        // Models nothing uses: chosen one by one, with a warning; one in use is refused.
        var models = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/cleanups/unused-models"));
        Assert.Equal(new[] { "spare/Spare-Q4.gguf", "split/Split-00001-of-00002.gguf" }, models.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Order(StringComparer.Ordinal));
        Assert.Contains("downloading it again", models.GetProperty("warning").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/storage/cleanups/unused-models", new { }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/storage/cleanups/unused-models", new { only = new[] { "used/Used-Q4.gguf" } }));
        var gone = await admin.JsonAsync(await admin.PostAsync("/api/admin/storage/cleanups/unused-models", new { only = new[] { "split/Split-00001-of-00002.gguf" } }));
        Assert.Equal(1, gone.GetProperty("count").GetInt64());
        Assert.Empty(Directory.GetFiles(Path.Combine(s.Library, "split")));
        Assert.True(File.Exists(Path.Combine(s.Library, "spare", "Spare-Q4.gguf")));
        Assert.True(File.Exists(Path.Combine(s.Library, "used", "Used-Q4.gguf")));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            // Its finished download would offer to add a model that is gone.
            Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().ModelDownloads.AnyAsync(d => d.Repo == "owner/split"));
        }
        var audit = await AuditAsync(admin, "storage.cleanup");
        Assert.Contains(audit, e => e.GetProperty("target").GetString() == "unused-models" && e.GetProperty("detail").GetString()!.Contains("split/Split-00001-of-00002.gguf", StringComparison.Ordinal));
        Assert.DoesNotContain(audit, e => e.GetProperty("target").GetString() == "old-backups");
        Assert.Contains(audit, e => e.GetProperty("target").GetString() == "disk-orphans");
    }

    [Fact]
    public async Task Without_the_backups_mounted_the_clean_up_says_why_and_does_not_run()
    {
        await using var s = NewApp();
        var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
        var plan = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/cleanups/old-backups"));
        Assert.Contains("not mounted", plan.GetProperty("problem").GetString(), StringComparison.Ordinal);
        var run = await admin.PostAsync("/api/admin/storage/cleanups/old-backups", new { });
        await StatusAssert.Is(HttpStatusCode.Conflict, run);
        Assert.Contains("not mounted", await run.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("missing", (await admin.JsonAsync(await admin.GetAsync("/api/admin/storage"))).GetProperty("backups").GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_backup_is_from_before_a_hold_by_its_start_in_UTC_and_without_one_by_its_name_in_any_zone()
    {
        await using var s = NewApp();
        var f = s.App;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // A host at UTC+03:30: each backup named in its time; MANIFEST says the start in UTC, except in a backup from before that.
        void Backup(string name, string? started)
        {
            Directory.CreateDirectory(Path.Combine(s.Backups, name));
            File.WriteAllText(Path.Combine(s.Backups, name, "RESULT"), "ok\n");
            File.WriteAllText(Path.Combine(s.Backups, name, "MANIFEST"), $"backup: {name}\n{(started is null ? "" : $"started: {started}\n")}host: h\n");
        }
        Backup("2026-10-10_033000", "2026-10-10T00:00:00Z");
        Backup("2026-10-09_170000", null);
        Backup("2026-10-09_150000", null);
        Backup("2026-10-09_073000", "2026-10-09T04:00:00Z");
        Backup("2026-10-09_033000", "2026-10-09T00:00:00Z");
        File.CreateSymbolicLink(Path.Combine(s.Backups, "latest"), "2026-10-10_033000");
        var (_, heldId, _) = await PersonAsync(f, admin);
        await using (var scope = f.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Users.Where(u => u.Id == heldId)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.LegalHoldSince, DateTimeOffset.Parse("2026-10-09T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture)));
        }

        var plan = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/cleanups/old-backups?keep=1"));
        var items = plan.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["2026-10-09_170000", "2026-10-09_150000", "2026-10-09_073000", "2026-10-09_033000"], items.Select(i => i.GetProperty("id").GetString()));
        // 03:30 there began at 00:00 UTC, before the hold; 07:30 after it. Without the start, 15:00 may be 01:00 UTC (at UTC+14),
        // so it counts as before; 17:00 is after 02:00 UTC in every zone.
        Assert.Equal(["ok", "ok · taken before a legal hold began", "ok", "ok · taken before a legal hold began"], items.Select(i => i.GetProperty("note").GetString()));
        Assert.Equal([null, null, "2026-10-09T04:00:00+00:00", "2026-10-09T00:00:00+00:00"],
            items.Select(i => i.GetProperty("at") is { ValueKind: JsonValueKind.String } at ? at.GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture) : null));
        Assert.Contains("2 of these are from before it", plan.GetProperty("warning").GetString(), StringComparison.Ordinal);
        var report = (await admin.JsonAsync(await admin.GetAsync("/api/admin/storage"))).GetProperty("backups").GetProperty("backups");
        Assert.Equal("2026-10-10T00:00:00+00:00", report[0].GetProperty("at").GetDateTimeOffset().ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_persons_room_for_files_refuses_uploads_past_it_and_an_admin_can_give_them_their_own()
    {
        await using var s = NewApp(new() { ["Storage:PersonMegabytes"] = "1" });
        var f = s.App;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annId, annName) = await PersonAsync(f, admin);

        await StatusAssert.Is(HttpStatusCode.OK, await UploadAsync(ann, "one.png", Png(600_000), "image/png"));
        var refused = await UploadAsync(ann, "two.png", Png(600_000), "image/png");
        await StatusAssert.Is(HttpStatusCode.RequestEntityTooLarge, refused);
        var why = await ann.JsonAsync(refused);
        Assert.Equal("quota", why.GetProperty("status").GetString());
        Assert.StartsWith("Your files take 586 KB of your 1 MB: this one (586 KB) does not fit.", why.GetProperty("error").GetString(), StringComparison.Ordinal);
        var mine = (await ann.JsonAsync(await ann.GetAsync("/api/account/data"))).GetProperty("files");
        Assert.Equal(600_000, mine.GetProperty("bytes").GetInt64());
        Assert.Equal(1024 * 1024, mine.GetProperty("limitBytes").GetInt64());

        // The tools say the same to the model, and make nothing.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var quotas = scope.ServiceProvider.GetRequiredService<StorageQuotas>();
            Assert.Null(await quotas.ToolRefusalAsync(annId, "picture"));
            // Up to the room exactly: an upload fits, then the tools have none left.
            await StatusAssert.Is(HttpStatusCode.OK, await UploadAsync(ann, "fill.png", Png(1024 * 1024 - 600_000), "image/png"));
            Assert.StartsWith("No picture was made: the person's files are full. Tell them: Your files take", await quotas.ToolRefusalAsync(annId, "picture"), StringComparison.Ordinal);
        }

        // Their own room: no limit, then more, then the company's again.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/storage/people/{annId}/quota", UriKind.Relative), new { megabytes = -1 }));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ann.Http.PutAsJsonAsync(new Uri($"/api/admin/storage/people/{annId}/quota", UriKind.Relative), new { megabytes = 0 }));
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/storage/people/{annId}/quota", UriKind.Relative), new { megabytes = 0 }));
        await StatusAssert.Is(HttpStatusCode.OK, await UploadAsync(ann, "two.png", Png(600_000), "image/png"));
        Assert.Equal(JsonValueKind.Null, (await ann.JsonAsync(await ann.GetAsync("/api/account/data"))).GetProperty("files").GetProperty("limitBytes").ValueKind);
        var people = (await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/people"))).GetProperty("people").EnumerateArray().ToList();
        Assert.Equal(0, people.Single(p => p.GetProperty("id").GetGuid() == annId).GetProperty("ownMegabytes").GetInt32());
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/storage/people/{annId}/quota", UriKind.Relative), new { megabytes = 5 }));
        Assert.Equal(5 * 1024 * 1024, (await ann.JsonAsync(await ann.GetAsync("/api/account/data"))).GetProperty("files").GetProperty("limitBytes").GetInt64());
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/storage/people/{annId}/quota", UriKind.Relative), new { megabytes = (int?)null }));
        await StatusAssert.Is(HttpStatusCode.RequestEntityTooLarge, await UploadAsync(ann, "three.png", Png(1000), "image/png"));
        Assert.Equal(new[] { "no limit", "5 MB", "the company's room again" },
            (await AuditAsync(admin, "storage.quota")).Where(e => e.GetProperty("target").GetString() == annName).Select(e => e.GetProperty("detail").GetString()).Reverse());

        // Someone with no files yet (Give someone a room): listed once they have a room of their own, before any upload.
        var (bob, bobId, _) = await PersonAsync(f, admin);
        async Task<List<JsonElement>> PeopleAsync() => [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/people"))).GetProperty("people").EnumerateArray()];
        Assert.DoesNotContain(await PeopleAsync(), p => p.GetProperty("id").GetGuid() == bobId);
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri($"/api/admin/storage/people/{bobId}/quota", UriKind.Relative), new { megabytes = 3 }));
        var listed = (await PeopleAsync()).Single(p => p.GetProperty("id").GetGuid() == bobId);
        Assert.Equal((0L, 3), (listed.GetProperty("count").GetInt64(), listed.GetProperty("ownMegabytes").GetInt32()));
        await StatusAssert.Is(HttpStatusCode.OK, await UploadAsync(bob, "big.png", Png(2 * 1024 * 1024), "image/png"));
    }

    [Fact]
    public async Task With_the_room_full_the_picture_tool_makes_nothing_and_says_why()
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        await using var s = NewApp(new() { ["Storage:PersonMegabytes"] = "1" }, gateway);
        var f = s.App;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, _, _) = await PersonAsync(f, admin);
        await StatusAssert.Is(HttpStatusCode.OK, await UploadAsync(ann, "full.png", Png(1024 * 1024), "image/png"));

        var chat = (await ann.JsonAsync(await ann.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();
        var before = app.Model.ImageRequests.Count;
        var res = await ann.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = """Draw: [call generate_image {"prompt":"A red fox in the snow"}]""" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var result = (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)
            .Single(e => e.GetProperty("type").GetString() == "tool_result");
        Assert.StartsWith("No picture was made: the person's files are full.", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        // Nothing was asked of the image model, and nothing was kept.
        Assert.Equal(before, app.Model.ImageRequests.Count);
        Assert.Equal(1, (await FilesAsync(admin)).GetProperty("total").GetProperty("count").GetInt64());
    }

    /// <summary>The video server, which must not be asked for a clip: what it was asked, and nothing made.</summary>
    private sealed class NoVideo : HttpMessageHandler
    {
        public ConcurrentQueue<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Enqueue(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") });
        }
    }

    [Theory]
    [InlineData("video", "generate_video", """{"prompt":"A fox running","seconds":2}""", "No video was made")]
    [InlineData("speech", "speak", """{"text":"Hello there"}""", "No sound file was made")]
    public async Task With_the_room_full_the_video_and_speech_tools_make_nothing_and_say_why(string tool, string function, string arguments, string refusal)
    {
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel(Llm.Api.Models.MediaModels.TextToSpeech, null, null, false, false, false, null, null, null, Mode: "audio_speech"));
        var video = new NoVideo();
        await using var s = NewApp(new() { ["Storage:PersonMegabytes"] = "1", ["Modules:videogen"] = "true" }, gateway, services => services
            .AddHttpClient(Llm.Api.Chat.Tools.VideoTool.Client).ConfigurePrimaryHttpMessageHandler(() => video)
            .Services.AddHttpClient(Llm.Api.Models.MediaControl.Client).ConfigurePrimaryHttpMessageHandler(() => video));
        var f = s.App;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, annId, annName) = await PersonAsync(f, admin);
        await StatusAssert.Is(HttpStatusCode.OK, await UploadAsync(ann, "full.png", Png(1024 * 1024), "image/png"));

        var chat = (await ann.JsonAsync(await ann.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { tool } }))).GetProperty("id").GetGuid();
        var res = await ann.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = $"Make it: [call {function} {arguments}]" });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var result = (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)
            .Single(e => e.GetProperty("type").GetString() == "tool_result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.StartsWith(refusal + ": the person's files are full. Tell them: Your files take", result.GetProperty("text").GetString(), StringComparison.Ordinal);
        // Nothing was asked of the video server or the speech model, and nothing was kept.
        Assert.DoesNotContain(video.Asked, p => p.Contains("vid_gen", StringComparison.Ordinal));
        Assert.DoesNotContain(app.Model.SpeechRequests, r => r["user"]?.GetValue<string>() == $"{annName}@example.test");
        await using var scope = f.Services.CreateAsyncScope();
        Assert.Equal(["full.png"], await scope.ServiceProvider.GetRequiredService<AppDbContext>().ChatAttachments.Where(a => a.UserId == annId).Select(a => a.FileName).ToListAsync());
    }

    [Fact]
    public async Task A_file_on_a_message_waiting_its_turn_is_in_its_chat_and_no_clean_up_takes_it()
    {
        await using var s = NewApp();
        var f = s.App;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ann, _, _) = await PersonAsync(f, admin);
        var chat = await ChatAsync(ann, "First question");
        var waiting = await UploadTextAsync(ann, "next.txt", "for the next question");
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.QueuedMessages.Add(new QueuedMessage { ConversationId = chat, Content = "And this", AttachmentsJson = JsonSerializer.Serialize(new[] { waiting }) });
            await db.SaveChangesAsync();
        }
        await OlderAsync(f, TimeSpan.FromDays(30));

        var row = Rows(await FilesAsync(admin)).Single(r => r.GetProperty("id").GetGuid() == waiting);
        Assert.Equal(("upload", "chat", chat), (row.GetProperty("origin").GetString(), row.GetProperty("state").GetString(), row.GetProperty("chatId").GetGuid()));
        var plan = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage/cleanups/unused-files?days=7"));
        Assert.Equal(0, plan.GetProperty("count").GetInt64());
    }

    private static string Vector(params (string Device, string Mount, double Value)[] series) => new JsonObject
    {
        ["status"] = "success",
        ["data"] = new JsonObject
        {
            ["resultType"] = "vector",
            ["result"] = new JsonArray([.. series.Select(x => (JsonNode)new JsonObject
            {
                ["metric"] = new JsonObject { ["device"] = x.Device, ["mountpoint"] = x.Mount, ["fstype"] = "ext4", ["instance"] = "node-exporter:9100" },
                ["value"] = new JsonArray(1_790_000_000, x.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            })]),
        },
    }.ToJsonString();

    /// <summary>
    /// Alertmanager as it holds what the app gives it: each alert by its labels as last posted, while its end is ahead,
    /// with its start to the millisecond (as Alertmanager's API writes times).
    /// </summary>
    private void AlertmanagerHolds(MovableClock clock) => app.Observe.Answers["/api/v2/alerts"] = _ => new JsonArray([.. app.Observe.To("/api/v2/alerts")
        .Where(r => r.Method == "POST").SelectMany(r => JsonNode.Parse(r.Body!)!.AsArray()).GroupBy(a => a!["labels"]!.ToJsonString()).Select(g => g.Last()!)
        .Where(a => DateTimeOffset.Parse(a["endsAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) > clock.Now)
        .Select(a => (JsonNode)new JsonObject
        {
            ["labels"] = a["labels"]!.DeepClone(), ["annotations"] = a["annotations"]?.DeepClone() ?? new JsonObject(),
            ["startsAt"] = DateTimeOffset.Parse(a["startsAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).UtcDateTime
                .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["status"] = new JsonObject { ["state"] = "active", ["silencedBy"] = new JsonArray(), ["inhibitedBy"] = new JsonArray() },
        })]).ToJsonString();

    [Fact]
    public async Task A_disk_past_the_share_is_an_alert_given_to_Alertmanager_ended_when_below_and_told_directly_without_it()
    {
        app.Observe.Reset();
        try
        {
            await using var s = NewApp();
            var f = s.App;
            var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
            var free = 50.0;
            app.Observe.Answers["/api/v1/query"] = args => args["query"]!.StartsWith("node_filesystem_size_bytes", StringComparison.Ordinal) ? Vector(("/dev/sdb1", "/data", 1000))
                : args["query"]!.StartsWith("node_filesystem_avail_bytes", StringComparison.Ordinal) ? Vector(("/dev/sdb1", "/data", free))
                : Vector();
            var watch = f.Services.GetRequiredService<StorageWatch>();
            List<JsonNode> Posted() => [.. app.Observe.To("/api/v2/alerts").Where(r => r.Method == "POST").Select(r => JsonNode.Parse(r.Body!)!)];
            AlertmanagerHolds(s.Clock);

            await watch.CheckAsync(CancellationToken.None);
            var alert = Assert.Single(Posted())!.AsArray().Single()!;
            Assert.Equal(StorageWatch.AlertName, alert["labels"]!["alertname"]!.GetValue<string>());
            Assert.Equal("/data", alert["labels"]!["mountpoint"]!.GetValue<string>());
            Assert.Equal("Disk /data is 95% full", alert["annotations"]!["summary"]!.GetValue<string>());
            Assert.Contains("warns above 80%", alert["annotations"]!["description"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.True(DateTimeOffset.Parse(alert["endsAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) > s.Clock.Now);
            var starts = alert["startsAt"]!.GetValue<string>();

            // On the Alerts page with the stack's rules, and on the Overview.
            var rules = (await admin.JsonAsync(await admin.GetAsync("/api/admin/alerts"))).GetProperty("rules").EnumerateArray().ToList();
            var rule = rules.Single(r => r.GetProperty("name").GetString() == StorageWatch.AlertName);
            Assert.Equal("firing", rule.GetProperty("state").GetString());
            var overview = (await admin.JsonAsync(await admin.GetAsync("/api/admin/overview"))).GetProperty("storage");
            Assert.True(overview.GetProperty("disks")[0].GetProperty("above").GetBoolean());
            Assert.Equal(80, overview.GetProperty("alertPercent").GetInt32());

            // Still past it: the same alert, kept up.
            s.Clock.Now += TimeSpan.FromMinutes(5);
            await watch.CheckAsync(CancellationToken.None);
            Assert.Equal(starts, Posted()[^1].AsArray().Single()!["startsAt"]!.GetValue<string>());

            // Below again: it ends.
            free = 900;
            await watch.CheckAsync(CancellationToken.None);
            var ended = Posted()[^1].AsArray().Single()!;
            Assert.True(DateTimeOffset.Parse(ended["endsAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) <= s.Clock.Now);
            Assert.Equal("inactive", (await admin.JsonAsync(await admin.GetAsync("/api/admin/alerts"))).GetProperty("rules").EnumerateArray()
                .Single(r => r.GetProperty("name").GetString() == StorageWatch.AlertName).GetProperty("state").GetString());

            // Alertmanager away: the admins are told directly, once each time it passes the share.
            app.Observe.Down["/api/v2/alerts"] = true;
            free = 50;
            s.Clock.Now += TimeSpan.FromTicks(1234);
            await watch.CheckAsync(CancellationToken.None);
            await watch.CheckAsync(CancellationToken.None);
            async Task<List<JsonElement>> BellAsync() => [.. (await admin.JsonAsync(await admin.GetAsync("/api/notifications"))).GetProperty("items").EnumerateArray()
                .Where(n => n.GetProperty("title").GetString() == "Warning: Disk /data is 95% full")];
            Assert.Equal("/admin/storage", Assert.Single(await BellAsync()).GetProperty("link").GetString());

            // Alertmanager back: it takes the same alert, and the news of it is the news they had.
            app.Observe.Down.TryRemove("/api/v2/alerts", out _);
            s.Clock.Now += TimeSpan.FromMinutes(5);
            await watch.CheckAsync(CancellationToken.None);
            await f.Services.GetRequiredService<NewsWatch>().CheckAlertsAsync(CancellationToken.None);
            Assert.Equal("/admin/storage", Assert.Single(await BellAsync()).GetProperty("link").GetString());
        }
        finally
        {
            app.Observe.Reset();
        }
    }

    [Fact]
    public async Task While_Prometheus_is_away_a_host_disk_past_the_share_keeps_its_one_alert_and_its_folders_raise_none()
    {
        app.Observe.Reset();
        try
        {
            // A share every disk is past; the model library's folder is on the host's /data, as node-exporter reports it.
            await using var s = NewApp(new() { ["Storage:AlertPercent"] = "0" });
            Directory.CreateDirectory(s.Library);
            var (size, free) = StorageDisks.Space(s.Library)!.Value;
            app.Observe.Answers["/api/v1/query"] = args => args["query"]!.StartsWith("node_filesystem_size_bytes", StringComparison.Ordinal) ? Vector(("/dev/sdb1", "/data", size))
                : args["query"]!.StartsWith("node_filesystem_avail_bytes", StringComparison.Ordinal) ? Vector(("/dev/sdb1", "/data", free))
                : Vector();
            var watch = s.App.Services.GetRequiredService<StorageWatch>();
            List<JsonArray> Posted() => [.. app.Observe.To("/api/v2/alerts").Where(r => r.Method == "POST").Select(r => JsonNode.Parse(r.Body!)!.AsArray())];
            DateTimeOffset Ends(JsonNode alert) => DateTimeOffset.Parse(alert["endsAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);

            await watch.CheckAsync(CancellationToken.None);
            var raised = Assert.Single(Assert.Single(Posted()))!;
            Assert.Equal(("/dev/sdb1", "/data"), (raised["labels"]!["device"]!.GetValue<string>(), raised["labels"]!["mountpoint"]!.GetValue<string>()));
            var starts = raised["startsAt"]!.GetValue<string>();

            // Prometheus away: the host's disk is as full as anyone knows. Its alert stays up, words and all, and the library's
            // folder, seen by the app alone now, raises none of its own (the admins would be told twice).
            app.Observe.Down["/api/v1/query"] = true;
            s.Clock.Now += TimeSpan.FromMinutes(5);
            await watch.CheckAsync(CancellationToken.None);
            var kept = Assert.Single(Posted()[^1])!;
            Assert.Equal(("/data", starts), (kept["labels"]!["mountpoint"]!.GetValue<string>(), kept["startsAt"]!.GetValue<string>()));
            Assert.True(Ends(kept) > s.Clock.Now);
            Assert.StartsWith("Disk /data is", kept["annotations"]!["summary"]!.GetValue<string>(), StringComparison.Ordinal);

            // Back: the same alert, since the same time.
            app.Observe.Down.TryRemove("/api/v1/query", out _);
            s.Clock.Now += TimeSpan.FromMinutes(5);
            await watch.CheckAsync(CancellationToken.None);
            var back = Assert.Single(Posted()[^1])!;
            Assert.Equal(("/data", starts), (back["labels"]!["mountpoint"]!.GetValue<string>(), back["startsAt"]!.GetValue<string>()));
            Assert.True(Ends(back) > s.Clock.Now);
        }
        finally
        {
            app.Observe.Reset();
        }
    }

    [Fact]
    public async Task Every_replica_shows_the_app_rule_as_Alertmanager_holds_it_and_without_Alertmanager_the_disks_now()
    {
        app.Observe.Reset();
        try
        {
            // A replica that does not lead has never looked at the disks itself.
            await using var s = NewApp(new() { ["Storage:AlertPercent"] = "0" });
            var admin = await new TestBrowser(s.App).SignedInAsync("admin", AppFixture.AdminPassword);
            app.Observe.Answers["/api/v2/alerts"] = _ => """
                [{"labels":{"alertname":"DiskAboveThreshold","severity":"warning","component":"storage","source":"app","device":"/dev/sdb1","mountpoint":"/data"},
                  "annotations":{"summary":"Disk /data is 91% full"},"startsAt":"2026-09-20T10:00:00Z","status":{"state":"active","silencedBy":[],"inhibitedBy":[]}},
                 {"labels":{"alertname":"DiskAboveThreshold","severity":"warning","source":"app","device":"/dev/sdc1","mountpoint":"/srv"},
                  "annotations":{"summary":"Disk /srv is 85% full"},"startsAt":"2026-09-21T10:00:00Z","status":{"state":"suppressed","silencedBy":["s1"],"inhibitedBy":[]}},
                 {"labels":{"alertname":"TargetDown","severity":"critical","job":"loki"},"startsAt":"2026-09-20T11:05:00Z","status":{"state":"active","silencedBy":[],"inhibitedBy":[]}}]
                """;
            async Task<JsonElement> RuleAsync() => (await admin.JsonAsync(await admin.GetAsync("/api/admin/alerts"))).GetProperty("rules").EnumerateArray()
                .Single(r => r.GetProperty("name").GetString() == StorageWatch.AlertName);
            var rule = await RuleAsync();
            Assert.Equal(("firing", 2), (rule.GetProperty("state").GetString(), rule.GetProperty("active").GetInt32()));
            Assert.Equal(DateTimeOffset.Parse("2026-09-20T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), rule.GetProperty("activeAt").GetDateTimeOffset());

            app.Observe.Answers["/api/v2/alerts"] = _ => "[]";
            Assert.Equal("inactive", (await RuleAsync()).GetProperty("state").GetString());

            // Alertmanager away: the disks against the share now (a folder of the app's, past a share of 0).
            app.Observe.Down["/api/v2/alerts"] = true;
            Directory.CreateDirectory(s.Library);
            rule = await RuleAsync();
            Assert.Equal(("firing", 1, JsonValueKind.Null), (rule.GetProperty("state").GetString(), rule.GetProperty("active").GetInt32(), rule.GetProperty("activeAt").ValueKind));
        }
        finally
        {
            app.Observe.Reset();
        }
    }

    [Fact]
    public async Task The_page_shows_databases_tables_argus_logs_and_metrics_and_the_daily_samples_make_its_trends()
    {
        app.Observe.Reset();
        try
        {
            await using var s = NewApp(new() { ["Retention:Days"] = "365" });
            var f = s.App;
            var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
            var (ann, _, _) = await PersonAsync(f, admin);
            await ChatAsync(ann, "Remember this", [await UploadTextAsync(ann, "n.txt", "notes")]);
            app.Observe.Answers["/api/v1/query"] = args => args["query"] switch
            {
                "prometheus_tsdb_storage_blocks_bytes" => Vector(("", "", 1_000_000)),
                "prometheus_tsdb_wal_storage_size_bytes" => Vector(("", "", 200_000)),
                "prometheus_tsdb_head_chunks_storage_size_bytes" => Vector(("", "", 50_000)),
                _ => Vector(),
            };
            app.Observe.Answers["/api/v1/status/flags"] = _ => """{"status":"success","data":{"storage.tsdb.retention.time":"30d","storage.tsdb.retention.size":"20GB"}}""";
            app.Observe.Answers["/config"] = _ => "server:\n  http_listen_port: 3100\nlimits_config:\n  retention_period: 336h\ncompactor:\n  retention_enabled: true\n";
            app.Observe.Answers["/loki/api/v1/index/volume"] = _ =>
                """{"status":"success","data":{"resultType":"vector","result":[{"metric":{"container":"app"},"value":[1790000000,"5000"]},{"metric":{"container":"litellm"},"value":[1790000000,"9000"]}]}}""";

            await f.Services.GetRequiredService<StorageWatch>().SampleAsync(CancellationToken.None);
            var page = await admin.JsonAsync(await admin.GetAsync("/api/admin/storage"));

            var databases = page.GetProperty("databases");
            var appDb = new Npgsql.NpgsqlConnectionStringBuilder(f.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["ConnectionStrings:App"]).Database!;
            var mine = databases.GetProperty("list").EnumerateArray().Single(d => d.GetProperty("name").GetString() == appDb);
            Assert.StartsWith("The app:", mine.GetProperty("what").GetString(), StringComparison.Ordinal);
            Assert.True(mine.GetProperty("bytes").GetInt64() > 1_000_000);
            Assert.Contains(databases.GetProperty("tables").EnumerateArray(), t => t.GetProperty("database").GetString() == appDb && t.GetProperty("name").GetString() == "chat_messages"
                && t.GetProperty("what").GetString() == "the chats' messages");
            Assert.Contains(databases.GetProperty("tables").EnumerateArray(), t => t.GetProperty("name").GetString() == "LiteLLM_SpendLogs");

            var files = page.GetProperty("files");
            Assert.Equal(1, files.GetProperty("total").GetProperty("count").GetInt64());
            Assert.Equal("upload", files.GetProperty("groups")[0].GetProperty("origin").GetString());
            Assert.Equal(5_000_000, page.GetProperty("argus").GetProperty("report").GetProperty("index_bytes").GetInt64());
            Assert.Equal("dotnet-docs", page.GetProperty("argus").GetProperty("packs")[0].GetProperty("name").GetString());
            var metrics = page.GetProperty("metrics");
            Assert.Equal(1_250_000, metrics.GetProperty("bytes").GetInt64());
            Assert.Equal("30 days", metrics.GetProperty("keeps").GetString());
            Assert.Equal("20GB", metrics.GetProperty("maxSize").GetString());
            var logs = page.GetProperty("logs");
            Assert.Equal("14 days", logs.GetProperty("keeps").GetString());
            Assert.Equal("litellm", logs.GetProperty("week")[0].GetProperty("name").GetString());
            Assert.NotEmpty(page.GetProperty("unseen").EnumerateArray());
            Assert.Contains(page.GetProperty("rules").EnumerateArray(), r => r.GetProperty("rule").GetString()!.StartsWith("Deleted 365 days after their last message", StringComparison.Ordinal));
            Assert.Equal(80, page.GetProperty("settings").GetProperty("alertPercent").GetInt32());
            Assert.Equal(80, page.GetProperty("disks").GetProperty("alertPercent").GetInt32());

            var trends = page.GetProperty("trends");
            Assert.Equal(5, trends.GetProperty("files:upload")[0][1].GetInt64());
            Assert.True(trends.GetProperty("db:" + appDb)[0][1].GetInt64() > 0);
            Assert.Equal(5_000_000 + 20_000_000 + 3_000_000 + 1000, trends.GetProperty("argus")[0][1].GetInt64());
            Assert.Equal(1_250_000, trends.GetProperty("metrics")[0][1].GetInt64());

            // Measure again: the folders now, and Argus's too.
            await StatusAssert.Is(HttpStatusCode.OK, await admin.GetAsync("/api/admin/storage?fresh=true"));
            Assert.Contains(app.Argus.Calls, c => c.PathAndQuery == "/admin/storage?fresh");

            // Again the same day: today's row is measured again, not doubled.
            await f.Services.GetRequiredService<StorageWatch>().SampleAsync(CancellationToken.None);
            await using var scope = f.Services.CreateAsyncScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().StorageSamples.CountAsync(x => x.Key == "files"));
        }
        finally
        {
            app.Observe.Reset();
        }
    }

    [Fact]
    public async Task A_v5_2_database_gains_the_storage_tables_and_keeps_its_people_and_files()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(app.ConnectionStringFor("up_" + Guid.NewGuid().ToString("N")[..8])).Options;
        var person = new Llm.Core.Identity.AppUser { Id = Guid.NewGuid(), UserName = "kept", NormalizedUserName = "KEPT", Email = "kept@example.test", NormalizedEmail = "KEPT@EXAMPLE.TEST" };
        var fileId = Guid.NewGuid();
        await using (var db = new AppDbContext(options))
        {
            // v5.2.0's last migration, and a file as v5.2.0 keeps one (its columns then).
            await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db)
                .MigrateAsync("20261005151513_ToolCertificates");
            // As v5.2.0 keeps a person: its columns then (later migrations add more).
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AspNetUsers" ("Id", "UserName", "NormalizedUserName", "Email", "NormalizedEmail", "DisplayName", "EmailConfirmed", "PhoneNumberConfirmed",
                    "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount", "CacheApiAnswers", "CreatedAt", "IsDisabled", "MemoryOff", "Source")
                VALUES ({person.Id}, {person.UserName}, {person.NormalizedUserName}, {person.Email}, {person.NormalizedEmail}, 'Kept', false, false,
                    false, true, 0, false, now(), false, false, 0)
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO chat_attachments ("Id", "UserId", "FileName", "ContentType", "Size", "Text", "Truncated", "Kind", "CreatedAt")
                VALUES ({fileId}, {person.Id}, 'kept.txt', 'text/plain', 4, 'kept', false, 'text', now())
                """);
        }
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            // Kept, with no tool named as its maker: its messages say where it came from, as before.
            var kept = await db.ChatAttachments.SingleAsync(a => a.Id == fileId);
            Assert.Equal(("kept", null), (kept.Text, kept.Origin));
            Assert.Empty(await db.StorageSamples.ToListAsync());
            db.StorageQuotas.Add(new StorageQuota { UserId = person.Id, Megabytes = 10 });
            await db.SaveChangesAsync();
            // A person removed takes their room with them.
            await db.Users.Where(u => u.Id == person.Id).ExecuteDeleteAsync();
            Assert.Empty(await db.StorageQuotas.ToListAsync());
        }
    }

    [Theory]
    [InlineData("limits_config:\n  retention_period: 336h\ncompactor:\n  retention_enabled: true\n", "14 days")]
    [InlineData("limits_config:\n  retention_period: 720h0m0s\n  max_query_series: 5000\ncompactor:\n  working_directory: /loki/compactor\n  retention_enabled: true\n", "30 days")]
    [InlineData("limits_config:\n  retention_period: 0s\ncompactor:\n  retention_enabled: true\n", "forever")]
    [InlineData("limits_config:\n  retention_period: 336h\ncompactor:\n  retention_enabled: false\ntable_manager:\n  retention_period: 336h\n", "forever")]
    [InlineData("limits_config:\n  per_stream:\n    retention_period: 1h\ncompactor:\n  retention_enabled: true\n", "forever")]
    [InlineData("server:\n  http_listen_port: 3100\n", null)]
    public void Lokis_retention_is_read_from_its_configuration(string yaml, string? keeps) =>
        Assert.Equal(keeps, StorageReport.LokiKeeps(yaml));

    [Fact]
    public void Durations_and_a_disks_pace_read_as_people_say_them()
    {
        Assert.Equal(TimeSpan.FromDays(30), StorageReport.ParseDuration("30d"));
        Assert.Equal(TimeSpan.FromHours(1.5), StorageReport.ParseDuration("1h30m"));
        Assert.Null(StorageReport.ParseDuration("forever"));
        Assert.Equal("15 days", StorageReport.Duration("15d"));
        Assert.Equal("36 hours", StorageReport.Duration("36h"));
        Assert.Equal("20GB", StorageReport.Duration("20GB"));
        // Filling 2 GB a day: 10 GB free is five days.
        var day = 86_400_000.0;
        var points = Enumerable.Range(0, 8).Select(i => new double?[] { i * day, 100e9 + i * 2e9 }).ToList();
        var (perDay, n) = StorageDisks.Pace(points, 0);
        Assert.Equal(8, n);
        Assert.Equal(2e9, perDay!.Value, 1);
        Assert.Equal(5.0, StorageDisks.FullIn(new Disk("d", "/", null, null, 200_000_000_000, 10_000_000_000, "host", []), perDay));
        Assert.Null(StorageDisks.FullIn(new Disk("d", "/", null, null, 1, 1, "host", []), -5));
        Assert.Equal("1.5 GB", StorageDisks.Size(1_610_612_736));
        Assert.Equal("900 bytes", StorageDisks.Size(900));
    }
}
