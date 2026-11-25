using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentDeployRoutingTests
{
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void Deploy_WithoutInputs_DefersToRecordAwareMcsValidation()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "deploy");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastManagedAgentDeployRequest);
        Assert.Null(operations.LastDeployRequest);
    }

    [Fact]
    public void Deploy_ProjectOnly_DefersToRecordAwareMcsValidation()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "deploy", "--project", "./my-agent");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("./my-agent", operations.LastManagedAgentDeployRequest?.Project);
        Assert.Null(operations.LastDeployRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Deploy_OptionsOnlyMcsTakes_KeepTheMcsRouteWithoutALocalRecord(bool withProject)
    {
        var operations = new FakeOperations();
        string[] args = ["agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "prod-west", "--schema-name", "cr_other", "--publish", "--overwrite"];

        var (exitCode, _, _) = Run(operations, withProject ? [.. args, "--project", "./mcs-project"] : args);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(operations.LastDeployRequest);
        var request = Assert.IsType<ManagedAgentDeployRequest>(operations.LastManagedAgentDeployRequest);
        Assert.Equal(withProject ? "./mcs-project" : null, request.Project);
        Assert.Equal(("agent-1", "prod-west", "cr_other", true, true), (request.AgentId, request.DeploymentName, request.SchemaName, request.Publish, request.Overwrite));
    }

    [Theory]
    [InlineData(DeploymentAgentTypeSource.DeploymentRecord)]
    [InlineData(DeploymentAgentTypeSource.AgentRecord)]
    public void Deploy_ALocalRecordOfTheAgentNamed_KeepsTheMcsRoute(DeploymentAgentTypeSource source)
    {
        var operations = LocalRecord(source);

        var (exitCode, _, _) = Run(operations, "agent", "deploy", "--agent-name", "support", "--deployment-id", "deployment-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(operations.LastDeployRequest);
        var request = Assert.IsType<ManagedAgentDeployRequest>(operations.LastManagedAgentDeployRequest);
        Assert.Equal(("support", "deployment-1", false, false), (request.AgentName, request.DeploymentId, request.Publish, request.Overwrite));
        var query = Assert.IsType<DeploymentAgentTypeQuery>(operations.LastDeploymentAgentTypeQuery);
        Assert.True(query.RequireNamedAgentMatch);
        Assert.Equal(("support", "deployment-1"), (query.AgentName, query.DeploymentId));
    }

    [Theory]
    [InlineData(DeploymentAgentTypeSource.Default)]
    [InlineData(DeploymentAgentTypeSource.Unresolved)]
    [InlineData(DeploymentAgentTypeSource.OptionShape)]
    public void Deploy_WithoutALocalRecordOfTheAgentNamed_TakesTheAmsRouteWithTheActiveEnvironment(DeploymentAgentTypeSource source)
    {
        var operations = LocalRecord(source);

        var (exitCode, _, _) = Run(operations, "agent", "deploy", "-an", "Contoso Agent", "-dn", "dev-proof");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(operations.LastManagedAgentDeployRequest);
        var request = Assert.IsType<AgentDeployRequest>(operations.LastDeployRequest);
        Assert.Equal(("Contoso Agent", "dev-proof", (string?)null, (string?)null, (string?)null, (string?)null), (request.AgentName, request.DeploymentName, request.AgentId, request.DeploymentId, request.EnvironmentId, request.CommitId));
    }

    [Fact]
    public void Deploy_EnvironmentId_TakesTheAmsRouteEvenForALocalRecord()
    {
        var operations = LocalRecord(DeploymentAgentTypeSource.DeploymentRecord);

        var (exitCode, _, _) = Run(operations, "agent", "deploy", "-aid", Agent, "-did", Deployment, "-eid", "home", "-cid", Commit);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Null(operations.LastManagedAgentDeployRequest);
        Assert.Equal((Agent, Deployment, "home", Commit), (operations.LastDeployRequest!.AgentId, operations.LastDeployRequest.DeploymentId, operations.LastDeployRequest.EnvironmentId, operations.LastDeployRequest.CommitId));
    }

    [Theory]
    [InlineData("--commit-id", "")]
    [InlineData("-cid", "   ")]
    [InlineData("--commit-id=", null)]
    public void Deploy_ABlankCommit_ReachesTheAmsRouteAsGivenRatherThanAsOmitted(string option, string? value)
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, ["agent", "deploy", "-aid", Agent, "-did", Deployment, "-eid", "home", option, .. value is null ? Array.Empty<string>() : [value]]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(value ?? string.Empty, operations.LastDeployRequest!.CommitId);
    }

    [Fact]
    public void Deploy_WithAmsFeaturesOff_KeepsTodaysRouteAndRefusesTheCommit()
    {
        var operations = new FakeOperations();

        var (routed, _, _) = Run(operations, [], "agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "dev");
        var (refused, _, stderr) = Run(new FakeOperations(), [], "agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "dev", "--commit-id", Commit);

        Assert.Equal(ExitCodes.Success, routed);
        Assert.NotNull(operations.LastManagedAgentDeployRequest);
        Assert.Null(operations.LastDeployRequest);
        Assert.Equal(ExitCodes.Usage, refused);
        Assert.Contains("--commit-id is not supported: this build has no Git Repository Service", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--environment-id")]
    [InlineData("--target-environment-id")]
    [InlineData("--package-revision")]
    public void Deploy_WithAmsFeaturesOff_RefusesTheOptionsThatSelectTheAmsRoute(string option)
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, [], "agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "dev", option, "value");

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Contains(option, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_CommitOnTheMcsRoute_IsStillRefused()
    {
        var operations = LocalRecord(DeploymentAgentTypeSource.DeploymentRecord);

        var (exitCode, _, stderr) = Run(operations, "agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "dev", "--commit-id", Commit);

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Contains("--commit-id is not supported: this build has no Git Repository Service", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_AsyncKeepsTheLocalRouteWhereItIsRefused()
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "dev", "--async");

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Contains("--async is not supported", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--project", "./mcs-project")]
    [InlineData("--schema-name", "cr_other")]
    [InlineData("--publish", null)]
    [InlineData("--overwrite", null)]
    [InlineData("--async", null)]
    public void Deploy_AmsRoute_RefusesOptionsOnlyMcsTakesBeforeDispatch(string option, string? value)
    {
        var operations = new FakeOperations();
        string[] args = ["agent", "deploy", "-aid", Agent, "-did", Deployment, "-eid", "home", option];

        var (exitCode, stdout, stderr) = Run(operations, [.. value is null ? args : [.. args, value], "--json"]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal($"{option} cannot be combined with the AMS route of `msagent agent deploy`.", document.RootElement.GetProperty("errorMessage").GetString());
    }

    [Theory]
    [InlineData("--target-environment-id", "this option moved to `msagent deployment create`.")]
    [InlineData("-tenv", "this option moved to `msagent deployment create`.")]
    [InlineData("--deployment-type", "this option moved to `msagent deployment create`.")]
    [InlineData("-dt", "this option moved to `msagent deployment create`.")]
    [InlineData("--entra-id", "this option moved to `msagent deployment create`.")]
    [InlineData("-ent", "this option moved to `msagent deployment create`.")]
    [InlineData("--entra-id-type", "this option moved to `msagent deployment create`.")]
    [InlineData("-eit", "this option moved to `msagent deployment create`.")]
    [InlineData("--endpoint-url", "this option moved to `msagent deployment create` and `msagent deployment update`.")]
    [InlineData("-eu", "this option moved to `msagent deployment create` and `msagent deployment update`.")]
    [InlineData("--package-revision", "use `--commit-id`; `msagent agent commit list` shows the commits.")]
    [InlineData("-pr", "use `--commit-id`; `msagent agent commit list` shows the commits.")]
    public void Deploy_MovedOptions_SelectTheAmsRouteEvenForALocalRecordAndAreRefusedWithTheirPointer(string option, string pointer)
    {
        var operations = LocalRecord(DeploymentAgentTypeSource.DeploymentRecord);

        var (exitCode, _, stderr) = Run(operations, "agent", "deploy", "--agent-id", "agent-1", "--deployment-name", "dev", option, "value");

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Contains(pointer, stderr, StringComparison.Ordinal);
        Assert.Contains("is not an option of `msagent agent deploy` for a code agent", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_EmptyMovedOption_DoesNotFallThroughToMcs()
    {
        var operations = new FakeOperations();

        var (exitCode, _, _) = Run(operations, "agent", "deploy", "--deployment-name", "dev", "--package-revision", "");

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
    }

    [Theory]
    [InlineData("-aid", Agent, "--deployment-id or --deployment-name is required for the AMS route of `msagent agent deploy`.")]
    [InlineData("-did", Deployment, "--agent-id or --agent-name is required for the AMS route of `msagent agent deploy`.")]
    public void Deploy_AmsRouteWithoutItsAgentOrDeployment_IsRefusedBeforeDispatch(string option, string value, string message)
    {
        var operations = new FakeOperations();

        var (exitCode, _, stderr) = Run(operations, "agent", "deploy", "-eid", "home", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        AssertNothingDispatched(operations);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("deployment-lifecycle-unavailable")]
    [InlineData("deployment-not-found")]
    public void Deploy_AmsRouteRefusal_NeverFallsBackToMcs(string errorKind)
    {
        var operations = new FakeOperations { OnAgentDeploy = _ => throw AgentDeployException.ServiceRejected("AMS refused.", ExitCodes.Failure, errorKind, "Ask.") };

        var (exitCode, _, stderr) = Run(operations, "agent", "deploy", "-aid", Agent, "-did", Deployment);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.NotNull(operations.LastDeployRequest);
        Assert.Null(operations.LastManagedAgentDeployRequest);
        Assert.Contains("AMS refused.", stderr, StringComparison.Ordinal);
    }

    private static FakeOperations LocalRecord(DeploymentAgentTypeSource source) => new() { OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = source } };

    private static void AssertNothingDispatched(FakeOperations operations)
    {
        Assert.Null(operations.LastDeployRequest);
        Assert.Null(operations.LastPackageUploadRequest);
        Assert.Null(operations.LastManagedAgentDeployRequest);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, params string[] args) => Run(operations, [FeatureName.AmsFeatures], args);

    private static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, FeatureName[] features, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(operations, featureFlags: new TestFeatureFlags(features)).InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }
}
