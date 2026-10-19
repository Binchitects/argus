using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeArena;

/// <summary>A file attached in the IDE's chat: uploaded to Arena (its id there), with its name, its kind (text, image, audio, video, file) and type.</summary>
internal sealed record Upload(string Id, string Name, string Kind, string ContentType);

/// <summary>
/// Files the person attached to a message in the IDE (clip, paste, drop), as the web chat attaches them: uploaded to Arena
/// through code-arena (<see cref="ArenaRelay"/>), which reads a document's text (PDF, Word, Excel, PowerPoint, text, code)
/// and keeps a picture, a sound or a video. The model reads a document's text after the message, between
/// <see cref="Marker"/> and <see cref="End"/>, and sees a picture when it can (a part of its own; named otherwise). The
/// session keeps that; <see cref="Split"/> takes the files out again, for the page (shown as the web shows attachments) and
/// for the sync (Arena keeps them as the question's attachments, so the web shows them and its model reads them itself).
/// </summary>
internal static partial class Uploads
{
    /// <summary>The most files one message carries.</summary>
    public const int Most = 10;

    /// <summary>The most of one document's text a message carries; more is cut.</summary>
    public const int MaxChars = Mentions.MaxChars;

    public const string Marker = "\n\n<uploaded>";
    public const string End = "\n</uploaded>";

    private static readonly string[] Kinds = ["text", "image", "audio", "video", "file"];

    /// <summary>The files a request names (<c>attachments</c>: Arena's {id, fileName, kind, contentType}); null when none, or one is not such a file.</summary>
    public static List<Upload>? From(JsonObject? body, out string? wrong)
    {
        wrong = null;
        if (body?["attachments"] is not JsonArray { Count: > 0 } all)
        {
            return null;
        }
        if (all.Count > Most)
        {
            wrong = $"At most {Most} files go with one message.";
            return null;
        }
        var files = new List<Upload>();
        foreach (var a in all)
        {
            if (a is not JsonObject o || !Guid.TryParse(o.Str("id"), out var id) || o.Str("kind") is not { } kind || !Kinds.Contains(kind))
            {
                wrong = "Each file is one uploaded to Arena: {id, fileName, kind, contentType}.";
                return null;
            }
            files.Add(new Upload(id.ToString(), Clean(o.Str("fileName") ?? "file"), kind, Clean(o.Str("contentType") ?? "application/octet-stream")));
        }
        return files;
    }

    /// <summary>A name or a type as an attribute holds it: no quotes, no line breaks, no angle brackets.</summary>
    private static string Clean(string value)
    {
        var text = new string([.. value.Select(c => c is '"' or '<' or '>' || char.IsControl(c) ? '_' : c)]).Trim();
        return text.Length <= 200 ? text : text[..200];
    }

    /// <summary>
    /// What the model reads of the files, after the message: each document's text (read from Arena with the key), and each
    /// picture as a part of its own when <paramref name="vision"/> (fetched as a data URL), else by its name. A file that
    /// cannot be read is named, with why.
    /// </summary>
    public static async Task<(string Section, JsonArray Pictures)> ReadAsync(IReadOnlyList<Upload> files, Config config, HttpClient http, bool vision, CancellationToken ct)
    {
        var sb = new StringBuilder(Marker);
        var pictures = new JsonArray();
        foreach (var f in files)
        {
            sb.Append($"\n<upload id=\"{f.Id}\" name=\"{f.Name}\" kind=\"{f.Kind}\" type=\"{f.ContentType}\">");
            switch (f.Kind)
            {
                case "text":
                    var text = await FetchAsync(f, config, http, MaxChars * 4L, ct) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
                    if (text is null)
                    {
                        sb.Append("(its text could not be read from Arena)</upload>");
                        continue;
                    }
                    if (text.Length > MaxChars)
                    {
                        text = text[..MaxChars] + $"\n… (cut: {text.Length:N0} characters in all)";
                    }
                    // Its words cannot close the block, nor the list, early.
                    sb.Append('\n').Append(text.Replace("</upload", "<\\/upload", StringComparison.Ordinal).Replace("<upload", "<\\upload", StringComparison.Ordinal)).Append("\n</upload>");
                    break;
                case "image" when vision:
                    if (await FetchAsync(f, config, http, ChatSync.MostImageBytes, ct) is { } picture)
                    {
                        pictures.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{f.ContentType};base64,{Convert.ToBase64String(picture)}" } });
                        sb.Append("(the picture is shown with this message)</upload>");
                    }
                    else
                    {
                        sb.Append("(a picture that could not be read from Arena)</upload>");
                    }
                    break;
                case "image":
                    sb.Append("(a picture: this model cannot see pictures, ask the person what it shows if it matters)</upload>");
                    break;
                default:
                    sb.Append($"({(f.Kind is "audio" ? "a sound" : f.Kind is "video" ? "a video" : "a file")}: not read here)</upload>");
                    break;
            }
        }
        return (sb.Append(End).ToString(), pictures);
    }

    /// <summary>A file's content from Arena, up to <paramref name="most"/> bytes; null when it cannot be had.</summary>
    private static async Task<byte[]?> FetchAsync(Upload f, Config config, HttpClient http, long most, CancellationToken ct)
    {
        if (config.Url is not { Length: > 0 } url || config.ApiKey is not { Length: > 0 } key)
        {
            return null;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/api/chat/attachments/{f.Id}/content");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (!res.IsSuccessStatusCode || res.Content.Headers.ContentLength > most)
            {
                return null;
            }
            var bytes = await res.Content.ReadAsByteArrayAsync(limit.Token);
            return bytes.Length <= most ? bytes : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    [GeneratedRegex("""^<upload id="(?<id>[^"]*)" name="(?<name>[^"]*)" kind="(?<kind>[^"]*)" type="(?<type>[^"]*)">""")]
    private static partial Regex Head();

    /// <summary>A message the model reads split into the rest (what was typed, and what else went with it) and the files attached; none when it has no such list at its end.</summary>
    public static (string Text, List<Upload> Files) Split(string content)
    {
        var at = content.LastIndexOf(Marker, StringComparison.Ordinal);
        if (at < 0 || !content.EndsWith(End, StringComparison.Ordinal))
        {
            return (content, []);
        }
        var files = new List<Upload>();
        foreach (var line in content[(at + Marker.Length)..^End.Length].Split('\n'))
        {
            if (Head().Match(line) is { Success: true } m)
            {
                files.Add(new Upload(m.Groups["id"].Value, m.Groups["name"].Value, m.Groups["kind"].Value, m.Groups["type"].Value));
            }
        }
        return (content[..at], files);
    }

    /// <summary>The files as Arena's chat lists a message's attachments (the page shows them, and the sync sends them so).</summary>
    public static JsonArray Shown(IEnumerable<Upload> files) => new([.. files.Select(f => (JsonNode)new JsonObject
    {
        ["id"] = f.Id, ["fileName"] = f.Name, ["kind"] = f.Kind, ["contentType"] = f.ContentType, ["size"] = 0, ["truncated"] = false,
    })]);
}
