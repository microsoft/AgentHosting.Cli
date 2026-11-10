using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
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
}
