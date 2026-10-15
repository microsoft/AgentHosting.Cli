using System.Net;
using System.Text;
using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

internal sealed class McsJsonActivityEnvelopeContent(
    HttpContent source,
    Action<string> serviceConversationId,
    Action<Exception> responseError) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        CopyTransformedToAsync(stream);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override async Task<Stream> CreateContentReadStreamAsync()
    {
        var reader = new McsJsonActivityReader(
            await source.ReadAsStreamAsync().ConfigureAwait(false),
            serviceConversationId);
        return new McsActivityEnvelopeStream(reader.ReadActivityAsync, responseError);
    }

    private async Task CopyTransformedToAsync(Stream destination)
    {
        await using var transformed = await CreateContentReadStreamAsync().ConfigureAwait(false);
        await transformed.CopyToAsync(destination).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            source.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class McsActivityEnvelopeStream(
    Func<CancellationToken, ValueTask<string?>> readActivity,
    Action<Exception> responseError) : Stream
{
    private readonly Func<CancellationToken, ValueTask<string?>> _readActivity = readActivity;
    private readonly Action<Exception> _responseError = responseError;
    private byte[] _pending = [];
    private int _pendingOffset;
    private bool _started;
    private bool _finished;
    private bool _hasActivity;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (_pendingOffset >= _pending.Length && !_finished)
        {
            if (!_started)
            {
                _pending = Encoding.UTF8.GetBytes("{\"activities\":[");
                _pendingOffset = 0;
                _started = true;
                break;
            }

            string? activity;
            try
            {
                activity = await _readActivity(cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                _responseError(exception);
                activity = null;
            }

            if (activity is null)
            {
                _pending = Encoding.UTF8.GetBytes("]}");
                _pendingOffset = 0;
                _finished = true;
                break;
            }

            _pending = Encoding.UTF8.GetBytes((_hasActivity ? "," : string.Empty) + activity);
            _pendingOffset = 0;
            _hasActivity = true;
        }

        if (_pendingOffset >= _pending.Length)
        {
            return 0;
        }

        var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count;
        return count;
    }
}
