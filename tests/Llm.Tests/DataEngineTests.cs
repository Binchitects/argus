using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using MySqlConnector;

namespace Llm.Tests;

/// <summary>
/// A MariaDB server with a shop: customers, orders, a secrets table, a view of the big orders; and its accounts: one that
/// reads the shop's open tables, one that may also write, one that may read the server's files.
/// </summary>
public sealed class MariaDbServer : IAsyncLifetime
{
    // Test-only passwords of a throwaway container.
    private const string RootPassword = "mariadb-root-test";

    private readonly IContainer _db = new ContainerBuilder("mariadb:11")
        .WithEnvironment("MARIADB_ROOT_PASSWORD", RootPassword)
        .WithPortBinding(3306, true)
        // The image first runs a server without networking to set itself up: the real one listens on 3306.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("port: 3306"))
        .Build();

    public string Connection(string user, string password, string database = "shop") =>
        $"Server={_db.Hostname};Port={_db.GetMappedPublicPort(3306)};Database={database};User ID={user};Password={password}";

    public string Root => Connection("root", RootPassword);

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        await RunAsync(Connection("root", RootPassword, "mysql"), """
            create database shop;
            create table shop.customers (id int primary key, name varchar(50) not null, country char(2) not null) comment 'People who bought';
            create table shop.orders (id int primary key, customer_id int, total decimal(10, 2) not null comment 'In euros', placed_at datetime not null);
            create table shop.secrets (id int, value text);
            create view shop.big_orders as select id, total from shop.orders where total > 100;
            insert into shop.customers values (1, 'Ana', 'PT'), (2, 'Ben', 'DE');
            insert into shop.orders values (1, 1, 50, '2026-10-01'), (2, 1, 150, '2026-10-02'), (3, 2, 300, '2026-10-03');
            insert into shop.secrets values (1, 'the vault code');
            create user 'reader'@'%' identified by 'reader-test';
            grant select on shop.customers to 'reader'@'%';
            grant select on shop.orders to 'reader'@'%';
            grant select on shop.big_orders to 'reader'@'%';
            create user 'writer'@'%' identified by 'writer-test';
            grant select, insert on shop.orders to 'writer'@'%';
            create user 'filer'@'%' identified by 'filer-test';
            grant file on *.* to 'filer'@'%';
            grant select on shop.orders to 'filer'@'%';
            """);
    }

    public static async Task RunAsync(string connection, string sql)
    {
        await using var conn = new MySqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();
}

/// <summary>
/// A SQL Server with the same shop (dbo), a function of its own, and its logins: one that reads (the secrets too, which
/// are not opened), one that may also write, one that owns the database.
/// </summary>
public sealed class SqlServerServer : IAsyncLifetime
{
    // Test-only passwords of a throwaway container (SQL Server wants three kinds of character).
    private const string SaPassword = "Sa-test-pass-1";

    private readonly IContainer _db = new ContainerBuilder("mcr.microsoft.com/mssql/server:2022-latest")
        .WithEnvironment("ACCEPT_EULA", "Y")
        .WithEnvironment("MSSQL_SA_PASSWORD", SaPassword)
        .WithEnvironment("MSSQL_PID", "Developer")
        .WithPortBinding(1433, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("SQL Server is now ready for client connections"))
        .Build();

    public string Connection(string user, string password, string database = "shop") =>
        $"Server={_db.Hostname},{_db.GetMappedPublicPort(1433)};Database={database};User ID={user};Password={password};Encrypt=True;TrustServerCertificate=True";

    public string Sa => Connection("sa", SaPassword);

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        // Ready for connections is logged before the system databases are recovered: the first sign-in that works.
        for (var i = 0; ; i++)
        {
            try
            {
                await RunAsync(Connection("sa", SaPassword, "master"), "create database shop");
                break;
            }
            catch (SqlException) when (i < 60)
            {
                await Task.Delay(1000);
            }
        }
        foreach (var batch in new[]
        {
            "create table dbo.customers (id int primary key, name nvarchar(50) not null, country char(2) not null)",
            "exec sp_addextendedproperty 'MS_Description', 'People who bought', 'SCHEMA', 'dbo', 'TABLE', 'customers'",
            "create table dbo.orders (id int primary key, customer_id int, total decimal(10, 2) not null, placed_at datetime2 not null)",
            "exec sp_addextendedproperty 'MS_Description', 'In euros', 'SCHEMA', 'dbo', 'TABLE', 'orders', 'COLUMN', 'total'",
            "create table dbo.secrets (id int, value nvarchar(100))",
            "create view dbo.big_orders as select id, total from dbo.orders where total > 100",
            // One whose plan cannot show what it does, and one SQL Server inlines (its plan names the table it reads).
            "create function dbo.twice(@x int) returns int with inline = off as begin return @x * 2 end",
            "create function dbo.secret_of(@id int) returns nvarchar(100) as begin return (select value from dbo.secrets where id = @id) end",
            """
            insert into dbo.customers values (1, 'Ana', 'PT'), (2, 'Ben', 'DE');
            insert into dbo.orders values (1, 1, 50, '2026-10-01'), (2, 1, 150, '2026-10-02'), (3, 2, 300, '2026-10-03');
            insert into dbo.secrets values (1, 'the vault code');
            create login reader with password = 'Reader-test-1', check_policy = off; create user reader for login reader;
            grant select on dbo.customers to reader; grant select on dbo.orders to reader; grant select on dbo.secrets to reader;
            grant select on dbo.big_orders to reader; grant view definition on dbo.big_orders to reader; grant execute on dbo.twice to reader; grant execute on dbo.secret_of to reader;
            create login planless with password = 'Planless-test-1', check_policy = off; create user planless for login planless;
            grant select on dbo.orders to planless;
            grant showplan to reader;
            create login writer with password = 'Writer-test-1', check_policy = off; create user writer for login writer;
            grant select, insert on dbo.orders to writer; grant showplan to writer;
            create login owner with password = 'Owner-test-1', check_policy = off; create user owner for login owner;
            alter role db_owner add member owner;
            """,
        })
        {
            await RunAsync(Sa, batch);
        }
    }

    public static async Task RunAsync(string connection, string sql)
    {
        await using var conn = new SqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> CountAsync(string table)
    {
        await using var conn = new SqlConnection(Sa);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"select count(*) from {table}", conn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();
}

/// <summary>Ask your data on MySQL or MariaDB: the account is the guard, so it may only read, and only what is open.</summary>
[Collection(nameof(AppCollection))]
public sealed class MySqlDataTests(AppFixture app, MariaDbServer db) : IClassFixture<MariaDbServer>
{
    [Fact]
    public async Task Only_an_account_that_only_reads_is_taken_and_every_table_it_reads_is_open()
    {
        await using var f = DataEngineTests.NewApp(app);
        var admin = await KnowledgeTests.AdminAsync(f);
        var (ana, _) = await KnowledgeTests.PersonAsync(f, admin, "mysqlana" + Guid.NewGuid().ToString("N")[..4]);
        var reader = db.Connection("reader", "reader-test");

        // An account that may read the server's files, or write, is refused; the reader's tables are listed, all open.
        var filer = await admin.PostAsync("/api/admin/data-sources/probe", new { kind = "mysql", connection = db.Connection("filer", "filer-test") });
        await StatusAssert.Is(HttpStatusCode.BadRequest, filer);
        Assert.Contains("FILE on *.*", await filer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var writer = await admin.PostAsync("/api/admin/data-sources/probe", new { kind = "mysql", connection = db.Connection("writer", "writer-test") });
        Assert.Contains("INSERT on shop.orders", await writer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var probed = await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources/probe", new { kind = "mysql", connection = reader }));
        Assert.True(probed.GetProperty("grantsOnly").GetBoolean());
        var tables = probed.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t);
        Assert.Equal(["shop.big_orders", "shop.customers", "shop.orders"], tables.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("view", "People who bought"), (tables["shop.big_orders"].GetProperty("kind").GetString(), tables["shop.customers"].GetProperty("comment").GetString()));
        Assert.Contains(tables["shop.orders"].GetProperty("columns").EnumerateArray(), c => c.GetProperty("name").GetString() == "total" && c.GetProperty("comment").GetString() == "In euros");

        // Opening fewer than the account reads is refused; without tables named, all it reads are open.
        var fewer = await admin.PostAsync("/api/admin/data-sources", new { name = "Shop", kind = "mysql", connection = reader, tables = new[] { "shop.orders" } });
        Assert.Contains("every table its account may read is open", await fewer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var made = await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources", new { name = "Shop", kind = "mysql", connection = reader, reach = "company" }));
        Assert.Equal("mysql", made.GetProperty("kind").GetString());
        Assert.StartsWith("reader@", made.GetProperty("where").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, made.GetProperty("tables").GetArrayLength());
        var source = made.GetProperty("id").GetGuid();

        async Task<HttpResponseMessage> Run(string sql) => await ana.PostAsync("/api/data/run", new { sourceId = source, sql });
        var counted = await ana.JsonAsync(await Run("select count(*) as `Orders` from shop.orders"));
        Assert.Equal(("Orders", "number", 3), (counted.GetProperty("columns")[0].GetProperty("name").GetString(), counted.GetProperty("columns")[0].GetProperty("type").GetString(),
            counted.GetProperty("rows")[0][0].GetInt32()));
        var big = await ana.JsonAsync(await Run("select id, total from shop.big_orders order by id"));
        Assert.Equal([150m, 300m], big.GetProperty("rows").EnumerateArray().Select(r => r[1].GetDecimal()));
        var month = await ana.JsonAsync(await Run("select date_format(placed_at, '%Y-%m-01') as `Month`, sum(total) as `Spent` from shop.orders group by 1"));
        Assert.Equal("2026-10-01", month.GetProperty("rows")[0][0].GetString());
        Assert.Contains("denied", await (await Run("select * from shop.secrets")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("One query at a time", await (await Run("select 1; delete from shop.orders")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // --x is not a comment in MySQL: what follows it is read as the server reads it.
        Assert.Contains("One query at a time", await (await Run("select 1 --x\n; delete from shop.orders")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("/*!", await (await Run("select 1 /*! ; delete from shop.orders */")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // A backslash escapes in MySQL's strings: this is one string, read as the server reads it.
        var quoted = await ana.JsonAsync(await Run("select 'a\\'; delete from shop.orders; -- ' as `Text`"));
        Assert.Equal("a'; delete from shop.orders; -- ", quoted.GetProperty("rows")[0][0].GetString());

        // The account given more than is open: refused until its tables are read again, which opens them.
        await MariaDbServer.RunAsync(db.Root, "grant select on shop.secrets to 'reader'@'%'");
        try
        {
            Assert.Contains("not open to questions (shop.secrets)", await (await Run("select count(*) from shop.orders")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
            var again = await admin.JsonAsync(await admin.PostAsync($"/api/admin/data-sources/{source}/schema", new { }));
            Assert.Contains(again.GetProperty("warnings").EnumerateArray(), w => w.GetString()!.Contains("Now open to questions too", StringComparison.Ordinal));
            await StatusAssert.Is(HttpStatusCode.OK, await Run("select count(*) from shop.secrets"));
        }
        finally
        {
            await MariaDbServer.RunAsync(db.Root, "revoke select on shop.secrets from 'reader'@'%'");
        }

        // The model is told the database's own SQL.
        var asked = await ana.JsonAsync(await ana.PostAsync("/api/data/ask", new { sourceId = source, question = "How many orders? [sql:select count(*) as `Orders` from shop.orders][chart:number]" }));
        Assert.Equal(3, asked.GetProperty("result").GetProperty("rows")[0][0].GetInt32());
        var system = app.Model.Requests.Last(r => r.Body["messages"]![0]!["content"]!.GetValue<string>().StartsWith("You answer questions about a MySQL or MariaDB database", StringComparison.Ordinal))
            .Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("backticks", system, StringComparison.Ordinal);

        // A limited time.
        await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/data-sources/{source}", UriKind.Relative), new { seconds = 1, mostRows = 2 });
        var two = await ana.JsonAsync(await Run("select * from shop.orders order by id"));
        Assert.Equal((2, true), (two.GetProperty("rows").GetArrayLength(), two.GetProperty("truncated").GetBoolean()));
        Assert.Contains("longer than 1 seconds", await (await Run("select sleep(3) from shop.orders")).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}

/// <summary>Ask your data on SQL Server: each query's plan read first, run in a transaction rolled back.</summary>
[Collection(nameof(AppCollection))]
public sealed class SqlServerDataTests(AppFixture app, SqlServerServer db) : IClassFixture<SqlServerServer>
{
    [Fact]
    public async Task A_query_runs_only_when_its_plan_reads_only_what_is_open()
    {
        await using var f = DataEngineTests.NewApp(app);
        var admin = await KnowledgeTests.AdminAsync(f);
        var (ana, _) = await KnowledgeTests.PersonAsync(f, admin, "mssqlana" + Guid.NewGuid().ToString("N")[..4]);
        var reader = db.Connection("reader", "Reader-test-1");

        // sysadmin and the database's owner are refused; a login that may write is warned of; the reader's tables listed.
        Assert.Contains("sysadmin", await (await admin.PostAsync("/api/admin/data-sources/probe", new { kind = "sqlserver", connection = db.Sa })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("grant showplan to [planless]", await (await admin.PostAsync("/api/admin/data-sources/probe", new { kind = "sqlserver", connection = db.Connection("planless", "Planless-test-1") })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("owns the database", await (await admin.PostAsync("/api/admin/data-sources/probe", new { kind = "sqlserver", connection = db.Connection("owner", "Owner-test-1") })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var warnedRes = await admin.PostAsync("/api/admin/data-sources/probe", new { kind = "sqlserver", connection = db.Connection("writer", "Writer-test-1") });
        Assert.True(warnedRes.IsSuccessStatusCode, await warnedRes.Content.ReadAsStringAsync());
        var warned = await admin.JsonAsync(warnedRes);
        Assert.Contains("may change 1 table", warned.GetProperty("warnings")[0].GetString(), StringComparison.Ordinal);
        var probed = await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources/probe", new { kind = "sqlserver", connection = reader }));
        var tables = probed.GetProperty("tables").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t);
        Assert.Equal(["dbo.big_orders", "dbo.customers", "dbo.orders", "dbo.secrets"], tables.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("view", "People who bought"), (tables["dbo.big_orders"].GetProperty("kind").GetString(), tables["dbo.customers"].GetProperty("comment").GetString()));
        Assert.Contains(tables["dbo.orders"].GetProperty("columns").EnumerateArray(), c => c.GetProperty("type").GetString() == "decimal(10, 2)" && c.GetProperty("comment").GetString() == "In euros");

        var source = (await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources", new
        {
            name = "Shop", kind = "sqlserver", connection = reader, tables = new[] { "dbo.customers", "dbo.orders", "dbo.big_orders" }, reach = "company",
        }))).GetProperty("id").GetGuid();
        async Task<string> Run(Guid id, string sql) => await (await ana.PostAsync("/api/data/run", new { sourceId = id, sql })).Content.ReadAsStringAsync();
        var countedText = await Run(source, "select count(*) as [Orders] from dbo.orders");
        Assert.True(countedText.Contains("\"columns\"", StringComparison.Ordinal), countedText);
        var counted = JsonDocument.Parse(countedText).RootElement;
        Assert.Equal(("Orders", "number", 3), (counted.GetProperty("columns")[0].GetProperty("name").GetString(), counted.GetProperty("columns")[0].GetProperty("type").GetString(),
            counted.GetProperty("rows")[0][0].GetInt32()));
        // A view open reads its tables (its plan names them, not the view).
        var big = JsonDocument.Parse(await Run(source, "select id, total from dbo.big_orders order by id")).RootElement;
        Assert.Equal([150m, 300m], big.GetProperty("rows").EnumerateArray().Select(r => r[1].GetDecimal()));
        var joined = JsonDocument.Parse(await Run(source, "select top (1) c.country as [Country], sum(o.total) as [Spent] from dbo.orders o join dbo.customers c on c.id = o.customer_id group by c.country order by 2 desc")).RootElement;
        Assert.Equal("DE", joined.GetProperty("rows")[0][0].GetString());

        // What the login may read but is not open, a function of the database's own, another database, two statements.
        Assert.Contains("not open to questions: dbo.secrets", await Run(source, "select o.id, s.value from dbo.orders o join dbo.secrets s on s.id = o.id"), StringComparison.Ordinal);
        Assert.Contains("[twice]()", await Run(source, "select dbo.twice(id) from dbo.orders"), StringComparison.Ordinal);
        Assert.Contains("not open to questions: dbo.secrets", await Run(source, "select dbo.secret_of(id) from dbo.orders"), StringComparison.Ordinal);
        Assert.Contains("not open to questions", await Run(source, "select name from sys.tables"), StringComparison.Ordinal);
        Assert.Contains("more than one statement", await Run(source, "select 1 as a select id from dbo.orders"), StringComparison.Ordinal);
        Assert.Contains("Only a query that reads", await Run(source, "delete from dbo.orders"), StringComparison.Ordinal);

        // A login that may write still only reads here: a write after WITH is read as what it is, and nothing changes.
        var writes = (await admin.JsonAsync(await admin.PostAsync("/api/admin/data-sources", new
        {
            name = "Shop (writer)", kind = "sqlserver", connection = db.Connection("writer", "Writer-test-1"), tables = new[] { "dbo.orders" }, reach = "company",
        }))).GetProperty("id").GetGuid();
        Assert.Contains("reads it as INSERT", await Run(writes, "with x as (select 9 as id) insert into dbo.orders select id, 1, 1, sysdatetime() from x"), StringComparison.Ordinal);
        Assert.Equal(3, await db.CountAsync("dbo.orders"));

        // The model is told the database's own SQL; a limited number of rows, for a limited time.
        var asked = JsonDocument.Parse(await (await ana.PostAsync("/api/data/ask", new { sourceId = source, question = "How many orders? [sql:select count(*) as Orders from dbo.orders][chart:number]" })).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(3, asked.GetProperty("result").GetProperty("rows")[0][0].GetInt32());
        var system = app.Model.Requests.Last(r => r.Body["messages"]![0]!["content"]!.GetValue<string>().StartsWith("You answer questions about a SQL Server database", StringComparison.Ordinal))
            .Body["messages"]![0]!["content"]!.GetValue<string>();
        Assert.Contains("TOP (n)", system, StringComparison.Ordinal);
        await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/data-sources/{source}", UriKind.Relative), new { seconds = 1, mostRows = 2 });
        var two = JsonDocument.Parse(await Run(source, "select * from dbo.orders order by id")).RootElement;
        Assert.Equal((2, true), (two.GetProperty("rows").GetArrayLength(), two.GetProperty("truncated").GetBoolean()));
        Assert.Contains("longer than 1 seconds", await Run(source, "with n as (select 1 as i union all select i + 1 from n where i < 1000000000) select count_big(*) as c from n option (maxrecursion 0)"), StringComparison.Ordinal);

        // A source stays its kind.
        var changed = await admin.Http.PatchAsJsonAsync(new Uri($"/api/admin/data-sources/{source}", UriKind.Relative), new { kind = "postgres" });
        await StatusAssert.Is(HttpStatusCode.BadRequest, changed);
    }
}

public static class DataEngineTests
{
    public static WebApplicationFactory<Program> NewApp(AppFixture app) =>
        app.Create(app.ConnectionStringFor("dbengine_" + Guid.NewGuid().ToString("N")[..8]), new FakeGateway(), new Dictionary<string, string?>());
}
