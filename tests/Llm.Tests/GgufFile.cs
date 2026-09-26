using System.Text;

namespace Llm.Tests;

/// <summary>
/// Real GGUF files, small: a key/value header, a tensor table and zeroed data, as
/// llama.cpp writes them. What the model library reads to tell what a file is.
/// </summary>
public sealed class GgufFile
{
    private readonly List<(string Key, Action<BinaryWriter> Write)> _values = [];
    private readonly List<(string Name, ulong[] Shape, long Bytes)> _tensors = [];

    public GgufFile Text(string key, string value) => Add(key, w => { w.Write(8u); S(w, value); });
    public GgufFile U32(string key, uint value) => Add(key, w => { w.Write(4u); w.Write(value); });
    public GgufFile F32(string key, float value) => Add(key, w => { w.Write(6u); w.Write(value); });
    public GgufFile Bool(string key, bool value) => Add(key, w => { w.Write(7u); w.Write((byte)(value ? 1 : 0)); });

    /// <summary>An array of strings, as a tokenizer's vocabulary is.</summary>
    public GgufFile Strings(string key, int count) => Add(key, w =>
    {
        w.Write(9u);
        w.Write(8u);
        w.Write((ulong)count);
        for (var i = 0; i < count; i++)
        {
            S(w, "t" + i);
        }
    });

    public GgufFile Tensor(string name, long bytes, params ulong[] shape)
    {
        _tensors.Add((name, shape.Length > 0 ? shape : [(ulong)bytes], bytes));
        return this;
    }

    private GgufFile Add(string key, Action<BinaryWriter> value)
    {
        _values.Add((key, w => { S(w, key); value(w); }));
        return this;
    }

    /// <summary>A language model: <paramref name="layers"/> layers of attention (and experts), its vocabulary, embeddings and output.</summary>
    public static GgufFile Language(string arch, int layers = 4, uint context = 40960, int kvHeads = 2, int headSize = 128, int experts = 0, int used = 0,
        string? name = null, string? template = "{% for m in messages %}<think>{{ m.content }}</think>{% if tools %}<tool_call>{% endif %}{% endfor %}",
        long layerBytes = 4096, long expertBytes = 0, int vocab = 64, int embedding = 64, bool tied = false, int nextn = 0, int interval = 0)
    {
        var g = new GgufFile().Text("general.architecture", arch).Text("general.type", "model")
            .U32(arch + ".block_count", (uint)(layers + nextn)).U32(arch + ".context_length", context).U32(arch + ".embedding_length", (uint)embedding)
            .U32(arch + ".attention.head_count", 8).U32(arch + ".attention.head_count_kv", (uint)kvHeads)
            .U32(arch + ".attention.key_length", (uint)headSize).U32(arch + ".attention.value_length", (uint)headSize)
            .F32(arch + ".rope.freq_base", 1_000_000).Strings("tokenizer.ggml.tokens", vocab);
        if (name is not null)
        {
            g.Text("general.name", name);
        }
        if (template is not null)
        {
            g.Text("tokenizer.chat_template", template);
        }
        if (experts > 0)
        {
            g.U32(arch + ".expert_count", (uint)experts).U32(arch + ".expert_used_count", (uint)used);
        }
        if (nextn > 0)
        {
            g.U32(arch + ".nextn_predict_layers", (uint)nextn);
        }
        if (interval > 0)
        {
            g.U32(arch + ".full_attention_interval", (uint)interval);
        }
        g.Tensor("token_embd.weight", vocab * embedding / 2, (ulong)embedding, (ulong)vocab);
        for (var i = 0; i < layers + nextn; i++)
        {
            g.Tensor($"blk.{i}.attn_qkv.weight", layerBytes, (ulong)embedding, (ulong)(layerBytes * 2 / embedding));
            if (experts > 0)
            {
                g.Tensor($"blk.{i}.ffn_up_exps.weight", expertBytes, (ulong)embedding, 16, (ulong)experts);
            }
        }
        g.Tensor("output_norm.weight", embedding * 4, (ulong)embedding);
        if (!tied)
        {
            g.Tensor("output.weight", vocab * embedding / 2, (ulong)embedding, (ulong)vocab);
        }
        return g;
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path), Encoding.UTF8);
        w.Write(0x46554747u);
        w.Write(3u);
        w.Write((ulong)_tensors.Count);
        w.Write((ulong)_values.Count);
        foreach (var (_, write) in _values)
        {
            write(w);
        }
        long offset = 0;
        foreach (var (name, shape, bytes) in _tensors)
        {
            S(w, name);
            w.Write((uint)shape.Length);
            foreach (var d in shape)
            {
                w.Write(d);
            }
            w.Write(2u);
            w.Write((ulong)offset);
            offset += (bytes + 31) / 32 * 32;
        }
        w.Write(new byte[(32 - (w.BaseStream.Position % 32)) % 32]);
        // The data, sparse: gigabytes of "weights" take no disk.
        w.Flush();
        w.BaseStream.SetLength(w.BaseStream.Position + offset);
    }

    private static void S(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write((ulong)b.Length);
        w.Write(b);
    }
}
