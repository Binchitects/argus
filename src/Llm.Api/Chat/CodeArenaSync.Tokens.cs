using System.Text.Json.Nodes;
using Llm.Api.ArenaMcp;
using Llm.Api.Endpoints;
using Llm.Api.Models;
using Llm.Core.Chat;

namespace Llm.Api.Chat;

/// <summary>
/// Texts to count in a model's tokens, for Code Arena's context view: each piece of its next request, and the whole request
/// (its messages and tools) when <paramref name="Request"/> is sent.
/// </summary>
public sealed record TokenizeRequest(string Model, string[] Texts, JsonObject? Request = null);

public static partial class CodeArenaSync
{
    /// <summary>The most texts, and characters in all, one call counts.</summary>
    internal const int MostTexts = 5_000, MostChars = 8_000_000;

    /// <summary>
    /// Counts Code Arena's texts with the model's own tokenizer, as the web chat's context view counts its requests (ModelTokens),
    /// so both show the same figures: for a model the person may use, on an engine of this Arena that has a tokenizer. Null
    /// counts: there is none (the reason in by), and Code Arena estimates.
    /// </summary>
    private static async Task<IResult> TokenizeAsync(TokenizeRequest body, HttpContext http, McpPeople people, ModelPolicy policy, ChatModels models, ModelTokens tokens)
    {
        var (me, refusal) = await CallerAsync(http, people);
        if (me is null)
        {
            return refusal!;
        }
        if (body.Texts is null || body.Texts.Length > MostTexts || body.Texts.Sum(t => (long)(t?.Length ?? 0)) > MostChars)
        {
            return AuthEndpoints.Problem(400, "texts", $"Send at most {MostTexts:N0} texts and {MostChars:N0} characters in all.");
        }
        var (mine, _, _) = await policy.ForAsync(me, await models.ListAsync(http.RequestAborted), http.RequestAborted);
        if (mine.All(m => m.Name != body.Model))
        {
            return AuthEndpoints.Problem(403, "model", $"{body.Model} is not among the models you may use.");
        }
        var pieces = body.Texts.Select(t => new WindowPiece("you", "", t ?? "")).ToList();
        var counted = await tokens.CountAsync(body.Model, body.Request ?? [], pieces, whole: body.Request is not null, TimeSpan.FromSeconds(20), http.RequestAborted);
        return Results.Ok(new { counts = counted.Counts, total = counted.Total, by = counted.By });
    }
}
