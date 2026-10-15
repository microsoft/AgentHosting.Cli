using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

/// <summary>
/// The harness captures whatever the CLI wrote, and sharing makes the CLI write what the directory
/// holds against an object id. These pin that a name or a sign-in address cannot reach a transcript
/// or a report, and that the object id naming which principal a failure concerned still can.
/// </summary>
public sealed class RedactionTests
{
    private const string ObjectId = "6d531c04-39e5-4c4c-9510-37b579c80b48";

    [Fact]
    public void ThePrincipalNameAServiceReturnedIsMasked()
    {
        var redacted = Redaction.Apply("""{"principalName":"Sample Person","objectId":"6d531c04-39e5-4c4c-9510-37b579c80b48"}""");

        Assert.DoesNotContain("Sample Person", redacted, StringComparison.Ordinal);
        Assert.Contains(ObjectId, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSignInAddressAServiceReturnedIsMasked()
    {
        var redacted = Redaction.Apply("""{"signInAddress":"sample.person@contoso.onmicrosoft.com"}""");

        Assert.DoesNotContain("sample.person", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("contoso.onmicrosoft.com", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sample.person@contoso.com")]
    [InlineData("sample_contoso.com#EXT#@fabrikam.onmicrosoft.com")]
    [InlineData("  PRINCIPAL        TYPE  ROLE   SIGN-IN ADDRESS\n  Sample  User  use  sample@contoso.com")]
    public void ASignInAddressIsMaskedWhereverItAppears(string value)
    {
        Assert.DoesNotContain("@contoso.com", Redaction.Apply(value), StringComparison.Ordinal);
        Assert.DoesNotContain("@fabrikam.onmicrosoft.com", Redaction.Apply(value), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePrincipalLineOfTheHumanOutputIsMasked()
    {
        var redacted = Redaction.Apply($"  principal    Sample Person (User {ObjectId})");

        Assert.DoesNotContain("Sample Person", redacted, StringComparison.Ordinal);
        Assert.Contains(ObjectId, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOwnerRefusalIsMasked()
    {
        var redacted = Redaction.Apply("Sample Person owns this agent, and an owner's access cannot be granted or revoked by sharing.");

        Assert.DoesNotContain("Sample Person", redacted, StringComparison.Ordinal);
        Assert.Contains("owns this agent", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AnObjectIdSurvivesOnItsOwnLine() =>
        Assert.Contains(ObjectId, Redaction.Apply($"  principal    {ObjectId}  (name unavailable in this environment)"), StringComparison.Ordinal);

    [Fact]
    public void ABearerTokenIsStillMasked() =>
        Assert.DoesNotContain("abc.def.ghi", Redaction.Apply("Authorization: Bearer abc.def.ghi"), StringComparison.Ordinal);

    [Fact]
    public void AClientSecretIsStillMasked() =>
        Assert.DoesNotContain("s3cr3t", Redaction.Apply("""{"client_secret":"s3cr3t"}"""), StringComparison.Ordinal);

    [Fact]
    public void TheAgentsOwnNameIsNotMasked() =>
        Assert.Contains("Contoso Agent", Redaction.Apply("""{"displayName":"Contoso Agent"}"""), StringComparison.Ordinal);

    [Fact]
    public void ATranscriptRecordsWhatRanWithoutNamingWho()
    {
        var directory = Path.Combine(Path.GetTempPath(), "msagent-transcript-" + Guid.NewGuid().ToString("N"));

        try
        {
            var transcript = new Transcript(directory);

            transcript.Record("msagent.exe", new MsagentResult(
                ["agent", "share", "--user", "sample.person@contoso.com"],
                0,
                "{\"principalName\":\"Sample Person\",\"objectId\":\"" + ObjectId + "\"}",
                string.Empty,
                TimeSpan.Zero));

            var written = File.ReadAllText(Path.Combine(directory, "transcript.log"));

            Assert.DoesNotContain("Sample Person", written, StringComparison.Ordinal);
            Assert.DoesNotContain("sample.person@contoso.com", written, StringComparison.Ordinal);
            Assert.Contains(ObjectId, written, StringComparison.Ordinal);
            Assert.Contains("agent share", written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void EverySentenceTheCliWritesAPrincipalsNameIntoIsMasked()
    {
        var output = string.Join(
            Environment.NewLine,
            "Shared Contoso Agent with Sample Person as 'use'.",
            "Sample Person already had 'use' on Contoso Agent. Nothing changed.",
            "Removed the direct share of Contoso Agent (agent-1) with Sample Person.",
            "Sample Person held no direct share of Contoso Agent (agent-1). Nothing changed.",
            "  Send this link to Sample Person:",
            "  Everyone in the organization can still use this agent, so this did not end Sample Person's access.",
            "Sample Person owns this agent, and an owner's access cannot be granted or revoked by sharing.",
            "'x' matches more than one account in this environment: Sample Person, Other Person.");

        var redacted = Redaction.Apply(output);

        Assert.DoesNotContain("Sample Person", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Other Person", redacted, StringComparison.Ordinal);
        Assert.Contains("Contoso Agent", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyValueIsLeftAlone() => Assert.Equal(string.Empty, Redaction.Apply(string.Empty));
}

/// <summary>
/// A report outlives the run it describes, and a failing sharing scenario carries whatever the CLI
/// wrote into its failure. These pin that the persisted artifacts are masked and that the object id
/// naming which principal was involved survives.
/// </summary>
public sealed class ReportRedactionTests : IDisposable
{
    private const string ObjectId = "6d531c04-39e5-4c4c-9510-37b579c80b48";

    private readonly string _runDirectory = Path.Combine(Path.GetTempPath(), "msagent-report-" + Guid.NewGuid().ToString("N"));

    public ReportRedactionTests() => Directory.CreateDirectory(_runDirectory);

    public void Dispose()
    {
        if (Directory.Exists(_runDirectory))
        {
            Directory.Delete(_runDirectory, recursive: true);
        }
    }

    [Fact]
    public void APersistedFailureNamesNoPersonButStillNamesThePrincipal()
    {
        var failure = new ScenarioResult
        {
            Scenario = new ScenarioDescriptor("MA-75", "agent share grants use access", "mcs-agent", SafetyTier.Reversible),
            Outcome = Outcome.Fail,
            Reason = "Shared Contoso Agent with Sample Person as 'use'.",
            Expected = "{\"principalName\":\"Sample Person\",\"objectId\":\"" + ObjectId + "\"}",
            Actual = "{\"signInAddress\":\"sample.person@contoso.com\"}",
            CommandLine = "msagent agent share --user sample.person@contoso.com",
            PassedChecks = ["Sample Person owns this agent, and an owner's access cannot be granted"],
        };

        Reporter.Persist(
            _runDirectory,
            RunReport.From("run", DateTimeOffset.UtcNow, TimeSpan.Zero, new MsagentBinary("msagent", "test", "1"), [failure], []),
            []);

        foreach (var name in new[] { "report.json", "report.md" })
        {
            var written = File.ReadAllText(Path.Combine(_runDirectory, name));

            Assert.DoesNotContain("Sample Person", written, StringComparison.Ordinal);
            Assert.DoesNotContain("sample.person@contoso.com", written, StringComparison.Ordinal);
            Assert.Contains(ObjectId, written, StringComparison.Ordinal);
            Assert.Contains("MA-75", written, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// A rejected configured value is never echoed, because the setting most likely to be mistyped is
/// the principal to share with and the likeliest mistake there is a sign-in address.
/// </summary>
public sealed class ConfigValueRedactionTests
{
    [Fact]
    public void AMalformedGuidIsRejectedWithoutEchoingIt()
    {
        var value = ConfigValues.Guid("sample.person@contoso.com", "mcs.shareUserObjectId", _ => "user");

        Assert.False(value.Supplied);
        Assert.DoesNotContain("sample.person", value.Detail, StringComparison.Ordinal);
        Assert.Contains("mcs.shareUserObjectId", value.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AUsableGuidIsStillDescribed()
    {
        var value = ConfigValues.Guid("6d531c04-39e5-4c4c-9510-37b579c80b48", "mcs.shareUserObjectId", supplied => $"user {supplied}");

        Assert.True(value.Supplied);
        Assert.Equal("6d531c04-39e5-4c4c-9510-37b579c80b48", value.Value);
    }
}
