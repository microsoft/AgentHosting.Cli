using System.Globalization;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Rendering;

/// <summary>Draws a transient spinner line on standard error while a command runs.</summary>
public sealed class ProgressSpinner : IProgressStatus
{
    private const int FallbackTerminalWidth = 80;
    private const string FrameColor = "36";
    private const string EraseBelow = "\u001b[J";
    private const char EmojiPresentationSelector = '\uFE0F';

    private static readonly TimeSpan FirstFrameDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);
    private static readonly string[] UnicodeFrames = ["\u280B", "\u2819", "\u2839", "\u2838", "\u283C", "\u2834", "\u2826", "\u2827", "\u2807", "\u280F"];
    private static readonly string[] AsciiFrames = ["|", "/", "-", "\\"];
    private static readonly (int First, int Last)[] WideCharacters = [(0x1100, 0x115F), (0x231A, 0x231B), (0x2329, 0x232A), (0x23E9, 0x23F3), (0x25FD, 0x25FE), (0x2600, 0x27BF), (0x2B1B, 0x2B55), (0x2E80, 0x303E), (0x3040, 0xA4CF), (0xAC00, 0xD7A3), (0xF900, 0xFAFF), (0xFE10, 0xFE19), (0xFE30, 0xFE6F), (0xFF00, 0xFF60), (0xFFE0, 0xFFE6), (0x1F000, 0x1FAFF), (0x20000, 0x3FFFD)];

    private readonly Lock _gate = new();
    private readonly TextWriter _terminal;
    private readonly bool _enabled;
    private readonly bool _ansi;
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
    /// <param name="ansi">Whether the terminal takes ANSI escape sequences, which color the frame and erase every row the spinner line occupies.</param>
    /// <param name="timeProvider">Schedules frames and measures the elapsed time.</param>
    /// <param name="terminalWidth">Reads the terminal width in columns, or zero when it is unknown.</param>
    public ProgressSpinner(TextWriter terminal, bool enabled, bool ansi, TimeProvider timeProvider, Func<int> terminalWidth)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _enabled = enabled;
        _ansi = ansi;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _terminalWidth = terminalWidth ?? throw new ArgumentNullException(nameof(terminalWidth));
        _frames = CanEncode(terminal.Encoding, string.Concat(UnicodeFrames)) ? UnicodeFrames : AsciiFrames;
        Error = TextWriter.Synchronized(new SpinnerAwareWriter(this));
    }

    /// <summary>The standard-error writer that clears the spinner line before anything else is written.</summary>
    public TextWriter Error { get; }

    /// <summary>Wraps <paramref name="input"/> so the spinner pauses while the operator types an answer.</summary>
    /// <param name="input">The reader prompts read answers from.</param>
    /// <param name="echoed">Whether the terminal echoes the typed answer and its line break, which redirected input does not.</param>
    /// <returns>The wrapped reader.</returns>
    public TextReader WrapInput(TextReader input, bool echoed) => new SpinnerAwareReader(this, input ?? throw new ArgumentNullException(nameof(input)), echoed);

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

                if (!TryWriteTerminal($"\r{line}{EraseAndReturn(Math.Max(0, Math.Min(_drawnWidth, usableColumns) - width))}"))
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

        var elapsed = $" ({FormatElapsed(_timeProvider.GetElapsedTime(session.StartTimestamp))})";
        var suffix = available >= elapsed.Length ? elapsed : string.Empty;
        var (shown, shownColumns) = Fit(session.Status is null ? session.Label : $"{session.Label}: {session.Status}", available - suffix.Length);

        return ($"{(_ansi ? $"\u001b[{FrameColor}m{frame}\u001b[0m" : frame)} {shown}{suffix}", frame.Length + 1 + shownColumns + suffix.Length);
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

    private void ResumeAfterInput(bool lineEntered, bool echoed)
    {
        lock (_gate)
        {
            _pausedReads--;

            if (lineEntered && (echoed || _atLineStart || (_session is not null && TryWriteTerminal(_terminal.NewLine))))
            {
                _atLineStart = true;
            }
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
            TryWriteTerminal($"\r{EraseAndReturn(Math.Min(_drawnWidth, UsableColumns(_terminalWidth())))}");
            _drawnWidth = 0;
        }
    }

    private string EraseAndReturn(int columns) => _ansi ? $"{EraseBelow}\r" : $"{new string(' ', columns)}\r";

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

    private static (string Text, int Columns) Fit(string text, int columns)
    {
        var used = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);

        while (elements.MoveNext())
        {
            var width = DisplayColumns(elements.GetTextElement());

            if (used + width > columns)
            {
                return (text[..elements.ElementIndex], used);
            }

            used += width;
        }

        return (text, used);
    }

    private static int DisplayColumns(string textElement) => Math.Max(textElement.Contains(EmojiPresentationSelector) ? 2 : 0, textElement.EnumerateRunes().Sum(DisplayColumns));

    private static int DisplayColumns(Rune rune) => Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format ? 0 : WideCharacters.Any(range => rune.Value >= range.First && rune.Value <= range.Last) ? 2 : 1;

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

    private sealed class SpinnerAwareReader(ProgressSpinner owner, TextReader input, bool echoed) : TextReader
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
                owner.ResumeAfterInput(line is not null, echoed);
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
                owner.ResumeAfterInput(line is not null, echoed);
            }
        }
    }
}
