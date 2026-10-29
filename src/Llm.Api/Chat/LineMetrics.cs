using System.Globalization;
using System.Text;
using Llm.Api.Access;

namespace Llm.Api.Chat;

/// <summary>
/// The answers' line for Prometheus (job "app", Admin → Dashboards): requests running and waiting by model and kind (chat,
/// api), the people with one running, what the line refused (busy, too long) and the time its requests waited; and where
/// each loaded model sits (its GPUs, or RAM) with the memory the app reckons it takes there (the Hardware dashboard). Served on
/// the app's own port to the stack's network only: Traefik routes no /metrics to the app, and a request that came through it
/// (X-Forwarded-For) is refused.
/// </summary>
public static class LineMetrics
{
    public static void MapLineMetrics(this IEndpointRouteBuilder app) =>
        app.MapGet("/metrics", (HttpContext http, AnswerGate gate, Models.EngineState engine) =>
        {
            // Through Traefik: X-Forwarded-For, which the forwarded-headers middleware takes in (keeping it as X-Original-For) and
            // whose client becomes the remote address. Only the stack's network (a private address, or none in tests) reads it.
            if (http.Request.Headers.ContainsKey("X-Forwarded-For") || http.Request.Headers.ContainsKey("X-Original-For")
                || http.Connection.RemoteIpAddress is { } from && !Internal(from))
            {
                return Results.NotFound();
            }
            return Results.Text(Render(gate) + RenderPlaces(engine), "text/plain; version=0.0.4");
        }).AllowAnonymous("Prometheus, on the stack's network: numbers of the line, no names.");

    /// <summary>Loopback, or a private network's address (10/8, 172.16/12, 192.168/16, fc00::/7, link-local).</summary>
    private static bool Internal(System.Net.IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal)
        {
            return true;
        }
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }

    /// <summary>
    /// Each loaded model's memory as the app reckons it (<see cref="Models.EngineState.Places"/>): a series for each of its
    /// GPUs (on="GPU 0"), one for RAM when it keeps some there, and on a machine without GPUs all of it in RAM. A model
    /// whose figures are not known is listed once, with 0, so the dashboard still shows that it runs.
    /// </summary>
    public static string RenderPlaces(Models.EngineState engine)
    {
        var sb = new StringBuilder("# HELP arena_model_memory_bytes Memory each loaded model takes, as the app reckons it: on each of its GPUs, and in RAM.\n# TYPE arena_model_memory_bytes gauge\n");
        var gpus = engine.Hardware?.Gpus ?? 0;
        foreach (var m in engine.Now.Models.Where(m => m.Status == "loaded").OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var place = engine.Places.GetValueOrDefault(m.Name);
            var kind = place?.Kind ?? engine.KindOf(m.Name);
            void Line(string on, long bytes) =>
                sb.Append(CultureInfo.InvariantCulture, $"arena_model_memory_bytes{{model=\"{L(m.Name)}\",engine=\"{L(kind)}\",on=\"{on}\"}} {bytes}\n");
            foreach (var (gpu, bytes) in (place?.PerGpu ?? new Dictionary<int, long>()).OrderBy(g => g.Key))
            {
                Line($"GPU {gpu}", bytes);
            }
            if (place is { Ram: > 0 })
            {
                Line("RAM", place.Ram);
            }
            else if (place is null || place.PerGpu.Count == 0)
            {
                Line(gpus == 0 ? "RAM" : "not known", 0);
            }
        }
        return sb.ToString();
    }

    private static string L(string v) => v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    public static string Render(AnswerGate gate)
    {
        var (running, waiting) = gate.Snapshot();
        var (refused, waited) = gate.Counters();
        var sb = new StringBuilder();
        void Gauge(string name, string help, IEnumerable<AnswerGate.Entry> entries)
        {
            sb.Append(CultureInfo.InvariantCulture, $"# HELP {name} {help}\n# TYPE {name} gauge\n");
            foreach (var g in entries.GroupBy(e => (e.Model, e.Kind)))
            {
                sb.Append(CultureInfo.InvariantCulture, $"{name}{{model=\"{L(g.Key.Model)}\",kind=\"{g.Key.Kind}\"}} {g.Count()}\n");
            }
        }
        Gauge("arena_line_running", "Requests running now (chat answers and API requests).", running);
        Gauge("arena_line_waiting", "Requests waiting their turn now.", waiting);
        sb.Append(CultureInfo.InvariantCulture, $"# HELP arena_line_people People with a request running now.\n# TYPE arena_line_people gauge\narena_line_people {running.Select(e => e.Person).Distinct().Count()}\n");
        sb.Append("# HELP arena_line_refused_total Requests the line refused: busy (the line was full) or too_long (waited past the limit).\n# TYPE arena_line_refused_total counter\n");
        foreach (var ((reason, kind), n) in refused)
        {
            sb.Append(CultureInfo.InvariantCulture, $"arena_line_refused_total{{reason=\"{reason}\",kind=\"{kind}\"}} {n}\n");
        }
        sb.Append("# HELP arena_line_waited_seconds Time requests waited for their turn.\n# TYPE arena_line_waited_seconds summary\n");
        foreach (var (kind, (count, seconds)) in waited)
        {
            sb.Append(CultureInfo.InvariantCulture, $"arena_line_waited_seconds_sum{{kind=\"{kind}\"}} {seconds.ToString("0.###", CultureInfo.InvariantCulture)}\narena_line_waited_seconds_count{{kind=\"{kind}\"}} {count}\n");
        }
        return sb.ToString();
    }
}
