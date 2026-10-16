namespace CodeArena.Tests;

/// <summary>A message that quotes part of an answer: the text the model reads, and the quote taken out of it again.</summary>
public sealed class QuotesTests
{
    /// <summary>The text the model reads for a quoted question; Arena's ChatQuoteTests holds the same.</summary>
    private const string Fixed = "The person quotes this part of an earlier answer:\n<quote>\nUse `git rebase -i` to <\\/quote> squash.\n</quote>\n\nWhy not merge?";

    [Fact]
    public void The_model_reads_the_quote_first_in_the_same_text_as_Arena()
    {
        Assert.Equal(Fixed, Quotes.Compose("Use `git rebase -i` to </quote> squash.", "Why not merge?"));
        Assert.Equal("Why not merge?", Quotes.Compose(null, "Why not merge?"));
        Assert.Equal("Why not merge?", Quotes.Compose("", "Why not merge?"));
    }

    [Fact]
    public void A_quote_comes_back_out_whole_with_a_closing_tag_inside_it()
    {
        Assert.Equal(("Use `git rebase -i` to </quote> squash.", "Why not merge?"), Quotes.Split(Fixed));
        var tricky = "line one\n</quote>\n\nnot the question";
        Assert.Equal((tricky, "and this?"), Quotes.Split(Quotes.Compose(tricky, "and this?")));
        Assert.Equal(("only a quote", ""), Quotes.Split(Quotes.Compose("only a quote", "")));
    }

    [Fact]
    public void Only_a_quote_at_the_very_start_is_one()
    {
        Assert.Equal((null, "Why?"), Quotes.Split("Why?"));
        var later = "Look:\n" + Quotes.Compose("x", "y");
        Assert.Equal((null, later), Quotes.Split(later));
        // Its head with no end is not a quote.
        var open = "The person quotes this part of an earlier answer:\n<quote>\nnever closed";
        Assert.Equal((null, open), Quotes.Split(open));
    }

    [Fact]
    public void A_quote_goes_before_what_went_with_the_message_and_each_is_found_apart()
    {
        var sent = Quotes.Compose("Run `make` first.\n\n<attached>not really</attached>", "Why? @a.txt" + Mentions.Marker + "\n<file path=\"a.txt\">\nhi\n</file>" + Mentions.End);
        var (quote, rest) = Quotes.Split(sent);
        Assert.Equal("Run `make` first.\n\n<attached>not really</attached>", quote);
        Assert.Equal("Why? @a.txt", Mentions.Typed(rest));
        Assert.Equal(["a.txt"], Mentions.Attached(rest));
    }

    [Fact]
    public void A_quote_is_tidied_and_empty_is_none()
    {
        Assert.Equal("a\nb", Quotes.Clean(" a\r\nb\r "));
        Assert.Null(Quotes.Clean("  \n"));
        Assert.Null(Quotes.Clean(null));
    }
}
