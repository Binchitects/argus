using System.Security.Cryptography;
using System.Text;
using Llm.Core.Access;
using Llm.Core.Chat;

namespace Llm.Api.Knowledge;

/// <summary>
/// A document a connector found: what it is in its source, its title and link, its version, who may read it (any of
/// <paramref name="Readers"/>, and every one of <paramref name="Requires"/>), and how to get its text (asked only when
/// its version changed; null when it cannot be read now).
/// </summary>
public sealed record FoundDocument(string Key, string Title, string? Url, string Version, IReadOnlyList<string> Readers, Func<CancellationToken, Task<string?>> Text,
    IReadOnlyList<string>? Requires = null);

/// <summary>What one sync of a source learnt besides its documents: what it could not read, and what to keep unread.</summary>
public sealed class SyncPass
{
    /// <summary>Part of the source could not be listed: documents not seen are kept, not removed.</summary>
    public bool Incomplete { get; set; }

    /// <summary>What could not be read, in words, for the admin page.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>Documents a connector did not list because nothing changed (a GitLab project with no new activity), by the start of their keys: kept.</summary>
    public List<string> Unchanged { get; } = [];

    /// <summary>Documents that could not be read this time (a page that timed out), by their keys: kept as they were.</summary>
    public HashSet<string> Kept { get; } = new(StringComparer.Ordinal);

    /// <summary>Documents the source says are gone (a file deleted, in Graph's changes), by their keys: removed even where the rest is kept unread.</summary>
    public HashSet<string> Gone { get; } = new(StringComparer.Ordinal);

    /// <summary>What a source that mirrors its own permissions could mirror, and where the admin's choice applies instead, in words for the admin page.</summary>
    public List<string> Mirror { get; } = [];
}

/// <summary>
/// Reads one kind of source: GitLab, a folder, a website, Confluence, SharePoint. Google Drive, Jira and the
/// others come as connectors of their own: a kind, a check of its settings, and its documents with their readers.
/// </summary>
public interface IKnowledgeConnector
{
    /// <summary>"gitlab", "folder", "website", "confluence", "sharepoint".</summary>
    string Kind { get; }

    /// <summary>Why the source cannot be read as set, in words, or null.</summary>
    string? Check(KnowledgeSource source);

    /// <summary>Every document the source has now. Throws when it cannot be read at all (the sync fails with the reason).</summary>
    IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, CancellationToken ct);
}

/// <summary>A connector that can try its settings before a source is saved: Test connection on the admin page.</summary>
public interface IConnectionTest
{
    /// <summary>What it reached, in words (who the account is, what it may read). Throws a <see cref="KnowledgeException"/> with why it cannot.</summary>
    Task<string> TestAsync(KnowledgeSource source, CancellationToken ct);
}

public static class Readers
{
    public const string Everyone = "everyone";
    public const string Admins = "admins";

    /// <summary>The readers of a folder's or a website's documents, from who may read the source. Admins always may, as they set it up.</summary>
    public static List<string> For(Audience audience, IEnumerable<Guid> groups) => audience switch
    {
        Audience.Everyone => [Everyone],
        Audience.Groups => [Admins, .. groups.Select(g => "group:" + g)],
        _ => [Admins],
    };

    /// <summary>A GitLab project's documents: its members may read them (<see cref="KnowledgeReaders"/>), admins only when they are members.</summary>
    public static string GitLab(long project) => "gitlab:" + project.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whom the admin chose for what a source's own permissions cannot tell (Confluence, SharePoint): decided at each search, so a change applies at once.</summary>
    public static string Chosen(Guid source) => "chosen:" + source.ToString("N");

    /// <summary>Someone a document was shared with, by their email (or user principal name).</summary>
    public static string Person(string email) => "person:" + email.Trim().ToLowerInvariant();

    /// <summary>A directory group (Microsoft Entra, LDAP, the company sign-in's groups claim), by its name or ID, as people's directory groups name it.</summary>
    public static string Directory(string group) => "directory:" + group.Trim().ToLowerInvariant();

    /// <summary>A text's SHA-256, as a version that changes when the text does.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>A source's token or client secret: stored encrypted with APP_DATA_KEY, read only by its connector, never shown.</summary>
public static class SourceSecret
{
    /// <summary>The secret as the connector sends it, or null when there is none or it cannot be decrypted (APP_DATA_KEY changed).</summary>
    public static string? Read(KnowledgeSource source, string? dataKey) =>
        source.SecretEncrypted is { Length: > 0 } stored && stored.StartsWith(Settings.SettingsCrypto.Prefix, StringComparison.Ordinal)
            ? Settings.SettingsCrypto.Decrypt(stored, dataKey) is { Length: > 0 } plain ? plain : null
            : null;

    /// <summary>Why a source's secret cannot be used, in words, or null.</summary>
    public static string? Problem(KnowledgeSource source, string? dataKey, string what) =>
        source.SecretEncrypted is not { Length: > 0 } ? $"Give the {what}."
        : Read(source, dataKey) is null ? $"The {what} cannot be read (APP_DATA_KEY changed?): enter it again."
        : null;
}
