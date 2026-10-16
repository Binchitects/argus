using System.Net;
using System.Text.Json;
using Llm.Api.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>The speech server as a download sees it: it fetches the model asked for, and answers once it has it.</summary>
    private sealed class SpeechServer : HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Fetched = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Fetched.Enqueue($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }

    [Fact]
    public async Task Picture_models_are_found_by_their_pipeline_and_files_downloaded_and_speech_models_are_fetched_by_the_speech_server()
    {
        var speech = new SpeechServer();
        await using var f = app.Create(app.ConnectionStringFor("hfm_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Engine:LibraryDir"] = _library },
            s => s.AddHttpClient(ModelDownloads.SpeechClient).ConfigurePrimaryHttpMessageHandler(() => speech));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

        // Pictures: by the text-to-image pipeline, not only GGUF.
        await admin.JsonAsync(await admin.GetAsync("/api/admin/models/hf/search?q=flux&kind=image"));
        var asked = app.HuggingFace.Searches.Last();
        Assert.Contains("pipeline_tag=text-to-image", asked, StringComparison.Ordinal);
        Assert.DoesNotContain("filter=gguf", asked, StringComparison.Ordinal);
        await admin.JsonAsync(await admin.GetAsync("/api/admin/models/hf/search?q=qwen"));
        Assert.Contains("filter=gguf", app.HuggingFace.Searches.Last(), StringComparison.Ordinal);

        // Its files, by what their names say they are; a README is none of them.
        var repo = await admin.JsonAsync(await admin.GetAsync($"/api/admin/models/hf/repo?id={FakeHuggingFace.Repo}"));
        var files = repo.GetProperty("files").EnumerateArray().ToDictionary(x => x.GetProperty("path").GetString()!, x => x.GetProperty("role").GetString());
        Assert.Equal("diffusion", files["Tiny-Q4_K_M.gguf"]);
        Assert.DoesNotContain("README.md", files.Keys);

        // A file picked by its path, downloaded and checked as a model's are.
        var made = await admin.PostAsync("/api/admin/models/hf/downloads", new { repo = FakeHuggingFace.Repo, files = new[] { "mmproj-Tiny-F16.gguf" } });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var done = await DownloadAsync(admin, (await admin.JsonAsync(made)).GetProperty("id").GetGuid());
        Assert.Equal("done", done.GetProperty("state").GetString());
        Assert.Equal(FakeHuggingFace.Files["mmproj-Tiny-F16.gguf"], await File.ReadAllBytesAsync(Path.Combine(_library, "acme/Tiny-GGUF/mmproj-Tiny-F16.gguf")));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/models/hf/downloads", new { repo = FakeHuggingFace.Repo, files = new[] { "../../etc/passwd" } }));

        // Speech: the speech server fetches it itself, as a download in the list.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/models/hf/speech", new { id = "../x" }));
        var fetch = await admin.PostAsync("/api/admin/models/hf/speech", new { id = "speaches-ai/piper-de_DE-thorsten-medium" });
        await StatusAssert.Is(HttpStatusCode.Created, fetch);
        var spoken = await DownloadAsync(admin, (await admin.JsonAsync(fetch)).GetProperty("id").GetGuid());
        Assert.Equal(("done", "speech"), (spoken.GetProperty("state").GetString(), spoken.GetProperty("kind").GetString()));
        Assert.Contains("POST /v1/models/speaches-ai/piper-de_DE-thorsten-medium", speech.Fetched);
    }

    [Fact]
    public void A_snapshot_for_vllm_leaves_out_what_vllm_never_reads()
    {
        static HfFile F(string path, long size = 10) => new(path, size, null);
        var snap = HuggingFace.SnapshotOf([F("config.json"), F("model-00001-of-00002.safetensors", 1000), F("model-00002-of-00002.safetensors", 900),
            F("model.safetensors.index.json"), F("tokenizer.json"), F("tokenizer.model"), F("original/consolidated.safetensors", 5000), F("metal/model.bin", 4000),
            F("a/original/x.safetensors", 300), F("model.gguf", 3000), F("README.md"), F(".gitattributes"), F("pytorch_model.bin", 7000)])!;
        Assert.Equal(["config.json", "model-00001-of-00002.safetensors", "model-00002-of-00002.safetensors", "model.safetensors.index.json", "tokenizer.json", "tokenizer.model"],
            snap.Files.Select(f => f.Path));
        Assert.Equal(1940, snap.Size);
        Assert.Equal(5000 + 4000 + 300 + 3000 + 10 + 10 + 7000, snap.Skipped);
        // No safetensors, or no config: nothing vLLM could load.
        Assert.Null(HuggingFace.SnapshotOf([F("config.json"), F("model.gguf")]));
        Assert.Null(HuggingFace.SnapshotOf([F("model.safetensors")]));
        Assert.Equal("hf/org/repo@abc", HuggingFace.SnapshotDir("org/repo", "abc"));
    }

    [Fact]
    public async Task A_snapshot_shows_its_size_first_then_downloads_into_its_own_folder_and_the_library_knows_it_whole()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var repo = await admin.JsonAsync(await admin.GetAsync($"/api/admin/models/hf/repo?id={FakeHuggingFace.SnapshotRepo}"));
        var snapshot = repo.GetProperty("snapshot");
        var wanted = FakeHuggingFace.SnapshotFiles.Where(x => !x.Key.StartsWith("original/", StringComparison.Ordinal) && !x.Key.StartsWith("metal/", StringComparison.Ordinal)
            && !x.Key.EndsWith(".gguf", StringComparison.Ordinal) && x.Key != "README.md" && x.Key != ".gitattributes").ToList();
        Assert.Equal(wanted.Sum(x => x.Value.Length), snapshot.GetProperty("size").GetInt64());
        Assert.Equal(wanted.Count, snapshot.GetProperty("files").GetInt32());
        Assert.Equal($"hf/{FakeHuggingFace.SnapshotRepo}@{FakeHuggingFace.Sha}", snapshot.GetProperty("path").GetString());
        Assert.False(snapshot.GetProperty("inLibrary").GetBoolean());
        // A GGUF-only repository has none to offer, and asking for one says why.
        Assert.Equal(JsonValueKind.Null, (await admin.JsonAsync(await admin.GetAsync($"/api/admin/models/hf/repo?id={FakeHuggingFace.Repo}"))).GetProperty("snapshot").ValueKind);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/models/hf/downloads", new { repo = FakeHuggingFace.Repo, snapshot = true }));

        var made = await admin.PostAsync("/api/admin/models/hf/downloads", new { repo = FakeHuggingFace.SnapshotRepo, snapshot = true });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var done = await DownloadAsync(admin, (await admin.JsonAsync(made)).GetProperty("id").GetGuid());
        Assert.Equal("done", done.GetProperty("state").GetString());
        var folder = Path.Combine(_library, "hf", "acme", "Flash-FP8@" + FakeHuggingFace.Sha);
        foreach (var (file, bytes) in wanted)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(folder, file)));
        }
        Assert.False(Directory.Exists(Path.Combine(folder, "original")));
        Assert.False(File.Exists(Path.Combine(folder, "Flash-Q4_K_M.gguf")));
        Assert.DoesNotContain(app.HuggingFace.Downloads, d => d.Path.Contains("/original/", StringComparison.Ordinal) || d.Path.Contains("/metal/", StringComparison.Ordinal));

        var listed = Assert.Single((await admin.JsonAsync(await admin.GetAsync("/api/admin/models/library/snapshots"))).EnumerateArray());
        Assert.Equal(($"hf/acme/Flash-FP8@{FakeHuggingFace.Sha}", FakeHuggingFace.SnapshotRepo, true),
            (listed.GetProperty("path").GetString(), listed.GetProperty("repo").GetString(), listed.GetProperty("complete").GetBoolean()));
        Assert.Equal("Qwen3MoeForCausalLM", listed.GetProperty("architectures")[0].GetString());
        Assert.True((await admin.JsonAsync(await admin.GetAsync($"/api/admin/models/hf/repo?id={FakeHuggingFace.SnapshotRepo}"))).GetProperty("snapshot").GetProperty("inLibrary").GetBoolean());

        // A shard gone: the library says it is not whole, and why.
        File.Delete(Path.Combine(folder, "model-00002-of-00002.safetensors"));
        var library = f.Services.GetRequiredService<ModelLibrary>();
        var broken = library.Snapshot($"hf/acme/Flash-FP8@{FakeHuggingFace.Sha}")!;
        Assert.False(broken.Complete);
        Assert.Contains("model-00002-of-00002.safetensors", broken.Why, StringComparison.Ordinal);
        Assert.Null(library.Snapshot("../outside"));
    }
}
