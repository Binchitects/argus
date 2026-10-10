using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// "The chat says no model": what the chat is told of a model of the engine while models load, unload and swap, with the
/// engine as deploy/services/llamacpp/router.sh runs it: llama-server always loads a model a request asks for
/// (--models-autoload), the app having made room first.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ModelStateChatNoModelTests(AppFixture app) : IDisposable
{
    private const string Big = "Qwen3.8-Flash-Next";

    /// <summary>No step waits longer than this: past it the test fails instead of hanging.</summary>
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(90);

    private readonly string _dir = Directory.CreateTempSubdirectory("llm-nomodel-").FullName;

    private string Config => Path.Combine(_dir, "config");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>The engine's models: the sections of the presets the app writes.</summary>
    private static HashSet<string> EngineModels(string config)
    {
        var presets = Path.Combine(config, "models.ini");
        return File.Exists(presets) ? [.. File.ReadAllLines(presets).Where(l => l.StartsWith('[') && l.EndsWith(']')).Select(l => l[1..^1])] : [];
    }

    /// <summary>router.sh runs llama-server with --models-autoload, whatever is kept.</summary>
    private static bool Autoload(string config) => EngineModels(config).Count > 0;

    /// <summary>
    /// The gateway and the router behind it, for the chat's requests: a request for a model of the engine that is not
    /// loaded has the router load it (at its limit the one used least recently unloads) while it loads models on
    /// request, and is refused while it does not (router.sh, every place kept). The answer itself is the fake model's.
    /// </summary>
    private sealed class Router(AppFixture app, string config) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _model = new(app.Model, disposeHandler: false);
        private readonly HttpMessageInvoker _engine = new(app.Engine, disposeHandler: false);

        /// <summary>The models whose requests the router refused as not loaded.</summary>
        public ConcurrentQueue<string> Refused { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null && request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
                && JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?["model"]?.GetValue<string>() is { } model
                && EngineModels(config).Contains(model) && app.Engine.StatusOf(model) != "loaded")
            {
                if (!Autoload(config))
                {
                    Refused.Enqueue(model);
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent(new JsonObject
                        {
                            ["error"] = new JsonObject
                            {
                                ["message"] = $"litellm.BadRequestError: OpenAIException - model is not loaded. Received Model Group={model}\nAvailable Model Group Fallbacks=None",
                                ["code"] = "400",
                            },
                        }.ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                }
                using var load = new HttpRequestMessage(HttpMethod.Post, new Uri("http://llamacpp:8080/models/load"))
                {
                    Content = new StringContent(new JsonObject { ["model"] = model }.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                load.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FakeEngine.Key);
                using var _ = await _engine.SendAsync(load, cancellationToken);
            }
            return await _model.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// The engine as the app asks it, the models in <see cref="Loading"/> listed loading while they are not loaded yet
    /// (after a restart router.sh loads the kept models one after another; a big one takes a while).
    /// </summary>
    private sealed class SlowEngine(FakeEngine engine) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(engine, disposeHandler: false);

        public ConcurrentDictionary<string, bool> Loading { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var res = await _inner.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath != "/models" || Loading.IsEmpty || !res.IsSuccessStatusCode)
            {
                return res;
            }
            var list = JsonNode.Parse(await res.Content.ReadAsStringAsync(cancellationToken))!;
            res.Dispose();
            foreach (var m in (list["data"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Loading.ContainsKey(m["id"]!.GetValue<string>()) && m["status"]?["value"]?.GetValue<string>() == "unloaded")
                {
                    m["status"] = new JsonObject { ["value"] = "loading" };
                }
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(list.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>
    /// Its own app, database and engine files: the engine on, holding 2 models at once, Qwen3.8-Flash-Next (the biggest
    /// file, the model new chats use) and <paramref name="others"/> added here, <paramref name="keep"/> kept loaded.
    /// </summary>
    private (WebApplicationFactory<Program> App, Router Router, SlowEngine Engine) NewApp(string[] keep, params string[] others)
    {
        var library = Path.Combine(_dir, "library");
        GgufFile.Language("qwen35", name: "Flash", layers: 8, interval: 4, layerBytes: 1 << 16)
            .U32("qwen35.ssm.state_size", 128).U32("qwen35.ssm.inner_size", 1024).U32("qwen35.ssm.group_count", 4).U32("qwen35.ssm.conv_kernel", 4)
            .Write(Path.Combine(library, "flash", "Flash-Q4_K_M.gguf"));
        for (var i = 0; i < others.Length; i++)
        {
            GgufFile.Language("qwen3", name: others[i], layerBytes: 4096L << i).Write(Path.Combine(library, others[i], "Tiny-Q4_K_M.gguf"));
        }
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, "keep"), string.Concat(keep.Select(k => k + "\n")));
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        app.Engine.Max = 2;
        var router = new Router(app, Config);
        var engine = new SlowEngine(app.Engine);
        var gateway = new FakeGateway();
        // The app registers its models at the gateway itself.
        gateway.Models.RemoveAll(m => m.Name == Big);
        var settings = new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = library, ["Engine:ModelsMax"] = "2",
        };
        var f = app.Create(app.ConnectionStringFor("nomodel_" + Guid.NewGuid().ToString("N")[..8]), gateway, settings, s =>
        {
            s.AddHttpClient<GatewayChat>().ConfigurePrimaryHttpMessageHandler(() => router);
            s.AddHttpClient<EngineClient>().ConfigurePrimaryHttpMessageHandler(() => engine);
        });
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        db.LocalModels.Add(new LocalModel { Name = Big, File = "flash/Flash-Q4_K_M.gguf", Context = 8192, Parallel = 1 });
        foreach (var name in others)
        {
            db.LocalModels.Add(new LocalModel { Name = name, File = $"{name}/Tiny-Q4_K_M.gguf", Context = 8192, Parallel = 1 });
        }
        db.SaveChanges();
        // As an admin's save does: the engine's presets and the gateway at once.
        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        catalog.WritePresetsAsync().GetAwaiter().GetResult();
        catalog.SyncGatewayAsync().GetAwaiter().GetResult();
        return (f, router, engine);
    }

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300; i++)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"Never: {what}");
    }

    private static async Task<TestBrowser> PersonAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var name = "n" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
    }

    /// <summary>A new chat on <paramref name="model"/>; null: it chooses none (the model new chats use).</summary>
    private static async Task<Guid> ChatAsync(TestBrowser b, string? model)
    {
        object body = model is null ? new { thinking = "off", tools = Array.Empty<string>() } : new { model, thinking = "off", tools = Array.Empty<string>() };
        return (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", body))).GetProperty("id").GetGuid();
    }

    /// <summary>A message and what the chat said back (the stream's events), within <see cref="Deadline"/>.</summary>
    private static async Task<(HttpStatusCode Status, List<JsonElement> Events, string Raw)> AskAsync(TestBrowser b, Guid chat, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text }).WaitAsync(Deadline);
        var raw = await res.Content.ReadAsStringAsync().WaitAsync(Deadline);
        List<JsonElement> events = [.. raw.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l[6..]).RootElement.Clone())];
        return (res.StatusCode, events, raw);
    }

    private static bool Done((HttpStatusCode Status, List<JsonElement> Events, string Raw) a) => a.Events.Any(e => e.GetProperty("type").GetString() == "done");

    /// <summary>The errors the chat showed, or the response itself when it was no stream.</summary>
    private static string Said((HttpStatusCode Status, List<JsonElement> Events, string Raw) a)
    {
        var errors = a.Events.Where(e => e.GetProperty("type").GetString() == "error").Select(e => e.GetProperty("message").GetString()).ToList();
        return errors.Count > 0 ? string.Join(" | ", errors) : $"HTTP {(int)a.Status}: {(a.Raw.Length > 400 ? a.Raw[..400] : a.Raw)}";
    }

    private static string? ModelOf((HttpStatusCode Status, List<JsonElement> Events, string Raw) a) =>
        a.Events.FirstOrDefault(e => e.GetProperty("type").GetString() == "assistant") is { ValueKind: JsonValueKind.Object } first ? first.GetProperty("model").GetString() : null;

    [Fact]
    public async Task Right_after_an_admin_unloads_another_model_and_through_a_swap_the_chat_is_answered_and_never_told_its_model_is_not_there()
    {
        var (f, router, _) = NewApp([Big], "tiny-a", "tiny-b");
        await using var _f = f;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var state = f.Services.GetRequiredService<EngineState>();
        // router.sh: one model kept of the two places, so the others load when a request asks for them.
        Assert.True(Autoload(Config));

        // Someone's tiny-a, loaded beside the big one kept loaded.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-a/load"));
        await EventuallyAsync(() => state.Spare.SequenceEqual(["tiny-a"]), "the watcher sees tiny-a loaded, and that it may make room");

        // An admin unloads it; the router lists it loaded until it has stopped (within 10 s: here 3).
        app.Engine.SlowStop = true;
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-a/unload"));
        var stops = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => app.Engine.Stop(), TaskScheduler.Default);

        // Right after: a chat on tiny-b (not loaded), which waits for tiny-a to have stopped (the engine would otherwise
        // unload the big one to load it), then a chat that chose no model.
        var b = await PersonAsync(f);
        var onB = await AskAsync(b, await ChatAsync(b, "tiny-b"), "hello b");
        await stops;
        Assert.True(Done(onB), "tiny-b right after tiny-a was unloaded: " + Said(onB));
        var none = await AskAsync(b, await ChatAsync(b, null), "hello default");
        Assert.True(Done(none), "no model chosen right after tiny-a was unloaded: " + Said(none));
        Assert.Equal(Big, ModelOf(none));

        // tiny-a has stopped; asked for again, the idle tiny-b makes room for it (a normal swap).
        Assert.Equal("loaded", app.Engine.StatusOf(Big));
        await EventuallyAsync(() => state.Spare.SequenceEqual(["tiny-b"]), "the watcher sees tiny-b loaded");
        var onA = await AskAsync(b, await ChatAsync(b, "tiny-a"), "hello a");
        Assert.True(Done(onA), "tiny-a, swapped back in for the idle tiny-b: " + Said(onA));
        Assert.Equal("loaded", app.Engine.StatusOf("tiny-a"));
        Assert.Equal("loaded", app.Engine.StatusOf(Big));
        Assert.Empty(router.Refused);
    }

    [Fact]
    public async Task With_every_place_kept_a_chat_while_the_kept_models_load_again_is_answered()
    {
        var (f, router, engine) = NewApp([Big, "tiny-b"], "tiny-b");
        await using var _f = f;
        var state = f.Services.GetRequiredService<EngineState>();
        await EventuallyAsync(() => state.Held.SequenceEqual([Big, "tiny-b"]) && app.Engine.StatusOf("tiny-b") == "loaded", "both kept models loaded");
        // router.sh: every place kept, and llama-server still loads what a request asks for.
        Assert.True(Autoload(Config));

        // The engine restarts (the presets changed): router.sh loads the kept models again one after another, the big one
        // first, which takes a while.
        engine.Loading[Big] = true;
        app.Engine.Restart();
        f.Services.GetRequiredService<EngineWatcher>().Wake();
        await EventuallyAsync(() => state.StatusOf(Big) == "loading" && state.StatusOf("tiny-b") == "unloaded", "the app sees the engine load the kept models again");

        var b = await PersonAsync(f);
        var config = await b.JsonAsync(await b.GetAsync("/api/chat/config"));
        string Offered(string name) => config.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == name) is var m
            ? $"{name}: loaded={m.GetProperty("loaded").GetBoolean()}, onRequest={m.GetProperty("onRequest").GetBoolean()}"
            : "";
        var loadsBefore = app.Engine.LoadsOf("tiny-b");

        // A chat on the kept tiny-b, and one that chose no model (the big one, loading).
        var onB = await AskAsync(b, await ChatAsync(b, "tiny-b"), "hello b");
        var none = await AskAsync(b, await ChatAsync(b, null), "hello default");

        // The chat offers both ("loads when asked"): each is answered once its model is loaded (the app loads it, or
        // waits for it), never sent to an engine that does not load it on request.
        Assert.True(Done(onB) && Done(none),
            $"offered: {Offered("tiny-b")}; {Offered(Big)}. The app's loads of tiny-b meanwhile: {app.Engine.LoadsOf("tiny-b") - loadsBefore}. " +
            $"Refused by the engine as not loaded: [{string.Join(", ", router.Refused)}]. The chat on tiny-b said: {(Done(onB) ? "(answered)" : Said(onB))}. " +
            $"The chat with no model ({ModelOf(none)}) said: {(Done(none) ? "(answered)" : Said(none))}");
    }
}
