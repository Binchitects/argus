namespace Llm.Core.Models;

/// <summary>
/// Another machine's OpenAI-compatible engine (llama.cpp, vLLM, SGLang, another
/// gateway): the gateway serves the models chosen from it beside this machine's.
/// A model named like one here is a second copy of it, and the gateway spreads
/// requests between them.
/// </summary>
public sealed class RemoteServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    /// <summary>Its OpenAI-compatible API, ending in /v1.</summary>
    public required string BaseUrl { get; set; }
    /// <summary>Its API key, encrypted with the app's key ring; null: it takes none.</summary>
    public string? ApiKeyProtected { get; set; }
    /// <summary>False only for a server whose certificate cannot be checked (self-signed, no CA to trust).</summary>
    public bool VerifyTls { get; set; } = true;
    public List<RemoteModel> Models { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A model of a remote server, as the gateway offers it.</summary>
public sealed class RemoteModel
{
    /// <summary>Its id at the server.</summary>
    public required string Remote { get; set; }
    /// <summary>Its name at the gateway and in the chat.</summary>
    public required string Name { get; set; }
    public int? Context { get; set; }
    public int? MaxOutput { get; set; }
    public bool Vision { get; set; }
    public bool Tools { get; set; } = true;
    public bool Thinking { get; set; }
    /// <summary>Per million tokens; null: free.</summary>
    public decimal? InputPerMtok { get; set; }
    public decimal? OutputPerMtok { get; set; }
}
