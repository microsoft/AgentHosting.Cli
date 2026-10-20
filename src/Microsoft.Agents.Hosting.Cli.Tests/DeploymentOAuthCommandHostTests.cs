using System.Text.Json;
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
        var (exitCode, stdout, _) =
            Run(new FakeOperations(), "deployment", "oauth", verb, "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("CodeAgent only.", stdout);
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
    public void OAuthList_Debug_WritesRedactedArmResponsesToStderr()
    {
        using var firstDocument = JsonDocument.Parse(
            """{"id":"/connections/first","properties":{"clientSecret":"***"}}""");
        using var secondDocument = JsonDocument.Parse(
            """{"id":"/connections/second","properties":{"clientId":"client-2"}}""");
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
                        RawArmResponse = firstDocument.RootElement.Clone(),
                    },
                    new()
                    {
                        ResourceId = "/connections/second",
                        ConnectionName = "second",
                        ServiceProviderId = "provider-id",
                        ServiceProviderDisplayName = "Provider",
                        RawArmResponse = secondDocument.RootElement.Clone(),
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
        Assert.StartsWith("ARM response:", stderr);
        Assert.Contains("ARM response:", stderr);
        Assert.Contains("/connections/first", stderr);
        Assert.Contains("/connections/second", stderr);
        Assert.Contains(BotServiceResponseRedactor.Mask, stderr);

        var response = stderr[(stderr.IndexOf('['))..];
        using var document = JsonDocument.Parse(response);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(2, document.RootElement.GetArrayLength());
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
        if (timeout)
        {
            Assert.Equal("transport-timeout", document.RootElement.GetProperty("errorKind").GetString());
        }
        else
        {
            Assert.Contains("Synthetic lookup failure", document.RootElement.GetProperty("errorMessage").GetString());
        }
    }
}
