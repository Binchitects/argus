using Microsoft.Extensions.DependencyInjection;
using Llm.Api.Models;

namespace Llm.Tests;

/// <summary>Every use a model has here, not only the one it is listed as.</summary>
public sealed class ModelUsesTests
{
    private static List<string> Ids(IReadOnlyList<ModelUse> uses) => [.. uses.Select(u => u.Id)];

    [Fact]
    public void A_chat_model_a_picture_model_reads_its_prompt_with_is_its_text_encoder_too()
    {
        Assert.Equal(["chat", "text_encoder"], Ids(new ModelProfile { Kind = ModelKind.Language, Architecture = "qwen3" }.Uses));
        Assert.Contains("Qwen-Image", new ModelProfile { Kind = ModelKind.Language, Architecture = "qwen2vl" }.Uses[1].Label, StringComparison.Ordinal);
        Assert.Equal(["chat"], Ids(new ModelProfile { Kind = ModelKind.Language, Architecture = "deepseek2" }.Uses));
        Assert.Equal(["text_encoder"], Ids(new ModelProfile { Kind = ModelKind.Encoder, Architecture = "t5" }.Uses));
        Assert.Equal(["image"], Ids(new ModelProfile { Kind = ModelKind.Image }.Uses));
        Assert.Equal(["projector", "vision", "hearing"], Ids(new ModelProfile { Kind = ModelKind.Projector, Projector = new ProjectorInfo(true, true, null, null) }.Uses));
    }

    [Fact]
    public void A_repositorys_uses_are_read_from_its_pipeline_tags_and_name_before_a_download()
    {
        // The text encoder of Qwen-Image as a GGUF: Qwen2.5-VL, so it chats and sees as well.
        Assert.Equal(["text_encoder", "chat", "vision"], Ids(ModelUses.Of("pottokao/Qwen-Image-2.1-Text-Encoder-Heretic-GGUF", null, ["gguf"])));
        Assert.Equal(["chat", "vision"], Ids(ModelUses.Of("org/SomeVL-GGUF", "image-text-to-text", ["gguf"])));
        Assert.Equal(["chat", "text_encoder"], Ids(ModelUses.Of("unsloth/Qwen3-4B-GGUF", "text-generation", ["gguf"])));
        Assert.Equal(["embedding"], Ids(ModelUses.Of("nomic-ai/nomic-embed-text-v1.5-GGUF", "sentence-similarity", ["gguf", "sentence-transformers"])));
        Assert.Equal(["image"], Ids(ModelUses.Of("black-forest-labs/FLUX.2-klein", "text-to-image", [])));
        Assert.Equal(["video"], Ids(ModelUses.Of("Wan-AI/Wan2.2-TI2V-5B", "text-to-video", [])));
        Assert.Equal(["speech"], Ids(ModelUses.Of("hexgrad/Kokoro-82M", "text-to-speech", [])));
        Assert.Equal(["transcription"], Ids(ModelUses.Of("openai/whisper-large-v3-turbo", "automatic-speech-recognition", [])));
        Assert.Equal(["chat"], Ids(ModelUses.Of("someone/mystery-GGUF", null, ["gguf"])));
    }
}

/// <summary>The model test bench: its capability tests each checked by its rule, and its capacity raised until more do not make more.</summary>
[Collection(nameof(AppCollection))]
public sealed class ModelBenchTests(AppFixture app)
{
    [Fact]
    public async Task A_models_capability_and_capacity_are_tested_and_kept_and_one_at_a_time()
    {
        var admin = await new TestBrowser(app.Factory).SignedInAsync("admin", AppFixture.AdminPassword);
        const string model = "Qwen3.8-Flash-Next";
        var started = await admin.JsonAsync(await admin.PostAsync($"/api/admin/models/{model}/bench", new { kind = "capability" }));
        var id = started.GetProperty("id").GetGuid();
        // One test of a model at a time.
        await StatusAssert.Is(System.Net.HttpStatusCode.Conflict, await admin.PostAsync($"/api/admin/models/{model}/bench", new { kind = "capacity" }));
        await app.Factory.Services.GetRequiredService<ModelBench>().Finished(id).WaitAsync(TimeSpan.FromSeconds(60));
        var run = await admin.JsonAsync(await admin.GetAsync($"/api/admin/models/bench/{id}"));
        Assert.Equal("done", run.GetProperty("status").GetString());
        var tests = run.GetProperty("results").GetProperty("tests").EnumerateArray().ToList();
        Assert.Equal(ModelBench.Tests.Select(t => t.Id), tests.Select(t => t.GetProperty("id").GetString()));
        // The fake model answers "Answer to: …": each test says why it did not pass.
        Assert.All(tests.Where(t => !t.GetProperty("passed").GetBoolean()), t => Assert.False(string.IsNullOrEmpty(t.GetProperty("why").GetString())));
        Assert.Equal(ModelBench.Tests.Count, run.GetProperty("results").GetProperty("total").GetInt32());

        var capacity = (await admin.JsonAsync(await admin.PostAsync($"/api/admin/models/{model}/bench", new { kind = "capacity", most = 4 }))).GetProperty("id").GetGuid();
        await app.Factory.Services.GetRequiredService<ModelBench>().Finished(capacity).WaitAsync(TimeSpan.FromSeconds(60));
        var measured = await admin.JsonAsync(await admin.GetAsync($"/api/admin/models/bench/{capacity}"));
        var levels = measured.GetProperty("results").GetProperty("levels").EnumerateArray().Select(l => l.GetProperty("atOnce").GetInt32()).ToList();
        Assert.Equal(1, levels[0]);
        Assert.True(levels.Count is >= 1 and <= 3, string.Join(",", levels));
        Assert.Equal(2, (await admin.JsonAsync(await admin.GetAsync($"/api/admin/models/{model}/bench"))).EnumerateArray().Count());
    }

    [Fact]
    public void The_long_text_holds_the_code_once_and_follows_the_context()
    {
        var small = ModelBench.Haystack(4096);
        var big = ModelBench.Haystack(64_000);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(small, "secret code is 4721"));
        Assert.True(big.Length > small.Length * 4);
    }
}
