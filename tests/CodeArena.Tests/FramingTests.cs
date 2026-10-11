using System.Text;
using System.Text.Json.Nodes;

namespace CodeArena.Tests;

/// <summary>Content-Length framing as debug adapters and language servers write it: read however the bytes arrive, and refused when it cannot be trusted.</summary>
public sealed class FramingTests
{
    private const int Cap = 32 * 1024 * 1024;

    private static byte[] Frame(string json, string? header = null) =>
        Encoding.UTF8.GetBytes((header ?? $"Content-Length: {Encoding.UTF8.GetByteCount(json)}") + "\r\n\r\n" + json);

    [Fact]
    public async Task Headers_split_across_reads_and_two_messages_in_one_read_come_out_whole()
    {
        var one = Frame("""{"seq":1,"type":"request","command":"initialize"}""");
        var two = Frame("""{"seq":2,"type":"event","event":"initialized"}""");
        var three = Frame("""{"seq":3,"type":"response"}""");
        // The first header cut in three, then the end of the first message with all of the second in one read.
        var reader = new FrameReader(new Chunks(one[..5], one[5..17], one[17..30], [.. one[30..], .. two], three), Cap);

        Assert.Equal(1, (await reader.ReadAsync(default))!["seq"]!.GetValue<int>());
        Assert.Equal("initialized", (await reader.ReadAsync(default))!["event"]!.GetValue<string>());
        Assert.Equal(3, (await reader.ReadAsync(default))!["seq"]!.GetValue<int>());
        Assert.Null(await reader.ReadAsync(default));
        Assert.Null(await reader.ReadAsync(default));
    }

    [Fact]
    public async Task The_header_is_matched_in_any_case_and_other_headers_are_passed_over()
    {
        var json = """{"seq":7}""";
        var reader = new FrameReader(new Chunks(
            Frame(json, $"content-length: {json.Length}"),
            Frame(json, $"Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nCONTENT-LENGTH:{json.Length}\r\nX-Other: 1")), Cap);
        Assert.Equal(7, (await reader.ReadAsync(default))!["seq"]!.GetValue<int>());
        Assert.Equal(7, (await reader.ReadAsync(default))!["seq"]!.GetValue<int>());
    }

    [Fact]
    public async Task The_length_counts_bytes_so_multi_byte_text_is_read_whole_even_cut_in_a_character()
    {
        var text = "héllo — 日本語 🐛";
        var json = new JsonObject { ["output"] = text }.ToJsonString(CodeArena.Json.Relaxed);
        var bytes = Frame(json);
        Assert.NotEqual(json.Length, Encoding.UTF8.GetByteCount(json));
        // Cut inside the bug's four bytes.
        var cut = bytes.Length - 6;
        var reader = new FrameReader(new Chunks(bytes[..cut], bytes[cut..], Frame("""{"seq":2}""")), Cap);
        Assert.Equal(text, (await reader.ReadAsync(default))!["output"]!.GetValue<string>());
        Assert.Equal(2, (await reader.ReadAsync(default))!["seq"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_message_past_the_cap_ends_the_stream_before_its_body_is_read()
    {
        var reader = new FrameReader(new Chunks(Frame(new string(' ', 90) + "{}"), Frame("{}")), 64);
        var e = await Assert.ThrowsAsync<FramingException>(() => reader.ReadAsync(default));
        Assert.Contains("larger than", e.Message);
        Assert.Null(await reader.ReadAsync(default));

        // A length no int holds is past any cap too.
        var huge = new FrameReader(new Chunks(Frame("{}", "Content-Length: 99999999999999")), Cap);
        Assert.Contains("larger than", (await Assert.ThrowsAsync<FramingException>(() => huge.ReadAsync(default))).Message);
    }

    [Fact]
    public async Task A_stream_that_ends_in_the_middle_of_a_message_is_an_error_and_between_two_is_the_end()
    {
        var whole = Frame("""{"seq":1,"body":"0123456789"}""");
        var cutBody = new FrameReader(new Chunks(whole[..^5]), Cap);
        Assert.Contains("middle of a message", (await Assert.ThrowsAsync<FramingException>(() => cutBody.ReadAsync(default))).Message);

        var cutHeader = new FrameReader(new Chunks("Content-Length: 1"u8.ToArray()), Cap);
        Assert.Contains("headers", (await Assert.ThrowsAsync<FramingException>(() => cutHeader.ReadAsync(default))).Message);

        Assert.Null(await new FrameReader(new Chunks(), Cap).ReadAsync(default));
    }

    [Theory]
    [InlineData("Content-Length: 2", "{x")]
    [InlineData("Content-Length: 3", "[1]")]
    [InlineData("Content-Length: 2", "\"\"")]
    [InlineData("Content-Length: ten", "{}")]
    [InlineData("Content-Length: -2", "{}")]
    [InlineData("Content-Type: text/plain", "{}")]
    [InlineData("no colon here", "{}")]
    public async Task A_bad_header_or_a_body_that_is_not_a_JSON_object_ends_the_stream(string header, string body)
    {
        var reader = new FrameReader(new Chunks(Frame(body, header), Frame("""{"seq":2}""")), Cap);
        await Assert.ThrowsAsync<FramingException>(() => reader.ReadAsync(default));
        Assert.Null(await reader.ReadAsync(default));
    }

    [Fact]
    public async Task Headers_that_never_end_are_refused()
    {
        var endless = Encoding.ASCII.GetBytes("X-Padding: " + new string('a', FrameReader.MaxHeader + 10));
        var reader = new FrameReader(new Chunks(endless, Frame("{}")), Cap);
        Assert.Contains("headers", (await Assert.ThrowsAsync<FramingException>(() => reader.ReadAsync(default))).Message);
    }

    [Fact]
    public async Task Text_that_is_not_UTF_8_and_half_of_a_surrogate_pair_are_read_as_a_replacement_character()
    {
        // As Python's json writes what it could not decode (debugpy, a program's output), and a byte that is not UTF-8.
        var json = Encoding.ASCII.GetBytes("""{"low":"\udc80","high":"\ud800x","end":"a\ud83d","pair":"PAIR","text":"\\udc80","raw":"x?y"}""".Replace("PAIR", "\\" + "ud83d" + "\\" + "udc1b", StringComparison.Ordinal));
        json[Array.LastIndexOf(json, (byte)'?')] = 0xFF;
        var frame = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n").Concat(json).ToArray();
        var message = (await new FrameReader(new Chunks(frame, Frame("""{"seq":2}""")), Cap).ReadAsync(default))!;

        Assert.Equal("�", message["low"]!.GetValue<string>());
        Assert.Equal("�x", message["high"]!.GetValue<string>());
        Assert.Equal("a�", message["end"]!.GetValue<string>());
        Assert.Equal("🐛", message["pair"]!.GetValue<string>());
        // An escaped backslash before a u is text, not an escape.
        Assert.Equal("\\udc80", message["text"]!.GetValue<string>());
        Assert.Equal("x�y", message["raw"]!.GetValue<string>());
        // It can be passed on.
        Assert.Equal(message.ToJsonString(), JsonNode.Parse(CodeArena.Json.Line(message))!.ToJsonString());
    }

    [Fact]
    public async Task A_message_is_written_whole_even_when_its_writer_gives_up_and_one_still_waiting_is_not_written()
    {
        using var slow = new SlowStream();
        using var writer = new FrameWriter(slow);
        using var giveUp = new CancellationTokenSource();
        var first = writer.WriteAsync(new JsonObject { ["seq"] = 1 }, giveUp.Token);
        var second = writer.WriteAsync(new JsonObject { ["seq"] = 2 }, giveUp.Token);
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await giveUp.CancelAsync();

        await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal("Content-Length: 9\r\n\r\n{\"seq\":1}", Encoding.UTF8.GetString(slow.Written.ToArray()));
    }

    [Fact]
    public async Task Messages_written_at_once_from_many_callers_come_out_whole_one_after_another()
    {
        using var pipe = new MemoryStream();
        using (var writer = new FrameWriter(pipe))
        {
            await Task.WhenAll(Enumerable.Range(1, 200).Select(i => Task.Run(() => writer.WriteAsync(new JsonObject { ["seq"] = i, ["text"] = new string('é', i * 7) }, default))));
        }
        pipe.Position = 0;
        var reader = new FrameReader(pipe, Cap);
        var seen = new List<int>();
        while (await reader.ReadAsync(default) is { } message)
        {
            Assert.Equal(message["seq"]!.GetValue<int>() * 7, message["text"]!.GetValue<string>().Length);
            seen.Add(message["seq"]!.GetValue<int>());
        }
        Assert.Equal(Enumerable.Range(1, 200), seen.Order());
        Assert.Equal("Content-Length: 9\r\n\r\n{\"seq\":1}", Encoding.UTF8.GetString(FrameWriter.Encode(new JsonObject { ["seq"] = 1 })));
    }

    /// <summary>A stream that writes half of what it is given, waits (as long as the write may), then the rest.</summary>
    private sealed class SlowStream : Stream
    {
        public MemoryStream Written { get; } = new();

        /// <summary>Done when the first write has begun.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            var half = buffer.Length / 2;
            Written.Write(buffer.Span[..half]);
            Started.TrySetResult();
            await Task.Delay(300, ct);
            Written.Write(buffer.Span[half..]);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Written.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>A stream whose reads return these chunks, one per read, then the end.</summary>
    private sealed class Chunks(params byte[][] chunks) : Stream
    {
        private readonly Queue<byte[]> _chunks = new(chunks);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (!_chunks.TryPeek(out var chunk))
            {
                return 0;
            }
            var n = Math.Min(chunk.Length, buffer.Length);
            chunk.AsSpan(0, n).CopyTo(buffer);
            _chunks.Dequeue();
            if (n < chunk.Length)
            {
                // What did not fit comes with the next read.
                var rest = new Queue<byte[]>([chunk[n..], .. _chunks]);
                _chunks.Clear();
                foreach (var c in rest)
                {
                    _chunks.Enqueue(c);
                }
            }
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
