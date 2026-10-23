using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentRequestValidatorTests
{
    private const string AgentId = "44444444-4444-4444-4444-444444444444";
    private const string DeploymentId = "11111111-1111-1111-1111-111111111111";
    private const string EntraId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Validate_DeploymentOnly_ReturnsVerbatim()
    {
        var result = DeploymentRequestValidator.Validate(new DeploymentRequest { DeploymentId = DeploymentId });

        Assert.Equal(DeploymentId, result.DeploymentId);
        Assert.Null(result.AgentId);
        Assert.Null(result.DeploymentName);
    }

    [Fact]
    public void Validate_AgentAndName_PassesThroughForDeterministicResolution()
    {
        var result = DeploymentRequestValidator.Validate(new DeploymentRequest { AgentId = AgentId, DeploymentName = "dev1" });

        Assert.Null(result.DeploymentId);
        Assert.Equal(AgentId, result.AgentId);
        Assert.Equal("dev1", result.DeploymentName);
    }

    [Fact]
    public void Validate_NoAddressing_ThrowsUsage()
    {
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.Validate(new DeploymentRequest()));
        Assert.Contains("Either --deployment-id <id> or both --agent-id and --deployment-name must be provided.", ex.Message);
    }

    [Theory]
    [InlineData("{11111111-1111-1111-1111-111111111111}")]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("11111111-1111-1111-1111-111111111111")]
    public void Validate_EquivalentDeploymentIdSpellings_CanonicalizeToDForm(string deploymentId)
    {
        // Braced, compact, and hyphenated GUID spellings must all normalize to the canonical "D" form so
        // they can address a record stored in "D" form (downstream lookups compare ids as strings).
        var result = DeploymentRequestValidator.Validate(new DeploymentRequest { DeploymentId = deploymentId });

        Assert.Equal(DeploymentId, result.DeploymentId);
    }

    [Fact]
    public void Validate_AgentWithoutName_ThrowsUsage()
    {
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.Validate(new DeploymentRequest { AgentId = AgentId }));
        Assert.Contains("Either --deployment-id <id> or both --agent-id and --deployment-name must be provided.", ex.Message);
    }

    [Fact]
    public void Validate_DeploymentCombinedWithAgentName_ThrowsUsage()
    {
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.Validate(
            new DeploymentRequest { DeploymentId = DeploymentId, AgentId = AgentId, DeploymentName = "dev1" }));
        Assert.Contains("--deployment-id cannot be combined with --agent-id/--deployment-name", ex.Message);
    }

    [Fact]
    public void Validate_DeploymentNotGuid_ThrowsUsage()
    {
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.Validate(new DeploymentRequest { DeploymentId = "not-a-guid" }));
        Assert.Contains("--deployment-id must be a valid GUID.", ex.Message);
    }

    [Fact]
    public void Validate_ValidDeployment_DoesNotThrow()
    {
        DeploymentRequestValidator.Validate(new DeploymentRequest { DeploymentId = DeploymentId });
    }

    [Fact]
    public void Validate_ValidAgentAndName_DoesNotThrow()
    {
        DeploymentRequestValidator.Validate(new DeploymentRequest { AgentId = AgentId, DeploymentName = "dev1" });
    }

    [Fact]
    public void ValidateUpdate_NoAddressing_ThrowsUsage()
    {
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.ValidateUpdate(
            new DeploymentUpdateRequest { Endpoint = "https://x.example/messages" }));
        Assert.Contains("Either --deployment-id <id> or both --agent-id and --deployment-name must be provided.", ex.Message);
    }

    [Fact]
    public void ValidateUpdate_NoMutableFields_ThrowsUsage()
    {
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.ValidateUpdate(
            new DeploymentUpdateRequest { DeploymentId = DeploymentId }));
        Assert.Contains("At least one", ex.Message);
    }

    [Fact]
    public void ValidateUpdate_AgentAndNameWithField_DoesNotThrow()
    {
        DeploymentRequestValidator.ValidateUpdate(
            new DeploymentUpdateRequest { AgentId = AgentId, DeploymentName = "dev1", Endpoint = "https://x.example/messages" });
    }

    [Fact]
    public void ValidateCreate_UnsupportedEntraIdType_ThrowsUsage()
    {
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.ValidateCreate(
            new DeploymentCreateRequest { AgentId = AgentId, DeploymentName = "dev1", EntraId = EntraId, EntraIdType = "Blueprint", DeploymentType = "Dev" }));
        Assert.Contains("--entra-id-type is not supported. Supported values are: AppReg.", ex.Message);
    }

    [Theory]
    [InlineData("appReg")]
    [InlineData("APPREG")]
    [InlineData("AppReg")]
    public void ValidateCreate_DocumentedAppRegCasing_IsAccepted(string entraIdType)
    {
        // The documented value "appReg" (and any casing) must be accepted, not rejected by a
        // case-sensitive supported-value check.
        DeploymentRequestValidator.ValidateCreate(
            new DeploymentCreateRequest { AgentId = AgentId, DeploymentName = "dev1", EntraId = EntraId, EntraIdType = entraIdType, DeploymentType = "Dev" });
    }

    [Theory]
    [InlineData("--endpoint-url")]
    [InlineData("--display-name")]
    [InlineData("--sku")]
    [InlineData("--deployment-type")]
    [InlineData("--description")]
    public void ValidateUpdate_BlankMutableField_ThrowsUsage(string flag)
    {
        var request = flag switch
        {
            "--endpoint-url" => new DeploymentUpdateRequest { DeploymentId = DeploymentId, Endpoint = "   " },
            "--display-name" => new DeploymentUpdateRequest { DeploymentId = DeploymentId, DisplayName = "   " },
            "--sku" => new DeploymentUpdateRequest { DeploymentId = DeploymentId, Sku = "   " },
            "--deployment-type" => new DeploymentUpdateRequest { DeploymentId = DeploymentId, DeploymentType = "   " },
            _ => new DeploymentUpdateRequest { DeploymentId = DeploymentId, Description = "   " },
        };

        // An explicitly-provided-but-blank mutable field is rejected rather than silently dropped: the
        // downstream PATCH omits blank values, so accepting it would appear to succeed while leaving the
        // field unchanged. Clearing is unsupported, so require a value or omit the option.
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.ValidateUpdate(request));
        Assert.Contains($"{flag} cannot be empty", ex.Message);
    }

    [Fact]
    public void ValidateCreate_TrimsRequiredAndOptionalStrings_AndCanonicalizesEnums()
    {
        var normalized = DeploymentRequestValidator.ValidateCreate(new DeploymentCreateRequest
        {
            AgentId = $"  {AgentId}  ",
            DeploymentName = "  dev1  ",
            EntraId = $"  {EntraId}  ",
            EntraIdType = "appreg",
            DeploymentType = "dev",
            Sku = "f0",
            ResourceGroup = "  rg-1  ",
            DisplayName = "  My Bot  ",
            Description = "  desc  ",
            Endpoint = "  https://x.example/messages  ",
        });

        Assert.Equal(AgentId, normalized.AgentId);
        Assert.Equal("dev1", normalized.DeploymentName);
        Assert.Equal(EntraId, normalized.EntraId);
        Assert.Equal("AppReg", normalized.EntraIdType);
        Assert.Equal("Dev", normalized.DeploymentType);
        Assert.Equal("F0", normalized.Sku);
        Assert.Equal("rg-1", normalized.ResourceGroup);
        Assert.Equal("My Bot", normalized.DisplayName);
        Assert.Equal("desc", normalized.Description);
        Assert.Equal("https://x.example/messages", normalized.Endpoint);
    }

    [Theory]
    [InlineData("--resource-group")]
    [InlineData("--location")]
    [InlineData("--display-name")]
    [InlineData("--description")]
    [InlineData("--deployment-type")]
    [InlineData("--sku")]
    [InlineData("--endpoint-url")]
    [InlineData("--subscription")]
    [InlineData("--tenant")]
    public void ValidateCreate_ProvidedButBlankOption_ThrowsUsage(string flag)
    {
        DeploymentCreateRequest Base() => new()
        {
            AgentId = AgentId,
            DeploymentName = "dev1",
            EntraId = EntraId,
            EntraIdType = "AppReg",
            DeploymentType = "Dev",
        };
        var request = flag switch
        {
            "--resource-group" => Base() with { ResourceGroup = "   " },
            "--location" => Base() with { Location = "   " },
            "--display-name" => Base() with { DisplayName = "   " },
            "--description" => Base() with { Description = "   " },
            "--deployment-type" => Base() with { DeploymentType = "   " },
            "--sku" => Base() with { Sku = "   " },
            "--endpoint-url" => Base() with { Endpoint = "   " },
            "--subscription" => Base() with { SubscriptionId = "   " },
            _ => Base() with { TenantId = "   " },
        };

        // Every value-bearing option — even optional ones like --location — must be non-blank when it is
        // provided; a blank value is rejected rather than silently ignored.
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.ValidateCreate(request));
        Assert.Contains($"{flag} cannot be empty", ex.Message);
    }

    [Fact]
    public void ValidateUpdate_BlankEndpointAlongsideRealChange_ThrowsUsage()
    {
        // A whitespace-only --endpoint alongside a genuine change must be rejected, not silently dropped:
        // the ARM patch omits blank values, so a blank endpoint would appear to succeed while leaving the
        // endpoint unchanged.
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.ValidateUpdate(new DeploymentUpdateRequest
        {
            DeploymentId = DeploymentId,
            DisplayName = "  New Name  ",
            Endpoint = "   ",
        }));

        Assert.Contains("--endpoint-url cannot be empty", ex.Message);
    }

    [Theory]
    [InlineData("--subscription")]
    [InlineData("--tenant")]
    public void ValidateCreate_NonGuidSubscriptionOrTenant_ThrowsUsage(string flag)
    {
        var request = new DeploymentCreateRequest
        {
            AgentId = AgentId,
            DeploymentName = "dev1",
            EntraId = EntraId,
            EntraIdType = "AppReg",
            DeploymentType = "Dev",
            SubscriptionId = flag == "--subscription" ? "not-a-guid" : null,
            TenantId = flag == "--tenant" ? "not-a-guid" : null,
        };

        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.ValidateCreate(request));
        Assert.Contains($"{flag} must be a valid GUID.", ex.Message);
    }

    [Fact]
    public void ValidateCreate_GuidSubscriptionAndTenant_AreTrimmed()
    {
        var normalized = DeploymentRequestValidator.ValidateCreate(new DeploymentCreateRequest
        {
            AgentId = AgentId,
            DeploymentName = "dev1",
            EntraId = EntraId,
            EntraIdType = "AppReg",
            DeploymentType = "Dev",
            SubscriptionId = $"  {DeploymentId}  ",
            TenantId = $"  {AgentId}  ",
        });

        Assert.Equal(DeploymentId, normalized.SubscriptionId);
        Assert.Equal(AgentId, normalized.TenantId);
    }

    [Fact]
    public void ValidateCreate_MissingDeploymentType_ThrowsUsage()
    {
        var ex = Assert.Throws<UsageException>(() => DeploymentRequestValidator.ValidateCreate(
            new DeploymentCreateRequest
            {
                AgentId = AgentId,
                DeploymentName = "dev1",
                EntraId = EntraId,
                EntraIdType = "AppReg",
                DeploymentType = null!,
            }));

        Assert.Contains("--deployment-type is required.", ex.Message);
    }

    [Fact]
    public void Required_TrimsValue()
    {
        Assert.Equal("value", DeploymentRequestValidator.Required("--value", "  value  "));
    }

    [Fact]
    public void Required_BlankValue_UsesSuppliedBlankMessage()
    {
        var exception = Assert.Throws<UsageException>(() =>
            DeploymentRequestValidator.Required("--value", "  "));

        Assert.Equal(
            "--value cannot be empty. Provide a value or omit the option.",
            exception.Message);
    }

    [Fact]
    public void Required_OmittedValue_UsesRequiredMessage()
    {
        var exception = Assert.Throws<UsageException>(() =>
            DeploymentRequestValidator.Required("--value", null));

        Assert.Equal("--value is required.", exception.Message);
    }

    [Fact]
    public void Optional_BlankValue_UsesConsistentMessage()
    {
        var exception = Assert.Throws<UsageException>(() =>
            DeploymentRequestValidator.Optional("--value", "  "));

        Assert.Equal(
            "--value cannot be empty. Provide a value or omit the option.",
            exception.Message);
    }

    [Fact]
    public void Trim_CanPreserveExplicitClear()
    {
        Assert.Equal(string.Empty, DeploymentRequestValidator.Trim("  ", preserveEmpty: true));
        Assert.Null(DeploymentRequestValidator.Trim(null, preserveEmpty: true));
    }

    [Fact]
    public void OptionalGuid_CanonicalizesGuid()
    {
        Assert.Equal(
            DeploymentId,
            DeploymentRequestValidator.OptionalGuid("--id", $" {{{DeploymentId}}} "));
    }

    [Fact]
    public void OptionalEnum_CanonicalizesCasing()
    {
        Assert.Equal(
            "Prod",
            DeploymentRequestValidator.OptionalEnum(
                "--type",
                " prod ",
                ["Dev", "Test", "Prod"]));
    }

    [Fact]
    public void OptionalHttpsUrl_RejectsNonHttpsValue()
    {
        var exception = Assert.Throws<UsageException>(() =>
            DeploymentRequestValidator.OptionalHttpsUrl("--endpoint", "http://example.test"));

        Assert.Equal("--endpoint must be a valid HTTPS URL.", exception.Message);
    }

    [Theory]
    [InlineData(" abcd ", "abcd")]
    [InlineData("A-b_9", "A-b_9")]
    [InlineData("a23456789012345678901234567890123456789012", "a23456789012345678901234567890123456789012")]
    public void DeploymentNameRule_AcceptsAndTrimsANameOfFourToFortyTwoAllowedCharacters(string value, string expected)
    {
        Assert.Equal(expected, DeploymentNameRule.Validate("--deployment-name", value));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData(" dev ")]
    [InlineData("a234567890123456789012345678901234567890123")]
    [InlineData("has space")]
    [InlineData("abc.d")]
    public void DeploymentNameRule_RejectsANameOutsideTheRuleAndNamesTheOption(string value)
    {
        var exception = Assert.Throws<UsageException>(() => DeploymentNameRule.Validate("--new-name", value));

        Assert.Equal("--new-name must be between 4 and 42 characters in length and can only contain the characters -, a-z, A-Z, 0-9, and _.", exception.Message);
    }
}
