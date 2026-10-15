using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Covers how the host picks a Power Platform API ring: it is read from the active auth profile
/// before the container exists, and state the store cannot read fails rather than silently
/// retargeting the run at production.
/// </summary>
public sealed class ProgramRingResolutionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ah-ring-{Guid.NewGuid():N}");

    public ProgramRingResolutionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteProfiles(string json)
    {
        File.WriteAllText(ProfilesPath, json);

        return _directory;
    }

    private string ProfilesPath => Path.Combine(_directory, "auth-profiles-csharp.json");

    private async Task<string> StoreProfilesAsync(string activeKey, params (string Key, PowerPlatformApiRing? Ring)[] profiles)
    {
        var store = new FileAuthProfileStore(ProfilesPath);
        foreach (var (key, ring) in profiles)
        {
            var profile = new AuthProfile { AccountKey = key, Username = $"{key}@contoso.com" };
            await store.UpsertAsync(ring is null ? profile : profile with { Ring = ring.Value }, default);
        }

        await store.SetActiveAsync(activeKey, default);

        return _directory;
    }

    private static async Task<string> TokenAsync(string configDirectory) =>
        PowerPlatformApiRingSelection.CanonicalToken(
            (await Program.ResolvePowerPlatformApiTargetAsync(configDirectory)).Ring);

    [Fact]
    public async Task WithNoProfilesFileTheHostTargetsProduction()
    {
        Assert.Equal("prod", await TokenAsync(_directory));
    }

    [Theory]
    [InlineData(PowerPlatformApiRing.Production, "prod")]
    [InlineData(PowerPlatformApiRing.Preprod, "preprod")]
    [InlineData(PowerPlatformApiRing.Test, "test")]
    public async Task TheActiveProfilesRingIsTheTarget(PowerPlatformApiRing stored, string expected)
    {
        Assert.Equal(expected, await TokenAsync(await StoreProfilesAsync("a", ("a", stored))));
    }

    [Fact]
    public async Task OnlyTheActiveProfilesRingCounts()
    {
        Assert.Equal(
            "preprod",
            await TokenAsync(await StoreProfilesAsync("b", ("a", PowerPlatformApiRing.Test), ("b", PowerPlatformApiRing.Preprod))));
    }

    [Fact]
    public async Task AProfileWithNoRingTargetsProduction()
    {
        Assert.Equal("prod", await TokenAsync(await StoreProfilesAsync("a", ("a", null))));
    }

    [Fact]
    public async Task AnActivePointerNamingNoProfileIsRefused()
    {
        var json = """
        {"schemaVersion":1,"activeAccountKey":"missing","profiles":[{"accountKey":"a","ring":"Preprod"}]}
        """;

        await Assert.ThrowsAsync<AuthProfileStateException>(() => TokenAsync(WriteProfiles(json)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"schemaVersion":1,"profiles":[]}""")]
    public async Task AProfilesFileWithNobodyActiveTargetsProduction(string json)
    {
        Assert.Equal("prod", await TokenAsync(WriteProfiles(json)));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"schemaVersion":1,"activeAccountKey":"a"}""")]
    [InlineData("""{"schemaVersion":1,"activeAccountKey":"a","profiles":{}}""")]
    [InlineData("""{"schemaVersion":1,"activeAccountKey":"a","profiles":[{"accountKey":"a","ring":"nonsense"}]}""")]
    [InlineData("""{"schemaVersion":99,"activeAccountKey":"a","profiles":[{"accountKey":"a","ring":"Preprod"}]}""")]
    [InlineData("""{"schemaVersion":1,"activeAccountKey":"a","profiles":[{"accountKey":"a","ring":987}]}""")]
    public async Task AnUnreadableProfilesFileIsRefusedRatherThanTargetingProduction(string json)
    {
        var directory = WriteProfiles(json);

        var failure = await Assert.ThrowsAsync<AuthProfileStateException>(() => TokenAsync(directory));

        Assert.NotNull(failure.Remediation);
    }

    [Fact]
    public async Task AnUnreadableProfilesFileIsReportedInsteadOfNamingACloud()
    {
        var directory = WriteProfiles("not json at all");

        var failure = await Assert.ThrowsAsync<AuthProfileStateException>(() => TokenAsync(directory));
        var message = Program.DescribeUnreadableRing(directory, failure);

        Assert.Contains(ProfilesPath, message, StringComparison.Ordinal);
        Assert.Contains("nothing was run", message, StringComparison.Ordinal);
        Assert.Contains("auth logout", message, StringComparison.Ordinal);
        Assert.True(
            message.IndexOf("auth logout", StringComparison.Ordinal)
                < message.IndexOf("auth login", StringComparison.Ordinal),
            "Clearing the stored sign-in has to be recommended before signing in again, because signing in reads it first.");

        foreach (var token in PowerPlatformApiRingSelection.AcceptedTokens)
        {
            Assert.DoesNotContain(token, message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DescribingAnUnreadableRingRequiresItsInputs()
    {
        Assert.ThrowsAny<ArgumentException>(() => Program.DescribeUnreadableRing("  ", new InvalidOperationException("x")));
        Assert.Throws<ArgumentNullException>(() => Program.DescribeUnreadableRing(_directory, null!));
    }

    public static TheoryData<string[]> RecoveryCommands() =>
    [
        ["auth", "logout"],
        ["auth", "login", "--cloud", "preprod"],
        ["--help"],
        ["--version"],
    ];

    [Theory]
    [MemberData(nameof(RecoveryCommands))]
    public async Task ARecoveryCommandStillStartsWhenTheStoredSignInCannotBeRead(string[] args)
    {
        var directory = WriteProfiles("not json at all");

        var invocation = await Program.ResolveTargetForInvocationAsync(directory, args);

        Assert.Equal(PowerPlatformApiRing.Production, invocation.Target.Ring);
        Assert.False(invocation.RingKnown);
    }

    [Theory]
    [MemberData(nameof(RecoveryCommands))]
    public async Task ARecoveryCommandKeepsAStoredRingItCanRead(string[] args)
    {
        var directory = await StoreProfilesAsync("a", ("a", PowerPlatformApiRing.Preprod));

        var invocation = await Program.ResolveTargetForInvocationAsync(directory, args);

        Assert.Equal(PowerPlatformApiRing.Preprod, invocation.Target.Ring);
        Assert.True(invocation.RingKnown);
    }

    [Fact]
    public async Task EveryOtherCommandStillReadsTheStoredRing()
    {
        var directory = await StoreProfilesAsync("a", ("a", PowerPlatformApiRing.Preprod));

        var invocation = await Program.ResolveTargetForInvocationAsync(directory, ["env", "list"]);

        Assert.Equal(PowerPlatformApiRing.Preprod, invocation.Target.Ring);
        Assert.True(invocation.RingKnown);
    }

    [Fact]
    public async Task EveryOtherCommandStillRefusesAnUnreadableStoredSignIn()
    {
        var directory = WriteProfiles("not json at all");

        await Assert.ThrowsAsync<AuthProfileStateException>(
            () => Program.ResolveTargetForInvocationAsync(directory, ["env", "list"]));
    }

    [Fact]
    public async Task ResolvingATargetForAnInvocationRequiresItsInputs()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Program.ResolveTargetForInvocationAsync("  ", ["auth", "logout"]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Program.ResolveTargetForInvocationAsync(_directory, null!));
    }

    [Fact]
    public async Task ARingStoredAsItsNumberIsStillRead()
    {
        var json = $$"""
        {"schemaVersion":1,"activeAccountKey":"a","profiles":[{"accountKey":"a","ring":{{(int)PowerPlatformApiRing.Preprod}}}]}
        """;

        Assert.Equal("preprod", await TokenAsync(WriteProfiles(json)));
    }

    [Fact]
    public async Task AConfigDirectoryIsRequired()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Program.ResolvePowerPlatformApiTargetAsync(null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Program.ResolvePowerPlatformApiTargetAsync("  "));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => ActiveProfileRing.ReadAsync("  "));
    }

    [Fact]
    public void TheConfigDirectoryIsSharedByEveryRing()
    {
        Assert.Equal("cfg", Program.ResolveConfigDirectory(name => name == "AH_CLI_CONFIG_DIR" ? "cfg" : null));
        Assert.Equal("agenthost-cli-csharp", Path.GetFileName(Program.ResolveConfigDirectory(_ => null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankConfigDirectoryOverrideFallsBackToTheDefault(string configured)
    {
        Assert.Equal(
            "agenthost-cli-csharp",
            Path.GetFileName(Program.ResolveConfigDirectory(name => name == "AH_CLI_CONFIG_DIR" ? configured : null)));
    }

    [Fact]
    public void ANullEnvironmentReaderIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => Program.ResolveConfigDirectory(null!));
    }
}

/// <summary>
/// Pins where the ring may be set. It belongs to the sign-in, so <c>--cloud</c> exists on the two
/// auth commands and nowhere else, and no environment variable selects it.
/// </summary>
public sealed class RingSurfaceTests
{
    private static (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = new AgentHostCli(new FakeOperations(), featureFlags: TestFeatureFlags.AllProductSurfaces)
            .InvokeAsync(args, stdout, stderr, color: false).GetAwaiter().GetResult();

        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("env", "list")]
    [InlineData("agent", "list")]
    public void TheRingIsNotAGlobalOption(string group, string verb)
    {
        var (exitCode, _, _) = Run(group, verb, "--cloud", "test", "--non-interactive");

        Assert.Equal(2, exitCode);
    }

    [Theory]
    [InlineData("login")]
    [InlineData("switch")]
    public void TheAuthCommandsThatSetTheRingAdvertiseIt(string verb)
    {
        var (_, stdout, _) = Run("auth", verb, "--help");

        Assert.Contains("--cloud", stdout, StringComparison.Ordinal);

        foreach (var token in PowerPlatformApiRingSelection.AcceptedTokens)
        {
            Assert.Contains(token, stdout, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("login")]
    [InlineData("switch")]
    public void ARingTheHostDoesNotServeIsRefused(string verb)
    {
        var (exitCode, _, stderr) = Run("auth", verb, "--cloud", "dev", "--non-interactive");

        Assert.Equal(2, exitCode);
        Assert.Contains("dev", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchWithNeitherSelectorIsRefused()
    {
        var (exitCode, _, stderr) = Run("auth", "switch", "--non-interactive");

        Assert.Equal(2, exitCode);
        Assert.Contains("--cloud", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NoEnvironmentVariableSelectsTheRing()
    {
        foreach (var path in new[] { new[] { "--help" }, ["auth", "login", "--help"], ["env", "list", "--help"] })
        {
            var (_, stdout, _) = Run(path);

            Assert.DoesNotContain("AH_CLI_CLOUD_TYPE", stdout, StringComparison.Ordinal);
        }
    }
}
