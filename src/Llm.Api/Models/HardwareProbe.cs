using System.Globalization;
using System.Text.Json;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// The GPUs and RAM the engines have, whatever they are: from Prometheus (the nvidia-smi and node exporters), else from an
/// engine's hardware file (services/gpu-probe.py: nvidia-smi or rocm-smi, any vendor), and, on a machine whose llama.cpp
/// runs on the CPU (Engine:Backend cpu), the RAM alone with no GPU. Null when none of them says: the Models page then
/// checks everything but memory. The servers beside the chat models are charged to the GPU each runs on (the picture
/// server's, the embedder's when it has one), never to a GPU by its position.
/// </summary>
public sealed class HardwareProbe(IHttpClientFactory http, IOptions<StackOptions> stack, IOptions<EngineOptions> engine, IServiceProvider services) : IDisposable
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (Hardware? Value, DateTimeOffset At) _last = (null, DateTimeOffset.MinValue);

    public void Dispose() => _gate.Dispose();

    public async Task<Hardware?> GetAsync(CancellationToken ct = default) => WithReserves(await ReadCachedAsync(ct), engine.Value, PictureGpu());

    /// <summary>The GPU the picture model runs on, as Admin → Models sets it; null when it names none (or none can be read now).</summary>
    private int? PictureGpu()
    {
        try
        {
            return services.GetService<MediaControl>()?.Pick(Llm.Core.Models.MediaModes.Image)?.Row.Gpu;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            return null;
        }
    }

    /// <summary>
    /// The machine with what the servers beside the chat models keep on its GPUs: the picture server on its model's GPU,
    /// else on Engine:MediaGpu, else on the first; the embedder on Engine:EmbedGpu when it has one. A GPU the machine does
    /// not have is charged nothing, and a machine without GPUs keeps everything in RAM.
    /// </summary>
    public static Hardware? WithReserves(Hardware? hw, EngineOptions o, int? pictureGpu)
    {
        if (hw?.Devices is not { Count: > 0 } devices)
        {
            return hw;
        }
        var reserves = new List<GpuReserve>();
        var picture = pictureGpu ?? o.MediaGpu ?? devices[0].Index;
        if (o.ImageReserveBytes > 0 && devices.Any(d => d.Index == picture))
        {
            reserves.Add(new GpuReserve(picture, "the picture server", o.ImageReserveBytes));
        }
        if (o.EmbedGpu is { } embed && o.EmbedReserveBytes > 0 && devices.Any(d => d.Index == embed))
        {
            reserves.Add(new GpuReserve(embed, "the embedder", o.EmbedReserveBytes));
        }
        return hw with { Reserves = reserves };
    }

    private async Task<Hardware?> ReadCachedAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _last.At < (_last.Value is null ? Retry : Fresh))
        {
            return _last.Value;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _last.At < (_last.Value is null ? Retry : Fresh))
            {
                return _last.Value;
            }
            Hardware? value = null;
            try
            {
                value = await ReadAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Prometheus is optional: no figures, no memory checks.
            }
            _last = (value, DateTimeOffset.UtcNow);
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Hardware?> ReadAsync(CancellationToken ct)
    {
        List<(Dictionary<string, string> Labels, double Value)> gpus = [], ram = [];
        try
        {
            gpus = await QueryAsync("nvidia_smi_memory_total_bytes", ct);
            ram = await QueryAsync("node_memory_MemTotal_bytes", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // No Prometheus: the hardware file may say.
        }
        var o = engine.Value;
        long? ramBytes = ram.Count > 0 ? (long)ram[0].Value : null;
        if (o.Backend.Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            // llama.cpp on the CPU: whatever GPUs the machine has, the models have none.
            return (ramBytes ?? ReadFile(o)?.RamTotal) is > 0 and var total ? Hardware.CpuOnly(total, o.RamReserveBytes) : null;
        }
        if (gpus.Count == 0 || ramBytes is null)
        {
            // Another vendor's GPUs, or no GPU exporter: what the engine's own probe wrote.
            return ReadFile(o) is { } fromFile && (ramBytes ?? fromFile.RamTotal) is > 0 and var r ? fromFile with { RamTotal = r } : null;
        }
        var reserved = await QueryAsync("nvidia_smi_memory_reserved_bytes", ct);
        var names = await QueryAsync("nvidia_smi_gpu_info", ct);
        var cap = await QueryAsync("nvidia_smi_power_limit_watts", ct);
        var own = await QueryAsync("nvidia_smi_power_default_limit_watts", ct);
        var index = await QueryAsync("nvidia_smi_index", ct);
        // One GPU per uuid, numbered as nvidia-smi numbers them (the engine runs with CUDA_DEVICE_ORDER=PCI_BUS_ID, so CUDA0 is index 0).
        static Dictionary<string, (Dictionary<string, string> Labels, double Value)> ByUuid(List<(Dictionary<string, string> Labels, double Value)> rows) =>
            rows.GroupBy(r => r.Labels.GetValueOrDefault("uuid") ?? "").ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var indexOf = ByUuid(index);
        var nameOf = ByUuid(names);
        var reservedOf = ByUuid(reserved);
        var devices = gpus.Select((g, i) =>
            {
                var uuid = g.Labels.GetValueOrDefault("uuid") ?? "";
                return new GpuDevice(
                    indexOf.TryGetValue(uuid, out var x) ? (int)x.Value : i,
                    nameOf.TryGetValue(uuid, out var n) ? n.Labels.GetValueOrDefault("name") ?? "GPU" : "GPU",
                    (long)g.Value,
                    reservedOf.TryGetValue(uuid, out var r) ? (long)r.Value : 0);
            })
            .OrderBy(d => d.Index).ToList();
        return new Hardware(
            names.Select(n => n.Labels.GetValueOrDefault("name")).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
            gpus.Count, (long)gpus.Sum(g => g.Value), (long)reserved.Sum(r => r.Value),
            ramBytes.Value, o.RamReserveBytes,
            cap.Count > 0 ? cap.Min(c => c.Value) : null, own.Count > 0 ? own.Min(c => c.Value) : null, devices);
    }

    private static Hardware? ReadFile(EngineOptions o)
    {
        try
        {
            // The file named, else the one SGLang's supervisor writes (a stack running SGLang without vLLM).
            var file = File.Exists(o.HardwareFile) ? o.HardwareFile : Path.Combine(o.VllmStatusDir, "sglang", "hardware.json");
            return File.Exists(file) ? FromFile(File.ReadAllText(file), o) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The machine as services/gpu-probe.py writes it: {"ram_bytes", "gpus": [{"index", "name", "vendor", "backend",
    /// "total_bytes", "free_bytes", "reserved_bytes"}]}. No GPU listed: a machine with none. Null when it is not that.
    /// </summary>
    public static Hardware? FromFile(string json, EngineOptions o)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var listed = root.TryGetProperty("gpus", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToList() : [];
            var devices = listed
                .Select((g, i) => new GpuDevice(g.TryGetProperty("index", out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : i,
                    Str(g, "name") ?? "GPU", Num(g, "total_bytes"), Num(g, "reserved_bytes"), Str(g, "vendor") ?? "other", Str(g, "backend") ?? "vulkan",
                    g.TryGetProperty("free_bytes", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : null))
                .Where(d => d.Total > 0).OrderBy(d => d.Index).ToList();
            var ram = Num(root, "ram_bytes");
            return devices.Count == 0
                ? Hardware.CpuOnly(ram, o.RamReserveBytes)
                : new Hardware(devices[0].Name, devices.Count, devices.Sum(d => d.Total), devices.Sum(d => d.Reserved), ram, o.RamReserveBytes, Devices: devices);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private async Task<List<(Dictionary<string, string> Labels, double Value)>> QueryAsync(string query, CancellationToken ct)
    {
        using var client = http.CreateClient("probe");
        var url = new Uri(stack.Value.PrometheusUrl.TrimEnd('/') + "/api/v1/query?query=" + Uri.EscapeDataString(query));
        using var res = await client.GetAsync(url, ct);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var rows = new List<(Dictionary<string, string>, double)>();
        foreach (var r in doc.RootElement.GetProperty("data").GetProperty("result").EnumerateArray())
        {
            var labels = r.GetProperty("metric").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
            if (double.TryParse(r.GetProperty("value")[1].GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                rows.Add((labels, v));
            }
        }
        return rows;
    }
}
