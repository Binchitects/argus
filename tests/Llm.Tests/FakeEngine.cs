using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>
/// llama.cpp's router, as the app sees it: its models are the default one and
/// the sections of the presets file the app writes (the real one reads it when
/// it restarts), one loaded at a time.
/// </summary>
public sealed class FakeEngine : HttpMessageHandler
{
    public const string Key = "engine-key-for-tests";
    private readonly Dictionary<string, string> _status = [];

    public string DefaultModel { get; set; } = "Qwen3.8-Flash-Next";
    public string? PresetsFile { get; set; }

    /// <summary>Models whose load fails, as with a file that is incomplete: the router marks them failed.</summary>
    public HashSet<string> Broken { get; } = [];
    public List<(string Method, string Path, string? Model)> Calls { get; } = [];

    public void Reset(string? presetsFile)
    {
        lock (_status)
        {
            _status.Clear();
            _status[DefaultModel] = "loaded";
            Calls.Clear();
            Broken.Clear();
        }
        PresetsFile = presetsFile;
    }

    /// <summary>The engine restarted: nothing is loaded.</summary>
    public void Restart()
    {
        lock (_status)
        {
            foreach (var k in _status.Keys.ToList())
            {
                _status[k] = "unloaded";
            }
        }
    }

    /// <summary>How many times the app asked to load the model.</summary>
    public int LoadsOf(string model)
    {
        lock (_status)
        {
            return Calls.Count(c => c.Path == "/models/load" && c.Model == model);
        }
    }

    public string? StatusOf(string model)
    {
        lock (_status)
        {
            Known();
            return _status.GetValueOrDefault(model);
        }
    }

    private void Known()
    {
        var names = new List<string> { DefaultModel };
        if (PresetsFile is not null && File.Exists(PresetsFile))
        {
            names.AddRange(File.ReadAllLines(PresetsFile).Where(l => l.StartsWith('[') && l.EndsWith(']')).Select(l => l[1..^1]));
        }
        foreach (var n in names.Where(n => !_status.ContainsKey(n)))
        {
            _status[n] = "unloaded";
        }
        foreach (var gone in _status.Keys.Where(k => !names.Contains(k)).ToList())
        {
            _status.Remove(gone);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization?.Parameter != Key)
        {
            return Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Invalid API Key"}}""");
        }
        var path = request.RequestUri!.AbsolutePath;
        var model = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?["model"]?.GetValue<string>();
        lock (_status)
        {
            Calls.Add((request.Method.Method, path, model));
            Known();
            switch (path)
            {
                case "/models":
                    return Json(HttpStatusCode.OK, new JsonObject
                    {
                        ["data"] = new JsonArray([.. _status.Select(s => (JsonNode)new JsonObject
                        {
                            ["id"] = s.Key,
                            ["status"] = s.Value == "failed" ? new JsonObject { ["value"] = "unloaded", ["exit_code"] = 1, ["failed"] = true } : new JsonObject { ["value"] = s.Value },
                        })]),
                    }.ToJsonString());
                case "/models/load" when model is not null && _status.ContainsKey(model):
                    foreach (var k in _status.Keys.ToList())
                    {
                        _status[k] = "unloaded";
                    }
                    _status[model] = Broken.Contains(model) ? "failed" : "loaded";
                    return Json(HttpStatusCode.OK, """{"success":true}""");
                case "/models/unload" when model is not null && _status.ContainsKey(model):
                    _status[model] = "unloaded";
                    return Json(HttpStatusCode.OK, """{"success":true}""");
                default:
                    return Json(HttpStatusCode.BadRequest, """{"error":{"message":"model not found"}}""");
            }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>Real GGUF headers, small: what the model library reads.</summary>
public static class GgufFile
{
    public static void Write(string path, string architecture, string? name = null, string? sizeLabel = null, uint? context = null, int padding = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path), Encoding.UTF8);
        var kv = new List<Action>();
        void Str(string key, string value) => kv.Add(() => { S(w, key); w.Write(8u); S(w, value); });
        Str("general.architecture", architecture);
        // An array before the keys wanted, as tokenizers put in real files.
        kv.Add(() => { S(w, "general.tags"); w.Write(9u); w.Write(8u); w.Write(2ul); S(w, "text"); S(w, "chat"); });
        if (name is not null)
        {
            Str("general.name", name);
        }
        if (sizeLabel is not null)
        {
            Str("general.size_label", sizeLabel);
        }
        if (context is { } c)
        {
            kv.Add(() => { S(w, architecture + ".context_length"); w.Write(4u); w.Write(c); });
        }
        w.Write(0x46554747u);
        w.Write(3u);
        w.Write(0ul);
        w.Write((ulong)kv.Count);
        foreach (var k in kv)
        {
            k();
        }
        w.Write(new byte[padding]);
    }

    private static void S(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write((ulong)b.Length);
        w.Write(b);
    }
}
