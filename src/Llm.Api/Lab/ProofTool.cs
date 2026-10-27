using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Microsoft.Extensions.Options;

namespace Llm.Api.Lab;

/// <summary>Configuration section "Lean": the science lab's proof checker (compose profile lean).</summary>
public sealed class LeanOptions
{
    /// <summary>The lean-jobs volume, as the app mounts it.</summary>
    public string Dir { get; set; } = "/lean";

    /// <summary>How long one check may take (loading Mathlib alone takes seconds).</summary>
    public int TimeoutSeconds { get; set; } = 180;
}

/// <summary>The proof checker: Lean 4 with Mathlib in a container of its own with no network (services/lean).</summary>
public sealed class LeanClient(IOptionsMonitor<LeanOptions> options, TimeProvider clock)
    : JobRunner(() => options.CurrentValue.Dir, "The proof checker (Lean)", "An admin turns on the lean profile (COMPOSE_PROFILES) and applies it.", clock);

/// <summary>
/// The science lab's proofs: a Lean file (a theorem and its proof, with Mathlib) checked by Lean in its own sandbox. A
/// proof is "checked" only when Lean accepts the file with no error and no sorry; "incomplete" when it leans on sorry;
/// "rejected" with Lean's messages, line by line, when it fails. What Lean said is the answer, not the model's word.
/// </summary>
public sealed partial class ProofTool(LeanClient lean, IOptionsMonitor<LeanOptions> options) : IChatTool
{
    public const string ToolId = "proofs";
    public const string Function = "lean_check";

    /// <summary>The longest Lean file a check takes.</summary>
    public const int MaxLength = 20_000;

    public string Id => ToolId;
    public string Title => "Proofs (Lean)";
    public string Description => "Proofs checked by Lean 4 with Mathlib: a theorem is proved only when Lean accepts its proof, with no sorry.";
    public string Icon => "badge-check";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult(lean.Unavailable());

    /// <summary>Off in new chats: the lab's page turns it on, and anyone may in a chat.</summary>
    public ToolSetting Defaults() => new() { ToolId = Id, OnByDefault = false };

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            Schema.Function(Function,
                "Checks a proof with Lean 4 and Mathlib. Give a whole Lean file: `import Mathlib`, then the theorem and its proof " +
                "(tactics such as simp, ring, linarith, nlinarith, norm_num, omega, positivity, induction). Returns checked (Lean accepts it, " +
                "no sorry), incomplete (it uses sorry) or rejected, with Lean's messages by line and column to correct it.",
                new JsonObject { ["source"] = Schema.Text("The whole Lean file, with real line breaks: import Mathlib on the first line, then e.g. theorem t (a b : ℝ) : (a + b)^2 = a^2 + 2*a*b + b^2 := by ring") },
                "source"),
        ],
        "Proofs (Lean): to prove a statement, write it as a Lean theorem with Mathlib and check it with lean_check. Say it is proved " +
        "only when lean_check says checked; when rejected, read Lean's messages, correct the proof and check it again (a few times at " +
        "most); when it stays rejected, say so and what Lean objects to.",
        CallAsync));

    private async Task<ToolResult> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        var source = args["source"]?.ToString() ?? "";
        if (source.Trim().Length == 0)
        {
            return new ToolResult("Give source: a whole Lean file, import Mathlib then the theorem and its proof.", IsError: true);
        }
        if (source.Length > MaxLength)
        {
            return new ToolResult($"The file is longer than {MaxLength} characters: check one theorem at a time.", IsError: true);
        }
        // A file written on one line with \n for its line breaks (as JSON would write them) is read with them.
        if (!source.Contains('\n', StringComparison.Ordinal) && source.Contains("\\n", StringComparison.Ordinal))
        {
            source = source.Replace("\\n", "\n", StringComparison.Ordinal);
        }
        SandboxResult run;
        try
        {
            run = await lean.RunAsync(source, [], options.CurrentValue.TimeoutSeconds, ct);
        }
        catch (SandboxException ex)
        {
            return new ToolResult(ex.Message, IsError: true);
        }
        if (run.TimedOut)
        {
            return new ToolResult($"Lean did not finish within {options.CurrentValue.TimeoutSeconds} seconds: a tactic searched too long. Try a more direct proof.", IsError: true);
        }
        if (run.Error is { } error)
        {
            return new ToolResult("Lean could not run: " + error, IsError: true);
        }
        var messages = Messages(run.Stdout + "\n" + run.Stderr);
        var errors = messages.Count(m => m["severity"]!.GetValue<string>() == "error");
        // "declaration uses `sorry`" (Lean 4.35; 'sorry' before).
        var sorry = messages.Any(m => m["text"]!.GetValue<string>() is var text && text.Contains("declaration uses", StringComparison.Ordinal) && text.Contains("sorry", StringComparison.Ordinal));
        var verdict = run.ExitCode == 0 && errors == 0 ? sorry ? "incomplete" : "checked" : "rejected";
        var result = new JsonObject
        {
            ["verdict"] = verdict,
            ["detail"] = verdict switch
            {
                "checked" => "Lean accepts the file: every theorem in it is proved.",
                "incomplete" => "Lean accepts the file, but a proof uses sorry: not proved.",
                _ => errors > 0 ? $"Lean rejects it: {errors} error{(errors == 1 ? "" : "s")}." : "Lean rejects it.",
            },
            ["messages"] = new JsonArray([.. messages.Take(20)]),
        };
        if (verdict == "rejected" && messages.Count == 0)
        {
            var said = (run.Stdout + run.Stderr).Trim();
            result["output"] = said[..Math.Min(said.Length, 2000)];
        }
        return new ToolResult(result.ToJsonString(Mcp.Plain));
    }

    /// <summary>Lean's messages: ".Proof.lean:3:2: error: ..." and the lines that follow each, as line, column, severity and text.</summary>
    public static List<JsonObject> Messages(string output)
    {
        var all = new List<JsonObject>();
        foreach (var line in output.Split('\n'))
        {
            if (Located().Match(line) is { Success: true } m)
            {
                all.Add(new JsonObject
                {
                    ["line"] = int.Parse(m.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    ["column"] = int.Parse(m.Groups["column"].Value, System.Globalization.CultureInfo.InvariantCulture),
                    ["severity"] = m.Groups["severity"].Value,
                    ["text"] = m.Groups["text"].Value,
                });
            }
            else if (all.Count > 0 && line.Length > 0)
            {
                var text = all[^1]["text"]!.GetValue<string>();
                if (text.Length < 1500)
                {
                    all[^1]["text"] = text + "\n" + line;
                }
            }
        }
        return all;
    }

    [GeneratedRegex(@"^[^:\s]*\.lean:(?<line>\d+):(?<column>\d+): (?<severity>error|warning|info)(?:\([^)]*\))?: ?(?<text>.*)$")]
    private static partial Regex Located();
}
