using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentAmsCommandHostTests
{
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Deployment = "77777777-7777-7777-7777-777777777777";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(null)]
    [InlineData("env-1")]
    public void List_CodeAgents_TakesTheAmsRouteInTheHomeEnvironment(string? environmentId)
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();
        string[] args = ["agent", "list", "-at", "CodeAgent", "--json"];

        var (exitCode, stdout, _) = Run(operations, handlers, environmentId is null ? args : [.. args, "-eid", environmentId]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(environmentId, handlers.Single<AgentAmsListRequest>().EnvironmentId);
        Assert.Null(operations.LastManagedAgentListRequest);
        using var document = JsonDocument.Parse(stdout);
        var agent = Assert.Single(document.RootElement.GetProperty("agents").EnumerateArray());
        Assert.Equal(("Contoso Agent", "CodeAgent"), (agent.GetProperty("displayName").GetString(), agent.GetProperty("agentType").GetString()));
    }

    [Fact]
    public void List_CodeAgents_RendersTheirTable()
    {
        var (_, stdout, _) = Run(new FakeOperations(), new RecordingAmsHandlers(), "agent", "list", "--agent-type", "codeagent");

        Assert.Contains("Code agents of environment 22222222-2222-2222-2222-222222222222", stdout, StringComparison.Ordinal);
        Assert.Contains("Contoso Agent", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--filter", "text")]
    [InlineData("--tenant-wide", null)]
    [InlineData("--limit", "5")]
    [InlineData("--project", ".")]
    public void List_CodeAgents_RefusesOptionsOnlyTheMcsListingTakes(string option, string? value)
    {
        var handlers = new RecordingAmsHandlers();
        string[] args = ["agent", "list", "-at", "CodeAgent", option];

        var (exitCode, _, stderr) = Run(new FakeOperations(), handlers, value is null ? args : [.. args, value]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains($"{option} cannot be combined with the AMS route of `msagent agent list`.", stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
    }

    [Fact]
    public void List_WithoutAnAgentType_KeepsTheMcsListing()
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, _) = Run(operations, handlers, "agent", "list");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(handlers.Requests);
        Assert.Null(operations.LastManagedAgentListRequest!.AgentType);
    }

    [Fact]
    public void List_CodeAgents_IsGatedByTheAmsFeatures()
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, stderr) = AmsCommandHost.Run(operations, handlers, new TestFeatureFlags(), "agent", "list", "--agent-type", "CodeAgent");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Listing code agents", stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
        Assert.Null(operations.LastManagedAgentListRequest);
    }

    [Fact]
    public void Show_AnAgentWithoutALocalRecord_TakesTheAmsRoute()
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, _) = Run(operations, handlers, "agent", "show", "-an", "Contoso Agent");

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = handlers.Single<AgentAmsShowRequest>();
        Assert.Equal(("Contoso Agent", (string?)null, (string?)null), (request.AgentName, request.AgentId, request.EnvironmentId));
        Assert.Null(operations.LastManagedAgentShowRequest);
        Assert.Contains($"Agent Contoso Agent ({Agent})", stdout, StringComparison.Ordinal);
        Assert.Contains("dev-proof", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_Json_CarriesTheDeploymentsAndTheirAvailability()
    {
        var (_, stdout, _) = Run(new FakeOperations(), new RecordingAmsHandlers(), "agent", "show", "-aid", Agent, "-eid", "env-1", "--json");

        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.GetProperty("deploymentsAvailable").GetBoolean());
        Assert.Equal(Deployment, Assert.Single(document.RootElement.GetProperty("deployments").EnumerateArray()).GetProperty("deploymentId").GetString());
    }

    [Theory]
    [InlineData(DeploymentAgentTypeSource.AgentRecord)]
    [InlineData(DeploymentAgentTypeSource.DeploymentRecord)]
    public void Show_ALocalRecordOfTheAgentNamed_KeepsTheMcsRoute(DeploymentAgentTypeSource source)
    {
        var operations = new FakeOperations { OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult { AgentType = AgentTypes.McsAgent, Source = source } };
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, _) = Run(operations, handlers, "agent", "show", "-an", "Support");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastManagedAgentShowRequest);
        Assert.Empty(handlers.Requests);
    }

    [Fact]
    public void Show_WithAmsFeaturesOff_KeepsTheMcsRoute()
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, _) = AmsCommandHost.Run(operations, handlers, new TestFeatureFlags(), "agent", "show", "-an", "Support");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastManagedAgentShowRequest);
        Assert.Empty(handlers.Requests);
    }

    [Fact]
    public void Update_BindsTheNewNameAndAnEmptyDescriptionThatClearsIt()
    {
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, _) = Run(new FakeOperations(), handlers, "agent", "update", "-aid", Agent, "-nn", "Renamed", "--description", "", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        var request = handlers.Single<AgentAmsUpdateRequest>();
        Assert.Equal((Agent, "Renamed", string.Empty), (request.AgentId, request.NewName, request.Description));
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(AgentAmsUpdateResult.UpdatedStatus, document.RootElement.GetProperty("status").GetString());
        Assert.Equal("Renamed", document.RootElement.GetProperty("agent").GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData(new[] { "-aid", Agent }, "--new-name or --description is required for `msagent agent update`.")]
    [InlineData(new[] { "-nn", "Renamed" }, "--agent-id or --agent-name is required for the AMS route of `msagent agent update`.")]
    public void Update_WithoutItsRequiredOptions_DispatchesNothing(string[] options, string message)
    {
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, stderr) = Run(new FakeOperations(), handlers, ["agent", "update", .. options]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
    }

    [Fact]
    public void Update_NameInUse_IsReported()
    {
        var handlers = new RecordingAmsHandlers { Failure = AgentDeployException.ServiceRejected($"AMS refused the agent name: The name 'Renamed' is already used by agent {Deployment} in this environment.", ExitCodes.Usage, "agent-name-in-use", "Choose another name, or use the agent that has it.") };

        var (exitCode, _, stderr) = Run(new FakeOperations(), handlers, "agent", "update", "-aid", Agent, "-nn", "Renamed");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains($"is already used by agent {Deployment}", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_AnAgentWithoutALocalRecord_TakesTheAmsRouteAndListsOnlyWhatWasRemoved()
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, _) = Run(operations, handlers, "agent", "delete", "-aid", Agent, "--non-interactive");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.True(handlers.Single<AgentAmsDeleteRequest>().NonInteractive);
        Assert.Null(operations.LastManagedAgentDeleteRequest);
        Assert.Contains($"Agent Contoso Agent ({Agent}) deleted.", stdout, StringComparison.Ordinal);
        Assert.Contains("Removed:", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Kept by AMS", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("hosting runtime", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_Json_HoldsOnlyWhatWasRemoved()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), new RecordingAmsHandlers(), "agent", "delete", "-aid", Agent, "--non-interactive", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.Equal(AgentAmsDeleteResult.DeletedStatus, root.GetProperty("status").GetString());
        Assert.Equal(Deployment, Assert.Single(root.GetProperty("deployments").EnumerateArray()).GetProperty("deploymentId").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("keptBots").ValueKind);
        Assert.False(root.TryGetProperty("runtimeCopiesKept", out _));
        Assert.False(root.TryGetProperty("ppmiRegistrationsKept", out _));
    }

    [Fact]
    public void Delete_Cancelled_ExitsSuccessfullyAndSaysNothingWasDeleted()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), new RecordingAmsHandlers { DeleteStatus = AgentAmsConfirmation.CancelledStatus }, "agent", "delete", "-an", "Contoso Agent", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(AgentAmsConfirmation.CancelledStatus, document.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("--all-resources", null)]
    [InlineData("--deployment-name", "dev")]
    [InlineData("--project", ".")]
    public void Delete_OptionsOnlyMcsTakes_KeepTheMcsRoute(string option, string? value)
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers();
        string[] args = ["agent", "delete", "-aid", Agent, "--non-interactive", option];

        var (exitCode, _, _) = Run(operations, handlers, value is null ? args : [.. args, value]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.NotNull(operations.LastManagedAgentDeleteRequest);
        Assert.Empty(handlers.Requests);
    }

    [Fact]
    public void Delete_ADeployStillRunning_PrintsAmsMessageAndExitsWithAFailureWithoutFallingBack()
    {
        var operations = new FakeOperations();
        var handlers = new RecordingAmsHandlers { Failure = AgentDeployException.ServiceRejected("AMS cannot finish deleting the agent yet: Run it again after 10:42 UTC.", ExitCodes.Failure, "agent-deploy-in-progress", "Run `msagent agent delete` again at the time the message names.") };

        var (exitCode, _, stderr) = Run(operations, handlers, "agent", "delete", "-aid", Agent, "-eid", "env-1", "--non-interactive");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Contains("Run it again after 10:42 UTC.", stderr, StringComparison.Ordinal);
        Assert.Null(operations.LastManagedAgentDeleteRequest);
    }

    [Fact]
    public void CommitList_RendersTheCommitsWithTheirDeploymentsAsTextAndJson()
    {
        var handlers = new RecordingAmsHandlers();

        var (exitCode, stdout, _) = Run(new FakeOperations(), handlers, "agent", "commit", "list", "-an", "Contoso Agent", "--json");
        var (_, text, _) = Run(new FakeOperations(), new RecordingAmsHandlers(), "agent", "commit", "list", "-aid", Agent, "-eid", "env-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("Contoso Agent", handlers.Single<AgentCommitListRequest>().AgentName);
        using var document = JsonDocument.Parse(stdout);
        var commit = Assert.Single(document.RootElement.GetProperty("commits").EnumerateArray());
        Assert.Equal(Commit, commit.GetProperty("commitId").GetString());
        Assert.Equal("dev-proof", Assert.Single(commit.GetProperty("deployments").EnumerateArray()).GetString());
        Assert.Contains($"Commits of agent {Agent}, newest first", text, StringComparison.Ordinal);
        Assert.Contains(Commit, text, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitList_WithoutAnAgent_DispatchesNothing()
    {
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, stderr) = Run(new FakeOperations(), handlers, "agent", "commit", "list");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--agent-id or --agent-name is required for the AMS route of `msagent agent commit list`.", stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("commit")]
    public void AmsOnlyAgentCommands_AreUnavailableWhenTheFeatureIsOff(string verb)
    {
        var handlers = new RecordingAmsHandlers();

        var (exitCode, _, _) = AmsCommandHost.Run(new FakeOperations(), handlers, new TestFeatureFlags(), verb == "update" ? ["agent", "update", "-aid", Agent, "-nn", "Renamed"] : ["agent", "commit", "list", "-aid", Agent]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(handlers.Requests);
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(FakeOperations operations, RecordingAmsHandlers handlers, params string[] args) => AmsCommandHost.Run(operations, handlers, new TestFeatureFlags(FeatureName.AmsFeatures), args);
}
