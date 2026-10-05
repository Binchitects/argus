using System.Net;
using System.Text.Json.Nodes;
using Argus.Indexing;

namespace Argus.Tests;

public class EmbedTests
{
    /// <summary>A llama.cpp embedding server whose model takes 100 tokens (here, characters) at most.</summary>
    sealed class SmallWindow(int window, string refusal) : HttpMessageHandler
    {
        public List<List<string>> Requests { get; } = [];

        // The embedder sends synchronously.
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => Answer(request);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Answer(request));

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var inputs = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())!["input"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            Requests.Add(inputs);
            if (inputs.FirstOrDefault(i => i.Length > window) is { } tooLong)
            {
                var message = string.Format(System.Globalization.CultureInfo.InvariantCulture, refusal, tooLong.Length, window);
                return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(new JsonObject { ["error"] = new JsonObject { ["message"] = message } }.ToJsonString()) };
            }
            var data = new JsonArray([.. inputs.Select((t, i) => (JsonNode)new JsonObject
            {
                ["index"] = i,
                ["embedding"] = new JsonArray([.. Enumerable.Range(0, Embed.Dim).Select(d => (JsonNode)(d == t.Length % Embed.Dim ? 1.0 : 0.01))]),
            })]);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["data"] = data }.ToJsonString()) };
        }
    }

    [Theory]
    [InlineData("input ({0} tokens) is larger than the max context size ({1} tokens). skipping")]
    [InlineData("input ({0} tokens) is too large to process. increase the physical batch size (current batch size: {1})")]
    public void A_text_longer_than_the_models_window_is_cut_to_fit_and_the_others_are_kept_whole(string refusal)
    {
        var server = new SmallWindow(100, refusal);
        using var client = new HttpClient(server);
        var texts = new List<string> { "short one", new('x', 500), "another short one" };

        var vectors = Embed.EmbedBatch(texts, client, "http://embed.test");

        Assert.Equal(3, vectors.Count);
        // The batch was refused, then sent item by item, and the long one shortened until it fit.
        var sent = server.Requests.Skip(1).SelectMany(r => r).ToList();
        Assert.Contains("short one", sent);
        Assert.Contains("another short one", sent);
        var cut = sent.Single(t => t.StartsWith('x') && t.Length <= 100);
        Assert.True(cut.Length >= 50, $"cut to {cut.Length} characters");
        Assert.Equal(cut.Length % Embed.Dim, Array.IndexOf(vectors[1], vectors[1].Max()));
    }

    [Fact]
    public void Another_refusal_is_still_an_error()
    {
        using var client = new HttpClient(new SmallWindow(100, "the model is not loaded ({0}, {1})"));
        var error = Assert.Throws<EmbeddingUnavailable>(() => Embed.EmbedBatch([new string('x', 500)], client, "http://embed.test"));
        Assert.Contains("400", error.Message, StringComparison.Ordinal);
    }
}
