namespace Llm.Core.Decks;

/// <summary>
/// A slide deck the model made from a person's words and changes when they ask: its slides as JSON (each a layout,
/// its words, a chart from the data or a picture), in the company's template when there is one. Every version is kept.
/// </summary>
public sealed class Deck
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid OwnerId { get; set; }
    public required string Title { get; set; }
    /// <summary>The slides, JSON: <c>[{ layout, title, subtitle, bullets, left, right, quote, by, chart, picture, notes }]</c>.</summary>
    public required string Slides { get; set; }
    /// <summary>The company template it is in; none: the built-in look.</summary>
    public Guid? TemplateId { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One version of a deck's slides, kept to go back to.</summary>
public sealed class DeckVersion
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid DeckId { get; set; }
    public int Version { get; set; }
    public required string Title { get; set; }
    public required string Slides { get; set; }
    /// <summary>What made it: the person's words, or "Edited", "Put back version 3".</summary>
    public string Note { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A company template an admin uploaded (.pptx): its masters, layouts, theme and logo carry into every deck made in it.</summary>
public sealed class DeckTemplate
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public required byte[] Data { get; set; }
    /// <summary>What the browser draws with, JSON: <c>{ colors: { dk1, lt1, dk2, lt2, accent1…6 }, fonts: { major, minor }, width, height }</c>.</summary>
    public string Theme { get; set; } = "{}";
    /// <summary>The one new decks are made in.</summary>
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
