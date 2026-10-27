namespace Llm.Api.Operations;

/// <summary>One of the stack's features (deploy/features): what it runs and what it needs.</summary>
/// <param name="Services">The containers it runs; none for a part of the app.</param>
/// <param name="Needs">What the host needs for it (gpu, gitlab), or null.</param>
/// <param name="Requires">The features it cannot work without.</param>
public sealed record Feature(string Id, bool OnByDefault, IReadOnlyList<string> Services, string? Needs, IReadOnlyList<string> Requires, string Title);

/// <summary>
/// Which of the stack's features are on: Features:On, the list COMPOSE_PROFILES gives the stack. Unset, every one
/// is (the tests, and the app run without the stack's compose file); set, those it names whose required features are
/// on too. Read once: the installer recreates the app when the list changes.
/// </summary>
public sealed class Features
{
    private static readonly Lazy<IReadOnlyList<Feature>> Listed = new(() =>
    {
        using var stream = typeof(Features).Assembly.GetManifestResourceStream("features")
            ?? throw new InvalidOperationException("deploy/features is not built into the app.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    private readonly HashSet<string> _on;

    public Features(IConfiguration config)
    {
        var named = config["Features:On"] is { } list
            ? list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : All.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        _on = [.. All.Where(f => Holds(f, named, [])).Select(f => f.Id)];
    }

    /// <summary>Every feature, as deploy/features lists them.</summary>
    public static IReadOnlyList<Feature> All => Listed.Value;

    /// <summary>The feature of that id; an id deploy/features does not list is a mistake in the code.</summary>
    public static Feature Of(string id) => All.FirstOrDefault(f => f.Id == id) ?? throw new ArgumentException($"deploy/features has no feature {id}.", nameof(id));

    public bool IsOn(string id) => _on.Contains(Of(id).Id);

    /// <summary>The features that are off, in deploy/features' order.</summary>
    public IReadOnlyList<string> Off => [.. All.Where(f => !_on.Contains(f.Id)).Select(f => f.Id)];

    /// <summary>Whether the stack runs a service: one of the core always, another when its feature is on.</summary>
    public bool Runs(string service) => All.FirstOrDefault(f => f.Services.Contains(service)) is not { } f || _on.Contains(f.Id);

    /// <summary>Whether a feature is on where the configuration says, for what is decided at startup (background work).</summary>
    public static bool On(IConfiguration config, string id) => new Features(config).IsOn(id);

    private static bool Holds(Feature f, HashSet<string> named, HashSet<string> seen) =>
        named.Contains(f.Id) && seen.Add(f.Id) && f.Requires.All(r => All.FirstOrDefault(x => x.Id == r) is { } x && Holds(x, named, [.. seen]));

    /// <summary>deploy/features: one feature a line, "id | on|off | services | needs | requires | title"; # starts a comment.</summary>
    public static IReadOnlyList<Feature> Parse(string text)
    {
        static IReadOnlyList<string> Words(string s) => s == "-" ? [] : s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var all = new List<Feature>();
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0)
            {
                continue;
            }
            var f = line.Split('|', StringSplitOptions.TrimEntries);
            if (f.Length != 6 || f[1] is not ("on" or "off"))
            {
                throw new FormatException($"deploy/features: not \"id | on|off | services | needs | requires | title\": {line}");
            }
            all.Add(new Feature(f[0], f[1] == "on", Words(f[2]), f[3] == "-" ? null : f[3], Words(f[4]), f[5]));
        }
        return all;
    }
}
