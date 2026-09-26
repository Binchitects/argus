using Llm.Core.Access;

namespace Llm.Core.Models;

/// <summary>
/// A model the engine (llama.cpp's router) can load besides the .env one: a
/// GGUF file in the model library and how to run it. The app writes these as
/// the engine's presets and registers each at the gateway.
/// </summary>
public sealed class LocalModel
{
    /// <summary>Its name at the engine, the gateway and in the chat.</summary>
    public required string Name { get; set; }
    /// <summary>The GGUF file, relative to the library (for a split model, its first part).</summary>
    public required string File { get; set; }
    /// <summary>A vision projector (mmproj) in the library, for a model that can see.</summary>
    public string? Projector { get; set; }
    /// <summary>The context, shared by the answers in parallel (one cache): the most one conversation can use.</summary>
    public int Context { get; set; } = 32768;
    public int? MaxOutput { get; set; }
    /// <summary>"auto": llama.cpp fits the model to the GPU when it loads (layers or experts to RAM as needed); "manual": <see cref="GpuLayers"/> and <see cref="CpuMoe"/>.</summary>
    public string Placement { get; set; } = "auto";
    public int GpuLayers { get; set; } = 99;
    /// <summary>Mixture-of-experts layers whose experts stay in RAM (llama.cpp --n-cpu-moe).</summary>
    public int CpuMoe { get; set; }
    public string KvType { get; set; } = "q8_0";
    public int Parallel { get; set; } = 1;
    /// <summary>Tokens of a prompt read in one step (llama.cpp --ubatch-size); null: the engine's 512.</summary>
    public int? Ubatch { get; set; }
    /// <summary>Drafting tokens with multi-token prediction: the model's own layer, or <see cref="DraftHead"/>.</summary>
    public bool Mtp { get; set; }
    /// <summary>A draft head in the library for <see cref="Mtp"/>, for a model without its own prediction layer.</summary>
    public string? DraftHead { get; set; }
    public int DraftMax { get; set; } = 3;
    /// <summary>A context past the one it was trained for, stretched with YaRN.</summary>
    public bool Yarn { get; set; }
    /// <summary>Sampling for requests that set none; null: the model's own recommendation (its file), else the engine's.</summary>
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? MinP { get; set; }
    public double? PresencePenalty { get; set; }
    /// <summary>More preset lines ("key = value", llama-server's long option names), for what the form does not cover.</summary>
    public string? ExtraPreset { get; set; }
    public bool Thinking { get; set; } = true;
    public bool Tools { get; set; } = true;
    /// <summary>Per million tokens; null: the gateway prices of .env apply.</summary>
    public decimal? InputPerMtok { get; set; }
    public decimal? OutputPerMtok { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Who may use a model (any model at the gateway, local or not). A model without a row: everyone.</summary>
public sealed class ModelAccess
{
    public required string Model { get; set; }
    public Audience Audience { get; set; }
    public List<Guid> Groups { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
