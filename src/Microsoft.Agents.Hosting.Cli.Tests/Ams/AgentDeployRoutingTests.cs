using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentDeployRoutingTests
{
    [Fact]
    public void Deploy_WithoutInputs_RequiresAnExplicitDeployment()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "deploy");

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Contains("--deployment-id or --deployment-name", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_ProjectOnly_DirectsCodeAgentsToPackageUpload()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "deploy", "--project", "./my-agent");

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Contains("msagent agent package upload", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deploy_McsPath_PreservesSelectorsAndFlags(bool withProject)
    {
        var operations = new FakeOperations();
        string[] args =
        [
            "agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "prod-west",
            "--schema-name", "cr_other", "--publish", "--overwrite",
        ];
        if (withProject)
        {
            args = [.. args, "--project", "./mcs-project"];
        }

        var (exitCode, _, _) = Run(operations, args);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(operations.LastDeployRequest);
        Assert.Null(operations.LastPackageUploadRequest);
        var request = Assert.IsType<ManagedAgentDeployRequest>(operations.LastManagedAgentDeployRequest);
        Assert.Equal(withProject ? "./mcs-project" : null, request.Project);
        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal("prod-west", request.DeploymentName);
        Assert.Equal("cr_other", request.SchemaName);
        Assert.True(request.Publish);
        Assert.True(request.Overwrite);
    }

    [Fact]
    public void Deploy_McsPath_PreservesIdSelectorsAndDefaultFlags()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(
            operations, "agent", "deploy", "--agent-name", "support", "--deployment-id", "deployment-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(operations.LastDeployRequest);
        Assert.Null(operations.LastPackageUploadRequest);
        var request = Assert.IsType<ManagedAgentDeployRequest>(operations.LastManagedAgentDeployRequest);
        Assert.Equal("support", request.AgentName);
        Assert.Equal("deployment-1", request.DeploymentId);
        Assert.False(request.Publish);
        Assert.False(request.Overwrite);
    }

    [Theory]
    [InlineData("--commit-id", "abc123")]
    [InlineData("--async", "true")]
    public void Deploy_McsPath_StillRejectsUnsupportedOperations(string option, string value)
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(
            operations, "agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "dev", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Contains(option, stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--project", "./mcs-project")]
    [InlineData("--agent-id", "agent-1")]
    [InlineData("--agent-name", "support")]
    [InlineData("--deployment-id", "deployment-1")]
    [InlineData("--schema-name", "cr_other")]
    [InlineData("--publish", "true")]
    [InlineData("--publish", "false")]
    [InlineData("--overwrite", "true")]
    [InlineData("--overwrite", "false")]
    [InlineData("--commit-id", "abc123")]
    [InlineData("--async", "true")]
    public void Deploy_AmsPath_RejectsMcsInputsBeforeDispatch(string option, string value)
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, stderr) = Run(operations, [.. AmsArgs(), option, value, "--json"]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Contains(option, document.RootElement.GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--agent-id", "44444444-4444-4444-4444-444444444444")]
    [InlineData("--package-revision", "66666666-6666-6666-6666-666666666666")]
    [InlineData("--environment-id", "home")]
    [InlineData("--target-environment-id", "target")]
    [InlineData("--deployment-type", "dev")]
    [InlineData("--entra-id", "88888888-8888-8888-8888-888888888888")]
    [InlineData("--entra-id-type", "app-reg")]
    public void Deploy_McsPath_RejectsEveryAmsSelectorRatherThanIgnoringIt(string option, string value)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(
            operations, "agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "dev", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
    }

    [Fact]
    public void Deploy_EmptyAmsSelector_DoesNotFallThroughToMcs()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(
            operations, "agent", "deploy", "--deployment-name", "dev", "--package-revision", "");

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
    }

    private static void AssertNothingDispatched(FakeOperations operations)
    {
        Assert.Null(operations.LastDeployRequest);
        Assert.Null(operations.LastPackageUploadRequest);
        Assert.Null(operations.LastManagedAgentDeployRequest);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(
            operations,
            featureFlags: new TestFeatureFlags(FeatureName.AmsFeatures))
            .InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string[] AmsArgs() =>
    [
        "agent", "deploy",
        "--agent-id", "44444444-4444-4444-4444-444444444444",
        "--package-revision", "66666666-6666-6666-6666-666666666666",
        "--target-environment-id", "33333333-3333-3333-3333-333333333333",
        "--deployment-name", "dev-proof",
        "--deployment-type", "dev",
        "--entra-id", "88888888-8888-8888-8888-888888888888",
        "--entra-id-type", "app-reg",
    ];
}
