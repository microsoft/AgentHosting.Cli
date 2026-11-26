using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Command-host tests for the <c>msagent deployment oauth</c> subgroup — create/show/list/delete — driven
/// through the full <see cref="Microsoft.Agents.Hosting.Cli.Hosting.AgentHostCli"/> parse → dispatch → render → exit
/// pipeline with a <see cref="FakeOperations"/> facade. They assert the CLI surface only: flag binding,
/// the shared <c>--deployment</c> | <c>--agent</c>+<c>--name</c> addressing, generic provider
/// parameters, patch-style update, live provider-catalogue filtering, and required-option handling.
/// </summary>
public sealed class DeploymentOAuthCommandHostTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(
        FakeOperations operations,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new Microsoft.Agents.Hosting.Cli.Hosting.AgentHostCli(operations);
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void OAuthHelp_ListsAllVerbs()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "deployment", "oauth", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        foreach (var verb in new[] { "create", "update", "show", "list", "delete", "list-providers" })
        {
            Assert.Contains(verb, stdout);
        }
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("show")]
    [InlineData("list")]
    [InlineData("delete")]
    [InlineData("list-providers")]
    public void OAuthVerbHelp_IdentifiesCodeAgentOnlySurface(string verb)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, _) =
            Run(operations, "deployment", "oauth", verb, "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("CodeAgent only.", stdout);
        Assert.Contains("Examples:", stdout);
        Assert.Contains($"    msagent deployment oauth {verb}", stdout);
        if (verb == "create")
        {
            Assert.Contains("--connection MyOAuth", stdout);
            Assert.Contains("--client-id <app-id> --client-secret <secret>", stdout);
        }
        else if (verb is "update" or "show" or "delete")
        {
            Assert.Contains("--connection <connection-name>", stdout);
            Assert.DoesNotContain("--connection MyOAuth", stdout);
        }
        Assert.Null(operations.LastCreateDeploymentOAuthRequest);
        Assert.Null(operations.LastUpdateDeploymentOAuthRequest);
        Assert.Null(operations.LastListDeploymentOAuthProvidersRequest);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public void OAuthMutationHelp_ShowsFileExamplesWithoutReadingInputOrEchoingCredentials(string verb)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(operations,
            "deployment", "oauth", verb, "--parameters", "@", "--client-secret", "SYNTHETIC_HELP_SECRET", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("--parameters '@.\\parameters.json'", stdout);
        if (verb == "update")
        {
            Assert.Contains("--parameters '@.\\parameters.json' TenantId=<tenant-id>", stdout);
            Assert.DoesNotContain("TenantId=organizations", stdout);
            Assert.DoesNotContain("TenantId=common", stdout);
        }
        Assert.DoesNotContain("SYNTHETIC_HELP_SECRET", stdout);
        Assert.Null(operations.LastCreateDeploymentOAuthRequest);
        Assert.Null(operations.LastUpdateDeploymentOAuthRequest);
    }

    [Fact]
    public void OAuthCreate_Routes_WithConnectionProviderAndParameters()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            "deployment", "oauth", "create",
            "--deployment-id", "dep-1",
            "--connection", "aad-conn",
            "--provider-name", "Aadv2",
            "--client-id", "client-1",
            "--client-secret", "secret-1",
            "--scopes", "openid profile",
            "--parameters", "TenantId=common", "TokenExchangeUrl=api://exchange");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<DeploymentOAuthCreateRequest>(operations.LastCreateDeploymentOAuthRequest);
        Assert.Equal("dep-1", request.DeploymentId);
        Assert.Equal("aad-conn", request.ConnectionName);
        Assert.Equal("Aadv2", request.ServiceProviderName);
        Assert.Equal("client-1", request.ClientId);
        Assert.Equal("secret-1", request.ClientSecret);
        Assert.Equal("openid profile", request.Scopes);
        Assert.Equal(["TenantId=common", "TokenExchangeUrl=api://exchange"], request.Parameters);
    }

    [Fact]
    public void OAuthUpdate_RoutesProvidedFieldsAndClearValues()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            "deployment", "oauth", "update",
            "--deployment-id", "dep-1",
            "--connection", "aad-conn",
            "--scopes", "",
            "--parameters", "TokenExchangeUrl=", "TenantId=organizations");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.IsType<DeploymentOAuthUpdateRequest>(operations.LastUpdateDeploymentOAuthRequest);
        Assert.Equal(string.Empty, request.Scopes);
        Assert.Equal(["TokenExchangeUrl=", "TenantId=organizations"], request.Parameters);
        Assert.Null(request.ClientId);
        Assert.Null(request.ClientSecret);
    }

    [Theory]
    [InlineData("create", false)]
    [InlineData("create", true)]
    [InlineData("update", false)]
    [InlineData("update", true)]
    public void OAuthMutation_ExpandsParameterFilesAlongsideInlineAssignments(string verb, bool relativePath)
    {
        using var file = new ParameterFile("""{"TenantId":"common","Audience":"@literal?a=b & c=d"}""");
        using var secondFile = new ParameterFile("""{"Resource":"api://resource"}""");
        var path = relativePath ? Path.GetRelativePath(Environment.CurrentDirectory, file.Path) : file.Path;
        var operations = new FakeOperations();
        string[] createOptions = verb == "create" ? ["--provider-name", "Aadv2"] : [];
        var (exitCode, _, stderr) = Run(operations,
            ["deployment", "oauth", verb, "--deployment-id", "dep-1", "--connection", "aad-conn",
                .. createOptions, "--parameters", $"@{path}", "TokenExchangeUrl=api://exchange",
                "--parameters", $"@{secondFile.Path}"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        DeploymentOAuthUpdateRequest request = verb == "create"
            ? Assert.IsType<DeploymentOAuthCreateRequest>(operations.LastCreateDeploymentOAuthRequest)
            : Assert.IsType<DeploymentOAuthUpdateRequest>(operations.LastUpdateDeploymentOAuthRequest);
        Assert.Equal(
            ["TenantId=common", "Audience=@literal?a=b & c=d", "TokenExchangeUrl=api://exchange", "Resource=api://resource"],
            request.Parameters);
    }

    [Theory]
    [InlineData("-par")]
    [InlineData("--parameters=")]
    [InlineData("-par=")]
    public void OAuthCreate_ParameterFileSupportsAliasAndAttachedValues(string option)
    {
        using var file = new ParameterFile("""{"TenantId":"common"}""");
        var operations = new FakeOperations();
        string[] parameterOptions = option.EndsWith('=')
            ? [$"{option}@{file.Path}"]
            : [option, $"@{file.Path}"];
        var (exitCode, _, stderr) = Run(operations,
            ["deployment", "oauth", "create", "--deployment-id", "dep-1", "--connection", "aad-conn",
                "--provider-name", "Aadv2", .. parameterOptions]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Equal(["TenantId=common"], operations.LastCreateDeploymentOAuthRequest!.Parameters);
    }

    [Fact]
    public void OAuthParameterFiles_PreserveResponseFilesForOtherOptionsAndCommands()
    {
        using var parameters = new ParameterFile("""{"TenantId":"common"}""");
        using var clientId = new ParameterFile("client-1");
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(operations,
            "deployment", "oauth", "create", "--deployment-id", "dep-1", "--connection", "aad-conn",
            "--provider-name", "Aadv2", "--client-id", $"@{clientId.Path}", "--parameters", $"@{parameters.Path}");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("client-1", operations.LastCreateDeploymentOAuthRequest!.ClientId);
        Assert.Equal(["TenantId=common"], operations.LastCreateDeploymentOAuthRequest.Parameters);

        using var arguments = new ParameterFile("deployment oauth list --deployment-id dep-2");
        (exitCode, _, stderr) = Run(operations, $"@{arguments.Path}");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("dep-2", operations.LastListDeploymentOAuthRequest!.DeploymentId);
    }

    [Theory]
    [InlineData("create", """{"TenantId":"SYNTHETIC_FILE_SECRET",}""")]
    [InlineData("update", """{"TenantId":"SYNTHETIC_FILE_SECRET",}""")]
    [InlineData("create", """["SYNTHETIC_FILE_SECRET"]""")]
    [InlineData("create", """{"":"SYNTHETIC_FILE_SECRET"}""")]
    [InlineData("create", """{"  ":"SYNTHETIC_FILE_SECRET"}""")]
    [InlineData("create", """{"TenantId=injected":"SYNTHETIC_FILE_SECRET"}""")]
    public void OAuthMutation_InvalidParameterFile_ReturnsSafeUsageErrorBeforeDispatch(string verb, string json)
    {
        using var file = new ParameterFile(json);
        var operations = new FakeOperations();
        string[] createOptions = verb == "create" ? ["--provider-name", "Aadv2"] : [];
        var (exitCode, stdout, stderr) = Run(operations,
            ["deployment", "oauth", verb, "--deployment-id", "dep-1", "--connection", "aad-conn",
                .. createOptions, "--parameters", $"@{file.Path}", "--json", "--debug"]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal(99002, document.RootElement.GetProperty("errorCode").GetInt32());
        Assert.Contains("--parameters", document.RootElement.GetProperty("errorMessage").GetString());
        Assert.DoesNotContain("SYNTHETIC_FILE_SECRET", stdout);
        Assert.Null(operations.LastCreateDeploymentOAuthRequest);
        Assert.Null(operations.LastUpdateDeploymentOAuthRequest);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("directory")]
    [InlineData("empty")]
    public void OAuthCreate_UnreadableParameterFile_ReturnsUsageError(string kind)
    {
        using var file = new ParameterFile("{}");
        var reference = kind switch
        {
            "missing" => $"@{file.Path}.missing",
            "directory" => $"@{Path.GetDirectoryName(file.Path)}",
            _ => "@",
        };
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(operations,
            "deployment", "oauth", "create", "--deployment-id", "dep-1", "--connection", "aad-conn",
            "--provider-name", "Aadv2", "--parameters", reference);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("--parameters", stderr);
        Assert.Null(operations.LastCreateDeploymentOAuthRequest);
    }

    [Theory]
    [InlineData("""{"TenantId":"common","tenantid":"organizations"}""", "Audience=value", false, "duplicate")]
    [InlineData("""{"TenantId":"common"}""", "tenantid=organizations", false, "duplicate")]
    [InlineData("""{"ClientId":"SYNTHETIC_FILE_SECRET"}""", "TenantId=common", false, "--client-id")]
    [InlineData("""{"ClientSecret":"SYNTHETIC_FILE_SECRET"}""", "TenantId=common", false, "--client-secret")]
    [InlineData("""{"Scopes":"openid"}""", "TenantId=common", false, "--scopes")]
    [InlineData("""{"TenantId":""}""", "Audience=value", false, "Both Key and Value")]
    [InlineData("""{"TenantId":null}""", "Audience=value", false, "Both Key and Value")]
    [InlineData("{}", null, true, "At least one")]
    public async Task OAuthParameterFiles_PreserveExistingValidation(
        string json, string? inline, bool update, string expectedMessage)
    {
        using var file = new ParameterFile(json);
        string[] inlineParameters = inline is null ? [] : [inline];
        var parameters = await DeploymentParameterInput.ReadParametersAsync(
            [$"@{file.Path}", .. inlineParameters], CancellationToken.None);
        var request = new DeploymentOAuthCreateRequest
        {
            DeploymentId = "11111111-1111-1111-1111-111111111111",
            ConnectionName = "aad-conn",
            ServiceProviderName = "Aadv2",
            Parameters = parameters,
        };

        var exception = Assert.Throws<UsageException>(() =>
        {
            if (update)
            {
                DeploymentOAuthPlan.ValidateUpdate(request);
            }
            else
            {
                DeploymentOAuthPlan.ValidateCreate(request);
            }
        });

        Assert.Contains(expectedMessage, exception.Message);
        Assert.DoesNotContain("SYNTHETIC_FILE_SECRET", exception.Message);
    }

    [Theory]
    [InlineData("""{"TokenExchangeUrl":""}""")]
    [InlineData("""{"TokenExchangeUrl":null}""")]
    public async Task OAuthParameterFiles_UpdatePreservesEmptyStringsForClearing(string json)
    {
        using var file = new ParameterFile(json);
        var parameters = await DeploymentParameterInput.ReadParametersAsync(
            [$"@{file.Path}"], CancellationToken.None);
        var plan = DeploymentOAuthPlan.ValidateUpdate(new DeploymentOAuthUpdateRequest
        {
            DeploymentId = "11111111-1111-1111-1111-111111111111",
            ConnectionName = "aad-conn",
            Parameters = parameters,
        });

        Assert.Equal(string.Empty, plan.ParsedParameters["TokenExchangeUrl"]);
        var provider = new OAuthProviderDefinition
        {
            Id = "provider-id",
            Name = "Aadv2",
            DisplayName = "Test provider",
            Parameters = [new() { Name = "TokenExchangeUrl", Type = "string", DisplayName = "Token exchange URL" }],
        };
        var existing = new DeploymentOAuthConnection
        {
            ResourceId = "/connections/test",
            ConnectionName = "aad-conn",
            ServiceProviderId = "provider-id",
            ServiceProviderDisplayName = "Test provider",
            Parameters = new Dictionary<string, string?> { ["TokenExchangeUrl"] = "previous" },
        };
        var payload = DeploymentOAuthPlan.BuildUpdate(provider, plan, existing, "/bots/test", null);
        Assert.Equal(string.Empty, Assert.Single(payload.Parameters).Value);

        var requiredProvider = provider with
        {
            Parameters = [provider.Parameters[0] with { IsRequired = true }],
        };
        Assert.Throws<UsageException>(() =>
            DeploymentOAuthPlan.BuildUpdate(requiredProvider, plan, existing, "/bots/test", null));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public void OAuthParameterFiles_ConvertJsonValuesBeforeProviderValidation(string verb)
    {
        using var file = new ParameterFile("""
            {"Enabled":true,"Count":12,"Items":[{"value":null}],"Settings":{"value":null},"Text":"null"}
            """);
        var operations = new FakeOperations();
        string[] createOptions = verb == "create" ? ["--provider-name", "Aadv2"] : [];
        var (exitCode, _, stderr) = Run(operations,
            ["deployment", "oauth", verb, "--deployment-id", "11111111-1111-1111-1111-111111111111",
                "--connection", "aad-conn", .. createOptions, "--parameters", $"@{file.Path}"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        DeploymentOAuthUpdateRequest request = verb == "create"
            ? operations.LastCreateDeploymentOAuthRequest!
            : operations.LastUpdateDeploymentOAuthRequest!;
        Assert.Equal(
            ["Enabled=true", "Count=12", """Items=[{"value":null}]""", """Settings={"value":null}""", "Text=null"],
            request.Parameters);
        var provider = new OAuthProviderDefinition
        {
            Id = "provider-id",
            Name = "Aadv2",
            DisplayName = "Test provider",
            Parameters =
            [
                new() { Name = "Enabled", Type = "boolean", DisplayName = "Enabled" },
                new() { Name = "Count", Type = "string", DisplayName = "Count" },
                new() { Name = "Items", Type = "string", DisplayName = "Items" },
                new() { Name = "Settings", Type = "string", DisplayName = "Settings" },
                new() { Name = "Text", Type = "string", DisplayName = "Text" },
            ],
        };
        var payload = verb == "create"
            ? DeploymentOAuthPlan.BuildCreate(provider,
                DeploymentOAuthPlan.ValidateCreate(Assert.IsType<DeploymentOAuthCreateRequest>(request)), "/bots/test", null)
            : DeploymentOAuthPlan.BuildUpdate(provider,
                DeploymentOAuthPlan.ValidateUpdate(request),
                new DeploymentOAuthConnection
                {
                    ResourceId = "/connections/test",
                    ConnectionName = "aad-conn",
                    ServiceProviderId = "provider-id",
                    ServiceProviderDisplayName = "Test provider",
                }, "/bots/test", null);

        Assert.Equal(request.Parameters,
            payload.Parameters.Select(parameter => $"{parameter.Key}={parameter.Value}"));
    }

    [Fact]
    public async Task OAuthParameterFiles_CancellationIsNotTranslatedToUsage()
    {
        using var file = new ParameterFile("{}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DeploymentParameterInput.ReadParametersAsync([$"@{file.Path}"], cancellation.Token));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public void OAuthMutation_HelpDoesNotReadParameterFiles(string verb)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(operations,
            "deployment", "oauth", verb, "--parameters", "@", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("@path", stdout);
        Assert.Null(operations.LastCreateDeploymentOAuthRequest);
        Assert.Null(operations.LastUpdateDeploymentOAuthRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OAuthParameterFiles_BootstrapDoesNotReadFileContents(bool nonInteractive)
    {
        using var file = new ParameterFile("""{"TenantId":"--non-interactive"}""");
        string[] interactionOptions = nonInteractive ? ["--non-interactive"] : [];
        string[] args =
        [
            "deployment", "oauth", "create", "--deployment-id", "dep-1", "--connection", "aad-conn",
            "--provider-name", "Aadv2", "--parameters", $"@{file.Path}", .. interactionOptions,
        ];

        Assert.Equal(!nonInteractive, Hosting.AgentHostCli.ResolveInteractionAllowed(args));
        Assert.True(Hosting.AgentHostCli.IsRingIndependent([.. args, "--help"]));
        Assert.True(Hosting.AgentHostCli.IsRingIndependent(
            ["deployment", "oauth", "create", "--parameters", "@", "--help"]));
    }

    private sealed class ParameterFile : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("msagent-oauth-").FullName;

        public ParameterFile(string json)
        {
            Path = System.IO.Path.Combine(_directory, "oauth parameters.json");
            File.WriteAllText(Path, json);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void OAuthCreate_MissingConnectionAndProvider_ExitTwo()
    {
        var (exitCode, _, stderr) = Run(
            new FakeOperations(),
            "deployment", "oauth", "create", "--deployment-id", "dep-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--connection", stderr);
        Assert.Contains("--provider-name", stderr);
    }

    [Fact]
    public void OAuthShow_Routes_WithConnection()
    {
        var operations = new FakeOperations();
        var (exitCode, _, _) = Run(
            operations,
            "deployment", "oauth", "show",
            "--deployment-id", "dep-1",
            "--connection", "aad-conn");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("dep-1", operations.LastShowDeploymentOAuthRequest!.DeploymentId);
        Assert.Equal("aad-conn", operations.LastShowDeploymentOAuthRequest.ConnectionName);
    }

    [Fact]
    public void OAuthList_Routes_ByAgentAndName()
    {
        var operations = new FakeOperations();
        var (exitCode, _, _) = Run(
            operations,
            "deployment", "oauth", "list",
            "--agent-id", "agent-1",
            "--deployment-name", "dev1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("agent-1", operations.LastListDeploymentOAuthRequest!.AgentId);
        Assert.Equal("dev1", operations.LastListDeploymentOAuthRequest.DeploymentName);
    }

    [Fact]
    public void OAuthDelete_Routes_WithConnection()
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            "deployment", "oauth", "delete",
            "--deployment-id", "dep-1",
            "--connection", "aad-conn");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("Connection 'aad-conn' deleted.", stdout.Trim());
        Assert.Equal("aad-conn", operations.LastDeleteDeploymentOAuthRequest!.ConnectionName);
    }

    [Fact]
    public void OAuthDelete_Json_HasMessageWithoutDeletedConnectionPayload()
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            "deployment", "oauth", "delete",
            "--deployment-id", "dep-1",
            "--connection", "aad-conn",
            "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("Connection 'aad-conn' deleted.", document.RootElement.GetProperty("message").GetString());
        Assert.False(document.RootElement.TryGetProperty("connection", out _));
        Assert.False(document.RootElement.TryGetProperty("operationTargetName", out _));
    }

    [Fact]
    public void OAuthShow_Json_UsesConnectionPayloadName()
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            "deployment", "oauth", "show",
            "--deployment-id", "dep-1",
            "--connection", "aad-conn",
            "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.TryGetProperty("connection", out var oauthConnection));
        Assert.False(document.RootElement.TryGetProperty("oauthConnection", out _));
        Assert.Equal("provider-id", oauthConnection.GetProperty("serviceProviderId").GetString());
        Assert.Equal(
            "Azure Active Directory v2",
            oauthConnection.GetProperty("serviceProviderDisplayName").GetString());
        Assert.False(oauthConnection.TryGetProperty("serviceProviderName", out _));
    }

    [Fact]
    public void OAuthList_Json_UsesConnectionsPayloadName()
    {
        var (exitCode, stdout, stderr) = Run(
            new FakeOperations(),
            "deployment", "oauth", "list",
            "--deployment-id", "dep-1",
            "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.True(document.RootElement.TryGetProperty("connections", out _));
        Assert.False(document.RootElement.TryGetProperty("oauthConnections", out _));
    }

    [Fact]
    public void OAuthList_Debug_DoesNotWriteAnArmDump()
    {
        var operations = new FakeOperations
        {
            OnListDeploymentOAuth = _ => new DeploymentResult<DeploymentOAuthConnection[]>
            {
                TargetName = "Connections",
                Status = DeploymentOperationStatus.Listed,
                Payload =
                [
                    new()
                    {
                        ResourceId = "/connections/first",
                        ConnectionName = "first",
                        ServiceProviderId = "provider-id",
                        ServiceProviderDisplayName = "Provider",
                    },
                    new()
                    {
                        ResourceId = "/connections/second",
                        ConnectionName = "second",
                        ServiceProviderId = "provider-id",
                        ServiceProviderDisplayName = "Provider",
                    },
                ],
            },
        };

        var (exitCode, stdout, stderr) = Run(
            operations,
            "deployment", "oauth", "list",
            "--deployment-id", "dep-1",
            "--debug");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("/connections/first", stdout);
        Assert.Contains("/connections/second", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void OAuthCreate_HelpUsesStableGenericParameterSurface()
    {
        var (exitCode, stdout, _) = Run(
            new FakeOperations(),
            "deployment", "oauth", "create", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("--client-id", stdout);
        Assert.Contains("--client-secret", stdout);
        Assert.Contains("--scopes", stdout);
        Assert.Contains("--parameters", stdout);
        Assert.DoesNotContain("--unique-identifier", stdout);
    }

    [Fact]
    public void OAuthListProviders_Routes()
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            "deployment", "oauth", "list-providers",
            "--subscription", "sub-1");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("sub-1", operations.LastListDeploymentOAuthProvidersRequest!.SubscriptionId);
        using var document = JsonDocument.Parse(stdout);
        var provider = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("Aadv2", provider.GetProperty("name").GetString());
    }

    [Fact]
    public void OAuthListProviders_Json_WrapsArrayInSuccessEnvelope()
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(
            operations,
            "deployment", "oauth", "list-providers",
            "--subscription", "sub-1",
            "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        var provider = Assert.Single(root.GetProperty("result").EnumerateArray());
        Assert.Equal("Aadv2", provider.GetProperty("name").GetString());
    }

    [Fact]
    public void OAuthListProviders_Filter_Routes()
    {
        var operations = new FakeOperations();
        var (exitCode, _, stderr) = Run(
            operations,
            "deployment", "oauth", "list-providers",
            "--subscription", "sub-1",
            "--provider-name", "Aadv2WithFic");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Equal("Aadv2WithFic", operations.LastListDeploymentOAuthProvidersRequest!.ServiceProviderName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OAuthListProviders_InvalidSelection_WithAndWithoutHelpReturnsSameError(bool json)
    {
        var expected = DeploymentException.OAuthServiceProviderNotFound("line");
        foreach (var help in new[] { false, true })
        {
            var operations = new FakeOperations
            {
                OnListDeploymentOAuthProviders = _ => throw expected,
            };
            string[] helpOptions = help ? ["--help"] : [];
            string[] outputOptions = json ? ["--json"] : [];
            var (exitCode, stdout, stderr) = Run(operations,
                ["deployment", "oauth", "list-providers", "--provider-name", "line",
                    .. helpOptions, .. outputOptions]);

            Assert.Equal(ExitCodes.Usage, exitCode);
            Assert.Equal("line", operations.LastListDeploymentOAuthProvidersRequest!.ServiceProviderName);
            if (json)
            {
                Assert.Empty(stderr);
                using var document = JsonDocument.Parse(stdout);
                Assert.False(document.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(expected.Message, document.RootElement.GetProperty("errorMessage").GetString());
                Assert.Equal(expected.ErrorKind, document.RootElement.GetProperty("errorKind").GetString());
                Assert.Equal(expected.Remediation, document.RootElement.GetProperty("remediation").GetString());
            }
            else
            {
                Assert.Empty(stdout);
                Assert.Contains(expected.Message, stderr);
                Assert.Contains(expected.Remediation!, stderr);
            }

            Assert.DoesNotContain("Usage:", stdout + stderr);
        }
    }

    [Theory]
    [InlineData("create", "-clsin", "SYNTHETIC_SECRET", false)]
    [InlineData("update", "--client-secret-stdin", "SYNTHETIC_SECRET", false)]
    [InlineData("update", "--client-secret-stdin", "SYNTHETIC_SECRET", true)]
    [InlineData("create", "--client-secret-stdin=SYNTHETIC_SECRET", null, false)]
    [InlineData("create", "--client-secret-stdin", "-SYNTHETIC_SECRET", false)]
    [InlineData("update", "-clsin", "--SYNTHETIC_SECRET", true)]
    [InlineData("update", "--client-secret-stdin=-SYNTHETIC_SECRET", null, false)]
    [InlineData("create", "--client-secret-stdin:-SYNTHETIC_SECRET", null, true)]
    [InlineData("create", "--client-secrt", "SYNTHETIC_SECRET", false)]
    [InlineData("update", "--client-secrt=SYNTHETIC_SECRET", null, false)]
    [InlineData("update", "--client-secrt:SYNTHETIC_SECRET", null, true)]
    [InlineData("update", "-clsc", "SYNTHETIC_SECRET", false)]
    public void UnmatchedInput_RefusesWithAMessageThatEchoesNoInput(string verb, string option, string? value, bool json)
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(operations,
            ["deployment", "oauth", verb, "--deployment-id", "dep-1", "--connection", "MyOAuth",
                .. verb == "create" ? ["--provider-name", "Aadv2"] : Array.Empty<string>(),
                option, .. value is null ? Array.Empty<string>() : [value],
                .. json ? ["--json"] : Array.Empty<string>()]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        const string expected = "Invalid OAuth command input. Check the options and Key=Value parameters; --client-secret-stdin takes no value. Run `msagent --help` for usage.";
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

        Assert.DoesNotContain("SYNTHETIC_SECRET", stdout + stderr);
        Assert.DoesNotContain("client-secrt", stdout + stderr);
        Assert.DoesNotContain("-clsc", stdout + stderr);
        Assert.Null(operations.LastCreateDeploymentOAuthRequest);
        Assert.Null(operations.LastUpdateDeploymentOAuthRequest);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public void StrayValueWithHelp_DoesNotEchoTheValue(string verb)
    {
        var (exitCode, stdout, stderr) = Run(new FakeOperations(),
            "deployment", "oauth", verb, "--connection", "MyOAuth", "-clsin", "SYNTHETIC_SECRET", "--help");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.DoesNotContain("SYNTHETIC_SECRET", stdout + stderr);
    }

    [Theory]
    [InlineData("--provider-name")]
    [InlineData("-pn")]
    public void OAuthListProviders_SelectedHelpDisplaysLiveParametersWithSingleLookup(string option)
    {
        var calls = 0;
        var operations = new FakeOperations
        {
            OnListDeploymentOAuthProviders = _ =>
            {
                calls++;
                return [new()
                {
                    Id = "provider-id", Name = "Google", DisplayName = "Google",
                    Parameters =
                    [
                        new() { Name = "ClientId", Type = "String", DisplayName = "Client ID",
                            Description = "Google application id.", IsRequired = true },
                        new() { Name = "Scopes", Type = "String", DisplayName = "Requested scopes",
                            DefaultValue = "openid" },
                    ],
                }];
            },
        };
        var (exitCode, stdout, stderr) = Run(operations,
            "deployment", "oauth", "list-providers", option, "google",
            "--subscription", "sub-1", "--tenant", "tenant-1", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("Usage:", stdout);
        Assert.Contains("--provider-name", stdout);
        Assert.Contains("Parameters:", stdout);
        Assert.Contains("ClientId <String> (REQUIRED)", stdout);
        Assert.Contains("Google application id.", stdout);
        Assert.Contains("Scopes <String>", stdout);
        Assert.Contains("Requested scopes Default: openid.", stdout);
        Assert.DoesNotContain("Scopes <String> (REQUIRED)", stdout);
        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        var clientId = Assert.Single(lines, line => line.TrimStart().StartsWith("ClientId <String>", StringComparison.Ordinal));
        var json = Assert.Single(lines, line => line.Contains("--json", StringComparison.Ordinal));
        var provider = Assert.Single(lines, line => line.Contains("--provider-name", StringComparison.Ordinal) &&
            line.Contains("OAuth service provider", StringComparison.Ordinal));
        Assert.Equal(clientId.IndexOf("Google application id.", StringComparison.Ordinal),
            json.IndexOf("Emit machine-readable", StringComparison.Ordinal));
        Assert.Equal(clientId.IndexOf("Google application id.", StringComparison.Ordinal),
            provider.IndexOf("OAuth service provider", StringComparison.Ordinal));
        Assert.True(stdout.IndexOf("Examples:", StringComparison.Ordinal) >
            stdout.IndexOf("Parameters:", StringComparison.Ordinal));
        Assert.Equal(1, calls);
        var request = Assert.IsType<DeploymentOAuthProviderListRequest>(operations.LastListDeploymentOAuthProvidersRequest);
        Assert.Equal("google", request.ServiceProviderName);
        Assert.Equal("sub-1", request.SubscriptionId);
        Assert.Equal("tenant-1", request.TenantId);
    }

    [Fact]
    public void OAuthListProviders_UnselectedHelpDoesNotReadCatalogue()
    {
        var operations = new FakeOperations();
        var (exitCode, stdout, stderr) = Run(operations, "deployment", "oauth", "list-providers", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("Usage:", stdout);
        Assert.Null(operations.LastListDeploymentOAuthProvidersRequest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OAuthListProviders_SelectedHelpReportsLookupFailuresWithoutDisplayingHelp(bool timeout)
    {
        Exception error = timeout
            ? new OperationCanceledException("Synthetic timeout")
            : new HttpRequestException("Synthetic lookup failure");
        var operations = new FakeOperations
        {
            OnListDeploymentOAuthProviders = _ => throw error,
        };
        var (exitCode, stdout, stderr) = Run(operations,
            "deployment", "oauth", "list-providers", "--provider-name", "Aadv2", "--help", "--json");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.DoesNotContain("Usage:", stdout);
        Assert.DoesNotContain("Examples:", stdout);
        if (timeout)
        {
            Assert.Equal("transport-timeout", document.RootElement.GetProperty("errorKind").GetString());
        }
        else
        {
            Assert.Contains("Synthetic lookup failure", document.RootElement.GetProperty("errorMessage").GetString());
        }
    }

    private const string AmsAgentId = "44444444-4444-4444-4444-444444444444";
    private const string AmsDeploymentId = "77777777-7777-7777-7777-777777777777";

    public static TheoryData<string[], Type> AmsOAuthCommands => new()
    {
        { ["create", "--connection", "graph", "--provider-name", "Aadv2", "--client-id", "client-1"], typeof(AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest>) },
        { ["update", "--connection", "graph", "--scopes", "openid"], typeof(AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest>) },
        { ["delete", "--connection", "graph"], typeof(AgentDeploymentAmsOAuthRequest<DeploymentOAuthDeleteRequest>) },
        { ["show", "--connection", "graph"], typeof(AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest>) },
        { ["list"], typeof(AgentDeploymentAmsOAuthRequest<DeploymentOAuthListRequest>) },
        { ["list-providers"], typeof(AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest>) },
    };

    [Theory]
    [MemberData(nameof(AmsOAuthCommands))]
    public void AmsRoute_DispatchesTheAddressWithoutLocalResolution(string[] verbAndOptions, Type requestType)
    {
        var operations = new FakeOperations();
        var handlers = new AmsOAuthHandlers();

        var (exitCode, _, stderr) = RunAms(operations, handlers, ["deployment", "oauth", verbAndOptions[0], "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1", .. verbAndOptions[1..]]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        var request = Assert.Single(handlers.Requests);
        Assert.IsType(requestType, request);
        var address = Assert.IsAssignableFrom<AgentDeploymentAmsRequest>(request);
        Assert.Equal((AmsAgentId, AmsDeploymentId, "env-1"), (address.AgentId, address.DeploymentId, address.EnvironmentId));
        AssertNoLocalOAuthRoute(operations);
    }

    [Fact]
    public void AmsRoute_ByNamesWithoutTheEnvironment_DispatchesTheNames()
    {
        var operations = new FakeOperations();
        var handlers = new AmsOAuthHandlers();

        var (exitCode, _, _) = RunAms(operations, handlers, "deployment", "oauth", "show", "--connection", "graph", "--agent-name", "Contoso Agent", "--deployment-name", "dev");

        Assert.Equal(ExitCodes.Success, exitCode);
        var address = Assert.IsType<AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest>>(Assert.Single(handlers.Requests));
        Assert.Equal(("Contoso Agent", "dev", null), (address.AgentName, address.DeploymentName, address.EnvironmentId));
        AssertNoLocalOAuthRoute(operations);
    }

    [Fact]
    public void AmsRoute_PassesTheOAuthContentAsLocalModeBindsIt()
    {
        var handlers = new AmsOAuthHandlers();

        var (exitCode, _, _) = RunAms(new FakeOperations(), handlers, "deployment", "oauth", "update", "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1", "--connection", "graph", "--client-id", "client-2", "--scopes", "", "--parameters", "TokenExchangeUrl=", "TenantId=organizations");

        Assert.Equal(ExitCodes.Success, exitCode);
        var content = Assert.IsType<AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest>>(Assert.Single(handlers.Requests)).OAuth;
        Assert.Equal(("graph", "client-2", string.Empty), (content.ConnectionName, content.ClientId, content.Scopes));
        Assert.Null(content.ClientSecret);
        Assert.False(content.ClientSecretFromStandardInput);
        Assert.Equal(["TokenExchangeUrl=", "TenantId=organizations"], content.Parameters);
    }

    [Fact]
    public void ClientSecretStdin_BindsOnTheAmsRoute()
    {
        var handlers = new AmsOAuthHandlers();

        var create = RunAms(new FakeOperations(), handlers, "deployment", "oauth", "create", "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1", "--connection", "graph", "--provider-name", "Aadv2", "--client-secret-stdin");
        var update = RunAms(new FakeOperations(), handlers, "deployment", "oauth", "update", "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1", "--connection", "graph", "-clsin");

        Assert.Equal((ExitCodes.Success, ExitCodes.Success), (create.ExitCode, update.ExitCode));
        var created = Assert.IsType<AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest>>(handlers.Requests[0]).OAuth;
        var updated = Assert.IsType<AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest>>(handlers.Requests[1]).OAuth;
        Assert.Equal((true, null), (created.ClientSecretFromStandardInput, created.ClientSecret));
        Assert.Equal((true, null), (updated.ClientSecretFromStandardInput, updated.ClientSecret));
    }

    [Fact]
    public void ClientSecretStdin_BindsOnTheLocalRoute()
    {
        var operations = new FakeOperations();

        var create = Run(operations, "deployment", "oauth", "create", "--deployment-id", "dep-1", "--connection", "graph", "--provider-name", "Aadv2", "--client-secret-stdin");
        var update = Run(operations, "deployment", "oauth", "update", "--deployment-id", "dep-1", "--connection", "graph", "--client-secret-stdin");

        Assert.Equal((ExitCodes.Success, ExitCodes.Success), (create.ExitCode, update.ExitCode));
        Assert.Equal((true, null), (operations.LastCreateDeploymentOAuthRequest!.ClientSecretFromStandardInput, operations.LastCreateDeploymentOAuthRequest.ClientSecret));
        Assert.Equal((true, null), (operations.LastUpdateDeploymentOAuthRequest!.ClientSecretFromStandardInput, operations.LastUpdateDeploymentOAuthRequest.ClientSecret));
    }

    [Fact]
    public void AmsRoute_ListPrintsNoArmResponseInDebugOutput()
    {
        var (exitCode, stdout, stderr) = RunAms(new FakeOperations(), new AmsOAuthHandlers(), "deployment", "oauth", "list", "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1", "--debug", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("graph", document.RootElement.GetProperty("connections")[0].GetProperty("connectionName").GetString());
    }

    public static TheoryData<string[], string> AmsCommandsMissingAnAddressOption => new()
    {
        { ["show", "--connection", "graph", "--deployment-id", AmsDeploymentId], "--agent-id or --agent-name is required for the AMS route of `msagent deployment oauth show`." },
        { ["show", "--connection", "graph", "--agent-id", AmsAgentId], "--deployment-id or --deployment-name is required for the AMS route of `msagent deployment oauth show`." },
        { ["list-providers", "--deployment-id", AmsDeploymentId], "--agent-id or --agent-name is required for the AMS route of `msagent deployment oauth list-providers`." },
        { ["list-providers", "--agent-id", AmsAgentId], "--deployment-id or --deployment-name is required for the AMS route of `msagent deployment oauth list-providers`." },
    };

    [Theory]
    [MemberData(nameof(AmsCommandsMissingAnAddressOption))]
    public void AmsRoute_MissingAddressOptionDispatchesNothing(string[] verbAndOptions, string message)
    {
        var operations = new FakeOperations();
        var handlers = new AmsOAuthHandlers();

        var (exitCode, _, stderr) = RunAms(operations, handlers, ["deployment", "oauth", .. verbAndOptions, "--environment-id", "env-1"]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains(message, stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
        AssertNoLocalOAuthRoute(operations);
    }

    public static TheoryData<string[], string> AmsCommandsWithAnotherRoutesOption => new()
    {
        { ["list", "--project", "."], "--project" },
        { ["list-providers", "--subscription", "sub-1"], "--subscription" },
        { ["list-providers", "--tenant", "tenant-1"], "--tenant" },
    };

    [Theory]
    [MemberData(nameof(AmsCommandsWithAnotherRoutesOption))]
    public void AmsRoute_RejectsTheOptionsOfTheOtherRoutes(string[] verbAndOptions, string option)
    {
        var operations = new FakeOperations();
        var handlers = new AmsOAuthHandlers();

        var (exitCode, _, stderr) = RunAms(operations, handlers, ["deployment", "oauth", .. verbAndOptions, "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1"]);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains($"{option} cannot be combined with the AMS route of `msagent deployment oauth {verbAndOptions[0]}`.", stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
        AssertNoLocalOAuthRoute(operations);
    }

    [Theory]
    [InlineData("--agent-id", AmsAgentId)]
    [InlineData("--deployment-id", AmsDeploymentId)]
    public void ListProviders_AnAmsAddressWithoutTheEnvironmentSelectsTheAmsRoute(string option, string value)
    {
        var operations = new FakeOperations();
        var handlers = new AmsOAuthHandlers();

        var (exitCode, _, stderr) = RunAms(operations, handlers, "deployment", "oauth", "list-providers", "--subscription", "sub-1", option, value);

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--subscription cannot be combined with the AMS route of `msagent deployment oauth list-providers`.", stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
        Assert.Null(operations.LastListDeploymentOAuthProvidersRequest);
    }

    [Fact]
    public void ListProviders_SelectedHelpOnTheAmsRouteShowsTheAmsCatalogue()
    {
        var operations = new FakeOperations();
        var handlers = new AmsOAuthHandlers();

        var (exitCode, stdout, stderr) = RunAms(operations, handlers, "deployment", "oauth", "list-providers", "--agent-id", AmsAgentId, "--deployment-id", AmsDeploymentId, "--environment-id", "env-1", "--provider-name", "aadv2", "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);
        Assert.Contains("ClientId <string> (REQUIRED)", stdout, StringComparison.Ordinal);
        var request = Assert.IsType<AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest>>(Assert.Single(handlers.Requests));
        Assert.Equal(("aadv2", AmsDeploymentId), (request.OAuth.ServiceProviderName, request.DeploymentId));
        Assert.Null(operations.LastListDeploymentOAuthProvidersRequest);
    }

    [Theory]
    [InlineData("create", "--client-secret-stdin", "Use an AMS deployment")]
    [InlineData("update", "--client-secret-stdin", "Use an AMS deployment")]
    [InlineData("list", "--environment-id", "Use an AMS deployment")]
    [InlineData("list-providers", "--environment-id", "Use the providers AMS offers for an AMS deployment")]
    public void Help_ShowsTheAmsRouteWhenTheAmsFeaturesAreOn(string verb, string option, string amsUsage)
    {
        var (exitCode, stdout, _) = RunAms(new FakeOperations(), new AmsOAuthHandlers(), "deployment", "oauth", verb, "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains(option, stdout, StringComparison.Ordinal);
        Assert.Contains(amsUsage, stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("list-providers")]
    public void Help_HidesTheAmsRouteWhenTheAmsFeaturesAreOff(string verb)
    {
        var (exitCode, stdout, _) = AmsCommandHost.Run(new FakeOperations(), new AmsOAuthHandlers(), new TestFeatureFlags(), "deployment", "oauth", verb, "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.DoesNotContain("--environment-id", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("AMS", stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("list-providers")]
    public void AmsFeatureOff_TheAmsRouteIsUnavailable(string verb)
    {
        var operations = new FakeOperations();
        var handlers = new AmsOAuthHandlers();

        var (exitCode, _, stderr) = AmsCommandHost.Run(operations, handlers, new TestFeatureFlags(), "deployment", "oauth", verb, "--environment-id", "env-1");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("--environment-id", stderr, StringComparison.Ordinal);
        Assert.Empty(handlers.Requests);
        AssertNoLocalOAuthRoute(operations);
    }

    private static void AssertNoLocalOAuthRoute(FakeOperations operations)
    {
        Assert.Null(operations.LastDeploymentAgentTypeQuery);
        Assert.Null(operations.LastCreateDeploymentOAuthRequest);
        Assert.Null(operations.LastUpdateDeploymentOAuthRequest);
        Assert.Null(operations.LastDeleteDeploymentOAuthRequest);
        Assert.Null(operations.LastShowDeploymentOAuthRequest);
        Assert.Null(operations.LastListDeploymentOAuthRequest);
        Assert.Null(operations.LastListDeploymentOAuthProvidersRequest);
    }

    private static (int ExitCode, string Stdout, string Stderr) RunAms(FakeOperations operations, AmsOAuthHandlers handlers, params string[] args) => AmsCommandHost.Run(operations, handlers, new TestFeatureFlags(FeatureName.AmsFeatures), args);

    private sealed class AmsOAuthHandlers :
        ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest>, DeploymentResult<DeploymentOAuthConnection>>,
        ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest>, DeploymentResult<DeploymentOAuthConnection>>,
        ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthDeleteRequest>, DeploymentResult<DeploymentOAuthConnection>>,
        ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest>, DeploymentResult<DeploymentOAuthConnection>>,
        ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthListRequest>, DeploymentResult<DeploymentOAuthConnection[]>>,
        ICommandHandler<AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest>, OAuthProviderDefinition[]>
    {
        private static readonly DeploymentOAuthConnection Connection = new() { ResourceId = "/bots/ams/connections/graph", ConnectionName = "graph", ServiceProviderId = "provider-aadv2", ServiceProviderDisplayName = "Azure Active Directory v2", ClientId = "client-1", Scopes = ["openid"] };

        public List<object> Requests { get; } = [];

        public Task<DeploymentResult<DeploymentOAuthConnection>> HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthCreateRequest> request, CancellationToken cancellationToken) => Record(request, new DeploymentResult<DeploymentOAuthConnection> { Status = DeploymentOperationStatus.Created, Payload = Connection, TargetName = "Connection", TargetInstanceName = "graph" });

        public Task<DeploymentResult<DeploymentOAuthConnection>> HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthUpdateRequest> request, CancellationToken cancellationToken) => Record(request, new DeploymentResult<DeploymentOAuthConnection> { Status = DeploymentOperationStatus.Updated, Payload = Connection, TargetName = "Connection", TargetInstanceName = "graph" });

        public Task<DeploymentResult<DeploymentOAuthConnection>> HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthDeleteRequest> request, CancellationToken cancellationToken) => Record(request, new DeploymentResult<DeploymentOAuthConnection> { Status = DeploymentOperationStatus.Deleted, TargetName = "Connection", TargetInstanceName = "graph" });

        public Task<DeploymentResult<DeploymentOAuthConnection>> HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthShowRequest> request, CancellationToken cancellationToken) => Record(request, new DeploymentResult<DeploymentOAuthConnection> { Status = DeploymentOperationStatus.Retrieved, Payload = Connection, TargetName = "Connection", TargetInstanceName = "graph" });

        public Task<DeploymentResult<DeploymentOAuthConnection[]>> HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthListRequest> request, CancellationToken cancellationToken) => Record(request, new DeploymentResult<DeploymentOAuthConnection[]> { Status = DeploymentOperationStatus.Listed, Payload = [Connection], TargetName = "Connections" });

        public Task<OAuthProviderDefinition[]> HandleAsync(AgentDeploymentAmsOAuthRequest<DeploymentOAuthProviderListRequest> request, CancellationToken cancellationToken) => Record<OAuthProviderDefinition[]>(request, [new() { Id = "provider-aadv2", Name = "Aadv2", DisplayName = "Azure Active Directory v2", Parameters = [new() { Name = "ClientId", Type = "string", DisplayName = "Client id", IsRequired = true }] }]);

        private Task<T> Record<T>(object request, T result)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
