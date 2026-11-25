using System.Net;
using System.Net.Http;

namespace Microsoft.Agents.Hosting.Cli.Core.Http;

/// <summary>Captures a bounded diagnostic copy only while the caller consumes the original content.</summary>
internal sealed class HttpDiagnosticContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly Action<ReadOnlyMemory<byte>, bool, Exception?, long> _completed;
    private readonly bool _ownsInner;
    private readonly byte[] _capture = new byte[HttpDiagnosticLoggingPolicy.MaximumBodyBytes + 1];
    private int _captured;
    private long _observed;
    private int _finished;

    public HttpDiagnosticContent(
        HttpContent inner, Action<ReadOnlyMemory<byte>, bool, Exception?, long> completed, bool ownsInner = true)
    {
        _inner = inner;
        _completed = completed;
        _ownsInner = ownsInner;
        foreach (var header in inner.Headers)
        {
            Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _inner.Headers.ContentLength.GetValueOrDefault();
        return _inner.Headers.ContentLength.HasValue;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var observer = new ObservingStream(stream, this, leaveOpen: true);
        try
        {
            _inner.CopyTo(observer, context, cancellationToken);
            Finish(complete: true);
        }
        catch (Exception error)
        {
            Finish(complete: false, error);
            throw;
        }
    }

    protected override async Task SerializeToStreamAsync(
        Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var observer = new ObservingStream(stream, this, leaveOpen: true);
        try
        {
            await _inner.CopyToAsync(observer, context, cancellationToken).ConfigureAwait(false);
            Finish(complete: true);
        }
        catch (Exception error)
        {
            Finish(complete: false, error);
            throw;
        }
    }

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
    {
        try
        {
            return new ObservingStream(_inner.ReadAsStream(cancellationToken), this, leaveOpen: !_ownsInner);
        }
        catch (Exception error)
        {
            Finish(complete: false, error);
            throw;
        }
    }

    protected override Task<Stream> CreateContentReadStreamAsync() =>
        CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new ObservingStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), this, leaveOpen: !_ownsInner);
        }
        catch (Exception error)
        {
            Finish(complete: false, error);
            throw;
        }
    }

    private void Observe(ReadOnlySpan<byte> bytes, bool completeAtLength = false)
    {
        if (Volatile.Read(ref _finished) != 0)
        {
            return;
        }

        var count = Math.Min(bytes.Length, _capture.Length - _captured);
        bytes[..count].CopyTo(_capture.AsSpan(_captured));
        _captured += count;
        _observed += bytes.Length;
        if (completeAtLength && _inner.Headers.ContentLength is { } length && _observed == length)
        {
            Finish(complete: true);
        }
    }

    private void Finish(bool complete, Exception? error = null)
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
        {
            _completed(_capture.AsMemory(0, _captured), complete, error, _observed);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Finish(complete: false);
            if (_ownsInner)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private sealed class ObservingStream(Stream innerStream, HttpDiagnosticContent owner, bool leaveOpen) : Stream
    {
        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => innerStream.CanWrite;
        public override long Length => innerStream.Length;
        public override long Position
        {
            get => innerStream.Position;
            set
            {
                innerStream.Position = value;
                owner.Finish(complete: false);
            }
        }

        public override void Flush() => innerStream.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => innerStream.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = innerStream.Seek(offset, origin);
            owner.Finish(complete: false);
            return position;
        }
        public override void SetLength(long value) => innerStream.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            try
            {
                var count = innerStream.Read(buffer);
                ReadCompleted(buffer[..count], buffer.Length);
                return count;
            }
            catch (Exception error)
            {
                owner.Finish(complete: false, error);
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var count = await innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                ReadCompleted(buffer.Span[..count], buffer.Length);
                return count;
            }
            catch (Exception error)
            {
                owner.Finish(complete: false, error);
                throw;
            }
        }

        private void ReadCompleted(ReadOnlySpan<byte> bytes, int requested)
        {
            owner.Observe(bytes, completeAtLength: true);
            if (bytes.IsEmpty && requested != 0)
            {
                owner.Finish(complete: true);
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            innerStream.Write(buffer);
            owner.Observe(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await innerStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            owner.Observe(buffer.Span);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.Finish(complete: false);
                if (!leaveOpen)
                {
                    innerStream.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            owner.Finish(complete: false);
            if (!leaveOpen)
            {
                await innerStream.DisposeAsync().ConfigureAwait(false);
            }

            GC.SuppressFinalize(this);
        }
    }
}
