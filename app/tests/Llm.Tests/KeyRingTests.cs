using Npgsql;

namespace Llm.Tests;

[Collection(nameof(AppCollection))]
public sealed class KeyRingTests(AppFixture app)
{
    private static Dictionary<string, string?> WithKey(string key) => new() { ["Auth:DataKey"] = key };

    [Fact]
    public async Task With_a_data_key_the_key_ring_is_encrypted_and_survives_a_restart()
    {
        var cs = app.ConnectionStringFor("ring_" + Guid.NewGuid().ToString("N")[..8]);
        HttpClient signedIn;
        await using (var first = app.Create(cs, new FakeGateway(), WithKey("a-data-key-for-tests")))
        {
            var b = await new TestBrowser(first).SignedInAsync("admin", AppFixture.AdminPassword);
            // OIDC keys are created (and protected) on first use.
            await b.GetAsync("/.well-known/openid-configuration");
            signedIn = b.Http;
        }
        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""select string_agg("Xml", '') from "DataProtectionKeys" """, conn);
            var xml = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.Contains("A256GCM", xml, StringComparison.Ordinal);
            Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
        }
        await using (var second = app.Create(cs, new FakeGateway(), WithKey("a-data-key-for-tests")))
        {
            // Same key: the earlier sign-in's password check works and the OIDC keys load.
            var b = await new TestBrowser(second).SignedInAsync("admin", AppFixture.AdminPassword);
            await StatusAssert.Is(System.Net.HttpStatusCode.OK, await b.GetAsync("/.well-known/openid-configuration"));
        }
        signedIn.Dispose();
    }

    [Fact]
    public async Task A_changed_data_key_fails_loudly_instead_of_silently_resetting_keys()
    {
        var cs = app.ConnectionStringFor("ring_" + Guid.NewGuid().ToString("N")[..8]);
        await using (var first = app.Create(cs, new FakeGateway(), WithKey("the-original-key")))
        {
            await new TestBrowser(first).GetAsync("/.well-known/openid-configuration");
        }
        await using var second = app.Create(cs, new FakeGateway(), WithKey("a-different-key"));
        var ex = Record.Exception(() => second.Server);
        Assert.NotNull(ex);
        Assert.Contains("APP_DATA_KEY", ex.ToString(), StringComparison.Ordinal);
    }
}
