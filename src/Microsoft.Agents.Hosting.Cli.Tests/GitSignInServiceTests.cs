using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class GitSignInServiceTests
{
    private const string EnvironmentHosts = "https://*.*.environment.api.test.powerplatform.com";
    private const string EnvironmentHost = "f4a0925eacdfec858a82dee7ee3a47a.d.environment.api.test.powerplatform.com";
    private const string DevClusterHost = "pp-grs-nguhoa.wus-il100.gateway.test.island.powerapps.com";
    private const string HelperWithoutMsagentOnPath = "!AH_CLI_FEATURES=AmsMcsAgents msagent auth git-credential";
    private const string DevClusterHelperWithoutMsagentOnPath = $"!AH_CLI_FEATURES=AmsMcsAgents MSAGENT_GIT_HOST={DevClusterHost} msagent auth git-credential";

    private static readonly string GitDirectory = Path.Combine(Path.GetTempPath(), "git-sign-in-tests", "cmd");

    private readonly Dictionary<string, List<string>> _globalConfig = new(StringComparer.Ordinal);
    private readonly RecordingTokenProvider _tokens = new() { AccessToken = "ppapi-token" };
    private readonly PlaygroundToolLocatorTests.RecordingProcessRunner _git;

    public GitSignInServiceTests() => _git = new PlaygroundToolLocatorTests.RecordingProcessRunner(WriteGlobalConfig);

    private static string GitPath => Path.GetFullPath(Path.Combine(GitDirectory, "git.exe"));

    [Fact]
    public async Task SetupGit_PointsTheRingsEnvironmentHostsAtMsagent_AfterAnEmptyHelperThatDropsOtherHelpers()
    {
        var result = await Service().HandleAsync(new AuthSetupGitRequest(), default);

        Assert.Equal([EnvironmentHosts], result.Hosts);
        Assert.Equal(["Configured git to sign in with msagent.", $"  hosts: {EnvironmentHosts}"], result.ToTextLines());
        Assert.Equal(
            [
                ["config", "--global", "--replace-all", $"credential.{EnvironmentHosts}.helper", string.Empty],
                ["config", "--global", "--add", $"credential.{EnvironmentHosts}.helper", HelperWithoutMsagentOnPath],
            ],
            _git.Requests.Select(request => request.Arguments.ToArray()));
        Assert.All(_git.Requests, request => Assert.Equal(GitPath, request.ExecutablePath));
        Assert.Empty(_tokens.RequestedResources);
    }

    [Fact]
    public async Task SetupGit_ReplacesTheHelpersAlreadyConfiguredForTheHost_AndARerunChangesNothing()
    {
        _globalConfig[$"credential.{EnvironmentHosts}.helper"] = ["manager", "!old-helper"];
        var service = Service();

        await service.HandleAsync(new AuthSetupGitRequest(), default);
        string[] configured = [.. _globalConfig[$"credential.{EnvironmentHosts}.helper"]];
        await service.HandleAsync(new AuthSetupGitRequest(), default);

        Assert.Equal([string.Empty, HelperWithoutMsagentOnPath], configured);
        Assert.Equal(configured, _globalConfig[$"credential.{EnvironmentHosts}.helper"]);
        Assert.Single(_globalConfig);
    }

    [Fact]
    public async Task SetupGit_WithTheGitHostOfAPersonalDevCluster_PointsThatExactHostAtMsagentToo()
    {
        var result = await Service(new AmsEndpointProfile { GitHost = DevClusterHost }).HandleAsync(new AuthSetupGitRequest(), default);

        Assert.Equal([EnvironmentHosts, $"https://{DevClusterHost}"], result.Hosts);
        Assert.Equal($"  hosts: {EnvironmentHosts}, https://{DevClusterHost}", result.ToTextLines()[1]);
        Assert.Equal([string.Empty, DevClusterHelperWithoutMsagentOnPath], _globalConfig[$"credential.{EnvironmentHosts}.helper"]);
        Assert.Equal([string.Empty, DevClusterHelperWithoutMsagentOnPath], _globalConfig[$"credential.https://{DevClusterHost}.helper"]);
        Assert.Equal(4, _git.Requests.Count);
    }

    [Theory]
    [InlineData("msagent.cmd")]
    [InlineData("msagent.exe")]
    public async Task SetupGit_WithMsagentOnPath_NamesItsFullPathForEveryHost_SingleQuotedForGitsShell(string launcher)
    {
        var toolsDirectory = Path.Combine(Path.GetTempPath(), "git-sign-in-tests", "Mary O'Neil", ".dotnet", "tools");
        var environment = new PlaygroundToolLocatorTests.FakePlaygroundEnvironment(isWindows: true, pathValue: $"{GitDirectory};{toolsDirectory}", existingPaths: [GitPath, Path.Combine(toolsDirectory, launcher)]);

        await Service(new AmsEndpointProfile { GitHost = DevClusterHost }, environment).HandleAsync(new AuthSetupGitRequest(), default);

        var helper = $"!AH_CLI_FEATURES=AmsMcsAgents MSAGENT_GIT_HOST={DevClusterHost} '{Path.Combine(Path.GetTempPath(), "git-sign-in-tests", @"Mary O'\''Neil", ".dotnet", "tools", launcher)}' auth git-credential";
        Assert.Equal([string.Empty, helper], _globalConfig[$"credential.{EnvironmentHosts}.helper"]);
        Assert.Equal([string.Empty, helper], _globalConfig[$"credential.https://{DevClusterHost}.helper"]);
    }

    [Theory]
    [InlineData("https://pp-grs-nguhoa.wus-il100.gateway.test.island.powerapps.com")]
    [InlineData("pp-grs-nguhoa.wus-il100.gateway.test.island.powerapps.com/appframework")]
    [InlineData("pp-grs-nguhoa.wus-il100.gateway.test.island.powerapps.com:443")]
    public async Task SetupGit_WithAGitHostThatIsNotAHostName_IsRefusedBeforeGitRuns(string gitHost)
    {
        var exception = await Assert.ThrowsAsync<AmsAgentException>(() => Service(new AmsEndpointProfile { GitHost = gitHost }).HandleAsync(new AuthSetupGitRequest(), default));

        Assert.Equal((ErrorCodes.AmsAgent.EndpointOverrideNotUsable, $"MSAGENT_GIT_HOST: '{gitHost}' is not a host name."), (exception.ErrorCode, exception.Message));
        Assert.Equal("Set it to the host alone, without a scheme, port or path.", exception.Remediation);
        Assert.Empty(_git.Requests);
    }

    [Fact]
    public async Task SetupGit_WithoutGitOnPath_SaysToInstallGit_AndRunsNothing()
    {
        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => Service(environment: PathWithGit(existingPaths: [])).HandleAsync(new AuthSetupGitRequest(), default));

        Assert.Equal((ErrorCodes.Authentication.GitNotFound, ExitCodes.Failure), (exception.ErrorCode, exception.ExitCode));
        Assert.Equal("git was not found on PATH, so it cannot sign in with msagent.", exception.Message);
        Assert.Equal("Install git from https://git-scm.com/downloads, open a new terminal, then run `msagent auth setup-git` again.", exception.Remediation);
        Assert.Empty(_git.Requests);
    }

    [Theory]
    [InlineData(255, "error: could not lock config file C:/Users/maker/.gitconfig: Permission denied\u001b[0m\nhint: close the editor\n", "git could not write msagent's credential helper into your global git config. error: could not lock config file C:/Users/maker/.gitconfig: Permission denied\uFFFD[0m hint: close the editor")]
    [InlineData(1, "", "git could not write msagent's credential helper into your global git config.")]
    public async Task SetupGit_WhenGitRefusesTheConfig_StopsWithGitsReasonOnOneLine(int exitCode, string standardError, string message)
    {
        var git = new PlaygroundToolLocatorTests.RecordingProcessRunner(_ => new PlaygroundProcessResult(exitCode, string.Empty, standardError));

        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => Service(runner: git).HandleAsync(new AuthSetupGitRequest(), default));

        Assert.Equal((ErrorCodes.Authentication.GitConfigurationFailed, ExitCodes.Failure, message), (exception.ErrorCode, exception.ExitCode, exception.Message));
        Assert.Equal("Fix the problem git reports, then run `msagent auth setup-git` again.", exception.Remediation);
        Assert.Single(git.Requests);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException), "The requested process could not be started.")]
    [InlineData(typeof(TimeoutException), "The process exceeded the configured timeout.")]
    public async Task SetupGit_WhenGitCannotRun_StopsWithTheRunnersReason(Type failureType, string reason)
    {
        var git = new PlaygroundToolLocatorTests.RecordingProcessRunner(_ => throw (Exception)Activator.CreateInstance(failureType, reason)!);

        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => Service(runner: git).HandleAsync(new AuthSetupGitRequest(), default));

        Assert.Equal((ErrorCodes.Authentication.GitConfigurationFailed, $"git could not write msagent's credential helper into your global git config. {reason}"), (exception.ErrorCode, exception.Message));
    }

    [Theory]
    [InlineData($"protocol=https\nhost={EnvironmentHost}\n\n")]
    [InlineData("protocol=https\r\nhost=F4A0925EACDFEC858A82DEE7EE3A47A.D.ENVIRONMENT.API.TEST.POWERPLATFORM.COM\r\npath=appframework/git/repositories/e4596b3c\r\n")]
    [InlineData($"host={DevClusterHost}\nprotocol=https\n\nprotocol=http\n")]
    public async Task GitCredential_GetForATrustedHostOverHttps_AnswersExactlyTheUserAndTheRingsToken(string request)
    {
        var result = await Service(new AmsEndpointProfile { GitHost = DevClusterHost }, input: request).HandleAsync(new AuthGitCredentialRequest { Operation = "get" }, default);

        Assert.Equal(["username=msagent", "password=ppapi-token"], result.ToTextLines());
        Assert.Equal([PowerPlatformApiTargetConfig.Test.Audience], _tokens.RequestedResources);
        Assert.Equal([null], _tokens.RequestedTargetTenants);
        Assert.Empty(_git.Requests);
    }

    [Theory]
    [InlineData($"protocol=http\nhost={EnvironmentHost}\n\n")]
    [InlineData("protocol=https\nhost=contoso.example.com\n\n")]
    [InlineData("protocol=https\nhost=a.b.c.environment.api.test.powerplatform.com\n\n")]
    [InlineData("protocol=https\nhost=f4a0925eacdfec858a82dee7ee3a47a.d.environment.api.powerplatform.com\n\n")]
    [InlineData($"protocol=https\nhost={DevClusterHost}\n\n")]
    [InlineData($"protocol=https\nhost={EnvironmentHost}:8443\n\n")]
    [InlineData("protocol=https\n\n")]
    [InlineData("")]
    public async Task GitCredential_GetForAnyOtherProtocolOrHost_AnswersNothing_AndAsksForNoToken(string request)
    {
        var result = await Service(input: request).HandleAsync(new AuthGitCredentialRequest { Operation = "get" }, default);

        Assert.Empty(result.ToTextLines());
        Assert.Empty(_tokens.RequestedResources);
    }

    [Theory]
    [InlineData("store")]
    [InlineData("erase")]
    public async Task GitCredential_StoreAndErase_DoNothing(string operation)
    {
        var result = await Service(input: $"protocol=https\nhost={EnvironmentHost}\nusername=msagent\npassword=ppapi-token\n\n").HandleAsync(new AuthGitCredentialRequest { Operation = operation }, default);

        Assert.Empty(result.ToTextLines());
        Assert.Empty(_tokens.RequestedResources);
        Assert.Empty(_git.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitCredential_GetWhileSignedOutOrWhenTheTokenNeedsInteraction_FailsWithExitCodeOne_AndTheSignInHint(bool interactionRequired)
    {
        AuthenticationException signedOut = interactionRequired ? new InteractionRequiredException("The sign-in has expired.") : new NotSignedInException("No account is signed in.", "Run `msagent auth login` to sign in, then retry.");
        var tokens = new FakePpapiTokenProvider { AcquisitionFailure = signedOut };

        var exception = await Assert.ThrowsAsync<AuthenticationException>(() => Service(input: $"protocol=https\nhost={EnvironmentHost}\n\n", tokens: tokens).HandleAsync(new AuthGitCredentialRequest { Operation = "get" }, default));

        Assert.Equal((ExitCodes.Failure, signedOut.ErrorCode, signedOut.Message, signedOut.Remediation), (exception.ExitCode, exception.ErrorCode, exception.Message, exception.Remediation));
        Assert.Contains("msagent auth login", exception.Remediation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GitCredentialCommand_WritesOnlyTheCredentialLinesToStdout_WithLineFeeds(bool json)
    {
        string[] arguments = ["auth", "git-credential", "get", .. json ? new[] { "--json" } : []];

        var (exitCode, stdout, stderr) = await RunAsync([FeatureName.AmsMcsAgents], $"protocol=https\nhost={EnvironmentHost}\n\n", arguments);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("username=msagent\npassword=ppapi-token\n", stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public async Task GitCredentialCommand_WhileSignedOut_WritesNothingToStdout_AndTheHintToStderr()
    {
        var (exitCode, stdout, stderr) = await RunAsync([FeatureName.AmsMcsAgents], $"protocol=https\nhost={EnvironmentHost}\n\n", new FakePpapiTokenProvider { AcquisitionFailure = new NotSignedInException("No account is signed in.", "Run `msagent auth login` to sign in, then retry.") }, "auth", "git-credential", "get");

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Empty(stdout);
        Assert.Contains("msagent auth login", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthHelpAndUsage_ListSetupGit_ButNeverTheHiddenGitCredential()
    {
        var help = await RunAsync([FeatureName.AmsMcsAgents], string.Empty, "auth", "--help");
        var usage = await RunAsync([FeatureName.AmsMcsAgents], string.Empty, "auth");

        Assert.Equal((ExitCodes.Success, ExitCodes.Usage), (help.ExitCode, usage.ExitCode));
        Assert.Contains("setup-git", help.Stdout, StringComparison.Ordinal);
        Assert.Contains("setup-git", usage.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("git-credential", help.Stdout + usage.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new FeatureName[0])]
    [InlineData(new[] { FeatureName.AmsFeatures })]
    public async Task GitCommands_WithoutTheMcsAgentsFeature_DoNotExist(FeatureName[] features)
    {
        var help = await RunAsync(features, string.Empty, "auth", "--help");
        var setup = await RunAsync(features, string.Empty, "auth", "setup-git");
        var credential = await RunAsync(features, $"protocol=https\nhost={EnvironmentHost}\n\n", "auth", "git-credential", "get");

        Assert.DoesNotContain("setup-git", help.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("git-credential", help.Stdout, StringComparison.Ordinal);
        Assert.Equal((ExitCodes.Usage, ExitCodes.Usage), (setup.ExitCode, credential.ExitCode));
        Assert.Empty(credential.Stdout);
        Assert.Empty(_git.Requests);
        Assert.Empty(_tokens.RequestedResources);
    }

    private GitSignInService Service(AmsEndpointProfile? endpoints = null, PlaygroundToolLocatorTests.FakePlaygroundEnvironment? environment = null, PlaygroundToolLocatorTests.RecordingProcessRunner? runner = null, string input = "", ITokenProvider? tokens = null) =>
        new(PowerPlatformApiTargetConfig.Test, endpoints ?? new AmsEndpointProfile(), tokens ?? _tokens, runner ?? _git, environment ?? PathWithGit(), new StringReader(input));

    private static PlaygroundToolLocatorTests.FakePlaygroundEnvironment PathWithGit(IEnumerable<string>? existingPaths = null) =>
        new(isWindows: true, pathValue: GitDirectory, existingPaths: existingPaths ?? [Path.Combine(GitDirectory, "git.exe")]);

    private Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(FeatureName[] features, string input, params string[] args) => RunAsync(features, input, _tokens, args);

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(FeatureName[] features, string input, ITokenProvider tokens, params string[] args)
    {
        var service = Service(input: input, tokens: tokens);
        var operations = new FakeOperations();
        await using var container = new ServiceCollection()
            .AddSingleton<ICommandHandler<AuthSetupGitRequest, AuthSetupGitResult>>(service)
            .AddSingleton<ICommandHandler<AuthGitCredentialRequest, AuthGitCredentialResult>>(service)
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await new AgentHostCli(operations, new CompositeHandlerProvider(new FacadeHandlerProvider(operations), container), new TestFeatureFlags(features)).InvokeAsync(args, stdout, stderr, color: false);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private PlaygroundProcessResult WriteGlobalConfig(PlaygroundProcessRequest request)
    {
        switch (request.Arguments)
        {
            case ["config", "--global", "--replace-all", var key, var value]:
                _globalConfig[key] = [value];
                break;
            case ["config", "--global", "--add", var key, var value]:
                _globalConfig.TryAdd(key, []);
                _globalConfig[key].Add(value);
                break;
            default:
                return new PlaygroundProcessResult(129, string.Empty, "usage: git config [<options>]");
        }

        return new PlaygroundProcessResult(0, string.Empty, string.Empty);
    }
}
