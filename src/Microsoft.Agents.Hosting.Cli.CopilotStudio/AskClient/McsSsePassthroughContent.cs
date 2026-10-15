using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

internal sealed class McsSsePassthroughContent : HttpContent
{
    private readonly HttpContent _source;
    private readonly Action<Exception>? _responseError;

    public McsSsePassthroughContent(HttpContent source, Action<Exception>? responseError = null)
    {
        _source = source;
        _responseError = responseError;
        foreach (var header in source.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        CopySourceToAsync(stream);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override async Task<Stream> CreateContentReadStreamAsync() =>
        new McsSseSanitizingStream(
            await _source.ReadAsStreamAsync().ConfigureAwait(false),
            _responseError);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task CopySourceToAsync(Stream destination)
    {
        await using var stream = await CreateContentReadStreamAsync().ConfigureAwait(false);
        await stream.CopyToAsync(destination).ConfigureAwait(false);
    }
}

internal sealed class McsSseSanitizingStream : Stream
{
    private readonly Stream _source;
    private readonly byte[] _sourceBuffer = new byte[4096];
    private readonly List<byte> _line = [];
    private readonly List<byte> _frame = [];
    private readonly List<byte> _prefix = [];
    private readonly Action<Exception>? _responseError;
    private byte[] _pending = [];
    private int _pendingOffset;
    private bool _activityFrame;
    private bool _completed;
    private bool _endChecked;
    private bool _ignoreLf;
    private bool _prefixChecked;

    public McsSseSanitizingStream(Stream source, Action<Exception>? responseError = null)
    {
        _source = source;
        _responseError = responseError;
    }

    public override bool CanRead => _source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => throw new NotSupportedException();
    public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        ReadCoreAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    private async ValueTask<int> ReadCoreAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        if (_completed && _pendingOffset >= _pending.Length)
        {
            FinishAtEndOfStream();
            return 0;
        }

        while (_pendingOffset >= _pending.Length && !_completed)
        {
            int read;
            try
            {
                read = await _source.ReadAsync(_sourceBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw CreateReadException();
            }
            catch (HttpRequestException)
            {
                throw CreateReadException();
            }

            if (read == 0)
            {
                _completed = true;
                if (_pendingOffset >= _pending.Length)
                {
                    FinishAtEndOfStream();
                }

                break;
            }

            ProcessBytes(_sourceBuffer.AsSpan(0, read));
        }

        if (_pendingOffset >= _pending.Length)
        {
            return 0;
        }

        var available = _pending.Length - _pendingOffset;
        var frameEnd = FindFrameEnd(_pending.AsSpan(_pendingOffset));
        if (frameEnd > 0)
        {
            available = Math.Min(available, frameEnd);
        }

        var count = Math.Min(buffer.Length, available);
        _pending.AsSpan(_pendingOffset, count).CopyTo(buffer.Span);
        _pendingOffset += count;
        return count;
    }

    private void ProcessBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (!_prefixChecked)
            {
                _prefix.Add(value);
                if (_prefix.Count < 3)
                {
                    continue;
                }

                _prefixChecked = true;
                if (_prefix[0] == 0xEF && _prefix[1] == 0xBB && _prefix[2] == 0xBF)
                {
                    _prefix.Clear();
                    continue;
                }

                foreach (var prefixValue in _prefix)
                {
                    ProcessByte(prefixValue);
                }

                _prefix.Clear();
            }
            else
            {
                ProcessByte(value);
            }
        }
    }

    private void ProcessByte(byte value)
    {
        if (value == (byte)'\r')
        {
            ProcessLine();
            _line.Clear();
            _ignoreLf = true;
            return;
        }

        if (value == (byte)'\n')
        {
            if (_ignoreLf)
            {
                _ignoreLf = false;
                return;
            }

            ProcessLine();
            _line.Clear();
            return;
        }

        _ignoreLf = false;
        _line.Add(value);
    }

    private void ProcessLine()
    {
        var length = _line.Count;

        if (length == 0)
        {
            if (_activityFrame)
            {
                QueueOutput(CollectionsMarshal.AsSpan(_frame));
                QueueOutput("\r\n"u8);
            }

            _frame.Clear();
            _activityFrame = false;
            return;
        }

        if (_line[0] == (byte)':')
        {
            return;
        }

        _frame.AddRange(CollectionsMarshal.AsSpan(_line)[..length].ToArray());
        _frame.Add((byte)'\r');
        _frame.Add((byte)'\n');

        var separator = _line.IndexOf((byte)':');
        if (separator == "event".Length &&
            Encoding.UTF8.GetString(CollectionsMarshal.AsSpan(_line)[..separator]) == "event")
        {
            var valueStart = separator + 1;
            if (valueStart < length && _line[valueStart] == (byte)' ')
            {
                valueStart++;
            }

            _activityFrame = Encoding.UTF8.GetString(
                CollectionsMarshal.AsSpan(_line)[valueStart..length]) == "activity";
        }
    }

    private void FinishAtEndOfStream()
    {
        if (_endChecked)
        {
            return;
        }

        _endChecked = true;
        if (!_prefixChecked)
        {
            foreach (var prefixValue in _prefix)
            {
                ProcessByte(prefixValue);
            }

            _prefix.Clear();
            _prefixChecked = true;
        }

        var partialLineIsComment = _line.Count > 0 && _line[0] == (byte)':';
        if (_frame.Count > 0 || (_line.Count > 0 && !partialLineIsComment))
        {
            _responseError?.Invoke(new McsLiveTestProtocolException(
                "The service returned a truncated SSE response."));
        }
    }

    private void QueueOutput(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        if (_pendingOffset >= _pending.Length)
        {
            _pending = bytes.ToArray();
            _pendingOffset = 0;
            return;
        }

        var remaining = _pending.AsSpan(_pendingOffset).ToArray();
        _pending = new byte[remaining.Length + bytes.Length];
        remaining.CopyTo(_pending);
        bytes.CopyTo(_pending.AsSpan(remaining.Length));
        _pendingOffset = 0;
    }

    private static int FindFrameEnd(ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index + 3 < bytes.Length; index++)
        {
            if (bytes[index] == (byte)'\r' &&
                bytes[index + 1] == (byte)'\n' &&
                bytes[index + 2] == (byte)'\r' &&
                bytes[index + 3] == (byte)'\n')
            {
                return index + 4;
            }
        }

        return 0;
    }

    private static McsLiveTestTransportException CreateReadException() =>
        new(null, "The Copilot Studio SSE response stream failed.");
}
