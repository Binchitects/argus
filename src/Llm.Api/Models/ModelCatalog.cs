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
/// The models the engine can load: those admins added. Writes
/// the engine's presets (config/engine/models.ini), the models to keep loaded
/// (config/engine/keep, one name a line), Prometheus's scrape targets
/// (config/engine/targets.json), and keeps the gateway's list in step.
/// </summary>
public sealed partial class ModelCatalog(AppDbContext db, ILiteLlm gateway, IOptions<EngineOptions> options, ModelLibrary library, ChatModels chatModels,
    RemoteServerClient remote, ModelHoursState hours, Modules modules, MediaControl media, ILogger<ModelCatalog> logger)
{
    public const string PresetsFile = "models.ini";
    public const string KeepFile = "keep";
    public const string TargetsFile = "targets.json";
    public const string MaxFile = "max";

    /// <summary>Preset keys a model may not set: they belong to the router, or would reach outside the library.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "model", "hf-repo", "hf-file", "hf", "model-url", "mmproj", "mmproj-url", "host", "port", "api-key", "api-key-file", "alias",
        "path", "ssl-key-file", "ssl-cert-file", "models-dir", "models-preset", "models-max", "models-autoload", "no-models-autoload", "webui", "no-webui",
        "log-file", "slot-save-path", "lora", "lora-scaled", "control-vector", "control-vector-scaled", "model-draft", "spec-draft-model", "hf-repo-draft",
        "chat-template-file", "grammar-file", "json-schema-file", "media-path", "mcp-servers-config", "log-prompts-dir", "lookup-cache-static",
        "lookup-cache-dynamic", "docker-repo", "hf-token", "help", "version", "completion-bash", "list-devices", "cache-list", "offline",
    };

    /// <summary>Preset keys the form sets: a second value in the extra lines would be ambiguous.</summary>
    private static readonly HashSet<string> Managed = new(StringComparer.Ordinal)
    {
        "ctx-size", "n-gpu-layers", "gpu-layers", "n-cpu-moe", "cpu-moe", "cache-type-k", "cache-type-v", "parallel", "jinja", "metrics", "kv-unified", "no-kv-unified",
        "threads", "ubatch-size", "batch-size", "fit", "device", "rope-scaling", "rope-scale", "yarn-orig-ctx", "spec-type", "spec-draft-n-max", "draft-max", "draft-n",
        "temp", "temperature", "top-p", "top-k", "min-p", "presence-penalty", "embedding", "embeddings", "pooling", "rerank", "reranking",
    };

    /// <summary>
    /// The engine's option names (llama.cpp build 10902, Models/engine-options.txt). A preset key
    /// outside them stops the whole engine, not one model, so none gets through.
    /// </summary>
    public static readonly IReadOnlySet<string> EngineOptionNames = LoadOptionNames();

    private static HashSet<string> LoadOptionNames()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream("engine-options.txt")
            ?? throw new InvalidOperationException("engine-options.txt is not embedded");
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];
    }

    private string Dir => options.Value.ConfigDir;

    /// <summary>Why a name cannot be a new model, or null.</summary>
    public static string? CheckName(string name) =>
        NameRule().IsMatch(name) ? null : "A name is 1-100 letters, digits and . _ : - (starting with a letter or digit).";

    /// <summary>Why these extra preset lines cannot be used, or null.</summary>
    public static string? CheckExtra(string? extra)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
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
            var key = m.Groups[1].Value;
            if (Reserved.Contains(key))
            {
                return $"{key} is set by the engine or reaches outside the model library: it cannot be set here.";
            }
            if (Managed.Contains(key))
            {
                return $"{key} is set by the form above: change it there.";
            }
            if (!EngineOptionNames.Contains(key))
            {
                return $"{key} is not an option of this engine's llama-server: a preset with it would stop the engine. Check the name (llama-server --help).";
            }
            if (!seen.Add(key))
            {
                return $"{key} is there twice.";
            }
        }
        return null;
    }

    /// <summary>A model's section of the engine's presets: the settings of its kind, and only what the engine should not decide itself.</summary>
    public static string Preset(LocalModel m, string library, int? threads = null, ModelProfile? profile = null)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        var root = library.TrimEnd('/');
        sb.Append('[').Append(m.Name).Append("]\n");
        sb.Append("model = ").Append(root).Append('/').Append(m.File).Append('\n');
        if (m.Projector is { Length: > 0 } p)
        {
            sb.Append("mmproj = ").Append(root).Append('/').Append(p).Append('\n');
        }
        // One cache for the answers in parallel: a conversation can use all of it.
        sb.Append(inv, $"ctx-size = {m.Context}\nparallel = {m.Parallel}\nkv-unified = true\n");
        sb.Append(inv, $"cache-type-k = {m.KvType}\ncache-type-v = {m.KvType}\n");
        if (m.Placement == "manual")
        {
            sb.Append(inv, $"n-gpu-layers = {m.GpuLayers}\n");
            if (m.CpuMoe > 0)
            {
                sb.Append(inv, $"n-cpu-moe = {m.CpuMoe}\n");
            }
        }
        else
        {
            // llama.cpp fits the layers and experts to the GPU memory free when it loads.
            sb.Append("fit = on\n");
        }
        if (Hardware.ParseDevices(m.Devices) is { Count: > 0 } devices)
        {
            // CUDA numbers GPUs as nvidia-smi does: the engine runs with CUDA_DEVICE_ORDER=PCI_BUS_ID.
            sb.Append("device = ").Append(string.Join(',', devices.Select(d => "CUDA" + d.ToString(inv)))).Append('\n');
        }
        if (threads is > 0)
        {
            sb.Append(inv, $"threads = {threads}\n");
        }
        if (m.Ubatch is { } u)
        {
            sb.Append(inv, $"ubatch-size = {u}\n");
            if (u > 2048)
            {
                sb.Append(inv, $"batch-size = {u}\n");
            }
        }
        if (m.Mtp)
        {
            if (m.DraftHead is { Length: > 0 } head)
            {
                sb.Append("model-draft = ").Append(root).Append('/').Append(head).Append('\n');
            }
            sb.Append(inv, $"spec-type = draft-mtp\nspec-draft-n-max = {m.DraftMax}\n");
        }
        if (m.Yarn && profile?.TrainedContext is { } trained && trained > 0 && m.Context > trained)
        {
            sb.Append(inv, $"rope-scaling = yarn\nrope-scale = {Math.Ceiling(m.Context * 100.0 / trained) / 100:0.##}\nyarn-orig-ctx = {trained}\n");
        }
        if (m.Temperature is { } temp)
        {
            sb.Append(inv, $"temp = {temp:0.###}\n");
        }
        if (m.TopP is { } topP)
        {
            sb.Append(inv, $"top-p = {topP:0.###}\n");
        }
        if (m.TopK is { } topK)
        {
            sb.Append(inv, $"top-k = {topK}\n");
        }
        if (m.MinP is { } minP)
        {
            sb.Append(inv, $"min-p = {minP:0.###}\n");
        }
        if (m.PresencePenalty is { } presence)
        {
            sb.Append(inv, $"presence-penalty = {presence:0.###}\n");
        }
        sb.Append("jinja = true\nmetrics = true\n");
        foreach (var raw in (m.ExtraPreset ?? "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }
            // Saved before a key was checked: only what the engine knows, and never what the form sets.
            var key = PresetLine().Match(line) is { Success: true } k ? k.Groups[1].Value : null;
            if (key is not null && !Reserved.Contains(key) && !Managed.Contains(key) && EngineOptionNames.Contains(key))
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
        var profiles = models.Any(m => m.Yarn) ? library.List().ToDictionary(e => e.File.Path, e => e.Profile, StringComparer.Ordinal) : [];
        var text = "# Written by the app (Admin -> Models). The engine restarts when this changes.\n\n" +
            string.Join("\n", models.Select(m => Preset(m, options.Value.EngineLibraryDir, options.Value.Threads, profiles.GetValueOrDefault(m.File))));
        Write(PresetsFile, text);
    }

    /// <summary>
    /// The models kept loaded now (loaded at start, and again whenever one is not): the
    /// working-hours window's in force, else the ones admins pinned.
    /// </summary>
    public IReadOnlyList<string> Kept() => hours.Now.Window is { } window ? window.Keep : Pinned();

    /// <summary>The models admins pinned to keep loaded: what is kept outside working hours.</summary>
    public IReadOnlyList<string> Pinned() =>
        Read(KeepFile) is { } text ? [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)] : [];

    public void SetKept(IEnumerable<string> models) => Write(KeepFile, string.Join('\n', models.Distinct(StringComparer.Ordinal)) + "\n");

    /// <summary>How many models the engine may hold at once (it restarts when this changes).</summary>
    public void WriteMax() => Write(MaxFile, options.Value.ModelsMax.ToString(CultureInfo.InvariantCulture) + "\n");

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

    /// <summary>
    /// Brings the gateway in step with the app: the engine's models added here, and the models
    /// chosen from other GPU servers. Each deployment is known by a fingerprint of everything
    /// it is registered with, so a changed one is replaced and two of one name (a model here
    /// and its copy on a server) are both kept: the gateway spreads requests between them.
    /// </summary>
    public async Task SyncGatewayAsync(CancellationToken ct = default)
    {
        var wanted = new Dictionary<string, (string Name, JsonObject Params, JsonObject Info)>(StringComparer.Ordinal);
        foreach (var m in await db.LocalModels.AsNoTracking().ToListAsync(ct))
        {
            // What its projector reads: pictures, sound (an omni model's), or both.
            var projector = m.Projector is { Length: > 0 } p ? library.Find(p)?.Profile.Projector ?? new ProjectorInfo(true, false, null, null) : null;
            var info = Info(m.Context, m.MaxOutput ?? DefaultMaxOutput(m.Context), projector?.Vision == true, m.Tools, m.Thinking, projector?.Audio == true);
            var litellm = new JsonObject { ["model"] = "openai/" + m.Name, ["api_base"] = "os.environ/ENGINE_API_BASE", ["api_key"] = "os.environ/ENGINE_API_KEY" };
            Prices(litellm, m.InputPerMtok, m.OutputPerMtok);
            wanted[Fingerprint("local", m.Name, m.File, m.Context, m.MaxOutput, m.Projector, projector?.Audio, m.Tools, m.Thinking, m.InputPerMtok, m.OutputPerMtok)] = (m.Name, litellm, info);
        }
        foreach (var server in await db.RemoteServers.AsNoTracking().ToListAsync(ct))
        {
            var key = remote.Unprotect(server.ApiKeyProtected);
            foreach (var m in server.Models)
            {
                var info = Info(m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking);
                info["llm_app_server"] = server.Name;
                var litellm = new JsonObject { ["model"] = "openai/" + m.Remote, ["api_base"] = server.BaseUrl, ["api_key"] = key ?? "none" };
                if (server.BaseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                {
                    // The public roots, or no check at all.
                    litellm["ssl_verify"] = server.VerifyTls;
                }
                Prices(litellm, m.InputPerMtok, m.OutputPerMtok);
                var keyPrint = key is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
                wanted[Fingerprint("remote", server.Id, server.BaseUrl, keyPrint, server.VerifyTls, m.Remote, m.Name, m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking,
                    m.InputPerMtok, m.OutputPerMtok)] = (m.Name, litellm, info);
            }
        }
        await MediaAsync(wanted, ct);
        var managed = await gateway.ManagedModelsAsync(ct);
        foreach (var old in managed.Where(g => g.Fingerprint is null || !wanted.ContainsKey(g.Fingerprint)))
        {
            await gateway.DeleteModelAsync(old.Id, ct);
        }
        var kept = managed.Where(g => g.Fingerprint is not null && wanted.ContainsKey(g.Fingerprint)).Select(g => g.Fingerprint!).ToHashSet(StringComparer.Ordinal);
        foreach (var (fingerprint, w) in wanted.Where(w => !kept.Contains(w.Key)))
        {
            await gateway.AddModelAsync(w.Name, w.Params, w.Info, fingerprint, ct);
            LogRegistered(logger, w.Name);
        }
        chatModels.Forget();
    }

    /// <summary>The picture and speech models turned on, while their servers run: people's keys reach them too.</summary>
    private async Task MediaAsync(Dictionary<string, (string Name, JsonObject Params, JsonObject Info)> wanted, CancellationToken ct)
    {
        void Add(string name, string model, string url, JsonObject info)
        {
            var litellm = new JsonObject { ["model"] = model, ["api_base"] = url + "/v1", ["api_key"] = "none" };
            wanted[Fingerprint("media", name, model, url, info.ToJsonString())] = (name, litellm, info);
        }
        var on = (await media.ListAsync(ct)).Where(x => x.State.Enabled).Select(x => x.Model.Name).ToHashSet(StringComparer.Ordinal);
        if (on.Contains(MediaModels.ImageModel) && await modules.HasAsync("imagegen", ct))
        {
            Add(MediaModels.ImageModel, "openai/sd-cpp-local", MediaModels.ImageUrl, new JsonObject { ["mode"] = "image_generation" });
        }
        if (await modules.HasAsync("audio", ct))
        {
            foreach (var (name, id, mode) in MediaModels.Speech.Where(s => on.Contains(s.Name)))
            {
                Add(name, "openai/" + id, MediaModels.AudioUrl, new JsonObject { ["mode"] = mode });
            }
        }
    }

    private static JsonObject Info(int? context, int? maxOutput, bool vision, bool tools, bool thinking, bool audio = false)
    {
        var info = new JsonObject
        {
            ["mode"] = "chat", ["supports_vision"] = vision, ["supports_function_calling"] = tools, ["supports_reasoning"] = thinking, ["supports_audio_input"] = audio,
        };
        if (context is { } c)
        {
            info["max_input_tokens"] = c;
            info["max_tokens"] = c;
        }
        if (maxOutput is { } o)
        {
            info["max_output_tokens"] = o;
        }
        return info;
    }

    private static void Prices(JsonObject litellm, decimal? input, decimal? output)
    {
        if (input is { } i)
        {
            litellm["input_cost_per_token"] = i / 1_000_000m;
        }
        if (output is { } o)
        {
            litellm["output_cost_per_token"] = o / 1_000_000m;
        }
    }

    /// <summary>The longest answer when none is set: half the context, at most 32,768 tokens.</summary>
    public static int DefaultMaxOutput(int context) => Math.Max(ModelAdvisor.MinOutput, Math.Min(32768, context / 2 / 1024 * 1024));

    private static string Fingerprint(params object?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture))))))[..16];

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
public sealed class ModelPolicy(AppDbContext db, AccessService access, EngineState engine, ModelCatalog catalog, ModelHoursState hours, IOptions<EngineOptions> options)
{
    /// <summary>The models on the engine. Their answers need them loaded.</summary>
    public async Task<HashSet<string>> OnEngineAsync(CancellationToken ct = default)
    {
        if (!options.Value.Enabled)
        {
            return [];
        }
        var names = await db.LocalModels.AsNoTracking().Select(m => m.Name).ToListAsync(ct);
        // A model with a copy on another GPU server answers from there while it is not loaded here.
        var elsewhere = (await db.RemoteServers.AsNoTracking().ToListAsync(ct)).SelectMany(s => s.Models).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        return [.. names.Where(n => !elsewhere.Contains(n))];
    }

    /// <summary>The names, of those given, this person may use.</summary>
    public async Task<HashSet<string>> AllowedAsync(AppUser user, IEnumerable<string> models, CancellationToken ct = default)
    {
        var member = await access.MembershipAsync(user, ct);
        var rules = await db.ModelAccess.AsNoTracking().ToDictionaryAsync(r => r.Model, ct);
        return [.. models.Where(m => !rules.TryGetValue(m, out var r) || member.May(r.Audience, r.Groups))];
    }

    /// <summary>Whether a model is there to answer: always, unless it is the engine's and not loaded.</summary>
    public bool Loaded(string model, IReadOnlySet<string> onEngine) =>
        !onEngine.Contains(model) || engine.Now is not { Error: null, At: not null } || engine.StatusOf(model) == "loaded";

    /// <summary>
    /// An engine model that is not loaded but loads when asked for: the engine has a place
    /// besides the models kept loaded (the least recently used unloads to make room).
    /// </summary>
    public bool OnRequest(string model, IReadOnlySet<string> onEngine) =>
        onEngine.Contains(model) && !Loaded(model, onEngine) && engine.StatusOf(model) is "unloaded" or "loading"
        && catalog.Kept().Count < options.Value.ModelsMax;

    /// <summary>Whether a model can answer: loaded, or loaded on request.</summary>
    public bool Ready(string model, IReadOnlySet<string> onEngine) => Loaded(model, onEngine) || OnRequest(model, onEngine);

    /// <summary>
    /// The chat's models this person may use, and the one a chat uses when it chose none:
    /// the working hours' default when it can answer, else the first loaded, else the first
    /// that loads when asked.
    /// </summary>
    public async Task<(IReadOnlyList<GatewayModel> Models, GatewayModel? Default, HashSet<string> OnEngine)> ForAsync(AppUser user, IReadOnlyList<GatewayModel> chatModels, CancellationToken ct = default)
    {
        var allowed = await AllowedAsync(user, chatModels.Select(m => m.Name), ct);
        var onEngine = await OnEngineAsync(ct);
        var mine = chatModels.Where(m => allowed.Contains(m.Name)).ToList();
        var hoursDefault = hours.Now.Window?.DefaultModel is { } preferred ? mine.FirstOrDefault(m => m.Name == preferred && Ready(m.Name, onEngine)) : null;
        return (mine, hoursDefault ?? mine.FirstOrDefault(m => Loaded(m.Name, onEngine)) ?? mine.FirstOrDefault(m => Ready(m.Name, onEngine)) ?? mine.FirstOrDefault(), onEngine);
    }

    /// <summary>Why this person cannot have an answer from this model now, or null.</summary>
    public async Task<string?> RefusalAsync(AppUser user, string model, CancellationToken ct = default)
    {
        if (!(await AllowedAsync(user, [model], ct)).Contains(model))
        {
            return $"You may not use {model}. Choose another model.";
        }
        // Only when the engine answered its last check: an engine that cannot be reached says so in its own error.
        var onEngine = await OnEngineAsync(ct);
        if (onEngine.Contains(model) && engine.Now is { Error: null, At: not null } && !Ready(model, onEngine))
        {
            return engine.StatusOf(model) == "loading"
                ? $"{model} is loading. Try again in a minute, or choose another model."
                : engine.StatusOf(model) == "failed"
                    ? $"{model} could not be loaded. Choose another model; an admin can see why under Admin → Models."
                    : $"{model} is not loaded right now, and the engine has no place for it beside the models kept loaded. An admin can load it under Admin → Models, or choose another model.";
        }
        return null;
    }
}
