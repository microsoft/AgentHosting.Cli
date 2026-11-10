using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Packaging;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Command-host coverage for <c>msagent env list</c> and <c>msagent env select</c>, plus the guarantee that
/// matters most on this surface: under <c>--non-interactive</c> no environment command can reach a
/// login flow. The strict cases run the real <see cref="AgentHostingOperations"/> facade against a spy
/// session whose every login flow is counted, so "no identity UI" is proven rather than asserted.
/// </summary>
public sealed class EnvironmentCommandHostTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(
        IAgentHostingOperations operations,
        params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(operations);
        var exitCode = cli.InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static FakeOperations Inventory() => new()
    {
        OnEnvironmentList = () => new EnvironmentListResult
        {
            Status = "listed",
            Environments =
            [
                new EnvironmentSummary
                {
                    EnvironmentId = "env-1",
                    DisplayName = "Contoso Dev",
                    TenantId = EnvironmentFixtures.ContosoTenant,
                    ApiEndpoint = "https://env-1.api.powerplatform.com/",
                    IsActive = true,
                },
            ],
        },
    };

    [Fact]
    public void EnvGroup_IsListedInHelp()
    {
        var (exitCode, stdout, _) = Run(new FakeOperations(), "--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("env", stdout);
    }

    [Fact]
    public void EnvWithoutVerb_IsUsageError_ExitTwo()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), "env");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.Contains("list, select", stderr);
    }

    [Fact]
    public void EnvSelect_WithoutAnEnvironmentId_IsUsageError_ExitTwo()
    {
        var (exitCode, _, stderr) = Run(new FakeOperations(), "env", "select");

        Assert.Equal(ExitCodes.Usage, exitCode);
        Assert.NotEmpty(stderr);
    }

    [Fact]
    public void EnvList_JsonEnvelope_CarriesTheInventory_AndNoTokenMaterial()
    {
        var (exitCode, stdout, stderr) = Run(Inventory(), "env", "list", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("listed", root.GetProperty("status").GetString());

        var environment = root.GetProperty("environments").EnumerateArray().Single();
        Assert.Equal("env-1", environment.GetProperty("environmentId").GetString());
        Assert.True(environment.GetProperty("isActive").GetBoolean());

        foreach (var forbidden in new[] { "token", "bearer", "secret", "authorization", "eyJ" })
        {
            Assert.DoesNotContain(forbidden, stdout, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EnvList_HumanOutput_MarksTheActiveEnvironment()
    {
        var (exitCode, stdout, _) = Run(Inventory(), "env", "list");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("env-1", stdout);
        Assert.Contains("Contoso Dev", stdout);
        Assert.Contains("*", stdout);
    }

    [Fact]
    public void EnvSelect_BindsThePositionalEnvironmentId()
    {
        var operations = new FakeOperations();

        var (exitCode, stdout, _) = Run(operations, "env", "select", "env-42");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("env-42", operations.LastSelectedEnvironmentId);
        Assert.Contains("env-42", stdout);
    }

    [Fact]
    public void EnvSelect_TenantMismatch_MapsToUsageExitAndKind()
    {
        var operations = new FakeOperations
        {
            OnEnvironmentSelect = _ => throw new EnvironmentTenantMismatchException(
                "Environment 'env-other' is in a different tenant."),
        };

        var (exitCode, stdout, _) = Run(operations, "env", "select", "env-other", "--json");

        Assert.Equal(ExitCodes.Usage, exitCode);

        using var document = JsonDocument.Parse(stdout);
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("tenant-mismatch", document.RootElement.GetProperty("errorKind").GetString());
    }

    [Fact]
    public void EnvList_UntrustedEndpoint_MapsToFailureExitAndKind()
    {
        var operations = new FakeOperations
        {
            OnEnvironmentList = () => throw new EndpointTrustException(
                "The environment 'env-evil' endpoint is not a trusted Power Platform host."),
        };

        var (exitCode, stdout, _) = Run(operations, "env", "list", "--json");

        Assert.Equal(ExitCodes.Failure, exitCode);

        using var document = JsonDocument.Parse(stdout);
        Assert.Equal("endpoint-untrusted", document.RootElement.GetProperty("errorKind").GetString());
    }

    [Fact]
    public void AuthStatus_ReportsTheActiveEnvironment_WithNoTokenMaterial()
    {
        var operations = new FakeOperations
        {
            OnStatus = () => new AuthStatusReport
            {
                Status = "signed-in",
                SignedIn = true,
                Account = "op@contoso.com",
                TenantId = EnvironmentFixtures.ContosoTenant,
                EnvironmentId = "env-1",
                EnvironmentDisplayName = "Contoso Dev",
            },
        };

        var (jsonExit, jsonStdout, _) = Run(operations, "auth", "status", "--json");
        var (humanExit, humanStdout, _) = Run(operations, "auth", "status");

        Assert.Equal(ExitCodes.Success, jsonExit);
        Assert.Equal(ExitCodes.Success, humanExit);

        using var document = JsonDocument.Parse(jsonStdout);
        Assert.Equal("env-1", document.RootElement.GetProperty("environmentId").GetString());
        Assert.Equal("Contoso Dev", document.RootElement.GetProperty("environmentDisplayName").GetString());
        Assert.Contains("Active environment: Contoso Dev (env-1).", humanStdout);

        foreach (var forbidden in new[] { "token", "bearer", "secret", "authorization", "eyJ" })
        {
            Assert.DoesNotContain(forbidden, jsonStdout, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, humanStdout, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AuthStatus_WithNoSelection_SaysSo()
    {
        var (_, stdout, _) = Run(new FakeOperations(), "auth", "status");

        Assert.Contains("No environment selected.", stdout);
    }

    [Fact]
    public void AuthStatus_ShowsAnUnboundSelection_AndSaysWhyItIsUnusable()
    {
        // Preserving the selection for visibility is only half of it: shown without the binding state,
        // a target no command will accept looks exactly like a healthy one.
        var operations = new FakeOperations
        {
            OnStatus = () => new AuthStatusReport
            {
                Status = "signed-in",
                SignedIn = true,
                Account = "op@fabrikam.com",
                TenantId = EnvironmentFixtures.FabrikamTenant,
                EnvironmentId = "env-1",
                EnvironmentDisplayName = "Contoso Dev",
                EnvironmentTenantId = EnvironmentFixtures.ContosoTenant,
                EnvironmentTenantBound = false,
            },
        };

        var (_, jsonStdout, _) = Run(operations, "auth", "status", "--json");
        var (_, humanStdout, _) = Run(operations, "auth", "status");

        using var document = JsonDocument.Parse(jsonStdout);
        Assert.False(document.RootElement.GetProperty("environmentTenantBound").GetBoolean());
        Assert.Equal(
            EnvironmentFixtures.ContosoTenant,
            document.RootElement.GetProperty("environmentTenantId").GetString());

        Assert.Contains("Active environment: Contoso Dev (env-1).", humanStdout);
        Assert.Contains("Not bound to the signed-in account", humanStdout);
        Assert.Contains(EnvironmentFixtures.ContosoTenant, humanStdout);
        Assert.Contains(EnvironmentFixtures.FabrikamTenant, humanStdout);
    }

    /// <summary>
    /// Builds the real facade over the spy session, so any identity UI attempt is counted rather than
    /// mocked away.
    /// </summary>
    private static (AgentHostingOperations Operations, FakeAuthenticationSession Session) RealFacade(
        IEnvironmentClient client,
        IAuthProfileStore profiles,
        bool interactionAllowed)
    {
        var session = new FakeAuthenticationSession();
        var reader = new PendingAbsStateReader();
        var environments = new EnvironmentSelectionService(
            client,
            new InMemoryEnvironmentSelectionStore(),
            profiles,
            PowerPlatformCloudMap.ForCloud(CloudInstance.Public),
            new FixedClock(EnvironmentFixtures.SelectedAt));
        var operations = new AgentHostingOperations(
            session,
            profiles,
            new StaticInteractionPolicy(interactionAllowed),
            FakePlatformInfo.Windows(),
            new RecordingMessagePresenter(),
            new AuthSessionContext { Cloud = CloudInstance.Public },
            new AbsProvisioningService(reader),
            new AbsApplyService(reader, new PendingAbsMutationPort()),
            environments,
            AgentCreateFixtures.PendingService(environments),
            AgentDeployFixtures.PendingUploadService(environments),
            AgentDeployFixtures.PendingService(environments));

        return (operations, session);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("select")]
    public void NonInteractive_EnvironmentCommands_OpenNoIdentityUi(string verb)
    {
        var (operations, session) = RealFacade(
            new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1")),
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant),
            interactionAllowed: false);

        string[] args = verb == "list"
            ? ["env", "list", "--non-interactive"]
            : ["env", "select", "env-1", "--non-interactive"];

        var (exitCode, _, _) = Run(operations, args);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(0, session.TotalLoginCalls);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("select")]
    public void NonInteractive_WhenSignedOut_FailsClearly_WithoutOpeningIdentityUi(string verb)
    {
        var (operations, session) = RealFacade(
            new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1")),
            InMemoryAuthProfileStore.SignedOut(),
            interactionAllowed: false);

        string[] args = verb == "list"
            ? ["env", "list", "--non-interactive"]
            : ["env", "select", "env-1", "--non-interactive"];

        var (exitCode, _, stderr) = Run(operations, args);

        // Exit 3 is "not signed in": the command refuses rather than prompting.
        Assert.Equal(ExitCodes.NotSignedIn, exitCode);
        Assert.Contains("msagent auth create", stderr);
        Assert.Equal(0, session.TotalLoginCalls);
    }

    [Fact]
    public void EnvironmentCommands_OpenNoIdentityUi_EvenWhenInteractionIsAllowed()
    {
        // The non-interactive flag is not what keeps these commands safe — they have no interactive
        // path at all. Proving that separately stops a future change from adding one silently.
        var (operations, session) = RealFacade(
            new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1")),
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant),
            interactionAllowed: true);

        Run(operations, "env", "list");
        Run(operations, "env", "select", "env-1");

        Assert.Equal(0, session.TotalLoginCalls);
    }
}
