using Npgsql;

namespace Llm.Api;

public static class DatabaseSettings
{
    /// <summary>
    /// ConnectionStrings:App when given (tests, local runs), otherwise built from
    /// Database:Host/Port/Name/Username/Password. Compose passes the parts rather
    /// than one string, so a password containing ';' cannot smuggle in options.
    /// </summary>
    public static string ConnectionString(IConfiguration config)
    {
        if (config.GetConnectionString("App") is { Length: > 0 } cs)
        {
            return new NpgsqlConnectionStringBuilder(cs) { GssEncryptionMode = GssEncryptionMode.Disable }.ConnectionString;
        }
        var db = config.GetSection("Database");
        return new NpgsqlConnectionStringBuilder
        {
            Host = db["Host"] ?? throw new InvalidOperationException("Set ConnectionStrings:App or Database:Host."),
            Port = db.GetValue("Port", 5432),
            Database = db["Name"] ?? "llmapp",
            Username = db["Username"] ?? throw new InvalidOperationException("Database:Username is not set."),
            Password = db["Password"] ?? throw new InvalidOperationException("Database:Password is not set."),
            // Postgres here is password-authenticated on a private network. Without
            // this the driver probes for Kerberos first, and the runtime image has
            // no libgssapi -- an alarming but harmless error on every start.
            GssEncryptionMode = GssEncryptionMode.Disable,
        }.ConnectionString;
    }
}
