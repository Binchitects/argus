using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// A tool the person or the project defines locally: a command Code Arena runs with the call's arguments as JSON on its
/// input (and each one as ARENA_ARG_NAME), its JSON schema what the model fills in, how it asks as the built-in tools of its
/// kind do (read: never; edit: as file edits; shell, the default: as commands).
/// <code>
/// { "name": "deploy_preview", "description": "Deploys this branch to a preview and says its address.",
///   "command": ["./scripts/deploy-preview.sh"], "kind": "shell", "timeout": 300,
///   "parameters": { "type": "object", "properties": { "env": { "type": "string", "enum": ["dev", "qa"] } }, "required": ["env"] } }
/// </code>
/// </summary>
internal sealed record OwnToolFile(string Name, string Description, JsonObject Parameters, IReadOnlyList<string>? Argv, string? Shell, ToolKind Kind, int Timeout,
    string File, string Source, string Origin)
{
    /// <summary>What the person enables: its definition, so a repository that changes its command after has it enabled again first.</summary>
    public string Stamp => OwnTools.Hash($"{Name}\n{Kind}\n{string.Join('\0', Argv ?? [])}\n{Shell}\n{Json.Line(Parameters)}");
}

/// <summary>An MCP server of the project's (Claude Code's .mcp.json at its root, or .arena/mcp.json), enabled per project as Claude Code asks.</summary>
internal sealed record ProjectServer(McpServerConfig Server, string File)
{
    public string Stamp => OwnTools.Hash($"{Server.Name}\n{Server.Command}\n{string.Join('\0', Server.Args)}\n{Server.Url}\n{string.Join('\0', Server.Env.Select(e => e.Key + "=" + e.Value))}");
}

/// <summary>
/// The tools of the person's and the project's own: command tools in tools/NAME.json (the project's .code-arena/tools and
/// .arena/tools; the person's config folder's tools and ~/.code-arena/tools) and the project's MCP servers (.mcp.json, as
/// Claude Code's; .arena/mcp.json). The person's run wherever they are; the project's, from a repository someone may have
/// cloned, only once the person enabled them for this project (the IDE's Configure Tools, /tools enable NAME), and again
/// when their command changes. Their calls ask as the built-in tools of their kind do.
/// </summary>
internal static partial class OwnTools
{
    public const int MaxFiles = 100;
    public const int MaxOutput = 30_000;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex ToolName();

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    private static readonly string[] ProjectFolders = [".code-arena", ".arena"];

    /// <summary>The command tools found, the project's first (they win by name over the person's), and what could not be read.</summary>
    public static (List<OwnToolFile> Tools, List<string> Problems) Load(AppPaths paths, string projectRoot)
    {
        var folders = ProjectFolders.Select(f => (Path.Combine(projectRoot, f, "tools"), "project", $"{f}/tools"))
            .Append((Path.Combine(paths.ConfigDir, "tools"), "person", "config"))
            .Concat(paths.Home is { Length: > 0 } home ? [(Path.Combine(home, ".code-arena", "tools"), "person", "~/.code-arena/tools")] : []);
        var tools = new List<OwnToolFile>();
        var problems = new List<string>();
        foreach (var (dir, source, origin) in folders.Where(f => projectRoot.Length > 0 || f.Item2 == "person").Where(f => Directory.Exists(f.Item1)))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).Take(MaxFiles))
            {
                try
                {
                    if (new FileInfo(file).Length > 100_000)
                    {
                        problems.Add($"{file}: over 100 KB, not read");
                        continue;
                    }
                    var tool = Parse(Json.ParseObject(File.ReadAllText(file)), file, source, origin, out var problem);
                    if (tool is null)
                    {
                        problems.Add($"{file}: {problem}");
                    }
                    else if (!tools.Any(t => t.Name == tool.Name))
                    {
                        tools.Add(tool);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    problems.Add($"{file}: {e.Message}");
                }
            }
        }
        return (tools, problems);
    }

    /// <summary>A tool file read, or null with why it will not do.</summary>
    public static OwnToolFile? Parse(JsonObject? o, string file, string source, string origin, out string? problem)
    {
        problem = null;
        if (o is null)
        {
            problem = "not a JSON object";
            return null;
        }
        var name = o.Str("name") ?? Path.GetFileNameWithoutExtension(file);
        if (!ToolName().IsMatch(name))
        {
            problem = $"\"{name}\" is not a tool's name: a letter, then letters, digits, _ or -, up to 64";
            return null;
        }
        if (o.Str("description") is not { Length: > 0 } description)
        {
            problem = "say what it does in \"description\": the model reads it to choose the tool";
            return null;
        }
        IReadOnlyList<string>? argv = o["command"] is JsonArray a ? [.. a.Select(x => x?.ToString() ?? "")] : null;
        var shell = o["command"] is JsonValue ? o.Str("command") : null;
        if (argv is not { Count: > 0 } && shell is not { Length: > 0 } || argv?.Any(x => x.Length == 0) == true)
        {
            problem = "\"command\" is the program and its arguments ([\"python3\", \"tools/x.py\"]) or a line for the shell";
            return null;
        }
        var parameters = o["parameters"] as JsonObject ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        if (parameters.Str("type") is { } type && type != "object" || parameters["properties"] is not (null or JsonObject))
        {
            problem = "\"parameters\" is a JSON schema of an object: { \"type\": \"object\", \"properties\": { … } }";
            return null;
        }
        parameters = parameters.Clone();
        parameters["type"] = "object";
        parameters["properties"] ??= new JsonObject();
        ToolKind known;
        switch (o.Str("kind"))
        {
            case "read":
                known = ToolKind.Read;
                break;
            case "edit":
                known = ToolKind.Edit;
                break;
            case null or "shell":
                known = ToolKind.Shell;
                break;
            case var other:
                problem = $"\"kind\" is read, edit or shell, not {other}";
                return null;
        }
        return new OwnToolFile(name, description, parameters, argv, shell, known, Math.Clamp(o.Int("timeout") ?? 120, 1, 600), file, source, origin);
    }

    /// <summary>The project's MCP servers: Claude Code's .mcp.json at its root and .arena/mcp.json ({"mcpServers": {NAME: {...}}}).</summary>
    public static (List<ProjectServer> Servers, List<string> Problems) ProjectServers(string projectRoot)
    {
        var servers = new List<ProjectServer>();
        var problems = new List<string>();
        if (projectRoot.Length == 0)
        {
            return (servers, problems);
        }
        foreach (var file in new[] { Path.Combine(projectRoot, ".mcp.json"), Path.Combine(projectRoot, ".arena", "mcp.json") }.Where(File.Exists))
        {
            try
            {
                var o = Json.ParseObject(File.ReadAllText(file));
                foreach (var (name, node) in o?["mcpServers"] as JsonObject ?? [])
                {
                    if (McpServerConfig.From(name, node) is { } server && !servers.Any(s => s.Server.Name == name))
                    {
                        // A repository's server never trusts itself: each of its calls asks (but in yolo).
                        servers.Add(new ProjectServer(new McpServerConfig
                        {
                            Name = server.Name, Command = server.Command, Args = server.Args, Env = server.Env, Cwd = server.Cwd, Url = server.Url, Headers = server.Headers,
                            Disabled = server.Disabled,
                        }, file));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                problems.Add($"{file}: {e.Message}");
            }
        }
        return (servers, problems);
    }

    /// <summary>A command tool as the model calls it: the command run with the call's arguments, asking as tools of its kind do.</summary>
    public static ToolDef Def(OwnToolFile t, string projectRoot) => new()
    {
        Name = t.Name,
        Kind = t.Kind,
        Description = t.Description,
        Parameters = t.Parameters.Clone(),
        // Shown as the command it runs: what the person approves.
        Summary = a => $"{(t.Argv is { } argv ? string.Join(' ', argv) : t.Shell)} {Fmt.OneLine(Json.Line(a), 80)}",
        Origin = t.Origin,
        Run = (a, c, ct) => RunAsync(t, a, c, t.Source == "project" ? projectRoot : c.Workspace.Root, ct),
    };

    private static async Task<ToolResult> RunAsync(OwnToolFile t, JsonObject args, ToolContext c, string folder, CancellationToken ct)
    {
        ProcessStartInfo psi;
        if (t.Argv is { } argv)
        {
            psi = new ProcessStartInfo(argv[0]) { WorkingDirectory = folder };
            foreach (var arg in argv.Skip(1))
            {
                psi.ArgumentList.Add(arg);
            }
            psi = c.Sandbox.Wrap(psi);
        }
        else
        {
            psi = LocalTools.ShellCommand(t.Shell!, c);
            psi.WorkingDirectory = folder;
        }
        // Each argument as its own variable too (ARENA_ARG_ENV), for a script that reads them so; never on its command line.
        foreach (var (key, value) in args)
        {
            if (value is JsonValue v && Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                psi.Environment["ARENA_ARG_" + key.ToUpperInvariant()] = v.ToString();
            }
        }
        psi.Environment["ARENA_TOOL"] = t.Name;
        Proc.PipeResult run;
        try
        {
            run = await Proc.PipeAsync(psi, Json.Line(args), TimeSpan.FromSeconds(t.Timeout), MaxOutput, ct);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ToolError($"{t.Name} could not start {psi.FileName}: {e.Message}");
        }
        var status = run.TimedOut ? $"timed out after {t.Timeout} s (stopped)" : run.TooLong ? $"stopped: it printed more than {MaxOutput:N0} characters" : $"exit code {run.ExitCode}";
        var text = (run.Output.TrimEnd() + (run.Error.Trim().Length > 0 ? "\n[stderr]\n" + run.Error.TrimEnd() : "")).Trim();
        var failed = run.ExitCode != 0 || run.TimedOut || run.TooLong;
        return new ToolResult((text.Length > 0 ? text + "\n" : "(no output)\n") + $"[{status}]", failed)
        {
            Display = (text.Length > 0 ? Fmt.Tail(text, 6) + "\n" : "") + (failed ? c.Ui.Red(status) : c.Ui.Dim(status)),
        };
    }
}

/// <summary>
/// What the person keeps for one project on this computer, never in the project: the project's own tools and MCP servers
/// they enabled (each by its definition's stamp), and their tool settings for this project. One file per project in the
/// data folder (a repository cannot enable its own tools).
/// </summary>
internal sealed class ProjectToolState(string dataDir, string root)
{
    private readonly object _gate = new();

    public string File { get; } = Path.Combine(dataDir, "projects", OwnTools.Hash(root) + ".json");

    public JsonObject Read()
    {
        lock (_gate)
        {
            try
            {
                return System.IO.File.Exists(File) ? Json.ParseObject(System.IO.File.ReadAllText(File)) ?? [] : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return [];
            }
        }
    }

    private void Write(JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        PrivateFiles.WriteAllText(File, state.ToJsonString(Json.Indented) + "\n");
    }

    /// <summary>Whether the person enabled this (tool:NAME, mcp:NAME) as it is defined now.</summary>
    public bool Enabled(string key, string stamp) => (Read()["enabled"] as JsonObject)?.Str(key) == stamp;

    /// <summary>Enables it as it is defined now (its stamp), or forgets it (null).</summary>
    public void Enable(string key, string? stamp)
    {
        lock (_gate)
        {
            var state = Read();
            var enabled = state["enabled"] as JsonObject ?? [];
            enabled.Remove(key);
            if (stamp is not null)
            {
                enabled[key] = stamp;
            }
            state["enabled"] = enabled.Count == 0 ? null : enabled;
            if (state["enabled"] is null)
            {
                state.Remove("enabled");
            }
            Write(state);
        }
    }

    /// <summary>The person's settings for the tools in this project, by tool name.</summary>
    public JsonObject Tools => Read()["tools"] as JsonObject ?? [];

    public void SetTools(JsonObject tools)
    {
        lock (_gate)
        {
            var state = Read();
            if (tools.Count == 0)
            {
                state.Remove("tools");
            }
            else
            {
                state["tools"] = tools.DeepClone();
            }
            Write(state);
        }
    }
}
