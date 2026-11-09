using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentChannelCatalogTests
{
    public static IEnumerable<object[]> ChannelNamesAndArmNames() =>
        DeploymentChannelCatalog.Channels.Select(channel => new object[] { channel.Name, channel.ArmName });

    public static IEnumerable<object[]> UnsupportedArmNamesAndNames() =>
        DeploymentChannelCatalog.Channels.Where(channel => !channel.IsSupported)
            .Select(channel => new object[] { channel.ArmName, channel.Name })
            .Append(["UnknownArmChannel", "UnknownArmChannel"]);

    public static IEnumerable<object[]> UnsupportedChannelNames() =>
        UnsupportedArmNamesAndNames().SelectMany(keys =>
            keys.Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Select(name => new object[] { name }));

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void SiteProjection_DoesNotSilentlyDiscardMalformedItemsBeforeMutationValidation(string channel)
    {
        var properties = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            sites = new object?[] { new { siteName = "Primary", siteId = "id-1" }, null, 42 },
        });
        var projected = DeploymentChannelCatalog.ProjectParameters(properties, Channel(channel).Parameters);
        Assert.Equal(3, projected["sites"].GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, projected["sites"][1].ValueKind);
        Assert.Equal(42, projected["sites"][2].GetInt32());
    }

    [Theory]
    [MemberData(nameof(ChannelNamesAndArmNames))]
    public void ChannelLookup_ResolvesNamesAndArmNames(string name, string armName)
    {
        var channel = Channel(name);

        Assert.Equal(armName, channel.ArmName);
        Assert.Same(
            channel,
            DeploymentChannelCatalog.FindChannelDefinition(armName, ChannelKeyType.ArmName));
    }

    [Fact]
    public void Catalogue_ContainsExactlySeventeenDistinctChannels()
    {
        Assert.Equal(17, DeploymentChannelCatalog.Channels.Count);
        Assert.Equal(
            17,
            DeploymentChannelCatalog.Channels.Select(channel => channel.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            17,
            DeploymentChannelCatalog.Channels.Select(channel => channel.ArmName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("telephony", ChannelKeyType.Name)]
    [InlineData("TelephonyChannel", ChannelKeyType.ArmName)]
    [InlineData("kik", ChannelKeyType.Name)]
    [InlineData("KikChannel", ChannelKeyType.ArmName)]
    public void RemovedChannel_IsNotRecognized(string key, ChannelKeyType keyType)
    {
        Assert.Throws<DeploymentException>(() =>
            DeploymentChannelCatalog.FindChannelDefinition(key, keyType));
    }

    [Theory]
    [InlineData(" DIRECTLINE ", ChannelKeyType.Name)]
    [InlineData(" DIRECTLINECHANNEL ", ChannelKeyType.ArmName)]
    public void SchemaLookup_PreservesCaseInsensitiveTrimmedKeys(string key, ChannelKeyType keyType)
    {
        var result = DeploymentChannelCatalog.FindChannelDefinition(key, keyType);

        Assert.Same(Channel("directline"), result);
    }

    [Fact]
    public void Catalogue_EnablesTenChannels()
    {
        Assert.Equal(
            ["directline", "facebook", "line",
             "m365extensions", "msteams", "outlook", "slack",
             "sms", "telegram", "webchat"],
            DeploymentChannelCatalog.SupportedChannelNames);
    }

    [Fact]
    public void Catalogue_DeclaresChannelsAndPropertiesInAlphabeticalOrder()
    {
        var names = DeploymentChannelCatalog.Channels.Select(channel => channel.Name).ToArray();

        Assert.Equal(names.OrderBy(name => name, StringComparer.Ordinal), names);

        foreach (var channel in DeploymentChannelCatalog.Channels)
        {
            AssertAlphabeticalParameters(channel.Parameters);
        }

        static void AssertAlphabeticalParameters(IReadOnlyList<ChannelParameterDefinition> parameters)
        {
            var names = parameters.Select(parameter => parameter.Name).ToArray();
            Assert.Equal(names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase), names);
            Assert.All(names, name => Assert.True(char.IsLower(name[0]), $"Parameter '{name}' must start with a lowercase letter."));
            Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (var parameter in parameters)
            {
                if (parameter.ItemParameters is { } items)
                {
                    AssertAlphabeticalParameters(items);
                }
            }
        }
    }

    [Fact]
    public void SlackParameters_LeaveEnablementToAzureAndPreserveCredentialRules()
    {
        var slack = Channel("slack");
        var cliEnabled = Assert.Single(slack.Parameters, property => property.Name == "isEnabled");

        Assert.False(cliEnabled.IsRequired);
        Assert.Null(cliEnabled.DefaultValue);
        Assert.True(Assert.Single(slack.Parameters, property => property.Name == "clientSecret").IsRequired);

    }

    [Fact]
    public void ParameterTypes_PreserveExistingNumericValues()
    {
        Assert.Equal(0, (int)DeploymentParameterType.String);
        Assert.Equal(1, (int)DeploymentParameterType.Boolean);
    }

    private static ChannelDefinition Channel(string name) =>
        DeploymentChannelCatalog.FindChannelDefinition(name, ChannelKeyType.Name);

}
