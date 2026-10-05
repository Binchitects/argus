using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Llm.Tests;

/// <summary>
/// Admins' MCP servers and APIs on servers whose certificates the system does not trust: refused
/// with why, then trusted by a CA the admin gives, or not checked at all (audited), over real TLS.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class ToolTlsTests(AppFixture app)
{
    private static string NewDatabase() => "tls_" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Its own app, whose tool servers are reached over the network as in production (not through the fakes' handler).</summary>
    private WebApplicationFactory<Program> NewApp(string database) =>
        app.Create(app.ConnectionStringFor(database), new FakeGateway(), new Dictionary<string, string?> { ["Auth:DataKey"] = "a-data-key-for-tls-tests" },
            s => s.AddHttpClient(ToolRegistry.McpClient).ConfigurePrimaryHttpMessageHandler(Mcp.Handler));

    private static async Task<List<JsonElement>> SendAsync(TestBrowser b, Guid id, string text)
    {
        var res = await b.PostAsync($"/api/chat/conversations/{id}/messages", new { content = text });
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return [.. (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("data: ", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement)];
    }

    private static JsonElement Event(List<JsonElement> events, string type) => events.First(e => e.GetProperty("type").GetString() == type);

    private static async Task<Guid> ChatAsync(TestBrowser b, string toolId) =>
        (await b.JsonAsync(await b.PostAsync("/api/chat/conversations", new { useArgus = false, tools = new[] { toolId } }))).GetProperty("id").GetGuid();

    private static async Task<JsonElement> RowAsync(TestBrowser admin, string toolId) =>
        (await admin.JsonAsync(await admin.GetAsync("/api/admin/tools"))).EnumerateArray().Single(t => t.GetProperty("id").GetString() == toolId).GetProperty("server");

    private static async Task<List<JsonElement>> AuditAsync(TestBrowser admin, string action) =>
        [.. (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=100"))).EnumerateArray().Where(e => e.GetProperty("action").GetString() == action)];

    private static List<string> Reasons(JsonElement test) => [.. test.GetProperty("certificate").GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!)];

    [Fact]
    public async Task An_mcp_server_signed_by_a_company_CA_is_refused_with_why_until_its_CA_is_trusted_and_the_choice_survives_a_restart()
    {
        using var ca = TestCertificates.Ca("Test company CA");
        using var certificate = TestCertificates.Server(ca, "localhost");
        await using var tls = await TlsServer.StartAsync(certificate, new FakeMcp(), "tools.example.test");
        var database = NewDatabase();
        var server = new { name = "Echo Desk", url = tls.Url("/mcp"), headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey };
        var pem = TestCertificates.Pem(ca);
        string toolId;
        await using (var f = NewApp(database))
        {
            var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

            // By default the system's CAs decide: refused, and the test says by whom it was issued and why.
            var refused = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", server));
            Assert.False(refused.GetProperty("ok").GetBoolean());
            Assert.Equal("Echo Desk's certificate is not trusted. An admin can trust the CA that signed it, or stop checking it, in Admin → Tools (Test says why).",
                refused.GetProperty("error").GetString());
            var about = refused.GetProperty("certificate");
            Assert.Equal("CN=localhost", about.GetProperty("subject").GetString());
            Assert.Contains("CN=Test company CA", about.GetProperty("issuer").GetString(), StringComparison.Ordinal);
            Assert.Equal(["localhost"], about.GetProperty("names").EnumerateArray().Select(n => n.GetString()));
            Assert.False(about.GetProperty("selfSigned").GetBoolean());
            Assert.Equal(["It was issued by Test company CA, a CA this server does not trust."], Reasons(refused));

            // What is not a CA certificate is refused before anything is tried.
            await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/tools/servers/test", new { server.name, server.url, tls = "OwnCa" }));
            await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/tools/servers/test", new { server.name, server.url, tls = "OwnCa", tlsCa = "not a certificate" }));
            using var key = RSA.Create(2048);
            var withKey = await admin.PostAsync("/api/admin/tools/servers", new { server.name, server.url, tls = "OwnCa", tlsCa = pem + key.ExportPkcs8PrivateKeyPem() });
            await StatusAssert.Is(HttpStatusCode.BadRequest, withKey);
            Assert.Contains("private key", await withKey.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // Its CA given: the test lists its tools, and it is added with it.
            var trusted = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", new { server.name, server.url, server.headerName, server.headerValue, tls = "OwnCa", tlsCa = pem }));
            Assert.True(trusted.GetProperty("ok").GetBoolean(), trusted.ToString());
            Assert.Equal("echo", trusted.GetProperty("tools")[0].GetProperty("name").GetString());
            var made = await admin.PostAsync("/api/admin/tools/servers", new { server.name, server.url, server.headerName, server.headerValue, tls = "OwnCa", tlsCa = pem });
            await StatusAssert.Is(HttpStatusCode.Created, made);
            toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
            var row = await RowAsync(admin, toolId);
            Assert.Equal("OwnCa", row.GetProperty("tls").GetString());
            Assert.Equal(pem.Trim(), row.GetProperty("tlsCa").GetString());
            Assert.Equal(["Test company CA"], row.GetProperty("tlsCaNames").EnumerateArray().Select(n => n.GetString()));
            // Audited by its name and fingerprint, never the PEM.
            var audited = Assert.Single(await AuditAsync(admin, "tool.server_tls")).GetProperty("detail").GetString()!;
            Assert.Contains("own CA: CN=Test company CA, O=Test company (SHA-256 ", audited, StringComparison.Ordinal);
            Assert.DoesNotContain("BEGIN CERTIFICATE", audited, StringComparison.Ordinal);

            // The chat's calls go over it.
            var chat = await ChatAsync(admin, toolId);
            Assert.Equal("echo: over tls", Event(await SendAsync(admin, chat, """Echo: [call echo_desk__echo {"text":"over tls"}]"""), "tool_result").GetProperty("text").GetString());
            // A long call's events too.
            var slow = await SendAsync(admin, chat, """Echo: [call echo_desk__echo {"text":"slow"}]""");
            Assert.Equal("Warming up", Event(slow, "tool_progress").GetProperty("message").GetString());
            Assert.Equal("echo: slow", Event(slow, "tool_result").GetProperty("text").GetString());
        }

        // A restart: the choice is read back from the database, and the connection made anew from it.
        ServerClients.Forget(Guid.Parse(toolId[4..]));
        await using (var f = NewApp(database))
        {
            var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
            Assert.Equal("OwnCa", (await RowAsync(admin, toolId)).GetProperty("tls").GetString());
            var chat = await ChatAsync(admin, toolId);
            Assert.Equal("echo: after a restart", Event(await SendAsync(admin, chat, """Echo: [call echo_desk__echo {"text":"after a restart"}]"""), "tool_result").GetProperty("text").GetString());

            // Back to the system's CAs: the next answer's connection is refused, and the chat says what an admin can do.
            await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/tools/servers/{toolId[4..]}", UriKind.Relative), new { tls = "System" }));
            Assert.Equal(JsonValueKind.Null, (await RowAsync(admin, toolId)).GetProperty("tlsCa").ValueKind);
            var notice = Event(await SendAsync(admin, chat, """Again: [call echo_desk__echo {"text":"no"}]"""), "notice").GetProperty("text").GetString()!;
            Assert.Contains("Echo Desk's certificate is not trusted. An admin can trust the CA that signed it", notice, StringComparison.Ordinal);
            Assert.Contains("checked against the system's CAs", (await AuditAsync(admin, "tool.server_tls")).First().GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_self_signed_server_is_trusted_by_its_own_certificate_or_not_checked_at_all_which_is_audited_and_shown()
    {
        using var certificate = TestCertificates.Server(null, "localhost");
        await using var tls = await TlsServer.StartAsync(certificate, new FakeMcp(), "tools.example.test");
        await using var f = NewApp(NewDatabase());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var server = new { name = "Lab Desk", url = tls.Url("/mcp"), headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey };

        var refused = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", server));
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.True(refused.GetProperty("certificate").GetProperty("selfSigned").GetBoolean());
        Assert.Equal(["It is self-signed: no CA this server trusts vouches for it."], Reasons(refused));
        Assert.Equal(0, tls.Requests);

        // Its own certificate as the CA to trust.
        var own = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test",
            new { server.name, server.url, server.headerName, server.headerValue, tls = "OwnCa", tlsCa = TestCertificates.Pem(certificate) }));
        Assert.True(own.GetProperty("ok").GetBoolean(), own.ToString());

        // Not checked: accepted, and said so in the audit log and on its card.
        var off = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", new { server.name, server.url, server.headerName, server.headerValue, tls = "Off" }));
        Assert.True(off.GetProperty("ok").GetBoolean(), off.ToString());
        var made = await admin.PostAsync("/api/admin/tools/servers", new { server.name, server.url, server.headerName, server.headerValue, tls = "Off", tlsCa = "ignored" });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
        var row = await RowAsync(admin, toolId);
        Assert.Equal("Off", row.GetProperty("tls").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("tlsCa").ValueKind);
        var audited = Assert.Single(await AuditAsync(admin, "tool.server_tls"));
        Assert.Equal("Lab Desk", audited.GetProperty("target").GetString());
        Assert.Equal("certificate NOT checked (any certificate accepted)", audited.GetProperty("detail").GetString());

        var chat = await ChatAsync(admin, toolId);
        Assert.Equal("echo: unchecked", Event(await SendAsync(admin, chat, """Echo: [call lab_desk__echo {"text":"unchecked"}]"""), "tool_result").GetProperty("text").GetString());

        // Saving it again unchanged writes nothing more about its certificate.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/tools/servers/{toolId[4..]}", UriKind.Relative), new { description = "The lab's desk" }));
        Assert.Single(await AuditAsync(admin, "tool.server_tls"));
    }

    [Fact]
    public async Task A_certificate_for_another_name_or_from_another_CA_is_refused_even_with_a_CA_trusted()
    {
        using var ca = TestCertificates.Ca("Test company CA");
        using var other = TestCertificates.Ca("Other CA");
        using var elsewhere = TestCertificates.Server(ca, "elsewhere.test");
        await using var tls = await TlsServer.StartAsync(elsewhere, new FakeMcp(), "tools.example.test");
        await using var f = NewApp(NewDatabase());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var server = new { name = "Far Desk", url = tls.Url("/mcp"), headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey };

        var wrongName = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", new { server.name, server.url, server.headerName, server.headerValue, tls = "OwnCa", tlsCa = TestCertificates.Pem(ca) }));
        Assert.False(wrongName.GetProperty("ok").GetBoolean());
        Assert.Equal(["It is for elsewhere.test, not localhost."], Reasons(wrongName));

        var wrongCa = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", new { server.name, server.url, server.headerName, server.headerValue, tls = "OwnCa", tlsCa = TestCertificates.Pem(other) }));
        Assert.False(wrongCa.GetProperty("ok").GetBoolean());
        Assert.Contains("It does not lead to the CA you gave (Other CA): it was issued by Test company CA.", Reasons(wrongCa));
        Assert.Contains("It is for elsewhere.test, not localhost.", Reasons(wrongCa));
        Assert.Equal(0, tls.Requests);

        // Not checked, nothing is: not even the name.
        var off = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", new { server.name, server.url, server.headerName, server.headerValue, tls = "Off" }));
        Assert.True(off.GetProperty("ok").GetBoolean(), off.ToString());
    }

    /// <summary>The pet store's document at /openapi.yaml, its server relative to it; the rest is the pet store.</summary>
    private sealed class PetsWithDocument(FakeMcp pets) : DelegatingHandler(pets)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath == "/openapi.yaml"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FakeMcp.PetsSpec.Replace("https://pets.test/v1", "/v1", StringComparison.Ordinal), Encoding.UTF8, "application/yaml") })
                : base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task An_api_on_a_company_CA_is_read_by_its_address_and_called_once_its_CA_is_trusted()
    {
        using var ca = TestCertificates.Ca("Test company CA");
        using var certificate = TestCertificates.Server(ca, "localhost");
        var pets = new FakeMcp();
        await using var tls = await TlsServer.StartAsync(certificate, new PetsWithDocument(pets), "pets.test");
        await using var f = NewApp(NewDatabase());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var api = new { name = "Pets", specUrl = tls.Url("/openapi.yaml"), headerName = "X-Api-Key", headerValue = "pets-key" };

        // Its document pasted: Read it still meets its address's certificate, and says why it is refused.
        var pasted = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test",
            new { api.name, spec = FakeMcp.PetsSpec.Replace("https://pets.test/v1", tls.Url("/v1"), StringComparison.Ordinal) }));
        Assert.False(pasted.GetProperty("ok").GetBoolean());
        Assert.Equal($"The API at {tls.Url("/v1")} cannot be called: its certificate is not trusted. Trust the CA that signed it, or stop checking it.",
            pasted.GetProperty("error").GetString());
        Assert.Equal(["It was issued by Test company CA, a CA this server does not trust."], Reasons(pasted));

        // Its document cannot be fetched by default: Read it says why, and adding it is refused.
        var refused = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", api));
        Assert.False(refused.GetProperty("ok").GetBoolean());
        Assert.Contains("its certificate is not trusted", refused.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(["It was issued by Test company CA, a CA this server does not trust."], Reasons(refused));
        var notAdded = await admin.PostAsync("/api/admin/tools/servers", api);
        await StatusAssert.Is(HttpStatusCode.BadRequest, notAdded);
        Assert.Contains("its certificate is not trusted", await notAdded.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // With its CA: read, its address from the document, and added.
        var pem = TestCertificates.Pem(ca);
        var read = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", new { api.name, api.specUrl, tls = "OwnCa", tlsCa = pem }));
        Assert.True(read.GetProperty("ok").GetBoolean(), read.ToString());
        Assert.Equal(tls.Url("/v1"), read.GetProperty("url").GetString());
        Assert.Equal(3, read.GetProperty("tools").GetArrayLength());
        var made = await admin.PostAsync("/api/admin/tools/servers", new { api.name, api.specUrl, api.headerName, api.headerValue, tls = "OwnCa", tlsCa = pem });
        await StatusAssert.Is(HttpStatusCode.Created, made);
        var toolId = (await admin.JsonAsync(made)).GetProperty("toolId").GetString()!;
        Assert.Equal("OwnCa", (await RowAsync(admin, toolId)).GetProperty("tls").GetString());

        // Its calls go over it, with the key.
        var chat = await ChatAsync(admin, toolId);
        var listed = Event(await SendAsync(admin, chat, """Which pets? [call pets__list_pets {"limit":1}]"""), "tool_result");
        Assert.Equal("HTTP 200 OK\n[{\"name\":\"Rex\"}]", listed.GetProperty("text").GetString());
        Assert.Contains(pets.PetCalls, c => c is { Method: "GET", PathAndQuery: "/v1/pets?limit=1", Key: "pets-key" });

        // Its CA taken away: the call is refused, and the model is told why.
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/tools/servers/{toolId[4..]}", UriKind.Relative), new { tls = "System" }));
        var failed = Event(await SendAsync(admin, chat, """Again? [call pets__list_pets {"limit":1}]"""), "tool_result");
        Assert.Equal("localhost's certificate is not trusted. An admin can trust the CA that signed it, or stop checking it, in Admin → Tools (Read it says why).", failed.GetProperty("text").GetString());
        // And Read it on its kept document says why.
        var reread = await admin.JsonAsync(await admin.PostAsync($"/api/admin/tools/servers/test?id={toolId[4..]}", new { api.name }));
        Assert.False(reread.GetProperty("ok").GetBoolean());
        Assert.Equal(["It was issued by Test company CA, a CA this server does not trust."], Reasons(reread));
    }

    /// <summary>The MCP server, but /moved sends each request on (307) to the address <paramref name="to"/> gives.</summary>
    private sealed class Moved(FakeMcp mcp, Func<string> to) : DelegatingHandler(mcp)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath == "/moved"
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri(to()) } })
                : base.SendAsync(request, cancellationToken);
    }

    [Fact]
    public async Task A_server_trusted_by_its_own_CA_or_not_checked_is_followed_on_its_own_host_but_never_to_another()
    {
        using var ca = TestCertificates.Ca("Test company CA");
        using var here = TestCertificates.Server(ca, "localhost");
        using var there = TestCertificates.Server(ca, "127.0.0.1");
        var to = "";
        await using var tls = await TlsServer.StartAsync(here, new Moved(new FakeMcp(), () => to), "tools.example.test");
        await using var elsewhere = await TlsServer.StartAsync(there, new FakeMcp(), "tools.example.test");
        await using var f = NewApp(NewDatabase());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        foreach (var (check, pem) in new (string, string?)[] { ("Off", null), ("OwnCa", TestCertificates.Pem(ca)) })
        {
            object Server(string url) => new { name = "Moving Desk", url, headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey, tls = check, tlsCa = pem };

            // On its own host: followed, with its key.
            to = tls.Url("/mcp");
            var same = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", Server(tls.Url("/moved"))));
            Assert.True(same.GetProperty("ok").GetBoolean(), same.ToString());

            // To another host (its certificate signed by the same CA): not followed, so neither the choice nor its key goes there.
            to = elsewhere.Url("/mcp", "127.0.0.1");
            var other = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test", Server(tls.Url("/moved"))));
            Assert.False(other.GetProperty("ok").GetBoolean(), other.ToString());
            Assert.Contains("HTTP 307", other.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.Equal(0, elsewhere.Requests);
        }
        // Asked for directly, the other host is trusted as its own server.
        var direct = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test",
            new { name = "Other Desk", url = elsewhere.Url("/mcp", "127.0.0.1"), headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey, tls = "OwnCa", tlsCa = TestCertificates.Pem(ca) }));
        Assert.True(direct.GetProperty("ok").GetBoolean(), direct.ToString());
    }

    [Fact]
    public async Task A_handshake_that_fails_for_another_reason_than_the_certificate_says_so_and_offers_no_certificate_choice()
    {
        using var certificate = TestCertificates.Server(null, "localhost");
#pragma warning disable SYSLIB0039, CA5397 // A server that speaks only TLS 1.1, as an old one does.
        await using var tls = await TlsServer.StartAsync(certificate, new FakeMcp(), "tools.example.test", o => o.SslProtocols = System.Security.Authentication.SslProtocols.Tls11);
#pragma warning restore SYSLIB0039, CA5397
        await using var f = NewApp(NewDatabase());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        foreach (var check in new[] { "System", "Off" })
        {
            var test = await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test",
                new { name = "Old Desk", url = tls.Url("/mcp"), headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey, tls = check }));
            Assert.False(test.GetProperty("ok").GetBoolean());
            var error = test.GetProperty("error").GetString()!;
            Assert.StartsWith("No secure connection could be set up with Old Desk (", error, StringComparison.Ordinal);
            Assert.EndsWith("Its certificate is not the reason: it may not speak https there, speak only an old TLS version, or share no cipher with this app.", error, StringComparison.Ordinal);
            Assert.False(test.TryGetProperty("certificate", out var about) && about.ValueKind != JsonValueKind.Null);
        }
        Assert.Equal(0, tls.Requests);
    }

    [Fact]
    public async Task A_server_signed_by_an_issuing_CA_is_trusted_by_its_root_by_the_issuing_CA_alone_and_by_both_when_it_does_not_send_it()
    {
        using var root = TestCertificates.Ca("Test root CA");
        using var issuing = TestCertificates.Intermediate(root, "Test issuing CA");
        using var other = TestCertificates.Ca("Other CA");
        using var certificate = TestCertificates.Server(issuing, "localhost");
        await using var sends = await TlsServer.StartAsync(certificate, new FakeMcp(), "tools.example.test", o => o.ServerCertificateChain = [issuing]);
        await using var bare = await TlsServer.StartAsync(certificate, new FakeMcp(), "tools.example.test");
        await using var f = NewApp(NewDatabase());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        async Task<JsonElement> TestAsync(TlsServer server, params X509Certificate2[] cas) => await admin.JsonAsync(await admin.PostAsync("/api/admin/tools/servers/test",
            new { name = "Chain Desk", url = server.Url("/mcp"), headerName = "X-Api-Key", headerValue = FakeMcp.ApiKey, tls = "OwnCa", tlsCa = string.Join("\n", cas.Select(TestCertificates.Pem)) }));

        // It sends its issuing CA: its root is enough, and so is the issuing CA alone (as far as it goes).
        foreach (var trusted in new[] { await TestAsync(sends, root), await TestAsync(sends, issuing), await TestAsync(sends, root, issuing) })
        {
            Assert.True(trusted.GetProperty("ok").GetBoolean(), trusted.ToString());
        }
        Assert.Equal(["It does not lead to the CA you gave (Other CA): it was issued by Test issuing CA."], Reasons(await TestAsync(sends, other)));

        // It does not: the root alone cannot be reached, so the issuing CA is given too, or alone.
        var rootOnly = await TestAsync(bare, root);
        Assert.False(rootOnly.GetProperty("ok").GetBoolean());
        Assert.Equal(["It does not lead to the CA you gave (Test root CA): it was issued by Test issuing CA."], Reasons(rootOnly));
        Assert.True((await TestAsync(bare, root, issuing)).GetProperty("ok").GetBoolean());
        Assert.True((await TestAsync(bare, issuing)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_plugins_server_takes_the_check_in_its_settings_and_its_oauth_token_address_on_that_server_goes_the_same_way()
    {
        using var ca = TestCertificates.Ca("Test company CA");
        using var certificate = TestCertificates.Server(ca, "localhost");
        var gitlab = new FakeMcp();
        await using var tls = await TlsServer.StartAsync(certificate, gitlab, "gitlab.test");
        await using var f = NewApp(NewDatabase());
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/plugins/install",
            new { name = "gitlab-issues", settings = new { gitlab_url = tls.Url(""), client_id = "app-id", client_secret = "app-secret" } }));
        var id = made.GetProperty("id").GetGuid();
        var toolId = made.GetProperty("toolId").GetString()!;
        Assert.Empty(await AuditAsync(admin, "tool.server_tls"));

        // In its settings: a CA that is not one is refused; its CA is kept.
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/plugins/{id}", UriKind.Relative), new { settings = new { }, tls = "OwnCa" }));
        await StatusAssert.Is(HttpStatusCode.NoContent,
            await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/plugins/{id}", UriKind.Relative), new { settings = new { }, tls = "OwnCa", tlsCa = TestCertificates.Pem(ca) }));
        var installed = (await admin.JsonAsync(await admin.GetAsync("/api/admin/plugins"))).GetProperty("installed").EnumerateArray().Single();
        Assert.Equal("OwnCa", installed.GetProperty("tls").GetString());
        Assert.Equal(["Test company CA"], installed.GetProperty("tlsCaNames").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal("GitLab issues", Assert.Single(await AuditAsync(admin, "tool.server_tls")).GetProperty("target").GetString());

        // A person connects: the code is traded at {gitlab_url}/oauth/token, on the same server, so with its CA.
        var go = await admin.GetAsync($"/api/account/connections/{Uri.EscapeDataString(toolId)}/connect");
        var state = System.Web.HttpUtility.ParseQueryString(go.Headers.Location!.Query)["state"]!;
        var back = await admin.GetAsync($"/api/account/connections/callback?code=good-code&state={Uri.EscapeDataString(state)}");
        Assert.Equal("/account?connected=GitLab%20issues", back.Headers.Location!.ToString());
        Assert.Contains(gitlab.GitLabCalls, c => c is { Method: "POST", PathAndQuery: "/oauth/token" });

        // And its API is called over it, as the person.
        var chat = await ChatAsync(admin, toolId);
        var read = Event(await SendAsync(admin, chat, "Mine? [call gitlab_issues__my_issues {}]"), "tool_result");
        Assert.StartsWith("HTTP 200", read.GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Contains(gitlab.GitLabCalls, c => c.PathAndQuery.StartsWith("/api/v4/issues", StringComparison.Ordinal) && c.Authorization == "Bearer token-1");
    }

    [Fact]
    public void The_check_is_described_for_the_audit_log_by_the_CAs_name_and_fingerprint()
    {
        using var ca = TestCertificates.Ca("Test company CA");
        var pem = TestCertificates.Pem(ca);
        Assert.Equal($"certificate checked against its own CA: {ca.Subject} (SHA-256 {ServerTls.Fingerprint(ca)})", ServerTls.Describe(Llm.Core.Chat.TlsCheck.OwnCa, pem));
        Assert.Equal("certificate checked against the system's CAs", ServerTls.Describe(Llm.Core.Chat.TlsCheck.System, null));
        Assert.Null(ServerTls.CheckCa(pem + "\n" + TestCertificates.Pem(TestCertificates.Ca("Second CA"))));
        Assert.NotNull(ServerTls.CheckCa(new string('x', ServerTls.MaxCaChars + 1)));
        Assert.Equal(64, ServerTls.Fingerprint(ca).Count(c => c != ':'));
        Assert.Equal(ca.RawData, X509Certificate2.CreateFromPem(pem).RawData);
    }
}
