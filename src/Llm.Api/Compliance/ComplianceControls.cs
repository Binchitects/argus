using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Llm.Api.Compliance;

/// <summary>docs/compliance/controls.yaml: the frameworks, and each control with what proves it.</summary>
public sealed class ControlsFile
{
    /// <summary>Framework key (iso27001, soc2, hipaa, gdpr) to its name and version.</summary>
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

    /// <summary>
    /// What a clause of each framework looks like: Annex A's controls (5.1–5.37, 6.1–6.8, 7.1–7.14, 8.1–8.34), the Trust
    /// Services Criteria, 45 CFR 160/164 sections with their paragraphs, the GDPR's articles with their paragraphs and points.
    /// </summary>
    public static bool IsClause(string framework, string clause) => framework switch
    {
        "iso27001" => Iso().Match(clause) is { Success: true } m && int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) is var n && n >= 1
            && n <= m.Groups[1].Value switch { "5" => 37, "6" => 8, "7" => 14, _ => 34 },
        "soc2" => Soc2().IsMatch(clause),
        "hipaa" => Hipaa().IsMatch(clause),
        "gdpr" => Gdpr().Match(clause) is { Success: true } g && int.Parse(g.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is >= 1 and <= 99,
        _ => false,
    };

    [GeneratedRegex(@"^A\.([5-8])\.(\d{1,2})$")]
    private static partial Regex Iso();

    [GeneratedRegex(@"^(CC[1-9]\.[1-9]|A1\.[1-3]|C1\.[12]|PI1\.[1-5]|P[1-8]\.[1-9])$")]
    private static partial Regex Soc2();

    [GeneratedRegex(@"^16[04]\.\d{3}(\([a-zA-Z0-9]{1,4}\))*$")]
    private static partial Regex Hipaa();

    [GeneratedRegex(@"^Art\.(\d{1,2})(\(\d{1,2}\))?(\([a-z]\))?$")]
    private static partial Regex Gdpr();
}
