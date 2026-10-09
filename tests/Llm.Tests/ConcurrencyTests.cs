using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Api.Models;
using Llm.Core.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Llm.Tests;

/// <summary>
/// People at once: each model has its own line (people on one model never wait for another's), and
/// each conversation keeps its engine slot, so the engine reads a turn's prompt from its cache.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ConcurrencyTests(AppFixture app) : IDisposable
{
    private const string Big = "Qwen3.8-Flash-Next";
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-slots-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class StaticOptions(ChatOptions value) : IOptionsMonitor<ChatOptions>
    {
        public ChatOptions CurrentValue => value;
        public ChatOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<ChatOptions, string?> listener) => null;
    }

    private static AnswerGate Gate(int perPerson = 1, int atOnce = 0, Dictionary<string, int>? places = null, TimeSpan? timeout = null)
    {
        var gate = new AnswerGate(new StaticOptions(new ChatOptions { AnswersPerPerson = perPerson, AnswersAtOnce = atOnce, QueueTimeout = timeout ?? TimeSpan.FromMinutes(10) }),
            TimeProvider.System);
        gate.SetPlaces(places ?? new Dictionary<string, int> { ["big"] = 1, ["small"] = 2 });
        return gate;
    }

    /// <summary>The lines one waiter was told, newest last.</summary>
    private sealed class Told
    {
        private readonly List<AnswerGate.Line> _lines = [];

        public Task Add(AnswerGate.Line line)
        {
            lock (_lines)
            {
                _lines.Add(line);
            }
            return Task.CompletedTask;
        }

        public async Task<AnswerGate.Line> LastAsync()
        {
            for (var i = 0; i < 50; i++)
            {
                lock (_lines)
                {
                    if (_lines.Count > 0)
                    {
                        return _lines[^1];
                    }
                }
                await Task.Delay(20);
            }
            throw new InvalidOperationException("never told the line");
        }
    }

    private static Task Nothing(AnswerGate.Line _) => Task.CompletedTask;

    [Fact]
    public async Task People_on_one_model_never_wait_for_another_models_line()
    {
        var gate = Gate();
        var (alice, bob, carol, dave, erin) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var a = await gate.EnterAsync(alice, "big", Nothing, default);
        Task<AnswerGate.Place> e;
        var bobTold = new Told();
        var b = gate.EnterAsync(bob, "big", bobTold.Add, default);
        Assert.Equal(new AnswerGate.Line("big", 0, 1, 1), await bobTold.LastAsync());

        // The big model is full; the small one has its own places.
        using (await gate.EnterAsync(carol, "small", Nothing, default).WaitAsync(TimeSpan.FromSeconds(5)))
        using (await gate.EnterAsync(dave, "small", Nothing, default).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            var erinTold = new Told();
            e = gate.EnterAsync(erin, "small", erinTold.Add, default);
            Assert.Equal(new AnswerGate.Line("small", 0, 2, 2), await erinTold.LastAsync());
            Assert.Equal((2, 1), gate.Now("small"));
            Assert.Equal((1, 1), gate.Now("big"));
            Assert.Equal((3, 2), gate.Now());
            // A model of no known size (a cloud one) has no line at all.
            using (await gate.EnterAsync(Guid.NewGuid(), "cloud", Nothing, default).WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.False(b.IsCompleted);
            }
            a.Dispose();
            using (await b.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.False(e.IsCompleted);
            }
            await Task.Yield();
            Assert.False(e.IsCompleted);
        }
        // Carol's and Dave's ended: Erin's turn on the small model.
        (await e.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal((0, 0), gate.Now());
    }

    [Fact]
    public async Task A_persons_own_limit_holds_across_models_and_the_line_says_so()
    {
        var gate = Gate(perPerson: 1);
        var alice = Guid.NewGuid();
        var big = await gate.EnterAsync(alice, "big", Nothing, default);
        var told = new Told();
        var small = gate.EnterAsync(alice, "small", told.Add, default);
        var line = await told.LastAsync();
        Assert.True(line.Yours);
        Assert.Equal("small", line.Model);
        // Someone else on the small model is not held by Alice's answers.
        using (await gate.EnterAsync(Guid.NewGuid(), "small", Nothing, default).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            big.Dispose();
            (await small.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        }
        Assert.Equal((0, 0), gate.Now());
    }

    [Fact]
    public async Task Within_a_model_the_line_is_in_turn_and_counts_only_its_own()
    {
        var gate = Gate(places: new Dictionary<string, int> { ["big"] = 1, ["other"] = 1 });
        var holder = await gate.EnterAsync(Guid.NewGuid(), "big", Nothing, default);
        var otherHolder = await gate.EnterAsync(Guid.NewGuid(), "other", Nothing, default);
        var (lowTold, highTold, otherTold) = (new Told(), new Told(), new Told());
        var low = gate.EnterAsync(Guid.NewGuid(), 0, "big", lowTold.Add, default);
        await lowTold.LastAsync();
        var other = gate.EnterAsync(Guid.NewGuid(), 0, "other", otherTold.Add, default);
        var high = gate.EnterAsync(Guid.NewGuid(), 5, "big", highTold.Add, default);
        await Task.Delay(1500);
        // The later, higher priority goes first; the other model's waiter is not in this line.
        Assert.Equal(0, (await highTold.LastAsync()).Ahead);
        Assert.Equal(1, (await lowTold.LastAsync()).Ahead);
        Assert.Equal(0, (await otherTold.LastAsync()).Ahead);
        holder.Dispose();
        using (await high.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.False(low.IsCompleted);
            Assert.False(other.IsCompleted);
        }
        (await low.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        otherHolder.Dispose();
        (await other.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        Assert.Equal((0, 0), gate.Now());
    }

    [Fact]
    public async Task The_whole_chats_limit_when_set_holds_every_model_and_the_line_names_none()
    {
        var gate = Gate(atOnce: 2, places: new Dictionary<string, int> { ["big"] = 5, ["small"] = 5 });
        using var one = await gate.EnterAsync(Guid.NewGuid(), "big", Nothing, default);
        using var two = await gate.EnterAsync(Guid.NewGuid(), "big", Nothing, default);
        var told = new Told();
        var third = gate.EnterAsync(Guid.NewGuid(), "small", told.Add, default);
        Assert.Equal(new AnswerGate.Line(null, 0, 2, 2), await told.LastAsync());
        two.Dispose();
        (await third.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task Each_models_places_are_shared_out_among_the_replicas()
    {
        var gate = Gate(places: new Dictionary<string, int> { ["big"] = 4, ["small"] = 1 });
        gate.Replicas = 3;
        // Four places on three replicas: two here (rounded up); one stays one.
        var one = await gate.EnterAsync(Guid.NewGuid(), "big", Nothing, default);
        var two = await gate.EnterAsync(Guid.NewGuid(), "big", Nothing, default);
        var three = gate.EnterAsync(Guid.NewGuid(), "big", Nothing, default);
        using (await gate.EnterAsync(Guid.NewGuid(), "small", Nothing, default).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            await Task.Delay(100);
            Assert.False(three.IsCompleted);
        }
        gate.Replicas = 1;
        (await three.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        one.Dispose();
        two.Dispose();
    }

    [Fact]
    public async Task Waiting_too_long_gives_up_and_names_the_busy_model()
    {
        var gate = Gate(timeout: TimeSpan.FromMilliseconds(300));
        using var holder = await gate.EnterAsync(Guid.NewGuid(), "big", Nothing, default);
        var ex = await Assert.ThrowsAsync<AnswerGate.TooLongException>(() => gate.EnterAsync(Guid.NewGuid(), "big", Nothing, default));
        Assert.StartsWith("big has been busy", ex.Message, StringComparison.Ordinal);
        Assert.Equal((1, 0), gate.Now());
    }

    [Fact]
    public void Each_model_has_the_places_of_all_its_slots_or_of_all_its_copies()
    {
        var (places, slots) = EngineWatcher.Capacity(
            [("big", 4), ("two", 2), ("one", 1), ("pooled", 2), ("vague", 2)],
            [("pooled", 4), ("vague", null), ("far", 8), ("far", 8), ("cloudy", null)]);
        // Parallel times copies: every slot serves answers, the side requests' one too (4 slots: 4 answers at once).
        Assert.Equal(new Dictionary<string, int> { ["big"] = 4, ["two"] = 2, ["one"] = 1, ["pooled"] = 6, ["far"] = 16 }, places);
        // Slots are chosen only for a model on this engine alone: the gateway picks among copies.
        Assert.Equal(new Dictionary<string, int> { ["big"] = 4, ["two"] = 2, ["one"] = 1 }, slots);
    }

    private static readonly IReadOnlySet<string> Loaded = new HashSet<string> { "m", "two", "one", "other" };

    /// <summary>What the engine says of a model's slots now: how many it has, and which answer requests not sent from the table.</summary>
    private static SlotTable.Seen Engine(int count, params int[] busy) => new(count, busy.ToHashSet(), TimeProvider.System.GetTimestamp());

    private static SlotTable Table()
    {
        var table = new SlotTable(TimeProvider.System);
        table.SetModels(new Dictionary<string, int> { ["m"] = 4, ["two"] = 2, ["one"] = 1, ["other"] = 3 }, Loaded);
        return table;
    }

    [Fact]
    public void A_conversation_goes_back_to_its_slot_and_a_new_one_to_the_least_recently_used()
    {
        var table = Table();
        var (a, b, c, d) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        int? Turn(Guid who)
        {
            using var lease = table.Take("m", who);
            return lease.Slot;
        }
        Assert.Equal(0, Turn(a));
        Assert.Equal(1, Turn(b));
        Assert.Equal(0, Turn(a));
        Assert.Equal(2, Turn(c));
        Assert.Equal(1, Turn(b));
        // A new conversation takes the slot used least recently (A's), not the side one.
        Assert.Equal(0, Turn(d));
        // A lost its slot: it takes the least recently used now (C's).
        Assert.Equal(2, Turn(a));
        Assert.Equal(1, Turn(b));
    }

    [Fact]
    public void A_busy_slot_is_never_chosen_and_with_none_idle_the_engine_chooses()
    {
        var table = Table();
        var (a, b, c, d) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using (var held = table.Take("m", a))
        {
            Assert.Equal(0, held.Slot);
            using var other = table.Take("m", b);
            Assert.Equal(1, other.Slot);
            // Slot 2 is busy with a request the table did not send (an API key's): the side requests' slot is the
            // only one idle, and an answer takes it rather than wait (every slot serves answers).
            using var last = table.Take("m", c, Engine(4, 2));
            Assert.Equal(3, last.Slot);
            // None is idle: the engine gives it the first that frees.
            using var none = table.Take("m", d, Engine(4, 2));
            Assert.Null(none.Slot);
        }
        // A's slot is busy elsewhere: A goes to another idle one, not the side requests' while others are idle.
        using (var moved = table.Take("m", a, Engine(4, 0)))
        {
            Assert.NotEqual(0, moved.Slot);
            Assert.NotEqual(3, moved.Slot);
        }
        // Busy when the engine was asked, and taken and given back here since: what it saw was the table's own request.
        var seen = Engine(4, 0, 1, 2);
        table.Take("m", b).Dispose();
        using (var back = table.Take("m", b, seen))
        {
            Assert.Equal(1, back.Slot);
        }
        // A slot the engine does not have (its slots were raised, it has not restarted) is never chosen.
        Assert.Null(table.Take("m", Guid.NewGuid(), Engine(1, 0)).Slot);
    }

    [Fact]
    public void Side_requests_go_to_their_slot_first_and_while_it_is_busy_to_another_idle_one()
    {
        var table = Table();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        table.Take("m", a).Dispose();
        table.Take("m", b).Dispose();
        using (var summary = table.Take("m", null))
        using (var check = table.Take("m", null))
        using (var title = table.Take("m", null))
        {
            // A long summary holds the side requests' slot: the safeguards' check does not wait behind it, it takes
            // an idle slot no conversation holds; the title then the one used least recently (A's, not B's).
            Assert.Equal(3, summary.Slot);
            Assert.Equal(2, check.Slot);
            Assert.Equal(0, title.Slot);
            using var answer = table.Take("m", b);
            Assert.Equal(1, answer.Slot);
            // None idle: the engine places it.
            Assert.Null(table.Take("m", null).Slot);
        }
        // The side requests' slot busy with an API key's request: another idle one.
        using (var elsewhere = table.Take("m", null, Engine(4, 3)))
        {
            Assert.NotNull(elsewhere.Slot);
            Assert.NotEqual(3, elsewhere.Slot);
        }
        // B kept its slot.
        Assert.Equal(1, table.Take("m", b).Slot);
        Assert.Equal(2, table.Take("other", null).Slot);
        // Two slots: both for conversations, side requests go where the engine puts them.
        Assert.Null(table.Take("two", null).Slot);
        Assert.Equal(0, table.Take("two", Guid.NewGuid()).Slot);
        Assert.Null(table.Take("one", Guid.NewGuid()).Slot);
        Assert.Null(table.Take("one", null).Slot);
        Assert.Null(table.Take("unknown", Guid.NewGuid()).Slot);
        Assert.Null(table.Take(null, Guid.NewGuid()).Slot);
    }

    [Fact]
    public void A_conversation_that_moves_models_keeps_its_slot_on_each()
    {
        var table = Table();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        table.Take("m", b).Dispose();
        table.Take("m", a).Dispose();
        // Auto sends one question to the other model, then the next back.
        using (var there = table.Take("other", a))
        {
            Assert.Equal(0, there.Slot);
        }
        using (var back = table.Take("m", a))
        {
            Assert.Equal(1, back.Slot);
        }
        // The model was unloaded: its slots are empty, and the table starts again.
        table.SetModels(new Dictionary<string, int> { ["m"] = 4, ["other"] = 3 }, new HashSet<string> { "other" });
        table.SetModels(new Dictionary<string, int> { ["m"] = 4, ["other"] = 3 }, new HashSet<string> { "m", "other" });
        Assert.Equal(0, table.Take("m", b).Slot);
        Assert.Equal(0, table.Take("other", a).Slot);
        // A model whose slots changed starts again too; one no longer named is forgotten.
        table.SetModels(new Dictionary<string, int> { ["m"] = 2 }, new HashSet<string> { "m" });
        Assert.Equal(2, table.Count("m"));
        Assert.Equal(0, table.Count("other"));
    }

    [Fact]
    public void Presets_keep_idle_slots_and_checkpoint_hybrid_models_unless_the_extra_lines_say_otherwise()
    {
        var hybrid = new ModelProfile { Kind = "language", Attention = "hybrid", RecurrentBytesPerSlot = 117_669_888 };
        var recurrent = new ModelProfile { Kind = "language", Attention = "recurrent" };
        var full = new ModelProfile { Kind = "language", Attention = "full" };
        string Preset(ModelProfile? p, string? extra = null) => ModelCatalog.Preset(new LocalModel { Name = "a", File = "a/A.gguf", Parallel = 4, ExtraPreset = extra }, "/library", null, p);

        Assert.Contains("cache-reuse = 256\nno-cache-idle-slots = true\nctx-checkpoints = 4\n", Preset(hybrid), StringComparison.Ordinal);
        Assert.Contains("ctx-checkpoints = 4\n", Preset(recurrent), StringComparison.Ordinal);
        Assert.Contains("no-cache-idle-slots = true\n", Preset(full), StringComparison.Ordinal);
        Assert.DoesNotContain("ctx-checkpoints", Preset(full), StringComparison.Ordinal);
        Assert.DoesNotContain("ctx-checkpoints", Preset(null), StringComparison.Ordinal);

        // The extra lines win, once each.
        var own = Preset(hybrid, "ctx-checkpoints = 8\ncache-idle-slots = true");
        Assert.Contains("ctx-checkpoints = 8\n", own, StringComparison.Ordinal);
        Assert.DoesNotContain("ctx-checkpoints = 4", own, StringComparison.Ordinal);
        Assert.DoesNotContain("no-cache-idle-slots", own, StringComparison.Ordinal);
        Assert.DoesNotContain("ctx-checkpoints", Preset(hybrid, "swa-checkpoints = 2").Replace("swa-checkpoints = 2", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Single(Preset(hybrid, "no-cache-idle-slots = true").Split('\n'), l => l.StartsWith("no-cache-idle-slots", StringComparison.Ordinal));

        // What the card says the cache keeps and costs.
        var model = new LocalModel { Name = "a", File = "a/A.gguf", Parallel = 4 };
        Assert.Equal(new TokenCache(4, 3, true, true, 4, 117_669_888, 16L * 117_669_888), TokenCache.Of(model, hybrid, pooled: false));
        Assert.Equal(new TokenCache(4, null, false, true, null, null, null), TokenCache.Of(model, full, pooled: true));
        model.ExtraPreset = "cache-idle-slots = true\nctx-checkpoints = 2";
        Assert.Equal(new TokenCache(4, 3, true, false, 2, 117_669_888, 8L * 117_669_888), TokenCache.Of(model, hybrid, pooled: false));
        Assert.Equal(new TokenCache(1, null, false, true, null, null, null), TokenCache.Of(new LocalModel { Name = "b", File = "b/B.gguf", Parallel = 1 }, null, false));
    }

    [Fact]
    public void Two_models_are_loaded_at_once_by_default()
    {
        Assert.Equal(2, new EngineOptions().ModelsMax);
        Assert.Equal("2", Llm.Api.Settings.SettingsCatalog.ByKey["Engine:ModelsMax"].Default);
    }

    // ---------------------------------------------------------------- through the app

    private string Config => Path.Combine(_dir, "config");

    /// <summary>
    /// Its own app, database and engine files: the engine on, holding 2 models at once, with Qwen3.8-Flash-Next (a hybrid
    /// model, the biggest file) added here with 4 slots and kept loaded (unless <paramref name="keep"/> says otherwise), and the
    /// <paramref name="others"/> (each file bigger than the one before). <paramref name="more"/>: settings of the test's own (a
    /// null value leaves the setting out).
    /// </summary>
    private WebApplicationFactory<Program> NewApp(IDictionary<string, string?>? more = null, int parallel = 4, MovableClock? clock = null,
        string[]? keep = null, params (string Name, int Parallel)[] others)
    {
        var library = Path.Combine(_dir, "library");
        GgufFile.Language("qwen35", name: "Flash", layers: 8, interval: 4, layerBytes: 1 << 16)
            .U32("qwen35.ssm.state_size", 128).U32("qwen35.ssm.inner_size", 1024).U32("qwen35.ssm.group_count", 4).U32("qwen35.ssm.conv_kernel", 4)
            .Write(Path.Combine(library, "flash", "Flash-Q4_K_M.gguf"));
        for (var i = 0; i < others.Length; i++)
        {
            GgufFile.Language("qwen3", name: others[i].Name, layerBytes: 4096L << i).Write(Path.Combine(library, others[i].Name, "Tiny-Q4_K_M.gguf"));
        }
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, "keep"), string.Concat((keep ?? [Big]).Select(k => k + "\n")));
        app.Engine.Reset(Path.Combine(Config, "models.ini"));
        app.Engine.Max = 2;
        var f = Start(app.ConnectionStringFor("slots_" + Guid.NewGuid().ToString("N")[..8]), more, clock);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
        db.LocalModels.Add(new LocalModel { Name = Big, File = "flash/Flash-Q4_K_M.gguf", Context = 8192, Parallel = parallel });
        foreach (var (name, p) in others)
        {
            db.LocalModels.Add(new LocalModel { Name = name, File = $"{name}/Tiny-Q4_K_M.gguf", Context = 8192, Parallel = p });
        }
        db.SaveChanges();
        // As an admin's save does: the engine's presets and the gateway at once (the watcher would within a minute).
        var catalog = scope.ServiceProvider.GetRequiredService<ModelCatalog>();
        catalog.WritePresetsAsync().GetAwaiter().GetResult();
        catalog.SyncGatewayAsync().GetAwaiter().GetResult();
        return f;
    }

    /// <summary>The app on <paramref name="database"/>, with the engine files of this test.</summary>
    private WebApplicationFactory<Program> Start(string database, IDictionary<string, string?>? more = null, MovableClock? clock = null)
    {
        var gateway = new FakeGateway();
        // The app registers its models at the gateway itself.
        gateway.Models.RemoveAll(m => m.Name == Big);
        var settings = new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = Config, ["Engine:LibraryDir"] = Path.Combine(_dir, "library"),
            ["Engine:ModelsMax"] = "2",
        };
        foreach (var (k, v) in more ?? new Dictionary<string, string?>())
        {
            if (v is null)
            {
                settings.Remove(k);
            }
            else
            {
                settings[k] = v;
            }
        }
        return app.Create(database, gateway, settings, clock is null ? null : s => s.AddSingleton<TimeProvider>(clock));
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
        var name = "s" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test" }));
        return await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!);
    }

    private static async Task<Guid> ChatAsync(TestBrowser b, string model) =>
        (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { model, thinking = "off", tools = Array.Empty<string>() }))).GetProperty("id").GetGuid();

    private static async Task<List<JsonElement>> AskAsync(TestBrowser b, Guid chat, string text) =>
        [.. (await (await b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text })).Content.ReadAsStringAsync())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("data: ", StringComparison.Ordinal))
            .Select(l => JsonDocument.Parse(l[6..]).RootElement.Clone())];

    /// <summary>The slot the request for a message went to (its answer, or its title), as the gateway was sent it.</summary>
    private int? SlotOf(string marker, bool title = false)
    {
        var sent = app.Model.Requests.Select(r => r.Body).Where(body =>
        {
            var messages = body["messages"]!.AsArray();
            var named = messages[0]?["content"] is JsonValue v && v.GetValue<string>().StartsWith("You name conversations", StringComparison.Ordinal);
            return named == title && messages.Any(m => m?["role"]?.GetValue<string>() == "user" && m["content"]?.ToJsonString().Contains(marker, StringComparison.Ordinal) == true);
        }).ToList();
        var request = Assert.Single(sent, b => b["messages"]!.AsArray().Last(m => m?["role"]?.GetValue<string>() == "user")!["content"]!.ToJsonString().Contains(marker, StringComparison.Ordinal));
        return request["id_slot"]?.GetValue<int>();
    }

    /// <summary>Longer than the app takes what the engine said of a model's slots as it is (EngineRoute: a second).</summary>
    private static Task SaidAgoAsync() => Task.Delay(1200);

    /// <summary>Whether the title of the chat whose first message carries <paramref name="marker"/> was asked.</summary>
    private bool Titled(string marker) => app.Model.Requests.Any(r => r.Body.ToJsonString().Contains(marker, StringComparison.Ordinal)
        && r.Body["messages"]![0]!["content"]!.ToJsonString().Contains("You name conversations", StringComparison.Ordinal));

    [Fact]
    public async Task Each_turn_goes_back_to_its_conversations_slot_side_requests_to_their_own_and_never_to_a_busy_one()
    {
        // The model for small steps is the big one here: its titles are side requests on it.
        await using var f = NewApp(new Dictionary<string, string?> { ["Chat:SmallModel"] = Big });
        var slots = f.Services.GetRequiredService<SlotTable>();
        await EventuallyAsync(() => slots.Count(Big) == 4, "the slot table knows the model's 4 slots");
        // The presets keep idle slots and checkpoint the hybrid model: its file's profile was read.
        var presets = await File.ReadAllTextAsync(Path.Combine(_dir, "config", "models.ini"));
        Assert.Contains("no-cache-idle-slots = true\nctx-checkpoints = 4\n", presets, StringComparison.Ordinal);

        var b = await PersonAsync(f);
        var (one, two) = (await ChatAsync(b, Big), await ChatAsync(b, Big));
        var tag = Guid.NewGuid().ToString("N")[..6];
        Assert.Contains(await AskAsync(b, one, $"[{tag}-a1] Hello there"), e => e.GetProperty("type").GetString() == "done");
        await EventuallyAsync(() => Titled($"{tag}-a1"), "the title was asked");
        Assert.Equal(0, SlotOf($"{tag}-a1"));
        Assert.Equal(3, SlotOf($"{tag}-a1", title: true));
        await AskAsync(b, two, $"[{tag}-b1] Another chat");
        Assert.Equal(1, SlotOf($"{tag}-b1"));
        await AskAsync(b, one, $"[{tag}-a2] And again");
        Assert.Equal(0, SlotOf($"{tag}-a2"));

        // Slot 0 is busy with an API key's request: the turn goes to an idle one, and stays there.
        app.Engine.BusySlots[Big] = [0];
        await SaidAgoAsync();
        await AskAsync(b, one, $"[{tag}-a3] Once more");
        Assert.Equal(2, SlotOf($"{tag}-a3"));
        app.Engine.BusySlots.Clear();
        await AskAsync(b, one, $"[{tag}-a4] And the last");
        Assert.Equal(2, SlotOf($"{tag}-a4"));

        // The side requests' slot is busy (an API key's long request, or a long summary): a title does not wait
        // behind it at the engine, it takes another idle slot.
        app.Engine.BusySlots[Big] = [3];
        await SaidAgoAsync();
        var four = await ChatAsync(b, Big);
        Assert.Contains(await AskAsync(b, four, $"[{tag}-d1] A fourth chat"), e => e.GetProperty("type").GetString() == "done");
        await EventuallyAsync(() => Titled($"{tag}-d1"), "the fourth title was asked");
        Assert.InRange(SlotOf($"{tag}-d1", title: true)!.Value, 0, 2);
        Assert.InRange(SlotOf($"{tag}-d1")!.Value, 0, 2);
        app.Engine.BusySlots.Clear();

        // Its slots were just raised to 4, and the engine still has its old 2 until it restarts: no request goes
        // to a slot it does not have (it would wait there for ever), the side requests' included.
        app.Engine.SlotCounts[Big] = 2;
        await SaidAgoAsync();
        var three = await ChatAsync(b, Big);
        Assert.Contains(await AskAsync(b, three, $"[{tag}-c1] A third chat"), e => e.GetProperty("type").GetString() == "done");
        await EventuallyAsync(() => Titled($"{tag}-c1"), "the third title was asked");
        Assert.True(SlotOf($"{tag}-c1", title: true) is null or < 2);
        Assert.InRange(SlotOf($"{tag}-c1")!.Value, 0, 1);
        app.Engine.SlotCounts.Clear();
        await SaidAgoAsync();

        // The engine reads a long prompt and does not say which slots are busy for seconds: the turn does not wait for
        // its word (half a second at most), and goes back to its slot by what it said a moment ago.
        await SaidAgoAsync();
        var usual = System.Diagnostics.Stopwatch.StartNew();
        await AskAsync(b, one, $"[{tag}-a5] Before the long prompt");
        usual.Stop();
        var before = SlotOf($"{tag}-a5");
        Assert.NotNull(before);
        await SaidAgoAsync();
        app.Engine.SlotsDelay = TimeSpan.FromSeconds(6);
        try
        {
            var took = System.Diagnostics.Stopwatch.StartNew();
            Assert.Contains(await AskAsync(b, one, $"[{tag}-a6] While it reads"), e => e.GetProperty("type").GetString() == "done");
            Assert.True(took.Elapsed < usual.Elapsed + TimeSpan.FromSeconds(1.3), $"the turn waited {took.Elapsed - usual.Elapsed} more for the engine's word on its slots");
            Assert.Equal(before, SlotOf($"{tag}-a6"));
        }
        finally
        {
            app.Engine.SlotsDelay = TimeSpan.Zero;
        }

        // Admin → Models says what the cache keeps and costs.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == Big);
        var cache = row.GetProperty("cache");
        Assert.Equal(4, cache.GetProperty("slots").GetInt32());
        Assert.Equal(3, cache.GetProperty("sideSlot").GetInt32());
        Assert.Equal(4, cache.GetProperty("checkpoints").GetInt32());
        Assert.True(cache.GetProperty("ramBytes").GetInt64() > 0);
    }

    [Fact]
    public async Task Each_sub_agent_keeps_a_slot_of_its_own_for_its_steps_off_the_side_one()
    {
        await using var f = NewApp(new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-slot-tests" });
        var slots = f.Services.GetRequiredService<SlotTable>();
        await EventuallyAsync(() => slots.Count(Big) == 4, "the slot table knows the model's 4 slots");
        var b = await PersonAsync(f);
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { model = Big, thinking = "off", tools = new[] { "agents", "calculator" } })))
            .GetProperty("id").GetGuid();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var plan = "[call delegate " + JsonSerializer.Serialize(new
        {
            tasks = new[]
            {
                new { title = "Sum", instructions = $$"""[{{tag}}-sum] Work it out: [call calculate {"expression":"2+2"}]""" },
                new { title = "Colour", instructions = $"[{tag}-colour] Name a colour." },
            },
        }) + "]";
        Assert.Contains(await AskAsync(b, chat, $"Plan the party {plan}"), e => e.GetProperty("type").GetString() == "done");

        List<int?> SlotsOf(string marker) => [.. app.Model.Requests.Select(r => r.Body)
            .Where(r => r["messages"]![0]!["content"]!.GetValue<string>().Contains("You are a sub-agent", StringComparison.Ordinal)
                && r["messages"]![1]!["content"]!.GetValue<string>().Contains(marker, StringComparison.Ordinal))
            .Select(r => r["id_slot"]?.GetValue<int>())];
        // The sum's two steps (its tool call, then its answer) in one slot: the second reads only the tool's result.
        var sum = SlotsOf($"{tag}-sum");
        Assert.Equal(2, sum.Count);
        Assert.Single(sum.Distinct());
        var colour = Assert.Single(SlotsOf($"{tag}-colour"));
        // Not the side requests' slot (3) while others are idle.
        Assert.All(sum.Append(colour), s => Assert.InRange(s!.Value, 0, 2));
    }

    [Fact]
    public async Task A_person_on_a_small_model_is_answered_while_the_big_one_is_full_and_who_waits_is_told_which()
    {
        await using var f = NewApp(new Dictionary<string, string?> { ["Chat:AnswersPerPerson"] = "1", ["Auth:DataKey"] = "a-data-key-for-slot-tests" }, parallel: 1, others: [("tiny-small", 1)]);
        var gate = f.Services.GetRequiredService<AnswerGate>();
        var slots = f.Services.GetRequiredService<SlotTable>();
        await EventuallyAsync(() => slots.Count(Big) == 1 && slots.Count("tiny-small") == 1, "the watcher read both models");
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // A tool whose server is slow to answer holds the big model's only place as long as the test wants.
        var made = await admin.PostAsync("/api/admin/tools/servers", new { name = "Slow Desk", url = "https://tools.example.test/mcp", headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
        var (alice, bob, carol) = (await PersonAsync(f), await PersonAsync(f), await PersonAsync(f));
        var held = (await alice.JsonAsync(await alice.PostAsync("/api/chat/conversations", new { model = Big, thinking = "off", tools = new[] { toolId } }))).GetProperty("id").GetGuid();
        var (bobs, carols) = (await ChatAsync(bob, Big), await ChatAsync(carol, "tiny-small"));

        app.Mcp.Hold = new TaskCompletionSource();
        try
        {
            var first = AskAsync(alice, held, "hello");
            await EventuallyAsync(() => gate.Now(Big) == (1, 0), "Alice's answer holds the big model's place");
            var waiting = AskAsync(bob, bobs, "me too");
            await EventuallyAsync(() => gate.Now(Big) == (1, 1), "Bob waits in the big model's line");
            // Carol's small model has its own line: she is answered meanwhile.
            var small = await AskAsync(carol, carols, "a quick one").WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Contains(small, e => e.GetProperty("type").GetString() == "done");
            Assert.DoesNotContain(small, e => e.GetProperty("type").GetString() == "queued");
            Assert.False(waiting.IsCompleted);
            app.Mcp.Hold.SetResult();
            Assert.Contains(await first.WaitAsync(TimeSpan.FromSeconds(20)), e => e.GetProperty("type").GetString() == "done");
            var events = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
            var queued = events.First(e => e.GetProperty("type").GetString() == "queued");
            Assert.Equal(Big, queued.GetProperty("model").GetString());
            Assert.Equal(0, queued.GetProperty("ahead").GetInt32());
            Assert.False(queued.GetProperty("yours").GetBoolean());
            Assert.Contains(events, e => e.GetProperty("type").GetString() == "done");
            Assert.Equal((0, 0), gate.Now());
        }
        finally
        {
            app.Mcp.Hold?.TrySetResult();
            app.Mcp.Hold = null;
        }
    }

    [Fact]
    public void A_model_marked_failed_is_tried_again_after_a_minute_then_less_and_less_often_once_each_time()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        var state = new EngineState(clock);
        state.Set([new EngineModel("small", "failed"), new EngineModel("big", "loaded")]);
        Assert.False(state.MayRetry("small"));
        Assert.False(state.MayAsk("small"));
        Assert.False(state.MayRetry("big"));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(state.MayRetry("small"));
        Assert.True(state.MayAsk("small"));

        // Tried (twice at once: one try). The watcher does not load it again before its next wait is over, however
        // often it looks; people's requests of the next half minute go too, as the engine loads it.
        state.Tried("small");
        state.Tried("small");
        Assert.False(state.MayRetry("small"));
        clock.Now += TimeSpan.FromSeconds(3);
        // Still failed 3 seconds after the try: the engine has not begun yet, it is part of the try.
        state.Set([new EngineModel("small", "failed")]);
        Assert.True(state.MayAsk("small"));
        Assert.False(state.MayRetry("small"));
        // Still failed 6 seconds after: the try failed, and the requests that follow wait for the next one.
        clock.Now += TimeSpan.FromSeconds(3);
        state.Set([new EngineModel("small", "failed")]);
        Assert.False(state.MayAsk("small"));
        Assert.False(state.MayRetry("small"));
        // Two minutes from the try, then four.
        clock.Now += TimeSpan.FromSeconds(110);
        Assert.False(state.MayRetry("small"));
        clock.Now += TimeSpan.FromSeconds(4);
        Assert.True(state.MayRetry("small"));
        state.Tried("small");
        clock.Now += TimeSpan.FromSeconds(31);
        Assert.False(state.MayAsk("small"));
        clock.Now += TimeSpan.FromMinutes(3);
        Assert.False(state.MayRetry("small"));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(state.MayRetry("small"));
        Assert.Equal([1d, 2, 4, 8, 16, 30, 30], Enumerable.Range(1, 7).Select(n => EngineState.Wait(n).TotalMinutes));

        // Once it loads, all is forgotten: a new failure waits a minute again.
        state.Set([new EngineModel("small", "loaded")]);
        state.Set([new EngineModel("small", "failed")]);
        Assert.False(state.MayRetry("small"));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.True(state.MayRetry("small"));
    }

    [Fact]
    public void The_models_that_make_room_are_those_not_held_smallest_first_whatever_their_size()
    {
        var sizes = new Dictionary<string, long> { ["big"] = 20L << 30, ["small"] = 100L << 20, ["mid"] = 4L << 30, ["huge"] = 40L << 30 };
        // Bigger than any asked for or not, a model not held may make room; the .env model (not added here: no size known) goes last.
        Assert.Equal(["small", "mid", "huge", "env"], EngineWatcher.SpareOf(["big", "env", "huge", "mid", "small"], ["big"], sizes));
        Assert.Empty(EngineWatcher.SpareOf(["big"], ["big"], sizes));

        // Held: the kept, then the model new chats use, then the model for small steps; no more than the engine holds.
        string? Known(string name) => name == "gone" ? null : "loaded";
        Assert.Equal(["big", "small"], EngineWatcher.Held([], "big", "small", 2, Known));
        Assert.Equal(["kept", "big"], EngineWatcher.Held(["kept"], "big", "small", 2, Known));
        Assert.Equal(["kept", "big", "small"], EngineWatcher.Held(["kept"], "big", "small", 3, Known));
        Assert.Equal(["big"], EngineWatcher.Held(["big"], "big", "gone", 3, Known));
        // One model at a time: only a kept one stays, and people on different models take turns, the default's too.
        Assert.Empty(EngineWatcher.Held([], "big", "small", 1, Known));
        Assert.Equal(["kept"], EngineWatcher.Held(["kept"], "big", "small", 1, Known));
    }

    /// <summary>Asks <paramref name="text"/> while the engine is full and no model that may make room is idle: the answer waits
    /// for room (EngineRoute.RoomWait); once the app has looked for an idle one, the clock passes the wait.</summary>
    private async Task<List<JsonElement>> AskWhileFullAsync(TestBrowser b, Guid chat, string text, MovableClock clock, string busy)
    {
        var asked = app.Engine.Calls.Count(c => c.Path == "/slots" && c.Model == busy);
        var answer = AskAsync(b, chat, text);
        await EventuallyAsync(() => app.Engine.Calls.Count(c => c.Path == "/slots" && c.Model == busy) > asked + 1, $"the app looked whether {busy} is idle, twice");
        Assert.False(answer.IsCompleted);
        clock.Now += EngineRoute.RoomWait + TimeSpan.FromSeconds(1);
        return await answer.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static bool Full(List<JsonElement> events, string model) => events.Any(e => e.GetProperty("type").GetString() == "error"
        && e.GetProperty("message").GetString()!.StartsWith($"{model} cannot be loaded now: the engine holds all the models it may", StringComparison.Ordinal));

    [Fact]
    public async Task A_model_asked_for_at_the_engines_limit_unloads_an_idle_one_not_kept_never_the_big_one()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = NewApp(new Dictionary<string, string?> { ["Engine:ModelsMax"] = "3" }, parallel: 1, clock: clock, others: [("tiny-a", 1), ("tiny-b", 1), ("tiny-c", 1)]);
        app.Engine.Max = 3;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // Two models asked for (API keys, say) beside the big one kept loaded: the engine is full.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-a/load"));
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/load"));
        var state = f.Services.GetRequiredService<EngineState>();
        await EventuallyAsync(() => state.Spare.SequenceEqual(["tiny-a", "tiny-b"]), "the watcher knows which may make room, the smallest first");
        var b = await PersonAsync(f);
        var chat = await ChatAsync(b, "tiny-c");

        // Both busy (an API key's requests): none makes room, the big one is never asked to, and the request is not
        // left to the engine (it would unload the model used least recently, whichever): the answer waits, then says so.
        app.Engine.BusySlots["tiny-a"] = [0];
        app.Engine.BusySlots["tiny-b"] = [0];
        Assert.True(Full(await AskWhileFullAsync(b, chat, "first", clock, "tiny-a"), "tiny-c"));
        Assert.DoesNotContain(app.Engine.Calls, c => c.Path == "/models/unload");
        Assert.DoesNotContain(app.Model.Requests, r => r.Body["model"]?.GetValue<string>() == "tiny-c");

        // Idle now: the smallest makes room, once.
        app.Engine.BusySlots.Clear();
        Assert.Contains(await AskAsync(b, chat, "second"), e => e.GetProperty("type").GetString() == "done");
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-a"));
        Assert.Equal("loaded", app.Engine.StatusOf("tiny-b"));
        Assert.Equal("loaded", app.Engine.StatusOf(Big));
        Assert.Single(app.Engine.Calls, c => c.Path == "/models/unload");
    }

    [Fact]
    public async Task An_idle_model_bigger_than_the_one_asked_for_makes_room_for_it()
    {
        await using var f = NewApp(parallel: 1, others: [("tiny-a", 1), ("tiny-b", 1)]);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        // Someone picked tiny-b, the bigger, which loaded beside the big one kept loaded, and sits idle since.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/load"));
        var state = f.Services.GetRequiredService<EngineState>();
        await EventuallyAsync(() => state.Spare.SequenceEqual(["tiny-b"]), "the watcher knows tiny-b may make room");
        var b = await PersonAsync(f);

        // The smaller tiny-a, asked for: tiny-b makes room, and the big one stays.
        Assert.Contains(await AskAsync(b, await ChatAsync(b, "tiny-a"), "hello"), e => e.GetProperty("type").GetString() == "done");
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-b"));
        Assert.Equal("loaded", app.Engine.StatusOf(Big));
        Assert.Single(app.Engine.Calls, c => c.Path == "/models/unload");
    }

    [Fact]
    public async Task With_nothing_kept_or_set_the_model_new_chats_get_never_makes_room_and_one_set_loads()
    {
        // As the live install: no model kept loaded and none set for new chats, two at once.
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = NewApp(new Dictionary<string, string?> { ["Chat:DefaultModel"] = "" }, parallel: 1, clock: clock, keep: [],
            others: [("tiny-a", 1), ("tiny-b", 1)]);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-a/load"));
        var state = f.Services.GetRequiredService<EngineState>();
        // The big model is the one new chats get (the first loaded): it never makes room, whatever is asked for.
        await EventuallyAsync(() => state.Default == Big && state.Spare.SequenceEqual(["tiny-a"]), "the watcher knows which may make room");
        var b = await PersonAsync(f);
        var chat = await ChatAsync(b, "tiny-b");

        // The small model answers someone (an API key); the big one is idle between two turns. The app does not unload
        // the big one, and does not send the request to the full engine either.
        app.Engine.BusySlots["tiny-a"] = [0];
        Assert.True(Full(await AskWhileFullAsync(b, chat, "first", clock, "tiny-a"), "tiny-b"));
        Assert.DoesNotContain(app.Engine.Calls, c => c.Path == "/models/unload");
        Assert.Equal("loaded", app.Engine.StatusOf(Big));
        Assert.DoesNotContain(app.Model.Requests, r => r.Body["model"]?.GetValue<string>() == "tiny-b");

        // The small model is idle again: it makes room, and the big one stays.
        app.Engine.BusySlots.Clear();
        Assert.Contains(await AskAsync(b, chat, "second"), e => e.GetProperty("type").GetString() == "done");
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-a"));
        Assert.Equal("loaded", app.Engine.StatusOf(Big));

        // A model set for new chats is the one held, and the app loads it (beside the big one, which may now make room).
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Chat:DefaultModel", value = (string?)"tiny-b", reset = false } } }));
        f.Services.GetRequiredService<EngineWatcher>().Wake();
        await EventuallyAsync(() => state.Default == "tiny-b" && app.Engine.StatusOf("tiny-b") == "loaded", "the model new chats use loaded");
        await EventuallyAsync(() => state.Spare.SequenceEqual([Big]), "the big one may make room now");
    }

    [Fact]
    public async Task With_none_set_new_chats_stay_on_the_model_they_were_getting_when_one_the_list_puts_first_loads()
    {
        await using var f = NewApp(new Dictionary<string, string?> { ["Chat:DefaultModel"] = "" }, parallel: 1, keep: [], others: [("tiny-a", 1)]);
        var state = f.Services.GetRequiredService<EngineState>();
        await EventuallyAsync(() => state.Default == Big, "the big model is the one new chats get");
        // The gateway lists tiny-a first; then someone has it loaded.
        var gateway = (FakeGateway)f.Services.GetRequiredService<Llm.Api.Gateway.ILiteLlm>();
        lock (gateway.Models)
        {
            var first = gateway.Models.Single(m => m.Name == "tiny-a");
            gateway.Models.Remove(first);
            gateway.Models.Insert(0, first);
        }
        f.Services.GetRequiredService<ChatModels>().Forget();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-a/load"));
        await EventuallyAsync(() => state.StatusOf("tiny-a") == "loaded" && state.Spare.SequenceEqual(["tiny-a"]), "tiny-a loaded, and it may make room");

        // A chat that chose no model is still answered by the big one, which stays held.
        Assert.Equal(Big, state.Default);
        var b = await PersonAsync(f);
        var chat = (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { thinking = "off", tools = Array.Empty<string>() }))).GetProperty("id").GetGuid();
        var events = await AskAsync(b, chat, "which model?");
        Assert.Equal(Big, events.First(e => e.GetProperty("type").GetString() == "assistant").GetProperty("model").GetString());
    }

    [Fact]
    public async Task The_model_new_chats_use_comes_back_after_an_API_keys_request_had_the_engine_unload_it()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = NewApp(new Dictionary<string, string?> { ["Chat:DefaultModel"] = "" }, parallel: 1, clock: clock, keep: [],
            others: [("tiny-a", 1), ("tiny-b", 1)]);
        var state = f.Services.GetRequiredService<EngineState>();
        var watcher = f.Services.GetRequiredService<EngineWatcher>();
        await EventuallyAsync(() => state.Default == Big, "the big model is the one new chats get");

        // API keys asked for two other models: the engine unloaded the one used least recently, the big one, to load the second.
        var engine = f.Services.GetRequiredService<EngineClient>();
        await engine.LoadAsync("tiny-a");
        await engine.LoadAsync("tiny-b");
        Assert.Equal("unloaded", app.Engine.StatusOf(Big));
        watcher.Wake();
        await EventuallyAsync(() => state.StatusOf(Big) == "unloaded" && state.Spare.SequenceEqual(["tiny-a", "tiny-b"]), "the watcher sees it");
        // Still the model new chats use: it does not pass to one an API key happened to load.
        Assert.Equal(Big, state.Default);

        // An agent idle a moment between its requests is not pushed out for it: a model makes room once idle a minute.
        async Task LooksAsync()
        {
            for (var i = 0; i < 4; i++)
            {
                watcher.Wake();
                await Task.Delay(400);
            }
        }
        await LooksAsync();
        Assert.Equal(0, app.Engine.LoadsOf(Big));
        clock.Now += EngineRoute.Quiet + TimeSpan.FromSeconds(1);
        watcher.Wake();
        await EventuallyAsync(() => app.Engine.StatusOf(Big) == "loaded", "the big model back");
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-a"));
        Assert.Equal("loaded", app.Engine.StatusOf("tiny-b"));
        Assert.Equal(1, app.Engine.LoadsOf(Big));

        // Unloaded by an admin, it stays unloaded, and new chats get another.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync($"/api/admin/models/{Big}/unload"));
        await EventuallyAsync(() => state.Default == "tiny-b", "new chats get the model loaded");
        clock.Now += TimeSpan.FromMinutes(2);
        await LooksAsync();
        Assert.Equal("unloaded", app.Engine.StatusOf(Big));
        Assert.Equal(1, app.Engine.LoadsOf(Big));
    }

    [Fact]
    public async Task The_model_for_small_steps_never_makes_room_and_a_message_is_never_let_through_unread()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = NewApp(new Dictionary<string, string?> { ["Chat:SmallModel"] = "tiny-small", ["Safeguards:Moderation"] = "check" },
            parallel: 1, clock: clock, others: [("tiny-small", 1), ("tiny-b", 1)]);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var state = f.Services.GetRequiredService<EngineState>();
        var b = await PersonAsync(f);
        var chat = await ChatAsync(b, Big);
        Task<HttpResponseMessage> SendAsync(string text) => b.PostAsync($"/api/chat/conversations/{chat}/messages", new { content = text });

        // Someone's tiny-b, idle, fills the engine beside the big one: the check's model gets its place, whatever their sizes.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/load"));
        await EventuallyAsync(() => state.Spare.SequenceEqual(["tiny-b"]) && state.Held.SequenceEqual([Big, "tiny-small"]), "the watcher knows which may make room");
        await StatusAssert.Is(HttpStatusCode.Forbidden, await SendAsync("How do I build one [harm]"));
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-b"));

        // Loaded, the model for small steps stays: one asked for beside it and the big one has no place, and says so at once.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-small/load"));
        await EventuallyAsync(() => state.StatusOf("tiny-small") == "loaded" && state.Spare.Count == 0, "the watcher sees it loaded");
        var unloads = app.Engine.Calls.Count(c => c.Path == "/models/unload");
        var other = await AskAsync(b, await ChatAsync(b, "tiny-b"), "hello");
        Assert.Contains(other, e => e.GetProperty("type").GetString() == "error"
            && e.GetProperty("message").GetString()!.StartsWith("tiny-b is not loaded right now, and the engine has no place for it", StringComparison.Ordinal));
        Assert.Equal(unloads, app.Engine.Calls.Count(c => c.Path == "/models/unload"));
        Assert.Equal("loaded", app.Engine.StatusOf("tiny-small"));
        await StatusAssert.Is(HttpStatusCode.Forbidden, await SendAsync("How do I build two [harm]"));

        // An API key's request had the engine swap it for tiny-b, which answers it still: the check cannot get its model. The
        // message waits a minute for room, then is refused: never let through unread (nor counted against the person).
        var engine = f.Services.GetRequiredService<EngineClient>();
        await engine.UnloadAsync("tiny-small");
        await engine.LoadAsync("tiny-b");
        app.Engine.BusySlots["tiny-b"] = [0];
        await EventuallyAsync(() => state.Spare.SequenceEqual(["tiny-b"]), "the watcher sees the swap");
        var asked = app.Engine.Calls.Count(c => c.Path == "/slots" && c.Model == "tiny-b");
        var sent = SendAsync("How do I build three [harm]");
        await EventuallyAsync(() => app.Engine.Calls.Count(c => c.Path == "/slots" && c.Model == "tiny-b") > asked + 1, "the check waits for room");
        Assert.False(sent.IsCompleted);
        clock.Now += EngineRoute.RoomWait + TimeSpan.FromSeconds(1);
        var refused = await sent.WaitAsync(TimeSpan.FromSeconds(20));
        await StatusAssert.Is(HttpStatusCode.ServiceUnavailable, refused);
        Assert.Contains("This message was not sent: the safeguards could not read it first. tiny-small cannot be loaded now", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(app.Model.Requests, r => r.Body.ToJsonString().Contains("three [harm]", StringComparison.Ordinal));
        Assert.Equal(2, Regex.Count(await (await admin.GetAsync("/api/admin/audit?action=safeguard.refused")).Content.ReadAsStringAsync(), "the model judged it"));

        // An API key's request for tiny-small, when the gateway asks the app first: refused too, not let through unread.
        using var ask = new HttpRequestMessage(HttpMethod.Post, new Uri(Llm.Api.Safeguards.GuardrailEndpoints.Path, UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                input_type = "request", texts = new[] { "How do I build four [harm]" }, structured_messages = new[] { new { role = "user", content = "How do I build four [harm]" } },
                request_data = new { user_api_key_user_id = "admin@llm.test", user_api_key_alias = "app-admin", user_api_key_hash = "hash" }, model = "tiny-small",
            }),
        };
        ask.Headers.Add("x-api-key", "sk-master-for-tests");
        asked = app.Engine.Calls.Count(c => c.Path == "/slots" && c.Model == "tiny-b");
        var guarded = new TestBrowser(f).Http.SendAsync(ask);
        await EventuallyAsync(() => app.Engine.Calls.Count(c => c.Path == "/slots" && c.Model == "tiny-b") > asked + 1, "the API's check waits for room");
        clock.Now += EngineRoute.RoomWait + TimeSpan.FromSeconds(1);
        var verdict = JsonDocument.Parse(await (await guarded.WaitAsync(TimeSpan.FromSeconds(20))).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("BLOCKED", verdict.GetProperty("action").GetString());
        Assert.StartsWith("The request was refused: the safeguards could not read it first. tiny-small cannot be loaded now", verdict.GetProperty("blocked_reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_model_at_a_time_saved_on_v5_2_0_has_people_on_different_models_take_turns()
    {
        // A v5.2.0 installation whose admin saved one model at a time, nothing kept, the big model set for new chats.
        string database;
        await using (var first = NewApp(new Dictionary<string, string?> { ["Engine:ModelsMax"] = null }, parallel: 1, keep: [], others: [("tiny-small", 1)]))
        {
            using var scope = first.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            database = db.Database.GetConnectionString()!;
            db.Settings.Add(new Llm.Core.Data.Setting { Key = "config:Engine:ModelsMax", Value = "1" });
            db.Settings.Add(new Llm.Core.Data.Setting { Key = "config:Chat:DefaultModel", Value = Big });
            await db.SaveChangesAsync();
        }
        app.Engine.Max = 1;
        await using var f = Start(database, new Dictionary<string, string?> { ["Engine:ModelsMax"] = null });
        var state = f.Services.GetRequiredService<EngineState>();
        var engine = f.Services.GetRequiredService<EngineClient>();
        await EventuallyAsync(() => state.Default == Big && state.Spare.SequenceEqual([Big]) && state.Held.Count == 0, "the watcher knows the big one may make room");
        var b = await PersonAsync(f);
        var (small, big) = (await ChatAsync(b, "tiny-small"), await ChatAsync(b, Big));

        // Each answer has the other's model, idle, unload; the engine then loads its own (as the router does for the request).
        async Task TurnAsync(Guid chat, string model, string other)
        {
            await EventuallyAsync(() => state.Spare.SequenceEqual([other]), $"{other} may make room");
            Assert.Contains(await AskAsync(b, chat, "my turn"), e => e.GetProperty("type").GetString() == "done");
            Assert.Equal("unloaded", app.Engine.StatusOf(other));
            await engine.LoadAsync(model);
            f.Services.GetRequiredService<EngineWatcher>().Wake();
        }
        await TurnAsync(small, "tiny-small", Big);
        await TurnAsync(big, Big, "tiny-small");
        await TurnAsync(small, "tiny-small", Big);
        Assert.Equal(3, app.Engine.Calls.Count(c => c.Path == "/models/unload"));
        // With one at a time, the app does not load the model new chats use again by itself.
        Assert.Equal(1, app.Engine.LoadsOf(Big));
    }

    /// <summary>An API key's request for <paramref name="model"/>, as the gateway asks the app before sending it (its guardrail): the app's verdict.</summary>
    private static async Task<JsonElement> GuardAsync(WebApplicationFactory<Program> f, string model)
    {
        using var ask = new HttpRequestMessage(HttpMethod.Post, new Uri(Llm.Api.Safeguards.GuardrailEndpoints.Path, UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                input_type = "request", texts = new[] { "hello" }, structured_messages = new[] { new { role = "user", content = "hello" } },
                request_data = new { user_api_key_user_id = "admin@llm.test", user_api_key_alias = "agent", user_api_key_hash = "hash" }, model,
            }),
        };
        ask.Headers.Add("x-api-key", "sk-master-for-tests");
        using var res = await new TestBrowser(f).Http.SendAsync(ask);
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    [Fact]
    public async Task An_API_keys_request_for_a_model_not_loaded_gets_room_as_the_chats_do_and_never_has_the_engine_unload_the_big_one()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = NewApp(parallel: 1, clock: clock, others: [("tiny-a", 1), ("tiny-b", 1)]);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var state = f.Services.GetRequiredService<EngineState>();
        var engine = f.Services.GetRequiredService<EngineClient>();
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-a/load"));
        await EventuallyAsync(() => state.Spare.SequenceEqual(["tiny-a"]), "the watcher knows tiny-a may make room");

        // An agent asks for tiny-b: the idle tiny-a makes room first. Left to the engine, it would unload the big one,
        // the model it loaded first (used least recently), to load tiny-b.
        Assert.Equal("NONE", (await GuardAsync(f, "tiny-b")).GetProperty("action").GetString());
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-a"));
        await engine.LoadAsync("tiny-b");
        Assert.Equal("loaded", app.Engine.StatusOf(Big));
        Assert.Single(app.Engine.Calls, c => c.Path == "/models/unload");

        // tiny-b answers the agent: another agent's request for tiny-a waits a minute for it, then is refused, as the chat's are.
        app.Engine.BusySlots["tiny-b"] = [0];
        await EventuallyAsync(() => state.Spare.SequenceEqual(["tiny-b"]), "the watcher sees tiny-b loaded");
        var asked = app.Engine.Calls.Count(c => c.Path == "/slots" && c.Model == "tiny-b");
        var waiting = GuardAsync(f, "tiny-a");
        await EventuallyAsync(() => app.Engine.Calls.Count(c => c.Path == "/slots" && c.Model == "tiny-b") > asked + 1, "the request waits for room");
        Assert.False(waiting.IsCompleted);
        clock.Now += EngineRoute.RoomWait + TimeSpan.FromSeconds(1);
        var verdict = await waiting.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("BLOCKED", verdict.GetProperty("action").GetString());
        Assert.StartsWith("tiny-a cannot be loaded now: the engine holds all the models it may, and those that may make room have been in use",
            verdict.GetProperty("blocked_reason").GetString(), StringComparison.Ordinal);

        // tiny-b set for new chats: every loaded model is held, and the request is refused at once.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PutAsJsonAsync(new Uri("/api/admin/config", UriKind.Relative),
            new { changes = new[] { new { key = "Chat:DefaultModel", value = (string?)"tiny-b", reset = false } } }));
        f.Services.GetRequiredService<EngineWatcher>().Wake();
        await EventuallyAsync(() => state.Spare.Count == 0, "the watcher holds tiny-b");
        verdict = await GuardAsync(f, "tiny-a").WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("BLOCKED", verdict.GetProperty("action").GetString());
        Assert.StartsWith("tiny-a cannot be loaded now: the engine holds all the models it may, and each is kept loaded or used by everyone",
            verdict.GetProperty("blocked_reason").GetString(), StringComparison.Ordinal);
        Assert.Single(app.Engine.Calls, c => c.Path == "/models/unload");
        Assert.Equal("loaded", app.Engine.StatusOf(Big));
        // A model that is loaded, or not the engine's, goes as it is.
        Assert.Equal("NONE", (await GuardAsync(f, Big)).GetProperty("action").GetString());
        Assert.Equal("NONE", (await GuardAsync(f, "gpt-elsewhere")).GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_place_made_for_one_model_is_its_own_until_it_loads()
    {
        await using var f = NewApp(new Dictionary<string, string?> { ["Engine:ModelsMax"] = "3" }, parallel: 1, others: [("tiny-a", 1), ("tiny-b", 1), ("tiny-c", 1), ("tiny-d", 1)]);
        app.Engine.Max = 3;
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var state = f.Services.GetRequiredService<EngineState>();
        var engine = f.Services.GetRequiredService<EngineClient>();
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-a/load"));
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-b/load"));
        await EventuallyAsync(() => state.Spare.SequenceEqual(["tiny-a", "tiny-b"]), "the watcher knows which may make room");

        // Two agents at once, for two models not loaded: each gets a place of its own, before the engine loads either.
        Assert.Equal("NONE", (await GuardAsync(f, "tiny-c")).GetProperty("action").GetString());
        Assert.Equal("NONE", (await GuardAsync(f, "tiny-d")).GetProperty("action").GetString());
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-a"));
        Assert.Equal("unloaded", app.Engine.StatusOf("tiny-b"));
        // A second request for a model on its way rides on its place.
        Assert.Equal("NONE", (await GuardAsync(f, "tiny-c")).GetProperty("action").GetString());
        Assert.Equal(2, app.Engine.Calls.Count(c => c.Path == "/models/unload"));

        // The engine loads both: the big one stays.
        await engine.LoadAsync("tiny-c");
        await engine.LoadAsync("tiny-d");
        Assert.Equal("loaded", app.Engine.StatusOf(Big));
    }

    [Fact]
    public async Task A_model_that_failed_to_load_is_tried_again_after_its_wait_once_asked_for_or_kept()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = NewApp(parallel: 1, clock: clock, others: [("tiny-small", 1)]);
        var state = f.Services.GetRequiredService<EngineState>();
        var b = await PersonAsync(f);
        var chat = await ChatAsync(b, "tiny-small");

        // The router killed it while it was being swapped out, and marked it failed (as SmolLM2 in the load test).
        app.Engine.Broken.Add("tiny-small");
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/models/tiny-small/load"));
        await EventuallyAsync(() => state.StatusOf("tiny-small") == "failed", "the app sees it failed");
        Assert.Contains(await AskAsync(b, chat, "hello"), e => e.GetProperty("type").GetString() == "error"
            && e.GetProperty("message").GetString()!.Contains("could not be loaded just now", StringComparison.Ordinal));
        // A minute later the next question has the engine load it again.
        app.Engine.Broken.Clear();
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Contains(await AskAsync(b, chat, "hello again"), e => e.GetProperty("type").GetString() == "done");

        // The big model, kept loaded, unloaded by the engine and then killed the same way: still broken, it is loaded
        // again once after each wait (1 minute, then 2), never every few seconds in between.
        app.Engine.Broken.Add(Big);
        await f.Services.GetRequiredService<EngineClient>().UnloadAsync(Big);
        var watcher = f.Services.GetRequiredService<EngineWatcher>();
        watcher.Wake();
        await EventuallyAsync(() => app.Engine.StatusOf(Big) == "failed" && state.StatusOf(Big) == "failed", "its load failed");
        var loads = app.Engine.LoadsOf(Big);
        // Its card says when it is tried again.
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().Single(m => m.GetProperty("name").GetString() == Big);
        Assert.Equal("failed", row.GetProperty("status").GetString());
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(1), row.GetProperty("retryAt").GetDateTimeOffset());
        async Task LooksAsync()
        {
            // Several of the watcher's rounds, each seeing it failed.
            for (var i = 0; i < 4; i++)
            {
                watcher.Wake();
                await Task.Delay(400);
            }
        }
        await LooksAsync();
        Assert.Equal(loads, app.Engine.LoadsOf(Big));
        clock.Now += TimeSpan.FromMinutes(1);
        watcher.Wake();
        await EventuallyAsync(() => app.Engine.LoadsOf(Big) == loads + 1, "tried again after a minute");
        await LooksAsync();
        clock.Now += TimeSpan.FromSeconds(20);
        await LooksAsync();
        clock.Now += TimeSpan.FromSeconds(50);
        await LooksAsync();
        Assert.Equal(loads + 1, app.Engine.LoadsOf(Big));
        clock.Now += TimeSpan.FromSeconds(55);
        watcher.Wake();
        await EventuallyAsync(() => app.Engine.LoadsOf(Big) == loads + 2, "tried again two minutes after the try before");
        await LooksAsync();
        Assert.Equal(loads + 2, app.Engine.LoadsOf(Big));
        app.Engine.Broken.Clear();
        clock.Now += TimeSpan.FromMinutes(4);
        watcher.Wake();
        await EventuallyAsync(() => app.Engine.StatusOf(Big) == "loaded", "the big model loaded again");
    }

    [Fact]
    public async Task Settings_saved_on_v5_2_0_keep_working_and_two_models_load_at_once_unless_an_admin_chose_one()
    {
        // A v5.2.0 installation that never saved Engine:ModelsMax (its default was 1): the engine holds two now.
        var max = Path.Combine(Config, "max");
        string database;
        await using (var first = NewApp(new Dictionary<string, string?> { ["Engine:ModelsMax"] = null }, others: [("tiny-small", 2)]))
        {
            await EventuallyAsync(() => File.Exists(max) && File.ReadAllText(max) == "2\n", "the engine is told 2 at once");
            using var scope = first.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            database = db.Database.GetConnectionString()!;
            // As v5.2.0's Settings page saved them: everyone's answers at once, and one model at a time.
            db.Settings.Add(new Llm.Core.Data.Setting { Key = "config:Chat:AnswersAtOnce", Value = "1" });
            db.Settings.Add(new Llm.Core.Data.Setting { Key = "config:Engine:ModelsMax", Value = "1" });
            await db.SaveChangesAsync();
        }
        File.Delete(max);
        await using var second = Start(database, new Dictionary<string, string?> { ["Engine:ModelsMax"] = null });
        var gate = second.Services.GetRequiredService<AnswerGate>();
        await EventuallyAsync(() => File.Exists(max) && File.ReadAllText(max) == "1\n", "the admin's one model at a time stands");
        await EventuallyAsync(() => gate.PlacesOf(Big) == 4 && gate.PlacesOf("tiny-small") == 2, "each model's places");
        // Answers at once, everyone, still holds all models together: the line is the whole chat's.
        using var one = await gate.EnterAsync(Guid.NewGuid(), Big, Nothing, default);
        var told = new Told();
        var other = gate.EnterAsync(Guid.NewGuid(), "tiny-small", told.Add, default);
        Assert.Equal(new AnswerGate.Line(null, 0, 1, 1), await told.LastAsync());
        one.Dispose();
        (await other.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
