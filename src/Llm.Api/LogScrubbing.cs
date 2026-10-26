using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Llm.Api.Safeguards;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Llm.Api;

/// <summary>
/// The app's log lines as the JSON console writes them (one object a line, the shape promtail and Loki read), with personal and
/// health data masked first: e-mail addresses, phone, card, IBAN, social security, NHS, national and Medicare numbers, dates of
/// birth and medical record numbers, in the message, its values and an exception's text. IP addresses stay, as the audit log
/// keeps them (security needs them).
/// </summary>
public sealed class ScrubbedJsonFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "scrubbed-json";

    private static readonly string[] Kinds = [.. Pii.AllKinds.Where(k => k != "ip")];

    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Scrub(string text) => Pii.Mask(text, Kinds);

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, Options))
        {
            json.WriteStartObject();
            json.WriteNumber("EventId", logEntry.EventId.Id);
            json.WriteString("LogLevel", logEntry.LogLevel.ToString());
            json.WriteString("Category", logEntry.Category);
            json.WriteString("Message", Scrub(message ?? ""));
            if (logEntry.Exception is not null)
            {
                json.WriteString("Exception", Scrub(logEntry.Exception.ToString()));
            }
            if (logEntry.State is IReadOnlyList<KeyValuePair<string, object?>> state)
            {
                json.WriteStartObject("State");
                json.WriteString("Message", Scrub(message ?? ""));
                foreach (var (key, value) in state)
                {
                    // The template is the code's own words; every value is scrubbed as text.
                    json.WriteString(key, key == "{OriginalFormat}" ? Convert.ToString(value, CultureInfo.InvariantCulture) : Scrub(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
                }
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }
}
