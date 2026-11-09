using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Commands.Commands.Deployment.Channel;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentChannelHelpTests
{
    [Theory]
    [InlineData("directline", "site")]
    [InlineData("webchat", "site")]
    [InlineData("facebook", "page")]
    [InlineData("line", "registration")]
    [InlineData("msteams", null)]
    [InlineData("slack", null)]
    [InlineData("sms", null)]
    [InlineData("telegram", null)]
    public void UpdateHelp_ShowsOnlyApplicableCollectionOptions(string channel, string? collection)
    {
        var help = Render("update", channel);
        foreach (var item in new[] { "site", "page", "registration" })
        {
            foreach (var operation in new[] { "add", "set", "remove" })
            {
                var option = $"--{operation}-{item}";
                if (item == collection)
                    Assert.Contains(option, help);
                else
                    Assert.DoesNotContain(option, help);
            }
        }
    }

    [Fact]
    public void SelectedHelp_DoesNotHideOptionsInSubsequentGenericHelp()
    {
        var (root, command, action) = CreateHelp("update");
        Assert.DoesNotContain("--add-page", Render(root, command, action, "directline"));
        var generic = Render(root, command, action, null);
        Assert.Contains("--add-site", generic);
        Assert.Contains("--add-page", generic);
        Assert.Contains("--add-registration", generic);
        Assert.DoesNotContain("--add-site", Render(root, command, action, "facebook"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedHelp_RestoresOriginalVisibilityEvenIfInnerHelpFails(bool fail)
    {
        var (root, command, _) = CreateHelp("update");
        command.Options.Single(option => option.Name == "--remove-page").Hidden = true;
        var original = command.Options.ToDictionary(option => option, option => option.Hidden);
        var action = new DeploymentChannelHelpAction(new CallbackHelpAction(_ =>
        {
            Assert.False(command.Options.Single(option => option.Name == "--add-site").Hidden);
            Assert.True(command.Options.Single(option => option.Name == "--add-page").Hidden);
            Assert.True(command.Options.Single(option => option.Name == "--add-registration").Hidden);
            if (fail)
                throw new InvalidOperationException("Synthetic help failure");
        }), new HashSet<Command> { command });
        if (fail)
            Assert.Throws<InvalidOperationException>(() => Render(root, command, action, "directline"));
        else
            Render(root, command, action, "directline");
        Assert.All(original, entry => Assert.Equal(entry.Value, entry.Key.Hidden));
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    public void SiteHelp_NestsWritableFieldsImmediatelyBelowArray(string channel)
    {
        foreach (var verb in new[] { "create", "update" })
        {
            var help = Render(verb, channel);
            var lines = Parameters(help).Split(Environment.NewLine);
            Assert.StartsWith("  sites <Array>", lines[1]);
            Assert.EndsWith(channel == "directline" ? "The Direct Line sites:" : "The Web Chat sites:", lines[1]);
            Assert.StartsWith("      siteName <String> (REQUIRED)", lines[2]);
            Assert.StartsWith("      appId <String>", lines[3]);
            Assert.Contains("tenantId <String>", help);
            Assert.Single(lines, line => line.Contains("(REQUIRED)", StringComparison.Ordinal));
            foreach (var field in new[] { "eTag", "siteId", "key", "key2", "isTokenEnabled", "isV1Enabled" })
                Assert.DoesNotContain($"{field} <", help);
            Assert.DoesNotContain("Site settings:", help);
            Assert.DoesNotContain("New-site identity:", help);
            Assert.DoesNotContain("Existing-site selector", help);
            Assert.DoesNotContain("not atomic", help);
            if (channel == "webchat")
            {
                Assert.Contains("isWebchatPreviewEnabled <Boolean>", help);
                Assert.DoesNotContain("trustedOrigins", help);
                Assert.DoesNotContain("isSecureSiteEnabled", help);
                Assert.DoesNotContain("isV3Enabled", help);
            }
            else
            {
                Assert.Contains("isSecureSiteEnabled <Boolean>", help);
                Assert.Contains("isV3Enabled <Boolean>", help);
                var origins = Assert.Single(lines, line => line.Contains("trustedOrigins <Array>", StringComparison.Ordinal));
                Assert.DoesNotContain("with properties:", origins);
                Assert.DoesNotContain("isWebchatPreviewEnabled", help);
            }
        }
    }

    [Theory]
    [InlineData("directline")]
    [InlineData("webchat")]
    [InlineData("facebook")]
    [InlineData("line")]
    public void CollectionHelp_AlignsAllParameterDescriptionsWithoutRemovingItemIndentation(string channel)
    {
        foreach (var verb in new[] { "create", "update" })
        {
            var lines = Parameters(Render(verb, channel))
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
            var columns = lines.Select(line =>
            {
                var match = Regex.Match(line, @"^\s+\S+ <[^>]+>(?: \(REQUIRED\))?\s+(?<description>\S)");
                Assert.True(match.Success, $"Invalid parameter help row: {line}");
                return match.Groups["description"].Index;
            }).Distinct();
            Assert.Single(columns);
            Assert.Contains(lines, line => line.StartsWith("  ", StringComparison.Ordinal) &&
                !line.StartsWith("      ", StringComparison.Ordinal));
            Assert.Contains(lines, line => line.StartsWith("      ", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public void FacebookHelp_NestsPageFieldsAndMarksRequiredFieldsForOperation(string verb)
    {
        var help = Render(verb, "facebook");
        var lines = Parameters(help).Split(Environment.NewLine);
        var pageIndex = Array.FindIndex(lines, line => line.StartsWith("  pages <Array>", StringComparison.Ordinal));
        Assert.True(pageIndex > 0);
        Assert.StartsWith("      id <String> (REQUIRED)", lines[pageIndex + 1]);
        Assert.StartsWith("      accessToken <String>", lines[pageIndex + 2]);
        Assert.Equal(verb == "create" ? 5 : 1, lines.Count(line => line.Contains("(REQUIRED)", StringComparison.Ordinal)));
        Assert.DoesNotContain("isEnabled <Boolean> (REQUIRED)", help);
        Assert.DoesNotContain("verifyToken <", help);
        Assert.DoesNotContain("callbackUrl <", help);
        Assert.DoesNotContain("Existing-page selector", help);
        Assert.DoesNotContain("Page fields", help);
        Assert.DoesNotContain("Complete pages arrays", help);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public void LineHelp_NestsCredentialsAndExposesGeneratedIdOnlyAsUpdateSelector(string verb)
    {
        var help = Render(verb, "line");
        var lines = Parameters(help).Split(Environment.NewLine);
        Assert.StartsWith("  lineRegistrations <Array>", lines[1]);
        Assert.Contains("      channelAccessToken <String>", help);
        Assert.Contains("      channelSecret <String>", help);
        if (verb == "create")
        {
            Assert.DoesNotContain("generatedId <", help);
            Assert.Equal(3, lines.Count(line => line.Contains("(REQUIRED)", StringComparison.Ordinal)));
            Assert.DoesNotContain("--add-registration", help);
        }
        else
        {
            Assert.StartsWith("      generatedId <String> (REQUIRED)", lines[2]);
            Assert.Single(lines, line => line.Contains("(REQUIRED)", StringComparison.Ordinal));
        }
        Assert.DoesNotContain("callbackUrl <", help);
        Assert.DoesNotContain("isValidated <", help);
        Assert.DoesNotContain("Registration fields", help);
        Assert.DoesNotContain("replace membership", help);
    }

    [Theory]
    [InlineData("slack", 2)]
    [InlineData("sms", 3)]
    [InlineData("telegram", 1)]
    public void ScalarHelp_ShowsOnlyWritableMetadataWithoutExtraGuidance(string channel, int requiredOnCreate)
    {
        foreach (var verb in new[] { "create", "update" })
        {
            var help = Render(verb, channel);
            var parameters = Parameters(help);
            Assert.Equal(verb == "create" ? requiredOnCreate : 0,
                parameters.Split(Environment.NewLine).Count(line => line.Contains("(REQUIRED)", StringComparison.Ordinal)));
            Assert.Contains("isEnabled <Boolean>", parameters);
            Assert.DoesNotContain("isEnabled <Boolean> (REQUIRED)", parameters);
            foreach (var name in new[] { "isValidated", "IsValidated", "lastSubmissionId", "redirectAction", "registerBeforeOAuthFlow" })
                Assert.DoesNotContain($"{name} <", parameters);
            foreach (var guidance in new[] { "cannot be cleared", "On update", "at least one of", "without polling", "even when isEnabled=false" })
                Assert.DoesNotContain(guidance, help);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public void MsTeamsHelp_KeepsDefaultsAndAllowedValuesWithoutExtraGuidance(string verb)
    {
        var help = Render(verb, "msteams");
        Assert.DoesNotContain("(REQUIRED)", Parameters(help));
        Assert.DoesNotContain("isEnabled <", help);
        Assert.Contains("Default: false.", help);
        Assert.Contains("Allowed values: CommercialDeployment, GCCModerateDeployment.", help);
        Assert.DoesNotContain("Terms acceptance cannot be revoked", help);
        Assert.DoesNotContain("These optional fields", help);
    }

    public static IEnumerable<object[]> UnsupportedOperations()
    {
        string[] operations = ["create", "update", "show", "delete"];
        return DeploymentChannelCatalog.Channels.Where(channel => !channel.IsSupported)
            .SelectMany(channel => operations.Select(verb => new object[] { verb, channel.Name }));
    }

    [Theory]
    [MemberData(nameof(UnsupportedOperations))]
    public void UnsupportedChannelHelp_RejectsSelection(string verb, string channel)
    {
        var ex = Assert.Throws<UsageException>(() => Render(verb, channel));
        Assert.Contains($"Channel '{channel}' is not supported.", ex.Message);
    }

    [Theory]
    [InlineData("show", "m365extensions")]
    [InlineData("update", "outlook")]
    [InlineData("delete", "directline")]
    [InlineData("delete", "webchat")]
    public void UnavailableOperationHelp_RejectsSelection(string verb, string channel)
    {
        var ex = Assert.Throws<DeploymentException>(() => Render(verb, channel));
        Assert.Equal($"The '{channel}' channel does not support '{verb}' operation.", ex.Message);
    }

    [Theory]
    [InlineData("create", "m365extensions")]
    [InlineData("create", "outlook")]
    [InlineData("show", "facebook")]
    [InlineData("delete", "line")]
    [InlineData("list", null)]
    public void NonParameterHelp_DoesNotAppendMetadataOrWarnings(string verb, string? channel)
    {
        var help = Render(verb, channel);
        Assert.DoesNotContain("Parameters:", help);
        Assert.DoesNotContain("has no configurable parameters", help);
    }

    private static string Parameters(string help)
    {
        var start = help.IndexOf("Parameters:", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return help[start..];
    }

    private static string Render(string verb, string? channel)
    {
        var (root, command, action) = CreateHelp(verb);
        return Render(root, command, action, channel);
    }

    private static string Render(RootCommand root, Command command, DeploymentChannelHelpAction action, string? channel)
    {
        var parse = root.Parse(channel is null ? [command.Name] : [command.Name, "--channel", channel]);
        using var output = new StringWriter();
        parse.InvocationConfiguration.Output = output;
        Assert.Equal(0, action.Invoke(parse));
        return output.ToString();
    }

    private static (RootCommand Root, Command Command, DeploymentChannelHelpAction Action) CreateHelp(string verb)
    {
        var symbols = verb switch
        {
            "create" => new DeploymentChannelCreateCommand().Symbols,
            "update" => new DeploymentChannelUpdateCommand().Symbols,
            "delete" => new DeploymentChannelDeleteCommand().Symbols,
            "show" => new DeploymentChannelShowCommand().Symbols,
            "list" => new DeploymentChannelListCommand().Symbols,
            _ => throw new ArgumentOutOfRangeException(nameof(verb)),
        };
        var command = new Command(verb);
        foreach (var symbol in symbols.OfType<CommandOption>())
        {
            if (symbol.ValueType == typeof(string[]))
                command.Add(new Option<string[]>(symbol.Name) { AllowMultipleArgumentsPerToken = symbol.AllowMultipleArgumentsPerToken });
            else
                command.Add(new Option<string>(symbol.Name));
        }

        var root = new RootCommand();
        root.Add(command);
        var inner = Assert.IsAssignableFrom<SynchronousCommandLineAction>(root.Options.OfType<HelpOption>().Single().Action);
        return (root, command, new DeploymentChannelHelpAction(inner, new HashSet<Command> { command }));
    }

    private sealed class CallbackHelpAction(Action<ParseResult> invoke) : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            invoke(parseResult);
            return 0;
        }
    }
}
