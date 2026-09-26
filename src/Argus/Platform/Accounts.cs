using System.Security.Cryptography;
using System.Text;
using Argus.Store;
using Argus.Util;
using Microsoft.Data.Sqlite;

namespace Argus.Platform;

/// <summary>A request the account layer refuses, with a message fit for the person who made it.</summary>
public sealed class AccountError(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// The application database: people, their sessions and keys, and their
/// conversations. Kept apart from the code index on purpose -- the index is
/// rebuilt from GitLab and can be thrown away; this cannot.
/// </summary>
public static class AppDb
{
    static readonly string[] Migrations =
    [
        """
        CREATE TABLE users (
            id              INTEGER PRIMARY KEY,
            username        TEXT NOT NULL UNIQUE COLLATE NOCASE,
            email           TEXT NOT NULL UNIQUE COLLATE NOCASE,
            display_name    TEXT NOT NULL DEFAULT '',
            role            TEXT NOT NULL CHECK (role IN ('admin', 'user')),
            password_hash   TEXT NOT NULL,
            gitlab_username TEXT,
            disabled        INTEGER NOT NULL DEFAULT 0,
            gateway_key     TEXT,
            created_at      REAL NOT NULL,
            last_login_at   REAL
        );
        CREATE TABLE sessions (
            token_hash   TEXT PRIMARY KEY,
            user_id      INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            created_at   REAL NOT NULL,
            expires_at   REAL NOT NULL,
            user_agent   TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX sessions_user ON sessions(user_id);
        CREATE TABLE api_keys (
            id           INTEGER PRIMARY KEY,
            user_id      INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            name         TEXT NOT NULL,
            prefix       TEXT NOT NULL,
            key_hash     TEXT NOT NULL UNIQUE,
            created_at   REAL NOT NULL,
            last_used_at REAL
        );
        CREATE TABLE conversations (
            id         TEXT PRIMARY KEY,
            user_id    INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE,
            title      TEXT NOT NULL,
            model      TEXT,
            created_at REAL NOT NULL,
            updated_at REAL NOT NULL
        );
        CREATE INDEX conversations_user ON conversations(user_id, updated_at DESC);
        CREATE TABLE messages (
            id              INTEGER PRIMARY KEY,
            conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
            role            TEXT NOT NULL CHECK (role IN ('user', 'assistant', 'tool')),
            content         TEXT NOT NULL DEFAULT '',
            reasoning       TEXT,
            tool_calls      TEXT,
            tool_call_id    TEXT,
            name            TEXT,
            is_error        INTEGER NOT NULL DEFAULT 0,
            created_at      REAL NOT NULL
        );
        CREATE INDEX messages_conversation ON messages(conversation_id, id);
        """,
    ];

    /// <summary><c>ARGUS_APP_DB</c>, or <c>app.db</c> beside the index.</summary>
    public static string PathFor(string indexDataDir) =>
        Environment.GetEnvironmentVariable("ARGUS_APP_DB") is { Length: > 0 } p ? p : Path.Combine(indexDataDir, "app.db");

    public static SqliteConnection Open(string path)
    {
        var conn = Db.Connect(path);
        var current = Convert.ToInt32(Sql.Scalar(conn, "PRAGMA user_version"));
        for (int v = current; v < Migrations.Length; v++)
        {
            using var tx = conn.BeginTransaction();
            Sql.Script(conn, Migrations[v]);
            Sql.Script(conn, $"PRAGMA user_version = {v + 1}");
            tx.Commit();
        }
        return conn;
    }

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
}

/// <summary>PBKDF2-HMAC-SHA256, self-describing so the cost can be raised without a migration.</summary>
public static class Passwords
{
    public const int Iterations = 600_000;
    public const int MinLength = 12;
    const string Scheme = "pbkdf2_sha256";

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch (FormatException) { return false; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Spent on a login for an unknown name, so timing does not say which names exist.</summary>
    public static readonly string Decoy = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));

    public static void RequireStrong(string password)
    {
        if (password.Length < MinLength) throw new AccountError($"A password needs at least {MinLength} characters.");
    }

    /// <summary>A password to hand to someone once: 20 characters, no look-alikes.</summary>
    public static string Generate()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, 20).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }
}

public static class Tokens
{
    public static string New(string prefix = "") =>
        prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string HashOf(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed record AppUser(
    long Id, string Username, string Email, string DisplayName, string Role, string? GitlabUsername,
    bool Disabled, double CreatedAt, double? LastLoginAt)
{
    public bool IsAdmin => Role == "admin";

    public System.Text.Json.Nodes.JsonObject ToJson() => new()
    {
        ["id"] = Id, ["username"] = Username, ["email"] = Email, ["display_name"] = DisplayName, ["role"] = Role,
        ["gitlab_username"] = GitlabUsername, ["disabled"] = Disabled, ["created_at"] = CreatedAt, ["last_login_at"] = LastLoginAt,
    };
}

public static class Users
{
    public const string ApiKeyPrefix = "ak_";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);
    static readonly System.Text.RegularExpressions.Regex UsernameShape = new("^[a-z0-9][a-z0-9._-]{1,31}$");
    static readonly System.Text.RegularExpressions.Regex EmailShape = new(@"^[^@\s]+@[^@\s]+$");

    const string Columns = "id, username, email, display_name, role, gitlab_username, disabled, created_at, last_login_at";

    static AppUser Map(Row r) => new(r.Long("id"), r.Str("username"), r.Str("email"), r.Str("display_name"), r.Str("role"),
        r.StrOrNull("gitlab_username"), r.Long("disabled") != 0, r.Double("created_at"),
        r.Get("last_login_at") is null ? null : r.Double("last_login_at"));

    public static List<AppUser> List(SqliteConnection conn) =>
        Sql.Query(conn, $"SELECT {Columns} FROM users ORDER BY username").Select(Map).ToList();

    public static AppUser? Get(SqliteConnection conn, long id) =>
        Sql.One(conn, $"SELECT {Columns} FROM users WHERE id = ?", id) is { } r ? Map(r) : null;

    public static AppUser? Find(SqliteConnection conn, string usernameOrEmail) =>
        Sql.One(conn, $"SELECT {Columns} FROM users WHERE username = ? OR email = ?", usernameOrEmail.Trim(), usernameOrEmail.Trim()) is { } r
            ? Map(r) : null;

    public static long Count(SqliteConnection conn) => Convert.ToInt64(Sql.Scalar(conn, "SELECT COUNT(*) FROM users"));

    static long ActiveAdmins(SqliteConnection conn) =>
        Convert.ToInt64(Sql.Scalar(conn, "SELECT COUNT(*) FROM users WHERE role = 'admin' AND disabled = 0"));

    public static AppUser Create(SqliteConnection conn, string username, string email, string password, string role = "user",
        string displayName = "", string? gitlabUsername = null)
    {
        username = username.Trim().ToLowerInvariant();
        email = email.Trim().ToLowerInvariant();
        if (!UsernameShape.IsMatch(username))
            throw new AccountError("A username is 2-32 characters of lowercase letters, digits, '.', '_' or '-', starting with a letter or digit.");
        if (!EmailShape.IsMatch(email)) throw new AccountError("That is not an email address.");
        if (role is not ("admin" or "user")) throw new AccountError("A role is 'admin' or 'user'.");
        Passwords.RequireStrong(password);
        if (Find(conn, username) is not null || Find(conn, email) is not null)
            throw new AccountError("Someone already has that username or email.", 409);
        Sql.Exec(conn, "INSERT INTO users (username, email, display_name, role, password_hash, gitlab_username, created_at) VALUES (?, ?, ?, ?, ?, ?, ?)",
            username, email, displayName.Trim(), role, Passwords.Hash(password), Blank(gitlabUsername), AppDb.Now());
        return Find(conn, username)!;
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static AppUser Update(SqliteConnection conn, long id, string? displayName = null, string? role = null, bool? disabled = null,
        string? gitlabUsername = null, bool clearGitlab = false)
    {
        var user = Get(conn, id) ?? throw new AccountError("No such person.", 404);
        if (role is not null && role is not ("admin" or "user")) throw new AccountError("A role is 'admin' or 'user'.");
        var losesAdmin = user.IsAdmin && !user.Disabled && ((role is not null && role != "admin") || disabled == true);
        if (losesAdmin && ActiveAdmins(conn) <= 1) throw new AccountError("That would leave nobody able to administer the service.", 409);
        using var tx = conn.BeginTransaction();
        if (displayName is not null) Sql.Exec(conn, "UPDATE users SET display_name = ? WHERE id = ?", displayName.Trim(), id);
        if (role is not null) Sql.Exec(conn, "UPDATE users SET role = ? WHERE id = ?", role, id);
        if (disabled is not null)
        {
            Sql.Exec(conn, "UPDATE users SET disabled = ? WHERE id = ?", disabled.Value ? 1 : 0, id);
            if (disabled.Value) Sql.Exec(conn, "DELETE FROM sessions WHERE user_id = ?", id);
        }
        if (gitlabUsername is not null || clearGitlab) Sql.Exec(conn, "UPDATE users SET gitlab_username = ? WHERE id = ?", Blank(gitlabUsername), id);
        tx.Commit();
        return Get(conn, id)!;
    }

    public static void Delete(SqliteConnection conn, long id)
    {
        var user = Get(conn, id) ?? throw new AccountError("No such person.", 404);
        if (user.IsAdmin && !user.Disabled && ActiveAdmins(conn) <= 1)
            throw new AccountError("That would leave nobody able to administer the service.", 409);
        Sql.Exec(conn, "DELETE FROM users WHERE id = ?", id);
    }

    public static void SetPassword(SqliteConnection conn, long id, string password)
    {
        Passwords.RequireStrong(password);
        using var tx = conn.BeginTransaction();
        Sql.Exec(conn, "UPDATE users SET password_hash = ? WHERE id = ?", Passwords.Hash(password), id);
        // Every other session ends: a password change is how someone locks out whoever else has theirs.
        Sql.Exec(conn, "DELETE FROM sessions WHERE user_id = ?", id);
        tx.Commit();
    }

    /// <summary>The person, when the password is theirs and the account is enabled; null otherwise, in constant-ish time.</summary>
    public static AppUser? CheckPassword(SqliteConnection conn, string usernameOrEmail, string password)
    {
        var row = Sql.One(conn, "SELECT id, password_hash, disabled FROM users WHERE username = ? OR email = ?",
            usernameOrEmail.Trim(), usernameOrEmail.Trim());
        var ok = Passwords.Verify(password, row?.Str("password_hash") ?? Passwords.Decoy);
        if (row is null || !ok || row.Long("disabled") != 0) return null;
        return Get(conn, row.Long("id"));
    }

    // --- sessions ---------------------------------------------------------------------

    public static string StartSession(SqliteConnection conn, long userId, string userAgent)
    {
        var token = Tokens.New();
        var now = AppDb.Now();
        using var tx = conn.BeginTransaction();
        Sql.Exec(conn, "DELETE FROM sessions WHERE expires_at < ?", now);
        Sql.Exec(conn, "INSERT INTO sessions (token_hash, user_id, created_at, expires_at, user_agent) VALUES (?, ?, ?, ?, ?)",
            Tokens.HashOf(token), userId, now, now + SessionLifetime.TotalSeconds, PyStr.Prefix(userAgent, 200));
        Sql.Exec(conn, "UPDATE users SET last_login_at = ? WHERE id = ?", now, userId);
        tx.Commit();
        return token;
    }

    public static AppUser? FromSession(SqliteConnection conn, string token)
    {
        var row = Sql.One(conn, "SELECT user_id, expires_at FROM sessions WHERE token_hash = ?", Tokens.HashOf(token));
        if (row is null || row.Double("expires_at") < AppDb.Now()) return null;
        var user = Get(conn, row.Long("user_id"));
        return user is { Disabled: false } ? user : null;
    }

    public static void EndSession(SqliteConnection conn, string token) =>
        Sql.Exec(conn, "DELETE FROM sessions WHERE token_hash = ?", Tokens.HashOf(token));

    // --- personal API keys (the code index over MCP) ------------------------------------

    public static (string Key, long Id) CreateApiKey(SqliteConnection conn, long userId, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 60) throw new AccountError("Give the key a name of up to 60 characters.");
        var key = Tokens.New(ApiKeyPrefix);
        Sql.Exec(conn, "INSERT INTO api_keys (user_id, name, prefix, key_hash, created_at) VALUES (?, ?, ?, ?, ?)",
            userId, name, key[..10], Tokens.HashOf(key), AppDb.Now());
        return (key, Convert.ToInt64(Sql.Scalar(conn, "SELECT last_insert_rowid()")));
    }

    public static List<Row> ApiKeys(SqliteConnection conn, long userId) =>
        Sql.Query(conn, "SELECT id, name, prefix, created_at, last_used_at FROM api_keys WHERE user_id = ? ORDER BY id", userId);

    public static bool DeleteApiKey(SqliteConnection conn, long userId, long keyId) =>
        Sql.Exec(conn, "DELETE FROM api_keys WHERE id = ? AND user_id = ?", keyId, userId) > 0;

    public static AppUser? FromApiKey(SqliteConnection conn, string key)
    {
        if (!key.StartsWith(ApiKeyPrefix, StringComparison.Ordinal)) return null;
        var row = Sql.One(conn, "SELECT id, user_id FROM api_keys WHERE key_hash = ?", Tokens.HashOf(key));
        if (row is null) return null;
        Sql.Exec(conn, "UPDATE api_keys SET last_used_at = ? WHERE id = ?", AppDb.Now(), row.Long("id"));
        var user = Get(conn, row.Long("user_id"));
        return user is { Disabled: false } ? user : null;
    }

    // --- the person's model-gateway key, used for their chats -----------------------------

    public static string? GatewayKey(SqliteConnection conn, long userId) =>
        Sql.Scalar(conn, "SELECT gateway_key FROM users WHERE id = ?", userId) as string;

    public static void SetGatewayKey(SqliteConnection conn, long userId, string? key) =>
        Sql.Exec(conn, "UPDATE users SET gateway_key = ? WHERE id = ?", key, userId);

    /// <summary>First start: the administrator named in the environment, when nobody exists yet.</summary>
    public static AppUser? Bootstrap(SqliteConnection conn)
    {
        if (Count(conn) > 0) return null;
        var username = Environment.GetEnvironmentVariable("ARGUS_ADMIN_USERNAME") ?? "";
        var email = Environment.GetEnvironmentVariable("ARGUS_ADMIN_EMAIL") ?? "";
        var password = Environment.GetEnvironmentVariable("ARGUS_ADMIN_PASSWORD") ?? "";
        if (username.Length == 0 || email.Length == 0 || password.Length == 0) return null;
        return Create(conn, username, email, password, "admin", "Administrator");
    }
}

/// <summary>
/// Failed sign-ins, counted per name and per address over a sliding window.
/// Only failures count, so a whole office behind one address signing in at
/// nine o'clock is never throttled, while guessing at one account -- or many
/// accounts from one address -- is.
/// </summary>
public sealed class LoginThrottle(int perName = 10, int perAddress = 50, TimeSpan? window = null, Func<DateTimeOffset>? now = null)
{
    readonly TimeSpan _window = window ?? TimeSpan.FromMinutes(10);
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<DateTimeOffset>> _failures = new();

    static string NameKey(string name) => "n:" + name.Trim().ToLowerInvariant();
    static string AddressKey(string address) => "a:" + address;

    TimeSpan? Over(string key, int limit)
    {
        if (!_failures.TryGetValue(key, out var list)) return null;
        lock (list)
        {
            var cutoff = _now() - _window;
            list.RemoveAll(t => t < cutoff);
            return list.Count >= limit ? list[0] + _window - _now() : null;
        }
    }

    /// <summary>How long to wait before another attempt is allowed, or null when one is.</summary>
    public TimeSpan? RetryAfter(string name, string address)
    {
        var a = Over(NameKey(name), perName);
        var b = Over(AddressKey(address), perAddress);
        return a is null ? b : b is null ? a : (a > b ? a : b);
    }

    public void Failed(string name, string address)
    {
        foreach (var key in new[] { NameKey(name), AddressKey(address) })
        {
            var list = _failures.GetOrAdd(key, _ => []);
            lock (list) list.Add(_now());
        }
    }

    public void Succeeded(string name) => _failures.TryRemove(NameKey(name), out _);
}
