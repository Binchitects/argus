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

    /// <summary>The picture server's reserve, by name.</summary>
    public const string Picture = "the picture server";

    /// <summary>The embedder's reserve, by name.</summary>
    public const string Embedder = "the embedder";

    /// <summary>What a GPU is doing now: how busy it is (0 to 1; null: not known) and the memory in use (null: not known).</summary>
    public sealed record GpuLoad(double? Busy, long? Used);

    private sealed record LoadSeen(IReadOnlyDictionary<int, GpuLoad> Value, DateTimeOffset At);

    private static readonly TimeSpan LoadFresh = TimeSpan.FromSeconds(5);
    private LoadSeen _load = new(new Dictionary<int, GpuLoad>(), DateTimeOffset.MinValue);

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// What each GPU is doing now, by its index: how busy it is and the memory in use, from the NVIDIA exporter, else from
    /// DCGM's (read again after 5 seconds); else the memory in use the hardware file says (any vendor). Empty when none says.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, GpuLoad>> LoadAsync(CancellationToken ct = default)
    {
        if (_load is { } seen && DateTimeOffset.UtcNow - seen.At < LoadFresh)
        {
            return seen.Value;
        }
        Dictionary<int, GpuLoad> value;
        try
        {
            value = Loads(await QueryAsync("nvidia_smi_index", ct), await QueryAsync("nvidia_smi_utilization_gpu_ratio", ct), await QueryAsync("nvidia_smi_memory_used_bytes", ct));
            if (value.Count == 0)
            {
                // No nvidia_smi exporter (Kubernetes uses the cluster's DCGM exporter): DCGM's.
                value = DcgmLoads(await QueryAsync("DCGM_FI_DEV_GPU_UTIL", ct), await QueryAsync("DCGM_FI_DEV_FB_USED", ct));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // No Prometheus: the hardware file may say.
            value = new Dictionary<int, GpuLoad>();
        }
        if (value.Count == 0 && ReadFile(engine.Value)?.Devices is { } devices)
        {
            value = devices.Where(d => d.Free is not null).ToDictionary(d => d.Index, d => new GpuLoad(null, Math.Max(0, d.Total - d.Free!.Value)));
        }
        _load = new(value, DateTimeOffset.UtcNow);
        return value;
    }

    /// <summary>The NVIDIA exporter's rows as each GPU's load, by its index (nvidia_smi_index, matched by its uuid; a GPU without one is left out).</summary>
    public static Dictionary<int, GpuLoad> Loads(IReadOnlyList<(Dictionary<string, string> Labels, double Value)> index,
        IReadOnlyList<(Dictionary<string, string> Labels, double Value)> busy, IReadOnlyList<(Dictionary<string, string> Labels, double Value)> used)
    {
        static Dictionary<string, double> ByUuid(IReadOnlyList<(Dictionary<string, string> Labels, double Value)> rows) =>
            rows.GroupBy(r => r.Labels.GetValueOrDefault("uuid") ?? "").Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        var busyOf = ByUuid(busy);
        var usedOf = ByUuid(used);
        return ByUuid(index).GroupBy(x => (int)x.Value).ToDictionary(g => g.Key, g => g.First().Key is var uuid
            ? new GpuLoad(busyOf.TryGetValue(uuid, out var b) ? Math.Clamp(b, 0, 1) : null, usedOf.TryGetValue(uuid, out var u) ? (long)u : null)
            : new GpuLoad(null, null));
    }

    /// <summary>
    /// NVIDIA's DCGM exporter's rows as each GPU's load, by its index (the gpu label; a row without one is left out): how
    /// busy in percent (DCGM_FI_DEV_GPU_UTIL), the memory in use in MiB (DCGM_FI_DEV_FB_USED).
    /// </summary>
    public static Dictionary<int, GpuLoad> DcgmLoads(IReadOnlyList<(Dictionary<string, string> Labels, double Value)> busy, IReadOnlyList<(Dictionary<string, string> Labels, double Value)> used)
    {
        static Dictionary<int, double> ByGpu(IReadOnlyList<(Dictionary<string, string> Labels, double Value)> rows) =>
            rows.Select(r => (Gpu: int.TryParse(r.Labels.GetValueOrDefault("gpu"), NumberStyles.None, CultureInfo.InvariantCulture, out var g) ? g : -1, r.Value))
                .Where(r => r.Gpu >= 0).GroupBy(r => r.Gpu).ToDictionary(g => g.Key, g => g.First().Value);
        var busyOf = ByGpu(busy);
        var usedOf = ByGpu(used);
        return busyOf.Keys.Union(usedOf.Keys).ToDictionary(g => g, g => new GpuLoad(
            busyOf.TryGetValue(g, out var b) ? Math.Clamp(b / 100, 0, 1) : null, usedOf.TryGetValue(g, out var u) ? (long)u << 20 : null));
    }

    public async Task<Hardware?> GetAsync(CancellationToken ct = default) => WithReserves(await ReadCachedAsync(ct), engine.Value, PictureGpu());

    /// <summary>
    /// The GPU the picture server runs on: its control file's (MediaControl placed it), else the GPU its model names in
    /// Admin → Models; null when neither says (or nothing can be read now).
    /// </summary>
    private int? PictureGpu()
    {
        try
        {
            return services.GetService<MediaControl>() is { } media ? media.GpuOf("imagegen") ?? media.Pick(Llm.Core.Models.MediaModes.Image)?.Row.Gpu : null;
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
            reserves.Add(new GpuReserve(picture, Picture, o.ImageReserveBytes));
        }
        if (o.EmbedGpu is { } embed && o.EmbedReserveBytes > 0 && devices.Any(d => d.Index == embed))
        {
            reserves.Add(new GpuReserve(embed, Embedder, o.EmbedReserveBytes));
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
