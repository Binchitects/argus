using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Llm.Api.Storage;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>People's files sealed at rest with a key from APP_KEY: stored encrypted, read as they were, and those from before sealed by the job.</summary>
[Collection(nameof(AppCollection))]
public sealed class FileSealingTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp(string? dataKey) =>
        app.Create(app.ConnectionStringFor("seal_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            dataKey is null ? null : new Dictionary<string, string?> { ["Auth:DataKey"] = dataKey });

    private static async Task<Guid> UploadAsync(TestBrowser b, string text)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        part.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(part, "file", "notes.txt");
        var res = await b.Http.PostAsync(new Uri("/api/chat/attachments", UriKind.Relative), form);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return (await b.JsonAsync(res)).GetProperty("id").GetGuid();
    }

    /// <summary>A text file's words as stored (a text file keeps its words, not its bytes).</summary>
    private static async Task<string> StoredAsync(WebApplicationFactory<Program> f, Guid id)
    {
        await using var scope = f.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .SqlQuery<string>($"""SELECT "Text" AS "Value" FROM chat_attachments WHERE "Id" = {id}""").SingleAsync();
    }

    private static bool Sealed(string stored) => stored.StartsWith(FileSealer.TextMark, StringComparison.Ordinal);

    [Fact]
    [Trait("Control", "DAT-05")]
    public async Task A_file_is_stored_encrypted_read_as_it_was_and_those_stored_before_are_sealed_by_the_job()
    {
        await using var f = NewApp("a-data-key-for-sealing-tests");
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        const string Text = "The launch is on Friday, at the harbour.";
        var id = await UploadAsync(admin, Text);

        var stored = await StoredAsync(f, id);
        Assert.True(Sealed(stored));
        Assert.DoesNotContain("harbour", stored, StringComparison.Ordinal);
        Assert.Equal(Text, await (await admin.GetAsync($"/api/chat/attachments/{id}/content")).Content.ReadAsStringAsync());

        // One stored before files were sealed: read as it is, then sealed by the job.
        await using (var scope = f.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlAsync($"""UPDATE chat_attachments SET "Text" = {Text} WHERE "Id" = {id}""");
        }
        Assert.False(Sealed(await StoredAsync(f, id)));
        Assert.Equal(Text, await (await admin.GetAsync($"/api/chat/attachments/{id}/content")).Content.ReadAsStringAsync());
        var check = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "files-sealed");
        Assert.Equal("warn", check.GetProperty("state").GetString());

        Assert.Equal(1, await f.Services.GetRequiredService<FileSealingJob>().SealAsync(CancellationToken.None));
        Assert.True(Sealed(await StoredAsync(f, id)));
        Assert.Equal(Text, await (await admin.GetAsync($"/api/chat/attachments/{id}/content")).Content.ReadAsStringAsync());
        check = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "files-sealed");
        Assert.Equal("ok", check.GetProperty("state").GetString());
    }

    [Fact]
    [Trait("Control", "DAT-05")]
    public async Task Without_APP_KEY_files_are_stored_as_they_are_and_the_check_says_so()
    {
        await using var f = NewApp(null);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var id = await UploadAsync(admin, "plain words");
        Assert.Equal("plain words", await StoredAsync(f, id));
        Assert.Equal(0, await f.Services.GetRequiredService<FileSealingJob>().SealAsync(CancellationToken.None));
        var check = (await admin.JsonAsync(await admin.GetAsync("/api/admin/compliance"))).GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("id").GetString() == "files-sealed");
        Assert.Equal("fail", check.GetProperty("state").GetString());
    }

    [Fact]
    [Trait("Control", "NET-01")]
    public async Task With_HSTS_set_the_app_sends_it_as_Traefik_does_and_without_it_clears_any_kept_before()
    {
        foreach (var (seconds, expected) in new[] { ("31536000", "max-age=31536000; includeSubDomains"), ("", "max-age=0") })
        {
            await using var f = app.Create(app.ConnectionStringFor("hsts_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
                new Dictionary<string, string?> { ["Stack:HstsSeconds"] = seconds == "" ? null : seconds });
            using var client = f.CreateClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/info", UriKind.Relative));
            req.Headers.Add("X-Forwarded-Proto", "https");
            var res = await client.SendAsync(req);
            Assert.Equal(expected, res.Headers.GetValues("Strict-Transport-Security").Single());
        }
    }

    [Fact]
    public void A_sealed_file_opens_only_with_its_key_and_a_file_that_was_never_sealed_reads_as_it_is()
    {
        var sealer = new FileSealer(FileSealer.Derive("one key"));
        var plain = Encoding.UTF8.GetBytes("a file");
        var sealedBytes = sealer.Seal(plain);
        Assert.Equal(plain, sealer.Open(sealedBytes));
        Assert.Same(sealedBytes, sealer.Seal(sealedBytes));
        Assert.Equal(plain, sealer.Open(plain));
        Assert.Equal("", sealer.SealText(""));
        Assert.Equal("words", sealer.OpenText(sealer.SealText("words")));
        Assert.Equal("words", sealer.OpenText("words"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => new FileSealer(FileSealer.Derive("another key")).Open(sealedBytes));
        Assert.Throws<InvalidOperationException>(() => new FileSealer(null).Open(sealedBytes));
    }
}
