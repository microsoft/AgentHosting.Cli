using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Command-host tests for the <c>msagent deployment</c> command tree — create/show/update/delete —
/// driven through the full <see cref="AgentHostCli"/> parse →
/// dispatch → render → exit pipeline with a <see cref="FakeOperations"/> facade and in-memory writers.
/// They assert the CLI surface only (flag binding, the <c>--deployment</c> | <c>--agent</c>+<c>--name</c>
/// addressing modes, az-style required-option aggregation, help splitting, and the exit taxonomy) —
/// no MSAL, broker, browser, or Azure transport is reachable. The
/// real-service integration of <c>deployment create</c> lives in <see cref="DeploymentCommandTests"/>.
/// </summary>
public class DeploymentCommandHostTests
{
    private const string AmsAgentId = "44444444-4444-4444-4444-444444444444";
    private const string AmsDeploymentId = "77777777-7777-7777-7777-777777777777";

    private static (int ExitCode, string Stdout, string Stderr) Run(
        FakeOperations operations,
        bool color,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(
            operations,
            featureFlags: TestFeatureFlags.AllProductSurfaces);
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
    [Fact]
    public void DeploymentHelp_ListsLifecycleCommands()
    {
        var (exitCode, stdout, stderr) = Run(new FakeOperations(), color: false, "deployment", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);

        // Lifecycle verbs listed under "Commands:".
        Assert.Contains("Commands:", stdout);

        foreach (var verb in new[] { "create", "delete", "show", "update" })
        {
            Assert.Contains(verb, stdout);
        }

        // `get` is a working-but-hidden verb: it must not appear in help.
        Assert.DoesNotContain("get <deployment-id>", stdout);
        // Usage line still carries the packaged tool name, not the assembly name, and advertises subgroups.
        Assert.Contains("msagent deployment [subgroup] [command] [options]", stdout);
    }

    [Fact]
    public void DeploymentCreate_WithoutAnAgentSelector_NamesTheSelector_ExitTwo()
    {
        var (exitCode, stdout, stderr) = Run(new FakeOperations(), color: false, "deployment", "create");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("--agent-id or --agent-name", stderr);
        Assert.DoesNotContain("AzureBotServiceFeatures", stderr);
    }

    [Fact]
    public void DeploymentCreate_MissingRequired_AggregatesAll_AzStyle_ExitTwo()
    {
        var (exitCode, stdout, stderr) = Run(new FakeOperations(), color: false, "deployment", "create", "--agent-id", "agent-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("--deployment-name", stderr);
        Assert.Contains("--deployment-type", stderr);
        Assert.Contains("--entra-id", stderr);
    }

    [Fact]
    public void DeploymentCreate_WithResourceGroup_RequiresEntraId_ExitTwo()
    {
        var (exitCode, _, stderr) = Run(
            new FakeOperations(),
            color: false,
            "deployment", "create",
            "--agent-id", "a",
            "--deployment-name", "n",
            "--deployment-type", "Dev",
            "--resource-group", "rg1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--entra-id", stderr);
    }

    [Fact]
    public void DeploymentCreate_WithoutResourceGroup_IsAccepted_NotAUsageError()
    {
        // --resource-group is optional at the CLI layer (auto-resolved by the backend when the
        // subscription has exactly one), so omitting it must not be a missing-required usage error.
        var (exitCode, _, stderr) = Run(
            new FakeOperations(),
            color: false,
            "deployment", "create",
            "--agent-id", "a",
            "--deployment-name", "n",
            "--deployment-type", "Dev",
            "--entra-id", "app-1",
            "--entra-id-type", "appReg");

        Assert.NotEqual(ExitCodes.Usage, exitCode);
        Assert.DoesNotContain("required arguments are missing", stderr);
    }

    // Addressing validation for show/update/delete now lives in DeploymentService (via
    // DeploymentRequestValidator), not the CLI, so these rules are covered by direct validator unit tests
    // in DeploymentRequestValidatorTests.

    [Fact]
    public void DeploymentShow_ByAgentAndName_PassesRawAddressingThrough()
    {
        var ops = new FakeOperations();

        var (exitCode, _, stderr) = Run(
            ops, color: false, "deployment", "show",
            "--agent-id", "agent-x", "--deployment-name", "dev", "--resource-group", "rg-x");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        // The CLI is pure plumbing: it passes the raw --agent/--name (and optional --resource-group)
        // straight through; DeploymentService resolves the deterministic id and runs its live sync/import.
        var request = Assert.IsType<DeploymentShowRequest>(ops.LastGetDeploymentRequest);
        Assert.Null(request.DeploymentId);
        Assert.Equal("agent-x", request.AgentId);
        Assert.Equal("dev", request.DeploymentName);
        Assert.Equal("rg-x", request.ResourceGroup);
    }

    [Fact]
    public void DeploymentShow_ByDeploymentId_PassesNullAgentName()
    {
        var ops = new FakeOperations();

        var (exitCode, _, stderr) = Run(ops, color: false, "deployment", "show", "--deployment-id", "dep-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("dep-1", ops.LastGetDeploymentId);
        Assert.Null(ops.LastGetDeploymentAgent);
        Assert.Null(ops.LastGetDeploymentName);
    }

    [Fact]
    public void DeploymentShow_Json_NestsDeploymentAndOmitsMessage()
    {
        var ops = new FakeOperations();

        var (jsonExit, jsonStdout, _) = Run(ops, color: false, "deployment", "show", "--deployment-id", "dep-77", "--json");
        var (humanExit, humanStdout, _) = Run(ops, color: false, "deployment", "show", "--deployment-id", "dep-77");

        Assert.Equal(ExitCodes.Success, jsonExit);
        Assert.Equal(ExitCodes.Success, humanExit);

        using var doc = JsonDocument.Parse(jsonStdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());

        // Show mirrors create: the deployment is nested under "deployment" (not flattened into the envelope).
        var deployment = root.GetProperty("deployment");
        Assert.Equal("dep-77", deployment.GetProperty("deploymentId").GetString());
        Assert.False(root.TryGetProperty("deploymentId", out _));

        // Unlike create, a read carries no "message".
        Assert.False(root.TryGetProperty("message", out _));

        // The human view is the same deployment object dumped as pretty JSON.
        Assert.Contains("\"deploymentId\": \"dep-77\"", NormalizeLines(humanStdout));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentShow_JsonOmitsArmResponse_AndDebugPreservesNormalOutput(bool json)
    {
        using var armDoc = JsonDocument.Parse("{\"id\":\"/bot\",\"properties\":{\"endpoint\":\"https://x\"}}");
        var arm = armDoc.RootElement.Clone();
        var ops = new FakeOperations
        {
            OnGetDeployment = request => new DeploymentResult<CodeAgentDeployment>
            {
                TargetName = "Deployment",
                Status = DeploymentOperationStatus.Retrieved,
                Payload = new CodeAgentDeployment
                {
                    DeploymentId = request.DeploymentId ?? "dep-9",
                    AgentId = "11111111-1111-1111-1111-111111111111",
                    DeploymentName = "n",
                    DeploymentType = DeploymentType.Dev,
                    Bot = new DeploymentBotResource
                    {
                        ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.BotService/botServices/n",
                        Name = "n",
                        TenantId = "tenant",
                        Sku = "F0",
                        Location = "global",
                        DisplayName = "n",
                        EntraId = "app",
                        EntraIdType = "AppReg",
                        RawArmResponse = arm,
                    },
                },
            },
        };

        var (jsonExit, jsonStdout, jsonStderr) = Run(
            ops,
            color: false,
            "deployment", "show",
            "--deployment-id", "dep-9",
            "--json");
        string[] outputOptions = json ? ["--json"] : [];
        var (debugExit, debugStdout, debugStderr) = Run(
            ops,
            color: false,
            ["deployment", "show", "--deployment-id", "dep-9", "--debug", .. outputOptions]);
        var (humanExit, humanStdout, _) = Run(ops, color: false, "deployment", "show", "--deployment-id", "dep-9");

        Assert.Equal(ExitCodes.Success, jsonExit);
        Assert.Equal(ExitCodes.Success, debugExit);
        Assert.Equal(ExitCodes.Success, humanExit);

        using var doc = JsonDocument.Parse(jsonStdout);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("status", out _));
        Assert.False(root.TryGetProperty("armResponse", out _));
        Assert.False(root.GetProperty("deployment").TryGetProperty("armResponse", out _));
        Assert.DoesNotContain("armResponse", humanStdout);
        Assert.Empty(jsonStderr);
        Assert.Equal(NormalizeLines(json ? jsonStdout : humanStdout), NormalizeLines(debugStdout));
        Assert.StartsWith("ARM response:", debugStderr);
        Assert.Contains("ARM response:", debugStderr);
        Assert.Contains("\"id\": \"/bot\"", debugStderr);
    }

    private static string NormalizeLines(string value)
        => value.Replace("\r\n", "\n").TrimEnd('\n');

    [Fact]
    public void DeploymentDelete_ByDeploymentId_PassesIdThrough()
    {
        var ops = new FakeOperations();

        var (exitCode, _, stderr) = Run(ops, color: false, "deployment", "delete", "--deployment-id", "dep-99");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("dep-99", ops.LastDeleteDeploymentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentDelete_DebugPreservesNormalOutputWithoutArmResponse(bool json)
    {
        var ops = new FakeOperations();
        var args = new List<string>
        {
            "deployment", "delete",
            "--deployment-id", "dep-99",
            "--debug",
        };
        if (json)
        {
            args.Add("--json");
        }

        var (exitCode, stdout, stderr) = Run(ops, color: false, [.. args]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        if (json)
        {
            using var document = JsonDocument.Parse(stdout);
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Deployment deleted.", document.RootElement.GetProperty("message").GetString());
        }
        else
        {
            Assert.Equal("Deployment deleted.", NormalizeLines(stdout));
        }
        Assert.Equal("dep-99", ops.LastDeleteDeploymentId);
    }

    [Fact]
    public void DeploymentUpdate_DisplayNameFlag_IsPlumbedIntoRequest()
    {
        var ops = new FakeOperations();
        var (exitCode, _, _) = Run(
            ops, color: false,
            "deployment", "update", "--deployment-id", "dep-1", "--display-name", "New Name");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("New Name", ops.LastUpdateDeploymentRequest?.DisplayName);
    }

    [Fact]
    public void DeploymentUpdate_DefaultFlag_IsPlumbedIntoRequest()
    {
        var ops = new FakeOperations();
        var (exitCode, _, _) = Run(
            ops, color: false,
            "deployment", "update", "--deployment-id", "dep-1", "--default");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(true, ops.LastUpdateDeploymentRequest?.IsDefault);
    }

    [Fact]
    public void DeploymentUpdate_WithoutDefaultFlag_LeavesIsDefaultNull()
    {
        var ops = new FakeOperations();
        var (exitCode, _, _) = Run(
            ops, color: false,
            "deployment", "update", "--deployment-id", "dep-1", "--endpoint-url", "https://x/api/messages");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(ops.LastUpdateDeploymentRequest?.IsDefault);
    }

    [Fact]
    public void DeploymentCreate_TargetEnvironmentSelectsTheAmsRouteAndBindsTheRecord()
    {
        var ops = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, stderr) = RunAms(ops, handlers, "deployment", "create", "-aid", AmsAgentId, "-tenv", "target-env", "-dn", "dev-proof", "-dt", "dev", "-ent", "88888888-8888-8888-8888-888888888888", "-eit", "app-reg", "-eu", "https://contoso-3978.usw2.devtunnels.ms/api/messages", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = handlers.Single<AgentDeploymentAmsCreateRequest>();
        Assert.Equal((AmsAgentId, (string?)null, (string?)null, "target-env", "dev-proof", "dev", "88888888-8888-8888-8888-888888888888", "app-reg", "https://contoso-3978.usw2.devtunnels.ms/api/messages"), (request.AgentId, request.AgentName, request.EnvironmentId, request.TargetEnvironmentId, request.DeploymentName, request.DeploymentType, request.EntraId, request.EntraIdType, request.EndpointUrl));
        Assert.Null(ops.LastAgentDeploymentCreateRequest);
        Assert.Null(ops.LastDeploymentRequest);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(AgentDeploymentAmsResult.CreatedStatus, document.RootElement.GetProperty("status").GetString());
        Assert.Equal("NotDeployed", document.RootElement.GetProperty("deployment").GetProperty("state").GetString());
    }

    [Fact]
    public void DeploymentCreate_AmsRoute_TakesTheAgentNameAndHomeEnvironmentAndShowsOnlyTheRecord()
    {
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, _) = RunAms(new FakeOperations(), handlers, "deployment", "create", "-an", "Contoso Agent", "-eid", "home-env", "-tenv", "target-env", "-dn", "dev-proof", "-dt", "dev", "-ent", "88888888-8888-8888-8888-888888888888", "-eit", "app-reg");

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = handlers.Single<AgentDeploymentAmsCreateRequest>();
        Assert.Equal(("Contoso Agent", "home-env"), (request.AgentName, request.EnvironmentId));
        Assert.Contains("created. It is a record only: nothing runs until it is deployed.", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Next:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("msagent agent deploy", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-dt", "--deployment-type")]
    [InlineData("-ent", "--entra-id")]
    [InlineData("-eit", "--entra-id-type")]
    [InlineData("-dn", "--deployment-name")]
    [InlineData("-aid", "--agent-id or --agent-name is required for the AMS route of `msagent deployment create`.")]
    public void DeploymentCreate_AmsRouteWithoutARequiredOption_DispatchesNothing(string missing, string message)
    {
        var handlers = new RecordingAmsHandlers();
        var args = new List<string> { "deployment", "create", "-tenv", "target-env" };
        foreach (var (option, value) in new[] { ("-aid", AmsAgentId), ("-dn", "dev"), ("-dt", "dev"), ("-ent", "88888888-8888-8888-8888-888888888888"), ("-eit", "app-reg") })
        {
            if (option != missing)
            {
                args.AddRange([option, value]);
            }
        }

        var (exitCode, _, stderr) = RunAms(new FakeOperations(), handlers, [.. args]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
    }

    [Theory]
    [InlineData("--sku", "S1")]
    [InlineData("--resource-group", "rg")]
    [InlineData("--project", ".")]
    [InlineData("--default", null)]
    public void DeploymentCreate_AmsRoute_RefusesOptionsOnlyTheLocalRoutesTake(string option, string? value)
    {
        var handlers = new RecordingAmsHandlers();
        string[] args = ["deployment", "create", "-aid", AmsAgentId, "-tenv", "target-env", "-dn", "dev", "-dt", "dev", "-ent", "88888888-8888-8888-8888-888888888888", "-eit", "app-reg", option];

        var (exitCode, _, stderr) = RunAms(new FakeOperations(), handlers, value is null ? args : [.. args, value]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains($"{option} cannot be combined with the AMS route of `msagent deployment create`.", stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
    }

    [Fact]
    public void DeploymentCreate_WithAmsFeaturesOff_RefusesTheTargetEnvironment()
    {
        var ops = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, stderr) = AmsCommandHost.Run(ops, handlers, new TestFeatureFlags(Microsoft.Agents.Hosting.Cli.Commands.Features.FeatureName.AzureBotServiceFeatures), "deployment", "create", "-aid", AmsAgentId, "--target-environment-id", "target-env", "-dn", "dev", "-dt", "dev", "-ent", "88888888-8888-8888-8888-888888888888", "-eit", "app-reg");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--target-environment-id", stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
        Assert.Null(ops.LastDeploymentRequest);
    }

    [Fact]
    public void DeploymentList_OfAnAgentWithoutALocalRecord_ListsItsAmsDeploymentsWithTheKeptBots()
    {
        var ops = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, _) = RunAms(ops, handlers, "deployment", "list", "-aid", AmsAgentId, "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(AmsAgentId, handlers.Single<AgentDeploymentAmsListRequest>().AgentId);
        Assert.Null(ops.LastAgentDeploymentListRequest);
        using var document = JsonDocument.Parse(stdout);
        var deployment = Assert.Single(document.RootElement.GetProperty("deployments").EnumerateArray());
        Assert.Equal(("Deployed", "package"), (deployment.GetProperty("state").GetString(), deployment.GetProperty("kind").GetString()));
        Assert.Equal("ams-old", Assert.Single(document.RootElement.GetProperty("keptBots").EnumerateArray()).GetProperty("botName").GetString());
    }

    [Fact]
    public void DeploymentList_Text_ShowsTheTableAndTheKeptBots()
    {
        var (_, stdout, _) = RunAms(new FakeOperations(), new RecordingAmsHandlers(), "deployment", "list", "-an", "Contoso Agent");

        Assert.Contains($"Deployments of agent {AmsAgentId}", stdout, StringComparison.Ordinal);
        Assert.Contains("PENDING ENDPOINT", stdout, StringComparison.Ordinal);
        Assert.Contains("Bots kept from deleted deployment records", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("msagent", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeploymentList_WithoutAnAgentOrWithALocalRecord_ListsTheLocalProject(bool localRecord)
    {
        var ops = localRecord ? LocalRecord(DeploymentAgentTypeSource.AgentRecord) : new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, _) = RunAms(ops, handlers, localRecord ? ["deployment", "list", "-an", "Support"] : ["deployment", "list"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(ops.LastAgentDeploymentListRequest);
        Assert.Empty(handlers.Requests);
    }

    [Fact]
    public void DeploymentShow_WithEnvironmentId_ReadsOnlyTheAmsDeployment()
    {
        var ops = LocalRecord(DeploymentAgentTypeSource.DeploymentRecord);
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, stderr) = RunAms(ops, handlers, "deployment", "show", "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = handlers.Single<AgentDeploymentAmsShowRequest>();
        Assert.Equal((AmsAgentId, AmsDeploymentId, "env-1"), (request.AgentId, request.DeploymentId, request.EnvironmentId));
        Assert.Null(ops.LastGetDeploymentRequest);
        Assert.Null(ops.LastAgentDeploymentShowRequest);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(AgentDeploymentAmsResult.ReadStatus, root.GetProperty("status").GetString());
        Assert.Equal(AmsDeploymentId, root.GetProperty("deployment").GetProperty("deploymentId").GetString());
    }

    [Fact]
    public void DeploymentShow_ByNamesWithoutALocalRecord_TakesTheAmsRouteWithTheActiveEnvironment()
    {
        var handlers = new RecordingAmsHandlers { Deployment = FakeOperations.CreateStubDeploymentRecord() with { Kind = AgentDeploymentAmsRecord.DevEndpointKind, PackageRevisionId = null, Endpoint = "https://old.devtunnels.ms/api/messages", RequestedEndpoint = "https://new.devtunnels.ms/api/messages", EndpointChangePending = true } };

        var (exitCode, stdout, _) = RunAms(new FakeOperations(), handlers, "deployment", "show", "-an", "Contoso Agent", "-dn", "dev-proof");

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = handlers.Single<AgentDeploymentAmsShowRequest>();
        Assert.Equal(("Contoso Agent", "dev-proof", (string?)null), (request.AgentName, request.DeploymentName, request.EnvironmentId));
        var lines = NormalizeLines(stdout).Split('\n');
        Assert.Equal($"Deployment dev-proof ({AmsDeploymentId})", lines[0]);
        Assert.Contains("  endpoint: https://old.devtunnels.ms/api/messages", lines);
        Assert.Contains("  requested endpoint: https://new.devtunnels.ms/api/messages, not deployed yet; the next `msagent agent deploy` without --commit-id puts it live.", lines);
    }

    [Fact]
    public void DeploymentShow_ALocalRecordOfTheDeploymentNamed_KeepsTheLocalRoute()
    {
        var ops = LocalRecord(DeploymentAgentTypeSource.DeploymentRecord);
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, _) = RunAms(ops, handlers, "deployment", "show", "-an", "Support", "-dn", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(ops.LastAgentDeploymentShowRequest);
        Assert.Empty(handlers.Requests);
    }

    [Fact]
    public void DeploymentDelete_AmsRoute_BindsTheConsentAndSaysWhatKeepsRunning()
    {
        var ops = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, stderr) = RunAms(ops, handlers, "deployment", "delete", "-aid", AmsAgentId, "-dn", "dev-proof", "--non-interactive");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = handlers.Single<AgentDeploymentAmsDeleteRequest>();
        Assert.Equal((AmsAgentId, "dev-proof", true), (request.AgentId, request.DeploymentName, request.NonInteractive));
        Assert.Null(ops.LastDeleteDeploymentRequest);
        Assert.Null(ops.LastAgentDeploymentDeleteRequest);
        var lines = NormalizeLines(stdout).Split('\n');
        Assert.Equal($"Deployment record dev-proof ({AmsDeploymentId}) deleted.", lines[0]);
        Assert.StartsWith("Still running: the deployment keeps answering in environment 33333333-3333-3333-3333-333333333333", lines[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentAmsConfirmation.CancelledStatus, "Delete cancelled; nothing was deleted.")]
    [InlineData(AgentAmsConfirmation.NameMismatchStatus, "The name typed did not match 'dev-proof'; nothing was deleted.")]
    public void DeploymentDelete_AmsRouteNotConfirmed_ExitsSuccessfullyAndSaysNothingWasDeleted(string status, string text)
    {
        var (exitCode, stdout, _) = RunAms(new FakeOperations(), new RecordingAmsHandlers { DeleteStatus = status }, "deployment", "delete", "-aid", AmsAgentId, "-did", AmsDeploymentId);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(text, NormalizeLines(stdout).Trim());
    }

    [Fact]
    public void DeploymentDelete_AmsFailureIsReportedWithoutFallingBackToAnotherRoute()
    {
        var ops = new FakeOperations();
        var handlers = new RecordingAmsHandlers { Failure = AgentDeployException.ServiceRejected("AMS does not delete code-agent deployment records in this environment yet.", ExitCodes.Failure, "deployment-delete-unavailable", "Ask the AMS team.") };

        var (exitCode, stdout, _) = RunAms(ops, handlers, "deployment", "delete", "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1", "--json");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Single(handlers.Requests);
        Assert.Null(ops.LastDeleteDeploymentRequest);
        Assert.Null(ops.LastAgentDeploymentDeleteRequest);
        using var document = JsonDocument.Parse(stdout);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("deployment-delete-unavailable", document.RootElement.GetProperty("errorKind").GetString());
    }

    [Theory]
    [InlineData("show", "--agent-id", "--agent-id or --agent-name is required for the AMS route of `msagent deployment show`.")]
    [InlineData("show", "--deployment-id", "--deployment-id or --deployment-name is required for the AMS route of `msagent deployment show`.")]
    [InlineData("delete", "--agent-id", "--agent-id or --agent-name is required for the AMS route of `msagent deployment delete`.")]
    [InlineData("delete", "--deployment-id", "--deployment-id or --deployment-name is required for the AMS route of `msagent deployment delete`.")]
    public void AmsRoute_MissingAddressOptionDispatchesNothing(string verb, string missing, string message)
    {
        var ops = new FakeOperations();
        var handlers = new RecordingAmsHandlers();
        var args = new List<string> { "deployment", verb, "--environment-id", "env-1" };
        foreach (var (option, value) in new[] { ("--agent-id", AmsAgentId), ("--deployment-id", AmsDeploymentId) })
        {
            if (!string.Equals(option, missing, StringComparison.Ordinal))
            {
                args.AddRange([option, value]);
            }
        }

        var (exitCode, _, stderr) = RunAms(ops, handlers, [.. args]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        AssertNoDeploymentRouteDispatched(ops, handlers);
    }

    [Theory]
    [InlineData("show", "--project", ".")]
    [InlineData("show", "--resource-group", "rg")]
    [InlineData("show", "--subscription", "sub")]
    [InlineData("show", "--tenant", "tenant")]
    [InlineData("delete", "--project", ".")]
    [InlineData("delete", "--resource-group", "rg")]
    [InlineData("delete", "--subscription", "sub")]
    [InlineData("delete", "--tenant", "tenant")]
    [InlineData("delete", "--delete-deployed-mcs-agent", null)]
    public void AmsRoute_RejectsOtherRouteOptions(string verb, string option, string? value)
    {
        var ops = new FakeOperations();
        var handlers = new RecordingAmsHandlers();
        string[] extra = value is null ? [option] : [option, value];

        var (exitCode, _, stderr) = RunAms(ops, handlers, ["deployment", verb, "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1", .. extra]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains($"{option} cannot be combined with the AMS route of `msagent deployment {verb}`.", stderr, StringComparison.Ordinal);
        AssertNoDeploymentRouteDispatched(ops, handlers);
    }

    private static FakeOperations LocalRecord(DeploymentAgentTypeSource source) => new() { OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = source } };

    private static (int ExitCode, string Stdout, string Stderr) RunAms(FakeOperations operations, RecordingAmsHandlers handlers, params string[] args) => AmsCommandHost.Run(operations, handlers, TestFeatureFlags.AllProductSurfaces, args);

    private static void AssertNoDeploymentRouteDispatched(FakeOperations ops, RecordingAmsHandlers handlers)
    {
        Assert.Empty(handlers.Requests);
        Assert.Null(ops.LastGetDeploymentRequest);
        Assert.Null(ops.LastDeleteDeploymentRequest);
        Assert.Null(ops.LastAgentDeploymentShowRequest);
        Assert.Null(ops.LastAgentDeploymentDeleteRequest);
    }
}
