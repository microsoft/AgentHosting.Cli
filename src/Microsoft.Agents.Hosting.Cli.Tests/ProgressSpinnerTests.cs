using System.Text;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class ProgressSpinnerTests
{
    private readonly ManualTimeProvider _time = new();
    private readonly StringWriter _terminal = new();
    private int _terminalWidth = 80;

    [Fact]
    public void Start_DrawsNothingForTheFirstHalfSecond()
    {
        using var scope = Create().Start("Deploying agent");

        _time.Advance(TimeSpan.FromMilliseconds(499));

        Assert.Empty(_terminal.ToString());
    }

    [Fact]
    public void Start_DrawsTheLabelAndElapsedTimeOnceHalfASecondHasPassed()
    {
        using var scope = Create().Start("Deploying agent");

        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal("\r\u280B Deploying agent (0s)\r", _terminal.ToString());
    }

    [Fact]
    public void Frames_AdvanceEveryTenthOfASecond()
    {
        using var scope = Create().Start("Working");

        _time.Advance(TimeSpan.FromMilliseconds(700));

        Assert.Equal("\r\u280B Working (0s)\r\r\u2819 Working (0s)\r\r\u2839 Working (0s)\r", _terminal.ToString());
    }

    [Fact]
    public void Frames_FallBackToAsciiWhenTheTerminalCannotShowBraille()
    {
        var terminal = new AsciiWriter();
        using var scope = Create(terminal: terminal).Start("Working");

        _time.Advance(TimeSpan.FromMilliseconds(900));

        Assert.Equal("\r| Working (0s)\r\r/ Working (0s)\r\r- Working (0s)\r\r\\ Working (0s)\r\r| Working (0s)\r", terminal.ToString());
    }

    [Theory]
    [InlineData(59, "59s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3725, "1h 02m")]
    public void Elapsed_IsShownInSecondsMinutesOrHours(int seconds, string expected)
    {
        using var scope = Create().Start("Working");

        _time.Advance(TimeSpan.FromSeconds(seconds));

        Assert.EndsWith($" Working ({expected})\r", _terminal.ToString());
    }

    [Fact]
    public void Report_ShowsTheStepAfterTheLabel()
    {
        var spinner = Create();
        using var scope = spinner.Start("Deploying agent");

        var shown = spinner.Report("Pushing changes");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.True(shown);
        Assert.Equal("\r\u280B Deploying agent: Pushing changes (0s)\r", _terminal.ToString());
    }

    [Fact]
    public void Report_BlankStepShowsTheLabelAlone()
    {
        var spinner = Create();
        using var scope = spinner.Start("Deploying agent");
        spinner.Report("Pushing changes");

        spinner.Report("   ");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal("\r\u280B Deploying agent (0s)\r", _terminal.ToString());
    }

    [Fact]
    public void Report_NeutralizesControlCharactersAndTabs()
    {
        var spinner = Create();
        using var scope = spinner.Start("Running evaluation");

        spinner.Report("In\u001b[2JProgress\tnow");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal("\r\u280B Running evaluation: In\uFFFD[2JProgress now (0s)\r", _terminal.ToString());
    }

    [Fact]
    public void Report_SaysNothingIsShownBeforeStartOrAfterTheScopeEnds()
    {
        var spinner = Create();

        Assert.False(spinner.Report("Pushing changes"));
        spinner.Start("Deploying agent").Dispose();
        Assert.False(spinner.Report("Pushing changes"));
    }

    [Fact]
    public void Disabled_NeverDrawsOrSchedulesFramesAndShowsNoStep()
    {
        var spinner = Create(enabled: false);
        using var scope = spinner.Start("Deploying agent");

        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(spinner.Report("Pushing changes"));
        Assert.Empty(_terminal.ToString());
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void Dispose_ClearsTheLineAndStopsTheFrames()
    {
        var scope = Create().Start("Working");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        scope.Dispose();
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal($"\r\u280B Working (0s)\r{Clear(14)}", _terminal.ToString());
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void Dispose_BeforeTheFirstFrameWritesNothing()
    {
        Create().Start("Working").Dispose();

        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(_terminal.ToString());
    }

    [Fact]
    public void Start_WhileAnotherIsShowingKeepsTheFirst()
    {
        var spinner = Create();
        using var first = spinner.Start("Deploying agent");

        spinner.Start("Pulling agent").Dispose();
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal("\r\u280B Deploying agent (0s)\r", _terminal.ToString());
    }

    [Theory]
    [InlineData(20, "\r\u280B Deploying ag (0s)\r")]
    [InlineData(6, "\r\u280B Dep\r")]
    [InlineData(2, "")]
    [InlineData(1, "")]
    [InlineData(0, "\r\u280B Deploying agent: Pushing changes (0s)\r")]
    public void Draw_FitsTheLineInsideTheTerminalWidth(int width, string expected)
    {
        _terminalWidth = width;
        var spinner = Create();
        using var scope = spinner.Start("Deploying agent");
        spinner.Report("Pushing changes");

        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(expected, _terminal.ToString());
    }

    [Fact]
    public void Draw_PadsOverTheTailOfALongerPreviousLine()
    {
        var spinner = Create();
        using var scope = spinner.Start("Deploying agent");
        spinner.Report("Activating workflows");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        spinner.Report("Publishing");
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal($"\r\u280B Deploying agent: Activating workflows (0s)\r\r\u2819 Deploying agent: Publishing (0s){new string(' ', 10)}\r", _terminal.ToString());
    }

    [Fact]
    public void Draw_StaysInsideATerminalThatShrinks()
    {
        var scope = Create().Start("Deploying agent");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        _terminalWidth = 10;
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _terminalWidth = 2;
        _time.Advance(TimeSpan.FromMilliseconds(100));

        scope.Dispose();

        Assert.Equal($"\r\u280B Deploying agent (0s)\r\r\u2819 De (0s)\r{Clear(1)}", _terminal.ToString());
    }

    [Theory]
    [InlineData(20, "\u90E8\u7F72\u4EE3\u7406", "\u8FDB\u884C\u4E2D", "\r\u280B \u90E8\u7F72\u4EE3\u7406: \u8FDB (0s)\r")]
    [InlineData(20, "Deploying", "e\u0301e\u0301", "\r\u280B Deploying: e\u0301 (0s)\r")]
    [InlineData(17, "Ship", "\U0001F680\U0001F680\U0001F680", "\r\u280B Ship: \U0001F680 (0s)\r")]
    [InlineData(14, "Go", "\u2705\u2705", "\r\u280B Go: \u2705 (0s)\r")]
    [InlineData(14, "Go", "\u2764\uFE0F\u2764\uFE0F", "\r\u280B Go: \u2764\uFE0F (0s)\r")]
    [InlineData(14, "Go", "\U0001F7E2\U0001F7E2", "\r\u280B Go: \U0001F7E2 (0s)\r")]
    public void Draw_MeasuresAndCutsTextByTerminalColumnsAndWholeCharacters(int width, string label, string status, string expected)
    {
        _terminalWidth = width;
        var spinner = Create();
        using var scope = spinner.Start(label);
        spinner.Report(status);

        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal(expected, _terminal.ToString());
    }

    [Fact]
    public void Dispose_ClearsEveryColumnAWideStepTook()
    {
        var spinner = Create();
        var scope = spinner.Start("Deploying agent");
        spinner.Report("\u8FDB\u884C\u4E2D");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        scope.Dispose();

        Assert.Equal($"\r\u280B Deploying agent: \u8FDB\u884C\u4E2D (0s)\r{Clear(30)}", _terminal.ToString());
    }

    [Fact]
    public void Draw_ColorsOnlyTheFrameAndErasesWithEscapeSequences()
    {
        var spinner = Create(ansi: true);
        var scope = spinner.Start("Deploying agent");
        spinner.Report("Pushing changes");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        spinner.Report("Publishing");
        _time.Advance(TimeSpan.FromMilliseconds(100));

        scope.Dispose();

        Assert.Equal("\r\u001b[36m\u280B\u001b[0m Deploying agent: Pushing changes (0s)\u001b[J\r\r\u001b[36m\u2819\u001b[0m Deploying agent: Publishing (0s)\u001b[J\r\r\u001b[J\r", _terminal.ToString());
    }

    [Fact]
    public void Draw_LeavesNoRowsBehindWhenTheTerminalRewrapsTheLineOnResize()
    {
        var terminal = new ReflowingTerminal(30);
        _terminalWidth = 30;
        var scope = Create(ansi: true, terminal: terminal).Start("Deploying agent");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        terminal.Resize(10);
        _terminalWidth = 10;

        _time.Advance(TimeSpan.FromMilliseconds(100));
        var redrawn = terminal.Rows.ToArray();
        scope.Dispose();

        Assert.Equal(["\u2819 De (0s)"], redrawn);
        Assert.Equal([string.Empty], terminal.Rows);
    }

    [Fact]
    public void Draw_AFailingTerminalStopsTheSpinnerQuietly()
    {
        var terminal = FailingTerminal();
        var spinner = Create(terminal: terminal);
        using var scope = spinner.Start("Working");

        _time.Advance(TimeSpan.FromMilliseconds(800));

        Assert.Single(terminal.Writes);
        Assert.False(spinner.Report("Pushing changes"));
        Assert.Equal(0, _time.ActiveTimers);
    }

    [Fact]
    public void Draw_SkipsFramesWhileTheTerminalIsStillTakingTheLastOne()
    {
        var terminal = new ScriptedWriter(() => _time.Advance(TimeSpan.FromSeconds(1)));
        using var scope = Create(terminal: terminal).Start("Working");

        _time.Advance(TimeSpan.FromMilliseconds(500));
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(["\r\u280B Working (0s)\r", "\r\u2819 Working (1s)\r"], terminal.Writes);
    }

    [Fact]
    public void Error_ClearsTheSpinnerBeforeAMessageAndRedrawsBelowIt()
    {
        var spinner = Create();
        using var scope = spinner.Start("Working");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        spinner.Error.WriteLine("Open this link to sign in.");
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal($"\r\u280B Working (0s)\r{Clear(14)}Open this link to sign in.{Environment.NewLine}\r\u2819 Working (0s)\r", _terminal.ToString());
    }

    [Fact]
    public void Error_KeepsTheSpinnerHiddenUntilAPartialLineEnds()
    {
        var spinner = Create();
        using var scope = spinner.Start("Working");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        spinner.Error.Write("Selecting: ");
        _time.Advance(TimeSpan.FromMilliseconds(300));
        spinner.Error.WriteLine();
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal($"\r\u280B Working (0s)\r{Clear(14)}Selecting: {Environment.NewLine}\r\u2819 Working (0s)\r", _terminal.ToString());
    }

    [Fact]
    public void Error_PassesWriteFailuresToTheCaller()
    {
        var spinner = Create(terminal: FailingTerminal());

        Assert.Throws<IOException>(() => spinner.Error.WriteLine("Open this link to sign in."));
    }

    [Fact]
    public void Error_WritesEachLineToTheTerminalInOneWrite()
    {
        var terminal = new ScriptedWriter();

        Create(terminal: terminal).Error.WriteLine("Open this link to sign in.");

        Assert.Equal([$"Open this link to sign in.{Environment.NewLine}"], terminal.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Error_KeepsEachLineWholeWhenWritersRace(bool asynchronous)
    {
        var error = Create().Error;
        var lines = Enumerable.Range(0, 2000).Select(index => $"Line {index}").ToArray();

        await Task.WhenAll(lines.Select(line => asynchronous ? error.WriteLineAsync(line.ToCharArray()) : Task.Run(() => error.WriteLine(line))));

        Assert.Equal(lines.Order(), _terminal.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrapInput_PausesTheSpinnerWhileTheOperatorTypes(bool asynchronous)
    {
        var spinner = Create();
        using var scope = spinner.Start("Deleting agent");
        _time.Advance(TimeSpan.FromMilliseconds(500));
        spinner.Error.WriteLine("Type the agent name to confirm.");
        var input = spinner.WrapInput(new TypingReader(_time, "agent"), echoed: true);

        var answer = asynchronous ? await input.ReadLineAsync(CancellationToken.None) : input.ReadLine();
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal("agent", answer);
        Assert.Equal($"\r\u280B Deleting agent (0s)\r{Clear(21)}Type the agent name to confirm.{Environment.NewLine}\r\u2819 Deleting agent (2s)\r", _terminal.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrapInput_EndOfInputKeepsTheSpinnerOffThePromptLine(bool echoed)
    {
        var spinner = Create();
        using var scope = spinner.Start("Deleting agent");
        spinner.Error.Write("Confirm by typing (agent): ");

        var answer = spinner.WrapInput(new TypingReader(_time, null), echoed).ReadLine();
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Null(answer);
        Assert.Equal("Confirm by typing (agent): ", _terminal.ToString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task WrapInput_ResumesTheSpinnerBelowAnAnsweredPrompt(bool echoed, bool asynchronous)
    {
        var spinner = Create();
        using var scope = spinner.Start("Deleting agent");
        spinner.Error.Write("Confirm by typing (agent): ");
        var input = spinner.WrapInput(new TypingReader(_time, "agent"), echoed);

        var answer = asynchronous ? await input.ReadLineAsync(CancellationToken.None) : input.ReadLine();
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal("agent", answer);
        Assert.Equal($"Confirm by typing (agent): {(echoed ? string.Empty : Environment.NewLine)}\r\u280B Deleting agent (2s)\r", _terminal.ToString());
    }

    [Fact]
    public void WrapInput_LeavesAnAnsweredPromptLineAloneWhenNoSpinnerIsShowing()
    {
        var spinner = Create(enabled: false);
        using var scope = spinner.Start("Deleting agent");
        spinner.Error.Write("Confirm by typing (agent): ");

        var answer = spinner.WrapInput(new TypingReader(_time, "agent"), echoed: false).ReadLine();

        Assert.Equal("agent", answer);
        Assert.Equal("Confirm by typing (agent): ", _terminal.ToString());
    }

    private ProgressSpinner Create(bool enabled = true, bool ansi = false, TextWriter? terminal = null) => new(terminal ?? _terminal, enabled, ansi, _time, () => _terminalWidth);

    private static string Clear(int width) => $"\r{new string(' ', width)}\r";

    private static ScriptedWriter FailingTerminal() => new(() => throw new IOException("The terminal is gone."));

    private sealed class AsciiWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.ASCII;
    }

    private sealed class ScriptedWriter(Action? onFirstWrite = null) : StringWriter
    {
        public List<string?> Writes { get; } = [];

        public override void Write(string? value)
        {
            Writes.Add(value);
            base.Write(value);

            if (Writes.Count == 1)
            {
                onFirstWrite?.Invoke();
            }
        }
    }

    private sealed class TypingReader(ManualTimeProvider time, string? line) : TextReader
    {
        public override string? ReadLine()
        {
            time.Advance(TimeSpan.FromSeconds(2));
            return line;
        }

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult(ReadLine());
    }

    private sealed class ReflowingTerminal(int columns) : StringWriter
    {
        private readonly List<StringBuilder> _lines = [new()];
        private int _columns = columns;
        private int _line;
        private int _offset;

        public IEnumerable<string> Rows => _lines.SelectMany(line => Enumerable.Range(0, Math.Max(1, (line.Length + _columns - 1) / _columns)).Select(row => line.ToString(row * _columns, Math.Min(_columns, line.Length - (row * _columns)))));

        public void Resize(int columns) => _columns = columns;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string? value)
        {
            base.Write(value);

            for (var index = 0; index < (value?.Length ?? 0); index++)
            {
                index = Interpret(value!, index);
            }
        }

        private int Interpret(string text, int index)
        {
            switch (text[index])
            {
                case '\r':
                    _offset -= _offset % _columns;
                    return index;
                case '\n':
                    _line++;
                    _offset = 0;

                    if (_line == _lines.Count)
                    {
                        _lines.Add(new StringBuilder());
                    }

                    return index;
                case '\u001b':
                    return Escape(text, index);
                default:
                    if (_offset < _lines[_line].Length)
                    {
                        _lines[_line][_offset] = text[index];
                    }
                    else
                    {
                        _lines[_line].Append(text[index]);
                    }

                    _offset++;
                    return index;
            }
        }

        private int Escape(string text, int index)
        {
            var final = index + 2;

            while (!char.IsAsciiLetter(text[final]))
            {
                final++;
            }

            switch (text[final])
            {
                case 'm':
                    return final;
                case 'J':
                    _lines[_line].Length = Math.Min(_lines[_line].Length, _offset);
                    _lines.RemoveRange(_line + 1, _lines.Count - _line - 1);
                    return final;
                default:
                    throw new NotSupportedException($"The terminal model does not handle ESC{text[(index + 1)..(final + 1)]}.");
            }
        }
    }
}
