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
    public int Context { get; set; } = 32768;
    public int? MaxOutput { get; set; }
    public int GpuLayers { get; set; } = 99;
    /// <summary>Mixture-of-experts layers whose experts stay in RAM (llama.cpp --n-cpu-moe).</summary>
    public int CpuMoe { get; set; }
    public string KvType { get; set; } = "q8_0";
    public int Parallel { get; set; } = 1;
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
