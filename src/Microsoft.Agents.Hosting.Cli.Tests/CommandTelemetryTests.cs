using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli.Tests;

public sealed class CommandTelemetryTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 22, 19, 0, 0, TimeSpan.Zero);

    public static TheoryData<string, string[]> CanonicalLeafCases => new()
    {
        { "abs preview", ["abs", "preview"] },
        { "abs apply", ["abs", "apply"] },
        { "agent create", ["agent", "create", "--agent-type", "MCSAgent", "--agent-name", "Contoso Agent"] },
        { "agent init", ["agent", "init", "--agent-name", "Contoso Agent", "--mcs-agent-source", "../other"] },
        { "agent list", ["agent", "list", "--environment-id", "env-2", "--limit", "5"] },
        { "agent show", ["agent", "show"] },
        { "agent pull", ["agent", "pull", "--project", "./here", "--agent-id", "agent-1"] },
        { "agent delete", ["agent", "delete", "--agent-id", "agent-1", "--non-interactive"] },
        { "agent deploy", ["agent", "deploy", "--deployment-name", "dev"] },
        {
            "agent package upload",
            [
                "agent", "package", "upload",
                "--agent-id", "44444444-4444-4444-4444-444444444444",
                "--package", @".\agent.zip",
                "--environment-id", "22222222-2222-2222-2222-222222222222",
            ]
        },
        {
            "agent share",
            [
                "agent", "share", "--agent-id", "agent-1",
                "--user", "ada@contoso.com", "--role", "use",
            ]
        },
        { "agent share show", ["agent", "share", "show", "--agent-id", "agent-1"] },
        {
            "agent unshare",
            ["agent", "unshare", "--agent-id", "agent-1", "--user", "ada@contoso.com"]
        },
        { "auth login", ["auth", "login"] },
        { "auth status", ["auth", "status"] },
        { "auth switch", ["auth", "switch", "--account", "operator"] },
        { "auth logout", ["auth", "logout"] },
        {
            "deployment create",
            [
                "deployment", "create", "--agent-id", "agent-1",
                "--deployment-type", "dev", "--deployment-name", "dev",
            ]
        },
        { "deployment list", ["deployment", "list", "--agent-id", "agent-1"] },
        {
            "deployment show",
            ["deployment", "show", "--agent-id", "agent-1", "--deployment-name", "dev"]
        },
        {
            "deployment connection list",
            [
                "deployment", "connection", "list",
                "--agent-id", "agent-1", "--deployment-name", "dev",
            ]
        },
        {
            "deployment update",
            [
                "deployment", "update", "--agent-id", "agent-1",
                "--deployment-name", "dev", "--new-name", "Shared development",
            ]
        },
        {
            "deployment connection update",
            [
                "deployment", "connection", "update",
                "--agent-id", "agent-1", "--deployment-name", "dev",
            ]
        },
        {
            "deployment delete",
            ["deployment", "delete", "--agent-id", "agent-1", "--deployment-name", "dev"]
        },
        { "env list", ["env", "list"] },
        { "env select", ["env", "select", "env-42"] },
        {
            "identity create blueprint",
            [
                "identity", "create", "blueprint",
                "--tenant", "11111111-1111-1111-1111-111111111111",
                "--display-name", "Contoso Agent",
                "--auth-type", "ClientSecret",
                "--config-format", "dotnet",
            ]
        },
        {
            "identity create app",
            [
                "identity", "create", "app",
                "--tenant", "11111111-1111-1111-1111-111111111111",
                "--display-name", "Contoso Agent",
                "--auth-type", "ClientSecret",
                "--config-format", "dotnet",
            ]
        },
    };

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public async Task ExplicitRootHelpSendsOneCompatibleHostCase(string helpToken)
    {
        var baseline = await InvokeAsync([helpToken]);
        using var collector = new OneShotCollector();
        var resolver = new FixedDestinationResolver(Destination(collector.Endpoint));
        var receiving = collector.ReceiveAsync();

        var actual = await InvokeAsync([helpToken], Factory(resolver));
        JsonElement item = Assert.Single(Parse((await receiving).Body));

        Assert.Equal(baseline, actual);
        Assert.Equal(ExitCodes.Success, actual.ExitCode);
        Assert.Equal("event", item.GetProperty("name").GetString());
        Assert.Equal(Timestamp, item.GetProperty("time").GetDateTimeOffset());
        Assert.Equal(
            0x100000,
            item.GetProperty("ext").GetProperty("utc").GetProperty("eventFlags").GetInt32());

        JsonElement data = item.GetProperty("data");
        using var dimensions =
            JsonDocument.Parse(data.GetProperty("customDimensions").GetString()!);
        Assert.Equal("powerappsclient", data.GetProperty("app_Name").GetString());
        Assert.Equal("AgentHostingCLI", data.GetProperty("clientType").GetString());
        Assert.Equal("prod", data.GetProperty("clusterCategory").GetString());
        Assert.Equal("browser", data.GetProperty("device_Id").GetString());
        Assert.Equal("HostCase", data.GetProperty("event_Name").GetString());
        Assert.Equal(1, data.GetProperty("sequenceNumber").GetInt32());
        Assert.True(Guid.TryParse(data.GetProperty("session_Id").GetString(), out _));
        Assert.Equal("info", data.GetProperty("severity").GetString());
        Assert.Equal(Timestamp, data.GetProperty("timestamp").GetDateTimeOffset());
        Assert.Equal("AgentHostingCLI.Host", data.GetProperty("operation_Name").GetString());
        Assert.Equal(
            "AgentHostingCLI.Host",
            dimensions.RootElement.GetProperty("operationName").GetString());
        Assert.Equal("false", dimensions.RootElement.GetProperty("isOnePlayer").GetString());
        Assert.Equal(
            "AgentHosting_CLI_Authoring",
            dimensions.RootElement.GetProperty("webPlayerScenario").GetString());
        Assert.Equal("prod", dimensions.RootElement.GetProperty("cloud").GetString());
        Assert.Equal("Unknown", dimensions.RootElement.GetProperty("station").GetString());
        Assert.Equal("eu", data.GetProperty("telemetryLocation").GetString());
        Assert.Equal(
            "eu",
            dimensions.RootElement.GetProperty("telemetryLocation").GetString());
        Assert.Equal(
            typeof(AgentHostCli).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion,
            dimensions.RootElement.GetProperty("cliVersion").GetString());
        Assert.Equal(
            "help",
            dimensions.RootElement.GetProperty("hostCategory").GetString());
        Assert.Equal(
            "success",
            dimensions.RootElement.GetProperty("exitClass").GetString());
        Assert.False(data.TryGetProperty("identityMode", out _));
        Assert.False(data.TryGetProperty("tenantId", out _));
        Assert.False(data.TryGetProperty("objectId", out _));
        Assert.False(data.TryGetProperty("pptoolsDeviceId", out _));
        Assert.False(dimensions.RootElement.TryGetProperty("tenantId", out _));
        Assert.False(data.TryGetProperty("probeId", out _));
        Assert.Equal(
            [
                "app_Name",
                "clientType",
                "clusterCategory",
                "customDimensions",
                "device_Id",
                "event_Name",
                "operation_Name",
                "sequenceNumber",
                "session_Id",
                "severity",
                "telemetryLocation",
                "timestamp",
            ],
            data.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.Equal(1, resolver.ResolveCalls);
        Assert.False(collector.HasPendingRequest);
    }

    [Theory]
    [InlineData("", "help")]
    [InlineData("--version", "version")]
    [InlineData("-v", "version")]
    [InlineData("not-a-command", "parser-rejection")]
    [InlineData("unknown-command hostile-secret", "parser-rejection")]
    [InlineData("abs", "group-usage")]
    [InlineData("agent", "group-usage")]
    [InlineData("agent package", "group-usage")]
    [InlineData("auth", "group-usage")]
    [InlineData("deployment", "group-usage")]
    [InlineData("env", "group-usage")]
    [InlineData("identity", "group-usage")]
    [InlineData("identity create", "group-usage")]
    [InlineData("auth --help", "help")]
    [InlineData("identity create blueprint --help", "help")]
    [InlineData("--help --version", "parser-rejection")]
    [InlineData("--version --help", "help")]
    public async Task FullHostLifecycleClassifiesOneHostCase(
        string commandLine,
        string expectedCategory)
    {
        string[] args = string.IsNullOrEmpty(commandLine)
            ? []
            : commandLine.Split(' ');
        using var collector = new OneShotCollector();
        var receiving = collector.ReceiveAsync();

        await InvokeAsync(
            args,
            Factory(new FixedDestinationResolver(Destination(collector.Endpoint))));

        JsonElement item = Assert.Single(Parse((await receiving).Body));
        JsonElement data = item.GetProperty("data");
        using var dimensions =
            JsonDocument.Parse(data.GetProperty("customDimensions").GetString()!);
        Assert.Equal("HostCase", data.GetProperty("event_Name").GetString());
        Assert.Equal(
            expectedCategory,
            dimensions.RootElement.GetProperty("hostCategory").GetString());
        Assert.Equal(
            expectedCategory is "help" or "version" ? "success" : "usage",
            dimensions.RootElement.GetProperty("exitClass").GetString());
        Assert.False(collector.HasPendingRequest);
    }

    [Theory]
    [InlineData("--help", "--version", "parser-rejection")]
    [InlineData("--version", "--help", "help")]
    public async Task MixedHelpAndVersionReflectsSelectedParserAction(
        string first,
        string second,
        string expectedCategory)
    {
        var help = await InvokeAsync(["--help"]);
        var version = await InvokeAsync(["--version"]);
        var baseline = await InvokeAsync([first, second]);
        var transport = new CapturingTransport();
        var observed = await InvokeAsync(
            [first, second],
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                transport: transport));
        JsonElement item = Assert.Single(
            Parse(Assert.IsType<string>(transport.Payload)));

        Assert.Equal(baseline, observed);
        if (expectedCategory == "help")
        {
            Assert.Equal(help, baseline);
            Assert.Equal(ExitCodes.Success, baseline.ExitCode);
            Assert.Empty(baseline.Stderr);
            Assert.Contains("Usage:", baseline.Stdout, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEqual(help, baseline);
            Assert.NotEqual(version, baseline);
            Assert.NotEqual(ExitCodes.Success, baseline.ExitCode);
            Assert.NotEmpty(baseline.Stderr);
        }

        Assert.Equal(
            expectedCategory,
            Dimensions(item).GetProperty("hostCategory").GetString());
    }

    [Fact]
    public async Task NormalCommandExecutesFacadeAndBatchesAttemptWithOutcome()
    {
        int calls = 0;
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                calls++;
                return new AuthStatusReport
                {
                    Status = "signed-out",
                    SignedIn = false,
                };
            },
        };
        using var collector = new OneShotCollector();
        var receiving = collector.ReceiveAsync();
        var controls = EnabledControls();
        await using ServiceProvider services = BuildServices(controls);

        var actual = await InvokeAsync(
            ["auth", "status"],
            Factory(
                new FixedDestinationResolver(Destination(collector.Endpoint)),
                controls),
            operations,
            services);
        JsonElement[] events = Parse((await receiving).Body);

        Assert.Equal(ExitCodes.Success, actual.ExitCode);
        Assert.Equal(1, calls);
        Assert.Equal(2, events.Length);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(item =>
                item.GetProperty("data").GetProperty("event_Name").GetString()));
        Assert.Equal(
            [1, 2],
            events.Select(item =>
                item.GetProperty("data").GetProperty("sequenceNumber").GetInt32()));
        Assert.False(collector.HasPendingRequest);
    }

    [Theory]
    [MemberData(nameof(CanonicalLeafCases))]
    public async Task EveryCanonicalLeafRecordsItsResolvedMetadataPath(
        string expectedPath,
        string[] args)
    {
        var transport = new CapturingTransport();

        var actual = await InvokeAsync(
            args,
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                transport: transport),
            McsOperations());
        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));

        Assert.Equal(ExitCodes.Success, actual.ExitCode);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(item =>
                item.GetProperty("data").GetProperty("event_Name").GetString()));
        Assert.All(events, item => Assert.Equal(
            "AgentHostingCLI." + expectedPath.Replace(' ', '.'),
            item.GetProperty("data").GetProperty("operation_Name").GetString()));
        Assert.Equal(
            "success",
            Dimensions(events[1]).GetProperty("outcomeCategory").GetString());
    }

    public static TheoryData<string, string[]> OptionLookingArgumentCases => new()
    {
        { "--help", ["env", "select", "--", "--help"] },
        { "--version", ["env", "select", "--", "--version"] },
        { "-h", ["env", "select", "--", "-h"] },
        { "-v", ["env", "select", "--", "-v"] },
        { "--version", ["env", "select", "--version"] },
        { "--help", ["auth", "switch", "--account", "--help"] },
        { "--version", ["auth", "switch", "--account", "--version"] },
        { "--help", ["auth", "switch", "--account=--help"] },
        { "--version", ["auth", "switch", "--account=--version"] },
        { "--help", ["auth", "switch", "--account:--help"] },
        { "--version", ["auth", "switch", "--account:--version"] },
    };

    [Theory]
    [MemberData(nameof(OptionLookingArgumentCases))]
    public async Task OptionLookingArgumentValuesRemainLeafDispatches(
        string expectedValue,
        string[] args)
    {
        var operations = new FakeOperations();
        var transport = new CapturingTransport();

        var actual = await InvokeAsync(
            args,
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                transport: transport),
            operations);
        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));
        string expectedPath = args[0] == "env" ? "env.select" : "auth.switch";

        Assert.Equal(ExitCodes.Success, actual.ExitCode);
        Assert.Equal(
            expectedValue,
            args[0] == "env"
                ? operations.LastSelectedEnvironmentId
                : operations.LastSwitchAccount);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(item =>
                item.GetProperty("data").GetProperty("event_Name").GetString()));
        Assert.All(events, item => Assert.Equal(
            "AgentHostingCLI." + expectedPath,
            item.GetProperty("data").GetProperty("operation_Name").GetString()));
    }

    [Theory]
    [InlineData("auth status", "auth", "status")]
    [InlineData("auth switch", "auth", "switch", "-acc", "operator")]
    [InlineData("agent create", "agent", "create", "-at", "MCSAgent", "-an", "Contoso Agent")]
    [InlineData("agent share", "agent", "share", "-aid", "agent-1", "-u", "ada@contoso.com", "-rl", "use")]
    [InlineData("deployment connection list", "deployment", "connection", "list", "-aid", "agent-1", "-dn", "dev")]
    [InlineData("deployment connection update", "deployment", "connection", "update", "-aid", "agent-1", "-dn", "dev")]
    public async Task AliasesPreserveCanonicalMetadataPath(
        string expectedPath,
        params string[] args)
    {
        var transport = new CapturingTransport();

        var actual = await InvokeAsync(
            args,
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                transport: transport),
            McsOperations());
        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));

        Assert.Equal(ExitCodes.Success, actual.ExitCode);
        Assert.All(events, item => Assert.Equal(
            "AgentHostingCLI." + expectedPath.Replace(' ', '.'),
            item.GetProperty("data").GetProperty("operation_Name").GetString()));
    }

    [Fact]
    public async Task DeploymentPathIsStableAcrossMcsAndCodeAgentRouting()
    {
        var mcsTransport = new CapturingTransport();
        var codeAgentTransport = new CapturingTransport();

        var mcs = await InvokeAsync(
            ["deployment", "list", "--agent-id", "agent-1"],
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                transport: mcsTransport),
            McsOperations());
        var codeAgent = await InvokeAsync(
            ["deployment", "list", "--agent-id", "agent-1"],
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                transport: codeAgentTransport),
            CodeAgentOperations());

        Assert.Equal(ExitCodes.Success, mcs.ExitCode);
        Assert.Equal(ExitCodes.Success, codeAgent.ExitCode);
        Assert.All(
            Parse(Assert.IsType<string>(mcsTransport.Payload))
                .Concat(Parse(Assert.IsType<string>(codeAgentTransport.Payload))),
            item => Assert.Equal(
                "AgentHostingCLI.deployment.list",
                item.GetProperty("data").GetProperty("operation_Name").GetString()));
    }

    [Fact]
    public async Task NestedParentOptionUsageRecordsAttemptAndRenderedOutcome()
    {
        var transport = new CapturingTransport();

        var actual = await InvokeAsync(
            [
                "agent", "share", "--role", "use", "show",
                "--agent-id", "agent-1",
            ],
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                transport: transport),
            McsOperations());
        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));

        Assert.Equal(ExitCodes.Usage, actual.ExitCode);
        Assert.Contains(
            "--role does not apply to `msagent agent share show`",
            actual.Stderr,
            StringComparison.Ordinal);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(item =>
                item.GetProperty("data").GetProperty("event_Name").GetString()));
        Assert.Equal(
            "usage",
            Dimensions(events[1]).GetProperty("outcomeCategory").GetString());
    }

    [Fact]
    public async Task InputSetupFailureRetainsAcceptedAttemptAndBoundedFailure()
    {
        int operationCalls = 0;
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                operationCalls++;
                return new AuthStatusReport
                {
                    Status = "signed-out",
                    SignedIn = false,
                };
            },
        };
        var transport = new CapturingTransport();
        var resolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        SystemCommandLineInputFactory inputFactory = (_, _, _, _) =>
            throw new InvalidOperationException("synthetic input setup failure");
        var cli = new AgentHostCli(
            operations,
            services: null,
            featureFlags: TestFeatureFlags.AllProductSurfaces,
            telemetryFactory: Factory(resolver, transport: transport),
            telemetryAccessor: null,
            inputFactory: inputFactory);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int exitCode = await cli.InvokeAsync(
            ["auth", "status"],
            stdout,
            stderr,
            color: false);
        string payload = Assert.IsType<string>(transport.Payload);
        JsonElement[] events = Parse(payload);

        Assert.Equal(ExitCodes.Failure, exitCode);
        Assert.Equal(0, operationCalls);
        Assert.Equal(TelemetryEnvironmentTarget.Unknown, resolver.LastTarget);
        Assert.Equal(
            ["CommandAttempt", "CommandOutcome"],
            events.Select(item =>
                item.GetProperty("data").GetProperty("event_Name").GetString()));
        Assert.All(events, item => Assert.Equal(
            "AgentHostingCLI.auth.status",
            item.GetProperty("data").GetProperty("operation_Name").GetString()));
        Assert.Equal(
            "failure",
            Dimensions(events[1]).GetProperty("outcomeCategory").GetString());
        Assert.DoesNotContain("synthetic", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task EnabledTelemetryPreservesHumanAndJsonOutput(
        bool json,
        bool commandFails)
    {
        string[] args = json
            ? ["auth", "status", "--json"]
            : ["auth", "status"];
        FakeOperations Operations() => commandFails
            ? new FakeOperations
            {
                OnStatus = () =>
                    throw new InvalidOperationException("synthetic command failure"),
            }
            : new FakeOperations();

        var baseline = await InvokeAsync(args, operations: Operations());
        var transport = new CapturingTransport();
        var observed = await InvokeAsync(
            args,
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                transport: transport),
            Operations());

        Assert.Equal(baseline, observed);
        Assert.NotNull(transport.Payload);
    }

    [Fact]
    public async Task TypedOperationFailurePreservesJsonAndRecordsRenderedOutcome()
    {
        FakeOperations Operations() => new()
        {
            OnPreview = _ => throw new ProvisioningException(
                ProvisioningErrorKind.MissingContext,
                "synthetic typed detail",
                "synthetic remediation"),
        };
        string[] args = ["abs", "preview", "--json"];
        var baseline = await InvokeAsync(args, operations: Operations());
        var transport = new CapturingTransport();

        var observed = await InvokeAsync(
            args,
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                transport: transport),
            Operations());
        JsonElement[] events = Parse(Assert.IsType<string>(transport.Payload));

        Assert.Equal(baseline, observed);
        Assert.Equal(ExitCodes.Usage, observed.ExitCode);
        Assert.Equal(
            "usage",
            Dimensions(events[1]).GetProperty("outcomeCategory").GetString());
        Assert.Equal(
            "usage",
            Dimensions(events[1]).GetProperty("exitClass").GetString());
        Assert.DoesNotContain(
            "synthetic",
            Assert.IsType<string>(transport.Payload),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rejected-transport", true)]
    [InlineData("clock", false)]
    [InlineData("completion", false)]
    public async Task HostTelemetryFaultsPreserveCommandOutputAndExit(
        string fault,
        bool commandFails)
    {
        FakeOperations Operations() => commandFails
            ? new FakeOperations
            {
                OnStatus = () =>
                    throw new InvalidOperationException("synthetic command failure"),
            }
            : new FakeOperations();
        string[] args = ["auth", "status", "--json"];
        var baseline = await InvokeAsync(args, operations: Operations());
        IClock clock = fault == "clock"
            ? new ThrowingClock()
            : new FixedClock();
        ICommandTelemetryTransport transport = fault switch
        {
            "rejected-transport" =>
                new FixedStatusTransport(HttpStatusCode.BadRequest),
            "completion" => new ThrowingTransport(),
            _ => new CountingTransport(),
        };

        var observed = await InvokeAsync(
            args,
            Factory(
                new FixedDestinationResolver(
                    Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                clock: clock,
                transport: transport),
            Operations());

        Assert.Equal(baseline, observed);
    }

    public static TheoryData<string[], bool> HostRenderingFailureCases => new()
    {
        { ["--help"], true },
        { ["--version"], true },
        { ["-v"], true },
        { ["not-a-command"], false },
        { ["auth"], false },
    };

    [Theory]
    [MemberData(nameof(HostRenderingFailureCases))]
    public async Task FailedHostRenderingDoesNotRecordAHostCase(
        string[] args,
        bool failStdout)
    {
        var resolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var transport = new CapturingTransport();
        var writer = new ThrowingWriter(
            new IOException("synthetic rendering failure"));
        TextWriter stdout = failStdout ? writer : new StringWriter();
        TextWriter stderr = failStdout ? new StringWriter() : writer;
        var cli = new AgentHostCli(
            new FakeOperations(),
            featureFlags: TestFeatureFlags.AllProductSurfaces,
            telemetryFactory: Factory(resolver, transport: transport));

        await Assert.ThrowsAsync<IOException>(() =>
            cli.InvokeAsync(args, stdout, stderr, color: false));

        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Null(transport.Payload);
    }

    [Fact]
    public async Task CancelledHelpRenderingDoesNotRecordAHostCase()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var transport = new CapturingTransport();
        var cli = new AgentHostCli(
            new FakeOperations(),
            featureFlags: TestFeatureFlags.AllProductSurfaces,
            telemetryFactory: Factory(resolver, transport: transport));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cli.InvokeAsync(
                ["--help"],
                new ThrowingWriter(new OperationCanceledException(cancellation.Token)),
                new StringWriter(),
                color: false));

        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Null(transport.Payload);
    }

    [Fact]
    public async Task TransportFailureDoesNotChangeHelpOutputOrExit()
    {
        var baseline = await InvokeAsync(["--help"]);
        var resolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));

        var actual = await InvokeAsync(["--help"], Factory(resolver));

        Assert.Equal(baseline, actual);
        Assert.Equal(1, resolver.ResolveCalls);
    }

    [Fact]
    public async Task SynchronousResolutionDeadlinePreservesHelpAndPreventsLateSend()
    {
        var baseline = await InvokeAsync(["--help"]);
        using var listener = new DiagnosticEventListener(
            OptionalMeasurementFailureCategory.TelemetryDeadline);
        var resolver = new SynchronousBlockingResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var transport = new CountingTransport();
        Task<(int ExitCode, string Stdout, string Stderr)> invocation =
            InvokeAsync(
                ["--help"],
                Factory(
                    resolver,
                    flushTimeout: TimeSpan.FromMilliseconds(50),
                    transport: transport));

        try
        {
            await resolver.Started.WaitAsync(TimeSpan.FromSeconds(2));
            var actual = await invocation.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(baseline, actual);
            Assert.True(resolver.Token.IsCancellationRequested);
            EventWrittenEventArgs diagnostic = await listener.NextAsync();
            Assert.Equal(
                (int)OptionalMeasurementFailureCategory.TelemetryDeadline,
                Assert.IsType<int>(Assert.Single(diagnostic.Payload!)));
        }
        finally
        {
            resolver.Release();
        }

        await resolver.Returned.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.Equal(0, transport.CallCount);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("unexpected")]
    public async Task OptOutOrInvalidOverridePreventsHelpTransport(string value)
    {
        var controls = new TelemetryControlService(
            new EnabledPreferenceStore(),
            () => value,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        var resolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));

        var actual = await InvokeAsync(
            ["--help"],
            Factory(resolver, controls));

        Assert.Equal(ExitCodes.Success, actual.ExitCode);
        Assert.Equal(0, resolver.ResolveCalls);
    }

    [Theory]
    [InlineData(false, false, "help")]
    [InlineData(false, true, "version")]
    [InlineData(false, false, "leaf")]
    [InlineData(true, true, "help")]
    [InlineData(true, false, "version")]
    [InlineData(true, true, "leaf")]
    public async Task PolicyDeadlinePreservesHostBehaviorAndPreventsLateActivation(
        bool blocksSynchronously,
        bool failsAfterRelease,
        string commandCase)
    {
        string[] args = commandCase switch
        {
            "help" => ["--help"],
            "version" => ["--version"],
            _ => ["auth", "status"],
        };
        var observer = new CommandTelemetryTenantObserver();
        var store = new BlockingPolicyPreferenceStore(
            blocksSynchronously,
            failsAfterRelease);
        var controls = new TelemetryControlService(
            store,
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        var resolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var transport = new CountingTransport();
        var operations = new FakeOperations
        {
            OnStatus = () =>
            {
                Assert.False(observer.IsObservationEnabled);
                return new AuthStatusReport
                {
                    Status = "signed-out",
                    SignedIn = false,
                };
            },
        };
        var baseline = await InvokeAsync(args, operations: new FakeOperations());
        using var listener = new DiagnosticEventListener(
            OptionalMeasurementFailureCategory.TelemetryPolicyDeadline);
        using DiagnosticEventListener? lateFailureListener =
            failsAfterRelease
                ? new DiagnosticEventListener(
                    OptionalMeasurementFailureCategory.TelemetrySessionCreation)
                : null;

        Task<(int ExitCode, string Stdout, string Stderr)> invocation =
            InvokeAsync(
                args,
                Factory(
                    resolver,
                    controls,
                    policyTimeout: TimeSpan.FromMilliseconds(20),
                    transport: transport,
                    tenantObserver: observer),
                operations);
        (int ExitCode, string Stdout, string Stderr) observed =
            (0, string.Empty, string.Empty);

        try
        {
            observed = await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(baseline, observed);
            await store.Started.WaitAsync(TimeSpan.FromSeconds(2));
            EventWrittenEventArgs diagnostic =
                await listener.NextAsync().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(
                (int)OptionalMeasurementFailureCategory.TelemetryPolicyDeadline,
                Assert.IsType<int>(Assert.Single(diagnostic.Payload!)));
            Assert.Equal(0, resolver.ResolveCalls);
            Assert.Equal(0, transport.CallCount);
            Assert.False(observer.IsObservationEnabled);
        }
        finally
        {
            store.Release();
            if (store.Started.IsCompleted)
            {
                await store.Returned.WaitAsync(TimeSpan.FromSeconds(2));
            }

            if (!invocation.IsCompleted)
            {
                await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            }
        }

        if (lateFailureListener is not null)
        {
            EventWrittenEventArgs lateFailure =
                await lateFailureListener.NextAsync();
            Assert.Equal(
                (int)OptionalMeasurementFailureCategory.TelemetrySessionCreation,
                Assert.IsType<int>(Assert.Single(lateFailure.Payload!)));
        }

        Assert.True(store.Token.IsCancellationRequested);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Equal(0, transport.CallCount);
        Assert.False(observer.IsObservationEnabled);
    }

    [Fact]
    public async Task PolicyStartupPropagatesCallerCancellation()
    {
        var store = new BlockingPolicyPreferenceStore(
            blocksSynchronously: false,
            failsAfterRelease: false);
        var controls = new TelemetryControlService(
            store,
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        var resolver = new FixedDestinationResolver(
            Destination(new Uri("http://127.0.0.1:1/OneCollector/1.0/")));
        var transport = new CountingTransport();
        using var cancellation = new CancellationTokenSource();
        Task<(int ExitCode, string Stdout, string Stderr)> invocation =
            InvokeAsync(
                ["--help"],
                Factory(
                    resolver,
                    controls,
                    policyTimeout: TimeSpan.FromMinutes(1),
                    transport: transport),
                cancellationToken: cancellation.Token);

        OperationCanceledException? actual = null;
        try
        {
            await store.Started.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () =>
                    await invocation.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            cancellation.Cancel();
            store.Release();
            if (store.Started.IsCompleted)
            {
                await store.Returned.WaitAsync(TimeSpan.FromSeconds(2));
            }

            try
            {
                await invocation.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException)
                when (cancellation.IsCancellationRequested)
            {
            }
        }

        Assert.NotNull(actual);
        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.True(store.Token.IsCancellationRequested);
        Assert.Equal(0, resolver.ResolveCalls);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task AlreadyCanceledPolicyStartupDoesNotStartAWorker()
    {
        var store = new BlockingPolicyPreferenceStore(
            blocksSynchronously: false,
            failsAfterRelease: false);
        var controls = new TelemetryControlService(
            store,
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        OperationCanceledException actual =
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                InvokeAsync(
                    ["--help"],
                    Factory(
                        new FixedDestinationResolver(
                            Destination(
                                new Uri("http://127.0.0.1:1/OneCollector/1.0/"))),
                        controls),
                    cancellationToken: cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal(0, store.ReadCalls);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        string[] args,
        CommandTelemetrySessionFactory? telemetryFactory = null,
        FakeOperations? operations = null,
        IServiceProvider? services = null,
        CancellationToken cancellationToken = default)
    {
        operations ??= new FakeOperations();
        IServiceProvider? commandServices = services is null
            ? null
            : new CompositeHandlerProvider(
                new FacadeHandlerProvider(operations),
                services);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cli = new AgentHostCli(
            operations,
            telemetryFactory,
            services: commandServices,
            featureFlags: TestFeatureFlags.AllProductSurfaces);

        int exitCode = await cli.InvokeAsync(
            args,
            stdout,
            stderr,
            color: false,
            cancellationToken);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static CommandTelemetrySessionFactory Factory(
        ITelemetryDestinationResolver resolver,
        TelemetryControlService? controls = null,
        TimeSpan? policyTimeout = null,
        TimeSpan? flushTimeout = null,
        IClock? clock = null,
        ICommandTelemetryTransport? transport = null,
        CommandTelemetryTenantObserver? tenantObserver = null) =>
        new(
            controls ?? EnabledControls(),
            resolver,
            clock ?? new FixedClock(),
            typeof(AgentHostCli).Assembly,
            tenantObserver,
            policyTimeout: policyTimeout ?? TelemetryTestBudgets.Policy,
            flushTimeout: flushTimeout ?? TelemetryTestBudgets.Flush,
            transport: transport);

    private static TelemetryControlService EnabledControls() =>
        new(
            new EnabledPreferenceStore(),
            () => null,
            compositionFallbackEnabled: true,
            collectionActivationAvailable: true);

    private static ServiceProvider BuildServices(TelemetryControlService controls)
    {
        var services = new ServiceCollection();
        services.AddSingleton<
            ICommandHandler<TelemetryEnableRequest, TelemetryControlReport>>(controls);
        services.AddSingleton<
            ICommandHandler<TelemetryDisableRequest, TelemetryControlReport>>(controls);
        services.AddSingleton<
            ICommandHandler<TelemetryStatusRequest, TelemetryControlReport>>(controls);
        return services.BuildServiceProvider();
    }

    private static JsonElement[] Parse(string payload) =>
        payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static JsonElement Dimensions(JsonElement item) =>
        JsonDocument.Parse(
            item.GetProperty("data").GetProperty("customDimensions").GetString()!)
            .RootElement.Clone();

    private static FakeOperations McsOperations() => new()
    {
        OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult
        {
            AgentType = AgentTypes.McsAgent,
            Source = DeploymentAgentTypeSource.DeploymentRecord,
        },
    };

    private static FakeOperations CodeAgentOperations() => new()
    {
        OnResolveDeploymentAgentType = _ => new DeploymentAgentTypeResult
        {
            AgentType = AgentTypes.CodeAgent,
            Source = DeploymentAgentTypeSource.DeploymentRecord,
        },
    };

    private static TelemetryDestination Destination(Uri endpoint) =>
        new(
            endpoint,
            SyntheticApiKey(),
            "powerappsclient",
            "prod",
            "prod",
            TelemetryLocation.Europe);

    private static string SyntheticApiKey() => string.Join(
        '-',
        new string('0', 32),
        new string('1', 8),
        new string('2', 4),
        new string('3', 4),
        new string('4', 4),
        new string('5', 12),
        new string('6', 4));

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Timestamp;
    }

    private sealed class ThrowingClock : IClock
    {
        public DateTimeOffset UtcNow =>
            throw new InvalidOperationException("synthetic clock failure");
    }

    private sealed class EnabledPreferenceStore : ITelemetryPreferenceStore
    {
        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(TelemetryRememberedPreference.Enabled);

        public ValueTask WriteAsync(bool enabled, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class BlockingPolicyPreferenceStore(
        bool blocksSynchronously,
        bool failsAfterRelease) : ITelemetryPreferenceStore
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _returned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => _started.Task;

        internal Task Returned => _returned.Task;

        internal int ReadCalls { get; private set; }

        internal CancellationToken Token { get; private set; }

        public ValueTask<TelemetryRememberedPreference> ReadAsync(
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            Token = cancellationToken;
            _started.TrySetResult();
            if (!blocksSynchronously)
            {
                return new ValueTask<TelemetryRememberedPreference>(
                    CompleteAsync());
            }

            try
            {
                _release.Task.GetAwaiter().GetResult();
                return failsAfterRelease
                    ? throw new InvalidOperationException(
                        "synthetic late policy failure")
                    : ValueTask.FromResult(
                        TelemetryRememberedPreference.Enabled);
            }
            finally
            {
                _returned.TrySetResult();
            }
        }

        public ValueTask WriteAsync(
            bool enabled,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("preference write was not expected");

        internal void Release() => _release.TrySetResult();

        private async Task<TelemetryRememberedPreference> CompleteAsync()
        {
            try
            {
                await _release.Task;
                if (failsAfterRelease)
                {
                    throw new InvalidOperationException(
                        "synthetic late policy failure");
                }

                return TelemetryRememberedPreference.Enabled;
            }
            finally
            {
                _returned.TrySetResult();
            }
        }
    }

    private sealed class FixedDestinationResolver(TelemetryDestination destination) :
        ITelemetryDestinationResolver
    {
        public bool ActivationAvailable => true;

        internal int ResolveCalls { get; private set; }

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            LastTarget = target;
            return ValueTask.FromResult<TelemetryDestination?>(destination);
        }

        internal TelemetryEnvironmentTarget? LastTarget { get; private set; }
    }

    private sealed class SynchronousBlockingResolver(
        TelemetryDestination destination) : ITelemetryDestinationResolver
    {
        private readonly ManualResetEventSlim _release = new();

        public bool ActivationAvailable => true;

        internal Task Started => _started.Task;

        internal Task Returned => _returned.Task;

        internal CancellationToken Token { get; private set; }

        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _returned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TelemetryDestination?> ResolveAsync(
            TelemetryEnvironmentTarget target,
            CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            _started.TrySetResult();
            _release.Wait(CancellationToken.None);
            _returned.TrySetResult();
            return ValueTask.FromResult<TelemetryDestination?>(destination);
        }

        internal void Release() => _release.Set();
    }

    private sealed class CountingTransport : ICommandTelemetryTransport
    {
        internal int CallCount { get; private set; }

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(HttpStatusCode.OK);
        }
    }

    private sealed class CapturingTransport : ICommandTelemetryTransport
    {
        internal string? Payload { get; private set; }

        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken)
        {
            Payload = payload;
            return ValueTask.FromResult(HttpStatusCode.OK);
        }
    }

    private sealed class FixedStatusTransport(HttpStatusCode status) :
        ICommandTelemetryTransport
    {
        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(status);
    }

    private sealed class ThrowingTransport : ICommandTelemetryTransport
    {
        public ValueTask<HttpStatusCode> SendAsync(
            string payload,
            TelemetryDestination destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<HttpStatusCode>(
                new InvalidOperationException("synthetic completion failure"));
    }

    private sealed class ThrowingWriter(Exception failure) : StringWriter
    {
        public override void Write(char value) => throw failure;

        public override void Write(string? value) => throw failure;

        public override void WriteLine(string? value) => throw failure;

        public override Task WriteAsync(string? value) =>
            Task.FromException(failure);
    }

    private sealed class OneShotCollector : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        internal OneShotCollector()
        {
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = new Uri($"http://127.0.0.1:{port}/OneCollector/1.0/");
        }

        internal Uri Endpoint { get; }

        internal bool HasPendingRequest => _listener.Pending();

        internal async Task<(string RequestLine, string Body)> ReceiveAsync()
        {
            using var client =
                await _listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            string requestLine = await reader.ReadLineAsync()
                ?? throw new EndOfStreamException("Collector request had no request line.");
            int contentLength = 0;

            for (
                string? line = await reader.ReadLineAsync();
                !string.IsNullOrEmpty(line);
                line = await reader.ReadLineAsync())
            {
                if (line.StartsWith(
                        "Content-Length:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(
                        line["Content-Length:".Length..].Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            var body = new char[contentLength];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = await reader.ReadAsync(body.AsMemory(offset));
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "Collector request ended before its body.");
                }

                offset += read;
            }

            byte[] response =
                "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8
                    .ToArray();
            await stream.WriteAsync(response);
            await stream.FlushAsync();
            return (requestLine, new string(body));
        }

        public void Dispose() => _listener.Stop();
    }
}
