using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>
/// The providers' own coding agents, run as the person: Claude Code (claude), Codex CLI (codex) and Gemini CLI (gemini),
/// each signed in on this machine with the person's own subscription (Claude Pro or Max, ChatGPT Plus or Pro, Gemini).
/// Code Arena never reads or relays those sign-ins: it runs the provider's program, non-interactively, in the working
/// directory, and reads what it prints; the provider's terms and limits apply as when the person runs it.
/// </summary>
internal static class ProviderAgents
{
    /// <summary>The command lines before the task, by name, when the config does not say otherwise.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Defaults = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["claude"] = ["claude", "-p"],
        ["codex"] = ["codex", "exec"],
        ["gemini"] = ["gemini", "-p"],
    };

    /// <summary>What each name is for the person: its provider and how one signs in.</summary>
    public static readonly IReadOnlyDictionary<string, string> About = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["claude"] = "Claude Code (Anthropic: a Claude Pro or Max subscription, signed in with `claude` then /login)",
        ["codex"] = "Codex CLI (OpenAI: a ChatGPT Plus or Pro subscription, signed in with `codex login`)",
        ["gemini"] = "Gemini CLI (Google: signed in with `gemini` and its Google sign-in)",
    };

    /// <summary>The agents here: the config's (an empty line turns one off), and the defaults whose program is on PATH.</summary>
    public static Dictionary<string, string[]> Available(Config config, Func<string, string?> env)
    {
        var found = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, line) in Defaults)
        {
            if (!config.Agents.ContainsKey(name) && OnPath(line[0], env) is { } exe)
            {
                found[name] = [exe, .. line[1..]];
            }
        }
        foreach (var (name, line) in config.Agents.Where(a => a.Value.Count > 0))
        {
            found[name] = [.. line];
        }
        return found;
    }

    private static string? OnPath(string program, Func<string, string?> env)
    {
        var exts = OperatingSystem.IsWindows() ? (env("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [""];
        foreach (var dir in (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var file = Path.Combine(dir, program + ext);
                if (File.Exists(file))
                {
                    return file;
                }
            }
        }
        return null;
    }

    /// <summary>The ask_agent tool, when any agent is here; null otherwise.</summary>
    public static ToolDef? Tool(Config config, Func<string, string?> env)
    {
        var agents = Available(config, env);
        if (agents.Count == 0)
        {
            return null;
        }
        var names = string.Join(", ", agents.Keys.Select(n => About.TryGetValue(n, out var a) ? $"{n} ({a})" : n));
        return new ToolDef
        {
            Name = "ask_agent",
            Kind = ToolKind.Shell,
            Description = $"Hand a task to one of the person's own coding agents, which runs it in the working directory with their own subscription and says what it did: {names}. " +
                "Give it the whole task as you would to a colleague: what to do, where, and how to check it. Use it when the person asks for that agent, or for a second opinion.",
            Parameters = LocalTools.Schema("""
                {"type":"object","properties":{
                  "agent":{"type":"string","description":"Which agent: its name."},
                  "task":{"type":"string","description":"The whole task."}},
                 "required":["agent","task"]}
                """),
            Summary = a => $"{a.Str("agent")}: {Fmt.OneLine(a.Str("task"), 60)}",
            Run = (args, ctx, ct) => RunAsync(agents, args.Str("agent") ?? "", args.Str("task") ?? "", ctx.Workspace.Root, ct),
        };
    }

    /// <summary>The most an agent's output that comes back to the model.</summary>
    public const int MaxChars = 30_000;

    public static async Task<ToolResult> RunAsync(IReadOnlyDictionary<string, string[]> agents, string name, string task, string folder, CancellationToken ct,
        Action<string>? line = null)
    {
        if (!agents.TryGetValue(name, out var command))
        {
            return new ToolResult($"There is no agent {name} here. Here: {string.Join(", ", agents.Keys)}.", true);
        }
        if (task.Trim().Length == 0)
        {
            return new ToolResult("Give the agent its task.", true);
        }
        var start = new ProcessStartInfo(command[0]) { WorkingDirectory = folder, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in command[1..])
        {
            start.ArgumentList.Add(a);
        }
        start.ArgumentList.Add(task);
        // Never the person's Arena key: the agent signs in with its own.
        start.Environment.Remove("ARENA_API_KEY");
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ToolResult($"{name} could not start: {e.Message}", true);
        }
        using (process)
        {
            process.StandardInput.Close();
            var output = new StringBuilder();
            var reading = Task.Run(async () =>
            {
                while (await process.StandardOutput.ReadLineAsync(ct) is { } l)
                {
                    lock (output)
                    {
                        output.AppendLine(l);
                    }
                    line?.Invoke(l);
                }
            }, ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
                await reading;
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            var said = output.ToString().Trim();
            if (said.Length > MaxChars)
            {
                said = "[…]\n" + said[^MaxChars..];
            }
            var err = (await errors).Trim();
            if (process.ExitCode != 0)
            {
                return new ToolResult($"{name} ended with code {process.ExitCode}." + (said.Length > 0 ? $"\n\n{said}" : "") + (err.Length > 0 ? $"\n\nIts errors:\n{Fmt.Head(err, 20, 400)}" : "") +
                    "\n\n(If it is not signed in, the person signs in with it once, in a terminal.)", true);
            }
            return new ToolResult(said.Length > 0 ? $"{name} says:\n\n{said}" : $"{name} finished and said nothing.");
        }
    }
}
