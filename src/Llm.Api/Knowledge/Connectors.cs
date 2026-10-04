using System.Security.Cryptography;
using System.Text;
using Llm.Core.Access;
using Llm.Core.Chat;

namespace Llm.Api.Knowledge;

/// <summary>
/// A document a connector found: what it is in its source, its title and link, its version, who may read it,
/// and how to get its text (asked only when its version changed; null when it cannot be read now).
/// </summary>
public sealed record FoundDocument(string Key, string Title, string? Url, string Version, IReadOnlyList<string> Readers, Func<CancellationToken, Task<string?>> Text);

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
}

/// <summary>
/// Reads one kind of source: GitLab, a folder, a website. Confluence, SharePoint and the others come as
/// connectors of their own: a kind, a check of its settings, and its documents with their readers.
/// </summary>
public interface IKnowledgeConnector
{
    /// <summary>"gitlab", "folder", "website".</summary>
    string Kind { get; }

    /// <summary>Why the source cannot be read as set, in words, or null.</summary>
    string? Check(KnowledgeSource source);

    /// <summary>Every document the source has now. Throws when it cannot be read at all (the sync fails with the reason).</summary>
    IAsyncEnumerable<FoundDocument> ReadAsync(KnowledgeSource source, SyncPass pass, CancellationToken ct);
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

    /// <summary>A text's SHA-256, as a version that changes when the text does.</summary>
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
