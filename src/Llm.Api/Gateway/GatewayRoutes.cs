using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Models;
using Llm.Api.Operations;
using Llm.Core.Data;
using Llm.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Gateway;

/// <summary>
/// One place a model name is answered: llama.cpp's router here, a copy of a vLLM or SGLang model, another GPU server, or a
/// picture or speech server; with the name it is sent, its key, how many requests it takes at once, its share of a pool, its
/// prices and its fallback. While LiteLLM still answers, each also carries what LiteLLM is given for it (<see cref="LiteLlm"/>
/// and <see cref="Info"/>), known there by <see cref="Fingerprint"/>.
/// </summary>
public sealed record Deployment
{
    public const string LlamaCpp = "llamacpp";
    public const string Remote = "remote";
    public const string Image = "image";
    public const string Audio = "audio";

    /// <summary>The name callers ask for; a copy of a model of several copies is also its own, NAME@i, kept out of lists.</summary>
    public required string Name { get; init; }

    /// <summary>llamacpp, vllm, sglang, remote, image or audio.</summary>
    public required string Kind { get; init; }

    /// <summary>Its OpenAI-style address, ending in /v1.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>The model's name as it is sent there.</summary>
    public required string Model { get; init; }

    /// <summary>The engines' own key (Engine:ApiKey) is sent: this stack's engines.</summary>
    public bool EngineKey { get; init; }

    /// <summary>Another server's key, as it is sent (never logged, never kept outside the process); null: none, or the engines'.</summary>
    public string? Key { get; init; }

    /// <summary>An https server's certificate checked against the public roots; null: http.</summary>
    public bool? VerifyTls { get; init; }

    /// <summary>The most requests it is sent at once; null: as many as come.</summary>
    public int? MaxParallel { get; init; }

    /// <summary>Its share when the copies of a pool are shuffled: its slots, or 1.</summary>
    public int Weight { get; init; } = 1;

    /// <summary>How many deployments answer this name (a pool when more than one).</summary>
    public int Copies { get; init; } = 1;

    /// <summary>A copy under its own name (NAME@i), for a conversation to stay on one: kept out of lists.</summary>
    public string? CopyOf { get; init; }

    /// <summary>The model that answers while this one cannot (a vLLM or SGLang model's, set under Admin → Models).</summary>
    public string? Fallback { get; init; }

    /// <summary>The GPU server it is on, for a remote one.</summary>
    public string? Server { get; init; }

    /// <summary>chat, image_generation, audio_transcription or audio_speech.</summary>
    public required string Mode { get; init; }

    /// <summary>A chat model's token prices.</summary>
    public TokenPrice? Price { get; init; }

    /// <summary>A picture or speech model's own price (per picture, per minute, per thousand characters), else Settings → Prices'.</summary>
    public decimal? UnitPrice { get; init; }

    /// <summary>Its window, longest answer and what it reads and does, as /v1/model/info tells it.</summary>
    public required JsonObject Info { get; init; }

    /// <summary>What LiteLLM is given for it (litellm_params).</summary>
    public required JsonObject LiteLlm { get; init; }

    /// <summary>A hash of everything it is registered with: a changed one is a new deployment.</summary>
    public required string Fingerprint { get; init; }
}

/// <summary>Routes besides the app's own tables' (tests give the models they ask for this way).</summary>
public interface IRoutesSource
{
    IEnumerable<Deployment> Routes();
}

/// <summary>
/// The model names and where each is answered, from the app's own tables: the engine's models (llama.cpp's, each copy of a
/// vLLM or SGLang model), the models chosen from other GPU servers, and the picture and speech models while their servers
/// run. Every model carries its prices (its own, else Settings → Prices), so every request has a cost.
/// </summary>
public sealed class GatewayRoutes(AppDbContext db, ModelLibrary library, RemoteServerClient remote, Modules modules, MediaControl media,
    IOptionsMonitor<PriceOptions> prices, VllmBackends vllm, IOptions<EngineOptions> engine, IEnumerable<IRoutesSource> sources)
{
    /// <summary>
    /// The models people may choose (chat, pictures, speech, transcription), each name once, in the order they were added:
    /// a copy under its own name (NAME@i) is in no list.
    /// </summary>
    public async Task<IReadOnlyList<GatewayModel>> ModelsAsync(CancellationToken ct)
    {
        var models = new List<GatewayModel>();
        foreach (var d in await BuildAsync(ct))
        {
            if (d.CopyOf is not null || models.Any(m => m.Name == d.Name) || d.Mode is not ("chat" or "image_generation" or "audio_transcription" or "audio_speech"))
            {
                continue;
            }
            // Built here, a number is an int; read from JSON, any integer.
            int? Int(string k) => d.Info[k] is JsonValue v ? v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var n) ? (int)Math.Min(n, int.MaxValue) : null : null;
            bool? Flag(string k) => d.Info[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
            decimal? PerMtok(string k) => d.LiteLlm[k] is JsonValue v && v.TryGetValue<decimal>(out var x) ? x * 1_000_000m : null;
            models.Add(new GatewayModel(d.Name, Int("max_input_tokens"), Int("max_output_tokens"), Flag("supports_vision") ?? false, Flag("supports_function_calling") ?? true,
                Flag("supports_reasoning") ?? true, PerMtok("input_cost_per_token"), PerMtok("cache_read_input_token_cost"), PerMtok("output_cost_per_token"), d.Mode,
                Flag("supports_audio_input") ?? false));
        }
        return models;
    }

    /// <summary>The routes: the app's own tables', and with <paramref name="others"/> those given besides (IRoutesSource).</summary>
    public async Task<List<Deployment>> BuildAsync(CancellationToken ct, bool others = true)
    {
        var defaults = prices.CurrentValue;
        var built = new List<Deployment>();
        // Each chat model on each server first, with what makes it this one and the requests it serves at once.
        var deployments = new List<(Deployment D, object?[] Print, int? Slots)>();
        foreach (var m in await db.LocalModels.AsNoTracking().Where(m => m.EngineKind == EngineKinds.LlamaCpp).OrderBy(m => m.CreatedAt).ThenBy(m => m.Name).ToListAsync(ct))
        {
            // What its projector reads: pictures, sound (an omni model's), or both.
            var projector = m.Projector is { Length: > 0 } p ? library.Find(p)?.Profile.Projector ?? new ProjectorInfo(true, false, null, null) : null;
            // The window one conversation really has: its slot's share of the context.
            var window = ModelCatalog.SlotWindow(m);
            var info = Info(window, Math.Min(m.MaxOutput ?? ModelCatalog.DefaultMaxOutput(window), window), projector?.Vision == true, m.Tools, m.Thinking, projector?.Audio == true);
            var litellm = new JsonObject { ["model"] = "openai/" + m.Name, ["api_base"] = "os.environ/ENGINE_API_BASE", ["api_key"] = "os.environ/ENGINE_API_KEY" };
            var price = TokenPrice.Of(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, defaults);
            Prices(litellm, price);
            var d = new Deployment
            {
                Name = m.Name, Kind = Deployment.LlamaCpp, BaseUrl = engine.Value.Url.TrimEnd('/') + "/v1", Model = m.Name, EngineKey = true, Mode = "chat", Price = price, Info = info, LiteLlm = litellm, Fingerprint = "",
            };
            deployments.Add((d, ["local", m.Name, m.File, m.Context, m.Parallel, m.MaxOutput, m.Projector, projector?.Audio, m.Tools, m.Thinking, price.Input, price.CachedInput, price.Output],
                m.Parallel));
        }
        // vLLM's and SGLang's models: each copy at its own port, never sent more at once than its answers at once, a pool when
        // there are several; each copy also under its own name (NAME@i, kept out of every list) for the chat to keep a conversation
        // on one; and the fallback that answers while it cannot.
        foreach (var m in await db.LocalModels.AsNoTracking().Where(m => m.EngineKind == EngineKinds.Vllm || m.EngineKind == EngineKinds.Sglang).OrderBy(m => m.CreatedAt).ThenBy(m => m.Name)
            .ToListAsync(ct))
        {
            if (vllm.For(m.Engine, m.EngineKind) is not { } backend || backend.Presets().Where(e => e.Model == m.Name).ToList() is not { Count: > 0 } entries)
            {
                continue;
            }
            var vision = VllmAdvisor.Read(library.FullPath(m.File))?.Vision == true;
            var info = Info(m.Context, Math.Min(m.MaxOutput ?? ModelCatalog.DefaultMaxOutput(m.Context), m.Context), vision, m.Tools, m.Thinking);
            if (m.Fallback is { Length: > 0 } fallback)
            {
                info["llm_app_fallbacks"] = new JsonArray(fallback);
            }
            var price = TokenPrice.Of(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, defaults);
            foreach (var e in entries)
            {
                var url = $"http://{backend.Host}:{e.Port}/v1";
                var litellm = new JsonObject
                {
                    ["model"] = "openai/" + e.Name, ["api_base"] = url, ["api_key"] = "os.environ/ENGINE_API_KEY", ["max_parallel_requests"] = Math.Max(1, m.Parallel),
                };
                Prices(litellm, price);
                object?[] print = [m.EngineKind, m.Name, e.Name, backend.Host, e.Port, m.Context, m.Parallel, m.MaxOutput, vision, m.Tools, m.Thinking, m.Fallback,
                    price.Input, price.CachedInput, price.Output];
                var d = new Deployment
                {
                    Name = m.Name, Kind = m.EngineKind, BaseUrl = url, Model = e.Name, EngineKey = true, MaxParallel = Math.Max(1, m.Parallel), Fallback = m.Fallback, Mode = "chat",
                    Price = price, Info = (JsonObject)info.DeepClone(), LiteLlm = litellm, Fingerprint = "",
                };
                deployments.Add((d, print, Math.Max(1, m.Parallel)));
                if (entries.Count > 1)
                {
                    var own = (JsonObject)info.DeepClone();
                    own["llm_app_replica_of"] = m.Name;
                    built.Add(d with { Name = e.Name, CopyOf = m.Name, Info = own, LiteLlm = (JsonObject)litellm.DeepClone(), Fingerprint = Fingerprint([.. print, "replica"]) });
                }
            }
        }
        foreach (var server in await db.RemoteServers.AsNoTracking().OrderBy(s => s.CreatedAt).ToListAsync(ct))
        {
            var key = remote.Unprotect(server.ApiKeyProtected);
            foreach (var m in server.Models)
            {
                var info = Info(m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking);
                info["llm_app_server"] = server.Name;
                var litellm = new JsonObject { ["model"] = "openai/" + m.Remote, ["api_base"] = server.BaseUrl, ["api_key"] = key ?? "none" };
                bool? verify = null;
                if (server.BaseUrl.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
                {
                    // The public roots, or no check at all.
                    litellm["ssl_verify"] = server.VerifyTls;
                    verify = server.VerifyTls;
                }
                var price = TokenPrice.Of(m.InputPerMtok, m.CachedInputPerMtok, m.OutputPerMtok, defaults);
                Prices(litellm, price);
                var keyPrint = key is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
                var d = new Deployment
                {
                    Name = m.Name, Kind = Deployment.Remote, BaseUrl = server.BaseUrl.TrimEnd('/'), Model = m.Remote, Key = key, VerifyTls = verify, MaxParallel = m.Parallel,
                    Server = server.Name, Mode = "chat", Price = price, Info = info, LiteLlm = litellm, Fingerprint = "",
                };
                deployments.Add((d, ["remote", server.Id, server.BaseUrl, keyPrint, server.VerifyTls, m.Remote, m.Name, m.Context, m.MaxOutput, m.Vision, m.Tools, m.Thinking,
                    price.Input, price.CachedInput, price.Output], m.Parallel));
            }
        }
        foreach (var pool in deployments.GroupBy(d => d.D.Name, StringComparer.Ordinal))
        {
            var size = pool.Count();
            foreach (var (d, print, slots) in pool)
            {
                // A model on one server is as it always was; on several, each copy carries its share of the pool.
                built.Add(size == 1
                    ? d with { Fingerprint = Fingerprint(print) }
                    : d with
                    {
                        Copies = size, MaxParallel = slots is > 0 ? slots : d.MaxParallel, Weight = slots is > 0 and var s ? s : 1, LiteLlm = Pooled(d.LiteLlm, d.Info, size, slots),
                        Fingerprint = Fingerprint([.. print, "pool", size, slots]),
                    });
            }
        }
        await MediaAsync(built, defaults, ct);
        // Those given besides first, as they were listed before the app's own.
        return others ? [.. sources.SelectMany(o => o.Routes()), .. built] : built;
    }

    /// <summary>The picture and speech models turned on, while their servers run: people's keys reach them too.</summary>
    private async Task MediaAsync(List<Deployment> built, PriceOptions defaults, CancellationToken ct)
    {
        void Add(string name, string kind, string model, string url, JsonObject info, decimal? own)
        {
            var litellm = new JsonObject { ["model"] = "openai/" + model, ["api_base"] = url + "/v1", ["api_key"] = "none" };
            MediaPrices(litellm, info["mode"]!.GetValue<string>(), defaults, own);
            built.Add(new Deployment
            {
                Name = name, Kind = kind, BaseUrl = url + "/v1", Model = model, Mode = info["mode"]!.GetValue<string>(), UnitPrice = own, Info = info, LiteLlm = litellm,
                Fingerprint = Fingerprint("media", name, "openai/" + model, url, info.ToJsonString(), litellm.ToJsonString()),
            });
        }
        await media.RefreshAsync(ct);
        foreach (var m in media.All)
        {
            // Each picture model under its own name (its server runs the one asked for); videos are made by the app, not the gateway.
            if (m.Mode == MediaModes.Image && await modules.HasAsync("imagegen", ct))
            {
                Add(m.Name, Deployment.Image, "sd-cpp-local", MediaModels.ImageUrl, new JsonObject { ["mode"] = m.Mode }, m.Row.UnitPrice);
            }
            else if (m.Id is not null && await modules.HasAsync("audio", ct))
            {
                Add(m.Name, Deployment.Audio, m.Id, MediaModels.AudioUrl, new JsonObject { ["mode"] = m.Mode }, m.Row.UnitPrice);
            }
        }
    }

    /// <summary>
    /// A picture or speech model's price as LiteLLM reads it: per picture, per second of sound
    /// transcribed, per character read aloud. Pictures and speech also carry token prices of 0:
    /// LiteLLM prices a request by its deployment's own entry only when that entry has a token
    /// or per-second price (LiteLLM 1.100, cost_calculator._select_model_name_for_cost_calc).
    /// </summary>
    private static void MediaPrices(JsonObject litellm, string mode, PriceOptions p, decimal? own)
    {
        switch (mode)
        {
            case "image_generation":
                litellm["input_cost_per_image"] = own ?? p.PerImage;
                litellm["input_cost_per_token"] = 0m;
                litellm["output_cost_per_token"] = 0m;
                break;
            case "audio_transcription":
                litellm["input_cost_per_second"] = (own ?? p.PerAudioMinute) / 60m;
                break;
            case "audio_speech":
                litellm["input_cost_per_character"] = (own ?? p.PerThousandCharacters) / 1000m;
                litellm["input_cost_per_token"] = 0m;
                litellm["output_cost_per_token"] = 0m;
                break;
        }
    }

    /// <summary>
    /// One copy of a model served by several engines (this machine's and other GPU servers'): the gateway sends each
    /// request to the least busy copy (router_settings in config/litellm.yaml), never more at once than a copy's
    /// slots, and weighs the copies by their slots when it shuffles.
    /// </summary>
    public static JsonObject Pooled(JsonObject litellm, JsonObject info, int copies, int? slots)
    {
        if (slots is > 0 and var s)
        {
            litellm["max_parallel_requests"] = s;
        }
        litellm["weight"] = slots is > 0 ? slots : 1;
        info["llm_app_pool"] = copies;
        return litellm;
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

    /// <summary>A chat model's prices, per token: LiteLLM prices a prompt's cached tokens at the cache read price, and at nothing without one.</summary>
    private static void Prices(JsonObject litellm, TokenPrice price)
    {
        litellm["input_cost_per_token"] = price.Input / 1_000_000m;
        litellm["cache_read_input_token_cost"] = price.CachedInput / 1_000_000m;
        litellm["output_cost_per_token"] = price.Output / 1_000_000m;
    }

    private static string Fingerprint(params object?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts.Select(p => Convert.ToString(p, CultureInfo.InvariantCulture))))))[..16];
}
