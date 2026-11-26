using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using static Microsoft.Agents.Hosting.Cli.Tests.Ams.AmsBotServiceFixtures;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentDeployServiceTests
{
    [Fact]
    public async Task Deploy_ById_ReadsTheDeploymentOnceAndDeploysItWithTheEntityTagOfThatRead()
    {
        var agents = new FakeAmsAgentTransport();
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var result = await Service(agents, deployments).DeployAsync(ById(), default);

        Assert.Empty(agents.Calls);
        Assert.Equal(["read", "deploy"], deployments.Calls);
        AssertDeployment(Assert.IsType<AmsDeploymentReadTransportRequest>(deployments.Requests[0]));
        var sent = Assert.IsType<AmsDeploymentDeployTransportRequest>(deployments.Requests[1]);
        AssertDeployment(sent);
        Assert.Equal(DeploymentETag, sent.ETag);
        Assert.Null(sent.CommitId);
        Assert.Equal(AgentDeployResult.DeployedStatus, result.Status);
        Assert.Equal(AgentDeployResult.BotKept, result.Bot);
        Assert.Equal(Commit, result.Deployment.CommitId);
        Assert.Null(result.PreviousEndpoint);
    }

    [Fact]
    public async Task Deploy_ByName_UsesTheListEntryAndItsEntityTagWithoutReadingTheDeploymentAgain()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent(OtherAgent, "Other Agent"), CodeAgent()] };
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(OtherDeployment, "test-proof"), DeploymentRecord() with { ETag = "W/\"listed\"" }] };

        await Service(agents, deployments).DeployAsync(new AgentDeployRequest { AgentName = " CONTOSO agent ", DeploymentName = "Dev-Proof" }, default);

        Assert.Equal(["list"], agents.Calls);
        Assert.Equal(["list", "deploy"], deployments.Calls);
        var sent = Assert.IsType<AmsDeploymentDeployTransportRequest>(deployments.Requests[1]);
        AssertDeployment(sent);
        Assert.Equal("W/\"listed\"", sent.ETag);
    }

    [Fact]
    public async Task Deploy_WithACommit_SendsItsCanonicalForm()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById() with { CommitId = $" {Commit.ToUpperInvariant()} " }, default);

        Assert.Equal(Commit, Assert.IsType<AmsDeploymentDeployTransportRequest>(deployments.Requests[1]).CommitId);
        Assert.Contains(result.ToTextLines(), line => line.StartsWith($"  package: commit {Commit}, Package Revision {Revision},", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    public async Task Deploy_WithABlankOrMalformedCommit_IsRefusedBeforeAnyCall(string commitId)
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var exception = await Assert.ThrowsAsync<UsageException>(() => Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById() with { CommitId = commitId }, default));

        Assert.Equal("--commit-id must be the 40 hexadecimal characters of a commit. `msagent agent commit list` shows the agent's commits.", exception.Message);
        Assert.Empty(deployments.Calls);
    }

    [Fact]
    public async Task Deploy_ProdWithACommit_IsRefusedAfterTheReadAndBeforeTheDeploy()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(deploymentType: "Prod")] };

        var exception = await Assert.ThrowsAsync<UsageException>(() => Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById() with { CommitId = Commit }, default));

        Assert.StartsWith("--commit-id cannot be used with a Prod deployment", exception.Message, StringComparison.Ordinal);
        Assert.Equal(["read"], deployments.Calls);
    }

    [Fact]
    public async Task Deploy_ProdWithoutACommit_LetsAmsDeployTheNewestUpload()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(deploymentType: "Prod")] };

        await Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById(), default);

        Assert.Equal(["read", "deploy"], deployments.Calls);
        Assert.Null(Assert.IsType<AmsDeploymentDeployTransportRequest>(deployments.Requests[1]).CommitId);
    }

    [Theory]
    [InlineData(AmsDeploymentResult.DeletingState, false)]
    [InlineData(AmsDeploymentResult.DeployedState, true)]
    public async Task Deploy_ADeploymentBeingDeleted_IsRefusedBeforeTheDeploy(string state, bool isDeleting)
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: state) with { IsDeleting = isDeleting }] };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById(), default));

        Assert.Equal("deployment-deleting", exception.ErrorKind);
        Assert.Equal(["read"], deployments.Calls);
    }

    [Fact]
    public async Task Deploy_ANeverDeployedRecord_ReportsTheBotThisDeployCreated()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: AmsDeploymentResult.NotDeployedState)] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById(), default);

        Assert.Equal(AgentDeployResult.BotCreated, result.Bot);
        Assert.Equal([$"Deployment dev-proof ({Deployment}) deployed.", .. result.Deployment.DetailLines(), "Azure Bot Service: created ams-bot-1."], result.ToTextLines());
    }

    [Fact]
    public async Task Deploy_WhenAmsBindsNoBot_SaysSo()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()], OnDeploy = input => DeploymentRecord() with { BotService = null } };

        var result = await Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById(), default);

        Assert.Equal(AgentDeployResult.BotNone, result.Bot);
        Assert.Contains("Azure Bot Service: none; AMS bound no bot to this deployment.", result.ToTextLines());
    }

    [Fact]
    public async Task Deploy_ADevEndpointDeployment_PutsTheRequestedEndpointLiveAndRecordsTheOneItReplaced()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DevEndpointRecord(RuntimeEndpoint, TunnelEndpoint)], OnDeploy = input => DevEndpointRecord(TunnelEndpoint, TunnelEndpoint) };

        var result = await Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById(), default);

        Assert.Null(Assert.IsType<AmsDeploymentDeployTransportRequest>(deployments.Requests[1]).CommitId);
        Assert.Equal(RuntimeEndpoint, result.PreviousEndpoint);
        Assert.Equal(AgentDeploymentAmsRecord.DevEndpointKind, result.Deployment.Kind);
        Assert.False(result.KindSwitched);
        Assert.Contains($"  endpoint: {TunnelEndpoint}", result.ToTextLines());
        Assert.DoesNotContain(result.ToTextLines(), line => line.StartsWith("Live:", StringComparison.Ordinal) || line.StartsWith("Switched", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, $"Switched from the dev endpoint to commit {Commit}; the bot now points at {RuntimeEndpoint}.")]
    [InlineData(false, $"Switched from the dev endpoint to commit {Commit}; the deployment now answers at {RuntimeEndpoint}.")]
    public async Task Deploy_ADevEndpointDeploymentWithACommit_DeploysThatUploadAndSaysTheDeploymentSwitchedToIt(bool hasBot, string switchLine)
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DevEndpointRecord(TunnelEndpoint, TunnelEndpoint)], OnDeploy = input => DeploymentRecord() with { BotService = hasBot ? Bot() : null } };

        var result = await Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById() with { CommitId = Commit }, default);

        Assert.Equal(["read", "deploy"], deployments.Calls);
        Assert.Equal(Commit, Assert.IsType<AmsDeploymentDeployTransportRequest>(deployments.Requests[1]).CommitId);
        Assert.True(result.KindSwitched);
        Assert.Equal(AgentDeploymentAmsRecord.PackageKind, result.Deployment.Kind);
        Assert.Equal(TunnelEndpoint, result.PreviousEndpoint);
        var text = result.ToTextLines();
        Assert.Contains(text, line => line.StartsWith($"  package: commit {Commit}, Package Revision {Revision},", StringComparison.Ordinal));
        Assert.Contains(switchLine, text);
    }

    [Fact]
    public async Task Deploy_APackageDeploymentWithARequestedEndpointAndNoCommit_SaysTheDeploymentSwitchedToThatEndpoint()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord() with { RequestedEndpoint = TunnelEndpoint }], OnDeploy = input => DevEndpointRecord(TunnelEndpoint, TunnelEndpoint) };

        var result = await Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById(), default);

        Assert.Null(Assert.IsType<AmsDeploymentDeployTransportRequest>(deployments.Requests[1]).CommitId);
        Assert.True(result.KindSwitched);
        Assert.Equal(AgentDeploymentAmsRecord.DevEndpointKind, result.Deployment.Kind);
        Assert.Equal(AgentDeployResult.BotKept, result.Bot);
        var text = result.ToTextLines();
        Assert.Contains($"  endpoint: {TunnelEndpoint}", text);
        Assert.Contains($"Switched from the package to the dev endpoint {TunnelEndpoint}.", text);
        Assert.DoesNotContain(text, line => line.StartsWith("  package:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_APackageDeploymentWithARequestedEndpointAndACommit_DeploysThatUploadWithoutASwitch()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord() with { RequestedEndpoint = TunnelEndpoint }] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById() with { CommitId = Commit }, default);

        Assert.Equal(Commit, Assert.IsType<AmsDeploymentDeployTransportRequest>(deployments.Requests[1]).CommitId);
        Assert.False(result.KindSwitched);
        Assert.False(result.Deployment.EndpointChangePending);
        Assert.DoesNotContain(result.ToTextLines(), line => line.StartsWith("Switched", StringComparison.Ordinal) || line.StartsWith("  requested endpoint:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deploy_ADevEndpointDeploymentWithTheSameEndpoint_NamesNoPreviousEndpoint()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DevEndpointRecord(TunnelEndpoint, TunnelEndpoint)], OnDeploy = input => DevEndpointRecord(TunnelEndpoint, TunnelEndpoint) };

        var result = await Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById(), default);

        Assert.Null(result.PreviousEndpoint);
        Assert.Contains($"  endpoint: {TunnelEndpoint}", result.ToTextLines());
    }

    [Fact]
    public async Task Deploy_AnUnknownDeploymentName_PointsToDeploymentCreateAndCreatesNothing()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var exception = await Assert.ThrowsAsync<ManagedAgentException>(() => Service(new FakeAmsAgentTransport(), deployments).DeployAsync(new AgentDeployRequest { AgentId = Agent, DeploymentName = "prod" }, default));

        Assert.Equal("deployment-not-found", exception.ErrorKind);
        Assert.Contains("msagent deployment create", exception.Remediation, StringComparison.Ordinal);
        Assert.Equal(["list"], deployments.Calls);
    }

    [Fact]
    public async Task Deploy_WhenTheDeployRouteIsUnavailable_FailsWithoutAnyOtherCall()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()], OnDeploy = input => throw AgentDeployException.ServiceRejected("unavailable", ExitCodes.Failure, AmsRefusals.LifecycleUnavailableKind, "ask") };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById(), default));

        Assert.Equal(AmsRefusals.LifecycleUnavailableKind, exception.ErrorKind);
        Assert.Equal(["read", "deploy"], deployments.Calls);
    }

    [Theory]
    [InlineData("deploymentId")]
    [InlineData("botService.msaAppId")]
    public async Task Deploy_AResponseForAnotherDeploymentOrBotApplication_IsAMismatch(string field)
    {
        var deployments = new FakeAmsDeploymentTransport
        {
            Deployments = [DeploymentRecord()],
            OnDeploy = input => field == "deploymentId" ? DeploymentRecord(OtherDeployment) : DeploymentRecord() with { BotService = Bot() with { MsaAppId = OtherAgent } },
        };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(new FakeAmsAgentTransport(), deployments).DeployAsync(ById(), default));

        Assert.Equal("ams-response-identity-mismatch", exception.ErrorKind);
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_WithEndpointOverrides_DeploysThroughTheControlPlaneOverride()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };
        var service = new AgentDeployService(Resolver(new FakeAmsAgentTransport(), deployments, new AmsEndpointProfile { ControlPlaneEndpoint = "https://ams-alias.example/" }), deployments);

        await service.DeployAsync(ById(), default);

        AmsTargetFixtures.AssertTarget(HomeControlPlane with { Endpoint = new Uri("https://ams-alias.example/"), RouteForm = AmsRouteForm.Direct }, Assert.IsType<AmsDeploymentDeployTransportRequest>(deployments.Requests[1]).ServiceTarget);
    }

    [Fact]
    public void Constructor_RequiresEveryDependency()
    {
        Assert.Throws<ArgumentNullException>(() => new AgentDeployService(null!, new FakeAmsDeploymentTransport()));
        Assert.Throws<ArgumentNullException>(() => new AgentDeployService(Resolver(), null!));
    }

    private static AgentDeployRequest ById() => new() { AgentId = Agent, DeploymentId = Deployment };

    private static AgentDeployService Service(FakeAmsAgentTransport agents, FakeAmsDeploymentTransport deployments) => new(Resolver(agents, deployments), deployments);
}
