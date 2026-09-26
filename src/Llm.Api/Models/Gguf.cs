using System.Text;

namespace Llm.Api.Models;

/// <summary>A tensor of a GGUF file: its name, shape, and the bytes it takes (from the offsets, so any quantization counts).</summary>
public sealed record GgufTensor(string Name, ulong[] Shape, uint Type, long Bytes)
{
    public long Elements => Shape.Aggregate(1L, (a, d) => a * (long)d);
}

/// <summary>An array value: its element type and length, and the elements when there are few (tokenizers hold hundreds of thousands).</summary>
public sealed record GgufArray(uint Type, long Length, object[]? Items);

/// <summary>
/// What a GGUF file says of itself: its key/value header and its tensor table,
/// for a split model across all of its parts. Never the tensor data.
/// </summary>
public sealed class GgufHeader
{
    public Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
    public List<GgufTensor> Tensors { get; } = [];

    public string? Text(string key) => Values.GetValueOrDefault(key) as string;

    public long? Whole(string key) => Values.GetValueOrDefault(key) switch
    {
        long l => l,
        ulong u => u > long.MaxValue ? long.MaxValue : (long)u,
        double d when d == Math.Floor(d) => (long)d,
        bool b => b ? 1 : 0,
        _ => null,
    };

    public double? Number(string key) => Values.GetValueOrDefault(key) switch
    {
        long l => l,
        ulong u => u,
        double d => d,
        _ => null,
    };

    public bool? Flag(string key) => Values.GetValueOrDefault(key) switch
    {
        bool b => b,
        long l => l != 0,
        _ => null,
    };

    public GgufArray? Array(string key) => Values.GetValueOrDefault(key) as GgufArray;

    /// <summary>A key of the model's own architecture ("qwen3.block_count" for "block_count").</summary>
    public string Arch(string key) => (Text("general.architecture") ?? "") + "." + key;
}

/// <summary>Reads GGUF headers (format versions 2 and 3), with limits that keep a damaged or hostile file from costing much.</summary>
public static class Gguf
{
    private const uint Magic = 0x46554747; // "GGUF"
    private const int MaxKeys = 10_000;
    private const long MaxTensors = 200_000;
    private const long MaxString = 16 << 20;
    private const int SmallArray = 1024;
    private const long MaxHeaderBytes = 256L << 20;

    /// <summary>The header of a file, and for a split model ("-00001-of-00003") the tensors of every part. Null when it is not a GGUF this reads.</summary>
    public static GgufHeader? Read(string path, IReadOnlyList<string>? moreParts = null)
    {
        try
        {
            var header = new GgufHeader();
            if (!ReadPart(path, header, withValues: true))
            {
                return null;
            }
            foreach (var part in moreParts ?? [])
            {
                ReadPart(part, header, withValues: false);
            }
            return header;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentException or OverflowException or InvalidDataException)
        {
            return null;
        }
    }

    private static bool ReadPart(string path, GgufHeader header, bool withValues)
    {
        using var file = File.OpenRead(path);
        using var stream = new BufferedStream(file, 1 << 20);
        using var r = new BinaryReader(stream, Encoding.UTF8);
        if (r.ReadUInt32() != Magic || r.ReadUInt32() is < 2 or > 3)
        {
            return false;
        }
        var tensors = r.ReadUInt64();
        var keys = r.ReadUInt64();
        if (tensors > MaxTensors || keys > MaxKeys)
        {
            throw new InvalidDataException("more tensors or keys than a model has");
        }
        long alignment = 32;
        for (ulong i = 0; i < keys; i++)
        {
            var key = ReadString(r);
            var value = ReadValue(r, r.ReadUInt32(), depth: 0);
            if (key == "general.alignment" && value is long a && a is > 0 and <= 1 << 20)
            {
                alignment = a;
            }
            if (withValues)
            {
                header.Values[key] = value;
            }
            Guard(stream);
        }
        var infos = new List<(string Name, ulong[] Shape, uint Type, long Offset)>((int)tensors);
        for (ulong i = 0; i < tensors; i++)
        {
            var name = ReadString(r);
            var dims = r.ReadUInt32();
            if (dims > 8)
            {
                throw new InvalidDataException("a tensor of more than 8 dimensions");
            }
            var shape = new ulong[dims];
            for (var d = 0; d < dims; d++)
            {
                shape[d] = r.ReadUInt64();
            }
            var type = r.ReadUInt32();
            var offset = checked((long)r.ReadUInt64());
            infos.Add((name, shape, type, offset));
            Guard(stream);
        }
        var dataStart = (stream.Position + alignment - 1) / alignment * alignment;
        var data = file.Length - dataStart;
        var sorted = infos.OrderBy(t => t.Offset).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var end = i + 1 < sorted.Count ? sorted[i + 1].Offset : data;
            header.Tensors.Add(new GgufTensor(sorted[i].Name, sorted[i].Shape, sorted[i].Type, Math.Max(0, end - sorted[i].Offset)));
        }
        return true;
    }

    private static void Guard(Stream s)
    {
        if (s.Position > MaxHeaderBytes)
        {
            throw new InvalidDataException("a header larger than a model's");
        }
    }

    private static string ReadString(BinaryReader r)
    {
        var length = r.ReadUInt64();
        if (length > MaxString)
        {
            throw new InvalidDataException("a string longer than a header holds");
        }
        return Encoding.UTF8.GetString(r.ReadBytes((int)length));
    }

    private static object ReadValue(BinaryReader r, uint type, int depth) => type switch
    {
        0 => (long)r.ReadByte(),
        1 => (long)r.ReadSByte(),
        2 => (long)r.ReadUInt16(),
        3 => (long)r.ReadInt16(),
        4 => (long)r.ReadUInt32(),
        5 => (long)r.ReadInt32(),
        6 => (double)r.ReadSingle(),
        7 => r.ReadByte() != 0,
        8 => ReadString(r),
        9 when depth == 0 => ReadArray(r),
        10 => r.ReadUInt64() is var u && u <= long.MaxValue ? (long)u : (object)u,
        11 => r.ReadInt64(),
        12 => r.ReadDouble(),
        _ => throw new InvalidDataException($"a value of unknown type {type}"),
    };

    private static GgufArray ReadArray(BinaryReader r)
    {
        var type = r.ReadUInt32();
        var length = r.ReadUInt64();
        if (length > 50_000_000)
        {
            throw new InvalidDataException("an array longer than a header holds");
        }
        if (length <= SmallArray)
        {
            var items = new object[length];
            for (var i = 0; i < (int)length; i++)
            {
                items[i] = ReadValue(r, type, depth: 1);
            }
            return new GgufArray(type, (long)length, items);
        }
        // Large (a tokenizer's): its length is what is wanted; the elements are skipped.
        if (type == 8)
        {
            for (ulong i = 0; i < length; i++)
            {
                var n = r.ReadUInt64();
                if (n > MaxString)
                {
                    throw new InvalidDataException("a string longer than a header holds");
                }
                r.BaseStream.Seek((long)n, SeekOrigin.Current);
            }
        }
        else
        {
            var width = type switch { 0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8, _ => throw new InvalidDataException("an array of arrays") };
            r.BaseStream.Seek((long)length * width, SeekOrigin.Current);
        }
        return new GgufArray(type, (long)length, null);
    }
}
