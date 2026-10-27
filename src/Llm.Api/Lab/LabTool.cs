using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Llm.Core.Chat;
using Microsoft.Extensions.Options;

namespace Llm.Api.Lab;

/// <summary>
/// The science lab's tools (Science lab): exact mathematics with SymPy (compute, check a claim, solve and put the solutions
/// back) and units with Pint, each run in the sandbox by lab.py with the call in a file beside it (the model's words are
/// read as mathematics there, never as the app's code). What a result says is the tool's: a claim is "proved" only when
/// SymPy shows it, "holds numerically" when it is only true at the points tried, "refuted" with where it fails.
/// </summary>
public sealed class LabTool(SandboxClient sandbox, IOptionsMonitor<SandboxOptions> options) : IChatTool
{
    public const string ToolId = "lab";

    private static readonly string Script = Read("lab.py");

    public string Id => ToolId;
    public string Title => "Science lab";
    public string Description => "Exact mathematics checked by SymPy (compute, check a claim, solve) and units by Pint: results shown and proved, not guessed.";
    public string Icon => "flask-conical";

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult(sandbox.Unavailable());

    /// <summary>Off in new chats: the lab's page turns it on, and anyone may in a chat.</summary>
    public ToolSetting Defaults() => new() { ToolId = Id, OnByDefault = false };

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(
        [
            Schema.Function("math_compute",
                "Computes exactly with SymPy: simplify, expand, factor, diff, integrate (with lower and upper for a definite integral), solve, limit, " +
                "series, evaluate (to digits), apart, together, and for a Matrix([[...]]) det, inverse, eigenvals. Write expressions as SymPy does: " +
                "x**2 + sin(x), sqrt(2), exp(-x**2), oo for infinity. Returns the result and its LaTeX.",
                new JsonObject
                {
                    ["expression"] = Schema.Text("The expression, e.g. x**2*sin(x)"),
                    ["operation"] = Schema.Text("simplify (default), expand, factor, diff, integrate, solve, limit, series, evaluate, apart, together, det, inverse or eigenvals"),
                    ["variable"] = Schema.Text("The variable, when there is more than one"),
                    ["order"] = new JsonObject { ["type"] = "integer", ["description"] = "diff: how many times; series: up to which power" },
                    ["lower"] = Schema.Text("integrate: the lower limit (e.g. 0, -oo)"),
                    ["upper"] = Schema.Text("integrate: the upper limit"),
                    ["at"] = Schema.Text("limit or series: the point"),
                    ["digits"] = new JsonObject { ["type"] = "integer", ["description"] = "evaluate: how many digits (15)" },
                }, "expression"),
            Schema.Function("math_check",
                "Checks that left equals right: proved when SymPy shows it exactly; holds numerically when it is only true at the points tried " +
                "(not a proof); refuted with a point where it fails. Use it before saying a step, an identity or an answer is right.",
                new JsonObject
                {
                    ["left"] = Schema.Text("One side, e.g. sin(x)**2 + cos(x)**2"),
                    ["right"] = Schema.Text("The other side, e.g. 1"),
                }, "left", "right"),
            Schema.Function("math_solve",
                "Solves equations exactly (a system too) and puts each solution back into every equation: checked says whether it holds.",
                new JsonObject
                {
                    ["equations"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "e.g. [\"x + y = 3\", \"x - y = 1\"]" },
                    ["variables"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Which to solve for (all of them when left out)" },
                }, "equations"),
            Schema.Function("units_convert",
                "Converts a quantity with units with Pint, and says when the units do not match (a dimension check): e.g. 9.81 m/s^2 to ft/s^2. " +
                "Without to, in base SI units.",
                new JsonObject
                {
                    ["quantity"] = Schema.Text("The quantity, e.g. 3 kWh or 72 km/h"),
                    ["to"] = Schema.Text("The units to convert to, e.g. MJ or m/s"),
                }, "quantity"),
        ],
        "Science lab: work mathematics with these tools instead of in your head. Compute with math_compute, check each step and the answer with " +
        "math_check (or math_solve, which checks its solutions), and units with units_convert. Say a result is proved only when math_check says " +
        "proved; holds numerically is evidence, not proof. Show the mathematics in LaTeX ($...$ or $$...$$) as the tool gives it.",
        (name, args, token) => CallAsync(name, args, token)));

    private async Task<ToolResult> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        var call = new JsonObject { ["op"] = name, ["args"] = args.DeepClone() };
        SandboxResult run;
        try
        {
            run = await sandbox.RunAsync(Script, [new SandboxFile("lab-call.json", Encoding.UTF8.GetBytes(call.ToJsonString()))], options.CurrentValue.TimeoutSeconds, ct);
        }
        catch (SandboxException ex)
        {
            return new ToolResult(ex.Message, IsError: true);
        }
        if (run.TimedOut)
        {
            return new ToolResult($"SymPy did not finish within {options.CurrentValue.TimeoutSeconds} seconds: try a simpler form, or a numeric evaluate.", IsError: true);
        }
        var line = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (line is null || JsonNode.Parse(line) is not JsonObject answer)
        {
            var said = run.Error ?? run.Stderr.Trim();
            return new ToolResult("The lab gave no answer: " + (said.Length > 0 ? said[..Math.Min(said.Length, 500)] : "nothing printed"), IsError: true);
        }
        return answer["ok"]?.GetValue<bool>() == true
            ? new ToolResult(answer["result"]!.ToJsonString(Mcp.Plain))
            : new ToolResult(answer["error"]?.GetValue<string>() ?? "The lab could not do that.", IsError: true);
    }

    private static string Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
