using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsModeCommandTests
{
    private readonly FakeOperations _operations = new() { OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = DeploymentAgentTypeSource.DeploymentRecord } };
    private readonly ServiceCollection _services = new();

    [Fact]
    public async Task ListViewAms_GoesToTheAmsListing_WithTheListingOptions()
    {
        var listing = Register<McsAmsAgentListRequest, ManagedAgentListResult>(new ManagedAgentListResult { Scope = "tenant" });

        Assert.Equal((ExitCodes.Success, string.Empty), await RunAsync("agent", "list", "-vw", "ams", "-repid", McsRecords.RepositoryId, "--tenant-wide"));
        Assert.Equal(("ams", McsRecords.RepositoryId, true), (listing.Request!.Options.View, listing.Request.Options.RepositoryId, listing.Request.Options.Tenant));
        Assert.Null(_operations.LastManagedAgentListRequest);
    }

    [Fact]
    public async Task DeployRemote_GoesToTheRemoteDeploy_WithItsOptions()
    {
        var deploy = Register<McsAmsAgentDeployRequest, McsAmsAgentDeployResult>(new McsAmsAgentDeployResult { Status = "accepted", AgentId = McsRecords.AgentId, DisplayName = "Contoso HR", AgentType = AgentTypes.McsAgent, DeploymentId = McsRecords.DevDeploymentId, DeploymentName = "contoso-dev", DeploymentType = DeploymentStages.Dev, TargetEnvironmentId = "env-1", OperationId = "op-deploy" });

        Assert.Equal((ExitCodes.Success, string.Empty), await RunAsync("agent", "deploy", "--remote", "--agent-id", McsRecords.AgentId, "--deployment-id", McsRecords.DevDeploymentId, "--commit-id", McsRecords.Commit, "--publish", "--overwrite", "--async"));
        Assert.Equal(new McsAmsAgentDeployRequest { AgentId = McsRecords.AgentId, DeploymentId = McsRecords.DevDeploymentId, CommitId = McsRecords.Commit, Publish = true, Overwrite = true, Async = true }, deploy.Request);
        Assert.Null(_operations.LastManagedAgentDeployRequest);
    }

    [Fact]
    public async Task Capture_GoesToTheCapture_WithItsOptions()
    {
        var capture = Register<McsAmsAgentCaptureRequest, McsAmsAgentCaptureResult>(new McsAmsAgentCaptureResult { Status = "accepted", AgentId = McsRecords.AgentId, DeploymentId = McsRecords.DevDeploymentId, OperationId = "op-capture" });

        Assert.Equal((ExitCodes.Success, string.Empty), await RunAsync("agent", "capture", "--agent-id", McsRecords.AgentId, "--environment-id", "env-1", "--message", "Fix greeting", "--resolve", "cloud", "--async"));
        Assert.Equal(new McsAmsAgentCaptureRequest { AgentId = McsRecords.AgentId, EnvironmentId = "env-1", Message = "Fix greeting", Resolve = "cloud", Async = true }, capture.Request);
    }

    [Fact]
    public async Task OperationShow_GoesToTheOperationShow_WithItsOptions()
    {
        var show = Register<McsAmsOperationShowRequest, McsAmsOperationShowResult>(new McsAmsOperationShowResult { OperationId = "op-deploy", Kind = "Deploy", Status = "Succeeded", AgentId = McsRecords.AgentId });

        Assert.Equal((ExitCodes.Success, string.Empty), await RunAsync("agent", "operation", "show", "--agent-name", "Contoso HR", "--operation-id", "op-deploy"));
        Assert.Equal(new McsAmsOperationShowRequest { AgentName = "Contoso HR", OperationId = "op-deploy" }, show.Request);
    }

    [Fact]
    public async Task CreateAndInit_TakeTheRepository_AndInitNeedsNoName()
    {
        Assert.Equal((ExitCodes.Success, string.Empty), await RunAsync("agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso HR", "--initial-prompt", "Help.", "--repository-id", McsRecords.RepositoryId));
        Assert.Equal((ExitCodes.Success, string.Empty), await RunAsync("agent", "init", "-repid", McsRecords.RepositoryId, "--non-interactive"));
        Assert.Equal((McsRecords.RepositoryId, McsRecords.RepositoryId, (string?)null, true), (_operations.LastManagedAgentCreateRequest!.RepositoryId, _operations.LastManagedAgentInitRequest!.RepositoryId, _operations.LastManagedAgentInitRequest.Name, _operations.LastManagedAgentInitRequest.NonInteractive));
    }

    private RecordingHandler<TRequest, TResult> Register<TRequest, TResult>(TResult result)
        where TResult : notnull
    {
        var handler = new RecordingHandler<TRequest, TResult>(result);
        _services.AddSingleton<ICommandHandler<TRequest, TResult>>(handler);
        return handler;
    }

    private async Task<(int ExitCode, string Stderr)> RunAsync(params string[] args)
    {
        await using var container = _services.BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await new AgentHostCli(_operations, Program.Handlers(amsMcsAgents: true, container, _operations), new TestFeatureFlags(FeatureName.AmsMcsAgents)).InvokeAsync(args, stdout, stderr, color: false);
        return (exitCode, stderr.ToString());
    }
}
