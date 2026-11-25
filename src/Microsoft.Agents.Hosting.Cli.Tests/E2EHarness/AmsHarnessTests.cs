using Microsoft.Agents.Hosting.Cli.Core;
using Microsoft.Agents.Hosting.Cli.E2E;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

[Collection(nameof(HarnessVariablesCollection))]
public sealed class AmsHarnessTests : IDisposable
{
    private const string Tenant = "5cae182f-63ff-49f9-9f35-3acc2724e738";
    private const string Home = "245ed99c-a1cc-eae7-9ab5-367e3d921f1d";
    private const string Test = "39b8de47-a594-e9f6-bb18-bac57b8ad289";
    private const string Prod = "de2285e4-8ea7-e717-9e9a-a17ac242ae4b";
    private const string ControlPlane = "https://ams.example.test";
    private const string DataPlane = "https://amsdp.example.test";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "msagent-ams-harness-" + Guid.NewGuid().ToString("N"));
    private readonly HarnessVariableScope _variables = new();

    public AmsHarnessTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _variables.Dispose();

        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void TheAmsSectionReadsItsEndpointsEnvironmentsAndSwitches()
    {
        File.WriteAllText(Path.Combine(_directory, "e2e.config.json"), $$"""{ "ams": { "controlPlane": { "endpoint": "{{ControlPlane}}" }, "dataPlane": { "endpoint": "{{DataPlane}}" }, "cloud": "test", "devEnvironmentId": "{{Home}}", "testEnvironmentId": "{{Test}}", "prodEnvironmentId": "{{Prod}}", "allowCreate": true, "packagePath": "echo.zip" } }""");

        var ams = E2EConfig.Load(_directory).Ams;

        Assert.Equal(ControlPlane, ams.ControlPlaneEndpoint);
        Assert.Equal(DataPlane, ams.DataPlane.Endpoint);
        Assert.Equal("test", ams.Cloud);
        Assert.Equal(new string?[] { Home, Test, Prod }, new[] { ams.DevEnvironmentId, ams.TestEnvironmentId, ams.ProdEnvironmentId });
        Assert.True(ams.AllowCreate);
        Assert.Equal("echo.zip", ams.PackagePath);
    }

    [Theory]
    [InlineData("""{ "ams": { "endpoint": "https://legacy.example.test" } }""", "https://legacy.example.test")]
    [InlineData("""{ "ams": { "endpoint": "https://legacy.example.test", "controlPlane": { "endpoint": "https://ams.example.test" } } }""", "https://ams.example.test")]
    public void TheOriginalEndpointSettingNamesTheControlPlaneUnlessControlPlaneEndpointIsSet(string config, string expected)
    {
        File.WriteAllText(Path.Combine(_directory, "e2e.config.json"), config);

        Assert.Equal(expected, E2EConfig.Load(_directory).Ams.ControlPlaneEndpoint);
    }

    [Theory]
    [InlineData(E2EConfig.EnvironmentPrefix)]
    [InlineData(E2EConfig.LegacyEnvironmentPrefix)]
    public void EnvironmentVariablesOverrideEveryAmsSetting(string prefix)
    {
        HarnessVariableScope.Set(prefix + "AMS_CONTROL_PLANE_ENDPOINT", ControlPlane);
        HarnessVariableScope.Set(prefix + "AMS_DATA_PLANE_ENDPOINT", DataPlane);
        HarnessVariableScope.Set(prefix + "AMS_CLOUD", "test");
        HarnessVariableScope.Set(prefix + "AMS_DEV_ENVIRONMENT_ID", Home);
        HarnessVariableScope.Set(prefix + "AMS_TEST_ENVIRONMENT_ID", Test);
        HarnessVariableScope.Set(prefix + "AMS_PROD_ENVIRONMENT_ID", Prod);
        HarnessVariableScope.Set(prefix + "AMS_ALLOW_CREATE", "true");
        HarnessVariableScope.Set(prefix + "AMS_PACKAGE_PATH", "echo.zip");

        var ams = E2EConfig.Load(_directory).Ams;

        Assert.Equal(new string?[] { ControlPlane, DataPlane, "test", Home, Test, Prod, "echo.zip" }, new[] { ams.ControlPlaneEndpoint, ams.DataPlane.Endpoint, ams.Cloud, ams.DevEnvironmentId, ams.TestEnvironmentId, ams.ProdEnvironmentId, ams.PackagePath });
        Assert.True(ams.AllowCreate);
    }

    [Fact]
    public void EveryAmsCapabilityIsAvailableWhenEverySettingIsSuppliedAndThePackageExists()
    {
        var capabilities = Resolve(ams => ams);

        Assert.All(Capabilities.AmsChain, capability => Assert.True(capabilities.Has(capability), $"{capability}: {capabilities.Reason(capability)}"));
    }

    [Theory]
    [InlineData("dataPlane", Capability.Ams, "no ams.dataPlane.endpoint supplied")]
    [InlineData("home", Capability.Ams, "no ams.devEnvironmentId supplied")]
    [InlineData("allowCreate", Capability.AmsCreate, "ams.allowCreate is false")]
    [InlineData("test", Capability.AmsCreate, "no ams.testEnvironmentId supplied")]
    [InlineData("package", Capability.AmsPackage, "no package at")]
    [InlineData("prod", Capability.AmsProd, "no ams.prodEnvironmentId supplied")]
    public void AMissingSettingTakesItsCapabilityAndEveryOneThatNeedsIt(string missing, Capability first, string reason)
    {
        var capabilities = Resolve(ams => missing switch
        {
            "dataPlane" => ams with { DataPlane = new AmsServiceConfig() },
            "home" => ams with { DevEnvironmentId = null },
            "allowCreate" => ams with { AllowCreate = false },
            "test" => ams with { TestEnvironmentId = null },
            "package" => ams with { PackagePath = Path.Combine(_directory, "absent.zip") },
            _ => ams with { ProdEnvironmentId = null },
        });

        AssertUnavailableFrom(capabilities, first, reason);
    }

    [Fact]
    public void AnAmsRunNeedsASignInAndAzOnThePath()
    {
        AssertUnavailableFrom(Resolve(ams => ams, signedIn: false), Capability.Ams, "not signed in");
        AssertUnavailableFrom(Resolve(ams => ams, azureCliPresent: false), Capability.AmsCreate, "az is not on PATH");
    }

    [Fact]
    public void WithoutAmsTakesTheCapabilityAndEveryOneThatNeedsItButNoneBefore()
    {
        var capabilities = Resolve(ams => ams).WithoutAms(Capability.AmsCreate, "az is signed in elsewhere");

        Assert.True(capabilities.Has(Capability.Ams));
        AssertUnavailableFrom(capabilities, Capability.AmsCreate, "az is signed in elsewhere");
    }

    [Fact]
    public void TheSignInIsReadFromAuthStatusOnlyWhenItReportsOne()
    {
        Assert.Equal(new AmsSignIn("test", Tenant), AmsServices.ReadSignIn(Result($$"""{"signedIn":true,"cloud":"test","tenantId":"{{Tenant}}"}""")));
        Assert.Null(AmsServices.ReadSignIn(Result("""{"signedIn":false}""")));
        Assert.Null(AmsServices.ReadSignIn(Result("not json")));
        Assert.Null(AmsServices.ReadSignIn(Result($$"""{"signedIn":true,"tenantId":"{{Tenant}}"}""", exitCode: 3)));
    }

    [Fact]
    public void ASignInMatchesTheExpectedTenantInAnyCaseAndTheCloudOnlyWhenOneIsExpected()
    {
        Assert.Null(AmsServices.SignInMismatch(new AmsSignIn("test", Tenant.ToUpperInvariant()), "TEST", Tenant));
        Assert.Null(AmsServices.SignInMismatch(new AmsSignIn("prod", Tenant), null, Tenant));
        Assert.Contains("the prod cloud, not test", AmsServices.SignInMismatch(new AmsSignIn("prod", Tenant), "test", Tenant), StringComparison.Ordinal);
        Assert.Contains($"tenant {Home}, not {Tenant}", AmsServices.SignInMismatch(new AmsSignIn("test", Home), "test", Tenant), StringComparison.Ordinal);
        Assert.Equal("msagent reports no sign-in", AmsServices.SignInMismatch(null, "test", Tenant));
    }

    [Fact]
    public void AzMustBeSignedInToTheExpectedTenant()
    {
        Assert.Null(AmsServices.AzureTenantMismatch(Result($"{Tenant}\r\n"), Tenant));
        Assert.Contains("az is not signed in", AmsServices.AzureTenantMismatch(Result(string.Empty, exitCode: 1), Tenant), StringComparison.Ordinal);
        Assert.Contains($"tenant {Home}, not {Tenant}", AmsServices.AzureTenantMismatch(Result(Home), Tenant), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheProbeTakesEveryAmsCapabilityForAnotherSignInAndRecordsTheSignInItFound()
    {
        var services = Services(Config(ams => ams));

        var capabilities = await services.ProbeAsync(Resolve(ams => ams), Result($$"""{"signedIn":true,"cloud":"prod","tenantId":"{{Tenant}}"}"""), Azure(), CancellationToken.None);

        AssertUnavailableFrom(capabilities, Capability.Ams, "the prod cloud, not test");
        Assert.Equal(new AmsSignIn("prod", Tenant), services.SignIn);
    }

    [Fact]
    public async Task TheProbeLeavesAzAloneWhenTheRunCannotCreate()
    {
        var services = Services(Config(ams => ams with { AllowCreate = false }));

        var capabilities = await services.ProbeAsync(Resolve(ams => ams with { AllowCreate = false }), Result($$"""{"signedIn":true,"cloud":"test","tenantId":"{{Tenant}}"}"""), Azure(), CancellationToken.None);

        Assert.True(capabilities.Has(Capability.Ams));
        Assert.Equal("test", services.CleanupContext.Cloud);
        Assert.Equal(Home, services.CleanupContext.HomeEnvironmentId);
    }

    [Fact]
    public async Task TheProbeChangesNothingWhenTheAmsSettingsAreMissing()
    {
        var services = Services(Config(ams => ams with { DataPlane = new AmsServiceConfig() }));
        var resolved = Resolve(ams => ams with { DataPlane = new AmsServiceConfig() });

        var capabilities = await services.ProbeAsync(resolved, Result("{}"), Azure(), CancellationToken.None);

        Assert.Same(resolved, capabilities);
        Assert.Null(services.SignIn);
    }

    [Fact]
    public void OnlyScenariosNeedingAnAmsCapabilityAreServed()
    {
        Assert.True(AmsServices.Serves(Scenario([Capability.Auth, Capability.AmsCreate])));
        Assert.False(AmsServices.Serves(Scenario([Capability.Auth, Capability.AzureCreate])));
    }

    [Fact]
    public void ItemsReadsTheArrayAtAPathAndNothingElse()
    {
        var root = AmsOutput.Document(Result("""{"agents":[{"agentId":"a"},{"agentId":"b"}],"agent":{"agentId":"a"}}""")).GetValueOrDefault();

        Assert.Equal("a,b", string.Join(',', AmsOutput.Items(root, "agents").Select(item => AmsOutput.Text(item, "agentId"))));
        Assert.Empty(AmsOutput.Items(root, "agent"));
        Assert.Empty(AmsOutput.Items(root, "missing"));
    }

    [Theory]
    [InlineData(1, "ERROR: When using this permission, the backing application of the service principal being created must in the local tenant", true)]
    [InlineData(1, "ERROR: Resource '8a5206d8-2905-44b8-99d8-886b81182882' does not exist or one of its queried reference-property objects are not present.", true)]
    [InlineData(1, "ERROR: Another object with the same value for property servicePrincipalNames already exists.", false)]
    [InlineData(0, "does not exist", false)]
    public void OnlyAServicePrincipalRefusedForAnAppRegistrationEntraDoesNotSeeYetIsTriedAgain(int exitCode, string error, bool notYetVisible) =>
        Assert.Equal(notYetVisible, CloudProbe.IsAppRegistrationNotYetVisible(new MsagentResult([], exitCode, string.Empty, error, TimeSpan.Zero)));

    [Fact]
    public void TheAppRegistrationLookupAndItsManualCommandKeepOnlyTheExactName()
    {
        const string DisplayName = "ahe2e-test-pool-dev-app";
        var arguments = CloudProbe.AppRegistrationListArguments(DisplayName);

        var command = Cleanup.ManualCommand(Ledger.Create(_directory, "run-id").RecordIntendedEntraApp(DisplayName, Tenant, "AC-10"));

        Assert.Equal($"[?displayName=='{DisplayName}'].appId", arguments[Array.IndexOf(arguments, "--query") + 1]);
        Assert.Contains(CommandLineArgument.Quote($"[?displayName=='{DisplayName}'].appId"), command, StringComparison.Ordinal);
        Assert.EndsWith("only if it prints exactly one", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaterOverlayWinsAndANullValueRemovesAnInheritedVariable()
    {
        var environment = new Dictionary<string, string?> { [MsagentFeatures.Variable] = "OtherFeatures", ["AH_CLI_AMS_ENDPOINT"] = "https://inherited.example.test" };

        AmsServices.Overlay(ControlPlane, DataPlane).Then(EnvironmentOverlay.Empty.Remove("AH_CLI_AMS_ENDPOINT")).ApplyTo(environment);

        Assert.Equal(MsagentFeatures.Ams, environment[MsagentFeatures.Variable]);
        Assert.False(environment.ContainsKey("AH_CLI_AMS_ENDPOINT"));
        Assert.Equal(DataPlane, environment["AH_CLI_AMSDP_ENDPOINT"]);
    }

    [Fact]
    public async Task OnlyTheAmsMsagentCarriesTheAmsVariables()
    {
        var fake = new FakeMsagent(Path.Combine(_directory, "msagent"), "{}");
        var msagent = new MsagentCli(fake.ExecutablePath, new Transcript(_directory), Path.Combine(_directory, "dumps"));
        var services = new AmsServices(Config(ams => ams), msagent);

        await msagent.RunAsync(["auth", "status"]);
        await services.Msagent.RunAsync(["agent", "list"]);

        Assert.Equal(new[] { $"{Environment.GetEnvironmentVariable(MsagentFeatures.Variable)}|{Environment.GetEnvironmentVariable("AH_CLI_AMS_ENDPOINT")}|{Environment.GetEnvironmentVariable("AH_CLI_AMSDP_ENDPOINT")}", $"{MsagentFeatures.Ams}|{ControlPlane}|{DataPlane}" }, fake.Environments);
    }

    [Theory]
    [InlineData(Home, Test, Prod, null, null)]
    [InlineData(Home, null, null, null, null)]
    [InlineData(Home, Test, Prod, Test, "ams.testEnvironmentId is listed in mcs.forbiddenEnvironmentIds")]
    [InlineData(Home, Home, Prod, null, "ams.devEnvironmentId and ams.testEnvironmentId name the same environment")]
    [InlineData(Home, Test, Test, null, "ams.testEnvironmentId and ams.prodEnvironmentId name the same environment")]
    public void TheAmsEnvironmentsMustBeDistinctAndNeverForbidden(string? home, string? test, string? prod, string? forbidden, string? problem)
    {
        var config = new E2EConfig { Mcs = new McsConfig { ForbiddenEnvironmentIds = forbidden is null ? [] : [forbidden] }, Ams = new AmsConfig { DevEnvironmentId = home, TestEnvironmentId = test, ProdEnvironmentId = prod } };

        var found = HarnessEnvironment.AmsEnvironmentProblem(config);

        if (problem is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.StartsWith(problem, found, StringComparison.Ordinal);
        }
    }

    private static MsagentResult Result(string standardOutput, int exitCode = 0) => new([], exitCode, standardOutput, string.Empty, TimeSpan.Zero);

    private static Scenario Scenario(IReadOnlyList<Capability> requires) => new() { Id = "AC-00", Title = "probe", Feature = "ams-code-agent", Requires = requires, Tier = SafetyTier.ReadOnly, Run = _ => Task.CompletedTask };

    private static void AssertUnavailableFrom(Capabilities capabilities, Capability first, string reason)
    {
        foreach (var capability in Capabilities.AmsChain.SkipWhile(candidate => candidate != first))
        {
            Assert.False(capabilities.Has(capability), $"{capability} should be unavailable");
            Assert.Contains(reason, capabilities.Reason(capability), StringComparison.Ordinal);
        }
    }

    private E2EConfig Config(Func<AmsConfig, AmsConfig> change)
    {
        var package = Path.Combine(_directory, "echo.zip");
        File.WriteAllText(package, "package");

        return new E2EConfig { TenantId = Tenant, Ams = change(new AmsConfig { ControlPlane = new AmsServiceConfig { Endpoint = ControlPlane }, DataPlane = new AmsServiceConfig { Endpoint = DataPlane }, Cloud = "test", DevEnvironmentId = Home, TestEnvironmentId = Test, ProdEnvironmentId = Prod, AllowCreate = true, PackagePath = package }) };
    }

    private Capabilities Resolve(Func<AmsConfig, AmsConfig> change, bool signedIn = true, bool azureCliPresent = true) => Capabilities.Resolve(Config(change), _directory, signedIn, signedIn ? "signed in" : "not signed in", azureCliPresent);

    private AmsServices Services(E2EConfig config) => new(config, new MsagentCli(Path.Combine(_directory, "unused"), new Transcript(_directory), Path.Combine(_directory, "dumps")));

    private AzureCli Azure() => new(new Transcript(_directory));
}
