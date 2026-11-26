using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentDeployCommandHostTests
{
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Home = "22222222-2222-2222-2222-222222222222";
    private const string Target = "33333333-3333-3333-3333-333333333333";
    private const string Operation = "55555555-5555-5555-5555-555555555555";
    private const string Revision = "66666666-6666-6666-6666-666666666666";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Entra = "88888888-8888-8888-8888-888888888888";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string TunnelEndpoint = "https://contoso-3978.usw2.devtunnels.ms/api/messages";
    private const string BotName = "ams-11111111-33333333-88888888-77777777";

    [Fact]
    public void PackageUpload_BindsOnlyPackagePublicationInputs()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, UploadArgs());

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = Assert.IsType<AgentPackageUploadRequest>(operations.LastPackageUploadRequest);
        Assert.Equal((Agent, (string?)null, ".\\agent.zip", Home), (request.AgentId, request.AgentName, request.PackagePath, request.EnvironmentId));
        Assert.Null(operations.LastDeployRequest);
    }

    [Fact]
    public void PackageUpload_BindsTheAgentNameWithoutAnEnvironment()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "package", "upload", "-an", "Contoso Agent", "--package", "agent.zip");

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = Assert.IsType<AgentPackageUploadRequest>(operations.LastPackageUploadRequest);
        Assert.Equal(((string?)null, "Contoso Agent", (string?)null), (request.AgentId, request.AgentName, request.EnvironmentId));
    }

    [Fact]
    public void PackageUpload_WithoutAnAgent_IsAUsageErrorAndDispatchesNothing()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "package", "upload", "--package", "agent.zip");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastPackageUploadRequest);
        Assert.Contains("--agent-id or --agent-name is required for the AMS route of `msagent agent package upload`.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageUpload_JsonReportsTheDurableReceiptWithItsCommitAndNoDeployment()
    {
        var operations = new FakeOperations { OnAgentPackageUpload = _ => FakeOperations.CreateStubPackageUploadResult() with { CommitId = Commit } };

        var (exitCode, stdout, _) = Run(operations, [.. UploadArgs(), "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("package-uploaded", root.GetProperty("status").GetString());
        Assert.Equal(Revision, root.GetProperty("packageRevisionId").GetString());
        Assert.Equal(Commit, root.GetProperty("commitId").GetString());
        Assert.Equal(Operation, root.GetProperty("packageOperationId").GetString());
        Assert.False(root.TryGetProperty("deploymentId", out _));
        Assert.False(root.TryGetProperty("targetEnvironmentId", out _));
    }

    [Fact]
    public void PackageUpload_HumanOutput_EndsWithTheCommit()
    {
        var operations = new FakeOperations { OnAgentPackageUpload = _ => FakeOperations.CreateStubPackageUploadResult() with { CommitId = Commit } };

        var (_, stdout, _) = Run(operations, UploadArgs());

        Assert.Equal($"  commit: {Commit}", stdout.TrimEnd().Split(Environment.NewLine)[^1]);
    }

    [Theory]
    [InlineData("Accepted", true)]
    [InlineData("Failed", true)]
    [InlineData("Accepted", false)]
    [InlineData("Failed", false)]
    public void PackageUpload_DurableFailure_RendersTypedOperationIdentity(string status, bool json)
    {
        var operations = new FakeOperations { OnAgentPackageUpload = _ => throw PackagePublicationException.DurableOperationDidNotSucceed(Operation, status, "client-request-id", "service-request-id") };

        var (exitCode, stdout, stderr) = Run(operations, json ? [.. UploadArgs(), "--json"] : UploadArgs());

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.NotNull(operations.LastPackageUploadRequest);
        Assert.Null(operations.LastDeployRequest);
        if (json)
        {
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            Assert.False(root.GetProperty("success").GetBoolean());
            Assert.Equal("publication-failed", root.GetProperty("errorKind").GetString());
            var partial = root.GetProperty("partialResult");
            Assert.Equal(Operation, partial.GetProperty("packageOperationId").GetString());
            Assert.Equal(status, partial.GetProperty("operationStatus").GetString());
            Assert.False(partial.TryGetProperty("packageRevisionId", out _));
        }
        else
        {
            Assert.Empty(stdout);
            Assert.Contains(Operation, stderr, StringComparison.Ordinal);
            Assert.Contains($"confirmed status: {status}", stderr, StringComparison.Ordinal);
            Assert.Contains("Do not retry blindly", stderr, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackageUpload_IndeterminateFailure_ReportsSafeClassificationWithoutDurableIdentity(bool json)
    {
        var operations = new FakeOperations
        {
            OnAgentPackageUpload = _ => throw PackagePublicationException.RequestFailed(
                "client-request-id",
                new HttpRequestException(HttpRequestError.ResponseEnded, "private-response-detail")),
        };

        var (exitCode, stdout, stderr) = Run(operations, json ? [.. UploadArgs(), "--json"] : UploadArgs());

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.DoesNotContain("private-response-detail", stdout + stderr, StringComparison.Ordinal);
        if (!json)
        {
            Assert.Empty(stdout);
            Assert.Contains("failureReason=response-ended", stderr, StringComparison.Ordinal);
            return;
        }

        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("publication-transport-failed", root.GetProperty("errorKind").GetString());
        Assert.Equal(15007, root.GetProperty("errorCode").GetInt32());
        Assert.Equal("request", root.GetProperty("failureStage").GetString());
        Assert.Equal("response-ended", root.GetProperty("failureReason").GetString());
        Assert.False(root.TryGetProperty("httpStatusCode", out _));
        Assert.False(root.TryGetProperty("partialResult", out _));
        Assert.Contains("indeterminate", root.GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageUpload_ResponseBodyFailure_ReportsReceivedStatusWithoutResponseDetails()
    {
        var operations = new FakeOperations
        {
            OnAgentPackageUpload = _ => throw PackagePublicationException.RequestFailed(
                "client-request-id",
                new HttpRequestException(HttpRequestError.ResponseEnded, "private-response-detail"),
                503),
        };

        var (exitCode, stdout, stderr) = Run(operations, [.. UploadArgs(), "--json"]);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("response-body", root.GetProperty("failureStage").GetString());
        Assert.Equal("response-ended", root.GetProperty("failureReason").GetString());
        Assert.Equal(503, root.GetProperty("httpStatusCode").GetInt32());
        Assert.DoesNotContain("before response", root.GetProperty("errorMessage").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-response-detail", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_BindsTheAgentTheExistingDeploymentTheHomeEnvironmentAndTheCommit()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, DeployArgs());

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = Assert.IsType<AgentDeployRequest>(operations.LastDeployRequest);
        Assert.Equal((Agent, Deployment, Home, Commit), (request.AgentId, request.DeploymentId, request.EnvironmentId, request.CommitId));
        Assert.Null(operations.LastPackageUploadRequest);
        Assert.Null(operations.LastManagedAgentDeployRequest);
    }

    [Fact]
    public void Deploy_JsonReportsTheDeploymentWhatWentLiveAndTheBot()
    {
        var operations = new FakeOperations { OnAgentDeploy = _ => FakeOperations.CreateStubDeployResult() with { Bot = AgentDeployResult.BotCreated, Deployment = FakeOperations.CreateStubDeploymentRecord() with { BotService = Bot() } } };

        var (exitCode, stdout, _) = Run(operations, [.. DeployArgs(), "--json"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal("agent-deployment-deployed", root.GetProperty("status").GetString());
        Assert.Equal("created", root.GetProperty("bot").GetString());
        var deployment = root.GetProperty("deployment");
        Assert.Equal(Deployment, deployment.GetProperty("deploymentId").GetString());
        Assert.Equal(Target, deployment.GetProperty("targetEnvironmentId").GetString());
        Assert.Equal("Deployed", deployment.GetProperty("state").GetString());
        Assert.Equal("package", deployment.GetProperty("kind").GetString());
        Assert.Equal(Commit, deployment.GetProperty("commitId").GetString());
        Assert.Equal(Revision, deployment.GetProperty("packageRevisionId").GetString());
        Assert.Equal(BotName, deployment.GetProperty("botService").GetProperty("name").GetString());
        Assert.Equal(Entra, deployment.GetProperty("botService").GetProperty("msaAppId").GetString());
        Assert.False(root.GetProperty("kindSwitched").GetBoolean());
    }

    [Theory]
    [InlineData(AgentDeploymentAmsRecord.PackageKind, $"Switched from the dev endpoint to commit {Commit}; the bot now points at https://runtime.example/api/messages.")]
    [InlineData(AgentDeploymentAmsRecord.DevEndpointKind, $"Switched from the package to the dev endpoint {TunnelEndpoint}.")]
    public void Deploy_TextAndJsonSayWhenTheDeploySwitchedTheDeploymentsKind(string kind, string switchLine)
    {
        var deployment = kind == AgentDeploymentAmsRecord.PackageKind ? FakeOperations.CreateStubDeploymentRecord() with { BotService = Bot() } : FakeOperations.CreateStubDeploymentRecord() with { Kind = kind, PackageRevisionId = null, CommitId = null, Endpoint = TunnelEndpoint, RequestedEndpoint = TunnelEndpoint, BotService = Bot() };
        var operations = new FakeOperations { OnAgentDeploy = _ => FakeOperations.CreateStubDeployResult() with { Bot = AgentDeployResult.BotKept, KindSwitched = true, Deployment = deployment } };

        var (_, stdout, _) = Run(operations, DeployArgs());
        var (exitCode, json, _) = Run(operations, [.. DeployArgs(), "--json"]);

        Assert.Contains(switchLine, stdout, StringComparison.Ordinal);
        Assert.Contains($"Azure Bot Service: kept {BotName}.", stdout, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("kindSwitched").GetBoolean());
        Assert.Equal(kind, document.RootElement.GetProperty("deployment").GetProperty("kind").GetString());
    }

    [Fact]
    public void Deploy_HumanOutput_ShowsTheDeploymentAndEndsWithWhetherTheBotWasCreatedOrKept()
    {
        var operations = new FakeOperations { OnAgentDeploy = _ => FakeOperations.CreateStubDeployResult() with { Bot = AgentDeployResult.BotKept, Deployment = FakeOperations.CreateStubDeploymentRecord() with { BotService = Bot() } } };

        var (_, stdout, _) = Run(operations, DeployArgs());

        Assert.Contains($"Deployment dev-proof ({Deployment}) deployed.", stdout, StringComparison.Ordinal);
        Assert.Contains($"  package: commit {Commit}, Package Revision {Revision},", stdout, StringComparison.Ordinal);
        Assert.Equal($"Azure Bot Service: kept {BotName}.", stdout.TrimEnd().Split(Environment.NewLine)[^1]);
        Assert.DoesNotContain("Live:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Switched", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_HumanOutput_ShowsTheDevEndpointThatWentLive()
    {
        var operations = new FakeOperations { OnAgentDeploy = _ => FakeOperations.CreateStubDeployResult() with { PreviousEndpoint = "https://runtime.example/api/messages", Deployment = FakeOperations.CreateStubDeploymentRecord() with { Kind = AgentDeploymentAmsRecord.DevEndpointKind, PackageRevisionId = null, CommitId = null, Endpoint = TunnelEndpoint, RequestedEndpoint = TunnelEndpoint } } };

        var (_, stdout, _) = Run(operations, DeployArgs());

        Assert.Contains($"  endpoint: {TunnelEndpoint}", stdout, StringComparison.Ordinal);
        Assert.Contains("Azure Bot Service: none; AMS bound no bot to this deployment.", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Live:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("  package:", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--package")]
    [InlineData("--endpoint")]
    [InlineData("--resource")]
    [InlineData("--repository")]
    [InlineData("--owner")]
    public void Deploy_RejectsPackageBytesOrCallerControlledServiceOverrides(string option)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, [.. DeployArgs(), option, "untrusted"]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Null(operations.LastDeployRequest);
        Assert.Null(operations.LastPackageUploadRequest);
    }

    [Fact]
    public void AgentGroupWithoutAVerb_ListsTheCodeAgentCommands()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), "agent");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Valid subgroups: package, commit. Valid commands: create, copy, init, list, show, update, pull, delete, deploy, share, unshare.", stderr, StringComparison.Ordinal);
    }

    private static AgentDeploymentBotService Bot() => new() { ResourceId = $"/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/ams-bots/providers/Microsoft.BotService/botServices/{BotName}", Name = BotName, MsaAppId = Entra };

    private static (int ExitCode, string Stdout, string Stderr) Run(IAgentHostingOperations operations, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(operations, featureFlags: new TestFeatureFlags(FeatureName.AmsFeatures)).InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static string[] UploadArgs() => ["agent", "package", "upload", "--agent-id", Agent, "--package", ".\\agent.zip", "--environment-id", Home];

    private static string[] DeployArgs() => ["agent", "deploy", "--agent-id", Agent, "--deployment-id", Deployment, "--environment-id", Home, "--commit-id", Commit];
}
