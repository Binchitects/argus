using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Llm.Api.Dashboards;

public sealed class DatasourceException(string message) : Exception(message);

/// <summary>A series or a single value as Prometheus and Loki give them: labels, and [epoch ms, value] points.</summary>
public sealed record RawSeries(IReadOnlyDictionary<string, string> Labels, IReadOnlyList<double?[]> Points);

/// <summary>One log line: its time (epoch ms, and the exact nanoseconds), its stream's labels, the line.</summary>
public sealed record LogLine(double Time, string Nanos, IReadOnlyDictionary<string, string> Labels, string Line);

internal static class Json
{
    public static async Task<JsonElement> ReadAsync(HttpResponseMessage res, string what, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonElement doc;
        try
        {
            doc = JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new DatasourceException($"{what} answered {(int)res.StatusCode}: {Short(text)}");
        }
        // Prometheus and Loki wrap their answers ({"status": ...}); Alertmanager's alerts are a bare list.
        var wrapped = doc.ValueKind == JsonValueKind.Object;
        if (!res.IsSuccessStatusCode || (wrapped && doc.TryGetProperty("status", out var st) && st.GetString() == "error"))
        {
            var error = wrapped && doc.TryGetProperty("error", out var e) ? e.GetString() : Short(text);
            throw new DatasourceException($"{what}: {error}");
        }
        return doc;
    }

    private static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public static Dictionary<string, string> Labels(JsonElement o) =>
        o.ValueKind == JsonValueKind.Object ? o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal) : [];

    /// <summary>"[1726, \"0.5\"]": Prometheus and Loki send values as strings (NaN and ±Inf too).</summary>
    public static double?[] Point(JsonElement pair)
    {
        var t = pair[0].GetDouble() * 1000;
        var raw = pair[1].GetString();
        return [t, double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null];
    }

    public static string Unix(DateTimeOffset t) => (t.ToUnixTimeMilliseconds() / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Prometheus's HTTP API: range and instant queries, and a label's values.</summary>
public sealed class PromDatasource(HttpClient http, IOptions<Operations.StackOptions> stack)
{
    public const string Uid = "prometheus";

    private Uri Url(string path) => new(stack.Value.PrometheusUrl.TrimEnd('/') + path);

    public async Task<IReadOnlyList<RawSeries>> RangeAsync(string query, DateTimeOffset start, DateTimeOffset end, TimeSpan step, CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["query"] = query, ["start"] = Json.Unix(start), ["end"] = Json.Unix(end),
            ["step"] = step.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        });
        using var res = await http.PostAsync(Url("/api/v1/query_range"), body, ct);
        var doc = await Json.ReadAsync(res, "Prometheus", ct);
        return [.. doc.GetProperty("data").GetProperty("result").EnumerateArray()
            .Select(r => new RawSeries(Json.Labels(r.GetProperty("metric")), [.. r.GetProperty("values").EnumerateArray().Select(Json.Point)]))];
    }

    public async Task<IReadOnlyList<RawSeries>> InstantAsync(string query, DateTimeOffset time, CancellationToken ct)
    {
        using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["query"] = query, ["time"] = Json.Unix(time) });
        using var res = await http.PostAsync(Url("/api/v1/query"), body, ct);
        var data = (await Json.ReadAsync(res, "Prometheus", ct)).GetProperty("data");
        var result = data.GetProperty("result");
        return data.GetProperty("resultType").GetString() switch
        {
            "vector" => [.. result.EnumerateArray().Select(r => new RawSeries(Json.Labels(r.GetProperty("metric")), [Json.Point(r.GetProperty("value"))]))],
            "matrix" => [.. result.EnumerateArray().Select(r => new RawSeries(Json.Labels(r.GetProperty("metric")), [.. r.GetProperty("values").EnumerateArray().Select(Json.Point)]))],
            "scalar" => [new RawSeries(new Dictionary<string, string>(), [Json.Point(result)])],
            _ => [],
        };
    }

    /// <summary>Any other read of the API (the rules), as JSON.</summary>
    public async Task<JsonElement> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var res = await http.GetAsync(Url(pathAndQuery), ct);
        return await Json.ReadAsync(res, "Prometheus", ct);
    }

    public async Task<IReadOnlyList<string>> LabelValuesAsync(string label, string? match, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var q = $"/api/v1/label/{Uri.EscapeDataString(label)}/values?start={Json.Unix(start)}&end={Json.Unix(end)}" + (match is { Length: > 0 } ? "&match[]=" + Uri.EscapeDataString(match) : "");
        using var res = await http.GetAsync(Url(q), ct);
        return [.. (await Json.ReadAsync(res, "Prometheus", ct)).GetProperty("data").EnumerateArray().Select(v => v.GetString() ?? "")];
    }
}

/// <summary>Loki's HTTP API: log lines, metric queries over them, and a label's values.</summary>
public sealed class LokiDatasource(HttpClient http, IOptions<DashboardOptions> options)
{
    public const string Uid = "loki";

    private Uri Url(string path) => new(options.Value.LokiUrl.TrimEnd('/') + path);

    /// <summary>Log lines (newest first) or, for a metric query, series.</summary>
    public Task<(IReadOnlyList<LogLine>? Lines, IReadOnlyList<RawSeries>? Series)> RangeAsync(string query, DateTimeOffset start, DateTimeOffset end, TimeSpan step,
        int limit, CancellationToken ct, string direction = "backward") =>
        RangeAsync(query, Nanos(start), Nanos(end), step, limit, ct, direction);

    /// <summary>The same, from and to the nanosecond: a live tail asks for the lines after the last one it has.</summary>
    public async Task<(IReadOnlyList<LogLine>? Lines, IReadOnlyList<RawSeries>? Series)> RangeAsync(string query, string startNanos, string endNanos, TimeSpan step,
        int limit, CancellationToken ct, string direction = "backward")
    {
        var q = $"/loki/api/v1/query_range?query={Uri.EscapeDataString(query)}&start={startNanos}&end={endNanos}" +
            $"&step={step.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}&limit={limit}&direction={direction}";
        using var res = await http.GetAsync(Url(q), ct);
        return Shape((await Json.ReadAsync(res, "Loki", ct)).GetProperty("data"), direction);
    }

    public async Task<(IReadOnlyList<LogLine>? Lines, IReadOnlyList<RawSeries>? Series)> InstantAsync(string query, DateTimeOffset time, int limit, CancellationToken ct)
    {
        var q = $"/loki/api/v1/query?query={Uri.EscapeDataString(query)}&time={time.ToUnixTimeMilliseconds()}000000&limit={limit}";
        using var res = await http.GetAsync(Url(q), ct);
        return Shape((await Json.ReadAsync(res, "Loki", ct)).GetProperty("data"), "backward");
    }

    public static string Nanos(DateTimeOffset t) => t.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "000000";

    private static (IReadOnlyList<LogLine>?, IReadOnlyList<RawSeries>?) Shape(JsonElement data, string direction)
    {
        var result = data.GetProperty("result");
        switch (data.GetProperty("resultType").GetString())
        {
            case "streams":
                var lines = new List<LogLine>();
                foreach (var stream in result.EnumerateArray())
                {
                    var labels = Json.Labels(stream.GetProperty("stream"));
                    foreach (var v in stream.GetProperty("values").EnumerateArray())
                    {
                        var nanos = v[0].GetString() ?? "0";
                        lines.Add(new LogLine(long.Parse(nanos, CultureInfo.InvariantCulture) / 1e6, nanos, labels, v[1].GetString() ?? ""));
                    }
                }
                // One order across streams, as Grafana shows them.
                var sorted = direction == "forward"
                    ? lines.OrderBy(l => l.Nanos.Length).ThenBy(l => l.Nanos, StringComparer.Ordinal)
                    : lines.OrderByDescending(l => l.Nanos.Length).ThenByDescending(l => l.Nanos, StringComparer.Ordinal);
                return ([.. sorted], null);
            case "matrix":
                return (null, [.. result.EnumerateArray().Select(r => new RawSeries(Json.Labels(r.GetProperty("metric")), [.. r.GetProperty("values").EnumerateArray().Select(Json.Point)]))]);
            case "vector":
                return (null, [.. result.EnumerateArray().Select(r => new RawSeries(Json.Labels(r.GetProperty("metric")), [Json.Point(r.GetProperty("value"))]))]);
            default:
                return (null, []);
        }
    }

    public async Task<IReadOnlyList<string>> LabelValuesAsync(string label, string? stream, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var q = $"/loki/api/v1/label/{Uri.EscapeDataString(label)}/values?start={start.ToUnixTimeMilliseconds()}000000&end={end.ToUnixTimeMilliseconds()}000000" +
            (stream is { Length: > 0 } ? "&query=" + Uri.EscapeDataString(stream) : "");
        using var res = await http.GetAsync(Url(q), ct);
        var doc = await Json.ReadAsync(res, "Loki", ct);
        return doc.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? [.. data.EnumerateArray().Select(v => v.GetString() ?? "")] : [];
    }
}
