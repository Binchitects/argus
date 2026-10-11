using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeArena;

/// <summary>Why a framed stream ended early: a header that is not one, a message past the cap, one cut off, or one that is not a JSON object.</summary>
internal sealed class FramingException(string message) : IOException(message);

/// <summary>
/// The messages of the Debug Adapter Protocol and the Language Server Protocol, read off a stream: each is a few
/// headers (<c>Content-Length: N</c>, the others passed over, the name in any case), a blank line, then N bytes of
/// JSON. The headers may come over several reads, and several messages in one; the length counts bytes, not
/// characters. A message larger than the caller's cap, a header that cannot be read, a stream cut in the middle of a
/// message, or a body that is not a JSON object ends the stream with a <see cref="FramingException"/>: nothing after
/// it could be trusted to start where a message does.
/// </summary>
internal sealed class FrameReader(Stream stream, int maxBytes)
{
    /// <summary>The most the headers of one message take.</summary>
    public const int MaxHeader = 8 * 1024;

    private static readonly byte[] EndOfHeader = "\r\n\r\n"u8.ToArray();

    // Read but not yet taken: the bytes from _start to _end. Never more than one message's headers and what came with them.
    private readonly byte[] _buffer = new byte[2 * MaxHeader];
    private int _start;
    private int _end;
    private bool _ended;

    /// <summary>The next message; null once the stream has ended between two (or after one that ended it).</summary>
    public async Task<JsonObject?> ReadAsync(CancellationToken ct)
    {
        if (_ended)
        {
            return null;
        }
        try
        {
            if (await ReadLengthAsync(ct) is not { } length)
            {
                _ended = true;
                return null;
            }
            var body = new byte[length];
            var kept = Math.Min(length, _end - _start);
            _buffer.AsSpan(_start, kept).CopyTo(body);
            _start += kept;
            try
            {
                await stream.ReadExactlyAsync(body.AsMemory(kept), ct);
            }
            catch (EndOfStreamException)
            {
                throw new FramingException("The stream ended in the middle of a message.");
            }
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(body);
            }
            catch (JsonException e)
            {
                throw new FramingException($"A message is not JSON: {e.Message}");
            }
            return node as JsonObject ?? throw new FramingException("A message is not a JSON object.");
        }
        catch (FramingException)
        {
            _ended = true;
            throw;
        }
    }

    /// <summary>The next message's length from its headers; null when the stream ended before one began.</summary>
    private async Task<int?> ReadLengthAsync(CancellationToken ct)
    {
        while (true)
        {
            var at = _buffer.AsSpan(_start, _end - _start).IndexOf(EndOfHeader);
            if (at >= 0)
            {
                var header = Encoding.ASCII.GetString(_buffer, _start, at);
                _start += at + EndOfHeader.Length;
                return Length(header);
            }
            if (_end - _start > MaxHeader)
            {
                throw new FramingException($"A message's headers run past {MaxHeader / 1024} KB.");
            }
            if (_start > 0)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                _end -= _start;
                _start = 0;
            }
            var n = await stream.ReadAsync(_buffer.AsMemory(_end), ct);
            if (n == 0)
            {
                return _end == 0 ? null : throw new FramingException("The stream ended in the middle of a message's headers.");
            }
            _end += n;
        }
    }

    private int Length(string header)
    {
        long? length = null;
        foreach (var line in header.Split("\r\n"))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new FramingException($"Not a header: {Fmt.OneLine(line, 80)}");
            }
            if (!line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!long.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                throw new FramingException($"Not a length: {Fmt.OneLine(line, 80)}");
            }
            length = n;
        }
        if (length is not { } bytes)
        {
            throw new FramingException("A message has no Content-Length header.");
        }
        return bytes <= maxBytes ? (int)bytes : throw new FramingException($"A message of {bytes:N0} bytes is larger than the {maxBytes:N0} taken.");
    }
}

/// <summary>
/// The other way: each message written whole with its Content-Length header (its UTF-8 bytes), one at a time
/// however many callers write at once.
/// </summary>
internal sealed class FrameWriter(Stream stream) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A message as it goes on the stream: its header, then its JSON.</summary>
    public static byte[] Encode(JsonNode message)
    {
        var body = Encoding.UTF8.GetBytes(Json.Line(message));
        var header = Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"Content-Length: {body.Length}\r\n\r\n"));
        return [.. header, .. body];
    }

    public async Task WriteAsync(JsonNode message, CancellationToken ct)
    {
        var bytes = Encode(message);
        await _gate.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
