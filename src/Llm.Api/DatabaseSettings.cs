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
            // An external server (a managed Postgres) usually wants TLS: Database:SslMode.
            SslMode = SslModeOf(db["SslMode"]),
            RootCertificate = db["RootCertificate"] is { Length: > 0 } ca ? ca : null,
        }.ConnectionString;
    }

    /// <summary>
    /// disable, prefer (the default), require, verify-ca or verify-full, as libpq and the gateway
    /// spell them (or Npgsql's own names): require encrypts without checking the certificate,
    /// verify-full checks it and the host name against Database:RootCertificate (or the system's roots).
    /// </summary>
    public static SslMode SslModeOf(string? value) =>
        string.IsNullOrWhiteSpace(value) ? SslMode.Prefer
        : Enum.TryParse<SslMode>(value.Replace("-", "", StringComparison.Ordinal).Trim(), ignoreCase: true, out var mode) ? mode
        : throw new InvalidOperationException($"Database:SslMode \"{value}\" is not one of disable, prefer, require, verify-ca, verify-full.");
}
