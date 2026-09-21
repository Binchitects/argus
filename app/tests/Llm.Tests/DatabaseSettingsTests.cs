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
}
