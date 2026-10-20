using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentChannelPlanTests
{
    [Theory]
    [InlineData("directline", true)]
    [InlineData("directline", false)]
    [InlineData("webchat", true)]
    [InlineData("webchat", false)]
    public void Sites_MissingInputIdentifiesOperation(string channel, bool create)
    {
        var error = Assert.Throws<UsageException>(() =>
        {
            if (create)
                DeploymentChannelPlan.ValidateCreate(CreateRequest(channel));
            else
                DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel));
        });

        Assert.Equal($"No site configuration was provided for '{channel}' channel {(create ? "create" : "update")}.", error.Message);
    }

    [Fact]
    public void Sites_UpdateShapedCreateRequestRejectsItemOptionAtPlanBoundary()
    {
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(
            UpdateRequest("directline") with { AddSite = ["siteName=SYNTHETIC_SECRET"] }));

        Assert.Equal("Option '--add-site' is not valid for 'directline' channel create.", error.Message);
    }

    [Theory]
    [InlineData("site", "directline")]
    [InlineData("page", "facebook")]
    [InlineData("registration", "line")]
    public void Collections_ConflictingOptionsListTheActualChoices(string itemName, string channel)
    {
        var request = UpdateRequest(channel, "isEnabled=false") with
        {
            AddSite = itemName == "site" ? ["siteName=SYNTHETIC_SECRET"] : [],
            AddPage = itemName == "page" ? ["id=one", "accessToken=SYNTHETIC_SECRET"] : [],
            AddRegistration = itemName == "registration" ? ["channelSecret=SYNTHETIC_SECRET"] : [],
        };
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(request));

        Assert.Equal($"Specify only one of --parameters, --add-{itemName}, --set-{itemName}, or --remove-{itemName}.", error.Message);
    }

    [Theory]
    [InlineData("directline", "siteName")]
    [InlineData("facebook", "id")]
    [InlineData("line", "generatedId")]
    public void Collections_UnknownSelectorDoesNotEchoTheValue(string channel, string selector)
    {
        var request = UpdateRequest(channel) with
        {
            RemoveSite = channel == "directline" ? ["siteName=SYNTHETIC_SECRET"] : [],
            RemovePage = channel == "facebook" ? ["id=SYNTHETIC_SECRET"] : [],
            RemoveRegistration = channel == "line" ? ["generatedId=SYNTHETIC_SECRET"] : [],
        };
        var existing = Snapshot("""{"properties":{"properties":{"sites":[],"pages":[],"lineRegistrations":[]}}}""");
        var plan = DeploymentChannelPlan.ValidateUpdate(request);
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.BuildUpdate(plan, existing, BotResourceId, null));

        Assert.Equal($"No item in '{channel}' matches the '{selector}' selector.", error.Message);
        Assert.DoesNotContain("SYNTHETIC_SECRET", error.Message);
    }

    [Theory]
    [InlineData("directline", "siteName")]
    [InlineData("facebook", "id")]
    public void Collections_AddExistingSelectorDoesNotEchoTheValue(string channel, string selector)
    {
        var request = UpdateRequest(channel) with
        {
            AddSite = channel == "directline" ? ["siteName=SYNTHETIC_SECRET"] : [],
            AddPage = channel == "facebook" ? ["id=SYNTHETIC_SECRET", "accessToken=SYNTHETIC_TOKEN"] : [],
        };
        var existing = Snapshot("""
            {"properties":{"properties":{"sites":[{"siteName":"SYNTHETIC_SECRET","siteId":"one"}],
              "pages":[{"id":"SYNTHETIC_SECRET"}]}}}
            """);
        var plan = DeploymentChannelPlan.ValidateUpdate(request);
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.BuildUpdate(plan, existing, BotResourceId, null));

        Assert.Equal($"An item with the same '{selector}' already exists in '{channel}'.", error.Message);
        Assert.DoesNotContain("SYNTHETIC_SECRET", error.Message);
        Assert.DoesNotContain("SYNTHETIC_TOKEN", error.Message);
    }

    [Theory]
    [InlineData("isEnabled", "\"SYNTHETIC_SECRET\"", "Boolean")]
    [InlineData("appId", "false", "String")]
    [InlineData("isEnabled", "null", "Boolean")]
    [InlineData("trustedOrigins", "[false]", "Array")]
    public void Sites_InvalidValuesIdentifyTheParameter(string field, string value, string type)
    {
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("directline", $$"""sites=[{"siteName":"Primary","{{field}}":{{value}}}]""")));

        Assert.Equal($"Parameter 'sites[].{field}' for 'directline' must have a valid {type} value.", error.Message);
    }

    [Theory]
    [InlineData("isEnabled=SYNTHETIC_SECRET", "isEnabled", "must be true or false")]
    [InlineData("trustedOrigins=[SYNTHETIC_SECRET", "trustedOrigins", "must contain valid JSON")]
    public void Sites_InvalidItemAssignmentUsesTheNestedParameterPath(string assignment, string field, string requirement)
    {
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("directline") with { SetSite = ["siteName=Primary", assignment] }));

        Assert.Equal($"Parameter 'sites[].{field}' for 'directline' {requirement}.", error.Message);
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    [InlineData("facebook")]
    [InlineData("line")]
    public void Collections_MissingReadbackIsAServiceFailure(string channel)
    {
        var request = UpdateRequest(channel) with
        {
            RemoveSite = channel is "directline" or "webchat" ? ["siteName=one"] : [],
            RemovePage = channel == "facebook" ? ["id=one"] : [],
            RemoveRegistration = channel == "line" ? ["generatedId=one"] : [],
        };
        var plan = DeploymentChannelPlan.ValidateUpdate(request);
        var error = Assert.Throws<DeploymentException>(() => DeploymentChannelPlan.BuildUpdate(plan, Snapshot(), BotResourceId, null));

        Assert.Equal("The channel response does not contain a usable configuration.", error.Message);
        Assert.Equal(ExitCodes.Failure, error.ExitCode);
        Assert.Equal("channel-configuration-unavailable", error.ErrorKind);
    }

    [Theory]
    [InlineData("facebook", "pages", false)]
    [InlineData("facebook", "pages", true)]
    [InlineData("line", "lineRegistrations", false)]
    [InlineData("line", "lineRegistrations", true)]
    public void Collections_EmptyReplacementOrFinalRemovalIsAUsageError(string channel, string parameterName, bool removeLast)
    {
        var request = UpdateRequest(channel, removeLast ? [] : [$"{parameterName}=[]"]) with
        {
            RemovePage = removeLast && channel == "facebook" ? ["id=one"] : [],
            RemoveRegistration = removeLast && channel == "line" ? ["generatedId=one"] : [],
        };
        var existing = Snapshot(channel == "facebook"
            ? """{"properties":{"properties":{"pages":[{"id":"one"}]}}}"""
            : """{"properties":{"properties":{"lineRegistrations":[{"generatedId":"one"}]}}}""");
        var error = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.BuildUpdate(DeploymentChannelPlan.ValidateUpdate(request), existing, BotResourceId, null));

        Assert.Equal($"Parameter '{parameterName}' requires at least one item.", error.Message);
        Assert.Equal(ExitCodes.Usage, error.ExitCode);
    }

    [Theory]
    [InlineData("directline", "sites", "site")]
    [InlineData("webchat", "sites", "site")]
    [InlineData("facebook", "pages", "page")]
    [InlineData("line", "lineRegistrations", "registration")]
    public void Collections_InvalidJsonUsesConsistentMessagesWithoutEchoingInput(
        string channel, string parameter, string itemName)
    {
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest(channel, parameter + """=[{"value":"SYNTHETIC_SECRET"}""")));

        Assert.Equal($"Parameter '{parameter}' must be a JSON array of {itemName} objects.", error.Message);
    }

    [Theory]
    [InlineData("directline", """sites=[{"siteName":"one","SITENAME":"SYNTHETIC_SECRET"}]""", "sites[].siteName")]
    [InlineData("facebook", """pages=[{"id":"one","ID":"SYNTHETIC_SECRET"}]""", "pages[].id")]
    [InlineData("line", """lineRegistrations=[{"generatedId":"one","GENERATEDID":"SYNTHETIC_SECRET"}]""", "lineRegistrations[].generatedId")]
    public void Collections_DuplicateFieldsUseCanonicalPathsWithoutEchoingValues(
        string channel, string assignment, string parameterPath)
    {
        var error = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel, assignment)));

        Assert.Equal($"Parameter '{parameterPath}' was specified more than once.", error.Message);
    }

    [Theory]
    [InlineData("facebook", """pages=[{"id":"one","accessToken":null}]""", "pages[].accessToken")]
    [InlineData("line", """lineRegistrations=[{"generatedId":"one","channelSecret":""}]""", "lineRegistrations[].channelSecret")]
    public void StringCollections_ClearingErrorsIdentifyField(
        string channel, string assignment, string parameterPath)
    {
        var error = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel, assignment)));

        Assert.Equal($"Parameter '{parameterPath}' for '{channel}' cannot be cleared.", error.Message);
    }

    [Theory]
    [InlineData("facebook", """pages=[{"id":"one","accessToken":false}]""", "pages[].accessToken")]
    [InlineData("line", """lineRegistrations=[{"generatedId":"one","channelSecret":42}]""", "lineRegistrations[].channelSecret")]
    public void StringCollections_InvalidValuesUseConsistentFieldDiagnostics(
        string channel, string assignment, string parameterPath)
    {
        var error = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel, assignment)));

        Assert.Equal($"Parameter '{parameterPath}' for '{channel}' must be a nonempty string.", error.Message);
    }

    [Theory]
    [InlineData("facebook", true)]
    [InlineData("facebook", false)]
    [InlineData("line", true)]
    [InlineData("line", false)]
    public void Collections_ExplicitMaskTextIsSentOnCreateAndReplacement(string channel, bool create)
    {
        var collection = channel == "facebook" ? "pages" : "lineRegistrations";
        var assignment = channel == "facebook"
            ? """pages=[{"id":"***","accessToken":"***"}]"""
            : """lineRegistrations=[{"channelSecret":"***","channelAccessToken":"***"}]""";
        string[] parameters = channel == "facebook" ? ["appId=***", "appSecret=***", assignment] : [assignment];
        var write = create
            ? DeploymentChannelPlan.BuildCreate(DeploymentChannelPlan.ValidateCreate(CreateRequest(channel, parameters)), BotResourceId, null)
            : DeploymentChannelPlan.BuildUpdate(DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel, parameters)), Snapshot(), BotResourceId, null);

        var item = Assert.Single(write.Parameters[collection].EnumerateArray());
        Assert.Equal(2, item.EnumerateObject().Count());
        Assert.All(item.EnumerateObject(), field => Assert.Equal("***", field.Value.GetString()));
    }

    [Theory]
    [InlineData("facebook")]
    [InlineData("line")]
    public void Collections_MaskTextSelectorsAndExplicitCredentialsDoNotReplayOtherSecrets(string channel)
    {
        var existing = Snapshot("""
            {"properties":{"properties":{
              "pages":[{"id":"***","accessToken":"***"},{"id":"two","accessToken":"***"}],
              "lineRegistrations":[{"generatedId":"***","channelSecret":"***","channelAccessToken":"***"},
                                   {"generatedId":"two","channelSecret":"***","channelAccessToken":"***"}]
            }}}
            """);
        var request = UpdateRequest(channel) with
        {
            SetPage = channel == "facebook" ? ["id=***", "accessToken=***"] : [],
            SetRegistration = channel == "line" ? ["generatedId=***", "channelSecret=***"] : [],
        };
        var write = DeploymentChannelPlan.BuildUpdate(DeploymentChannelPlan.ValidateUpdate(request), existing, BotResourceId, null);
        var items = write.Parameters[channel == "facebook" ? "pages" : "lineRegistrations"];

        Assert.Equal("***", items[0].GetProperty(channel == "facebook" ? "id" : "generatedId").GetString());
        Assert.Equal("***", items[0].GetProperty(channel == "facebook" ? "accessToken" : "channelSecret").GetString());
        Assert.Equal(2, items[0].EnumerateObject().Count());
        Assert.Single(items[1].EnumerateObject());
    }

    [Theory]
    [InlineData("page", "--add-page")]
    [InlineData("site", "--add-site")]
    [InlineData("registration", "--add-registration")]
    public void Collections_WrongChannelIdentifiesExactOption(string collection, string optionName)
    {
        var request = UpdateRequest("slack") with
        {
            AddPage = collection == "page" ? ["id=one", "accessToken=SYNTHETIC_SECRET"] : [],
            AddSite = collection == "site" ? ["siteName=one"] : [],
            AddRegistration = collection == "registration" ? ["channelSecret=SYNTHETIC_SECRET"] : [],
        };
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(request));

        Assert.Equal($"Option '{optionName}' is not valid for 'slack' channel update.", error.Message);
    }

    [Fact]
    public void Line_AddReadonlySelectorReportsActualUpdateOperation()
    {
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("line") with
            {
                AddRegistration = ["generatedId=one", "channelSecret=SYNTHETIC_SECRET", "channelAccessToken=SYNTHETIC_TOKEN"],
            }));

        Assert.Equal("Parameter 'generatedId' is misspelled or not valid for 'line' channel update.", error.Message);
    }

    private const string DeploymentId = "11111111-1111-1111-1111-111111111111";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateCreate_MissingOrBlankChannel_IdentifiesOption(string? channel)
    {
        var request = CreateRequest("msteams") with { ChannelName = channel! };
        var ex = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(request));
        Assert.StartsWith("--channel ", ex.Message);
    }

    [Fact]
    public void Telegram_TokenIsRequiredOnCreateButCanBeReplacedOrOmittedOnUpdate()
    {
        var error = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(CreateRequest("telegram", "isEnabled=false")));
        Assert.Contains("accessToken", error.Message);

        var create = DeploymentChannelPlan.BuildCreate(DeploymentChannelPlan.ValidateCreate(CreateRequest("telegram",
            "isEnabled=false", "accessToken=synthetic-token")), BotResourceId, null);
        Assert.Equal("TelegramChannel", create.ChannelArmName);
        Assert.Equal(2, create.Parameters.Count);
        Assert.Equal("synthetic-token", create.Parameters["accessToken"].GetString());
        Assert.False(create.Parameters["isEnabled"].GetBoolean());
        Assert.DoesNotContain("isValidated", create.Parameters);

        var existing = Snapshot("""
            {"properties":{"properties":{"accessToken":"***","isEnabled":true,"isValidated":true}}}
            """);
        var replacement = DeploymentChannelPlan.BuildUpdate(
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("telegram", "accessToken=synthetic-new-token")),
            existing, BotResourceId, null);
        Assert.Equal("synthetic-new-token", replacement.Parameters["accessToken"].GetString());
        Assert.True(replacement.Parameters["isEnabled"].GetBoolean());
        Assert.True(replacement.Parameters["isValidated"].GetBoolean());

        var sparse = DeploymentChannelPlan.BuildUpdate(
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("telegram", "isEnabled=false")),
            existing, BotResourceId, null);
        Assert.False(sparse.Parameters["isEnabled"].GetBoolean());
        Assert.True(sparse.Parameters["isValidated"].GetBoolean());
        Assert.DoesNotContain("accessToken", sparse.Parameters);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Telegram_TokenCannotBeBlankOnCreateOrClearedOnUpdate(string value)
    {
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(
            CreateRequest("telegram", "isEnabled=false", "accessToken=" + value)));
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("telegram", "accessToken=" + value)));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Telegram_RejectsUserValidationStateOnCreateAndUpdate(string value)
    {
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(CreateRequest("telegram",
            "isEnabled=false", "accessToken=synthetic-token", "isValidated=" + value)));
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("telegram", "isValidated=" + value)));
    }

    [Fact]
    public void Sms_CreateRequiresCredentialsAndPhoneButDoesNotInjectEnabledOrValidated()
    {
        var defaults = DeploymentChannelPlan.BuildCreate(DeploymentChannelPlan.ValidateCreate(CreateRequest("sms",
            "ACCOUNTSID=synthetic-sid", "authToken=synthetic-token", "phone=+12025550123")), BotResourceId, null);
        Assert.Equal("SmsChannel", defaults.ChannelArmName);
        Assert.Equal(3, defaults.Parameters.Count);
        Assert.Equal("synthetic-sid", defaults.Parameters["accountSID"].GetString());
        Assert.Equal("synthetic-token", defaults.Parameters["authToken"].GetString());
        Assert.Equal("+12025550123", defaults.Parameters["phone"].GetString());

        var disabled = DeploymentChannelPlan.BuildCreate(DeploymentChannelPlan.ValidateCreate(CreateRequest("sms",
            "accountSID=synthetic-sid", "authToken=synthetic-token", "phone=+12025550123", "isEnabled=false")), BotResourceId, null);
        Assert.Equal(4, disabled.Parameters.Count);
        Assert.False(disabled.Parameters["isEnabled"].GetBoolean());
        Assert.DoesNotContain("isValidated", disabled.Parameters);
    }

    [Theory]
    [InlineData("accountSID")]
    [InlineData("authToken")]
    [InlineData("phone")]
    public void Sms_CredentialsAndPhoneAreRequiredOnCreateAndCannotBeClearedOnUpdate(string name)
    {
        string[] fields = ["accountSID=synthetic-sid", "authToken=synthetic-token", "phone=+12025550123"];
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(
            CreateRequest("sms", fields.Where(field => !field.StartsWith(name + "=", StringComparison.Ordinal)).ToArray())));
        Assert.Contains(name, error.Message);
        Assert.DoesNotContain("synthetic-token", error.Message);

        foreach (var value in new[] { "", "   " })
            Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(UpdateRequest("sms", $"{name}={value}")));
    }

    [Fact]
    public void Sms_RejectsUserValidationStateOnCreateAndUpdate()
    {
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(CreateRequest("sms",
            "accountSID=synthetic-sid", "authToken=synthetic-token", "phone=+12025550123", "isValidated=false")));
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(UpdateRequest("sms", "isValidated=true")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("1")]
    public void Sms_OptionalEnabledRequiresAnExplicitBooleanWhenSupplied(string value)
    {
        Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("sms", "isEnabled=" + value)));
    }

    [Theory]
    [InlineData("\"***\"")]
    [InlineData("null")]
    public void Sms_SparseUpdatePreservesSafeStateAndOmitsHiddenToken(string token)
    {
        var existing = Snapshot("""
            {"properties":{"properties":{"accountSID":"live-sid","phone":"+12025550123",
              "isEnabled":true,"isValidated":true,"unexpected":"ignore","authToken":
            """ + token + "}}}");
        var update = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("sms", "isEnabled=false"));
        Assert.Single(update.NormalizedParameters);
        var write = DeploymentChannelPlan.BuildUpdate(update, existing, BotResourceId, null);

        Assert.Equal("live-sid", write.Parameters["accountSID"].GetString());
        Assert.Equal("+12025550123", write.Parameters["phone"].GetString());
        Assert.True(write.Parameters["isValidated"].GetBoolean());
        Assert.False(write.Parameters["isEnabled"].GetBoolean());
        Assert.DoesNotContain("authToken", write.Parameters);
        Assert.DoesNotContain("unexpected", write.Parameters);
    }

    [Fact]
    public void Sms_UpdateSendsExplicitCredentialsWithoutRequiringOmittedCreateFields()
    {
        var existing = Snapshot("""
            {"properties":{"properties":{"accountSID":"live-sid","phone":"+12025550123",
              "isEnabled":false,"isValidated":true,"authToken":"***"}}}
            """);
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("sms", "authToken=synthetic-new-token"));
        var write = DeploymentChannelPlan.BuildUpdate(plan, existing, BotResourceId, null);

        Assert.Equal("synthetic-new-token", write.Parameters["authToken"].GetString());
        Assert.Equal("live-sid", write.Parameters["accountSID"].GetString());
        Assert.Equal("+12025550123", write.Parameters["phone"].GetString());
        Assert.False(write.Parameters["isEnabled"].GetBoolean());
    }

    [Fact]
    public void Line_AllFieldsCreateAndSelectorUpdatesFollowRequiredSemantics()
    {
        var created = DeploymentChannelPlan.BuildCreate(DeploymentChannelPlan.ValidateCreate(CreateRequest("line",
            """lineRegistrations=[{"CHANNELSECRET":"synthetic=secret","channelAccessToken":"synthetic-token"}]""")), BotResourceId, null);
        var item = Assert.Single(created.Parameters["lineRegistrations"].EnumerateArray());
        Assert.Equal("synthetic=secret", item.GetProperty("channelSecret").GetString());
        Assert.Equal("synthetic-token", item.GetProperty("channelAccessToken").GetString());
        Assert.False(item.TryGetProperty("generatedId", out _));

        var changed = LineUpdate(UpdateRequest("line",
            """lineRegistrations=[{"GENERATEDID":"two"},{"generatedId":"one","channelSecret":"new-secret"},{"channelSecret":"third-secret","channelAccessToken":"third-token"}]"""));
        var items = changed.Parameters["lineRegistrations"];
        Assert.Equal(3, items.GetArrayLength());
        Assert.Equal("two", items[0].GetProperty("generatedId").GetString());
        Assert.Single(items[0].EnumerateObject());
        Assert.Equal("new-secret", items[1].GetProperty("channelSecret").GetString());
        Assert.False(items[1].TryGetProperty("channelAccessToken", out _));
        Assert.Equal("https://callback.example/one", changed.Parameters["callbackUrl"].GetString());
        Assert.True(changed.Parameters["isValidated"].GetBoolean());
        Assert.DoesNotContain("etag", changed.Parameters);
        Assert.DoesNotContain("unknown", changed.Parameters);
        Assert.DoesNotContain("***", JsonSerializer.Serialize(changed.Parameters));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("[{}]")]
    [InlineData("""[{"generatedId":"one","GENERATEDID":"two"}]""")]
    [InlineData("""[{"generatedId":"one"},{"generatedId":"one"}]""")]
    [InlineData("""[{"generatedId":null}]""")]
    [InlineData("""[{"generatedId":"one","channelSecret":null}]""")]
    [InlineData("""[{"generatedId":"one","channelSecret":""}]""")]
    [InlineData("""[{"generatedId":"one","channelAccessToken":"   "}]""")]
    [InlineData("""[{"generatedId":"one","channelSecret":true}]""")]
    [InlineData("""[{"channelSecret":"SYNTHETIC_SECRET"}]""")]
    [InlineData("""[{"channelAccessToken":"SYNTHETIC_SECRET"}]""")]
    [InlineData("""[{"unknown":"SYNTHETIC_SECRET"}]""")]
    [InlineData("""[{"channelSecret":"SYNTHETIC_SECRET"}""")]
    public void Line_RejectsInvalidArraysWithoutEchoingCredentials(string array)
    {
        var error = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("line", "lineRegistrations=" + array)));
        Assert.DoesNotContain("SYNTHETIC_SECRET", error.Message);
    }

    [Fact]
    public void Line_RejectsSelectorsOnCreateAndRequiresNewCredentials()
    {
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(CreateRequest("line")));
        foreach (var item in new[] { "{}", """{"channelSecret":"secret"}""", """{"channelAccessToken":"token"}""",
            """{"generatedId":"one","channelSecret":"secret","channelAccessToken":"token"}""" })
        {
            Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(
                CreateRequest("line", "lineRegistrations=[" + item + "]")));
        }

        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(UpdateRequest("line", "lineRegistrations=")));
        foreach (var field in new[] { "callbackUrl", "isValidated", "generatedId", "channelSecret", "channelAccessToken" })
        {
            Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(UpdateRequest("line", field + "=synthetic")));
        }
    }

    [Fact]
    public void Line_TargetedOperationsPreserveUnrelatedIdentitiesWithoutCredentialReplay()
    {
        var added = LineUpdate(UpdateRequest("line") with { AddRegistration = ["channelSecret=new-secret", "channelAccessToken=new-token"] });
        Assert.Equal(3, added.Parameters["lineRegistrations"].GetArrayLength());
        Assert.Single(added.Parameters["lineRegistrations"][0].EnumerateObject());
        Assert.Equal("new-secret", added.Parameters["lineRegistrations"][2].GetProperty("channelSecret").GetString());
        var set = LineUpdate(UpdateRequest("line") with { SetRegistration = ["generatedId=two", "channelAccessToken=changed"] });
        Assert.Equal("one", set.Parameters["lineRegistrations"][0].GetProperty("generatedId").GetString());
        Assert.Single(set.Parameters["lineRegistrations"][0].EnumerateObject());
        Assert.Equal("changed", set.Parameters["lineRegistrations"][1].GetProperty("channelAccessToken").GetString());
        Assert.False(set.Parameters["lineRegistrations"][1].TryGetProperty("channelSecret", out _));
        var removed = LineUpdate(UpdateRequest("line") with { RemoveRegistration = ["generatedId=one"] });
        Assert.Equal("two", Assert.Single(removed.Parameters["lineRegistrations"].EnumerateArray()).GetProperty("generatedId").GetString());
    }

    [Fact]
    public void Line_RejectsMixedModesUnknownSelectorsAndMeaninglessSets()
    {
        DeploymentChannelUpdateRequest[] requests =
        [
            UpdateRequest("line", """lineRegistrations=[{"generatedId":"missing"}]"""),
            UpdateRequest("line") with { AddRegistration = ["generatedId=one", "channelSecret=secret", "channelAccessToken=token"] },
            UpdateRequest("line") with { AddRegistration = ["channelSecret=secret"] },
            UpdateRequest("line") with { SetRegistration = ["generatedId=one"] },
            UpdateRequest("line") with { SetRegistration = ["channelSecret=secret"] },
            UpdateRequest("line") with { SetRegistration = ["generatedId=missing", "channelSecret=secret"] },
            UpdateRequest("line") with { RemoveRegistration = ["generatedId=missing"] },
            UpdateRequest("line") with { RemoveRegistration = ["generatedId=one", "channelSecret=secret"] },
            UpdateRequest("line") with { RemoveRegistration = ["generatedId=one"], AddRegistration = ["channelSecret=secret", "channelAccessToken=token"] },
            UpdateRequest("line", """lineRegistrations=[{"generatedId":"one"}]""") with { RemoveRegistration = ["generatedId=two"] },
            UpdateRequest("facebook") with { RemoveRegistration = ["generatedId=one"] },
            UpdateRequest("line") with { AddPage = ["id=one", "accessToken=token"] },
            UpdateRequest("line") with { AddSite = ["siteName=one"] },
        ];
        foreach (var request in requests)
        {
            Assert.Throws<UsageException>(() => LineUpdate(request));
        }
        var remove = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("line") with { RemoveRegistration = ["generatedId=one"] });
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.BuildUpdate(remove,
            Snapshot("""{"properties":{"properties":{"lineRegistrations":[{"generatedId":"one"}]}}}"""), BotResourceId, null));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("""[{"generatedId":"one"},{"generatedId":"one"}]""")]
    [InlineData("""[{"generatedId":"   "}]""")]
    public void Line_AmbiguousStateRequiresExplicitCompleteReplacement(string registrations)
    {
        var existing = Snapshot("{\"properties\":{\"properties\":{\"lineRegistrations\":" + registrations + "}}}");
        var targeted = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("line") with { RemoveRegistration = ["generatedId=one"] });
        var error = Assert.Throws<DeploymentException>(() => DeploymentChannelPlan.BuildUpdate(targeted, existing, BotResourceId, null));
        Assert.Equal("The channel response does not contain a usable configuration.", error.Message);
        Assert.Equal(ExitCodes.Failure, error.ExitCode);
        var replacement = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("line",
            """lineRegistrations=[{"channelSecret":"secret","channelAccessToken":"token"}]"""));
        Assert.Single(DeploymentChannelPlan.BuildUpdate(replacement, existing, BotResourceId, null).Parameters["lineRegistrations"].EnumerateArray());
    }

    private static BotServiceChannelWriteRequest LineUpdate(DeploymentChannelUpdateRequest request) =>
        DeploymentChannelPlan.BuildUpdate(DeploymentChannelPlan.ValidateUpdate(request), Snapshot("""
            {"properties":{"properties":{"callbackUrl":"https://callback.example/one","isValidated":true,"etag":"ignore","unknown":true,
              "lineRegistrations":[{"generatedId":"one","channelSecret":"***","channelAccessToken":"***","unknown":"ignore"},
                                   {"generatedId":"two","channelSecret":"***","channelAccessToken":"***"}]}}}
            """), BotResourceId, null);

    [Fact]
    public void Facebook_AllParametersCreateAndUpdateUseTypedPagesAndPreserveSafeOutputs()
    {
        string[] fields = ["APPID=app-new", "isEnabled=false", "appSecret=synthetic-secret",
            """PAGES=[{"ID":"three","ACCESSTOKEN":"synthetic=token"}]"""];
        var create = DeploymentChannelPlan.BuildCreate(
            DeploymentChannelPlan.ValidateCreate(CreateRequest("facebook", fields)), BotResourceId, null);
        Assert.False(create.Parameters["isEnabled"].GetBoolean());
        Assert.Equal("three", Assert.Single(create.Parameters["pages"].EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("synthetic=token", create.Parameters["pages"][0].GetProperty("accessToken").GetString());
        Assert.Equal(4, create.Parameters.Count);

        var update = FacebookUpdate(UpdateRequest("facebook", fields));
        Assert.Equal("app-new", update.Parameters["appId"].GetString());
        Assert.Equal("synthetic-secret", update.Parameters["appSecret"].GetString());
        Assert.False(update.Parameters["isEnabled"].GetBoolean());
        Assert.Equal("https://callback.example", update.Parameters["callbackUrl"].GetString());
        Assert.Single(update.Parameters["pages"].EnumerateArray());
        Assert.DoesNotContain("verifyToken", update.Parameters);
        Assert.DoesNotContain("etag", update.Parameters);
        Assert.DoesNotContain("unknown", update.Parameters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public void Facebook_CreateOmitsEnabledUnlessExplicitlySupplied(string? enabled)
    {
        string[] fields = ["appId=app", "appSecret=secret", """pages=[{"id":"one","accessToken":"token"}]"""];
        var plan = DeploymentChannelPlan.ValidateCreate(CreateRequest("facebook",
            enabled is null ? fields : [.. fields, "isEnabled=" + enabled]));
        var write = DeploymentChannelPlan.BuildCreate(plan, BotResourceId, null);

        Assert.Equal(enabled is not null, write.Parameters.ContainsKey("isEnabled"));
        if (enabled is not null)
            Assert.Equal(bool.Parse(enabled), write.Parameters["isEnabled"].GetBoolean());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[1]")]
    [InlineData("""[{"id":true}]""")]
    [InlineData("""[{"id":null}]""")]
    [InlineData("""[{"id":""}]""")]
    [InlineData("""[{"accessToken":"SYNTHETIC_TOKEN"}]""")]
    [InlineData("""[{"id":"one","ID":"two"}]""")]
    [InlineData("""[{"id":"one"},{"id":"one"}]""")]
    [InlineData("""[{"id":"one","accessToken":null}]""")]
    [InlineData("""[{"id":"one","accessToken":false}]""")]
    [InlineData("""[{"id":"one","accessToken":""}]""")]
    [InlineData("""[{"id":"one","unknown":"SYNTHETIC_TOKEN"}]""")]
    [InlineData("""[{"id":"one","accessToken":"SYNTHETIC_TOKEN"}""")]
    public void Facebook_RejectsMalformedArraysWithoutEchoingSecrets(string value)
    {
        var create = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(
            CreateRequest("facebook", "appId=app", "isEnabled=false", "appSecret=synthetic-secret", $"pages={value}")));
        var update = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("facebook", $"pages={value}")));
        Assert.DoesNotContain("SYNTHETIC_TOKEN", create.Message);
        Assert.DoesNotContain("SYNTHETIC_TOKEN", update.Message);
    }

    [Theory]
    [InlineData("verifyToken=synthetic")]
    [InlineData("callbackUrl=https://example.test")]
    [InlineData("id=one")]
    [InlineData("accessToken=synthetic")]
    [InlineData("pages.id=one")]
    public void Facebook_RejectsReadonlyAndFlatPageAssignments(string assignment)
    {
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(
            CreateRequest("facebook", "appId=app", "isEnabled=false", assignment)));
        Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("facebook", assignment)));
    }

    [Fact]
    public void Facebook_TargetedOperationsPreserveUnrelatedPagesAndNeverReplayMasks()
    {
        var added = FacebookUpdate(UpdateRequest("facebook") with { AddPage = ["id=three", "accessToken=new=token"] });
        Assert.Equal(["one", "two", "three"], PageIds(added));
        Assert.Equal("new=token", added.Parameters["pages"][2].GetProperty("accessToken").GetString());
        Assert.DoesNotContain("appSecret", added.Parameters);
        Assert.False(added.Parameters["pages"][0].TryGetProperty("accessToken", out _));
        Assert.False(added.Parameters["pages"][0].TryGetProperty("unknown", out _));

        var set = FacebookUpdate(UpdateRequest("facebook") with { SetPage = ["id=two", "accessToken=replacement"] });
        Assert.Equal(["one", "two"], PageIds(set));
        Assert.Equal("replacement", set.Parameters["pages"][1].GetProperty("accessToken").GetString());
        Assert.False(set.Parameters["pages"][0].TryGetProperty("accessToken", out _));
        Assert.Equal("app-old", set.Parameters["appId"].GetString());
        Assert.True(set.Parameters["isEnabled"].GetBoolean());

        var removed = FacebookUpdate(UpdateRequest("facebook") with { RemovePage = ["id=one"] });
        Assert.Equal(["two"], PageIds(removed));
        var scalar = FacebookUpdate(UpdateRequest("facebook", "isEnabled=false"));
        Assert.Equal(["one", "two"], PageIds(scalar));
        Assert.False(scalar.Parameters["isEnabled"].GetBoolean());
    }

    [Fact]
    public void Facebook_ReplacementUsesExactMembershipAndRequiresTokensForNewIds()
    {
        var replaced = FacebookUpdate(UpdateRequest("facebook",
            """pages=[{"id":"two"},{"id":"three","accessToken":"new-token"}]"""));
        Assert.Equal(["two", "three"], PageIds(replaced));
        Assert.False(replaced.Parameters["pages"][0].TryGetProperty("accessToken", out _));
        Assert.Throws<UsageException>(() => FacebookUpdate(UpdateRequest("facebook", """pages=[{"id":"three"}]""")));
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(
            CreateRequest("facebook", "appId=app", "isEnabled=false", "appSecret=synthetic-secret", """pages=[{"id":"one"}]""")));
    }

    [Theory]
    [InlineData("appSecret")]
    [InlineData("pages")]
    public void Facebook_CreateRequiresCredentialsAndPages(string omitted)
    {
        string[] fields = ["appId=app", "isEnabled=false", "appSecret=synthetic-secret",
            """pages=[{"id":"one","accessToken":"synthetic-token"}]"""];
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(
            CreateRequest("facebook", fields.Where(field => !field.StartsWith(omitted + "=", StringComparison.Ordinal)).ToArray())));

        Assert.Contains(omitted, error.Message);
        Assert.DoesNotContain("synthetic-secret", error.Message);
        Assert.DoesNotContain("synthetic-token", error.Message);
    }

    [Theory]
    [InlineData("appSecret=")]
    [InlineData("appSecret=   ")]
    [InlineData("pages=")]
    public void Facebook_UpdateRejectsClearingRequiredRootValues(string assignment)
    {
        var error = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("facebook", assignment)));

        Assert.Contains("cannot be cleared", error.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Facebook_PageTokenCannotBeClearedButCanBeOmittedOrReplaced(string token)
    {
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("facebook", "pages=[{\"id\":\"one\",\"accessToken\":" + token + "}]")));
        Assert.Equal("Parameter 'pages[].accessToken' for 'facebook' cannot be cleared.", error.Message);

        var preserved = FacebookUpdate(UpdateRequest("facebook", """pages=[{"id":"one"}]"""));
        Assert.False(preserved.Parameters["pages"][0].TryGetProperty("accessToken", out _));
        var replaced = FacebookUpdate(UpdateRequest("facebook", """pages=[{"id":"one","accessToken":"replacement"}]"""));
        Assert.Equal("replacement", replaced.Parameters["pages"][0].GetProperty("accessToken").GetString());
    }

    [Fact]
    public void Facebook_SetRequiresAWritableChangeRatherThanRequiringUpdateCredentials()
    {
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("facebook") with { SetPage = ["id=one"] }));

        Assert.Contains("at least one writable change", error.Message);
        Assert.Contains("id selector", error.Message);
    }

    [Fact]
    public void Facebook_RejectsInvalidModesSelectorsAndFinalPageRemoval()
    {
        DeploymentChannelUpdateRequest[] invalid =
        [
            UpdateRequest("facebook", "isEnabled=false") with { AddPage = ["id=three", "accessToken=token"] },
            UpdateRequest("facebook") with { AddPage = ["id=three", "accessToken=token"], RemovePage = ["id=one"] },
            UpdateRequest("facebook") with { AddPage = ["id=three", "accessToken=token"], AddSite = ["siteName=site"] },
            UpdateRequest("facebook") with { AddPage = ["id=one", "accessToken=token"] },
            UpdateRequest("facebook") with { AddPage = ["id=three"] },
            UpdateRequest("facebook") with { SetPage = ["id=two"] },
            UpdateRequest("facebook") with { SetPage = ["accessToken=token"] },
            UpdateRequest("facebook") with { SetPage = ["id=missing", "accessToken=token"] },
            UpdateRequest("facebook") with { RemovePage = ["id=missing"] },
            UpdateRequest("facebook") with { RemovePage = ["id=one", "accessToken=token"] },
            UpdateRequest("facebook") with { SetPage = ["id=one", "id=two", "accessToken=token"] },
            UpdateRequest("msteams") with { AddPage = ["id=one", "accessToken=token"] },
            UpdateRequest("webchat") with { RemovePage = ["id=one"] },
        ];
        foreach (var request in invalid)
        {
            Assert.Throws<UsageException>(() => FacebookUpdate(request));
        }

        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("facebook") with { RemovePage = ["id=one"] });
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.BuildUpdate(plan,
            Snapshot("""{"properties":{"properties":{"pages":[{"id":"one","accessToken":"***"}]}}}"""), BotResourceId, null));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("""[{"accessToken":"***"}]""")]
    [InlineData("""[{"id":"one"},{"id":"one"}]""")]
    [InlineData("""[{"id":"   "}]""")]
    public void Facebook_UnreadableLivePagesBlockReuseButAllowExplicitReplacement(string pages)
    {
        var existing = Snapshot("{\"properties\":{\"properties\":{\"isEnabled\":false,\"appId\":\"app\",\"pages\":" + pages + "}}}");
        var scalar = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("facebook", "isEnabled=false"));
        var error = Assert.Throws<DeploymentException>(() => DeploymentChannelPlan.BuildUpdate(scalar, existing, BotResourceId, null));
        Assert.Equal("The channel response does not contain a usable configuration.", error.Message);
        Assert.Equal(ExitCodes.Failure, error.ExitCode);
        var replacement = DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("facebook", """pages=[{"id":"new","accessToken":"fresh"}]"""));
        Assert.Equal(["new"], PageIds(DeploymentChannelPlan.BuildUpdate(replacement, existing, BotResourceId, null)));
    }

    private static BotServiceChannelWriteRequest FacebookUpdate(DeploymentChannelUpdateRequest request) =>
        DeploymentChannelPlan.BuildUpdate(DeploymentChannelPlan.ValidateUpdate(request),
            Snapshot("""
                {"properties":{"properties":{"appId":"app-old","appSecret":"***","isEnabled":true,
                  "callbackUrl":"https://callback.example","verifyToken":"***","etag":"ignore","unknown":true,
                  "pages":[{"id":"one","accessToken":"***","unknown":"ignore"},{"id":"two","accessToken":"***"}]}}}
                """), BotResourceId, null);

    private static string[] PageIds(BotServiceChannelWriteRequest write) =>
        write.Parameters["pages"].EnumerateArray().Select(page => page.GetProperty("id").GetString()!).ToArray();

    [Theory]
    [InlineData("VerificationToken")]
    [InlineData("SigningSecret")]
    public void ValidateCreate_NormalizesCaseInsensitiveSlackParametersWithoutDefaultingEnabled(string credential)
    {
        var request = CreateRequest(
            "slack",
            "CLIENTID=client-1",
            "clientsecret=secret-1",
            $"{credential}=token-1");
        var plan = DeploymentChannelPlan.ValidateCreate(request);
        var upsert = DeploymentChannelPlan.BuildCreate(plan, BotResourceId, "tenant-1");
        var parameters = Parameters(upsert);

        Assert.Equal("slack", plan.Request.ChannelName);
        Assert.Equal("SlackChannel", plan.ChannelDefinition.ArmName);
        Assert.Equal(BotResourceId, upsert.BotResourceId);
        Assert.Equal("client-1", parameters["clientId"].GetString());
        Assert.Equal("secret-1", parameters["clientSecret"].GetString());
        Assert.DoesNotContain("isEnabled", parameters);
        Assert.True(parameters["registerBeforeOAuthFlow"].GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("chat:write")]
    public void SlackCreate_AppliesArmDefaultScopesOnlyWhenOmitted(string? scopes)
    {
        string[] assignments = ["clientId=client", "clientSecret=secret", "signingSecret=sign",
            .. (scopes is null ? [] : new[] { "scopes=" + scopes })];
        var plan = DeploymentChannelPlan.ValidateCreate(CreateRequest("slack", assignments));
        var write = DeploymentChannelPlan.BuildCreate(plan, BotResourceId, null);

        const string defaultScopes = "channels:history,channels:read,chat:write,dnd:read,emoji:read,files:read,files:write," +
            "groups:history,groups:read,im:history,im:read,im:write,mpim:history,mpim:read,mpim:write," +
            "pins:read,pins:write,reactions:read,reactions:write,team:read,usergroups:read," +
            "users:read,users:write,users:read.email";
        Assert.Equal(scopes ?? defaultScopes, write.Parameters["scopes"].GetString());
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void SlackCreate_PreservesExplicitEnabledValues(string enabled)
    {
        var plan = DeploymentChannelPlan.ValidateCreate(CreateRequest("slack",
            "clientId=client", "clientSecret=secret", "signingSecret=sign", "isEnabled=" + enabled));
        var write = DeploymentChannelPlan.BuildCreate(plan, BotResourceId, null);

        Assert.Equal(bool.Parse(enabled), write.Parameters["isEnabled"].GetBoolean());
    }

    [Fact]
    public void SlackCreate_RequiresAtLeastOneSigningOrVerificationSecret()
    {
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(
            CreateRequest("slack", "clientId=client", "clientSecret=secret", "scopes=chat:write")));
        Assert.Contains("at least one of signingSecret or verificationToken", error.Message);
    }

    [Theory]
    [InlineData("clientId")]
    [InlineData("clientSecret")]
    [InlineData("signingSecret")]
    [InlineData("verificationToken")]
    [InlineData("scopes")]
    [InlineData("landingPageUrl")]
    public void SlackStrings_CannotBeCleared(string field)
    {
        foreach (var value in new[] { "", "   " })
        {
            var error = Assert.Throws<UsageException>(() =>
                DeploymentChannelPlan.ValidateUpdate(UpdateRequest("slack", $"{field}={value}")));
            Assert.Contains("cannot be cleared", error.Message);
        }
    }

    [Theory]
    [InlineData("redirectAction")]
    [InlineData("lastSubmissionId")]
    [InlineData("isValidated")]
    [InlineData("registerBeforeOAuthFlow")]
    public void SlackReadonlyFields_AreRejectedForCreateAndUpdate(string field)
    {
        foreach (var create in new[] { true, false })
        {
            var error = Assert.Throws<UsageException>(() =>
            {
                if (create)
                    DeploymentChannelPlan.ValidateCreate(CreateRequest("slack", $"{field}=SYNTHETIC_VALUE"));
                else
                    DeploymentChannelPlan.ValidateUpdate(UpdateRequest("slack", $"{field}=SYNTHETIC_VALUE"));
            });
            Assert.Contains("not valid", error.Message);
            Assert.DoesNotContain("SYNTHETIC_VALUE", error.Message);
        }
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void SlackRegistrationControl_IsReadonlyAndAlwaysSetInternally(string value)
    {
        var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(CreateRequest(
            "slack", "clientId=client", "clientSecret=secret", "signingSecret=sign", $"registerBeforeOAuthFlow={value}")));
        Assert.Contains("not valid", error.Message);
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("slack", "isEnabled=false", $"registerBeforeOAuthFlow={value}")));
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("slack", "isEnabled=false"));
        var write = DeploymentChannelPlan.BuildUpdate(plan,
            Snapshot("""{"properties":{"properties":{"isEnabled":true,"registerBeforeOAuthFlow":false}}}"""), BotResourceId, null);
        Assert.True(write.Parameters["registerBeforeOAuthFlow"].GetBoolean());
        Assert.False(write.Parameters["isEnabled"].GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Slack_ExplicitMaskTextIsSentForSecretsAndNonSecrets(bool create)
    {
        string[] assignments = ["clientId=***", "clientSecret=***", "signingSecret=***", "verificationToken=***", "scopes=***"];
        var write = create
            ? DeploymentChannelPlan.BuildCreate(DeploymentChannelPlan.ValidateCreate(CreateRequest("slack", assignments)), BotResourceId, null)
            : DeploymentChannelPlan.BuildUpdate(DeploymentChannelPlan.ValidateUpdate(UpdateRequest("slack", assignments)), Snapshot(), BotResourceId, null);

        foreach (var assignment in assignments)
            Assert.Equal("***", write.Parameters[assignment.Split('=', 2)[0]].GetString());
    }

    [Fact]
    public void Slack_ExistingMaskTextIsPreservedOnlyForNonSecrets()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("slack", "isEnabled=false"));
        var write = DeploymentChannelPlan.BuildUpdate(plan, Snapshot("""
            {"properties":{"properties":{
              "clientId":"***","scopes":"***","clientSecret":"***","signingSecret":"***","verificationToken":"***"
            }}}
            """), BotResourceId, null);

        Assert.Equal("***", write.Parameters["clientId"].GetString());
        Assert.Equal("***", write.Parameters["scopes"].GetString());
        Assert.DoesNotContain("clientSecret", write.Parameters);
        Assert.DoesNotContain("signingSecret", write.Parameters);
        Assert.DoesNotContain("verificationToken", write.Parameters);
    }

    [Fact]
    public void SlackUpdate_DefaultsRegistrationToTrueAndDoesNotReplayReadonlyFields()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("slack", "landingPageUrl=https://new.example"));
        var write = DeploymentChannelPlan.BuildUpdate(plan, Snapshot("""
            {"properties":{"properties":{
              "clientId":"existing-client","clientSecret":"***","verificationToken":"***","signingSecret":"***",
              "registerBeforeOAuthFlow":false,"IsValidated":false,"lastSubmissionId":"submission","redirectAction":"***",
              "scopes":"chat:write","isEnabled":true
            }}}
            """), BotResourceId, null);

        Assert.True(write.Parameters["registerBeforeOAuthFlow"].GetBoolean());
        Assert.Equal("existing-client", write.Parameters["clientId"].GetString());
        Assert.Equal("chat:write", write.Parameters["scopes"].GetString());
        foreach (var name in new[] { "clientSecret", "signingSecret", "verificationToken", "IsValidated", "lastSubmissionId", "redirectAction" })
            Assert.DoesNotContain(name, write.Parameters);
    }

    [Fact]
    public void ValidateCreate_MissingRequiredProperty_Throws()
    {
        var request = CreateRequest("slack", "clientId=client-1", "verificationToken=token-1");
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(request));

        Assert.Contains("clientSecret", ex.Message);
    }

    [Theory]
    [InlineData("lines")]
    [MemberData(nameof(DeploymentChannelCatalogTests.UnsupportedChannelNames), MemberType = typeof(DeploymentChannelCatalogTests))]
    public void ValidateCreate_UnsupportedChannel_Throws(string channelName)
    {
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(CreateRequest(channelName)));

        Assert.Contains($"Channel '{channelName}' is not supported.", ex.Message);
        Assert.Contains("Supported values are:", ex.Message);
        Assert.DoesNotContain("--channel is not supported", ex.Message);
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

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("   ")]
    [InlineData("0")]
    public void ValidateUpdate_OptionalBoolean_RequiresBooleanValue(string value)
    {
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("sms", $"isEnabled={value}")));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void ValidateUpdate_OptionalBoolean_AcceptsExplicitValueWithoutAddingOmittedFields(string value, bool expected)
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("sms", $"isEnabled={value}"));
        var write = DeploymentChannelPlan.BuildUpdate(plan, Snapshot("""{"properties":{"properties":{}}}"""), BotResourceId, null);

        Assert.Equal(expected, Assert.Single(Parameters(write)).Value.GetBoolean());
    }

    [Theory]
    [InlineData("acceptedTerms")]
    [InlineData("callingWebhook")]
    [InlineData("deploymentEnvironment")]
    [InlineData("enableCalling")]
    [InlineData("incomingCallRoute")]
    public void ValidateUpdate_MsTeamsOptionalProperties_RejectClearing(string name)
    {
        foreach (var value in new[] { "", "   " })
        {
            var error = Assert.Throws<UsageException>(() =>
                DeploymentChannelPlan.ValidateUpdate(UpdateRequest("msteams", $"{name}={value}")));

            Assert.Contains("cannot be cleared", error.Message);
            Assert.DoesNotContain("is required", error.Message);
        }
    }

    [Theory]
    [InlineData("msteams", "callingWebhook")]
    [InlineData("msteams", "incomingCallRoute")]
    [InlineData("slack", "signingSecret")]
    [InlineData("slack", "scopes")]
    [InlineData("telegram", "accessToken")]
    public void ValidateUpdate_LiteralNullIsStringTextNotAClearingRequest(string channel, string field)
    {
        foreach (var value in new[] { "null", "NULL" })
        {
            var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel, $"{field}={value}"));
            var write = DeploymentChannelPlan.BuildUpdate(plan, Snapshot(), BotResourceId, null);

            Assert.Equal(value, write.Parameters[field].GetString());
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public void ValidateMsTeams_RejectsServiceManagedEnabledAndInvalidEnvironment(string operation)
    {
        foreach (var assignment in new[] { "isEnabled=false", "deploymentEnvironment=UNSUPPORTED_ENVIRONMENT" })
        {
            var error = Assert.Throws<UsageException>(() =>
            {
                if (operation == "create")
                    DeploymentChannelPlan.ValidateCreate(CreateRequest("msteams", assignment));
                else
                    DeploymentChannelPlan.ValidateUpdate(UpdateRequest("msteams", assignment));
            });
            Assert.DoesNotContain("UNSUPPORTED_ENVIRONMENT", error.Message);
            Assert.Contains(assignment.StartsWith("isEnabled", StringComparison.Ordinal)
                ? "not valid" : "CommercialDeployment, GCCModerateDeployment", error.Message);
        }
    }

    [Fact]
    public void BuildUpdate_MsTeams_NormalizesEnvironmentWithoutInjectingCreateDefaults()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("msteams", "deploymentEnvironment=gccmoderatedeployment"));
        var upsert = DeploymentChannelPlan.BuildUpdate(plan, Snapshot("""{"properties":{"properties":{}}}"""), BotResourceId, "tenant-1");

        Assert.Single(Parameters(upsert));
        Assert.Equal("GCCModerateDeployment", Parameters(upsert)["deploymentEnvironment"].GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void BuildUpdate_MsTeams_RejectsOnlyRevocationOfAcceptedTerms(bool existing, bool requested)
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("msteams", $"acceptedTerms={requested}"));
        var snapshot = Snapshot(JsonSerializer.Serialize(new { properties = new { properties = new { acceptedTerms = existing } } }));

        if (existing && !requested)
        {
            var error = Assert.Throws<UsageException>(() =>
                DeploymentChannelPlan.BuildUpdate(plan, snapshot, BotResourceId, "tenant-1"));
            Assert.Contains("cannot be revoked", error.Message);
        }
        else
        {
            var upsert = DeploymentChannelPlan.BuildUpdate(plan, snapshot, BotResourceId, "tenant-1");
            Assert.Equal(requested, Parameters(upsert)["acceptedTerms"].GetBoolean());
        }
    }

    [Fact]
    public void ValidateUpdate_RequiredProperty_RejectsEmptyValue()
    {
        var request = UpdateRequest("slack", "clientId=");
        var ex = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(request));

        Assert.Contains("cannot be cleared", ex.Message);
    }

    [Theory]
    [InlineData("m365extensions", "M365Extensions")]
    [InlineData("outlook", "OutlookChannel")]
    public void ValidateCreate_PropertylessChannel_HasNoParameters(string channel, string armName)
    {
        var plan = DeploymentChannelPlan.ValidateCreate(CreateRequest(channel));
        var upsert = DeploymentChannelPlan.BuildCreate(plan, BotResourceId, "tenant-1");

        Assert.Equal(armName, plan.ChannelDefinition.ArmName);
        Assert.Empty(plan.NormalizedParameters);
        Assert.Empty(upsert.Parameters);
    }

    [Fact]
    public void ValidateUpdate_ChannelWithoutParameters_RejectsUpdate()
    {
        var ex = Assert.Throws<DeploymentException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest("m365extensions", "isEnabled=true")));

        Assert.Equal("channel-operation-not-supported", ex.ErrorKind);
        Assert.Equal("The 'm365extensions' channel does not support 'update' operation.", ex.Message);
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void ValidateDelete_AutomaticallyCreatedChannel_RejectsDelete(string channel)
    {
        var ex = Assert.Throws<DeploymentException>(() =>
            DeploymentChannelPlan.ValidateDelete(DeleteRequest(channel)));

        Assert.Equal("channel-operation-not-supported", ex.ErrorKind);
        Assert.Equal($"The '{channel}' channel does not support 'delete' operation.", ex.Message);
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

        Assert.Equal("Parameter 'unknown' is misspelled or not valid for 'msteams' channel create.", ex.Message);
    }

    [Theory]
    [InlineData("directline", "siteName")]
    [InlineData("webchat", "siteName")]
    [InlineData("directline", "appId")]
    [InlineData("webchat", "appId")]
    [InlineData("directline", "isEnabled")]
    [InlineData("webchat", "isEnabled")]
    [InlineData("directline", "key")]
    [InlineData("webchat", "key")]
    [InlineData("directline", "unknown")]
    [InlineData("webchat", "unknown")]
    public void ValidateMutation_RejectsFlatSiteFieldsWithStandardError(string channel, string parameter)
    {
        const string value = "SYNTHETIC_SITE_VALUE";
        var createError = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateCreate(CreateRequest(channel, $"{parameter}={value}")));
        var updateError = Assert.Throws<UsageException>(() =>
            DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel, $"{parameter}={value}")));

        Assert.Equal($"Parameter '{parameter}' is misspelled or not valid for '{channel}' channel create.", createError.Message);
        Assert.Equal($"Parameter '{parameter}' is misspelled or not valid for '{channel}' channel update.", updateError.Message);
        Assert.DoesNotContain(value, createError.Message);
        Assert.DoesNotContain(value, updateError.Message);
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

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void Sites_CreateValidatesEveryItemAndDoesNotUseLegacyFlatFields(string channel)
    {
        var plan = DeploymentChannelPlan.ValidateCreate(CreateRequest(channel,
            """sites=[{"siteName":"Primary","appId":"app-1","isEnabled":false,"isDetailedLoggingEnabled":true},{"SITENAME":"Secondary","tenantId":"tenant-2","isBlockUserUploadEnabled":true}]"""));
        var sites = DeploymentChannelPlan.BuildCreate(plan, BotResourceId, null).Parameters["sites"];
        Assert.Equal(2, sites.GetArrayLength());
        Assert.Equal("Secondary", sites[1].GetProperty("siteName").GetString());
        Assert.False(sites[0].GetProperty("isEnabled").GetBoolean());
        Assert.True(sites[1].GetProperty("isBlockUserUploadEnabled").GetBoolean());

        foreach (var invalid in new[]
        {
            """sites=[{"siteName":"Primary"},{}]""",
            """sites=[{"siteName":"Primary"},{"siteName":"PRIMARY"}]""",
            """sites=[{"siteName":"Primary","siteId":"id"}]""",
            """sites=[{"siteName":"Primary","isTokenEnabled":true}]""",
            """sites=[{"siteName":"Primary","isDetailedLoggingEnabled":true,"isNoStorageEnabled":true}]""",
            """sites=[{"siteName":"Primary","appId":5}]""",
            """sites=[{"siteName":"Primary","appId":null}]""",
            """sites=[{"siteName":"Primary","SITENAME":"Other"}]""",
            """sites=[null]""", """sites={}""", """sites=[INVALID_SECRET_VALUE""",
        })
        {
            var error = Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(CreateRequest(channel, invalid)));
            Assert.DoesNotContain("INVALID_SECRET_VALUE", error.Message);
        }
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void Sites_ValidateSelectorsChangesAndExclusiveModes(string channel)
    {
        DeploymentChannelUpdateRequest[] invalid =
        [
            UpdateRequest(channel),
            UpdateRequest(channel, "sites=[]") with { AddSite = ["siteName=Third"] },
            UpdateRequest(channel) with { AddSite = ["siteName=Third"], SetSite = ["siteName=Primary", "isEnabled=false"] },
            UpdateRequest(channel) with { SetSite = ["isEnabled=false"] },
            UpdateRequest(channel) with { SetSite = ["siteName=Primary"] },
            UpdateRequest(channel) with { SetSite = ["siteName=Primary", "siteId=id-1", "isEnabled=false"] },
            UpdateRequest(channel) with { SetSite = ["siteId=***", "isEnabled=false"] },
            UpdateRequest(channel) with { SetSite = ["siteName=", "appId=a"] },
            UpdateRequest(channel) with { SetSite = ["siteName=Primary", "isTokenEnabled=false"] },
            UpdateRequest(channel) with { SetSite = ["siteName=Primary", "eTag=etag"] },
            UpdateRequest(channel) with { RemoveSite = ["siteName=Primary", "isEnabled=false"] },
            UpdateRequest(channel) with { AddSite = ["siteName=Third", "isTokenEnabled=false"] },
            UpdateRequest(channel, """sites=[{"siteName":"Primary","isEnabled":"false"}]"""),
            UpdateRequest(channel, """sites=[{"siteName":"Primary","siteId":"id-1"}]"""),
            UpdateRequest(channel, """sites=[{"siteName":null,"isEnabled":false}]"""),
            UpdateRequest(channel, """sites=[{"siteName":"","isEnabled":false}]"""),
            UpdateRequest(channel, """sites=[{"isEnabled":false}]"""),
        ];
        foreach (var request in invalid)
        {
            Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(request));
        }
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void Sites_OptionalBooleansCannotBeClearedThroughArraysOrTargetedOperations(string channel)
    {
        foreach (var value in new[] { "", "null" })
        {
            Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
                UpdateRequest(channel) with { SetSite = ["siteName=Primary", $"isEnabled={value}"] }));
        }

        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest(channel, """sites=[{"siteName":"Primary","isEnabled":null}]""")));
    }

    [Theory]
    [InlineData("directline", "")]
    [InlineData("directline", "[]")]
    [InlineData("webchat", "")]
    [InlineData("webchat", "[]")]
    public void Sites_EmptyAssignmentsAndArraysClearCollectionOnUpdate(string channel, string value)
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel, $"sites={value}"));
        var write = DeploymentChannelPlan.BuildUpdate(plan, SiteSnapshot(), BotResourceId, null);

        Assert.Empty(write.Parameters["sites"].EnumerateArray());
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateCreate(CreateRequest(channel, "sites=")));
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel, "sites=null")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    public void Sites_CollectionClearingRequiresExplicitOptIn(string value)
    {
        var channel = DeploymentChannelCatalog.FindChannelDefinition("directline", ChannelKeyType.Name);
        channel = channel with
        {
            Parameters = channel.Parameters.Select(field => field.Name == "sites"
                ? field with { IsClearable = false } : field).ToArray(),
        };
        var fields = channel.Parameters.Single(field => field.Name == "sites").ItemParameters!;

        var error = Assert.Throws<UsageException>(() => DeploymentChannelSites.ValidateUpdate(
            UpdateRequest("directline", $"sites={value}"), channel, fields));

        Assert.Contains("cannot be cleared", error.Message);
    }

    [Theory]
    [InlineData("directline", "appId")]
    [InlineData("directline", "tenantId")]
    [InlineData("webchat", "appId")]
    [InlineData("webchat", "tenantId")]
    public void Sites_ClearableStringsNormalizeEmptyAssignmentsAndJsonValuesToNull(string channel, string field)
    {
        DeploymentChannelUpdateRequest[] requests =
        [
            UpdateRequest(channel) with { SetSite = ["siteName=Primary", $"{field}="] },
            UpdateRequest(channel, $$"""sites=[{"siteName":"Primary","{{field}}":null},{"siteName":"Secondary"}]"""),
            UpdateRequest(channel, $$"""sites=[{"siteName":"Primary","{{field}}":""},{"siteName":"Secondary"}]"""),
        ];
        foreach (var request in requests)
        {
            var plan = DeploymentChannelPlan.ValidateUpdate(request);
            var sites = DeploymentChannelPlan.BuildUpdate(plan, SiteSnapshot(), BotResourceId, null).Parameters["sites"];

            Assert.Equal(JsonValueKind.Null, sites[0].GetProperty(field).ValueKind);
            Assert.Equal("id-1", sites[0].GetProperty("siteId").GetString());
            Assert.Equal("app-2", sites[1].GetProperty("appId").GetString());
        }
    }

    [Theory]
    [InlineData(DeploymentParameterType.String, "default-text", JsonValueKind.Null)]
    [InlineData(DeploymentParameterType.Integer, "1", JsonValueKind.Null)]
    [InlineData(DeploymentParameterType.Boolean, "false", JsonValueKind.Null)]
    [InlineData(DeploymentParameterType.Array, """["default"]""", JsonValueKind.Array)]
    public void Sites_FieldClearingRequiresOptInRegardlessOfTypeOrDefault(
        DeploymentParameterType type, string defaultValue, JsonValueKind expectedKind)
    {
        var channel = DeploymentChannelCatalog.FindChannelDefinition("directline", ChannelKeyType.Name);
        foreach (var value in new[] { null, defaultValue })
        {
            foreach (var clearable in new[] { false, true })
            {
                var fields = channel.Parameters.Single(field => field.Name == "sites").ItemParameters!
                    .Select(field => field.Name == "appId"
                        ? field with { Type = type, DefaultValue = value, IsClearable = clearable } : field).ToArray();
                var request = UpdateRequest("directline") with { SetSite = ["siteName=Primary", "appId="] };
                if (!clearable)
                {
                    Assert.Throws<UsageException>(() => DeploymentChannelSites.ValidateUpdate(request, channel, fields));
                    if (type == DeploymentParameterType.Array)
                    {
                        Assert.Throws<UsageException>(() => DeploymentChannelSites.ValidateUpdate(
                            UpdateRequest("directline", """sites=[{"siteName":"Primary","appId":[]}]"""),
                            channel, fields));
                    }
                }
                else
                {
                    var mutation = DeploymentChannelSites.ValidateUpdate(request, channel, fields);
                    var cleared = Assert.Single(mutation.Items).GetProperty("appId");

                    Assert.Equal(expectedKind, cleared.ValueKind);
                    if (type == DeploymentParameterType.Array)
                    {
                        Assert.Empty(cleared.EnumerateArray());
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Sites_OriginsClearingAlwaysProducesEmptyArray(string value)
    {
        var request = value == "null"
            ? UpdateRequest("directline", """sites=[{"siteName":"Primary","trustedOrigins":null},{"siteName":"Secondary"}]""")
            : UpdateRequest("directline") with { SetSite = ["siteName=Primary", $"trustedOrigins={value}"] };
        var plan = DeploymentChannelPlan.ValidateUpdate(request);
        var sites = DeploymentChannelPlan.BuildUpdate(plan, SiteSnapshot(), BotResourceId, null).Parameters["sites"];

        Assert.Empty(sites[0].GetProperty("trustedOrigins").EnumerateArray());
        Assert.Equal("app-1", sites[0].GetProperty("appId").GetString());
        Assert.Equal("id-2", sites[1].GetProperty("siteId").GetString());
    }

    [Fact]
    public void Sites_SelectorIsIndependentOfCreationRequirementAndCannotBeClearedOrRenamed()
    {
        var channel = DeploymentChannelCatalog.FindChannelDefinition("directline", ChannelKeyType.Name);
        var fields = channel.Parameters.Single(field => field.Name == "sites").ItemParameters!
            .Select(field => field.IsSelector ? field with { IsRequired = false, IsClearable = true } : field).ToArray();
        channel = channel with
        {
            Parameters = channel.Parameters.Select(field => field.Name == "sites"
                ? field with { ItemParameters = fields } : field).ToArray(),
        };
        var mutation = DeploymentChannelSites.ValidateUpdate(
            UpdateRequest("directline") with { SetSite = ["SITENAME=PRIMARY", "APPID=new-app"] },
            channel, fields);
        var sites = DeploymentChannelSites.BuildUpdate(mutation, channel, SiteSnapshot())["sites"];
        Assert.Equal("Primary", sites[0].GetProperty("siteName").GetString());
        Assert.Equal("new-app", sites[0].GetProperty("appId").GetString());

        var added = DeploymentChannelSites.ValidateCreate(
            CreateRequest("directline", """SITES=[{"SITENAME":"New","APPID":"new-app"}]"""),
            channel, fields);
        var initial = DeploymentChannelSites.BuildInitialWrite(channel, DeploymentChannelSites.BuildCreate(added))["sites"];
        Assert.Equal("New", Assert.Single(initial.EnumerateArray()).GetProperty("siteName").GetString());
        Assert.Single(initial[0].EnumerateObject());
        foreach (var value in new[] { "\"\"", "null" })
        {
            Assert.Throws<UsageException>(() => DeploymentChannelSites.ValidateUpdate(
                UpdateRequest("directline", $$"""sites=[{"siteName":{{value}},"appId":"new-app"}]"""),
                channel, fields));
        }

        Assert.Throws<UsageException>(() => DeploymentChannelSites.ValidateUpdate(
            UpdateRequest("directline") with { SetSite = ["appId=new-app"] },
            channel, fields));
    }

    [Fact]
    public void Sites_OptionalEnumsRequireAllowedValuesButCanBeOmitted()
    {
        var channel = DeploymentChannelCatalog.FindChannelDefinition("directline", ChannelKeyType.Name);
        var fields = channel.Parameters.Single(field => field.Name == "sites").ItemParameters!
            .Select(field => field.Name == "appId" ? field with { AllowedValues = ["one", "two"] } : field).ToArray();

        foreach (var value in new[] { "null", "\"\"", "\"invalid\"" })
        {
            Assert.Throws<UsageException>(() => DeploymentChannelSites.ValidateUpdate(
                UpdateRequest("directline", "sites=[{\"siteName\":\"Primary\",\"appId\":" + value + "}]"),
                channel, fields));
        }

        var valid = DeploymentChannelSites.ValidateUpdate(
            UpdateRequest("directline") with { SetSite = ["siteName=Primary", "appId=ONE"] }, channel, fields);
        Assert.Equal("one", Assert.Single(valid.Items).GetProperty("appId").GetString());
        var omitted = DeploymentChannelSites.ValidateUpdate(
            UpdateRequest("directline") with { SetSite = ["siteName=Primary", "isEnabled=false"] }, channel, fields);
        Assert.False(Assert.Single(omitted.Items).TryGetProperty("appId", out _));
    }

    [Fact]
    public void Sites_ExistingNullBooleanDoesNotPreventUpdatingAnotherField()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("directline") with
        {
            SetSite = ["siteName=Primary", "appId=new-app"],
        });
        var existing = Snapshot("""{"properties":{"properties":{"sites":[{"siteName":"Primary","siteId":"id-1","isEnabled":null}]}}}""");
        var site = Assert.Single(DeploymentChannelPlan.BuildUpdate(plan, existing, BotResourceId, null).Parameters["sites"].EnumerateArray());

        Assert.Equal("new-app", site.GetProperty("appId").GetString());
        Assert.Equal(JsonValueKind.Null, site.GetProperty("isEnabled").ValueKind);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Sites_RequiredNonSelectorMayBeOmittedButNotClearedOnUpdate(string value)
    {
        var channel = DeploymentChannelCatalog.FindChannelDefinition("directline", ChannelKeyType.Name);
        var fields = channel.Parameters.Single(field => field.Name == "sites").ItemParameters!
            .Select(field => field.Name == "appId" ? field with { IsRequired = true } : field).ToArray();
        var omitted = DeploymentChannelSites.ValidateUpdate(
            UpdateRequest("directline") with { SetSite = ["siteName=Primary", "isEnabled=false"] },
            channel, fields);
        Assert.False(Assert.Single(omitted.Items).TryGetProperty("appId", out _));

        Assert.Throws<UsageException>(() => DeploymentChannelSites.ValidateUpdate(
            UpdateRequest("directline", "sites=[{\"siteName\":\"Primary\",\"appId\":" + value + "}]"),
            channel, fields));
    }

    [Theory]
    [InlineData("directline", "siteName=PRIMARY")]
    [InlineData("webchat", "siteName=PRIMARY")]
    public void Sites_SetPreservesOtherSitesAndSafeStateWithoutReplayingCredentials(string channel, string selector)
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest(channel) with
        {
            SetSite = [selector, "isEnabled=false", "appId="],
        });
        var sites = DeploymentChannelPlan.BuildUpdate(plan, SiteSnapshot(), BotResourceId, null).Parameters["sites"];
        Assert.Equal(2, sites.GetArrayLength());
        Assert.Equal("Primary", sites[0].GetProperty("siteName").GetString());
        Assert.Equal("id-1", sites[0].GetProperty("siteId").GetString());
        Assert.False(sites[0].GetProperty("isEnabled").GetBoolean());
        Assert.True(sites[0].GetProperty("isTokenEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sites[0].GetProperty("appId").ValueKind);
        Assert.Equal("tenant-1", sites[0].GetProperty("tenantId").GetString());
        Assert.Equal("id-2", sites[1].GetProperty("siteId").GetString());
        Assert.False(sites[1].GetProperty("isEnabled").GetBoolean());
        Assert.Equal("app-2", sites[1].GetProperty("appId").GetString());
        if (channel == "webchat")
        {
            Assert.False(sites[0].GetProperty("isWebchatPreviewEnabled").GetBoolean());
        }
        else
        {
            Assert.True(sites[0].GetProperty("isV1Enabled").GetBoolean());
        }

        Assert.DoesNotContain("SYNTHETIC_KEY", sites.GetRawText());
        Assert.DoesNotContain("***", sites.GetRawText());
        Assert.DoesNotContain("eTag", sites.GetRawText());
        Assert.DoesNotContain("unknown", sites.GetRawText());
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void Sites_AddRemoveAndReplaceRetainIdentitiesAndEnforceItemPermissions(string channel)
    {
        JsonElement Update(DeploymentChannelUpdateRequest request) => DeploymentChannelPlan.BuildUpdate(
            DeploymentChannelPlan.ValidateUpdate(request), SiteSnapshot(), BotResourceId, null).Parameters["sites"];

        var added = Update(UpdateRequest(channel) with { AddSite = ["siteName=Third", "appId=app-3", "isEnabled=false"] });
        Assert.Equal(3, added.GetArrayLength());
        Assert.Equal("id-1", added[0].GetProperty("siteId").GetString());
        Assert.Equal("Third", added[2].GetProperty("siteName").GetString());
        Assert.False(added[2].GetProperty("isEnabled").GetBoolean());
        Assert.False(added[2].TryGetProperty("siteId", out _));
        var removed = Update(UpdateRequest(channel) with { RemoveSite = ["siteName=Primary"] });
        Assert.Equal("id-2", Assert.Single(removed.EnumerateArray()).GetProperty("siteId").GetString());
        var replacement = Update(UpdateRequest(channel,
            """sites=[{"siteName":"SECONDARY","isEnabled":true},{"siteName":"Third","appId":"app-3","isEnabled":false}]"""));
        Assert.Equal(2, replacement.GetArrayLength());
        Assert.Equal("Secondary", replacement[0].GetProperty("siteName").GetString());
        Assert.Equal("app-2", replacement[0].GetProperty("appId").GetString());
        Assert.True(replacement[0].GetProperty("isEnabled").GetBoolean());
        Assert.Equal("Third", replacement[1].GetProperty("siteName").GetString());
        Assert.False(replacement[1].GetProperty("isEnabled").GetBoolean());
        Assert.Empty(Update(UpdateRequest(channel, "sites=[]")).EnumerateArray());

        DeploymentChannelUpdateRequest[] invalid =
        [
            UpdateRequest(channel) with { AddSite = ["siteName=primary"] },
            UpdateRequest(channel) with { SetSite = ["siteName=Missing", "isEnabled=false"] },
            UpdateRequest(channel) with { RemoveSite = ["siteName=Missing"] },
            UpdateRequest(channel, """sites=[{"siteName":"Third","key":"supplied-key"}]"""),
            UpdateRequest(channel, """sites=[{"siteName":"Primary"},{"siteName":"PRIMARY"}]"""),
        ];
        foreach (var request in invalid)
        {
            Assert.Throws<UsageException>(() => Update(request));
        }
    }

    [Fact]
    public void Sites_DirectLineOriginsAreArraysAndEffectiveLoggingConflictIsRejected()
    {
        foreach (var value in new[] { "https://one.test, https://two.test", """["https://one.test","https://two.test"]""" })
        {
            var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("directline") with
            {
                SetSite = ["siteName=Primary", $"trustedOrigins={value}"],
            });
            var sites = DeploymentChannelPlan.BuildUpdate(plan, SiteSnapshot(), BotResourceId, null).Parameters["sites"];
            Assert.Equal(2, sites[0].GetProperty("trustedOrigins").GetArrayLength());
        }

        Assert.Throws<UsageException>(() => DeploymentChannelPlan.ValidateUpdate(
            UpdateRequest("directline", """sites=[{"siteName":"Primary","trustedOrigins":[false]}]""")));
        var conflict = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("webchat") with
        {
            SetSite = ["siteName=Primary", "isNoStorageEnabled=true"],
        });
        Assert.Throws<UsageException>(() => DeploymentChannelPlan.BuildUpdate(
            conflict, SiteSnapshot(), BotResourceId, null));
    }

    [Theory]
    [InlineData("""[{"siteName":"Primary"}]""")]
    [InlineData("""[{"siteId":"id-1","siteName":"   "}]""")]
    [InlineData("""[{"siteId":"id-1","siteName":"Primary","isEnabled":"SYNTHETIC_SECRET"}]""")]
    [InlineData("""[{"siteId":"id-1","siteName":"Primary"},{"siteId":"id-2","siteName":"PRIMARY"}]""")]
    [InlineData("""[{"siteId":"id-1","siteName":"Primary"},{"siteId":"id-2","siteName":" Primary "}]""")]
    [InlineData("""[{"siteId":"id-1","siteName":"Primary"},{"siteId":"ID-1","siteName":"Secondary"}]""")]
    [InlineData("""[null]""")]
    [InlineData("""null""")]
    [InlineData("""[{"siteName":"Primary","siteId":false}]""")]
    public void Sites_UnreadableOrAmbiguousLiveStateFailsWithoutConstructingReplacement(string value)
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("webchat") with { AddSite = ["siteName=Third"] });
        var error = Assert.Throws<DeploymentException>(() => DeploymentChannelPlan.BuildUpdate(
            plan, Snapshot("{\"properties\":{\"properties\":{\"sites\":" + value + "}}}"), BotResourceId, null));

        Assert.Equal("channel-configuration-unavailable", error.ErrorKind);
        Assert.Equal(ExitCodes.Failure, error.ExitCode);
        Assert.Equal("The channel response does not contain a usable configuration.", error.Message);
        Assert.DoesNotContain("SYNTHETIC_SECRET", error.Message);
    }

    private static DeploymentChannelRecord SiteSnapshot() => Snapshot("""
        {"properties":{"properties":{"sites":[
          {"siteName":"Primary","siteId":"id-1","isTokenEnabled":true,"isEnabled":true,"appId":"app-1",
           "tenantId":"tenant-1","isV1Enabled":true,"isWebchatPreviewEnabled":false,"isDetailedLoggingEnabled":true,
           "isNoStorageEnabled":false,"key":"SYNTHETIC_KEY","key2":"***","eTag":42,"unknown":true},
          {"siteName":"Secondary","siteId":"id-2","isTokenEnabled":false,"isEnabled":false,
           "appId":"app-2","key":"***"}
        ]}}}
        """);

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void Sites_MaskTextRemainsUsableAcrossCreationAndUpdates(string channel)
    {
        var origins = channel == "directline" ? ""","trustedOrigins":["***"]""" : "";
        var definition = DeploymentChannelCatalog.FindChannelDefinition(channel, ChannelKeyType.Name);
        var created = DeploymentChannelPlan.BuildCreate(DeploymentChannelPlan.ValidateCreate(CreateRequest(channel,
            $$"""sites=[{"siteName":"***","appId":"***","tenantId":"***"{{origins}}}]""")), BotResourceId, null);
        var response = Snapshot($$"""
            {"properties":{"properties":{"sites":[
              {"siteName":"***","siteId":"***","appId":"***","tenantId":"***","key":"***"{{origins}}}
            ]}
            }
            }
            """);
        var configured = DeploymentChannelSites.BuildNewSiteConfiguration(definition, created.Parameters, response);
        Assert.NotNull(configured);
        var update = UpdateRequest(channel) with
        {
            SetSite = ["siteName=***", "appId=***", "tenantId=***", .. channel == "directline" ? new[] { "trustedOrigins=***" } : []],
        };
        var updated = DeploymentChannelPlan.BuildUpdate(DeploymentChannelPlan.ValidateUpdate(update), response, BotResourceId, null);

        IReadOnlyDictionary<string, JsonElement>[] writes = [created.Parameters, configured, updated.Parameters];
        foreach (var parameters in writes)
        {
            var site = Assert.Single(parameters["sites"].EnumerateArray());
            Assert.Equal("***", site.GetProperty("siteName").GetString());
            Assert.Equal("***", site.GetProperty("appId").GetString());
            Assert.Equal("***", site.GetProperty("tenantId").GetString());
            Assert.False(site.TryGetProperty("key", out _));
            if (channel == "directline")
                Assert.Equal("***", Assert.Single(site.GetProperty("trustedOrigins").EnumerateArray()).GetString());
        }
        Assert.Equal("***", configured["sites"][0].GetProperty("siteId").GetString());
        Assert.Equal("***", updated.Parameters["sites"][0].GetProperty("siteId").GetString());
    }

    [Fact]
    public void Sites_ReuseDoesNotNormalizeOrRenameExistingStringValues()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("webchat") with
        {
            SetSite = ["siteName=PRIMARY", "isEnabled=false"],
        });
        var existing = Snapshot("""
            {"properties":{"properties":{"sites":[
              {"siteName":" Primary ","siteId":"id-1","appId":" app-1 ","tenantId":""}
            ]}}}
            """);
        var site = Assert.Single(DeploymentChannelPlan.BuildUpdate(plan, existing, BotResourceId, null)
            .Parameters["sites"].EnumerateArray());
        Assert.Equal(" Primary ", site.GetProperty("siteName").GetString());
        Assert.Equal("id-1", site.GetProperty("siteId").GetString());
        Assert.Equal(" app-1 ", site.GetProperty("appId").GetString());
        Assert.Equal("", site.GetProperty("tenantId").GetString());
    }

    [Fact]
    public void Sites_ReplacementDoesNotRequireSettingsOfExplicitlyRemovedSites()
    {
        var plan = DeploymentChannelPlan.ValidateUpdate(UpdateRequest("webchat",
            """sites=[{"siteName":"Secondary"}]"""));
        var existing = Snapshot("""
            {"properties":{"properties":{"sites":[
              {"siteName":"Primary","siteId":"id-1","isEnabled":"unreadable"},
              {"siteName":"Secondary","siteId":"id-2","isEnabled":true}
            ]}}}
            """);
        var site = Assert.Single(DeploymentChannelPlan.BuildUpdate(plan, existing, BotResourceId, null)
            .Parameters["sites"].EnumerateArray());
        Assert.Equal("id-2", site.GetProperty("siteId").GetString());
        Assert.True(site.GetProperty("isEnabled").GetBoolean());
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

    private static IReadOnlyDictionary<string, System.Text.Json.JsonElement> Parameters(BotServiceChannelWriteRequest upsert) =>
        upsert.Parameters;

    private const string BotResourceId =
        "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/dev";
}
