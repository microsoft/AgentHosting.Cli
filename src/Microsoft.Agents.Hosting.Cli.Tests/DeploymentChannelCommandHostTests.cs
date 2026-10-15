using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentChannelCommandHostTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(
        FakeOperations operations,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new Microsoft.Agents.Hosting.Cli.Hosting.AgentHostCli(
            operations,
            featureFlags: new TestFeatureFlags(FeatureName.AzureBotServiceFeatures));
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Create_BindsShortAddressingChannelAndParameters()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            "deployment", "channel", "create",
            "--deployment-id", "dep-1",
            "--channel", "slack",
            "--parameters", "clientId=client-1", "clientSecret=secret-1", "verificationToken=token-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<DeploymentChannelCreateRequest>(operations.LastCreateChannelRequest);
        Assert.Equal("dep-1", request.DeploymentId);
        Assert.Equal("slack", request.ChannelName);
        Assert.Equal(
            ["clientId=client-1", "clientSecret=secret-1", "verificationToken=token-1"],
            request.Parameters);
    }

    [Fact]
    public void Show_BindsAgentAndNameAddressing()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            "deployment", "channel", "show",
            "--agent-id", "agent-1",
            "--deployment-name", "dev",
            "--channel", "msteams");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<DeploymentChannelShowRequest>(operations.LastGetChannelRequest);
        Assert.Null(request.DeploymentId);
        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal("dev", request.DeploymentName);
    }

    [Fact]
    public void Show_RejectsMcsDeployment()
    {
        var operations = new FakeOperations
        {
            OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult
            {
                AgentType = AgentTypes.McsAgent,
                Source = DeploymentAgentTypeSource.DeploymentRecord,
            },
        };

        var (exitCode, _, stderr) = Run(
            operations,
            "deployment", "channel", "show",
            "--deployment-id", "dep-1",
            "--channel", "msteams");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(
            "`msagent deployment channel` applies to a CodeAgent deployment",
            stderr);
        Assert.Null(operations.LastGetChannelRequest);
        Assert.Equal("dep-1", operations.LastDeploymentAgentTypeQuery!.DeploymentId);
    }

    [Fact]
    public void Show_DoesNotAcceptParameters()
    {
        var (exitCode, _, stderr) = Run(
            new FakeOperations(),
            "deployment", "channel", "show",
            "--deployment-id", "dep-1",
            "--channel", "msteams",
            "--parameters", "enableCalling=true");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--parameters", stderr);
    }

    [Fact]
    public void CreateHelp_IncludesM365Extensions()
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            "deployment", "channel", "create", "--channel", "m365extensions", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("m365extensions", stdout);
        Assert.DoesNotContain("Parameters:", stdout);
        Assert.DoesNotContain("None.", stdout);
    }

    [Fact]
    public void CreateHelp_ShowsGenericParametersAndNoTypedChannelOptions()
    {
        var (exitCode, stdout, _) = Run(
            new FakeOperations(),
            "deployment", "channel", "create", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("--parameters", stdout);
        Assert.Contains("--deployment-id", stdout);
        Assert.Contains("--agent-id", stdout);
        Assert.Contains("--deployment-name", stdout);
        Assert.DoesNotContain("--client-secret", stdout);
        Assert.DoesNotContain("--enable-calling", stdout);
        Assert.Contains("Specify --channel <name>", stdout);
    }

    [Theory]
    [InlineData("msteams", "enableCalling", "<Boolean>", "Whether calling is enabled.")]
    [InlineData("msteams", "deploymentEnvironment", "<String>", "Default: CommercialDeployment.")]
    [InlineData("slack", "clientSecret", "(REQUIRED)", "The Slack client secret.")]
    [InlineData("directline", "trustedOrigins", "<String>", "Comma-separated trusted origins.")]
    [InlineData("directline", "isV1Enabled", "(READ-ONLY)", "Whether Direct Line v1 is enabled.")]
    [InlineData("directline", "siteName", "(REQUIRED)", "The channel site name.")]
    [InlineData("webchat", "siteName", "(REQUIRED)", "The channel site name.")]
    public void MutationHelp_AppendsCatalogParameters(
        string channel,
        string parameter,
        string firstMarker,
        string secondMarker)
    {
        foreach (var verb in new[] { "create", "update" })
        {
            var (exitCode, stdout, stderr) = Run(
                new FakeOperations(),
                "deployment", "channel", verb,
                "--channel", channel,
                "--help");

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            Assert.Contains("Parameters:", stdout);
            Assert.Contains(parameter, stdout);
            Assert.Contains(firstMarker, stdout);
            Assert.Contains(secondMarker, stdout);
            Assert.DoesNotContain("Clearable on update.", stdout);
            Assert.DoesNotContain("Secret.", stdout);
        }
    }

    [Fact]
    public void List_DoesNotAcceptChannelOrParametersOptions()
    {
        var (exitCode, _, stderr) = Run(
            new FakeOperations(),
            "deployment", "channel", "list",
            "--deployment-id", "dep-1",
            "--channel", "slack");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--channel", stderr);
    }

    [Fact]
    public void List_Debug_WritesOnlyArmResponseArrayToStderr()
    {
        using var teamsDocument = System.Text.Json.JsonDocument.Parse(
            """{"id":"/channels/MsTeamsChannel","properties":{"channelName":"MsTeamsChannel"}}""");
        using var slackDocument = System.Text.Json.JsonDocument.Parse(
            """{"id":"/channels/SlackChannel","properties":{"channelName":"SlackChannel","properties":{"clientSecret":"***"}}}""");
        var operations = new FakeOperations
        {
            OnListChannels = _ => new DeploymentResult<DeploymentChannelRecord[]>
            {
                Status = DeploymentOperationStatus.Listed,
                TargetName = "Channels",
                Payload =
                [
                    new()
                    {
                        ResourceId = "/channels/MsTeamsChannel",
                        ChannelName = "msteams",
                        RawArmResponse = teamsDocument.RootElement.Clone(),
                    },
                    new()
                    {
                        ResourceId = "/channels/SlackChannel",
                        ChannelName = "slack",
                        RawArmResponse = slackDocument.RootElement.Clone(),
                    },
                ],
            },
        };

        var (exitCode, stdout, stderr) = Run(
            operations,
            "deployment", "channel", "list",
            "--deployment-id", "dep-1",
            "--debug");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stdout);
        Assert.StartsWith("ARM response:", stderr);
        Assert.Contains("ARM response:", stderr);
        Assert.Contains("/channels/MsTeamsChannel", stderr);
        Assert.Contains("/channels/SlackChannel", stderr);

        var response = stderr[stderr.IndexOf('[')..];
        using var document = System.Text.Json.JsonDocument.Parse(response);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(2, document.RootElement.GetArrayLength());
    }
}
