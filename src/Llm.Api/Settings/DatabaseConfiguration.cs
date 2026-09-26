using Npgsql;

namespace Llm.Api.Settings;

/// <summary>
/// The app's own settings as saved in the Settings page: rows "config:&lt;key&gt;" of the
/// settings table, as configuration. Added last, so a saved value wins over the
/// environment. <see cref="Reload"/> after a save makes IOptionsMonitor consumers
/// see it at once.
/// </summary>
public sealed class DatabaseConfigurationSource(string connectionString, string? dataKey) : IConfigurationSource
{
    public DatabaseConfigurationProvider Provider { get; } = new(connectionString, dataKey);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public sealed class DatabaseConfigurationProvider(string connectionString, string? dataKey) : ConfigurationProvider
{
    public const string RowPrefix = "config:";

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var conn = new NpgsqlConnection(connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT key, value FROM settings WHERE key LIKE 'config:%'", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0)[RowPrefix.Length..];
                // A value that no longer decrypts (APP_DATA_KEY changed) is left out:
                // the environment's value applies, and the Settings page says so.
                if (SettingsCrypto.Decrypt(reader.GetString(1), dataKey) is { } value)
                {
                    data[key] = value;
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidCatalogName)
        {
            // Before the first migration there is no database or table: nothing is saved yet.
        }
        Data = data;
    }

    /// <summary>Reads the table again and tells every IOptionsMonitor.</summary>
    public void Reload()
    {
        Load();
        OnReload();
    }
}
