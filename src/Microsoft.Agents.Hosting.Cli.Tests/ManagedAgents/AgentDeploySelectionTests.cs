using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

[Collection("CurrentDirectory")]
public sealed class AgentDeploySelectionTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace = new(withProjectFile: false);
    private readonly JsonAgentConfigStore _config = ManagedAgentFixtures.Store();

    public AgentDeploySelectionTests()
    {
        _workspace.AddFile(Path.Combine(".mcs", "botdefinition.json"), "{\"source\":\"cloud\"}");
        _workspace.AddFile(Path.Combine(".mcs", "botdefinition.yml"), "source: cloud\n");
        _workspace.AddFile(Path.Combine(".mcs", "workflow-state.json"), "{\"env-1\":{\"workflow-1\":1}}");
    }

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData(AgentTypes.McsAgent)]
    [InlineData(AgentTypes.McsWorkflow)]
    public void AnAgentWithoutDeploymentsGetsCreateAndDeployCommands(string agentType)
    {
        RegisterAgent(agentType: agentType);

        var failure = Failure();

        Assert.Equal("deployment-records-empty", failure.ErrorKind);
        Assert.Equal("Agent 'Tmp1' has no deployment records. Create a deployment before deploying.", failure.Message);
        Assert.Equal(ExitCodes.Usage, failure.ExitCode);
        Assert.Equal(
            $"Create a deployment for 'Tmp1':{Environment.NewLine}  msagent deployment create --project \"{_workspace.Root}\" --agent-id \"agent-1\" --deployment-name development --deployment-type dev{Environment.NewLine}Then deploy:{Environment.NewLine}  msagent agent deploy --project \"{_workspace.Root}\" --agent-id \"agent-1\" --deployment-name development",
            failure.Remediation);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(" ", "\t")]
    public void BlankSelectorsDoNotInferTheOnlyDeployment(string? deploymentId, string? deploymentName)
    {
        RegisterAgent();
        RegisterDeployment();

        var failure = Failure(deploymentId: deploymentId, deploymentName: deploymentName);

        Assert.Equal("deployment-selector-required", failure.ErrorKind);
        Assert.Equal($"No deployment selected for agent 'Tmp1'.{Environment.NewLine}Available deployments: development", failure.Message);
        Assert.Contains("--deployment-id or --deployment-name", failure.Remediation, StringComparison.Ordinal);
        Assert.Contains("--deployment-id \"deployment-1\"", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("msagent deployment create", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingDeploymentsAreListedWithTheirStoredNames()
    {
        RegisterAgent();
        RegisterDeployment();
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, ManagedAgentFixtures.Deployment("deployment-2", name: "production", stage: DeploymentStages.Prod, environmentId: "env-2"));

        var failure = Failure(agentName: "Tmp1");

        Assert.Contains("Available deployments: development, production", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no deployment records", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MultipleAgentsReportEachAgentsDeploymentState(bool firstHasDeployment, bool secondHasDeployment)
    {
        RegisterAgent();
        RegisterAgent("agent-2", "Tmp2");

        if (firstHasDeployment)
        {
            RegisterDeployment();
        }

        if (secondHasDeployment)
        {
            RegisterDeployment("deployment-2", "agent-2", "second-development");
        }

        var failure = Failure();

        Assert.Contains($"Tmp1 (agent-1): {(firstHasDeployment ? "development" : "no deployment records")}", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"Tmp2 (agent-2): {(secondHasDeployment ? "second-development" : "no deployment records")}", failure.Message, StringComparison.Ordinal);
        Assert.Equal(firstHasDeployment || secondHasDeployment ? "deployment-selector-required" : "deployment-records-empty", failure.ErrorKind);
        Assert.Equal(!firstHasDeployment && !secondHasDeployment, failure.Message.Contains("None of the agents in this folder has deployment records.", StringComparison.Ordinal));
        Assert.Contains($"msagent {(firstHasDeployment ? "agent deploy" : "deployment create")} --project \"{_workspace.Root}\" --agent-id \"agent-1\"", failure.Remediation, StringComparison.Ordinal);
        Assert.Contains($"msagent {(secondHasDeployment ? "agent deploy" : "deployment create")} --project \"{_workspace.Root}\" --agent-id \"agent-2\"", failure.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void AnExplicitAgentSelectorLimitsTheDiagnosticToItsOwner(bool selectById, bool selectedHasDeployment)
    {
        RegisterAgent();
        RegisterAgent("agent-2", "Tmp2");
        RegisterDeployment("deployment-2", "agent-2", "other-development");

        if (selectedHasDeployment)
        {
            RegisterDeployment();
        }

        var failure = Failure(agentId: selectById ? "agent-1" : null, agentName: selectById ? null : "Tmp1");

        Assert.Equal(selectedHasDeployment ? "deployment-selector-required" : "deployment-records-empty", failure.ErrorKind);
        Assert.Contains("Tmp1", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Tmp2", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("other-development", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-2", failure.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnUnknownDeploymentOnAnEmptyAgentRetainsTheSuppliedSelector(bool selectById)
    {
        RegisterAgent();

        var failure = Failure(deploymentId: selectById ? "missing-id" : null, deploymentName: selectById ? null : "no-such-slot");

        Assert.Equal("deployment-not-found", failure.ErrorKind);
        Assert.Contains(selectById ? "missing-id" : "no-such-slot", failure.Message, StringComparison.Ordinal);
        Assert.Contains("no deployment records", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment create", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownDeploymentInAnEmptyFolderListsAllRegisteredAgents()
    {
        RegisterAgent();
        RegisterAgent("agent-2", "Tmp2");

        var failure = Failure(deploymentName: "no-such-slot");

        Assert.Equal("deployment-not-found", failure.ErrorKind);
        Assert.Contains("no-such-slot", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Tmp1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Tmp2", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownDeploymentDoesNotReportExistingRecordsAsEmpty()
    {
        RegisterAgent();
        RegisterDeployment();

        var failure = Failure(agentId: "agent-1", deploymentName: "no-such-slot");

        Assert.Equal("deployment-not-found", failure.ErrorKind);
        Assert.Contains("no-such-slot", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("no deployment records", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent deployment list", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownAgentNameIsNotReportedAsEmptyDeployments()
    {
        RegisterAgent();

        var failure = Failure(agentName: "missing-agent");

        Assert.Equal("agent-not-found", failure.ErrorKind);
        Assert.Contains("missing-agent", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("msagent deployment create", failure.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "agent-id-conflict")]
    [InlineData(true, "agent-not-found")]
    public void AnUnknownAgentIdentifierPreservesItsExistingError(bool multipleAgents, string errorKind)
    {
        RegisterAgent();

        if (multipleAgents)
        {
            RegisterAgent("agent-2", "Tmp2");
        }

        Assert.Equal(errorKind, Failure(agentId: "missing-agent").ErrorKind);
    }

    [Fact]
    public void ADeploymentNameSharedByAgentsStillRequiresDisambiguation()
    {
        RegisterAgent();
        RegisterAgent("agent-2", "Tmp2");
        RegisterDeployment();
        RegisterDeployment("deployment-2", "agent-2");

        var failure = Failure(deploymentName: "development");

        Assert.Equal("deployment-ambiguous", failure.ErrorKind);
        Assert.Contains("Tmp1", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Tmp2", failure.Message, StringComparison.Ordinal);
        Assert.Contains("--agent-id or --agent-name", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitIdentifiersRetainPrecedenceOverNames()
    {
        RegisterAgent();
        RegisterAgent("agent-2", "Tmp2");
        RegisterDeployment();
        RegisterDeployment("deployment-2", "agent-2");

        var resolved = Resolve(agentId: "AGENT-1", agentName: "Tmp2", deploymentId: "DEPLOYMENT-1", deploymentName: "missing-name");

        Assert.Equal("agent-1", resolved.Agent.AgentId);
        Assert.Equal("deployment-1", resolved.Deployment.DeploymentId);
    }

    [Fact]
    public void AUniqueDeploymentNameStillResolvesAcrossAgents()
    {
        RegisterAgent();
        RegisterAgent("agent-2", "Tmp2");
        RegisterDeployment("deployment-2", "agent-2", "unique-development");

        Assert.Equal("agent-2", Resolve(deploymentName: " UNIQUE-DEVELOPMENT ").Agent.AgentId);
    }

    [Fact]
    public void ASelectedDeploymentMustStillBelongToTheSelectedAgent()
    {
        RegisterAgent();
        RegisterAgent("agent-2", "Tmp2");
        RegisterDeployment();
        RegisterDeployment("deployment-2", "agent-2");

        Assert.Equal("deployment-not-found", Failure(agentId: "agent-1", deploymentId: "deployment-2").ErrorKind);
    }

    [Fact]
    public void OtherOperationsKeepTheirImplicitSingleDeploymentResolution()
    {
        RegisterAgent();
        RegisterDeployment();

        Assert.Equal("deployment-1", ManagedAgentFixtures.AgentResolver(_config).ResolveAgentDeployment(_workspace.Root, null, null, null, null).Deployment.DeploymentId);
    }

    [Fact]
    public void MissingConfigurationKeepsItsOwnDiagnostic()
    {
        var failure = Failure();

        Assert.Equal("config-not-found", failure.ErrorKind);
        Assert.Contains(_workspace.ConfigPath, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedConfigurationKeepsItsOwnDiagnostic()
    {
        _workspace.AddFile(Path.Combine(".config", "agent.config.json"), "not json");

        Assert.Equal("config-invalid", Failure().ErrorKind);
    }

    [Fact]
    public void AConfigurationWithoutAgentsDoesNotSuggestCreatingADeployment()
    {
        _workspace.AddFile(Path.Combine(".config", "agent.config.json"), JsonSerializer.Serialize(new AgentConfigDocument(), AgentConfigJsonContext.Default.AgentConfigDocument));

        var failure = Failure();

        Assert.Equal("agent-not-found", failure.ErrorKind);
        Assert.DoesNotContain("msagent deployment create", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeAgentDoesNotReceiveMcsDeploymentCreationInstructions()
    {
        RegisterAgent(agentType: AgentTypes.CodeAgent);

        var failure = Failure();

        Assert.Equal("not-an-mcs-agent", failure.ErrorKind);
        Assert.DoesNotContain("msagent deployment create", failure.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedAgentTypesDoNotRecommendMcsCreationForCodeAgents(bool codeAgentHasDeployment)
    {
        RegisterAgent();
        RegisterAgent("code-agent", "Code Agent", AgentTypes.CodeAgent);

        if (codeAgentHasDeployment)
        {
            ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, ManagedAgentFixtures.Deployment("code-deployment", "code-agent") with { AgentType = AgentTypes.CodeAgent });
        }

        var failure = Failure();

        Assert.Equal("deployment-records-empty", failure.ErrorKind);
        Assert.StartsWith(codeAgentHasDeployment ? "No deployment selected." : "None of the agents in this folder has deployment records.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Tmp1 (agent-1): no deployment records", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Code Agent (code-agent): CodeAgent", failure.Message, StringComparison.Ordinal);
        Assert.Contains("package-revision", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("--agent-id \"code-agent\"", failure.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACodeAgentDeploymentWithoutAnAgentRecordIsNotOfferedToTheOnlyMcsAgent(bool selectById)
    {
        RegisterAgent();
        RegisterOrphanCodeAgentDeployment();

        var failure = Failure(agentId: selectById ? "agent-1" : null);

        Assert.Equal("deployment-records-empty", failure.ErrorKind);
        Assert.Equal("Agent 'Tmp1' has no deployment records. Create a deployment before deploying.", failure.Message);
        Assert.Contains($"msagent deployment create --project \"{_workspace.Root}\" --agent-id \"agent-1\"", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("code-deployment", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("code-production", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeAgentDeploymentWithoutAnAgentRecordLeavesEveryMcsAgentWithoutRecords()
    {
        RegisterAgent();
        RegisterAgent("agent-2", "Tmp2");
        RegisterOrphanCodeAgentDeployment();

        var failure = Failure();

        Assert.Equal("deployment-records-empty", failure.ErrorKind);
        Assert.StartsWith("None of the agents in this folder has deployment records.", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Tmp1 (agent-1): no deployment records", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Tmp2 (agent-2): no deployment records", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("code-production", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("code-deployment", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("code-production", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void ACodeAgentDeploymentWithoutAnAgentRecordDoesNotHideTheMcsAgentsOwnDeployments()
    {
        RegisterAgent();
        RegisterDeployment();
        RegisterOrphanCodeAgentDeployment();

        var failure = Failure();

        Assert.Equal("deployment-selector-required", failure.ErrorKind);
        Assert.Equal($"No deployment selected for agent 'Tmp1'.{Environment.NewLine}Available deployments: development", failure.Message);
        Assert.Contains($"msagent agent deploy --project \"{_workspace.Root}\" --agent-id \"agent-1\" --deployment-id \"deployment-1\"", failure.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionCommandsQuoteValuesTheShellWouldOtherwiseExpand()
    {
        Assert.Equal(
            $"Select --deployment-id or --deployment-name for 'Tmp1', for example:{Environment.NewLine}  msagent agent deploy --project 'C:\\projects\\$HOME' --agent-id 'agent`1' --deployment-id 'deploy\"ment'",
            ManagedAgentException.DeploymentSelectionFailed(
                @"C:\projects\$HOME",
                [ManagedAgentFixtures.Agent("agent`1", "Tmp1")],
                [ManagedAgentFixtures.Deployment("deploy\"ment", "agent`1", "development")]).Remediation);
    }

    [Fact]
    public void CreationCommandsQuoteValuesTheShellWouldOtherwiseExpand()
    {
        Assert.Equal(
            $"Create a deployment for 'Tmp1':{Environment.NewLine}  msagent deployment create --project 'C:\\projects\\$HOME' --agent-id 'agent`1' --deployment-name development --deployment-type dev{Environment.NewLine}Then deploy:{Environment.NewLine}  msagent agent deploy --project 'C:\\projects\\$HOME' --agent-id 'agent`1' --deployment-name development",
            ManagedAgentException.DeploymentSelectionFailed(@"C:\projects\$HOME", [ManagedAgentFixtures.Agent("agent`1", "Tmp1")], []).Remediation);
    }

    [Theory]
    [InlineData(@"C:\Users\O'Brien\agent", @"""C:\Users\O'Brien\agent""")]
    [InlineData(@"C:\Users\O'Brien\$HOME", @"'C:\Users\O''Brien\$HOME'")]
    public void AnApostropheStaysLiteralInEitherQuoteStyle(string projectDirectory, string expected)
    {
        Assert.Contains(
            $"msagent deployment create --project {expected} --agent-id \"agent-1\"",
            ManagedAgentException.DeploymentSelectionFailed(projectDirectory, [ManagedAgentFixtures.Agent("agent-1", "Tmp1")], []).Remediation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleCodeAgentsKeepTheirOwnDeploymentGuidance()
    {
        RegisterAgent(agentType: AgentTypes.CodeAgent);
        RegisterAgent("agent-2", "Tmp2", AgentTypes.CodeAgent);

        Assert.Contains("package-revision", Failure().Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void DisplayNamesCannotInjectLinesOrTerminalControlsIntoGuidance()
    {
        RegisterAgent(name: "Tmp1\r\nSpoofed\u001b[31m");

        var failure = Failure();

        Assert.Contains("Tmp1\uFFFD\uFFFDSpoofed\uFFFD[31m", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Tmp1\r\n", failure.Remediation, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", failure.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrdinaryFailuresStillSanitizeEmbeddedLineBreaks(bool color)
    {
        var stderr = new StringWriter();

        Assert.Equal(ExitCodes.Usage, new CliOutputRenderer(TextWriter.Null, stderr, color).RenderFailure(new UsageException("Untrusted\r\nInjected\u001b[2J"), json: false));
        Assert.Equal(
            $"{(color ? "\u001b[31m" : string.Empty)}Untrusted\uFFFD\uFFFDInjected\uFFFD[2J{(color ? "\u001b[0m" : string.Empty)}{Environment.NewLine}",
            stderr.ToString());
    }

    [Fact]
    public void FormattedGuidanceSanitizesEveryDynamicFieldBeforeAddingLines()
    {
        var failure = ManagedAgentException.DeploymentSelectionFailed(
            "project\r\ninjected",
            [ManagedAgentFixtures.Agent("agent\r\ninjected", "agent-name\r\ninjected")],
            [ManagedAgentFixtures.Deployment("deployment\r\ninjected", "agent\r\ninjected", "deployment-name\r\ninjected")],
            "selector\r\ninjected");
        var stderr = new StringWriter();

        Assert.True(failure.HasMultilineGuidance);
        Assert.Equal(ExitCodes.Usage, new CliOutputRenderer(TextWriter.Null, stderr, color: false).RenderFailure(failure, json: false));
        Assert.Contains("selector\uFFFD\uFFFDinjected", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent-name\uFFFD\uFFFDinjected", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("deployment-name\uFFFD\uFFFDinjected", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("project\uFFFD\uFFFDinjected", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("agent\uFFFD\uFFFDinjected", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("deployment\uFFFD\uFFFDinjected", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\r\ninjected", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains($"{Environment.NewLine}  msagent agent deploy", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task TheCommandRendersRecordAwareErrorsWithEitherFeatureSetting(bool json, bool amsEnabled, bool hasDeployment)
    {
        RegisterAgent();

        if (hasDeployment)
        {
            RegisterDeployment();
        }

        var expected = Failure();
        var snapshot = Snapshot();
        var operations = new FakeOperations
        {
            OnManagedAgentDeploy = request =>
            {
                Resolve(request.AgentId, request.AgentName, request.DeploymentId, request.DeploymentName);
                throw new InvalidOperationException("Deployment selection must fail before deploying.");
            },
        };
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string[] arguments = ["agent", "deploy", "--project", _workspace.Root];

        if (json)
        {
            arguments = [.. arguments, "--json"];
        }

        var exitCode = await new AgentHostCli(
            operations,
            featureFlags: amsEnabled ? new TestFeatureFlags(FeatureName.AmsFeatures) : new TestFeatureFlags())
            .InvokeAsync(arguments, stdout, stderr, color: false);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.NotNull(operations.LastManagedAgentDeployRequest);
        Assert.Null(operations.LastDeployRequest);

        if (json)
        {
            using var document = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(expected.ErrorKind, document.RootElement.GetProperty("errorKind").GetString());
            Assert.Equal(expected.Message, document.RootElement.GetProperty("errorMessage").GetString());
            Assert.Equal(expected.Remediation, document.RootElement.GetProperty("remediation").GetString());
            Assert.Equal(string.Empty, stderr.ToString());
        }
        else
        {
            Assert.Contains(expected.Message, stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains(expected.Remediation!, stderr.ToString(), StringComparison.Ordinal);
        }

        AssertUnchanged(snapshot);
    }

    [Fact]
    public void TheCurrentDirectoryCanDiscoverAnEnclosingConfiguration()
    {
        RegisterAgent();
        var nestedDirectory = Path.Combine(_workspace.Root, "nested");
        Directory.CreateDirectory(nestedDirectory);
        using var scope = new CurrentDirectoryScope(nestedDirectory);

        var failure = Assert.Throws<ManagedAgentException>(() =>
        {
            var resolver = ManagedAgentFixtures.AgentResolver(_config);
            resolver.ResolveAgentDeployment(resolver.ResolveProjectDirectory(null), null, null, null, null, requireExplicitDeployment: true);
        });

        Assert.Equal("deployment-records-empty", failure.ErrorKind);
        Assert.Contains($"--project \"{_workspace.Root}\"", failure.Remediation, StringComparison.Ordinal);
    }

    private void RegisterAgent(string agentId = "agent-1", string name = "Tmp1", string agentType = AgentTypes.McsAgent) =>
        _config.Add(_workspace.Root, ManagedAgentFixtures.Agent(agentId, name) with { AgentType = agentType, AgentLocation = agentId });

    private void RegisterDeployment(string deploymentId = "deployment-1", string agentId = "agent-1", string name = "development") =>
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, ManagedAgentFixtures.Deployment(deploymentId, agentId, name));

    private void RegisterOrphanCodeAgentDeployment() =>
        ((IAgentDeploymentRegistry)_config).Add(_workspace.Root, ManagedAgentFixtures.Deployment("code-deployment", "remote-code-agent", "code-production", DeploymentStages.Prod) with { AgentType = AgentTypes.CodeAgent });

    private (ManagedAgentRecord Agent, AgentDeploymentRecord Deployment) Resolve(string? agentId = null, string? agentName = null, string? deploymentId = null, string? deploymentName = null) =>
        ManagedAgentFixtures.AgentResolver(_config).ResolveAgentDeployment(_workspace.Root, agentId, agentName, deploymentId, deploymentName, requireExplicitDeployment: true);

    private ManagedAgentException Failure(string? agentId = null, string? agentName = null, string? deploymentId = null, string? deploymentName = null)
    {
        var snapshot = Snapshot();
        var failure = Assert.Throws<ManagedAgentException>(() => Resolve(agentId, agentName, deploymentId, deploymentName));

        AssertUnchanged(snapshot);

        return failure;
    }

    private Dictionary<string, byte[]> Snapshot() =>
        Directory.EnumerateFiles(_workspace.Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

    private void AssertUnchanged(Dictionary<string, byte[]> snapshot)
    {
        Assert.Equal(snapshot.Keys.Order(), Directory.EnumerateFiles(_workspace.Root, "*", SearchOption.AllDirectories).Order());

        foreach (var file in snapshot)
        {
            Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        }
    }
}
