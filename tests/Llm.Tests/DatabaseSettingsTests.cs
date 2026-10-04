using Llm.Api;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Llm.Tests;

public sealed class DatabaseSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Key, (string?)v.Value))).Build();

    [Fact]
    public void A_full_connection_string_wins()
    {
        var cs = DatabaseSettings.ConnectionString(Config(("ConnectionStrings:App", "Host=a;Database=b"), ("Database:Host", "ignored")));
        var b = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal(("a", "b"), (b.Host, b.Database));
    }

    [Fact]
    public void Parts_are_assembled_with_defaults()
    {
        var cs = new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(Config(
            ("Database:Host", "postgres"), ("Database:Username", "u"), ("Database:Password", "p"))));
        Assert.Equal(("postgres", 5432, "llmapp", "u", "p"), (cs.Host, cs.Port, cs.Database, cs.Username, cs.Password));
        Assert.Equal(GssEncryptionMode.Disable, cs.GssEncryptionMode);
    }

    [Fact]
    public void A_password_cannot_inject_connection_options()
    {
        var cs = new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(Config(
            ("Database:Host", "postgres"), ("Database:Username", "u"), ("Database:Password", "x;Database=postgres;Host=evil"))));
        Assert.Equal(("postgres", "llmapp", "x;Database=postgres;Host=evil"), (cs.Host, cs.Database, cs.Password));
    }

    [Fact]
    public void Missing_host_is_a_clear_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseSettings.ConnectionString(Config()));
        Assert.Contains("Database:Host", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_external_server_takes_tls_as_libpq_and_the_gateway_spell_it()
    {
        SslMode Mode(string? value, string? ca = null)
        {
            var parts = new List<(string, string)> { ("Database:Host", "db.example.test"), ("Database:Port", "6432"), ("Database:Username", "u"), ("Database:Password", "p") };
            if (value is not null)
            {
                parts.Add(("Database:SslMode", value));
            }
            if (ca is not null)
            {
                parts.Add(("Database:RootCertificate", ca));
            }
            var cs = new NpgsqlConnectionStringBuilder(DatabaseSettings.ConnectionString(Config([.. parts])));
            Assert.Equal(6432, cs.Port);
            Assert.Equal(ca, cs.RootCertificate);
            return cs.SslMode;
        }
        Assert.Equal(SslMode.Prefer, Mode(null));
        Assert.Equal(SslMode.Disable, Mode("disable"));
        Assert.Equal(SslMode.Require, Mode("require"));
        Assert.Equal(SslMode.VerifyFull, Mode("verify-full", "/certs/db-ca.pem"));
        Assert.Equal(SslMode.VerifyCA, Mode("VerifyCA"));
        var ex = Assert.Throws<InvalidOperationException>(() => Mode("always"));
        Assert.Contains("verify-full", ex.Message, StringComparison.Ordinal);
    }
}
