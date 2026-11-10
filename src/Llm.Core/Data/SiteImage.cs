namespace Llm.Core.Data;

/// <summary>
/// An image of the site's own that an admin chose (Settings → Branding): the app's icon, beside its name in the sidebar,
/// on the sign-in page, in the browser's tab and on a phone's home screen. Public, as the name is: not sealed.
/// </summary>
public sealed class SiteImage
{
    /// <summary>Which image: "icon".</summary>
    public required string Name { get; set; }
    /// <summary>image/png, image/jpeg, image/webp or image/svg+xml, read from the file itself.</summary>
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    /// <summary>Its SHA-256 (hex): its address changes with it, so a browser never keeps the old one.</summary>
    public required string Sha256 { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? UpdatedBy { get; set; }
}
