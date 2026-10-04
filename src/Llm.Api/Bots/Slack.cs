using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Llm.Api.Bots;

/// <summary>
/// The Slack bot (a Slack app with the Events API): a mention in a channel, or a direct
/// message, is a question. Every event is checked against the app's signing secret; the
/// asker is found by the email of their Slack profile (users.info); the answer goes back
/// in the question's thread (chat.postMessage).
/// </summary>
public sealed class SlackBot(IHttpClientFactory http, IOptionsMonitor<BotOptions> options, TimeProvider clock) : IChatPlatform
{
    public const string Api = "https://slack.com/api";

    /// <summary>Events seen lately, by id: Slack sends one again when it got no answer in three seconds.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new();

    private SlackOptions O => options.CurrentValue.Slack;

    public string Name => "slack";

    public string Title => "Slack";

    /// <summary>Slack shows up to 40,000 characters, and asks for at most 4,000 for messages people read.</summary>
    public int Limit => 4000;

    public bool Ready => O is { SigningSecret.Length: > 0, BotToken.Length: > 0 };

    public PlatformOptions Options => O;

    /// <summary>Whether Slack signed this body: v0= and the HMAC-SHA256 of "v0:timestamp:body" under the signing secret, made at most five minutes ago.</summary>
    public bool SignatureOk(string? timestamp, string? signature, string body)
    {
        if (O.SigningSecret is not { Length: > 0 } secret || signature is null || !long.TryParse(timestamp, out var at)
            || Math.Abs(clock.GetUtcNow().ToUnixTimeSeconds() - at) > 300)
        {
            return false;
        }
        var expected = "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}")));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature));
    }

    /// <summary>The first time this event is seen (in the last hour).</summary>
    public bool FirstTime(string? eventId)
    {
        if (eventId is null)
        {
            return true;
        }
        var now = clock.GetUtcNow();
        foreach (var old in _seen.Where(s => now - s.Value > TimeSpan.FromHours(1)).Select(s => s.Key).ToList())
        {
            _seen.TryRemove(old, out _);
        }
        return _seen.TryAdd(eventId, now);
    }

    /// <summary>
    /// The question an event asks: a mention of the bot, or a direct message to it. Null for any
    /// other event (the bot's own posts, edits, joins). <paramref name="channel"/> says where.
    /// </summary>
    public static BotQuestion? Read(JsonObject e, out string channel, out bool direct)
    {
        channel = e["channel"]?.GetValue<string>() ?? "";
        direct = e["channel_type"]?.GetValue<string>() == "im";
        var type = e["type"]?.GetValue<string>();
        if (e["bot_id"] is not null || e["subtype"] is not null || e["user"]?.GetValue<string>() is not { Length: > 0 } user || channel.Length == 0
            || !(type == "app_mention" || (type == "message" && direct)))
        {
            return null;
        }
        var ts = e["ts"]?.GetValue<string>() ?? "";
        var root = e["thread_ts"]?.GetValue<string>() ?? ts;
        var text = BotText.SlackQuestion(e["text"]?.GetValue<string>() ?? "");
        return new BotQuestion($"{e["team"]?.GetValue<string>()}:{channel}:{root}", user, $"<@{user}>", text, new JsonObject { ["channel"] = channel, ["thread_ts"] = root });
    }

    public async Task<BotQuestion> ResolveAsync(BotQuestion question, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, $"users.info?user={Uri.EscapeDataString(question.User)}");
        using var response = await http.CreateClient(BotEndpoints.Client).SendAsync(request, ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        return body?["ok"]?.GetValue<bool>() == true && body["user"]?["profile"]?["email"]?.GetValue<string>() is { Length: > 0 } email
            ? question with { Email = email }
            : question;
    }

    public async Task PostAsync(BotQuestion question, string text, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "chat.postMessage");
        request.Content = new StringContent(new JsonObject
        {
            ["channel"] = question.Reply["channel"]?.DeepClone(), ["thread_ts"] = question.Reply["thread_ts"]?.DeepClone(), ["text"] = text, ["mrkdwn"] = true,
            ["unfurl_links"] = false,
        }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.CreateClient(BotEndpoints.Client).SendAsync(request, ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject;
        if (!response.IsSuccessStatusCode || body?["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException($"Slack said {body?["error"]?.GetValue<string>() ?? ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
    }

    public string Format(string markdown) => BotText.SlackMarkdown(markdown);

    public string Link(string url, string text) => $"<{url}|{text}>";

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{Api}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", O.BotToken);
        return request;
    }
}
