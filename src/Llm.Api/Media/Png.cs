using System.Buffers.Binary;
using System.IO.Compression;

namespace Llm.Api.Media;

/// <summary>
/// PNG read and written by hand, for the media studio's painted edits: the pictures it blends are the browser's canvas and
/// the picture server's own, 8 bits a channel and not interlaced. Others are refused (<see cref="InvalidDataException"/>).
/// </summary>
public static class Png
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The largest side read: a picture larger than this is no canvas of the studio's.</summary>
    public const int MostSide = 4096;

    /// <summary>A PNG's pixels, four bytes each (red, green, blue, alpha), row by row.</summary>
    public sealed record Image(int Width, int Height, byte[] Rgba);

    public static Image Decode(byte[] png)
    {
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("not a PNG");
        }
        int width = 0, height = 0, depth = 0, color = 0;
        byte[]? palette = null, alphas = null;
        using var data = new MemoryStream();
        for (var at = 8; at + 12 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if (length < 0 || at + 12 + length > png.Length)
            {
                throw new InvalidDataException("a PNG chunk runs past the end");
            }
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var body = png.AsSpan(at + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8];
                    color = body[9];
                    if (body[12] != 0)
                    {
                        throw new InvalidDataException("an interlaced PNG");
                    }
                    break;
                case "PLTE":
                    palette = body.ToArray();
                    break;
                case "tRNS":
                    alphas = body.ToArray();
                    break;
                case "IDAT":
                    data.Write(body);
                    break;
            }
            at += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }
        if (width is <= 0 or > MostSide || height is <= 0 or > MostSide || depth != 8)
        {
            throw new InvalidDataException("a PNG this does not read (8 bits a channel, at most 4096 pixels a side)");
        }
        var channels = color switch
        {
            0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4,
            _ => throw new InvalidDataException("a PNG of an unknown colour type"),
        };
        if (color == 3 && palette is null)
        {
            throw new InvalidDataException("a palette PNG without its palette");
        }
        var stride = width * channels;
        var raw = new byte[height * stride];
        data.Position = 0;
        using (var z = new ZLibStream(data, CompressionMode.Decompress))
        {
            var previous = new byte[stride];
            var row = new byte[stride];
            for (var y = 0; y < height; y++)
            {
                var filter = z.ReadByte();
                z.ReadExactly(row);
                Unfilter(filter, row, previous, channels);
                row.CopyTo(raw, y * stride);
                (previous, row) = (row, previous);
            }
        }
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var s = i * channels;
            var o = i * 4;
            switch (color)
            {
                case 0:
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = raw[s];
                    rgba[o + 3] = 255;
                    break;
                case 2:
                    rgba[o] = raw[s];
                    rgba[o + 1] = raw[s + 1];
                    rgba[o + 2] = raw[s + 2];
                    rgba[o + 3] = 255;
                    break;
                case 3:
                    var p = raw[s] * 3;
                    if (p + 2 >= palette!.Length)
                    {
                        throw new InvalidDataException("a PNG colour past its palette");
                    }
                    rgba[o] = palette[p];
                    rgba[o + 1] = palette[p + 1];
                    rgba[o + 2] = palette[p + 2];
                    rgba[o + 3] = alphas is not null && raw[s] < alphas.Length ? alphas[raw[s]] : (byte)255;
                    break;
                case 4:
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = raw[s];
                    rgba[o + 3] = raw[s + 1];
                    break;
                default:
                    Array.Copy(raw, s, rgba, o, 4);
                    break;
            }
        }
        return new Image(width, height, rgba);
    }

    private static void Unfilter(int filter, byte[] row, byte[] up, int bpp)
    {
        for (var i = 0; i < row.Length; i++)
        {
            int a = i >= bpp ? row[i - bpp] : 0, b = up[i], c = i >= bpp ? up[i - bpp] : 0;
            row[i] = (byte)(row[i] + filter switch
            {
                0 => 0,
                1 => a,
                2 => b,
                3 => (a + b) / 2,
                4 => Paeth(a, b, c),
                _ => throw new InvalidDataException("a PNG row of an unknown filter"),
            });
        }
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>Pixels as a PNG without alpha (every pixel is opaque here), each row filtered the way that packs it best.</summary>
    public static byte[] Encode(Image image)
    {
        var (w, h) = (image.Width, image.Height);
        var stride = w * 3;
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var previous = new byte[stride];
            var row = new byte[stride];
            var best = new byte[stride];
            var trial = new byte[stride];
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var s = (y * w + x) * 4;
                    row[x * 3] = image.Rgba[s];
                    row[x * 3 + 1] = image.Rgba[s + 1];
                    row[x * 3 + 2] = image.Rgba[s + 2];
                }
                var bestFilter = 0;
                var bestSum = long.MaxValue;
                for (var f = 0; f <= 4; f++)
                {
                    long sum = 0;
                    for (var i = 0; i < stride; i++)
                    {
                        int a = i >= 3 ? row[i - 3] : 0, b = previous[i], c = i >= 3 ? previous[i - 3] : 0;
                        var v = (byte)(row[i] - f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => Paeth(a, b, c) });
                        trial[i] = v;
                        sum += v < 128 ? v : 256 - v;
                    }
                    if (sum < bestSum)
                    {
                        (bestSum, bestFilter) = (sum, f);
                        trial.CopyTo(best, 0);
                    }
                }
                z.WriteByte((byte)bestFilter);
                z.Write(best);
                (previous, row) = (row, previous);
            }
        }
        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, w);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), h);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream to, string type, byte[] body)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, body.Length);
        to.Write(four);
        var named = System.Text.Encoding.ASCII.GetBytes(type);
        to.Write(named);
        to.Write(body);
        var crc = Crc(Crc(0xFFFFFFFFu, named), body) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        to.Write(four);
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

    private static uint Crc(uint crc, byte[] bytes)
    {
        foreach (var b in bytes)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc;
    }

    /// <summary>
    /// The changed picture over the original where the mask is light (its red, 0 to 255: how much of the change), the original
    /// elsewhere: a painted edit changes only what was painted, its edge as soft as the mask's. All three the same size.
    /// </summary>
    public static Image Blend(Image original, Image changed, Image mask)
    {
        if (changed.Width != original.Width || changed.Height != original.Height || mask.Width != original.Width || mask.Height != original.Height)
        {
            throw new InvalidDataException("the pictures to blend differ in size");
        }
        var o = new byte[original.Rgba.Length];
        for (var i = 0; i < o.Length; i += 4)
        {
            var m = mask.Rgba[i];
            for (var c = 0; c < 3; c++)
            {
                o[i + c] = (byte)((original.Rgba[i + c] * (255 - m) + changed.Rgba[i + c] * m + 127) / 255);
            }
            o[i + 3] = 255;
        }
        return new Image(original.Width, original.Height, o);
    }

    /// <summary>A picture with see-through parts over light grey checks (24 pixels), as pictures without a background are shown; opaque, it is itself.</summary>
    public static Image OverChecks(Image image)
    {
        var o = (byte[])image.Rgba.Clone();
        var any = false;
        for (var i = 0; i < o.Length; i += 4)
        {
            var a = o[i + 3];
            if (a == 255)
            {
                continue;
            }
            any = true;
            var p = i / 4;
            var check = ((p % image.Width / 24) + (p / image.Width / 24)) % 2 == 0 ? 255 : 229;
            for (var c = 0; c < 3; c++)
            {
                o[i + c] = (byte)((o[i + c] * a + check * (255 - a) + 127) / 255);
            }
            o[i + 3] = 255;
        }
        return any ? new Image(image.Width, image.Height, o) : image;
    }

    /// <summary>A rectangle of a picture, in pixels.</summary>
    public sealed record Area(int X, int Y, int Width, int Height);

    /// <summary>
    /// Where a mask is painted (its red above a trace), with a quarter of its size around it (32 pixels at least), and at least
    /// <paramref name="least"/> pixels a side where the picture allows; null when nothing is painted.
    /// </summary>
    public static Area? AreaAround(Image mask, int least)
    {
        int x0 = mask.Width, y0 = mask.Height, x1 = -1, y1 = -1;
        for (var y = 0; y < mask.Height; y++)
        {
            for (var x = 0; x < mask.Width; x++)
            {
                if (mask.Rgba[(y * mask.Width + x) * 4] > 8)
                {
                    (x0, y0, x1, y1) = (Math.Min(x0, x), Math.Min(y0, y), Math.Max(x1, x), Math.Max(y1, y));
                }
            }
        }
        if (x1 < 0)
        {
            return null;
        }
        var margin = Math.Max(32, Math.Max(x1 - x0, y1 - y0) / 4);
        (int Start, int Length) Side(int from, int to, int whole)
        {
            var start = Math.Max(0, from - margin);
            var end = Math.Min(whole, to + 1 + margin);
            var grow = Math.Min(least, whole) - (end - start);
            if (grow > 0)
            {
                start = Math.Max(0, start - (grow + 1) / 2);
                end = Math.Min(whole, start + Math.Min(least, whole));
                start = Math.Max(0, end - Math.Min(least, whole));
            }
            return (start, end - start);
        }
        var (left, w) = Side(x0, x1, mask.Width);
        var (top, h) = Side(y0, y1, mask.Height);
        return new Area(left, top, w, h);
    }

    public static Image Crop(Image image, Area area)
    {
        var o = new byte[area.Width * area.Height * 4];
        for (var y = 0; y < area.Height; y++)
        {
            Array.Copy(image.Rgba, ((area.Y + y) * image.Width + area.X) * 4, o, y * area.Width * 4, area.Width * 4);
        }
        return new Image(area.Width, area.Height, o);
    }

    /// <summary>A copy of <paramref name="image"/> with <paramref name="part"/> over it at (x, y).</summary>
    public static Image Paste(Image image, Image part, int x, int y)
    {
        var o = (byte[])image.Rgba.Clone();
        for (var row = 0; row < part.Height && y + row < image.Height; row++)
        {
            var width = Math.Min(part.Width, image.Width - x);
            Array.Copy(part.Rgba, row * part.Width * 4, o, ((y + row) * image.Width + x) * 4, width * 4);
        }
        return new Image(image.Width, image.Height, o);
    }

    /// <summary>A picture at another size, each pixel from the four nearest of the original (bilinear).</summary>
    public static Image Resize(Image image, int width, int height)
    {
        if (width == image.Width && height == image.Height)
        {
            return image;
        }
        var o = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + 0.5) * image.Height / height - 0.5, 0, image.Height - 1);
            int ya = (int)sy, yb = Math.Min(ya + 1, image.Height - 1);
            var fy = sy - ya;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5) * image.Width / width - 0.5, 0, image.Width - 1);
                int xa = (int)sx, xb = Math.Min(xa + 1, image.Width - 1);
                var fx = sx - xa;
                for (var c = 0; c < 4; c++)
                {
                    double P(int px, int py) => image.Rgba[(py * image.Width + px) * 4 + c];
                    var top = P(xa, ya) * (1 - fx) + P(xb, ya) * fx;
                    var bottom = P(xa, yb) * (1 - fx) + P(xb, yb) * fx;
                    o[(y * width + x) * 4 + c] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                }
            }
        }
        return new Image(width, height, o);
    }

    /// <summary>A picture file's size from its header (PNG, JPEG, WebP, GIF), or null when it is none of these.</summary>
    public static (int Width, int Height)? SizeOf(byte[] file)
    {
        var f = file.AsSpan();
        if (f.Length >= 24 && f[..8].SequenceEqual(Signature))
        {
            return (BinaryPrimitives.ReadInt32BigEndian(f[16..]), BinaryPrimitives.ReadInt32BigEndian(f[20..]));
        }
        if (f.Length >= 10 && f[0] == (byte)'G' && f[1] == (byte)'I' && f[2] == (byte)'F')
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(f[6..]), BinaryPrimitives.ReadUInt16LittleEndian(f[8..]));
        }
        if (f.Length >= 30 && f[..4].SequenceEqual("RIFF"u8) && f[8..12].SequenceEqual("WEBP"u8))
        {
            var kind = f[12..16];
            if (kind.SequenceEqual("VP8 "u8))
            {
                return (BinaryPrimitives.ReadUInt16LittleEndian(f[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(f[28..]) & 0x3FFF);
            }
            if (kind.SequenceEqual("VP8L"u8))
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(f[21..]);
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
            if (kind.SequenceEqual("VP8X"u8))
            {
                return ((f[24] | f[25] << 8 | f[26] << 16) + 1, (f[27] | f[28] << 8 | f[29] << 16) + 1);
            }
            return null;
        }
        if (f.Length >= 4 && f[0] == 0xFF && f[1] == 0xD8)
        {
            // JPEG: the first start-of-frame marker says the size.
            for (var at = 2; at + 9 < f.Length;)
            {
                if (f[at] != 0xFF)
                {
                    return null;
                }
                var marker = f[at + 1];
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                {
                    at += 2;
                    continue;
                }
                var length = BinaryPrimitives.ReadUInt16BigEndian(f[(at + 2)..]);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    return (BinaryPrimitives.ReadUInt16BigEndian(f[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(f[(at + 5)..]));
                }
                at += 2 + length;
            }
        }
        return null;
    }
}
