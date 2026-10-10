using Llm.Api.Models;
using Llm.Core.Access;

namespace Llm.Api.Gateway;

/// <summary>Which credit a request to the gateway counts to, by its model.</summary>
public static class CreditKinds
{
    /// <summary>A picture, video or speech model's kind; any other model's request with an API key is an API request.</summary>
    public static CreditKind OfModel(string? model) => (model is null ? null : MediaControl.Find(model)?.Mode) switch
    {
        "image_generation" => CreditKind.Pictures,
        "video_generation" => CreditKind.Video,
        "audio_transcription" or "audio_speech" => CreditKind.Speech,
        _ => CreditKind.Api,
    };
}
