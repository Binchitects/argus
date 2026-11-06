using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace CodeArena;

/// <summary>
/// A VM's screen as a picture: PNG made from what the hypervisors give (QEMU's PPM, Hyper-V's 16-bit pixels), and read
/// back to describe it in words for a model that cannot see pictures.
/// </summary>
internal static class VmPictures
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a];

    public static bool IsPng(byte[] bytes) => bytes.AsSpan().StartsWith(Signature);

    /// <summary>A PNG's width and height (its header), or null.</summary>
    public static (int Width, int Height)? Size(byte[] png) =>
        IsPng(png) && png.Length >= 24 ? (BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20))) : null;

    /// <summary>An 8-bit RGB picture as a PNG.</summary>
    public static byte[] Png(int width, int height, byte[] rgb)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * width * 3, width * 3);
            }
        }
        using var png = new MemoryStream();
        png.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        png.Write(four);
        var typed = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        png.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(four, Crc(typed));
        png.Write(four);
    }

    private static readonly uint[] CrcTable = [.. Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
        }
        return c;
    })];

    private static uint Crc(byte[] data)
    {
        var c = 0xffffffffu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xff] ^ (c >> 8);
        }
        return c ^ 0xffffffffu;
    }

    /// <summary>A binary PPM (P6, 8 bits) as a PNG; null for anything else.</summary>
    public static byte[]? PpmToPng(byte[] ppm)
    {
        if (ppm.Length < 2 || ppm[0] != 'P' || ppm[1] != '6')
        {
            return null;
        }
        var at = 2;
        var fields = new int[3];
        for (var f = 0; f < 3; f++)
        {
            while (at < ppm.Length && (char.IsWhiteSpace((char)ppm[at]) || ppm[at] == '#'))
            {
                if (ppm[at] == '#')
                {
                    while (at < ppm.Length && ppm[at] != '\n')
                    {
                        at++;
                    }
                }
                else
                {
                    at++;
                }
            }
            var start = at;
            while (at < ppm.Length && char.IsAsciiDigit((char)ppm[at]))
            {
                at++;
            }
            if (!int.TryParse(Encoding.ASCII.GetString(ppm, start, at - start), NumberStyles.None, CultureInfo.InvariantCulture, out fields[f]))
            {
                return null;
            }
        }
        at++;
        var (width, height, max) = (fields[0], fields[1], fields[2]);
        if (width <= 0 || height <= 0 || max != 255 || (long)width * height * 3 > ppm.Length - at)
        {
            return null;
        }
        return Png(width, height, ppm[at..(at + width * height * 3)]);
    }

    /// <summary>16-bit pixels (5 red, 6 green, 5 blue, little-endian), row by row, as a PNG; null when there are too few.</summary>
    public static byte[]? Rgb565ToPng(int width, int height, byte[] pixels)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height * 2)
        {
            return null;
        }
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            var p = pixels[i * 2] | pixels[i * 2 + 1] << 8;
            rgb[i * 3] = (byte)((p >> 11 & 0x1f) * 255 / 31);
            rgb[i * 3 + 1] = (byte)((p >> 5 & 0x3f) * 255 / 63);
            rgb[i * 3 + 2] = (byte)((p & 0x1f) * 255 / 31);
        }
        return Png(width, height, rgb);
    }

    /// <summary>A PNG's pixels as 8-bit RGB (gray, RGB and RGBA at 8 bits, not interlaced: what hypervisors write); null for another.</summary>
    public static (int Width, int Height, byte[] Rgb)? Read(byte[] png)
    {
        if (Size(png) is not (var width, var height) || width <= 0 || height <= 0 || (long)width * height > 50_000_000)
        {
            return null;
        }
        int depth = 0, color = -1, interlace = 0;
        using var data = new MemoryStream();
        for (var at = 8; at + 8 <= png.Length;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            var type = Encoding.ASCII.GetString(png, at + 4, 4);
            if (length < 0 || at + 12 + length > png.Length)
            {
                return null;
            }
            if (type == "IHDR")
            {
                (depth, color, interlace) = (png[at + 16], png[at + 17], png[at + 20]);
            }
            else if (type == "IDAT")
            {
                data.Write(png, at + 8, length);
            }
            else if (type == "IEND")
            {
                break;
            }
            at += 12 + length;
        }
        var channels = color switch { 0 => 1, 2 => 3, 6 => 4, _ => 0 };
        if (depth != 8 || channels == 0 || interlace != 0)
        {
            return null;
        }
        data.Position = 0;
        using var z = new ZLibStream(data, CompressionMode.Decompress);
        var stride = width * channels;
        var previous = new byte[stride];
        var row = new byte[stride];
        var rgb = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var filter = z.ReadByte();
            if (filter < 0)
            {
                return null;
            }
            z.ReadExactly(row);
            for (var i = 0; i < stride; i++)
            {
                int left = i >= channels ? row[i - channels] : 0, up = previous[i], corner = i >= channels ? previous[i - channels] : 0;
                row[i] = (byte)(row[i] + filter switch
                {
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    4 => Paeth(left, up, corner),
                    _ => 0,
                });
            }
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 3;
                if (channels == 1)
                {
                    rgb[o] = rgb[o + 1] = rgb[o + 2] = row[x];
                }
                else
                {
                    rgb[o] = row[x * channels];
                    rgb[o + 1] = row[x * channels + 1];
                    rgb[o + 2] = row[x * channels + 2];
                }
            }
            (previous, row) = (row, previous);
        }
        return (width, height, rgb);
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var (pa, pb, pc) = (Math.Abs(p - a), Math.Abs(p - b), Math.Abs(p - c));
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    /// <summary>
    /// The screen in words, for a model that cannot see it: its size, how much of it is dark, its main colour, and what
    /// that suggests (off or asleep, a text console, a desktop).
    /// </summary>
    public static string Describe(byte[] png)
    {
        if (Read(png) is not (var width, var height, var rgb))
        {
            return Size(png) is (var w, var h) ? $"The screen is {w}×{h}." : "The picture could not be read.";
        }
        var pixels = width * height;
        int dark = 0, bright = 0;
        var buckets = new Dictionary<int, int>();
        for (var i = 0; i < pixels; i++)
        {
            int r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
            var light = (r * 299 + g * 587 + b * 114) / 1000;
            dark += light < 40 ? 1 : 0;
            bright += light > 200 ? 1 : 0;
            var key = (r >> 6) << 4 | (g >> 6) << 2 | b >> 6;
            buckets[key] = buckets.GetValueOrDefault(key) + 1;
        }
        var (main, count) = buckets.MaxBy(kv => kv.Value) is var top ? (top.Key, top.Value) : (0, 0);
        var share = (int)Math.Round(100.0 * count / pixels);
        var darkShare = (int)Math.Round(100.0 * dark / pixels);
        var colour = Colour((main >> 4 & 3) * 85, (main >> 2 & 3) * 85, (main & 3) * 85);
        var sb = new StringBuilder($"The screen is {width}×{height}: {darkShare}% of it dark, {share}% of it {colour}.");
        if (count == pixels || darkShare >= 99)
        {
            sb.Append(" It is blank: the VM may be booting, asleep (its display off), or showing nothing yet.");
        }
        else if (darkShare >= 85 && bright * 100 / pixels <= 12)
        {
            sb.Append(" It looks like a text console (light characters on dark): a login prompt or a terminal.");
        }
        else
        {
            sb.Append(" It looks like a desktop or an application's window.");
        }
        return sb.ToString();
    }

    private static string Colour(int r, int g, int b) => (r, g, b) switch
    {
        _ when r < 60 && g < 60 && b < 60 => "black",
        _ when r > 200 && g > 200 && b > 200 => "white",
        _ when Math.Abs(r - g) < 40 && Math.Abs(g - b) < 40 => "grey",
        _ when b >= r && b >= g => "blue",
        _ when g >= r && g >= b => "green",
        _ when r >= g && r >= b && g > 120 => "yellow or orange",
        _ => "red",
    };
}
