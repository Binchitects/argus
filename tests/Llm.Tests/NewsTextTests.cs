using Llm.Api.Notifications;

namespace Llm.Tests;

/// <summary>The bell's words: plain, short, a gateway's error as its message.</summary>
public sealed class NewsTextTests
{
    [Theory]
    [InlineData("Here's a simple React app:\n\n```jsx\nimport React from 'react';\n```\n\nRun **npm start**.", "Here's a simple React app: (code) Run npm start.")]
    [InlineData("The model could not answer: litellm.InternalServerError: InternalServerError: OpenAIException - Connection reset by peer", "The model could not answer: Connection reset by peer")]
    [InlineData("See [the docs](https://x.test/docs) and `make`.\n- one\n- two", "See the docs and make. one two")]
    [InlineData("## Summary\n> quoted", "Summary quoted")]
    [InlineData("   \n  ", null)]
    public void Notifications_are_plain_words(string text, string? expected) => Assert.Equal(expected, NewsText.Plain(text));

    [Fact]
    public void A_long_one_is_cut_with_an_ellipsis()
    {
        var plain = NewsText.Plain(new string('a', 400) + " end")!;
        Assert.Equal(NewsText.Most, plain.Length);
        Assert.EndsWith("…", plain, StringComparison.Ordinal);
    }
}

/// <summary>The gateway's errors as sentences for the person: a model that failed to load, and no classes.</summary>
public sealed class GatewayExplainTests
{
    [Fact]
    public void A_model_that_failed_to_load_is_explained()
    {
        var said = Llm.Api.Chat.GatewayChat.Explain("""{"error":{"message":"litellm.InternalServerError: InternalServerError: OpenAIException - model name=Qwen3.8-Flash-Next failed to load"}}""", 500);
        Assert.StartsWith("Qwen3.8-Flash-Next could not be loaded: most often it needs more GPU memory", said, StringComparison.Ordinal);
        Assert.Equal("The model could not answer: upstream reset", Llm.Api.Chat.GatewayChat.Explain("""{"error":{"message":"litellm.APIError: APIError: OpenAIException - upstream reset"}}""", 502));
    }
}
