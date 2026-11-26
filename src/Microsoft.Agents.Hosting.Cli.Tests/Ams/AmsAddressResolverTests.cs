using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using static Microsoft.Agents.Hosting.Cli.Tests.Ams.AmsBotServiceFixtures;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsAddressResolverTests
{
    [Fact]
    public async Task Agent_ById_IsCanonicalAndResolvedWithoutACall()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };

        var address = await Resolver(agents, new FakeAmsDeploymentTransport()).ResolveAgentAsync(new AgentAmsShowRequest { AgentId = $" {Agent.ToUpperInvariant()} ", AgentName = "Ignored" }, "agent show", default);

        Assert.Equal(Agent, address.AgentId);
        Assert.Equal(HomeEnvironment, address.Home.Environment.EnvironmentId);
        Assert.Empty(agents.Calls);
    }

    [Theory]
    [InlineData(" Contoso Agent ", "contoso agent")]
    [InlineData("CONTOSO AGENT", " Contoso Agent")]
    [InlineData("λογοσ", "ΛΟΓΟΣ")]
    public async Task Agent_ByName_MatchesAfterTrimmingAndLoweringBothSides(string stored, string requested)
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent(OtherAgent, "Other"), CodeAgent(displayName: stored)] };

        var address = await Resolver(agents, new FakeAmsDeploymentTransport()).ResolveAgentAsync(new AgentAmsShowRequest { AgentName = requested }, "agent show", default);

        Assert.Equal(Agent, address.AgentId);
        Assert.Equal(["list"], agents.Calls);
        AmsTargetFixtures.AssertTarget(HomeDataPlane, Assert.IsType<AmsServiceTarget>(Assert.Single(agents.Requests)));
    }

    [Fact]
    public async Task Agent_ByName_ComparesLoweredNamesRatherThanIgnoringCase()
    {
        Assert.Equal(0, string.Compare("ΛΟΓΟΣ", "λογος", StringComparison.OrdinalIgnoreCase));
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent(displayName: "ΛΟΓΟΣ")] };

        var exception = await Assert.ThrowsAsync<ManagedAgentException>(() => Resolver(agents, new FakeAmsDeploymentTransport()).ResolveAgentAsync(new AgentAmsShowRequest { AgentName = "λογος" }, "agent show", default));

        Assert.Equal("agent-not-found", exception.ErrorKind);
    }

    [Fact]
    public async Task Agent_ByName_ConsidersOnlyCodeAgents()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent(OtherAgent, "Contoso Agent", "CopilotStudio"), CodeAgent()] };

        var address = await Resolver(agents, new FakeAmsDeploymentTransport()).ResolveAgentAsync(new AgentAmsShowRequest { AgentName = "Contoso Agent" }, "agent show", default);

        Assert.Equal(Agent, address.AgentId);
    }

    [Fact]
    public async Task Agent_ByName_NoneIsNotFoundInTheHomeEnvironment()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent(displayName: "Contoso")] };

        var exception = await Assert.ThrowsAsync<ManagedAgentException>(() => Resolver(agents, new FakeAmsDeploymentTransport()).ResolveAgentAsync(new AgentAmsShowRequest { AgentName = "Contoso Agent" }, "agent show", default));

        Assert.Equal("agent-not-found", exception.ErrorKind);
        Assert.Equal(ErrorCodes.ManagedAgent.AgentNotFound, exception.ErrorCode);
        Assert.Equal(ExitCodes.Usage, exception.ExitCode);
        Assert.Equal($"No code agent named 'Contoso Agent' is in environment {HomeEnvironment}.", exception.Message);
    }

    [Fact]
    public async Task Agent_ByName_ManyIsAmbiguousListingTheirIds()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent(), CodeAgent(OtherAgent, "contoso agent ")] };

        var exception = await Assert.ThrowsAsync<ManagedAgentException>(() => Resolver(agents, new FakeAmsDeploymentTransport()).ResolveAgentAsync(new AgentAmsShowRequest { AgentName = "Contoso Agent" }, "agent show", default));

        Assert.Equal("agent-ambiguous", exception.ErrorKind);
        Assert.Equal(ErrorCodes.ManagedAgent.AgentAmbiguous, exception.ErrorCode);
        Assert.Equal($"More than one code agent is named 'Contoso Agent': {Agent}, {OtherAgent}.", exception.Message);
        Assert.Contains("--agent-id", exception.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_ByName_FindsAnAgentOnALaterPageOfOneList()
    {
        var handler = new CapturingPackageHandler(request => FakeHttpResponse.Json(HttpStatusCode.OK, request.RequestUri!.Query.Contains("skiptoken", StringComparison.Ordinal)
            ? $$"""{ "value": [{ "id": "{{Agent}}", "tenantId": "{{EnvironmentFixtures.ContosoTenant}}", "environmentId": "{{HomeEnvironment}}", "displayName": "Contoso Agent", "agentType": "CodeAgent" }] }"""
            : $$"""{ "value": [{ "id": "{{OtherAgent}}", "tenantId": "{{EnvironmentFixtures.ContosoTenant}}", "environmentId": "{{HomeEnvironment}}", "displayName": "Other", "agentType": "CodeAgent" }], "nextLink": "https://env-1.api.powerplatform.com/agenthosting/agents?api-version=1&$skiptoken=2" }"""));
        var agents = new AmsAgentAdapter(new HttpClient(handler), new FakePpapiTokenProvider { Scopes = PowerPlatformApiPermissions.AppsRead });

        var address = await Resolver(agents, new PendingAmsDeploymentTransport()).ResolveAgentAsync(new AgentAmsShowRequest { AgentName = "contoso agent" }, "agent show", default);

        Assert.Equal(Agent, address.AgentId);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(null, " ", "--agent-id or --agent-name is required for `msagent agent show`.")]
    [InlineData("not-a-guid", null, "--agent-id must be a non-empty GUID.")]
    [InlineData("00000000-0000-0000-0000-000000000000", "Contoso Agent", "--agent-id must be a non-empty GUID.")]
    public async Task Agent_InvalidSelectorsAreRefusedBeforeAnyCall(string? agentId, string? agentName, string message)
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };

        var exception = await Assert.ThrowsAsync<UsageException>(() => Resolver(agents, new FakeAmsDeploymentTransport()).ResolveAgentAsync(new AgentAmsShowRequest { AgentId = agentId, AgentName = agentName }, "agent show", default));

        Assert.Equal(message, exception.Message);
        Assert.Empty(agents.Calls);
    }

    [Fact]
    public async Task Home_IsTheActiveEnvironmentUnlessOneIsNamed()
    {
        var resolver = Resolver();

        var active = await resolver.ResolveAgentAsync(new AgentAmsShowRequest { AgentId = Agent }, "agent show", default);
        var named = await resolver.ResolveAgentAsync(new AgentAmsShowRequest { AgentId = Agent, EnvironmentId = TargetEnvironment }, "agent show", default);

        Assert.Equal(HomeEnvironment, active.Home.Environment.EnvironmentId);
        AmsTargetFixtures.AssertTarget(HomeControlPlane, active.Home.ControlPlane);
        Assert.Equal(TargetEnvironment, named.Home.Environment.EnvironmentId);
        Assert.Equal("https://target.api.powerplatform.com/", named.Home.DataPlane.Endpoint.AbsoluteUri);
        Assert.Equal(TargetEnvironment, named.Home.ControlPlane.EnvironmentId);
    }

    [Fact]
    public async Task Deployment_ById_IsResolvedWithoutACall()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var address = await Resolver(agents, deployments).ResolveDeploymentAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentId = Deployment.ToUpperInvariant(), DeploymentName = "ignored" }, "deployment show", default);

        AssertDeployment(address.Reference);
        Assert.Null(address.Listed);
        Assert.Empty(agents.Calls);
        Assert.Empty(deployments.Calls);
    }

    [Fact]
    public async Task Deployment_ByName_ReadsOneListAndKeepsTheEntry()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(OtherDeployment, "test"), DeploymentRecord(deploymentName: " Dev-Proof ")] };

        var address = await Resolver(new FakeAmsAgentTransport(), deployments).ResolveDeploymentAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentName = "dev-proof" }, "deployment show", default);

        AssertDeployment(address.Reference);
        Assert.Equal(" Dev-Proof ", address.Listed!.DeploymentName);
        Assert.Equal(["list"], deployments.Calls);
        var listed = Assert.IsType<AmsAgentReadTransportRequest>(Assert.Single(deployments.Requests));
        AmsTargetFixtures.AssertTarget(HomeControlPlane, listed.ServiceTarget);
        Assert.Equal(Agent, listed.AgentId);
    }

    [Fact]
    public async Task Deployment_ByName_NoneIsNotFoundAndPointsToDeploymentCreate()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var exception = await Assert.ThrowsAsync<ManagedAgentException>(() => Resolver(new FakeAmsAgentTransport(), deployments).ResolveDeploymentAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentName = "prod" }, "deployment show", default));

        Assert.Equal("deployment-not-found", exception.ErrorKind);
        Assert.Equal(ErrorCodes.Deployment.SlotNotFound, exception.ErrorCode);
        Assert.Equal($"Agent {Agent} has no deployment named 'prod'.", exception.Message);
        Assert.Contains("msagent deployment create", exception.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deployment_ByName_ManyIsAmbiguousListingTheirIds()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(), DeploymentRecord(OtherDeployment, "DEV-PROOF")] };

        var exception = await Assert.ThrowsAsync<ManagedAgentException>(() => Resolver(new FakeAmsAgentTransport(), deployments).ResolveDeploymentAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentName = "dev-proof" }, "deployment show", default));

        Assert.Equal("deployment-ambiguous", exception.ErrorKind);
        Assert.Equal(ErrorCodes.Deployment.SlotAmbiguous, exception.ErrorCode);
        Assert.Equal($"More than one deployment is named 'dev-proof': {Deployment}, {OtherDeployment}.", exception.Message);
    }

    [Fact]
    public async Task Deployment_ByNameOfAnAgentByName_ReadsEachListOnce()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        await Resolver(agents, deployments).ResolveDeploymentAsync(new AgentDeploymentAmsShowRequest { AgentName = "Contoso Agent", DeploymentName = "dev-proof" }, "deployment show", default);

        Assert.Equal(["list"], agents.Calls);
        Assert.Equal(["list"], deployments.Calls);
    }

    [Theory]
    [InlineData("Contoso Agent", null, null, "--deployment-id or --deployment-name is required for `msagent deployment show`.")]
    [InlineData(null, null, "dev", "--agent-id or --agent-name is required for `msagent deployment show`.")]
    [InlineData("Contoso Agent", "not-a-guid", null, "--deployment-id must be a non-empty GUID.")]
    public async Task Deployment_InvalidSelectorsAreRefusedBeforeAnyCall(string? agentName, string? deploymentId, string? deploymentName, string message)
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var exception = await Assert.ThrowsAsync<UsageException>(() => Resolver(agents, deployments).ResolveDeploymentAsync(new AgentDeploymentAmsShowRequest { AgentName = agentName, DeploymentId = deploymentId, DeploymentName = deploymentName }, "deployment show", default));

        Assert.Equal(message, exception.Message);
        Assert.Empty(agents.Calls);
        Assert.Empty(deployments.Calls);
    }

    [Fact]
    public async Task ListDeployments_RefusesAnEntryOfAnotherAgent()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord() with { AgentId = OtherAgent }] };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Resolver(new FakeAmsAgentTransport(), deployments).ResolveDeploymentAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentName = "dev-proof" }, "deployment show", default));

        Assert.Equal("ams-response-identity-mismatch", exception.ErrorKind);
        Assert.Contains("agentId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryListDeployments_IsNullOnlyWhenAmsDoesNotListDeploymentsYet()
    {
        var unavailable = new FakeAmsDeploymentTransport { ListFailure = AgentDeployException.ServiceRejected("unavailable", ExitCodes.Failure, AmsRefusals.LifecycleUnavailableKind, "ask") };
        var failing = new FakeAmsDeploymentTransport { ListFailure = AgentDeployException.ServiceRejected("failed", ExitCodes.Failure, "request-failed", "retry") };
        var resolver = Resolver(new FakeAmsAgentTransport(), unavailable);
        var agent = await resolver.ResolveAgentAsync(new AgentAmsShowRequest { AgentId = Agent }, "agent show", default);

        Assert.Null(await resolver.TryListDeploymentsAsync(agent, default));
        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Resolver(new FakeAmsAgentTransport(), failing).TryListDeploymentsAsync(agent, default));
        Assert.Equal("request-failed", exception.ErrorKind);
    }

    [Fact]
    public async Task BotDeployment_ListedAsNeverDeployed_IsRefusedWithTheDeployStep()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: AmsDeploymentResult.NotDeployedState)] };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Resolver(new FakeAmsAgentTransport(), deployments).ResolveBotDeploymentAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentName = "dev-proof" }, "deployment channel list", default));

        Assert.Equal("deployment-not-deployed", exception.ErrorKind);
        Assert.Equal(ExitCodes.Failure, exception.ExitCode);
        Assert.Contains("msagent agent deploy", exception.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BotDeployment_ById_IsResolvedWithoutACall()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: AmsDeploymentResult.NotDeployedState)] };

        AssertDeployment(await Resolver(new FakeAmsAgentTransport(), deployments).ResolveBotDeploymentAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentId = Deployment }, "deployment channel list", default));

        Assert.Empty(deployments.Calls);
    }

    [Fact]
    public async Task TargetEnvironment_NotAvailableToTheProfile_IsRefused()
    {
        var resolver = Resolver();
        var agent = await resolver.ResolveAgentAsync(new AgentAmsShowRequest { AgentId = Agent }, "deployment create", default);

        await Assert.ThrowsAnyAsync<AgentHostingException>(() => resolver.ResolveTargetEnvironmentAsync(agent.Home, UnavailableEnvironment, default));
        Assert.Equal(TargetEnvironment, (await resolver.ResolveTargetEnvironmentAsync(agent.Home, TargetEnvironment, default)).EnvironmentId);
    }

    [Fact]
    public void Constructor_RequiresEveryDependency()
    {
        var targets = AmsTargetFixtures.Targets();
        var environments = Environments();
        var agents = new FakeAmsAgentTransport();
        var deployments = new FakeAmsDeploymentTransport();

        Assert.Throws<ArgumentNullException>(() => new AmsAddressResolver(null!, environments, agents, deployments));
        Assert.Throws<ArgumentNullException>(() => new AmsAddressResolver(targets, null!, agents, deployments));
        Assert.Throws<ArgumentNullException>(() => new AmsAddressResolver(targets, environments, null!, deployments));
        Assert.Throws<ArgumentNullException>(() => new AmsAddressResolver(targets, environments, agents, null!));
    }
}
