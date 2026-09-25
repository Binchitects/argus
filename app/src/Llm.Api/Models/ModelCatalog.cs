using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Access;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Core.Chat;
using Llm.Api.Operations;
using Llm.Core.Access;
using Llm.Core.Data;
using Llm.Core.Identity;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>What the engine said last: its models and which are loaded. Kept by <see cref="EngineWatcher"/>.</summary>
public sealed class EngineState
{
    private volatile Snapshot _now = new([], null, null);

    public sealed record Snapshot(IReadOnlyList<EngineModel> Models, string? Error, DateTimeOffset? At);

    public Snapshot Now => _now;

    public void Set(IReadOnlyList<EngineModel> models) => _now = new Snapshot(models, null, DateTimeOffset.UtcNow);

    public void Fail(string error) => _now = _now with { Error = error, At = DateTimeOffset.UtcNow };

    /// <summary>"loaded", "loading", "unloaded", or null when the engine does not have that model.</summary>
    public string? StatusOf(string model) => _now.Models.FirstOrDefault(m => m.Name == model)?.Status;
}

/// <summary>
/// The models the engine can load: the .env one and those admins added. Writes
/// the engine's presets (config/engine/models.ini), which model to keep loaded
/// (config/engine/active), Prometheus's scrape targets (config/engine/targets.json),
/// and keeps the gateway's list in step.
/// </summary>
public sealed partial class ModelCatalog(AppDbContext db, ILiteLlm gateway, IOptions<EngineOptions> options, ChatModels chatModels, ILogger<ModelCatalog> logger)
{
    public const string PresetsFile = "models.ini";
    public const string ActiveFile = "active";
    public const string TargetsFile = "targets.json";
    /// <summary>What <see cref="ActiveFile"/> holds when an admin unloaded every model on purpose.</summary>
    public const string None = "-";

    /// <summary>Preset keys a model may not set: they belong to the router, or would reach outside the library.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "model", "hf-repo", "hf-file", "hf", "model-url", "mmproj", "mmproj-url", "host", "port", "api-key", "api-key-file", "alias",
        "path", "ssl-key-file", "ssl-cert-file", "models-dir", "models-preset", "models-max", "models-autoload", "webui", "log-file",
        "slot-save-path", "lora", "control-vector", "model-draft", "chat-template-file", "grammar-file", "json-schema-file",
    };

    private string Dir => options.Value.ConfigDir;

    /// <summary>Why a name cannot be a new model, or null.</summary>
    public static string? CheckName(string name) =>
        NameRule().IsMatch(name) ? null : "A name is 1-100 letters, digits and . _ : - (starting with a letter or digit).";

    /// <summary>Why these extra preset lines cannot be used, or null.</summary>
    public static string? CheckExtra(string? extra)
    {
        foreach (var raw in (extra ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }
            var m = PresetLine().Match(line);
            if (!m.Success)
            {
                return $"\"{line}\" is not a preset line: write key = value, with llama-server's long option names (e.g. flash-attn = on).";
            }
            if (Reserved.Contains(m.Groups[1].Value))
            {
                return $"{m.Groups[1].Value} is set by the Models page or the engine, not in extra lines.";
            }
        }
        return null;
    }

    /// <summary>A model's section of the engine's presets.</summary>
    public static string Preset(LocalModel m, string library)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.Append('[').Append(m.Name).Append("]\n");
        sb.Append("model = ").Append(library.TrimEnd('/')).Append('/').Append(m.File).Append('\n');
        if (m.Projector is { Length: > 0 } p)
        {
            sb.Append("mmproj = ").Append(library.TrimEnd('/')).Append('/').Append(p).Append('\n');
        }
        sb.Append(inv, $"ctx-size = {m.Context}\n");
        sb.Append(inv, $"n-gpu-layers = {m.GpuLayers}\n");
        sb.Append(inv, $"n-cpu-moe = {m.CpuMoe}\n");
        sb.Append(inv, $"cache-type-k = {m.KvType}\ncache-type-v = {m.KvType}\n");
        sb.Append(inv, $"parallel = {m.Parallel}\n");
        sb.Append("jinja = true\nmetrics = true\n");
        foreach (var raw in (m.ExtraPreset ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';'))
            {
                sb.Append(line).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Writes the presets for the engine; it restarts when they change.</summary>
    public async Task WritePresetsAsync(CancellationToken ct = default)
    {
        var models = await db.LocalModels.AsNoTracking().OrderBy(m => m.Name).ToListAsync(ct);
        var text = "# Written by the app (Admin -> Models). The engine restarts when this changes.\n\n" +
            string.Join("\n", models.Select(m => Preset(m, options.Value.EngineLibraryDir)));
        Write(PresetsFile, text);
    }

    /// <summary>The model to keep loaded: an admin's last choice, the .env model, or none (<see cref="None"/>).</summary>
    public string Active() => Read(ActiveFile) is { Length: > 0 } a ? a : options.Value.DefaultModel ?? None;

    public void SetActive(string model) => Write(ActiveFile, model);

    /// <summary>Prometheus scrapes each loaded model's metrics (/metrics?model=NAME).</summary>
    public void WriteTargets(IEnumerable<string> loaded)
    {
        var host = new Uri(options.Value.Url).Authority;
        var targets = new JsonArray([.. loaded.Select(m => (JsonNode)new JsonObject
        {
            ["targets"] = new JsonArray(host),
            ["labels"] = new JsonObject { ["model"] = m, ["tier"] = "optional", ["component"] = "inference" },
        })]);
        Write(TargetsFile, targets.ToJsonString());
    }

    /// <summary>Adds the app's models to the gateway, and takes away the ones removed or changed since.</summary>
    public async Task SyncGatewayAsync(CancellationToken ct = default)
    {
        var models = await db.LocalModels.AsNoTracking().ToListAsync(ct);
        var managed = await gateway.ManagedModelsAsync(ct);
        var wanted = models.ToDictionary(m => m.Name, m => (Model: m, Fingerprint: Fingerprint(m)));
        foreach (var old in managed.Where(g => !wanted.TryGetValue(g.Name, out var w) || w.Fingerprint != g.Fingerprint))
        {
            await gateway.DeleteModelAsync(old.Id, ct);
        }
        var kept = managed.Where(g => wanted.TryGetValue(g.Name, out var w) && w.Fingerprint == g.Fingerprint).Select(g => g.Name).ToHashSet();
        foreach (var (name, w) in wanted.Where(w => !kept.Contains(w.Key)))
        {
            var m = w.Model;
            var info = new JsonObject
            {
                ["mode"] = "chat", ["max_input_tokens"] = m.Context, ["max_output_tokens"] = m.MaxOutput ?? Math.Min(m.Context, 32768),
                ["max_tokens"] = m.Context, ["supports_vision"] = m.Projector is { Length: > 0 },
                ["supports_function_calling"] = m.Tools, ["supports_reasoning"] = m.Thinking,
            };
            var litellm = new JsonObject { ["model"] = "openai/" + name, ["api_base"] = "os.environ/ENGINE_API_BASE", ["api_key"] = "os.environ/ENGINE_API_KEY" };
            if (m.InputPerMtok is { } input)
            {
                litellm["input_cost_per_token"] = input / 1_000_000m;
            }
            if (m.OutputPerMtok is { } output)
            {
                litellm["output_cost_per_token"] = output / 1_000_000m;
            }
            await gateway.AddModelAsync(name, litellm, info, w.Fingerprint, ct);
            LogRegistered(logger, name);
        }
        chatModels.Forget();
    }

    private static string Fingerprint(LocalModel m) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', m.Context, m.MaxOutput, m.Projector, m.Tools, m.Thinking, m.InputPerMtok, m.OutputPerMtok))))[..16];

    private string? Read(string file)
    {
        try
        {
            return File.ReadAllText(Path.Combine(Dir, file)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Atomic, and only when the text changes: the engine restarts on a new presets file.</summary>
    private void Write(string file, string text)
    {
        var path = Path.Combine(Dir, file);
        if (Read(file) == text.Trim())
        {
            return;
        }
        Directory.CreateDirectory(Dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,99}$")]
    private static partial Regex NameRule();

    [GeneratedRegex(@"^([a-z0-9][a-z0-9-]*)\s*=\s*([^\r\n\[\]]+)$")]
    private static partial Regex PresetLine();

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {Model} registered at the gateway")]
    private static partial void LogRegistered(ILogger logger, string model);
}

/// <summary>Who may use which model, and whether it can answer now.</summary>
public sealed class ModelPolicy(AppDbContext db, AccessService access, EngineState engine, IOptions<EngineOptions> options)
{
    /// <summary>The models on the engine: the .env one and the app's. Their answers need them loaded.</summary>
    public async Task<HashSet<string>> OnEngineAsync(CancellationToken ct = default)
    {
        if (!options.Value.Enabled)
        {
            return [];
        }
        var names = await db.LocalModels.AsNoTracking().Select(m => m.Name).ToListAsync(ct);
        if (options.Value.DefaultModel is { Length: > 0 } d)
        {
            names.Add(d);
        }
        return [.. names];
    }

    /// <summary>The names, of those given, this person may use.</summary>
    public async Task<HashSet<string>> AllowedAsync(AppUser user, IEnumerable<string> models, CancellationToken ct = default)
    {
        var member = await access.MembershipAsync(user, ct);
        var rules = await db.ModelAccess.AsNoTracking().ToDictionaryAsync(r => r.Model, ct);
        return [.. models.Where(m => !rules.TryGetValue(m, out var r) || member.May(r.Audience, r.Groups))];
    }

    /// <summary>Whether a model can answer now: always, unless it is the engine's and not loaded.</summary>
    public bool Ready(string model, IReadOnlySet<string> onEngine) =>
        !onEngine.Contains(model) || engine.Now is not { Error: null, At: not null } || engine.StatusOf(model) == "loaded";

    /// <summary>The chat's models this person may use, and the one a chat uses when it chose none: the first that is ready.</summary>
    public async Task<(IReadOnlyList<GatewayModel> Models, GatewayModel? Default, HashSet<string> OnEngine)> ForAsync(AppUser user, IReadOnlyList<GatewayModel> chatModels, CancellationToken ct = default)
    {
        var allowed = await AllowedAsync(user, chatModels.Select(m => m.Name), ct);
        var onEngine = await OnEngineAsync(ct);
        var mine = chatModels.Where(m => allowed.Contains(m.Name)).ToList();
        return (mine, mine.FirstOrDefault(m => Ready(m.Name, onEngine)) ?? mine.FirstOrDefault(), onEngine);
    }

    /// <summary>Why this person cannot have an answer from this model now, or null.</summary>
    public async Task<string?> RefusalAsync(AppUser user, string model, CancellationToken ct = default)
    {
        if (!(await AllowedAsync(user, [model], ct)).Contains(model))
        {
            return $"You may not use {model}. Choose another model.";
        }
        // Only when the engine answered its last check: an engine that cannot be reached says so in its own error.
        if ((await OnEngineAsync(ct)).Contains(model) && engine.Now is { Error: null, At: not null } && engine.StatusOf(model) is not "loaded")
        {
            return engine.StatusOf(model) == "loading"
                ? $"{model} is loading. Try again in a minute, or choose another model."
                : engine.StatusOf(model) == "failed"
                    ? $"{model} could not be loaded. Choose another model; an admin can see why under Admin → Models."
                    : $"{model} is not loaded right now. An admin can load it under Admin → Models, or choose another model.";
        }
        return null;
    }
}
