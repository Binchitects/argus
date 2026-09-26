namespace Argus.Packs;

/// <summary>
/// Binary and int8 quantisation for embeddings.
///
/// A vector is stored twice, neither time as float32: 96 bytes of sign bits for
/// the coarse Hamming pass and 768 bytes of int8 for the rescore. The bit order
/// (LSB-first within each byte) must match between stored and query vectors, so
/// it lives here and nowhere else.
/// </summary>
public static class Quantize
{
    public static int Dim => Indexing.Embed.Dim;
    const int Int8Max = 127;

    static void CheckDim(IReadOnlyList<double> vec)
    {
        if (vec.Count != Dim) throw new ArgumentException($"expected {Dim} dimensions, got {vec.Count}");
    }

    /// <summary>Pack the sign of each component into one bit, LSB-first.</summary>
    public static byte[] ToBits(IReadOnlyList<double> vec)
    {
        CheckDim(vec);
        var output = new byte[Dim / 8];
        for (int i = 0; i < vec.Count; i++)
            if (vec[i] > 0) output[i >> 3] |= (byte)(1 << (i & 7));
        return output;
    }

    /// <summary>Scale to signed 8-bit with a per-vector scale (largest component maps to 127).</summary>
    public static byte[] ToInt8(IReadOnlyList<double> vec)
    {
        CheckDim(vec);
        double peak = 0;
        foreach (var x in vec) peak = Math.Max(peak, Math.Abs(x));
        var output = new byte[Dim];
        if (peak == 0) return output;
        double scale = Int8Max / peak;
        for (int i = 0; i < vec.Count; i++)
        {
            // Python's round() is round-half-to-even; so is MidpointRounding.ToEven.
            var v = (long)Math.Round(vec[i] * scale, MidpointRounding.ToEven);
            v = Math.Max(-Int8Max, Math.Min(Int8Max, v));
            output[i] = unchecked((byte)(sbyte)v);
        }
        return output;
    }

    /// <summary>Cosine-rank int8 candidates against a float query, best first (stable on ties).</summary>
    public static List<(long Id, double Score)> Rescore(IReadOnlyList<double> query, IReadOnlyList<(long Id, byte[] Raw)> candidates)
    {
        if (candidates.Count == 0) return [];
        CheckDim(query);
        double qnorm = 0;
        foreach (var x in query) qnorm += x * x;
        qnorm = Math.Sqrt(qnorm);
        if (qnorm == 0) return candidates.Select(c => (c.Id, 0.0)).ToList();

        var scored = new List<(long, double)>(candidates.Count);
        foreach (var (id, raw) in candidates)
        {
            double dot = 0, norm = 0;
            int n = Math.Min(raw.Length, query.Count);
            for (int i = 0; i < n; i++)
            {
                double v = (sbyte)raw[i];
                dot += query[i] * v;
                norm += v * v;
            }
            norm = Math.Sqrt(norm);
            scored.Add((id, norm != 0 ? dot / (qnorm * norm) : 0.0));
        }
        return scored.OrderByDescending(p => p.Item2).ToList();
    }
}
