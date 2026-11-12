using Llm.Api.Access;

namespace Llm.Api.Gateway;

/// <summary>
/// The rest of the model API with keys: gateway.DOMAIN goes to the app alone (Traefik and the Helm chart's ingress), which
/// answers the requests that ask a model (AnswerCache, KeySpeech) and these, the reading ones and the embeddings
/// (<see cref="OwnApi"/>): the models a key may use, their information, embeddings, Anthropic's count_tokens, and a 404
/// that says so for what it does not serve (rerank, moderations, translations, picture edits).
/// </summary>
public static class ApiPassage
{
    /// <summary>The paths, with and without /v1 as clients send both: OpenAI's and Anthropic's.</summary>
    public static readonly string[] Paths =
    [
        "/v1/models", "/models", "/v1/models/{id}", "/models/{id}",
        "/v1/model/info", "/model/info",
        "/v1/embeddings", "/embeddings",
        "/v1/moderations", "/moderations",
        "/v1/rerank", "/rerank", "/v2/rerank",
        "/v1/messages/count_tokens", "/messages/count_tokens",
        "/v1/audio/translations", "/audio/translations",
        "/v1/images/edits", "/images/edits", "/v1/images/variations", "/images/variations",
    ];

    public static void MapApiPassage(this IEndpointRouteBuilder app)
    {
        foreach (var path in Paths)
        {
            // A browser's preflight (OPTIONS) is answered too.
            app.MapMethods(path, ["GET", "POST", "OPTIONS"], PassAsync).AllowAnonymous("The OpenAI- and Anthropic-style API: the key is checked.").DisableAntiforgery();
        }
    }

    /// <summary>Answered by the app's own gateway (<see cref="OwnApi"/>); a path it has no answer for is 404 there.</summary>
    private static async Task PassAsync(HttpContext http) => await OwnApi.TryAsync(http);
}
