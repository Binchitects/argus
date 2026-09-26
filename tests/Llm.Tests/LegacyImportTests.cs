using System.Net;
using Npgsql;

namespace Llm.Tests;

/// <summary>Moving from Authelia: its users.yml is imported once and people keep their passwords.</summary>
[Collection(nameof(AppCollection))]
public sealed class LegacyImportTests(AppFixture app)
{
    // A real hash from `authelia crypto hash generate argon2` (Authelia 4.39), for a test-only password.
    private const string AutheliaHash = "$argon2id$v=19$m=65536,t=3,p=4$3ZlkaKMKxgl32LoTrP1pHQ$a9c6aru4GRZhW6vVqn3tOvycFLZBAFt6mqJTLjOaLGI";
    private const string Password = "purple elephant dancing quietly";

    [Fact]
    public async Task People_arrive_with_their_roles_and_old_passwords_and_the_hash_is_upgraded()
    {
        var file = Path.Combine(Path.GetTempPath(), $"users-{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(file, $"""
            users:
              boss:
                disabled: false
                displayname: The Boss
                password: '{AutheliaHash}'
                email: Boss@Example.test
                groups: [admins]
              worker:
                displayname: A Worker
                password: '{AutheliaHash}'
                email: worker@example.test
                groups: [users]
              idle:
                displayname: Never Signs In
                password: '{AutheliaHash}'
                email: idle@example.test
                groups: [users]
              gone:
                disabled: true
                displayname: Former
                password: '{AutheliaHash}'
                email: gone@example.test
                groups: [users]
            """);
        var db = "import_" + Guid.NewGuid().ToString("N")[..8];
        var cs = app.ConnectionStringFor(db);
        var settings = new Dictionary<string, string?> { ["Auth:ImportUsersFile"] = file };
        try
        {
            await using (var factory = app.Create(cs, new FakeGateway(), settings))
            {
                var boss = await new TestBrowser(factory).SignedInAsync("boss", Password);
                var me = await boss.JsonAsync(await boss.GetAsync("/api/auth/me"));
                Assert.True(me.GetProperty("isAdmin").GetBoolean());
                Assert.Equal("boss@example.test", me.GetProperty("email").GetString());
                Assert.Equal("The Boss", me.GetProperty("displayName").GetString());

                var worker = await new TestBrowser(factory).SignedInAsync("worker", Password);
                Assert.False((await worker.JsonAsync(await worker.GetAsync("/api/auth/me"))).GetProperty("isAdmin").GetBoolean());

                var gone = await new TestBrowser(factory).LoginAsync("gone", Password);
                Assert.Equal(HttpStatusCode.Forbidden, gone.StatusCode);

                // No admin was seeded from configuration: the file already had one.
                Assert.Equal(HttpStatusCode.Unauthorized, (await new TestBrowser(factory).LoginAsync("admin", AppFixture.AdminPassword)).StatusCode);
            }

            // The first sign-in replaced Authelia's hash with the current format; the others keep theirs until they sign in.
            await using (var conn = new NpgsqlConnection(cs))
            {
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("""select "UserName", "PasswordHash" from "AspNetUsers" order by "UserName" """, conn);
                await using var r = await cmd.ExecuteReaderAsync();
                var hashes = new Dictionary<string, string>();
                while (await r.ReadAsync())
                {
                    hashes[r.GetString(0)] = r.GetString(1);
                }
                Assert.DoesNotContain("$argon2", hashes["boss"], StringComparison.Ordinal);
                Assert.StartsWith("$argon2", hashes["idle"], StringComparison.Ordinal);
            }

            // A restart does not import again (edits made in the app stay).
            await File.WriteAllTextAsync(file, $"users:\n  latecomer:\n    password: '{AutheliaHash}'\n    email: late@example.test\n    groups: [admins]\n");
            await using (var factory = app.Create(cs, new FakeGateway(), settings))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await new TestBrowser(factory).LoginAsync("latecomer", Password)).StatusCode);
                await new TestBrowser(factory).SignedInAsync("boss", Password);
            }
        }
        finally
        {
            File.Delete(file);
        }
    }
}
