using System.Globalization;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>code-arena index and code-arena search: the folder's code index, with no model.</summary>
internal static partial class Cli
{
    public const string IndexHelp = """
        code-arena index [--rebuild]
            Build or bring up to date this folder's code index (kept in Code Arena's data folder, never in the
            repository), then say what it holds: files, symbols, languages, the files skipped as secrets.
        code-arena search "what or name" [--path P] [--tests] [--budget N] [--json]
        code-arena search --symbol NAME [--show source|callers|callees|impact] [--depth N] [--budget N] [--json]
        code-arena search --outline [PATH] [--budget N] [--json]
            The answers the agent's code_search, code_symbol and code_outline give, here with no model.
        """;

    private static (Config? Config, Workspace? Workspace) IndexWorkspace(CliEnv env)
    {
        try
        {
            var config = Config.Load(env.Paths.ConfigFile);
            return (config, new Workspace(env.Cwd, config.AllowedPaths));
        }
        catch (InvalidOperationException e)
        {
            env.Err.WriteLine($"code-arena: {e.Message}");
            return (null, null);
        }
    }

    private static int IndexCommand(string[] args, CliEnv env, CancellationToken ct)
    {
        if (args.Any(a => a is "-h" or "--help"))
        {
            env.Out.WriteLine(IndexHelp);
            return 0;
        }
        if (args.FirstOrDefault(a => a != "--rebuild") is { } other)
        {
            env.Err.WriteLine($"code-arena: index does not take {other} (code-arena index --help)");
            return 2;
        }
        if (IndexWorkspace(env).Workspace is not { } workspace)
        {
            return 1;
        }
        var (index, sync, rebuilt) = CodeIndex.Open(env.Paths.DataDir, workspace, ct, rebuild: args.Contains("--rebuild"));
        var files = index.Files;
        var symbols = files.Sum(f => f.Symbols.Count);
        var languages = files.Where(f => f.Lang is not null).GroupBy(f => f.Lang!).OrderByDescending(g => g.Count())
            .Select(g => $"{LanguageName(g.Key)} {g.Count():N0}");
        var o = env.Out;
        o.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Code index of {workspace.Root}: {files.Count:N0} files, {symbols:N0} symbols ({string.Join(", ", languages)} files)."));
        o.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{(rebuilt ? "Built" : "Synced")} in {sync.Took.TotalSeconds:0.0} s: {sync.Read:N0} read, {sync.Kept:N0} kept, {sync.Removed:N0} removed."));
        var size = File.Exists(index.File) ? new FileInfo(index.File).Length : 0;
        o.WriteLine($"Kept in {index.File} ({Files.Size(size)}).");
        var secrets = files.Where(f => f.Tags.HasFlag(CodeFileTags.Sensitive)).Select(f => f.Path).Order(StringComparer.Ordinal).ToList();
        if (secrets.Count > 0)
        {
            o.WriteLine($"Skipped as secrets (listed, never read): {string.Join(", ", secrets.Take(20))}{(secrets.Count > 20 ? $", … {secrets.Count - 20} more" : "")}.");
        }
        return 0;
    }

    private static string LanguageName(string lang) => lang switch
    {
        "csharp" => "C#",
        "typescript" => "TypeScript",
        "javascript" => "JavaScript",
        "python" => "Python",
        "markdown" => "Markdown",
        "go" => "Go",
        "java" => "Java",
        "kotlin" => "Kotlin",
        "rust" => "Rust",
        _ => lang,
    };

    private static int SearchCommand(string[] args, CliEnv env, CancellationToken ct)
    {
        string? query = null, symbol = null, path = null, show = null;
        string? outline = null;
        var outlined = false;
        var json = false;
        var tests = false;
        int? budget = null, depth = null;
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
                int Number() => int.TryParse(Value(), CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"{a} needs a number.");
                switch (a)
                {
                    case "-h" or "--help":
                        env.Out.WriteLine(IndexHelp);
                        return 0;
                    case "--symbol":
                        symbol = Value();
                        break;
                    case "--show":
                        show = Value();
                        break;
                    case "--depth":
                        depth = Number();
                        break;
                    case "--outline":
                        outlined = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            outline = args[++i];
                        }
                        break;
                    case "--path":
                        path = Value();
                        break;
                    case "--budget":
                        budget = Number();
                        break;
                    case "--tests":
                        tests = true;
                        break;
                    case "--json":
                        json = true;
                        break;
                    default:
                        if (a.StartsWith('-'))
                        {
                            throw new ArgumentException($"search does not take {a}.");
                        }
                        query = query is null ? a : $"{query} {a}";
                        break;
                }
            }
            if ((query is null ? 0 : 1) + (symbol is null ? 0 : 1) + (outlined ? 1 : 0) != 1)
            {
                throw new ArgumentException("search needs one of: a question, --symbol NAME, --outline [PATH].");
            }
            if (show is not null and not ("source" or "callers" or "callees" or "impact"))
            {
                throw new ArgumentException("--show is source, callers, callees or impact.");
            }
        }
        catch (ArgumentException e)
        {
            env.Err.WriteLine($"code-arena: {e.Message} (code-arena search --help)");
            return 2;
        }
        if (IndexWorkspace(env).Workspace is not { } workspace)
        {
            return 1;
        }
        var (index, _, _) = CodeIndex.Open(env.Paths.DataDir, workspace, ct);
        var tokens = Math.Clamp(budget ?? (symbol is not null ? 1500 : outlined ? 1200 : 2000), 300, 4000);
        CodeAnswer answer;
        if (symbol is not null)
        {
            answer = index.Search().Symbol(new SymbolQuery(symbol) { Show = show ?? "source", Depth = depth ?? 2, Budget = tokens });
        }
        else if (outlined)
        {
            var full = outline is null ? null : workspace.Show(Path.GetFullPath(outline, env.Cwd)).Replace('\\', '/');
            answer = index.Search().Outline(full, tokens);
        }
        else
        {
            var within = path is null ? null : path.IndexOfAny(['*', '?', '[', '{']) >= 0 ? path : workspace.Show(Path.GetFullPath(path, env.Cwd)).Replace('\\', '/');
            answer = index.Search().Answer(new CodeQuery(query!) { Path = within, Tests = tests, Budget = tokens });
        }
        if (json)
        {
            var items = new JsonArray();
            foreach (var h in answer.Items)
            {
                items.Add(new JsonObject
                {
                    ["path"] = h.Path, ["start"] = h.Start, ["end"] = h.End, ["kind"] = h.Kind, ["name"] = h.Name,
                    ["why"] = new JsonArray([.. h.Why.Select(w => (JsonNode)w)]),
                });
            }
            var result = new JsonObject
            {
                ["route"] = answer.Route, ["items"] = items, ["complete"] = answer.Complete, ["shown"] = answer.Shown, ["total"] = answer.Total, ["text"] = answer.Text,
            };
            env.Out.WriteLine(result.ToJsonString(Json.Relaxed));
        }
        else
        {
            env.Out.WriteLine(answer.Text);
        }
        // A name of several symbols: the caller names one (path#Qualified) and asks again.
        return answer.Route == "symbol" && answer.Items.Count > 1 && answer.Items.All(h => h.Why is ["candidate"]) ? 3 : 0;
    }
}
