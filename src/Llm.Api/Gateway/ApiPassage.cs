using Llm.Api.Access;
using Microsoft.AspNetCore.Http.Features;

namespace Llm.Api.Gateway;

/// <summary>
/// The rest of the model API with keys, by way of the app: gateway.DOMAIN goes to the app alone (Traefik and the Helm
/// chart's ingress, with no way around it while the app is down), which answers the requests that ask a model itself
/// (AnswerCache, KeySpeech) and passes on these, the reading ones and the embeddings, with the caller's own key, which the
/// gateway checks. Nothing else reaches the gateway from outside: its own management (keys, people, teams, models, spend,
/// settings) and its admin pages are not found here.
/// </summary>
public static class ApiPassage
{
    /// <summary>The paths passed on, with and without /v1 as the gateway takes both: OpenAI's and Anthropic's, nothing of the gateway's own.</summary>
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
            // A browser's preflight (OPTIONS) is answered by the gateway, as before.
            app.MapMethods(path, ["GET", "POST", "OPTIONS"], PassAsync).AllowAnonymous("The OpenAI- and Anthropic-style API: the gateway checks the key.").DisableAntiforgery();
        }
    }

    private static async Task PassAsync(HttpContext http, IHttpClientFactory factory)
    {
        var request = http.Request;
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            // The gateway decides, as when it is reached directly.
            limit.MaxRequestBodySize = null;
        }
        var reads = HttpMethods.IsGet(request.Method) || HttpMethods.IsOptions(request.Method);
        using var forward = new HttpRequestMessage(new HttpMethod(request.Method), new Uri(request.Path + request.QueryString, UriKind.Relative));
        if (!reads || request.ContentLength > 0)
        {
            forward.Content = new StreamContent(request.Body);
            if (request.ContentType is { } type)
            {
                forward.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            }
            if (request.ContentLength is { } length)
            {
                forward.Content.Headers.ContentLength = length;
            }
        }
        foreach (var (name, values) in request.Headers)
        {
            if (!AnswerCacheEndpoints.Dropped.Contains(name))
            {
                forward.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        forward.Headers.TryAddWithoutValidation("X-Forwarded-For", http.Connection.RemoteIpAddress?.ToString());
        forward.Headers.TryAddWithoutValidation("X-Forwarded-Proto", request.Scheme);
        forward.Headers.TryAddWithoutValidation("X-Forwarded-Host", request.Host.Value);

        HttpResponseMessage res;
        try
        {
            res = await factory.CreateClient(AnswerCache.Client).SendAsync(forward, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
        }
        catch (HttpRequestException)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            await http.Response.WriteAsJsonAsync(new { error = new { message = "The model gateway is not reachable right now.", type = "gateway_unreachable", code = "502" } }, http.RequestAborted);
            return;
        }
        using (res)
        {
            http.Response.StatusCode = (int)res.StatusCode;
            foreach (var (name, values) in res.Headers.Concat(res.Content.Headers))
            {
                if (name is not ("Transfer-Encoding" or "Connection" or "Keep-Alive"))
                {
                    http.Response.Headers[name] = values.ToArray();
                }
            }
            await using var upstream = await res.Content.ReadAsStreamAsync(http.RequestAborted);
            await upstream.CopyToAsync(http.Response.Body, http.RequestAborted);
        }
    }
}
