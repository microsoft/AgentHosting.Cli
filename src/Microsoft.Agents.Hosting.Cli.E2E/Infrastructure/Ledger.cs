using System.Text.Json;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>The kind of resource a ledger entry tracks.</summary>
public enum ResourceKind
{
    McsAgent,
    AzureBot,
    EntraApp,
    LocalWorkspace,
    ConfigEdit,
    EvaluationTestSet,
    EvaluationRun,
    McsWorkflow,

    StandaloneWorkflow,

    StandaloneConnectionReference,

    AmsAgent,

    AmsFlow,

    LeftoverBot,

    GrsRepository,

    AmsConnector,

    AmsConnectionReference,
}

/// <summary>Where a resource stands: created and still present, removed, or left behind.</summary>
public enum ResourceState
{
    Created,
    Deleted,
    Orphaned,
    Restored,

    Retained,
}

/// <summary>How cleanup removes or restores one resource.</summary>
public sealed record CleanupPlan
{
    public string? Via { get; init; }

    public string? ProjectDirectory { get; init; }

    public string? DeploymentName { get; init; }

    public string? EnvironmentId { get; init; }

    /// <summary>The schema name a deploy targets, which identifies the agent before the service assigns an id.</summary>
    public string? SchemaName { get; init; }

    /// <summary>The cloud agent a child resource hangs off, which the CLI needs in order to address it.</summary>
    public string? McsAgentId { get; init; }

    /// <summary>The name a resource was created under, used to find it when its id was never learned.</summary>
    public string? Correlation { get; init; }

    /// <summary>The local record a standalone workflow is deleted through.</summary>
    public string? AgentId { get; init; }

    public string? ResourceGroup { get; init; }

    /// <summary>The subscription the resource was created in, so cleanup targets the same one.</summary>
    public string? SubscriptionId { get; init; }

    public string? PriorValue { get; init; }

    public string? WorkflowId { get; init; }

    public string? ConnectorId { get; init; }

    public bool AmsOff { get; init; }

    public string? GitUrl { get; init; }
}

/// <summary>One resource the run created or changed, recorded before the call that creates it.</summary>
public sealed record LedgerEntry
{
    public required int Sequence { get; init; }

    public required ResourceKind Kind { get; init; }

    /// <summary>The service-assigned id, or the schema name until a deploy reports one.</summary>
    public required string Id { get; set; }

    public string? DisplayName { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public CleanupPlan Cleanup { get; init; } = new();

    public ResourceState State { get; set; } = ResourceState.Created;

    /// <summary>Whether <see cref="Id"/> is the id the service assigned, rather than an intent recorded before the call.</summary>
    public bool Resolved { get; set; } = true;

    public string? Failure { get; set; }
}

/// <summary>The durable record of everything a run created; cleanup acts on this and nothing else.</summary>
public sealed class Ledger
{
    private readonly string _path;
    private readonly List<LedgerEntry> _entries;
    private readonly Lock _gate = new();

    private Ledger(string path, string runId, List<LedgerEntry> entries)
    {
        _path = path;
        RunId = runId;
        _entries = entries;
    }

    /// <summary>The run this ledger belongs to.</summary>
    public string RunId { get; }

    /// <summary>Every entry, in creation order.</summary>
    public IReadOnlyList<LedgerEntry> Entries
    {
        get { lock (_gate) { return [.. _entries]; } }
    }

    /// <summary>Entries that still hold a real resource.</summary>
    public IReadOnlyList<LedgerEntry> Outstanding =>
        [.. Entries.Where(entry => entry.State == ResourceState.Created)];

    /// <summary>Starts a ledger for a new run.</summary>
    public static Ledger Create(string runDirectory, string runId)
    {
        Directory.CreateDirectory(runDirectory);
        var ledger = new Ledger(Path.Combine(runDirectory, "ledger.json"), runId, []);
        ledger.Flush();
        return ledger;
    }

    /// <summary>Reopens a previous run's ledger so cleanup can resume.</summary>
    public static Ledger Open(string runDirectory, string runId)
    {
        var path = Path.Combine(runDirectory, "ledger.json");

        if (!File.Exists(path))
        {
            throw new E2EException($"No ledger at {path}.");
        }

        var document = JsonSerializer.Deserialize<LedgerDocument>(File.ReadAllText(path), ConfigJson.Options)
            ?? throw new E2EException($"The ledger at {path} is empty.");

        foreach (var entry in document.Entries)
        {
            entry.Resolved = entry.Resolved && IsServiceAssigned(entry);
        }

        return new Ledger(path, document.RunId ?? runId, [.. document.Entries]);
    }

    /// <summary>
    /// Whether an entry's id is one the service assigned rather than the intent it was recorded under.
    /// </summary>
    private static bool IsServiceAssigned(LedgerEntry entry) =>
        entry.Kind is not (ResourceKind.EvaluationRun or ResourceKind.McsAgent)
        || !string.Equals(entry.Id, entry.DisplayName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Records a Copilot Studio agent by its service-assigned id.</summary>
    public LedgerEntry RecordMcsAgent(string agentId, string environmentId, string? schemaName, string projectDirectory, string deploymentName, string scenarioId) =>
        Record(ResourceKind.McsAgent, agentId, schemaName, scenarioId, new CleanupPlan
        {
            Via = "deployment delete --delete-deployed-mcs-agent",
            ProjectDirectory = projectDirectory,
            DeploymentName = deploymentName,
            EnvironmentId = environmentId,
            SchemaName = schemaName,
        });

    /// <summary>
    /// Records the agent a deploy is about to create, before the call that creates it.
    ///
    /// A deploy that creates the agent and then fails to return — a crash, a timeout, a killed process —
    /// would otherwise leave a cloud agent no ledger names, and cleanup would report nothing left behind
    /// while it existed. The schema name is the identity the deploy targets, so it is enough for cleanup
    /// to act on until the service assigns an id.
    /// </summary>
    /// <param name="schemaName">The schema name the deploy targets.</param>
    /// <param name="environmentId">The environment being deployed into.</param>
    /// <param name="projectDirectory">The directory the CLI addresses the agent through.</param>
    /// <param name="deploymentName">The deployment slot being deployed.</param>
    /// <param name="scenarioId">The scenario that owns the deploy.</param>
    public LedgerEntry RecordIntendedMcsAgent(string schemaName, string environmentId, string projectDirectory, string deploymentName, string scenarioId)
    {
        lock (_gate)
        {
            if (TrackedBySchemaName(schemaName) is { } tracked)
            {
                return tracked;
            }
        }

        return Record(ResourceKind.McsAgent, schemaName, schemaName, scenarioId, new CleanupPlan
        {
            Via = "deployment delete --delete-deployed-mcs-agent",
            ProjectDirectory = projectDirectory,
            DeploymentName = deploymentName,
            EnvironmentId = environmentId,
            SchemaName = schemaName,
        }, resolved: false);
    }

    /// <summary>
    /// The entry already tracking a schema name, whether or not a deploy has named its agent yet.
    ///
    /// A promotion deploys a workspace a second time, into another environment. The agent is the same
    /// one, so a second entry would make cleanup delete it twice and report the second attempt as an
    /// orphan.
    /// </summary>
    private LedgerEntry? TrackedBySchemaName(string schemaName) =>
        _entries.FirstOrDefault(candidate =>
            candidate.Kind == ResourceKind.McsAgent
            && string.Equals(candidate.Cleanup.SchemaName, schemaName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Names the agent the service assigned to an intent recorded before the deploy.</summary>
    /// <param name="schemaName">The schema name the intent was recorded under.</param>
    /// <param name="agentId">The service-assigned agent id.</param>
    public void ResolveMcsAgent(string schemaName, string agentId)
    {
        lock (_gate)
        {
            if (_entries.FirstOrDefault(candidate =>
                    candidate.Kind == ResourceKind.McsAgent
                    && string.Equals(candidate.Id, schemaName, StringComparison.OrdinalIgnoreCase)) is not { } intent)
            {
                return;
            }

            intent.Id = agentId;
            intent.Resolved = true;
            FlushLocked();
        }
    }

    /// <summary>Records a standalone workflow by its Dataverse identifier.</summary>
    /// <param name="workflowId">The Dataverse workflow the record addresses.</param>
    /// <param name="environmentId">The environment holding the flow.</param>
    /// <param name="displayName">The workflow name, which its delete confirmation requires.</param>
    /// <param name="projectDirectory">The directory the CLI addresses the workflow through.</param>
    /// <param name="agentId">The local record addressing the flow.</param>
    /// <param name="scenarioId">The scenario that created it.</param>
    public LedgerEntry RecordMcsWorkflow(string workflowId, string environmentId, string? displayName, string projectDirectory, string agentId, string scenarioId) =>
        Record(ResourceKind.McsWorkflow, workflowId, displayName, scenarioId, new CleanupPlan
        {
            Via = "agent delete",
            ProjectDirectory = projectDirectory,
            EnvironmentId = environmentId,
            AgentId = agentId,
        });

    public LedgerEntry RecordStandaloneWorkflow(string workflowId, string environmentId, string? displayName, string projectDirectory, string? agentId, string? deploymentName, string scenarioId) =>
        Record(ResourceKind.StandaloneWorkflow, $"{workflowId}@{environmentId}", displayName, scenarioId, new CleanupPlan
        {
            Via = "agent delete through the record, else a clone of the flow",
            ProjectDirectory = projectDirectory,
            EnvironmentId = environmentId,
            AgentId = agentId,
            DeploymentName = deploymentName,
            WorkflowId = workflowId,
        });

    public LedgerEntry RecordStandaloneConnectionReference(string logicalName, string connectorId, string environmentId, string scenarioId) =>
        Record(ResourceKind.StandaloneConnectionReference, $"{logicalName}@{environmentId}", logicalName, scenarioId, new CleanupPlan
        {
            Via = "deployment connection remove --connection-reference --force",
            EnvironmentId = environmentId,
            Correlation = logicalName,
            ConnectorId = connectorId,
        });

    /// <summary>Records an Azure bot, with the subscription it was created in.</summary>
    /// <param name="botName">The bot resource name.</param>
    /// <param name="resourceGroup">The resource group holding it.</param>
    /// <param name="subscriptionId">The subscription it was created in.</param>
    /// <param name="scenarioId">The scenario that created it.</param>
    public LedgerEntry RecordAzureBot(string botName, string resourceGroup, string? subscriptionId, string scenarioId) =>
        Record(ResourceKind.AzureBot, botName, botName, scenarioId, new CleanupPlan { Via = "az bot delete", ResourceGroup = resourceGroup, SubscriptionId = subscriptionId });

    /// <summary>Records an Entra app registration, which only <c>az</c> can remove.</summary>
    public LedgerEntry RecordEntraApp(string applicationId, string? displayName, string scenarioId) =>
        Record(ResourceKind.EntraApp, applicationId, displayName, scenarioId, new CleanupPlan { Via = "az ad app delete" });

    /// <summary>Records an isolated workspace so a passing scenario's copy can be removed.</summary>
    public LedgerEntry RecordLocalWorkspace(string directory, string scenarioId) =>
        Record(ResourceKind.LocalWorkspace, directory, null, scenarioId, new CleanupPlan { Via = "delete directory" });

    /// <summary>
    /// Records an evaluation test set by the name it is created under, before the call that creates it.
    /// The delete verb resolves a name as readily as an id, so the name alone is enough for cleanup to
    /// act on even when the creating call never returned.
    /// </summary>
    /// <param name="name">The name the test set is created under.</param>
    /// <param name="environmentId">The environment holding it.</param>
    /// <param name="mcsAgentId">The cloud agent it belongs to.</param>
    /// <param name="scenarioId">The scenario that creates it.</param>
    public LedgerEntry RecordEvaluationTestSet(string name, string environmentId, string mcsAgentId, string scenarioId) =>
        Record(ResourceKind.EvaluationTestSet, name, name, scenarioId, new CleanupPlan
        {
            Via = "agent eval testset delete --force",
            EnvironmentId = environmentId,
            McsAgentId = mcsAgentId,
        });

    /// <summary>
    /// Records an evaluation run by the name it is started under, before the call that starts it.
    /// </summary>
    /// <param name="intent">A name unique to this scenario's run.</param>
    /// <param name="environmentId">The environment holding it.</param>
    /// <param name="mcsAgentId">The cloud agent it belongs to.</param>
    /// <param name="runName">The name the run carries in the service, used to find it if the start never returned.</param>
    /// <param name="scenarioId">The scenario that starts it.</param>
    public LedgerEntry RecordEvaluationRun(string intent, string environmentId, string mcsAgentId, string runName, string scenarioId) =>
        Record(ResourceKind.EvaluationRun, intent, intent, scenarioId, new CleanupPlan
        {
            Via = "agent eval cancel, then agent eval delete",
            EnvironmentId = environmentId,
            McsAgentId = mcsAgentId,
            Correlation = runName,
        }, resolved: false);

    /// <summary>Names the run the service assigned to an intent recorded before it was started.</summary>
    /// <param name="intent">The name the intent was recorded under.</param>
    /// <param name="runId">The service-assigned run id.</param>
    public void ResolveEvaluationRun(string intent, string runId)
    {
        lock (_gate)
        {
            if (_entries.FirstOrDefault(candidate =>
                    candidate.Kind == ResourceKind.EvaluationRun
                    && string.Equals(candidate.Id, intent, StringComparison.OrdinalIgnoreCase)) is not { } recorded)
            {
                return;
            }

            recorded.Id = runId;
            recorded.Resolved = true;
            FlushLocked();
        }
    }

    /// <summary>Records a reversible change together with the value cleanup restores.</summary>
    public LedgerEntry RecordConfigEdit(string target, string? priorValue, string scenarioId) =>
        Record(ResourceKind.ConfigEdit, target, target, scenarioId, new CleanupPlan { Via = "restore prior value", PriorValue = priorValue });

    public LedgerEntry RecordAmsAgent(string agentId, string? displayName, string projectDirectory, bool amsOff, string scenarioId)
    {
        var entry = Record(ResourceKind.AmsAgent, agentId, displayName, scenarioId, new CleanupPlan { Via = "deployment delete for each test and prod deployment, then agent delete", ProjectDirectory = projectDirectory, AmsOff = amsOff });

        return entry.Cleanup.AmsOff && !amsOff ? Replace(entry, entry with { Cleanup = entry.Cleanup with { AmsOff = false } }) : entry;
    }

    public LedgerEntry RecordAmsFlow(string name, string environmentId, string scenarioId) => RecordNamed(ResourceKind.AmsFlow, name, environmentId, scenarioId, "turned off and deleted through Dataverse by its exact name");

    public LedgerEntry RecordLeftoverBot(string schemaName, string scenarioId) => Record(ResourceKind.LeftoverBot, schemaName, schemaName, scenarioId, new CleanupPlan { Via = "deleted through Dataverse in each organization, after checking its schema name", SchemaName = schemaName });

    public LedgerEntry RecordGrsRepository(string repositoryId, string gitUrl, string environmentId, string scenarioId) => Record(ResourceKind.GrsRepository, repositoryId, repositoryId, scenarioId, new CleanupPlan { Via = "soft-deleted through GRS once AMS lists no agent in it", EnvironmentId = environmentId, GitUrl = gitUrl });

    public LedgerEntry RecordAmsConnector(string name, string environmentId, string scenarioId) => RecordNamed(ResourceKind.AmsConnector, name, environmentId, scenarioId, "deleted through Dataverse by its exact name, after its connection references");

    public LedgerEntry RecordAmsConnectionReference(string logicalName, string environmentId, string scenarioId) => RecordNamed(ResourceKind.AmsConnectionReference, logicalName, environmentId, scenarioId, "deleted through Dataverse by its exact logical name");

    /// <summary>Marks an entry removed once its resource is confirmed gone.</summary>
    public void MarkDeleted(string id) => SetState(id, ResourceState.Deleted, null);

    /// <summary>Marks an entry restored to its prior value.</summary>
    public void MarkRestored(string id) => SetState(id, ResourceState.Restored, null);

    /// <summary>Marks an entry left behind, with the reason removal failed.</summary>
    public void MarkOrphaned(string id, string failure) => SetState(id, ResourceState.Orphaned, failure);

    public void MarkRetained(string id, string reason) => SetState(id, ResourceState.Retained, reason);

    private LedgerEntry RecordNamed(ResourceKind kind, string name, string environmentId, string scenarioId, string via) => Record(kind, $"{name}@{environmentId}", name, scenarioId, new CleanupPlan { Via = via, EnvironmentId = environmentId, Correlation = name });

    private LedgerEntry Record(ResourceKind kind, string id, string? displayName, string scenarioId, CleanupPlan cleanup, bool resolved = true)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new E2EException($"{scenarioId} tried to record a {kind} with no id. Cleanup acts only on service-assigned ids.");
        }

        lock (_gate)
        {
            if (_entries.FirstOrDefault(candidate => candidate.Kind == kind && string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                return existing;
            }

            var entry = new LedgerEntry
            {
                Sequence = _entries.Count + 1,
                Kind = kind,
                Id = id,
                DisplayName = displayName,
                CreatedBy = scenarioId,
                CreatedAt = DateTimeOffset.UtcNow,
                Cleanup = cleanup,
                Resolved = resolved,
            };

            _entries.Add(entry);
            FlushLocked();
            return entry;
        }
    }

    private LedgerEntry Replace(LedgerEntry existing, LedgerEntry replacement)
    {
        lock (_gate)
        {
            _entries[_entries.FindIndex(candidate => ReferenceEquals(candidate, existing))] = replacement;
            FlushLocked();
            return replacement;
        }
    }

    private void SetState(string id, ResourceState state, string? failure)
    {
        lock (_gate)
        {
            foreach (var entry in _entries.Where(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                entry.State = state;
                entry.Failure = failure;
            }

            FlushLocked();
        }
    }

    private void Flush()
    {
        lock (_gate)
        {
            FlushLocked();
        }
    }

    private void FlushLocked() =>
        File.WriteAllText(_path, JsonSerializer.Serialize(new LedgerDocument { RunId = RunId, Entries = _entries }, ConfigJson.Options));

    private sealed record LedgerDocument
    {
        public string? RunId { get; init; }

        public List<LedgerEntry> Entries { get; init; } = [];
    }
}
