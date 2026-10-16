using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>
/// The routes of Arena's chat the IDE's page reaches through code-arena, so the web chat's own components work in it
/// unchanged: a fixed list, each sent on to Arena with the person's API key, which never reaches the page. Arena takes the
/// key on these routes alone (KeyAuthentication there). What the page sends here passes code-arena's own checks first (this
/// run's token, the same site); a form's body (multipart) is taken on the upload alone. The person's files: uploaded, read
/// (a picture, a document's text, a sound with its ranges), and a document's pages.
/// </summary>
internal static partial class ArenaRelay
{
    /// <summary>Where a file is uploaded (a form with one file): Arena reads its text, or keeps the picture or sound.</summary>
    public const string Upload = "/api/chat/attachments";

    /// <summary>The query parameters passed on: ?download (the file itself), ?upTo (how many pages to draw).</summary>
    private static readonly string[] Passed = ["download", "upTo"];

    /// <summary>The response headers passed back to the page.</summary>
    private static readonly string[] Back = ["Content-Disposition", "Content-Range", "Accept-Ranges", "Cache-Control", "X-Content-Type-Options"];

    /// <summary>The most an upload may hold when Arena has not said its limit (Arena's own default, 20 MB), with room for the form around it.</summary>
    public const long DefaultUpload = 20 * 1024 * 1024;

    /// <summary>Whether code-arena relays this request to Arena.</summary>
    public static bool Relays(string method, string path) => (method, path) switch
    {
        ("POST", Upload) => true,
        ("GET" or "HEAD", _) => FileRoute().IsMatch(path),
        _ => false,
    };

    [GeneratedRegex("^/api/chat/attachments/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/(content|pages|pages/[0-9]{1,5})$")]
    private static partial Regex FileRoute();

    /// <summary>Sends the request on to Arena with the key, and its answer back as Arena gave it.</summary>
    public static async Task RelayAsync(Config config, HttpClient http, HttpRequest req, HttpResponse res, CancellationToken ct)
    {
        if (config.Url is not { Length: > 0 } url || config.ApiKey is not { Length: > 0 } key)
        {
            await res.ErrorAsync(503, "no_arena", "This needs Arena: code-arena has no Arena address or API key here (code-arena login).", ct);
            return;
        }
        if (req.Method == "POST" && !(req.Header("Content-Type") ?? "").StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            await res.ErrorAsync(415, "unsupported", "Send the file as a form (multipart/form-data).", ct);
            return;
        }
        var query = string.Join('&', Passed.Where(req.Query.ContainsKey).Select(k => $"{k}={Uri.EscapeDataString(req.Query[k])}"));
        using var message = new HttpRequestMessage(new HttpMethod(req.Method), url.TrimEnd('/') + req.Path + (query.Length > 0 ? "?" + query : ""));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Headers.Add("X-Requested-With", "code-arena");
        if (req.Header("Range") is { Length: > 0 } range)
        {
            message.Headers.TryAddWithoutValidation("Range", range);
        }
        if (req.Method == "POST")
        {
            message.Content = new ByteArrayContent(req.Body);
            message.Content.Headers.TryAddWithoutValidation("Content-Type", req.Header("Content-Type"));
        }
        byte[] body;
        HttpResponseMessage answer;
        try
        {
            answer = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            body = await answer.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await res.ErrorAsync(502, "arena", $"Arena did not answer: {e.Message}", ct);
            return;
        }
        using (answer)
        {
            foreach (var name in Back)
            {
                if ((answer.Headers.TryGetValues(name, out var values) || answer.Content.Headers.TryGetValues(name, out values)) && string.Join(", ", values) is { Length: > 0 } value)
                {
                    res.Headers[name] = value;
                }
            }
            await res.SendAsync((int)answer.StatusCode, body, answer.Content.Headers.ContentType?.ToString(), ct);
        }
    }
}
