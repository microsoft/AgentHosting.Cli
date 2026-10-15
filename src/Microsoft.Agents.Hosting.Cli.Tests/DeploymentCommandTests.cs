using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentCommandTests
{
    private const string AgentId = "22222222-2222-2222-2222-222222222222";
    private const string AppId = "55555555-5555-5555-5555-555555555555";

    private static (int ExitCode, string Stdout, string Stderr) Run(string projectDirectory, params string[] args)
    {
        Directory.CreateDirectory(projectDirectory);
        var locator = new AgentConfigLocator();
        var store = new ProjectDeploymentStore(new JsonAgentConfigStore(locator, new CommandTestClock()), locator);
        var service = DeploymentServiceTestFactory.Build(store, new CapturingBotProvisioningPort());
        var operations = new FakeOperations
        {
            OnCreateDeployment = request => service.CreateAsync(request with { Project = projectDirectory }, CancellationToken.None).GetAwaiter().GetResult(),
        };

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(
                operations,
                featureFlags: TestFeatureFlags.AllProductSurfaces)
            .InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed class CommandTestClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "ah-deployment-cmd-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Create_Json_EmitsDeploymentRecordEnvelope()
    {
        var dir = TempDir();

        var (exitCode, stdout, stderr) = Run(
            dir,
            "deployment", "create",
            "--agent-id", AgentId,
            "--deployment-name", "dev1",
            "--deployment-type", "Dev",
            "--resource-group", "rg-cmd-tests",
            "--entra-id", AppId,
            "--entra-id-type", "AppReg",
            "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("Deployment 'dev1' created.", root.GetProperty("message").GetString());
        Assert.False(root.TryGetProperty("deploymentId", out _));

        var record = root.GetProperty("deployment");
        Assert.Equal(DeterministicDeploymentId.Compute(AgentId, "dev1").ToString(), record.GetProperty("deploymentId").GetString());
        Assert.Equal("dev1", record.GetProperty("deploymentName").GetString());
        Assert.Equal((int)DeploymentType.Dev, record.GetProperty("deploymentType").GetInt32());
    }

    [Fact]
    public void Create_StoresRecordInTheProjectConfig()
    {
        var dir = TempDir();

        var (exitCode, _, stderr) = Run(
            dir,
            "deployment", "create",
            "--agent-id", AgentId,
            "--deployment-name", "dev1",
            "--deployment-type", "Dev",
            "--resource-group", "rg-cmd-tests",
            "--entra-id", AppId,
            "--entra-id-type", "AppReg");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var path = Path.Combine(dir, AgentConfigLocator.ConfigDirectoryName, AgentConfigLocator.ConfigFileName);
        Assert.True(File.Exists(path));

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var record = Assert.Single(document.RootElement.GetProperty("agentDeployments").EnumerateArray().ToList());
        Assert.Equal(AgentId, record.GetProperty("agentId").GetString());
        Assert.Equal("dev1", record.GetProperty("deploymentName").GetString());
        Assert.Equal("dev", record.GetProperty("deploymentType").GetString());
        Assert.Equal("CodeAgent", record.GetProperty("agentType").GetString());
        Assert.Equal(AppId, record.GetProperty("entraAppId").GetString());
    }

    [Fact]
    public void Create_WithoutAnAgentSelector_ExitsUsage()
    {
        var (exitCode, _, stderr) = Run(TempDir(), "deployment", "create", "--deployment-name", "dev1", "--deployment-type", "Dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--agent-id or --agent-name", stderr);
        Assert.DoesNotContain("AzureBotServiceFeatures", stderr);
    }

    [Fact]
    public void Create_MissingRequiredOption_ExitsUsage()
    {
        var (exitCode, _, stderr) = Run(TempDir(), "deployment", "create", "--agent-id", AgentId, "--deployment-name", "dev1", "--deployment-type", "Dev");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--entra-id", stderr);
    }

    [Fact]
    public void Create_InvalidType_ExitsUsage()
    {
        var (exitCode, _, stderr) = Run(
            TempDir(),
            "deployment", "create",
            "--agent-id", AgentId,
            "--deployment-name", "dev1",
            "--deployment-type", "Staging",
            "--resource-group", "rg-cmd-tests",
            "--entra-id", AppId,
            "--entra-id-type", "AppReg");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--deployment-type", stderr);
    }

    [Fact]
    public void DeploymentGroup_NoSubcommand_ExitsUsage()
    {
        var (exitCode, _, stderr) = Run(TempDir(), "deployment");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("No deployment command specified", stderr);
    }
}
