using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Llm.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>Other GPU servers: their models at the gateway beside this machine's, their keys kept secret, and their health.</summary>
[Collection(nameof(AppCollection))]
public sealed class RemoteServersTests(AppFixture app)
{
    private (WebApplicationFactory<Program> App, FakeGateway Gateway) NewApp()
    {
        app.Remote.Down = false;
        var gateway = new FakeGateway();
        // Without the llama.cpp engine: servers work with any engine, or none.
        var f = app.Create(app.ConnectionStringFor("remote_" + Guid.NewGuid().ToString("N")[..8]), gateway, new Dictionary<string, string?> { ["Engine:Enabled"] = "false" });
        return (f, gateway);
    }

    private static Task<TestBrowser> AdminAsync(WebApplicationFactory<Program> f) => new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

    private static object Server(object[] models, string key = FakeRemote.Key) => new { name = "GPU box", baseUrl = FakeRemote.Base + "/", apiKey = key, models };

    [Fact]
    public async Task A_server_is_looked_up_before_it_is_added_and_a_wrong_address_or_key_says_why()
    {
        var (f, _) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);

        var offers = await admin.JsonAsync(await admin.PostAsync("/api/admin/servers/probe", new { baseUrl = FakeRemote.Base, apiKey = FakeRemote.Key }));
        var models = offers.GetProperty("models").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!, m => m.GetProperty("context").GetInt32());
        // The window each reports, under the names vLLM and llama.cpp use.
        Assert.Equal(new Dictionary<string, int> { ["big-remote"] = 65536, ["small-remote"] = 32768 }, models);

        var badKey = await admin.PostAsync("/api/admin/servers/probe", new { baseUrl = FakeRemote.Base, apiKey = "wrong" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, badKey);
        Assert.Contains("refused the key (HTTP 401)", await badKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/servers/probe", new { baseUrl = "ftp://gpu-box/v1" }));
        var unreachable = await admin.PostAsync("/api/admin/servers/probe", new { baseUrl = "http://elsewhere:8000/v1", apiKey = FakeRemote.Key });
        Assert.Contains("cannot be reached", await unreachable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // A server that does not answer is not added.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/servers", Server([new { remote = "big-remote" }], key: "wrong")));
    }

    [Fact]
    public async Task Its_models_reach_the_gateway_with_its_address_and_key_and_the_key_is_never_shown()
    {
        var (f, gateway) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);
        var made = await admin.PostAsync("/api/admin/servers", Server([
            new { remote = "big-remote", name = "big", context = 65536, maxOutput = 8192, thinking = true },
            // Named like the .env model: a second copy of it, and the gateway spreads requests between them.
            new { remote = "small-remote", name = "Qwen3.8-Flash-Next", context = 32768 },
        ]));
        await StatusAssert.Is(HttpStatusCode.Created, made);

        var big = Assert.Single(gateway.Managed.Values, m => m.Name == "big");
        Assert.Equal("openai/big-remote", big.Params["model"]!.GetValue<string>());
        Assert.Equal(FakeRemote.Base, big.Params["api_base"]!.GetValue<string>());
        Assert.Equal(FakeRemote.Key, big.Params["api_key"]!.GetValue<string>());
        Assert.Null(big.Params["ssl_verify"]);
        Assert.Equal(65536, big.Info["max_input_tokens"]!.GetValue<int>());
        Assert.Equal("GPU box", big.Info["llm_app_server"]!.GetValue<string>());
        Assert.Single(gateway.Managed.Values, m => m.Name == "Qwen3.8-Flash-Next");

        // Kept encrypted, and never sent back.
        using (var scope = f.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().RemoteServers.SingleAsync();
            Assert.NotNull(stored.ApiKeyProtected);
            Assert.DoesNotContain(FakeRemote.Key, stored.ApiKeyProtected, StringComparison.Ordinal);
        }
        var listed = await admin.GetAsync("/api/admin/servers");
        var text = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(FakeRemote.Key, text, StringComparison.Ordinal);
        var server = JsonDocument.Parse(text).RootElement.EnumerateArray().Single();
        Assert.True(server.GetProperty("keySet").GetBoolean());
        Assert.True(server.GetProperty("status").GetProperty("up").GetBoolean());
        Assert.All(server.GetProperty("models").EnumerateArray(), m => Assert.True(m.GetProperty("listed").GetBoolean()));

        // The Models page shows them with their server, and the chat offers them.
        var rows = (await admin.JsonAsync(await admin.GetAsync("/api/admin/models"))).GetProperty("models").EnumerateArray().ToList();
        Assert.Contains(rows, r => r.GetProperty("name").GetString() == "big" && r.GetProperty("source").GetString() == "remote" && r.GetProperty("server").GetString() == "GPU box");
        var chat = (await admin.JsonAsync(await admin.GetAsync("/api/chat/config"))).GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Contains("big", chat);

        // Renamed: the old deployment goes, the new one comes. The key is kept when none is sent.
        var id = server.GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/servers/{id}", UriKind.Relative),
            new { models = new[] { new { remote = "big-remote", name = "big-2", context = 65536 } } }));
        Assert.Equal(["big-2"], gateway.Managed.Values.Select(m => m.Name));
        Assert.Equal(FakeRemote.Key, gateway.Managed.Values.Single().Params["api_key"]!.GetValue<string>());

        // Down: the page says so, and which models it no longer lists once it is back without them.
        app.Remote.Down = true;
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/servers/{id}", UriKind.Relative), new { name = "GPU box" }));
        var down = (await admin.JsonAsync(await admin.GetAsync("/api/admin/servers"))).EnumerateArray().Single().GetProperty("status");
        Assert.False(down.GetProperty("up").GetBoolean());
        Assert.Contains("cannot be reached", down.GetProperty("error").GetString(), StringComparison.Ordinal);
        app.Remote.Down = false;
        app.Remote.Models = """[{"id":"other"}]""";
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/servers/{id}", UriKind.Relative), new { name = "GPU box" }));
        var gone = (await admin.JsonAsync(await admin.GetAsync("/api/admin/servers"))).EnumerateArray().Single().GetProperty("models").EnumerateArray().Single();
        Assert.False(gone.GetProperty("listed").GetBoolean());
        app.Remote.Models = new FakeRemote().Models;

        // Removed: its models leave the gateway.
        await StatusAssert.Is(HttpStatusCode.OK, await admin.Http.DeleteAsync(new Uri($"/api/admin/servers/{id}", UriKind.Relative)));
        Assert.Empty(gateway.Managed);
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("server.add", audit);
        Assert.Contains("server.remove", audit);
    }

    [Fact]
    public async Task Https_servers_are_checked_against_the_stacks_bundle_unless_an_admin_says_not_to()
    {
        var (f, gateway) = NewApp();
        await using var _f = f;
        var admin = await AdminAsync(f);
        // The fake answers any scheme on gpu-box: what is under test is what the gateway is told.
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/servers",
            new { name = "Checked", baseUrl = "https://gpu-box:8000/v1", apiKey = FakeRemote.Key, models = new[] { new { remote = "big-remote", name = "checked" } } }));
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/servers",
            new { name = "Unchecked", baseUrl = "https://gpu-box:8000/v1", apiKey = FakeRemote.Key, verifyTls = false, models = new[] { new { remote = "big-remote", name = "unchecked" } } }));
        Assert.Equal("/certs/bundle.crt", gateway.Managed.Values.Single(m => m.Name == "checked").Params["ssl_verify"]!.GetValue<string>());
        Assert.False(gateway.Managed.Values.Single(m => m.Name == "unchecked").Params["ssl_verify"]!.GetValue<bool>());
        // Two models of one name on one server, or a name that is not one, are refused.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/servers",
            new { name = "Twice", baseUrl = FakeRemote.Base, apiKey = FakeRemote.Key, models = new[] { new { remote = "a", name = "x" }, new { remote = "b", name = "x" } } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/servers",
            new { name = "Bad name", baseUrl = FakeRemote.Base, apiKey = FakeRemote.Key, models = new[] { new { remote = "a", name = "has space" } } }));
        await StatusAssert.Is(HttpStatusCode.Conflict, await admin.PostAsync("/api/admin/servers",
            new { name = "Checked", baseUrl = FakeRemote.Base, apiKey = FakeRemote.Key, models = new[] { new { remote = "a" } } }));
    }
}
