using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentChannelPlanTests
{
    private const string DeploymentId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void ValidateCreate_NormalizesCaseInsensitiveParametersAndAppliesDefaults()
    {
        var request = CreateRequest(
            "slack",
            "CLIENTID=client-1",
            "clientsecret=secret-1",
            "VerificationToken=token-1");
        var plan = DeploymentChannelPlan.ValidateCreate(request);
        var upsert = DeploymentChannelPlan.BuildCreate(plan, BotResourceId, "tenant-1");
        var parameters = Parameters(upsert);

        Assert.Equal("slack", plan.Request.ChannelName);
        Assert.Equal("SlackChannel", plan.ChannelDefinition.ArmName);
        Assert.Equal(BotResourceId, upsert.BotResourceId);
        Assert.Equal("client-1", parameters["clientId"].GetString());
        Assert.Equal("secret-1", parameters["clientSecret"].GetString());
        Assert.True(parameters["isEnabled"].GetBoolean());
        Assert.True(parameters["registerBeforeOAuthFlow"].GetBoolean());
    }

    [Fact]
    public void ValidateCreate_MissingRequiredProperty_Throws()
    {
        var request = CreateRequest("slack", "clientId=client-1", "verificationToken=token-1");
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(request));

        Assert.Contains("clientSecret", ex.Message);
    }

    [Fact]
    public void ValidateCreate_DirectLineRequiresSiteName()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(CreateRequest("directline")));

        Assert.Equal("--parameters siteName=... is required for 'directline'.", ex.Message);
    }

    [Fact]
    public void ValidateCreate_WebChatRequiresSiteName()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(CreateRequest("webchat")));

        Assert.Equal("--parameters siteName=... is required for 'webchat'.", ex.Message);
    }

    [Fact]
    public void ValidateCreate_RecognizedUnsupportedChannel_UsesSharedEnumError()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(CreateRequest("email")));

        Assert.Contains("--channel is not supported", ex.Message);
        Assert.Contains("Supported values are:", ex.Message);
    }

    [Fact]
    public void ValidateCreate_UnknownChannel_Throws()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(CreateRequest("unknown")));

        Assert.Contains("--channel is not supported", ex.Message);
        Assert.Contains("Supported values are:", ex.Message);
    }

    [Fact]
    public void ValidateCreate_CaseInsensitiveDuplicateProperty_ThrowsWithoutEchoingKey()
    {
        var request = CreateRequest("msteams", "enableCalling=true", "ENABLECALLING=false");
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(request));

        Assert.Contains("duplicate key at position 2", ex.Message);
        Assert.DoesNotContain("enableCalling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateUpdate_OptionalProperty_AllowsClearValue()
    {
        var request = UpdateRequest("msteams", "callingWebhook=");
        var plan = DeploymentChannelPlan.ValidateUpdate(request);
        var upsert = DeploymentChannelPlan.BuildUpdate(
            plan,
            Snapshot("""{"properties":{"properties":{"isEnabled":true,"callingWebhook":"https://old.example"}}}"""),
            BotResourceId,
            "tenant-1");

        Assert.Equal(JsonValueKind.Null, Parameters(upsert)["callingWebhook"].ValueKind);
    }

    [Fact]
    public void ValidateUpdate_RequiredProperty_RejectsEmptyValue()
    {
        var request = UpdateRequest("slack", "clientId=");
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(request));

        Assert.Contains("cannot be cleared", ex.Message);
    }

    [Fact]
    public void ValidateUpdate_DirectLineCannotClearSiteName()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("directline", "siteName=")));

        Assert.Equal("--parameters siteName=... is required for 'directline' and cannot be cleared.", ex.Message);
    }

    [Fact]
    public void ValidateUpdate_WebChatCannotClearSiteName()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("webchat", "siteName=")));

        Assert.Equal("--parameters siteName=... is required for 'webchat' and cannot be cleared.", ex.Message);
    }

    [Fact]
    public void ValidateCreate_M365Extensions_HasNoParameters()
    {
        var plan = DeploymentChannelPlan.ValidateCreate(CreateRequest("m365extensions"));
        var upsert = DeploymentChannelPlan.BuildCreate(plan, BotResourceId, "tenant-1");

        Assert.Equal("M365Extensions", plan.ChannelDefinition.ArmName);
        Assert.Empty(plan.NormalizedParameters);
        Assert.Empty(upsert.Parameters);
    }

    [Fact]
    public void ValidateUpdate_ChannelWithoutParameters_RejectsUpdate()
    {
        var ex = Assert.Throws<DeploymentException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("m365extensions", "isEnabled=true")));

        Assert.Equal("channel-operation-not-supported", ex.ErrorKind);
        Assert.Equal("m365extensions does not support update.", ex.Message);
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void ValidateDelete_AutomaticallyCreatedChannel_RejectsDelete(string channel)
    {
        var ex = Assert.Throws<DeploymentException>(() =>
            DeploymentChannelPlan.ValidateDelete(DeleteRequest(channel)));

        Assert.Equal("channel-operation-not-supported", ex.ErrorKind);
        Assert.Equal($"{channel} does not support delete.", ex.Message);
    }

    [Fact]
    public void ValidateUpdate_InvalidBoolean_ThrowsWithoutEchoingValue()
    {
        const string invalid = "SYNTHETIC_BOOLEAN_VALUE";
        var request = UpdateRequest("msteams", $"enableCalling={invalid}");
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(request));

        Assert.Contains("true or false", ex.Message);
        Assert.DoesNotContain(invalid, ex.Message);
    }

    [Fact]
    public void ValidateCreate_UnknownParameter_Throws()
    {
        var request = CreateRequest("msteams", "unknown=value");
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(request));

        Assert.Contains("unknown", ex.Message);
        Assert.Contains("not declared", ex.Message);
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void BuildCreate_NestedChannel_ShapesSingleSite(string channel)
    {
        var plan = DeploymentChannelPlan.ValidateCreate(CreateRequest(channel, "siteName=Primary"));
        var upsert = DeploymentChannelPlan.BuildCreate(plan, BotResourceId, "tenant-1");

        var sites = Parameters(upsert)["sites"];
        var site = Assert.Single(sites.EnumerateArray());
        Assert.Equal("Primary", site.GetProperty("siteName").GetString());
        Assert.True(site.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public void BuildUpdate_DirectLine_ShapesCompletePropertySet()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest(
                "directline",
                "appId=app-1",
                "isBlockUserUploadEnabled=true",
                "isDetailedLoggingEnabled=false",
                "isEnabled=true",
                "isEndpointParametersEnabled=false",
                "isNoStorageEnabled=true",
                "isSecureSiteEnabled=false",
                "isTokenEnabled=true",
                "isV3Enabled=false",
                "siteName=Property Matrix",
                "tenantId=tenant-1",
                "trustedOrigins=https://one.example,https://two.example"));
        var upsert = DeploymentChannelPlan.BuildUpdate(
            plan,
            Snapshot("""{"properties":{"properties":{"sites":[{}]}}}"""),
            BotResourceId,
            "tenant-1");

        var site = Assert.Single(Parameters(upsert)["sites"].EnumerateArray());
        Assert.Equal("app-1", site.GetProperty("appId").GetString());
        Assert.True(site.GetProperty("isBlockUserUploadEnabled").GetBoolean());
        Assert.False(site.GetProperty("isDetailedLoggingEnabled").GetBoolean());
        Assert.True(site.GetProperty("isEnabled").GetBoolean());
        Assert.False(site.GetProperty("isEndpointParametersEnabled").GetBoolean());
        Assert.True(site.GetProperty("isNoStorageEnabled").GetBoolean());
        Assert.False(site.GetProperty("isSecureSiteEnabled").GetBoolean());
        Assert.True(site.GetProperty("isTokenEnabled").GetBoolean());
        Assert.False(site.GetProperty("isV3Enabled").GetBoolean());
        Assert.Equal("Property Matrix", site.GetProperty("siteName").GetString());
        Assert.Equal("tenant-1", site.GetProperty("tenantId").GetString());
        Assert.Equal(2, site.GetProperty("trustedOrigins").GetArrayLength());
    }

    [Fact]
    public void BuildUpdate_WebChat_ShapesCompletePropertySet()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest(
                "webchat",
                "appId=app-1",
                "isBlockUserUploadEnabled=true",
                "isDetailedLoggingEnabled=false",
                "isEnabled=true",
                "isEndpointParametersEnabled=false",
                "isNoStorageEnabled=true",
                "isTokenEnabled=true",
                "isWebchatPreviewEnabled=true",
                "siteName=Property Matrix",
                "tenantId=tenant-1"));
        var upsert = DeploymentChannelPlan.BuildUpdate(
            plan,
            Snapshot("""{"properties":{"properties":{"sites":[{}]}}}"""),
            BotResourceId,
            "tenant-1");

        var site = Assert.Single(Parameters(upsert)["sites"].EnumerateArray());
        Assert.Equal("app-1", site.GetProperty("appId").GetString());
        Assert.True(site.GetProperty("isBlockUserUploadEnabled").GetBoolean());
        Assert.False(site.GetProperty("isDetailedLoggingEnabled").GetBoolean());
        Assert.True(site.GetProperty("isEnabled").GetBoolean());
        Assert.False(site.GetProperty("isEndpointParametersEnabled").GetBoolean());
        Assert.True(site.GetProperty("isNoStorageEnabled").GetBoolean());
        Assert.True(site.GetProperty("isTokenEnabled").GetBoolean());
        Assert.True(site.GetProperty("isWebchatPreviewEnabled").GetBoolean());
        Assert.Equal("Property Matrix", site.GetProperty("siteName").GetString());
        Assert.Equal("tenant-1", site.GetProperty("tenantId").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidateMutation_DirectLineRejectsReadOnlyV1(bool isCreate)
    {
        var request = isCreate
            ? CreateRequest("directline", "isV1Enabled=false")
            : UpdateRequest("directline", "isV1Enabled=false");

        var ex = Assert.Throws<UsageException>(() =>
            isCreate
                ? DeploymentChannelPlan.ValidateCreate(request)
                : DeploymentChannelPlan.ValidateUpdate((DeploymentChannelUpdateRequest)request));

        Assert.Equal("--parameters isV1Enabled=... is read-only for 'directline' and cannot be created or updated.", ex.Message);
    }

    [Theory]
    [InlineData("eTag")]
    [InlineData("isWebchatPreviewEnabled")]
    [InlineData("isWebChatSpeechEnabled")]
    public void ValidateUpdate_DirectLineRejectsNonPersistedParameter(string parameter)
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("directline", $"{parameter}=value")));

        Assert.Contains("is not declared by 'directline'", ex.Message);
    }

    [Theory]
    [InlineData("eTag")]
    [InlineData("isSecureSiteEnabled")]
    [InlineData("isV1Enabled")]
    [InlineData("isV3Enabled")]
    [InlineData("isWebChatSpeechEnabled")]
    [InlineData("trustedOrigins")]
    public void ValidateUpdate_WebChatRejectsNonPersistedParameter(string parameter)
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("webchat", $"{parameter}=value")));

        Assert.Contains("is not declared by 'webchat'", ex.Message);
    }

    [Fact]
    public void BuildUpdate_DirectLineDoesNotReplayReadOnlyV1()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("directline", "siteName=Primary", "isV3Enabled=false"));
        var upsert = DeploymentChannelPlan.BuildUpdate(
            plan,
            Snapshot("""{"properties":{"properties":{"sites":[{"siteName":"Primary","isV1Enabled":true,"isV3Enabled":true}]}}}"""),
            BotResourceId,
            "tenant-1");

        var site = Assert.Single(Parameters(upsert)["sites"].EnumerateArray());
        Assert.False(site.TryGetProperty("isV1Enabled", out _));
        Assert.False(site.GetProperty("isV3Enabled").GetBoolean());
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void BuildUpdate_NestedChannel_OverridesSitesArrayWithSingleMergedSite(string channel)
    {
        var parameters = new[] { "isEnabled=false" };
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel, parameters));
        var upsert = DeploymentChannelPlan.BuildUpdate(
            plan,
            Snapshot(
                """
                {"properties":{"properties":{"sites":[
                  {"siteName":"Primary","isEnabled":true},
                  {"siteName":"Secondary","isEnabled":true}
                ]}}}
                """),
            BotResourceId,
            "tenant-1");

        var sites = Parameters(upsert)["sites"];
        var site = Assert.Single(sites.EnumerateArray());
        Assert.Equal("Primary", site.GetProperty("siteName").GetString());
        Assert.False(site.GetProperty("isEnabled").GetBoolean());
    }

    [Fact]
    public void ValidateUpdate_MergesSafeExistingValuesWithoutReplayingSecrets()
    {
        var request = UpdateRequest("slack", "clientSecret=new-secret", "isEnabled=false");
        var plan = DeploymentChannelPlan.ValidateUpdate(request);
        var upsert = DeploymentChannelPlan.BuildUpdate(
            plan,
            Snapshot(
                """
                {"properties":{"properties":{
                  "isEnabled":true,
                  "clientId":"existing-client",
                  "clientSecret":"***",
                  "landingPageUrl":"https://old.example"
                }}}
                """),
            BotResourceId,
            "tenant-1");
        var parameters = Parameters(upsert);

        Assert.False(parameters["isEnabled"].GetBoolean());
        Assert.Equal("existing-client", parameters["clientId"].GetString());
        Assert.Equal("new-secret", parameters["clientSecret"].GetString());
        Assert.Equal("https://old.example", parameters["landingPageUrl"].GetString());
    }

    [Fact]
    public void BuildUpdate_UsesPersistedParameters_NotRawArmResponse()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("msteams", "enableCalling=true"));
        var existing = new DeploymentChannelRecord
        {
            ChannelName = "msteams",
            ResourceId = $"{BotResourceId}/channels/MsTeamsChannel",
            Parameters = new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["isEnabled"] = JsonValue("true"),
                ["callingWebhook"] = JsonValue("\"https://saved.example\""),
            },
            RawArmResponse = System.Text.Json.JsonDocument.Parse(
                """{"properties":{"properties":{"isEnabled":false,"callingWebhook":"https://raw.example"}}}""")
                .RootElement.Clone(),
        };

        var upsert = DeploymentChannelPlan.BuildUpdate(plan, existing, BotResourceId, "tenant-1");

        Assert.Equal("https://saved.example", Parameters(upsert)["callingWebhook"].GetString());
        Assert.True(Parameters(upsert)["isEnabled"].GetBoolean());
        Assert.True(Parameters(upsert)["enableCalling"].GetBoolean());
    }

    [Fact]
    public void RequestModels_FollowOAuthVerbPattern()
    {
        Assert.Null(typeof(DeploymentChannelShowRequest).GetProperty(nameof(DeploymentChannelUpdateRequest.Parameters)));
        Assert.Null(typeof(DeploymentChannelDeleteRequest).GetProperty(nameof(DeploymentChannelUpdateRequest.Parameters)));
        Assert.Null(typeof(DeploymentChannelListRequest).GetProperty(nameof(DeploymentChannelRequest.ChannelName)));
        Assert.Equal(typeof(DeploymentChannelRequest), typeof(DeploymentChannelShowRequest).BaseType);
        Assert.Equal(typeof(DeploymentChannelRequest), typeof(DeploymentChannelDeleteRequest).BaseType);
        Assert.Equal(typeof(DeploymentChannelRequest), typeof(DeploymentChannelCreateRequest).BaseType);
        Assert.Equal(typeof(DeploymentChannelCreateRequest), typeof(DeploymentChannelUpdateRequest).BaseType);
        Assert.Equal(typeof(DeploymentRequest), typeof(DeploymentChannelListRequest).BaseType);
    }

    [Fact]
    public void Validate_CanonicalizesDeploymentGuidWithSharedValidator()
    {
        var plan = DeploymentChannelPlan.Validate(
            ShowRequest("msteams") with { DeploymentId = "{11111111-1111-1111-1111-111111111111}" });

        Assert.Equal(DeploymentId, plan.DeploymentId);
        Assert.Equal("msteams", plan.ChannelName);
    }

    [Fact]
    public void Validate_UnsupportedChannel_UsesSharedEnumError()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.Validate(ShowRequest("email")));

        Assert.Contains("--channel is not supported", ex.Message);
        Assert.Contains("Supported values are:", ex.Message);
    }

    [Fact]
    public void Validate_InvalidAgentGuidUsesSharedValidation()
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.Validate(
                ShowRequest("msteams") with
                {
                    DeploymentId = null,
                    AgentId = "not-a-guid",
                    DeploymentName = "test-deployment",
                }));

        Assert.Equal("--agent-id must be a valid GUID.", ex.Message);
    }

    private static DeploymentChannelCreateRequest CreateRequest(string channel, params string[] parameters) =>
        new()
        {
            DeploymentId = DeploymentId,
            ChannelName = channel,
            Parameters = parameters,
        };

    private static DeploymentChannelUpdateRequest UpdateRequest(string channel, params string[] parameters) =>
        new()
        {
            DeploymentId = DeploymentId,
            ChannelName = channel,
            Parameters = parameters,
        };

    private static DeploymentChannelShowRequest ShowRequest(string channel) =>
        new()
        {
            DeploymentId = DeploymentId,
            ChannelName = channel,
        };

    private static DeploymentChannelDeleteRequest DeleteRequest(string channel) =>
        new()
        {
            DeploymentId = DeploymentId,
            ChannelName = channel,
        };

    private static DeploymentChannelRecord Snapshot(string json = """{"properties":{"properties":{}}}""")
    {
        var raw = System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();
        var inner = raw.GetProperty("properties").GetProperty("properties");
        return new()
        {
            ChannelName = "channel",
            ResourceId = $"{BotResourceId}/channels/channel",
            Parameters = inner.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => property.Value.Clone(),
                    StringComparer.Ordinal),
            RawArmResponse = raw,
        };
    }

    private static System.Text.Json.JsonElement JsonValue(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> Parameters(BotServiceChannelUpsertRequest upsert) =>
        upsert.Parameters;

    private const string BotResourceId =
        "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev";
}
