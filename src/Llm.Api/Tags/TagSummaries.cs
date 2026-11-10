using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Chat;
using Llm.Api.Gateway;
using Llm.Api.Models;
using Llm.Core.Identity;
using Microsoft.Extensions.Options;

namespace Llm.Api.Tags;

/// <summary>What a chat, a project or a prompt is about, in a sentence or two, and the known tags that fit it.</summary>
public sealed record Summary(string Brief, IReadOnlyList<string> Tags);

/// <summary>
/// Summarize and tag: the model for small steps (else the person's default model) reads a chat, a project or a prompt and
/// its answer, writes what it is about in a sentence or two, and picks the known tags that fit (Tags:Known), only those.
/// The text goes masked as the person's own messages are (Safeguards), and is cut to <see cref="MaxRead"/>.
/// </summary>
public sealed class TagSummaries(SmallModel small, ModelPolicy policy, ChatModels models, Safeguards.Safeguards safeguards, GatewayChat gateway, IOptionsMonitor<TagOptions> tags)
{
    public const int MaxRead = 12_000;

    internal static string Instruction(IReadOnlyList<string> known) =>
        "You file work for a company. Read the text below and write, in the language it is mostly written in, what it is about and what came of it, " +
        "in at most two sentences. Then, on a last line of its own, write \"Tags:\" and the tags that fit it, comma separated, chosen only from: " +
        string.Join(", ", known) + ". Write \"Tags:\" with none after it when none fits. Write nothing else.";

    /// <summary>The summary, or null when no model may be used for it or it said nothing.</summary>
    public async Task<Summary?> SummarizeAsync(AppUser user, string text, CancellationToken ct)
    {
        var model = await small.ForAsync(user, ct) ?? (await policy.ForAsync(user, await models.ListAsync(ct), ct)).Default;
        if (model is null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var known = tags.CurrentValue.KnownTags;
        var masked = (await safeguards.MaskerAsync(user.Email ?? "", ct, model.Name))(text.Trim());
        var request = new JsonObject
        {
            ["model"] = model.Name,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = Instruction(known) },
                new JsonObject { ["role"] = "user", ["content"] = "<text>\n" + (masked.Length > MaxRead ? masked[..MaxRead] + "\n…" : masked) + "\n</text>" }),
            ["stream"] = true, ["max_tokens"] = 300, ["temperature"] = 0.2, ["user"] = user.Email!.ToLowerInvariant(),
        };
        if (ThinkingPresets.TemplateKwargs(SmallModel.NoThinking(model)) is { } kwargs)
        {
            request["chat_template_kwargs"] = kwargs;
        }
        var said = new StringBuilder();
        await foreach (var e in gateway.StreamAsync(request, user.Email!, ct))
        {
            if (e is ContentDelta c)
            {
                said.Append(c.Text);
            }
        }
        return Read(said.ToString(), known);
    }

    /// <summary>The model's words as a summary: the lines before "Tags:", and of the tags after it only the known ones.</summary>
    public static Summary? Read(string said, IReadOnlyList<string> known)
    {
        var lines = said.Replace("\r", "", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).ToList();
        var at = lines.FindLastIndex(l => l.StartsWith("tags:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("**tags:**", StringComparison.OrdinalIgnoreCase));
        var picked = at < 0 ? [] : Tagging.Clean(lines[at][(lines[at].IndexOf(':', StringComparison.Ordinal) + 1)..].Replace("*", "", StringComparison.Ordinal).Split(','))
            .Where(known.Contains).ToList();
        var brief = string.Join(" ", (at < 0 ? lines : lines[..at]).Where(l => l.Length > 0)).Trim();
        if (brief.StartsWith("summary:", StringComparison.OrdinalIgnoreCase))
        {
            brief = brief[8..].Trim();
        }
        if (brief.Length == 0)
        {
            return null;
        }
        return new Summary(brief.Length <= 2000 ? brief : brief[..1999] + "…", picked);
    }
}
