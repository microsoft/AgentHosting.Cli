using System.ComponentModel;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

public sealed class AmsMcsServices
{
    public const string Feature = "mcs-ams";

    public const string Pack = "ams";

    public const string MsnWeather = "shared_msnweather";

    public const string UnreachableEndpoint = "https://no-ams.invalid";

    private const string NoAmsScenario = "no mcs-ams scenario is scheduled";

    private static readonly string[] GitNames = ["git.exe", "git"];

    private static readonly string[] IdentityKeys = ["user.name", "user.email"];

    private readonly SessionCache<string, string> _ring = new();
    private readonly Transcript _transcript;
    private (string Message, string Cause)? _stopped;

    public AmsMcsServices(AmsMcsConfig config, string runId, MsagentCli msagent, Transcript transcript, IAccessTokens tokens, HttpMessageInvoker? client = null)
    {
        Config = config;
        Client = client;
        _transcript = transcript;
        WorkspaceRoot = config.WorkspaceRoot is { Length: > 0 } root ? Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)), runId) : null;
        On = msagent.With(AmsOn(config));
        Off = msagent.With(AmsOff(config));
        NoGit = On.With(EnvironmentOverlay.Empty.Set("PATH", WithoutGit(Environment.GetEnvironmentVariable("PATH"))));
        Unreachable = On.With(EnvironmentOverlay.Empty.Set(AmsEndpointProfile.DataPlaneEndpointVariable, UnreachableEndpoint).Set(AmsEndpointProfile.OnBehalfOfEndpointVariable, UnreachableEndpoint).Set(AmsEndpointProfile.ControlPlaneEndpointVariable, UnreachableEndpoint));
        Git = new GitCli(transcript, On.Overlay);
        Dataverse = new DataverseProbe(tokens, transcript, config.OrganizationsByEnvironment(), client);
        MsagentTokens = new MsagentTokens(On, config.GitHost, PowerPlatformApiTargetConfig.Test.Audience);
        Connections = new ConnectionProbe(MsagentTokens, transcript, PowerPlatformApiTargetConfig.Test, client);
    }

    public AmsMcsConfig Config { get; }

    public HttpMessageInvoker? Client { get; }

    public string? WorkspaceRoot { get; }

    public MsagentCli On { get; }

    public MsagentCli Off { get; }

    public MsagentCli NoGit { get; }

    public MsagentCli Unreachable { get; }

    public GitCli Git { get; init; }

    public DataverseProbe Dataverse { get; }

    public MsagentTokens MsagentTokens { get; }

    public ConnectionProbe Connections { get; }

    public AmsPool Pool { get; } = new();

    public TimeSpan EndpointTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public string ConnectionLink => $"https://make.test.powerapps.com/environments/{Config.TestEnvironmentId}/connections/available?apiName={MsnWeather}";

    public Uri? DevOrganization => Config.OrganizationsByEnvironment().GetValueOrDefault(Config.DevEnvironmentId ?? string.Empty);

    public static TimeSpan CallTimeout { get; } = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<LedgerEntry> ManualCleanup { get; } =
    [
        Example(ResourceKind.AmsAgent, "<agent id>", new CleanupPlan { ProjectDirectory = "<project>" }),
        Example(ResourceKind.AmsFlow, "<flow name>@<environment id>", new CleanupPlan { EnvironmentId = "<environment id>", Correlation = "<flow name>" }),
        Example(ResourceKind.LeftoverBot, "<schema name>", new CleanupPlan { SchemaName = "<schema name>" }),
        Example(ResourceKind.GrsRepository, "<repository id>", new CleanupPlan { GitUrl = "<git url>" }),
        Example(ResourceKind.AmsConnector, "<connector name>@<environment id>", new CleanupPlan { EnvironmentId = "<environment id>", Correlation = "<connector name>" }),
        Example(ResourceKind.AmsConnectionReference, "<logical name>@<environment id>", new CleanupPlan { EnvironmentId = "<environment id>", Correlation = "<logical name>" }),
    ];

    public static EnvironmentOverlay AmsOn(AmsMcsConfig config) => config.Settings.Aggregate(EnvironmentOverlay.Empty.Set(GitCredentialHelper.FeatureVariable, config.FeatureValue), (overlay, setting) => overlay.Set(setting.Variable, setting.Value));

    public static EnvironmentOverlay AmsOff(AmsMcsConfig config) => config.Settings.Aggregate(EnvironmentOverlay.Empty.Set(GitCredentialHelper.FeatureVariable, config.FeaturesWithoutAms), (overlay, setting) => overlay.Remove(setting.Variable));

    public static string WithoutGit(string? path) => string.Join(Path.PathSeparator, (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Where(entry => !GitNames.Any(name => File.Exists(Path.Combine(entry.Trim('"'), name)))));

    public static string? WorkingTreeHolding(string directory)
    {
        for (var candidate = new DirectoryInfo(directory); candidate is not null; candidate = candidate.Parent)
        {
            if (Path.Exists(Path.Combine(candidate.FullName, ".git")))
            {
                return candidate.FullName;
            }
        }

        return null;
    }

    public async Task<Capabilities> ProbeAsync(Capabilities capabilities, IReadOnlyList<Scenario> scheduled, CancellationToken cancellationToken)
    {
        var amsScheduled = scheduled.Any(scenario => string.Equals(scenario.Feature, Feature, StringComparison.OrdinalIgnoreCase));
        var ring = capabilities.Has(Capability.AmsMcsRing) ? null : $"needs amsMcs.ring: {capabilities.Reason(Capability.AmsMcsRing)}";
        var root = ProbeWorkspaceRoot();
        var git = await ProbeGitAsync(cancellationToken).ConfigureAwait(false);
        var endpoints = await ProbeWhenAsync(amsScheduled, NoAmsScenario, capabilities.Has(Capability.AmsMcs) ? null : $"needs amsMcs: {capabilities.Reason(Capability.AmsMcs)}", () => ProbeEndpointsAsync(cancellationToken), string.Empty, cancellationToken).ConfigureAwait(false);
        var oracle = await ProbeWhenAsync(amsScheduled, NoAmsScenario, ring, () => ProbeOracleAsync(cancellationToken), string.Empty, cancellationToken).ConfigureAwait(false);
        var connection = await ProbeWhenAsync(scheduled.Any(scenario => scenario.Requires.Contains(Capability.AmsMcsConnection)), "no scheduled scenario needs it", ring, () => ProbeConnectionAsync(cancellationToken), $"; check or make the MSN Weather connection at {ConnectionLink}", cancellationToken).ConfigureAwait(false);

        return capabilities.With(Capability.AmsMcsWorkspaceRoot, root.Available, root.Detail).With(Capability.Git, git.Available, git.Detail).With(Capability.AmsMcsOracle, oracle.Available, oracle.Detail).With(Capability.AmsMcsConnection, connection.Available, connection.Detail).With(Capability.AmsMcsEndpoints, endpoints.Available, endpoints.Detail);
    }

    public async Task<string> EnsureRingAsync(Ledger ledger, string scenarioId, CancellationToken cancellationToken)
    {
        if (_stopped is { } stopped)
        {
            throw new ScenarioBlockedException(stopped.Message, stopped.Cause);
        }

        try
        {
            return await _ring.GetAsync(Pack, () => SelectDevAsync(ledger, scenarioId, cancellationToken)).ConfigureAwait(false);
        }
        catch (ScenarioBlockedException exception)
        {
            _stopped = (exception.Message, exception.Cause);
            throw;
        }
    }

    public MsagentOutcome Record(Ledger ledger, MsagentResult result, string project, bool amsOff, string scenarioId)
    {
        var outcome = MsagentOutcome.Read(result);

        if (!outcome.Success)
        {
            return outcome;
        }

        if (!string.IsNullOrEmpty(outcome.AgentId))
        {
            ledger.RecordAmsAgent(outcome.AgentId, outcome.DisplayName, project, amsOff, scenarioId);
        }

        if (!string.IsNullOrEmpty(outcome.SchemaName))
        {
            ledger.RecordLeftoverBot(outcome.SchemaName, scenarioId);
        }

        if (outcome.Ams is { RepositoryCreated: true } ams)
        {
            ledger.RecordGrsRepository(ams.RepositoryId!, ams.GitUrl!, Config.DevEnvironmentId!, scenarioId);
        }

        return outcome;
    }

    public Task<MsagentResult> ListAgentsAsync(string environmentId, string? repositoryId, string? agentType, CancellationToken cancellationToken)
    {
        string[] repository = repositoryId is null ? [] : ["--repository-id", repositoryId];
        string[] type = agentType is null ? [] : ["--agent-type", agentType];

        return On.RunAsync(["agent", "list", "--view", "ams", "--environment-id", environmentId, .. repository, .. type, "--limit", "1000", "--json", "--non-interactive"], timeout: CallTimeout, cancellationToken: cancellationToken);
    }

    public async Task<McsAmsOperationShowResult> WaitForOperationAsync(string project, string agentId, string operationId, TimeSpan limit, TimeSpan interval, CancellationToken cancellationToken)
    {
        var check = $"operation {operationId} finishes";
        var deadline = DateTimeOffset.UtcNow + limit;

        while (true)
        {
            var shown = await On.RunAsync(["agent", "operation", "show", "--project", project, "--agent-id", agentId, "--operation-id", operationId, "--json", "--non-interactive"], project, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (shown.ExitCode != 0)
            {
                throw new AssertionException(check, "agent operation show to exit 0", $"exit {shown.ExitCode}", Redaction.Apply(shown.Output.Trim()), shown);
            }

            var operation = shown.Json<McsAmsOperationShowResult>();

            if (string.Equals(operation.Status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return operation;
            }

            if (string.Equals(operation.Status, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new AssertionException(check, "Succeeded", "Failed", $"{operation.ErrorCode}: {operation.ErrorMessage}", shown);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AssertionException(check, $"an end within {limit.TotalSeconds:N0}s", operation.Status, null, shown);
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static LedgerEntry Example(ResourceKind kind, string id, CleanupPlan cleanup) => new() { Sequence = 0, Kind = kind, Id = id, DisplayName = id, CreatedBy = Pack, CreatedAt = DateTimeOffset.MinValue, Cleanup = cleanup };

    private static async Task<(bool Available, string Detail)> ProbeWhenAsync(bool needed, string unneeded, string? prerequisite, Func<Task<(bool Available, string Detail)>> probe, string advice, CancellationToken cancellationToken)
    {
        if (!needed || prerequisite is not null)
        {
            return (false, prerequisite is null ? $"not probed: {unneeded}" : prerequisite);
        }

        try
        {
            return await probe().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return (false, exception.Message.ReplaceLineEndings(" ") + advice);
        }
    }

    private (bool Available, string Detail) ProbeWorkspaceRoot() =>
        WorkspaceRoot is null ? (false, "no amsMcs.workspaceRoot supplied")
        : WorkingTreeHolding(Path.GetDirectoryName(WorkspaceRoot)!) is { } tree ? (false, $"amsMcs.workspaceRoot {Path.GetDirectoryName(WorkspaceRoot)} is inside the git working tree {tree}; AMS mode refuses a project inside another repository")
        : !AmsMcsFiles.IsMissingOrEmpty(WorkspaceRoot) ? (false, $"{WorkspaceRoot} isn't empty")
        : (true, WorkspaceRoot);

    private async Task<(bool Available, string Detail)> ProbeGitAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await Git.RequireAsync(null, ["--version"], cancellationToken).ConfigureAwait(false);

            foreach (var key in IdentityKeys)
            {
                if ((await Git.ConfigAsync(Path.GetTempPath(), key, cancellationToken).ConfigureAwait(false)).Count == 0)
                {
                    return (false, $"git has no {key}: run git config --global {key} <value>");
                }
            }

            return (true, $"{version.StdOut.Trim()}, with user.name and user.email");
        }
        catch (Exception exception) when (exception is Win32Exception or E2EException)
        {
            return (false, $"git isn't usable from PATH: {exception.Message.ReplaceLineEndings(" ")}");
        }
    }

    private async Task<(bool Available, string Detail)> ProbeEndpointsAsync(CancellationToken cancellationToken)
    {
        (string Role, string Endpoint)[] endpoints = [("the data plane", Config.DataPlane.Endpoint!), ("the on-behalf-of gateway", Config.OnBehalfOf.Endpoint!), ("the control plane", Config.ControlPlane.Endpoint!), ("GRS's git host", $"https://{Config.GitHost}/")];

        foreach (var (role, endpoint) in endpoints)
        {
            if (await UnreachableAsync(endpoint, cancellationToken).ConfigureAwait(false) is { } failure)
            {
                var detail = $"{role} {endpoint} {failure}";
                _stopped = ($"The AMS deployment isn't reachable, so the run changes nothing there: {detail}.", "the AMS deployment");
                return (false, detail);
            }
        }

        return (true, "the data plane, the on-behalf-of gateway, the control plane and GRS's git host answer");
    }

    private async Task<string?> UnreachableAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            await OracleHttp.ReachAsync(new Uri(endpoint), EndpointTimeout, _transcript, cancellationToken, Client).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return exception is OperationCanceledException ? $"didn't answer within {EndpointTimeout.TotalSeconds:N0}s" : $"isn't reachable: {exception.Message.ReplaceLineEndings(" ")}";
        }
    }

    private async Task<(bool Available, string Detail)> ProbeOracleAsync(CancellationToken cancellationToken)
    {
        foreach (var environment in Config.Environments)
        {
            if (!await Dataverse.CanReadAsync(environment.Id!, cancellationToken).ConfigureAwait(false))
            {
                return (false, $"{environment.Organization} refused a read; run az login for this tenant");
            }
        }

        return (true, "Dev, Test and Prod organizations, through az");
    }

    private async Task<(bool Available, string Detail)> ProbeConnectionAsync(CancellationToken cancellationToken) =>
        (await Connections.UsableConnectorsAsync(Config.TestEnvironmentId!, cancellationToken).ConfigureAwait(false)).Contains(MsnWeather)
            ? (true, $"an MSN Weather connection in {Config.TestEnvironmentId}")
            : (false, $"Test holds no MSN Weather connection the signed-in user can use; make one at {ConnectionLink}");

    private async Task<string> SelectDevAsync(Ledger ledger, string scenarioId, CancellationToken cancellationToken)
    {
        var listed = await On.RunAsync(["env", "list", "--json", "--non-interactive"], cancellationToken: cancellationToken).ConfigureAwait(false);

        if (listed.ExitCode != 0)
        {
            throw RingBlocked($"msagent env list exited {listed.ExitCode}");
        }

        var environments = listed.Json<EnvironmentListResult>().Environments;
        var active = environments.FirstOrDefault(environment => environment.IsActive)?.EnvironmentId;

        if (Config.Environments.FirstOrDefault(environment => !environments.Any(candidate => AmsIds.Same(candidate.EnvironmentId, environment.Id))) is { } missing)
        {
            throw RingBlocked($"{missing.IdKey} {missing.Id} isn't one msagent env list shows; sign in with msagent auth login --cloud test, with a test-ring account");
        }

        if (active is null)
        {
            throw RingBlocked("no environment is active in msagent");
        }

        if (!Config.Environments.Any(environment => AmsIds.Same(environment.Id, active)))
        {
            throw RingBlocked($"the active environment {active} isn't one of amsMcs's three");
        }

        if (Config.Environments.FirstOrDefault(environment => (Config.ForbiddenEnvironmentIds ?? []).Any(forbidden => AmsIds.Same(forbidden.Trim(), environment.Id))) is { } forbidden)
        {
            throw RingBlocked($"{forbidden.IdKey} {forbidden.Id} is in amsMcs.forbiddenEnvironmentIds");
        }

        var dev = Config.DevEnvironmentId!;

        if (!AmsIds.Same(active, dev))
        {
            ledger.RecordConfigEdit("env-select", active, scenarioId);

            var selected = await On.RunAsync(["env", "select", dev, "--non-interactive"], cancellationToken: cancellationToken).ConfigureAwait(false);

            if (selected.ExitCode != 0)
            {
                throw RingBlocked($"msagent env select {dev} exited {selected.ExitCode}");
            }
        }

        return dev;
    }

    private static ScenarioBlockedException RingBlocked(string reason) => new($"The test-ring guard stopped before changing anything: {reason}.", "the test ring");
}
