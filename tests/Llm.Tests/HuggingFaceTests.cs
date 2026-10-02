using System.Net;
using System.Text.Json;
using Llm.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Admin → Models → Hugging Face: finding GGUF models, and downloading them into the library (resumed, checked).</summary>
[Collection(nameof(AppCollection))]
public sealed class HuggingFaceTests(AppFixture app) : IDisposable
{
    private readonly string _library = Directory.CreateTempSubdirectory("llm-hf-").FullName;

    public void Dispose() => Directory.Delete(_library, recursive: true);

    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("hf_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?> { ["Engine:LibraryDir"] = _library });

    private static async Task<JsonElement> DownloadAsync(TestBrowser admin, Guid id)
    {
        for (var i = 0; i < 100; i++)
        {
            var d = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models/hf/downloads"))).EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == id);
            if (d.GetProperty("state").GetString() is "done" or "failed" or "paused")
            {
                return d;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("The download never ended.");
    }

    [Fact]
    public void File_names_say_their_quantisation_and_split_parts_are_one_model()
    {
        Assert.Equal("Q4_K_M", HuggingFace.QuantOf("Qwen3-8B-Q4_K_M.gguf"));
        Assert.Equal("IQ4_XS", HuggingFace.QuantOf("Qwen3.8-Flash-Next-IQ4_XS.gguf"));
        Assert.Equal("UD-Q4_K_XL", HuggingFace.QuantOf("Qwen3-30B-A3B-UD-Q4_K_XL.gguf"));
        Assert.Equal("Q8_0", HuggingFace.QuantOf("Q8_0/Tiny-Q8_0.gguf"));
        Assert.Equal("BF16", HuggingFace.QuantOf("model-BF16.gguf"));
        var models = HuggingFace.Group([new HfFile("a/M-Q8_0-00002-of-00002.gguf", 2, null), new HfFile("a/M-Q8_0-00001-of-00002.gguf", 3, null), new HfFile("mmproj-M-F16.gguf", 1, null), new HfFile("README.md", 1, null)]);
        Assert.Equal(2, models.Count);
        var split = models.Single(m => !m.Projector);
        Assert.Equal("a/M-Q8_0.gguf", split.Name);
        Assert.Equal(5, split.Size);
        Assert.Equal(["a/M-Q8_0-00001-of-00002.gguf", "a/M-Q8_0-00002-of-00002.gguf"], split.Files.Select(f => f.Path));
        Assert.True(models.Single(m => m.Projector).Projector);
    }

    [Fact]
    public async Task A_model_is_found_downloaded_resumed_after_a_cut_and_checked()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.GetAsync("/api/admin/models/hf/search?q=a"));
        var found = await admin.JsonAsync(await admin.GetAsync("/api/admin/models/hf/search?q=tiny"));
        Assert.Equal(FakeHuggingFace.Repo, found[0].GetProperty("id").GetString());

        var repo = await admin.JsonAsync(await admin.GetAsync($"/api/admin/models/hf/repo?id={FakeHuggingFace.Repo}"));
        Assert.Equal("qwen3", repo.GetProperty("architecture").GetString());
        Assert.Equal(40960, repo.GetProperty("context").GetInt32());
        var models = repo.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(["Tiny-Q4_K_M.gguf", "Q8_0/Tiny-Q8_0.gguf", "mmproj-Tiny-F16.gguf"], models.Select(m => m.GetProperty("name").GetString()));
        var split = models.Single(m => m.GetProperty("name").GetString() == "Q8_0/Tiny-Q8_0.gguf");
        Assert.Equal(2, split.GetProperty("parts").GetInt32());
        Assert.Equal(350_000, split.GetProperty("size").GetInt64());
        Assert.False(split.GetProperty("inLibrary").GetBoolean());

        await StatusAssert.Is(HttpStatusCode.Forbidden, await admin.GetAsync("/api/admin/models/hf/repo?id=acme/Gated-GGUF"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.GetAsync("/api/admin/models/hf/repo?id=..%2F..%2Fetc"));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/models/hf/downloads", new { repo = FakeHuggingFace.Repo, models = new[] { "nope.gguf" } }));

        // The second part's connection drops partway: it fails, and resuming goes on from there (a range request).
        app.HuggingFace.CutOnce = ("Q8_0/Tiny-Q8_0-00002-of-00002.gguf", 60_000);
        var made = await admin.PostAsync("/api/admin/models/hf/downloads", new { repo = FakeHuggingFace.Repo, models = new[] { "Q8_0/Tiny-Q8_0.gguf", "mmproj-Tiny-F16.gguf" } });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var id = (await admin.JsonAsync(made)).GetProperty("id").GetGuid();
        var cut = await DownloadAsync(admin, id);
        Assert.Equal("failed", cut.GetProperty("state").GetString());
        Assert.True(File.Exists(Path.Combine(_library, "acme/Tiny-GGUF/Q8_0/Tiny-Q8_0-00001-of-00002.gguf")));
        Assert.Equal(60_000, new FileInfo(Path.Combine(_library, "acme/Tiny-GGUF/Q8_0/Tiny-Q8_0-00002-of-00002.gguf.part")).Length);
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/models/hf/downloads", new { repo = FakeHuggingFace.Repo, models = new[] { "Q8_0/Tiny-Q8_0.gguf" } }));

        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/models/hf/downloads/{id}/resume"));
        var whole = await DownloadAsync(admin, id);
        Assert.Equal("done", whole.GetProperty("state").GetString());
        Assert.Equal(400_000, whole.GetProperty("bytes").GetInt64());
        Assert.Equal(60_000, app.HuggingFace.Downloads.Last(d => d.Path.EndsWith("00002-of-00002.gguf", StringComparison.Ordinal)).Range!.Ranges.First().From);
        foreach (var file in new[] { "Q8_0/Tiny-Q8_0-00001-of-00002.gguf", "Q8_0/Tiny-Q8_0-00002-of-00002.gguf", "mmproj-Tiny-F16.gguf" })
        {
            Assert.Equal(FakeHuggingFace.Files[file], await File.ReadAllBytesAsync(Path.Combine(_library, "acme/Tiny-GGUF", file)));
        }
        Assert.Empty(Directory.EnumerateFiles(_library, "*.part", SearchOption.AllDirectories));
        repo = await admin.JsonAsync(await admin.GetAsync($"/api/admin/models/hf/repo?id={FakeHuggingFace.Repo}"));
        Assert.True(repo.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "Q8_0/Tiny-Q8_0.gguf").GetProperty("inLibrary").GetBoolean());

        // A file whose bytes are not the ones listed is thrown away, and the download says so.
        app.HuggingFace.Corrupt = "Tiny-Q4_K_M.gguf";
        try
        {
            var bad = (await admin.JsonAsync(await admin.PostAsync("/api/admin/models/hf/downloads", new { repo = FakeHuggingFace.Repo, models = new[] { "Tiny-Q4_K_M.gguf" } }))).GetProperty("id").GetGuid();
            var failed = await DownloadAsync(admin, bad);
            Assert.Equal("failed", failed.GetProperty("state").GetString());
            Assert.Contains("SHA-256", failed.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(_library, "acme/Tiny-GGUF/Tiny-Q4_K_M.gguf")));
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri($"/api/admin/models/hf/downloads/{bad}", UriKind.Relative)));
        }
        finally
        {
            app.HuggingFace.Corrupt = null;
        }
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("model.download", audit);
    }
}
