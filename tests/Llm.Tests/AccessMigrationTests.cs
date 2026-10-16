using Llm.Api.Access;
using Llm.Core.Access;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// The move to roles and rules keeps every decision as it was: each person, before and after, may use the
/// same tools and models and read the same knowledge sources (the old check, Membership.May, against the new
/// one), admins are Owners, and a person whose API access was off still has none.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class AccessMigrationTests(AppFixture app)
{
    /// <summary>The check before roles and rules: admins always; else everyone, or a member of one of the listed groups.</summary>
    private static bool OldMay(bool admin, IReadOnlySet<Guid> groups, int audience, Guid[] listed) =>
        admin || audience switch
        {
            0 => true,
            2 => listed.Any(groups.Contains),
            _ => false,
        };

    private sealed record Person(Guid Id, string Name, bool Admin, Guid[] AppGroups, string[] Directory, bool ApiOff = false, int Source = 0);

    private sealed record Resource(string Kind, string Id, int Audience, Guid[] Groups);

    [Fact]
    public async Task Every_decision_is_the_same_after_the_move()
    {
        var cs = app.ConnectionStringFor("access_" + Guid.NewGuid().ToString("N")[..8]);
        var options = new DbContextOptionsBuilder<AppDbContext>();
        options.UseNpgsql(cs);
        options.UseOpenIddict<Guid>();
        await using var db = new AppDbContext(options.Options);
        var migrator = db.GetService<IMigrator>();
        var all = db.Database.GetMigrations().ToList();
        await migrator.MigrateAsync(all[all.FindIndex(m => m.EndsWith("_AccessControl", StringComparison.Ordinal)) - 1]);

        Guid G(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");
        Guid ml = G(101), ops = G(102), linked = G(103), gone = G(199);
        Person[] people =
        [
            new(G(1), "boss", true, [], []),
            new(G(2), "sara", false, [ml], []),
            new(G(3), "omid", false, [ops], []),
            new(G(4), "dana", false, [ml, ops], [], ApiOff: true),
            new(G(5), "lee", false, [], ["cn=Platform,ou=groups,dc=example,dc=test"], Source: 1),
            new(G(6), "nobody", false, [], []),
            new(G(7), "ldapboss", true, [], ["cn=Admins,ou=groups,dc=example,dc=test"], ApiOff: true, Source: 1),
        ];
        Resource[] resources =
        [
            new("tool", "web", 0, []),
            new("tool", "python", 1, []),
            new("tool", "mcp:" + G(301), 2, [ml]),
            new("tool", "image", 2, [ml, ops]),
            new("tool", "laya", 2, []),
            new("tool", "video", 2, [gone]),
            new("tool", "calculator", 2, [linked]),
            new("model", "qwen", 0, []),
            new("model", "big", 1, []),
            new("model", "team", 2, [ops]),
            new("model", "platform", 2, [linked, gone]),
            new("knowledge_source", G(401).ToString(), 0, []),
            new("knowledge_source", G(402).ToString(), 1, []),
            new("knowledge_source", G(403).ToString(), 2, [ml]),
            new("knowledge_source", G(404).ToString(), 1, []),
        ];
        string Kind(Resource r) => r.Id == G(404).ToString() ? "confluence" : "folder";

        await using (var conn = new NpgsqlConnection(cs))
        {
            await conn.OpenAsync();
            var sql = new System.Text.StringBuilder("""
                INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName") VALUES ('00000000-0000-0000-0000-00000000aaaa','admin','ADMIN'), ('00000000-0000-0000-0000-00000000bbbb','member','MEMBER');
                """);
            sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"""
                INSERT INTO groups ("Id","Name","Priority","CreatedAt","CreditPerMember","Scim") VALUES ('{ml}','ML',0,now(),false,false), ('{ops}','Ops',0,now(),false,false);
                INSERT INTO groups ("Id","Name","Directory","Priority","CreatedAt","CreditPerMember","Scim") VALUES ('{linked}','Platform','Platform',0,now(),false,false);
                """);
            foreach (var p in people)
            {
                sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"""
                    INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount","DisplayName","Source","IsDisabled","CreatedAt","DirectoryGroups","ApiOff")
                      VALUES ('{p.Id}','{p.Name}','{p.Name.ToUpperInvariant()}','{p.Name}@example.test','{p.Name.ToUpperInvariant()}@EXAMPLE.TEST',true,false,false,true,0,'{p.Name}',{p.Source},false,now(),ARRAY[{string.Join(',', p.Directory.Select(d => $"'{d}'"))}]::text[],{(p.ApiOff ? "true" : "false")});
                    INSERT INTO "AspNetUserRoles" ("UserId","RoleId") VALUES ('{p.Id}','{(p.Admin ? "00000000-0000-0000-0000-00000000aaaa" : "00000000-0000-0000-0000-00000000bbbb")}');
                    """);
                foreach (var g in p.AppGroups)
                {
                    sql.Append(System.Globalization.CultureInfo.InvariantCulture, $"""INSERT INTO group_members ("GroupId","UserId","AddedAt") VALUES ('{g}','{p.Id}',now());""");
                }
            }
            string Uuids(Guid[] ids) => $"ARRAY[{string.Join(',', ids.Select(g => $"'{g}'"))}]::uuid[]";
            foreach (var r in resources)
            {
                sql.Append(r.Kind switch
                {
                    "tool" => $"""INSERT INTO tool_settings ("ToolId","Enabled","Audience","Groups","OnByDefault","AskFirst","UpdatedAt") VALUES ('{r.Id}',true,{r.Audience},{Uuids(r.Groups)},true,false,now());""",
                    "model" => $"""INSERT INTO model_access ("Model","Audience","Groups","UpdatedAt") VALUES ('{r.Id}',{r.Audience},{Uuids(r.Groups)},now());""",
                    _ => $"""
                        INSERT INTO knowledge_sources ("Id","Name","Kind","Location","Wiki","Issues","MaxPages","BlogPosts","SitePages","Audience","Groups","State","CreatedAt")
                          VALUES ('{r.Id}','source {r.Id[^3..]}','{Kind(r)}','/knowledge/{r.Id[^3..]}',false,false,10,false,true,{r.Audience},{Uuids(r.Groups)},'synced',now());
                        INSERT INTO knowledge_documents ("Id","SourceId","Key","Title","Readers","Requires","SyncedAt")
                          VALUES (gen_random_uuid(),'{r.Id}','doc','Doc',ARRAY['everyone']::text[],ARRAY[]::text[],now());
                        """,
                });
                sql.Append('\n');
            }
            await using var cmd = new NpgsqlCommand(sql.ToString(), conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await migrator.MigrateAsync();
        await AccessService.SyncBuiltInRolesAsync(db);
        var version = await db.AccessMeta.Select(m => m.Version).SingleAsync();
        var snapshot = await AccessService.LoadAsync(db, version, default);
        var now = DateTimeOffset.UtcNow;
        var directoryGroups = await db.Groups.Where(g => g.Directory != null).ToListAsync();
        var checkedCount = 0;
        foreach (var p in people)
        {
            var groups = p.AppGroups.Concat(directoryGroups.Where(g => AccessService.InDirectoryGroup(p.Directory, g.Directory!)).Select(g => g.Id)).ToHashSet();
            var principal = Principal.Of(p.Id, groups);
            Assert.Equal(p.Admin, snapshot.IsOwner(principal, now));
            Assert.Equal(!p.ApiOff, snapshot.Check(principal, Permissions.ApiKeys, now).Allowed);
            Assert.True(snapshot.Check(principal, Permissions.Chat, now).Allowed);
            foreach (var r in resources)
            {
                var action = r.Kind == "knowledge_source" ? ResourceKinds.Read : ResourceKinds.Use;
                Assert.True(OldMay(p.Admin, groups, r.Audience, r.Groups) == snapshot.Check(principal, r.Kind, r.Id, action, now).Allowed,
                    $"{p.Name} on {r.Kind} {r.Id}: was {OldMay(p.Admin, groups, r.Audience, r.Groups)}");
                checkedCount++;
            }
        }
        Assert.Equal(people.Length * resources.Length, checkedCount);
        // A resource no rule named (a tool or a model added later) is everyone's, as one without a row was.
        Assert.True(snapshot.Check(Principal.Of(G(6)), ResourceKinds.Tool, "new-tool", ResourceKinds.Use, now).Allowed);
        // The folders' documents are read by the rules on their source; the Identity roles are gone.
        Guid folder = G(401), confluence = G(404);
        var readers = await db.KnowledgeDocuments.Where(d => d.SourceId == folder).Select(d => d.Readers).SingleAsync();
        Assert.Equal(["chosen:" + folder.ToString("N")], readers);
        Assert.Equal(["everyone"], await db.KnowledgeDocuments.Where(d => d.SourceId == confluence).Select(d => d.Readers).SingleAsync());
        Assert.Equal(0, await db.UserRoles.CountAsync());
        Assert.Equal(BuiltInRoles.All.Count, await db.AccessRoles.CountAsync(r => r.BuiltIn));
        Assert.Equal(BuiltInRoles.Find(BuiltInRoles.Owner)!.Permissions.Count, (await db.AccessRoles.SingleAsync(r => r.Key == BuiltInRoles.Owner)).Permissions.Count);
    }
}
