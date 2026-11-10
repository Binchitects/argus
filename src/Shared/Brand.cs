using System.Reflection;

namespace Branding;

/// <summary>
/// The names people see: the product's and its code-intelligence service's, set at build time (branding.env at the
/// repository's root, or PRODUCT_NAME and CODE_INTEL_NAME in the build's environment). Text people read uses these and
/// never spells a name out; nothing else (a key, a path, a service, a setting) is called by them.
/// </summary>
public static class Brand
{
    /// <summary>The product's name: "Farzan Arena" unless built with another.</summary>
    public static string Product { get; } = Read("BrandProduct", "Farzan Arena");

    /// <summary>The code-intelligence service's name: "Farzan Graph" unless built with another.</summary>
    public static string CodeIntel { get; } = Read("BrandCodeIntel", "Farzan Graph");

    private static string Read(string key, string fallback) =>
        typeof(Brand).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : fallback;
}
