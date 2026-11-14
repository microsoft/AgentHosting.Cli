using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Command-host tests. Each drives the full parse → dispatch → render → exit pipeline through
/// <see cref="AgentHostCli"/> with a <see cref="FakeOperations"/> facade and in-memory writers, so the
/// output/error envelopes and the frozen exit taxonomy are exercised offline — no MSAL, broker,
/// browser, or Azure transport is reachable from the host.
/// </summary>
public class CommandHostTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(
        FakeOperations operations,
        bool color,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(operations, featureFlags: TestFeatureFlags.AllProductSurfaces);
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void NoArgs_ShowsHelp_ExitZero()
    {
        var (exitCode, stdout, stderr) = Run(new FakeOperations(), color: false);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Usage:", stdout);
        Assert.Contains("msagent [command] [options]", stdout);
        var normalizedStdout = Regex.Replace(stdout, @"\s+", " ");
        Assert.Contains("Microsoft AgentHosting CLI", normalizedStdout);
        var informationalVersion = typeof(AgentHostCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion;
        Assert.Contains(
            $"Version: {informationalVersion} ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription})",
            normalizedStdout);
        Assert.Contains(
            "Create, Edit, Deploy & Test Copilot Studio Agents locally",
            normalizedStdout);
        Assert.DoesNotContain("Microsoft.Agents.Hosting.Cli [command] [options]", stdout);
        Assert.Contains("auth", stdout);
        Assert.Contains("abs", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void NestedHelp_UsesPackagedCommandName()
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            color: false,
            "auth",
            "login",
            "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("msagent auth login [options]", stdout);
        Assert.DoesNotContain("Microsoft.Agents.Hosting.Cli auth login", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void VersionFlag_ExitsZero()
    {
        // System.CommandLine's --version prints the entry assembly's informational version. Under the
        // test host that is the runner, not the tool, so the flag's contract (exit 0, non-empty) is
        // asserted here and the tool's own version identity is asserted separately below.
        var (exitCode, stdout, _) = Run(new FakeOperations(), color: false, "--version");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stdout));
    }

    [Fact]
    public void CliAssembly_CarriesInformationalVersion()
    {
        var informationalVersion = typeof(AgentHostCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.NotNull(informationalVersion);
    }

    [Fact]
    public void UnknownCommand_IsUsageError_ExitTwo()
    {
        var (exitCode, stdout, stderr) = Run(new FakeOperations(), color: false, "bogus");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.NotEmpty(stderr);
        Assert.Empty(stdout);
    }

    [Fact]
    public void GroupWithoutVerb_IsUsageError_ExitTwo()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), color: false, "auth");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("auth", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SwitchMissingRequiredAccount_IsUsageError_ExitTwo()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), color: false, "auth", "switch");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--account", stderr);
    }

    [Fact]
    public void JsonSuccess_EmitsFlattenedEnvelopeToStdout_ExitZero()
    {
        var operations = new FakeOperations
        {
            OnStatus = () => new AuthStatusReport
            {
                Status = "signed-in",
                SignedIn = true,
                Account = "op@contoso.com",
                TenantId = "tenant-1",
            },
        };

        var (exitCode, stdout, stderr) = Run(operations, color: false, "auth", "status", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("signed-in", root.GetProperty("status").GetString());
        Assert.Equal("op@contoso.com", root.GetProperty("account").GetString());
        Assert.Equal("tenant-1", root.GetProperty("tenantId").GetString());

        // "success" must be the first property in the envelope.
        Assert.Equal("success", root.EnumerateObject().First().Name);
    }

    [Fact]
    public void JsonSuccess_ScalarPayload_IsWrappedUnderResult()
    {
        var stdout = new StringWriter();
        var renderer = new CliOutputRenderer(stdout, new StringWriter(), color: false);

        renderer.RenderSuccess("complete", json: true);

        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("complete", root.GetProperty("result").GetString());
    }

    [Theory]
    [InlineData("slack", false)]
    [InlineData("slack", true)]
    [InlineData("webchat", false)]
    [InlineData("webchat", true)]
    public void DebugSuccess_WritesArmResponseBeforeNormalMessagesWarningsAndActions(string channel, bool json)
    {
        const string warning = "Review the requested configuration.";
        var result = new DeploymentResult<DeploymentChannelRecord>
        {
            Status = DeploymentOperationStatus.Updated,
            TargetName = "Channel",
            TargetInstanceName = channel,
            Warnings = [warning],
            AuthorizationUrl = channel == "slack" ? new Uri("https://slack.com/oauth/v2/authorize?state=SYNTHETIC_STATE") : null,
            Payload = new()
            {
                ChannelName = channel,
                ResourceId = $"/channels/{channel}",
                RawArmResponse = JsonSerializer.SerializeToElement(new { lastDiagnostic = "ARM_END" }),
            },
        };
        var output = new StringWriter();
        var renderer = new CliOutputRenderer(output, output, color: false);

        renderer.RenderSuccess(result, json, debug: true);

        var text = output.ToString();
        Assert.StartsWith("ARM response:", text);
        var diagnosticEnd = text.IndexOf("ARM_END", StringComparison.Ordinal);
        var normalStart = text.IndexOf(json ? "\"success\": true" : $"Channel '{channel}'", StringComparison.Ordinal);
        Assert.True(normalStart > diagnosticEnd, "Normal output must follow the ARM diagnostics.");
        Assert.True(text.IndexOf(warning, StringComparison.Ordinal) > normalStart);
        if (channel == "slack")
        {
            Assert.True(text.IndexOf(json ? "\"authorizationUrl\":" : "Authorization URL:", StringComparison.Ordinal) > normalStart);
            Assert.True(text.IndexOf("SYNTHETIC_STATE", StringComparison.Ordinal) > normalStart);
        }
    }

    [Fact]
    public void JsonFailure_TypedException_MapsExitAndEnvelope()
    {
        var operations = new FakeOperations
        {
            OnStatus = () => throw new NotSignedInException(
                "No signed-in account.", remediation: "Run `msagent auth login`."),
        };

        var (exitCode, stdout, stderr) = Run(operations, color: false, "auth", "status", "--json");

        Assert.Equal(ExitCodes.NotSignedIn, exitCode);
        Assert.Empty(stderr);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(ExitCodes.NotSignedIn, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("No signed-in account.", root.GetProperty("errorMessage").GetString());
        Assert.Equal("Run `msagent auth login`.", root.GetProperty("remediation").GetString());
        Assert.False(root.TryGetProperty("errorKind", out _));
    }

    [Fact]
    public void JsonFailure_ProvisioningException_CarriesKindAndTaxonomyExit()
    {
        var operations = new FakeOperations
        {
            OnPreview = _ => throw new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                "No subscription resolved.",
                "Pass --subscription."),
        };

        var (exitCode, stdout, _) = Run(operations, color: false, "abs", "preview", "--json");

        Assert.Equal(ExitCodes.Usage, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(ExitCodes.Usage, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("missing-context", root.GetProperty("errorKind").GetString());
        Assert.Equal("Pass --subscription.", root.GetProperty("remediation").GetString());
    }

    [Fact]
    public void GenericException_MapsToFailureExitOne()
    {
        var operations = new FakeOperations
        {
            OnStatus = () => throw new InvalidOperationException("boom"),
        };

        var (exitCode, stdout, _) = Run(operations, color: false, "auth", "status", "--json");

        Assert.Equal(ExitCodes.Failure, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(ExitCodes.Failure, root.GetProperty("exitCode").GetInt32());
        Assert.False(root.TryGetProperty("errorKind", out _));
    }

    [Fact]
    public void TransportTimeout_MapsToStructuredFailure()
    {
        var operations = new FakeOperations
        {
            OnStatus = () => throw new TaskCanceledException("transport-internal-detail"),
        };

        var (exitCode, stdout, _) = Run(operations, color: false, "auth", "status", "--json");

        Assert.Equal(ExitCodes.Failure, exitCode);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("transport-timeout", root.GetProperty("errorKind").GetString());
        Assert.DoesNotContain("transport-internal-detail", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesWithoutRenderingATimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new FakeOperations
        {
            OnStatusAsync = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<AuthStatusReport>(token);
            },
        };
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new AgentHostCli(operations).InvokeAsync(
                ["auth", "status", "--json"],
                stdout,
                stderr,
                color: false,
                cancellation.Token));

        Assert.Empty(stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void HumanSuccess_GoesToStdout_HumanFailure_GoesToStderr()
    {
        var success = new FakeOperations
        {
            OnStatus = () => new AuthStatusReport { Status = "signed-out", SignedIn = false },
        };
        var (successExit, successStdout, successStderr) = Run(success, color: false, "auth", "status");
        Assert.Equal(ExitCodes.Success, successExit);
        Assert.Contains("Not signed in", successStdout);
        Assert.Empty(successStderr);

        var failure = new FakeOperations
        {
            OnStatus = () => throw new NotSignedInException("No account.", remediation: "Run login."),
        };
        var (failureExit, failureStdout, failureStderr) = Run(failure, color: false, "auth", "status");
        Assert.Equal(ExitCodes.NotSignedIn, failureExit);
        Assert.Empty(failureStdout);
        Assert.Contains("No account.", failureStderr);
        Assert.Contains("Run login.", failureStderr);
    }

    [Fact]
    public void ColorSuppression_ControlsAnsiEscape()
    {
        var operations = new FakeOperations
        {
            OnStatus = () => throw new NotSignedInException("No account."),
        };

        var (_, _, plainStderr) = Run(operations, color: false, "auth", "status");
        Assert.False(plainStderr.Contains('\u001b'));

        var (_, _, coloredStderr) = Run(operations, color: true, "auth", "status");
        Assert.True(coloredStderr.Contains('\u001b'));
    }

    [Fact]
    public void AbsFlags_BindRecursivelyOnGroup_ToBothVerbs()
    {
        var preview = new FakeOperations();
        Run(preview, color: false,
            "abs", "preview", "--tenant", "t1", "--subscription", "s1", "--bot-name", "b1");
        Assert.NotNull(preview.LastAbsRequest);
        Assert.Equal("t1", preview.LastAbsRequest!.Tenant);
        Assert.Equal("s1", preview.LastAbsRequest.Subscription);
        Assert.Equal("b1", preview.LastAbsRequest.BotName);
        Assert.False(preview.LastAbsRequest.Force);

        var apply = new FakeOperations();
        Run(apply, color: false, "abs", "apply", "--bot-name", "b2", "--force");
        Assert.NotNull(apply.LastAbsRequest);
        Assert.Equal("b2", apply.LastAbsRequest!.BotName);
        Assert.True(apply.LastAbsRequest.Force);
    }

    [Fact]
    public void GlobalFlagsBindBeforeAndAfterVerb()
    {
        var operations = new FakeOperations
        {
            OnStatus = () => new AuthStatusReport { Status = "signed-out", SignedIn = false },
        };

        var (beforeExit, beforeStdout, _) = Run(operations, color: false, "--json", "auth", "status");
        var (afterExit, afterStdout, _) = Run(operations, color: false, "auth", "status", "--json");

        Assert.Equal(ExitCodes.Success, beforeExit);
        Assert.Equal(ExitCodes.Success, afterExit);
        using (var before = JsonDocument.Parse(beforeStdout))
        {
            Assert.True(before.RootElement.GetProperty("success").GetBoolean());
        }

        using var after = JsonDocument.Parse(afterStdout);
        Assert.True(after.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void AuthLogin_ExplicitScopesBindWithoutResourceNormalization()
    {
        var operations = new FakeOperations();

        Run(
            operations,
            color: false,
            "auth",
            "login",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--scope",
            "https://graph.microsoft.com/AgentIdentityBlueprint.ReadWrite.All",
            "--scope",
            "https://graph.microsoft.com/AgentIdentityBlueprintPrincipal.Create");

        Assert.NotNull(operations.LastLoginRequest);
        Assert.Equal(
            [
                "https://graph.microsoft.com/AgentIdentityBlueprint.ReadWrite.All",
                "https://graph.microsoft.com/AgentIdentityBlueprintPrincipal.Create",
            ],
            operations.LastLoginRequest!.Scopes);
    }

    [Fact]
    public void IdentityBlueprintHelp_ExposesApprovedCommandContract()
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            color: false,
            "identity",
            "create",
            "blueprint",
            "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("msagent identity create blueprint --tenant <tenant> --display-name <display-name> --auth-type <auth-type> --config-format <config-format> [options]", stdout);
        Assert.Contains("--tenant", stdout);
        Assert.Contains("--display-name", stdout);
        Assert.Contains("--auth-type", stdout);
        Assert.Contains("--config-format", stdout);
        Assert.Contains("--oauth", stdout);
        Assert.Contains("--redirect-uri", stdout);
        Assert.Contains("--teams-sso", stdout);
        Assert.Contains("--observability", stdout);
        Assert.Contains("--mcp-discovery", stdout);
        Assert.Contains("--subscription", stdout);
        Assert.Contains("--resource-group", stdout);
        Assert.Contains("--location", stdout);
        Assert.Contains("--managed-identity-name", stdout);
        Assert.Contains("--dry-run", stdout);
    }

    [Fact]
    public void IdentityBlueprint_RedirectWithoutOAuth_IsUsageError()
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            color: false,
            "identity",
            "create",
            "blueprint",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
            "--redirect-uri",
            "https://token.botframework.com/.auth/web/redirect");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("--redirect-uri requires --oauth", stderr);
    }

    [Fact]
    public void IdentityBlueprint_FederatedCredentialsRequireManagedIdentityCoordinates()
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            color: false,
            "identity",
            "create",
            "blueprint",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "FederatedCredentials",
            "--config-format",
            "node");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains(
            "FederatedCredentials requires --subscription, --resource-group, --location, and --managed-identity-name",
            stderr);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("FederatedCredentials,ClientSecret")]
    public void IdentityBlueprint_RejectsUnsupportedAuthTypeSpellings(string value)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "blueprint",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            value,
            "--config-format",
            "dotnet");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("--auth-type must be FederatedCredentials or ClientSecret", stderr);
        Assert.Null(operations.LastBlueprintIdentityRequest);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("DotNet,Node")]
    public void IdentityBlueprint_RejectsUnsupportedConfigFormatSpellings(string value)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "blueprint",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("--config-format must be dotnet or node", stderr);
        Assert.Null(operations.LastBlueprintIdentityRequest);
    }

    [Fact]
    public void IdentityBlueprint_AllOptionsBindToCoreRequest()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "blueprint",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "FederatedCredentials",
            "--config-format",
            "node",
            "--subscription",
            "sub",
            "--resource-group",
            "rg",
            "--location",
            "westus",
            "--managed-identity-name",
            "agent-mi",
            "--oauth",
            "--redirect-uri",
            "https://contoso.example/callback",
            "--teams-sso",
            "--observability",
            "--mcp-discovery",
            "--dry-run",
            "--non-interactive");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<BlueprintIdentityRequest>(operations.LastBlueprintIdentityRequest);
        Assert.Equal(IdentityCredentialType.FederatedCredentials, request.AuthType);
        Assert.Equal(IdentityConfigFormat.Node, request.ConfigFormat);
        Assert.Equal("sub", request.SubscriptionId);
        Assert.Equal("rg", request.ResourceGroup);
        Assert.Equal("westus", request.Location);
        Assert.Equal("agent-mi", request.ManagedIdentityName);
        Assert.Equal("https://contoso.example/callback", request.RedirectUri);
        Assert.True(request.OAuth);
        Assert.True(request.TeamsSso);
        Assert.True(request.Observability);
        Assert.True(request.McpDiscovery);
        Assert.True(request.DryRun);
        Assert.True(request.NonInteractive);
    }

    [Theory]
    [InlineData(
        false,
        "One-time client secret (store securely; shown once): one-time-secret",
        "Generated dotnet configuration:")]
    [InlineData(
        true,
        "\"oneTimeClientSecret\": \"one-time-secret\"",
        "\"configuration\":")]
    public void IdentityBlueprint_RendersOneTimeSecretOutsideGeneratedConfiguration(
        bool json,
        string expectedSecretShape,
        string expectedConfigurationShape)
    {
        var operations = new FakeOperations
        {
            OnCreateBlueprintIdentity = request => new BlueprintIdentityReport
            {
                Status = "created",
                TenantId = request.TenantId,
                DisplayName = request.DisplayName,
                ApplicationId = "app-id",
                OneTimeClientSecret = "one-time-secret",
                AdminConsentUrl =
                    "https://login.microsoftonline.com/tenant/v2.0/adminconsent?client_id=app-id",
                Changes = [],
                NextActions =
                [
                    "Open the tenant-wide admin-consent URL: " +
                    "https://login.microsoftonline.com/tenant/v2.0/adminconsent?client_id=app-id",
                ],
                Configuration = new IdentityGeneratedConfig
                {
                    Format = "dotnet",
                    Content = """{"AuthType":"ClientSecret","ClientId":"app-id"}""",
                },
            },
        };
        var arguments = new List<string>
        {
            "identity",
            "create",
            "blueprint",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
        };
        if (json)
        {
            arguments.Add("--json");
        }

        var (exitCode, stdout, stderr) = Run(operations, color: false, arguments.ToArray());

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains(expectedSecretShape, stdout);
        Assert.Contains(expectedConfigurationShape, stdout);
        Assert.Contains("https://login.microsoftonline.com/tenant/v2.0/adminconsent", stdout);
        Assert.Equal(1, stdout.Split("one-time-secret", StringSplitOptions.None).Length - 1);
        if (json)
        {
            Assert.Contains("\"adminConsentUrl\":", stdout);
        }
    }

    [Fact]
    public void IdentityApp_RendersClientSecretExpiry()
    {
        var expiresOn = DateTimeOffset.Parse("2027-07-31T00:00:00Z");
        var operations = new FakeOperations
        {
            OnCreateAppRegistrationIdentity = request => new AppRegistrationIdentityReport
            {
                Status = "created",
                TenantId = request.TenantId,
                DisplayName = request.DisplayName,
                ClientSecretExpiresOn = expiresOn,
                Changes = [],
                NextActions = [],
            },
        };

        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains($"Client secret expires on: {expiresOn:O}", stdout);
    }

    [Theory]
    [InlineData(
        false,
        "One-time client secret (store securely; shown once): one-time-secret",
        "Generated dotnet configuration:")]
    [InlineData(
        true,
        "\"oneTimeClientSecret\": \"one-time-secret\"",
        "\"configuration\":")]
    public void IdentityApp_RendersOneTimeSecretOutsideGeneratedConfiguration(
        bool json,
        string expectedSecretShape,
        string expectedConfigurationShape)
    {
        var operations = new FakeOperations
        {
            OnCreateAppRegistrationIdentity = request => new AppRegistrationIdentityReport
            {
                Status = "created",
                TenantId = request.TenantId,
                DisplayName = request.DisplayName,
                OneTimeClientSecret = "one-time-secret",
                Changes = [],
                NextActions = [],
                Configuration = new IdentityGeneratedConfig
                {
                    Format = "dotnet",
                    Content = """{"AuthType":"ClientSecret","ClientId":"app-id"}""",
                },
            },
        };
        var arguments = new List<string>
        {
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
        };
        if (json)
        {
            arguments.Add("--json");
        }

        var (exitCode, stdout, stderr) = Run(operations, color: false, arguments.ToArray());

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains(expectedSecretShape, stdout);
        Assert.Contains(expectedConfigurationShape, stdout);
        Assert.Equal(1, stdout.Split("one-time-secret", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void IdentityBlueprint_JsonPartialFailure_RetainsFailureExitAndProgress()
    {
        var operations = new FakeOperations
        {
            OnCreateBlueprintIdentity = _ => throw PartialBlueprintFailure(),
        };

        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "blueprint",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
            "--json");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("permission-denied", root.GetProperty("errorKind").GetString());
        var partial = root.GetProperty("partialResult");
        Assert.Equal("partial", partial.GetProperty("status").GetString());
        Assert.Equal("app-id", partial.GetProperty("applicationId").GetString());
        Assert.Equal("app-object-id", partial.GetProperty("applicationObjectId").GetString());
        Assert.Equal(JsonValueKind.Null, partial.GetProperty("configuration").ValueKind);
        Assert.DoesNotContain("secret", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityBlueprint_HumanPartialFailure_RendersProgressToStderr()
    {
        var operations = new FakeOperations
        {
            OnCreateBlueprintIdentity = _ => throw PartialBlueprintFailure(),
        };

        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "blueprint",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("Grant failed.", stderr);
        Assert.Contains("Blueprint identity partial: Contoso Agent", stderr);
        Assert.Contains("Application ID: app-id", stderr);
        Assert.Contains("Next: Grant consent, then rerun.", stderr);
    }

    private static BlueprintIdentityProvisioningException PartialBlueprintFailure() =>
        new(
            new TargetRequestException(
                "Grant failed.",
                ExitCodes.Failure,
                TargetErrorKind.PermissionDenied,
                status: 403),
            new BlueprintIdentityReport
            {
                Status = "partial",
                TenantId = "11111111-1111-1111-1111-111111111111",
                DisplayName = "Contoso Agent",
                ApplicationId = "app-id",
                ApplicationObjectId = "app-object-id",
                Changes =
                [
                    new IdentityChange
                    {
                        Resource = "Blueprint",
                        Action = "create",
                        Detail = "Created Blueprint application app-id.",
                    },
                ],
                NextActions = ["Grant consent, then rerun."],
            });

    [Fact]
    public void IdentityAppHelp_ExposesApprovedCommandContract()
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            color: false,
            "identity",
            "create",
            "app",
            "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("msagent identity create app --tenant <tenant> --display-name <display-name> --auth-type <auth-type> --config-format <config-format> [options]", stdout);
        Assert.Contains("--tenant", stdout);
        Assert.Contains("--display-name", stdout);
        Assert.Contains("--auth-type", stdout);
        Assert.Contains("--config-format", stdout);
        Assert.Contains("--app-id", stdout);
        Assert.Contains("--object-id", stdout);
        Assert.Contains("--oauth", stdout);
        Assert.Contains("--redirect-uri", stdout);
        Assert.Contains("--teams-sso", stdout);
        Assert.Contains("--observability", stdout);
        Assert.Contains("--mcp-discovery", stdout);
        Assert.Contains("--subscription", stdout);
        Assert.Contains("--resource-group", stdout);
        Assert.Contains("--location", stdout);
        Assert.Contains("--managed-identity-name", stdout);
        Assert.Contains("--dry-run", stdout);
    }

    [Fact]
    public void IdentityApp_RedirectWithoutOAuthOrSso_IsUsageError()
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
            "--redirect-uri",
            "https://token.botframework.com/.auth/web/redirect");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("--redirect-uri requires --oauth or --teams-sso", stderr);
        Assert.Null(operations.LastAppRegistrationIdentityRequest);
    }

    [Fact]
    public void IdentityApp_RedirectWithTeamsSso_IsAllowed()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
            "--teams-sso",
            "--redirect-uri",
            "https://contoso.example/callback");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<AppRegistrationIdentityRequest>(
            operations.LastAppRegistrationIdentityRequest);
        Assert.False(request.OAuth);
        Assert.True(request.TeamsSso);
        Assert.Equal("https://contoso.example/callback", request.RedirectUri);
    }

    [Theory]
    [InlineData("UserManagedIdentity")]
    [InlineData("FederatedCredentials")]
    public void IdentityApp_ManagedIdentityAuthTypesRequireCoordinates(string authType)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            authType,
            "--config-format",
            "node");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains(
            $"{authType} requires --subscription, --resource-group, --location, and --managed-identity-name",
            stderr);
        Assert.Null(operations.LastAppRegistrationIdentityRequest);
    }

    [Fact]
    public void IdentityApp_ClientSecretRejectsManagedIdentityCoordinates()
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
            "--subscription",
            "22222222-2222-2222-2222-222222222222");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("ClientSecret must not be combined with", stderr);
        Assert.Null(operations.LastAppRegistrationIdentityRequest);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("ManagedIdentity")]
    public void IdentityApp_RejectsUnsupportedAuthTypeSpellings(string value)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            value,
            "--config-format",
            "dotnet");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains(
            "--auth-type must be UserManagedIdentity, FederatedCredentials, or ClientSecret",
            stderr);
        Assert.Null(operations.LastAppRegistrationIdentityRequest);
    }

    [Theory]
    [InlineData("--app-id")]
    [InlineData("--object-id")]
    public void IdentityApp_RejectsMalformedTargetIds(string option)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
            option,
            "not-a-guid");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("must be a valid", stderr);
        Assert.Null(operations.LastAppRegistrationIdentityRequest);
    }

    [Fact]
    public void IdentityApp_RejectsConflictingApplicationSelectors()
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
            "--app-id",
            "33333333-3333-3333-3333-333333333333",
            "--object-id",
            "44444444-4444-4444-4444-444444444444");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("--app-id and --object-id cannot be used together", stderr);
        Assert.Null(operations.LastAppRegistrationIdentityRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IdentityApp_ConsentRequired_IsANonzeroFailureWithPartialResult(bool json)
    {
        var report = new AppRegistrationIdentityReport
        {
            Status = "consent-required",
            TenantId = "11111111-1111-1111-1111-111111111111",
            DisplayName = "Contoso Agent",
            AdminConsentUrl = "https://login.microsoftonline.com/tenant/v2.0/adminconsent",
            Changes = [],
            NextActions = ["Grant consent, then rerun."],
        };
        var operations = new FakeOperations
        {
            OnCreateAppRegistrationIdentity = _ => throw new AppRegistrationIdentityProvisioningException(
                new InteractionRequiredException(
                    "Microsoft Graph consent is required.",
                    remediation: "Grant consent, then rerun.",
                    resource: InteractionRequiredResource.MicrosoftGraph),
                report),
        };
        var arguments = new List<string>
        {
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "ClientSecret",
            "--config-format",
            "dotnet",
        };
        if (json)
        {
            arguments.Add("--json");
        }

        var (exitCode, stdout, stderr) = Run(operations, color: false, arguments.ToArray());

        Assert.Equal(ExitCodes.NotSignedIn, exitCode);
        if (json)
        {
            Assert.Empty(stderr);
            Assert.Contains("\"success\": false", stdout);
            Assert.Contains("\"partialResult\":", stdout);
            Assert.Contains("\"status\": \"consent-required\"", stdout);
        }
        else
        {
            Assert.Empty(stdout);
            Assert.Contains("Microsoft Graph consent is required.", stderr);
            Assert.Contains("App Registration identity consent-required: Contoso Agent", stderr);
            Assert.Contains("Grant consent, then rerun.", stderr);
        }
    }

    [Fact]
    public void IdentityApp_AllOptionsBindToCoreRequest()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            color: false,
            "identity",
            "create",
            "app",
            "--tenant",
            "11111111-1111-1111-1111-111111111111",
            "--display-name",
            "Contoso Agent",
            "--auth-type",
            "UserManagedIdentity",
            "--config-format",
            "dotnet",
            "--app-id",
            "33333333-3333-3333-3333-333333333333",
            "--subscription",
            "sub",
            "--resource-group",
            "rg",
            "--location",
            "westus",
            "--managed-identity-name",
            "agent-mi",
            "--oauth",
            "--redirect-uri",
            "https://contoso.example/callback",
            "--teams-sso",
            "--observability",
            "--mcp-discovery",
            "--dry-run",
            "--non-interactive");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<AppRegistrationIdentityRequest>(operations.LastAppRegistrationIdentityRequest);
        Assert.Equal("11111111-1111-1111-1111-111111111111", request.TenantId);
        Assert.Equal("Contoso Agent", request.DisplayName);
        Assert.Equal(IdentityCredentialType.UserManagedIdentity, request.AuthType);
        Assert.Equal(IdentityConfigFormat.DotNet, request.ConfigFormat);
        Assert.Equal("33333333-3333-3333-3333-333333333333", request.ApplicationId);
        Assert.Null(request.ApplicationObjectId);
        Assert.Equal("sub", request.SubscriptionId);
        Assert.Equal("rg", request.ResourceGroup);
        Assert.Equal("westus", request.Location);
        Assert.Equal("agent-mi", request.ManagedIdentityName);
        Assert.Equal("https://contoso.example/callback", request.RedirectUri);
        Assert.True(request.OAuth);
        Assert.True(request.TeamsSso);
        Assert.True(request.Observability);
        Assert.True(request.McpDiscovery);
        Assert.True(request.DryRun);
        Assert.True(request.NonInteractive);
    }

    [Fact]
    public void CliAssembly_DoesNotReferenceAuthOrAzureLibraries()
    {
        var forbidden = new[] { "Msal", "Identity.Client", "Azure", "Graph", "WAM" };

        var referenced = typeof(AgentHostCli).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        foreach (var name in referenced)
        {
            Assert.DoesNotContain(forbidden, token => name.Contains(token, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void SlowCommand_ShowsItsProgressLabelAndClearsItBeforeTheResult()
    {
        var time = new ManualTimeProvider();
        var operations = new FakeOperations
        {
            OnManagedAgentDeploy = _ =>
            {
                time.Advance(TimeSpan.FromMilliseconds(500));
                return new ManagedAgentDeployResult { AgentId = "agent-1", DisplayName = "Agent", DeploymentId = "slot-1", DeploymentName = "dev", DeploymentType = "dev", Action = "push", TargetEnvironmentId = "env-1", SchemaName = "cr_agent" };
            },
        };

        var (exitCode, output) = RunWithSpinner(operations, time, "agent", "deploy", "--deployment-name", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.StartsWith($"\r\u280B Deploying agent (0s)\r\r{new string(' ', 22)}\rDeployed cr_agent to dev (dev) in env-1.", output);
    }

    [Fact]
    public void Copy_ShowsItsProgressLabel()
    {
        var time = new ManualTimeProvider();
        var operations = new FakeOperations
        {
            OnManagedAgentCopy = request =>
            {
                time.Advance(TimeSpan.FromMilliseconds(500));
                return new FakeOperations().CopyManagedAgentAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            },
        };

        var (exitCode, output) = RunWithSpinner(operations, time, "agent", "copy", "--agent-name", "NB1 Copy");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.StartsWith("\r\u280B Copying agent (0s)\r", output);
    }

    [Fact]
    public void SignIn_ShowsNoSpinner()
    {
        var time = new ManualTimeProvider();
        var operations = new FakeOperations
        {
            OnLogin = _ =>
            {
                time.Advance(TimeSpan.FromSeconds(1));
                return new AuthLoginResult { Status = "signed-in", Account = "op@contoso.com" };
            },
        };

        var (exitCode, output) = RunWithSpinner(operations, time, "auth", "login");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("\u280B", output);
        Assert.Contains("op@contoso.com", output);
    }

    [Fact]
    public void CommandWithoutALabel_ShowsItsCommandPath()
    {
        var time = new ManualTimeProvider();

        var (exitCode, output) = RunWithSpinner(SlowEnvironmentList(time, TimeSpan.FromMilliseconds(500)), time, "env", "list");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.StartsWith($"\r\u280B Running env list (0s)\r\r{new string(' ', 23)}\r", output);
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--debug")]
    public void MachineOrDebugOutput_ShowsNoSpinner(string flag)
    {
        var time = new ManualTimeProvider();

        var (exitCode, output) = RunWithSpinner(SlowEnvironmentList(time, TimeSpan.FromSeconds(1)), time, "env", "list", flag);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("Running env list", output);
    }

    [Fact]
    public void FailingCommand_ClearsTheSpinnerBeforeTheError()
    {
        var time = new ManualTimeProvider();
        var operations = new FakeOperations
        {
            OnEnvironmentList = () =>
            {
                time.Advance(TimeSpan.FromMilliseconds(500));
                throw new InvalidOperationException("The service is unavailable.");
            },
        };

        var (exitCode, output) = RunWithSpinner(operations, time, "env", "list");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal($"\r\u280B Running env list (0s)\r\r{new string(' ', 23)}\rInvalidOperationException: The service is unavailable.{Environment.NewLine}", output);
    }

    [Fact]
    public async Task CancelledCommand_ClearsTheSpinner()
    {
        var time = new ManualTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var output = new StringWriter();
        var operations = new FakeOperations
        {
            OnStatusAsync = token =>
            {
                time.Advance(TimeSpan.FromMilliseconds(500));
                cancellation.Cancel();
                return Task.FromCanceled<AuthStatusReport>(token);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpinnerHost(operations, time, output).InvokeAsync(["auth", "status"], output, output, color: false, cancellation.Token));

        Assert.Equal($"\r\u280B Running auth status (0s)\r\r{new string(' ', 26)}\r", output.ToString());
    }

    private static (int ExitCode, string Output) RunWithSpinner(FakeOperations operations, ManualTimeProvider time, params string[] args)
    {
        var output = new StringWriter();
        var exitCode = SpinnerHost(operations, time, output).InvokeAsync(args, output, output, color: false).GetAwaiter().GetResult();
        return (exitCode, output.ToString());
    }

    private static AgentHostCli SpinnerHost(FakeOperations operations, ManualTimeProvider time, TextWriter terminal) =>
        new(operations, telemetryFactory: null, featureFlags: TestFeatureFlags.AllProductSurfaces, progressSpinner: new ProgressSpinner(terminal, enabled: true, ansi: false, time, () => 80));

    private static FakeOperations SlowEnvironmentList(ManualTimeProvider time, TimeSpan duration) => new()
    {
        OnEnvironmentList = () =>
        {
            time.Advance(duration);
            return new EnvironmentListResult { Status = "listed", Environments = [] };
        },
    };
}
