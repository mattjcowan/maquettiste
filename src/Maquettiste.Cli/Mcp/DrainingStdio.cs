using System.Text.Json;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// Makes the stdio server answer every request it has read before it sees the end of its input. The MCP stdio transport stops the
/// server as soon as stdin ends, so a client that writes a request and closes its end at once (<c>echo request | maquettiste mcp</c>)
/// used to get no answer. The pair wraps stdin and stdout, follows the newline-delimited JSON-RPC messages that pass through them
/// (a request read on stdin is pending until a response with its id is written on stdout, or the client cancels it), and holds
/// the end of stdin back until nothing is pending. The bytes themselves pass through unchanged.
/// </summary>
internal sealed class DrainingStdio
{
    /// <summary>How long the end of input waits for pending answers at most, so a request the server never answers cannot keep the process alive.</summary>
    public static readonly TimeSpan MaxDrain = TimeSpan.FromMinutes(15);

    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private TaskCompletionSource _idle = NewIdle(completed: true);

    /// <summary>Wraps a stdin/stdout pair.</summary>
    /// <param name="input">The server's input (the client's requests).</param>
    /// <param name="output">The server's output (the answers).</param>
    public DrainingStdio(Stream input, Stream output)
    {
        Input = new InputStream(input, this);
        Output = new OutputStream(output, this);
    }

    /// <summary>The input stream to give the transport.</summary>
    public Stream Input { get; }

    /// <summary>The output stream to give the transport.</summary>
    public Stream Output { get; }

    /// <summary>The ids of the requests read and not answered yet (for tests).</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _pending.Count;
        }
    }

    private static TaskCompletionSource NewIdle(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed)
            source.SetResult();
        return source;
    }

    /// <summary>Returns the key of a JSON-RPC id (a number or a string); <see langword="null"/> for anything else.</summary>
    private static string? IdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => "n:" + id.GetRawText(),
        JsonValueKind.String => "s:" + id.GetString(),
        _ => null,
    };

    private void Add(string key)
    {
        lock (_gate)
        {
            if (_pending.Add(key) && _pending.Count == 1)
                _idle = NewIdle(completed: false);
        }
    }

    private void Remove(string key)
    {
        lock (_gate)
        {
            if (_pending.Remove(key) && _pending.Count == 0)
                _idle.TrySetResult();
        }
    }

    private Task WhenIdle()
    {
        lock (_gate)
            return _idle.Task;
    }

    /// <summary>A line the client sent: a request is pending from now on; a cancellation notice ends the request it names.</summary>
    private void OnInputLine(ReadOnlySpan<byte> line)
    {
        if (Parse(line) is not { } message)
            return;
        using (message)
        {
            var root = message.RootElement;
            if (!root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
                return;
            if (root.TryGetProperty("id", out var id))
            {
                if (IdKey(id) is { } key)
                    Add(key);
            }
            else if (method.ValueEquals("notifications/cancelled")
                && root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("requestId", out var requestId) && IdKey(requestId) is { } cancelled)
            {
                Remove(cancelled);
            }
        }
    }

    /// <summary>A line the server wrote: a response (an id and no method) answers the request with that id.</summary>
    private void OnOutputLine(ReadOnlySpan<byte> line)
    {
        if (Parse(line) is not { } message)
            return;
        using (message)
        {
            var root = message.RootElement;
            if (!root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var id) && IdKey(id) is { } key)
                Remove(key);
        }
    }

    private static JsonDocument? Parse(ReadOnlySpan<byte> line)
    {
        var trimmed = line.TrimEnd((byte)'\r');
        if (trimmed.IsEmpty || trimmed[0] != (byte)'{')
            return null;
        try
        {
            var reader = new Utf8JsonReader(trimmed);
            return JsonDocument.TryParseValue(ref reader, out var document) ? document : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Splits a byte stream into lines, whatever the chunking.</summary>
    private sealed class LineSplitter(Action<ReadOnlySpan<byte>> onLine)
    {
        private readonly Lock _gate = new();
        private byte[] _buffer = new byte[4096];
        private int _length;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                while (!bytes.IsEmpty)
                {
                    var newline = bytes.IndexOf((byte)'\n');
                    var part = newline < 0 ? bytes : bytes[..newline];
                    Grow(part.Length);
                    part.CopyTo(_buffer.AsSpan(_length));
                    _length += part.Length;
                    if (newline < 0)
                        return;
                    onLine(_buffer.AsSpan(0, _length));
                    _length = 0;
                    bytes = bytes[(newline + 1)..];
                }
            }
        }

        /// <summary>Ends an unterminated last line; returns whether there was one.</summary>
        public bool End()
        {
            lock (_gate)
            {
                if (_length == 0)
                    return false;
                onLine(_buffer.AsSpan(0, _length));
                _length = 0;
                return true;
            }
        }

        private void Grow(int extra)
        {
            if (_length + extra <= _buffer.Length)
                return;
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + extra));
        }
    }

    /// <summary>Stdin: passes bytes through and, at the end, waits for the pending answers before it reports the end.</summary>
    private sealed class InputStream : Stream
    {
        private readonly Stream _inner;
        private readonly DrainingStdio _owner;
        private readonly LineSplitter _lines;

        public InputStream(Stream inner, DrainingStdio owner)
        {
            _inner = inner;
            _owner = owner;
            _lines = new LineSplitter(owner.OnInputLine);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            if (read > 0)
            {
                _lines.Append(buffer[..read]);
                return read;
            }

            if (_lines.End() && !buffer.IsEmpty)
            {
                buffer[0] = (byte)'\n';
                return 1;
            }

            _owner.WhenIdle().Wait(MaxDrain);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0)
            {
                _lines.Append(buffer.Span[..read]);
                return read;
            }

            // A last request without a newline (echo -n) is completed with one, or the transport would drop it.
            if (_lines.End() && !buffer.IsEmpty)
            {
                buffer.Span[0] = (byte)'\n';
                return 1;
            }

            try
            {
                await _owner.WhenIdle().WaitAsync(MaxDrain, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A request the server never answered: end anyway.
            }

            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stdout: passes bytes through, then marks the requests the written responses answer.</summary>
    private sealed class OutputStream : Stream
    {
        private readonly Stream _inner;
        private readonly LineSplitter _lines;

        public OutputStream(Stream inner, DrainingStdio owner)
        {
            _inner = inner;
            _lines = new LineSplitter(owner.OnOutputLine);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            _lines.Append(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _lines.Append(buffer.Span);
        }

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
