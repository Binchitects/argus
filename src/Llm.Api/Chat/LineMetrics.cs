using System.Globalization;
using System.Text;
using Llm.Api.Access;

namespace Llm.Api.Chat;

/// <summary>
/// The answers' line for Prometheus (job "app", Admin → Dashboards): requests running and waiting by model and kind (chat,
/// api), the people with one running, what the line refused (busy, too long) and the time its requests waited. Served on
/// the app's own port to the stack's network only: Traefik routes no /metrics to the app, and a request that came through it
/// (X-Forwarded-For) is refused.
/// </summary>
public static class LineMetrics
{
    public static void MapLineMetrics(this IEndpointRouteBuilder app) =>
        app.MapGet("/metrics", (HttpContext http, AnswerGate gate) =>
        {
            // Through Traefik: X-Forwarded-For, which the forwarded-headers middleware takes in (keeping it as X-Original-For) and
            // whose client becomes the remote address. Only the stack's network (a private address, or none in tests) reads it.
            if (http.Request.Headers.ContainsKey("X-Forwarded-For") || http.Request.Headers.ContainsKey("X-Original-For")
                || http.Connection.RemoteIpAddress is { } from && !Internal(from))
            {
                return Results.NotFound();
            }
            return Results.Text(Render(gate), "text/plain; version=0.0.4");
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

    public static string Render(AnswerGate gate)
    {
        var (running, waiting) = gate.Snapshot();
        var (refused, waited) = gate.Counters();
        var sb = new StringBuilder();
        static string L(string v) => v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
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
