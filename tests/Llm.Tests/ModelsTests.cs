using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Api.Gateway;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

/// <summary>Admin → Models: the library, adding models to the engine, switching live, and who may use which model.</summary>
[Collection(nameof(AppCollection))]
public sealed class ModelsTests(AppFixture app) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-models-").FullName;

    private string Config => Path.Combine(_dir, "config");
    private string Library => Path.Combine(_dir, "library");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Its own app, database and engine files, with a library of real (small) GGUF files of every kind.</summary>
    private (WebApplicationFactory<Program> App, FakeGateway Gateway) NewApp()
    {
        GgufFile.Language("qwen3", name: "Tiny", context: 40960).Write(Path.Combine(Library, "tiny", "Tiny-4B-Q4_K_M.gguf"));
        new GgufFile().Text("general.architecture", "clip").Bool("clip.has_vision_encoder", true).U32("clip.vision.projection_dim", 64)
            .Tensor("v.blk.0.attn_q.weight", 2048).Write(Path.Combine(Library, "tiny", "mmproj-Tiny-F16.gguf"));
        new GgufFile().Text("general.architecture", "clip").Bool("clip.has_vision_encoder", true).U32("clip.vision.projection_dim", 4096)
            .Tensor("v.blk.0.attn_q.weight", 2048).Write(Path.Combine(Library, "other", "mmproj-Other-F16.gguf"));
        new GgufFile().Tensor("double_blocks.0.img_attn.qkv.weight", 4096).Write(Path.Combine(Library, "image", "flux-2-klein-4b-Q4_0.gguf"));
        GgufFile.Language("qwen3moe", name: "Big", context: 262144, experts: 8, used: 2, expertBytes: 8192).Write(Path.Combine(Library, "big", "Big-00001-of-00002.gguf"));
        new GgufFile().U32("split.no", 1).Tensor("blk.3.ffn_down_exps.weight", 8192, 64, 16, 8).Write(Path.Combine(Library, "big", "Big-00002-of-00002.gguf"));
        new GgufFile().Text("general.architecture", "qwen3moe").Bool("qwen3moe.nextn_shared_target_tensors", true).U32("qwen3moe.block_count", 5)
            .U32("qwen3moe.nextn_predict_layers", 1).U32("qwen3moe.embedding_length", 64).Strings("tokenizer.ggml.tokens", 64)
            .Tensor("blk.4.nextn.eh_proj.weight", 2048).Write(Path.Combine(Library, "big", "mtp-Big-Q8_0.gguf"));
        new GgufFile().Text("general.architecture", "nomic-bert").U32("nomic-bert.pooling_type", 1).U32("nomic-bert.context_length", 2048)
            .Tensor("token_embd.weight", 4096).Write(Path.Combine(Library, "embed", "nomic-embed-text-v1.5.f16.gguf"));
        GgufFile.Language("llama", name: "Plain", template: "{% for m in messages %}{{ m.content }}{% endfor %}", headSize: 72)
            .Write(Path.Combine(Library, "plain", "Plain-Q8_0.gguf"));
        GgufFile.Language("qwen3", name: "Part").Write(Path.Combine(Library, "part", "Part-00001-of-00003.gguf"));
        Directory.CreateDirectory(Config);
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        var f = app.Create(app.ConnectionStringFor("models_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = Library,
            ["StackEnv:LLAMACPP_THREADS"] = "12",
        });
        return (f, gateway);
    }

    private static Task<TestBrowser> AdminAsync(WebApplicationFactory<Program> f) => new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

    private static async Task<JsonElement> ModelsAsync(TestBrowser admin) => await admin.JsonAsync(await admin.GetAsync("/api/admin/models"));

    private static JsonElement Row(JsonElement list, string name) => list.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);

    private static readonly object Tiny = new
    {
        name = "tiny-b", file = "tiny/Tiny-4B-Q4_K_M.gguf", projector = "tiny/mmproj-Tiny-F16.gguf", context = 16384, maxOutput = 4096,
        kvType = "q8_0", parallel = 2, extraPreset = "flash-attn = on\n# a note\ncache-reuse = 256", inputPerMtok = 0.1m,
    };

    /// <summary>The watcher checks every few seconds; tests wait for what it should reach.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 150; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"Never: {what}");
    }

    [Fact]
    public async Task The_library_tells_what_each_file_is()
    {
        var (f, _) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);
        var files = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models/library"))).EnumerateArray().ToDictionary(x => x.GetProperty("path").GetString()!);
        string Kind(string path) => files[path].GetProperty("profile").GetProperty("kind").GetString()!;
        Assert.Equal(
            ["big/Big-00001-of-00002.gguf", "big/mtp-Big-Q8_0.gguf", "embed/nomic-embed-text-v1.5.f16.gguf", "image/flux-2-klein-4b-Q4_0.gguf", "other/mmproj-Other-F16.gguf",
             "part/Part-00001-of-00003.gguf", "plain/Plain-Q8_0.gguf", "tiny/Tiny-4B-Q4_K_M.gguf", "tiny/mmproj-Tiny-F16.gguf"],
            files.Keys.Order(StringComparer.Ordinal));
        var tiny = files["tiny/Tiny-4B-Q4_K_M.gguf"].GetProperty("profile");
        Assert.Equal("language", tiny.GetProperty("kind").GetString());
        Assert.Equal("dense", tiny.GetProperty("structure").GetString());
        Assert.Equal(40960, tiny.GetProperty("trainedContext").GetInt32());
        Assert.Equal("Q4_K_M", tiny.GetProperty("quant").GetString());
        Assert.True(tiny.GetProperty("thinking").GetBoolean());
        Assert.True(tiny.GetProperty("tools").GetBoolean());
        // 4 layers of 2 heads of 128 keys and 128 values, a cache of 8 bits (34 bytes a block of 32).
        Assert.Equal(4 * 2 * 256 * 34 / 32, tiny.GetProperty("kvBytesPerToken").GetProperty("q8_0").GetInt64());
        var big = files["big/Big-00001-of-00002.gguf"];
        Assert.Equal(2, big.GetProperty("parts").GetInt32());
        Assert.Equal("moe", big.GetProperty("profile").GetProperty("structure").GetString());
        Assert.Equal(8, big.GetProperty("profile").GetProperty("experts").GetProperty("count").GetInt32());
        // The second part's experts count too.
        Assert.Equal(5 * 8192, big.GetProperty("profile").GetProperty("experts").GetProperty("bytes").GetInt64());
        Assert.Equal("projector", Kind("tiny/mmproj-Tiny-F16.gguf"));
        Assert.Equal("draft", Kind("big/mtp-Big-Q8_0.gguf"));
        Assert.Equal("image", Kind("image/flux-2-klein-4b-Q4_0.gguf"));
        Assert.Equal("embedding", Kind("embed/nomic-embed-text-v1.5.f16.gguf"));
        Assert.Equal("unknown", Kind("part/Part-00001-of-00003.gguf"));
        Assert.Contains("1 of its 3 parts", files["part/Part-00001-of-00003.gguf"].GetProperty("profile").GetProperty("why").GetString(), StringComparison.Ordinal);
        // Heads of 72 cannot take a quantized cache (no flash attention for them); no thinking in its template.
        var plain = files["plain/Plain-Q8_0.gguf"].GetProperty("profile");
        Assert.Equal(["bf16", "f16"], plain.GetProperty("kvBytesPerToken").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.False(plain.GetProperty("thinking").GetBoolean());
    }

    [Fact]
    public async Task Each_kind_is_asked_only_what_it_has_within_its_limits()
    {
        var (f, _) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);
        async Task RefusedAsync(object body, string code)
        {
            var res = await admin.PostAsync("/api/admin/models", body);
            await StatusAssert.Is(HttpStatusCode.BadRequest, res);
            Assert.Equal(code, (await admin.JsonAsync(res)).GetProperty("status").GetString());
        }
        // Only language models: the rest are made for something else, and say what.
        await RefusedAsync(new { name = "x", file = "image/flux-2-klein-4b-Q4_0.gguf" }, "file");
        await RefusedAsync(new { name = "x", file = "embed/nomic-embed-text-v1.5.f16.gguf" }, "file");
        await RefusedAsync(new { name = "x", file = "tiny/mmproj-Tiny-F16.gguf" }, "file");
        await RefusedAsync(new { name = "x", file = "big/mtp-Big-Q8_0.gguf" }, "file");
        await RefusedAsync(new { name = "x", file = "part/Part-00001-of-00003.gguf" }, "file");
        // Its trained context is the most, unless stretched; the answer leaves room for a prompt.
        await RefusedAsync(new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 65536 }, "context");
        await RefusedAsync(new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 2048 }, "context");
        await RefusedAsync(new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 16384, maxOutput = 16000 }, "maxOutput");
        // A projector made for another width, a cache its heads cannot take, thinking its template does not have.
        await RefusedAsync(new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", projector = "other/mmproj-Other-F16.gguf" }, "projector");
        await RefusedAsync(new { name = "x", file = "plain/Plain-Q8_0.gguf", kvType = "q8_0", thinking = false, tools = false }, "kvType");
        await RefusedAsync(new { name = "x", file = "plain/Plain-Q8_0.gguf", kvType = "f16", thinking = true, tools = false }, "thinking");
        // A dense model has no experts; a model without a prediction layer or head does not draft.
        await RefusedAsync(new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", placement = "manual", cpuMoe = 2 }, "cpuMoe");
        await RefusedAsync(new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", mtp = true }, "mtp");
        await RefusedAsync(new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", temperature = 3.0 }, "sampling");
        // An option the engine does not know would stop it: refused, as is one the form sets.
        await RefusedAsync(new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", extraPreset = "bogus-key = 1" }, "extra");
        await RefusedAsync(new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", extraPreset = "ctx-size = 8192" }, "extra");

        // Stretched with YaRN: past its training, the preset says how.
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new { name = "tiny-long", file = "tiny/Tiny-4B-Q4_K_M.gguf", context = 65536, yarn = true }));
        // A mixture of experts placed by hand, drafting with its head, with its own sampling.
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", new
        {
            name = "big-hand", file = "big/Big-00001-of-00002.gguf", context = 131072, placement = "manual", gpuLayers = 99, cpuMoe = 3, ubatch = 4096,
            mtp = true, draftHead = "big/mtp-Big-Q8_0.gguf", draftMax = 2, temperature = 0.6, topK = 20,
        }));
        var presets = await File.ReadAllTextAsync(Path.Combine(Config, "models.ini"));
        Assert.Contains("[tiny-long]\nmodel = /library/tiny/Tiny-4B-Q4_K_M.gguf\nctx-size = 65536\nparallel = 1\nkv-unified = true\ncache-type-k = q8_0\ncache-type-v = q8_0\nfit = on\nthreads = 12\n", presets, StringComparison.Ordinal);
        Assert.Contains("rope-scaling = yarn\nrope-scale = 1.6\nyarn-orig-ctx = 40960\n", presets, StringComparison.Ordinal);
        Assert.Contains("n-gpu-layers = 99\nn-cpu-moe = 3\nthreads = 12\nubatch-size = 4096\nbatch-size = 4096\nmodel-draft = /library/big/mtp-Big-Q8_0.gguf\nspec-type = draft-mtp\nspec-draft-n-max = 2\ntemp = 0.6\ntop-k = 20\n", presets, StringComparison.Ordinal);
        Assert.DoesNotContain("fit = on\nthreads = 12\nubatch", presets, StringComparison.Ordinal);

        // What the form shows as it is filled in: the file's kind, its limits, a start, and (without Prometheus) no memory figures.
        var advice = await admin.JsonAsync(await admin.PostAsync("/api/admin/models/advice", new { file = "big/Big-00001-of-00002.gguf", context = 999_999 }));
        Assert.Equal("moe", advice.GetProperty("profile").GetProperty("structure").GetString());
        Assert.Equal(262144, advice.GetProperty("limits").GetProperty("context").GetProperty("max").GetInt32());
        Assert.Equal(["big/mtp-Big-Q8_0.gguf"], advice.GetProperty("limits").GetProperty("draftHeads").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("q8_0", advice.GetProperty("recommended").GetProperty("kvType").GetString());
        Assert.Equal("unknown", advice.GetProperty("estimate").GetProperty("fit").GetString());
        Assert.Contains(advice.GetProperty("problems").EnumerateArray(), p => p.GetProperty("field").GetString() == "context" && p.GetProperty("error").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/models/advice", new { file = "../outside.gguf" }));
    }

    [Fact]
    public async Task An_added_model_becomes_an_engine_preset_and_a_gateway_model_and_goes_again()
    {
        var (f, gateway) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);

        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/models", new { name = "x", file = "../../etc/passwd.gguf" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/models", new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", extraPreset = "model = /etc/shadow" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/models", new { name = "x", file = "tiny/Tiny-4B-Q4_K_M.gguf", extraPreset = "[evil]" }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/models", new { name = "bad name!", file = "tiny/Tiny-4B-Q4_K_M.gguf" }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/models", new { name = "qwen3.8-flash-next", file = "tiny/Tiny-4B-Q4_K_M.gguf" }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/models", new { name = "FLUX.2-klein-4B", file = "tiny/Tiny-4B-Q4_K_M.gguf" }));

        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", Tiny));
        var presets = await File.ReadAllTextAsync(Path.Combine(Config, "models.ini"));
        Assert.Contains("[tiny-b]\nmodel = /library/tiny/Tiny-4B-Q4_K_M.gguf\nmmproj = /library/tiny/mmproj-Tiny-F16.gguf\nctx-size = 16384\nparallel = 2\nkv-unified = true\n", presets, StringComparison.Ordinal);
        Assert.Contains("fit = on\nthreads = 12\njinja = true\nmetrics = true\nflash-attn = on\ncache-reuse = 256\n", presets, StringComparison.Ordinal);
        Assert.DoesNotContain("a note", presets, StringComparison.Ordinal);
        var added = Assert.Single(gateway.Managed.Values);
        Assert.Equal("tiny-b", added.Name);
        Assert.Equal("openai/tiny-b", added.Params["model"]!.GetValue<string>());
        Assert.Equal("os.environ/ENGINE_API_KEY", added.Params["api_key"]!.GetValue<string>());
        Assert.True(added.Info["supports_vision"]!.GetValue<bool>());
        Assert.Equal(1e-7m, added.Params["input_cost_per_token"]!.GetValue<decimal>());

        // The engine has it once it has read the presets; the page shows it with its settings.
        Assert.Equal("local", Row(await ModelsAsync(admin), "tiny-b").GetProperty("source").GetString());
        await EventuallyAsync(async () => Row(await ModelsAsync(admin), "tiny-b").GetProperty("status").GetString() == "unloaded", "the engine lists tiny-b");

        // A change the gateway must know of (the context) registers it again; one it need not (parallel) does not.
        var first = gateway.Managed.Keys.Single();
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny-b", UriKind.Relative), new { parallel = 4 }));
        Assert.Equal(first, gateway.Managed.Keys.Single());
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny-b", UriKind.Relative), new { context = 32768 }));
        Assert.NotEqual(first, gateway.Managed.Keys.Single());
        Assert.Contains("ctx-size = 32768", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);
        // A value left out is kept; one named in "clear" is emptied.
        Assert.Equal(4096, Row(await ModelsAsync(admin), "tiny-b").GetProperty("maxOutput").GetInt32());
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PatchAsJsonAsync(new Uri("/api/admin/models/tiny-b", UriKind.Relative), new { clear = new[] { "maxOutput", "inputPerMtok" } }));
        var cleared = Row(await ModelsAsync(admin), "tiny-b");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("maxOutput").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("inputPerMtok").ValueKind);
        // Without a longest answer: half the context.
        Assert.Equal(16384, gateway.Managed.Values.Single().Info["max_output_tokens"]!.GetValue<int>());

        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.DeleteAsync(new Uri("/api/admin/models/tiny-b", UriKind.Relative)));
        Assert.DoesNotContain("[tiny-b]", await File.ReadAllTextAsync(Path.Combine(Config, "models.ini")), StringComparison.Ordinal);
        Assert.Empty(gateway.Managed);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("model.add", audit);
        Assert.Contains("model.remove", audit);
    }

    [Fact]
    public async Task A_switch_is_live_remembered_and_kept_after_the_engine_restarts()
    {
        var (f, _) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", Tiny));
        await EventuallyAsync(async () => Row(await ModelsAsync(admin), "tiny-b").GetProperty("status").GetString() == "unloaded", "the engine lists tiny-b");

        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/load"));
        Assert.Equal("loaded", app.Engine.StatusOf("tiny-b"));
        Assert.Equal("unloaded", app.Engine.StatusOf("Qwen3.8-Flash-Next"));
        Assert.Equal("tiny-b", await File.ReadAllTextAsync(Path.Combine(Config, "active")));
        await EventuallyAsync(async () => (await ModelsAsync(admin)).GetProperty("engine").GetProperty("loaded").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["tiny-b"]), "the page shows tiny-b loaded");
        await EventuallyAsync(async () => (await File.ReadAllTextAsync(Path.Combine(Config, "targets.json"))).Contains("\"model\":\"tiny-b\"", StringComparison.Ordinal), "Prometheus scrapes tiny-b");

        // The engine restarts with nothing loaded: the watcher loads the model chosen last.
        app.Engine.Restart();
        await EventuallyAsync(() => Task.FromResult(app.Engine.StatusOf("tiny-b") == "loaded"), "tiny-b loaded again after the restart");

        // Unloaded on purpose: nothing comes back.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/unload"));
        Assert.Equal("-", await File.ReadAllTextAsync(Path.Combine(Config, "active")));
        await Task.Delay(1500);
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-b"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync("/api/admin/models/nothing-like-it/load"));
    }

    [Fact]
    public async Task A_model_that_fails_to_load_is_not_tried_again_and_the_env_model_takes_its_place()
    {
        var (f, _) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", Tiny));
        await EventuallyAsync(async () => Row(await ModelsAsync(admin), "tiny-b").GetProperty("status").GetString() == "unloaded", "the engine lists tiny-b");

        // Its file is incomplete: the load unloads the model before it, then fails.
        app.Engine.Broken.Add("tiny-b");
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/load"));
        await EventuallyAsync(() => Task.FromResult(app.Engine.StatusOf("Qwen3.8-Flash-Next") == "loaded"), "the .env model loaded in its place");
        Assert.Equal("Qwen3.8-Flash-Next", await File.ReadAllTextAsync(Path.Combine(Config, "active")));
        await EventuallyAsync(async () => Row(await ModelsAsync(admin), "tiny-b").GetProperty("status").GetString() == "failed", "the page shows tiny-b failed");
        Assert.Equal(1, app.Engine.LoadsOf("tiny-b"));

        // Nothing else to fall back to: it is not tried again every few seconds.
        app.Engine.Broken.Add("Qwen3.8-Flash-Next");
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/Qwen3.8-Flash-Next/load"));
        await Task.Delay(1500);
        Assert.Equal(2, app.Engine.LoadsOf("Qwen3.8-Flash-Next"));
        Assert.Equal("failed", Row(await ModelsAsync(admin), "Qwen3.8-Flash-Next").GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_model_is_for_whom_an_admin_says_and_one_not_loaded_says_so()
    {
        var (f, gateway) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/models", Tiny));
        await EventuallyAsync(async () => Row(await ModelsAsync(admin), "tiny-b").GetProperty("atGateway").GetBoolean(), "tiny-b at the gateway");

        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = "modelmember", email = "modelmember@example.test" }));
        var member = await new TestBrowser(f).SignedInAsync("modelmember", made.GetProperty("password").GetString()!);
        async Task<Dictionary<string, bool>> ChatModelsAsync(TestBrowser b) =>
            (await b.JsonAsync(await b.GetAsync("/api/chat/config"))).GetProperty("models").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("loaded").GetBoolean());
        await EventuallyAsync(async () => (await ChatModelsAsync(member)).GetValueOrDefault("tiny-b") == false, "tiny-b listed, not loaded");
        Assert.True((await ChatModelsAsync(member))["Qwen3.8-Flash-Next"]);

        // Only a group may use tiny-b.
        var group = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Tinkerers" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/models/tiny-b/access", UriKind.Relative), new { audience = "Groups", groups = new[] { group } }));
        Assert.DoesNotContain("tiny-b", (await ChatModelsAsync(member)).Keys);
        Assert.Contains("tiny-b", (await ChatModelsAsync(admin)).Keys);
        await StatusAssert.Is(HttpStatusCode.Forbidden, await member.PostAsync("/api/chat/conversations", new { model = "tiny-b" }));

        // Their API key carries the models they may use: LiteLLM refuses the rest.
        await EventuallyAsync(() => Task.FromResult(gateway.KeysOf("modelmember@example.test").Single().Models.Order().SequenceEqual(["FLUX.2-klein-4B", "Qwen3.8-Flash-Next"])), "the member's key without tiny-b");
        Assert.All(gateway.KeysOf("admin@llm.test"), k => Assert.Empty(k.Models));

        // In the group, the member may choose it; while it is not loaded, the answer says so.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.PostAsync($"/api/admin/groups/{group}/members", new { userIds = new[] { made.GetProperty("id").GetGuid() } }));
        await EventuallyAsync(() => Task.FromResult(gateway.KeysOf("modelmember@example.test").Single().Models.Count == 0), "the member's key back to every model");
        var chat = (await member.JsonAsync(await member.PostAsync("/api/chat/conversations", new { model = "tiny-b", useArgus = false }))).GetProperty("id").GetGuid();
        var answer = await (await member.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = "hello" })).Content.ReadAsStringAsync();
        Assert.Contains("tiny-b is not loaded right now", answer, StringComparison.Ordinal);

        // Loaded (the .env model goes), a chat that chose no model gets the loaded one.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/load"));
        await EventuallyAsync(async () => (await ChatModelsAsync(member)).GetValueOrDefault("tiny-b"), "tiny-b loaded for the chat");
        var fresh = (await member.JsonAsync(await member.PostAsync("/api/chat/conversations", new { useArgus = false }))).GetProperty("id").GetGuid();
        await (await member.PostAsync($"/api/chat/conversations/{fresh}/messages", new { content = "which model?" })).Content.ReadAsStringAsync();
        Assert.Equal("tiny-b", app.Model.Requests.Last(r => r.Body["user"]!.GetValue<string>() == "modelmember@example.test").Body["model"]!.GetValue<string>());
        Assert.Equal("tiny-b", (await member.JsonAsync(await member.GetAsync("/api/chat/config"))).GetProperty("model").GetString());
    }
}
