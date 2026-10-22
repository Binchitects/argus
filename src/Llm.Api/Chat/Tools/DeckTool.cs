using System.Text.Json.Nodes;
using Llm.Api.Decks;
using Llm.Core.Chat;

namespace Llm.Api.Chat.Tools;

/// <summary>
/// Decks in the chat: "make a deck about this" makes one as the person (their model, credit, data), kept under Decks with
/// its link in the answer, to change, present and download there. Its pictures are made when it is opened.
/// </summary>
public sealed class DeckTool(DeckMaker maker) : IChatTool
{
    public const string ToolId = "decks";

    public string Id => ToolId;
    public string Title => "Decks";
    public string Description => "Makes a slide deck from the chat, to change, present and download as PowerPoint or PDF.";
    public string Icon => "presentation";

    public ToolSetting Defaults() => new() { ToolId = Id, OnByDefault = false };

    public Task<string?> UnavailableAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    private const string Instructions =
        "When the person asks for slides, a deck or a presentation, call make_deck with everything it should say: the subject, who it is for, " +
        "and the facts, numbers and points from the chat. Then give its link as a Markdown link, in a sentence; do not write the slides out.";

    private static readonly JsonArray Functions =
    [
        Schema.Function("make_deck", "Makes a slide deck (title, points, charts, pictures, speaker notes) and returns its link.",
            new JsonObject { ["about"] = Schema.Text("What the deck is about, with everything it should say: the facts, numbers and points from the chat.") }, "about"),
    ];

    public Task<IToolRun> StartAsync(ToolContext context, CancellationToken ct) => Task.FromResult<IToolRun>(new LocalRun(Functions, Instructions, async (_, args, token) =>
    {
        var (deck, slides, problem) = await maker.CreateAsync(context.User, Schema.Str(args, "about") ?? "", null, token);
        if (deck is null)
        {
            var why = problem is IValueHttpResult { Value: { } v } ? JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(v, Mcp.Plain))?["error"]?.GetValue<string>() : null;
            return new ToolResult($"No deck was made{(why is null ? "." : $": {why}")}", IsError: true);
        }
        return new ToolResult(new JsonObject
        {
            ["title"] = deck.Title, ["slides"] = slides.Count, ["link"] = $"/decks/{deck.Id}",
            ["outline"] = new JsonArray([.. slides.Select(s => (JsonNode?)(s.Title ?? s.Quote ?? ""))]),
        }.ToJsonString(Mcp.Plain));
    }));
}
