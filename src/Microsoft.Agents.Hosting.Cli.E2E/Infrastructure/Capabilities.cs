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

        var ams = ConfigValues.Text(config.Ams.Endpoint, "ams.endpoint", value => value);
        Add(Capability.Ams, ams.Supplied, ams.Detail);

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

    public Capabilities With(Capability capability, bool available, string detail)
    {
        var states = new Dictionary<Capability, CapabilityState>(_states)
        {
            [capability] = new CapabilityState(capability, available, detail),
        };

        return new Capabilities(states);
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
