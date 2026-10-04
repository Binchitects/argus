using System.Text.Json;
using System.Text.Json.Nodes;
using Llm.Api.Endpoints;
using Llm.Api.Identity;
using Llm.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Llm.Api.Bots;

/// <summary>
/// Where the chat platforms and the mail gateway reach the app: Slack's events, Mattermost's
/// outgoing webhooks and slash commands, Teams' Bot Connector, and inbound email. None has a
/// session; each is checked by its platform's own means (a signature, a token, a signed
/// JWT, a shared secret). Admins see which are set up, and their addresses.
/// </summary>
public static class BotEndpoints
{
    /// <summary>The HTTP client for the platforms' APIs.</summary>
    public const string Client = "bots";

    /// <summary>The largest body a platform may post.</summary>
    private const int MaxBody = 1_000_000;

    public static void AddBots(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<BotOptions>(config.GetSection("Bots"));
        services.AddHttpClient(Client, c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<SlackBot>();
        services.AddSingleton<MattermostBot>();
        services.AddSingleton<TeamsBot>();
        services.AddSingleton<MailBot>();
        services.AddScoped<BotConversation>();
        services.AddSingleton<BotAnswers>();
        services.AddHostedService(sp => sp.GetRequiredService<BotAnswers>());
    }

    public static void MapBots(this IEndpointRouteBuilder app)
    {
        var bots = app.MapGroup("/api/bots").AllowAnonymous().DisableAntiforgery();
        bots.MapPost("/slack", SlackAsync);
        bots.MapPost("/mattermost", MattermostAsync);
        bots.MapPost("/teams", TeamsAsync);
        app.MapPost("/api/mail/inbound", MailAsync).AllowAnonymous().DisableAntiforgery();
        app.MapGet("/api/admin/bots", StatusAsync).RequireAuthorization(AdminEndpoints.Policy);
    }

    private static async Task<string?> BodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBody)
        {
            return null;
        }
        using var reader = new StreamReader(request.Body);
        var buffer = new char[MaxBody + 1];
        var read = await reader.ReadBlockAsync(buffer, ct);
        return read > MaxBody ? null : new string(buffer, 0, read);
    }

    private static JsonObject? ParseObject(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The fields of a form or a JSON object, as text.</summary>
    private static async Task<Dictionary<string, string>?> FieldsAsync(HttpRequest request, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (request.HasFormContentType)
        {
            if (request.ContentLength > MaxBody * 10)
            {
                return null;
            }
            var form = await request.ReadFormAsync(ct);
            foreach (var (k, v) in form)
            {
                fields[k] = v.ToString();
            }
            return fields;
        }
        if (await BodyAsync(request, ct) is not { } body || ParseObject(body) is not { } json)
        {
            return null;
        }
        foreach (var (k, v) in json)
        {
            fields[k] = v is JsonValue value && value.TryGetValue<string>(out var s) ? s : v?.ToJsonString() ?? "";
        }
        return fields;
    }

    private static IResult NotSetUp(string platform) => Results.Json(new { error = $"{platform} is not set up here (Settings → Chat bots)." }, statusCode: 404);

    private static async Task<IResult> SlackAsync(HttpRequest request, SlackBot slack, BotAnswers answers, CancellationToken ct)
    {
        if (!slack.Ready)
        {
            return NotSetUp(slack.Title);
        }
        if (await BodyAsync(request, ct) is not { } body)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        if (!slack.SignatureOk(request.Headers["X-Slack-Request-Timestamp"].FirstOrDefault(), request.Headers["X-Slack-Signature"].FirstOrDefault(), body))
        {
            return Results.Json(new { error = "the signature does not match" }, statusCode: 401);
        }
        var json = ParseObject(body);
        switch (json?["type"]?.GetValue<string>())
        {
            case "url_verification":
                // Slack checks the address when it is saved: it gets its challenge back.
                return Results.Ok(new { challenge = json["challenge"]?.GetValue<string>() });
            case "event_callback" when json["event"] is JsonObject e && slack.FirstTime(json["event_id"]?.GetValue<string>()):
                if (SlackBot.Read(e, out var channel, out var direct) is not { } question)
                {
                    return Results.Ok(new { status = "ignored" });
                }
                if (!direct && !slack.Options.Allows(channel))
                {
                    answers.Start(slack, question, refusal: "I do not answer in this channel. Ask me in a direct message, or ask an admin to add the channel.");
                    return Results.Ok(new { status = "refused", reason = "not an allowed channel" });
                }
                answers.Start(slack, question);
                return Results.Ok(new { status = "accepted" });
            default:
                return Results.Ok(new { status = "ignored" });
        }
    }

    private static async Task<IResult> MattermostAsync(HttpRequest request, MattermostBot mattermost, BotAnswers answers, CancellationToken ct)
    {
        if (!mattermost.Ready)
        {
            return NotSetUp(mattermost.Title);
        }
        var fields = await FieldsAsync(request, ct);
        if (fields is null || !mattermost.TokenOk(fields.GetValueOrDefault("token")))
        {
            return Results.Json(new { error = "unknown token" }, statusCode: 401);
        }
        if (MattermostBot.Read(fields, out var channel) is not { } question)
        {
            return Results.Ok(new { });
        }
        var command = fields.ContainsKey("command");
        if (!mattermost.Options.Allows(channel, fields.GetValueOrDefault("channel_name")))
        {
            // A slash command is told at once, to the asker only; a webhook post is left alone.
            return Results.Ok(command ? new { response_type = "ephemeral", text = "I do not answer in this channel. Ask an admin to add it." } : new { response_type = "", text = "" });
        }
        answers.Start(mattermost, question);
        return Results.Ok(command ? new { response_type = "ephemeral", text = "Asking as you… the answer comes in this channel." } : new { response_type = "", text = "" });
    }

    private static async Task<IResult> TeamsAsync(HttpRequest request, TeamsBot teams, BotAnswers answers, CancellationToken ct)
    {
        if (!teams.Ready)
        {
            return NotSetUp(teams.Title);
        }
        if (await BodyAsync(request, ct) is not { } body || ParseObject(body) is not { } activity)
        {
            return Results.BadRequest(new { error = "not an activity" });
        }
        if (!await teams.AuthenticAsync(request.Headers.Authorization.FirstOrDefault(), activity, ct))
        {
            return Results.Json(new { error = "the token is not the Bot Connector's" }, statusCode: 401);
        }
        if (TeamsBot.Read(activity, out var channel, out var direct) is not { } question)
        {
            return Results.Ok();
        }
        if (!direct && !teams.Options.Allows(channel, activity["channelData"]?["team"]?["id"]?.GetValue<string>()))
        {
            answers.Start(teams, question, refusal: "I do not answer in this channel. Ask me in a chat, or ask an admin to add the channel.");
            return Results.Ok();
        }
        answers.Start(teams, question);
        return Results.Ok();
    }

    private static async Task<IResult> MailAsync(HttpRequest request, MailBot mail, BotAnswers answers, Audit audit, CancellationToken ct)
    {
        if (!mail.Ready)
        {
            return NotSetUp("Email in");
        }
        if (!mail.SecretOk(request))
        {
            return Results.Json(new { error = "wrong secret" }, statusCode: 401);
        }
        if (await FieldsAsync(request, ct) is not { } fields)
        {
            return Results.BadRequest(new { error = "a form or a JSON object with from, to, subject and text" });
        }
        if (mail.Read(fields, out var why) is not { } question)
        {
            await audit.WriteAsync("bot.refused", "email", success: false, detail: why);
            return Results.Ok(new { status = "ignored", reason = why });
        }
        answers.Start(mail, question);
        return Results.Accepted(value: new { status = "accepted" });
    }

    /// <summary>Each platform: set up or not, the address to give it, and the chats it has.</summary>
    private static async Task<IResult> StatusAsync(SlackBot slack, MattermostBot mattermost, TeamsBot teams, MailBot mail, AppDbContext db, IOptions<AuthOptions> auth,
        CancellationToken ct)
    {
        var origin = auth.Value.Origin;
        var threads = await db.BotThreads.AsNoTracking().GroupBy(t => t.Platform).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        object View(IChatPlatform p, string path) => new { name = p.Name, title = p.Title, ready = p.Ready, url = $"{origin}{path}", threads = threads.GetValueOrDefault(p.Name) };
        return Results.Ok(new
        {
            platforms = new[]
            {
                View(slack, "/api/bots/slack"), View(mattermost, "/api/bots/mattermost"), View(teams, "/api/bots/teams"), View(mail, "/api/mail/inbound"),
            },
        });
    }
}
