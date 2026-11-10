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
    public static string Product { get; } = Read("BrandProduct", DefaultProduct);

    /// <summary>The code-intelligence service's name: "Farzan Graph" unless built with another.</summary>
    public static string CodeIntel { get; } = Read("BrandCodeIntel", DefaultCodeIntel);

    /// <summary>The names a text people read was written with (docs, dashboards): the defaults.</summary>
    public const string DefaultProduct = "Farzan Arena", DefaultCodeIntel = "Farzan Graph";

    /// <summary>A text written with the default names (a dashboard's title, a page of the manual), with the names built.</summary>
    public static string Named(string text) =>
        Product == DefaultProduct && CodeIntel == DefaultCodeIntel ? text
            : text.Replace(DefaultProduct, Product, StringComparison.Ordinal).Replace(DefaultCodeIntel, CodeIntel, StringComparison.Ordinal);

    private static string Read(string key, string fallback) =>
        typeof(Brand).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value is { Length: > 0 } v ? v : fallback;
}
