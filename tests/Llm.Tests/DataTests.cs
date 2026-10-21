using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Llm.Tests;

/// <summary>
/// Ask your data: a PostgreSQL database an admin connects (read only, the tables chosen, for everyone or groups); a
/// question turned into SQL by the model and run; SQL the person changed; boards of pinned charts, refreshed and shared.
/// </summary>
[Collection(nameof(AppCollection))]
public sealed class DataTests(AppFixture app)
{
    private WebApplicationFactory<Program> NewApp() =>
        app.Create(app.ConnectionStringFor("data_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>());

    private static async Task<(TestBrowser Browser, Guid Id)> PersonAsync(TestBrowser admin, WebApplicationFactory<Program> f, string display)
    {
        var name = "d" + Guid.NewGuid().ToString("N")[..10];
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/people", new { userName = name, email = $"{name}@example.test", displayName = display }));
        return (await new TestBrowser(f).SignedInAsync(name, made.GetProperty("password").GetString()!), made.GetProperty("id").GetGuid());
    }

    /// <summary>
    /// A shop's database: customers, three orders, a secrets table nobody may ask, and a view of the big orders. A role that
    /// may read it all (the allow-list keeps the secrets out), and one that may also write.
    /// </summary>
    private async Task<(string Reader, string Writer, string Admin)> ShopAsync()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var database = $"shop_{id}";
        var admin = app.ConnectionStringFor("postgres");
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var create = new NpgsqlCommand($"create database {database}; create role reader_{id} login password 'read-{id}'; create role writer_{id} login password 'write-{id}'", conn);
            await create.ExecuteNonQueryAsync();
        }
        var shop = new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;
        await using (var conn = new NpgsqlConnection(shop))
        {
            await conn.OpenAsync();
            await using var seed = new NpgsqlCommand($"""
                create schema shop;
                create table shop.customers (id int primary key, name text not null, country text not null);
                comment on table shop.customers is 'People who bought';
                create table shop.orders (id int primary key, customer_id int references shop.customers, total numeric(10, 2) not null, placed_at timestamptz not null);
                comment on column shop.orders.total is 'In euros';
                create table shop.secrets (id int, value text);
                create view shop.big_orders as select id, total from shop.orders where total > 100;
                insert into shop.customers values (1, 'Ana', 'PT'), (2, 'Ben', 'DE');
                insert into shop.orders values (1, 1, 50, '2026-10-01'), (2, 1, 150, '2026-10-02'), (3, 2, 300, '2026-10-03');
                insert into shop.secrets values (1, 'the vault code');
                grant usage on schema shop to reader_{id}, writer_{id};
                grant select on all tables in schema shop to reader_{id}, writer_{id};
                grant insert on shop.orders to writer_{id};
                """, conn);
            await seed.ExecuteNonQueryAsync();
        }
        string As(string role, string password) => new NpgsqlConnectionStringBuilder(shop) { Username = role, Password = password }.ConnectionString;
        return (As($"reader_{id}", $"read-{id}"), As($"writer_{id}", $"write-{id}"), shop);
    }

    private static async Task<long> CountAsync(string connection, string table)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var count = new NpgsqlCommand($"select count(*) from {table}", conn);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task An_admin_connects_a_read_only_database_and_its_chosen_tables_are_asked_by_its_groups_only()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, anaId) = await PersonAsync(admin, f, "Ana");
        var (ben, benId) = await PersonAsync(admin, f, "Ben");
        var (reader, writer, owner) = await ShopAsync();

        // A superuser is refused; a role that may write is warned of; the reader's tables are listed.
        var refused = await admin.PostAsync("/api/admin/data-sources/probe", new { connection = owner });
        await StatusAssert.Is(HttpStatusCode.BadRequest, refused);
        Assert.Contains("superuser", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var warned = await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources/probe", new { connection = writer }));
        Assert.Contains("may change 1 table", warned.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        var probed = await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources/probe", new { connection = reader }));
        Assert.Empty(probed.GetProperty("warnings").EnumerateArray());
        var tables = probed.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t);
        Assert.Equal(["shop.big_orders", "shop.customers", "shop.orders", "shop.secrets"], tables.Keys.Order());
        Assert.Equal("view", tables["shop.big_orders"].GetProperty("kind").GetString());
        Assert.Equal("People who bought", tables["shop.customers"].GetProperty("comment").GetString());
        Assert.Contains(tables["shop.orders"].GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "total" && c.GetProperty("comment").GetString() == "In euros");

        // For a group; a table it does not have is refused; the connection never comes back.
        var team = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Sales" }))).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/admin/groups/{team}/members", new { userIds = new[] { anaId } });
        await StatusAssert.Is(HttpStatusCode.BadRequest, await admin.PostAsync("/api/admin/data-sources", new { name = "Shop", connection = reader, tables = new[] { "shop.nope" } }));
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources", new
        {
            name = "Shop", description = "The web shop.", connection = reader, tables = new[] { "shop.customers", "shop.orders", "shop.big_orders" }, notes = "Totals are in euros.",
            reach = "groups", groupIds = new[] { team },
        }));
        var source = made.GetProperty("id").GetGuid();
        var listed = (await admin.GetAsync("/api/admin/data-sources")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("read-", await listed, StringComparison.Ordinal);
        Assert.StartsWith("reader_", made.GetProperty("where").GetString(), StringComparison.Ordinal);

        // Ana (in the group) sees its open tables only; Ben does not see it at all.
        var seen = (await ana.JsonAsync(await ana.GetAsync("/api/data/sources"))).EnumerateArray().Single();
        Assert.Equal(["shop.big_orders", "shop.customers", "shop.orders"], seen.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order());
        Assert.Empty((await ben.JsonAsync(await ben.GetAsync("/api/data/sources"))).EnumerateArray());
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.PostAsync("/api/data/run", new { sourceId = source, sql = "select 1" }));

        // Read only: what it may read, and nothing else, a limited number of rows, for a limited time.
        async Task<HttpResponseMessage> Run(string sql) => await ana.PostAsync("/api/data/run", new { sourceId = source, sql });
        var counted = await ana.JsonAsync(await Run("select count(*) as \"Orders\" from shop.orders;"));
        Assert.Equal(("Orders", "number", 3), (counted.GetProperty("columns")[0].GetProperty("name").GetString(), counted.GetProperty("columns")[0].GetProperty("type").GetString(),
            counted.GetProperty("rows")[0][0].GetInt32()));
        var big = await ana.JsonAsync(await Run("select id, total from shop.big_orders order by id"));
        Assert.Equal([150m, 300m], big.GetProperty("rows").EnumerateArray().Select(r => r[1].GetDecimal()));
        var secret = await Run("select o.id, s.value from shop.orders o join shop.secrets s on s.id = o.id");
        await StatusAssert.Is(HttpStatusCode.BadRequest, secret);
        Assert.Contains("shop.secrets", await secret.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("Only a query that reads", await (await Run("delete from shop.orders")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("One query at a time", await (await Run("select 1; delete from shop.orders")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A role that may write still only reads here: the transaction is read only, and a string the check reads wrongly
        // cannot carry a COMMIT, and a write after it, out of it.
        var writes = (await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources", new { name = "Shop (writer)", connection = writer, tables = new[] { "shop.orders" }, reach = "company" })))
            .GetProperty("id").GetGuid();
        async Task<string> Write(string sql) => await (await ana.PostAsync("/api/data/run", new { sourceId = writes, sql })).Content.ReadAsStringAsync();
        Assert.Contains("read-only transaction", await Write("with added as (insert into shop.orders values (9, 1, 1, now()) returning id) select count(*) from added"), StringComparison.Ordinal);
        Assert.Contains("multiple commands", await Write("select E'\\'';commit;insert into shop.orders values (9, 1, 1, now()); --'"), StringComparison.Ordinal);
        Assert.Equal(3, await CountAsync(owner, "shop.orders"));

        await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/data-sources/{source}", UriKind.Relative), new { mostRows = 2, seconds = 1 });
        var two = await ana.JsonAsync(await Run("select * from shop.orders order by id"));
        Assert.Equal((2, true), (two.GetProperty("rows").GetArrayLength(), two.GetProperty("truncated").GetBoolean()));
        Assert.Contains("longer than 1 seconds", await (await Run("select pg_sleep(3)")).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Every query is kept: who, what, how it went.
        var queries = (await admin.JsonAsync(await admin.GetAsync($"/api/admin/data-sources/{source}/queries"))).EnumerateArray().ToList();
        Assert.Equal(7, queries.Count);
        Assert.All(queries, q => Assert.Equal("Ana", q.GetProperty("person").GetString()));
        Assert.Contains(queries, q => q.GetProperty("error").GetString()?.Contains("shop.secrets", StringComparison.Ordinal) == true);

        // Asking is a feature of its own.
        await admin.PostAsync("/api/admin/access/bulk", new { people = new[] { anaId }, steps = new[] { new { kind = "feature.deny", feature = "feature.data" } }, dryRun = false });
        await StatusAssert.Is(HttpStatusCode.Forbidden, await ana.GetAsync("/api/data/sources"));
        _ = benId;
    }

    [Fact]
    public async Task A_question_becomes_SQL_explained_and_run_and_a_failed_one_is_written_again()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, _) = await PersonAsync(admin, f, "Ana");
        var (reader, _, _) = await ShopAsync();
        var source = (await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources", new
        {
            name = "Shop", connection = reader, tables = new[] { "shop.customers", "shop.orders" }, notes = "Totals are in euros.", reach = "company",
        }))).GetProperty("id").GetGuid();

        var asked = await ana.JsonAsync(await ana.PostAsync("/api/data/ask", new { sourceId = source, question = "How many orders by country? [sql:select c.country as \"Country\", count(*) as \"Orders\" from shop.orders o join shop.customers c on c.id = o.customer_id group by 1 order by 1][chart:bar]" }));
        Assert.Contains("count(*)", asked.GetProperty("sql").GetString(), StringComparison.Ordinal);
        Assert.Equal("Counts what was asked.", asked.GetProperty("explanation").GetString());
        Assert.Equal("bar", asked.GetProperty("chart").GetProperty("type").GetString());
        Assert.Equal([("DE", 1), ("PT", 2)], asked.GetProperty("result").GetProperty("rows").EnumerateArray().Select(r => (r[0].GetString(), r[1].GetInt32())));

        // The model saw only the open tables, the admin's notes, and was asked for JSON.
        var request = app.Model.Requests.Last(r => r.Body["messages"]![0]!["content"]!.GetValue<string>().StartsWith("You answer questions about a PostgreSQL database", StringComparison.Ordinal));
        var system = request.Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("shop.orders (table)", system, StringComparison.Ordinal);
        Assert.Contains("total numeric(10,2) -- In euros", system, StringComparison.Ordinal);
        Assert.Contains("Totals are in euros.", system, StringComparison.Ordinal);
        Assert.DoesNotContain("shop.secrets", system, StringComparison.Ordinal);
        Assert.Equal("data", request.Body["metadata"]!["tags"]![0]!.GetValue<string>());

        // A query that fails goes back to the model with why; its second try is run.
        var fixedUp = await ana.JsonAsync(await ana.PostAsync("/api/data/ask", new { sourceId = source, question = "Total spent? [sql:select sum(amount) from shop.orders][fix:select sum(total) as \"Spent\" from shop.orders]" }));
        Assert.Equal(500m, fixedUp.GetProperty("result").GetProperty("rows")[0][0].GetDecimal());
        var retry = app.Model.Requests.Last(r => r.Body["messages"]!.AsArray().Count == 4 && r.Body["messages"]![3]!["content"]!.ToString().StartsWith("That query failed", StringComparison.Ordinal));
        Assert.Contains("amount", retry.Body["messages"]![3]!["content"]!.GetValue<string>(), StringComparison.Ordinal);
        // Twice wrong: the answer says why, with its SQL to fix by hand.
        var wrong = await ana.JsonAsync(await ana.PostAsync("/api/data/ask", new { sourceId = source, question = "Total? [sql:select sum(amount) from shop.orders]" }));
        Assert.Equal(JsonValueKind.Null, wrong.GetProperty("result").ValueKind);
        Assert.Contains("amount", wrong.GetProperty("error").GetString(), StringComparison.Ordinal);
        // What the tables cannot answer.
        var cannot = await ana.JsonAsync(await ana.PostAsync("/api/data/ask", new { sourceId = source, question = "Will it rain? [cannot]" }));
        Assert.Equal(("", "The tables say nothing about the weather."), (cannot.GetProperty("sql").GetString(), cannot.GetProperty("explanation").GetString()));
        // The person's question went through their safeguards.
        await SafeguardTestRules.ChangeAsync(f, SafeguardTestRules.BlockedWords, r => (r.Enabled, r.Patterns) = (true, "project falcon"));
        Assert.False((await ana.PostAsync("/api/data/ask", new { sourceId = source, question = "Orders of project falcon?" })).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_board_keeps_pinned_charts_refreshed_as_its_owner_and_is_shared_like_an_app()
    {
        await using var f = NewApp();
        var admin = await new TestBrowser(f).SignedInAsync("admin", AppFixture.AdminPassword);
        var (ana, anaId) = await PersonAsync(admin, f, "Ana");
        var (ben, _) = await PersonAsync(admin, f, "Ben");
        var (reader, _, owner) = await ShopAsync();
        var team = (await admin.JsonAsync(await admin.PostAsync("/api/admin/groups", new { name = "Sales" }))).GetProperty("id").GetGuid();
        await admin.PostAsync($"/api/admin/groups/{team}/members", new { userIds = new[] { anaId } });
        var source = (await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources", new
        {
            name = "Shop", connection = reader, tables = new[] { "shop.orders" }, reach = "groups", groupIds = new[] { team },
        }))).GetProperty("id").GetGuid();

        var board = (await ana.JsonAsync(await ana.PostAsync("/api/data/boards", new { name = "Sales week" }))).GetProperty("id").GetGuid();
        await StatusAssert.Is(HttpStatusCode.BadRequest, await ana.PostAsync($"/api/data/boards/{board}/pins", new { sourceId = source, sql = "select * from shop.secrets" }));
        var pin = await ana.JsonAsync(await ana.PostAsync($"/api/data/boards/{board}/pins", new
        {
            sourceId = source, question = "Orders so far", sql = "select count(*) as \"Orders\" from shop.orders", chart = new { type = "number" }, refresh = "hourly",
        }));
        Assert.Equal(("Orders so far", 3), (pin.GetProperty("title").GetString(), pin.GetProperty("result").GetProperty("rows")[0][0].GetInt32()));
        var pinId = pin.GetProperty("id").GetGuid();

        // Private, then everyone's: Ben sees it as it was last run, and cannot change it.
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/data/boards/{board}"));
        await ana.Http.PutAsJsonAsync(new Uri($"/api/data/boards/{board}", UriKind.Relative), new { reach = "company" });
        var shown = await ben.JsonAsync(await ben.GetAsync($"/api/data/boards/{board}"));
        Assert.Equal(("Ana", false, "Shop"), (shown.GetProperty("board").GetProperty("owner").GetString(), shown.GetProperty("board").GetProperty("mine").GetBoolean(),
            shown.GetProperty("pins")[0].GetProperty("source").GetString()));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.PostAsync($"/api/data/pins/{pinId}/refresh"));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.Http.DeleteAsync(new Uri($"/api/data/pins/{pinId}", UriKind.Relative)));

        // An hour on, the refresher runs it again as Ana: a new order shows.
        await using (var conn = new NpgsqlConnection(owner))
        {
            await conn.OpenAsync();
            await using var add = new NpgsqlCommand("insert into shop.orders values (4, 2, 20, '2026-10-04')", conn);
            await add.ExecuteNonQueryAsync();
        }
        var refresher = f.Services.GetRequiredService<Llm.Api.Insights.BoardRefresher>();
        Assert.Equal(0, await refresher.RunDueAsync(CancellationToken.None));
        await using (var scope = f.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Llm.Core.Data.AppDbContext>();
            var saved = await db.DataPins.FindAsync(pinId);
            saved!.RanAt = DateTimeOffset.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await refresher.RunDueAsync(CancellationToken.None));
        var after = await ben.JsonAsync(await ben.GetAsync($"/api/data/boards/{board}"));
        Assert.Equal(4, after.GetProperty("pins")[0].GetProperty("result").GetProperty("rows")[0][0].GetInt32());

        // Out of the group, Ana's board cannot ask the database any more: the chart says so, its last result kept.
        await admin.Http.DeleteAsync(new Uri($"/api/admin/groups/{team}/members/{anaId}", UriKind.Relative));
        var stale = await ana.JsonAsync(await ana.PostAsync($"/api/data/pins/{pinId}/refresh"));
        Assert.Equal("Its board's owner may no longer ask this database.", stale.GetProperty("error").GetString());
        Assert.Equal(4, stale.GetProperty("result").GetProperty("rows")[0][0].GetInt32());
        await StatusAssert.Is(HttpStatusCode.NoContent, await ana.Http.DeleteAsync(new Uri($"/api/data/boards/{board}", UriKind.Relative)));
        await StatusAssert.Is(HttpStatusCode.NotFound, await ben.GetAsync($"/api/data/boards/{board}"));
    }

    [Fact]
    public void The_chat_tool_shows_rows_as_a_table()
    {
        var result = new Llm.Api.Insights.DataResult([new("Country", "text"), new("Orders", "number")], [new() { "PT", 2 }, new() { "D|E", 1 }], false, 12);
        Assert.Equal("2 rows, in 12 ms.\n| Country | Orders |\n| --- | ---: |\n| PT | 2 |\n| D\\|E | 1 |\n", Llm.Api.Chat.Tools.DataTool.Table(result).ReplaceLineEndings("\n"));
    }
}
