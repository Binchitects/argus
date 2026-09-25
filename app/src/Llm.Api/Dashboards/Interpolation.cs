using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Llm.Api.Dashboards;

/// <summary>A dashboard variable's chosen values: "$__all" for All.</summary>
public sealed record VariableValue(Variable Variable, IReadOnlyList<string> Values, IReadOnlyList<string> Options);

/// <summary>
/// Grafana's rules for Prometheus and Loki queries, as its backend applies them,
/// so a panel gives what Grafana gives: the step for a range and resolution,
/// the $__ macros, the dashboard's variables in a query, and series names from
/// a legend format.
/// </summary>
public static partial class Interpolation
{
    /// <summary>
    /// The step: the interval the page asked for (range / points, rounded as
    /// Grafana rounds it), never finer than the panel's or the datasource's minimum.
    /// </summary>
    public static TimeSpan Step(TimeSpan requested, TimeSpan minimum)
    {
        var step = requested > TimeSpan.Zero ? requested : TimeSpan.FromSeconds(15);
        return step < minimum ? minimum : step;
    }

    /// <summary>Grafana's $__rate_interval: at least four scrapes, and a step plus one scrape.</summary>
    public static TimeSpan RateInterval(TimeSpan step, TimeSpan scrape) =>
        step + scrape > scrape * 4 ? step + scrape : scrape * 4;

    /// <summary>
    /// Grafana's gtime.FormatInterval, as its backend writes $__interval: the largest
    /// unit the span reaches, whole (90 s is "1m").
    /// </summary>
    public static string Duration(TimeSpan span)
    {
        var ms = (long)span.TotalMilliseconds;
        foreach (var (unit, size) in new[] { ("y", 31_536_000_000L), ("d", 86_400_000L), ("h", 3_600_000L), ("m", 60_000L), ("s", 1000L), ("ms", 1L) })
        {
            if (ms >= size)
            {
                return (ms / size).ToString(CultureInfo.InvariantCulture) + unit;
            }
        }
        return "1ms";
    }

    /// <summary>Go's time.Duration.String(), as Grafana writes $__rate_interval: "1m15s", "4m0s", "1h0m0s".</summary>
    public static string GoDuration(TimeSpan span)
    {
        if (span < TimeSpan.FromSeconds(1))
        {
            return span.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture) + "ms";
        }
        var h = (long)span.TotalHours;
        var m = span.Minutes;
        var sec = (span - TimeSpan.FromHours(h) - TimeSpan.FromMinutes(m)).TotalSeconds.ToString("0.#########", CultureInfo.InvariantCulture) + "s";
        return h > 0 ? $"{h}h{m}m{sec}" : m > 0 ? $"{m}m{sec}" : sec;
    }

    /// <summary>A Grafana duration ("15s", "1m", "2h", "1d") or null.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        var m = DurationText().Match(text?.Trim() ?? "");
        if (!m.Success)
        {
            return null;
        }
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            "w" => TimeSpan.FromDays(n * 7),
            "y" => TimeSpan.FromDays(n * 365),
            _ => null,
        };
    }

    [GeneratedRegex(@"^>?\s*(\d+(?:\.\d+)?)(ms|s|m|h|d|w|y)$")]
    private static partial Regex DurationText();

    /// <summary>
    /// The $__ macros and the dashboard's variables in a Prometheus or Loki query.
    /// Names that are neither ($1 in label_replace) are left alone.
    /// </summary>
    public static string Expand(string query, DateTimeOffset from, DateTimeOffset to, TimeSpan step, TimeSpan scrape, IReadOnlyDictionary<string, VariableValue> vars, bool loki, bool instant = false)
    {
        var range = to - from;
        var rate = RateInterval(step, scrape);
        var rangeS = Math.Round(range.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var macros = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__interval"] = Duration(step),
            ["__interval_ms"] = ((long)step.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            ["__rate_interval"] = GoDuration(rate),
            ["__rate_interval_ms"] = ((long)rate.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            ["__range"] = rangeS + "s",
            ["__range_s"] = rangeS,
            ["__range_ms"] = ((long)range.TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            // Loki's: the step of a range query, the whole range of an instant one.
            ["__auto"] = instant ? rangeS + "s" : Duration(step),
        };
        return VariableRef().Replace(query, m =>
        {
            var name = m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["c"].Value;
            if (macros.TryGetValue(name, out var macro))
            {
                return macro;
            }
            return vars.TryGetValue(name, out var v) ? Format(v, loki) : m.Value;
        });
    }

    // $name, ${name} or ${name:format}, [[name]]
    [GeneratedRegex(@"\$\{(?<b>\w+)(?::\w+)?\}|\[\[(?<c>\w+)\]\]|\$(?<a>__\w+|[A-Za-z_]\w*)")]
    private static partial Regex VariableRef();

    /// <summary>
    /// As Grafana formats a variable for Prometheus and Loki: one value as it is
    /// (regex-escaped when the variable may hold several), several as (a|b), All as
    /// its all-value or every option. Always safe inside a double-quoted string.
    /// </summary>
    public static string Format(VariableValue v, bool loki)
    {
        var values = v.Values.Contains("$__all")
            ? v.Variable.AllValue is { Length: > 0 } all ? null : v.Options
            : v.Values;
        if (values is null)
        {
            return Quote(v.Variable.AllValue!);
        }
        var several = v.Variable.Multi || v.Variable.IncludeAll;
        if (values.Count == 1)
        {
            return Quote(several ? RegexEscape(values[0]) : values[0]);
        }
        return Quote("(" + string.Join('|', values.Select(RegexEscape)) + ")");
    }

    /// <summary>A value to go inside "…" in PromQL and LogQL: a quote or backslash must not end it.</summary>
    internal static string Quote(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    internal static string RegexEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if ("\\^$*+?.()|{}[]".Contains(c, StringComparison.Ordinal))
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>A series' name: the legend format with {{label}} filled in, or Grafana's default metric{a="b"}.</summary>
    public static string Legend(string? format, IReadOnlyDictionary<string, string> labels, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(format) && format != "__auto")
        {
            // A format that comes out empty (its labels missing) gives way to the default, as in Grafana.
            var named = LegendRef().Replace(format, m => labels.TryGetValue(m.Groups[1].Value, out var v) ? v : "");
            if (!string.IsNullOrWhiteSpace(named))
            {
                return named;
            }
        }
        var name = labels.GetValueOrDefault("__name__") ?? "";
        var rest = labels.Where(kv => kv.Key != "__name__").OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}=\"{kv.Value}\"").ToList();
        if (name.Length == 0 && rest.Count == 0)
        {
            return fallback;
        }
        return rest.Count == 0 ? name : $"{name}{{{string.Join(", ", rest)}}}";
    }

    [GeneratedRegex(@"\{\{\s*([\w.]+)\s*\}\}")]
    private static partial Regex LegendRef();
}
