using System.Text;
using UglyToad.PdfPig;

namespace Llm.Api.Chat;

public sealed class AttachmentException(string message) : Exception(message);

/// <summary>
/// A file's text, for the model. Text and code as they are; PDF page by page;
/// Word, Excel, PowerPoint, OpenDocument and RTF as their text (<see cref="Documents"/>);
/// nothing else binary.
/// </summary>
public static class Attachments
{
    /// <summary>The pictures the chat takes. Never SVG: it is a document that can carry script.</summary>
    public static readonly IReadOnlyList<string> ImageTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    /// <summary>The image's type from its first bytes (not its name, not what the browser said), or null for anything else.</summary>
    public static string? ImageType(string fileName, byte[] bytes)
    {
        _ = fileName;
        ReadOnlySpan<byte> b = bytes;
        if (b.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }
        if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }
        if (b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }

    /// <summary>The text, whether it was cut at <paramref name="maxChars"/>, and whether it was converted (a PDF or a document, not text as it is).</summary>
    public static (string Text, bool Truncated, bool Converted) Extract(string fileName, string contentType, byte[] bytes, int maxChars)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        string text;
        var converted = true;
        if (ext == ".pdf" || contentType == "application/pdf")
        {
            try
            {
                using var pdf = PdfDocument.Open(bytes);
                var sb = new StringBuilder();
                foreach (var page in pdf.GetPages())
                {
                    sb.Append("--- page ").Append(page.Number).Append(" ---\n").Append(page.Text).Append('\n');
                    if (sb.Length > maxChars)
                    {
                        break;
                    }
                }
                text = sb.ToString();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new AttachmentException($"{fileName} could not be read as a PDF.");
            }
            if (string.IsNullOrWhiteSpace(text.Replace("--- page", "", StringComparison.Ordinal)))
            {
                throw new AttachmentException($"{fileName} has no text layer (a scan?). Only PDFs with text can be read.");
            }
        }
        else if (Documents.Text(fileName, bytes, maxChars) is { } document)
        {
            if (string.IsNullOrWhiteSpace(document))
            {
                throw new AttachmentException($"{fileName} has no text in it.");
            }
            text = document;
        }
        else if (LooksBinary(bytes))
        {
            throw new AttachmentException($"{fileName} is not a file the chat can read. Attach text, code, Markdown, CSV, JSON, logs, PDFs, Word, Excel, PowerPoint or OpenDocument files, or pictures.");
        }
        else
        {
            converted = false;
            text = new UTF8Encoding(false, false).GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
            if (text.Length > 0 && text[0] == '﻿')
            {
                text = text[1..];
            }
        }
        return text.Length > maxChars ? (text[..maxChars], true, converted) : (text, false, converted);
    }

    /// <summary>A NUL byte in the first 8 KB: what git calls binary.</summary>
    private static bool LooksBinary(byte[] bytes) => bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);
}
