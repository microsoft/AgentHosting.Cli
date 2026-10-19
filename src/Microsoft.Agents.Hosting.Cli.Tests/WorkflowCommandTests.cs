using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class WorkflowCommandTests
{
    private static (int ExitCode, string Stdout, string Stderr, FakeOperations Operations) Run(params string[] args)
    {
        var operations = new FakeOperations();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(operations).InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString(), operations);
    }

    [Fact]
    public void CreateRoutesAWorkflowScaffold()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "create", "--agent-type", "MCSWorkflow", "--name", "WF Hook 2");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Equal(AgentTypes.McsWorkflow, operations.LastManagedAgentCreateRequest!.AgentType);
        Assert.Equal("WF Hook 2", operations.LastManagedAgentCreateRequest.Name);
    }

    [Fact]
    public void CreateCarriesTheCloneSelectors()
    {
        var (_, _, _, operations) = Run(
            "agent", "create",
            "--agent-type", "MCSWorkflow",
            "--mcs-clone-workflow-id", "11111111-2222-3333-4444-555555555555",
            "--mcs-clone-environment-id", "env-2",
            "--project", "proj");

        Assert.Equal("11111111-2222-3333-4444-555555555555", operations.LastManagedAgentCreateRequest!.CloneWorkflowId);
        Assert.Equal("env-2", operations.LastManagedAgentCreateRequest.CloneEnvironmentId);
        Assert.Equal("proj", operations.LastManagedAgentCreateRequest.Project);
    }

    [Fact]
    public void TheCloneOptionHasAShortAlias()
    {
        var (_, _, _, operations) = Run("agent", "create", "--agent-type", "MCSWorkflow", "-mcwid", "11111111-2222-3333-4444-555555555555");

        Assert.Equal("11111111-2222-3333-4444-555555555555", operations.LastManagedAgentCreateRequest!.CloneWorkflowId);
    }

    [Theory]
    [InlineData("--language", "dotnet")]
    [InlineData("--language-version", "10")]
    public void CreateRejectsCodeAgentOptionsOnTheWorkflowRoute(string option, string value)
    {
        var (exitCode, _, _, operations) = Run("agent", "create", "--agent-type", "MCSWorkflow", "--name", "WF", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastManagedAgentCreateRequest);
    }

    [Theory]
    [InlineData("--schema-name", "con_flow")]
    [InlineData("--initial-prompt", "hello")]
    [InlineData("--mcs-clone-agent-id", "agent-1")]
    public void CreateRejectsAgentOnlyOptionsOnTheWorkflowRoute(string option, string value)
    {
        var (exitCode, _, stderr, operations) = Run("agent", "create", "--agent-type", "MCSWorkflow", "--name", "WF", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("MCSWorkflow was supplied", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentCreateRequest);
    }

    [Fact]
    public void CreateRejectsTheWorkflowCloneOptionOnTheAgentRoute()
    {
        var (exitCode, _, stderr, _) = Run(
            "agent", "create", "--agent-type", "MCSAgent", "--name", "A", "--description", "d", "--initial-prompt", "p",
            "--mcs-clone-workflow-id", "11111111-2222-3333-4444-555555555555");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("MCSWorkflow only", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitWorkflowTypeWinsOverTheCloneEnvironment()
    {
        var (exitCode, _, _, operations) = Run(
            "agent", "create",
            "--agent-type", "MCSWorkflow",
            "--mcs-clone-workflow-id", "11111111-2222-3333-4444-555555555555",
            "--mcs-clone-environment-id", "env-2");

        Assert.Equal(0, exitCode);
        Assert.Equal(AgentTypes.McsWorkflow, operations.LastManagedAgentCreateRequest!.AgentType);
    }

    [Fact]
    public void TheCloneEnvironmentWithoutAnAgentTypeAsksForTheWorkflowTypeWhenCloningAWorkflow()
    {
        var (exitCode, _, stderr, operations) = Run(
            "agent", "create",
            "--mcs-clone-workflow-id", "11111111-2222-3333-4444-555555555555",
            "--mcs-clone-environment-id", "env-2");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Add --agent-type MCSWorkflow", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentCreateRequest);
    }

    [Fact]
    public void AnUnknownAgentTypeNamesTheWorkflowType()
    {
        var (exitCode, _, stderr, _) = Run("agent", "create", "--agent-type", "MCSFlow", "--name", "WF");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("MCSWorkflow", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void InitRoutesAWorkflow()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "init", "--agent-name", "devwf", "--agent-type", "MCSWorkflow", "--mcs-workflow-source", "wfdir");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("devwf", operations.LastManagedAgentInitRequest!.Name);
        Assert.Equal("MCSWorkflow", operations.LastManagedAgentInitRequest.AgentType);
        Assert.Equal("wfdir", operations.LastManagedAgentInitRequest.McsWorkflowSource);
    }

    [Fact]
    public void InitStillCarriesTheAgentSource()
    {
        var (_, _, _, operations) = Run("agent", "init", "--agent-name", "a", "--mcs-agent-source", "agentdir");

        Assert.Equal("agentdir", operations.LastManagedAgentInitRequest!.McsAgentSource);
        Assert.Null(operations.LastManagedAgentInitRequest.McsWorkflowSource);
    }

    [Fact]
    public void PullCarriesTheDeploymentSelectors()
    {
        var (exitCode, _, _, operations) = Run("agent", "pull", "-aid", "wf-1", "-dn", "devwf");

        Assert.Equal(0, exitCode);
        Assert.Equal("wf-1", operations.LastManagedAgentPullRequest!.AgentId);
        Assert.Equal("devwf", operations.LastManagedAgentPullRequest.DeploymentName);
    }

    [Fact]
    public void PullStillWorksWithoutADeploymentSelector()
    {
        var (exitCode, _, _, operations) = Run("agent", "pull", "-aid", "agent-1");

        Assert.Equal(0, exitCode);
        Assert.Null(operations.LastManagedAgentPullRequest!.DeploymentName);
    }

    [Fact]
    public void DeployStillCarriesTheWorkflowSelectors()
    {
        var (exitCode, _, _, operations) = Run("agent", "deploy", "-aid", "wf-1", "-dn", "devwf", "--publish");

        Assert.Equal(0, exitCode);
        Assert.Equal("wf-1", operations.LastManagedAgentDeployRequest!.AgentId);
        Assert.Equal("devwf", operations.LastManagedAgentDeployRequest.DeploymentName);
        Assert.True(operations.LastManagedAgentDeployRequest.Publish);
    }

    [Fact]
    public void ListRoutesTheWorkflowType()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "list", "--agent-type", "MCSWorkflow");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("MCSWorkflow", operations.LastManagedAgentListRequest!.AgentType);
    }

    [Fact]
    public void ListCarriesTheFilterAndLimit()
    {
        var (exitCode, _, _, operations) = Run("agent", "list", "--agent-type", "MCSWorkflow", "-flt", "Hook", "-lim", "5", "-eid", "env-2");

        Assert.Equal(0, exitCode);
        Assert.Equal("Hook", operations.LastManagedAgentListRequest!.Filter);
        Assert.Equal(5, operations.LastManagedAgentListRequest.Limit);
        Assert.Equal("env-2", operations.LastManagedAgentListRequest.EnvironmentId);
    }

    [Fact]
    public void ListStillDefaultsToAgents()
    {
        var (exitCode, _, _, operations) = Run("agent", "list");

        Assert.Equal(0, exitCode);
        Assert.Null(operations.LastManagedAgentListRequest!.AgentType);
        Assert.Null(operations.LastManagedAgentListRequest.Filter);
    }

    [Fact]
    public void ListRejectsAnAgentTypeItCannotList()
    {
        var (exitCode, _, stderr, operations) = Run("agent", "list", "--agent-type", "CodeAgent");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("cannot be listed", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentListRequest);
    }

    [Fact]
    public void ListRejectsAnUnknownAgentType()
    {
        var (exitCode, _, _, operations) = Run("agent", "list", "--agent-type", "MCSFlow");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastManagedAgentListRequest);
    }

    private static (int ExitCode, string Stderr, FakeOperations Operations) RunRouted(string agentType, params string[] args)
    {
        var operations = new FakeOperations
        {
            OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = agentType, Source = DeploymentAgentTypeSource.AgentRecord },
        };

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(operations).InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stderr.ToString(), operations);
    }

    [Fact]
    public void DeploymentCreateRoutesAWorkflowToTheManagedAgentBackend()
    {
        var (exitCode, stderr, operations) = RunRouted(
            AgentTypes.McsWorkflow,
            "deployment", "create", "-dn", "devwf", "-dt", "dev", "-eid", "env-1", "-aid", "wf-1");

        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("wf-1", operations.LastAgentDeploymentCreateRequest!.AgentId);
        Assert.Equal("devwf", operations.LastAgentDeploymentCreateRequest.DeploymentName);
        Assert.Equal("env-1", operations.LastAgentDeploymentCreateRequest.EnvironmentId);
    }

    [Fact]
    public void DeploymentCreateDoesNotDemandAzureBotServiceForAWorkflow()
    {
        var (_, stderr, _) = RunRouted(
            AgentTypes.McsWorkflow,
            "deployment", "create", "-dn", "devwf", "-dt", "dev", "-eid", "env-1", "-aid", "wf-1");

        Assert.DoesNotContain("AzureBotServiceFeatures", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentUpdateRoutesAWorkflowToTheManagedAgentBackend()
    {
        var (exitCode, _, operations) = RunRouted(
            AgentTypes.McsWorkflow,
            "deployment", "update", "-aid", "wf-1", "-dn", "devwf", "--new-name", "prodwf");

        Assert.Equal(0, exitCode);
        Assert.Equal("wf-1", operations.LastAgentDeploymentUpdateRequest!.AgentId);
    }

    [Fact]
    public void DeploymentOAuthRefusesAWorkflow()
    {
        var (exitCode, stderr, _) = RunRouted(
            AgentTypes.McsWorkflow,
            "deployment", "oauth", "list", "-aid", "wf-1", "-dn", "devwf");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("CodeAgent deployment", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentCreateStillRoutesACodeAgentToTheAzureBackend()
    {
        var (exitCode, stderr, operations) = RunRouted(
            AgentTypes.CodeAgent,
            "deployment", "create", "-dn", "devbot", "-dt", "dev", "-aid", "agent-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("AzureBotServiceFeatures", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentCreateRequest);
    }
}
