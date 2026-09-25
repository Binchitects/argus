using System.Text.Json.Nodes;
using Argus.Util;

namespace Argus.Access;

/// <summary>
/// The audit trail as a log stream: one JSON line per tool call or refusal
/// (argus/auditlog.py). The audit table stays the record; this puts the same
/// fact on stdout so Promtail/Loki can chart and search it. ARGUS_AUDIT_LOG=0
/// turns the stream off.
/// </summary>
public static class AuditLog
{
    const int ArgMax = 300;
    static readonly Lock Gate = new();

    /// <summary>Tests capture the stream here.</summary>
    public static TextWriter? Writer { get; set; }

    static bool Enabled() =>
        !new[] { "0", "false", "no", "off" }.Contains((Environment.GetEnvironmentVariable("ARGUS_AUDIT_LOG") ?? "1").Trim().ToLowerInvariant());

    static JsonNode? Clip(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue v when v.TryGetValue<string>(out var s) && PyStr.Len(s) > ArgMax:
                return JsonValue.Create(PyStr.Prefix(s, ArgMax) + "...");
            case JsonArray a:
            {
                var output = new JsonArray();
                foreach (var item in a.Take(20)) output.Add(Clip(item?.DeepClone()));
                return output;
            }
            case JsonObject o:
            {
                var output = new JsonObject();
                foreach (var (k, v) in o.Take(20)) output[k] = Clip(v?.DeepClone());
                return output;
            }
            default:
                return value?.DeepClone();
        }
    }

    static void Emit(JsonObject fields)
    {
        if (!Enabled()) return;
        var line = new JsonObject { ["ts"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") };
        foreach (var (k, v) in fields) line[k] = v?.DeepClone();
        try
        {
            var text = PyJson.Dumps(line, ensureAscii: false);
            lock (Gate)
            {
                var w = Writer ?? Console.Out;
                w.Write(text + "\n");
                w.Flush();
            }
        }
        catch (Exception)
        {
            // The log stream must never break the call it describes.
        }
    }

    public static void ToolCall(string tool, string? user, long? userId, JsonObject args, int? reposVisible, double durationMs, Exception? error)
    {
        var outcome = error is null ? "ok" : error.GetType().Name == "AccessNotice" ? "no_access" : "error";
        Emit(new JsonObject
        {
            ["event"] = "tool_call",
            ["tool"] = tool,
            ["user"] = user,
            ["user_id"] = userId,
            ["outcome"] = outcome,
            ["error"] = error?.GetType().Name,
            ["duration_ms"] = Math.Round(durationMs, 1, MidpointRounding.ToEven),
            ["repos_visible"] = reposVisible,
            ["args"] = Clip(args),
        });
    }

    public static void Denied(string reason, string path, string? detail = null) =>
        Emit(new JsonObject { ["event"] = "denied", ["reason"] = reason, ["path"] = path, ["detail"] = detail });

    public static void IndexStart(IReadOnlyList<string> branches, bool allowPartial, int repos) =>
        Emit(new JsonObject
        {
            ["event"] = "index_start", ["repos"] = repos,
            ["branches"] = new JsonArray(branches.Select(b => (JsonNode?)b).ToArray()),
            ["allow_partial"] = allowPartial,
        });

    public static void IndexRepo(string repo, string branch, string outcome, double? durationMs = null, int? indexed = null,
        int? deleted = null, int? skipped = null, int? errors = null, bool? timedOut = null, bool? symbolsFailed = null, string? error = null) =>
        Emit(new JsonObject
        {
            ["event"] = "index_repo", ["repo"] = repo, ["branch"] = branch, ["outcome"] = outcome,
            ["duration_ms"] = durationMs, ["indexed"] = indexed, ["deleted"] = deleted, ["skipped"] = skipped,
            ["errors"] = errors, ["timed_out"] = timedOut, ["symbols_failed"] = symbolsFailed, ["error"] = error,
        });

    public static void IndexEnd(int returncode, double durationMs, int repos, int failed, int upToDate, string? reason = null,
        int empty = 0, int embedded = 0) =>
        Emit(new JsonObject
        {
            ["event"] = "index_end", ["outcome"] = returncode == 0 ? "ok" : "error", ["returncode"] = returncode,
            ["duration_ms"] = durationMs, ["repos"] = repos, ["failed"] = failed, ["up_to_date"] = upToDate,
            ["empty"] = empty, ["embedded"] = embedded, ["reason"] = reason,
        });

    public static void IndexWebhook(string repo, bool started = false, int queued = 0, int collapsed = 0)
    {
        var fields = new JsonObject { ["event"] = "index_webhook", ["repo"] = repo };
        if (started) fields["outcome"] = "started";
        else if (collapsed != 0) { fields["outcome"] = "collapsed"; fields["collapsed_from"] = collapsed; }
        else { fields["outcome"] = "queued"; fields["queued"] = queued; }
        Emit(fields);
    }

    public static void IndexScheduled(int interval, int? firstPassIn = null, string? reason = null, string? skipped = null)
    {
        var fields = new JsonObject { ["event"] = "index_scheduled", ["interval"] = interval };
        if (firstPassIn is not null)
        {
            fields["first_pass_in"] = firstPassIn;
            fields["reason"] = reason ?? "the index is current";
        }
        if (skipped is not null)
        {
            fields["skipped"] = skipped;
            fields["outcome"] = "skipped";
        }
        Emit(fields);
    }
}
