using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentParameterValidatorTests
{
    [Fact]
    public void Parse_SplitsOnFirstEqualsAndTrimsBothSides()
    {
        var result = DeploymentParameterValidator.ParseParameterAssignments(
            "--parameters",
            [" RedirectUri = https://example.test/callback?state=one=two "],
            DeploymentParameterEmptyValuePolicy.Reject);

        Assert.Equal("https://example.test/callback?state=one=two", result["RedirectUri"]);
    }

    [Fact]
    public void Parse_RejectsMalformedEntryByPositionWithoutEchoingContent()
    {
        const string credential = "SYNTHETIC_PARAMETER_CREDENTIAL";

        var ex = Assert.Throws<UsageException>(() =>
            DeploymentParameterValidator.ParseParameterAssignments(
                "--parameters",
                ["Valid=one", credential],
                DeploymentParameterEmptyValuePolicy.Reject));

        Assert.Contains("position 2", ex.Message);
        Assert.Contains("Key=Value", ex.Message);
        Assert.DoesNotContain(credential, ex.Message);
    }

    [Fact]
    public void Parse_RejectsEmptyKeyWithoutEchoingValue()
    {
        const string credential = "SYNTHETIC_PARAMETER_CREDENTIAL";

        var ex = Assert.Throws<UsageException>(() =>
            DeploymentParameterValidator.ParseParameterAssignments(
                "--parameters",
                [$" = {credential}"],
                DeploymentParameterEmptyValuePolicy.Allow));

        Assert.Contains("position 1", ex.Message);
        Assert.Contains("Key is required", ex.Message);
        Assert.DoesNotContain(credential, ex.Message);
    }

    [Fact]
    public void Parse_RejectsEmptyValueWhenPolicyRejects()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentParameterValidator.ParseParameterAssignments(
                "--parameters",
                ["TenantId="],
                DeploymentParameterEmptyValuePolicy.Reject));

        Assert.Contains("position 1", ex.Message);
        Assert.Contains("Both Key and Value are required", ex.Message);
        Assert.DoesNotContain("TenantId", ex.Message);
    }

    [Fact]
    public void Parse_AllowsEmptyValueWhenPolicyAllows()
    {
        var result = DeploymentParameterValidator.ParseParameterAssignments(
            "--parameters",
            ["TenantId="],
            DeploymentParameterEmptyValuePolicy.Allow);

        Assert.Equal(string.Empty, result["TenantId"]);
    }

    [Fact]
    public void Parse_RejectsCaseInsensitiveDuplicateWithoutEchoingKey()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentParameterValidator.ParseParameterAssignments(
                "--parameters",
                ["TenantId=one", "tenantid=two"],
                DeploymentParameterEmptyValuePolicy.Reject));

        Assert.Contains("duplicate key at position 2", ex.Message);
        Assert.DoesNotContain("TenantId", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeParameters_ReturnsCatalogCasedValuesWithoutMutatingSupplied()
    {
        var definitions = new[]
        {
            new ChannelParameterDefinition(
                DeploymentParameterType.Boolean,
                "isEnabled",
                "Whether the channel is enabled."),
            new ChannelParameterDefinition(
                DeploymentParameterType.String,
                "landingPageUrl",
                "The landing page URL."),
        };
        var supplied = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ISENABLED"] = "True",
            ["landingpageurl"] = string.Empty,
        };

        var result = DeploymentParameterValidator.NormalizeParameters(
            definitions,
            supplied,
            definition => definition.Name,
            definition => definition.Type,
            (parameterName, type) => new UsageException($"{parameterName} must be {type}."));

        Assert.Equal(["isEnabled", "landingPageUrl"], result.Keys);
        Assert.Equal("true", result["isEnabled"]);
        Assert.Equal(string.Empty, result["landingPageUrl"]);
        Assert.Equal(["ISENABLED", "landingpageurl"], supplied.Keys);
    }

    [Fact]
    public void RejectOmittedOrBlankRequiredParameters_RejectsOmittedAndBlankValues()
    {
        var definitions = new[]
        {
            new ChannelParameterDefinition(
                DeploymentParameterType.String,
                "requiredValue",
                "Required.",
                IsRequired: true),
        };

        Assert.Throws<UsageException>(() =>
            DeploymentParameterValidator.RejectOmittedOrBlankRequiredParameters(
                definitions,
                new Dictionary<string, string?>(),
                definition => definition.Name,
                definition => definition.IsRequired,
                parameterName => new UsageException($"{parameterName} is required.")));
        Assert.Throws<UsageException>(() =>
            DeploymentParameterValidator.RejectOmittedOrBlankRequiredParameters(
                definitions,
                new Dictionary<string, string?> { ["requiredValue"] = string.Empty },
                definition => definition.Name,
                definition => definition.IsRequired,
                parameterName => new UsageException($"{parameterName} is required.")));
    }

    [Fact]
    public void RejectClearingRequiredParameters_AllowsOmissionButRejectsBlank()
    {
        var definitions = new[]
        {
            new ChannelParameterDefinition(
                DeploymentParameterType.String,
                "requiredValue",
                "Required.",
                IsRequired: true),
        };

        DeploymentParameterValidator.RejectClearingRequiredParameters(
            definitions,
            new Dictionary<string, string?>(),
            definition => definition.Name,
            definition => definition.IsRequired,
            parameterName => new UsageException($"{parameterName} cannot be cleared."));
        Assert.Throws<UsageException>(() =>
            DeploymentParameterValidator.RejectClearingRequiredParameters(
                definitions,
                new Dictionary<string, string?> { ["requiredValue"] = string.Empty },
                definition => definition.Name,
                definition => definition.IsRequired,
                parameterName => new UsageException($"{parameterName} cannot be cleared.")));
    }

    [Fact]
    public void ApplyOptionalParameterDefaults_DoesNotUseDefaultForRequiredParameter()
    {
        var definitions = new[]
        {
            new ChannelParameterDefinition(
                DeploymentParameterType.String,
                "requiredValue",
                "Required.",
                IsRequired: true,
                DefaultValue: "must-not-apply"),
            new ChannelParameterDefinition(
                DeploymentParameterType.Boolean,
                "optionalValue",
                "Optional.",
                DefaultValue: "True"),
        };

        var result = DeploymentParameterValidator.ApplyOptionalParameterDefaults(
            definitions,
            new Dictionary<string, string?>(),
            definition => definition.Name,
            definition => definition.IsRequired,
            definition => definition.Type,
            definition => definition.DefaultValue,
            (parameterName, type) => new UsageException($"{parameterName} must be {type}."));

        Assert.False(result.ContainsKey("requiredValue"));
        Assert.Equal("true", result["optionalValue"]);
    }
}
