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

    /// <summary>Its own app, database and engine files, with a library of real (small) GGUF files.</summary>
    private (WebApplicationFactory<Program> App, FakeGateway Gateway) NewApp()
    {
        GgufFile.Write(Path.Combine(Library, "tiny", "Tiny-4B-Q4_K_M.gguf"), "qwen3", "Tiny", "4B", 40960, padding: 1000);
        GgufFile.Write(Path.Combine(Library, "tiny", "mmproj-Tiny-F16.gguf"), "clip", "Tiny vision");
        GgufFile.Write(Path.Combine(Library, "image", "flux-2-klein-4b-Q4_0.gguf"), "flux");
        GgufFile.Write(Path.Combine(Library, "big", "Big-00001-of-00002.gguf"), "qwen3moe", "Big", "80B", 262144, padding: 10);
        File.WriteAllBytes(Path.Combine(Library, "big", "Big-00002-of-00002.gguf"), new byte[5000]);
        Directory.CreateDirectory(Config);
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        var gateway = new FakeGateway();
        gateway.Models.Add(new GatewayModel("FLUX.2-klein-4B", null, null, false, false, false, null, null, null, Mode: "image_generation"));
        var f = app.Create(app.ConnectionStringFor("models_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = Library,
        });
        return (f, gateway);
    }

    private static Task<TestBrowser> AdminAsync(WebApplicationFactory<Program> f) => new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

    private static async Task<JsonElement> ModelsAsync(TestBrowser admin) => await admin.JsonAsync(await admin.GetAsync("/api/admin/models"));

    private static JsonElement Row(JsonElement list, string name) => list.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name);

    private static readonly object Tiny = new
    {
        name = "tiny-b", file = "tiny/Tiny-4B-Q4_K_M.gguf", projector = "tiny/mmproj-Tiny-F16.gguf", context = 16384, maxOutput = 4096,
        gpuLayers = 99, cpuMoe = 0, kvType = "q8_0", parallel = 2, extraPreset = "flash-attn = on\n# a note\nubatch-size = 512", inputPerMtok = 0.1m,
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
    public async Task The_library_lists_gguf_files_for_what_they_are()
    {
        var (f, _) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);
        var files = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models/library"))).EnumerateArray().ToDictionary(x => x.GetProperty("path").GetString()!);
        Assert.Equal(["big/Big-00001-of-00002.gguf", "image/flux-2-klein-4b-Q4_0.gguf", "tiny/Tiny-4B-Q4_K_M.gguf", "tiny/mmproj-Tiny-F16.gguf"], files.Keys.Order(StringComparer.Ordinal));
        var tiny = files["tiny/Tiny-4B-Q4_K_M.gguf"];
        Assert.Equal("model", tiny.GetProperty("role").GetString());
        Assert.Equal(40960, tiny.GetProperty("trainedContext").GetInt32());
        Assert.Equal("4B", tiny.GetProperty("sizeLabel").GetString());
        Assert.Equal("projector", files["tiny/mmproj-Tiny-F16.gguf"].GetProperty("role").GetString());
        Assert.Equal("other", files["image/flux-2-klein-4b-Q4_0.gguf"].GetProperty("role").GetString());
        var big = files["big/Big-00001-of-00002.gguf"];
        Assert.Equal(2, big.GetProperty("parts").GetInt32());
        Assert.True(big.GetProperty("size").GetInt64() > 5000);
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
        Assert.Contains("[tiny-b]\nmodel = /library/tiny/Tiny-4B-Q4_K_M.gguf\nmmproj = /library/tiny/mmproj-Tiny-F16.gguf\nctx-size = 16384\n", presets, StringComparison.Ordinal);
        Assert.Contains("parallel = 2\njinja = true\nmetrics = true\nflash-attn = on\nubatch-size = 512\n", presets, StringComparison.Ordinal);
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
        Assert.Equal(32768, gateway.Managed.Values.Single().Info["max_output_tokens"]!.GetValue<int>());

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
