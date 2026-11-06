namespace Llm.Api.Identity;

/// <summary>
/// The colour themes a person can choose (Your account → Appearance): Arena's own, the calm named ones, then VS Code's. The web has the
/// same list (src/web/src/lib/color-themes.ts, checked against this one by its tests) and the colours themselves.
/// </summary>
public static class ColorThemes
{
    public const string Default = "default";
    public static readonly string[] All = [Default, "sakura", "midnight", "copper", "forest", "ocean", "lavender", "vscode", "modern", "monokai", "solarized", "onedark", "dracula", "github", "nord", "contrast"];
}
