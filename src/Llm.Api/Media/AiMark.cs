using System.Text;

namespace Llm.Api.Media;

/// <summary>
/// What a model made says so in its own metadata (the EU AI Act's Article 50(2): machine readable, as far as the format
/// allows): IPTC's digital source type in XMP, which photo tools and platforms read: "trainedAlgorithmicMedia" for what a
/// model made from words, "compositeWithTrainedAlgorithmicMedia" for a picture of the person's that a model changed. A PNG
/// gets an iTXt chunk after its header, a JPEG an APP1 segment after its start; other formats are left as they are.
/// </summary>
public static class AiMark
{
    public const string Generated = "http://cv.iptc.org/newscodes/digitalsourcetype/trainedAlgorithmicMedia";
    public const string Composite = "http://cv.iptc.org/newscodes/digitalsourcetype/compositeWithTrainedAlgorithmicMedia";

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Xmp(string sourceType) => Encoding.UTF8.GetBytes(
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
        + "<rdf:Description rdf:about=\"\" xmlns:Iptc4xmpExt=\"http://iptc.org/std/Iptc4xmpExt/2008-02-29/\" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\""
        + $" Iptc4xmpExt:DigitalSourceType=\"{sourceType}\" xmp:CreatorTool=\"Argus Arena\"/></rdf:RDF></x:xmpmeta><?xpacket end=\"r\"?>");

    /// <summary>The file with the mark, once (a file marked already, or of another type, comes back as it was).</summary>
    /// <param name="composite">A picture of the person's that a model changed, not one it made from words.</param>
    public static byte[] Mark(byte[] data, string contentType, bool composite = false)
    {
        if (IsMarked(data))
        {
            return data;
        }
        var xmp = Xmp(composite ? Composite : Generated);
        return contentType switch
        {
            "image/png" when data.AsSpan().StartsWith(PngSignature) && data.Length > 33 => Png(data, xmp),
            "image/jpeg" when data.Length > 2 && data[0] == 0xFF && data[1] == 0xD8 => Jpeg(data, xmp),
            _ => data,
        };
    }

    /// <summary>Whether it says it was made, or changed, by a model.</summary>
    public static bool IsMarked(ReadOnlySpan<byte> data) => data.IndexOf("digitalsourcetype/trainedAlgorithmicMedia"u8) >= 0
        || data.IndexOf("digitalsourcetype/compositeWithTrainedAlgorithmicMedia"u8) >= 0;

    private static byte[] Png(byte[] png, byte[] xmp)
    {
        // keyword, null, compression flag and method, language tag and its null, translated keyword and its null, the text.
        List<byte> body = [.. Encoding.ASCII.GetBytes("XML:com.adobe.xmp"), 0, 0, 0, 0, 0, .. xmp];
        var chunk = new byte[12 + body.Count];
        WriteBigEndian(chunk, 0, (uint)body.Count);
        Encoding.ASCII.GetBytes("iTXt").CopyTo(chunk, 4);
        body.CopyTo(chunk, 8);
        WriteBigEndian(chunk, 8 + body.Count, Crc32(chunk.AsSpan(4, 4 + body.Count)));
        // After the signature and IHDR (8 + 4 + 4 + 13 + 4 bytes), which comes first.
        const int AfterHeader = 33;
        return [.. png.AsSpan(0, AfterHeader), .. chunk, .. png.AsSpan(AfterHeader)];
    }

    private static byte[] Jpeg(byte[] jpeg, byte[] xmp)
    {
        var name = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        var length = 2 + name.Length + xmp.Length;
        byte[] segment = [0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. name, .. xmp];
        return [.. jpeg.AsSpan(0, 2), .. segment, .. jpeg.AsSpan(2)];
    }

    private static void WriteBigEndian(byte[] to, int at, uint value)
    {
        to[at] = (byte)(value >> 24);
        to[at + 1] = (byte)(value >> 16);
        to[at + 2] = (byte)(value >> 8);
        to[at + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        }
        return c;
    })];

    /// <summary>PNG's CRC-32 over a chunk's type and data.</summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return c ^ 0xFFFFFFFFu;
    }
}
