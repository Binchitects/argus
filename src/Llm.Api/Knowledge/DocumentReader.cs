using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Llm.Api.Chat;
using Llm.Api.Chat.Tools;
using Microsoft.Extensions.Options;
using UglyToad.PdfPig;

namespace Llm.Api.Knowledge;

/// <summary>
/// The text of a file for the company's knowledge, whatever it is: text, Markdown, code, CSV, JSON and logs as they are;
/// HTML as its readable text; Word, Excel, PowerPoint and OpenDocument files; PDFs, with the pages that have no text (a
/// scan) read by OCR; pictures (PNG, JPEG, TIFF, BMP, GIF, WebP, HEIC) by OCR; older Office files (.doc, .xls, .ppt),
/// RTF and others LibreOffice opens, converted; e-mails (.eml) with their headers; EPUB books. OCR and conversion run in
/// the sandbox (the sandbox feature: Tesseract, in Knowledge:OcrLanguages, and LibreOffice there), with no network; without
/// it those files are said to need it. An archive's files (.zip) are documents each of their own (<see cref="Members"/>).
/// </summary>
public sealed partial class DocumentReader(SandboxClient sandbox, IOptionsMonitor<KnowledgeOptions> options, Operations.Modules modules)
{
    public const int MaxChars = 2_000_000;

    /// <summary>Pages of a scanned PDF read by OCR in one sandbox run (each run may take up to five minutes).</summary>
    private const int PagesPerRun = 12;

    /// <summary>The most pages of one PDF read by OCR.</summary>
    public const int MaxOcrPages = 300;

    public static readonly string[] Pictures = [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".gif", ".webp", ".heic", ".heif"];

    /// <summary>Files LibreOffice reads that nothing in the app does.</summary>
    public static readonly string[] Converted = [".doc", ".xls", ".ppt", ".rtf", ".pps", ".dot", ".xlt", ".pot", ".wpd", ".wps", ".odg", ".vsd", ".vsdx", ".pub", ".sxw", ".sxc", ".sxi", ".hwp"];

    public static readonly string[] Archives = [".zip"];

    /// <summary>Why a file was read as empty, for the admin page: null when it was read.</summary>
    public sealed record Read(string Text, string? Problem = null);

    /// <summary>Whether this file is one an archive is opened for, not read itself.</summary>
    public static bool IsArchive(string name) => Archives.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>An archive's files (a .zip's), each with its path inside it; folders and hidden files left out, at most 5,000.</summary>
    public static IEnumerable<(string Path, byte[] Bytes)> Members(byte[] archive, long maxBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
        var n = 0;
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.EndsWith('/') || path.Split('/').Any(part => part.StartsWith('.') || part == "__MACOSX") || entry.Length is 0 || entry.Length > maxBytes)
            {
                continue;
            }
            if (++n > 5_000)
            {
                yield break;
            }
            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            yield return (path, ms.ToArray());
        }
    }

    public async Task<Read> ReadAsync(string name, byte[] bytes, CancellationToken ct)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        try
        {
            if (ext is ".html" or ".htm" or ".xhtml")
            {
                var (title, text) = Html.Read(Encoding.UTF8.GetString(bytes));
                return new(title.Length > 0 ? $"# {title}\n\n{text}" : text);
            }
            if (ext == ".eml")
            {
                return new(Email(Encoding.UTF8.GetString(bytes)));
            }
            if (ext == ".epub")
            {
                return new(Epub(bytes));
            }
            if (Pictures.Contains(ext))
            {
                return (await OcrAsync(name, bytes, "picture", [], ct)).Plain;
            }
            if (Converted.Contains(ext))
            {
                return (await OcrAsync(name, bytes, "convert", [], ct)).Plain;
            }
            if (ext == ".pdf")
            {
                return await PdfAsync(name, bytes, ct);
            }
            return new(Attachments.Extract(name, "", bytes, MaxChars).Text);
        }
        catch (AttachmentException ex)
        {
            return new("", ex.Message);
        }
    }

    /// <summary>A PDF's text layer, page by page; the pages with next to no text (scanned) read by OCR.</summary>
    private async Task<Read> PdfAsync(string name, byte[] bytes, CancellationToken ct)
    {
        var pages = new List<string>();
        try
        {
            using var pdf = PdfDocument.Open(bytes);
            foreach (var page in pdf.GetPages())
            {
                pages.Add(page.Text);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // A PDF the reader cannot open: the sandbox draws its pages instead.
            return (await OcrAsync(name, bytes, "convert", [], ct)).Plain;
        }
        var scanned = pages.Select((text, i) => (text, page: i + 1)).Where(p => p.text.Count(char.IsLetterOrDigit) < 20).Select(p => p.page).ToList();
        string? problem = null;
        if (scanned.Count > 0)
        {
            var read = await OcrAsync(name, bytes, "pdf", [.. scanned.Take(MaxOcrPages)], ct);
            problem = read.Problem;
            if (read.Pages is { } byPage)
            {
                foreach (var (page, text) in byPage)
                {
                    pages[page - 1] = text;
                }
            }
            if (scanned.Count > MaxOcrPages)
            {
                problem = $"Only the first {MaxOcrPages} scanned pages were read.";
            }
        }
        var sb = new StringBuilder();
        for (var i = 0; i < pages.Count && sb.Length < MaxChars; i++)
        {
            sb.Append("--- page ").Append(i + 1).Append(" ---\n").Append(pages[i]).Append('\n');
        }
        return new(sb.Length > MaxChars ? sb.ToString(0, MaxChars) : sb.ToString(), problem);
    }

    private sealed record OcrRead(string Text, string? Problem, IReadOnlyDictionary<int, string>? Pages = null)
    {
        public Read Plain => new(Text, Problem);
    }

    private async Task<OcrRead> OcrAsync(string name, byte[] bytes, string kind, IReadOnlyList<int> pages, CancellationToken ct)
    {
        if (!await modules.HasAsync("sandbox", ct))
        {
            return new("", kind == "convert"
                ? $"{name}: opening this kind of file needs the sandbox feature (LibreOffice), which is off."
                : $"{name}: reading text from pictures and scans needs the sandbox feature (OCR), which is off.");
        }
        var file = "in" + Path.GetExtension(name).ToLowerInvariant();
        var langs = options.CurrentValue.OcrLanguages;
        var texts = new SortedDictionary<int, string>();
        var whole = new StringBuilder();
        string? problem = null;
        IEnumerable<IReadOnlyList<int>> batches = kind == "pdf" ? pages.Chunk(PagesPerRun) : [[]];
        foreach (var batch in batches)
        {
            var spec = new JsonObject
            {
                ["file"] = file, ["kind"] = kind, ["langs"] = langs, ["pages"] = new JsonArray([.. batch.Select(p => (JsonNode)p)]),
            };
            SandboxResult result;
            try
            {
                result = await sandbox.RunAsync(Script, [new SandboxFile(file, bytes), new SandboxFile("spec.json", Encoding.UTF8.GetBytes(spec.ToJsonString()))], 300, ct);
            }
            catch (SandboxException ex)
            {
                return new("", $"{name}: the sandbox could not read it: {ex.Message}");
            }
            var answer = result.Files.FirstOrDefault(f => f.Name == "result.json");
            if (answer is null || JsonNode.Parse(answer.Bytes) is not JsonObject read)
            {
                problem = $"{name}: {(result.TimedOut ? "reading it took too long" : "it could not be read")}" + (result.Stderr.Length > 0 ? $" ({Last(result.Stderr)})" : "");
                continue;
            }
            if (read["error"]?.GetValue<string>() is { } error)
            {
                problem = $"{name}: {error}";
            }
            if (read["pages"] is JsonObject byPage)
            {
                foreach (var (page, text) in byPage)
                {
                    texts[int.Parse(page, System.Globalization.CultureInfo.InvariantCulture)] = text?.GetValue<string>() ?? "";
                }
            }
            whole.Append(read["text"]?.GetValue<string>() ?? "");
        }
        return new(whole.ToString(), problem, kind == "pdf" ? texts : null);
    }

    private static string Last(string stderr) => stderr.Trim().Split('\n')[^1].Trim();

    /// <summary>
    /// In the sandbox: spec.json says the file, what to do and the pages. A picture is read by Tesseract; a PDF's pages are
    /// drawn at 200 dpi and read; another file is turned into a PDF by LibreOffice, its text taken, and its pages without
    /// text read. The text goes into result.json (stdout keeps only 64 KB).
    /// </summary>
    private const string Script = """
        import json, os, subprocess, glob
        os.environ["HOME"] = "/tmp"
        spec = json.load(open("spec.json"))
        name, kind, langs = spec["file"], spec["kind"], spec["langs"]
        out = {"text": "", "pages": {}}
        def ocr(path):
            import pytesseract
            from PIL import Image
            try:
                from pillow_heif import register_heif_opener
                register_heif_opener()
            except Exception:
                pass
            image = Image.open(path)
            frames = []
            for i in range(getattr(image, "n_frames", 1)):
                image.seek(i)
                frames.append(pytesseract.image_to_string(image.convert("RGB"), lang=langs))
            return "\n".join(frames)
        def pdf_pages(pdf, pages):
            for p in pages:
                for f in glob.glob("pg-*.png"):
                    os.remove(f)
                subprocess.run(["pdftoppm", "-r", "200", "-f", str(p), "-l", str(p), "-png", pdf, "pg"], check=True, timeout=120)
                drawn = sorted(glob.glob("pg-*.png"))
                out["pages"][str(p)] = ocr(drawn[0]) if drawn else ""
        try:
            if kind == "picture":
                out["text"] = ocr(name)
            elif kind == "pdf":
                pdf_pages(name, spec["pages"])
            else:
                os.makedirs("conv", exist_ok=True)
                subprocess.run(["soffice", "--headless", "--convert-to", "pdf", "--outdir", "conv", name], check=True, timeout=240,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env={**os.environ, "HOME": "/tmp"})
                pdf = os.path.join("conv", os.path.splitext(name)[0] + ".pdf")
                text = subprocess.run(["pdftotext", "-layout", pdf, "-"], capture_output=True, text=True, timeout=120).stdout
                if sum(c.isalnum() for c in text) < 20:
                    count = int(subprocess.run(["pdfinfo", pdf], capture_output=True, text=True).stdout.split("Pages:")[1].split()[0])
                    pdf_pages(pdf, range(1, min(count, 60) + 1))
                    text = "\n".join(out["pages"][k] for k in sorted(out["pages"], key=int))
                    out["pages"] = {}
                out["text"] = text
        except Exception as e:
            out["error"] = type(e).__name__ + ": " + str(e)[:300]
        json.dump(out, open("result.json", "w"))
        """;

    /// <summary>An e-mail (RFC 822 / MIME): its subject, from, to and date, then its text (the plain part, else the HTML part's text).</summary>
    public static string Email(string raw)
    {
        var (headers, body) = SplitHeaders(raw);
        var sb = new StringBuilder();
        foreach (var h in new[] { "Subject", "From", "To", "Cc", "Date" })
        {
            if (headers.TryGetValue(h, out var v))
            {
                sb.Append(h).Append(": ").Append(DecodeWords(v)).Append('\n');
            }
        }
        sb.Append('\n').Append(BodyText(headers, body));
        return sb.ToString().Trim();
    }

    private static (Dictionary<string, string> Headers, string Body) SplitHeaders(string raw)
    {
        raw = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        var end = raw.IndexOf("\n\n", StringComparison.Ordinal);
        var head = end < 0 ? raw : raw[..end];
        var body = end < 0 ? "" : raw[(end + 2)..];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        foreach (var line in head.Split('\n'))
        {
            if (line.Length > 0 && char.IsWhiteSpace(line[0]) && last is not null)
            {
                headers[last] += " " + line.Trim();
            }
            else if (line.IndexOf(':') is var colon and > 0)
            {
                last = line[..colon].Trim();
                headers[last] = line[(colon + 1)..].Trim();
            }
        }
        return (headers, body);
    }

    private static string BodyText(Dictionary<string, string> headers, string body)
    {
        var type = headers.GetValueOrDefault("Content-Type") ?? "text/plain";
        if (type.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) && Boundary().Match(type) is { Success: true } b)
        {
            var parts = body.Split("--" + b.Groups[1].Value).Skip(1).Where(p => !p.StartsWith("--", StringComparison.Ordinal)).Select(p => SplitHeaders(p.TrimStart('\n'))).ToList();
            var plain = parts.FirstOrDefault(p => (p.Headers.GetValueOrDefault("Content-Type") ?? "text/plain").StartsWith("text/plain", StringComparison.OrdinalIgnoreCase));
            if (plain.Headers is not null)
            {
                return Decode(plain.Headers, plain.Body);
            }
            var nested = parts.FirstOrDefault(p => (p.Headers.GetValueOrDefault("Content-Type") ?? "").StartsWith("multipart/", StringComparison.OrdinalIgnoreCase));
            if (nested.Headers is not null)
            {
                return BodyText(nested.Headers, nested.Body);
            }
            var html = parts.FirstOrDefault(p => (p.Headers.GetValueOrDefault("Content-Type") ?? "").StartsWith("text/html", StringComparison.OrdinalIgnoreCase));
            return html.Headers is not null ? Html.Read(Decode(html.Headers, html.Body)).Text : "";
        }
        var text = Decode(headers, body);
        return type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ? Html.Read(text).Text : text;
    }

    private static string Decode(Dictionary<string, string> headers, string body)
    {
        var encoding = (headers.GetValueOrDefault("Content-Transfer-Encoding") ?? "").Trim().ToLowerInvariant();
        var charset = Charset().Match(headers.GetValueOrDefault("Content-Type") ?? "") is { Success: true } c ? c.Groups[1].Value : "utf-8";
        Encoding enc;
        try
        {
            enc = Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            enc = Encoding.UTF8;
        }
        if (encoding == "base64")
        {
            try
            {
                return enc.GetString(Convert.FromBase64String(string.Concat(body.Where(ch => !char.IsWhiteSpace(ch)))));
            }
            catch (FormatException)
            {
                return body;
            }
        }
        return encoding == "quoted-printable" ? QuotedPrintable(body, enc) : body;
    }

    private static string QuotedPrintable(string s, Encoding enc)
    {
        s = s.Replace("=\n", "", StringComparison.Ordinal);
        var bytes = new List<byte>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '=' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
            {
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(s[i].ToString()));
            }
        }
        return enc.GetString([.. bytes]);
    }

    /// <summary>Encoded words in a header (=?UTF-8?B?...?= and =?UTF-8?Q?...?=) as their text.</summary>
    private static string DecodeWords(string value) => EncodedWord().Replace(value, m =>
    {
        try
        {
            var enc = Encoding.GetEncoding(m.Groups["cs"].Value);
            return string.Equals(m.Groups["e"].Value, "B", StringComparison.OrdinalIgnoreCase)
                ? enc.GetString(Convert.FromBase64String(m.Groups["t"].Value))
                : QuotedPrintable(m.Groups["t"].Value.Replace('_', ' '), enc);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return m.Value;
        }
    });

    /// <summary>An EPUB book: its chapters' text in the order of their files.</summary>
    public static string Epub(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            using var reader = new StreamReader(entry.Open());
            var (title, text) = Html.Read(reader.ReadToEnd());
            if (text.Length > 0)
            {
                sb.Append(title.Length > 0 ? $"# {title}\n\n" : "").Append(text).Append("\n\n");
            }
            if (sb.Length > MaxChars)
            {
                break;
            }
        }
        return sb.ToString().Trim();
    }

    [GeneratedRegex(@"boundary=""?([^"";]+)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Boundary();

    [GeneratedRegex(@"charset=""?([A-Za-z0-9_\-]+)""?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Charset();

    [GeneratedRegex(@"=\?(?<cs>[^?]+)\?(?<e>[BbQq])\?(?<t>[^?]*)\?=", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedWord();
}
