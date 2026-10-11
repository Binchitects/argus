using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace CodeArena;

/// <summary>Why a framed stream ended early: a header that is not one, a message past the cap, one cut off, or one that is not a JSON object.</summary>
internal sealed class FramingException(string message) : IOException(message);

/// <summary>
/// The messages of the Debug Adapter Protocol and the Language Server Protocol, read off a stream: each is a few
/// headers (<c>Content-Length: N</c>, the others passed over, the name in any case), a blank line, then N bytes of
/// JSON. The headers may come over several reads, and several messages in one; the length counts bytes, not
/// characters. A message larger than the caller's cap, a header that cannot be read, a stream cut in the middle of a
/// message, or a body that is not a JSON object ends the stream with a <see cref="FramingException"/>: nothing after
/// it could be trusted to start where a message does. Text that is not UTF-8, or half of a surrogate pair, is read as
/// U+FFFD.
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
                node = JsonNode.Parse(Repair(body));
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

    /// <summary>
    /// A body as it can be read as text: bytes that are not UTF-8 become U+FFFD, and so does an escaped half of a
    /// surrogate pair. Python's json writes a byte it could not decode as one ("\udcff"; debugpy passes a program's
    /// output so), and .NET refuses to read such a string: the first one would end the stream. Changed in place
    /// when it is UTF-8.
    /// </summary>
    public static byte[] Repair(byte[] body)
    {
        if (!Utf8.IsValid(body))
        {
            body = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(body));
        }
        var i = 0;
        while (i < body.Length && body.AsSpan(i).IndexOf((byte)'\\') is var next and >= 0)
        {
            i += next;
            if (i + 6 > body.Length || body[i + 1] != 'u' || Unit(body, i + 2) is not { } unit)
            {
                // \", \\, \n and the like (or what the parser refuses).
                i += 2;
                continue;
            }
            if (unit is >= 0xD800 and <= 0xDBFF && i + 12 <= body.Length && body[i + 6] == '\\' && body[i + 7] == 'u' && Unit(body, i + 8) is >= 0xDC00 and <= 0xDFFF)
            {
                i += 12;
                continue;
            }
            if (unit is >= 0xD800 and <= 0xDFFF)
            {
                // As long as what it replaces: nothing moves.
                "\\uFFFD"u8.CopyTo(body.AsSpan(i));
            }
            i += 6;
        }
        return body;
    }

    /// <summary>The four hex digits of a \u escape that start here.</summary>
    private static int? Unit(byte[] body, int at) =>
        int.TryParse(body.AsSpan(at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unit) ? unit : null;

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
/// however many callers write at once. Only the wait for a message's turn can be cancelled: one cut off half way
/// would leave the reader at the other end in the middle of it, and every message after it unread.
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

    public Task WriteAsync(JsonNode message, CancellationToken ct) => WriteAsync(() => message, ct);

    /// <summary>
    /// Writes the message <paramref name="next"/> makes once it is this caller's turn; false when it makes none.
    /// Messages numbered in it reach the stream in the order they were numbered.
    /// </summary>
    public async Task<bool> WriteAsync(Func<JsonNode?> next, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (next() is not { } message)
            {
                return false;
            }
            await stream.WriteAsync(Encode(message), CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
