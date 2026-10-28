using System.Buffers.Binary;
using System.Text;

namespace Llm.Api.Media;

/// <summary>
/// What a model made says so in its own metadata (the EU AI Act's Article 50(2): machine readable, as far as the format
/// allows): IPTC's digital source type in XMP, which photo tools and platforms read: "trainedAlgorithmicMedia" for what a
/// model made from words, "compositeWithTrainedAlgorithmicMedia" for a picture of the person's that a model changed. Where
/// XMP's own spec (part 3) puts it: a PNG gets an iTXt chunk after its header, a JPEG an APP1 segment after its start, an
/// MP3 a PRIV frame in its ID3v2 tag, an MP4 or a QuickTime movie a uuid box at its end, a WAV or an AVI a _PMX chunk at its
/// end. WebM has no place for XMP: it gets a Matroska tag, DIGITAL_SOURCE_TYPE, at the end of its segment. What is added
/// at the end leaves every offset before it as it was. Other formats, and a file whose layout is not the plain one (an
/// MP3's tag unsynchronised, an MP4 whose last box runs to the end), are left as they are.
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
            "audio/mpeg" => Mp3(data, xmp),
            "video/mp4" or "video/quicktime" when Boxes(data) => [.. data, .. Box(xmp)],
            "audio/wav" or "video/x-msvideo" when Riff(data) => Riff(data, xmp),
            "video/webm" => WebM(data, composite ? Composite : Generated),
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

    private static byte[] Mp3(byte[] mp3, byte[] xmp)
    {
        // A PRIV frame owned by "XMP": first in the file's ID3v2.3 or 2.4 tag, or in a tag of its own before the sound.
        byte[] body = [.. "XMP"u8, 0, .. xmp];
        if (mp3.Length >= 10 && mp3.AsSpan(0, 3).SequenceEqual("ID3"u8))
        {
            // Unsynchronised, with an extended header or a footer, or another version: left as it is.
            if (mp3[3] is not (3 or 4) || (mp3[5] & 0xF0) != 0)
            {
                return mp3;
            }
            var frame = Frame(body, syncsafe: mp3[3] == 4);
            var header = mp3[..10];
            WriteSyncsafe(header, 6, Syncsafe(mp3.AsSpan(6, 4)) + frame.Length);
            return [.. header, .. frame, .. mp3.AsSpan(10)];
        }
        // Not an MPEG frame's sync either: not an MP3, left as it is.
        if (mp3.Length < 2 || mp3[0] != 0xFF || (mp3[1] & 0xE0) != 0xE0)
        {
            return mp3;
        }
        var own = Frame(body, syncsafe: false);
        byte[] tag = [.. "ID3"u8, 3, 0, 0, 0, 0, 0, 0];
        WriteSyncsafe(tag, 6, own.Length);
        return [.. tag, .. own, .. mp3];
    }

    private static byte[] Frame(byte[] body, bool syncsafe)
    {
        byte[] frame = [.. "PRIV"u8, 0, 0, 0, 0, 0, 0, .. body];
        if (syncsafe)
        {
            WriteSyncsafe(frame, 4, body.Length);
        }
        else
        {
            WriteBigEndian(frame, 4, (uint)body.Length);
        }
        return frame;
    }

    private static int Syncsafe(ReadOnlySpan<byte> b) => (b[0] << 21) | (b[1] << 14) | (b[2] << 7) | b[3];

    private static void WriteSyncsafe(byte[] to, int at, int value)
    {
        for (var i = 0; i < 4; i++)
        {
            to[at + i] = (byte)((value >> (21 - 7 * i)) & 0x7F);
        }
    }

    private static readonly byte[] XmpUuid = Convert.FromHexString("BE7ACFCB97A942E89C71999491E3AFAC");

    /// <summary>Whether the file is top-level boxes from ftyp to its very end, the last with its own size.</summary>
    private static bool Boxes(byte[] mp4)
    {
        if (mp4.Length < 8 || !mp4.AsSpan(4, 4).SequenceEqual("ftyp"u8))
        {
            return false;
        }
        long at = 0;
        while (at + 8 <= mp4.Length)
        {
            long size = ReadBigEndian(mp4, (int)at);
            if (size == 1 && at + 16 <= mp4.Length)
            {
                size = ((long)ReadBigEndian(mp4, (int)at + 8) << 32) | ReadBigEndian(mp4, (int)at + 12);
            }
            if (size < 8)
            {
                return false;
            }
            at += size;
        }
        return at == mp4.Length;
    }

    private static byte[] Box(byte[] xmp)
    {
        var box = new byte[8 + XmpUuid.Length + xmp.Length];
        WriteBigEndian(box, 0, (uint)box.Length);
        "uuid"u8.CopyTo(box.AsSpan(4));
        XmpUuid.CopyTo(box, 8);
        xmp.CopyTo(box, 8 + XmpUuid.Length);
        return box;
    }

    /// <summary>Whether the file is one RIFF whose size runs to its end.</summary>
    private static bool Riff(byte[] riff) => riff.Length >= 12 && riff.AsSpan(0, 4).SequenceEqual("RIFF"u8)
        && BinaryPrimitives.ReadUInt32LittleEndian(riff.AsSpan(4)) + 8L == riff.Length;

    private static byte[] Riff(byte[] riff, byte[] xmp)
    {
        var chunk = new byte[8 + xmp.Length + xmp.Length % 2];
        "_PMX"u8.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4), (uint)xmp.Length);
        xmp.CopyTo(chunk, 8);
        byte[] marked = [.. riff, .. chunk];
        BinaryPrimitives.WriteUInt32LittleEndian(marked.AsSpan(4), (uint)(marked.Length - 8));
        return marked;
    }

    private static byte[] WebM(byte[] webm, string sourceType)
    {
        // The EBML header, then the Segment; Tags { Tag { Targets {}, SimpleTag { TagName, TagString } } } at the Segment's end.
        if (webm.Length < 12 || !webm.AsSpan(0, 4).SequenceEqual((byte[])[0x1A, 0x45, 0xDF, 0xA3]) || Vint(webm, 4) is not var (headerSize, n, _))
        {
            return webm;
        }
        var at = 4 + n + headerSize;
        if (at + 5 > webm.Length || !webm.AsSpan((int)at, 4).SequenceEqual((byte[])[0x18, 0x53, 0x80, 0x67]) || Vint(webm, (int)at + 4) is not var (size, width, unknown))
        {
            return webm;
        }
        var start = at + 4 + width;
        byte[] tags = Element([0x12, 0x54, 0xC3, 0x67], Element([0x73, 0x73], [
            .. Element([0x63, 0xC0], []),
            .. Element([0x67, 0xC8], [.. Element([0x45, 0xA3], "DIGITAL_SOURCE_TYPE"u8.ToArray()), .. Element([0x44, 0x87], Encoding.UTF8.GetBytes(sourceType))]),
        ]));
        if (unknown)
        {
            return [.. webm, .. tags];
        }
        var grown = size + tags.Length;
        if (start + size != webm.Length || grown >= (1L << (7 * width)) - 1)
        {
            return webm;
        }
        byte[] marked = [.. webm, .. tags];
        for (var i = 0; i < width; i++)
        {
            marked[at + 4 + i] = (byte)(grown >> (8 * (width - 1 - i)));
        }
        marked[at + 4] |= (byte)(0x80 >> (width - 1));
        return marked;
    }

    /// <summary>An EBML size at the offset: its value, its width in bytes, and whether it is "unknown" (every bit set).</summary>
    private static (long Value, int Width, bool Unknown)? Vint(byte[] b, int at)
    {
        if (at >= b.Length || b[at] == 0)
        {
            return null;
        }
        var width = 1 + System.Numerics.BitOperations.LeadingZeroCount((uint)b[at]) - 24;
        if (at + width > b.Length)
        {
            return null;
        }
        long value = b[at] & (0xFF >> width);
        for (var i = 1; i < width; i++)
        {
            value = (value << 8) | b[at + i];
        }
        return (value, width, value == (1L << (7 * width)) - 1);
    }

    /// <summary>An EBML element: its id, its size in eight bytes, its data.</summary>
    private static byte[] Element(byte[] id, byte[] data)
    {
        var size = new byte[8];
        for (var i = 0; i < 8; i++)
        {
            size[i] = (byte)((long)data.Length >> (8 * (7 - i)));
        }
        size[0] = 0x01;
        return [.. id, .. size, .. data];
    }

    private static uint ReadBigEndian(byte[] from, int at) =>
        (uint)((from[at] << 24) | (from[at + 1] << 16) | (from[at + 2] << 8) | from[at + 3]);

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
