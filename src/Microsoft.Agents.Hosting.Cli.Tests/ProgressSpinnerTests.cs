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

        Assert.Equal("\r\u280B Deploying agent (0s)", _terminal.ToString());
    }

    [Fact]
    public void Frames_AdvanceEveryTenthOfASecond()
    {
        using var scope = Create().Start("Working");

        _time.Advance(TimeSpan.FromMilliseconds(700));

        Assert.Equal("\r\u280B Working (0s)\r\u2819 Working (0s)\r\u2839 Working (0s)", _terminal.ToString());
    }

    [Fact]
    public void Frames_FallBackToAsciiWhenTheTerminalCannotShowBraille()
    {
        var terminal = new AsciiWriter();
        using var scope = Create(terminal: terminal).Start("Working");

        _time.Advance(TimeSpan.FromMilliseconds(900));

        Assert.Equal("\r| Working (0s)\r/ Working (0s)\r- Working (0s)\r\\ Working (0s)\r| Working (0s)", terminal.ToString());
    }

    [Theory]
    [InlineData(59, "59s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3725, "1h 02m")]
    public void Elapsed_IsShownInSecondsMinutesOrHours(int seconds, string expected)
    {
        using var scope = Create().Start("Working");

        _time.Advance(TimeSpan.FromSeconds(seconds));

        Assert.EndsWith($" Working ({expected})", _terminal.ToString());
    }

    [Fact]
    public void Report_ShowsTheStepAfterTheLabel()
    {
        var spinner = Create();
        using var scope = spinner.Start("Deploying agent");

        var shown = spinner.Report("Pushing changes");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.True(shown);
        Assert.Equal("\r\u280B Deploying agent: Pushing changes (0s)", _terminal.ToString());
    }

    [Fact]
    public void Report_BlankStepShowsTheLabelAlone()
    {
        var spinner = Create();
        using var scope = spinner.Start("Deploying agent");
        spinner.Report("Pushing changes");

        spinner.Report("   ");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal("\r\u280B Deploying agent (0s)", _terminal.ToString());
    }

    [Fact]
    public void Report_NeutralizesControlCharactersAndTabs()
    {
        var spinner = Create();
        using var scope = spinner.Start("Running evaluation");

        spinner.Report("In\u001b[2JProgress\tnow");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Equal("\r\u280B Running evaluation: In\uFFFD[2JProgress now (0s)", _terminal.ToString());
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

        Assert.Equal($"\r\u280B Working (0s){Clear(14)}", _terminal.ToString());
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

        Assert.Equal("\r\u280B Deploying agent (0s)", _terminal.ToString());
    }

    [Theory]
    [InlineData(20, "\r\u280B Deploying ag (0s)")]
    [InlineData(6, "\r\u280B Dep")]
    [InlineData(2, "")]
    [InlineData(1, "")]
    [InlineData(0, "\r\u280B Deploying agent: Pushing changes (0s)")]
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

        Assert.Equal($"\r\u280B Deploying agent: Activating workflows (0s)\r\u2819 Deploying agent: Publishing (0s){new string(' ', 10)}", _terminal.ToString());
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

        Assert.Equal($"\r\u280B Deploying agent (0s)\r\u2819 De (0s){Clear(1)}", _terminal.ToString());
    }

    [Fact]
    public void Draw_ColorsOnlyTheFrameAndClearsByVisibleWidth()
    {
        var scope = Create(color: true).Start("Working");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        scope.Dispose();

        Assert.Equal($"\r\u001b[36m\u280B\u001b[0m Working (0s){Clear(14)}", _terminal.ToString());
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

        Assert.Equal(["\r\u280B Working (0s)", "\r\u2819 Working (1s)"], terminal.Writes);
    }

    [Fact]
    public void Error_ClearsTheSpinnerBeforeAMessageAndRedrawsBelowIt()
    {
        var spinner = Create();
        using var scope = spinner.Start("Working");
        _time.Advance(TimeSpan.FromMilliseconds(500));

        spinner.Error.WriteLine("Open this link to sign in.");
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal($"\r\u280B Working (0s){Clear(14)}Open this link to sign in.{Environment.NewLine}\r\u2819 Working (0s)", _terminal.ToString());
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

        Assert.Equal($"\r\u280B Working (0s){Clear(14)}Selecting: {Environment.NewLine}\r\u2819 Working (0s)", _terminal.ToString());
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
        var input = spinner.WrapInput(new TypingReader(_time, "agent"));

        var answer = asynchronous ? await input.ReadLineAsync(CancellationToken.None) : input.ReadLine();
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal("agent", answer);
        Assert.Equal($"\r\u280B Deleting agent (0s){Clear(21)}Type the agent name to confirm.{Environment.NewLine}\r\u2819 Deleting agent (2s)", _terminal.ToString());
    }

    [Fact]
    public void WrapInput_EndOfInputKeepsTheSpinnerOffThePromptLine()
    {
        var spinner = Create();
        using var scope = spinner.Start("Deleting agent");
        spinner.Error.Write("Confirm by typing (agent): ");

        var answer = spinner.WrapInput(new TypingReader(_time, null)).ReadLine();
        _time.Advance(TimeSpan.FromMilliseconds(500));

        Assert.Null(answer);
        Assert.Equal("Confirm by typing (agent): ", _terminal.ToString());
    }

    private ProgressSpinner Create(bool enabled = true, bool color = false, TextWriter? terminal = null) => new(terminal ?? _terminal, enabled, color, _time, () => _terminalWidth);

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
}
