namespace Argus.Store;

/// <summary>
/// What each way one repository uses another is, how sure a link of it is, and where it sits: its kind (from what the
/// file declared and how), its layer (build: what it is built from; ci: its pipeline's; deploy: what runs it; runtime:
/// what it talks to; declared: what its owners say; history: what changes with it), and the prior confidence of a link
/// of that kind made the way its name matched. Priors are starting values, to be calibrated against links an owner
/// confirmed; a link's confidence is its prior, less when a code link rests on one file, and a pair's is the noisy-OR
/// of its kinds (the best of each kind: names of one kind are not independent votes).
/// </summary>
public static class LinkKinds
{
    public const string Build = "build", Ci = "ci", Deploy = "deploy", Runtime = "runtime", Declared = "declared", History = "history";

    public static readonly string[] Layers = [Build, Ci, Deploy, Runtime, Declared, History];

    /// <summary>The layers walked when none are asked for: every one but history (what merely changes together).</summary>
    public static readonly string[] Walkable = [Build, Ci, Deploy, Runtime, Declared];

    /// <summary>The confidence a link must have to be walked by default (likely or strong).</summary>
    public const double Walked = 0.5;

    /// <summary>A link of a code import or an #include that rests on one file is less sure than one many files make.</summary>
    public const double OneFile = 0.85;

    /// <summary>A link's kind and layer, from the kind of what the file declared and its form.</summary>
    public static (string Kind, string Layer) Of(string declKind, string form) => declKind switch
    {
        "nuget" or "npm" or "pypi" or "cargo" or "maven" => ("package:" + declKind, Build),
        "cs" or "cs-type" => ("import:csharp", Build),
        "java" or "java-package" => ("import:java", Build),
        "py" => ("import:python", Build),
        "go" => ("import:go", Build),
        "proto" => ("import:proto", Build),
        "include" => ("include", Build),
        "repo" => form switch
        {
            "ci" => ("ci:include", Ci),
            "git" => ("package:git", Build),
            _ => ("repository", Build),
        },
        "image" => form switch
        {
            "base" => ("image:base", Build),
            "ci" => ("image:ci", Ci),
            _ => ("image:deploy", Deploy),
        },
        _ when declKind.StartsWith("runtime:", StringComparison.Ordinal) => (declKind, Runtime),
        _ when declKind.StartsWith("declared", StringComparison.Ordinal) => ("declared", Declared),
        "cochange" => ("cochange", History),
        _ => (declKind, Build),
    };

    /// <summary>
    /// How sure a link of a kind is, by how its name matched: exact; type (a C# type's namespace); member (a Kotlin
    /// function's package); prefix (a module in a package); top (a one-segment module name); module (a Go import's
    /// module); suffix (a .proto found by the end of its path); registry, sub (a sub-image), flat (a private registry's
    /// bare name); include (a header found file by file); settled:* (a name several repositories provide, chosen by what
    /// else the user has: a package reference, the same file's other uses, its other links), from how its name matched.
    /// </summary>
    public static double Prior(string kind, string how, string? settled = null)
    {
        var prior = kind switch
        {
            _ when kind.StartsWith("package:", StringComparison.Ordinal) => kind == "package:git" ? 0.95 : 0.97,
            "repository" => 0.95,
            "ci:include" => 0.97,
            "import:go" => 0.90,
            "import:proto" => how == "suffix" ? 0.70 : 0.95,
            "include" => 0.85,
            "image:base" or "image:ci" or "image:deploy" => how switch { "flat" => 0.60, "sub" => 0.85, _ => 0.90 },
            "declared" => 0.99,
            "cochange" => 0.45,
            _ when kind.StartsWith("runtime:", StringComparison.Ordinal) => 0.70,
            _ => how switch
            {
                "type" or "member" or "prefix" => 0.75,
                "top" => 0.60,
                _ => 0.85,
            },
        };
        return settled switch
        {
            "settled:manifest" => prior * 0.95,
            "settled:file" => prior * 0.80,
            "settled:graph" => prior * 0.70,
            _ => prior,
        };
    }

    /// <summary>strong (0.85 or more), likely (0.5 or more), weak; a candidate is a name several provide that nothing settled.</summary>
    public static string Tier(double confidence) => confidence >= 0.85 ? "strong" : confidence >= Walked ? "likely" : "weak";

    /// <summary>Whether a link of this kind comes from code that names what it uses (its references can be searched for).</summary>
    public static bool FromCode(string kind) => kind.StartsWith("import:", StringComparison.Ordinal) || kind == "include";

    /// <summary>The noisy-OR of independent ways: 1 - Π(1 - c).</summary>
    public static double AnyOf(IEnumerable<double> confidences) => 1 - confidences.Aggregate(1.0, (left, c) => left * (1 - c));
}
