using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AgentCopyCommandTests
{
    private const string CloudAgentId = "50505050-1111-2222-3333-444444444444";

    private const string CloudWorkflowId = "a1a1a1a1-1111-2222-3333-444444444444";

    private static (int ExitCode, string Stdout, string Stderr, FakeOperations Operations) Run(params string[] args)
    {
        var operations = new FakeOperations();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(operations).InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString(), operations);
    }

    [Fact]
    public void Copy_OfACloudAgent_BindsEveryOption()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "copy", "--mcs-clone-agent-id", CloudAgentId, "--environment-id", "env-1", "--agent-name", "NB1 Copy", "--schema-name", "cr_nb1copy", "--project", "proj");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = operations.LastManagedAgentCopyRequest!;
        Assert.Equal(CloudAgentId, request.CloneAgentId);
        Assert.Equal("env-1", request.EnvironmentId);
        Assert.Equal("NB1 Copy", request.Name);
        Assert.Equal("cr_nb1copy", request.SchemaName);
        Assert.Equal("proj", request.Project);
    }

    [Fact]
    public void Copy_OfALocalAgent_BindsItsFolderAsTheProject()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "copy", "--project", "NB1", "--agent-name", "NB1 Copy");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = operations.LastManagedAgentCopyRequest!;
        Assert.Equal("NB1", request.Project);
        Assert.Equal("NB1 Copy", request.Name);
        Assert.Null(request.CloneAgentId);
    }

    [Fact]
    public void Copy_BindsTheShortAliases()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "copy", "-p", "NB1", "-an", "NB1 Copy", "-sn", "cr_nb1copy", "-eid", "env-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = operations.LastManagedAgentCopyRequest!;
        Assert.Equal("NB1", request.Project);
        Assert.Equal("NB1 Copy", request.Name);
        Assert.Equal("cr_nb1copy", request.SchemaName);
        Assert.Equal("env-1", request.EnvironmentId);
    }

    [Fact]
    public void Copy_BindsTheCloudAgentAlias()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "copy", "-mcaid", CloudAgentId, "-an", "NB1 Copy");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Equal(CloudAgentId, operations.LastManagedAgentCopyRequest!.CloneAgentId);
    }

    [Theory]
    [InlineData("--mcs-clone-workflow-id")]
    [InlineData("-mcwid")]
    public void Bind_ReadsTheCloudWorkflowId(string option)
    {
        var (exitCode, _, stderr, operations) = Run("agent", "copy", option, CloudWorkflowId, "--agent-name", "Flow Copy", "--environment-id", "env-1", "--project", "proj");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = operations.LastManagedAgentCopyRequest!;
        Assert.Equal(CloudWorkflowId, request.CloneWorkflowId);
        Assert.Null(request.CloneAgentId);
        Assert.Equal("Flow Copy", request.Name);
        Assert.Equal("env-1", request.EnvironmentId);
        Assert.Equal("proj", request.Project);
    }

    [Fact]
    public void Copy_WithoutAProject_LeavesTheSourceToTheCurrentDirectory()
    {
        var (exitCode, _, _, operations) = Run("agent", "copy", "--agent-name", "NB1 Copy");

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = operations.LastManagedAgentCopyRequest!;
        Assert.Null(request.Project);
        Assert.Null(request.CloneAgentId);
    }

    [Theory]
    [InlineData("--share-resources")]
    [InlineData("-sr")]
    [InlineData("--mcs-agent-id")]
    [InlineData("-mcsaid")]
    [InlineData("--new-name")]
    [InlineData("-nn")]
    [InlineData("--from-project")]
    [InlineData("-fp")]
    [InlineData("--agent-id")]
    [InlineData("-aid")]
    public void Copy_WithARemovedOption_IsAUsageError(string option)
    {
        var (exitCode, _, stderr, operations) = Run("agent", "copy", "--agent-name", "NB1 Copy", option);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains($"Unrecognized command or argument '{option}'", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentCopyRequest);
    }

    [Fact]
    public void Fork_IsNoLongerAnAgentCommand()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "fork", "--agent-name", "NB1 Copy");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Unrecognized command or argument 'fork'", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentCopyRequest);
    }

    [Fact]
    public void Copy_ReportsNoDependencyPolicy()
    {
        var (textExitCode, text, _, _) = Run("agent", "copy", "--agent-name", "NB1 Copy");
        var (jsonExitCode, json, _, _) = Run("agent", "copy", "--agent-name", "NB1 Copy", "--json");

        Assert.Equal(ExitCodes.Success, textExitCode);
        Assert.Equal(ExitCodes.Success, jsonExitCode);
        Assert.DoesNotContain("dependencies", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dependencyMode", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sharedLinks", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_WithoutAnAgentName_IsAUsageError()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "copy", "--mcs-clone-agent-id", CloudAgentId);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("requires --agent-name", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentCopyRequest);
    }

    [Fact]
    public void Copy_HelpSaysNothingIsCreatedInTheCloudUntilTheFirstDeploy()
    {
        var (exitCode, stdout, _, _) = Run("agent", "copy", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("the first deploy creates the new agent", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_HelpNamesTheCopyWithAgentNameAndOffersNoRemovedOption()
    {
        var (exitCode, stdout, _, _) = Run("agent", "copy", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("msagent agent copy --agent-name <agent-name> [options]", stdout, StringComparison.Ordinal);
        Assert.Contains("msagent agent copy --mcs-clone-agent-id <mcs-clone-agent-id> --agent-name <agent-name> [options]", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--new-name", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--from-project", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--agent-id", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--mcs-agent-id", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--share-resources", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--dependencies", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--source", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_ListsTheCloudWorkflowRoute()
    {
        var (exitCode, stdout, _, _) = Run("agent", "copy", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("msagent agent copy --mcs-clone-workflow-id <mcs-clone-workflow-id> --agent-name <agent-name> [options]", stdout, StringComparison.Ordinal);
    }
}
