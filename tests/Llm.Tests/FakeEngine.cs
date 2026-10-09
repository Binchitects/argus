using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>
/// llama.cpp's router, as the app sees it: its models are the default one and
/// the sections of the presets file the app writes (the real one reads it when
/// it restarts), up to <see cref="Max"/> loaded at once: loading one more first
/// unloads the one used least recently.
/// </summary>
public sealed class FakeEngine : HttpMessageHandler
{
    public const string Key = "engine-key-for-tests";
    private readonly Dictionary<string, string> _status = [];
    /// <summary>Loaded models, least recently used first.</summary>
    private readonly List<string> _used = [];

    /// <summary>--models-max.</summary>
    public int Max { get; set; } = 1;

    public string DefaultModel { get; set; } = "Qwen3.8-Flash-Next";
    public string? PresetsFile { get; set; }

    /// <summary>Models whose load fails, as with a file that is incomplete: the router marks them failed.</summary>
    public HashSet<string> Broken { get; } = [];

    /// <summary>Slots answering now by model, as /slots says (requests the app did not send: an API key's, another replica's).</summary>
    public Dictionary<string, HashSet<int>> BusySlots { get; } = [];

    /// <summary>Slots a model has other than its preset says (the preset changed, the engine has not restarted yet).</summary>
    public Dictionary<string, int> SlotCounts { get; } = [];

    /// <summary>How long /slots takes to answer: llama-server answers between two batches, so while it reads a long prompt, seconds.</summary>
    public TimeSpan SlotsDelay { get; set; }

    /// <summary>
    /// Whether a model told to unload stops slowly, as the router's do: listed loaded (and no longer counted against
    /// <see cref="Max"/>) until <see cref="Stop"/>.
    /// </summary>
    public bool SlowStop { get; set; }

    /// <summary>The models told to unload that have not stopped yet (<see cref="SlowStop"/>).</summary>
    private readonly HashSet<string> _stopping = [];
    public List<(string Method, string Path, string? Model)> Calls { get; } = [];

    public void Reset(string? presetsFile)
    {
        lock (_status)
        {
            _status.Clear();
            _used.Clear();
            _status[DefaultModel] = "loaded";
            _used.Add(DefaultModel);
            Calls.Clear();
            Broken.Clear();
            BusySlots.Clear();
            SlotCounts.Clear();
            SlotsDelay = TimeSpan.Zero;
            SlowStop = false;
            _stopping.Clear();
            Max = 1;
        }
        PresetsFile = presetsFile;
    }

    /// <summary>The models told to unload have stopped (<see cref="SlowStop"/>).</summary>
    public void Stop()
    {
        lock (_status)
        {
            foreach (var m in _stopping)
            {
                _status[m] = "unloaded";
            }
            _stopping.Clear();
        }
    }

    /// <summary>How many times the app called <paramref name="path"/> (for <paramref name="model"/>, when given).</summary>
    public int CallsTo(string path, string? model = null)
    {
        lock (_status)
        {
            return Calls.Count(c => c.Path == path && (model is null || c.Model == model));
        }
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
            _used.Clear();
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
        if (path == "/slots" && SlotsDelay > TimeSpan.Zero)
        {
            await Task.Delay(SlotsDelay, cancellationToken);
        }
        var model = request.Content is null
            ? System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["model"]
            : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))?["model"]?.GetValue<string>();
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
                    _used.Remove(model);
                    _stopping.Remove(model);
                    // At the limit, the one used least recently makes room.
                    while (_used.Count >= Max && _used.Count > 0)
                    {
                        _status[_used[0]] = "unloaded";
                        _used.RemoveAt(0);
                    }
                    _status[model] = Broken.Contains(model) ? "failed" : "loaded";
                    if (_status[model] == "loaded")
                    {
                        _used.Add(model);
                    }
                    return Json(HttpStatusCode.OK, """{"success":true}""");
                case "/slots" when model is not null && _status.GetValueOrDefault(model) == "loaded":
                    // Its slots, as its preset sets them; the busy ones as the test says.
                    var busy = BusySlots.GetValueOrDefault(model) ?? [];
                    return Json(HttpStatusCode.OK, new JsonArray([.. Enumerable.Range(0, SlotCounts.GetValueOrDefault(model, ParallelOf(model)))
                        .Select(i => (JsonNode)new JsonObject { ["id"] = i, ["is_processing"] = busy.Contains(i), ["n_ctx"] = 8192 })]).ToJsonString());
                case "/models/unload" when model is not null && _status.ContainsKey(model):
                    if (SlowStop && _status[model] == "loaded")
                    {
                        _stopping.Add(model);
                    }
                    else
                    {
                        _status[model] = "unloaded";
                    }
                    _used.Remove(model);
                    return Json(HttpStatusCode.OK, """{"success":true}""");
                default:
                    return Json(HttpStatusCode.BadRequest, """{"error":{"message":"model not found"}}""");
            }
        }
    }

    /// <summary>A model's slots: "parallel = N" in its section of the presets, else 1.</summary>
    private int ParallelOf(string model)
    {
        if (PresetsFile is null || !File.Exists(PresetsFile))
        {
            return 1;
        }
        var section = File.ReadAllLines(PresetsFile).SkipWhile(l => l != $"[{model}]").Skip(1).TakeWhile(l => !l.StartsWith('['));
        return section.Select(l => l.Split('=', 2, StringSplitOptions.TrimEntries)).Where(p => p.Length == 2 && p[0] == "parallel")
            .Select(p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault(1);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
