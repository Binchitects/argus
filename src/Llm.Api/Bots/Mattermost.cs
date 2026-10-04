using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Llm.Api.Bots;

/// <summary>
/// The Mattermost bot: an outgoing webhook (a post that starts with its trigger word) or a
/// slash command calls the app with its token; the asker is found by the email of their
/// Mattermost account and the answer posted by the bot account, in the post's thread (a
/// slash command's answer starts a thread of its own, quoting the question).
/// </summary>
public sealed class MattermostBot(IHttpClientFactory http, IOptionsMonitor<BotOptions> options) : IChatPlatform
{
    private MattermostOptions O => options.CurrentValue.Mattermost;

    private string? BaseUrl => O.Url?.Trim().TrimEnd('/');

    public string Name => "mattermost";

    public string Title => "Mattermost";

    /// <summary>Mattermost's posts hold 16,383 characters.</summary>
    public int Limit => 16000;

    public bool Ready => BaseUrl is { Length: > 0 } && O is { BotToken.Length: > 0, Tokens.Length: > 0 };

    public PlatformOptions Options => O;

    /// <summary>Whether the token is one of the webhooks' and commands' an admin saved.</summary>
    public bool TokenOk(string? token) =>
        token is { Length: > 0 } && (O.Tokens ?? "").Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(t), Encoding.UTF8.GetBytes(token)));

    /// <summary>The question in an outgoing webhook's or a slash command's fields; null when there is none (a post by a bot).</summary>
    public static BotQuestion? Read(IReadOnlyDictionary<string, string> f, out string channel)
    {
        string? F(string key) => f.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        channel = F("channel_id") ?? "";
        if (F("user_id") is not { } user || channel.Length == 0)
        {
            return null;
        }
        var name = F("user_name") is { } n ? $"@{n}" : "there";
        var text = (F("text") ?? "").Trim();
        if (F("command") is null)
        {
            // An outgoing webhook: the post starts with its trigger word.
            if (F("trigger_word") is { } trigger && text.StartsWith(trigger, StringComparison.OrdinalIgnoreCase))
            {
                text = text[trigger.Length..].TrimStart(' ', ':', ',').Trim();
            }
            var post = F("post_id") ?? "";
            return new BotQuestion($"{channel}:{post}", user, name, text, new JsonObject { ["channel_id"] = channel, ["post_id"] = post });
        }
        // A slash command has no post: its answer starts a thread, a chat of its own.
        return new BotQuestion($"{channel}:command:{F("trigger_id") ?? Guid.NewGuid().ToString("N")}", user, name, text,
            new JsonObject { ["channel_id"] = channel, ["quote"] = true });
    }

    public async Task<BotQuestion> ResolveAsync(BotQuestion question, CancellationToken ct)
    {
        var client = http.CreateClient(BotEndpoints.Client);
        using var who = Request(HttpMethod.Get, $"users/{Uri.EscapeDataString(question.User)}");
        using var person = await client.SendAsync(who, ct);
        var email = person.IsSuccessStatusCode && JsonNode.Parse(await person.Content.ReadAsStringAsync(ct))?["email"]?.GetValue<string>() is { Length: > 0 } e ? e : null;
        // A reply in a thread is answered in that thread: its first post is the thread.
        if (question.Reply["post_id"]?.GetValue<string>() is { Length: > 0 } postId)
        {
            using var read = Request(HttpMethod.Get, $"posts/{Uri.EscapeDataString(postId)}");
            using var post = await client.SendAsync(read, ct);
            var root = post.IsSuccessStatusCode && JsonNode.Parse(await post.Content.ReadAsStringAsync(ct))?["root_id"]?.GetValue<string>() is { Length: > 0 } r ? r : postId;
            var reply = (JsonObject)question.Reply.DeepClone();
            reply["root_id"] = root;
            return question with { Email = email, Thread = $"{question.Reply["channel_id"]}:{root}", Reply = reply };
        }
        return question with { Email = email };
    }

    public async Task PostAsync(BotQuestion question, string text, CancellationToken ct)
    {
        if (question.Reply["quote"]?.GetValue<bool>() == true)
        {
            text = $"> {question.Name}: {question.Text.Replace("\n", "\n> ", StringComparison.Ordinal)}\n\n{text}";
        }
        using var request = Request(HttpMethod.Post, "posts");
        request.Content = new StringContent(new JsonObject
        {
            ["channel_id"] = question.Reply["channel_id"]?.DeepClone(), ["root_id"] = question.Reply["root_id"]?.DeepClone() ?? "", ["message"] = BotText.Fit(text, Limit, ""),
        }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.CreateClient(BotEndpoints.Client).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Mattermost said {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{BaseUrl}/api/v4/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", O.BotToken);
        return request;
    }
}
