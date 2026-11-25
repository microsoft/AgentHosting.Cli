using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ConsoleStandardInputTests
{
    private const int Limit = 64;

    public static TheoryData<byte[], string> EncodedInputs => new()
    {
        { Encoding.UTF8.GetBytes("s3cr\u00e9t\n"), "s3cr\u00e9t\n" },
        { [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("s3cr\u00e9t\r\n")], "s3cr\u00e9t\r\n" },
        { [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("s3cr\u00e9t\n")], "s3cr\u00e9t\n" },
        { [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("s3cr\u00e9t\n")], "s3cr\u00e9t\n" },
        { [.. Encoding.UTF32.GetPreamble(), .. Encoding.UTF32.GetBytes("s3cr\u00e9t\n")], "s3cr\u00e9t\n" },
        { [.. new UTF32Encoding(bigEndian: true, byteOrderMark: true).GetPreamble(), .. new UTF32Encoding(bigEndian: true, byteOrderMark: true).GetBytes("s3cr\u00e9t\n")], "s3cr\u00e9t\n" },
        { [.. Encoding.UTF8.GetPreamble()], string.Empty },
        { [], string.Empty },
    };

    public static TheoryData<byte[], string> MalformedInputs => new()
    {
        { [0x73, 0x65, 0xC3, 0x28, 0x74], "UTF-8" },
        { [0xEF, 0xBB, 0xBF, 0x61, 0xFF, 0x62, 0x0A], "UTF-8" },
        { [0xFF, 0xFE, 0x61, 0x00, 0x62], "UTF-16" },
        { [0xFF, 0xFE, 0x61, 0x00, 0x00, 0xD8], "UTF-16" },
        { [0xFE, 0xFF, 0x00, 0x61, 0x00], "UTF-16" },
        { [0xFF, 0xFE, 0x00, 0x00, 0x00, 0x00, 0x11, 0x00], "UTF-32" },
        { [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x11, 0x00, 0x00], "UTF-32" },
    };

    [Theory]
    [MemberData(nameof(EncodedInputs))]
    public async Task ReadToEndAsync_DecodesUtf8OrTheEncodingTheByteOrderMarkNames(byte[] bytes, string expected)
    {
        var input = new ConsoleStandardInput(() => new MemoryStream(bytes), () => true);

        Assert.Equal(expected, await input.ReadToEndAsync(Limit, CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(MalformedInputs))]
    public async Task ReadToEndAsync_RefusesMalformedTextInEveryEncodingWithoutShowingIt(byte[] bytes, string encodingName)
    {
        var input = new ConsoleStandardInput(() => new MemoryStream(bytes), () => true);

        var exception = await Assert.ThrowsAsync<UsageException>(() => input.ReadToEndAsync(Limit, CancellationToken.None));

        Assert.Equal($"Standard input is not valid {encodingName} text.", exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task ReadToEndAsync_AcceptsInputOfExactlyTheLimit()
    {
        var input = new ConsoleStandardInput(() => new MemoryStream(Encoding.UTF8.GetBytes(new string('s', Limit))), () => true);

        Assert.Equal(new string('s', Limit), await input.ReadToEndAsync(Limit, CancellationToken.None));
    }

    [Fact]
    public async Task ReadToEndAsync_RefusesLongerInputAfterReadingOneByteMoreThanTheLimit()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("SYNTHETIC_SECRET" + new string('s', 1024 * 1024)));
        var input = new ConsoleStandardInput(() => stream, () => true);

        var exception = await Assert.ThrowsAsync<UsageException>(() => input.ReadToEndAsync(Limit, CancellationToken.None));

        Assert.Equal($"Standard input is longer than {Limit} bytes.", exception.Message);
        Assert.DoesNotContain("SYNTHETIC", exception.Message);
        Assert.False(stream.CanRead);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ReadToEndAsync_RequiresAPositiveLimit(int maximumBytes)
    {
        var opens = 0;
        var input = new ConsoleStandardInput(() => { opens++; return Stream.Null; }, () => true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => input.ReadToEndAsync(maximumBytes, CancellationToken.None));
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task StandardInput_IsOpenedOnlyWhenReadAndClosedAfterwards()
    {
        var opens = 0;
        var stream = new MemoryStream("secret"u8.ToArray());
        var input = new ConsoleStandardInput(() => { opens++; return stream; }, () => false);

        Assert.False(input.IsRedirected);
        Assert.Equal(0, opens);
        Assert.Equal("secret", await input.ReadToEndAsync(Limit, CancellationToken.None));
        Assert.Equal(1, opens);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public void Constructor_RequiresBothDelegates()
    {
        Assert.Throws<ArgumentNullException>(() => new ConsoleStandardInput(null!, () => true));
        Assert.Throws<ArgumentNullException>(() => new ConsoleStandardInput(() => Stream.Null, null!));
    }

    [Fact]
    public async Task ReadToEndAsync_EndsWhenCancelledWhileTheStreamIgnoresCancellation()
    {
        var stream = new CancellationIgnoringStream();
        var input = new ConsoleStandardInput(() => stream, () => true);
        using var cancellation = new CancellationTokenSource();

        var read = input.ReadToEndAsync(Limit, cancellation.Token);
        await stream.ReadStarted.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task ReadToEndAsync_RefusesAnAlreadyCancelledReadAndClosesTheStream()
    {
        var stream = new CancellationIgnoringStream();
        var input = new ConsoleStandardInput(() => stream, () => true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => input.ReadToEndAsync(Limit, new CancellationToken(canceled: true)).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(stream.IsDisposed);
    }

    private sealed class CancellationIgnoringStream : Stream
    {
        private readonly TaskCompletionSource<int> _pendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReadStarted => _readStarted.Task;

        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult();
            return new ValueTask<int>(_pendingRead.Task);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            _pendingRead.TrySetResult(0);
            base.Dispose(disposing);
        }
    }
}