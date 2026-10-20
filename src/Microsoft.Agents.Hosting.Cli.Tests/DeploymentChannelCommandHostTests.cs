using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentChannelCommandHostTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(
        FakeOperations operations,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new Microsoft.Agents.Hosting.Cli.Hosting.AgentHostCli(
            operations,
            featureFlags: new TestFeatureFlags(FeatureName.AzureBotServiceFeatures));
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("show")]
    [InlineData("list")]
    [InlineData("delete")]
    public void ChannelHelp_ListsOnlySupportedChannels(string? verb)
    {
        string[] args = verb is null
            ? ["deployment", "channel", "--help"]
            : ["deployment", "channel", verb, "--help"];
        var (exitCode, stdout, stderr) = Run(new FakeOperations(), args);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        if (verb is "create" or "update" or "show" or "delete")
        {
            Assert.Contains($"Supported: {string.Join(", ", DeploymentChannelCatalog.SupportedChannelNames)}.", stdout);
        }
        foreach (var channel in DeploymentChannelCatalog.Channels.Where(channel => !channel.IsSupported))
        {
            Assert.DoesNotContain(channel.Name, stdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(channel.ArmName, stdout, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Create_BindsShortAddressingChannelAndParameters()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            "deployment", "channel", "create",
            "--deployment-id", "dep-1",
            "--channel", "slack",
            "--parameters", "clientId=client-1", "clientSecret=secret-1", "verificationToken=token-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<DeploymentChannelCreateRequest>(operations.LastCreateChannelRequest);
        Assert.Equal("dep-1", request.DeploymentId);
        Assert.Equal("slack", request.ChannelName);
        Assert.Equal(
            ["clientId=client-1", "clientSecret=secret-1", "verificationToken=token-1"],
            request.Parameters);
    }

    public static IEnumerable<object[]> ChannelMutations()
    {
        foreach (var channel in DeploymentChannelCatalog.SupportedChannelNames)
            foreach (var verb in new[] { "create", "update" })
                yield return [channel, verb];
    }

    [Theory]
    [MemberData(nameof(ChannelMutations))]
    public void ChannelMutation_OutputModeDoesNotChangeTheOperationRequest(string channel, string verb)
    {
        foreach (var nonInteractive in new[] { false, true })
        {
            string? expectedRequest = null;
            foreach (var outputOptions in new string[][] { [], ["--json"], ["--debug"], ["--json", "--debug"] })
            {
                var operations = new FakeOperations();
                string[] options = nonInteractive ? ["--non-interactive"] : [];
                var (exitCode, _, _) = Run(operations,
                    ["deployment", "channel", verb, "--deployment-id", "dep-1", "--channel", channel,
                        "--parameters", "isEnabled=false", .. outputOptions, .. options]);

                Assert.Equal(ExitCodes.Success, exitCode);
                var request = Assert.IsAssignableFrom<DeploymentChannelCreateRequest>(
                    verb == "create" ? operations.LastCreateChannelRequest : operations.LastUpdateChannelRequest);
                Assert.Equal(nonInteractive, request.NonInteractive);
                var serialized = JsonSerializer.Serialize(request, request.GetType());
                expectedRequest ??= serialized;
                Assert.Equal(expectedRequest, serialized);
            }
        }
    }

    [Theory]
    [InlineData("create", "unknown-channel")]
    [InlineData("show", "lines")]
    [InlineData("show", "m365extensions")]
    [InlineData("show", "outlook")]
    [InlineData("update", "m365extensions")]
    [InlineData("update", "outlook")]
    [InlineData("delete", "directline")]
    public void SelectedChannelHelp_RejectsInvalidSelectionsWithoutDisplayingHelpOrExecuting(string verb, string channel)
    {
        foreach (var json in new[] { false, true })
        {
            var operations = new FakeOperations();
            string[] outputOptions = json ? ["--json"] : [];
            var (exitCode, stdout, stderr) = Run(operations,
                ["deployment", "channel", verb, "--channel", channel, "--help", .. outputOptions]);
            Assert.Equal(ExitCodes.Usage, exitCode);
            var expectedMessage = DeploymentChannelCatalog.SupportedChannelNames.Contains(channel)
                ? $"The '{channel}' channel does not support '{verb}' operation."
                : $"Channel '{channel}' is not supported. Supported values are: {string.Join(", ", DeploymentChannelCatalog.SupportedChannelNames)}.";
            if (json)
            {
                Assert.Empty(stderr);
                using var document = JsonDocument.Parse(stdout);
                Assert.False(document.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(ExitCodes.Usage, document.RootElement.GetProperty("exitCode").GetInt32());
                Assert.Equal(expectedMessage, document.RootElement.GetProperty("errorMessage").GetString());
            }
            else
            {
                Assert.Empty(stdout);
                Assert.Contains(expectedMessage, stderr);
            }

            Assert.DoesNotContain("Usage:", stdout + stderr);
            Assert.DoesNotContain("Parameters:", stdout + stderr);
            Assert.Null(operations.LastCreateChannelRequest);
            Assert.Null(operations.LastUpdateChannelRequest);
            Assert.Null(operations.LastGetChannelRequest);
            Assert.Null(operations.LastDeleteChannelRequest);
        }
    }

    [Theory]
    [InlineData("--add-registration")]
    [InlineData("--set-registration")]
    [InlineData("--remove-registration")]
    [InlineData("-adr")]
    [InlineData("-str")]
    [InlineData("-rmr")]
    public void Update_BindsRegistrationFieldsAndAliases(string option)
    {
        var operations = new FakeOperations();
        var fields = option is "--add-registration" or "-adr"
            ? new[] { "channelSecret=synthetic=secret", "channelAccessToken=synthetic=token" }
            : option is "--set-registration" or "-str"
                ? new[] { "generatedId=one", "channelAccessToken=synthetic=token" }
                : new[] { "generatedId=one" };
        var (exitCode, _, stderr) = Run(operations,
            ["deployment", "channel", "update", "--deployment-id", "dep-1", "--channel", "line", option, .. fields]);
        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<DeploymentChannelUpdateRequest>(operations.LastUpdateChannelRequest);
        Assert.Equal(fields, option switch
        {
            "--add-registration" or "-adr" => request.AddRegistration,
            "--set-registration" or "-str" => request.SetRegistration,
            _ => request.RemoveRegistration,
        });
        Assert.Empty(request.Parameters);
    }

    [Theory]
    [InlineData("--add-registration")]
    [InlineData("--set-registration")]
    [InlineData("--remove-registration")]
    public void Create_RejectsUpdateOnlyRegistrationOptionsWithoutEchoingCredentials(string option)
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(operations,
            "deployment", "channel", "create", "--deployment-id", "dep-1", "--channel", "line",
            option, "channelSecret=SYNTHETIC_SECRET", "channelAccessToken=SYNTHETIC_TOKEN");
        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.DoesNotContain("SYNTHETIC_", stderr);
        Assert.Null(operations.LastCreateChannelRequest);
    }

    [Fact]
    public void Create_RejectingAnItemOptionDoesNotEchoCredentialFields()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(),
            "deployment", "channel", "create", "--deployment-id", "dep-1", "--channel", "facebook",
            "--add-page", "id=page-1", "accessToken=SYNTHETIC_CREDENTIAL");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Option '--add-page' is misspelled or not valid for 'facebook' channel create.", stderr);
        Assert.DoesNotContain("SYNTHETIC_CREDENTIAL", stderr);
        Assert.DoesNotContain("id=page-1", stderr);
    }

    [Theory]
    [InlineData("create", "--set-pages", false)]
    [InlineData("update", "--set-pages", false)]
    [InlineData("update", "--set-pages=SYNTHETIC_INLINE_SECRET", false)]
    [InlineData("update", "--set-pages=SYNTHETIC_INLINE_SECRET", true)]
    [InlineData("create", "-stp", false)]
    public void InvalidOption_ReportsNameAndChannelWithoutValues(string verb, string option, bool json)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(operations,
            ["deployment", "channel", verb, "--deployment-id", "dep-1", "--channel", "Facebook",
                option, "id=page-1", "accessToken=SYNTHETIC_FIELD_SECRET", .. json ? ["--json"] : Array.Empty<string>()]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        var expected = $"Option '{option.Split('=', 2)[0]}' is misspelled or not valid for 'facebook' channel {verb}."
            + " Run `msagent --help` for usage.";
        if (json)
        {
            Assert.Empty(stderr);
            using var document = JsonDocument.Parse(stdout);
            Assert.False(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(expected, document.RootElement.GetProperty("errorMessage").GetString());
        }
        else
        {
            Assert.Empty(stdout);
            Assert.Contains(expected, stderr);
        }
        Assert.DoesNotContain("SYNTHETIC_", stdout + stderr);
        Assert.DoesNotContain("id=page-1", stdout + stderr);
        Assert.Null(operations.LastCreateChannelRequest);
        Assert.Null(operations.LastUpdateChannelRequest);
    }

    [Fact]
    public void InvalidOptions_ReportDistinctNamesWithoutValues()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(),
            "deployment", "channel", "update", "--deployment-id", "dep-1", "--channel", "facebook",
            "--set-pages=FIRST_SECRET", "--add-pages", "accessToken=SECOND_SECRET", "--set-pages=THIRD_SECRET");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(
            "Option '--set-pages' is misspelled or not valid for 'facebook' channel update. " +
            "Option '--add-pages' is misspelled or not valid for 'facebook' channel update. " +
            "Run `msagent --help` for usage.", stderr);
        Assert.DoesNotContain("_SECRET", stderr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("SYNTHETIC_CHANNEL_SECRET")]
    public void InvalidOption_WithoutRecognizedChannel_OmitsChannelValue(string? channel)
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(),
            ["deployment", "channel", "create", "--deployment-id", "dep-1",
                .. channel is null ? Array.Empty<string>() : ["--channel", channel], "--set-pages=INLINE_SECRET"]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Option '--set-pages' is misspelled or not valid for channel create.", stderr);
        Assert.DoesNotContain("SECRET", stderr);
    }

    [Fact]
    public void UnmatchedFields_WithoutOptionName_DoNotEchoValues()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(),
            "deployment", "channel", "update", "--deployment-id", "dep-1", "--channel", "facebook",
            "accessToken=SYNTHETIC_SECRET");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("Invalid channel command input. Check the options and Key=Value fields.", stderr);
        Assert.DoesNotContain("SYNTHETIC_SECRET", stderr);
    }

    [Theory]
    [InlineData("facebook", "--add-page", "id=page-1")]
    [InlineData("facebook", "--set-page", "id=page-1")]
    [InlineData("facebook", "--remove-page", "id=page-1")]
    public void Create_RejectsUpdateOnlyPageOptions(string channel, string option, string field)
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(operations, "deployment", "channel", "create",
            "--deployment-id", "dep-1", "--channel", channel, option, field, "accessToken=SYNTHETIC_SECRET");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.DoesNotContain("SYNTHETIC", stderr);
        Assert.Null(operations.LastCreateChannelRequest);
        Assert.Null(operations.LastUpdateChannelRequest);
    }

    [Theory]
    [InlineData("--add-page")]
    [InlineData("--set-page")]
    [InlineData("--remove-page")]
    [InlineData("-adp")]
    [InlineData("-stp")]
    [InlineData("-rmp")]
    public void Update_BindsPageFieldsWithoutSplittingEmbeddedEquals(string option)
    {
        var operations = new FakeOperations();
        string[] fields = option is "--remove-page" or "-rmp"
            ? ["id=page-1"] : ["id=page-1", "accessToken=synthetic=token"];
        var (exitCode, _, stderr) = Run(operations,
            ["deployment", "channel", "update", "--deployment-id", "dep-1", "--channel", "facebook", option, .. fields]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<DeploymentChannelUpdateRequest>(operations.LastUpdateChannelRequest);
        Assert.Equal(fields, option switch
        {
            "--add-page" or "-adp" => request.AddPage,
            "--set-page" or "-stp" => request.SetPage,
            _ => request.RemovePage,
        });
        Assert.Empty(request.Parameters);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public void Facebook_BindsAllParametersTogether(string verb)
    {
        string[] fields = ["appId=app", "isEnabled=false", "appSecret=synthetic=secret",
            """pages=[{"id":"one","accessToken":"synthetic=token"},{"id":"two","accessToken":"other"}]"""];
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(operations,
            ["deployment", "channel", verb, "--deployment-id", "dep-1", "--channel", "facebook", "--parameters", .. fields]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = verb == "create"
            ? Assert.IsType<DeploymentChannelCreateRequest>(operations.LastCreateChannelRequest)
            : Assert.IsType<DeploymentChannelUpdateRequest>(operations.LastUpdateChannelRequest);
        Assert.Equal(fields, request.Parameters);
    }

    [Theory]
    [InlineData("directline", "update", "--add-site")]
    [InlineData("webchat", "update", "--add-site")]
    [InlineData("directline", "update", "--set-site")]
    [InlineData("webchat", "update", "--set-site")]
    [InlineData("directline", "update", "--remove-site")]
    [InlineData("webchat", "update", "--remove-site")]
    public void Mutations_BindSiteOperationFields(string channel, string verb, string option)
    {
        var operations = new FakeOperations();
        string[] fields = option == "--remove-site"
            ? ["siteName=Primary"]
            : ["siteName=Primary", "appId=value=with=equals"];
        var (exitCode, _, stderr) = Run(operations,
            ["deployment", "channel", verb, "--deployment-id", "dep-1", "--channel", channel, option, .. fields]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<DeploymentChannelUpdateRequest>(operations.LastUpdateChannelRequest);
        var bound = option switch
        {
            "--add-site" => request.AddSite,
            "--set-site" => request.SetSite,
            _ => request.RemoveSite,
        };
        Assert.Equal(fields, bound);
        Assert.Empty(request.Parameters);
    }

    [Theory]
    [InlineData("directline", "create")]
    [InlineData("webchat", "create")]
    [InlineData("directline", "update")]
    [InlineData("webchat", "update")]
    public void Mutations_BindCompleteSitesAssignmentWithoutChangingJson(string channel, string verb)
    {
        const string assignment = """sites=[{"siteName":"Primary","appId":"value=with=equals"},{"siteName":"Secondary"}]""";
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(operations, "deployment", "channel", verb,
            "--deployment-id", "dep-1", "--channel", channel, "--parameters", assignment);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = verb == "create"
            ? Assert.IsType<DeploymentChannelCreateRequest>(operations.LastCreateChannelRequest)
            : Assert.IsType<DeploymentChannelUpdateRequest>(operations.LastUpdateChannelRequest);
        Assert.Equal(assignment, Assert.Single(request.Parameters));
        if (request is DeploymentChannelUpdateRequest update)
        {
            Assert.Empty(update.AddSite);
        }
    }

    [Theory]
    [InlineData("--add-site")]
    [InlineData("--set-site")]
    [InlineData("--remove-site")]
    public void Create_RejectsUpdateOnlySiteOptionsWithoutEchoingValues(string option)
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(operations, "deployment", "channel", "create",
            "--deployment-id", "dep-1", "--channel", "directline",
            option, "siteId=SYNTHETIC_SITE_ID");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.DoesNotContain("SYNTHETIC_SITE_ID", stderr);
        Assert.Null(operations.LastCreateChannelRequest);
    }

    [Theory]
    [InlineData("create", false, false, false)]
    [InlineData("create", true, false, false)]
    [InlineData("create", false, true, false)]
    [InlineData("create", true, true, false)]
    [InlineData("update", false, false, false)]
    [InlineData("update", true, false, false)]
    [InlineData("update", false, true, false)]
    [InlineData("update", true, true, false)]
    [InlineData("create", false, false, true)]
    [InlineData("create", true, false, true)]
    [InlineData("create", false, true, true)]
    [InlineData("create", true, true, true)]
    [InlineData("update", false, false, true)]
    [InlineData("update", true, false, true)]
    [InlineData("update", false, true, true)]
    [InlineData("update", true, true, true)]
    public void SlackAuthorizationAction_RemainsInNormalOutputWhenDebugAddsRedactedDiagnostics(
        string verb, bool json, bool debug, bool nonInteractive)
    {
        var uri = new Uri("https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE");
        var result = new DeploymentResult<DeploymentChannelRecord>
        {
            Status = DeploymentOperationStatus.WriteAccepted,
            TargetName = "Channel",
            TargetInstanceName = "slack",
            AuthorizationUrl = uri,
            Warnings = [DeploymentChannelSlack.AuthorizationGuidance],
            Payload = new()
            {
                ChannelName = "slack",
                ResourceId = "/channels/SlackChannel",
                AuthorizationUrl = uri,
                RawArmResponse = JsonSerializer.SerializeToElement(new { redirectAction = "***" }),
            },
        };
        var operations = new FakeOperations { OnCreateChannel = _ => result, OnUpdateChannel = _ => result };
        string[] options = [.. (json ? new[] { "--json" } : []), .. (debug ? new[] { "--debug" } : []),
            .. (nonInteractive ? new[] { "--non-interactive" } : [])];
        var (exitCode, stdout, stderr) = Run(operations,
            ["deployment", "channel", verb, "--deployment-id", "dep-1", "--channel", "slack",
                "--parameters", "clientId=app", "clientSecret=secret", "signingSecret=sign", .. options]);
        Assert.Equal(ExitCodes.Success, exitCode);
        if (debug)
        {
            Assert.DoesNotContain("SYNTHETIC_STATE", stderr);
            Assert.Contains("ARM response:", stderr);
        }
        else
        {
            Assert.Empty(stderr);
        }
        Assert.Contains("write accepted by Azure", stdout);
        string guidance;
        if (json)
        {
            using var document = JsonDocument.Parse(stdout);
            Assert.Equal(uri.AbsoluteUri, document.RootElement.GetProperty("authorizationUrl").GetString());
            Assert.False(document.RootElement.GetProperty("channel").TryGetProperty("authorizationUrl", out _));
            Assert.False(document.RootElement.TryGetProperty("nextAction", out _));
            Assert.Equal("Channel 'slack' write accepted by Azure.", document.RootElement.GetProperty("message").GetString());
            guidance = Assert.IsType<string>(Assert.Single(document.RootElement.GetProperty("warnings").EnumerateArray()).GetString());
        }
        else
        {
            Assert.Contains("Authorization URL: " + uri.AbsoluteUri, stdout);
            Assert.DoesNotContain("Next:", stdout);
            var warning = Assert.Single(stdout.Split('\n'), line => line.StartsWith("Warning:", StringComparison.Ordinal));
            guidance = warning["Warning: ".Length..].TrimEnd('\r');
        }
        Assert.Equal(DeploymentChannelSlack.AuthorizationGuidance, guidance);
        Assert.DoesNotContain("opened browser", guidance);
    }

    [Theory]
    [InlineData("create", DeploymentOperationStatus.WriteAccepted)]
    [InlineData("update", DeploymentOperationStatus.WriteAccepted)]
    [InlineData("create", DeploymentOperationStatus.Existed)]
    public void SlackWithoutAuthorizationAction_DoesNotClaimAuthorizationIsPending(string verb, DeploymentOperationStatus status)
    {
        var result = new DeploymentResult<DeploymentChannelRecord>
        {
            Status = status,
            TargetName = "Channel",
            TargetInstanceName = "slack",
            Warnings = status == DeploymentOperationStatus.WriteAccepted ? [DeploymentChannelSlack.AuthorizationGuidance] : [],
            Payload = new() { ChannelName = "slack", ResourceId = "/channels/SlackChannel" },
        };
        var operations = new FakeOperations { OnCreateChannel = _ => result, OnUpdateChannel = _ => result };
        var (exitCode, stdout, stderr) = Run(operations,
            "deployment", "channel", verb, "--deployment-id", "dep-1", "--channel", "slack", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        var message = Assert.IsType<string>(root.GetProperty("message").GetString());
        Assert.Contains(status == DeploymentOperationStatus.Existed ? "already exists" : "write accepted by Azure", message);
        Assert.DoesNotContain("authorization", message, StringComparison.OrdinalIgnoreCase);
        if (status == DeploymentOperationStatus.WriteAccepted)
        {
            Assert.Equal(DeploymentChannelSlack.AuthorizationGuidance, Assert.Single(root.GetProperty("warnings").EnumerateArray()).GetString());
        }
        else
        {
            Assert.False(root.TryGetProperty("warnings", out _));
        }
        Assert.False(root.TryGetProperty("nextAction", out _));
        Assert.False(root.TryGetProperty("authorizationUrl", out _));
    }

    [Theory]
    [InlineData("create", false, false)]
    [InlineData("create", true, false)]
    [InlineData("create", false, true)]
    [InlineData("create", true, true)]
    [InlineData("update", false, false)]
    [InlineData("update", true, false)]
    [InlineData("update", false, true)]
    [InlineData("update", true, true)]
    public void ConfigurationWarning_IsVisibleWithoutFailingTheCommand(string verb, bool json, bool debug)
    {
        const string warning = "Sites were created, but configuration did not complete. Sites may remain at service defaults.";
        var result = new DeploymentResult<DeploymentChannelRecord>
        {
            Status = verb == "create" ? DeploymentOperationStatus.Created : DeploymentOperationStatus.Updated,
            TargetName = "Channel",
            TargetInstanceName = "webchat",
            Warnings = [warning],
            Payload = new()
            {
                ChannelName = "webchat",
                ResourceId = "/bots/test/channels/WebChatChannel",
                RawArmResponse = JsonSerializer.SerializeToElement(new { properties = new { siteName = "Primary" } }),
                Parameters = new Dictionary<string, JsonElement>
                {
                    ["sites"] = JsonSerializer.SerializeToElement(new[] { new { siteName = "Primary" } }),
                },
            },
        };
        var operations = new FakeOperations
        {
            OnCreateChannel = _ => result,
            OnUpdateChannel = _ => result,
        };
        string[] options = [.. (json ? new[] { "--json" } : []), .. (debug ? new[] { "--debug" } : [])];
        var (exitCode, stdout, stderr) = Run(operations,
            ["deployment", "channel", verb, "--deployment-id", "dep-1", "--channel", "webchat",
                "--parameters", """sites=[{"siteName":"Primary","isEnabled":false}]""", .. options]);

        Assert.Equal(ExitCodes.Success, exitCode);
        if (debug)
        {
            Assert.Contains("\"siteName\": \"Primary\"", stderr);
            Assert.DoesNotContain(warning, stderr);
        }
        else
        {
            Assert.Empty(stderr);
        }
        if (json)
        {
            using var document = JsonDocument.Parse(stdout);
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(warning, Assert.Single(document.RootElement.GetProperty("warnings").EnumerateArray()).GetString());
            Assert.Equal("Primary", document.RootElement.GetProperty("channel").GetProperty("parameters")
                .GetProperty("sites")[0].GetProperty("siteName").GetString());
        }
        else
        {
            Assert.Contains("Warning: " + warning, stdout);
            Assert.Contains("Primary", stdout);
        }
    }

    [Fact]
    public void Show_BindsAgentAndNameAddressing()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            "deployment", "channel", "show",
            "--agent-id", "agent-1",
            "--deployment-name", "dev",
            "--channel", "msteams");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<DeploymentChannelShowRequest>(operations.LastGetChannelRequest);
        Assert.Null(request.DeploymentId);
        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal("dev", request.DeploymentName);
    }

    [Fact]
    public void Show_RejectsMcsDeployment()
    {
        var operations = new FakeOperations
        {
            OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult
            {
                AgentType = AgentTypes.McsAgent,
                Source = DeploymentAgentTypeSource.DeploymentRecord,
            },
        };

        var (exitCode, _, stderr) = Run(
            operations,
            "deployment", "channel", "show",
            "--deployment-id", "dep-1",
            "--channel", "msteams");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(
            "`msagent deployment channel` applies to a CodeAgent deployment",
            stderr);
        Assert.Null(operations.LastGetChannelRequest);
        Assert.Equal("dep-1", operations.LastDeploymentAgentTypeQuery!.DeploymentId);
    }

    [Fact]
    public void Show_DoesNotAcceptParameters()
    {
        var (exitCode, _, stderr) = Run(
            new FakeOperations(),
            "deployment", "channel", "show",
            "--deployment-id", "dep-1",
            "--channel", "msteams",
            "--parameters", "enableCalling=true");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--parameters", stderr);
    }

    [Theory]
    [InlineData("m365extensions")]
    [InlineData("outlook")]
    public void CreateHelp_IncludesPropertylessChannel(string channel)
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            "deployment", "channel", "create", "--channel", channel, "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains(channel, stdout);
        Assert.DoesNotContain("Parameters:", stdout);
        Assert.DoesNotContain("None.", stdout);
    }

    [Fact]
    public void CreateHelp_ShowsGenericParametersAndNoTypedChannelOptions()
    {
        var (exitCode, stdout, _) = Run(
            new FakeOperations(),
            "deployment", "channel", "create", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("--parameters", stdout);
        Assert.Contains("--deployment-id", stdout);
        Assert.Contains("--agent-id", stdout);
        Assert.Contains("--deployment-name", stdout);
        Assert.DoesNotContain("--client-secret", stdout);
        Assert.DoesNotContain("--enable-calling", stdout);
        Assert.Contains("Specify --channel <name>", stdout);
    }

    [Theory]
    [InlineData("msteams", "enableCalling", "<Boolean>", "Whether calling is enabled.")]
    [InlineData("msteams", "deploymentEnvironment", "<String>", "Default: CommercialDeployment.")]
    [InlineData("slack", "clientSecret", "<String>", "The Slack client secret.")]
    [InlineData("directline", "appId", "<String>", "The application id associated with the channel site.")]
    [InlineData("webchat", "appId", "<String>", "The application id associated with the channel site.")]
    public void MutationHelp_AppendsCatalogParameters(
        string channel,
        string parameter,
        string firstMarker,
        string secondMarker)
    {
        foreach (var verb in new[] { "create", "update" })
        {
            var (exitCode, stdout, stderr) = Run(
                new FakeOperations(),
                "deployment", "channel", verb,
                "--channel", channel,
                "--help");

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Empty(stderr);
            Assert.Contains("Parameters:", stdout);
            Assert.Contains(parameter, stdout);
            Assert.Contains(firstMarker, stdout);
            Assert.Contains(secondMarker, stdout);
            Assert.DoesNotContain("Clearable on update.", stdout);
            Assert.DoesNotContain(" Secret.", stdout);
        }
    }

    [Fact]
    public void List_DoesNotAcceptChannelOrParametersOptions()
    {
        var (exitCode, _, stderr) = Run(
            new FakeOperations(),
            "deployment", "channel", "list",
            "--deployment-id", "dep-1",
            "--channel", "slack");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--channel", stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void List_Debug_AddsFullArmResponseToFilteredOutput(bool json)
    {
        using var teamsDocument = System.Text.Json.JsonDocument.Parse(
            """{"id":"/channels/MsTeamsChannel","properties":{"channelName":"MsTeamsChannel"}}""");
        using var slackDocument = System.Text.Json.JsonDocument.Parse(
            """{"id":"/channels/SlackChannel","properties":{"channelName":"SlackChannel","properties":{"clientSecret":"***"}}}""");
        using var unknownDocument = System.Text.Json.JsonDocument.Parse(
            """
            {"id":"/channels/UnknownArmChannel","properties":{"channelName":"UnknownArmChannel",
              "properties":{"description":"visible","entries":[{"accessToken":"SYNTHETIC_SECRET"}]}}}
            """);
        var operations = new FakeOperations
        {
            OnListChannels = _ => new DeploymentResult<DeploymentChannelRecord[]>
            {
                Status = DeploymentOperationStatus.Listed,
                TargetName = "Channels",
                Payload =
                [
                    new()
                    {
                        ResourceId = "/channels/MsTeamsChannel",
                        ChannelName = "msteams",
                        RawArmResponse = teamsDocument.RootElement.Clone(),
                    },
                    new()
                    {
                        ResourceId = "/channels/SlackChannel",
                        ChannelName = "slack",
                        RawArmResponse = slackDocument.RootElement.Clone(),
                    },
                ],
                ArmResponse = JsonSerializer.SerializeToElement(new[]
                {
                    teamsDocument.RootElement,
                    slackDocument.RootElement,
                    BotServiceResponseRedactor.RedactChannel(unknownDocument.RootElement, definition: null),
                }),
            },
        };

        string[] outputOptions = json ? ["--json"] : [];
        var (exitCode, stdout, stderr) = Run(
            operations,
            ["deployment", "channel", "list",
                "--deployment-id", "dep-1",
                "--debug", .. outputOptions]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("/channels/MsTeamsChannel", stdout);
        Assert.Contains("/channels/SlackChannel", stdout);
        Assert.DoesNotContain("UnknownArmChannel", stdout);
        Assert.DoesNotContain("SYNTHETIC_SECRET", stdout);
        Assert.StartsWith("ARM response:", stderr);
        Assert.Contains("ARM response:", stderr);
        Assert.Contains("/channels/MsTeamsChannel", stderr);
        Assert.Contains("/channels/SlackChannel", stderr);
        Assert.Contains("/channels/UnknownArmChannel", stderr);
        Assert.DoesNotContain("SYNTHETIC_SECRET", stderr);

        var response = stderr[stderr.IndexOf('[')..];
        using var document = System.Text.Json.JsonDocument.Parse(response);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(3, document.RootElement.GetArrayLength());
        var unknown = document.RootElement[2].GetProperty("properties").GetProperty("properties");
        Assert.Equal("visible", unknown.GetProperty("description").GetString());
        Assert.Equal("***", unknown.GetProperty("entries")[0].GetProperty("accessToken").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void List_ExcludesDiagnosticOnlyChannelsFromNormalOutput(bool json)
    {
        var operations = new FakeOperations
        {
            OnListChannels = _ => new DeploymentResult<DeploymentChannelRecord[]>
            {
                Status = DeploymentOperationStatus.Listed,
                TargetName = "Channels",
                Payload =
                [
                    new() { ResourceId = "/channels/SlackChannel", ChannelName = "slack" },
                ],
                ArmResponse = JsonSerializer.SerializeToElement(new[]
                {
                    new { id = "/channels/SlackChannel" },
                    new { id = "/channels/UnknownArmChannel" },
                }),
            },
        };
        var args = new List<string> { "deployment", "channel", "list", "--deployment-id", "dep-1" };
        if (json)
        {
            args.Add("--json");
        }

        var (exitCode, stdout, stderr) = Run(operations, args.ToArray());

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        using var document = System.Text.Json.JsonDocument.Parse(stdout);
        if (json)
        {
            Assert.False(document.RootElement.TryGetProperty("warnings", out _));
        }

        var channels = json ? document.RootElement.GetProperty("channels") : document.RootElement;
        Assert.Single(channels.EnumerateArray());
        Assert.False(channels[0].TryGetProperty("isSupported", out _));
        Assert.Equal("slack", channels[0].GetProperty("channelName").GetString());
        Assert.DoesNotContain("UnknownArmChannel", stdout);
    }
}
