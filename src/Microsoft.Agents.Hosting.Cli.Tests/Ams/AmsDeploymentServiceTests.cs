using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.Extensions.DependencyInjection;
using static Microsoft.Agents.Hosting.Cli.Tests.Ams.AmsBotServiceFixtures;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsDeploymentServiceTests
{
    [Fact]
    public async Task Create_WritesARecordOnlyWithTheAgentsLanguage()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };
        var deployments = new FakeAmsDeploymentTransport { OnCreate = Created };

        var result = await Service(agents, deployments).CreateAsync(CreateRequest(), default);

        Assert.Equal(["get"], agents.Calls);
        Assert.Equal(["create"], deployments.Calls);
        var sent = Assert.IsType<AmsDeploymentCreateTransportRequest>(Assert.Single(deployments.Requests));
        AmsTargetFixtures.AssertTarget(HomeControlPlane, sent.ServiceTarget);
        Assert.Equal((Agent, TargetEnvironment, "dev-proof", "Dev", Entra, "AppReg", "DotNet", "10"), (sent.AgentId, sent.TargetEnvironmentId, sent.DeploymentName, sent.DeploymentType, sent.EntraId, sent.EntraIdType, sent.Language, sent.LanguageVersion));
        Assert.Null(sent.RequestedEndpoint);
        Assert.Equal(AgentDeploymentAmsResult.CreatedStatus, result.Status);
        Assert.Equal(AmsDeploymentResult.NotDeployedState, result.Deployment.State);
        var text = result.ToTextLines();
        Assert.Equal($"Deployment dev-proof ({Deployment}) created. It is a record only: nothing runs until it is deployed.", text[0]);
        Assert.Contains("  package: none deployed yet", text);
        Assert.Contains("  endpoint: none deployed yet", text);
        Assert.Equal("  Azure Bot Service: none", text[^1]);
    }

    [Fact]
    public async Task Create_WithAnEndpointUrl_RequestsItForADevEndpointDeployment()
    {
        var deployments = new FakeAmsDeploymentTransport { OnCreate = request => DevEndpointRecord(null, request.RequestedEndpoint!, AmsDeploymentResult.NotDeployedState) };

        var result = await Service(new FakeAmsAgentTransport { Agents = [CodeAgent()] }, deployments).CreateAsync(CreateRequest() with { EndpointUrl = $" {TunnelEndpoint} " }, default);

        Assert.Equal(TunnelEndpoint, Assert.IsType<AmsDeploymentCreateTransportRequest>(Assert.Single(deployments.Requests)).RequestedEndpoint);
        Assert.Equal(AgentDeploymentAmsRecord.DevEndpointKind, result.Deployment.Kind);
        Assert.True(result.Deployment.EndpointChangePending);
        Assert.Contains($"  requested endpoint: {TunnelEndpoint}, not deployed yet; the next `msagent agent deploy` without --commit-id puts it live.", result.ToTextLines());
    }

    [Theory]
    [InlineData("name", " ", "--deployment-name is required for `msagent deployment create`.")]
    [InlineData("name", "x", "--deployment-name must not exceed 64 characters.")]
    [InlineData("type", "staging", "--deployment-type must be dev, test, or prod.")]
    [InlineData("type", null, "--deployment-type is required for `msagent deployment create`.")]
    [InlineData("entra", "not-a-guid", "--entra-id must be a non-empty GUID.")]
    [InlineData("entraType", "managed", "--entra-id-type must be app-reg, blueprint, or default.")]
    [InlineData("target", null, "--target-environment-id is required for `msagent deployment create`.")]
    [InlineData("endpointForTest", TunnelEndpoint, "--endpoint-url is supported only for Dev deployments; a Test deployment always runs a package.")]
    [InlineData("endpoint", "http://contoso-3978.usw2.devtunnels.ms/api/messages", "--endpoint-url must be an absolute https URL without user info, query, or fragment.")]
    [InlineData("endpoint", "https://contoso-3978.usw2.devtunnels.ms/api/messages?code=secret", "--endpoint-url must be an absolute https URL without user info, query, or fragment.")]
    [InlineData("agent", null, "--agent-id or --agent-name is required for `msagent deployment create`.")]
    public async Task Create_InvalidOptionsAreRefusedBeforeAnyCall(string option, string? value, string message)
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };
        var deployments = new FakeAmsDeploymentTransport { OnCreate = Created };
        var request = option switch
        {
            "name" => CreateRequest() with { DeploymentName = value == "x" ? new string('x', AgentDeploymentAmsCreateRequest.DeploymentNameMaxLength + 1) : value },
            "type" => CreateRequest() with { DeploymentType = value },
            "entra" => CreateRequest() with { EntraId = value },
            "entraType" => CreateRequest() with { EntraIdType = value },
            "target" => CreateRequest() with { TargetEnvironmentId = value },
            "endpointForTest" => CreateRequest() with { DeploymentType = "test", EndpointUrl = value },
            "endpoint" => CreateRequest() with { EndpointUrl = value },
            _ => CreateRequest() with { AgentId = value },
        };

        var exception = await Assert.ThrowsAsync<UsageException>(() => Service(agents, deployments).CreateAsync(request, default));

        Assert.Equal(message, exception.Message);
        Assert.Empty(agents.Calls);
        Assert.Empty(deployments.Calls);
    }

    [Fact]
    public async Task Create_ForAnAgentThatIsNotBound_IsRefusedBeforeTheCreate()
    {
        var deployments = new FakeAmsDeploymentTransport { OnCreate = Created };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(new FakeAmsAgentTransport { Agents = [CodeAgent() with { RepositoryBindingStatus = "Provisioning" }] }, deployments).CreateAsync(CreateRequest(), default));

        Assert.Equal("agent-not-deployable", exception.ErrorKind);
        Assert.Empty(deployments.Calls);
    }

    [Fact]
    public async Task Create_InAnEnvironmentNotAvailableToTheProfile_IsRefusedBeforeTheCreate()
    {
        var deployments = new FakeAmsDeploymentTransport { OnCreate = Created };

        await Assert.ThrowsAnyAsync<AgentHostingException>(() => Service(new FakeAmsAgentTransport { Agents = [CodeAgent()] }, deployments).CreateAsync(CreateRequest() with { TargetEnvironmentId = UnavailableEnvironment }, default));

        Assert.Empty(deployments.Calls);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("tenantId")]
    [InlineData("homeEnvironmentId")]
    [InlineData("agentId")]
    [InlineData("environmentId")]
    [InlineData("deploymentType")]
    [InlineData("entraAppId")]
    [InlineData("entraAppIdType")]
    public async Task Create_AnAnswerForAnotherRecord_IsAMismatch(string field)
    {
        var deployments = new FakeAmsDeploymentTransport
        {
            OnCreate = request => field switch
            {
                "id" => Created(request) with { DeploymentId = Guid.Empty.ToString() },
                "tenantId" => Created(request) with { TenantId = EnvironmentFixtures.FabrikamTenant },
                "homeEnvironmentId" => Created(request) with { HomeEnvironmentId = TargetEnvironment },
                "agentId" => Created(request) with { AgentId = OtherAgent },
                "environmentId" => Created(request) with { TargetEnvironmentId = HomeEnvironment },
                "deploymentType" => Created(request) with { DeploymentType = "Prod" },
                "entraAppId" => Created(request) with { EntraId = OtherAgent },
                _ => Created(request) with { EntraIdType = "Blueprint" },
            },
        };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(new FakeAmsAgentTransport { Agents = [CodeAgent()] }, deployments).CreateAsync(CreateRequest(), default));

        Assert.Equal("ams-response-identity-mismatch", exception.ErrorKind);
        Assert.Contains(field, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ShowsEachDeploymentsStateKindBotAndPendingEndpointWithTheKeptBots()
    {
        var deployments = new FakeAmsDeploymentTransport
        {
            Deployments = [DeploymentRecord(), DeploymentRecord(OtherDeployment, "test-proof", AmsDeploymentResult.NotDeployedState, "Test"), DevEndpointRecord(RuntimeEndpoint, TunnelEndpoint) with { DeploymentId = Revision, DeploymentName = "tunnel" }],
            KeptBots = [new AgentAmsKeptBot { DeploymentId = Commit, DeploymentName = "old", EnvironmentId = TargetEnvironment, BotName = "ams-old" }],
        };

        var result = await Service(new FakeAmsAgentTransport(), deployments).ListAsync(new AgentDeploymentAmsListRequest { AgentId = Agent }, default);

        Assert.Equal(["list"], deployments.Calls);
        Assert.Equal((Agent, HomeEnvironment), (result.AgentId, result.HomeEnvironmentId));
        Assert.Equal([(AmsDeploymentResult.DeployedState, "package", false), (AmsDeploymentResult.NotDeployedState, "package", false), (AmsDeploymentResult.DeployedState, "dev-endpoint", true)], result.Deployments.Select(deployment => (deployment.State, deployment.Kind, deployment.EndpointChangePending)));
        var text = string.Join(Environment.NewLine, result.ToTextLines());
        Assert.Contains("NAME", text, StringComparison.Ordinal);
        Assert.Contains("PENDING ENDPOINT", text, StringComparison.Ordinal);
        Assert.Contains("not deployed", text, StringComparison.Ordinal);
        Assert.Contains("dev endpoint", text, StringComparison.Ordinal);
        Assert.Contains(TunnelEndpoint, text, StringComparison.Ordinal);
        Assert.DoesNotContain("msagent", text, StringComparison.Ordinal);
        Assert.Contains("Bots kept from deleted deployment records", text, StringComparison.Ordinal);
        Assert.Contains("ams-old", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_APackageDeploymentWithARequestedEndpoint_ShowsItAsPending()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord() with { RequestedEndpoint = TunnelEndpoint }] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).ListAsync(new AgentDeploymentAmsListRequest { AgentId = Agent }, default);

        var deployment = Assert.Single(result.Deployments);
        Assert.Equal((AgentDeploymentAmsRecord.PackageKind, true, TunnelEndpoint), (deployment.Kind, deployment.EndpointChangePending, deployment.RequestedEndpoint));
        var text = string.Join(Environment.NewLine, result.ToTextLines());
        Assert.Contains(TunnelEndpoint, text, StringComparison.Ordinal);
        Assert.DoesNotContain("msagent", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_WithoutDeployments_SaysSo()
    {
        var result = await Service(new FakeAmsAgentTransport(), new FakeAmsDeploymentTransport()).ListAsync(new AgentDeploymentAmsListRequest { AgentId = Agent }, default);

        Assert.Equal([$"Agent {Agent} has no deployments."], result.ToTextLines());
    }

    [Fact]
    public async Task Show_ById_ReadsTheRecordOnce()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).ShowAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentId = Deployment }, default);

        Assert.Equal(["read"], deployments.Calls);
        Assert.Equal(AgentDeploymentAmsResult.ReadStatus, result.Status);
        Assert.Equal($"Deployment dev-proof ({Deployment})", result.ToTextLines()[0]);
        Assert.Contains("  Azure Bot Service: ams-bot-1, app " + Entra, result.ToTextLines());
    }

    [Fact]
    public async Task Show_ByName_UsesTheListEntryAndShowsARequestedEndpointThatIsNotDeployedYet()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DevEndpointRecord(RuntimeEndpoint, TunnelEndpoint)] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).ShowAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentName = "dev-proof" }, default);

        Assert.Equal(["list"], deployments.Calls);
        Assert.Contains($"  endpoint: {RuntimeEndpoint}", result.ToTextLines());
        Assert.Contains($"  requested endpoint: {TunnelEndpoint}, not deployed yet; the next `msagent agent deploy` without --commit-id puts it live.", result.ToTextLines());
    }

    [Fact]
    public async Task Show_APackageDeploymentWithARequestedEndpoint_SaysTheNextDeployWithoutACommitSwitchesIt()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord() with { RequestedEndpoint = TunnelEndpoint }] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).ShowAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentId = Deployment }, default);

        var text = result.ToTextLines();
        Assert.Contains("  stage: Dev; kind: package; state: deployed", text);
        Assert.Contains($"  endpoint: {RuntimeEndpoint}", text);
        Assert.Contains($"  requested endpoint: {TunnelEndpoint}, not deployed yet; the next `msagent agent deploy` without --commit-id switches the deployment from its package to this dev endpoint.", text);
    }

    [Theory]
    [InlineData("Dev")]
    [InlineData("Test")]
    [InlineData("Prod")]
    public async Task Update_RenamesTheRecordWithTheEntityTagOfTheRead(string deploymentType)
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(deploymentType: deploymentType)] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).UpdateAsync(new AgentDeploymentAmsUpdateRequest { AgentId = Agent, DeploymentId = Deployment, NewName = " dev-renamed " }, default);

        Assert.Equal(["read", "update"], deployments.Calls);
        var sent = Assert.IsType<AmsDeploymentUpdateTransportRequest>(deployments.Requests[1]);
        AssertDeployment(sent);
        Assert.Equal((DeploymentETag, "dev-renamed", (string?)null), (sent.ETag, sent.DeploymentName, sent.RequestedEndpoint));
        Assert.Equal(AgentDeploymentAmsResult.UpdatedStatus, result.Status);
        Assert.Equal($"Deployment dev-renamed ({Deployment}) updated. Only its record changed.", result.ToTextLines()[0]);
        Assert.DoesNotContain(result.ToTextLines(), line => line.StartsWith("The requested endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_ANewEndpoint_SaysItGoesLiveAtTheNextDeploy()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DevEndpointRecord(RuntimeEndpoint, RuntimeEndpoint)] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).UpdateAsync(new AgentDeploymentAmsUpdateRequest { AgentId = Agent, DeploymentName = "dev-proof", EndpointUrl = TunnelEndpoint }, default);

        Assert.Equal(["list", "update"], deployments.Calls);
        Assert.Equal(TunnelEndpoint, Assert.IsType<AmsDeploymentUpdateTransportRequest>(deployments.Requests[1]).RequestedEndpoint);
        Assert.Contains($"  requested endpoint: {TunnelEndpoint}, not deployed yet; the next `msagent agent deploy` without --commit-id puts it live.", result.ToTextLines());
        Assert.DoesNotContain(result.ToTextLines(), line => line.StartsWith("The requested endpoint", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_AnEndpointForADevPackageDeployment_RequestsTheSwitchAtTheNextDeploy()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var result = await Service(new FakeAmsAgentTransport(), deployments).UpdateAsync(new AgentDeploymentAmsUpdateRequest { AgentId = Agent, DeploymentId = Deployment, EndpointUrl = TunnelEndpoint }, default);

        Assert.Equal(["read", "update"], deployments.Calls);
        Assert.Equal(TunnelEndpoint, Assert.IsType<AmsDeploymentUpdateTransportRequest>(deployments.Requests[1]).RequestedEndpoint);
        Assert.Equal((AgentDeploymentAmsRecord.PackageKind, true), (result.Deployment.Kind, result.Deployment.EndpointChangePending));
        Assert.Contains($"  requested endpoint: {TunnelEndpoint}, not deployed yet; the next `msagent agent deploy` without --commit-id switches the deployment from its package to this dev endpoint.", result.ToTextLines());
        Assert.DoesNotContain(result.ToTextLines(), line => line.StartsWith("The requested endpoint", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Test")]
    [InlineData("Prod")]
    public async Task Update_AnEndpointForATestOrProdDeployment_IsRefusedAfterTheReadAndBeforeThePatch(string deploymentType)
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(deploymentType: deploymentType)] };

        var exception = await Assert.ThrowsAsync<UsageException>(() => Service(new FakeAmsAgentTransport(), deployments).UpdateAsync(new AgentDeploymentAmsUpdateRequest { AgentId = Agent, DeploymentId = Deployment, NewName = "renamed", EndpointUrl = TunnelEndpoint }, default));

        Assert.Equal($"--endpoint-url is supported only for Dev deployments; a {deploymentType} deployment always runs a package.", exception.Message);
        Assert.Equal(["read"], deployments.Calls);
    }

    [Theory]
    [InlineData(null, null, "--new-name or --endpoint-url is required for `msagent deployment update`.")]
    [InlineData(" ", " ", "--new-name or --endpoint-url is required for `msagent deployment update`.")]
    [InlineData(null, "https://contoso.example/api/messages#top", "--endpoint-url must be an absolute https URL without user info, query, or fragment.")]
    [InlineData("x", null, "--new-name must not exceed 64 characters.")]
    public async Task Update_InvalidOptionsAreRefusedBeforeAnyCall(string? newName, string? endpointUrl, string message)
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var exception = await Assert.ThrowsAsync<UsageException>(() => Service(new FakeAmsAgentTransport(), deployments).UpdateAsync(new AgentDeploymentAmsUpdateRequest { AgentId = Agent, DeploymentId = Deployment, NewName = newName == "x" ? new string('x', 65) : newName, EndpointUrl = endpointUrl }, default));

        Assert.Equal(message, exception.Message);
        Assert.Empty(deployments.Calls);
    }

    [Fact]
    public async Task Update_ARecordBeingDeleted_IsRefusedBeforeThePatch()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: AmsDeploymentResult.DeletingState)] };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(new FakeAmsAgentTransport(), deployments).UpdateAsync(new AgentDeploymentAmsUpdateRequest { AgentId = Agent, DeploymentId = Deployment, NewName = "renamed" }, default));

        Assert.Equal("deployment-deleting", exception.ErrorKind);
        Assert.Equal(["read"], deployments.Calls);
    }

    [Theory]
    [InlineData(AmsDeploymentResult.NotDeployedState)]
    [InlineData(AmsDeploymentResult.DeletingState)]
    public async Task Delete_ARecordThatIsNotLive_IsDeletedWithoutAsking(string state)
    {
        var prompt = new ScriptedConfirmationPrompt { Throws = true };
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: state)] };

        var result = await Service(new FakeAmsAgentTransport(), deployments, prompt).DeleteAsync(new AgentDeploymentAmsDeleteRequest { AgentId = Agent, DeploymentId = Deployment }, default);

        Assert.Equal(0, prompt.Calls);
        Assert.Equal(["read", "delete"], deployments.Calls);
        var sent = Assert.IsType<AmsDeploymentDeleteTransportRequest>(deployments.Requests[1]);
        AssertDeployment(sent);
        Assert.Equal(DeploymentETag, sent.ETag);
        Assert.Equal(AgentDeploymentAmsDeleteResult.DeletedStatus, result.Status);
        Assert.False(result.WasLive);
        Assert.Equal([$"Deployment record dev-proof ({Deployment}) deleted."], result.ToTextLines());
    }

    [Theory]
    [InlineData(AmsDeploymentResult.DeployedState)]
    [InlineData(AmsDeploymentResult.DeployingState)]
    public async Task Delete_ALiveRecord_AsksForItsNameAfterSayingWhatKeepsRunning(string state)
    {
        var prompt = new ScriptedConfirmationPrompt();
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: state)] };

        var result = await Service(new FakeAmsAgentTransport(), deployments, prompt).DeleteAsync(new AgentDeploymentAmsDeleteRequest { AgentId = Agent, DeploymentName = "dev-proof" }, default);

        Assert.Equal("dev-proof", prompt.LastExpected);
        Assert.StartsWith($"About to delete the record of live deployment dev-proof ({Deployment}).", prompt.LastDetail, StringComparison.Ordinal);
        Assert.Contains($"Still running: the deployment keeps answering in environment {TargetEnvironment} through its Azure Bot Service instance ams-bot-1.", prompt.LastDetail, StringComparison.Ordinal);
        Assert.Contains("Until `msagent agent delete` removes it", prompt.LastDetail, StringComparison.Ordinal);
        Assert.Equal(["list", "delete"], deployments.Calls);
        Assert.True(result.WasLive);
        Assert.Equal(AgentDeploymentAmsDeleteResult.DeletedStatus, result.Status);
        Assert.Equal([$"Deployment record dev-proof ({Deployment}) deleted.", $"Still running: the deployment keeps answering in environment {TargetEnvironment} through its Azure Bot Service instance ams-bot-1."], result.ToTextLines());
    }

    [Theory]
    [InlineData(ConfirmationOutcome.Declined, AgentAmsConfirmation.CancelledStatus, "Delete cancelled; nothing was deleted.")]
    [InlineData(ConfirmationOutcome.Mismatched, AgentAmsConfirmation.NameMismatchStatus, "The name typed did not match 'dev-proof'; nothing was deleted.")]
    public async Task Delete_ALiveRecordThatIsNotConfirmed_DeletesNothing(ConfirmationOutcome outcome, string status, string text)
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var result = await Service(new FakeAmsAgentTransport(), deployments, new ScriptedConfirmationPrompt { Outcome = outcome }).DeleteAsync(new AgentDeploymentAmsDeleteRequest { AgentId = Agent, DeploymentId = Deployment }, default);

        Assert.Equal(["read"], deployments.Calls);
        Assert.Equal(status, result.Status);
        Assert.Equal([text], result.ToTextLines());
    }

    [Fact]
    public async Task Delete_ALiveRecordNonInteractively_DeletesWithoutPrompting()
    {
        var prompt = new ScriptedConfirmationPrompt { Throws = true };
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()] };

        var result = await Service(new FakeAmsAgentTransport(), deployments, prompt).DeleteAsync(new AgentDeploymentAmsDeleteRequest { AgentId = Agent, DeploymentId = Deployment, NonInteractive = true }, default);

        Assert.Equal(0, prompt.Calls);
        Assert.Equal(["read", "delete"], deployments.Calls);
        Assert.Equal(AgentDeploymentAmsDeleteResult.DeletedStatus, result.Status);
        Assert.True(result.WasLive);
    }

    [Fact]
    public async Task Handlers_DispatchEveryRequestToItsOperation()
    {
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(state: AmsDeploymentResult.NotDeployedState)], OnCreate = Created };
        var service = Service(new FakeAmsAgentTransport { Agents = [CodeAgent()] }, deployments);

        await ((ICommandHandler<AgentDeploymentAmsCreateRequest, AgentDeploymentAmsResult>)service).HandleAsync(CreateRequest(), default);
        await ((ICommandHandler<AgentDeploymentAmsListRequest, AgentDeploymentAmsListResult>)service).HandleAsync(new AgentDeploymentAmsListRequest { AgentId = Agent }, default);
        await ((ICommandHandler<AgentDeploymentAmsShowRequest, AgentDeploymentAmsResult>)service).HandleAsync(new AgentDeploymentAmsShowRequest { AgentId = Agent, DeploymentId = Deployment }, default);
        await ((ICommandHandler<AgentDeploymentAmsUpdateRequest, AgentDeploymentAmsResult>)service).HandleAsync(new AgentDeploymentAmsUpdateRequest { AgentId = Agent, DeploymentId = Deployment, NewName = "renamed" }, default);
        await ((ICommandHandler<AgentDeploymentAmsDeleteRequest, AgentDeploymentAmsDeleteResult>)service).HandleAsync(new AgentDeploymentAmsDeleteRequest { AgentId = Agent, DeploymentId = Deployment }, default);

        Assert.Equal(["create", "list", "read", "read", "update", "read", "delete"], deployments.Calls);
    }

    [Fact]
    public void Composition_ServesEveryRouteFromTheOneServiceAndRequiresEveryDependency()
    {
        var services = new ServiceCollection();
        services.AddAgentHostAuthentication(new AgentHostAuthOptions { ClientId = "c47b0a43-21ff-45f7-9280-6561e31342a5", ConfigDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) });
        services.AddSingleton(new AmsEndpointProfile());
        services.AddSingleton<IEnvironmentClient>(new FakeEnvironmentClient());
        services.AddSingleton<IClock>(new FixedClock(EnvironmentFixtures.SelectedAt));
        services.AddSingleton<IAmsAgentReadTransport>(new PendingAmsAgentReadTransport());
        services.AddSingleton<IAmsAgentTransport>(new FakeAmsAgentTransport());
        services.AddSingleton<IAmsDeploymentTransport>(new PendingAmsDeploymentTransport());
        services.AddSingleton<IConfirmationPrompt>(new ScriptedConfirmationPrompt());
        using var provider = services.BuildServiceProvider();
        var deploymentService = provider.GetRequiredService<AmsDeploymentService>();
        var agentService = provider.GetRequiredService<AmsAgentService>();

        Assert.All([typeof(ICommandHandler<AgentDeploymentAmsCreateRequest, AgentDeploymentAmsResult>), typeof(ICommandHandler<AgentDeploymentAmsListRequest, AgentDeploymentAmsListResult>), typeof(ICommandHandler<AgentDeploymentAmsShowRequest, AgentDeploymentAmsResult>), typeof(ICommandHandler<AgentDeploymentAmsUpdateRequest, AgentDeploymentAmsResult>), typeof(ICommandHandler<AgentDeploymentAmsDeleteRequest, AgentDeploymentAmsDeleteResult>)], route => Assert.Same(deploymentService, provider.GetRequiredService(route)));
        Assert.All([typeof(ICommandHandler<AgentAmsListRequest, AgentAmsListResult>), typeof(ICommandHandler<AgentAmsShowRequest, AgentAmsShowResult>), typeof(ICommandHandler<AgentAmsUpdateRequest, AgentAmsUpdateResult>), typeof(ICommandHandler<AgentAmsDeleteRequest, AgentAmsDeleteResult>), typeof(ICommandHandler<AgentCommitListRequest, AgentCommitListResult>)], route => Assert.Same(agentService, provider.GetRequiredService(route)));
        Assert.Throws<ArgumentNullException>(() => new AmsDeploymentService(null!, new FakeAmsAgentTransport(), new FakeAmsDeploymentTransport(), new ScriptedConfirmationPrompt()));
        Assert.Throws<ArgumentNullException>(() => new AmsDeploymentService(Resolver(), null!, new FakeAmsDeploymentTransport(), new ScriptedConfirmationPrompt()));
        Assert.Throws<ArgumentNullException>(() => new AmsDeploymentService(Resolver(), new FakeAmsAgentTransport(), null!, new ScriptedConfirmationPrompt()));
        Assert.Throws<ArgumentNullException>(() => new AmsDeploymentService(Resolver(), new FakeAmsAgentTransport(), new FakeAmsDeploymentTransport(), null!));
    }

    private static AgentDeploymentAmsCreateRequest CreateRequest() => new() { AgentId = Agent, DeploymentName = " dev-proof ", DeploymentType = "dev", EntraId = Entra.ToUpperInvariant(), EntraIdType = "app-reg", TargetEnvironmentId = TargetEnvironment };

    private static AmsDeploymentResult Created(AmsDeploymentCreateTransportRequest request) => DeploymentRecord(state: AmsDeploymentResult.NotDeployedState, deploymentName: request.DeploymentName, deploymentType: request.DeploymentType) with { TargetEnvironmentId = request.TargetEnvironmentId, EntraId = request.EntraId, EntraIdType = request.EntraIdType };

    private static AmsDeploymentService Service(FakeAmsAgentTransport agents, FakeAmsDeploymentTransport deployments, IConfirmationPrompt? prompt = null) => new(Resolver(agents, deployments), agents, deployments, prompt ?? new ScriptedConfirmationPrompt());
}
