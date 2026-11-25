using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentDeploymentUpdateCommandHostTests
{
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Revision = "66666666-6666-6666-6666-666666666666";
    private const string TunnelEndpoint = "https://contoso-3978.usw2.devtunnels.ms/api/messages";

    [Fact]
    public void Update_RenamesAnAmsDeploymentById()
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, _) = Run(operations, handlers, "deployment", "update", "--agent-id", Agent, "--deployment-id", Deployment, "--new-name", "dev-renamed", "--environment-id", "env-1", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = handlers.Single<AgentDeploymentAmsUpdateRequest>();
        Assert.Equal((Agent, Deployment, "env-1", "dev-renamed", (string?)null), (request.AgentId, request.DeploymentId, request.EnvironmentId, request.NewName, request.EndpointUrl));
        Assert.Null(operations.LastAgentDeploymentUpdateRequest);
        Assert.Null(operations.LastUpdateDeploymentRequest);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(AgentDeploymentAmsResult.UpdatedStatus, document.RootElement.GetProperty("status").GetString());
        Assert.Equal("dev-renamed", document.RootElement.GetProperty("deployment").GetProperty("deploymentName").GetString());
    }

    [Fact]
    public void Update_ChangesTheDevEndpointOfADeploymentNamedWithoutALocalRecord()
    {
        var handlers = new RecordingAmsHandlers { Deployment = FakeOperations.CreateStubDeploymentRecord() with { Kind = AgentDeploymentAmsRecord.DevEndpointKind, PackageRevisionId = null, RequestedEndpoint = TunnelEndpoint, EndpointChangePending = true } };

        var (exitCode, stdout, _) = Run(new FakeOperations(), handlers, "deployment", "update", "-an", "Contoso Agent", "-dn", "dev-proof", "-eu", TunnelEndpoint);

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = handlers.Single<AgentDeploymentAmsUpdateRequest>();
        Assert.Equal(("Contoso Agent", "dev-proof", TunnelEndpoint, (string?)null, (string?)null), (request.AgentName, request.DeploymentName, request.EndpointUrl, request.NewName, request.EnvironmentId));
        Assert.Contains("Only its record changed.", stdout, StringComparison.Ordinal);
        Assert.Contains($"  requested endpoint: {TunnelEndpoint}, not deployed yet; the next `msagent agent deploy` without --commit-id puts it live.", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("The requested endpoint is not deployed yet", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--package-revision")]
    [InlineData("-pr")]
    public void Update_PackageRevision_SelectsTheAmsRouteAndPointsToAgentDeploy(string option)
    {
        var operations = new FakeOperations { OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.CodeAgent, Source = DeploymentAgentTypeSource.DeploymentRecord } };
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, stderr) = Run(operations, handlers, "deployment", "update", "--agent-id", Agent, "--deployment-id", Deployment, option, Revision);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--package-revision is not an option of `msagent deployment update` for a code agent: use `msagent agent deploy --commit-id`; `msagent agent commit list` shows the commits.", stderr, StringComparison.Ordinal);
        AssertNothingDispatched(operations, handlers);
    }

    [Fact]
    public void Update_AmsRouteWithoutANewNameOrEndpoint_DispatchesNothing()
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, stderr) = Run(operations, handlers, "deployment", "update", "--agent-id", Agent, "--deployment-id", Deployment, "--environment-id", "env-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--new-name or --endpoint-url is required for the AMS route of `msagent deployment update`.", stderr, StringComparison.Ordinal);
        AssertNothingDispatched(operations, handlers);
    }

    [Theory]
    [InlineData("--sku", "S1")]
    [InlineData("--project", ".")]
    [InlineData("--deployment-type", "test")]
    [InlineData("--display-name", "Dev")]
    [InlineData("--description", "text")]
    [InlineData("--resource-group", "rg")]
    public void Update_AmsRoute_RefusesOptionsOnlyTheLocalRoutesTake(string option, string value)
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, stderr) = Run(operations, handlers, "deployment", "update", "--agent-id", Agent, "--deployment-id", Deployment, "--environment-id", "env-1", "--new-name", "renamed", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains($"{option} cannot be combined with the AMS route of `msagent deployment update`.", stderr, StringComparison.Ordinal);
        AssertNothingDispatched(operations, handlers);
    }

    [Fact]
    public void Update_ALocalRecordOfTheDeploymentNamed_KeepsTheLocalRoute()
    {
        var operations = new FakeOperations { OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = DeploymentAgentTypeSource.DeploymentRecord } };
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, _) = Run(operations, handlers, "deployment", "update", "--agent-name", "Support", "--deployment-name", "dev", "--new-name", "renamed");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("renamed", operations.LastAgentDeploymentUpdateRequest!.NewName);
        Assert.Empty(handlers.Requests);
    }

    [Theory]
    [InlineData(Agent, true)]
    [InlineData("55555555-5555-5555-5555-555555555555", false)]
    public void Update_AgentIdAndNameOfALocalBotServiceDeployment_KeepTheLocalRouteOnlyForItsOwnAgent(string agentId, bool local)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = ManagedAgentFixtures.Store();
            store.Add(root, ManagedAgentFixtures.Deployment(deploymentId: Deployment, agentId: Agent, name: "dev") with { AgentType = AgentTypes.CodeAgent });
            var resolver = new DeploymentAgentTypeResolver(new AgentConfigLocator(), store, store);
            var operations = new FakeOperations { OnResolveDeploymentAgentType = query => resolver.Resolve(query with { Project = root }) };
            var handlers = new RecordingAmsHandlers();

            var (exitCode, _, _) = Run(operations, handlers, "deployment", "update", "--agent-id", agentId, "--deployment-name", "dev", "--endpoint-url", TunnelEndpoint);

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Equal(local, operations.LastUpdateDeploymentRequest is not null);
            Assert.Equal(local ? 0 : 1, handlers.Requests.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Update_ReportsAnAmsRefusalWithoutFallingBackToAnotherRoute()
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers { Failure = AgentDeployException.ServiceRejected("The deployment changed since msagent read it, so AMS changed nothing.", ExitCodes.Failure, "deployment-changed", "Run the command again; it reads the current record first.") };

        var (exitCode, stdout, _) = Run(operations, handlers, "deployment", "update", "--agent-id", Agent, "--deployment-id", Deployment, "--new-name", "renamed", "--json");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Single(handlers.Requests);
        Assert.Null(operations.LastAgentDeploymentUpdateRequest);
        Assert.Null(operations.LastUpdateDeploymentRequest);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("deployment-changed", document.RootElement.GetProperty("errorKind").GetString());
    }

    [Theory]
    [InlineData("--package-revision", Revision)]
    [InlineData("--environment-id", "env-1")]
    public void Update_AmsOptionsAreUnavailableWhenTheFeatureIsOff(string option, string value)
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, _) = AmsCommandHost.Run(operations, handlers, new TestFeatureFlags(), "deployment", "update", "--agent-id", Agent, "--deployment-id", Deployment, "--new-name", "renamed", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations, handlers);
    }

    private static void AssertNothingDispatched(FakeOperations operations, RecordingAmsHandlers handlers)
    {
        Assert.Empty(handlers.Requests);
        Assert.Null(operations.LastAgentDeploymentUpdateRequest);
        Assert.Null(operations.LastUpdateDeploymentRequest);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, RecordingAmsHandlers handlers, params string[] args) => AmsCommandHost.Run(operations, handlers, TestFeatureFlags.AllProductSurfaces, args);
}
