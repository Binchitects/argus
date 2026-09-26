using System.Globalization;
using System.Text.Json;
using Llm.Api.Operations;
using Microsoft.Extensions.Options;

namespace Llm.Api.Models;

/// <summary>
/// The GPU memory and RAM the engine has, from Prometheus (the nvidia-smi and
/// node exporters). Null when Prometheus or the GPU exporter is not there: the
/// Models page then checks everything but memory.
/// </summary>
public sealed class HardwareProbe(IHttpClientFactory http, IOptions<StackOptions> stack, IOptions<EngineOptions> engine) : IDisposable
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (Hardware? Value, DateTimeOffset At) _last = (null, DateTimeOffset.MinValue);

    public void Dispose() => _gate.Dispose();

    public async Task<Hardware?> GetAsync(CancellationToken ct = default)
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
        var gpus = await QueryAsync("nvidia_smi_memory_total_bytes", ct);
        var ram = await QueryAsync("node_memory_MemTotal_bytes", ct);
        if (gpus.Count == 0 || ram.Count == 0)
        {
            return null;
        }
        var reserved = await QueryAsync("nvidia_smi_memory_reserved_bytes", ct);
        var names = await QueryAsync("nvidia_smi_gpu_info", ct);
        var cap = await QueryAsync("nvidia_smi_power_limit_watts", ct);
        var own = await QueryAsync("nvidia_smi_power_default_limit_watts", ct);
        var o = engine.Value;
        return new Hardware(
            names.Select(n => n.Labels.GetValueOrDefault("name")).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
            gpus.Count, (long)gpus.Sum(g => g.Value), (long)reserved.Sum(r => r.Value), o.ImageReserveBytes,
            (long)ram[0].Value, o.RamReserveBytes,
            cap.Count > 0 ? cap.Min(c => c.Value) : null, own.Count > 0 ? own.Min(c => c.Value) : null);
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
