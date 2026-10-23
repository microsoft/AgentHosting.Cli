using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

internal sealed class FakeConnectionPort : ICopilotConnectionPort
{
    public List<CopilotConnectionView> Views { get; } = [];

    public List<CopilotWorkflowView> Workflows { get; } = [];

    public List<CopilotConnectionBinding> Bound { get; } = [];

    public int BindCalls { get; private set; }

    public int ActivateCalls { get; private set; }

    public List<string> ActivatedWorkflows { get; } = [];

    public List<string> DraftWorkflows { get; } = [];

    public List<string> RefusedWorkflows { get; } = [];

    private CopilotWorkflowActivation Activation() => new()
    {
        Activated = ActivatedWorkflows,
        Refused = RefusedWorkflows,
        Observed = ActivatedWorkflows
            .Select(name => (name, running: true))
            .Concat(DraftWorkflows.Select(name => (name, running: false)))
            .ToDictionary(entry => entry.name, entry => entry.running, StringComparer.OrdinalIgnoreCase),
    };

    public Exception? ActivateThrows { get; init; }

    public Exception? ReadThrows { get; init; }

    public List<string> Directories { get; } = [];

    public Task<CopilotConnectionSnapshot> ReadConnectionsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken)
    {
        ReadCalls++;
        Directories.Add(projectDirectory);
        return ReadThrows is not null ? Task.FromException<CopilotConnectionSnapshot>(ReadThrows) : Task.FromResult(new CopilotConnectionSnapshot { References = Views, Workflows = Workflows });
    }

    public int ReadCalls { get; private set; }

    public int DeclaredConnectorCalls { get; private set; }

    public List<string> DeclaredConnectors { get; } = [];

    public Exception? DeclaredConnectorsThrows { get; init; }

    public Task<IReadOnlyList<string>> ReadDeclaredConnectorsAsync(CopilotEnvironmentContext environment, string projectDirectory, CancellationToken cancellationToken)
    {
        DeclaredConnectorCalls++;
        Directories.Add(projectDirectory);

        if (DeclaredConnectorsThrows is not null)
        {
            return Task.FromException<IReadOnlyList<string>>(DeclaredConnectorsThrows);
        }

        return Task.FromResult<IReadOnlyList<string>>(DeclaredConnectors.Count > 0
            ? DeclaredConnectors
            : [.. Views.Where(view => view.IsDeclared).Select(view => view.ConnectorId)]);
    }

    public Task BindAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotConnectionBinding> bindings, CancellationToken cancellationToken)
    {
        BindCalls++;
        Directories.Add(projectDirectory);
        Bound.AddRange(bindings);
        return Task.CompletedTask;
    }

    public List<CopilotWorkflowStateChange> StateChanges { get; } = [];

    public Queue<string> CreatedReferenceNames { get; } = new();

    public List<string> RemovedReferenceNames { get; } = [];

    public List<bool> RemovalConfirmations { get; } = [];

    public Queue<CopilotConnectionReferenceRemoval> Removals { get; } = new();

    public Task<CopilotConnectionReferenceRemoval> RemoveConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string logicalName, bool confirmed, CancellationToken cancellationToken)
    {
        Directories.Add(projectDirectory);
        RemovedReferenceNames.Add(logicalName);
        RemovalConfirmations.Add(confirmed);

        return Task.FromResult(Removals.Count > 0 ? Removals.Dequeue() : new CopilotConnectionReferenceRemoval(true, []));
    }

    public List<string> CreatedReferenceConnectors { get; } = [];

    public Exception? CreateReferenceThrows { get; init; }

    public Task<string> CreateConnectionReferenceAsync(CopilotEnvironmentContext environment, string projectDirectory, string connectorInternalId, CancellationToken cancellationToken)
    {
        Directories.Add(projectDirectory);
        CreatedReferenceConnectors.Add(connectorInternalId);

        return CreateReferenceThrows is not null
            ? Task.FromException<string>(CreateReferenceThrows)
            : Task.FromResult(CreatedReferenceNames.Count > 0 ? CreatedReferenceNames.Dequeue() : $"cre98_agent.{connectorInternalId}.00000000000000000000000000000000");
    }

    public Task<CopilotWorkflowActivation> SetWorkflowStatesAsync(CopilotEnvironmentContext environment, string projectDirectory, IReadOnlyList<CopilotWorkflowStateChange> changes, CancellationToken cancellationToken)
    {
        ActivateCalls++;
        Directories.Add(projectDirectory);
        StateChanges.AddRange(changes);

        return ActivateThrows is not null
            ? Task.FromException<CopilotWorkflowActivation>(ActivateThrows)
            : Task.FromResult(Activation());
    }
}

internal sealed class FakeConnectorCatalogPort : IConnectorCatalogPort
{
    public List<string> InternalIds { get; } = [];

    public List<CopilotConnectorInfo> Connectors { get; } = [];

    public Queue<IReadOnlyList<string>> Responses { get; } = new();

    public int Calls { get; private set; }

    public Exception? Throws { get; init; }

    public Dictionary<string, IReadOnlyList<CopilotConnectionCandidate>> Connections { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? ConnectionsThrow { get; init; }

    public List<string> ConnectionLookups { get; } = [];

    public Task<IReadOnlyList<CopilotConnectionCandidate>> ListConnectionsAsync(CopilotEnvironmentContext environment, string connectorInternalId, CancellationToken cancellationToken)
    {
        ConnectionLookups.Add(connectorInternalId);

        if (ConnectionsThrow is not null)
        {
            return Task.FromException<IReadOnlyList<CopilotConnectionCandidate>>(ConnectionsThrow);
        }

        if (Connections.TryGetValue(connectorInternalId, out var connections))
        {
            return Task.FromResult(connections);
        }

        return Serves(connectorInternalId)
            ? Task.FromResult<IReadOnlyList<CopilotConnectionCandidate>>([])
            : Task.FromException<IReadOnlyList<CopilotConnectionCandidate>>(new ConnectorNotFoundException(connectorInternalId, environment.EnvironmentId));
    }

    private bool Serves(string connectorInternalId) =>
        Connectors.Any(connector => connector.InternalId.Equals(connectorInternalId, StringComparison.OrdinalIgnoreCase))
        || InternalIds.Any(internalId => internalId.Equals(connectorInternalId, StringComparison.OrdinalIgnoreCase));

    public Task<IReadOnlyList<CopilotConnectorInfo>> ListConnectorsAsync(CopilotEnvironmentContext environment, CancellationToken cancellationToken)
    {
        Calls++;

        if (Throws is not null)
        {
            return Task.FromException<IReadOnlyList<CopilotConnectorInfo>>(Throws);
        }

        if (Connectors.Count > 0)
        {
            return Task.FromResult((IReadOnlyList<CopilotConnectorInfo>)Connectors);
        }

        return Task.FromResult<IReadOnlyList<CopilotConnectorInfo>>(
            [.. (Responses.Count > 0 ? Responses.Dequeue() : InternalIds).Select(id => new CopilotConnectorInfo { InternalId = id, DisplayName = id })]);
    }
}

internal sealed class FakeConnectionLifecyclePort : IConnectionLifecyclePort
{
    public List<(string ConnectorId, string ConnectionId)> Deleted { get; } = [];

    public Exception? Throws { get; init; }

    public Task DeleteConnectionAsync(CopilotEnvironmentContext environment, string connectorInternalId, string connectionId, CancellationToken cancellationToken)
    {
        Deleted.Add((connectorInternalId, connectionId));

        return Throws is not null ? Task.FromException(Throws) : Task.CompletedTask;
    }
}

internal sealed class ScriptedSelectionPrompt : IConnectionSelectionPrompt
{
    public Queue<ConnectionSelection> Choices { get; } = new();

    public Queue<int[]> Connectors { get; } = new();

    public List<string> Asked { get; } = [];
    public List<IReadOnlyList<ConnectionSelectionReference>> References { get; } = [];

    public List<IReadOnlyList<ConnectionSelectionOption>> Options { get; } = [];

    public List<ConnectorMenu> Menus { get; } = [];

    public List<string> Reported { get; } = [];

    public void Report(string activity) => Reported.Add(activity);

    public Task<IReadOnlyList<int>> ChooseConnectorsAsync(ConnectorMenu menu, CancellationToken cancellationToken)
    {
        Menus.Add(menu);

        if (Connectors.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        var numbers = Connectors.Dequeue();
        var chosen = numbers
            .Select(number => menu.Connectors.Select((connector, position) => (connector, position)).FirstOrDefault(entry => entry.connector.Number == number, (null!, -1)).position)
            .Where(position => position >= 0)
            .ToArray();

        return Task.FromResult<IReadOnlyList<int>>(chosen);
    }

    public Task<ConnectionSelection> SelectAsync(ConnectionSelectionRequest request, CancellationToken cancellationToken)
    {
        Asked.Add(request.ConnectorId);
        References.Add(request.References);
        Options.Add(request.Options);

        return Task.FromResult(Choices.Count > 0 ? Choices.Dequeue() : new ConnectionSelection(ConnectionSelectionKind.Back));
    }
}

internal sealed class ScriptedConsentFlow : IConnectionConsentFlow
{
    public Queue<ConnectionConsentResult> Outcomes { get; } = new();

    public List<string> RequestedConnectors { get; } = [];

    public List<bool> OpenedBrowser { get; } = [];

    public bool Throws { get; init; }

    public Task<ConnectionConsentResult> CreateConnectionAsync(CopilotEnvironmentContext environment, string connectorId, bool openBrowser, CancellationToken cancellationToken)
    {
        if (Throws)
        {
            throw new InvalidOperationException("The consent flow must not be reached.");
        }

        RequestedConnectors.Add(connectorId);
        OpenedBrowser.Add(openBrowser);

        return Task.FromResult(Outcomes.Count > 0
            ? Outcomes.Dequeue()
            : new ConnectionConsentResult(ConnectionConsentStatus.Cancelled, null, null, null, "No outcome was scripted."));
    }
}

internal sealed class RecordingBrowserLauncher : IBrowserLauncher
{
    private readonly TaskCompletionSource<Uri> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<Uri> Launched { get; } = [];

    public Exception? Throws { get; init; }

    /// <summary>Completes with the first link opened, so a test can answer a flow that is still waiting.</summary>
    public Task<Uri> FirstLaunch => _first.Task;

    public void Launch(Uri url)
    {
        if (Throws is not null)
        {
            throw Throws;
        }

        Launched.Add(url);
        _first.TrySetResult(url);
    }
}

internal sealed class RecordingMessagePresenter : IInteractiveMessagePresenter
{
    public List<string> Messages { get; } = [];

    public Task PresentAsync(string message, CancellationToken cancellationToken)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingProgressStatus(bool shown) : IProgressStatus
{
    public List<string> Reports { get; } = [];

    public bool Report(string status)
    {
        Reports.Add(status);
        return shown;
    }
}

internal sealed class ScriptedConfirmationPrompt : IConfirmationPrompt
{
    public bool Answer { get; init; } = true;

    public ConfirmationOutcome? Outcome { get; init; }

    public bool Throws { get; init; }

    public int Calls { get; private set; }

    public string? LastDetail { get; private set; }

    public string? LastExpected { get; private set; }

    public Task<ConfirmationOutcome> ConfirmAsync(string detail, string expectedResponse, CancellationToken cancellationToken)
    {
        Calls++;
        LastDetail = detail;
        LastExpected = expectedResponse;

        return Throws
            ? throw new InvalidOperationException("The prompt must not be constructed under --non-interactive.")
            : Task.FromResult(Outcome ?? (Answer ? ConfirmationOutcome.Confirmed : ConfirmationOutcome.Declined));
    }
}

internal sealed class TemporaryWorkspace : IDisposable
{
    public TemporaryWorkspace(bool withProjectFile = true, string schemaName = "con_agent", string displayName = "Contoso Agent")
    {
        Root = Path.Combine(Path.GetTempPath(), "ah-managed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        if (withProjectFile)
        {
            File.WriteAllText(Path.Combine(Root, AgentConfigLocator.ProjectSettingsFileName), $"displayName: \"{displayName}\"\nschemaName: \"{schemaName}\"\ntemplate: cliagent-1.0.0\n");
        }
    }

    public string Root { get; }

    public string ConfigPath => Path.Combine(Root, ".config", "agent.config.json");

    public void AddFile(string relativePath, string content)
    {
        var target = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal static class ManagedAgentFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    public static JsonAgentConfigStore Store() => new(new AgentConfigLocator(), new FixedClock(Now));

    public static CopilotEnvironmentResolver Resolver(params PowerPlatformEnvironment[] environments) =>
        TrackedResolver(environments).Resolver;

    public static CopilotEnvironmentResolver ResolverWithoutSelection(params PowerPlatformEnvironment[] environments) =>
        new(
            EnvironmentFixtures.Service(new FakeEnvironmentClient(environments), new InMemoryEnvironmentSelectionStore(), InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant)),
            PowerPlatformCloudMap.ForCloud(CloudInstance.Public));

    public static ConnectorReadinessWait ConnectorReadiness(IConnectorCatalogPort? connectors = null) =>
        new(connectors ?? new FakeConnectorCatalogPort(), TimeSpan.Zero, (_, _) => Task.CompletedTask);

    public static WorkflowStateCache WorkflowStates(ICopilotFileSystem? fileSystem = null) =>
        new(fileSystem ?? new FakeCopilotFileSystem());

    public static WorkflowActivationPass ActivationPass(ICopilotConnectionPort? connections = null, WorkflowStateCache? workflowStates = null) =>
        new(connections ?? new FakeConnectionPort(), workflowStates ?? WorkflowStates(), Principals());

    public static (CopilotEnvironmentResolver Resolver, FakeEnvironmentClient Client) TrackedResolver(params PowerPlatformEnvironment[] environments)
    {
        var client = new FakeEnvironmentClient(environments.Length == 0 ? [EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization)] : environments);
        var service = EnvironmentFixtures.Service(client, new InMemoryEnvironmentSelectionStore(), InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

        service.SelectAsync(environments.Length == 0 ? "env-1" : environments[0].EnvironmentId, CancellationToken.None).GetAwaiter().GetResult();

        return (new CopilotEnvironmentResolver(service, PowerPlatformCloudMap.ForCloud(CloudInstance.Public)), client);
    }

    public static ManagedAgentCloneMaterialiser Materialiser(ICopilotWorkspacePort workspaces) =>
        new(workspaces, Identities());

    public static ManagedAgentResolver AgentResolver(JsonAgentConfigStore store) => new(store, store, new AgentConfigLocator());

    public static CopilotProjectIdentityReader Identities() => new(new AgentConfigLocator());

    public static IWorkflowWorkspacePort WorkflowWorkspace() => new FakeWorkflowWorkspacePort();

    public static ManagedAgentPrincipalResolver Principals() => new(InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));

    public static ManagedAgentRecord Agent(string agentId = "agent-1", string displayName = "Contoso Agent", string environmentId = "env-1") => new()
    {
        AgentId = agentId,
        DisplayName = displayName,
        AgentType = AgentTypes.McsAgent,
        AgentLocation = AgentConfigLocator.CurrentDirectoryLocation,
        TenantId = EnvironmentFixtures.ContosoTenant,
        EnvironmentId = environmentId,
        Origin = ManagedAgentOrigins.AgentHostCli,
        EntityTag = string.Empty,
        AgentState = EntityStates.Saved,
    };

    public static AgentDeploymentRecord Deployment(string deploymentId = "slot-1", string agentId = "agent-1", string name = "dev", string stage = DeploymentStages.Dev, string environmentId = "env-1", DateTimeOffset? lastDeployedTime = null) => new()
    {
        DeploymentId = deploymentId,
        AgentId = agentId,
        AgentType = AgentTypes.McsAgent,
        TenantId = EnvironmentFixtures.ContosoTenant,
        HomeEnvironmentId = "env-1",
        DeploymentName = name,
        DeploymentType = stage,
        EnvironmentId = environmentId,
        LastDeployedTime = lastDeployedTime,
        EntityTag = string.Empty,
    };

    public static CopilotEnvironmentContext EnvironmentContext(string environmentId = "env-1") => new()
    {
        EnvironmentId = environmentId,
        TenantId = EnvironmentFixtures.ContosoTenant,
        DataverseOrganization = new Uri(CopilotTestFixtures.DataverseOrganization),
    };

    public static CopilotConnectionView Reference(string logicalName, string connectorId, string? boundConnectionId = null, params CopilotConnectionCandidate[] candidates) =>
        new()
        {
            LogicalName = logicalName,
            ConnectorId = connectorId,
            BoundConnectionId = boundConnectionId,
            BoundConnectionExists = boundConnectionId is not null,
            Candidates = candidates,
        };

    public const string Principal = "op@contoso.com";

    public static ConnectionPlanItem Plan(CopilotConnectionView view) => ConnectionReadinessPlanner.Plan(view, Principal);

    public static CopilotConnectionCandidate Candidate(string connectionId, string status = "Connected", string? owner = Principal) =>
        new() { ConnectionId = connectionId, DisplayName = $"{connectionId}@contoso.com", Status = status, Owner = owner };
}

