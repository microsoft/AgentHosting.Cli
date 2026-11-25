using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Tests.Ams;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class AgentTypeTelemetryTests
{
    private static readonly string[] ParallelSchemaNames = ["con_one_probe", "con_two_probe"];

    [Theory]
    [InlineData(AgentTypes.McsAgent, false)]
    [InlineData(AgentTypes.McsAgent, true)]
    [InlineData(AgentTypes.McsWorkflow, false)]
    [InlineData(AgentTypes.McsWorkflow, true)]
    [InlineData(AgentTypes.CodeAgent, false)]
    [InlineData(AgentTypes.CodeAgent, true)]
    public async Task ResolvedAgentTypeEnrichesBothEventsWithoutIdentifiers(string agentType, bool explicitSelector)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        var agent = store.Add(project.Root, ManagedAgentFixtures.Agent(
            "telemetry-agent-sentinel", "Telemetry Private Agent") with
        {
            AgentType = agentType,
            McsSchemaName = agentType == AgentTypes.McsAgent ? "con_schema_probe" : null,
            McsWorkflowId = agentType == AgentTypes.McsWorkflow ? "11111111-2222-3333-4444-555555555555" : null,
        });
        var accessor = new CommandTelemetrySessionAccessor();
        var services = new ServiceCollection();
        services.AddSingleton(accessor);
        services.AddSingleton<IAddressedAgentObserver, CommandTelemetryAgentObserver>();
        services.AddSingleton<IManagedAgentRegistry>(store);
        services.AddSingleton<IAgentDeploymentRegistry>(store);
        services.AddSingleton<AgentConfigLocator>();
        services.AddSingleton<ManagedAgentShowService>();
        services.AddSingleton<ICommandHandler<ManagedAgentShowRequest, ManagedAgentShowResult>, ShowHandler>();
        using var provider = services.BuildServiceProvider();
        var transport = new CapturingTransport();
        var cli = new AgentHostCli(
            new FakeOperations(),
            Factory(transport),
            services: provider,
            featureFlags: TestFeatureFlags.AllProductSurfaces,
            telemetryAccessor: accessor);
        string[] args = explicitSelector
            ? ["agent", "show", "--project", project.Root, "--agent-id", agent.AgentId, "--json"]
            : ["agent", "show", "--project", project.Root, "--json"];

        int exitCode = await cli.InvokeAsync(args, TextWriter.Null, TextWriter.Null, color: false);

        Assert.Equal(0, exitCode);
        string payload = Assert.IsType<string>(transport.Payload);
        JsonElement[] events = Parse(payload);
        Assert.Equal(2, events.Length);
        Assert.All(events, item =>
        {
            Assert.Equal(agentType, Dimensions(item).GetProperty("agentType").GetString());
            Assert.False(Data(item).TryGetProperty("agentType", out _));
            Assert.False(Data(item).TryGetProperty("mcsAgentSchemaName", out _));
            Assert.False(Data(item).TryGetProperty("mcsWorkflowId", out _));
            Assert.False(Dimensions(item).TryGetProperty("mcsAgentId", out _));
            if (agentType == AgentTypes.McsWorkflow)
            {
                Assert.Equal(agent.McsWorkflowId, Dimensions(item).GetProperty("mcsWorkflowId").GetString());
            }
            if (agentType == AgentTypes.McsAgent)
            {
                Assert.Equal("con_schema_probe", Dimensions(item).GetProperty("mcsAgentSchemaName").GetString());
            }
            else
            {
                Assert.False(Dimensions(item).TryGetProperty("mcsAgentSchemaName", out _));
            }
        });
        Assert.DoesNotContain(agent.AgentId, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(agent.DisplayName, payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentTypes.McsAgent)]
    [InlineData(AgentTypes.McsWorkflow)]
    [InlineData(AgentTypes.CodeAgent)]
    public async Task NormalResolutionRetainsTypeWhenTheOperationLaterFails(string agentType)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        store.Add(project.Root, ManagedAgentFixtures.Agent() with
        {
            AgentType = agentType,
            McsSchemaName = "con_failure_probe",
        });
        var accessor = new CommandTelemetrySessionAccessor();
        var services = new ServiceCollection();
        services.AddSingleton(accessor);
        services.AddSingleton<IAddressedAgentObserver, CommandTelemetryAgentObserver>();
        services.AddSingleton<IManagedAgentRegistry>(store);
        services.AddSingleton<IAgentDeploymentRegistry>(store);
        services.AddSingleton<AgentConfigLocator>();
        services.AddSingleton<ManagedAgentResolver>();
        services.AddSingleton<ICommandHandler<ManagedAgentShowRequest, ManagedAgentShowResult>, FailingShowHandler>();
        using var provider = services.BuildServiceProvider();
        var transport = new CapturingTransport();
        var cli = new AgentHostCli(new FakeOperations(), Factory(transport),
            services: provider, telemetryAccessor: accessor);

        int exitCode = await cli.InvokeAsync(
            ["agent", "show", "--project", project.Root, "--json"],
            TextWriter.Null, TextWriter.Null, color: false);

        Assert.NotEqual(0, exitCode);
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(agentType, Dimensions(item).GetProperty("agentType").GetString());
            Assert.Equal(agentType == AgentTypes.McsAgent, Dimensions(item).TryGetProperty("mcsAgentSchemaName", out var schema));
            if (agentType == AgentTypes.McsAgent)
            {
                Assert.Equal("con_failure_probe", schema.GetString());
            }
        });
    }

    [Theory]
    [InlineData("show", "success")]
    [InlineData("update", "success")]
    [InlineData("delete", "success")]
    [InlineData("show", "wrong-type")]
    [InlineData("show", "wrong-tenant")]
    [InlineData("show", "after-read-failure")]
    [InlineData("list", "success")]
    public async Task ExplicitAmsOperationsObserveOnlyValidatedAgentTargets(string verb, string mode)
    {
        var agent = AmsBotServiceFixtures.CodeAgent(agentType: mode == "wrong-type" ? "CopilotStudio" : AgentTypes.CodeAgent) with
        {
            TenantId = mode == "wrong-tenant" ? EnvironmentFixtures.FabrikamTenant : EnvironmentFixtures.ContosoTenant,
        };
        var agents = new FakeAmsAgentTransport { Agents = [agent] };
        var deployments = new FakeAmsDeploymentTransport
        {
            ListFailure = mode == "after-read-failure" ? new HttpRequestException("Synthetic failure after validated read.") : null,
        };
        var accessor = new CommandTelemetrySessionAccessor();
        var services = new ServiceCollection();
        services.AddSingleton(accessor);
        services.AddSingleton<IAddressedAgentObserver, CommandTelemetryAgentObserver>();
        services.AddSingleton(AmsBotServiceFixtures.Resolver(agents, deployments));
        services.AddSingleton<IAmsAgentReadTransport>(agents);
        services.AddSingleton<IAmsAgentTransport>(agents);
        services.AddSingleton<IConfirmationPrompt>(new ScriptedConfirmationPrompt());
        services.AddSingleton<AmsAgentService>();
        services.AddSingleton<ICommandHandler<AgentAmsShowRequest, AgentAmsShowResult>>(sp => sp.GetRequiredService<AmsAgentService>());
        services.AddSingleton<ICommandHandler<AgentAmsUpdateRequest, AgentAmsUpdateResult>>(sp => sp.GetRequiredService<AmsAgentService>());
        services.AddSingleton<ICommandHandler<AgentAmsDeleteRequest, AgentAmsDeleteResult>>(sp => sp.GetRequiredService<AmsAgentService>());
        services.AddSingleton<ICommandHandler<AgentAmsListRequest, AgentAmsListResult>>(sp => sp.GetRequiredService<AmsAgentService>());
        using var provider = services.BuildServiceProvider();
        var transport = new CapturingTransport();
        var cli = new AgentHostCli(new FakeOperations(), Factory(transport),
            services: provider, featureFlags: TestFeatureFlags.Everything, telemetryAccessor: accessor);
        string[] args =
        [
            "agent", verb, "--environment-id", AmsBotServiceFixtures.HomeEnvironment, "--json",
            .. verb == "list" ? new[] { "--agent-type", AgentTypes.CodeAgent } : ["--agent-id", agent.AgentId],
            .. verb == "update" ? new[] { "--new-name", "Private renamed agent" } : Array.Empty<string>(),
        ];

        int exitCode = await cli.InvokeAsync(args, TextWriter.Null, TextWriter.Null, color: false);

        Assert.Equal(mode == "success", exitCode == 0);
        Assert.Contains(verb == "list" ? "list" : "get", agents.Calls);
        bool expectedType = verb != "list" && mode is not ("wrong-type" or "wrong-tenant");
        string payload = Assert.IsType<string>(transport.Payload);
        var events = Parse(payload);
        Assert.Equal(2, events.Length);
        Assert.All(events, item =>
        {
            var dimensions = Dimensions(item);
            Assert.Equal(expectedType, dimensions.TryGetProperty("agentType", out var type));
            if (expectedType)
            {
                Assert.Equal(AgentTypes.CodeAgent, type.GetString());
            }
            Assert.False(dimensions.TryGetProperty("mcsAgentSchemaName", out _));
            Assert.False(dimensions.TryGetProperty("mcsAgentId", out _));
            Assert.False(dimensions.TryGetProperty("mcsWorkflowId", out _));
        });
        Assert.DoesNotContain(agent.AgentId, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Private renamed agent", payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("agent create", true)]
    [InlineData("agent update", true)]
    [InlineData("agent pull", true)]
    [InlineData("agent deploy", true)]
    [InlineData("agent copy", true)]
    [InlineData("agent init", true)]
    [InlineData("agent share", true)]
    [InlineData("agent eval run", true)]
    [InlineData("deployment create", true)]
    [InlineData("deployment channel list", true)]
    [InlineData("test ask", true)]
    [InlineData("test playground", true)]
    [InlineData("agent list", false)]
    [InlineData("deployment list", false)]
    [InlineData("deployment oauth list-providers", false)]
    [InlineData("env list", false)]
    [InlineData("auth status", false)]
    public void MetadataDistinguishesAgentTargetsFromInventory(string path, bool expected)
    {
        var commands = CommandReflector.Discover(typeof(AgentGroup).Assembly, TestFeatureFlags.Everything)
            .SelectMany(group => Commands(group, group.Name))
            .ToDictionary(item => item.Path, item => item.Metadata);
        Assert.Equal(expected, commands[path].TargetsAgent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObserverFailuresDoNotChangeResolvedTargets(bool throwOnAvailability)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        var expected = store.Add(project.Root, ManagedAgentFixtures.Agent());
        var observer = new ThrowingObserver(throwOnAvailability);
        var services = new ServiceCollection();
        services.AddSingleton<IAddressedAgentObserver>(observer);
        services.AddSingleton<IManagedAgentRegistry>(store);
        services.AddSingleton<IAgentDeploymentRegistry>(store);
        services.AddSingleton<AgentConfigLocator>();
        services.AddSingleton<ManagedAgentResolver>();
        using var provider = services.BuildServiceProvider();

        var actual = provider.GetRequiredService<ManagedAgentResolver>().ResolveAgent(project.Root, null, null);

        Assert.Equal(expected.AgentId, actual.AgentId);
        Assert.True(observer.Calls > 0);
    }

    [Theory]
    [InlineData(AgentTypes.McsAgent)]
    [InlineData(AgentTypes.McsWorkflow)]
    public async Task TypedScaffoldsObserveTheirActualAgentType(string type)
    {
        using var project = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        var observer = new RecordingObserver();
        using var provider = new ServiceCollection()
            .AddSingleton<IAddressedAgentObserver>(observer).BuildServiceProvider();
        var environments = ManagedAgentFixtures.Resolver();
        var locator = new AgentConfigLocator();
        var home = new HomeDeploymentRegistrar(store, store);
        var principals = ManagedAgentFixtures.Principals();
        var request = new ManagedAgentCreateRequest
        {
            Name = "Telemetry Scaffold",
            Description = "Offline scaffold",
            InitialPrompt = "Offline instructions",
            Project = project.Root,
            AgentType = type,
        };
        ManagedAgentCreateResult result;
        if (type == AgentTypes.McsWorkflow)
        {
            var service = ActivatorUtilities.CreateInstance<WorkflowCreateService>(provider,
                environments, store, home, new FakeWorkflowStorePort(), new FakeWorkflowScaffoldPort(),
                locator, principals, new FakeWorkflowWorkspacePort());
            result = await service.CreateAsync(request, CancellationToken.None);
        }
        else
        {
            var workspaces = new FakeCopilotWorkspacePort();
            var naming = new SyncNamingRulesAdapter();
            var service = ActivatorUtilities.CreateInstance<ManagedAgentCreateService>(provider,
                new CopilotScaffoldService(new EmbeddedTemplateScaffolder(), new CopilotSchemaName(naming), new SystemCopilotFileSystem()),
                environments, new FakeCopilotCatalogPort(), store, home, ManagedAgentFixtures.Materialiser(workspaces),
                locator, ManagedAgentFixtures.Identities(), principals, workspaces, ManagedAgentFixtures.AgentNames(), naming);
            result = await service.CreateAsync(request, CancellationToken.None);
        }
        Assert.NotEmpty(observer.Types);
        Assert.All(observer.Types, observed => Assert.Equal(result.AgentType, observed));
        if (type == AgentTypes.McsAgent)
        {
            Assert.Equal(result.SchemaName, Assert.Single(observer.Schemas));
        }
        else
        {
            Assert.Empty(observer.Schemas);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeploymentDispatchObservesRecordsButNotItsCodeAgentFallback(bool configured)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        var observer = new RecordingObserver();
        var resolver = new DeploymentAgentTypeResolver(new AgentConfigLocator(), store, store, observer);
        if (configured)
        {
            store.Add(project.Root, ManagedAgentFixtures.Agent());
        }

        var fallback = resolver.Resolve(new DeploymentAgentTypeQuery
        {
            Project = project.Root,
            AgentId = "unresolved-agent-sentinel",
        });
        Assert.Equal(AgentTypes.CodeAgent, fallback.AgentType);
        Assert.Empty(observer.Types);

        if (configured)
        {
            var actual = resolver.Resolve(new DeploymentAgentTypeQuery { Project = project.Root });
            Assert.Equal(actual.AgentType, Assert.Single(observer.Types));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LiveTargetsObserveExplicitOrImplicitMcsAgents(bool explicitTarget)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var resolver = new LiveTestTargetResolver(store, store, new AgentConfigLocator(),
            ManagedAgentFixtures.Identities(), new CommandTelemetryAgentObserver(accessor));
        const string tenant = "11111111-1111-1111-1111-111111111111";
        LiveTestTargetRequest request;
        if (explicitTarget)
        {
            request = new LiveTestTargetRequest
            {
                SchemaName = "private_schema_sentinel", EnvironmentId = "environment-sentinel",
                TenantId = tenant, DeploymentName = "deployment-sentinel",
            };
        }
        else
        {
            store.Add(project.Root, ManagedAgentFixtures.Agent() with
            {
                McsSchemaName = "private_schema_sentinel",
            });
            store.Add(project.Root, ManagedAgentFixtures.Deployment() with { TenantId = tenant, IsDefault = true });
            request = new LiveTestTargetRequest { Project = project.Root };
        }
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["test", "playground"]), targetsAgent: true);
        var target = resolver.Resolve(request);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.Equal(explicitTarget ? LiveTestTargetSource.Explicit : LiveTestTargetSource.Configured, target.Source);
        string payload = Assert.IsType<string>(transport.Payload);
        Assert.All(Parse(payload), item =>
        {
            Assert.Equal(AgentTypes.McsAgent, Dimensions(item).GetProperty("agentType").GetString());
            Assert.Equal("private_schema_sentinel", Dimensions(item).GetProperty("mcsAgentSchemaName").GetString());
            Assert.Equal(target.EnvironmentId, Dimensions(item).GetProperty("environmentId").GetString());
        });
    }

    [Fact]
    public void DisabledInvocationDoesNotAcceptAgentType()
    {
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(CommandTelemetrySession.Disabled());
        var observer = new CommandTelemetryAgentObserver(accessor);
        Assert.False(observer.IsObservationEnabled);
        observer.Observe(AgentTypes.McsAgent);
        Assert.False(observer.IsObservationEnabled);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("schema")]
    [InlineData("default")]
    public async Task NonProjectEvaluationsEnrichBothEventsAfterCatalogResolution(string selector)
    {
        using var project = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        using var provider = new ServiceCollection()
            .AddSingleton<IAddressedAgentObserver>(new CommandTelemetryAgentObserver(accessor)).BuildServiceProvider();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = selector == "id" ? "private_bot_sentinel" : null };
        catalog.Agents.Add(new CopilotAgentSummary
        {
            AgentId = "private_bot_sentinel", SchemaName = "private_schema_sentinel", DisplayName = "Private Agent Sentinel",
        });
        var environments = ManagedAgentFixtures.Resolver(
            EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization) with
            {
                AgentManagementUrl = "https://powervamg.us-il301.gateway.prod.island.powerapps.com/",
            });
        var resolver = ActivatorUtilities.CreateInstance<EvaluationTargetResolver>(provider,
            environments, catalog, ManagedAgentFixtures.AgentResolver(store),
            new AgentConfigLocator(), ManagedAgentFixtures.Identities());
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "eval", "list"]), targetsAgent: true);
        await resolver.ResolveAsync(new EvaluationListRequest
        {
            Project = project.Root,
            AgentId = selector == "id" ? "private_bot_sentinel" : null,
            AgentName = selector switch
            {
                "name" => "Private Agent Sentinel",
                "schema" => "private_schema_sentinel",
                _ => null,
            },
        }, "agent eval list", CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Failure);
        await session.DisposeAsync();

        string payload = Assert.IsType<string>(transport.Payload);
        Assert.All(Parse(payload), item =>
        {
            Assert.Equal(AgentTypes.McsAgent, Dimensions(item).GetProperty("agentType").GetString());
            Assert.Equal(selector != "id", Dimensions(item).TryGetProperty("mcsAgentSchemaName", out var schema));
            if (selector != "id")
            {
                Assert.Equal("private_schema_sentinel", schema.GetString());
            }
        });
        Assert.DoesNotContain("private_bot_sentinel", payload, StringComparison.Ordinal);
        if (selector == "id")
        {
            Assert.DoesNotContain("private_schema_sentinel", payload, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("Private Agent Sentinel", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LateTypeEnrichmentSurvivesIdentityClearing()
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["test", "playground"]), targetsAgent: true);
        session.RecordOutcome(CommandTelemetryOutcome.Failure, errorCode: 19015);
        session.ObserveAgentType(AgentTypes.McsAgent, "con_late_probe");
        session.ObserveResources(AgentTypes.McsAgent, mcsAgentId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", environmentId: "env-1");
        session.ObserveProfileMutation(1);
        await session.DisposeAsync();

        var events = Parse(Assert.IsType<string>(transport.Payload));
        Assert.All(events, item =>
        {
            Assert.Equal(AgentTypes.McsAgent, Dimensions(item).GetProperty("agentType").GetString());
            Assert.Equal("con_late_probe", Dimensions(item).GetProperty("mcsAgentSchemaName").GetString());
            Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", Dimensions(item).GetProperty("mcsAgentId").GetString());
            Assert.Equal("env-1", Dimensions(item).GetProperty("environmentId").GetString());
        });
        Assert.Equal("19015", Dimensions(events[1]).GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData(AgentTypes.McsAgent, AgentTypes.McsAgent, true)]
    [InlineData(AgentTypes.McsAgent, AgentTypes.McsWorkflow, false)]
    [InlineData(AgentTypes.CodeAgent, "private-agent-sentinel", false)]
    public async Task RepeatedOrConflictingObservationsNeverGuessType(string first, string second, bool expected)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "show"]), targetsAgent: true);
        session.ObserveAgentType(first);
        session.ObserveAgentType(second);
        session.ObserveAgentType(first);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        string payload = Assert.IsType<string>(transport.Payload);
        Assert.All(Parse(payload), item =>
        {
            Assert.Equal(expected, Dimensions(item).TryGetProperty("agentType", out JsonElement type));
            if (expected)
            {
                Assert.Equal(first, type.GetString());
            }
        });
        Assert.DoesNotContain("private-agent-sentinel", payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostAndUntargetedRecordsOmitType(bool host)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        if (host)
        {
            session.RecordHost(CommandTelemetryHostCategory.Help);
        }
        else
        {
            session.RecordAttempt(CommandTelemetryPath.FromMetadata(["env", "list"]));
            session.RecordOutcome(CommandTelemetryOutcome.Success);
        }
        session.ObserveAgentType(AgentTypes.McsAgent, "con_untargeted_probe");
        session.ObserveResources(AgentTypes.McsAgent, mcsAgentId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", environmentId: "env-1");
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.False(Dimensions(item).TryGetProperty("agentType", out _));
            Assert.False(Dimensions(item).TryGetProperty("mcsAgentSchemaName", out _));
            Assert.False(Dimensions(item).TryGetProperty("mcsAgentId", out _));
            Assert.False(Dimensions(item).TryGetProperty("mcsWorkflowId", out _));
            Assert.False(Dimensions(item).TryGetProperty("environmentId", out _));
        });
    }

    [Fact]
    public async Task DisposedNestedScopeCannotAttributeLateWorkToItsParent()
    {
        var accessor = new CommandTelemetrySessionAccessor();
        var observer = new CommandTelemetryAgentObserver(accessor);
        var transport = new CapturingTransport();
        await using var outer = Session(transport);
        outer.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "show"]), targetsAgent: true);
        using var outerScope = accessor.Push(outer);
        observer.Observe(AgentTypes.McsAgent, "con_parent_probe");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerScope = accessor.Push(CommandTelemetrySession.Disabled());
        Task late = Task.Run(async () =>
        {
            await release.Task;
            _ = accessor.SessionId;
            Assert.False(observer.IsObservationEnabled);
            observer.Observe(AgentTypes.McsAgent, "con_child_probe");
            observer.ObserveResources(AgentTypes.McsAgent, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", null, "env-child");
        });
        innerScope.Dispose();
        release.SetResult();
        await late;
        outer.RecordOutcome(CommandTelemetryOutcome.Success);
        await outer.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(AgentTypes.McsAgent, Dimensions(item).GetProperty("agentType").GetString());
            Assert.Equal("con_parent_probe", Dimensions(item).GetProperty("mcsAgentSchemaName").GetString());
            Assert.False(Dimensions(item).TryGetProperty("mcsAgentId", out _));
            Assert.False(Dimensions(item).TryGetProperty("environmentId", out _));
        });
    }

    [Theory]
    [InlineData(null, "con_First", true)]
    [InlineData("con_First", null, true)]
    [InlineData("con_First", "CON_FIRST", true)]
    [InlineData("con_First", "con_Second", false)]
    [InlineData("con_First", "", false)]
    [InlineData("con_First", "bad schema", false)]
    [InlineData("", "con_First", false)]
    [InlineData("bad/schema", "con_First", false)]
    [InlineData(null, null, false)]
    public async Task SchemaObservationsPreserveFirstSpellingOrOmitStickyConflicts(
        string? first, string? second, bool expected)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "show"]), targetsAgent: true);
        session.ObserveAgentType(AgentTypes.McsAgent, first);
        session.ObserveAgentType(AgentTypes.McsAgent, second);
        session.ObserveAgentType(AgentTypes.McsAgent, first);
        session.RecordOutcome(CommandTelemetryOutcome.Cancellation);
        await session.DisposeAsync();

        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(AgentTypes.McsAgent, Dimensions(item).GetProperty("agentType").GetString());
            Assert.Equal(expected, Dimensions(item).TryGetProperty("mcsAgentSchemaName", out var schema));
            if (expected)
            {
                Assert.Equal(first ?? second, schema.GetString());
            }
            Assert.False(Data(item).TryGetProperty("mcsAgentSchemaName", out _));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchemaValidationUsesExistingSupportedCharactersAndMaximumLength(bool tooLong)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        string schemaName = "con_a-1.{x}!" + new string('a',
            new SyncNamingRulesAdapter().MaximumSchemaNameLength - "con_a-1.{x}!".Length + (tooLong ? 1 : 0));
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "show"]), targetsAgent: true);
        session.ObserveAgentType(AgentTypes.McsAgent, schemaName);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(!tooLong, Dimensions(item).TryGetProperty("mcsAgentSchemaName", out var schema));
            if (!tooLong)
            {
                Assert.Equal(schemaName, schema.GetString());
            }
        });
    }

    [Theory]
    [InlineData(AgentTypes.CodeAgent)]
    [InlineData(AgentTypes.McsWorkflow)]
    public async Task NonMcsAgentTypesNeverEmitSchema(string type)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "show"]), targetsAgent: true);
        session.ObserveAgentType(type, "con_ignored_probe");
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
            Assert.False(Dimensions(item).TryGetProperty("mcsAgentSchemaName", out _)));
    }

    [Fact]
    public async Task ConflictingAgentTypesAlsoSuppressKnownSchema()
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "show"]), targetsAgent: true);
        session.ObserveAgentType(AgentTypes.McsAgent, "con_known_probe");
        session.ObserveAgentType(AgentTypes.CodeAgent);
        session.ObserveAgentType(AgentTypes.McsAgent, "con_known_probe");
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.False(Dimensions(item).TryGetProperty("agentType", out _));
            Assert.False(Dimensions(item).TryGetProperty("mcsAgentSchemaName", out _));
        });
    }

    [Fact]
    public async Task ParallelMcsScopesKeepSchemasIndependent()
    {
        var accessor = new CommandTelemetrySessionAccessor();
        var observer = new CommandTelemetryAgentObserver(accessor);
        await Task.WhenAll(ParallelSchemaNames.Select(async schemaName =>
        {
            var transport = new CapturingTransport();
            await using var session = Session(transport);
            using var scope = accessor.Push(session);
            session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "show"]), targetsAgent: true);
            await Task.Yield();
            observer.Observe(AgentTypes.McsAgent, schemaName);
            session.RecordOutcome(CommandTelemetryOutcome.Success);
            await session.DisposeAsync();
            observer.Observe(AgentTypes.McsAgent, "con_late_probe");
            Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
                Assert.Equal(schemaName, Dimensions(item).GetProperty("mcsAgentSchemaName").GetString()));
        }));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("prepare")]
    [InlineData("catalog")]
    [InlineData("cancel")]
    public async Task CopyTelemetryUsesSourceSchemaEvenWhenPreparationFails(string failureStage)
    {
        bool fail = failureStage != "none";
        using var project = new TemporaryWorkspace(withProjectFile: false);
        project.AddFile(Path.Combine("Source Agent", AgentConfigLocator.ProjectSettingsFileName),
            "displayName: \"Source Agent\"\nschemaName: \"con_source_probe\"\n");
        var store = ManagedAgentFixtures.Store();
        store.Add(project.Root, ManagedAgentFixtures.Agent("source-agent", "Source Agent") with { AgentLocation = "Source Agent" });
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var copies = new FakeCopyPort
        {
            OnPrepare = failureStage == "prepare" ? () => throw new InvalidOperationException("Synthetic preparation failure.") : null,
        };
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var catalog = new FakeCopilotCatalogPort();
        if (failureStage is "catalog" or "cancel")
        {
            catalog.ListThrowsFor.Add("env-1", failureStage == "cancel"
                ? new OperationCanceledException("Synthetic catalog cancellation.")
                : new InvalidOperationException("Synthetic catalog failure."));
        }
        var service = new ManagedAgentCopyService(
            ManagedAgentFixtures.AgentResolver(store), store, new AgentConfigLocator(),
            ManagedAgentFixtures.Resolver(), catalog,
            ManagedAgentFixtures.Materialiser(workspaces),
            CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()),
            ManagedAgentFixtures.Identities(), new CopilotSchemaName(new SyncNamingRulesAdapter()),
            ManagedAgentFixtures.AgentNames(), copies, ManagedAgentFixtures.Principals(), workspaces,
            new FakeWorkflowStorePort(), new FakeWorkflowWorkspacePort(), new FakeWorkflowScaffoldPort(),
            new CommandTelemetryAgentObserver(accessor));
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "copy"]), targetsAgent: true);
        var request = new ManagedAgentCopyRequest { Project = project.Root, Name = "Copied Agent", SchemaName = "con_target_probe" };
        if (failureStage == "cancel")
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.CopyAsync(request, CancellationToken.None));
        }
        else if (fail)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CopyAsync(request, CancellationToken.None));
        }
        else
        {
            Assert.Equal("con_target_probe", (await service.CopyAsync(request, CancellationToken.None)).SchemaName);
        }
        session.RecordOutcome(failureStage == "cancel" ? CommandTelemetryOutcome.Cancellation
            : fail ? CommandTelemetryOutcome.Failure : CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        string payload = Assert.IsType<string>(transport.Payload);
        Assert.All(Parse(payload), item =>
            Assert.Equal("con_source_probe", Dimensions(item).GetProperty("mcsAgentSchemaName").GetString()));
        Assert.DoesNotContain("con_target_probe", payload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloneTelemetryUsesNormalCatalogSourceSchema(bool fail)
    {
        using var project = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var workspaces = new FakeCopilotWorkspacePort
        {
            CloneThrows = fail ? new IOException("Synthetic clone failure.") : null,
        };
        var naming = new SyncNamingRulesAdapter();
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-agent", SchemaNameReturns = "con_source_probe" };
        var service = new ManagedAgentCreateService(
            new CopilotScaffoldService(new EmbeddedTemplateScaffolder(), new CopilotSchemaName(naming), new SystemCopilotFileSystem()),
            ManagedAgentFixtures.Resolver(), catalog, store, new HomeDeploymentRegistrar(store, store),
            ManagedAgentFixtures.Materialiser(workspaces), new AgentConfigLocator(), ManagedAgentFixtures.Identities(),
            ManagedAgentFixtures.Principals(), workspaces, ManagedAgentFixtures.AgentNames(), naming,
            new CommandTelemetryAgentObserver(accessor));
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "create"]), targetsAgent: true);
        var request = new ManagedAgentCreateRequest { Project = project.Root, CloneAgentId = "cloud-agent", Name = "Cloned Agent" };
        if (fail)
        {
            await Assert.ThrowsAsync<Microsoft.Agents.Hosting.Cli.Core.Errors.ManagedAgentException>(
                () => service.CreateAsync(request, CancellationToken.None));
        }
        else
        {
            await service.CreateAsync(request, CancellationToken.None);
        }
        session.RecordOutcome(fail ? CommandTelemetryOutcome.Failure : CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.Equal(1, catalog.GetAgentCalls);
        Assert.Single(catalog.ResolvedValues);
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
            Assert.Equal("con_source_probe", Dimensions(item).GetProperty("mcsAgentSchemaName").GetString()));
    }

    [Fact]
    public async Task InitTelemetryCapturesExistingWorkspaceSchema()
    {
        using var project = new TemporaryWorkspace(withProjectFile: false);
        project.AddFile(AgentConfigLocator.ProjectSettingsFileName,
            "displayName: \"Existing Agent\"\nschemaName: \"con_existing_probe\"\n");
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var service = new ManagedAgentInitService(
            ManagedAgentFixtures.Store(), new AgentConfigLocator(),
            CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()),
            ManagedAgentFixtures.Resolver(), ManagedAgentFixtures.Identities(),
            new CommandTelemetryAgentObserver(accessor));
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "init"]), targetsAgent: true);
        await service.InitAsync(new ManagedAgentInitRequest { McsAgentSource = project.Root, Name = "Existing Agent" }, CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
            Assert.Equal("con_existing_probe", Dimensions(item).GetProperty("mcsAgentSchemaName").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectEvaluationsDistinguishKnownSchemaFromCloudIdFallback(bool knownSchema)
    {
        using var project = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        store.Add(project.Root, ManagedAgentFixtures.Agent() with
        {
            McsAgentId = "cloud-agent",
            McsSchemaName = knownSchema ? "con_selected_probe" : null,
        });
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "cloud-agent" };
        var environment = EnvironmentFixtures.Environment("env-1", instanceUrl: CopilotTestFixtures.DataverseOrganization) with
        {
            AgentManagementUrl = "https://powervamg.us-il301.gateway.prod.island.powerapps.com/",
        };
        var service = new EvaluationTargetResolver(
            ManagedAgentFixtures.Resolver(environment), catalog, ManagedAgentFixtures.AgentResolver(store),
            new AgentConfigLocator(), ManagedAgentFixtures.Identities(), new CommandTelemetryAgentObserver(accessor));
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "eval", "list"]), targetsAgent: true);
        await service.ResolveAsync(new EvaluationListRequest { Project = project.Root }, "agent eval list", CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.Equal(knownSchema ? "con_selected_probe" : "cloud-agent", Assert.Single(catalog.ResolvedValues));
        Assert.Equal(0, catalog.GetAgentCalls);
        Assert.Empty(catalog.RequestedMaximumCounts);
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(knownSchema, Dimensions(item).TryGetProperty("mcsAgentSchemaName", out var schema));
            if (knownSchema)
            {
                Assert.Equal("con_selected_probe", schema.GetString());
            }
        });
    }

    [Fact]
    public void SchemaAwareResolutionStillSupportsLegacyTypeOnlyObservers()
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        var agent = store.Add(project.Root, ManagedAgentFixtures.Agent() with { McsSchemaName = "con_legacy_probe" });
        var observer = new LegacyRecordingObserver();
        var resolver = new ManagedAgentResolver(store, store, new AgentConfigLocator(), observer);

        Assert.Equal(agent, resolver.ResolveAgent(project.Root, null, null));
        Assert.Equal(AgentTypes.McsAgent, Assert.Single(observer.Types));
    }

    [Fact]
    public async Task ParallelScopesKeepTypesIndependent()
    {
        var accessor = new CommandTelemetrySessionAccessor();
        var observer = new CommandTelemetryAgentObserver(accessor);
        await Task.WhenAll(new[] { AgentTypes.McsWorkflow, AgentTypes.CodeAgent }.Select(async type =>
        {
            var transport = new CapturingTransport();
            await using var session = Session(transport);
            using var scope = accessor.Push(session);
            session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "show"]), targetsAgent: true);
            await Task.Yield();
            observer.Observe(type);
            observer.ObserveResources(type, null, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", type);
            session.RecordOutcome(CommandTelemetryOutcome.Success);
            await session.DisposeAsync();
            Assert.False(observer.IsObservationEnabled);
            Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
            {
                Assert.Equal(type, Dimensions(item).GetProperty("agentType").GetString());
                Assert.Equal(type, Dimensions(item).GetProperty("environmentId").GetString());
                Assert.Equal(type == AgentTypes.McsWorkflow, Dimensions(item).TryGetProperty("mcsWorkflowId", out _));
            });
        }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResolvedEnvironmentEnrichesLeafEventsWithoutRequiringAgentOrTenant(bool tenantKnown)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["env", "show"]));
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        session.ObserveResolvedEnvironment(new ResolvedEnvironmentTarget(
            "Default-11111111-2222-3333-4444-555555555555",
            tenantKnown ? "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" : string.Empty));
        await session.DisposeAsync();

        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal("Default-11111111-2222-3333-4444-555555555555",
                Dimensions(item).GetProperty("environmentId").GetString());
            Assert.False(Data(item).TryGetProperty("environmentId", out _));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PullTelemetryUsesValidatedWorkspaceBotNotStoredBot(bool trusted)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        store.Add(project.Root, ManagedAgentFixtures.Agent() with { McsAgentId = "99999999-9999-9999-9999-999999999999" });
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var observer = new CommandTelemetryAgentObserver(accessor);
        var resolver = new ManagedAgentResolver(store, store, new AgentConfigLocator(), observer);
        var workspaces = new FakeCopilotWorkspacePort
        {
            AgentIdReturns = "11111111-2222-3333-4444-555555555555",
            DataverseOrganizationReturns = trusted ? CopilotTestFixtures.DataverseOrganization : "https://evil.example.com/",
        };
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());
        var service = new ManagedAgentPullService(resolver, locator,
            new CopilotWorkspaceService(workspaces, locator), ManagedAgentFixtures.Resolver());
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "pull"]), targetsAgent: true);
        if (trusted)
        {
            await service.PullAsync(new ManagedAgentPullRequest { Project = project.Root }, CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<Microsoft.Agents.Hosting.Cli.Core.Errors.CopilotStudioException>(
                () => service.PullAsync(new ManagedAgentPullRequest { Project = project.Root }, CancellationToken.None));
        }
        session.RecordOutcome(trusted ? CommandTelemetryOutcome.Success : CommandTelemetryOutcome.Failure);
        await session.DisposeAsync();

        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(trusted, Dimensions(item).TryGetProperty("mcsAgentId", out var id));
            if (trusted)
            {
                Assert.Equal(workspaces.AgentIdReturns, id.GetString());
                Assert.Equal("env-1", Dimensions(item).GetProperty("environmentId").GetString());
            }
            Assert.False(Data(item).TryGetProperty("mcsAgentId", out _));
        });
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task DeployTelemetryUsesTargetBotAndRetainsItOnPushFailure(bool attachTarget, bool fail, bool persistenceFailure)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        store.Add(project.Root, ManagedAgentFixtures.Agent() with { McsAgentId = "99999999-9999-9999-9999-999999999999" });
        store.Add(project.Root, ManagedAgentFixtures.Deployment());
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var observer = new CommandTelemetryAgentObserver(accessor);
        var resolver = new ManagedAgentResolver(store, store, new AgentConfigLocator(), observer);
        var workspaces = new FakeCopilotWorkspacePort
        {
            WorkspaceExists = !attachTarget,
            AgentIdReturns = "11111111-2222-3333-4444-555555555555",
            PushThrows = fail ? new IOException("Synthetic push failure.") : null,
        };
        var attach = new RecordingAttachPort
        {
            AgentId = workspaces.AgentIdReturns,
            OnAttach = () => workspaces.WorkspaceExists = true,
        };
        var catalog = new FakeCopilotCatalogPort();
        var environments = ManagedAgentFixtures.Resolver();
        var locator = CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem());
        IManagedAgentRegistry agents = persistenceFailure ? new RefusingAgentRegistry(store) : store;
        var service = new ManagedAgentDeployService(resolver, agents, store, environments,
            locator, new CopilotWorkspaceService(workspaces, locator), ManagedAgentFixtures.Identities(),
            attach, catalog, new CopilotCatalogService(catalog, environments, locator,
                new FixedClock(ManagedAgentFixtures.Now), (_, _) => Task.CompletedTask),
            ManagedAgentFixtures.Principals(), ManagedAgentFixtures.ActivationPass(),
            ManagedAgentFixtures.AgentNames(), new FixedClock(ManagedAgentFixtures.Now));
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
        var request = new ManagedAgentDeployRequest { Project = project.Root, DeploymentName = "dev" };
        if (fail || persistenceFailure)
        {
            await Assert.ThrowsAsync<Microsoft.Agents.Hosting.Cli.Core.Errors.ManagedAgentException>(
                () => service.DeployAsync(request, CancellationToken.None));
        }
        else
        {
            Assert.Equal(workspaces.AgentIdReturns,
                (await service.DeployAsync(request, CancellationToken.None)).TargetMcsAgentId);
        }
        session.RecordOutcome(fail || persistenceFailure ? CommandTelemetryOutcome.Failure : CommandTelemetryOutcome.Success);
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(workspaces.AgentIdReturns, Dimensions(item).GetProperty("mcsAgentId").GetString());
            Assert.Equal("env-1", Dimensions(item).GetProperty("environmentId").GetString());
            Assert.False(Dimensions(item).TryGetProperty("mcsWorkflowId", out _));
            string[] deployKeys = ["resourceAction", "deploymentType", "publishRequested", "publishStatus", "workflowState"];
            Assert.All(deployKeys, key => Assert.False(Data(item).TryGetProperty(key, out _)));
            if (Data(item).GetProperty("event_Name").GetString() == "CommandOutcome")
            {
                var dimensions = Dimensions(item);
                Assert.Equal("dev", dimensions.GetProperty("deploymentType").GetString());
                Assert.False(dimensions.GetProperty("publishRequested").GetBoolean());
                Assert.Equal("not-requested", dimensions.GetProperty("publishStatus").GetString());
                Assert.Equal(attachTarget || !fail, dimensions.TryGetProperty("resourceAction", out var action));
                if (attachTarget || !fail)
                {
                    Assert.Equal(attachTarget ? "created" : "updated-nochange", action.GetString());
                }
                Assert.False(dimensions.TryGetProperty("workflowState", out _));
            }
            else
            {
                Assert.All(deployKeys, key => Assert.False(Dimensions(item).TryGetProperty(key, out _)));
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteTelemetryUsesNormallyResolvedBotAndOmitsAbsentTarget(bool absent)
    {
        using var project = new TemporaryWorkspace();
        var store = ManagedAgentFixtures.Store();
        store.Add(project.Root, ManagedAgentFixtures.Agent() with { McsAgentId = "99999999-9999-9999-9999-999999999999" });
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var resolver = new ManagedAgentResolver(store, store, new AgentConfigLocator(), new CommandTelemetryAgentObserver(accessor));
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = absent ? null : "11111111-2222-3333-4444-555555555555" };
        var service = new ManagedAgentDeleteService(resolver, store,
            CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()), ManagedAgentFixtures.Resolver(),
            catalog, ManagedAgentFixtures.Identities(), new FakeAgentResourcePort(), new ScriptedConfirmationPrompt());
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "delete"]), targetsAgent: true);
        await service.DeleteAsync(new ManagedAgentDeleteRequest { Project = project.Root, NonInteractive = true }, CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();

        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(!absent, Dimensions(item).TryGetProperty("mcsAgentId", out var id));
            if (!absent)
            {
                Assert.Equal(catalog.ResolveReturns, id.GetString());
            }
        });
    }

    [Theory]
    [InlineData("scaffold")]
    [InlineData("clone")]
    [InlineData("init")]
    public async Task WorkflowTelemetryCapturesNormallyGeneratedOrReadWorkflowIds(string operation)
    {
        using var project = new TemporaryWorkspace(withProjectFile: false);
        var store = ManagedAgentFixtures.Store();
        var workflows = new FakeWorkflowStorePort();
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var observer = new CommandTelemetryAgentObserver(accessor);
        var locator = new AgentConfigLocator();
        var environments = ManagedAgentFixtures.Resolver();
        var principals = ManagedAgentFixtures.Principals();
        var workspace = new FakeWorkflowWorkspacePort();
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", operation == "init" ? "init" : "create"]), targetsAgent: true);
        string? expected;
        if (operation == "init")
        {
            WorkflowFixtures.Write(project.Root, "Workflow");
            var service = new WorkflowInitService(store, locator, environments, workflows, principals, workspace, observer);
            expected = (await service.InitAsync(new ManagedAgentInitRequest
            {
                Name = "Workflow", AgentType = AgentTypes.McsWorkflow, McsWorkflowSource = project.Root,
            }, CancellationToken.None)).McsWorkflowId;
        }
        else
        {
            string? cloneId = null;
            if (operation == "clone")
            {
                var id = Guid.NewGuid();
                cloneId = id.ToString();
                workflows.Cloud[id] = new WorkflowDocument { WorkflowId = id, Name = "Workflow", State = WorkflowStateProjection.Draft };
            }
            var service = new WorkflowCreateService(environments, store, new HomeDeploymentRegistrar(store, store),
                workflows, new FakeWorkflowScaffoldPort(), locator, principals, workspace, observer);
            expected = (await service.CreateAsync(new ManagedAgentCreateRequest
            {
                Name = "Workflow", AgentType = AgentTypes.McsWorkflow, Project = project.Root, CloneWorkflowId = cloneId,
            }, CancellationToken.None)).McsWorkflowId;
        }
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(expected, Dimensions(item).GetProperty("mcsWorkflowId").GetString());
            Assert.False(Dimensions(item).TryGetProperty("mcsAgentId", out _));
            Assert.False(Data(item).TryGetProperty("mcsWorkflowId", out _));
        });
    }

    [Theory]
    [InlineData(null, "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("invalid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", null)]
    [InlineData("00000000-0000-0000-0000-000000000000", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", null)]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "11111111-2222-3333-4444-555555555555", null)]
    [InlineData("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "", null)]
    public async Task CloudResourceIdsNormalizeAndOmitInvalidOrConflictingValues(string? first, string? second, string? expected)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
        session.ObserveResources(AgentTypes.McsAgent, mcsAgentId: first);
        session.RecordOutcome(CommandTelemetryOutcome.Failure);
        session.ObserveResources(AgentTypes.McsAgent, mcsAgentId: second);
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(expected is not null, Dimensions(item).TryGetProperty("mcsAgentId", out var id));
            if (expected is not null)
            {
                Assert.Equal(expected, id.GetString());
            }
        });
    }

    [Theory]
    [InlineData("env-1", "ENV-1", "env-1")]
    [InlineData("env-1", "env-2", null)]
    [InlineData("not an environment", "env-1", null)]
    [InlineData("env-1", "", null)]
    public async Task ResourceTypeConflictOmitsCloudIdsWithoutLosingIndependentEnvironment(string first, string second, string? expected)
    {
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "deploy"]), targetsAgent: true);
        session.ObserveResources(AgentTypes.McsAgent, mcsAgentId: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", environmentId: first);
        session.ObserveResources(AgentTypes.McsWorkflow, mcsWorkflowId: "11111111-2222-3333-4444-555555555555", environmentId: second);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            var dimensions = Dimensions(item);
            Assert.False(dimensions.TryGetProperty("agentType", out _));
            Assert.False(dimensions.TryGetProperty("mcsAgentId", out _));
            Assert.False(dimensions.TryGetProperty("mcsWorkflowId", out _));
            Assert.Equal(expected is not null, dimensions.TryGetProperty("environmentId", out var id));
            if (expected is not null)
            {
                Assert.Equal(expected, id.GetString());
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkflowCopyTelemetryKeepsOnlySourceIdEvenWhenPreparationCancels(bool cloudSource)
    {
        using var project = new TemporaryWorkspace(withProjectFile: false);
        var id = Guid.NewGuid();
        var store = ManagedAgentFixtures.Store();
        if (!cloudSource)
        {
            WorkflowFixtures.Write(project.Root, "Source", workflowId: id);
            store.Add(project.Root, WorkflowFixtures.Record(workflowId: id.ToString()));
        }
        var transport = new CapturingTransport();
        await using var session = Session(transport);
        var accessor = new CommandTelemetrySessionAccessor();
        using var scope = accessor.Push(session);
        var workspaces = new FakeCopilotWorkspacePort { WorkspaceExists = false };
        var copies = new FakeCopyPort { OnPrepare = () => throw new OperationCanceledException("Synthetic preparation cancellation.") };
        var workflows = new FakeWorkflowStorePort
        {
            DownloadThrows = new OperationCanceledException("Synthetic source-download cancellation."),
        };
        var service = new ManagedAgentCopyService(ManagedAgentFixtures.AgentResolver(store), store,
            new AgentConfigLocator(), ManagedAgentFixtures.Resolver(), new FakeCopilotCatalogPort(),
            ManagedAgentFixtures.Materialiser(workspaces), CopilotTestFixtures.Locator(workspaces, new SystemCopilotFileSystem()),
            ManagedAgentFixtures.Identities(), new CopilotSchemaName(new SyncNamingRulesAdapter()), ManagedAgentFixtures.AgentNames(),
            copies, ManagedAgentFixtures.Principals(), workspaces, workflows,
            new FakeWorkflowWorkspacePort(), new FakeWorkflowScaffoldPort(), new CommandTelemetryAgentObserver(accessor));
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["agent", "copy"]), targetsAgent: true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CopyAsync(new ManagedAgentCopyRequest
        {
            Project = project.Root, Name = "Copy", CloneWorkflowId = cloudSource ? id.ToString() : null,
        }, CancellationToken.None));
        session.RecordOutcome(CommandTelemetryOutcome.Cancellation);
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(id.ToString(), Dimensions(item).GetProperty("mcsWorkflowId").GetString());
            Assert.False(Dimensions(item).TryGetProperty("mcsAgentId", out _));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeploymentCloudDeleteTelemetryIncludesOnlyNormallyResolvedCloudTarget(bool deleteCloud)
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
        var catalog = new FakeCopilotCatalogPort { ResolveReturns = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" };
        var service = new AgentDeploymentDeleteService(resolver, store, store, ManagedAgentFixtures.Resolver(), catalog,
            ManagedAgentFixtures.Identities(), new ScriptedConfirmationPrompt(), new AgentConfigLocator());
        session.RecordAttempt(CommandTelemetryPath.FromMetadata(["deployment", "delete"]), targetsAgent: true);
        await service.DeleteAsync(new AgentDeploymentDeleteRequest
        {
            Project = project.Root, DeploymentName = "dev", DeleteDeployedMcsAgent = deleteCloud, NonInteractive = true,
        }, CancellationToken.None);
        session.RecordOutcome(CommandTelemetryOutcome.Success);
        await session.DisposeAsync();
        Assert.All(Parse(Assert.IsType<string>(transport.Payload)), item =>
        {
            Assert.Equal(deleteCloud, Dimensions(item).TryGetProperty("mcsAgentId", out var id));
            if (deleteCloud)
            {
                Assert.Equal(catalog.ResolveReturns, id.GetString());
            }
        });
        Assert.Equal(deleteCloud ? 1 : 0, catalog.DeleteCalls);
    }

    private static CommandTelemetrySession Session(CapturingTransport transport) =>
        new(new Resolver(), new FixedClock(ManagedAgentFixtures.Now), "1.2.3-test",
            transport: transport, flushTimeout: TelemetryTestBudgets.Flush);

    private static IEnumerable<(string Path, CommandMetadata Metadata)> Commands(CommandGroupMetadata group, string path) =>
        group.Commands.SelectMany(command => Commands(command, $"{path} {command.Name}"))
            .Concat(group.Groups.SelectMany(child => Commands(child, $"{path} {child.Name}")));

    private static IEnumerable<(string Path, CommandMetadata Metadata)> Commands(CommandMetadata command, string path) =>
        new[] { (path, command) }.Concat(command.Commands.SelectMany(child => Commands(child, $"{path} {child.Name}")));

    private static CommandTelemetrySessionFactory Factory(CapturingTransport transport) =>
        new(
            new TelemetryControlService(
                new PreferenceStore(),
                () => null,
                compositionFallbackEnabled: true,
                collectionActivationAvailable: true),
            new Resolver(),
            new FixedClock(ManagedAgentFixtures.Now),
            typeof(AgentHostCli).Assembly,
            flushTimeout: TelemetryTestBudgets.Flush,
            transport: transport);

    private static JsonElement[] Parse(string payload) =>
        payload.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static JsonElement Data(JsonElement item) => item.GetProperty("data");

    private static JsonElement Dimensions(JsonElement item) =>
        JsonDocument.Parse(Data(item).GetProperty("customDimensions").GetString()!).RootElement.Clone();

    private sealed class ShowHandler(ManagedAgentShowService service) :
        ICommandHandler<ManagedAgentShowRequest, ManagedAgentShowResult>
    {
        public Task<ManagedAgentShowResult> HandleAsync(ManagedAgentShowRequest request, CancellationToken cancellationToken) =>
            service.ShowAsync(request, cancellationToken);
    }

    private sealed class FailingShowHandler(ManagedAgentResolver resolver) :
        ICommandHandler<ManagedAgentShowRequest, ManagedAgentShowResult>
    {
        public Task<ManagedAgentShowResult> HandleAsync(ManagedAgentShowRequest request, CancellationToken cancellationToken)
        {
            resolver.ResolveAgent(resolver.ResolveProjectDirectory(request.Project), request.AgentId, request.AgentName);
            throw new InvalidOperationException("Synthetic failure after target resolution.");
        }
    }

    private sealed class ThrowingObserver(bool throwOnAvailability) : IAddressedAgentObserver
    {
        public int Calls { get; private set; }
        public bool IsObservationEnabled
        {
            get
            {
                Calls++;
                return throwOnAvailability ? throw new InvalidOperationException("Synthetic availability failure.") : true;
            }
        }
        public void Observe(string agentType) => throw new InvalidOperationException("Synthetic callback failure.");
    }

    private sealed class RecordingObserver : IAddressedAgentObserver
    {
        public List<string> Types { get; } = [];
        public List<string> Schemas { get; } = [];
        public bool IsObservationEnabled => true;
        public void Observe(string agentType) => Types.Add(agentType);
        public void Observe(string agentType, string? mcsAgentSchemaName)
        {
            Observe(agentType);
            if (mcsAgentSchemaName is not null)
            {
                Schemas.Add(mcsAgentSchemaName);
            }
        }

    }

    private sealed class LegacyRecordingObserver : IAddressedAgentObserver
    {
        public List<string> Types { get; } = [];
        public bool IsObservationEnabled => true;
        public void Observe(string agentType) => Types.Add(agentType);
    }

    private sealed class PreferenceStore : ITelemetryPreferenceStore
    {
        public ValueTask<TelemetryRememberedPreference> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(TelemetryRememberedPreference.Enabled);

        public ValueTask WriteAsync(bool enabled, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class Resolver : ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        public ValueTask<TelemetryDestination?> ResolveAsync(TelemetryEnvironmentTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult<TelemetryDestination?>(
                new TelemetryDestination(
                    new Uri("http://127.0.0.1:1/OneCollector/1.0/"),
                    TelemetryIngestionConfiguration.ProductionKey,
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
}
