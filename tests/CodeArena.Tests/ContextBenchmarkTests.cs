using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Xunit.Abstractions;

namespace CodeArena.Tests;

/// <summary>Runs only where a real model is given (evals/run_context_bench.py sets CODE_ARENA_BENCH_URL): a measurement, not a test.</summary>
public sealed class BenchFactAttribute : FactAttribute
{
    public BenchFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODE_ARENA_BENCH_URL")))
        {
            Skip = "No model to measure with: evals/run_context_bench.py gives one (CODE_ARENA_BENCH_URL).";
        }
    }
}

/// <summary>
/// A long session of real coding tasks on a small C# shop (a bug in a sum, a parser, a constant to find, a price in a
/// 900-line catalogue, a method to add, a report's order, a rename across files), one after another in one session, with a
/// real model: each task's tokens, requests and seconds, whether its hidden check passes, and how the model's tool calls
/// went. Each arm as it says: "before" (results whole until the window fills, calls run as the model wrote them) and
/// "after" (results summarised once used, calls mended). Written to CODE_ARENA_BENCH_OUT as JSON.
/// </summary>
public sealed class ContextBenchmarkTests(ITestOutputHelper log) : IDisposable
{
    private readonly FakeGateway _unused = new();

    public void Dispose() => _unused.Dispose();

    /// <summary>A task: what the person asks, and the check that runs against the files after it (a program's lines).</summary>
    public sealed record BenchTask(string Id, string Prompt, string Check);

    public static readonly BenchTask[] Tasks =
    [
        new("subtotal", "The cart's subtotal is wrong: a cart with 3 of an item at 2.50 gives 5.50 instead of 7.50. Find the cause in the code and fix it. `dotnet build` builds the project.",
            """
            var cart = new Shop.Cart();
            cart.AddItem("a", 3, 2.50m);
            cart.AddItem("b", 2, 1.25m);
            Expect(cart.Subtotal == 10.00m, $"subtotal {cart.Subtotal}");
            """),
        new("parser", "Prices typed with a decimal comma (\"1,5\") are refused by the price parser, and they must be read as 1.5; prices with a point must still work. Fix it.",
            """
            Expect(Shop.PriceParser.Parse("1,5") == 1.5m, "1,5");
            Expect(Shop.PriceParser.Parse("12.75") == 12.75m, "12.75");
            Expect(Shop.PriceParser.Parse(" 3 ") == 3m, "3");
            """),
        new("tax", "The VAT rate changed from 20% to 25%. Find where the shop keeps it and change it.",
            """
            Expect(Shop.Tax.Rate == 0.25m, $"rate {Shop.Tax.Rate}");
            """),
        new("catalog", "The catalogue's price for the SKU tea-217 is wrong: it must be 4.75. Fix only that price.",
            """
            Expect(Shop.Catalog.PriceOf("tea-217") == 4.75m, $"tea-217 {Shop.Catalog.PriceOf("tea-217")}");
            Expect(Shop.Catalog.PriceOf("tea-216") == 2.16m, "tea-216 unchanged");
            Expect(Shop.Catalog.PriceOf("tea-218") == 2.18m, "tea-218 unchanged");
            """),
        new("discount", "Add a method `Discount(decimal percent)` to the cart: the subtotal less that percent, the percent kept between 0 and 100.",
            """
            var cart = new Shop.Cart();
            cart.AddItem("a", 2, 10m);
            Expect(cart.Discount(25m) == cart.Subtotal * 0.75m, $"25% {cart.Discount(25m)}");
            Expect(cart.Discount(150m) == 0m, "150% is 100%");
            Expect(cart.Discount(-5m) == cart.Subtotal, "-5% is 0%");
            """),
        new("report", "The cart report must list its lines sorted by SKU (now they come in the order added). Fix the report.",
            """
            var cart = new Shop.Cart();
            cart.AddItem("zz", 1, 1m);
            cart.AddItem("aa", 2, 3m);
            var text = Shop.Report.Format(cart);
            Expect(text.IndexOf("aa", StringComparison.Ordinal) >= 0 && text.IndexOf("aa", StringComparison.Ordinal) < text.IndexOf("zz", StringComparison.Ordinal), text);
            """),
        new("rename", "Rename the cart's method AddItem to Add, everywhere it is used, so the project still builds.",
            """
            var cart = new Shop.Cart();
            cart.Add("a", 3, 2.50m);
            Expect(cart.Lines.Count == 1, "a line added");
            Expect(typeof(Shop.Cart).GetMethod("AddItem") is null, "AddItem is gone");
            """),
    ];

    /// <summary>The shop as each arm starts it: its project, its code with the tasks' bugs, a long catalogue, and helpers.</summary>
    public static Dictionary<string, string> Shop()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["shop.csproj"] = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """,
            ["Program.cs"] = """
                var cart = new Shop.Cart();
                cart.AddItem("tea-217", 2, Shop.Catalog.PriceOf("tea-217"));
                cart.AddItem("cup-3", 1, Shop.PriceParser.Parse("4.20"));
                Console.WriteLine(Shop.Report.Format(cart));
                """,
            ["src/Cart.cs"] = """
                namespace Shop;

                /// <summary>What the person is buying: lines of an item, how many, and its price each.</summary>
                public sealed class Cart
                {
                    private readonly List<CartLine> _lines = [];

                    public IReadOnlyList<CartLine> Lines => _lines;

                    public void AddItem(string sku, int count, decimal unitPrice)
                    {
                        if (count <= 0)
                        {
                            throw new ArgumentOutOfRangeException(nameof(count));
                        }
                        _lines.Add(new CartLine(sku, count, unitPrice));
                    }

                    /// <summary>The lines' prices together, before tax.</summary>
                    public decimal Subtotal => _lines.Sum(l => l.Count + l.UnitPrice);

                    /// <summary>The subtotal with tax.</summary>
                    public decimal Total => Math.Round(Subtotal * (1 + Tax.Rate), 2);
                }

                public sealed record CartLine(string Sku, int Count, decimal UnitPrice)
                {
                    public decimal Amount => Count * UnitPrice;
                }
                """,
            ["src/Tax.cs"] = """
                namespace Shop;

                /// <summary>The tax the shop adds.</summary>
                public static class Tax
                {
                    /// <summary>Value added tax, as a share.</summary>
                    public const decimal Rate = 0.20m;
                }
                """,
            ["src/PriceParser.cs"] = """
                using System.Globalization;

                namespace Shop;

                /// <summary>Prices as people type them.</summary>
                public static class PriceParser
                {
                    public static decimal Parse(string text)
                    {
                        var trimmed = text.Trim();
                        if (!decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || trimmed.Contains(','))
                        {
                            throw new FormatException($"Not a price: {text}");
                        }
                        return price;
                    }
                }
                """,
            ["src/Report.cs"] = """
                using System.Text;

                namespace Shop;

                /// <summary>The cart as a text the person reads.</summary>
                public static class Report
                {
                    public static string Format(Cart cart)
                    {
                        var sb = new StringBuilder();
                        foreach (var line in cart.Lines)
                        {
                            sb.AppendLine($"{line.Sku} x {line.Count} = {line.Amount:0.00}");
                        }
                        sb.AppendLine($"Subtotal {cart.Subtotal:0.00}");
                        sb.AppendLine($"Total {cart.Total:0.00}");
                        return sb.ToString();
                    }
                }
                """,
        };
        // A catalogue of 900 lines: each SKU's price, tea-217's wrong.
        var catalog = new StringBuilder("namespace Shop;\n\n/// <summary>The shop's prices by SKU.</summary>\npublic static class Catalog\n{\n    public static decimal PriceOf(string sku) => sku switch\n    {\n");
        foreach (var kind in new[] { "tea", "cup", "pot" })
        {
            for (var i = 1; i <= 300; i++)
            {
                var price = kind == "tea" && i == 217 ? "21.70m" : (i / 100m).ToString("0.00", CultureInfo.InvariantCulture) + "m";
                catalog.Append(CultureInfo.InvariantCulture, $"        \"{kind}-{i}\" => {price},\n");
            }
        }
        catalog.Append("        _ => throw new KeyNotFoundException(sku),\n    };\n}\n");
        files["src/Catalog.cs"] = catalog.ToString();
        // Helpers that grep and listings find too: the shop's other code.
        for (var n = 1; n <= 8; n++)
        {
            var helper = new StringBuilder($"namespace Shop.Util;\n\n/// <summary>Helpers of part {n}.</summary>\npublic static class Helpers{n}\n{{\n");
            for (var m = 1; m <= 25; m++)
            {
                helper.Append(CultureInfo.InvariantCulture, $"    /// <summary>Rounds an amount for part {n}, way {m}.</summary>\n    public static decimal Round{m}(decimal amount) => Math.Round(amount * {m}m / {m}m, 2);\n\n");
            }
            files[$"src/Util/Helpers{n}.cs"] = helper.Append("}\n").ToString();
        }
        return files;
    }

    /// <summary>The check's program: the shop's code (src/) built beside it, the task's lines run; exit 0 when they hold.</summary>
    private static async Task<(bool Passed, string Said)> CheckAsync(string work, BenchTask task, string root)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, "check-" + task.Id + "-" + Guid.NewGuid().ToString("N")[..6])).FullName;
        File.WriteAllText(Path.Combine(dir, "check.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="Check.cs" />
                <Compile Include="{work}/src/**/*.cs" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Check.cs"), "var failed = 0;\nvoid Expect(bool ok, string what) { if (!ok) { failed++; Console.WriteLine(\"FAILED: \" + what); } }\n" +
            task.Check + "\nreturn failed == 0 ? 0 : 1;\n");
        var start = new ProcessStartInfo("dotnet", ["run", "--project", dir]) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        return (p.ExitCode == 0, Fmt.OneLine((await output) + (await error), 400));
    }

    private static string Env(string name, string otherwise) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : otherwise;

    [BenchFact]
    public async Task A_long_session_of_coding_tasks_before_and_after_condensing_and_mending()
    {
        var url = Env("CODE_ARENA_BENCH_URL", "");
        var model = Env("CODE_ARENA_BENCH_MODEL", "bench");
        var context = int.Parse(Env("CODE_ARENA_BENCH_CONTEXT", "32768"), CultureInfo.InvariantCulture);
        var arms = Env("CODE_ARENA_BENCH_ARMS", "before,after").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var runs = int.Parse(Env("CODE_ARENA_BENCH_RUNS", "1"), CultureInfo.InvariantCulture);
        var only = Env("CODE_ARENA_BENCH_TASKS", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        var results = new JsonArray();
        for (var run = 1; run <= runs; run++)
        {
            foreach (var arm in arms)
            {
                results.Add(await ArmAsync(url, model, context, arm, run, [.. Tasks.Where(t => only.Count == 0 || only.Contains(t.Id))]));
            }
        }
        var report = new JsonObject
        {
            ["model"] = model, ["context"] = context, ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ["arms"] = results,
        };
        if (Environment.GetEnvironmentVariable("CODE_ARENA_BENCH_OUT") is { Length: > 0 } output)
        {
            File.WriteAllText(output, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        log.WriteLine(report.ToJsonString());
    }

    private async Task<JsonObject> ArmAsync(string url, string model, int context, string arm, int run, BenchTask[] tasks)
    {
        using var h = new Harness(_unused, null, c =>
        {
            c.Remove("url");
            c["gateway"] = url;
            c["apiKey"] = "bench";
            c["context"] = context;
            c["autoCommit"] = false;
            c["syncChats"] = false;
            c["planCritique"] = false;
            c["thinking"] = "off";
            c["condenseResults"] = arm != "before";
        });
        foreach (var (path, text) in Shop())
        {
            var full = Path.Combine(h.Work, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        var output = new StringWriter();
        var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
        await using var rt = await Runtime.StartAsync(new Options { Model = model, Mode = "yolo" }, env, new Ui(env.In, output, output, false, false), CancellationToken.None);
        rt.Agent.RepairCalls = arm != "before";
        var done = new JsonArray();
        foreach (var task in tasks)
        {
            var spend = new Spend();
            var clock = Stopwatch.StartNew();
            string? problem = null;
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                await rt.Agent.RunAsync(task.Prompt, spend, limit.Token);
            }
            catch (Exception e) when (e is GatewayException or OperationCanceledException or InvalidOperationException)
            {
                problem = Fmt.OneLine(e.Message, 300);
            }
            var seconds = clock.Elapsed.TotalSeconds;
            var (passed, said) = await CheckAsync(h.Work, task, h.Root);
            var row = new JsonObject
            {
                ["task"] = task.Id, ["passed"] = passed, ["prompt"] = spend.Prompt, ["cached"] = spend.Cached, ["completion"] = spend.Completion,
                ["requests"] = spend.Requests, ["seconds"] = Math.Round(seconds, 1), ["toolChars"] = spend.ToolChars,
                ["check"] = passed ? null : said, ["problem"] = problem, ["window"] = rt.Agent.Estimate(),
            };
            done.Add(row);
            log.WriteLine($"{arm} #{run} {task.Id}: {(passed ? "pass" : "FAIL")} prompt {spend.Prompt:N0} ({spend.Cached:N0} cached), {spend.Requests} requests, {seconds:0}s {problem}");
        }
        var stats = rt.Agent.CallStats.ToJson();
        stats["condensed"] = new JsonObject
        {
            ["summarised"] = rt.Agent.CondensedSoFar.Summarised, ["outlined"] = rt.Agent.CondensedSoFar.Outlined, ["dropped"] = rt.Agent.CondensedSoFar.Dropped,
            ["freedChars"] = rt.Agent.CondensedSoFar.Freed,
        };
        return new JsonObject { ["arm"] = arm, ["run"] = run, ["tasks"] = done, ["calls"] = stats };
    }

    /// <summary>
    /// The same long session with its model's steps given (a fake gateway), the tools real on the shop: each task reads the
    /// cart, greps, reads the catalogue's outline and a part, lists two helpers through the shell, edits the cart and reads
    /// it again. What the requests carried, with condensing off and on: the measurement that needs no GPU.
    /// </summary>
    [Fact]
    public async Task A_replayed_long_session_carries_less_with_condensing_and_the_last_task_alike()
    {
        var sizes = new Dictionary<bool, (long Total, long Last, int Requests)>();
        foreach (var condense in new[] { false, true })
        {
            using var gateway = new FakeGateway { Context = 262_144 };
            using var h = new Harness(gateway, null, c => { c.Remove("url"); c["condenseResults"] = condense; c["autoCommit"] = false; c["syncChats"] = false; });
            foreach (var (path, text) in Shop())
            {
                var full = Path.Combine(h.Work, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            var steps = new[]
            {
                ("read_file", """{"path":"src/Cart.cs"}"""),
                ("grep", """{"pattern":"Rate|Subtotal","path":"src"}"""),
                ("read_file", """{"path":"src/Catalog.cs"}"""),
                ("read_file", """{"path":"src/Catalog.cs","offset":200,"limit":300}"""),
                ("run_shell", """{"command":"cat src/Util/Helpers1.cs src/Util/Helpers2.cs"}"""),
                ("edit_file", """{"path":"src/Cart.cs","old_string":"    public IReadOnlyList<CartLine> Lines => _lines;","new_string":"    public IReadOnlyList<CartLine> Lines => _lines; // the lines"}"""),
                ("read_file", """{"path":"src/Cart.cs"}"""),
            };
            var prompts = Tasks.Select(t => t.Prompt).ToHashSet(StringComparer.Ordinal);
            gateway.Answer = req =>
            {
                var messages = req["messages"]!.AsArray();
                var asked = messages.Select((m, i) => (m, i)).Last(x => x.m!["role"]!.GetValue<string>() == "user" && prompts.Contains(x.m["content"]!.ToString())).i;
                var done = messages.Skip(asked + 1).Count(m => m!["role"]!.GetValue<string>() == "tool");
                return done < steps.Length ? new Reply($"Step {done + 1}: `Subtotal` at src/Cart.cs:20.", [steps[done]]) : Reply.Say("Done: the cart's lines at src/Cart.cs:9.");
            };
            var output = new StringWriter();
            var env = new CliEnv { In = new StringReader(""), Out = output, Err = output, Env = _ => null, Cwd = h.Work, Paths = h.Paths };
            await using var rt = await Runtime.StartAsync(new Options { Mode = "yolo" }, env, new Ui(env.In, output, output, false, false), CancellationToken.None);
            foreach (var task in Tasks)
            {
                File.WriteAllText(Path.Combine(h.Work, "src/Cart.cs"), Shop()["src/Cart.cs"]);
                await rt.Agent.RunAsync(task.Prompt, new Spend(), CancellationToken.None);
            }
            var chat = gateway.Requests.Where(r => !FakeGateway.Summarizing(r)).Select(r => (long)r["messages"]!.ToJsonString().Length).ToList();
            sizes[condense] = (chat.Sum(), chat[^1], chat.Count);
            log.WriteLine($"condense {(condense ? "on " : "off")}: {chat.Count} requests, {chat.Sum():N0} characters of messages in all, the last {chat[^1]:N0}; {rt.Agent.CondensedSoFar.Describe(3.0)}");
        }
        // Less than two thirds of what the requests carried, and of the last request.
        Assert.True(sizes[true].Total < sizes[false].Total * 2 / 3, $"{sizes[true].Total:N0} against {sizes[false].Total:N0}");
        Assert.True(sizes[true].Last < sizes[false].Last * 2 / 3, $"{sizes[true].Last:N0} against {sizes[false].Last:N0}");
        Assert.Equal(sizes[false].Requests, sizes[true].Requests);
    }

    /// <summary>The shop builds as given, and each task's check fails on it: the bugs are there to fix.</summary>
    [Fact]
    public async Task The_shop_builds_and_every_check_fails_before_its_task()
    {
        var root = Directory.CreateTempSubdirectory("bench-shop-").FullName;
        try
        {
            var work = Path.Combine(root, "work");
            foreach (var (path, text) in Shop())
            {
                var full = Path.Combine(work, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            Assert.Contains("        \"tea-217\" => 21.70m,\n", File.ReadAllText(Path.Combine(work, "src/Catalog.cs")), StringComparison.Ordinal);
            foreach (var task in Tasks.Where(t => t.Id is "subtotal" or "catalog"))
            {
                var (passed, said) = await CheckAsync(work, task, root);
                Assert.False(passed, said);
                Assert.Contains("FAILED:", said, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
