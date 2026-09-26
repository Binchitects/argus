using System.Text.RegularExpressions;

namespace Llm.Api.Operations;

public sealed record EnvSample(string File, string Title, string? Hardware, string? Download, string? Measured, string? Status, string Model, string Block);

/// <summary>Reads the shipped .env samples: their header and the block between `# >>> MODEL` and `# &lt;&lt;&lt; MODEL`.</summary>
public static partial class EnvSamples
{
    [GeneratedRegex(@"^# >>> MODEL.*?^# <<< MODEL[^\n]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Block();

    [GeneratedRegex(@"^# (TITLE|HARDWARE|DOWNLOAD|MEASURED|STATUS): (.*)$", RegexOptions.Multiline)]
    private static partial Regex Meta();

    [GeneratedRegex(@"^MODEL_NAME=(.*)$", RegexOptions.Multiline)]
    private static partial Regex ModelName();

    public static IReadOnlyList<EnvSample> Read(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }
        var list = new List<EnvSample>();
        foreach (var path in Directory.GetFiles(dir, "*.env").Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (Block().Match(text) is not { Success: true } block)
            {
                continue;
            }
            var meta = Meta().Matches(text).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());
            list.Add(new EnvSample(
                Path.GetFileName(path),
                meta.GetValueOrDefault("TITLE") ?? Path.GetFileName(path),
                meta.GetValueOrDefault("HARDWARE"), meta.GetValueOrDefault("DOWNLOAD"),
                meta.GetValueOrDefault("MEASURED"), meta.GetValueOrDefault("STATUS"),
                ModelName().Match(block.Value) is { Success: true } m ? m.Groups[1].Value.Trim() : "?",
                block.Value));
        }
        return list;
    }
}
