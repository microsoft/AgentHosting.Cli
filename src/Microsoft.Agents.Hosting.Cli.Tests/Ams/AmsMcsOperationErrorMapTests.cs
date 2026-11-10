using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AmsMcsOperationErrorMapTests
{
    private const string Requested = "2fd4e1c67a2d28fced849ee1bb76e7391b93eb12";

    private static readonly Uri Url = new("https://env-1.api.powerplatform.com/agenthosting/agents/a/mcs/deployments/d/deploy?api-version=1");
    private static readonly ServiceCorrelation Correlation = new("client-1", "service-1");

    public static TheoryData<string, int, int, int> ContractCodes => new()
    {
        { "McsCommitNotAllowedForStage", 400, ErrorCodes.AmsAgent.CommitNotAllowedForStage, ExitCodes.Usage },
        { "McsCommitNotAccepted", 400, ErrorCodes.AmsAgent.CommitNotAccepted, ExitCodes.Usage },
        { "McsDeploymentStageEnvironmentInvalid", 400, ErrorCodes.AmsAgent.DeploymentStageEnvironmentInvalid, ExitCodes.Usage },
        { "McsSourceDeploymentNotReady", 400, ErrorCodes.AmsAgent.SourceDeploymentNotReady, ExitCodes.Failure },
        { "McsDeploymentOperationInProgress", 409, ErrorCodes.AmsAgent.DeploymentOperationInProgress, ExitCodes.Failure },
        { "McsOperationCapacityExceeded", 429, ErrorCodes.AmsAgent.OperationCapacityExceeded, ExitCodes.Failure },
        { "McsSourceConflict", 409, ErrorCodes.AmsAgent.SourceConflict, ExitCodes.Usage },
        { "McsCaptureConflict", 409, ErrorCodes.AmsAgent.CaptureConflict, ExitCodes.Usage },
        { "McsDevEditsNotInCommit", 409, ErrorCodes.AmsAgent.DevEditsNotInCommit, ExitCodes.Usage },
        { "McsCaptureBaseUnknown", 409, ErrorCodes.AmsAgent.CaptureBaseUnknown, ExitCodes.Failure },
        { "McsRepositoryNotWritable", 403, ErrorCodes.AmsAgent.RepositoryNotWritable, ExitCodes.Failure },
        { "McsRepositoryNotFound", 404, ErrorCodes.AmsAgent.RepositoryNotFound, ExitCodes.Usage },
        { "McsRepositoryNotSupported", 400, ErrorCodes.AmsAgent.RepositoryNotSupported, ExitCodes.Usage },
        { "McsAgentFolderBound", 409, ErrorCodes.AmsAgent.AgentFolderBound, ExitCodes.Usage },
        { "McsBotClaimed", 409, ErrorCodes.AmsAgent.BotClaimed, ExitCodes.Usage },
        { "McsBotNotFound", 404, ErrorCodes.AmsAgent.BotNotFound, ExitCodes.Usage },
        { "McsDeploymentIdConflict", 409, ErrorCodes.AmsAgent.DeploymentIdConflict, ExitCodes.Failure },
        { "McsAgentIdConflict", 409, ErrorCodes.AmsAgent.AgentIdConflict, ExitCodes.Failure },
        { "McsSourceValidationFailed", 400, ErrorCodes.AmsAgent.SourceValidationFailed, ExitCodes.Usage },
        { "McsSourceTooLarge", 413, ErrorCodes.AmsAgent.SourceTooLarge, ExitCodes.Usage },
        { "McsSourceCheckFailed", 400, ErrorCodes.AmsAgent.SourceCheckFailed, ExitCodes.Usage },
        { "McsOnBehalfOfUnavailable", 403, ErrorCodes.AmsAgent.OnBehalfOfUnavailable, ExitCodes.Failure },
        { "McsOperationFailed", 500, ErrorCodes.AmsAgent.OperationFailed, ExitCodes.Failure },
        { "McsCaptureAgentNotInCloud", 409, ErrorCodes.AmsAgent.CaptureAgentNotInCloud, ExitCodes.Failure },
        { "InvalidAgentFieldValue", 400, ErrorCodes.AmsAgent.RequestRejected, ExitCodes.Usage },
        { "MissingAgentField", 400, ErrorCodes.AmsAgent.RequestRejected, ExitCodes.Usage },
    };

    [Theory]
    [MemberData(nameof(ContractCodes))]
    public void EveryContractCode_MapsToItsOwnCliError(string code, int status, int errorCode, int exitCode)
    {
        var mapped = Assert.IsType<AmsAgentException>(AmsMcsOperationErrorMap.Map(status, AmsMcsOperationAdapterTests.Error(code, "detail"), "POST", Url, Correlation));

        Assert.Equal((errorCode, exitCode), (mapped.ErrorCode, mapped.ExitCode));
        Assert.False(string.IsNullOrWhiteSpace(mapped.Remediation));
        Assert.EndsWith("(clientRequestId=client-1, serviceRequestId=service-1).", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryErrorFixture_IsAnErrorBodyThatMapsByItsCode()
    {
        var fixtures = Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Ams", "WireFixtures"), "*.error.json").Select(Path.GetFileName).ToArray();

        Assert.NotEmpty(fixtures);
        Assert.All(fixtures, fixture =>
        {
            var code = AmsMcsWire.ReadErrorBody(WireFixture.Read(fixture!))?.Code;
            var mapped = AmsMcsOperationErrorMap.Map(400, WireFixture.Read(fixture!), "POST", Url, Correlation);

            Assert.False(string.IsNullOrWhiteSpace(code), fixture);
            Assert.Equal(ContractCodes.Any(row => string.Equals((string)row[0], code, StringComparison.OrdinalIgnoreCase)) ? typeof(AmsAgentException) : typeof(TargetRequestException), mapped.GetType());
        });
    }

    [Theory]
    [InlineData("McsCommitNotAllowedForStage", "Deploy the commit to Dev first with `msagent agent deploy --remote --deployment-name <dev-deployment> --commit-id <commit>`, or drop --commit-id to take Dev's last deploy.")]
    [InlineData("McsCommitNotAccepted", "Drop --commit-id.")]
    [InlineData("McsSourceDeploymentNotReady", "Deploy to the preceding stage first with `msagent agent deploy --remote --deployment-name <preceding-deployment>`.")]
    [InlineData("McsCaptureAgentNotInCloud", "Deploy to it first with `msagent agent deploy --agent-id <agent-id> --deployment-name <dev-deployment>`, then capture.")]
    [InlineData("McsCaptureBaseUnknown", "Run `msagent agent deploy` to Dev from your clone, which records the commit Dev holds, then run the command again; a remote deploy with --overwrite replaces Dev's content instead.")]
    [InlineData("McsBotClaimed", "Clone that agent's repository and run `msagent agent init` there.")]
    public void CodesThatACommandResolves_NameTheCommand(string code, string remediation) =>
        Assert.Equal(remediation, AmsMcsOperationErrorMap.Map(409, AmsMcsOperationAdapterTests.Error(code, "detail"), "POST", Url, Correlation).Remediation);

    [Theory]
    [InlineData("McsSourceValidationFailed", true)]
    [InlineData("McsSourceConflict", true)]
    [InlineData("McsSourceCheckFailed", true)]
    [InlineData("McsBotClaimed", true)]
    [InlineData("InvalidAgentFieldValue", true)]
    [InlineData("McsRepositoryNotWritable", false)]
    [InlineData("McsRepositoryNotFound", false)]
    public void OnlyCodesWhoseDetailsExplainTheFailure_ShowTheSanitizedServiceMessage(string code, bool shown)
    {
        var mapped = AmsMcsOperationErrorMap.Map(400, AmsMcsOperationAdapterTests.Error(code, "Path ContosoHR/topics/Greeting.mcs.yml changed\\u001b[31m"), "POST", Url, Correlation);

        Assert.Equal(shown, mapped.Message.Contains("ContosoHR/topics/Greeting.mcs.yml", StringComparison.Ordinal));
        Assert.DoesNotContain("\u001b", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LongServiceDetail_IsBounded()
    {
        Assert.True(AmsMcsOperationErrorMap.Map(400, AmsMcsOperationAdapterTests.Error("McsSourceValidationFailed", new string('x', 5000)), "POST", Url, Correlation).Message.Length < 1200);
    }

    [Fact]
    public void MissingUserIdentity_AsksForSignIn()
    {
        var mapped = AmsMcsOperationErrorMap.Map(401, AmsMcsOperationAdapterTests.Error("MissingUserIdentity", "x"), "GET", Url, Correlation);

        Assert.Equal(ExitCodes.NotSignedIn, mapped.ExitCode);
        Assert.Contains("auth login", mapped.Remediation, StringComparison.Ordinal);
    }

    [Fact]
    public void CapacityCode_KeepsTheRetryAfter()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), Assert.IsType<AmsAgentException>(AmsMcsOperationErrorMap.Map(429, AmsMcsOperationAdapterTests.Error("mcsoperationcapacityexceeded", "x"), "POST", Url, Correlation, TimeSpan.FromSeconds(3))).RetryAfter);
    }

    [Theory]
    [InlineData("AgentNotFound", 404)]
    [InlineData("DeploymentNotFound", 404)]
    [InlineData("PreconditionFailed", 412)]
    [InlineData("PreconditionFailed", 409)]
    [InlineData("AgentConcurrencyConflict", 409)]
    [InlineData("InvalidAgentETag", 400)]
    public void ACodeOutsideTheMcsContract_FallsBackToTheStatusMapping_WithCorrelation(string code, int status)
    {
        var mapped = Assert.IsType<TargetRequestException>(AmsMcsOperationErrorMap.Map(status, AmsMcsOperationAdapterTests.Error(code, "x"), "DELETE", Url, Correlation));

        Assert.Equal(status, mapped.Status);
        Assert.Contains("clientRequestId=client-1", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceValidationFailed_WithAnotherDetail_StaysASourceRejection_AndShowsTheDetail()
    {
        var mapped = AmsMcsOperationErrorMap.Map(400, """{ "error": { "code": "McsSourceValidationFailed", "message": "The agent source is not a valid Copilot Studio workspace.", "details": [{ "code": "TopicInvalid", "message": "ContosoHR/topics/Greeting.mcs.yml: unknown kind." }] } }""", "PUT", Url, Correlation);

        Assert.Equal(ErrorCodes.AmsAgent.SourceValidationFailed, mapped.ErrorCode);
        Assert.Contains("Service detail: The agent source is not a valid Copilot Studio workspace.; TopicInvalid: ContosoHR/topics/Greeting.mcs.yml: unknown kind.", mapped.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CommitNotFound")]
    [InlineData("PathNotFound")]
    public void SourceValidationFailed_ThatNamesAMissingCommitOrFolder_IsCommitNotFound(string detailCode)
    {
        var mapped = AmsMcsOperationErrorMap.Map(400, CodeWithDetail("McsSourceValidationFailed", detailCode, "Commit 'abc' was not found."), "POST", Url, Correlation);

        Assert.Equal((ErrorCodes.AmsAgent.CommitNotFound, ExitCodes.Usage, "commit-not-found"), (mapped.ErrorCode, mapped.ExitCode, ((AmsAgentException)mapped).ErrorKind));
        Assert.Equal("Push the commit, or pass another --commit-id.", mapped.Remediation);
        Assert.Contains($"{detailCode}: Commit 'abc' was not found.", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeployWhoseCommitIsMissing_FailsAsCommitNotFound_WithGrsText()
    {
        var mapped = AmsMcsOperationErrorMap.OperationFailed(Operation("mcs-operation.deploy-commit-not-found.json"));

        Assert.Equal(ErrorCodes.AmsAgent.CommitNotFound, mapped.ErrorCode);
        Assert.Contains("CommitNotFound: Commit '2fd4e1c67a2d28fced849ee1bb76e7391b93eb12' was not found in repository 'cccccccc-3333-4333-8333-000000000003'.", mapped.Message, StringComparison.Ordinal);
        Assert.EndsWith("(operation 12345678-7777-4777-8777-000000000007).", mapped.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcs-agent-register.bot-claimed.error.json", "in folder 'ContosoHR' of repository 'cccccccc-3333-4333-8333-000000000013'")]
    [InlineData("mcs-agent-register.bot-claimed-other-environment.error.json", "is already claimed by agent 'aaaaaaaa-1111-4111-8111-000000000021'.")]
    public void BothFormsOfAClaimedBot_MapAlike_ShowingTheServiceText(string fixture, string shown)
    {
        var mapped = Assert.IsType<AmsAgentException>(AmsMcsOperationErrorMap.Map(409, WireFixture.Read(fixture), "POST", Url, Correlation));

        Assert.Equal((ErrorCodes.AmsAgent.BotClaimed, ExitCodes.Usage, "mcs-bot-claimed"), (mapped.ErrorCode, mapped.ExitCode, mapped.ErrorKind));
        Assert.Equal("Clone that agent's repository and run `msagent agent init` there.", mapped.Remediation);
        Assert.StartsWith("Another AMS agent already holds this Copilot Studio agent or flow. Service detail: Copilot Studio resource 'b0b0b0b0-4444-4444-8444-000000000014'", mapped.Message, StringComparison.Ordinal);
        Assert.Contains(shown, mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AClaimedBot_AfterRegisterCreatedARepository_NamesTheRepositoryToReuse()
    {
        var mapped = Assert.IsType<AmsAgentException>(AmsMcsOperationErrorMap.Map(409, WireFixture.Read("mcs-agent-register.repository-created.error.json"), "POST", Url, Correlation));

        Assert.Equal(ErrorCodes.AmsAgent.BotClaimed, mapped.ErrorCode);
        Assert.Equal("Clone that agent's repository and run `msagent agent init` there. AMS kept the repository it created for this agent, which the service detail names: pass it as --repository-id to use it.", mapped.Remediation);
        Assert.Contains("RepositoryId: cccccccc-3333-4333-8333-000000000003", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedAgentFolder_IsARequestRejection_ShowingTheServiceDetail()
    {
        var mapped = Assert.IsType<AmsAgentException>(AmsMcsOperationErrorMap.Map(400, AmsMcsOperationAdapterTests.Error("InvalidAgentFieldValue", "'SourceControl.AgentFolder' has an unsupported value."), "POST", Url, Correlation));

        Assert.Equal((ErrorCodes.AmsAgent.RequestRejected, ExitCodes.Usage, "ams-request-rejected"), (mapped.ErrorCode, mapped.ExitCode, mapped.ErrorKind));
        Assert.StartsWith("The Agent Management Service refused a value msagent sent. Service detail: 'SourceControl.AgentFolder' has an unsupported value.", mapped.Message, StringComparison.Ordinal);
        Assert.Equal("Correct the value the service detail names, such as the agent folder, then run the command again.", mapped.Remediation);
    }

    [Theory]
    [InlineData("mcs-deploy.base-unknown.error.json", "Run a local deploy, which records the commit Dev holds")]
    [InlineData("mcs-capture.base-unknown.error.json", "so it cannot capture Dev's edits")]
    public void AnUnknownCloudBase_ShowsTheServiceText_AndSendsTheUserToALocalDeploy(string fixture, string shown)
    {
        var mapped = Assert.IsType<AmsAgentException>(AmsMcsOperationErrorMap.Map(409, WireFixture.Read(fixture), "POST", Url, Correlation));

        Assert.Equal((ErrorCodes.AmsAgent.CaptureBaseUnknown, ExitCodes.Failure), (mapped.ErrorCode, mapped.ExitCode));
        Assert.StartsWith("AMS doesn't know which commit Dev holds. Service detail: ", mapped.Message, StringComparison.Ordinal);
        Assert.Contains(shown, mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATokenThatExpiresTooSoon_AsksForANewSignIn()
    {
        var mapped = Assert.IsType<AmsAgentException>(AmsMcsOperationErrorMap.Map(403, WireFixture.Read("mcs-agent-deploy.caller-token-expires-too-soon.error.json"), "POST", Url, Correlation));

        Assert.Equal(ErrorCodes.AmsAgent.OnBehalfOfUnavailable, mapped.ErrorCode);
        Assert.Equal("Your sign-in token could not be renewed with enough lifetime for the operation. Run `msagent auth login` and retry.", mapped.Remediation);
        Assert.True(AmsMcsOperationErrorMap.RefusesTokenLifetime(WireFixture.Read("mcs-agent-deploy.caller-token-expires-too-soon.error.json")));
    }

    [Theory]
    [InlineData("""{ "error": { "code": "McsSourceConflict", "message": "ContosoHR changed", "details": "ContosoHR" } }""")]
    [InlineData("""{ "error": { "code": "McsSourceConflict", "message": "ContosoHR changed", "details": ["ContosoHR"] } }""")]
    [InlineData("""{ "error": { "code": "McsSourceConflict", "message": "ContosoHR changed", "details": [{ "code": 7 }] } }""")]
    public void MalformedDetails_StillMapTheCodeWithItsMessage(string body)
    {
        var mapped = AmsMcsOperationErrorMap.Map(409, body, "POST", Url, Correlation);

        Assert.Equal(ErrorCodes.AmsAgent.SourceConflict, mapped.ErrorCode);
        Assert.Contains("Service detail: ContosoHR changed (", mapped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedOperation_MapsItsRecordedCodeAndNamesTheOperation()
    {
        var mapped = AmsMcsOperationErrorMap.OperationFailed(Operation("mcs-operation.failed.json"));

        Assert.Equal(ErrorCodes.AmsAgent.OperationFailed, mapped.ErrorCode);
        Assert.Equal("The operation failed in Copilot Studio. Service detail: Publishing the agent failed: the topic 'Greeting' references a missing variable.; ConnectionReferenceUnusable: cr1a2_sharedoffice365 (operation 12345678-7777-4777-8777-000000000007).", mapped.Message);
    }

    [Theory]
    [InlineData("SomethingNew", "The Deploy operation failed with SomethingNew.")]
    [InlineData(null, "The operation failed in Copilot Studio.")]
    public void FailedOperation_WithAnUnknownOrMissingCode_IsAnOperationFailure(string? code, string message)
    {
        var mapped = AmsMcsOperationErrorMap.OperationFailed(Operation("mcs-operation.failed.json") with { Error = new AmsMcsOperationError { Code = code, Message = "Publish failed." } });

        Assert.Equal(ErrorCodes.AmsAgent.OperationFailed, mapped.ErrorCode);
        Assert.Equal($"{message} Service detail: Publish failed. (operation 12345678-7777-4777-8777-000000000007).", mapped.Message);
    }

    [Fact]
    public void ACaptureConflict_NamesTheCommitTheMergeRanAgainstAndEachConflictingPath_AndOffersTheWaysOutOnePerLine()
    {
        var mapped = AmsMcsOperationErrorMap.OperationFailed(Operation("mcs-operation.capture-conflict.json"));

        Assert.Equal((ErrorCodes.AmsAgent.CaptureConflict, ExitCodes.Usage, "mcs-capture-conflict", true), (mapped.ErrorCode, mapped.ExitCode, mapped.ErrorKind, mapped.HasMultilineGuidance));
        Assert.Equal($"Nothing was captured: Dev's edits and commit {Requested} changed the same lines in ContosoHR/behaviors/skill-1/SKILL.md, ContosoHR/topics/Greeting.mcs.yml. (operation 12345678-7777-4777-8777-000000000017).", mapped.Message);
        Assert.Equal(["Capture again with `--resolve cloud`; the other changes of both sides merge as usual.", "Deploy the latest commit to Dev with `msagent agent deploy --remote --deployment-name <dev-deployment> --overwrite`, which first saves Dev's edits on its snapshot branch."], mapped.Remediation!.Split(Environment.NewLine));
    }

    [Theory]
    [InlineData(Requested, Requested)]
    [InlineData(null, "9fceb02d0ae598e95dc970b74767f19372d61af8")]
    public void AConflictInADevDeploy_SaysNothingWasDeployedOrCaptured_AndOffersToDeployTheRequestedCommit(string? requestedCommitSha, string named)
    {
        var mapped = AmsMcsOperationErrorMap.OperationFailed(Operation("mcs-operation.capture-conflict.json") with { Kind = "Deploy" }, requestedCommitSha);

        Assert.StartsWith($"Nothing was deployed or captured: Dev's edits and commit {Requested} changed the same lines in ", mapped.Message, StringComparison.Ordinal);
        Assert.Equal(["Capture with `--resolve cloud`, then deploy again; the other changes of both sides merge as usual.", $"Deploy commit {named} with --overwrite, which first saves Dev's edits on its snapshot branch."], mapped.Remediation!.Split(Environment.NewLine));
    }

    [Theory]
    [InlineData("Capture", "MCSAgent", "Nothing was captured: new commits kept arriving while the capture merged Dev's edits.", "Capture again; it merges with the newest commit.")]
    [InlineData("Capture", null, "Nothing was captured: new commits kept arriving while the capture merged Dev's edits.", "Capture again; it merges with the newest commit.")]
    [InlineData("Capture", "MCSWorkflow", "Nothing was captured: the workflow changed in a commit after the one Dev runs, and a workflow capture doesn't merge.", "Deploy the latest commit to Dev, which replaces Dev's edits, or make the same edits in your clone, commit and push them.")]
    [InlineData("Deploy", "MCSAgent", "Nothing was deployed: new commits kept arriving while the deploy captured Dev's edits.", "Deploy again; the capture merges with the newest commit.")]
    [InlineData("Deploy", "MCSWorkflow", "Nothing was deployed: the workflow changed in a commit after the one Dev runs, and a workflow capture doesn't merge, so Dev's edits weren't saved.", "To keep Dev's edits, make them in your clone, commit and push them. Then run the command again with --overwrite, which replaces Dev's edits with the commit.")]
    public void ASourceConflictOfACaptureOrADeploy_IsWordedForTheOperationAndTheAgentsType_WithoutTheServiceDetail(string kind, string? agentType, string message, string remediation)
    {
        var operation = Operation("mcs-operation.failed.json") with { Kind = kind, Error = new AmsMcsOperationError { Code = "McsSourceConflict", Message = "ContosoHR changed." } };

        var mapped = AmsMcsOperationErrorMap.OperationFailed(operation, agentType: agentType);

        Assert.Equal((ErrorCodes.AmsAgent.SourceConflict, ExitCodes.Usage), (mapped.ErrorCode, mapped.ExitCode));
        Assert.Equal(($"{message} (operation 12345678-7777-4777-8777-000000000007).", remediation), (mapped.Message, mapped.Remediation));
        Assert.Equal(remediation, AmsMcsOperationErrorMap.OperationRemediation(operation, agentType));
    }

    [Fact]
    public void DevEditsNotInTheCommit_NameTheRequestedCommitAndWhereTheyWereSaved_WithoutTheServiceDetail()
    {
        var mapped = AmsMcsOperationErrorMap.OperationFailed(Operation("mcs-operation.deploy-dev-edits-not-in-commit.json"));

        Assert.Equal((ErrorCodes.AmsAgent.DevEditsNotInCommit, ExitCodes.Usage, "mcs-dev-edits-not-in-commit"), (mapped.ErrorCode, mapped.ExitCode, mapped.ErrorKind));
        Assert.Equal("Nothing was deployed: Dev had edits that commit 9fceb02d0ae598e95dc970b74767f19372d61af8 doesn't hold. They were saved on main as commit 1f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c. (operation 12345678-7777-4777-8777-000000000007).", mapped.Message);
        Assert.Equal("Run `git pull`, then deploy the saved commit or a later one, or run the command again with --overwrite.", mapped.Remediation);
    }

    private static AmsMcsOperationRecord Operation(string fixture) => AmsMcsWire.Parse(WireFixture.Read(fixture), AmsMcsWire.ReadOperation);

    private static string CodeWithDetail(string code, string detailCode, string? detailMessage) => JsonSerializer.Serialize(new { error = new { code, message = "x", details = new[] { new { code = detailCode, message = detailMessage } } } });
}
