using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

[Collection(AmsMcsTestKit.Collection)]
public sealed class AmsMcsProbeTests : IDisposable
{
    private const string RunId = "20261108-050000-ab12";

    private static readonly string[] EndpointProbes = ["GET https://data-plane.example.invalid/", "GET https://on-behalf-of.example.invalid/", "GET https://control-plane.example.invalid/", $"GET https://{AmsMcsTestKit.GitHost}/"];

    private readonly ScratchDirectory _scratch = new();
    private readonly OracleStub _oracle = new();
    private readonly FakeMsagent _msagent = new();

    public void Dispose() => _scratch.Dispose();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NoWorkspaceRootIsASkipThatNamesTheKey(string? workspaceRoot)
    {
        var services = Services(AmsMcsTestKit.Config(workspaceRoot));

        var capabilities = await ProbeAsync(services);

        Assert.Null(services.WorkspaceRoot);
        Assert.False(capabilities.Has(Capability.AmsMcsWorkspaceRoot));
        Assert.Equal("no amsMcs.workspaceRoot supplied", capabilities.Reason(Capability.AmsMcsWorkspaceRoot));
    }

    [Fact]
    public async Task TheProjectsGoInTheRunFolderUnderTheExpandedRoot()
    {
        using var variables = new VariableScope("MSAGENT_E2E_TEST_ROOT");
        Environment.SetEnvironmentVariable("MSAGENT_E2E_TEST_ROOT", _scratch.Path);
        var services = Services(AmsMcsTestKit.Config(Path.Combine("%MSAGENT_E2E_TEST_ROOT%", "projects")));

        var capabilities = await ProbeAsync(services);

        Assert.Equal(Path.Combine(_scratch.Path, "projects", RunId), services.WorkspaceRoot);
        Assert.True(capabilities.Has(Capability.AmsMcsWorkspaceRoot));
        Assert.Equal(services.WorkspaceRoot, capabilities.Reason(Capability.AmsMcsWorkspaceRoot));
    }

    [Fact]
    public async Task AnEmptyRunFolderIsAccepted()
    {
        _scratch.Folder(Path.Combine("projects", RunId));

        Assert.True((await ProbeAsync(Services(AmsMcsTestKit.Config(Path.Combine(_scratch.Path, "projects"))))).Has(Capability.AmsMcsWorkspaceRoot));
    }

    [Fact]
    public async Task ARunFolderThatIsNotEmptyIsRefused()
    {
        _scratch.Place(Path.Combine("projects", RunId, "leftover.txt"), "x");

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config(Path.Combine(_scratch.Path, "projects"))));

        Assert.False(capabilities.Has(Capability.AmsMcsWorkspaceRoot));
        Assert.Equal($"{Path.Combine(_scratch.Path, "projects", RunId)} isn't empty", capabilities.Reason(Capability.AmsMcsWorkspaceRoot));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("inner", true)]
    [InlineData("inner", false)]
    public async Task ARootInsideAGitWorkingTreeIsRefused(string below, bool gitFolder)
    {
        _ = gitFolder ? _scratch.Folder(".git") : _scratch.Place(".git", "gitdir: elsewhere");
        var root = Path.Combine(_scratch.Path, below);

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config(root)));

        Assert.False(capabilities.Has(Capability.AmsMcsWorkspaceRoot));
        Assert.Equal($"amsMcs.workspaceRoot {Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)} is inside the git working tree {_scratch.Path}; AMS mode refuses a project inside another repository", capabilities.Reason(Capability.AmsMcsWorkspaceRoot));
    }

    [Fact]
    public void TheWorkingTreeIsTheNearestFolderHoldingGit()
    {
        _scratch.Folder(Path.Combine("outer", ".git"));
        _scratch.Folder(Path.Combine("outer", "inner", ".git"));

        Assert.Equal(Path.Combine(_scratch.Path, "outer", "inner"), AmsMcsServices.WorkingTreeHolding(Path.Combine(_scratch.Path, "outer", "inner", "missing", "deeper")));
        Assert.Null(AmsMcsServices.WorkingTreeHolding(_scratch.Path));
    }

    [Fact]
    public async Task GitWithAnIdentityIsAvailable()
    {
        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config()));

        Assert.True(capabilities.Has(Capability.Git));
        Assert.StartsWith("git version ", capabilities.Reason(Capability.Git), StringComparison.Ordinal);
        Assert.EndsWith(", with user.name and user.email", capabilities.Reason(Capability.Git), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "user.name")]
    [InlineData("[user]\n\tname = E2E Harness\n", "user.email")]
    public async Task GitWithoutAnIdentityIsASkipThatSaysWhatToSet(string globalConfig, string missing)
    {
        var capabilities = await ProbeAsync(new AmsMcsServices(AmsMcsTestKit.Config(), RunId, Msagent(), Transcript(), new FakeTokens()) { Git = AmsMcsTestKit.Git(_scratch, Transcript(), globalConfig) });

        Assert.False(capabilities.Has(Capability.Git));
        Assert.Equal($"git has no {missing}: run git config --global {missing} <value>", capabilities.Reason(Capability.Git));
    }

    [Fact]
    public async Task AMissingGitIsASkip()
    {
        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config(), executable: Path.Combine(_scratch.Path, "no-such-git")));

        Assert.False(capabilities.Has(Capability.Git));
        Assert.StartsWith("git isn't usable from PATH: ", capabilities.Reason(Capability.Git), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGitThatFailsIsASkip()
    {
        var failing = AmsMcsTestKit.Script(_scratch.Folder("bin"), "git", "echo broken 1>&2\r\nexit /b 3\r\n", "echo broken >&2\nexit 3\n");

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config(), executable: failing));

        Assert.False(capabilities.Has(Capability.Git));
        Assert.Equal($"git isn't usable from PATH: git --version exited 3 in {Environment.CurrentDirectory}: broken", capabilities.Reason(Capability.Git));
    }

    [Fact]
    public async Task TheOracleIsNotProbedWhenNoAmsScenarioIsScheduled()
    {
        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config()), new Scenario[] { AmsMcsTestKit.AmsScenario() with { Feature = "mcs-agent" } });

        Assert.False(capabilities.Has(Capability.AmsMcsOracle));
        Assert.Equal("not probed: no mcs-ams scenario is scheduled", capabilities.Reason(Capability.AmsMcsOracle));
        Assert.Empty(_oracle.Calls);
    }

    [Fact]
    public async Task TheOracleAndTheConnectionNeedTheRing()
    {
        var config = AmsMcsTestKit.Config() with { DevEnvironmentId = null };

        var capabilities = await ProbeAsync(Services(config), [AmsMcsTestKit.AmsScenario(Capability.AmsMcsConnection)], config);

        Assert.Equal("needs amsMcs.ring: no amsMcs.devEnvironmentId supplied", capabilities.Reason(Capability.AmsMcsOracle));
        Assert.Equal("needs amsMcs.ring: no amsMcs.devEnvironmentId supplied", capabilities.Reason(Capability.AmsMcsConnection));
        Assert.Equal(EndpointProbes, _oracle.Calls.Select(call => call.Target));
    }

    [Fact]
    public async Task TheOracleReadsEachOrganizationFromConfigWithoutTheBapLookup()
    {
        _oracle.Answer(HttpMethod.Get, "/api/data/v9.2/WhoAmI", HttpStatusCode.OK, "{}");
        var tokens = new FakeTokens();

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config(), tokens), [AmsMcsTestKit.AmsScenario()]);

        Assert.True(capabilities.Has(Capability.AmsMcsOracle));
        Assert.Equal("Dev, Test and Prod organizations, through az", capabilities.Reason(Capability.AmsMcsOracle));
        Assert.Equal(["dev.example.invalid", "test.example.invalid", "prod.example.invalid"], _oracle.Calls.Skip(EndpointProbes.Length).Select(call => call.Uri.Host));
        Assert.Equal(["https://dev.example.invalid", "https://test.example.invalid", "https://prod.example.invalid"], tokens.Resources);
    }

    [Fact]
    public async Task AnOrganizationThatRefusesAReadMakesTheOracleUnavailable()
    {
        _oracle.Answer(HttpMethod.Get, "dev.example.invalid/api/data/v9.2/WhoAmI", HttpStatusCode.OK, "{}").Answer(HttpMethod.Get, "test.example.invalid/api/data/v9.2/WhoAmI", HttpStatusCode.Unauthorized);

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config()), [AmsMcsTestKit.AmsScenario()]);

        Assert.False(capabilities.Has(Capability.AmsMcsOracle));
        Assert.Equal("https://test.example.invalid refused a read; run az login for this tenant", capabilities.Reason(Capability.AmsMcsOracle));
    }

    [Fact]
    public async Task ATokenFailureMakesTheOracleUnavailableWithItsReason()
    {
        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config(), new FakeTokens { Failure = new E2EException("az could not sign in:\nAADSTS50076") }), [AmsMcsTestKit.AmsScenario()]);

        Assert.False(capabilities.Has(Capability.AmsMcsOracle));
        Assert.Equal("az could not sign in: AADSTS50076", capabilities.Reason(Capability.AmsMcsOracle));
    }

    [Fact]
    public async Task ATimedOutReadMakesTheOracleUnavailable()
    {
        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config(), new FakeTokens { Failure = new TaskCanceledException("the read timed out") }), [AmsMcsTestKit.AmsScenario()]);

        Assert.Equal("the read timed out", capabilities.Reason(Capability.AmsMcsOracle));
    }

    [Fact]
    public async Task ACancelledProbeIsNotTakenForAnUnavailableOracle()
    {
        using var cancellation = new CancellationTokenSource();
        var services = Services(AmsMcsTestKit.Config(), new FakeTokens { OnToken = cancellation.Cancel, Failure = new OperationCanceledException(cancellation.Token) });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.ProbeAsync(Resolve(AmsMcsTestKit.Config()), [AmsMcsTestKit.AmsScenario()], cancellation.Token));
    }

    [Fact]
    public async Task TheConnectionIsNotProbedUnlessAScheduledScenarioNeedsIt()
    {
        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config()), [AmsMcsTestKit.AmsScenario(Capability.AmsMcs)]);

        Assert.False(capabilities.Has(Capability.AmsMcsConnection));
        Assert.Equal("not probed: no scheduled scenario needs it", capabilities.Reason(Capability.AmsMcsConnection));
        Assert.DoesNotContain(_oracle.Calls, call => call.Target.Contains("connectivity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnMsnWeatherConnectionTheUserCanUseMakesTheConnectionAvailable()
    {
        var token = FakeTokens.Jwt("harness@example.invalid");
        _msagent.Answer("auth git-credential get", FakeTokens.Credential(token));
        _oracle.Answer(HttpMethod.Get, "/connectivity/connections", HttpStatusCode.OK, Connections("harness@example.invalid", "shared_msnweather"));
        var tokens = new FakeTokens();

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config(), tokens), [AmsMcsTestKit.AmsScenario(Capability.AmsMcsConnection)]);

        var call = Assert.Single(_oracle.Calls, call => call.Target.Contains("connectivity", StringComparison.Ordinal));
        Assert.True(capabilities.Has(Capability.AmsMcsConnection));
        Assert.Equal($"an MSN Weather connection in {AmsMcsTestKit.Test}", capabilities.Reason(Capability.AmsMcsConnection));
        Assert.Equal(PowerPlatformApiDiscovery.GetEnvironmentEndpoint(PowerPlatformApiTargetConfig.Test, AmsMcsTestKit.Test).Host, call.Uri.Host);
        Assert.Equal($"Bearer {token}", call.Authorization!.ToString());
        Assert.Single(_msagent.CallsTo("auth git-credential get"));
        Assert.DoesNotContain(PowerPlatformApiTargetConfig.Test.Audience, tokens.Resources);
    }

    [Fact]
    public async Task NoUsableMsnWeatherConnectionGivesTheLinkToMakeOne()
    {
        _msagent.Answer("auth git-credential get", FakeTokens.Credential(FakeTokens.Jwt("harness@example.invalid")));
        _oracle.Answer(HttpMethod.Get, "/connectivity/connections", HttpStatusCode.OK, Connections("someone-else@example.invalid", "shared_msnweather"));

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config()), [AmsMcsTestKit.AmsScenario(Capability.AmsMcsConnection)]);

        Assert.False(capabilities.Has(Capability.AmsMcsConnection));
        Assert.Equal($"Test holds no MSN Weather connection the signed-in user can use; make one at https://make.test.powerapps.com/environments/{AmsMcsTestKit.Test}/connections/available?apiName=shared_msnweather", capabilities.Reason(Capability.AmsMcsConnection));
    }

    [Fact]
    public async Task AConnectionListingErrorGivesTheLinkToo()
    {
        _msagent.Answer("auth git-credential get", FakeTokens.Credential(FakeTokens.Jwt("harness@example.invalid")));
        _oracle.Answer(HttpMethod.Get, "/connectivity/connections", HttpStatusCode.InternalServerError);

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config()), [AmsMcsTestKit.AmsScenario(Capability.AmsMcsConnection)]);

        Assert.Equal($"The Power Platform API refused the connection listing: 500.; check or make the MSN Weather connection at https://make.test.powerapps.com/environments/{AmsMcsTestKit.Test}/connections/available?apiName=shared_msnweather", capabilities.Reason(Capability.AmsMcsConnection));
    }

    [Fact]
    public async Task NoTokenFromMsagentGivesItsReasonAndTheLink()
    {
        _msagent.Answer("auth git-credential get", "username=msagent\n");

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config()), [AmsMcsTestKit.AmsScenario(Capability.AmsMcsConnection)]);

        Assert.False(capabilities.Has(Capability.AmsMcsConnection));
        Assert.Equal($"msagent auth git-credential get gave no token; check or make the MSN Weather connection at https://make.test.powerapps.com/environments/{AmsMcsTestKit.Test}/connections/available?apiName=shared_msnweather", capabilities.Reason(Capability.AmsMcsConnection));
        Assert.DoesNotContain(_oracle.Calls, call => call.Target.Contains("connectivity", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"unique_name":"harness@example.invalid"}""", true)]
    [InlineData("""{"upn":"harness@example.invalid","unique_name":"someone-else@example.invalid"}""", true)]
    [InlineData("""{"upn":"someone-else@example.invalid","unique_name":"harness@example.invalid"}""", false)]
    public async Task TheSignedInUserIsTheTokensUpnOrElseItsUniqueName(string payload, bool usable)
    {
        _oracle.Answer(HttpMethod.Get, "/connectivity/connections", HttpStatusCode.OK, Connections("harness@example.invalid", "shared_msnweather"));

        var connectors = await new ConnectionProbe(new FakeTokens(FakeTokens.JwtOf(payload)), Transcript(), client: new HttpMessageInvoker(_oracle)).UsableConnectorsAsync(AmsMcsTestKit.Dev, CancellationToken.None);

        Assert.Equal(usable, connectors.Contains("shared_msnweather"));
    }

    [Theory]
    [InlineData("opaque-token", "The Power Platform API token is not a JWT.")]
    [InlineData("""{"oid":"00000000-0000-0000-0000-000000000001"}""", "The Power Platform API token names no signed-in user.")]
    [InlineData("""{"upn":42}""", "The Power Platform API token names no signed-in user.")]
    public async Task ATokenThatNamesNoUserStopsTheListing(string token, string message)
    {
        var failure = await Assert.ThrowsAsync<E2EException>(() => new ConnectionProbe(new FakeTokens(token.StartsWith('{') ? FakeTokens.JwtOf(token) : token), Transcript(), client: new HttpMessageInvoker(_oracle)).UsableConnectorsAsync(AmsMcsTestKit.Dev, CancellationToken.None));

        Assert.Equal(message, failure.Message);
        Assert.Empty(_oracle.Calls);
    }

    [Fact]
    public async Task TheConnectionProbeStaysOnThePublicRingWhenGivenNoTarget()
    {
        _oracle.Answer(HttpMethod.Get, "/connectivity/connections", HttpStatusCode.OK, Connections("harness@example.invalid", "shared_office365"));
        var tokens = new FakeTokens(FakeTokens.Jwt("harness@example.invalid"));

        var usable = await new ConnectionProbe(tokens, Transcript(), client: new HttpMessageInvoker(_oracle)).UsableConnectorsAsync(AmsMcsTestKit.Dev, CancellationToken.None);

        Assert.Equal(["shared_office365"], usable);
        Assert.Equal(PowerPlatformApiDiscovery.GetEnvironmentEndpoint(PowerPlatformApiTargetConfig.Public, AmsMcsTestKit.Dev).Host, Assert.Single(_oracle.Calls).Uri.Host);
        Assert.Equal([PowerPlatformApiTargetConfig.Public.Audience], tokens.Resources);
    }

    [Fact]
    public async Task TheEndpointsAreNotProbedWhenNoAmsScenarioIsScheduled()
    {
        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config()));

        Assert.False(capabilities.Has(Capability.AmsMcsEndpoints));
        Assert.Equal("not probed: no mcs-ams scenario is scheduled", capabilities.Reason(Capability.AmsMcsEndpoints));
        Assert.Empty(_oracle.Calls);
    }

    [Fact]
    public async Task TheEndpointsNeedAmsMcs()
    {
        var config = AmsMcsTestKit.Config() with { Features = "EvaluationFeatures" };

        var capabilities = await ProbeAsync(Services(config), [AmsMcsTestKit.AmsScenario()]);

        Assert.False(capabilities.Has(Capability.AmsMcsEndpoints));
        Assert.Equal("needs amsMcs: amsMcs.features 'EvaluationFeatures' doesn't hold AmsMcsAgents", capabilities.Reason(Capability.AmsMcsEndpoints));
        Assert.DoesNotContain(_oracle.Calls, call => EndpointProbes.Contains(call.Target));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnyHttpAnswerFromEachEndpointCountsAsReachable(HttpStatusCode status)
    {
        foreach (var probe in EndpointProbes)
        {
            _oracle.Answer(HttpMethod.Get, probe[4..], status);
        }

        var capabilities = await ProbeAsync(Services(AmsMcsTestKit.Config()), [AmsMcsTestKit.AmsScenario()]);

        Assert.True(capabilities.Has(Capability.AmsMcsEndpoints));
        Assert.Equal("the data plane, the on-behalf-of gateway, the control plane and GRS's git host answer", capabilities.Reason(Capability.AmsMcsEndpoints));
        Assert.Equal(EndpointProbes, _oracle.Calls.Take(EndpointProbes.Length).Select(call => call.Target));
        Assert.All(_oracle.Calls.Take(EndpointProbes.Length), call => Assert.Null(call.Authorization));
        Assert.Contains($"# reach GET https://{AmsMcsTestKit.GitHost}/ -> {(int)status}", File.ReadAllText(Path.Combine(_scratch.Path, "run", "transcript.log")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data-plane.example.invalid", "the data plane https://data-plane.example.invalid", 1)]
    [InlineData("on-behalf-of.example.invalid", "the on-behalf-of gateway https://on-behalf-of.example.invalid", 2)]
    [InlineData("control-plane.example.invalid", "the control plane https://control-plane.example.invalid", 3)]
    [InlineData(AmsMcsTestKit.GitHost, $"GRS's git host https://{AmsMcsTestKit.GitHost}/", 4)]
    public async Task AnEndpointThatCannotBeReachedStopsTheRunBeforeTheRingGuard(string host, string endpoint, int probed)
    {
        _oracle.Fail(host, new HttpRequestException("No such host is known.\n(port 443)"));
        var services = Services(AmsMcsTestKit.Config());

        var capabilities = await ProbeAsync(services, [AmsMcsTestKit.AmsScenario()]);
        var blocked = await Assert.ThrowsAsync<ScenarioBlockedException>(() => services.EnsureRingAsync(Ledger.Create(_scratch.Folder("ledger"), RunId), "AM-02", CancellationToken.None));

        Assert.False(capabilities.Has(Capability.AmsMcsEndpoints));
        Assert.Equal($"{endpoint} isn't reachable: No such host is known. (port 443)", capabilities.Reason(Capability.AmsMcsEndpoints));
        Assert.Equal(EndpointProbes.Take(probed), _oracle.Calls.Take(probed).Select(call => call.Target));
        Assert.DoesNotContain(_oracle.Calls.Skip(probed), call => EndpointProbes.Contains(call.Target));
        Assert.Equal($"The AMS deployment isn't reachable, so the run changes nothing there: {endpoint} isn't reachable: No such host is known. (port 443).", blocked.Message);
        Assert.Equal("the AMS deployment", blocked.Cause);
        Assert.Null(blocked.BlocksCapability);
        Assert.Empty(_msagent.Calls);
    }

    [Fact]
    public async Task AnEndpointThatDoesNotAnswerInTimeIsUnreachable()
    {
        _oracle.Silence("on-behalf-of.example.invalid");
        var services = new AmsMcsServices(AmsMcsTestKit.Config(), RunId, Msagent(), Transcript(), new FakeTokens(), new HttpMessageInvoker(_oracle)) { Git = AmsMcsTestKit.Git(_scratch, Transcript()), EndpointTimeout = TimeSpan.FromMilliseconds(50) };

        var capabilities = await ProbeAsync(services, [AmsMcsTestKit.AmsScenario()]);

        Assert.Equal("the on-behalf-of gateway https://on-behalf-of.example.invalid didn't answer within 0s", capabilities.Reason(Capability.AmsMcsEndpoints));
        Assert.Equal(TimeSpan.FromSeconds(30), Services(AmsMcsTestKit.Config()).EndpointTimeout);
    }

    [Fact]
    public async Task ACancelledEndpointProbeIsNotTakenForAnUnreachableEndpointAndStopsNothing()
    {
        using var cancellation = new CancellationTokenSource();
        _oracle.Silence("data-plane.example.invalid");
        _oracle.OnCall = cancellation.Cancel;
        _msagent.Answer("env list", AmsMcsTestKit.EnvironmentList(AmsMcsTestKit.Dev, AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod));
        var services = Services(AmsMcsTestKit.Config());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => services.ProbeAsync(Resolve(AmsMcsTestKit.Config()), [AmsMcsTestKit.AmsScenario()], cancellation.Token));

        Assert.Equal(AmsMcsTestKit.Dev, await services.EnsureRingAsync(Ledger.Create(_scratch.Folder("ledger"), RunId), "AM-02", CancellationToken.None));
    }

    [Fact]
    public async Task TheReachUsesTheSharedClientWhenGivenNone()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OracleHttp.ReachAsync(new Uri("https://data-plane.example.invalid/"), TimeSpan.FromSeconds(5), Transcript(), cancellation.Token));
    }

    [Fact]
    public async Task WithoutOrganizationsTheOracleKeepsTheBapLookupOnTheSharedClient()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var tokens = new FakeTokens();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DataverseProbe(tokens, Transcript()).InstanceAsync(AmsMcsTestKit.Dev, cancellation.Token));

        Assert.Equal(["https://service.powerapps.com/"], tokens.Resources);
        Assert.Empty(_oracle.Calls);
    }

    private static string Connections(string createdBy, string connector) => $$$$"""{"value":[{"properties":{"apiId":"/providers/Microsoft.PowerApps/apis/{{{{connector}}}}","statuses":[{"status":"Connected"}],"createdBy":{"userPrincipalName":"{{{{createdBy}}}}"}}}]}""";

    private static Capabilities Resolve(AmsMcsConfig config) => Capabilities.Resolve(new E2EConfig { AmsMcs = config }, Path.GetTempPath(), signedIn: true, "signed in", azureCliPresent: false);

    private static Task<Capabilities> ProbeAsync(AmsMcsServices services, IReadOnlyList<Scenario>? scheduled = null, AmsMcsConfig? config = null) => services.ProbeAsync(Resolve(config ?? services.Config), scheduled ?? [], CancellationToken.None);

    private Transcript Transcript() => new(_scratch.Folder("run"));

    private MsagentCli Msagent() => _msagent.Cli(Transcript(), _scratch.Path);

    private AmsMcsServices Services(AmsMcsConfig config, IAccessTokens? tokens = null, string executable = "git") => new(config, RunId, Msagent(), Transcript(), tokens ?? new FakeTokens(), new HttpMessageInvoker(_oracle)) { Git = AmsMcsTestKit.Git(_scratch, Transcript(), executable: executable) };
}
