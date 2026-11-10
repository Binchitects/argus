using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Llm.Api.Gateway;

/// <summary>
/// The app's own gateway for Prometheus (the app's /metrics, job "app"): its requests by model, deployment, surface and
/// how they ended; their time and their first token's (histograms by model); tokens by kind; what each deployment runs at
/// once; upstream errors, refusals and fallbacks; and the ledger's lines not yet written.
/// </summary>
public sealed class GatewayMetrics
{
    private static readonly double[] Buckets = [0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 900];
    private readonly ConcurrentDictionary<(string Model, string Deployment, string Surface, string Status), long> _requests = new();
    private readonly ConcurrentDictionary<(string Model, string Type), long> _tokens = new();
    private readonly ConcurrentDictionary<(string Deployment, string Reason), long> _errors = new();
    private readonly ConcurrentDictionary<string, long> _refused = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Model, string Fallback), long> _fallbacks = new();
    private readonly ConcurrentDictionary<string, Histogram> _seconds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Histogram> _ttft = new(StringComparer.Ordinal);

    private sealed class Histogram
    {
        public readonly long[] Counts = new long[Buckets.Length];
        public long Count;
        public double Sum;
        private readonly Lock _lock = new();

        public void Observe(double v)
        {
            lock (_lock)
            {
                for (var i = 0; i < Buckets.Length; i++)
                {
                    if (v <= Buckets[i])
                    {
                        Counts[i]++;
                    }
                }
                Count++;
                Sum += v;
            }
        }
    }

    /// <summary>A request's line in the ledger, counted as it is written.</summary>
    public void Booked(Llm.Core.Gateway.GatewayRequest r)
    {
        var deployment = r.Deployment ?? "";
        if (r.Status == Llm.Core.Gateway.GatewayRequest.Refused)
        {
            _refused.AddOrUpdate(r.Refusal ?? "other", 1, (_, n) => n + 1);
            return;
        }
        _requests.AddOrUpdate((r.Model, deployment, r.Surface, r.Status), 1, (_, n) => n + 1);
        _seconds.GetOrAdd(r.Model, _ => new Histogram()).Observe(r.DurationMs / 1000.0);
        if (r.FirstTokenAt is { } first)
        {
            _ttft.GetOrAdd(r.Model, _ => new Histogram()).Observe(Math.Max(0, (first - r.StartedAt).TotalSeconds));
        }
        _tokens.AddOrUpdate((r.Model, "prompt"), Math.Max(0, r.PromptTokens - r.CachedTokens), (_, n) => n + Math.Max(0, r.PromptTokens - r.CachedTokens));
        _tokens.AddOrUpdate((r.Model, "cached"), r.CachedTokens, (_, n) => n + r.CachedTokens);
        _tokens.AddOrUpdate((r.Model, "completion"), r.CompletionTokens, (_, n) => n + r.CompletionTokens);
        if (r.FallbackFrom is { } from)
        {
            _fallbacks.AddOrUpdate((from, r.Model), 1, (_, n) => n + 1);
        }
        if (r.Status == Llm.Core.Gateway.GatewayRequest.Failed)
        {
            var reason = r.HttpStatus switch { 502 => "unreachable", >= 500 => "server_error", 404 => "not_found", >= 400 => "refused", _ => "stream_error" };
            _errors.AddOrUpdate((deployment, reason), 1, (_, n) => n + 1);
        }
    }

    private static string L(string v) => v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    public string Render(IReadOnlyDictionary<string, int> inflight, int pending)
    {
        var sb = new StringBuilder();
        void Head(string name, string type, string help) => sb.Append(CultureInfo.InvariantCulture, $"# HELP {name} {help}\n# TYPE {name} {type}\n");
        Head("arena_gateway_requests_total", "counter", "Requests the app's own gateway sent, by how they ended.");
        foreach (var ((model, deployment, surface, status), n) in _requests)
        {
            sb.Append(CultureInfo.InvariantCulture, $"arena_gateway_requests_total{{model=\"{L(model)}\",deployment=\"{L(deployment)}\",surface=\"{L(surface)}\",status=\"{status}\"}} {n}\n");
        }
        void Histograms(string name, string help, ConcurrentDictionary<string, Histogram> all)
        {
            Head(name, "histogram", help);
            foreach (var (model, h) in all)
            {
                for (var i = 0; i < Buckets.Length; i++)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{name}_bucket{{model=\"{L(model)}\",le=\"{Buckets[i].ToString(CultureInfo.InvariantCulture)}\"}} {h.Counts[i]}\n");
                }
                sb.Append(CultureInfo.InvariantCulture, $"{name}_bucket{{model=\"{L(model)}\",le=\"+Inf\"}} {h.Count}\n{name}_sum{{model=\"{L(model)}\"}} {h.Sum.ToString("0.###", CultureInfo.InvariantCulture)}\n{name}_count{{model=\"{L(model)}\"}} {h.Count}\n");
            }
        }
        Histograms("arena_gateway_request_seconds", "A request's time, from its arrival to its end.", _seconds);
        Histograms("arena_gateway_ttft_seconds", "The time to a request's first words.", _ttft);
        Head("arena_gateway_tokens_total", "counter", "Tokens by kind: prompt (less what the cache read), cached, completion.");
        foreach (var ((model, type), n) in _tokens)
        {
            sb.Append(CultureInfo.InvariantCulture, $"arena_gateway_tokens_total{{model=\"{L(model)}\",type=\"{type}\"}} {n}\n");
        }
        Head("arena_gateway_inflight", "gauge", "Requests each deployment answers now.");
        foreach (var (deployment, n) in inflight)
        {
            sb.Append(CultureInfo.InvariantCulture, $"arena_gateway_inflight{{deployment=\"{L(deployment)}\"}} {n}\n");
        }
        Head("arena_gateway_upstream_errors_total", "counter", "Requests a model server failed, by why.");
        foreach (var ((deployment, reason), n) in _errors)
        {
            sb.Append(CultureInfo.InvariantCulture, $"arena_gateway_upstream_errors_total{{deployment=\"{L(deployment)}\",reason=\"{reason}\"}} {n}\n");
        }
        Head("arena_gateway_refused_total", "counter", "Requests refused before anything was sent, by why.");
        foreach (var (reason, n) in _refused)
        {
            sb.Append(CultureInfo.InvariantCulture, $"arena_gateway_refused_total{{reason=\"{L(reason)}\"}} {n}\n");
        }
        Head("arena_gateway_fallbacks_total", "counter", "Requests a fallback answered.");
        foreach (var ((model, fallback), n) in _fallbacks)
        {
            sb.Append(CultureInfo.InvariantCulture, $"arena_gateway_fallbacks_total{{model=\"{L(model)}\",fallback=\"{L(fallback)}\"}} {n}\n");
        }
        Head("arena_gateway_log_pending", "gauge", "Ledger lines not yet written.");
        sb.Append(CultureInfo.InvariantCulture, $"arena_gateway_log_pending {pending}\n");
        return sb.ToString();
    }
}
