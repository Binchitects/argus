using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Llm.Api.Schedules;
using Microsoft.Extensions.Options;

namespace Llm.Api.Bots;

/// <summary>
/// Email in: a mail gateway posts each email it receives (from, to, subject, text) to the
/// app with a shared secret. The sender, found by their address, gets the answer by email
/// (and has it in a chat: a reply to the answer, same subject, carries that chat on). A
/// sender with no account gets nothing back, so a forged sender cannot use the app to send mail.
/// </summary>
public sealed class MailBot(Mailer mailer, IOptionsMonitor<BotOptions> options, IOptionsMonitor<MailOptions> mail) : IChatPlatform
{
    private MailInOptions O => options.CurrentValue.Email;

    public string Name => "email";

    public string Title => "Email";

    public int Limit => 200_000;

    /// <summary>Set up: its secret saved, and the app can send email.</summary>
    public bool Ready => O.Secret is { Length: > 0 } && mailer.Configured;

    public PlatformOptions Options => O;

    public bool AnswersStrangers => false;

    /// <summary>Whether the request carries the secret: in X-Mail-Secret, as a bearer token, or as a basic-auth password (https://any:SECRET@host/…).</summary>
    public bool SecretOk(HttpRequest request)
    {
        if (O.Secret is not { Length: > 0 } secret)
        {
            return false;
        }
        var supplied = request.Headers["X-Mail-Secret"].FirstOrDefault();
        var authorization = request.Headers.Authorization.FirstOrDefault() ?? "";
        if (supplied is null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            supplied = authorization["Bearer ".Length..].Trim();
        }
        if (supplied is null && authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pair = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
                supplied = pair[(pair.IndexOf(':', StringComparison.Ordinal) + 1)..];
            }
            catch (FormatException)
            {
                supplied = null;
            }
        }
        return supplied is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>
    /// The question in an email: its subject and text, from its sender. Null, with why, for one
    /// not to answer: no sender, the app's own address (a loop), or a sender the gateway
    /// found forged (spf or dkim given and not "pass").
    /// </summary>
    public BotQuestion? Read(IReadOnlyDictionary<string, string> f, out string? why)
    {
        string? F(string key) => f.TryGetValue(key, out var v) && v.Trim().Length > 0 ? v.Trim() : null;
        why = null;
        if (F("from") is not { } from || !MailAddress.TryCreate(from, out var sender))
        {
            why = "no sender";
            return null;
        }
        if (mail.CurrentValue.From is { Length: > 0 } own && MailAddress.TryCreate(own, out var app) && string.Equals(app.Address, sender.Address, StringComparison.OrdinalIgnoreCase))
        {
            why = "sent by the app itself";
            return null;
        }
        foreach (var check in new[] { "spf", "dkim" })
        {
            if (F(check) is { } verdict && !verdict.Contains("pass", StringComparison.OrdinalIgnoreCase))
            {
                why = $"{check} did not pass ({verdict})";
                return null;
            }
        }
        var subject = F("subject") ?? "";
        var text = F("text") ?? "";
        var question = subject.Length > 0 ? $"{subject}\n\n{text}".Trim() : text;
        var name = sender.DisplayName is { Length: > 0 } d ? d : sender.Address;
        return new BotQuestion(BotText.Subject(subject), sender.Address, name, question, new JsonObject { ["to"] = sender.Address, ["subject"] = subject })
        {
            Email = sender.Address,
        };
    }

    public Task<BotQuestion> ResolveAsync(BotQuestion question, CancellationToken ct) => Task.FromResult(question);

    public Task PostAsync(BotQuestion question, string text, CancellationToken ct)
    {
        var subject = question.Reply["subject"]?.GetValue<string>() is { Length: > 0 } s ? s : "Your question";
        if (!subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase))
        {
            subject = "Re: " + subject;
        }
        return mailer.SendAsync(question.Reply["to"]!.GetValue<string>(), subject, text, ct);
    }

    public string Link(string url, string text) => $"{text}: {url}";

    public string Footer(string link) => $"\n\n--\nThe chat: {link}\nReply to this email to ask more in the same chat.";
}
