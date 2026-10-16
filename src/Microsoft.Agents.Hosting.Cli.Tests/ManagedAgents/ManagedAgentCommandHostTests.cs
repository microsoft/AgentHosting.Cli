using System.CommandLine;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Commands.Common;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ManagedAgentCommandHostTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(IAgentHostingOperations operations, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(
            operations,
            featureFlags: TestFeatureFlags.AllProductSurfaces)
            .InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static FakeOperations McsOperations() => new()
    {
        OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = DeploymentAgentTypeSource.DeploymentRecord },
    };

    [Fact]
    public void Create_WithMcsAgentType_BindsEveryMcsOption()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(
            operations,
            "agent", "create",
            "--agent-type", "MCSAgent",
            "--agent-name", "Contoso Agent",
            "--description", "helpdesk",
            "--schema-name", "con_agent",
            "--mcs-agent-type", "Classic",
            "--init-template", "minimal",
            "--initial-prompt", "Be helpful.");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(operations.LastCreateRequest);

        var request = Assert.IsType<ManagedAgentCreateRequest>(operations.LastManagedAgentCreateRequest);
        Assert.Equal("Contoso Agent", request.Name);
        Assert.Equal("helpdesk", request.Description);
        Assert.Equal("con_agent", request.SchemaName);
        Assert.Equal("Classic", request.McsAgentType);
        Assert.Equal("minimal", request.InitTemplate);
        Assert.Equal("Be helpful.", request.InitialPrompt);
    }

    [Fact]
    public void Create_WithMcsAlias_TakesTheMcsPath()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "create", "--agent-type", "mcs", "--agent-name", "Contoso Agent");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastManagedAgentCreateRequest);
    }

    [Fact]
    public void Create_BindsTheCloneSelectors()
    {
        var operations = new FakeOperations();

        Run(
            operations,
            "agent", "create",
            "--agent-type", "MCSAgent",
            "--agent-name", "Fork",
            "--mcs-clone-agent-schemaname", "src_agent",
            "--mcs-clone-environment-id", "env-src");

        Assert.Equal("src_agent", operations.LastManagedAgentCreateRequest!.CloneSchemaName);
        Assert.Equal("env-src", operations.LastManagedAgentCreateRequest.CloneEnvironmentId);
    }

    [Fact]
    public void Create_WithMcsAgentTypeAndLanguage_IsAUsageError()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso Agent", "--language", "dotnet");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastManagedAgentCreateRequest);
        Assert.Contains("CodeAgent only", stderr, StringComparison.Ordinal);
    }


    [Fact]
    public void ShortAliases_AreUniqueInTheRealCommandTree()
    {
        var operations = new FakeOperations();

        foreach (var command in new[] { "show", "deploy", "delete", "pull", "create" })
        {
            var (_, stdout, _) = Run(operations, "agent", command, "--help");
            var aliases = System.Text.RegularExpressions.Regex.Matches(stdout, @"(?m)^\s+(-[a-z]{1,6})[,\s]")
                .Select(match => match.Groups[1].Value)
                .ToArray();

            Assert.Equal(aliases.Length, aliases.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void ShortAliases_AreUniqueAcrossTheCatalog()
    {
        var duplicates = OptionCatalog.DeclaredAliases
            .GroupBy(entry => entry.Value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        Assert.Empty(duplicates);
    }

    [Fact]
    public void ShortAliases_NeverShadowHelp()
    {
        Assert.DoesNotContain("-h", OptionCatalog.DeclaredAliases.Values, StringComparer.Ordinal);
    }

    [Fact]
    public void Completion_IsNotACommand()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), "completion");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Unrecognized command", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_AcceptsTheShortAliases()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "create", "-at", "MCSAgent", "-an", "Contoso Agent", "-d", "a description", "--initial-prompt", "hello");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Contoso Agent", operations.LastManagedAgentCreateRequest?.Name);
        Assert.Equal("a description", operations.LastManagedAgentCreateRequest?.Description);
    }

    [Fact]
    public void Show_AcceptsTheShortAgentSelector()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "show", "-aid", "agent-1", "-p", "../elsewhere");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastManagedAgentShowRequest?.AgentId);
        Assert.Equal("../elsewhere", operations.LastManagedAgentShowRequest?.Project);
    }

    [Fact]
    public void Create_WithMcsAgentTypeAndIdempotencyKey_IsAUsageError()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso Agent", "--idempotency-key", "key-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastManagedAgentCreateRequest);
        Assert.Contains("CodeAgent only", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_OnTheCodeAgentPath_RejectsTheProjectDirectory()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "create", "--agent-name", "Contoso Agent", "--language", "dotnet", "--language-version", "10", "--project", "../elsewhere");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastCreateRequest);
        Assert.Contains("--project", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--schema-name", "con_agent")]
    [InlineData("--mcs-agent-type", "Classic")]
    [InlineData("--initial-prompt", "hello")]
    [InlineData("--mcs-clone-agent-id", "9a1b")]
    [InlineData("--mcs-clone-agent-schemaname", "cr_agent")]
    [InlineData("--mcs-clone-environment-id", "env-2")]
    public void Create_OnTheCodeAgentPath_RejectsMcsOptions(string option, string value)
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "create", "--agent-name", "Contoso Agent", "--language", "dotnet", "--language-version", "net10.0", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastCreateRequest);
        Assert.Contains(option, stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--mcs-clone-agent-id", "9a1b")]
    [InlineData("--mcs-clone-agent-schemaname", "cr_agent")]
    [InlineData("--mcs-clone-environment-id", "env-2")]
    public void Create_WithACloneOptionAndNoAgentType_RoutesToMcs(string option, string value)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "create", option, value);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(AgentTypes.McsAgent, operations.LastManagedAgentCreateRequest?.AgentType);
        Assert.Null(operations.LastCreateRequest);
    }

    [Fact]
    public void Create_WithAnExplicitCodeAgentType_StillRejectsCloneOptions()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "create", "--agent-type", "CodeAgent", "--agent-name", "Contoso Agent", "--mcs-clone-agent-id", "9a1b");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastCreateRequest);
        Assert.Null(operations.LastManagedAgentCreateRequest);
        Assert.Contains("--mcs-clone-agent-id applies to MCS agents only", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_OnTheCodeAgentPath_StillRequiresLanguage()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "create", "--agent-name", "Contoso Agent");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastCreateRequest);
        Assert.Contains("--language", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("Code")]
    [InlineData("MCS Agent")]
    public void Create_WithAnUnknownAgentType_IsAUsageError(string agentType)
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "create", "--agent-type", agentType, "--agent-name", "Contoso Agent", "--language", "dotnet", "--language-version", "net10.0");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastCreateRequest);
        Assert.Null(operations.LastManagedAgentCreateRequest);
        Assert.Contains(agentType, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_BindsTheTargetEnvironment()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso Agent", "--environment-id", "env-2");

        Assert.Equal("env-2", operations.LastManagedAgentCreateRequest?.EnvironmentId);
    }

    [Theory]
    [InlineData("--agent-id", "agent-1")]
    [InlineData("--agent-name", "Contoso")]
    [InlineData("--schema-name", "con_agent")]
    public void Deploy_WithAProjectAndAnMcsOption_DefersToRecordAwareValidation(string option, string value)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "deploy", "--project", ".", option, value);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(".", operations.LastManagedAgentDeployRequest?.Project);
        Assert.Equal(value, option switch
        {
            "--agent-id" => operations.LastManagedAgentDeployRequest?.AgentId,
            "--agent-name" => operations.LastManagedAgentDeployRequest?.AgentName,
            _ => operations.LastManagedAgentDeployRequest?.SchemaName,
        });
        Assert.Null(operations.LastDeployRequest);
    }

    [Theory]
    [InlineData("--deployment-name", "dev")]
    [InlineData("--deployment-id", "slot-1")]
    public void Deploy_WithAProjectAndADeploymentSelector_TakesTheMcsPath(string option, string value)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "deploy", "--project", "../elsewhere", option, value);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("../elsewhere", operations.LastManagedAgentDeployRequest?.Project);
        Assert.Null(operations.LastDeployRequest);
    }

    [Theory]
    [InlineData("--publish")]
    [InlineData("--overwrite")]
    public void Deploy_WithAProjectAndAnMcsFlag_DefersToRecordAwareValidation(string flag)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "deploy", "--project", ".", flag);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(".", operations.LastManagedAgentDeployRequest?.Project);
        Assert.True(flag == "--publish" ? operations.LastManagedAgentDeployRequest?.Publish : operations.LastManagedAgentDeployRequest?.Overwrite);
        Assert.Null(operations.LastDeployRequest);
    }

    [Fact]
    public void Deploy_WithAProjectAlone_DefersToRecordAwareMcsValidation()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "deploy", "--project", ".");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(".", operations.LastManagedAgentDeployRequest?.Project);
        Assert.Null(operations.LastDeployRequest);
        Assert.Null(operations.LastPackageUploadRequest);
    }

    [Fact]
    public void Deploy_RendersTheUploadedFileCount()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "deploy", "--deployment-name", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("2 file(s) uploaded", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_BindsTheProjectDirectory()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso Agent", "--project", "../elsewhere");

        Assert.Equal("../elsewhere", operations.LastManagedAgentCreateRequest?.Project);
    }

    [Fact]
    public void List_BindsTheProjectDirectory()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "list", "--project", "../elsewhere");

        Assert.Equal("../elsewhere", operations.LastManagedAgentListRequest?.Project);
    }

    [Fact]
    public void Delete_BindsTheProjectDirectory()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "delete", "--project", "../elsewhere", "--agent-id", "agent-1");

        Assert.Equal("../elsewhere", operations.LastManagedAgentDeleteRequest?.Project);
    }

    [Fact]
    public void DeploymentCreate_BindsTheProjectDirectory()
    {
        var operations = McsOperations();

        Run(operations, "deployment", "create", "--project", "../elsewhere", "--agent-id", "agent-1", "--deployment-type", "dev", "--deployment-name", "dev");

        Assert.Equal("../elsewhere", operations.LastAgentDeploymentCreateRequest?.Project);
    }

    [Fact]
    public void DeploymentList_BindsTheProjectDirectory()
    {
        var operations = McsOperations();

        Run(operations, "deployment", "list", "--project", "../elsewhere", "--agent-id", "agent-1");

        Assert.Equal("../elsewhere", operations.LastAgentDeploymentListRequest?.Project);
    }

    [Fact]
    public void DeploymentUpdateConnection_BindsTheProjectDirectory()
    {
        var operations = McsOperations();

        Run(operations, "deployment", "connection", "update", "--project", "../elsewhere", "--agent-id", "agent-1", "--deployment-name", "dev");

        Assert.Equal("../elsewhere", operations.LastAgentDeploymentConnectionRequest?.Project);
    }

    [Fact]
    public void Show_BindsItsOptions()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "show", "--project", "../elsewhere", "--agent-id", "agent-1", "--agent-name", "Contoso");

        Assert.Equal("../elsewhere", operations.LastManagedAgentShowRequest?.Project);
        Assert.Equal("agent-1", operations.LastManagedAgentShowRequest?.AgentId);
        Assert.Equal("Contoso", operations.LastManagedAgentShowRequest?.AgentName);
    }

    [Fact]
    public void Show_WithNoSelector_StillDispatches()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "show");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastManagedAgentShowRequest);
    }

    [Fact]
    public void Show_EmitsAParseableJsonEnvelope()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "show", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Show_RendersTheDeploymentsUnderTheirAgent()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "show");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Contoso Agent (agent-1)", stdout, StringComparison.Ordinal);
        Assert.Contains("deployments    1", stdout, StringComparison.Ordinal);
        Assert.Contains("dev (slot-1)", stdout, StringComparison.Ordinal);
        Assert.Contains("homeEnvironmentId env-1", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_RendersOneTotalForComponentsAndConnectors()
    {
        var operations = new FakeOperations
        {
            OnManagedAgentDeploy = _ => new ManagedAgentDeployResult
            {
                AgentId = "agent-1",
                DisplayName = "Contoso Agent",
                DeploymentId = "slot-1",
                DeploymentName = "dev",
                DeploymentType = "dev",
                Action = "push",
                TargetEnvironmentId = "env-1",
                TargetMcsAgentId = "cloud-1",
                SchemaName = "con_agent",
                PushedComponentCount = 3,
                AuxiliaryChangeCount = 2,
                DeployedOn = DateTimeOffset.UnixEpoch,
            },
        };

        var (exitCode, stdout, _) = Run(operations, "agent", "deploy", "-aid", "agent-1", "-dn", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("pushed     5", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("connector/flow/prompt", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_RendersTheAgentIdAsItsOwnField()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "show");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("agentId        agent-1", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_RendersTheOwningAgentIdOnEachDeployment()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "show");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("agentId           agent-1", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_RendersWhereEachAgentLives()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "show");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains($"agentLocation  {AgentConfigLocator.CurrentDirectoryLocation}", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_JsonEnvelope_CarriesTheDeployments()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "agent", "show", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var deployment = document.RootElement.GetProperty("deployments").EnumerateArray().Single();
        Assert.Equal("slot-1", deployment.GetProperty("deploymentId").GetString());
        Assert.Equal("agent-1", deployment.GetProperty("agentId").GetString());
    }

    [Fact]
    public void Create_McsJsonEnvelope_CarriesTheNextSteps()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso Agent", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("agent-created", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("scaffold", document.RootElement.GetProperty("createdFrom").GetString());
    }

    [Fact]
    public void Create_McsHumanOutput_DoesNotClaimTheAgentExistsRemotely()
    {
        var (_, stdout, _) = Run(new FakeOperations(), "agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso Agent");

        Assert.Contains("Created", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("deployed", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("published", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Copilot Studio", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Create_McsHumanOutput_SuggestsNoFollowUpCommands()
    {
        var (_, stdout, _) = Run(new FakeOperations(), "agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso Agent");

        Assert.DoesNotContain("Next:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("msagent deployment create", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("msagent agent deploy", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Init_BindsItsOptions()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "init", "--agent-name", "Contoso Agent", "--mcs-agent-source", "../other");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Contoso Agent", operations.LastManagedAgentInitRequest!.Name);
        Assert.Equal("../other", operations.LastManagedAgentInitRequest.McsAgentSource);
    }

    [Fact]
    public void Init_WithoutANameIsAUsageError()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "init");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastManagedAgentInitRequest);
    }

    [Fact]
    public void Init_RejectsCorrelationId()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "init", "--agent-name", "Contoso Agent", "--correlation-id", "abc");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastManagedAgentInitRequest);
    }

    [Fact]
    public void List_BindsItsOptions()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "list", "--environment-id", "env-2", "--limit", "5");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("env-2", operations.LastManagedAgentListRequest!.EnvironmentId);
        Assert.Equal(5, operations.LastManagedAgentListRequest.Limit);
        Assert.False(operations.LastManagedAgentListRequest.Tenant);
    }

    [Fact]
    public void List_WithANonNumericLimitIsAUsageError()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "list", "--limit", "many");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastManagedAgentListRequest);
    }

    [Fact]
    public void Pull_BindsItsOptions()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "pull", "--project", "./here", "--agent-id", "agent-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("./here", operations.LastManagedAgentPullRequest!.Project);
        Assert.Equal("agent-1", operations.LastManagedAgentPullRequest.AgentId);
    }

    [Fact]
    public void Delete_PassesNonInteractiveThrough()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "delete", "--agent-id", "agent-1", "--non-interactive");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(operations.LastManagedAgentDeleteRequest!.NonInteractive);
    }

    [Fact]
    public void Delete_BindsTheDeploymentSelector()
    {
        var operations = new FakeOperations();

        Run(operations, "agent", "delete", "--agent-name", "Contoso Agent", "--deployment-name", "prod");

        Assert.Equal("Contoso Agent", operations.LastManagedAgentDeleteRequest!.AgentName);
        Assert.Equal("prod", operations.LastManagedAgentDeleteRequest.DeploymentName);
    }

    [Fact]
    public void DeploymentCreate_BindsItsOptions()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(
            operations,
            "deployment", "create",
            "--agent-id", "agent-1",
            "--deployment-type", "prod",
            "--deployment-name", "prod-west",
            "--environment-id", "env-prod");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastAgentDeploymentCreateRequest!.AgentId);
        Assert.Equal("prod", operations.LastAgentDeploymentCreateRequest.DeploymentType);
        Assert.Equal("prod-west", operations.LastAgentDeploymentCreateRequest.DeploymentName);
        Assert.Equal("env-prod", operations.LastAgentDeploymentCreateRequest.EnvironmentId);
    }

    [Fact]
    public void DeploymentCreate_WithoutRequiredOptionsIsAUsageError()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "create", "--agent-id", "agent-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastAgentDeploymentCreateRequest);
    }

    [Fact]
    public void DeploymentList_BindsTheAgentId()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "list", "--agent-id", "agent-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastAgentDeploymentListRequest!.AgentId);
    }

    [Fact]
    public void DeploymentList_WithoutAnAgentIdListsEveryAgent()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "list");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastAgentDeploymentListRequest);
        Assert.Null(operations.LastAgentDeploymentListRequest?.AgentId);
    }

    [Fact]
    public void DeploymentUpdateConnection_BindsItsOptions()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(
            operations,
            "deployment", "connection", "update",
            "--agent-id", "agent-1",
            "--agent-name", "Contoso Helpdesk",
            "--deployment-name", "prod-west",
            "--schema-name", "cr123_override");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastAgentDeploymentConnectionRequest!.AgentId);
        Assert.Equal("Contoso Helpdesk", operations.LastAgentDeploymentConnectionRequest.AgentName);
        Assert.Equal("prod-west", operations.LastAgentDeploymentConnectionRequest.DeploymentName);
        Assert.Equal("cr123_override", operations.LastAgentDeploymentConnectionRequest.SchemaName);
    }

    [Fact]
    public void DeploymentDelete_BindsEveryOption()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "delete", "-aid", "agent-1", "-dn", "dev", "--delete-deployed-mcs-agent", "-p", "../elsewhere");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastAgentDeploymentDeleteRequest?.AgentId);
        Assert.Equal("dev", operations.LastAgentDeploymentDeleteRequest?.DeploymentName);
        Assert.True(operations.LastAgentDeploymentDeleteRequest?.DeleteDeployedMcsAgent);
        Assert.Equal("../elsewhere", operations.LastAgentDeploymentDeleteRequest?.Project);
    }

    [Fact]
    public void DeploymentDelete_DefaultsToKeepingTheCloudAgent()
    {
        var operations = McsOperations();

        Run(operations, "deployment", "delete", "-aid", "agent-1", "-did", "slot-1");

        Assert.False(operations.LastAgentDeploymentDeleteRequest?.DeleteDeployedMcsAgent);
    }

    [Fact]
    public void DeploymentDelete_CarriesTheNonInteractiveFlag()
    {
        var operations = McsOperations();

        Run(operations, "deployment", "delete", "-aid", "agent-1", "-dn", "dev", "--non-interactive");

        Assert.True(operations.LastAgentDeploymentDeleteRequest?.NonInteractive);
    }

    [Fact]
    public void DeploymentShow_BindsEverySelector()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "show", "-an", "Contoso Agent", "-dn", "dev", "-p", "../elsewhere");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Contoso Agent", operations.LastAgentDeploymentShowRequest?.AgentName);
        Assert.Equal("dev", operations.LastAgentDeploymentShowRequest?.DeploymentName);
        Assert.Equal("../elsewhere", operations.LastAgentDeploymentShowRequest?.Project);
    }

    [Fact]
    public void DeploymentShow_CarriesTheAgentNameAlongsideTheAgentId()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "show", "--agent-id", "agent-1", "--agent-name", "Contoso Agent");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastAgentDeploymentShowRequest?.AgentId);
        Assert.Equal("Contoso Agent", operations.LastAgentDeploymentShowRequest?.AgentName);
    }

    [Fact]
    public void DeploymentUpdate_BindsTheRename()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "update", "-aid", "agent-1", "-dn", "dev", "--new-name", "Shared development", "-p", "../elsewhere");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastAgentDeploymentUpdateRequest?.AgentId);
        Assert.Equal("dev", operations.LastAgentDeploymentUpdateRequest?.DeploymentName);
        Assert.Equal("Shared development", operations.LastAgentDeploymentUpdateRequest?.NewName);
        Assert.Equal("../elsewhere", operations.LastAgentDeploymentUpdateRequest?.Project);
    }

    [Fact]
    public void DeploymentUpdateConnection_StillRoutesToTheConnectionVerb()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "connection", "update", "-aid", "agent-1", "-dn", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastAgentDeploymentConnectionRequest?.AgentId);
        Assert.Null(operations.LastAgentDeploymentUpdateRequest);
    }

    [Fact]
    public void DeploymentUpdateConnection_BindsTheSelectionOptions()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "connection", "update", "-aid", "agent-1", "-dn", "dev", "--connection-reference", "cr_sql", "--connector", "shared_sql", "--connection-id", "conn-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("cr_sql", operations.LastAgentDeploymentConnectionRequest?.ConnectionReference);
        Assert.Equal("shared_sql", operations.LastAgentDeploymentConnectionRequest?.Connector);
        Assert.Equal("conn-1", operations.LastAgentDeploymentConnectionRequest?.ConnectionId);
    }

    [Theory]
    [InlineData("--role")]
    [InlineData("--user")]
    public void AgentShareShow_RejectsParentOptionsPlacedBeforeIt(string option)
    {
        var operations = McsOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "share", option, "use", "show", "-aid", "agent-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains($"{option} does not apply to `msagent agent share show`", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentShareShowRequest);
        Assert.Null(operations.LastManagedAgentShareRequest);
    }

    [Fact]
    public void DeploymentConnectionUpdate_IsNoLongerNestedUnderDeploymentUpdate()
    {
        var operations = McsOperations();

        var (exitCode, _, stderr) = Run(operations, "deployment", "update", "--new-name", "value", "connection", "-aid", "agent-1", "-dn", "dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Unrecognized command or argument", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentConnectionRequest);
    }

    [Fact]
    public void DeploymentUpdate_StillAcceptsItsOwnOptionsBeforeTheVerb()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "update", "--new-name", "Renamed", "-aid", "agent-1", "-dn", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Renamed", operations.LastAgentDeploymentUpdateRequest?.NewName);
    }

    [Theory]
    [InlineData("--resource-group")]
    [InlineData("--subscription")]
    [InlineData("--tenant")]
    public void DeploymentUpdateConnection_RejectsCodeAgentOnlyOptions(string option)
    {
        var operations = McsOperations();

        var (exitCode, _, stderr) = Run(operations, "deployment", "connection", "update", "-aid", "agent-1", "-dn", "dev", option, "value");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Unrecognized command or argument", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastAgentDeploymentConnectionRequest);
    }

    [Fact]
    public void DeploymentUpdateConnection_BindsEveryRecordSelector()
    {
        var operations = McsOperations();

        var (exitCode, _, _) = Run(operations, "deployment", "connection", "update", "--project", "../elsewhere", "--agent-id", "agent-1", "--agent-name", "Contoso Agent", "--deployment-id", "slot-1", "--deployment-name", "dev", "--schema-name", "contoso_agent");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("../elsewhere", operations.LastAgentDeploymentConnectionRequest?.Project);
        Assert.Equal("agent-1", operations.LastAgentDeploymentConnectionRequest?.AgentId);
        Assert.Equal("Contoso Agent", operations.LastAgentDeploymentConnectionRequest?.AgentName);
        Assert.Equal("slot-1", operations.LastAgentDeploymentConnectionRequest?.DeploymentId);
        Assert.Equal("dev", operations.LastAgentDeploymentConnectionRequest?.DeploymentName);
        Assert.Equal("contoso_agent", operations.LastAgentDeploymentConnectionRequest?.SchemaName);
    }

    [Theory]
    [InlineData("show")]
    [InlineData("update")]
    [InlineData("delete")]
    public void DeploymentVerbsThatRoute_StillAcceptTheAzureCoordinates(string verb)
    {
        var (exitCode, _, stderr) = Run(McsOperations(), "deployment", verb, "-aid", "agent-1", "-dn", "dev", "--resource-group", "rg", "--subscription", "sub", "--tenant", "t");

        Assert.DoesNotContain("Unrecognized command or argument", stderr, StringComparison.Ordinal);
        Assert.NotEqual(ExitCodes.Usage, exitCode);
    }

    [Fact]
    public void TypedFailures_KeepTheirExitCodeAndErrorKind()
    {
        var operations = new FakeOperations
        {
            OnManagedAgentCreate = _ => throw ManagedAgentException.ConfigNotFound("C:\\src\\.config\\agent.config.json"),
        };

        var (exitCode, stdout, _) = Run(operations, "agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso Agent", "--json");

        Assert.Equal(ExitCodes.Usage, exitCode);

        using var document = JsonDocument.Parse(stdout);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("config-not-found", document.RootElement.GetProperty("errorKind").GetString());
    }
}
