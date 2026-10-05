using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>What Laya said of a shell command: how likely it is to destroy, to write outside the workspace, and to reach the network.</summary>
internal sealed record CommandRisk(double Destructive, double Outside, double Network)
{
    /// <summary>Whether any risk is at or past <see cref="LayaGuard.Threshold"/>: then the person is asked, whatever the mode.</summary>
    public bool High => Destructive >= LayaGuard.Threshold || Outside >= LayaGuard.Threshold || Network >= LayaGuard.Threshold;

    /// <summary>"Laya: destructive 92%, outside the workspace 4%, network 1%".</summary>
    public string Describe() => $"Laya: destructive {Percent(Destructive)}, outside the workspace {Percent(Outside)}, network {Percent(Network)}";

    /// <summary>The risks past the threshold, in words: "may be destructive (92%) and reach the network (81%)".</summary>
    public string Flagged()
    {
        var said = new List<string>();
        if (Destructive >= LayaGuard.Threshold)
        {
            said.Add($"be destructive ({Percent(Destructive)})");
        }
        if (Outside >= LayaGuard.Threshold)
        {
            said.Add($"write outside the workspace ({Percent(Outside)})");
        }
        if (Network >= LayaGuard.Threshold)
        {
            said.Add($"reach the network ({Percent(Network)})");
        }
        return "may " + string.Join(" and ", said);
    }

    private static string Percent(double p) => Math.Round(p * 100).ToString(CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// Laya's look at shell commands, through Arena's decide tool when Arena MCP offers it (the
/// laya module runs there). Before a command runs without asking (yolo, or "always" for its
/// first words), Laya is asked whether it is destructive, writes outside the workspace, or
/// reaches the network; at <see cref="Threshold"/> or past it on any, the person is asked
/// anyway. Every approval prompt for a command shows the three probabilities. It only adds
/// questions: when Laya cannot answer, the mode decides as it always did.
/// </summary>
internal sealed partial class LayaGuard(McpClient arena, Workspace workspace, Ui ui)
{
    /// <summary>
    /// The probability at which a risk asks. Measured with Laya's English checkpoint on 95 commands
    /// labelled by hand (RealLayaTests.Commands, docs/code-arena.md): 45 of 52 risky ones asked,
    /// 2 of 43 harmless ones; at 0.7, 37 of the risky ones.
    /// </summary>
    public const double Threshold = 0.6;

    public const string Tool = "decide";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private bool _warned;

    /// <summary>A guard when Arena serves decide, else null.</summary>
    public static LayaGuard? For(McpClient? arena, Workspace workspace, Ui ui) =>
        arena?.Tools.Any(t => t.Str("name") == Tool) == true ? new LayaGuard(arena, workspace, ui) : null;

    /// <summary>What Laya says of the command, or null when it cannot say (then nothing changes).</summary>
    public async Task<CommandRisk?> AssessAsync(string? command, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(Patience);
        try
        {
            var result = await arena.CallAsync(Tool, Request(command, workspace), wait.Token);
            if (!result.IsError && Parse(result.Text) is { } risk)
            {
                return risk;
            }
            Warn(result.IsError ? result.Text : "its answer could not be read");
        }
        catch (McpException e)
        {
            Warn(e.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Warn($"it did not answer within {Patience.TotalSeconds:0} seconds");
        }
        return null;
    }

    /// <summary>
    /// The decide call: the command, the workspace, and the paths outside it the command names
    /// (found here: Laya reads text, it does not compare paths), with three yes/no questions.
    /// </summary>
    public static JsonObject Request(string command, Workspace workspace)
    {
        var outside = OutsidePaths(command, workspace);
        return new JsonObject
        {
            ["state"] = $"Shell command: {command.Trim()}\nWorking folder: {workspace.Root}\nPaths outside the working folder: {(outside.Count > 0 ? string.Join(", ", outside) : "none")}",
            ["questions"] = new JsonArray(
                YesNo("destructive", "Does this command delete, overwrite or discard files, data or history?"),
                YesNo("outside", "Does this command write to, change or delete one of the paths outside the working folder?"),
                YesNo("network", "Does this command download, upload, install packages, push to a git remote, or connect to another machine?")),
            // Commands are English to the checkpoints, and the English one is calibrated.
            ["checkpoint"] = "english",
        };
    }

    private static JsonObject YesNo(string id, string question) => new() { ["id"] = id, ["type"] = "noul", ["question"] = question };

    /// <summary>The three probabilities from decide's answer, or null when it is not one.</summary>
    public static CommandRisk? Parse(string text)
    {
        var answers = Json.ParseObject(text)?["answers"] as JsonObject;
        double? P(string id) => answers?[id]?["noul"] is JsonValue v && v.TryGetValue<double>(out var p) ? p : null;
        return P("destructive") is { } d && P("outside") is { } o && P("network") is { } n ? new CommandRisk(d, o, n) : null;
    }

    /// <summary>The paths a command names outside every allowed folder: ~, $HOME, .., absolute paths elsewhere (not /dev/null, not URLs).</summary>
    public static IReadOnlyList<string> OutsidePaths(string command, Workspace workspace)
    {
        var found = new List<string>();
        foreach (var token in Separators().Split(command))
        {
            if (token.Length == 0 || token is "/dev/null" or "NUL" || token.Contains("://", StringComparison.Ordinal) || token.Contains('@'))
            {
                continue;
            }
            string full;
            try
            {
                if (token.StartsWith('~') || token.StartsWith("$HOME", StringComparison.Ordinal) || token.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = token[(token.StartsWith('~') ? 1 : token.StartsWith('$') ? 5 : 13)..].TrimStart('/', '\\');
                    full = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), rest));
                }
                else if (token.StartsWith("..", StringComparison.Ordinal) || token.StartsWith('/') || token.StartsWith('\\') || WindowsDrive().IsMatch(token))
                {
                    full = Path.GetFullPath(token, workspace.Root);
                }
                else
                {
                    continue;
                }
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (!workspace.Inside(Path.TrimEndingDirectorySeparator(full)) && !found.Contains(token))
            {
                found.Add(token);
            }
        }
        return [.. found.Take(8)];
    }

    /// <summary>The terminal's question for a command: with Laya's probabilities, and why it asks when the mode would not.</summary>
    public static string Question(string tool, CommandRisk? risk, bool modeAsks) =>
        Note(risk, modeAsks) is { } note ? $"Allow {tool}? ({note})" : $"Allow {tool}?";

    /// <summary>What an approval prompt shows of Laya's look: the probabilities, and why it asks when the mode would not.</summary>
    public static string? Note(CommandRisk? risk, bool modeAsks) =>
        risk is null ? null
        : modeAsks ? risk.Describe()
        : $"Laya says this command {risk.Flagged()}, so this asks although the mode would run it. {risk.Describe()}";

    /// <summary>For a run that cannot ask: why a command Laya flagged does not run.</summary>
    public static string CannotAsk(string tool, CommandRisk risk) =>
        $"{tool} was not run: Laya says this command {risk.Flagged()}, which needs the person's approval, and this run cannot ask. " +
        "Say what you would have done, or find a safer way.";

    private void Warn(string why)
    {
        if (!_warned)
        {
            _warned = true;
            ui.Info($"Laya did not check this command ({Fmt.OneLine(why, 120)}): commands run as the mode says.");
        }
    }

    [GeneratedRegex(@"[\s;&|<>()'""=`]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex WindowsDrive();
}
