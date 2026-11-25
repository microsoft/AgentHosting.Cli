namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>A prerequisite a scenario needs before it can run.</summary>
public enum Capability
{
    Auth,
    McsDev,
    McsTest,
    McsShareUser,
    WorkspaceCli,
    WorkspaceClassic,
    WorkspaceWorkflow,
    AzureRead,
    AzureCreate,
    AzureExistingBot,
    Ams,
    AmsCreate,
    AmsPackage,
    AmsHosted,
    AmsProd,

    StandaloneFixtures,

    StandaloneOracle,

    StandaloneFlowRuns,

    CopyCorpus,
}

/// <summary>How much a scenario can disturb, used for scheduling and for the plan summary.</summary>
public enum SafetyTier
{
    ReadOnly,
    Reversible,
    Creating,
}

/// <summary>Whether a capability resolved, and the reason when it did not.</summary>
public sealed record CapabilityState(Capability Capability, bool Available, string Detail);

/// <summary>Turns the config and the machine's environment into the set of capabilities a run has.</summary>
public sealed class Capabilities
{
    private readonly Dictionary<Capability, CapabilityState> _states;

    private Capabilities(Dictionary<Capability, CapabilityState> states) => _states = states;

    /// <summary>Every capability in declaration order, resolved or not.</summary>
    public IReadOnlyList<CapabilityState> All => [.. Enum.GetValues<Capability>().Select(capability => _states[capability])];

    /// <summary>Whether a capability resolved.</summary>
    public bool Has(Capability capability) => _states[capability].Available;

    /// <summary>The reason a capability is unavailable, for the skip message.</summary>
    public string Reason(Capability capability) => _states[capability].Detail;

    /// <summary>The first unmet capability in the list, or null when all are met.</summary>
    public Capability? FirstMissing(IReadOnlyList<Capability> required) =>
        required.Where(capability => !Has(capability)).Cast<Capability?>().FirstOrDefault();

    /// <summary>Resolves every capability from the config, the fixture paths, and whether <c>az</c> is present.</summary>
    public static Capabilities Resolve(E2EConfig config, string fixturesDirectory, bool signedIn, string signedInDetail, bool azureCliPresent, bool armConsentGranted = true)
    {
        var states = new Dictionary<Capability, CapabilityState>();

        void Add(Capability capability, bool available, string detail) =>
            states[capability] = new CapabilityState(capability, available, detail);

        Add(Capability.Auth, signedIn, signedInDetail);

        var tenant = ConfigValues.Guid(config.TenantId, "tenantId", value => $"tenant {Short(value)}");
        var devEnvironment = ConfigValues.EnvironmentId(config.Mcs.DevEnvironmentId, "mcs.devEnvironmentId", value => $"env {Short(value)}");
        var testEnvironment = ConfigValues.EnvironmentId(config.Mcs.TestEnvironmentId, "mcs.testEnvironmentId", value => $"env {Short(value)}");

        Add(Capability.McsDev,
            signedIn && tenant.Supplied && devEnvironment.Supplied,
            !tenant.Supplied ? tenant.Detail : devEnvironment.Detail);

        Add(Capability.McsTest,
            signedIn && tenant.Supplied && testEnvironment.Supplied,
            !tenant.Supplied ? tenant.Detail : testEnvironment.Detail);

        var shareUser = ConfigValues.Guid(config.Mcs.ShareUserObjectId, "mcs.shareUserObjectId", value => $"user {Short(value)}");

        Add(Capability.McsShareUser,
            states[Capability.McsDev].Available && shareUser.Supplied,
            !states[Capability.McsDev].Available ? states[Capability.McsDev].Detail : shareUser.Detail);

        AddWorkspace(Add, Capability.WorkspaceCli, config.Mcs.Workspaces.CliAgent, fixturesDirectory, "MCSCliAgent");
        AddWorkspace(Add, Capability.WorkspaceClassic, config.Mcs.Workspaces.ClassicAgent, fixturesDirectory, "MCSClassicAgent");
        AddWorkspace(Add, Capability.WorkspaceWorkflow, config.Mcs.Workspaces.Workflow, fixturesDirectory, "MCSWorkflow");

        var subscription = ConfigValues.Guid(config.Azure.SubscriptionId, "azure.subscriptionId", value => $"sub {Short(value)}");
        var resourceGroup = ConfigValues.Text(config.Azure.ResourceGroup, "azure.resourceGroup", value => value);
        var azureCoordinates = subscription.Supplied && resourceGroup.Supplied;

        Add(Capability.AzureRead,
            signedIn && azureCoordinates && azureCliPresent && armConsentGranted,
            !subscription.Supplied ? subscription.Detail
            : !resourceGroup.Supplied ? resourceGroup.Detail
            : !azureCliPresent ? "az is not on PATH, so a created bot could not be cleaned up"
            : !armConsentGranted ? "Azure Resource Manager consent not granted — run one `abs` command interactively once"
            : $"{subscription.Detail} / {resourceGroup.Value} · az {AzureCliDetail()}");

        Add(Capability.AzureCreate,
            states[Capability.AzureRead].Available && config.Azure.AllowCreate,
            !states[Capability.AzureRead].Available ? states[Capability.AzureRead].Detail
            : config.Azure.AllowCreate ? "the run creates and deletes its own bot"
            : "azure.allowCreate is false");

        var existingBot = ConfigValues.Text(config.Azure.ExistingBotName, "azure.existingBotName", value => $"{value} (read-only)");

        Add(Capability.AzureExistingBot,
            states[Capability.AzureRead].Available && existingBot.Supplied,
            !states[Capability.AzureRead].Available ? states[Capability.AzureRead].Detail : existingBot.Detail);

        AddAms(Add, states, config, signedIn, tenant, azureCliPresent);

        var corpusRoot = ConfigValues.Text(config.Mcs.Copy.CorpusRoot, "mcs.copy.corpusRoot", value => value);
        var corpusAgents = corpusRoot.Usable is { } root && Directory.Exists(root) ? Directory.GetDirectories(root).Length : 0;

        Add(Capability.CopyCorpus,
            states[Capability.McsDev].Available && corpusAgents > 0,
            !states[Capability.McsDev].Available ? states[Capability.McsDev].Detail
            : !corpusRoot.Supplied ? corpusRoot.Detail
            : corpusAgents > 0 ? $"{corpusAgents} agents under {corpusRoot.Value} (read-only)"
            : $"no agent workspaces under {corpusRoot.Value}");

        var standaloneFixtures = Path.Combine(fixturesDirectory, StandaloneFixturesFolder);
        var standaloneFixturesExist = Directory.Exists(standaloneFixtures);
        Add(Capability.StandaloneFixtures, standaloneFixturesExist,
            standaloneFixturesExist ? $"fixtures/{StandaloneFixturesFolder} (bundled)" : $"fixtures not found at {standaloneFixtures}");

        Add(Capability.StandaloneOracle, false, NotProbed);
        Add(Capability.StandaloneFlowRuns, false, NotProbed);

        return new Capabilities(states);
    }

    public const string StandaloneFixturesFolder = "StandaloneWorkflows";

    private const string NotProbed = "not probed: no mcs-standaloneworkflow scenario is scheduled";

    /// <summary>The Agent Management Service capabilities, each one needing every capability before it.</summary>
    public static IReadOnlyList<Capability> AmsChain { get; } = [Capability.Ams, Capability.AmsCreate, Capability.AmsPackage, Capability.AmsHosted, Capability.AmsProd];

    /// <summary>Returns these capabilities with one Agent Management Service capability, and every one that needs it, unavailable for the same reason.</summary>
    public Capabilities WithoutAms(Capability capability, string detail) => AmsChain.SkipWhile(candidate => candidate != capability).Aggregate(this, (capabilities, candidate) => capabilities.With(candidate, false, detail));

    public Capabilities With(Capability capability, bool available, string detail)
    {
        var states = new Dictionary<Capability, CapabilityState>(_states)
        {
            [capability] = new CapabilityState(capability, available, detail),
        };

        return new Capabilities(states);
    }

    private static void AddAms(Action<Capability, bool, string> add, Dictionary<Capability, CapabilityState> states, E2EConfig config, bool signedIn, ConfigValue tenant, bool azureCliPresent)
    {
        var controlPlane = ConfigValues.Text(config.Ams.ControlPlaneEndpoint, "ams.controlPlane.endpoint", value => value);
        var dataPlane = ConfigValues.Text(config.Ams.DataPlane.Endpoint, "ams.dataPlane.endpoint", value => value);
        var home = ConfigValues.EnvironmentId(config.Ams.DevEnvironmentId, "ams.devEnvironmentId", value => $"home env {Short(value)}");
        var test = ConfigValues.EnvironmentId(config.Ams.TestEnvironmentId, "ams.testEnvironmentId", value => $"test env {Short(value)}");
        var prod = ConfigValues.EnvironmentId(config.Ams.ProdEnvironmentId, "ams.prodEnvironmentId", value => $"prod env {Short(value)}");
        var package = ConfigValues.Text(config.Ams.PackagePath, "ams.packagePath", value => value);
        var missing = new[] { tenant, controlPlane, dataPlane, home }.FirstOrDefault(value => !value.Supplied);

        add(Capability.Ams, signedIn && missing is null, !signedIn ? "not signed in" : missing?.Detail ?? $"{controlPlane.Value} · {home.Detail}");

        var ams = states[Capability.Ams];
        add(Capability.AmsCreate, ams.Available && config.Ams.AllowCreate && test.Supplied && azureCliPresent, !ams.Available ? ams.Detail : !config.Ams.AllowCreate ? "ams.allowCreate is false" : !test.Supplied ? test.Detail : !azureCliPresent ? "az is not on PATH, so the run could not create or delete its App Registrations" : "the run creates and deletes its own agents, bots and App Registrations");

        var create = states[Capability.AmsCreate];
        var packageExists = package.Usable is { } path && File.Exists(path);
        add(Capability.AmsPackage, create.Available && packageExists, !create.Available ? create.Detail : !package.Supplied ? package.Detail : packageExists ? package.Value! : $"no package at {package.Value}");

        var uploads = states[Capability.AmsPackage];
        add(Capability.AmsHosted, uploads.Available, uploads.Available ? "hosted deploys leave a hosted runtime copy that the Agent Management Service keeps" : uploads.Detail);

        var hosted = states[Capability.AmsHosted];
        add(Capability.AmsProd, hosted.Available && prod.Supplied, !hosted.Available ? hosted.Detail : prod.Detail);
    }

    private static void AddWorkspace(Action<Capability, bool, string> add, Capability capability, string? configured, string fixturesDirectory, string fixtureName)
    {
        var path = string.IsNullOrWhiteSpace(configured) ? Path.Combine(fixturesDirectory, fixtureName) : configured;
        var exists = Directory.Exists(path);
        var bundled = string.IsNullOrWhiteSpace(configured);

        add(capability, exists,
            exists ? (bundled ? $"fixtures/{fixtureName} (bundled)" : path) : $"workspace not found at {path}");
    }

    private static string AzureCliDetail() => AzureCli.Path is { Length: > 0 } path ? path : "resolved";

    private static string Short(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= 8 ? value : value[..(value.StartsWith("Default-", StringComparison.OrdinalIgnoreCase) ? 16 : 8)] + "…";
}
