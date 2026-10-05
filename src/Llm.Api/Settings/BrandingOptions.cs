namespace Llm.Api.Settings;

/// <summary>Configuration section "Branding": what people see the service called (Settings page, applies at once).</summary>
public sealed class BrandingOptions
{
    public string ProductName { get; set; } = "Argus Arena";
    public string? SignInHeadline { get; set; } = "Your organisation's model, code search and usage, in one place.";
    public string? SupportContact { get; set; }

    /// <summary>Where this version's complete source is: the AGPL (section 13) has every user of it offered the source.</summary>
    public string SourceUrl { get; set; } = "https://github.com/Binchitects/argus";
}
