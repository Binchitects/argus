using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Llm.Tests;

/// <summary>
/// Hugging Face as the downloads see it: a search, one GGUF repository (a single-file model,
/// a split one, a vision projector), a gated one, Laya's checkpoints, and files served with range requests.
/// </summary>
public sealed class FakeHuggingFace : HttpMessageHandler
{
    public const string Repo = "acme/Tiny-GGUF";
    public const string Sha = "abc123";

    public static readonly Dictionary<string, byte[]> Files = new()
    {
        ["Tiny-Q4_K_M.gguf"] = Content("Tiny-Q4_K_M.gguf", 300_000),
        ["Q8_0/Tiny-Q8_0-00001-of-00002.gguf"] = Content("q8-1", 200_000),
        ["Q8_0/Tiny-Q8_0-00002-of-00002.gguf"] = Content("q8-2", 150_000),
        ["mmproj-Tiny-F16.gguf"] = Content("mmproj", 50_000),
        ["README.md"] = Encoding.UTF8.GetBytes("# Tiny"),
    };

    /// <summary>A safetensors repository for vLLM, with what vLLM never reads beside it: an original/ copy, a metal/ one, a GGUF file, the read-me.</summary>
    public const string SnapshotRepo = "acme/Flash-FP8";

    public static readonly Dictionary<string, byte[]> SnapshotFiles = new()
    {
        ["config.json"] = Encoding.UTF8.GetBytes("""{"architectures":["Qwen3MoeForCausalLM"],"num_hidden_layers":2,"num_attention_heads":4,"num_key_value_heads":2,"head_dim":64}"""),
        ["generation_config.json"] = Encoding.UTF8.GetBytes("{}"),
        ["model.safetensors.index.json"] = Encoding.UTF8.GetBytes("""{"weight_map":{"a":"model-00001-of-00002.safetensors","b":"model-00002-of-00002.safetensors"}}"""),
        ["model-00001-of-00002.safetensors"] = Content("shard-1", 120_000),
        ["model-00002-of-00002.safetensors"] = Content("shard-2", 80_000),
        ["tokenizer.json"] = Content("tokens", 20_000),
        ["tokenizer_config.json"] = Encoding.UTF8.GetBytes("{}"),
        ["chat_template.jinja"] = Encoding.UTF8.GetBytes("{{ messages }}"),
        ["original/consolidated.safetensors"] = Content("original", 200_000),
        ["metal/model.bin"] = Content("metal", 90_000),
        ["Flash-Q4_K_M.gguf"] = Content("gguf", 70_000),
        ["README.md"] = Encoding.UTF8.GetBytes("# Flash"),
        [".gitattributes"] = Encoding.UTF8.GetBytes("*.safetensors filter=lfs"),
    };

    public const string LayaRepo = "convaiinnovations/laya";

    /// <summary>Laya's repository: the English checkpoint at its root, the others in folders, and what else it holds.</summary>
    public static readonly Dictionary<string, byte[]> LayaFiles = new()
    {
        ["rl_agent_config.json"] = Encoding.UTF8.GetBytes("""{"encoder":"answerdotai/ModernBERT-large"}"""),
        ["model.safetensors"] = Content("english", 60_000),
        ["encoder/config.json"] = Encoding.UTF8.GetBytes("{}"),
        ["tokenizer/tokenizer.json"] = Content("en-tokens", 3_000),
        ["tokenizer/tokenizer_config.json"] = Encoding.UTF8.GetBytes("{}"),
        ["multilingual/rl_agent_config.json"] = Encoding.UTF8.GetBytes("""{"encoder":"jhu-clsp/mmBERT-base"}"""),
        ["multilingual/model.safetensors"] = Content("multilingual", 50_000),
        ["multilingual/encoder/config.json"] = Encoding.UTF8.GetBytes("{}"),
        ["multilingual/tokenizer/tokenizer.json"] = Content("ml-tokens", 30_000),
        ["multilingual/tokenizer/tokenizer_config.json"] = Encoding.UTF8.GetBytes("{}"),
        ["typed-decisions/model.safetensors"] = Content("typed", 60_000),
        ["README.md"] = Encoding.UTF8.GetBytes("# Laya"),
    };

    /// <summary>A request for this file is cut off after this many bytes, once.</summary>
    public (string File, int After)? CutOnce { get; set; }

    /// <summary>This file is served with other bytes than the listed hash.</summary>
    public string? Corrupt { get; set; }

    public List<(string Path, RangeHeaderValue? Range)> Downloads { get; } = [];

    /// <summary>The searches asked, by their query string.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Searches { get; } = new();

    private static byte[] Content(string seed, int size)
    {
        var bytes = new byte[size];
        var s = Encoding.UTF8.GetBytes(seed);
        for (var i = 0; i < size; i++)
        {
            bytes[i] = (byte)(s[i % s.Length] + i / 251);
        }
        return bytes;
    }

    private static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        if (path == "/api/models")
        {
            Searches.Enqueue(request.RequestUri!.Query);
            return Json(new[] { new { id = Repo, downloads = 1234, likes = 5, lastModified = "2026-09-01T00:00:00.000Z", gated = false, pipeline_tag = "text-generation", tags = new[] { "gguf", "qwen3" } } });
        }
        if (path is "/api/models/acme/Gated-GGUF")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        if (path == $"/api/models/{Repo}")
        {
            return Json(new { id = Repo, sha = Sha, gated = false, downloads = 1234, likes = 5, cardData = new { license = "apache-2.0" }, gguf = new { total = 600_000_000, architecture = "qwen3", context_length = 40960 } });
        }
        if (path == $"/api/models/{SnapshotRepo}")
        {
            return Json(new { id = SnapshotRepo, sha = Sha, gated = false, downloads = 7, likes = 1, cardData = new { license = "apache-2.0" } });
        }
        if (path == $"/api/models/{SnapshotRepo}/tree/{Sha}")
        {
            return Json(SnapshotFiles.Select(f => f.Key.EndsWith(".safetensors", StringComparison.Ordinal) || f.Key.EndsWith(".gguf", StringComparison.Ordinal)
                ? (object)new { type = "file", path = f.Key, size = 134, lfs = new { oid = Hash(f.Value), size = f.Value.Length } }
                : new { type = "file", path = f.Key, size = f.Value.Length }));
        }
        if (path.StartsWith($"/{SnapshotRepo}/resolve/{Sha}/", StringComparison.Ordinal) && SnapshotFiles.TryGetValue(path[$"/{SnapshotRepo}/resolve/{Sha}/".Length..], out var snap))
        {
            lock (Downloads)
            {
                Downloads.Add((SnapshotRepo + "/" + path[$"/{SnapshotRepo}/resolve/{Sha}/".Length..], request.Headers.Range));
            }
            var from = request.Headers.Range?.Ranges.First().From ?? 0;
            return new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(snap[(int)from..]) };
        }
        if (path == $"/api/models/{LayaRepo}")
        {
            return Json(new { id = LayaRepo, sha = Sha, gated = false, downloads = 99, likes = 9, cardData = new { license = "apache-2.0" } });
        }
        if (path == $"/api/models/{LayaRepo}/tree/{Sha}")
        {
            return Json(LayaFiles.Select(f => f.Key.EndsWith(".safetensors", StringComparison.Ordinal) || f.Key.EndsWith("tokenizer.json", StringComparison.Ordinal)
                ? (object)new { type = "file", path = f.Key, size = 134, lfs = new { oid = Hash(f.Value), size = f.Value.Length } }
                : new { type = "file", path = f.Key, size = f.Value.Length }));
        }
        if (path.StartsWith($"/{LayaRepo}/resolve/{Sha}/", StringComparison.Ordinal) && LayaFiles.TryGetValue(path[$"/{LayaRepo}/resolve/{Sha}/".Length..], out var laya))
        {
            lock (Downloads)
            {
                Downloads.Add((LayaRepo + "/" + path[$"/{LayaRepo}/resolve/{Sha}/".Length..], request.Headers.Range));
            }
            var at = request.Headers.Range?.Ranges.First().From ?? 0;
            return new HttpResponseMessage(at > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(laya[(int)at..]) };
        }
        if (path == $"/api/models/{Repo}/tree/{Sha}")
        {
            return Json(Files.Select(f => f.Key.EndsWith(".gguf", StringComparison.Ordinal)
                ? (object)new { type = "file", path = f.Key, size = 134, lfs = new { oid = Hash(f.Value), size = f.Value.Length } }
                : new { type = "file", path = f.Key, size = f.Value.Length }).Append(new { type = "directory", path = "Q8_0" }));
        }
        var prefix = $"/{Repo}/resolve/{Sha}/";
        if (path.StartsWith(prefix, StringComparison.Ordinal) && Files.TryGetValue(path[prefix.Length..], out var bytes))
        {
            var file = path[prefix.Length..];
            lock (Downloads)
            {
                Downloads.Add((file, request.Headers.Range));
            }
            if (Corrupt == file)
            {
                bytes = [.. bytes];
                bytes[^1] ^= 0xFF;
            }
            var from = request.Headers.Range?.Ranges.First().From ?? 0;
            var body = bytes[(int)from..];
            if (CutOnce is { } cut && cut.File == file)
            {
                CutOnce = null;
                var res = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(new CutStream(body, cut.After)) };
                return res;
            }
            await Task.Yield();
            return new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    /// <summary>A body that breaks after so many bytes, as a dropped connection does.</summary>
    private sealed class CutStream(byte[] data, int after) : Stream
    {
        private int _at;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= after)
            {
                throw new IOException("The connection was reset.");
            }
            var n = Math.Min(count, Math.Min(after, data.Length) - _at);
            Array.Copy(data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
