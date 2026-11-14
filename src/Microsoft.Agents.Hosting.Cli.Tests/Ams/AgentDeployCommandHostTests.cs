using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

/// <summary>Command binding and truthful evidence boundaries for package upload and deploy.</summary>
public sealed class AgentDeployCommandHostTests
{
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Home = "22222222-2222-2222-2222-222222222222";
    private const string Target = "33333333-3333-3333-3333-333333333333";
    private const string Operation = "55555555-5555-5555-5555-555555555555";
    private const string Revision = "66666666-6666-6666-6666-666666666666";
    private const string Entra = "88888888-8888-8888-8888-888888888888";

    [Fact]
    public void PackageUpload_BindsOnlyPackagePublicationInputs()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, UploadArgs());

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = Assert.IsType<AgentPackageUploadRequest>(operations.LastPackageUploadRequest);
        Assert.Equal(Agent, request.AgentId);
        Assert.Equal(".\\agent.zip", request.PackagePath);
        Assert.Equal(Home, request.EnvironmentId);
        Assert.Null(operations.LastDeployRequest);
    }

    [Fact]
    public void Deploy_BindsExistingRevisionAndEveryProjectionInput()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, DeployArgs());

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = Assert.IsType<AgentDeployRequest>(operations.LastDeployRequest);
        Assert.Equal(Agent, request.AgentId);
        Assert.Equal(Revision, request.PackageRevisionId);
        Assert.Equal(Home, request.EnvironmentId);
        Assert.Equal(Target, request.TargetEnvironmentId);
        Assert.Equal("dev-proof", request.DeploymentName);
        Assert.Equal("dev", request.DeploymentType);
        Assert.Equal(Entra, request.EntraId);
        Assert.Equal("app-reg", request.EntraIdType);
        Assert.Null(operations.LastPackageUploadRequest);
    }

    [Theory]
    [InlineData("--agent-id")]
    [InlineData("--package-revision")]
    [InlineData("--target-environment-id")]
    [InlineData("--deployment-name")]
    [InlineData("--deployment-type")]
    [InlineData("--entra-id")]
    [InlineData("--entra-id-type")]
    public void Deploy_WithoutARequiredOption_IsAUsageErrorAndDispatchesNothing(string option)
    {
        var operations = new FakeOperations();
        var args = DeployArgs().ToList();
        var index = args.IndexOf(option);
        args.RemoveRange(index, 2);

        var (exitCode, _, stderr) = Run(operations, [.. args]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastDeployRequest);
        Assert.Contains(option, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_WithoutTarget_JsonReportsUsageAndDispatchesNothing()
    {
        var operations = new FakeOperations();
        var args = DeployArgs().ToList();
        args.RemoveRange(args.IndexOf("--target-environment-id"), 2);
        args.Add("--json");

        var (exitCode, stdout, stderr) = Run(operations, [.. args]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastDeployRequest);
        Assert.Null(operations.LastPackageUploadRequest);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(
            "--target-environment-id",
            document.RootElement.GetProperty("errorMessage").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_ExplicitSameHomeTarget_IsBoundAndRendered()
    {
        var operations = new FakeOperations();
        var args = DeployArgs();
        args[Array.IndexOf(args, "--target-environment-id") + 1] = Home;

        var (exitCode, stdout, _) = Run(operations, [.. args, "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(Home, operations.LastDeployRequest!.TargetEnvironmentId);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(Home, document.RootElement.GetProperty("targetEnvironmentId").GetString());
    }

    [Fact]
    public void PackageUpload_JsonReportsTheDurableReceiptAndNoDeployment()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), [.. UploadArgs(), "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        Assert.Equal("package-uploaded", root.GetProperty("status").GetString());
        Assert.Equal(Revision, root.GetProperty("packageRevisionId").GetString());
        Assert.Equal(
            "55555555-5555-5555-5555-555555555555",
            root.GetProperty("packageOperationId").GetString());
        Assert.False(root.TryGetProperty("deploymentId", out _));
        Assert.False(root.TryGetProperty("targetEnvironmentId", out _));
    }

    [Theory]
    [InlineData("Accepted", true)]
    [InlineData("Failed", true)]
    [InlineData("Accepted", false)]
    [InlineData("Failed", false)]
    public void PackageUpload_DurableFailure_RendersTypedOperationIdentity(string status, bool json)
    {
        var operations = new FakeOperations
        {
            OnAgentPackageUpload = _ => throw PackagePublicationException.DurableOperationDidNotSucceed(
                Operation,
                status,
                "client-request-id",
                "service-request-id"),
        };
        string[] args = json ? [.. UploadArgs(), "--json"] : UploadArgs();

        var (exitCode, stdout, stderr) = Run(operations, args);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.NotNull(operations.LastPackageUploadRequest);
        Assert.Null(operations.LastDeployRequest);
        if (json)
        {
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal(ExitCodes.Failure, root.GetProperty("exitCode").GetInt32());
            Assert.Equal("publication-failed", root.GetProperty("errorKind").GetString());
            var partial = root.GetProperty("partialResult");
            Assert.Equal(1, partial.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(Operation, partial.GetProperty("packageOperationId").GetString());
            Assert.Equal(status, partial.GetProperty("operationStatus").GetString());
            Assert.False(partial.TryGetProperty("packageRevisionId", out _));
            Assert.False(root.TryGetProperty("packageRevisionId", out _));
            Assert.False(root.TryGetProperty("deploymentId", out _));
        }
        else
        {
            Assert.Empty(stdout);
            Assert.Contains(Operation, stderr, StringComparison.Ordinal);
            Assert.Contains($"confirmed status: {status}", stderr, StringComparison.Ordinal);
            Assert.Contains("no Package Revision was confirmed", stderr, StringComparison.Ordinal);
            Assert.Contains("Do not retry blindly", stderr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PackageUpload_IndeterminateFailure_DoesNotInventDurableOperationIdentity()
    {
        var operations = new FakeOperations
        {
            OnAgentPackageUpload = _ => throw PackagePublicationException.RequestFailed(
                "client-request-id",
                new HttpRequestException("private-response-detail")),
        };

        var (exitCode, stdout, stderr) = Run(operations, [.. UploadArgs(), "--json"]);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("publication-transport-failed", root.GetProperty("errorKind").GetString());
        Assert.False(root.TryGetProperty("partialResult", out _));
        Assert.Contains("indeterminate", root.GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_JsonMakesEveryEmpiricalNonclaimExplicit()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), [.. DeployArgs(), "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        Assert.Equal("deployment-projection-acknowledged", root.GetProperty("status").GetString());
        Assert.Equal(Revision, root.GetProperty("packageRevisionId").GetString());
        Assert.Equal(
            "77777777-7777-7777-7777-777777777777",
            root.GetProperty("deploymentId").GetString());
        Assert.True(root.GetProperty("projectionAcknowledged").GetBoolean());
        Assert.Equal("current-grs", root.GetProperty("packageSource").GetString());
        Assert.False(root.GetProperty("publicDeployOperationCreated").GetBoolean());
        Assert.False(root.GetProperty("targetLocalPackageStagingPerformed").GetBoolean());
        Assert.False(root.GetProperty("runtimeReadinessProven").GetBoolean());
        Assert.False(root.GetProperty("crossIslandDemonstrated").GetBoolean());
        Assert.False(root.TryGetProperty("packageOperationId", out _));
    }

    [Fact]
    public void Deploy_HumanOutput_DistinguishesProjectionFromCrossIslandProof()
    {
        var (_, stdout, _) = Run(new FakeOperations(), DeployArgs());

        Assert.Contains(
            "Deployment created and target-routed AHRG projection acknowledged.",
            stdout,
            StringComparison.Ordinal);
        Assert.Contains("Package source: current GRS", stdout, StringComparison.Ordinal);
        Assert.Contains("Public Deploy Operation: not created", stdout, StringComparison.Ordinal);
        Assert.Contains("Cross-island call: not demonstrated.", stdout, StringComparison.Ordinal);
        Assert.Contains("Runtime readiness: not proven.", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Package uploaded", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--package")]
    [InlineData("--endpoint")]
    [InlineData("--resource")]
    [InlineData("--repository")]
    [InlineData("--storage")]
    [InlineData("--owner")]
    public void Deploy_RejectsPackageBytesOrCallerControlledServiceOverrides(string option)
    {
        var operations = new FakeOperations();
        var args = DeployArgs().Concat([option, "untrusted"]).ToArray();

        var (exitCode, _, _) = Run(operations, args);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastDeployRequest);
        Assert.Null(operations.LastPackageUploadRequest);
    }

    [Fact]
    public void AgentGroupWithoutAVerb_IsAUsageError()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), "agent");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Valid subgroups: package. Valid commands: create, copy, init, list, show, pull, delete, deploy, share, unshare.", stderr, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(
        IAgentHostingOperations operations,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(
            operations,
            featureFlags: new TestFeatureFlags(FeatureName.AmsFeatures));
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string[] UploadArgs() =>
    [
        "agent", "package", "upload",
        "--agent-id", Agent,
        "--package", ".\\agent.zip",
        "--environment-id", Home,
    ];

    private static string[] DeployArgs() =>
    [
        "agent", "deploy",
        "--agent-id", Agent,
        "--package-revision", Revision,
        "--environment-id", Home,
        "--target-environment-id", Target,
        "--deployment-name", "dev-proof",
        "--deployment-type", "dev",
        "--entra-id", Entra,
        "--entra-id-type", "app-reg",
    ];
}
