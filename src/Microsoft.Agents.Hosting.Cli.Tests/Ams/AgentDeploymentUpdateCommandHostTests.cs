using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentDeploymentUpdateCommandHostTests
{
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Revision = "66666666-6666-6666-6666-666666666666";

    [Fact]
    public void Update_BindsOnlyExistingDeploymentPackageInputs()
    {
        var operations = new FakeOperations();

        var result = Run(
            operations,
            "deployment", "update",
            "--agent-id", Agent,
            "--deployment-id", Deployment,
            "--package-revision", Revision,
            "--environment-id", "env-1",
            "--json",
            "--non-interactive");

        Assert.Equal(ExitCodes.Success, result.ExitCode);
        var request = Assert.IsType<AgentDeploymentPackageUpdateRequest>(
            operations.LastAgentDeploymentPackageUpdateRequest);
        Assert.Equal(Agent, request.AgentId);
        Assert.Equal(Deployment, request.DeploymentId);
        Assert.Equal(Revision, request.PackageRevisionId);
        Assert.Equal("env-1", request.EnvironmentId);
        Assert.Null(operations.LastAgentDeploymentUpdateRequest);
        Assert.Null(operations.LastUpdateDeploymentRequest);

        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        Assert.Equal("agent-deployment-updated", root.GetProperty("status").GetString());
        Assert.Equal(Deployment, root.GetProperty("deploymentId").GetString());
        Assert.Equal(Revision, root.GetProperty("packageRevisionId").GetString());
        Assert.DoesNotContain(root.EnumerateObject(), property =>
            property.Name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("repository", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("commit", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("--agent-id")]
    [InlineData("--deployment-id")]
    [InlineData("--package-revision")]
    public void Update_MissingRequiredOptionDispatchesNothing(string missing)
    {
        var arguments = new List<string> { "deployment", "update" };
        foreach (var option in new[] { "--agent-id", "--deployment-id", "--package-revision" })
        {
            if (!string.Equals(option, missing, StringComparison.Ordinal))
            {
                arguments.AddRange([option, Agent]);
            }
        }
        if (string.Equals(missing, "--package-revision", StringComparison.Ordinal))
        {
            arguments.AddRange(["--environment-id", "env-1"]);
        }
        var operations = new FakeOperations();

        var result = Run(operations, [.. arguments]);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Null(operations.LastAgentDeploymentPackageUpdateRequest);
        Assert.Contains(missing, result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--new-name", "renamed")]
    [InlineData("--sku", "S1")]
    [InlineData("--project", ".")]
    [InlineData("--deployment-name", "dev")]
    public void Update_RejectsOtherRouteInputs(string option, string value)
    {
        var operations = new FakeOperations();

        var result = Run(
            operations,
            "deployment", "update",
            "--agent-id", Agent,
            "--deployment-id", Deployment,
            "--package-revision", Revision,
            option, value);

        Assert.Equal(ExitCodes.Usage, result.ExitCode);
        Assert.Null(operations.LastAgentDeploymentPackageUpdateRequest);
        Assert.Contains(option, result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_ReportsFailureWithoutFallingBackToAnotherRoute()
    {
        var operations = new FakeOperations
        {
            OnAgentDeploymentPackageUpdate = _ => throw AgentDeployException.ServiceRejected(
                "The deployment changed.",
                ExitCodes.Failure,
                "agent-deployment-concurrency-conflict",
                "Run the update again."),
        };

        var result = Run(
            operations,
            "deployment", "update",
            "--agent-id", Agent,
            "--deployment-id", Deployment,
            "--package-revision", Revision,
            "--json");

        Assert.Equal(ExitCodes.Failure, result.ExitCode);
        Assert.NotNull(operations.LastAgentDeploymentPackageUpdateRequest);
        Assert.Null(operations.LastAgentDeploymentUpdateRequest);
        Assert.Null(operations.LastUpdateDeploymentRequest);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Update_AmsOptionsAreUnavailableWhenFeatureIsDisabled()
    {
        var operations = new FakeOperations();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = await new AgentHostCli(operations, featureFlags: new TestFeatureFlags())
            .InvokeAsync(
                ["deployment", "update", "--agent-id", Agent, "--deployment-id", Deployment, "--package-revision", Revision],
                stdout,
                stderr,
                color: false);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastAgentDeploymentPackageUpdateRequest);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(
        IAgentHostingOperations operations,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(
            operations,
            featureFlags: new TestFeatureFlags(FeatureName.AmsFeatures))
            .InvokeAsync(args, stdout, stderr, color: false)
            .GetAwaiter()
            .GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}
