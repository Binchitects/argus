using System.Runtime.CompilerServices;
using System.Text;

namespace CodeArena;

/// <summary>One server-sent event: its name (default "message") and its data lines joined.</summary>
internal sealed record SseEvent(string Event, string Data, string? Id);

/// <summary>Server-sent events, as the gateway streams answers and MCP servers stream results.</summary>
internal static class Sse
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        string? name = null;
        string? id = null;
        var any = false;
        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null || line.Length == 0)
            {
                if (any)
                {
                    yield return new SseEvent(name ?? "message", data.ToString(), id);
                }
                if (line is null)
                {
                    yield break;
                }
                data.Clear();
                name = null;
                any = false;
                continue;
            }
            if (line[0] == ':')
            {
                continue; // a comment: keep-alive
            }
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }
            switch (field)
            {
                case "data":
                    if (any)
                    {
                        data.Append('\n');
                    }
                    data.Append(value);
                    any = true;
                    break;
                case "event":
                    name = value;
                    break;
                case "id":
                    id = value;
                    break;
            }
        }
    }
}
