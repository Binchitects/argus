using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Llm.Api.Compliance;

/// <summary>docs/compliance/controls.yaml: the frameworks, and each control with what proves it.</summary>
public sealed class ControlsFile
{
    /// <summary>Framework key (iso27001, soc2, ..., commoncriteria) to its name and version.</summary>
    public Dictionary<string, string> Frameworks { get; set; } = [];
    public List<Control> Controls { get; set; } = [];
}

/// <summary>One control Arena provides (see the file's head for each field).</summary>
public sealed class Control
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Meets { get; set; } = "";
    /// <summary>Framework key to the clauses this control serves.</summary>
    public Dictionary<string, List<string>> Frameworks { get; set; } = [];
    /// <summary>file.md#anchor, relative to docs/.</summary>
    public List<string> Docs { get; set; } = [];
    /// <summary>Project/Class.Method or deploy/test_x.py::test_name.</summary>
    public List<string> Tests { get; set; } = [];
    /// <summary>app:id (a <see cref="ComplianceChecks"/> check) or live:id (deploy/scripts/compliance-check.py).</summary>
    public List<string> Checks { get; set; } = [];
    public string? Operator { get; set; }
}

/// <summary>Reads the controls: the copy built into the app (Admin → Compliance), or a text (the guard test reads the repository's).</summary>
public static partial class ComplianceControls
{
    public const string ControlTrait = "Control";

    private static readonly Lazy<string> BuiltText = new(() =>
    {
        using var stream = typeof(ComplianceControls).Assembly.GetManifestResourceStream("compliance-controls.yaml")
            ?? throw new InvalidOperationException("controls.yaml is not built into the app.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<ControlsFile> Built = new(() => Parse(BuiltText.Value));

    /// <summary>controls.yaml as built into the app, word for word (the evidence carries it).</summary>
    public static string Text => BuiltText.Value;

    public static ControlsFile All => Built.Value;

    public static ControlsFile Parse(string yaml) =>
        new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build().Deserialize<ControlsFile>(yaml) ?? new ControlsFile();

    [GeneratedRegex(@"^[A-Z]{3}-\d{2}$")]
    public static partial Regex IdFormat();

    private static readonly Lazy<IReadOnlyDictionary<string, FrameworkCatalog>> BuiltCatalogs = new(() => FrameworkCatalog.Read(Resource));

    /// <summary>Each framework's clauses, as frameworks.yaml and its catalogs built into the app list them.</summary>
    public static IReadOnlyDictionary<string, FrameworkCatalog> Catalogs => BuiltCatalogs.Value;

    /// <summary>Whether a clause a control cites is in its framework's catalog: itself, or the nearest one it is part of.</summary>
    public static bool IsClause(string framework, string clause) => Catalogs.TryGetValue(framework, out var c) && c.Entry(clause) is not null;

    private static string Resource(string name)
    {
        using var stream = typeof(ComplianceControls).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not built into the app.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
