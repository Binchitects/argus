using System.Text.Json.Nodes;
using Llm.Core.Chat;

namespace CodeArena;

/// <summary>
/// What fills the window, as Claude Code's /context shows it: the next request in its pieces (Code Arena's words and the
/// instruction files, the memory, each tool's definition, the skills listed and loaded, the files that went with messages,
/// each message, tool call and result, the summary), counted in the model's own tokens by Arena (its engine's tokenizer,
/// POST /api/code-arena/tokenize) or else estimated, with the room kept for the answer, what is free and what could be
/// freed. Built as the web chat builds its own (ContextWindow), so both show the same figures. /context prints it, and the
/// IDE's Context shows it (GET /api/context).
/// </summary>
internal static class ContextView
{
    /// <summary>How long Arena has to count.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    public static async Task<JsonObject> WindowAsync(Runtime rt, CancellationToken ct)
    {
        var agent = rt.Agent;
        var request = agent.NextRequest();
        var pieces = agent.Pieces(request);
        var (counts, total, by) = await CountAsync(rt, request, pieces, ct);
        // Estimated: the next request's tokens as the status bar has them (the gateway's last count and what was added since).
        var (tokens, format) = ContextWindow.Count(pieces, counts, total ?? (counts is null ? agent.Estimate() : null), agent.CharsPerToken);
        var freeable = ContextWindow.Freeable(pieces, tokens, rt.Model.Context, group => rt.Tools.All.Any(t => t.Group == group) && !rt.Demand.IsOff(group),
            leaveOut: false, dropFiles: false, compact: agent.Messages.Count >= 3);
        return ContextWindow.Shape(pieces, tokens, format, rt.Model.Context, agent.Reserve, counts is not null, by, freeable);
    }

    /// <summary>Each piece's tokens and the whole request's as Arena counted them; null counts and why, when it did not.</summary>
    private static async Task<(IReadOnlyList<int>? Counts, long? Total, string By)> CountAsync(Runtime rt, JsonObject request, List<WindowPiece> pieces, CancellationToken ct)
    {
        if (rt.Config.Url is not { Length: > 0 } url || rt.Config.ApiKey is not { Length: > 0 } key)
        {
            return (null, null, "no Arena to count with");
        }
        var body = new JsonObject
        {
            ["model"] = rt.Model.Name,
            ["texts"] = new JsonArray([.. pieces.Select(p => (JsonNode)(p.Weight > 0 ? "" : p.Text))]),
            ["request"] = request.DeepClone(),
        };
        using var within = CancellationTokenSource.CreateLinkedTokenSource(ct);
        within.CancelAfter(Patience);
        try
        {
            using var req = ChatSync.Request(HttpMethod.Post, url.TrimEnd('/') + "/api/code-arena/tokenize", key, body);
            using var res = await rt.Http.SendAsync(req, within.Token);
            if (!res.IsSuccessStatusCode)
            {
                return (null, null, "Arena did not count");
            }
            var said = Json.ParseObject(await res.Content.ReadAsStringAsync(within.Token));
            var by = said?.Str("by") ?? "Arena did not say";
            if (said?["counts"] is not JsonArray counted || counted.Count != pieces.Count)
            {
                return (null, null, by);
            }
            return ([.. counted.Select(c => c?.GetValue<int>() ?? 0)], said.Long("total"), by);
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or FormatException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (null, null, "Arena is out of reach");
        }
    }
}
