using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

/// <summary>Draws a transient spinner line on standard error while a command runs.</summary>
public sealed class ProgressSpinner : IProgressStatus
{
    private const int FallbackTerminalWidth = 80;
    private const string FrameColor = "36";

    private static readonly TimeSpan FirstFrameDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);
    private static readonly string[] UnicodeFrames = ["\u280B", "\u2819", "\u2839", "\u2838", "\u283C", "\u2834", "\u2826", "\u2827", "\u2807", "\u280F"];
    private static readonly string[] AsciiFrames = ["|", "/", "-", "\\"];

    private readonly Lock _gate = new();
    private readonly TextWriter _terminal;
    private readonly bool _enabled;
    private readonly bool _color;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int> _terminalWidth;
    private readonly string[] _frames;
    private Session? _session;
    private int _drawnWidth;
    private int _pausedReads;
    private int _drawInProgress;
    private bool _atLineStart = true;

    /// <summary>Creates a spinner that draws on <paramref name="terminal"/>.</summary>
    /// <param name="terminal">The standard-error writer the spinner line is drawn on.</param>
    /// <param name="enabled">Whether the terminal can show a spinner at all.</param>
    /// <param name="color">Whether the spinner frame may use ANSI color.</param>
    /// <param name="timeProvider">Schedules frames and measures the elapsed time.</param>
    /// <param name="terminalWidth">Reads the terminal width in columns, or zero when it is unknown.</param>
    public ProgressSpinner(TextWriter terminal, bool enabled, bool color, TimeProvider timeProvider, Func<int> terminalWidth)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _enabled = enabled;
        _color = color;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _terminalWidth = terminalWidth ?? throw new ArgumentNullException(nameof(terminalWidth));
        _frames = CanEncode(terminal.Encoding, string.Concat(UnicodeFrames)) ? UnicodeFrames : AsciiFrames;
        Error = TextWriter.Synchronized(new SpinnerAwareWriter(this));
    }

    /// <summary>The standard-error writer that clears the spinner line before anything else is written.</summary>
    public TextWriter Error { get; }

    /// <summary>Wraps <paramref name="input"/> so the spinner pauses while the operator types an answer.</summary>
    /// <param name="input">The reader prompts read answers from.</param>
    /// <returns>The wrapped reader.</returns>
    public TextReader WrapInput(TextReader input) => new SpinnerAwareReader(this, input ?? throw new ArgumentNullException(nameof(input)));

    /// <summary>Shows <paramref name="label"/> beside the spinner until the returned scope is disposed.</summary>
    /// <param name="label">What the command is doing, such as "Deploying agent".</param>
    /// <returns>The scope that stops the spinner and clears its line.</returns>
    public IDisposable Start(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        lock (_gate)
        {
            if (!_enabled || _session is not null)
            {
                return NoSession.Instance;
            }

            _session = new Session(this, Clean(label), _timeProvider.GetTimestamp());
            _session.Timer = _timeProvider.CreateTimer(static state => ((Session)state!).Draw(), _session, FirstFrameDelay, FrameInterval);

            return _session;
        }
    }

    /// <inheritdoc />
    public bool Report(string status)
    {
        lock (_gate)
        {
            if (_session is null)
            {
                return false;
            }

            _session.Status = Clean(status) is { Length: > 0 } cleaned ? cleaned : null;

            return true;
        }
    }

    private void Draw(Session session)
    {
        if (Interlocked.Exchange(ref _drawInProgress, 1) == 1)
        {
            return;
        }

        try
        {
            var usableColumns = UsableColumns(_terminalWidth());

            lock (_gate)
            {
                if (!ReferenceEquals(_session, session) || _pausedReads > 0 || !_atLineStart)
                {
                    return;
                }

                var (line, width) = Compose(session, usableColumns);

                if (width == 0)
                {
                    ClearLine();

                    return;
                }

                session.Frame++;

                if (!TryWriteTerminal($"\r{line}{new string(' ', Math.Max(0, Math.Min(_drawnWidth, usableColumns) - width))}"))
                {
                    _session = null;
                    _drawnWidth = 0;
                    session.Timer?.Dispose();

                    return;
                }

                _drawnWidth = width;
            }
        }
        finally
        {
            Volatile.Write(ref _drawInProgress, 0);
        }
    }

    private (string Line, int Width) Compose(Session session, int usableColumns)
    {
        var frame = _frames[session.Frame % _frames.Length];
        var available = usableColumns - frame.Length - 1;

        if (available < 0)
        {
            return (string.Empty, 0);
        }

        var message = session.Status is null ? session.Label : $"{session.Label}: {session.Status}";
        var elapsed = $" ({FormatElapsed(_timeProvider.GetElapsedTime(session.StartTimestamp))})";
        var suffix = available >= elapsed.Length ? elapsed : string.Empty;
        var room = available - suffix.Length;
        var shown = message.Length > room ? message[..room] : message;

        return ($"{(_color ? $"\u001b[{FrameColor}m{frame}\u001b[0m" : frame)} {shown}{suffix}", frame.Length + 1 + shown.Length + suffix.Length);
    }

    private void WriteThrough(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_gate)
        {
            ClearLine();
            _terminal.Write(text);
            _atLineStart = text[^1] == '\n';
        }
    }

    private void PauseForInput()
    {
        lock (_gate)
        {
            ClearLine();
            _pausedReads++;
        }
    }

    private void ResumeAfterInput(bool lineEntered)
    {
        lock (_gate)
        {
            _pausedReads--;
            _atLineStart |= lineEntered;
        }
    }

    private void Stop(Session session)
    {
        session.Timer?.Dispose();

        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }

            _session = null;
            ClearLine();
        }
    }

    private void ClearLine()
    {
        if (_drawnWidth > 0)
        {
            TryWriteTerminal($"\r{new string(' ', Math.Min(_drawnWidth, UsableColumns(_terminalWidth())))}\r");
            _drawnWidth = 0;
        }
    }

    private bool TryWriteTerminal(string text)
    {
        try
        {
            _terminal.Write(text);

            return true;
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m" : elapsed.TotalMinutes >= 1 ? $"{elapsed.Minutes}m {elapsed.Seconds:00}s" : $"{elapsed.Seconds}s";

    private static int UsableColumns(int terminalWidth) => (terminalWidth > 0 ? terminalWidth : FallbackTerminalWidth) - 1;

    private static string Clean(string text) => TerminalText.Sanitize(text).Replace('\t', ' ').Trim();

    private static bool CanEncode(Encoding encoding, string text)
    {
        try
        {
            return encoding.GetString(encoding.GetBytes(text)) == text;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class Session(ProgressSpinner owner, string label, long startTimestamp) : IDisposable
    {
        public string Label { get; } = label;

        public long StartTimestamp { get; } = startTimestamp;

        public string? Status { get; set; }

        public int Frame { get; set; }

        public ITimer? Timer { get; set; }

        public void Draw() => owner.Draw(this);

        public void Dispose() => owner.Stop(this);
    }

    private sealed class NoSession : IDisposable
    {
        public static readonly NoSession Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class SpinnerAwareWriter(ProgressSpinner owner) : TextWriter(owner._terminal.FormatProvider)
    {
        public override Encoding Encoding => owner._terminal.Encoding;

        public override void Write(char value) => owner.WriteThrough(new string(value, 1));

        public override void Write(string? value) => owner.WriteThrough(value);

        public override void Write(char[] buffer, int index, int count) => owner.WriteThrough(new string(buffer, index, count));

        public override void Write(ReadOnlySpan<char> buffer) => owner.WriteThrough(new string(buffer));

        public override void WriteLine(string? value) => owner.WriteThrough(value + NewLine);

        public override void Flush() => owner._terminal.Flush();
    }

    private sealed class SpinnerAwareReader(ProgressSpinner owner, TextReader input) : TextReader
    {
        public override int Peek() => input.Peek();

        public override int Read() => input.Read();

        public override string? ReadLine()
        {
            owner.PauseForInput();
            string? line = null;

            try
            {
                line = input.ReadLine();

                return line;
            }
            finally
            {
                owner.ResumeAfterInput(line is not null);
            }
        }

        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            owner.PauseForInput();
            string? line = null;

            try
            {
                line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                return line;
            }
            finally
            {
                owner.ResumeAfterInput(line is not null);
            }
        }
    }
}
