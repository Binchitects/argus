using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// A plugin's own MCP server (tools: sandbox: folder), run in the Python sandbox: no network, a read-only root, its own
/// user, its time limit (services/sandbox/runner.py). Each list and each call is one short job: the server's files and
/// its settings (settings.json) go in, and it reads initialize and the one request from its input and answers them. Its
/// tools' list is kept per version, so a chat starts it without waiting.
/// </summary>
public sealed class SandboxServerTool(McpServer server, SandboxClient sandbox, IServiceScopeFactory scopes, string? dataKey, IOptionsMonitor<SandboxOptions> options) : IServerTool
{
    private static readonly ConcurrentDictionary<(Guid, string?), (JsonArray Tools, string? Instructions)> Listed = new();

    public McpServer Server => server;
    public string Id => McpServerTool.Prefix + server.Id;
    public string Title => server.Name;
    public string Description => server.Description ?? $"{server.Name}'s own tools, run in the sandbox.";
    public string Icon => "plug";
    public string Slug => McpServerTool.Slugify(server.Name);

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult(sandbox.Unavailable());

    /// <summary>The server's files, from the version installed, and its settings as settings.json.</summary>
    private async Task<List<SandboxFile>> FilesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var release = await db.PluginReleases.AsNoTracking().Where(r => r.ServerId == server.Id).OrderByDescending(r => r.InstalledAt).FirstOrDefaultAsync(ct)
            ?? throw new McpException($"{server.Name}'s files are not here: install it again.");
        var package = Plugins.PluginCatalog.FromFiles(JsonSerializer.Deserialize<Dictionary<string, string>>(release.FilesJson) ?? []);
        var files = package.SandboxFiles.Select(f => new SandboxFile(f.Key, Encoding.UTF8.GetBytes(f.Value))).ToList();
        var settings = new JsonObject(Plugins.PluginInstaller.Values(server, dataKey).Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        files.Add(new SandboxFile("settings.json", Encoding.UTF8.GetBytes(settings.ToJsonString())));
        return files;
    }

    /// <summary>
    /// The job: the server's main run with initialize and the request as its input, its answers on its output. The
    /// messages are data (a JSON string), never code.
    /// </summary>
    public static string Job(JsonObject request)
    {
        var messages = new JsonArray(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "arena", ["version"] = "1" } } },
            new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
            request);
        var input = string.Concat(messages.Select(m => m!.ToJsonString() + "\n"));
        return "import io, os, runpy, sys\n" +
               "sys.path.insert(0, os.getcwd())\n" +
               $"sys.stdin = io.StringIO({JsonSerializer.Serialize(input)})\n" +
               "sys.argv = ['server.py']\n" +
               "runpy.run_path('server.py', run_name='__main__')\n";
    }

    /// <summary>One request answered by the server in the sandbox: its result (and the server's instructions), or why not.</summary>
    private async Task<(JsonObject Result, string? Instructions)> AskAsync(JsonObject request, CancellationToken ct)
    {
        var result = await sandbox.RunAsync(Job(request), await FilesAsync(ct), server.CallTimeoutMinutes is { } m ? m * 60 : options.CurrentValue.TimeoutSeconds, ct);
        if (result.Error is { } error)
        {
            throw new McpException($"{server.Name} could not run in the sandbox: {error}");
        }
        if (result.TimedOut)
        {
            throw new McpException($"{server.Name} did not answer in time.");
        }
        string? instructions = null;
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonObject? answer;
            try
            {
                answer = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                continue;
            }
            if (answer?["id"] is not JsonValue id || !id.TryGetValue<int>(out var number))
            {
                continue;
            }
            if (number == 1)
            {
                instructions = answer["result"]?["instructions"]?.GetValue<string>();
                continue;
            }
            if (number != 2)
            {
                continue;
            }
            if (answer["error"] is JsonObject e)
            {
                throw new McpException($"{server.Name}: {e["message"]}");
            }
            return (answer["result"] as JsonObject ?? [], instructions);
        }
        var why = result.Stderr.Trim() is { Length: > 0 } stderr ? stderr[^Math.Min(stderr.Length, 1500)..] : $"it ended (exit code {result.ExitCode}) without answering";
        throw new McpException($"{server.Name} gave no answer: {why}");
    }

    public async Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct)
    {
        var key = (server.Id, server.PluginVersion);
        if (!Listed.TryGetValue(key, out var listed))
        {
            var (answer, instructions) = await AskAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }, ct);
            listed = (answer["tools"] as JsonArray ?? [], instructions);
            Listed[key] = listed;
        }
        var prefix = Slug + "__";
        var functions = Mcp.ToOpenAiTools((JsonArray)listed.Tools.DeepClone(), name => prefix + name);
        return new LocalRun(functions, listed.Instructions, async (function, args, token) =>
        {
            var name = function.StartsWith(prefix, StringComparison.Ordinal) ? function[prefix.Length..] : function;
            try
            {
                var (result, _) = await AskAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() },
                }, token);
                var text = string.Join("\n", (result["content"] as JsonArray ?? []).OfType<JsonObject>().Select(c => c["text"]?.GetValue<string>()).OfType<string>());
                return new ToolResult(text, result["isError"]?.GetValue<bool>() == true);
            }
            catch (McpException ex)
            {
                return new ToolResult(ex.Message, true);
            }
        })
        {
            AsksFirst = function => function.StartsWith(prefix, StringComparison.Ordinal) && server.Writes.Contains(function[prefix.Length..]),
        };
    }
}
