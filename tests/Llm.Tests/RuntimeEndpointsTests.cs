using System.Net;
using System.Text.Json;
using Llm.Api.Chat.Tools;
using Llm.Api.Models;
using Llm.Api.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Admin → Monitoring's engines and sandboxes (Operations/Runtimes): each with its state; an admin's Reload, Restart or
/// Reset written as the file its service follows (the services themselves are tested in tests/deploy), audited, and refused
/// while one is pending; llama.cpp's Reload done over its API, model after model. The picture server's Restart is in
/// ModelStateMediaTests.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class RuntimeEndpointsTests(AppFixture app) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("llm-runtimes-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<JsonElement> RowAsync(TestBrowser admin, string id) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/runtimes"))).EnumerateArray().Single(r => r.GetProperty("id").GetString() == id);

    private static async Task<string?> StateAsync(TestBrowser admin, string id) => (await RowAsync(admin, id)).GetProperty("state").GetString();

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"Never: {what}");
    }

    private static async Task<List<JsonElement>> AuditAsync(TestBrowser admin) =>
        [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray().Where(e => e.GetProperty("action").GetString()!.StartsWith("runtime.", StringComparison.Ordinal))];

    [Fact]
    public async Task A_sandbox_reset_stops_its_job_running_tells_the_one_waiting_and_a_second_is_refused_meanwhile()
    {
        await using var sandbox = new FakeSandbox { HoldReset = true };
        await using var f = app.Create(app.ConnectionStringFor("rt_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(),
            new Dictionary<string, string?> { ["Sandbox:Dir"] = sandbox.Dir });
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await EventuallyAsync(async () => await StateAsync(admin, "sandbox") == "up", "the sandbox up");
        var row = await RowAsync(admin, "sandbox");
        Assert.Equal(("sandbox", "Python sandbox", "reset"), (row.GetProperty("kind").GetString(), row.GetProperty("label").GetString(), string.Join(",", row.GetProperty("actions").EnumerateArray())));

        var client = f.Services.GetRequiredService<SandboxClient>();
        var running = client.RunAsync("# slow\n", [], timeoutSeconds: 60, CancellationToken.None);
        await EventuallyAsync(() => Task.FromResult(client.Load()?.Busy == 1), "the slow job runs");
        var waiting = client.RunAsync("print(2)\n", [], timeoutSeconds: 60, CancellationToken.None);
        await EventuallyAsync(() => Task.FromResult(client.Load()?.Waiting == 1), "the second waits behind it");

        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/runtimes/sandbox/reset"));
        Assert.Equal(["gen=1", "action=reset", "by=admin"], (await File.ReadAllLinesAsync(Path.Combine(sandbox.Dir, "reset")))[..3]);
        row = await RowAsync(admin, "sandbox");
        Assert.Equal(("resetting", 1, 1), (row.GetProperty("state").GetString(), row.GetProperty("busy").GetInt32(), row.GetProperty("waiting").GetInt32()));
        // Asked again before the runner did it: refused. The tools say why they wait.
        var twice = await admin.PostAsync("/api/admin/runtimes/sandbox/reset");
        await StatusAssert.Is(HttpStatusCode.Conflict, twice);
        Assert.Contains("Python sandbox is resetting since", (await admin.JsonAsync(twice)).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal("The Python sandbox is being reset by an admin: try again in a minute.", client.Unavailable());

        // The runner does it: each caller is told, and it is up again.
        sandbox.HoldReset = false;
        var stopped = await Assert.ThrowsAsync<SandboxException>(() => running.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("An admin reset the Python sandbox while this job was running: run it again.", stopped.Message);
        var told = await Assert.ThrowsAsync<SandboxException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("An admin reset the Python sandbox while this job waited in line: run it again.", told.Message);
        await EventuallyAsync(async () => await StateAsync(admin, "sandbox") == "up", "the sandbox back");
        var last = (await RowAsync(admin, "sandbox")).GetProperty("last");
        Assert.Equal(("reset", "admin"), (last.GetProperty("action").GetString(), last.GetProperty("by").GetString()));
        var audited = Assert.Single(await AuditAsync(admin));
        Assert.Equal(("runtime.reset", "sandbox", "1 running, 1 waiting told"),
            (audited.GetProperty("action").GetString(), audited.GetProperty("target").GetString(), audited.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Llamacpps_restart_is_a_file_its_router_follows_and_its_reload_loads_each_model_again()
    {
        var config = Path.Combine(_dir, "config");
        Directory.CreateDirectory(config);
        app.Engine.Reset(Path.Combine(config, "models.ini"));
        var model = app.Engine.DefaultModel;
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var f = app.Create(app.ConnectionStringFor("rt_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = config, ["Engine:LibraryDir"] = Path.Combine(_dir, "library"),
            ["Engine:Vllm:0:Name"] = "vllm", ["Replicas:Enabled"] = "false",
        }, s => s.AddSingleton<TimeProvider>(clock));
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        await EventuallyAsync(async () => (await RowAsync(admin, "llamacpp")) is var r && r.GetProperty("state").GetString() == "up"
            && r.GetProperty("detail").GetString()!.Contains(model, StringComparison.Ordinal), "llama.cpp up with its model");
        // Not configured, nothing heard: down, and says why.
        Assert.Equal("down", await StateAsync(admin, "vllm"));

        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/runtimes/llamacpp/restart"));
        Assert.Equal(["gen=1", "action=restart", "by=admin"], (await File.ReadAllLinesAsync(Path.Combine(config, LlamaCppBackend.RestartFile)))[..3]);
        Assert.Equal("restarting", await StateAsync(admin, "llamacpp"));
        // Neither another restart nor a reload meanwhile.
        var twice = await admin.PostAsync("/api/admin/runtimes/llamacpp/restart");
        await StatusAssert.Is(HttpStatusCode.Conflict, twice);
        Assert.Contains("llama.cpp is restarting since", (await admin.JsonAsync(twice)).GetProperty("error").GetString(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/runtimes/llamacpp/reload"));
        // The router has had its time, and the watcher has seen llama-server answer since: up.
        clock.Now += Runtimes.RouterSettle + TimeSpan.FromSeconds(1);
        await EventuallyAsync(async () =>
        {
            f.Services.GetRequiredService<EngineWatcher>().Wake();
            return await StateAsync(admin, "llamacpp") == "up";
        }, "llama.cpp back");

        // A reload: the model unloaded and loaded again, and the file says which.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/runtimes/llamacpp/reload"));
        await EventuallyAsync(() => Task.FromResult(app.Engine.CallsTo("/models/unload", model) == 1 && app.Engine.LoadsOf(model) >= 1 && app.Engine.StatusOf(model) == "loaded"),
            "unloaded and loaded again");
        Assert.Contains($"model={model}", await File.ReadAllLinesAsync(Path.Combine(config, LlamaCppBackend.ReloadFile)));
        await EventuallyAsync(async () => await StateAsync(admin, "llamacpp") == "up", "reloaded");

        var audit = await AuditAsync(admin);
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "runtime.restart" && e.GetProperty("target").GetString() == "llamacpp");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "runtime.reload" && e.GetProperty("target").GetString() == "llamacpp"
            && e.GetProperty("detail").GetString() == $"1 model ({model}); 0 answers cut");
        Assert.Equal("reload", (await RowAsync(admin, "llamacpp")).GetProperty("last").GetProperty("action").GetString());
        // An engine is not reset, and what the stack does not run is not there.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/runtimes/llamacpp/reset"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await admin.PostAsync("/api/admin/runtimes/nothing/restart"));
        Assert.Equal(2, (await AuditAsync(admin)).Count);

        // Done, the reload is no longer on its way: its model unloaded later (the app making room, an admin), or loading
        // again and never done, does not make it so, and an admin may restart a llama.cpp that hangs. The file says it is
        // done, for every replica.
        app.Engine.LostLoads = 1000;
        try
        {
            await f.Services.GetRequiredService<LlamaCppBackend>().UnloadAsync(model, CancellationToken.None);
            await EventuallyAsync(() =>
            {
                f.Services.GetRequiredService<EngineWatcher>().Wake();
                return Task.FromResult(f.Services.GetRequiredService<EngineState>().StatusOf(model) is "unloaded" or "loading");
            }, "the model unloaded");
            Assert.Contains(await StateAsync(admin, "llamacpp"), new[] { "up", "loading" });
            Assert.Contains(await File.ReadAllLinesAsync(Path.Combine(config, LlamaCppBackend.ReloadFile)), l => l.StartsWith("done=", StringComparison.Ordinal));
            await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/runtimes/llamacpp/restart"));
        }
        finally
        {
            app.Engine.LostLoads = 0;
        }
    }

    [Fact]
    public async Task A_vllm_engines_reload_and_restart_are_pending_until_its_supervisor_says_it_did_them()
    {
        var config = Path.Combine(_dir, "config");
        var status = Path.Combine(_dir, "vllm-status");
        Directory.CreateDirectory(config);
        app.Engine.Reset(Path.Combine(config, "models.ini"));
        var vllm = new FakeVllm(Path.Combine(config, "vllm"), Path.Combine(status, "vllm")) { LoadTicks = 1 };
        await using var f = app.Create(app.ConnectionStringFor("rt_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>
        {
            ["Engine:Enabled"] = "true", ["Engine:ApiKey"] = FakeEngine.Key, ["Engine:ConfigDir"] = config, ["Engine:VllmStatusDir"] = status,
            ["Engine:Vllm:0:Name"] = "vllm", ["Replicas:Enabled"] = "false",
        }, s => s.AddHttpClient(VllmBackend.Client).ConfigurePrimaryHttpMessageHandler(() => vllm));
        var backend = f.Services.GetRequiredService<VllmBackends>().All.Single();
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            db.LocalModels.Add(new Llm.Core.Models.LocalModel { Name = "flash", File = "hf/org/flash@r", EngineKind = Llm.Core.Models.EngineKinds.Vllm, Context = 8192, Parallel = 8, Devices = "0" });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<ModelCatalog>().WriteVllmPresetsAsync();
        }
        vllm.Tick();
        await backend.LoadAsync("flash", CancellationToken.None);
        vllm.Tick();
        vllm.Tick();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var row = await RowAsync(admin, "vllm");
        Assert.Equal(("up", "vLLM", "1 model running: flash."), (row.GetProperty("state").GetString(), row.GetProperty("label").GetString(), row.GetProperty("detail").GetString()));

        // Pending until the supervisor's heartbeat says it did it; then its models load again.
        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/runtimes/vllm/reload"));
        Assert.Equal("reloading", await StateAsync(admin, "vllm"));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/runtimes/vllm/restart"));
        vllm.Tick();
        Assert.Equal(1, vllm.Restarts);
        Assert.Equal("loading", await StateAsync(admin, "vllm"));
        vllm.Tick();
        Assert.Equal("up", await StateAsync(admin, "vllm"));

        await StatusAssert.Is(HttpStatusCode.Accepted, await admin.PostAsync("/api/admin/runtimes/vllm/restart"));
        Assert.Equal(["gen=1", "action=restart", "by=admin"], (await File.ReadAllLinesAsync(Path.Combine(config, "vllm", VllmBackend.RestartFile)))[..3]);
        Assert.Equal("restarting", await StateAsync(admin, "vllm"));
        vllm.Tick();
        vllm.Tick();
        Assert.Equal(("up", 2), (await StateAsync(admin, "vllm"), vllm.Restarts));
        var audit = await AuditAsync(admin);
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "runtime.reload" && e.GetProperty("target").GetString() == "vllm"
            && e.GetProperty("detail").GetString() == "1 model; 0 answers cut, 0 waiting");
        Assert.Contains(audit, e => e.GetProperty("action").GetString() == "runtime.restart" && e.GetProperty("target").GetString() == "vllm");
    }
}
