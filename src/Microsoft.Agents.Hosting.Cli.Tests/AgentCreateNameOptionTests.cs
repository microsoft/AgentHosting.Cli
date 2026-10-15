using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AgentCreateNameOptionTests
{
    private static (int ExitCode, string Stdout, string Stderr, FakeOperations Operations) Run(params string[] args)
    {
        var operations = new FakeOperations();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(operations).InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString(), operations);
    }

    [Theory]
    [InlineData("--agent-name")]
    [InlineData("-an")]
    [InlineData("--name")]
    [InlineData("-n")]
    public void EverySpellingBindsTheSameName(string spelling)
    {
        var (exitCode, _, stderr, operations) = Run("agent", "create", "--agent-type", "MCSWorkflow", spelling, "WF Hook 2");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("WF Hook 2", operations.LastManagedAgentCreateRequest!.Name);
    }

    [Fact]
    public void TheHelpAdvertisesTheAgentNameSpelling()
    {
        var (exitCode, stdout, _, _) = Run("agent", "create", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("--agent-name", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMissingNameFailureNamesTheAgentNameSpelling()
    {
        var operations = new FakeOperations();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await new AgentHostCli(operations, featureFlags: TestFeatureFlags.AllProductSurfaces)
            .InvokeAsync(["agent", "create", "--language", "dotnet", "--language-version", "10"], stdout, stderr, color: false);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--agent-name is required for a CodeAgent.", stderr.ToString(), StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentCreateRequest);
    }

    [Fact]
    public void InitKeepsItsOwnAgentNameOption()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "init", "--agent-name", "devwf", "--agent-type", "MCSWorkflow");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("devwf", operations.LastManagedAgentInitRequest!.Name);
    }

    [Fact]
    public void InitStillRefusesTheBareNameSpelling()
    {
        var (exitCode, _, _, operations) = Run("agent", "init", "--name", "devwf");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastManagedAgentInitRequest);
    }
}
