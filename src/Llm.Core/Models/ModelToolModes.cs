namespace Llm.Core.Models;

/// <summary>
/// How a chat model is given its tools (Admin → Models → Tools): each definition to call directly, as functions a
/// Python program calls (run_code, tools as code), or both. A model with no row has the automatic choice
/// (<see cref="ToolModes"/>: direct for small models, both for the others).
/// </summary>
public sealed class ModelToolMode
{
    /// <summary>The model's name at the gateway and in the chat.</summary>
    public required string Model { get; set; }

    /// <summary><see cref="ToolModes.Direct"/>, <see cref="ToolModes.Code"/> or <see cref="ToolModes.Both"/>.</summary>
    public required string Mode { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The ways a model is given its tools.</summary>
public static class ToolModes
{
    /// <summary>Every tool's definition, called one call at a time; no run_code.</summary>
    public const string Direct = "direct";

    /// <summary>run_code, and the tools a program may call only as functions in it (those it may not, as they are).</summary>
    public const string Code = "code";

    /// <summary>Every tool's definition and run_code: a program for many calls, a direct call for one or two.</summary>
    public const string Both = "both";

    public static readonly string[] All = [Direct, Code, Both];
}
