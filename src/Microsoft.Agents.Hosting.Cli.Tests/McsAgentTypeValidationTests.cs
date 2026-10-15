using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Pins baseline defect 3.4: <c>--mcs-agent-type</c> was only inspected inside the create service,
/// fourteen lines after an environment round-trip, so a typo cost a service call.
/// </summary>
public sealed class McsAgentTypeValidationTests
{
    private static (int ExitCode, string Stdout, string Stderr, FakeOperations Operations) Run(params string[] args)
    {
        var operations = new FakeOperations();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(operations).InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString(), operations);
    }

    private static string[] CreateWith(string mcsAgentType) =>
    [
        "agent", "create",
        "--agent-type", "MCSAgent",
        "--agent-name", "Contoso",
        "--description", "d",
        "--initial-prompt", "p",
        "--mcs-agent-type", mcsAgentType,
    ];

    [Fact]
    public void AnUnknownAgentTypeIsRejectedBeforeAnyWork()
    {
        var (exitCode, _, stderr, operations) = Run(CreateWith("Bogus"));

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Unknown --mcs-agent-type 'Bogus'.", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentCreateRequest);
    }

    [Fact]
    public void TheRejectionNamesTheSupportedValues()
    {
        Assert.Contains("Supported values: CLI, Classic.", Run(CreateWith("Bogus")).Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CLI")]
    [InlineData("cli")]
    [InlineData("Classic")]
    [InlineData("classic")]
    [InlineData("cli-copilot")]
    [InlineData("clicopilot")]
    public void EveryAcceptedSpellingStillReachesTheService(string spelling)
    {
        var (exitCode, _, _, operations) = Run(CreateWith(spelling));

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(spelling, operations.LastManagedAgentCreateRequest?.McsAgentType);
    }

    [Fact]
    public void AnAbsentAgentTypeIsStillAccepted()
    {
        var (exitCode, _, _, operations) = Run("agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso", "--description", "d", "--initial-prompt", "p");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastManagedAgentCreateRequest);
    }

    [Theory]
    [InlineData(null, McsAgentType.Cli)]
    [InlineData("", McsAgentType.Cli)]
    [InlineData("cli", McsAgentType.Cli)]
    [InlineData("CLI", McsAgentType.Cli)]
    [InlineData("cli-copilot", McsAgentType.Cli)]
    [InlineData("clicopilot", McsAgentType.Cli)]
    [InlineData(" Classic ", McsAgentType.Classic)]
    public void TryParseAcceptsEverySpellingResolveAccepts(string? spelling, McsAgentType expected)
    {
        Assert.True(CopilotAuthoringModes.TryParse(spelling, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("class")]
    [InlineData("cli copilot")]
    public void TryParseRejectsEverythingElse(string spelling)
    {
        Assert.False(CopilotAuthoringModes.TryParse(spelling, out _));
    }

    [Fact]
    public void TryParseAndResolveAgreeOnWhatIsValid()
    {
        foreach (var spelling in new[] { "cli", "CLI", "classic", "Classic", "cli-copilot", "clicopilot", null, "" })
        {
            Assert.True(CopilotAuthoringModes.TryParse(spelling, out _));
            CopilotAuthoringModes.Resolve(spelling);
        }

        foreach (var spelling in new[] { "Bogus", "class" })
        {
            Assert.False(CopilotAuthoringModes.TryParse(spelling, out _));
            Assert.Throws<UsageException>(() => CopilotAuthoringModes.Resolve(spelling));
        }
    }
}
