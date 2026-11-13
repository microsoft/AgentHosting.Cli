using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class NumericErrorCodeTests
{
    [Fact]
    public void OwnedCleanupOnlyHasItsOwnCodeAndPreservesPrimaryFailures()
    {
        Assert.Equal(12020, RenderCode(new Auth.ProfileCredentialCleanupException(false)));
        var primary = new CacheProtectionUnavailableException("Protected storage is locked.");
        var wrapper = new Auth.ProfileCredentialCleanupException(true, primary);
        Assert.Equal(12006, RenderCode(wrapper));
        Assert.Equal(primary.ExitCode, wrapper.ExitCode);
        Assert.Equal(primary.ErrorCode, RenderCode(new AuthOperationException(true, true, null, primary)));
    }
    [Fact]
    public void CleanupKeepsPrimaryKindAndExceptionWithoutRenderingItsBody()
    {
        var primary = new TokenClaimException("FAKE_PRIMARY_CANARY", TokenClaimFailure.WrongTenant);
        var cleanup = new Auth.ProfileCredentialCleanupException(true, primary);
        Assert.Equal(primary.ErrorKind, cleanup.ErrorKind);
        Assert.Same(primary, cleanup.InnerException);
        Assert.DoesNotContain(primary.Message, cleanup.Message);
    }

    public static TheoryData<Exception, int, int> GeneralFailures() => new()
    {
        { new UnclassifiedException(), 99001, ExitCodes.Failure },
        { new InvalidOperationException("Unexpected failure."), 99001, ExitCodes.Failure },
        { new UsageException("Missing option."), 99002, ExitCodes.Usage },
        { new PendingImplementationException("operation"), 99003, ExitCodes.Failure },
        { new TransportTimeoutException(new TimeoutException()), 99004, ExitCodes.Failure },
    };

    [Theory]
    [MemberData(nameof(GeneralFailures))]
    public void JsonFailure_UsesGeneralCodesWithoutChangingExitCodes(Exception failure, int expectedCode, int expectedExit)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = new CliOutputRenderer(stdout, stderr, color: false).RenderFailure(failure, json: true);

        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("errorCode").GetInt32());
        Assert.Equal(expectedExit, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal(expectedExit, exitCode);
        Assert.Empty(stderr.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthenticationContextFailure_RendersInternalFailureAndActionableGuidance(bool json)
    {
        var failure = Assert.ThrowsAny<AgentHostingException>(() => ResourceTokenContext.Require(null));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = new CliOutputRenderer(stdout, stderr, color: false).RenderFailure(failure, json);

        Assert.Equal(ExitCodes.Failure, exitCode);
        if (json)
        {
            using var document = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(99005, document.RootElement.GetProperty("errorCode").GetInt32());
            Assert.Equal("authentication-context-invalid", document.RootElement.GetProperty("errorKind").GetString());
            Assert.Equal(failure.Remediation, document.RootElement.GetProperty("remediation").GetString());
            Assert.Empty(stderr.ToString());
        }
        else
        {
            Assert.Empty(stdout.ToString());
            Assert.Contains("authentication context", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("CLI version", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("token provider", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains("Do not include tokens or credentials", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("auth clear", stderr.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("auth create", stderr.ToString(), StringComparison.Ordinal);
        }
    }

    public static TheoryData<AgentHostingException, int> DomainFailures() => new()
    {
        { ManagedAgentException.ConfigNotFound("agent.config.json"), 10 },
        { ManagedAgentException.AgentNotFound("agent"), 11 },
        { new NotSignedInException("Sign in."), 12 },
        { new EnvironmentNotFoundException("Environment missing."), 13 },
        { DeploymentException.NotFound("deployment"), 14 },
        { DeploymentException.ChannelConfigurationUnavailable(), 14 },
        { new AgentPackageException(AgentPackageRejection.SourceLink, "Linked source."), 15 },
        { new ProvisioningException(ProvisioningErrorKind.NamingConflict, "Conflict.", "Rename."), 16 },
        { CopilotStudioException.ServiceError("pull"), 17 },
        { EvaluationException.RunNotFound("run"), 18 },
    };

    [Theory]
    [MemberData(nameof(DomainFailures))]
    public void JsonFailure_IdentifiesTheMajorArea(AgentHostingException failure, int expectedGroup)
    {
        var code = RenderCode(failure);

        Assert.Equal(expectedGroup, code / 1000);
        Assert.InRange(code % 1000, 1, 999);
    }

    [Fact]
    public void ParserFailure_UsesTheGenericUsageCode()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exitCode = new CliOutputRenderer(stdout, stderr, color: false).RenderUsageError("Unknown command.", json: true);

        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(99002, document.RootElement.GetProperty("errorCode").GetInt32());
        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.False(document.RootElement.TryGetProperty("errorKind", out _));
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void RecoveryGuidanceWrapper_PreservesTheOriginalCode()
    {
        var original = ManagedAgentException.ConfigNotFound("agent.config.json");
        var wrapped = AgentCreationException.PreserveGeneratedIdempotencyKey(original, "recovery-key");

        Assert.Equal(10001, RenderCode(wrapped));
        Assert.Equal(RenderCode(original), RenderCode(wrapped));
        Assert.Equal(original.ExitCode, wrapped.ExitCode);
        Assert.Same(original, wrapped.InnerException);
    }

    [Fact]
    public void ProgressWrappers_PreserveTheOriginalCodeAndPartialReport()
    {
        var original = new InteractionRequiredException("Consent required.");
        var appReport = new AppRegistrationIdentityReport
        {
            Status = "partial",
            TenantId = "tenant",
            DisplayName = "app",
            Changes = [],
            NextActions = [],
        };
        var blueprintReport = new BlueprintIdentityReport
        {
            Status = "partial",
            TenantId = "tenant",
            DisplayName = "blueprint",
            Changes = [],
            NextActions = [],
        };
        AgentHostingException[] wrapped =
        [
            new AppRegistrationIdentityProvisioningException(original, appReport),
            new BlueprintIdentityProvisioningException(original, blueprintReport),
        ];

        foreach (var failure in wrapped)
        {
            var stdout = new StringWriter();
            var exitCode = new CliOutputRenderer(stdout, new StringWriter(), color: false).RenderFailure(failure, json: true);

            using var document = JsonDocument.Parse(stdout.ToString());
            Assert.Equal(original.ErrorCode, document.RootElement.GetProperty("errorCode").GetInt32());
            Assert.Equal("partial", document.RootElement.GetProperty("partialResult").GetProperty("status").GetString());
            Assert.Equal(ExitCodes.NotSignedIn, exitCode);
            Assert.Same(original, failure.InnerException);
        }
    }

    [Fact]
    public void DistinctConditions_SharingAnErrorKindHaveDifferentCodes()
    {
        var lookup = ManagedAgentException.ConnectionReferenceNotDeclared("reference", "workspace", [], "list");
        var declaration = ManagedAgentException.ConnectionReferenceNotDeclared("connector", "environment", new InvalidOperationException(), "list");

        Assert.Equal(lookup.ErrorKind, declaration.ErrorKind);
        Assert.NotEqual(RenderCode(lookup), RenderCode(declaration));
    }

    [Fact]
    public void NamedUsageFactories_HaveDistinctDeploymentCodes()
    {
        var missing = DeploymentException.RequiredParameter("parameter", "channel");
        var invalid = DeploymentException.InvalidParameterValue("parameter", "channel");

        Assert.Equal(14, RenderCode(missing) / 1000);
        Assert.Equal(14, RenderCode(invalid) / 1000);
        Assert.NotEqual(RenderCode(missing), RenderCode(invalid));
        Assert.Null(missing.ErrorKind);
        Assert.Equal(ExitCodes.Usage, missing.ExitCode);
    }

    [Fact]
    public void UpstreamCategories_DoNotChangeTheOperationCode()
    {
        var first = AgentCreationException.ServiceRejected("first-category", "Rejected.", ExitCodes.Usage, "Retry.");
        var second = AgentCreationException.ServiceRejected("second-category", "Rejected.", ExitCodes.Failure, "Retry.");
        var deploy = AgentDeployException.ServiceRejected("first-category", ExitCodes.Usage, "first-category", "Retry.");

        Assert.Equal(RenderCode(first), RenderCode(second));
        Assert.NotEqual(RenderCode(first), RenderCode(deploy));
        Assert.Equal("first-category", first.ErrorKind);
        Assert.Equal("second-category", second.ErrorKind);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(9999)]
    [InlineData(100000)]
    [InlineData(int.MaxValue)]
    public void ExplicitCode_MustHaveFiveDigits(int errorCode)
    {
        var failure = Assert.Throws<ArgumentOutOfRangeException>(() => new ExplicitCodeException(errorCode));

        Assert.Equal("errorCode", failure.ParamName);
    }

    [Theory]
    [InlineData(10000)]
    [InlineData(45678)]
    [InlineData(99999)]
    public void ExplicitCode_IsRenderedWithoutChangingOtherMetadata(int errorCode)
    {
        var failure = new ExplicitCodeException(errorCode);
        var stdout = new StringWriter();

        var exitCode = new CliOutputRenderer(stdout, new StringWriter(), color: false).RenderFailure(failure, json: true);

        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(errorCode, document.RootElement.GetProperty("errorCode").GetInt32());
        Assert.Equal("custom-condition", document.RootElement.GetProperty("errorKind").GetString());
        Assert.Equal("Correct the input.", document.RootElement.GetProperty("remediation").GetString());
        Assert.Equal(ExitCodes.Usage, exitCode);
    }

    [Theory]
    [InlineData(typeof(ErrorCodes.Workspace), 10)]
    [InlineData(typeof(ErrorCodes.ManagedAgent), 11)]
    [InlineData(typeof(ErrorCodes.Authentication), 12)]
    [InlineData(typeof(ErrorCodes.Environment), 13)]
    [InlineData(typeof(ErrorCodes.Deployment), 14)]
    [InlineData(typeof(ErrorCodes.Package), 15)]
    [InlineData(typeof(ErrorCodes.Provisioning), 16)]
    [InlineData(typeof(ErrorCodes.CopilotStudio), 17)]
    [InlineData(typeof(ErrorCodes.Evaluation), 18)]
    [InlineData(typeof(ErrorCodes.General), 99)]
    public void Catalog_AssignsUniqueCodesWithinTheDocumentedArea(Type area, int prefix)
    {
        var codes = area.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => Assert.IsType<int>(field.GetRawConstantValue()))
            .ToArray();

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, code => Assert.InRange(code, prefix * 1000 + 1, prefix * 1000 + 999));
    }

    [Fact]
    public void ClassifiedConditions_DoNotCollapseToOneCodePerExceptionClass()
    {
        var categories = new[]
        {
            Enum.GetValues<ProvisioningErrorKind>()
                .Select(kind => new ProvisioningException(kind, "Failed.", "Retry.").ErrorCode),
            Enum.GetValues<TargetErrorKind>()
                .Select(kind => new TargetRequestException("Failed.", ExitCodes.Failure, kind, 403).ErrorCode),
            Enum.GetValues<AgentPackageRejection>()
                .Select(kind => new AgentPackageException(kind, "Failed.").ErrorCode),
            Enum.GetValues<TokenClaimFailure>()
                .Select(kind => new TokenClaimException("Failed.", kind).ErrorCode),
        };

        foreach (var codes in categories)
        {
            var values = codes.ToArray();
            Assert.Equal(values.Length, values.Distinct().Count());
            Assert.DoesNotContain(99001, values);
        }
    }

    [Fact]
    public void DeploymentUpdate_ReadAndMutationFailuresHaveDifferentCodes()
    {
        var read = AgentDeployException.DeploymentUpdateTimedOut("read", "request", false, new TimeoutException());
        var update = AgentDeployException.DeploymentUpdateTimedOut("update", "request", true, new TimeoutException());

        Assert.NotEqual(RenderCode(read), RenderCode(update));
        Assert.Equal(14, RenderCode(read) / 1000);
        Assert.Equal(14, RenderCode(update) / 1000);
    }

    [Fact]
    public void HumanFailure_DoesNotIncludeTheNumericCode()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        new CliOutputRenderer(stdout, stderr, color: false).RenderFailure(new ExplicitCodeException(45678), json: false);

        Assert.Empty(stdout.ToString());
        Assert.Equal($"Custom failure.{Environment.NewLine}Correct the input.{Environment.NewLine}", stderr.ToString());
    }

    private static int RenderCode(Exception failure)
    {
        var stdout = new StringWriter();
        new CliOutputRenderer(stdout, new StringWriter(), color: false).RenderFailure(failure, json: true);
        using var document = JsonDocument.Parse(stdout.ToString());
        return document.RootElement.GetProperty("errorCode").GetInt32();
    }

    private sealed class UnclassifiedException() : AgentHostingException("Unclassified failure.", ExitCodes.Failure);

    private sealed class ExplicitCodeException(int errorCode)
        : AgentHostingException("Custom failure.", ExitCodes.Usage, "custom-condition", "Correct the input.", errorCode: errorCode);
}
