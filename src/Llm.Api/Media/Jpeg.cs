namespace Llm.Api.Media;

/// <summary>
/// Pictures written as baseline JPEG (4:4:4, the standard tables of the JPEG specification's annex K), for the media
/// studio's thumbnails: a tenth of a PNG's size for a photograph, and nothing to install.
/// </summary>
public static class Jpeg
{
    private static readonly byte[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly byte[] LumaQuant =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly byte[] ChromaQuant =
    [
        17, 18, 24, 47, 99, 99, 99, 99, 18, 21, 26, 66, 99, 99, 99, 99, 24, 26, 56, 99, 99, 99, 99, 99, 47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99, 99,
    ];

    // The standard Huffman tables: how many codes of each length (1 to 16), then the values in order.
    private static readonly byte[] DcLumaCounts = [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcLumaValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] DcChromaCounts = [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];
    private static readonly byte[] DcChromaValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] AcLumaCounts = [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7d];
    private static readonly byte[] AcLumaValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07, 0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xa1, 0x08,
        0x23, 0x42, 0xb1, 0xc1, 0x15, 0x52, 0xd1, 0xf0, 0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0a, 0x16, 0x17, 0x18, 0x19, 0x1a, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2a, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59,
        0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4, 0xb5, 0xb6,
        0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda, 0xe1, 0xe2,
        0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf1, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];
    private static readonly byte[] AcChromaCounts = [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];
    private static readonly byte[] AcChromaValues =
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71, 0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91,
        0xa1, 0xb1, 0xc1, 0x09, 0x23, 0x33, 0x52, 0xf0, 0x15, 0x62, 0x72, 0xd1, 0x0a, 0x16, 0x24, 0x34, 0xe1, 0x25, 0xf1, 0x17, 0x18, 0x19, 0x1a, 0x26,
        0x27, 0x28, 0x29, 0x2a, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3a, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4a, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58,
        0x59, 0x5a, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6a, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7a, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8a, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9a, 0xa2, 0xa3, 0xa4, 0xa5, 0xa6, 0xa7, 0xa8, 0xa9, 0xaa, 0xb2, 0xb3, 0xb4,
        0xb5, 0xb6, 0xb7, 0xb8, 0xb9, 0xba, 0xc2, 0xc3, 0xc4, 0xc5, 0xc6, 0xc7, 0xc8, 0xc9, 0xca, 0xd2, 0xd3, 0xd4, 0xd5, 0xd6, 0xd7, 0xd8, 0xd9, 0xda,
        0xe2, 0xe3, 0xe4, 0xe5, 0xe6, 0xe7, 0xe8, 0xe9, 0xea, 0xf2, 0xf3, 0xf4, 0xf5, 0xf6, 0xf7, 0xf8, 0xf9, 0xfa,
    ];

    private sealed record Table((ushort Code, byte Length)[] Codes);

    private static Table Build(byte[] counts, byte[] values)
    {
        var codes = new (ushort, byte)[256];
        int code = 0, k = 0;
        for (var length = 1; length <= 16; length++)
        {
            for (var i = 0; i < counts[length - 1]; i++)
            {
                codes[values[k++]] = ((ushort)code, (byte)length);
                code++;
            }
            code <<= 1;
        }
        return new Table(codes);
    }

    private static readonly Table DcLuma = Build(DcLumaCounts, DcLumaValues);
    private static readonly Table DcChroma = Build(DcChromaCounts, DcChromaValues);
    private static readonly Table AcLuma = Build(AcLumaCounts, AcLumaValues);
    private static readonly Table AcChroma = Build(AcChromaCounts, AcChromaValues);

    private static readonly float[] Cos = [.. Enumerable.Range(0, 64).Select(i => (float)Math.Cos((2 * (i / 8) + 1) * (i % 8) * Math.PI / 16))];

    private static byte[] Scaled(byte[] table, int quality)
    {
        var q = Math.Clamp(quality, 1, 100);
        var scale = q < 50 ? 5000 / q : 200 - q * 2;
        return [.. table.Select(t => (byte)Math.Clamp((t * scale + 50) / 100, 1, 255))];
    }

    /// <summary>Pixels (four bytes each: red, green, blue, alpha, which is dropped) as a JPEG of this quality (1 to 100).</summary>
    public static byte[] Encode(Png.Image image, int quality = 80)
    {
        var luma = Scaled(LumaQuant, quality);
        var chroma = Scaled(ChromaQuant, quality);
        using var o = new MemoryStream();
        o.Write([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        // Both tables in zigzag order, as the file keeps them.
        o.Write([0xFF, 0xDB, 0, 132, 0]);
        o.Write(ZigZag.Select(z => luma[z]).ToArray());
        o.WriteByte(1);
        o.Write(ZigZag.Select(z => chroma[z]).ToArray());
        o.Write([0xFF, 0xC0, 0, 17, 8, (byte)(image.Height >> 8), (byte)image.Height, (byte)(image.Width >> 8), (byte)image.Width, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1]);
        WriteTable(o, 0x00, DcLumaCounts, DcLumaValues);
        WriteTable(o, 0x10, AcLumaCounts, AcLumaValues);
        WriteTable(o, 0x01, DcChromaCounts, DcChromaValues);
        WriteTable(o, 0x11, AcChromaCounts, AcChromaValues);
        o.Write([0xFF, 0xDA, 0, 12, 3, 1, 0x00, 2, 0x11, 3, 0x11, 0, 63, 0]);
        var bits = new Bits(o);
        var block = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            block[c] = new float[64];
        }
        var previous = new int[3];
        for (var by = 0; by < image.Height; by += 8)
        {
            for (var bx = 0; bx < image.Width; bx += 8)
            {
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        // Past the edge: the edge's pixel again.
                        var s = (Math.Min(by + y, image.Height - 1) * image.Width + Math.Min(bx + x, image.Width - 1)) * 4;
                        float r = image.Rgba[s], g = image.Rgba[s + 1], b = image.Rgba[s + 2];
                        block[0][y * 8 + x] = 0.299f * r + 0.587f * g + 0.114f * b - 128;
                        block[1][y * 8 + x] = -0.168736f * r - 0.331264f * g + 0.5f * b;
                        block[2][y * 8 + x] = 0.5f * r - 0.418688f * g - 0.081312f * b;
                    }
                }
                for (var c = 0; c < 3; c++)
                {
                    previous[c] = Block(bits, block[c], c == 0 ? luma : chroma, previous[c], c == 0 ? DcLuma : DcChroma, c == 0 ? AcLuma : AcChroma);
                }
            }
        }
        bits.Flush();
        o.Write([0xFF, 0xD9]);
        return o.ToArray();
    }

    private static void WriteTable(Stream o, byte id, byte[] counts, byte[] values)
    {
        var length = 2 + 1 + 16 + values.Length;
        o.Write([0xFF, 0xC4, (byte)(length >> 8), (byte)length, id]);
        o.Write(counts);
        o.Write(values);
    }

    /// <summary>One 8×8 block: its cosine transform, quantised, written in zigzag order; its DC is returned for the next block's difference.</summary>
    private static int Block(Bits bits, float[] samples, byte[] quant, int previousDc, Table dc, Table ac)
    {
        Span<int> q = stackalloc int[64];
        for (var v = 0; v < 8; v++)
        {
            for (var u = 0; u < 8; u++)
            {
                var sum = 0f;
                for (var y = 0; y < 8; y++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        sum += samples[y * 8 + x] * Cos[x * 8 + u] * Cos[y * 8 + v];
                    }
                }
                var cu = u == 0 ? 0.70710678f : 1f;
                var cv = v == 0 ? 0.70710678f : 1f;
                var i = v * 8 + u;
                q[i] = (int)MathF.Round(0.25f * cu * cv * sum / quant[i]);
            }
        }
        var diff = q[0] - previousDc;
        var size = Magnitude(diff);
        bits.Put(dc.Codes[size]);
        bits.Put(Amplitude(diff, size), size);
        var run = 0;
        for (var k = 1; k < 64; k++)
        {
            var value = q[ZigZag[k]];
            if (value == 0)
            {
                run++;
                continue;
            }
            while (run > 15)
            {
                bits.Put(ac.Codes[0xF0]);
                run -= 16;
            }
            var s = Magnitude(value);
            bits.Put(ac.Codes[(run << 4) | s]);
            bits.Put(Amplitude(value, s), s);
            run = 0;
        }
        if (run > 0)
        {
            bits.Put(ac.Codes[0x00]);
        }
        return q[0];
    }

    private static int Magnitude(int v)
    {
        var a = Math.Abs(v);
        var n = 0;
        while (a > 0)
        {
            n++;
            a >>= 1;
        }
        return n;
    }

    private static int Amplitude(int v, int size) => v >= 0 ? v : v + (1 << size) - 1;

    /// <summary>Bits written most significant first, a 0x00 after each 0xFF byte as the format asks.</summary>
    private sealed class Bits(Stream o)
    {
        private int _buffer;
        private int _count;

        public void Put((ushort Code, byte Length) code) => Put(code.Code, code.Length);

        public void Put(int value, int length)
        {
            for (var i = length - 1; i >= 0; i--)
            {
                _buffer = (_buffer << 1) | ((value >> i) & 1);
                if (++_count == 8)
                {
                    Byte();
                }
            }
        }

        private void Byte()
        {
            o.WriteByte((byte)_buffer);
            if (_buffer == 0xFF)
            {
                o.WriteByte(0);
            }
            _buffer = 0;
            _count = 0;
        }

        public void Flush()
        {
            while (_count != 0)
            {
                _buffer = (_buffer << 1) | 1;
                if (++_count == 8)
                {
                    Byte();
                }
            }
        }
    }

    /// <summary>A picture made smaller (each new pixel the average of those it covers), its longer side at most <paramref name="side"/>.</summary>
    public static Png.Image Smaller(Png.Image image, int side)
    {
        var scale = Math.Min(1.0, (double)side / Math.Max(image.Width, image.Height));
        if (scale >= 1)
        {
            return image;
        }
        var (w, h) = (Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
        var o = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            int y0 = y * image.Height / h, y1 = Math.Max(y0 + 1, (y + 1) * image.Height / h);
            for (var x = 0; x < w; x++)
            {
                int x0 = x * image.Width / w, x1 = Math.Max(x0 + 1, (x + 1) * image.Width / w);
                long r = 0, g = 0, b = 0, n = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var s = (sy * image.Width + sx) * 4;
                        r += image.Rgba[s];
                        g += image.Rgba[s + 1];
                        b += image.Rgba[s + 2];
                        n++;
                    }
                }
                var d = (y * w + x) * 4;
                o[d] = (byte)(r / n);
                o[d + 1] = (byte)(g / n);
                o[d + 2] = (byte)(b / n);
                o[d + 3] = 255;
            }
        }
        return new Png.Image(w, h, o);
    }
}
