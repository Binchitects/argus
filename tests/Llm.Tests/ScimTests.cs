using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Llm.Tests;

[Collection(nameof(AppCollection))]
public sealed class ScimTests(AppFixture app, CompanyIdpFixture company) : IClassFixture<CompanyIdpFixture>
{
    private const string PatchOp = "urn:ietf:params:scim:api:messages:2.0:PatchOp";

    /// <summary>The identity provider's SCIM client: the token as a bearer, SCIM's media type.</summary>
    private sealed class ScimClient(WebApplicationFactory<Program> factory, string token)
    {
        private readonly HttpClient _http = Client(factory, token);

        private static HttpClient Client(WebApplicationFactory<Program> factory, string token)
        {
            var http = new TestBrowser(factory).Http;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return http;
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
        {
            var req = new HttpRequestMessage(method, new Uri("/scim/v2" + path, UriKind.Relative));
            if (body is not null)
            {
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/scim+json");
            }
            return _http.SendAsync(req);
        }

        public async Task<JsonElement> JsonAsync(HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
        {
            var res = await SendAsync(method, path, body);
            await StatusAssert.Is(expected, res);
            Assert.Equal("application/scim+json", res.Content.Headers.ContentType?.MediaType);
            return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        }

        public Task<JsonElement> GetAsync(string path) => JsonAsync(HttpMethod.Get, path);

        public static object Patch(params object[] operations) => new { schemas = new[] { PatchOp }, Operations = operations };
    }

    private static object NewUser(string userName, string email, string? externalId = null, bool active = true) => new
    {
        schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" },
        userName,
        externalId = externalId ?? Guid.NewGuid().ToString(),
        name = new { givenName = "Given", familyName = "Family" },
        emails = new[] { new { value = email, type = "work", primary = true } },
        active,
    };

    private static async Task<string> MakeTokenAsync(WebApplicationFactory<Program> f)
    {
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var res = await admin.PostAsync("/api/admin/company-sign-in/scim-token");
        await StatusAssert.Is(HttpStatusCode.OK, res);
        return (await admin.JsonAsync(res)).GetProperty("token").GetString()!;
    }

    private async Task<(WebApplicationFactory<Program> App, ScimClient Scim)> ScimAsync()
    {
        var f = await company.AppAsync(app);
        company.ScimToken ??= await MakeTokenAsync(f);
        return (f, new ScimClient(f, company.ScimToken));
    }

    [Fact]
    public async Task Scim_needs_its_token_which_is_shown_once_stored_as_a_hash_and_replaced_at_once()
    {
        var database = app.ConnectionStringFor("scimtoken_" + Guid.NewGuid().ToString("N")[..8]);
        await using var f = app.Create(database, new FakeGateway());
        var none = await new ScimClient(f, "scim_guess").SendAsync(HttpMethod.Get, "/Users");
        await StatusAssert.Is(HttpStatusCode.Unauthorized, none);
        Assert.StartsWith("Bearer", none.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        var first = await MakeTokenAsync(f);
        Assert.StartsWith("scim_", first, StringComparison.Ordinal);
        await new ScimClient(f, first).GetAsync("/Users");
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var status = await admin.JsonAsync(await admin.GetAsync("/api/admin/company-sign-in"));
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("scim").GetProperty("tokenMadeAt").ValueKind);
        Assert.DoesNotContain(first, status.ToString(), StringComparison.Ordinal);
        // Only its SHA-256 is stored.
        await using (var conn = new Npgsql.NpgsqlConnection(database))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand("select value from settings where key like 'scim.%'", conn);
            var stored = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(first))), stored);
        }

        var second = await MakeTokenAsync(f);
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await new ScimClient(f, first).SendAsync(HttpMethod.Get, "/Users"));
        await new ScimClient(f, second).GetAsync("/Users");
        await StatusAssert.Is(HttpStatusCode.NoContent, await admin.Http.DeleteAsync(new Uri("/api/admin/company-sign-in/scim-token", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await new ScimClient(f, second).SendAsync(HttpMethod.Get, "/Users"));
        var audit = (await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=20"))).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("scim.token", audit);
        Assert.Contains("scim.token_revoke", audit);
    }

    [Fact]
    public async Task A_person_deactivated_through_scim_is_disabled_here_and_their_keys_stop_at_once()
    {
        var (f, scim) = await ScimAsync();
        var made = await scim.JsonAsync(HttpMethod.Post, "/Users", NewUser("hana@example.test", "hana@example.test"), HttpStatusCode.Created);
        var id = made.GetProperty("id").GetString();
        Assert.Equal("hana", made.GetProperty("userName").GetString());
        Assert.Equal("Given Family", made.GetProperty("displayName").GetString());
        Assert.True(made.GetProperty("active").GetBoolean());
        var key = Assert.Single(company.Gateway.KeysOf("hana@example.test"));
        Assert.False(key.Blocked);

        // She signs in with her company account: the person SCIM made, found by email.
        var (session, answer) = await company.SignInAsync(f, new("sub-hana", "hana", "hana@example.test", ["llm-users"]));
        Assert.Equal("/chat", answer.Headers.Location!.ToString());
        Assert.Equal(id, (await session.JsonAsync(await session.GetAsync("/api/auth/me"))).GetProperty("id").GetString());

        // Entra ID's way: a path, and the value as text.
        var off = await scim.JsonAsync(HttpMethod.Patch, $"/Users/{id}", ScimClient.Patch(new { op = "Replace", path = "active", value = "False" }));
        Assert.False(off.GetProperty("active").GetBoolean());
        await StatusAssert.Is(HttpStatusCode.Unauthorized, await session.GetAsync("/api/auth/me"));
        Assert.True(key.Blocked);
        var (_, refused) = await company.SignInAsync(f, new("sub-hana", "hana", "hana@example.test", ["llm-users"]));
        Assert.StartsWith("/login?error=company_disabled", refused.Headers.Location!.ToString(), StringComparison.Ordinal);
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/people"))).GetProperty("people").EnumerateArray().Single(p => p.GetProperty("userName").GetString() == "hana");
        Assert.Equal("scim", row.GetProperty("disabledReason").GetString());
        Assert.Equal("oidc", row.GetProperty("source").GetString());
        Assert.Contains((await admin.JsonAsync(await admin.GetAsync("/api/admin/audit?take=50"))).EnumerateArray(),
            e => e.GetProperty("action").GetString() == "person.disable" && e.GetProperty("target").GetString() == "hana" && e.GetProperty("actor").GetString() == "scim");

        // Okta's way: no path, an object of attributes.
        var on = await scim.JsonAsync(HttpMethod.Patch, $"/Users/{id}", ScimClient.Patch(new { op = "replace", value = new { active = true } }));
        Assert.True(on.GetProperty("active").GetBoolean());
        Assert.False(key.Blocked);

        // Deleted in the provider: disabled here, not deleted (their chats stay until an admin deletes them).
        await StatusAssert.Is(HttpStatusCode.NoContent, await scim.SendAsync(HttpMethod.Delete, $"/Users/{id}"));
        Assert.True(key.Blocked);
        Assert.False((await scim.GetAsync($"/Users/{id}")).GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Scim_finds_makes_and_changes_people_and_refuses_what_would_clash()
    {
        var (f, scim) = await ScimAsync();
        var made = await scim.JsonAsync(HttpMethod.Post, "/Users", NewUser("Ivo.Petrov@example.test", "ivo.petrov@example.test", externalId: "ext-ivo"), HttpStatusCode.Created);
        var id = made.GetProperty("id").GetString();
        Assert.Equal("ivo.petrov", made.GetProperty("userName").GetString());
        Assert.Equal("ext-ivo", made.GetProperty("externalId").GetString());

        // The ways identity providers look someone up.
        async Task<int> Count(string filter) => (await scim.GetAsync("/Users?filter=" + Uri.EscapeDataString(filter))).GetProperty("totalResults").GetInt32();
        Assert.Equal(1, await Count("userName eq \"ivo.petrov@example.test\""));
        Assert.Equal(1, await Count("externalId eq \"ext-ivo\""));
        Assert.Equal(1, await Count("emails[type eq \"work\"].value eq \"IVO.PETROV@example.test\""));
        Assert.Equal(1, await Count($"id eq \"{id}\" and active eq true"));
        Assert.Equal(0, await Count("userName eq \"nobody\""));
        var invalid = await scim.JsonAsync(HttpMethod.Get, "/Users?filter=" + Uri.EscapeDataString("userName eq \"a\" or userName eq \"b\""), expected: HttpStatusCode.BadRequest);
        Assert.Equal("invalidFilter", invalid.GetProperty("scimType").GetString());
        await scim.JsonAsync(HttpMethod.Get, "/Users?filter=" + Uri.EscapeDataString("userName co \"ivo\""), expected: HttpStatusCode.BadRequest);

        var clash = await scim.JsonAsync(HttpMethod.Post, "/Users", NewUser("ivo.petrov", "someone.else@example.test"), HttpStatusCode.Conflict);
        Assert.Equal("uniqueness", clash.GetProperty("scimType").GetString());
        await scim.JsonAsync(HttpMethod.Post, "/Users", NewUser("noemail", "not-an-email"), HttpStatusCode.BadRequest);

        var put = await scim.JsonAsync(HttpMethod.Put, $"/Users/{id}", new
        {
            userName = "ivo.petrov@example.test",
            displayName = "Ivo P.",
            emails = new[] { new { value = "ivo.petrov@example.test", primary = true } },
            active = true,
        });
        Assert.Equal("Ivo P.", put.GetProperty("displayName").GetString());

        // A new email: a new key under it, and the old one gone.
        Assert.NotEmpty(company.Gateway.KeysOf("ivo.petrov@example.test"));
        var moved = await scim.JsonAsync(HttpMethod.Patch, $"/Users/{id}", ScimClient.Patch(new { op = "Replace", path = "emails[type eq \"work\"].value", value = "ivo@example.test" }));
        Assert.Equal("ivo@example.test", moved.GetProperty("emails")[0].GetProperty("value").GetString());
        Assert.Empty(company.Gateway.KeysOf("ivo.petrov@example.test"));
        Assert.NotEmpty(company.Gateway.KeysOf("ivo@example.test"));

        // One part of the name keeps the other.
        var renamed = await scim.JsonAsync(HttpMethod.Patch, $"/Users/{id}", ScimClient.Patch(new { op = "replace", path = "name.givenName", value = "Ivan" }));
        Assert.Equal("Ivan P.", renamed.GetProperty("displayName").GetString());

        // Local admins are not seen, so the provider cannot change them.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var adminId = (await admin.JsonAsync(await admin.GetAsync("/api/auth/me"))).GetProperty("id").GetString();
        Assert.Equal(0, await Count("userName eq \"admin\""));
        await scim.JsonAsync(HttpMethod.Get, $"/Users/{adminId}", expected: HttpStatusCode.NotFound);
        await scim.JsonAsync(HttpMethod.Patch, $"/Users/{adminId}", ScimClient.Patch(new { op = "replace", path = "active", value = false }), HttpStatusCode.NotFound);
        await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);

        var page = await scim.GetAsync("/Users?startIndex=1&count=1");
        Assert.Equal(1, page.GetProperty("itemsPerPage").GetInt32());
        Assert.True(page.GetProperty("totalResults").GetInt32() >= 1);
        var bad = await scim.SendAsync(HttpMethod.Patch, $"/Users/{id}", new { Operations = new[] { new { op = "move", path = "active" } } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, bad);
    }

    [Fact]
    public async Task Scim_groups_are_app_groups_whose_name_and_members_only_the_provider_changes()
    {
        var (f, scim) = await ScimAsync();
        var jo = (await scim.JsonAsync(HttpMethod.Post, "/Users", NewUser("jo", "jo@example.test"), HttpStatusCode.Created)).GetProperty("id").GetString();
        var kim = (await scim.JsonAsync(HttpMethod.Post, "/Users", NewUser("kim", "kim@example.test"), HttpStatusCode.Created)).GetProperty("id").GetString();
        var made = await scim.JsonAsync(HttpMethod.Post, "/Groups", new
        {
            schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" },
            displayName = "Data Science",
            externalId = "grp-ds",
            members = new[] { new { value = jo } },
        }, HttpStatusCode.Created);
        var gid = made.GetProperty("id").GetString();
        Assert.Single(made.GetProperty("members").EnumerateArray());

        // Entra ID's lookup: by name, without the members.
        var found = await scim.GetAsync("/Groups?filter=" + Uri.EscapeDataString("displayName eq \"data science\"") + "&excludedAttributes=members");
        Assert.Equal(1, found.GetProperty("totalResults").GetInt32());
        Assert.False(found.GetProperty("Resources")[0].TryGetProperty("members", out _));

        var patched = await scim.JsonAsync(HttpMethod.Patch, $"/Groups/{gid}", ScimClient.Patch(
            new { op = "Add", path = "members", value = new[] { new { value = kim } } },
            new { op = "Remove", path = $"members[value eq \"{jo}\"]" }));
        Assert.Equal([kim], patched.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("value").GetString()));
        Assert.Contains("Data Science", (await scim.GetAsync($"/Users/{kim}")).GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("display").GetString()));
        Assert.Equal(1, (await scim.GetAsync("/Groups?filter=" + Uri.EscapeDataString($"members[value eq \"{kim}\"]"))).GetProperty("totalResults").GetInt32());

        // In the app it is a group like any other, for access rules; its members are the provider's to change.
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var row = (await admin.JsonAsync(await admin.GetAsync("/api/admin/groups"))).EnumerateArray().Single(g => g.GetProperty("id").GetString() == gid);
        Assert.True(row.GetProperty("scim").GetBoolean());
        Assert.Equal(1, row.GetProperty("members").GetInt32());
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync($"/api/admin/groups/{gid}/members", new { userIds = new[] { Guid.Parse(jo!) } }));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.DeleteAsync(new Uri($"/api/admin/groups/{gid}/members/{kim}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.Http.PatchAsync(new Uri($"/api/admin/groups/{gid}", UriKind.Relative),
            new StringContent("{\"name\":\"Renamed\"}", Encoding.UTF8, "application/json")));

        // A name another group has is refused; groups made in the app are not seen.
        await StatusAssert.Is(HttpStatusCode.Created, await admin.PostAsync("/api/admin/groups", new { name = "Finance" }));
        await scim.JsonAsync(HttpMethod.Patch, $"/Groups/{gid}", ScimClient.Patch(new { op = "replace", path = "displayName", value = "finance" }), HttpStatusCode.Conflict);
        Assert.Equal(0, (await scim.GetAsync("/Groups?filter=" + Uri.EscapeDataString("displayName eq \"Finance\""))).GetProperty("totalResults").GetInt32());
        var renamed = await scim.JsonAsync(HttpMethod.Patch, $"/Groups/{gid}", ScimClient.Patch(new { op = "replace", value = new { displayName = "DS Team" } }));
        Assert.Equal("DS Team", renamed.GetProperty("displayName").GetString());

        await StatusAssert.Is(HttpStatusCode.NoContent, await scim.SendAsync(HttpMethod.Delete, $"/Groups/{gid}"));
        await scim.JsonAsync(HttpMethod.Get, $"/Groups/{gid}", expected: HttpStatusCode.NotFound);
        Assert.DoesNotContain((await admin.JsonAsync(await admin.GetAsync("/api/admin/groups"))).EnumerateArray(), g => g.GetProperty("id").GetString() == gid);
    }

    [Fact]
    public async Task A_scim_group_named_as_the_admin_group_makes_its_members_admins_at_sign_in()
    {
        // Entra ID's tokens name groups by object ID; its SCIM sends their names.
        var (f, scim) = await ScimAsync();
        var lee = (await scim.JsonAsync(HttpMethod.Post, "/Users", NewUser("lee@example.test", "lee@example.test"), HttpStatusCode.Created)).GetProperty("id").GetString();
        foreach (var name in new[] { "llm-users", "llm-admins" })
        {
            await scim.JsonAsync(HttpMethod.Post, "/Groups", new { displayName = name, members = new[] { new { value = lee } } }, HttpStatusCode.Created);
        }
        var (b, answer) = await company.SignInAsync(f, new("sub-lee", "lee@example.test", "lee@example.test", ["0f3c2d4e-0000-4000-8000-00000000abcd"]));
        Assert.Equal("/chat", answer.Headers.Location!.ToString());
        Assert.True((await b.JsonAsync(await b.GetAsync("/api/auth/me"))).GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task The_discovery_documents_say_what_this_server_supports()
    {
        var (_, scim) = await ScimAsync();
        var config = await scim.GetAsync("/ServiceProviderConfig");
        Assert.True(config.GetProperty("patch").GetProperty("supported").GetBoolean());
        Assert.False(config.GetProperty("bulk").GetProperty("supported").GetBoolean());
        Assert.True(config.GetProperty("filter").GetProperty("supported").GetBoolean());
        Assert.Equal("oauthbearertoken", config.GetProperty("authenticationSchemes")[0].GetProperty("type").GetString());
        var types = await scim.GetAsync("/ResourceTypes");
        Assert.Equal(["User", "Group"], types.GetProperty("Resources").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.Equal("/Groups", (await scim.GetAsync("/ResourceTypes/Group")).GetProperty("endpoint").GetString());
        var schemas = await scim.GetAsync("/Schemas");
        Assert.Equal(2, schemas.GetProperty("totalResults").GetInt32());
        var user = await scim.GetAsync("/Schemas/urn:ietf:params:scim:schemas:core:2.0:User");
        Assert.Contains("userName", user.GetProperty("attributes").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
        await scim.JsonAsync(HttpMethod.Get, "/Nothing", expected: HttpStatusCode.NotFound);
    }
}
