using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using static Microsoft.Agents.Hosting.Cli.Tests.Ams.AmsBotServiceFixtures;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsAgentServiceTests
{
    private static readonly AgentDeployException ListUnavailable = AgentDeployException.ServiceRejected("unavailable", ExitCodes.Failure, AmsRefusals.LifecycleUnavailableKind, "ask");

    [Fact]
    public async Task List_ShowsOnlyTheCodeAgentsOfTheActiveEnvironment()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent(), CodeAgent(OtherAgent, "Copilot", "CopilotStudio")] };

        var result = await Service(agents, new FakeAmsDeploymentTransport()).ListAsync(new AgentAmsListRequest(), default);

        Assert.Equal(["list"], agents.Calls);
        AmsTargetFixtures.AssertTarget(HomeDataPlane, Assert.IsType<AmsServiceTarget>(Assert.Single(agents.Requests)));
        Assert.Equal(HomeEnvironment, result.EnvironmentId);
        Assert.Equal([Agent], result.Agents.Select(agent => agent.AgentId));
        var text = string.Join(Environment.NewLine, result.ToTextLines());
        Assert.Contains($"Code agents of environment {HomeEnvironment}", text, StringComparison.Ordinal);
        Assert.Contains("Contoso Agent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Copilot", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_OfANamedEnvironmentWithoutCodeAgents_SaysSo()
    {
        var agents = new FakeAmsAgentTransport();

        var result = await Service(agents, new FakeAmsDeploymentTransport()).ListAsync(new AgentAmsListRequest { EnvironmentId = TargetEnvironment }, default);

        Assert.Equal(TargetEnvironment, Assert.IsType<AmsServiceTarget>(Assert.Single(agents.Requests)).EnvironmentId);
        Assert.Equal([$"Environment {TargetEnvironment} has no code agents."], result.ToTextLines());
    }

    [Fact]
    public async Task Show_ReadsTheAgentWithItsDeploymentsAndKeptBots()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord()], KeptBots = [new AgentAmsKeptBot { DeploymentId = Commit, DeploymentName = "old", BotName = "ams-old" }] };

        var result = await Service(agents, deployments).ShowAsync(new AgentAmsShowRequest { AgentName = "contoso agent" }, default);

        Assert.Equal(["list", "get"], agents.Calls);
        Assert.Equal(["list"], deployments.Calls);
        Assert.True(result.DeploymentsAvailable);
        Assert.Equal(["dev-proof"], result.Deployments.Select(deployment => deployment.DeploymentName));
        Assert.Equal(["ams-old"], result.KeptBots.Select(bot => bot.BotName));
        var text = string.Join(Environment.NewLine, result.ToTextLines());
        Assert.StartsWith($"Agent Contoso Agent ({Agent})", text, StringComparison.Ordinal);
        Assert.Contains("  description: Answers questions.", text, StringComparison.Ordinal);
        Assert.Contains("dev-proof", text, StringComparison.Ordinal);
        Assert.Contains("ams-old", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_WhenAmsDoesNotListDeploymentsYet_ShowsTheAgentAndSaysSo()
    {
        var deployments = new FakeAmsDeploymentTransport { ListFailure = ListUnavailable };

        var result = await Service(new FakeAmsAgentTransport { Agents = [CodeAgent()] }, deployments).ShowAsync(new AgentAmsShowRequest { AgentId = Agent }, default);

        Assert.False(result.DeploymentsAvailable);
        Assert.Empty(result.Deployments);
        Assert.Contains("Deployments: unavailable; AMS does not list deployments in this environment yet.", result.ToTextLines());
    }

    [Fact]
    public async Task Show_WithoutDeployments_SaysSo()
    {
        var result = await Service(new FakeAmsAgentTransport { Agents = [CodeAgent()] }, new FakeAmsDeploymentTransport()).ShowAsync(new AgentAmsShowRequest { AgentId = Agent }, default);

        Assert.Equal("Deployments: none.", result.ToTextLines()[^1]);
    }

    [Fact]
    public async Task Show_AnAgentOfAnotherType_IsRefused()
    {
        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(new FakeAmsAgentTransport { Agents = [CodeAgent(agentType: "CopilotStudio")] }, new FakeAmsDeploymentTransport()).ShowAsync(new AgentAmsShowRequest { AgentId = Agent }, default));

        Assert.Contains("not a CodeAgent", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(" Renamed ", null, "Renamed", null)]
    [InlineData(null, "", null, "")]
    [InlineData("Renamed", "New text", "Renamed", "New text")]
    public async Task Update_PatchesTheAgentWithTheEntityTagOfTheRead(string? newName, string? description, string? sentName, string? sentDescription)
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };

        var result = await Service(agents, new FakeAmsDeploymentTransport()).UpdateAsync(new AgentAmsUpdateRequest { AgentId = Agent, NewName = newName, Description = description }, default);

        Assert.Equal(["get", "update"], agents.Calls);
        var sent = Assert.IsType<AmsAgentUpdateTransportRequest>(agents.Requests[1]);
        AmsTargetFixtures.AssertTarget(HomeDataPlane, sent.ServiceTarget);
        Assert.Equal((AgentETag, sentName, sentDescription), (sent.ETag, sent.DisplayName, sent.Description));
        Assert.Equal(AgentAmsUpdateResult.UpdatedStatus, result.Status);
        Assert.Equal($"Agent {sentName ?? "Contoso Agent"} ({Agent}) updated.", result.ToTextLines()[0]);
    }

    [Fact]
    public async Task Update_WithNothingToChange_IsRefusedBeforeAnyCall()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };

        var exception = await Assert.ThrowsAsync<UsageException>(() => Service(agents, new FakeAmsDeploymentTransport()).UpdateAsync(new AgentAmsUpdateRequest { AgentId = Agent, NewName = " " }, default));

        Assert.Equal("--new-name or --description is required for `msagent agent update`.", exception.Message);
        Assert.Empty(agents.Calls);
    }

    [Fact]
    public async Task Update_AnAnswerForAnotherAgent_IsAMismatch()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()], OnUpdate = _ => CodeAgent(OtherAgent) };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(agents, new FakeAmsDeploymentTransport()).UpdateAsync(new AgentAmsUpdateRequest { AgentId = Agent, NewName = "Renamed" }, default));

        Assert.Equal("ams-response-identity-mismatch", exception.ErrorKind);
    }

    [Fact]
    public async Task Delete_ListsWhatWillBeRemovedAsksForTheNameAndCascadesWithTheEntityTag()
    {
        var prompt = new ScriptedConfirmationPrompt();
        var agents = new FakeAmsAgentTransport
        {
            Agents = [CodeAgent()],
            OnCascadeDelete = request => new AmsAgentCascadeDeleteResult
            {
                AgentId = request.AgentId,
                Deployments = [new AgentAmsRemovedDeployment { DeploymentId = Deployment, DeploymentName = "dev-proof", EnvironmentId = TargetEnvironment, DeploymentType = "Dev", BotName = "ams-bot-1", BotDeleted = true }, new AgentAmsRemovedDeployment { DeploymentId = OtherDeployment, DeploymentName = "test", EnvironmentId = TargetEnvironment, DeploymentType = "Test" }],
                KeptBots = [new AgentAmsRemovedDeployment { DeploymentId = Commit, DeploymentName = "old", BotName = "ams-old", BotDeleted = false }],
            },
        };
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(), DeploymentRecord(OtherDeployment, "test", AmsDeploymentResult.NotDeployedState, "Test")], KeptBots = [new AgentAmsKeptBot { DeploymentId = Commit, DeploymentName = "old", BotName = "ams-old" }] };

        var result = await Service(agents, deployments, prompt).DeleteAsync(new AgentAmsDeleteRequest { AgentId = Agent }, default);

        Assert.Equal("Contoso Agent", prompt.LastExpected);
        Assert.Equal(
            string.Join(Environment.NewLine, [
                $"About to delete code agent Contoso Agent ({Agent}) of environment {HomeEnvironment}:",
                $"  deployment dev-proof ({Deployment}, Dev) in {TargetEnvironment}, with bot ams-bot-1",
                $"  deployment test ({OtherDeployment}, Test) in {TargetEnvironment}",
                $"  bot ams-old kept from deleted deployment old ({Commit})",
                "  the agent record",
                string.Empty,
                "This deletes every bot of the agent with its channels and OAuth connections. Local files are not touched."]),
            prompt.LastDetail);
        Assert.Equal(["get", "cascadeDelete"], agents.Calls);
        var sent = Assert.IsType<AmsAgentCascadeDeleteTransportRequest>(agents.Requests[1]);
        AmsTargetFixtures.AssertTarget(HomeControlPlane, sent.ServiceTarget);
        Assert.Equal(AgentETag, sent.ETag);
        Assert.Equal(AgentAmsDeleteResult.DeletedStatus, result.Status);
        Assert.Equal(
            [
                $"Agent Contoso Agent ({Agent}) deleted.",
                "Removed:",
                $"  deployment dev-proof ({Deployment}, Dev) in {TargetEnvironment}, bot ams-bot-1 deleted",
                $"  deployment test ({OtherDeployment}, Test) in {TargetEnvironment}, no bot",
                $"  kept bot of deleted deployment old ({Commit}), bot ams-old already gone",
                "  the agent record",
            ],
            result.ToTextLines());
    }

    [Theory]
    [InlineData(ConfirmationOutcome.Declined, AgentAmsConfirmation.CancelledStatus, "Delete cancelled; nothing was deleted.")]
    [InlineData(ConfirmationOutcome.Mismatched, AgentAmsConfirmation.NameMismatchStatus, "The name typed did not match 'Contoso Agent'; nothing was deleted.")]
    public async Task Delete_ThatIsNotConfirmed_DeletesNothing(ConfirmationOutcome outcome, string status, string text)
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };

        var result = await Service(agents, new FakeAmsDeploymentTransport(), new ScriptedConfirmationPrompt { Outcome = outcome }).DeleteAsync(new AgentAmsDeleteRequest { AgentId = Agent }, default);

        Assert.Equal(["get"], agents.Calls);
        Assert.Equal(status, result.Status);
        Assert.Equal([text], result.ToTextLines());
    }

    [Fact]
    public async Task Delete_NonInteractively_CascadesWithoutPromptingAndConfirmsAnUnnamedAgentById()
    {
        var prompt = new ScriptedConfirmationPrompt { Throws = true };
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent(displayName: null)] };

        var result = await Service(agents, new FakeAmsDeploymentTransport(), prompt).DeleteAsync(new AgentAmsDeleteRequest { AgentId = Agent, NonInteractive = true }, default);

        Assert.Equal(0, prompt.Calls);
        Assert.Equal(["get", "cascadeDelete"], agents.Calls);
        Assert.Equal($"Agent {TextGrid.Dash} ({Agent}) deleted.", result.ToTextLines()[0]);
        Assert.Equal(["Delete cancelled; nothing was deleted."], (await Service(new FakeAmsAgentTransport { Agents = [CodeAgent(displayName: null)] }, new FakeAmsDeploymentTransport(), new ScriptedConfirmationPrompt { Answer = false }).DeleteAsync(new AgentAmsDeleteRequest { AgentId = Agent }, default)).ToTextLines());
    }

    [Fact]
    public async Task Delete_WhenAmsDoesNotListDeploymentsYet_DeletesNothing()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(agents, new FakeAmsDeploymentTransport { ListFailure = ListUnavailable }, new ScriptedConfirmationPrompt { Throws = true }).DeleteAsync(new AgentAmsDeleteRequest { AgentId = Agent }, default));

        Assert.Equal(AmsRefusals.LifecycleUnavailableKind, exception.ErrorKind);
        Assert.Equal(["get"], agents.Calls);
    }

    [Fact]
    public async Task Delete_ADeployStillRunning_PassesAmsRefusalOn()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()], OnCascadeDelete = _ => throw AgentDeployException.ServiceRejected("AMS cannot finish deleting the agent yet: run it again after 10:42 UTC.", ExitCodes.Failure, "agent-deploy-in-progress", "Run `msagent agent delete` again.") };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(agents, new FakeAmsDeploymentTransport(), new ScriptedConfirmationPrompt()).DeleteAsync(new AgentAmsDeleteRequest { AgentId = Agent }, default));

        Assert.Equal("agent-deploy-in-progress", exception.ErrorKind);
        Assert.NotEqual(ExitCodes.Success, exception.ExitCode);
    }

    [Fact]
    public async Task Delete_AnAnswerForAnotherAgent_IsAMismatch()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()], OnCascadeDelete = _ => new AmsAgentCascadeDeleteResult { AgentId = OtherAgent, Deployments = [], KeptBots = [] } };

        var exception = await Assert.ThrowsAsync<AgentDeployException>(() => Service(agents, new FakeAmsDeploymentTransport(), new ScriptedConfirmationPrompt()).DeleteAsync(new AgentAmsDeleteRequest { AgentId = Agent }, default));

        Assert.Equal("ams-response-identity-mismatch", exception.ErrorKind);
    }

    [Fact]
    public async Task CommitList_ShowsTheUploadsNewestFirstWithTheDeploymentsThatRunThem()
    {
        var agents = new FakeAmsAgentTransport
        {
            Agents = [CodeAgent()],
            Packages =
            [
                new AmsAgentPackage { PackageRevisionId = OtherDeployment, CommitId = new string('b', 40), SizeInBytes = 10, CreatedTime = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) },
                new AmsAgentPackage { PackageRevisionId = Revision, CommitId = Commit, SizeInBytes = 2048, CreatedTime = new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero) },
                new AmsAgentPackage { PackageRevisionId = Deployment, CreatedTime = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero) },
            ],
        };
        var deployments = new FakeAmsDeploymentTransport { Deployments = [DeploymentRecord(), DeploymentRecord(OtherDeployment, "test") with { CommitId = Commit.ToUpperInvariant(), PackageRevisionId = OtherAgent }, DeploymentRecord(Entra, "legacy") with { CommitId = null, PackageRevisionId = Deployment }] };

        var result = await Service(agents, deployments).ListCommitsAsync(new AgentCommitListRequest { AgentId = Agent }, default);

        Assert.Equal(["packages"], agents.Calls);
        Assert.Equal(["list"], deployments.Calls);
        Assert.True(result.DeploymentsAvailable);
        Assert.Equal([Commit, null, new string('b', 40)], result.Commits.Select(commit => commit.CommitId));
        Assert.Equal(["dev-proof", "test"], result.Commits[0].Deployments);
        Assert.Equal(["legacy"], result.Commits[1].Deployments);
        Assert.Empty(result.Commits[2].Deployments);
        var text = string.Join(Environment.NewLine, result.ToTextLines());
        Assert.Contains($"Commits of agent {Agent}, newest first", text, StringComparison.Ordinal);
        Assert.Contains("dev-proof, test", text, StringComparison.Ordinal);
        Assert.Contains("2048", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommitList_WhenAmsDoesNotListDeploymentsYet_ShowsTheCommitsAndSaysSo()
    {
        var agents = new FakeAmsAgentTransport { Packages = [new AmsAgentPackage { PackageRevisionId = Revision, CommitId = Commit }] };

        var result = await Service(agents, new FakeAmsDeploymentTransport { ListFailure = ListUnavailable }).ListCommitsAsync(new AgentCommitListRequest { AgentId = Agent }, default);

        Assert.False(result.DeploymentsAvailable);
        Assert.Contains("Deployments: unavailable; AMS does not list deployments in this environment yet.", result.ToTextLines());
    }

    [Fact]
    public async Task CommitList_WithoutUploads_SaysSo()
    {
        var result = await Service(new FakeAmsAgentTransport(), new FakeAmsDeploymentTransport()).ListCommitsAsync(new AgentCommitListRequest { AgentId = Agent }, default);

        Assert.Equal([$"Agent {Agent} has no uploads."], result.ToTextLines());
    }

    [Fact]
    public async Task Handlers_DispatchEveryRequestToItsOperation()
    {
        var agents = new FakeAmsAgentTransport { Agents = [CodeAgent()] };
        var service = Service(agents, new FakeAmsDeploymentTransport());

        await ((ICommandHandler<AgentAmsListRequest, AgentAmsListResult>)service).HandleAsync(new AgentAmsListRequest(), default);
        await ((ICommandHandler<AgentAmsShowRequest, AgentAmsShowResult>)service).HandleAsync(new AgentAmsShowRequest { AgentId = Agent }, default);
        await ((ICommandHandler<AgentAmsUpdateRequest, AgentAmsUpdateResult>)service).HandleAsync(new AgentAmsUpdateRequest { AgentId = Agent, Description = "d" }, default);
        await ((ICommandHandler<AgentAmsDeleteRequest, AgentAmsDeleteResult>)service).HandleAsync(new AgentAmsDeleteRequest { AgentId = Agent, NonInteractive = true }, default);
        await ((ICommandHandler<AgentCommitListRequest, AgentCommitListResult>)service).HandleAsync(new AgentCommitListRequest { AgentId = Agent }, default);

        Assert.Equal(["list", "get", "get", "update", "get", "cascadeDelete", "packages"], agents.Calls);
    }

    [Fact]
    public void Constructor_RequiresEveryDependency()
    {
        var agents = new FakeAmsAgentTransport();

        Assert.Throws<ArgumentNullException>(() => new AmsAgentService(null!, agents, agents, new ScriptedConfirmationPrompt()));
        Assert.Throws<ArgumentNullException>(() => new AmsAgentService(Resolver(), null!, agents, new ScriptedConfirmationPrompt()));
        Assert.Throws<ArgumentNullException>(() => new AmsAgentService(Resolver(), agents, null!, new ScriptedConfirmationPrompt()));
        Assert.Throws<ArgumentNullException>(() => new AmsAgentService(Resolver(), agents, agents, null!));
    }

    private static AmsAgentService Service(FakeAmsAgentTransport agents, FakeAmsDeploymentTransport deployments, IConfirmationPrompt? prompt = null) => new(Resolver(agents, deployments), agents, agents, prompt ?? new ScriptedConfirmationPrompt());
}
