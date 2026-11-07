using Argus.Store;
using Argus.Util;

namespace Argus.Tests;

/// <summary>
/// Configuration files are stored with their secret values taken out, their runtime links read as before. Every value
/// here is invented for the test (no real secret has these shapes' contents).
/// </summary>
public sealed class ConfigSecretsTests
{
    // Built at run time, so no secret-shaped literal sits in the repository for a scanner to find.
    static readonly string FakeGitLab = "glpat-" + new string('a', 20);
    static readonly string FakePassword = "pw-" + Guid.NewGuid().ToString("N")[..12];

    [Theory]
    [InlineData(".env", true)]
    [InlineData("deploy/.env.production", true)]
    [InlineData("config/app.env", true)]
    [InlineData("src/main/resources/application.properties", true)]
    [InlineData("src/Api/appsettings.Development.json", true)]
    [InlineData("src/Api/settings.json", false)]
    [InlineData("src/app.cs", false)]
    public void Which_files_are_masked(string path, bool masked) => Assert.Equal(masked, ConfigSecrets.Applies(path));

    [Fact]
    public void A_dotenv_keeps_its_keys_hosts_and_placeholders_and_loses_its_secrets()
    {
        var env = $"""
            DATABASE_HOST=db.corp.example
            DB_PASSWORD={FakePassword}
            export API_KEY={FakePassword}x
            OAUTH_TOKEN_URL=https://auth.corp.example/token
            PASSWORD_MIN_LENGTH=12
            SMTP_PASSWORD=${"{"}SMTP_PASSWORD{"}"}
            REDIS_PASSWORD=
            CI_TOKEN={FakeGitLab}
            KAFKA_TOPIC=orders.created # the topic
            """;
        var stored = ConfigSecrets.Mask(".env", env);
        Assert.DoesNotContain(FakePassword, stored);
        Assert.DoesNotContain(FakeGitLab, stored);
        Assert.Contains("DB_PASSWORD=[removed: secret]", stored);
        Assert.Contains("export API_KEY=[removed: secret]", stored);
        Assert.Contains("DATABASE_HOST=db.corp.example", stored);
        Assert.Contains("OAUTH_TOKEN_URL=https://auth.corp.example/token", stored);
        Assert.Contains("PASSWORD_MIN_LENGTH=12", stored);
        Assert.Contains("SMTP_PASSWORD=${SMTP_PASSWORD}", stored);
        Assert.Contains("REDIS_PASSWORD=\n", stored + "\n");
        Assert.Contains("KAFKA_TOPIC=orders.created # the topic", stored);
    }

    [Fact]
    public void Properties_and_appsettings_lose_their_secrets_and_a_connection_strings_password()
    {
        var properties = $"spring.datasource.url=jdbc:postgresql://db:5432/orders\nspring.datasource.password={FakePassword}\n";
        var stored = ConfigSecrets.Mask("application.properties", properties);
        Assert.Contains("spring.datasource.url=jdbc:postgresql://db:5432/orders", stored);
        Assert.DoesNotContain(FakePassword, stored);

        var json = $$"""
            { "ConnectionStrings": { "Orders": "Host=db;Database=orders;Username=app;Password={{FakePassword}}" },
              "Jwt": { "Issuer": "https://auth.corp.example", "SigningKey": "{{FakePassword}}" },
              "Kafka": { "Topic": "orders.created" } }
            """;
        var appsettings = ConfigSecrets.Mask("appsettings.json", json);
        Assert.DoesNotContain(FakePassword, appsettings);
        Assert.Contains("\"Issuer\": \"https://auth.corp.example\"", appsettings);
        Assert.Contains("Host=db;Database=orders", appsettings);
        Assert.Contains("\"Topic\": \"orders.created\"", appsettings);
    }

    [Fact]
    public void Code_is_never_changed()
    {
        var code = $"var password = \"{FakePassword}\";";
        Assert.Equal(code, ConfigSecrets.Mask("src/Login.cs", code));
    }

    [Fact]
    public void Files_stored_before_are_masked_once_with_their_search_text()
    {
        using var ix = new TestIndex();
        var repo = ix.Repo(11, "grp/orders");
        var env = $"DB_HOST=db.corp.example\nDB_PASSWORD={FakePassword}\n";
        // As an older release stored it: whole.
        ix.File(repo, "deploy/.env", env, "dotenv");
        ix.File(repo, "src/app.c", $"/* {FakePassword} */", "c");
        Sql.Exec(ix.Conn, "INSERT OR IGNORE INTO pending_tasks (name) VALUES ('mask-config-secrets')");
        Db.RunPending(ix.Conn);

        Assert.DoesNotContain(FakePassword, Sql.Scalar(ix.Conn, "SELECT content FROM files WHERE path = 'deploy/.env'")!.ToString());
        Assert.Equal(0L, Convert.ToInt64(Sql.Scalar(ix.Conn,
            "SELECT COUNT(*) FROM files_fts f JOIN files x ON x.id = f.rowid WHERE files_fts MATCH ? AND x.path = 'deploy/.env'", $"\"{FakePassword}\"")));
        Assert.Equal(1L, Convert.ToInt64(Sql.Scalar(ix.Conn, "SELECT COUNT(*) FROM files_fts WHERE files_fts MATCH 'db'")));
        // Code is left as it is, and the task runs once.
        Assert.Contains(FakePassword, Sql.Scalar(ix.Conn, "SELECT content FROM files WHERE path = 'src/app.c'")!.ToString());
        Assert.Equal(0L, Convert.ToInt64(Sql.Scalar(ix.Conn, "SELECT COUNT(*) FROM pending_tasks")));
    }
}
