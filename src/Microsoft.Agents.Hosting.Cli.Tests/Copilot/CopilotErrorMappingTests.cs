using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class CopilotErrorMappingTests
{
    public static TheoryData<CopilotStudioException, int, string> Failures() => new()
    {
        { CopilotStudioException.EnvironmentNotSelected(), ExitCodes.Usage, CopilotErrorKinds.EnvironmentNotSelected },
        { CopilotStudioException.EnvironmentMissingInstanceUrl("env-1"), ExitCodes.Usage, CopilotErrorKinds.EnvironmentMissingInstanceUrl },
        { CopilotStudioException.WorkspaceNotFound("C:/agents"), ExitCodes.Usage, CopilotErrorKinds.WorkspaceNotFound },
        { CopilotStudioException.WorkspaceNotEmpty("C:/agents"), ExitCodes.Usage, CopilotErrorKinds.WorkspaceNotEmpty },
        { CopilotStudioException.AgentNotFound("contoso_helpdesk"), ExitCodes.Usage, CopilotErrorKinds.AgentNotFound },
        { CopilotStudioException.AgentAlreadyExists("contoso_helpdesk"), ExitCodes.Usage, CopilotErrorKinds.AgentAlreadyExists },
        { CopilotStudioException.PushConflictsRequirePull(["Greeting"]), ExitCodes.Usage, CopilotErrorKinds.PushConflictsRequirePull },
        { CopilotStudioException.ConfirmationRequired("delete"), ExitCodes.Usage, CopilotErrorKinds.ConfirmationRequired },
        { CopilotStudioException.TemplateNotFound("default"), ExitCodes.Usage, CopilotErrorKinds.TemplateNotFound },
        { CopilotStudioException.StatusTimeout("agent-1", TimeSpan.FromMinutes(60)), ExitCodes.Failure, CopilotErrorKinds.StatusTimeout },
        { CopilotStudioException.ServiceError("clone"), ExitCodes.Failure, CopilotErrorKinds.ServiceError },
        { CopilotStudioException.MissingConnectionReference("env-1", [], new InvalidOperationException("boom")), ExitCodes.Failure, CopilotErrorKinds.MissingConnectionReference },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void Failure_CarriesTheDocumentedExitCodeKindAndRemediation(CopilotStudioException failure, int expectedExitCode, string expectedErrorKind)
    {
        Assert.Equal(expectedExitCode, failure.ExitCode);
        Assert.Equal(expectedErrorKind, failure.ErrorKind);
        Assert.False(string.IsNullOrWhiteSpace(failure.Remediation));
        Assert.False(string.IsNullOrWhiteSpace(failure.Message));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public void Failure_CarriesNoSecretMaterial(CopilotStudioException failure, int expectedExitCode, string expectedErrorKind)
    {
        _ = expectedExitCode;
        _ = expectedErrorKind;
        var text = $"{failure.Message} {failure.Remediation}";

        foreach (var forbidden in new[] { "token", "bearer", "secret", "password", "authorization", "sig=", "eyJ" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotMatch(new Regex("[A-Za-z0-9_-]{40,}"), text);
    }

    [Fact]
    public void PushConflict_NamesTheConflictingComponents()
    {
        var failure = CopilotStudioException.PushConflictsRequirePull(["Greeting", "Escalate"]);

        Assert.Contains("Greeting", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Escalate", failure.Message, StringComparison.Ordinal);
        Assert.Contains("msagent agent pull", failure.Remediation!, StringComparison.Ordinal);
        Assert.Contains("--overwrite", failure.Remediation!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "1 remote change ")]
    [InlineData(2, "2 remote changes ")]
    public void PushConflict_PluralisesTheRemoteChangeCount(int count, string expected)
    {
        var failure = CopilotStudioException.PushConflictsRequirePull([.. Enumerable.Range(0, count).Select(index => $"Topic{index}")]);

        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void KnowledgeUploadFailure_SaysComponentsWerePushedAndRetryIsSafe()
    {
        var failure = CopilotStudioException.KnowledgeUploadFailed(4, new InvalidOperationException("Improper response, not implemented"));

        Assert.Equal(CopilotErrorKinds.KnowledgeUploadFailed, failure.ErrorKind);
        Assert.Contains("4 component(s) were pushed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Improper response, not implemented", failure.Message, StringComparison.Ordinal);
        Assert.Contains("knowledge files are empty", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public void KnowledgeUploadFailure_UnwrapsTheInnerChain()
    {
        var failure = CopilotStudioException.KnowledgeUploadFailed(1, new InvalidOperationException("outer", new HttpRequestException("inner")));

        Assert.Contains("outer", failure.Message, StringComparison.Ordinal);
        Assert.Contains("HttpRequestException: inner", failure.Message, StringComparison.Ordinal);
    }

    public static TheoryData<CopilotStudioException> PushFailuresCarryingServiceDetail()
    {
        var serviceFailure = new InvalidOperationException(
            "ExportKeyInvalidCreate at https://contoso.crm.dynamics.com/api/data/v9.2/connectionreferences",
            new HttpRequestException("ServiceRequestId: f91155b4-0a6d-4094-a6eb-eef92a8291c4"));
        string[] refusals = ["Workflow 'Escalate' was not activated: the related connection references need connections created"];

        return new TheoryData<CopilotStudioException>
        {
            CopilotStudioException.PushFailedAfterRefusedComponents("env-1", refusals, serviceFailure),
            CopilotStudioException.DuplicateConnectionReference("env-1", refusals, serviceFailure),
            CopilotStudioException.DuplicateConnectionReference("env-1", [], serviceFailure),
            CopilotStudioException.MissingConnectionReference("env-1", refusals, serviceFailure),
            CopilotStudioException.MissingConnectionReference("env-1", [], serviceFailure),
        };
    }

    [Theory]
    [MemberData(nameof(PushFailuresCarryingServiceDetail))]
    public void APushFailure_KeepsServiceDetailOutOfTheEnvelope(CopilotStudioException failure)
    {
        var text = $"{failure.Message} {failure.Remediation}";

        Assert.DoesNotContain("ExportKeyInvalidCreate", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceRequestId", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("f91155b4", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dynamics.com", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpRequestException", text, StringComparison.Ordinal);
        Assert.DoesNotContain("was not activated", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(PushFailuresCarryingServiceDetail))]
    public void APushFailure_KeepsTheServiceDetailOnTheInnerExceptionForDiagnostics(CopilotStudioException failure)
    {
        Assert.NotNull(failure.InnerException);
        Assert.Contains("ExportKeyInvalidCreate", failure.InnerException!.Message, StringComparison.Ordinal);
        Assert.Contains("env-1", failure.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(failure.Remediation));
    }

    [Theory]
    [MemberData(nameof(PushFailuresCarryingServiceDetail))]
    public void APushFailure_IsNotRenderedWithItsInnerChain(CopilotStudioException failure)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        new CliOutputRenderer(stdout, stderr, color: false).RenderFailure(failure, json: true);

        Assert.DoesNotContain("ExportKeyInvalidCreate", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ServiceRequestId", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, "1 component")]
    [InlineData(3, "3 components")]
    public void APushFailureAfterRefusals_ReportsHowManyWereRefused(int count, string expected)
    {
        var failure = CopilotStudioException.PushFailedAfterRefusedComponents(
            "env-1",
            [.. Enumerable.Range(0, count).Select(index => $"Flow{index} was not activated: nope")],
            new InvalidOperationException("boom"));

        Assert.Equal(CopilotErrorKinds.PushFailedAfterRefusedComponents, failure.ErrorKind);
        Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateConnectionReference_MentionsEarlierRefusalsOnlyWhenThereWereSome()
    {
        var withRefusals = CopilotStudioException.DuplicateConnectionReference("env-1", ["Flow0 was refused"], new InvalidOperationException("boom"));
        var withoutRefusals = CopilotStudioException.DuplicateConnectionReference("env-1", [], new InvalidOperationException("boom"));

        Assert.Equal(CopilotErrorKinds.DuplicateConnectionReference, withoutRefusals.ErrorKind);
        Assert.Contains("1 component", withRefusals.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("had also refused", withoutRefusals.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingConnectionReference_MentionsEarlierRefusalsOnlyWhenThereWereSome()
    {
        var withRefusals = CopilotStudioException.MissingConnectionReference("env-1", ["Flow0 was refused"], new InvalidOperationException("boom"));
        var withoutRefusals = CopilotStudioException.MissingConnectionReference("env-1", [], new InvalidOperationException("boom"));

        Assert.Equal(CopilotErrorKinds.MissingConnectionReference, withoutRefusals.ErrorKind);
        Assert.Contains("1 component", withRefusals.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("had also refused", withoutRefusals.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingConnectionReference_KeepsTheDataverseDetailOnTheInnerException()
    {
        var failure = CopilotStudioException.MissingConnectionReference("env-1", [], CopilotTestFixtures.MissingEntity());
        var text = $"{failure.Message} {failure.Remediation}";

        Assert.DoesNotContain("0x80040217", text, StringComparison.Ordinal);
        Assert.DoesNotContain("service-request-1", text, StringComparison.Ordinal);
        Assert.Contains("0x80040217", failure.InnerException!.ToString(), StringComparison.Ordinal);
        Assert.Contains("service-request-1", failure.InnerException!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateConnectionReference_KeepsItsExactMessage()
    {
        Assert.Equal(
            "A connection reference this push declares already exists in env-1, so the component push did not complete and the agent definition was left as it is.",
            CopilotStudioException.DuplicateConnectionReference("env-1", [], new InvalidOperationException("boom")).Message);

        Assert.Equal(
            "A connection reference this push declares already exists in env-1, so the component push did not complete and the agent definition was left as it is. The service had also refused 1 component earlier in this push.",
            CopilotStudioException.DuplicateConnectionReference("env-1", ["Flow0 was refused"], new InvalidOperationException("boom")).Message);
    }

    [Fact]
    public void AMissingConnectionReference_HasItsExactMessage()
    {
        Assert.Equal(
            "A connection reference this push names does not exist in env-1, even after the baseline was re-read from the environment, so the component push did not complete and the agent definition was left as it is.",
            CopilotStudioException.MissingConnectionReference("env-1", [], new InvalidOperationException("boom")).Message);

        Assert.Equal(
            "A connection reference this push names does not exist in env-1, even after the baseline was re-read from the environment, so the component push did not complete and the agent definition was left as it is. The service had also refused 1 component earlier in this push.",
            CopilotStudioException.MissingConnectionReference("env-1", ["Flow0 was refused"], new InvalidOperationException("boom")).Message);
    }

    [Fact]
    public void UnexpectedFailures_CarryTheirTypeAndInnerChain()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var renderer = new CliOutputRenderer(stdout, stderr, color: false);

        var exitCode = renderer.RenderFailure(new InvalidOperationException("Improper response, not implemented", new HttpRequestException("501")), json: false);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Contains("InvalidOperationException: Improper response, not implemented", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("HttpRequestException: 501", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TypedFailures_KeepTheirMessageVerbatim()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var renderer = new CliOutputRenderer(stdout, stderr, color: false);

        renderer.RenderFailure(CopilotStudioException.AgentNotFound("con_agent"), json: false);

        Assert.DoesNotContain("CopilotStudioException", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectedAgentNotInTarget_ExplainsWhyAnIdDoesNotTravel()
    {
        var failure = CopilotStudioException.ConnectedAgentNotInTarget("env-test", new InvalidOperationException("Unsupported sync operation. A component references a parent agent that could not be created on the cloud."));

        Assert.Equal(CopilotErrorKinds.ConnectedAgentNotInTarget, failure.ErrorKind);
        Assert.Contains("env-test", failure.Message, StringComparison.Ordinal);
        Assert.Contains("referenced by id", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Deploy the referenced agent to this environment first", failure.Remediation!, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorKinds_AreKebabCase()
    {
        var kinds = typeof(CopilotErrorKinds)
            .GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

        foreach (var kind in kinds)
        {
            Assert.Matches(new Regex("^[a-z]+(-[a-z]+)*$"), kind);
        }
    }
}
