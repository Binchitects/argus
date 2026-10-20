using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Llm.Tests;

/// <summary>
/// A vLLM engine as the app sees it, in process: services/vllm/supervisor.sh (each <see cref="Tick"/> reads the desired
/// state and writes each process's status and the heartbeat, a process ready at once, or after <see cref="LoadTicks"/>)
/// and its processes' HTTP (/metrics with the requests running and waiting the test sets, by port). The real
/// supervisor and fake vLLM are tested in tests/deploy and by VllmBackendTests in a container.
/// </summary>
public sealed class FakeVllm(string config, string status) : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, int> _ticks = new(StringComparer.Ordinal);

    /// <summary>Requests running and waiting in each process, by port.</summary>
    public ConcurrentDictionary<int, (int Running, int Waiting)> Busy { get; } = new();

    /// <summary>Processes that fail to start (by entry name), as one out of memory.</summary>
    public HashSet<string> Broken { get; } = [];

    /// <summary>Whether the supervisor runs (writes its heartbeat).</summary>
    public bool Running { get; set; } = true;

    /// <summary>Ticks a process loads for before it is ready.</summary>
    public int LoadTicks { get; set; }

    /// <summary>The /sleep and /wake_up the supervisor sent, in order (entry name and which).</summary>
    public ConcurrentQueue<string> Sleeps { get; } = new();

    public void Tick()
    {
        Directory.CreateDirectory(status);
        if (Running)
        {
            File.WriteAllText(Path.Combine(status, "supervisor.json"), $$"""{"at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}""");
        }
        var path = Path.Combine(config, "models.json");
        var desired = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["models"] is JsonArray list ? list.OfType<JsonObject>().ToList() : [];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in desired)
        {
            var name = e["name"]!.GetValue<string>();
            names.Add(name);
            var ticks = _ticks.AddOrUpdate(name, 1, (_, n) => n + 1);
            var was = Phase(name);
            var phase = Broken.Contains(name) ? "failed" : ticks <= LoadTicks ? "loading" : e["sleep"]?.GetValue<bool>() == true ? "sleeping" : "ready";
            if (was != phase && phase is "sleeping" or "ready" && was is "sleeping" or "ready")
            {
                Sleeps.Enqueue($"{name} {(phase == "sleeping" ? "sleep" : "wake")}");
            }
            Write(name, phase, e["port"]!.GetValue<int>());
        }
        foreach (var file in Directory.EnumerateFiles(status, "*.json").Where(f => !names.Contains(Path.GetFileNameWithoutExtension(f)) && Path.GetFileName(f) is not ("supervisor.json" or "hardware.json")))
        {
            File.Delete(file);
            _ticks.TryRemove(Path.GetFileNameWithoutExtension(file), out _);
        }
    }

    public string? Phase(string name)
    {
        var path = Path.Combine(status, name + ".json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["phase"]?.GetValue<string>() : null;
    }

    private void Write(string name, string phase, int port) =>
        File.WriteAllText(Path.Combine(status, name + ".json"), new JsonObject { ["name"] = name, ["phase"] = phase, ["port"] = port, ["log"] = new JsonArray() }.ToJsonString());

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.AbsolutePath == "/metrics")
        {
            var (running, waiting) = Busy.GetValueOrDefault(uri.Port);
            var text = $"""
                # TYPE vllm:num_requests_running gauge
                vllm:num_requests_running{"{"}model_name="m"{"}"} {running}
                vllm:num_requests_waiting{"{"}model_name="m"{"}"} {waiting}
                vllm:kv_cache_usage_perc{"{"}model_name="m"{"}"} 0.25
                vllm:prefix_cache_queries_total{"{"}model_name="m"{"}"} 1000
                vllm:prefix_cache_hits_total{"{"}model_name="m"{"}"} 750

                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") });
        }
        throw new HttpRequestException(HttpRequestError.ConnectionError, $"Connection refused ({uri.Authority})");
    }
}
