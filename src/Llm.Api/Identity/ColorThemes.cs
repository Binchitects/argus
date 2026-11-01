namespace Llm.Api.Identity;

/// <summary>
/// The colour themes a person can choose (Your account → Appearance): Arena's own, then the named ones. The web has the
/// same list (src/web/src/lib/color-themes.ts, checked against this one by its tests) and the colours themselves.
/// </summary>
public static class ColorThemes
{
    public const string Default = "default";
    public static readonly string[] All = [Default, "sakura", "midnight", "copper", "forest", "ocean", "lavender"];
}
