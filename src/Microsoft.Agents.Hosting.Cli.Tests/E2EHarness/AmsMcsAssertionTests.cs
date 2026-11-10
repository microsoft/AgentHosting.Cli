using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsAssertionTests : IDisposable
{
    private const string Refusal = """{"success":false,"exitCode":2,"errorCode":99002,"errorMessage":"Unrecognized command or argument 'capture'.","remediation":"Run msagent agent --help."}""";

    private readonly ScratchDirectory _scratch = new();
    private readonly Assertions _assert;

    public AmsMcsAssertionTests() => _assert = new Assertions(new Transcript(_scratch.Folder("run")));

    public void Dispose() => _scratch.Dispose();

    [Fact]
    public void AConditionThatHoldsPassesAndOneThatDoesNotFailsWithItsDetail()
    {
        _assert.True(true, "the folder is empty", "entries=0");

        var failure = Assert.Throws<AssertionException>(() => _assert.True(false, "AMS lists the agent", "rows=0"));

        Assert.Equal(["the folder is empty"], _assert.Passed);
        Assert.Equal("AMS lists the agent: expected true, got false. rows=0", failure.Message);
        Assert.Null(failure.CommandLine);
    }

    [Theory]
    [InlineData(Refusal, 2, null, null)]
    [InlineData(Refusal, 2, 2, null)]
    [InlineData(Refusal, 2, 2, "unrecognized COMMAND")]
    [InlineData(Refusal, 2, null, "Run msagent agent --help")]
    [InlineData("""{"success":false,"errorCode":99002}""", 0, 0, null)]
    [InlineData("""{"success":true,"errorCode":99002}""", 1, 1, null)]
    public void ARefusalWithTheExpectedCodeExitAndTextPasses(string stdout, int exitCode, int? expectedExit, string? fragment)
    {
        var outcome = _assert.Refused(Result(stdout, exitCode), 99002, "capture is unknown", expectedExit, fragment);

        Assert.Equal(99002, outcome.ErrorCode);
        Assert.Equal(["capture is unknown"], _assert.Passed);
    }

    [Theory]
    [InlineData(Refusal, 2, 19010, null, null, "errorCode 19010", "exit 2, errorCode 99002: Unrecognized command or argument 'capture'. Run msagent agent --help.")]
    [InlineData(Refusal, 2, 99002, 1, null, "errorCode 99002 and exit 1", "exit 2, errorCode 99002: Unrecognized command or argument 'capture'. Run msagent agent --help.")]
    [InlineData(Refusal, 2, 99002, null, "PreconditionFailed", "errorCode 99002 with \"PreconditionFailed\"", "exit 2, errorCode 99002: Unrecognized command or argument 'capture'. Run msagent agent --help.")]
    [InlineData("""{"success":true,"status":"agent-created","errorCode":99002}""", 0, 99002, null, null, "errorCode 99002", "exit 0, errorCode 99002: ")]
    [InlineData("Unhandled exception.", 1, 99002, 1, null, "errorCode 99002 and exit 1", "exit 1, errorCode : Unhandled exception.")]
    public void AnythingElseFailsNamingWhatWasExpectedAndWhatCame(string stdout, int exitCode, int errorCode, int? expectedExit, string? fragment, string expected, string actual)
    {
        var failure = Assert.Throws<AssertionException>(() => _assert.Refused(Result(stdout, exitCode), errorCode, "the create is refused", expectedExit, fragment));

        Assert.Equal("the create is refused", failure.Check);
        Assert.Equal(expected, failure.Expected);
        Assert.Equal(actual, failure.Actual);
        Assert.Equal("msagent agent create --project x", failure.CommandLine);
        Assert.Empty(_assert.Passed);
    }

    [Fact]
    public void ASignInPromptBlocksUnlessExitThreeIsWhatIsExpected()
    {
        var blocked = Assert.Throws<ScenarioBlockedException>(() => _assert.Refused(Result("""{"success":false,"errorCode":10003,"errorMessage":"Sign in first."}""", 3), 10003, "a refusal"));
        var expected = _assert.Refused(Result("""{"success":false,"errorCode":10003,"errorMessage":"Sign in first."}""", 3), 10003, "a refusal", 3);

        Assert.Equal("exit 3 · consent required for Unspecified · msagent auth login", blocked.Message);
        Assert.Equal(10003, expected.ErrorCode);
    }

    private static MsagentResult Result(string stdout, int exitCode) => new(["agent", "create", "--project", "x"], exitCode, stdout, string.Empty, TimeSpan.Zero);
}
