using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Ports;
using Microsoft.Agents.Hosting.Cli.Rendering;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AuthProfileCommandHostTests
{
    private const string Canary = "FAKE_AUTH_SECRET_CANARY_4A";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string App = "22222222-2222-2222-2222-222222222222";
    private static readonly IFeatureFlags ApplicationSupport = new TestFeatureFlags(FeatureName.AppUserSupport);

    public static TheoryData<string, string, string?> ApplicationOptions() => new()
    {
        { "--applicationId", "-id", App },
        { "--clientSecret", "-cs", Canary },
        { "--certificateDiskPath", "-cdp", "FAKE_CERTIFICATE.pfx" },
        { "--certificatePassword", "-cp", Canary },
        { "--managedIdentity", "-mi", null },
        { "--githubFederated", "-ghf", null },
        { "--azureDevOpsFederated", "-adof", null },
    };

    [Fact]
    public async Task ApplicationOptionsAreAbsentFromDefaultCreateHelp()
    {
        var result = await Run(new(), ["auth", "create", "--help"], featureFlags: new TestFeatureFlags());

        Assert.Equal(0, result.Exit);
        foreach (var row in ApplicationOptions())
        {
            Assert.DoesNotContain((string)row[0], result.Output);
            Assert.DoesNotMatch($@"(?<!\S){row[1]}(?=[,\s])", result.Output);
        }
        foreach (var option in new[] { "--name", "--environment", "--cloud", "--tenant", "--deviceCode", "--username", "--password" })
            Assert.Contains(option, result.Output);
    }

    [Theory]
    [MemberData(nameof(ApplicationOptions))]
    public async Task DisabledApplicationOptionsAndAliasesCannotDispatch(string option, string alias, string? value)
    {
        foreach (var spelling in new[] { option, alias })
        {
            var operations = new FakeOperations();
            var result = await Run(operations, ["auth", "create", .. ApplicationCreationArguments(option, spelling, value), "--json"],
                featureFlags: new TestFeatureFlags());

            Assert.Equal(2, result.Exit);
            Assert.Null(operations.LastAuthCreate);
            Assert.DoesNotContain(Canary, result.Output + result.Error);
            using var json = JsonDocument.Parse(result.Output);
            Assert.Equal(99002, json.RootElement.GetProperty("errorCode").GetInt32());
        }
    }

    [Theory]
    [MemberData(nameof(ApplicationOptions))]
    public async Task ApplicationOptInRestoresOptionsAliasesAndCreation(string option, string alias, string? value)
    {
        var help = await Run(new(), ["auth", "create", "--help"], featureFlags: ApplicationSupport);
        Assert.Equal(0, help.Exit);
        Assert.Contains(option, help.Output);
        Assert.Contains(alias, help.Output);
        foreach (var spelling in new[] { option, alias })
        {
            var operations = new FakeOperations();
            var result = await Run(operations, ["auth", "create", .. ApplicationCreationArguments(option, spelling, value),
                "--non-interactive=true", "--json"], featureFlags: ApplicationSupport);

            Assert.Equal(0, result.Exit);
            Assert.NotNull(operations.LastAuthCreate);
            Assert.DoesNotContain(Canary, result.Output + result.Error);
        }
    }

    private static string[] ApplicationCreationArguments(string option, string spelling, string? value) =>
    [
        "--tenant", Tenant,
        .. option == "--applicationId" ? Array.Empty<string>() : ["--applicationId", App],
        .. option switch
        {
            "--applicationId" => new[] { "--clientSecret", Canary },
            "--certificatePassword" => ["--certificateDiskPath", "FAKE_CERTIFICATE.pfx"],
            _ => [],
        },
        spelling,
        .. value is null ? Array.Empty<string>() : [value],
    ];

    [Theory]
    [InlineData()]
    [InlineData("--deviceCode")]
    [InlineData("--username", "FAKE_USER", "--password", Canary)]
    public async Task UserCreationRemainsAvailableWithoutApplicationSupport(params string[] options)
    {
        var operations = new FakeOperations();
        var result = await Run(operations, ["auth", "create", "--tenant", Tenant, .. options, "--json"],
            featureFlags: new TestFeatureFlags());

        Assert.Equal(0, result.Exit);
        Assert.Equal(Tenant, operations.LastAuthCreate?.Tenant);
    }

    [Fact]
    public async Task LegacyCommandsAreAbsentFromDefaultHelp()
    {
        var result = await Run(new(), ["auth", "--help"]);
        Assert.Equal(0, result.Exit);
        Assert.DoesNotMatch(@"(?m)^\s+login(?:\s|$)", result.Output);
        Assert.DoesNotMatch(@"(?m)^\s+logout(?:\s|$)", result.Output);
        foreach (var verb in new[] { "create", "list", "select", "delete", "name", "clear", "who", "status" })
            Assert.Matches($@"(?m)^\s+{verb}(?:\s|$)", result.Output);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("logout")]
    public async Task LegacyCommandsCannotDispatchWithoutOptIn(string verb)
    {
        var calls = 0;
        var operations = new FakeOperations
        {
            OnLogin = _ => { calls++; return new() { Status = "signed-in" }; },
            OnLogout = () => { calls++; return new() { Status = "signed-out" }; },
        };
        foreach (string[] options in new string[][] { [], ["--help"], ["--json"] })
        {
            var result = await Run(operations, ["auth", verb, .. options]);
            Assert.Equal(2, result.Exit);
            Assert.Equal(0, calls);
            if (options.Contains("--json"))
            {
                using var json = JsonDocument.Parse(result.Output);
                Assert.Equal(99002, json.RootElement.GetProperty("errorCode").GetInt32());
            }
        }
    }

    [Theory]
    [InlineData("login", "signed-in")]
    [InlineData("logout", "signed-out")]
    public async Task LegacyOptInRestoresHelpAndJsonContracts(string verb, string status)
    {
        var flags = new TestFeatureFlags(FeatureName.LegacyAuth);
        var help = await Run(new(), ["auth", verb, "--help"], featureFlags: flags);
        Assert.Equal(0, help.Exit);
        Assert.Contains($"msagent auth {verb}", help.Output);

        var operations = new FakeOperations();
        var result = await Run(operations, ["auth", verb, "--json"], featureFlags: flags);
        Assert.Equal(0, result.Exit);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(status, json.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        if (verb == "login")
        {
            Assert.NotNull(operations.LastLoginRequest);
            Assert.Equal("op@contoso.com", json.RootElement.GetProperty("account").GetString());
        }
    }

    [Theory]
    [InlineData(AuthPrincipalType.ServicePrincipal, "", App)]
    [InlineData(AuthPrincipalType.ServicePrincipal, " \t ", App)]
    [InlineData(AuthPrincipalType.ServicePrincipal, "existing-account", "existing-account")]
    [InlineData(AuthPrincipalType.User, "operator@example.test", "operator@example.test")]
    [InlineData(AuthPrincipalType.User, "", "")]
    public async Task HumanViewsDisplayApplicationIdForMissingNonUserAccountsWithoutChangingJson(
        AuthPrincipalType principalType, string account, string expectedAccount)
    {
        var profile = FakeOperations.SafeAuthProfile with
        {
            Account = account, ClientId = App, PrincipalType = principalType,
        };
        var result = new AuthProfileListResult([profile]);
        var output = new StringWriter();
        var renderer = new CliOutputRenderer(output, TextWriter.Null, color: false);

        renderer.RenderSuccess(result, json: false);

        var lines = output.ToString().Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("User/AppId", lines[0]);
        var accountColumn = lines[0].IndexOf("User/AppId", StringComparison.Ordinal);
        var cloudColumn = lines[0].IndexOf("Cloud", StringComparison.Ordinal);
        Assert.Equal(expectedAccount, lines[1][accountColumn..cloudColumn].TrimEnd());
        output.GetStringBuilder().Clear();

        var pickerOutput = new StringWriter();
        await new ConsoleAuthProfilePicker(new StringReader("q\n"), pickerOutput).PickAsync([profile], default);
        Assert.Contains($" | {expectedAccount} | {profile.Cloud} | ", pickerOutput.ToString());
        renderer.RenderSuccess(new AuthProfileOperationResult("selected", profile), json: false);
        Assert.Contains($" | {expectedAccount} | {profile.Cloud} | ", output.ToString());
        output.GetStringBuilder().Clear();

        renderer.RenderSuccess(result, json: true);

        using var json = JsonDocument.Parse(output.ToString());
        var metadata = Assert.Single(json.RootElement.GetProperty("profiles").EnumerateArray());
        Assert.Equal(account, metadata.GetProperty("account").GetString());
        Assert.Equal(App, metadata.GetProperty("clientId").GetString());
    }

    [Fact]
    public void ListRendersAlignedColumnsWithTheActiveMarkerInItsOwnColumn()
    {
        var result = new AuthProfileListResult(
        [
            FakeOperations.SafeAuthProfile with
            {
                Index = 12, IsActive = false, Name = "Dev with spaces",
                TenantId = Tenant,
                CredentialMethod = AuthCredentialMethod.Browser,
                ConnectionUrl = "https://api.powerplatform.com",
            },
            FakeOperations.SafeAuthProfile with
            {
                ProfileId = "workload-profile", Index = 3, IsActive = true, Name = null,
                TenantId = "33333333-3333-3333-3333-333333333333",
                Account = "", ClientId = App, Cloud = "UsGovHigh",
                PrincipalType = AuthPrincipalType.ServicePrincipal,
                CredentialMethod = AuthCredentialMethod.Certificate,
                ConnectionUrl = "https://high.api.gov.powerplatform.microsoft.us",
            },
        ]);
        var output = new StringWriter();

        new CliOutputRenderer(output, TextWriter.Null, color: false).RenderSuccess(result, json: false);

        string[] expected =
        [
            $"  {"Index",-5}  {"Active",-6}  {"Name",-15}  {"User/AppId",-36}  {"Cloud",-9}  {"Tenant",-36}  {"Type",-14}  Endpoint",
            $"  {"12",-5}  {"",-6}  {"Dev with spaces",-15}  {"operator@example.test",-36}  {"Public",-9}  {Tenant,-36}  {"User",-14}  https://api.powerplatform.com",
            $"  {"3",-5}  {"*",-6}  {"(unnamed)",-15}  {App,-36}  {"UsGovHigh",-9}  {"33333333-3333-3333-3333-333333333333",-36}  {"AppCertificate",-14}  https://high.api.gov.powerplatform.microsoft.us",
        ];
        Assert.Equal(expected, output.ToString().Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [Theory]
    [InlineData(AuthCredentialMethod.Broker, "OperatingSystem")]
    [InlineData(AuthCredentialMethod.Browser, "User")]
    [InlineData(AuthCredentialMethod.DeviceCode, "User")]
    [InlineData(AuthCredentialMethod.Password, "User")]
    [InlineData(AuthCredentialMethod.ClientSecret, "Application")]
    [InlineData(AuthCredentialMethod.Certificate, "AppCertificate")]
    [InlineData(AuthCredentialMethod.DefaultAzureCredential, "AzureIdentity")]
    [InlineData(AuthCredentialMethod.GitHubFederated, "GitHubFederated")]
    [InlineData(AuthCredentialMethod.AzureDevOpsFederated, "AzureDevOpsFederated")]
    public void ListUsesPacTypeLabelsForCredentialMethods(AuthCredentialMethod method, string expectedType)
    {
        var result = new AuthProfileListResult([FakeOperations.SafeAuthProfile with { CredentialMethod = method }]);
        var output = new StringWriter();

        new CliOutputRenderer(output, TextWriter.Null, color: false).RenderSuccess(result, json: false);

        var lines = output.ToString().Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("Type", lines[0]);
        var typeColumn = lines[0].IndexOf("Type", StringComparison.Ordinal);
        var endpointColumn = lines[0].IndexOf("Endpoint", StringComparison.Ordinal);
        Assert.Equal(expectedType, lines[1][typeColumn..endpointColumn].TrimEnd());
    }

    [Fact]
    public void ListRendersHeadersWithoutProfiles()
    {
        var output = new StringWriter();

        new CliOutputRenderer(output, TextWriter.Null, color: false)
            .RenderSuccess(new AuthProfileListResult([]), json: false);

        Assert.Equal($"  Index  Active  Name  User/AppId  Cloud  Tenant  Type  Endpoint{System.Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task UnexpectedCredentialExceptionNeverEmitsInputsOrInnerExceptions()
    {
        var result = await Run(new() { OnAuthCreate = (_, _) => throw new InvalidOperationException(Canary,
            new Exception("inner-" + Canary)) }, ["auth", "create", "-mi", "--json", "--debug"], featureFlags: ApplicationSupport);
        Assert.Equal(1, result.Exit);
        Assert.DoesNotContain(Canary, result.Output + result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(99001, json.RootElement.GetProperty("errorCode").GetInt32());
    }

    [Fact]
    public async Task InteractiveMetadataPickerIsAvailableAndDoesNotChooseAnImplicitDefault()
    {
        var type = typeof(AgentHostCli).Assembly.GetType("Microsoft.Agents.Hosting.Cli.Ports.ConsoleAuthProfilePicker");
        Assert.NotNull(type);
        var output = new StringWriter();
        var picker = (IAuthProfilePicker)Activator.CreateInstance(type, new StringReader("\ninvalid\n2\n"), output)!;
        var first = FakeOperations.SafeAuthProfile with { Name = null, IsActive = false, TenantId = Tenant };
        var second = first with { ProfileId = "second", Index = 2, TenantId = "33333333-3333-3333-3333-333333333333" };
        var chosen = await picker.PickAsync([first, second], default);
        Assert.Equal("second", chosen);
        Assert.Contains($"1   (unnamed) | {first.Account} | {first.Cloud} | Tenant: {first.TenantId}", output.ToString());
        Assert.Contains($"2   (unnamed) | {second.Account} | {second.Cloud} | Tenant: {second.TenantId}", output.ToString());
        Assert.Contains("Select", output.ToString());
        Assert.DoesNotContain("\u001B", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "(none)")]
    [InlineData("", "(none)")]
    [InlineData(" \t ", "(none)")]
    [InlineData(Tenant, Tenant)]
    [InlineData("\u001B[31mtenant", "\uFFFD[31mtenant")]
    public async Task HumanViewsDisplaySafeTenantMetadataWithoutChangingJson(string? tenant, string expectedTenant)
    {
        var profile = FakeOperations.SafeAuthProfile with
        {
            TenantId = tenant, Name = "\u001B[31mname",
            DefaultEnvironment = new("env-a", "Default", "44444444-4444-4444-4444-444444444444",
                "https://env.api.powerplatform.com", null),
        };
        var pickerOutput = new StringWriter();
        var chosen = await new ConsoleAuthProfilePicker(new StringReader("1\n"), pickerOutput).PickAsync([profile], default);
        Assert.Equal(profile.ProfileId, chosen);
        Assert.Contains($"Tenant: {expectedTenant}", pickerOutput.ToString());
        Assert.DoesNotContain("\u001B", pickerOutput.ToString(), StringComparison.Ordinal);

        var output = new StringWriter();
        var renderer = new CliOutputRenderer(output, TextWriter.Null, color: false);
        var result = new AuthProfileListResult([profile]);
        renderer.RenderSuccess(result, json: false);
        var lines = output.ToString().Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var tenantColumn = lines[0].IndexOf("Tenant", StringComparison.Ordinal);
        Assert.True(tenantColumn >= 0);
        var typeColumn = lines[0].IndexOf("Type", StringComparison.Ordinal);
        Assert.Equal(expectedTenant, lines[1][tenantColumn..typeColumn].TrimEnd());
        Assert.DoesNotContain("\u001B", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear();

        renderer.RenderSuccess(new AuthProfileOperationResult("selected", profile), json: false);
        Assert.Contains($"Tenant: {expectedTenant}", output.ToString());
        Assert.DoesNotContain("\u001B", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear();

        renderer.RenderSuccess(result, json: true);
        using var json = JsonDocument.Parse(output.ToString());
        var metadata = Assert.Single(json.RootElement.GetProperty("profiles").EnumerateArray());
        Assert.Equal(tenant, metadata.GetProperty("tenantId").GetString());
    }

    [Theory]
    [InlineData("created")]
    [InlineData("selected")]
    [InlineData("named")]
    [InlineData("deleted")]
    public void ProfileSummariesIdentifyTenantAndDeletedFallback(string status)
    {
        var profile = FakeOperations.SafeAuthProfile with { Name = null, TenantId = Tenant };
        var active = profile with { ProfileId = "fallback", TenantId = "33333333-3333-3333-3333-333333333333" };
        var output = new StringWriter();
        new CliOutputRenderer(output, TextWriter.Null, color: false)
            .RenderSuccess(new AuthProfileOperationResult(status, profile, active), json: false);
        Assert.Contains($"Tenant: {profile.TenantId}", output.ToString());
        if (status == "deleted")
            Assert.Contains($"Active profile: 1 (unnamed) | {active.Account} | {active.Cloud} | Tenant: {active.TenantId}", output.ToString());
    }

    private static async Task<(int Exit, string Output, string Error)> Run(
        FakeOperations operations, string[] args, IFeatureFlags? featureFlags = null, CancellationToken token = default)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await new AgentHostCli(operations, featureFlags: featureFlags).InvokeAsync(args, output, error, false, token);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task CreateBindsLocalAliasesAndSafeJson()
    {
        var operations = new FakeOperations();
        var result = await Run(operations, ["auth", "create", "-n", "Dev", "-env", "Environment",
            "-ci", "China", "-t", Tenant, "-id", App, "-cs", Canary, "--json", "--debug"], featureFlags: ApplicationSupport);
        Assert.Equal(0, result.Exit);
        var request = Assert.IsType<AuthCreateRequest>(operations.LastAuthCreate);
        Assert.Equal("Dev", request.Name);
        Assert.Equal("Environment", request.Environment);
        Assert.Equal(PowerPlatformApiTargetSelection.TargetFor(Core.Auth.CloudInstance.China), request.Target);
        Assert.Equal(Tenant, request.Tenant);
        Assert.Equal(App, request.ApplicationId);
        Assert.Equal(Canary, request.ClientSecret);
        Assert.Empty(request.Scopes);
        Assert.Null(request.Resource);
        Assert.DoesNotContain(Canary, result.Output + result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("opaque-profile", json.RootElement.GetProperty("profile").GetProperty("profileId").GetString());
        Assert.DoesNotContain("credentialReference", result.Output);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 2)]
    [InlineData(" \t ", 2)]
    [InlineData("unsupported", 2)]
    public async Task CreateCloudDefaultAndNonemptyRequirementAreUnchanged(string? cloud, int exit)
    {
        var operations = new FakeOperations();
        string[] options = cloud is null ? [] : ["--cloud=" + cloud];
        var result = await Run(operations, ["auth", "create", "-mi", .. options, "--json", "--non-interactive=true"],
            featureFlags: ApplicationSupport);
        Assert.Equal(exit, result.Exit);
        if (exit == 0)
            Assert.Equal(PowerPlatformApiTargetConfig.Public, operations.LastAuthCreate!.Target);
        else
            Assert.Null(operations.LastAuthCreate);
    }

    public static TheoryData<string[], AuthCredentialMethod> CredentialAliasBindings() => new()
    {
        { ["-un", "FAKE_USER", "-pw", Canary], AuthCredentialMethod.Password },
        { ["-t", Tenant, "-id", App, "-cdp", "FAKE_CERTIFICATE.pfx", "-cp", Canary], AuthCredentialMethod.Certificate },
        { ["-t", Tenant, "-id", App, "-ghf"], AuthCredentialMethod.GitHubFederated },
        { ["-t", Tenant, "-id", App, "-adof"], AuthCredentialMethod.AzureDevOpsFederated },
        { ["-mi"], AuthCredentialMethod.DefaultAzureCredential },
        { ["-dc"], AuthCredentialMethod.DeviceCode },
    };

    [Theory]
    [MemberData(nameof(CredentialAliasBindings))]
    public async Task LocalCredentialAliasesBindToApprovedCoreMethods(string[] options, AuthCredentialMethod method)
    {
        var operations = new FakeOperations();
        var result = await Run(operations, ["auth", "create", .. options, "--json"], featureFlags: ApplicationSupport);
        Assert.Equal(0, result.Exit);
        Assert.Equal(method, Auth.ProfileCredentialValidation.Method(operations.LastAuthCreate!, brokerSupported: false, interactionAllowed: true));
        Assert.DoesNotContain(Canary, result.Output + result.Error);
    }

    [Theory]
    [InlineData("--deviceCode", "-dc")]
    [InlineData("--managedIdentity", "-mi")]
    [InlineData("--githubFederated", "-ghf")]
    [InlineData("--azureDevOpsFederated", "-adof")]
    public async Task CreateHelpAdvertisesExactLocalSwitch(string option, string alias)
    {
        var result = await Run(new(), ["auth", "create", "--help"], featureFlags: ApplicationSupport);
        Assert.Equal(0, result.Exit);
        Assert.Contains(option, result.Output);
        Assert.Contains(alias, result.Output);
        Assert.DoesNotContain("--resource", result.Output);
        Assert.DoesNotContain("--scope", result.Output);
        Assert.DoesNotContain("--ring", result.Output);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("list")]
    [InlineData("select")]
    [InlineData("delete")]
    [InlineData("name")]
    [InlineData("clear")]
    [InlineData("who")]
    public async Task LifecycleIsReachableAndNeverExposesSeparateRing(string verb)
    {
        var help = await Run(new(), ["auth", verb, "--help"]);
        Assert.Equal(0, help.Exit);
        Assert.Contains($"msagent auth {verb}", help.Output);
        Assert.DoesNotContain("--ring", help.Output);
        Assert.Equal(2, (await Run(new(), ["auth", verb, "--ring", "test"])).Exit);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("token")]
    [InlineData("update")]
    [InlineData("update", "--help")]
    [InlineData("update", "--index", "1", "--name=", "--json")]
    [InlineData("update", "--index", "1", "--environment", "env-id", "--non-interactive=true")]
    public async Task RemovedVerbsAreRejected(params string[] args) =>
        Assert.Equal(2, (await Run(new(), ["auth", .. args])).Exit);

    [Theory]
    [InlineData("--kind")]
    [InlineData("--url")]
    [InlineData("-u")]
    [InlineData("--resource")]
    [InlineData("--scopes")]
    [InlineData("--loginHint")]
    public async Task CreateRejectsUnapprovedOptions(string option) =>
        Assert.Equal(2, (await Run(new(), ["auth", "create", option, "FAKE_VALUE"])).Exit);

    [Fact]
    public async Task SelectDeleteAndNameBindWithoutRetargeting()
    {
        var operations = new FakeOperations();
        Assert.Equal(0, (await Run(operations, ["auth", "select", "--name", "Dev", "--json"])).Exit);
        Assert.Equal("Dev", operations.LastAuthSelect?.Selector.Name);
        Assert.Equal(0, (await Run(operations, ["auth", "delete", "--index", "2", "--json"])).Exit);
        Assert.Equal(2, operations.LastAuthDelete?.Index);
        Assert.Equal(0, (await Run(operations, ["auth", "name", "--index", "2", "--name", "dev", "--json"])).Exit);
        Assert.Equal("dev", operations.LastAuthName?.Name);
        Assert.Equal(2, (await Run(operations, ["auth", "select", "--index", "1", "--name", "Dev"])).Exit);
        Assert.Equal(2, (await Run(operations, ["auth", "delete"])).Exit);
        Assert.Equal(2, (await Run(operations, ["auth", "name", "--name", "Dev"])).Exit);
    }

    [Theory]
    [InlineData("select", "--index", "2", 2, null)]
    [InlineData("select", "-i", "2", 2, null)]
    [InlineData("select", "--name", "Dev", null, "Dev")]
    [InlineData("select", "-n", "Dev", null, "Dev")]
    [InlineData("delete", "--index", "2", 2, null)]
    [InlineData("delete", "-i", "2", 2, null)]
    [InlineData("delete", "--name", "Dev", null, "Dev")]
    [InlineData("delete", "-n", "Dev", null, "Dev")]
    public async Task SelectAndDeleteBindLongAndShortSelectors(string verb, string option, string value, int? index, string? name)
    {
        var operations = new FakeOperations();
        var result = await Run(operations, ["auth", verb, option, value, "--json"]);
        Assert.Equal(0, result.Exit);
        Assert.Equal(new AuthProfileSelector(index, name),
            verb == "select" ? operations.LastAuthSelect?.Selector : operations.LastAuthDelete);
    }

    [Theory]
    [InlineData("select", "-i", "-n")]
    [InlineData("select", "-i", "--name")]
    [InlineData("select", "--index", "-n")]
    [InlineData("delete", "-i", "-n")]
    [InlineData("delete", "-i", "--name")]
    [InlineData("delete", "--index", "-n")]
    public async Task SelectAndDeleteRejectCombinedSelectorsUsingAliases(string verb, string indexOption, string nameOption)
    {
        var operations = new FakeOperations();
        var result = await Run(operations, ["auth", verb, indexOption, "2", nameOption, "Dev"]);
        Assert.Equal(2, result.Exit);
        Assert.Contains("Supply exactly one", result.Output + result.Error);
        Assert.Null(operations.LastAuthSelect);
        Assert.Null(operations.LastAuthDelete);
    }

    [Theory]
    [InlineData("--index", "--name")]
    [InlineData("-i", "-n")]
    [InlineData("-i", "--name")]
    [InlineData("--index", "-n")]
    public async Task NameBindsLongAndShortOptions(string indexOption, string nameOption)
    {
        var operations = new FakeOperations();
        var result = await Run(operations, ["auth", "name", indexOption, "2", nameOption, "Dev", "--json"]);
        Assert.Equal(0, result.Exit);
        Assert.Equal(2, operations.LastAuthName?.Selector.Index);
        Assert.Equal("Dev", operations.LastAuthName?.Name);
    }

    [Theory]
    [InlineData("-i", "2")]
    [InlineData("-n", "Dev")]
    public async Task NameStillRequiresBothOptionsUsingAliases(string option, string value)
    {
        var operations = new FakeOperations();
        var result = await Run(operations, ["auth", "name", option, value]);
        Assert.Equal(2, result.Exit);
        Assert.Null(operations.LastAuthName);
    }

    [Theory]
    [InlineData("select")]
    [InlineData("delete")]
    [InlineData("name")]
    public async Task ProfileCommandsHelpAdvertisesIndexAndNameAliases(string verb)
    {
        var result = await Run(new(), ["auth", verb, "--help"]);
        Assert.Equal(0, result.Exit);
        Assert.Matches(@"(?<!\S)-i(?=[,\s])", result.Output);
        Assert.Matches(@"(?<!\S)-n(?=[,\s])", result.Output);
    }

    public static TheoryData<string[]> SecretParseFailures() => new()
    {
        { ["auth", "create", "--password", Canary, "--bogus", "--json", "--debug"] },
        { ["auth", "create", "-pw=" + Canary, "--version", "--json"] },
        { ["auth", "create", "--clientSecret:" + Canary, "--help", "--version", "--json"] },
        { ["auth", "create", "-cp", "-" + Canary, "--json"] },
        { ["auth", "create", "--clientSecret=" + Canary, "--managedIdentity", "--json"] },
        { ["auth", "create", "-pw", Canary, "--username", "user", "--githubFederated", "--json"] },
    };

    public static TheoryData<string[]> DefaultFeatureCredentialParseFailures() => new()
    {
        { ["auth", "creat", "--clientSecret", Canary] },
        { ["auth", "creat", "-cs", Canary] },
        { ["auth", "creat", "--certificatePassword", Canary] },
        { ["auth", "creat", "-cp", Canary] },
        { ["auth", "creat", "--password", Canary] },
        { ["auth", "creat", "-pw", Canary] },
        { ["AUTH", "CREATE", "--ClientSecret=" + Canary] },
        { ["AUTH", "CREATE", "--CertificatePassword:" + Canary] },
    };

    [Theory]
    [MemberData(nameof(DefaultFeatureCredentialParseFailures))]
    public async Task MalformedCommandPathsNeverEchoCredentialsWithDefaultFeatures(string[] args)
    {
        foreach (bool jsonOutput in new[] { false, true })
        {
            var operations = new FakeOperations();
            var result = await Run(operations,
                [.. args, "--non-interactive", "--debug", .. jsonOutput ? ["--json"] : Array.Empty<string>()]);

            Assert.Equal(2, result.Exit);
            Assert.DoesNotContain(Canary, result.Output + result.Error);
            Assert.Null(operations.LastAuthCreate);
            if (jsonOutput)
            {
                using var json = JsonDocument.Parse(result.Output);
                Assert.Equal(99002, json.RootElement.GetProperty("errorCode").GetInt32());
                Assert.Equal(2, json.RootElement.GetProperty("exitCode").GetInt32());
            }
        }
    }

    [Fact]
    public async Task HelpWithDashPrefixedCredentialValueNeverEchoesIt()
    {
        var result = await Run(new(), ["auth", "create", "--password", "--" + Canary, "--help", "--json"]);
        Assert.Equal(0, result.Exit);
        Assert.DoesNotContain(Canary, result.Output + result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedCredentialSpellingsCannotLeakAtTheRootParser(bool json)
    {
        var args = new List<string> { "AUTH", "CREATE", "--PASSWORD=" + Canary, "--debug" };
        if (json) args.Add("--json");
        var result = await Run(new(), [.. args]);
        Assert.Equal(2, result.Exit);
        Assert.DoesNotContain(Canary, result.Output + result.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoncallerTimeoutRetainsRecognizedPartialState(bool json)
    {
        var operations = new FakeOperations { OnAuthCreate = (_, _) =>
            throw new AuthOperationCanceledException(true, true, FakeOperations.SafeAuthProfile,
                new OperationCanceledException(Canary)) };
        var args = new List<string> { "auth", "create", "-mi" };
        if (json) args.Add("--json");
        var result = await Run(operations, [.. args], featureFlags: ApplicationSupport);
        Assert.Equal(1, result.Exit);
        Assert.DoesNotContain(Canary, result.Output + result.Error);
        Assert.Contains("committed", result.Output + result.Error, StringComparison.OrdinalIgnoreCase);
        if (json)
        {
            using var doc = JsonDocument.Parse(result.Output);
            Assert.Equal(99004, doc.RootElement.GetProperty("errorCode").GetInt32());
            Assert.True(doc.RootElement.GetProperty("partialResult").GetProperty("metadataCommitted").GetBoolean());
        }
    }

    [Theory]
    [InlineData("create", "--cloud", "unsupported")]
    [InlineData("login", "--cloud", "unsupported")]
    [InlineData("login", "--tenant", "invalid-guid")]
    public async Task ExplicitInteractiveModeGatePrecedesSemanticInputValidation(string verb, string option, string value)
    {
        var operations = new FakeOperations();
        var result = await Run(operations, ["auth", verb, option, value, "--non-interactive=true", "--json"],
            featureFlags: new TestFeatureFlags(FeatureName.LegacyAuth));
        Assert.Equal(3, result.Exit);
        Assert.Null(operations.LastAuthCreate);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.Equal(12007, doc.RootElement.GetProperty("errorCode").GetInt32());
    }

    [Theory]
    [MemberData(nameof(SecretParseFailures))]
    public async Task AllCredentialDiagnosticsAreSafeAndUseUsageEnvelope(string[] args)
    {
        var result = await Run(new(), args, featureFlags: ApplicationSupport);
        Assert.DoesNotContain(Canary, result.Output + result.Error);
        Assert.Equal(2, result.Exit);
        Assert.Empty(result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(99002, json.RootElement.GetProperty("errorCode").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitInteractiveCreateFailsBeforeDispatch(bool device)
    {
        var operations = new FakeOperations();
        var args = new List<string> { "auth", "create", "--non-interactive", "--json" };
        if (device) args.Add("-dc");
        var result = await Run(operations, [.. args]);
        Assert.Equal(3, result.Exit);
        Assert.Null(operations.LastAuthCreate);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal(12007, json.RootElement.GetProperty("errorCode").GetInt32());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task CallerCancellationReportsOnlySafeDurableState(bool committed, bool json, bool markers)
    {
        using var cancellation = new CancellationTokenSource();
        var operations = new FakeOperations
        {
            OnAuthCreate = (_, token) =>
            {
                cancellation.Cancel();
                var primary = new OperationCanceledException(Canary, token);
                primary.Data["untrusted"] = Canary;
                if (markers)
                {
                    primary.Data["msagent.metadata-committed"] = committed;
                    primary.Data["msagent.owned-cleanup"] = Canary;
                    throw primary;
                }
                throw new AuthOperationCanceledException(committed, true,
                    committed ? FakeOperations.SafeAuthProfile : null, primary);
            },
        };
        var args = new List<string> { "auth", "create", "-mi" };
        if (json) args.Add("--json");
        var result = await Run(operations, [.. args], featureFlags: ApplicationSupport, token: cancellation.Token);
        Assert.Equal(130, result.Exit);
        Assert.DoesNotContain(Canary, result.Output + result.Error);
        Assert.Contains("cleanup", result.Output + result.Error, StringComparison.OrdinalIgnoreCase);
        if (json)
        {
            using var document = JsonDocument.Parse(result.Output);
            var partial = document.RootElement.GetProperty("partialResult");
            Assert.Equal(committed, partial.GetProperty("metadataCommitted").GetBoolean());
            Assert.True(partial.GetProperty("cleanupIncomplete").GetBoolean());
            Assert.False(partial.TryGetProperty("profileDeleted", out _));
            Assert.False(partial.TryGetProperty("activeProfile", out _));
            Assert.Equal(130, document.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Empty(result.Error);
        }
        else
        {
            Assert.Empty(result.Output);
            Assert.Contains(committed ? "was committed" : "was not committed", result.Error);
            Assert.DoesNotContain("120", result.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimaryFailureKeepsItsCodeWithSafeCommittedAndCleanupGuidance(bool json)
    {
        var operations = new FakeOperations { OnAuthCreate = (_, _) =>
            throw new AuthOperationException(true, true, FakeOperations.SafeAuthProfile,
                new CacheProtectionUnavailableException(Canary)) };
        var args = new List<string> { "auth", "create", "-mi" };
        if (json) args.Add("--json");
        var result = await Run(operations, [.. args], featureFlags: ApplicationSupport);
        Assert.Equal(1, result.Exit);
        Assert.DoesNotContain(Canary, result.Output + result.Error);
        Assert.Contains("committed", result.Output + result.Error, StringComparison.OrdinalIgnoreCase);
        if (json)
        {
            using var doc = JsonDocument.Parse(result.Output);
            Assert.Equal(12006, doc.RootElement.GetProperty("errorCode").GetInt32());
            Assert.True(doc.RootElement.GetProperty("partialResult").GetProperty("cleanupIncomplete").GetBoolean());
        }
        else
        {
            Assert.Empty(result.Output);
            Assert.DoesNotContain("12006", result.Error);
        }
    }

    [Fact]
    public async Task HumanProfileResultsReportSelectionAndDeletedFallback()
    {
        var who = await Run(new(), ["auth", "who"]);
        Assert.Equal(0, who.Exit);
        Assert.Contains("Profile:", who.Output, StringComparison.Ordinal);
        Assert.Contains(FakeOperations.SafeAuthProfile.Account, who.Output, StringComparison.Ordinal);
        var deletion = await Run(new(), ["auth", "delete", "--index", "1"]);
        Assert.Equal(0, deletion.Exit);
        Assert.Contains("Active profile: none", deletion.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionCacheIdentityIncludesBothKindsOfAliases()
    {
        var builder = new CommandTreeBuilder(new CliOutputRenderer(TextWriter.Null, TextWriter.Null, false),
            new FacadeHandlerProvider(new FakeOperations()), TestFeatureFlags.AllProductSurfaces,
            new Option<bool>("--json"), new Option<bool>("--non-interactive"));
        var materialise = typeof(CommandTreeBuilder).GetMethod("Materialise", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var first = new CommandOption("--local", "Same description") { ShortAlias = "-a", ExtraAliases = ["--first"] };
        var second = first with { ShortAlias = "-b", ExtraAliases = ["--second"] };
        var a = (Option)materialise.Invoke(builder, [first])!;
        var b = (Option)materialise.Invoke(builder, [second])!;
        Assert.NotSame(a, b);
        Assert.Same(a, materialise.Invoke(builder, [first]));
        Assert.Contains("-b", b.Aliases);
        Assert.Contains("--second", b.Aliases);
        Assert.DoesNotContain("-a", b.Aliases);
    }
}
