namespace Llm.Api.Models;

/// <summary>Something a model can be used for here: its id, what it is called, and where it is taken as that.</summary>
public sealed record ModelUse(string Id, string Label, string How);

/// <summary>
/// Every use a model has here, not only the one it is listed as: a language model chats, and the one a picture model reads
/// its prompt with (Qwen3 for FLUX.2 klein and Z-Image, Qwen2.5-VL for Qwen-Image, Mistral for FLUX.2 dev, Llama for
/// HiDream, Gemma for others) is that model's text encoder too; an embedding model embeds, a reranker ranks, a diffusion
/// model makes pictures or video, a projector lets its model see or hear. Read from a GGUF's header (ModelProfile) or, before
/// a download, from a Hugging Face repository's pipeline, tags and name.
/// </summary>
public static class ModelUses
{
    public static readonly ModelUse Chat = new("chat", "Chat", "Admin → Models → Add a model: a chat model (the API and Code Arena have it too).");
    public static readonly ModelUse Vision = new("vision", "Sees pictures", "With its vision projector (mmproj) chosen as the chat model's.");
    public static readonly ModelUse TextEncoder = new("text_encoder", "A picture or video model's text encoder",
        "Admin → Models → Add a picture or video model: choose it as its text encoder (llm, t5xxl or clip).");
    public static readonly ModelUse Embedding = new("embedding", "Embeddings", "The embedding server's model: memory, knowledge packs and search read with it.");
    public static readonly ModelUse Rerank = new("rerank", "Reranks", "A reranker: it orders passages for search; it does not chat.");
    public static readonly ModelUse Image = new("image", "Makes pictures", "Admin → Models → Add a picture model, with its text encoder and decoder (vae).");
    public static readonly ModelUse ImageEdit = new("image_edit", "Edits pictures", "A picture model that takes a picture too: the media studio's edits use it.");
    public static readonly ModelUse Video = new("video", "Makes video", "Admin → Models → Add a video model, with its text encoder and decoder.");
    public static readonly ModelUse Speech = new("speech", "Speaks", "Admin → Models → Add a speech model (text to speech).");
    public static readonly ModelUse Transcription = new("transcription", "Transcribes speech", "Admin → Models → Add a speech model (speech to text).");
    public static readonly ModelUse Hearing = new("hearing", "Hears audio", "With its audio projector chosen as the chat model's.");
    public static readonly ModelUse Draft = new("draft", "Drafts for another model", "Turn on drafting for its model, and choose it there.");
    public static readonly ModelUse Projector = new("projector", "Lets a model see or hear", "Choose it as a chat model's projector (mmproj).");

    /// <summary>
    /// The language models' architectures that picture and video models read their prompts with (stable-diffusion.cpp's
    /// --llm), by what reads them.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> EncoderOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["qwen3"] = "FLUX.2 klein, Z-Image",
        ["qwen2vl"] = "Qwen-Image",
        ["qwen25vl"] = "Qwen-Image",
        ["qwen2"] = "Qwen-Image (as Qwen2.5-VL's text part)",
        ["mistral3"] = "FLUX.2 dev",
        ["llama"] = "HiDream",
        ["gemma3"] = "Gemma-based picture models",
    };

    /// <summary>What a file of the library can be used for, from what its header says it is.</summary>
    public static IReadOnlyList<ModelUse> Of(ModelProfile p)
    {
        List<ModelUse> uses = p.Kind switch
        {
            ModelKind.Language => [Chat],
            ModelKind.Embedding => [Embedding],
            ModelKind.Reranker => [Rerank],
            ModelKind.Image => [Image],
            ModelKind.Video => [Video],
            ModelKind.Encoder => [TextEncoder],
            ModelKind.Draft => [Draft],
            ModelKind.Projector => [Projector, .. p.Projector is { Vision: true } ? new[] { Vision } : [], .. p.Projector is { Audio: true } ? new[] { Hearing } : []],
            _ => [],
        };
        if (p.Kind == ModelKind.Language && p.Architecture is { } arch && EncoderOf.TryGetValue(arch, out var readers))
        {
            uses.Add(TextEncoder with { Label = $"{TextEncoder.Label} ({readers})" });
        }
        return uses;
    }

    /// <summary>What a repository's models can be used for, before they are downloaded: its pipeline, tags and name.</summary>
    public static IReadOnlyList<ModelUse> Of(string id, string? pipeline, IReadOnlyList<string> tags)
    {
        var uses = new List<ModelUse>();
        void Add(ModelUse u)
        {
            if (uses.All(x => x.Id != u.Id))
            {
                uses.Add(u);
            }
        }
        var name = id.ToLowerInvariant();
        var tagged = tags.Select(t => t.ToLowerInvariant()).ToHashSet();
        switch (pipeline)
        {
            case "text-generation" or "conversational" or "text2text-generation":
                Add(Chat);
                break;
            case "image-text-to-text" or "visual-question-answering" or "image-to-text":
                Add(Chat);
                Add(Vision);
                break;
            case "audio-text-to-text" or "any-to-any":
                Add(Chat);
                Add(Hearing);
                break;
            case "feature-extraction" or "sentence-similarity":
                Add(Embedding);
                break;
            case "text-ranking" or "text-classification" when name.Contains("rerank", StringComparison.Ordinal):
                Add(Rerank);
                break;
            case "text-to-image":
                Add(Image);
                break;
            case "image-to-image":
                Add(ImageEdit);
                break;
            case "text-to-video" or "image-to-video":
                Add(Video);
                break;
            case "text-to-speech" or "text-to-audio":
                Add(Speech);
                break;
            case "automatic-speech-recognition":
                Add(Transcription);
                break;
        }
        if (tagged.Contains("sentence-transformers") || name.Contains("embed", StringComparison.Ordinal))
        {
            Add(Embedding);
        }
        if (name.Contains("rerank", StringComparison.Ordinal))
        {
            Add(Rerank);
        }
        // A text encoder of a picture model, as a GGUF: a language model too when it is one (Qwen2.5-VL, Qwen3), so it chats.
        if (name.Contains("text-encoder", StringComparison.Ordinal) || name.Contains("text_encoder", StringComparison.Ordinal) || name.Contains("te-", StringComparison.Ordinal) && tagged.Contains("gguf"))
        {
            Add(TextEncoder);
            if (name.Contains("qwen", StringComparison.Ordinal) || name.Contains("mistral", StringComparison.Ordinal) || name.Contains("llama", StringComparison.Ordinal) || name.Contains("gemma", StringComparison.Ordinal))
            {
                Add(Chat);
                if (name.Contains("vl", StringComparison.Ordinal) || name.Contains("image", StringComparison.Ordinal))
                {
                    Add(Vision);
                }
            }
        }
        // A plain chat model of a family a picture model reads its prompt with: its text encoder too.
        if (uses.Contains(Chat) && (name.Contains("qwen3-", StringComparison.Ordinal) && !name.Contains("embed", StringComparison.Ordinal) || name.Contains("qwen2.5-vl", StringComparison.Ordinal)))
        {
            Add(TextEncoder);
        }
        if (uses.Count == 0 && tagged.Contains("gguf"))
        {
            // A GGUF with no pipeline: most are chat models; its header says for sure once it is in the library.
            Add(Chat);
        }
        return uses;
    }
}
