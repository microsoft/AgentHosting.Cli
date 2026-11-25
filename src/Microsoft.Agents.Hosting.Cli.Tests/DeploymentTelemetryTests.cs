using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class DeploymentTelemetryTests
{
    private static readonly string[] DeployKeys =
        ["resourceAction", "deploymentType", "publishRequested", "publishStatus", "workflowState"];

    [Theory]
    [InlineData("success", "succeeded")]
    [InlineData("blocked", "blocked")]
    [InlineData("failed", "failed")]
    [InlineData("cancelled", "unknown")]
    [InlineData("push-failed", "not-reached")]
    [InlineData("not-requested", "not-requested")]
    [InlineData("no-change", "succeeded")]
    [InlineData("no-change-failed", "failed")]
    public async Task AgentPublicationDistinguishesConfirmedPushFromPublication(string mode, string expectedStatus)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        store.Add(project.Root, ManagedAgentFixtures.Agent());
        store.Add(project.Root, ManagedAgentFixtures.Deployment());
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var resolver = new ManagedAgentResolver(store, store, new AgentConfigLocator(), new CommandTelemetryAgentObserver(accessor));
        var workspaces = new FakeCopilotWorkspacePort
        {
            AgentIdReturns = "a260baca-7b6e-4e15-8d55-06dfb1ee2b36",
            PushThrows = mode == "push-failed" ? new IOException("Synthetic push failure.") : null,
        };
        bool noChange = mode is "no-change" or "no-change-failed";
        if (!noChange)
        {
            workspaces.LocalChanges.Add(CopilotTestFixtures.Change("con_greeting"));
        }
        if (mode == "blocked")
        {
            workspaces.PushWarnings.Add("A component did not land.");
        }
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = workspaces.AgentIdReturns };
        catalog.PublishedOnSequence.Enqueue(null);
        catalog.PublishedOnSequence.Enqueue(ManagedAgentFixtures.Now);
        if (mode is "failed" or "cancelled" or "no-change-failed")
        {
            catalog.PublishThrowsSequence.Enqueue(mode == "cancelled"
                ? new OperationCanceledException("Synthetic publication cancellation.")
                : new IOException("Synthetic publish failure."));
        }
        var service = AgentService(store, resolver, workspaces, catalog);
        bool publish = mode != "not-requested";
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
        var request = new ManagedAgentDeployRequest { Project = project.Root, DeploymentName = "dev", Publish = publish };
        if (mode == "cancelled")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.DeployAsync(request, CancellationToken.None));
        }
        else if (mode is "blocked" or "failed" or "push-failed" or "no-change-failed")
        {
            await Assert.ThrowsAsync<ManagedAgentException>(() => service.DeployAsync(request, CancellationToken.None));
        }
        else
        {
            var result = await service.DeployAsync(request, CancellationToken.None);
            Assert.Equal(mode is "success" or "no-change", result.Published);
        }
        session.RecordOutcome(mode == "cancelled" ? CommandTelemetryOutcome.Cancellation
            : mode is "blocked" or "failed" or "push-failed" or "no-change-failed" ? CommandTelemetryOutcome.Failure
            : CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        var dimensions = Outcome(transport);
        Assert.Equal("dev", dimensions.GetProperty("deploymentType").GetString());
        Assert.Equal(publish, dimensions.GetProperty("publishRequested").GetBoolean());
        Assert.Equal(expectedStatus, dimensions.GetProperty("publishStatus").GetString());
        Assert.Equal(mode != "push-failed", dimensions.TryGetProperty("resourceAction", out var action));
        if (mode != "push-failed")
        {
            Assert.Equal(noChange ? "updated-nochange" : "updated", action.GetString());
        }
        Assert.False(dimensions.TryGetProperty("workflowState", out _));
        Assert.Equal(mode is "success" or "failed" or "cancelled" or "no-change" or "no-change-failed" ? 1 : 0, catalog.PublishCalls);
    }

    [Theory]
    [InlineData("none", "updated-nochange")]
    [InlineData("settings", "updated")]
    [InlineData("files", "updated")]
    [InlineData("auxiliary", "updated")]
    [InlineData("collection", "updated")]
    [InlineData("created", "created")]
    public async Task AgentResourceActionUsesPushStatusAcrossAllWorkspaces(string mode, string expectedAction)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        store.Add(project.Root, ManagedAgentFixtures.Agent());
        store.Add(project.Root, ManagedAgentFixtures.Deployment());
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var resolver = new ManagedAgentResolver(store, store, new AgentConfigLocator(), new CommandTelemetryAgentObserver(accessor));
        var workspaces = new FakeCopilotWorkspacePort
        {
            AgentIdReturns = "a260baca-7b6e-4e15-8d55-06dfb1ee2b36",
            WorkspaceExists = mode != "created",
            ComponentCountReturns = 0,
            UploadedFileNamesReturns = mode == "files" ? ["SKILL.md"] : [],
            AuxiliaryChangeCountReturns = mode == "auxiliary" ? 1 : 0,
        };
        if (mode == "settings")
        {
            workspaces.LocalChanges.Add(CopilotTestFixtures.Change("settings", componentType: "update"));
        }
        if (mode == "collection")
        {
            project.AddFile(AgentConfigLocator.ReferencesFileName, "componentCollections:\n  - schemaName:\n    directory: Shared Tools/\n");
            project.AddFile(Path.Combine("Shared Tools", AgentConfigLocator.CollectionProjectFileName), "schemaName: con_sharedtools\n");
            workspaces.PushedChangesByDirectory[Path.Combine(project.Root, "Shared Tools")] =
                [CopilotTestFixtures.Change("con_shared_topic")];
        }
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = mode == "created" ? null : workspaces.AgentIdReturns };
        var attach = new RecordingAttachPort
        {
            AgentId = workspaces.AgentIdReturns,
            OnAttach = () => workspaces.WorkspaceExists = true,
        };
        var service = AgentService(store, resolver, workspaces, catalog, attach);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);

        var result = await service.DeployAsync(
            new ManagedAgentDeployRequest { Project = project.Root, DeploymentName = "dev" }, default);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.Equal(expectedAction, Outcome(transport).GetProperty("resourceAction").GetString());
        Assert.Equal(mode == "created", result.TargetAgentCreated);
        Assert.Equal(mode == "created" ? "reattach" : "push", result.Action);
        Assert.Equal(mode == "collection" ? 2 : 1, workspaces.PushCalls);
        Assert.Equal(0, result.PushedComponentCount);
        Assert.Equal(mode == "files" ? 1 : 0, result.UploadedFileCount);
        Assert.Equal(mode == "auxiliary" ? 1 : 0, result.AuxiliaryChangeCount);
    }

    [Theory]
    [InlineData("draft", "created", "not-requested", "draft")]
    [InlineData("update", "updated", "not-requested", "draft")]
    [InlineData("replace", "replaced", "not-requested", "draft")]
    [InlineData("automatic", "created", "succeeded", "published")]
    [InlineData("explicit", "created", "succeeded", "published")]
    [InlineData("blocked", "created", "blocked", "draft")]
    [InlineData("failed", "created", "failed", "draft")]
    [InlineData("refused", "created", "failed", "draft")]
    [InlineData("cancelled", "created", "unknown", "draft")]
    [InlineData("persistence", "created", "succeeded", "published")]
    [InlineData("cache-write", "created", "succeeded", "published")]
    [InlineData("unconfirmed", "created", "unknown", "published")]
    [InlineData("unknown-state", "created", "not-requested", "unknown")]
    public async Task WorkflowMilestonesSurviveActivationAndPersistenceFailures(
        string mode, string expectedAction, string expectedStatus, string expectedState)
    {
        using var project = new TemporaryWorkspace(withProjectFile: false);
        bool automatic = mode is "automatic" or "blocked" or "failed" or "refused" or "cancelled" or "persistence" or "cache-write" or "unconfirmed";
        var id = WorkflowFixtures.Write(project.Root, "Workflow", activated: automatic);
        var store = ManagedAgentFixtures.Store();
        store.Add(project.Root, WorkflowFixtures.Record(workflowId: id.ToString()));
        store.Add(project.Root, WorkflowFixtures.Deployment());
        var workflows = new FakeWorkflowStorePort
        {
            DraftOnPush = true,
            ReplacedOnPush = mode == "replace",
            ObservedStateOnPush = mode == "unconfirmed" ? WorkflowStateProjection.Activated
                : mode == "unknown-state" ? WorkflowStateProjection.Unknown : null,
        };
        if (mode is "update" or "replace")
        {
            workflows.Cloud[id] = new WorkflowDocument { WorkflowId = id, Name = "Workflow", State = WorkflowStateProjection.Draft };
        }
        var connections = new FakeConnectionPort
        {
            ReadThrows = mode == "failed" ? new IOException("Synthetic activation discovery failure.") : null,
            ActivateThrows = mode == "cancelled" ? new OperationCanceledException("Synthetic activation cancellation.") : null,
        };
        connections.Workflows.Add(new CopilotWorkflowView
        {
            WorkflowId = id.ToString(), DisplayName = "Workflow", CanEnable = mode != "blocked",
        });
        if (mode == "blocked")
        {
            connections.Views.Add(new CopilotConnectionView
            {
                LogicalName = "shared_office365", ConnectorId = "shared_office365", IsDeclared = true,
            });
        }
        else if (mode == "refused")
        {
            connections.RefusedWorkflows.Add("Synthetic service refusal.");
        }
        else if (mode != "unconfirmed")
        {
            connections.ActivatedWorkflows.Add("Workflow");
        }
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var resolver = new ManagedAgentResolver(store, store, new AgentConfigLocator(), new CommandTelemetryAgentObserver(accessor));
        var targetResolver = new WorkflowProjectResolver(resolver, new AgentConfigLocator(),
            ManagedAgentFixtures.Resolver(), new FakeWorkflowWorkspacePort());
        var states = ManagedAgentFixtures.WorkflowStates(mode == "cache-write"
            ? new FailingActivationCacheFileSystem() : new FakeCopilotFileSystem());
        IManagedAgentRegistry agents = mode == "persistence" ? new RefusingAgentRegistry(store) : store;
        var service = new WorkflowDeployService(targetResolver, agents, store, workflows,
            ManagedAgentFixtures.ActivationPass(connections, states), states,
            ManagedAgentFixtures.Principals(), new FixedClock(ManagedAgentFixtures.Now));
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
        var request = new ManagedAgentDeployRequest
        {
            Project = project.Root, DeploymentName = "devwf", Publish = mode == "explicit",
        };
        if (mode == "cancelled")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.DeployAsync(request, CancellationToken.None));
        }
        else if (mode == "persistence")
        {
            await Assert.ThrowsAsync<ManagedAgentException>(() => service.DeployAsync(request, CancellationToken.None));
        }
        else
        {
            var result = await service.DeployAsync(request, CancellationToken.None);
            Assert.Equal(mode == "cache-write" ? "draft" : expectedState, result.WorkflowState);
            if (mode == "cache-write")
            {
                Assert.Equal(1, connections.ActivateCalls);
                Assert.False(result.Published);
                Assert.Null(result.PublishedOn);
                Assert.Equal(
                    "The workflows could not be activated after the push: Synthetic cache write failure. Run `msagent deployment connection update` to retry.",
                    Assert.Single(result.Warnings));
            }
        }
        session.RecordOutcome(mode == "cancelled" ? CommandTelemetryOutcome.Cancellation
            : mode == "persistence" ? CommandTelemetryOutcome.Failure : CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        var dimensions = Outcome(transport);
        Assert.Equal(expectedAction, dimensions.GetProperty("resourceAction").GetString());
        Assert.Equal(expectedStatus, dimensions.GetProperty("publishStatus").GetString());
        Assert.Equal(expectedState, dimensions.GetProperty("workflowState").GetString());
        Assert.Equal("dev", dimensions.GetProperty("deploymentType").GetString());
        Assert.Equal(mode == "explicit", dimensions.GetProperty("publishRequested").GetBoolean());
        Assert.Equal(id.ToString(), dimensions.GetProperty("mcsWorkflowId").GetString());
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("request")]
    [InlineData("action")]
    [InlineData("invalid-stage")]
    [InlineData("invalid-action")]
    [InlineData("invalid-status")]
    [InlineData("invalid-state")]
    [InlineData("type")]
    public async Task InvalidOrConflictingDeployFactsAreStickyOmitted(string mode)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
        session.ObserveAgentType(AgentTypes.McsWorkflow);
        var initial = new DeploymentTelemetryObservation
        {
            DeploymentType = "test", PublishRequested = true, ResourceAction = DeploymentResourceAction.Created,
            PublishStatus = DeploymentPublishStatus.NotReached, WorkflowState = DeploymentWorkflowState.Draft,
        };
        session.ObserveDeployment(initial);
        session.ObserveDeployment(mode switch
        {
            "stage" => new() { DeploymentType = "prod" },
            "request" => new() { PublishRequested = false },
            "action" => new() { ResourceAction = DeploymentResourceAction.Updated },
            "invalid-stage" => new() { DeploymentType = "arbitrary-sensitive-input" },
            "invalid-action" => new() { ResourceAction = (DeploymentResourceAction)999 },
            "invalid-status" => new() { PublishStatus = (DeploymentPublishStatus)999 },
            "invalid-state" => new() { WorkflowState = (DeploymentWorkflowState)999 },
            _ => new(),
        });
        if (mode == "type")
        {
            session.ObserveAgentType(AgentTypes.McsAgent);
        }
        session.ObserveDeployment(initial with { PublishStatus = DeploymentPublishStatus.Succeeded });
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        var dimensions = Outcome(transport);
        Assert.All(DeployKeys, key => Assert.False(dimensions.TryGetProperty(key, out _)));
    }

    [Theory]
    [InlineData("push", true, AgentTypes.McsAgent)]
    [InlineData("deploy", false, AgentTypes.McsAgent)]
    [InlineData("deploy", true, AgentTypes.CodeAgent)]
    [InlineData("deploy", true, null)]
    public async Task DeployFactsRequireTargetedMcsDeploy(string verb, bool targetsAgent, string? agentType)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", verb]), targetsAgent: targetsAgent);
        if (agentType is not null)
        {
            session.ObserveAgentType(agentType);
        }
        session.ObserveDeployment(new()
        {
            DeploymentType = "prod", PublishRequested = false, ResourceAction = DeploymentResourceAction.Created,
            PublishStatus = DeploymentPublishStatus.NotRequested, WorkflowState = DeploymentWorkflowState.Draft,
        });
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        var dimensions = Outcome(transport);
        Assert.All(DeployKeys, key => Assert.False(dimensions.TryGetProperty(key, out _)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeployObservationsRemainInvocationLocal(bool disposeChild)
    {
        var parentTransport = new CapturingTransport();
        var childTransport = new CapturingTransport();
        await using var parent = Session(parentTransport);
        await using var child = Session(childTransport);
        var accessor = new CommandTelemetrySessionAccessor();
        var observer = new CommandTelemetryAgentObserver(accessor);
        using var parentScope = accessor.Push(parent);
        parent.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
        observer.Observe(AgentTypes.McsAgent);
        observer.ObserveDeployment(new() { DeploymentType = "prod", PublishRequested = false });
        using (accessor.Push(child))
        {
            child.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
            observer.Observe(AgentTypes.McsWorkflow);
            observer.ObserveDeployment(new() { DeploymentType = "test", PublishRequested = true });
            child.RecordOutcome(CommandTelemetryOutcome.Success);
            if (disposeChild)
            {
                await child.DisposeAsync();
            }
            observer.ObserveDeployment(new() { ResourceAction = DeploymentResourceAction.Replaced });
        }
        parent.RecordOutcome(CommandTelemetryOutcome.Success);
        await child.DisposeAsync();
        await parent.DisposeAsync();

        var parentDimensions = Outcome(parentTransport);
        var childDimensions = Outcome(childTransport);
        Assert.Equal("prod", parentDimensions.GetProperty("deploymentType").GetString());
        Assert.Equal("test", childDimensions.GetProperty("deploymentType").GetString());
        Assert.False(parentDimensions.TryGetProperty("resourceAction", out _));
        Assert.Equal(!disposeChild, childDimensions.TryGetProperty("resourceAction", out _));
    }

    [Fact]
    public void DeployObservationsAreOptionalAndIsolateObserverFailures()
    {
        var store = ManagedAgentFixtures.Store();
        var observation = new DeploymentTelemetryObservation { PublishRequested = true };
        var legacy = new LegacyObserver();
        new ManagedAgentResolver(store, store, new AgentConfigLocator(), legacy).ObserveDeployment(observation);
        Assert.Equal(0, legacy.TypeCalls);
        new ManagedAgentResolver(store, store, new AgentConfigLocator(), new ThrowingObserver()).ObserveDeployment(observation);
        new ManagedAgentResolver(store, store, new AgentConfigLocator()).ObserveDeployment(observation);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("test")]
    [InlineData("prod")]
    public async Task ParallelInvocationsKeepIndependentDeployFacts(string stage)
    {
        var accessor = new CommandTelemetrySessionAccessor();
        var observer = new CommandTelemetryAgentObserver(accessor);
        var outcomes = await Task.WhenAll(
            EmitAsync(DeploymentResourceAction.Created, DeploymentPublishStatus.Succeeded),
            EmitAsync(DeploymentResourceAction.Updated, DeploymentPublishStatus.Failed));

        Assert.Equal("created", outcomes[0].GetProperty("resourceAction").GetString());
        Assert.Equal("succeeded", outcomes[0].GetProperty("publishStatus").GetString());
        Assert.Equal("updated", outcomes[1].GetProperty("resourceAction").GetString());
        Assert.Equal("failed", outcomes[1].GetProperty("publishStatus").GetString());
        Assert.All(outcomes, dimensions => Assert.Equal(stage, dimensions.GetProperty("deploymentType").GetString()));

        async Task<JsonElement> EmitAsync(DeploymentResourceAction action, DeploymentPublishStatus status)
        {
            var transport = new CapturingTransport();
            await using var session = Session(transport);
            using var scope = accessor.Push(session);
            session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
            observer.Observe(AgentTypes.McsAgent);
            observer.ObserveDeployment(new() { DeploymentType = stage, PublishRequested = true });
            await Task.Yield();
            observer.ObserveDeployment(new() { ResourceAction = action, PublishStatus = status });
            session.RecordOutcome(CommandTelemetryOutcome.Success);
            await session.DisposeAsync();
            return Outcome(transport);
        }
    }

    [Fact]
    public async Task DisabledAndUnstartedSessionsDoNotAcceptDeployFacts()
    {
        var accessor = new CommandTelemetrySessionAccessor();
        var observer = new CommandTelemetryAgentObserver(accessor);
        var observation = new DeploymentTelemetryObservation
        {
            DeploymentType = "prod", PublishRequested = true, ResourceAction = DeploymentResourceAction.Created,
        };
        observer.ObserveDeployment(observation);
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        using (accessor.Push(session))
        {
            observer.ObserveDeployment(observation);
            session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
            observer.Observe(AgentTypes.McsAgent);
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }
        await session.DisposeAsync();
        Assert.All(DeployKeys, key => Assert.False(Outcome(transport).TryGetProperty(key, out _)));
        await using var disabled = CommandTelemetrySession.Disabled();
        using (accessor.Push(disabled))
        {
            disabled.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
            observer.Observe(AgentTypes.McsAgent);
            observer.ObserveDeployment(observation);
            disabled.RecordOutcome(CommandTelemetryOutcome.Success);
            Assert.False(observer.IsObservationEnabled);
        }
    }

    private static ManagedAgentDeployService AgentService(JsonAgentConfigStore store, ManagedAgentResolver resolver,
        FakeCopilotWorkspacePort workspaces, FakeCopilotCatalogPort catalog, RecordingAttachPort? attach = null)
    {
        var environments = ManagedAgentFixtures.Resolver();
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());
        return new ManagedAgentDeployService(resolver, store, store, environments, locator,
            new CopilotWorkspaceService(workspaces, locator), ManagedAgentFixtures.Identities(),
            attach ?? new RecordingAttachPort(), catalog, new CopilotCatalogService(catalog, environments, locator,
                new FixedClock(ManagedAgentFixtures.Now), (_, _) => Task.CompletedTask),
            ManagedAgentFixtures.Principals(), ManagedAgentFixtures.ActivationPass(),
            ManagedAgentFixtures.AgentNames(), new FixedClock(ManagedAgentFixtures.Now));
    }

    private static JsonElement Outcome(CapturingTransport transport)
    {
        var events = Assert.IsType<string>(transport.Payload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("data").Clone()).ToArray();
        Assert.Equal(2, events.Length);
        Assert.All(events, data =>
        {
            using var dimensions = JsonDocument.Parse(data.GetProperty("customDimensions").GetString()!);
            string[] keys = dimensions.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        });
        Assert.All(events, data => Assert.All(DeployKeys, key => Assert.False(data.TryGetProperty(key, out _))));
        Assert.Equal("CommandAttempt", events[0].GetProperty("event_Name").GetString());
        var attempt = JsonDocument.Parse(events[0].GetProperty("customDimensions").GetString()!).RootElement;
        Assert.All(DeployKeys, key => Assert.False(attempt.TryGetProperty(key, out _)));
        Assert.Equal("CommandOutcome", events[1].GetProperty("event_Name").GetString());
        return JsonDocument.Parse(events[1].GetProperty("customDimensions").GetString()!).RootElement.Clone();
    }

    private static CommandTelemetrySession Session(CapturingTransport transport) =>
        new(new Resolver(), new FixedClock(ManagedAgentFixtures.Now), "1.2.3-test",
            transport: transport, flushTimeout: TelemetryTestBudgets.Flush);

    private sealed class Resolver : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;
        public ValueTask<TelemetryDestination?> ResolveAsync(TelemetryEnvironmentTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult<TelemetryDestination?>(new TelemetryDestination(
                new Uri("http://127.0.0.1:1/OneCollector/1.0/"), TelemetryIngestionConfiguration.ProductionKey,
                "powerappsclient", "prod", "prod", TelemetryLocation.Global));
    }

    private sealed class CapturingTransport : ICommandTelemetryTransport
    {
        public string? Payload { get; private set; }
        public ValueTask<HttpStatusCode> SendAsync(string payload, TelemetryDestination destination, CancellationToken cancellationToken)
        {
            Payload = payload;
            return ValueTask.FromResult(HttpStatusCode.OK);
        }
    }

    private sealed class LegacyObserver : IAddressedAgentObserver
    {
        public bool IsObservationEnabled => true;
        public int TypeCalls { get; private set; }
        public void Observe(string agentType) => TypeCalls++;
    }

    private sealed class FailingActivationCacheFileSystem : ICopilotFileSystem
    {
        private readonly FakeCopilotFileSystem _inner = new();
        private int _writes;

        public string CurrentDirectory => _inner.CurrentDirectory;
        public string ResolveFullPath(string path) => _inner.ResolveFullPath(path);
        public bool IsEmptyDirectory(string path) => _inner.IsEmptyDirectory(path);
        public bool FileExists(string path) => _inner.FileExists(path);
        public string ReadAllText(string path) => _inner.ReadAllText(path);
        public string GetDirectoryName(string path) => _inner.GetDirectoryName(path);
        public string Combine(string left, string right) => _inner.Combine(left, right);

        public void WriteAllText(string path, string content)
        {
            if (++_writes == 2)
            {
                throw new IOException("Synthetic cache write failure.");
            }
            _inner.WriteAllText(path, content);
        }
    }

    private sealed class ThrowingObserver : IAddressedAgentObserver
    {
        public bool IsObservationEnabled => true;
        public void Observe(string agentType) => throw new InvalidOperationException();
        public void ObserveDeployment(DeploymentTelemetryObservation observation) => throw new InvalidOperationException();
    }
}
