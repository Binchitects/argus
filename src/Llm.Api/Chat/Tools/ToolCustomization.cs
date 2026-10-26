using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Llm.Core.Chat;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Chat.Tools;

/// <summary>Configuration section "Tools".</summary>
public sealed class ToolOptions
{
    /// <summary>Failed calls in a row after which a server's or plugin's tool is turned off (the admins hear); 0: never.</summary>
    public int DisableAfterFailures { get; set; } = 25;
}

/// <summary>
/// Every tool call, recorded in the background (calls run side by side, sub-agents' too): for each person's calls a day, the
/// Tools page's numbers and a plugin's health. A server's or plugin's tool that keeps failing is turned off, and the admins
/// hear of it.
/// </summary>
public sealed partial class ToolUsage(IServiceScopeFactory scopes, IOptionsMonitor<ToolOptions> options, ILogger<ToolUsage> logger) : BackgroundService
{
    private readonly Channel<ToolCall> _calls = Channel.CreateUnbounded<ToolCall>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, int> _failuresInARow = new(StringComparer.Ordinal);
    // Calls not yet written, counted for quotas meanwhile.
    private readonly ConcurrentDictionary<(Guid, string), int> _pending = new();

    public void Record(ToolCall call)
    {
        if (call.UserId is { } user)
        {
            _pending.AddOrUpdate((user, call.ToolId), 1, (_, n) => n + 1);
        }
        _calls.Writer.TryWrite(call);
    }

    /// <summary>The tool's failed calls in a row, as of now (since the app started).</summary>
    public int FailuresInARow(string toolId) => _failuresInARow.GetValueOrDefault(toolId);

    /// <summary>The person's calls of the tool since midnight (UTC), written or still waiting to be.</summary>
    public async Task<int> TodayAsync(AppDbContext db, Guid userId, string toolId, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.Date;
        return await db.ToolCalls.CountAsync(c => c.UserId == userId && c.ToolId == toolId && c.At >= since, ct) + _pending.GetValueOrDefault((userId, toolId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ToolCall>();
        while (await _calls.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
        {
            while (batch.Count < 500 && _calls.Reader.TryRead(out var call))
            {
                batch.Add(call);
            }
            try
            {
                await WriteAsync(batch, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogNotWritten(logger, batch.Count, ex.Message);
            }
            foreach (var c in batch.Where(c => c.UserId is not null))
            {
                _pending.AddOrUpdate((c.UserId!.Value, c.ToolId), 0, (_, n) => Math.Max(0, n - 1));
            }
            batch.Clear();
        }
    }

    private async Task WriteAsync(List<ToolCall> batch, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ToolCalls.AddRange(batch);
        await db.SaveChangesAsync(ct);
        var most = options.CurrentValue.DisableAfterFailures;
        foreach (var call in batch)
        {
            var row = call.Failed ? _failuresInARow.AddOrUpdate(call.ToolId, 1, (_, n) => n + 1) : _failuresInARow.AddOrUpdate(call.ToolId, 0, (_, _) => 0);
            // Only a server's, a plugin's or an admin's own tool: a built-in one failing is the stack's to fix, not to switch off.
            if (!call.Failed || most <= 0 || row < most || !(call.ToolId.StartsWith(McpServerTool.Prefix, StringComparison.Ordinal) || call.ToolId.StartsWith(CustomTools.Prefix, StringComparison.Ordinal)))
            {
                continue;
            }
            _failuresInARow[call.ToolId] = 0;
            var setting = await db.ToolSettings.SingleOrDefaultAsync(s => s.ToolId == call.ToolId, ct);
            if (setting is { Enabled: false })
            {
                continue;
            }
            if (setting is null)
            {
                setting = new ToolSetting { ToolId = call.ToolId };
                db.ToolSettings.Add(setting);
            }
            setting.Enabled = false;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await scope.ServiceProvider.GetRequiredService<Identity.Audit>().WriteAsync("tool.auto_disabled", call.ToolId, success: false, detail: $"{row} failed calls in a row; the last: {call.Error}");
            await scope.ServiceProvider.GetRequiredService<Notifications.Notifier>().ToAdminsAsync(new Notifications.News("alert",
                $"A tool was turned off: {call.ToolId}", $"{row} calls in a row failed (the last: {call.Error}). Turn it back on under Admin → Tools once it works.", "/admin/tools"), ct);
            LogDisabled(logger, call.ToolId, row);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} tool calls could not be recorded: {Reason}")]
    private static partial void LogNotWritten(ILogger logger, int count, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The tool {Tool} was turned off after {Count} failed calls in a row")]
    private static partial void LogDisabled(ILogger logger, string tool, int count);
}

/// <summary>A function's admin settings, from <see cref="ToolSetting.FunctionsJson"/>.</summary>
public sealed record FunctionSettings(string? Description, JsonObject Fixed, JsonObject Defaults)
{
    public static IReadOnlyDictionary<string, FunctionSettings> Parse(string? json)
    {
        var all = new Dictionary<string, FunctionSettings>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject root)
        {
            return all;
        }
        foreach (var (name, node) in root)
        {
            if (node is JsonObject f)
            {
                all[name] = new FunctionSettings(f["description"]?.GetValue<string>(), f["fixed"] as JsonObject ?? [], f["defaults"] as JsonObject ?? []);
            }
        }
        return all;
    }
}

/// <summary>
/// A tool's run as the admin made it (Admin → Tools → a tool → Customize): some functions not offered, some asking first,
/// descriptions of their own, arguments fixed (and not offered to the model) or filled when left out, the admin's words to the
/// model, and a person's calls a day. Each call is recorded (<see cref="ToolUsage"/>).
/// </summary>
public sealed class CustomizedRun : IToolRun
{
    private readonly IToolRun inner;
    private readonly ToolSetting setting;
    private readonly string toolId;
    private readonly Guid? userId;
    private readonly ToolUsage usage;
    private readonly Func<CancellationToken, Task<int>>? today;
    private readonly IReadOnlyDictionary<string, FunctionSettings> functions;

    public CustomizedRun(IToolRun inner, ToolSetting setting, string toolId, Guid? userId, ToolUsage usage, Func<CancellationToken, Task<int>>? today)
    {
        this.inner = inner;
        this.setting = setting;
        this.toolId = toolId;
        this.userId = userId;
        this.usage = usage;
        this.today = today;
        functions = FunctionSettings.Parse(setting.FunctionsJson);
        Functions = new JsonArray([.. inner.Functions.OfType<JsonObject>().Where(f => !setting.DisabledFunctions.Contains(NameOf(f))).Select(f => (JsonNode)Shape(f))]);
        Instructions = setting.Instructions is { Length: > 0 } words
            ? setting.ReplaceInstructions ? words : $"{inner.Instructions}\n{words}".Trim()
            : inner.Instructions;
    }

    public JsonArray Functions { get; }
    public string? Instructions { get; }

    public bool AsksFirst(string name) => setting.AskFirstFunctions.Contains(name) || inner.AsksFirst(name);

    private static string NameOf(JsonObject f) => f["function"]?["name"]?.GetValue<string>() ?? "";

    /// <summary>The function as the model sees it: the admin's description, and no fixed argument to fill.</summary>
    private JsonObject Shape(JsonObject f)
    {
        if (!functions.TryGetValue(NameOf(f), out var custom))
        {
            return (JsonObject)f.DeepClone();
        }
        var copy = (JsonObject)f.DeepClone();
        var fn = (JsonObject)copy["function"]!;
        if (custom.Description is { Length: > 0 } d)
        {
            fn["description"] = d;
        }
        if (fn["parameters"] is JsonObject parameters)
        {
            if (parameters["properties"] is JsonObject props)
            {
                foreach (var (arg, _) in custom.Fixed)
                {
                    props.Remove(arg);
                }
                foreach (var (arg, value) in custom.Defaults)
                {
                    if (props[arg] is JsonObject p)
                    {
                        p["description"] = $"{p["description"]?.GetValue<string>()} (default {value?.ToJsonString()})".Trim();
                    }
                }
            }
            if (parameters["required"] is JsonArray required)
            {
                parameters["required"] = new JsonArray([.. required.Where(r => r is not null && !custom.Fixed.ContainsKey(r.GetValue<string>()) && !custom.Defaults.ContainsKey(r.GetValue<string>()))
                    .Select(r => r!.DeepClone())]);
            }
        }
        return copy;
    }

    public async Task<ToolResult> CallAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        if (setting.DisabledFunctions.Contains(name))
        {
            return new ToolResult($"{name} is turned off here.", IsError: true);
        }
        if (setting.CallsPerDay is > 0 and var most && today is not null && await today(ct) >= most)
        {
            return new ToolResult($"The person has used this tool {most} times today, the most a day here: answer without it, and say so.", IsError: true);
        }
        if (functions.TryGetValue(name, out var custom))
        {
            arguments = (JsonObject)arguments.DeepClone();
            foreach (var (arg, value) in custom.Defaults)
            {
                if (arguments[arg] is null)
                {
                    arguments[arg] = value?.DeepClone();
                }
            }
            foreach (var (arg, value) in custom.Fixed)
            {
                arguments[arg] = value?.DeepClone();
            }
        }
        var clock = Stopwatch.StartNew();
        ToolResult result;
        try
        {
            result = await inner.CallAsync(name, arguments, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            usage.Record(new ToolCall { ToolId = toolId, Function = name, UserId = userId, DurationMs = (int)clock.ElapsedMilliseconds, Failed = true, Error = Cut(ex.Message) });
            throw;
        }
        usage.Record(new ToolCall
        {
            ToolId = toolId, Function = name, UserId = userId, DurationMs = (int)clock.ElapsedMilliseconds, Failed = result.IsError, Error = result.IsError ? Cut(result.Text) : null,
        });
        return result;
    }

    private static string Cut(string text) => text.Length > 500 ? text[..499] + "…" : text;
}
