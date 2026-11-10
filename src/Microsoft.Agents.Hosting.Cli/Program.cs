using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Auth.OwnedStorage;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Features;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Ports;
using Microsoft.Agents.Hosting.Cli.Rendering;
using Microsoft.Agents.Hosting.Cli.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Agents.Hosting.Cli;

/// <summary>
/// Process entry point and dependency-injection composition root for the demonstrator tool.
///
/// It is the only place that touches the process environment and the real clock: it resolves the color
/// policy and the <c>AH_CLI_*</c> settings into an <see cref="AgentHostAuthOptions"/>, builds the service
/// container via <see cref="ServiceCollectionExtensions.AddAgentHostAuthentication"/>, resolves the
/// <see cref="IAgentHostingOperations"/> facade, and hands parsing/dispatch to <see cref="AgentHostCli"/>.
///
/// The host references the <c>Microsoft.Agents.Hosting.Cli.Auth</c> project only to call the composition extension
/// with resolved primitives; it names no MSAL/Azure type, so the command host and operation core stay
/// hermetic and the offline-guarantee test stays green. Azure Bot Service adapters are supplied through
/// the same composition root without changing the command host.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) => MacOsMainThread.Run(() => RunAsync(args));

    private static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 2 &&
            args[0] == PlaygroundTerminalSession.ChildOption &&
            args[1].StartsWith(PlaygroundTerminalSession.PipeNamePrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(args[1][PlaygroundTerminalSession.PipeNamePrefix.Length..], "N", out _))
        {
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                return await PlaygroundTerminalSession.RunChildAsync(
                    args[1],
                    environment => new PlaygroundLauncher(inheritedEnvironment: environment),
                    cancellation.Token,
                    token => PlaygroundTerminalSession.AcknowledgeFailureAsync(
                        Console.IsInputRedirected,
                        Console.IsOutputRedirected || Console.IsErrorRedirected,
                        token));
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
        }

        var configDirectory = ResolveConfigDirectory(Environment.GetEnvironmentVariable);
        InvocationTarget invocation;

        try
        {
            invocation = await ResolveTargetForInvocationAsync(configDirectory, args);
        }
        catch (Exception failure) when (failure is AgentHostingException or IOException or UnauthorizedAccessException)
        {
            return ReportConfigurationFailure(DescribeUnreadableRing(configDirectory, failure));
        }

        var powerPlatformApiTarget = invocation.Target;

        FeatureFlags featureFlags;
        try
        {
            featureFlags = FeatureFlags.LoadFromAssemblyFolder(typeof(Program).Assembly);
        }
        catch (FileNotFoundException failure)
        {
            return ReportConfigurationFailure(failure.Message);
        }
        catch (IOException failure)
        {
            return ReportConfigurationFailure($"Feature controls could not be loaded: {failure.Message}");
        }
        catch (UnauthorizedAccessException failure)
        {
            return ReportConfigurationFailure($"Feature controls could not be loaded: {failure.Message}");
        }
        catch (JsonException failure)
        {
            return ReportConfigurationFailure($"Feature controls are malformed: {failure.Message}");
        }

        var color = ConsoleColorPolicy.ShouldEmitColor(
            noColorFlag: GlobalOptions.IsPresent(args, "--no-color"),
            noColorEnv: Environment.GetEnvironmentVariable("NO_COLOR"),
            outputRedirected: Console.IsOutputRedirected,
            errorRedirected: Console.IsErrorRedirected);

        var progressSpinner = new ProgressSpinner(Console.Error, ConsoleProgressPolicy.ShouldShowProgress(Console.IsErrorRedirected, Environment.GetEnvironmentVariable("CI")), color, TimeProvider.System, ConsoleWidth);

        var options = new AgentHostAuthOptions
        {
            ClientId = Environment.GetEnvironmentVariable("AH_CLI_CLIENT_ID"),
            GraphClientId = Environment.GetEnvironmentVariable("AH_CLI_GRAPH_CLIENT_ID"),
            GraphRedirectUri = Environment.GetEnvironmentVariable("AH_CLI_GRAPH_REDIRECT_URI"),
            ArmClientId = Environment.GetEnvironmentVariable("AH_CLI_ARM_CLIENT_ID"),
            ArmRedirectUri = Environment.GetEnvironmentVariable("AH_CLI_ARM_REDIRECT_URI"),
            ConfigDirectory = configDirectory,
            AllowUnprotectedCache = ParseBoolean(Environment.GetEnvironmentVariable("AH_CLI_ALLOW_UNPROTECTED_CACHE")),
            Cloud = powerPlatformApiTarget.Cloud,
            InteractionAllowed = AgentHostCli.ResolveInteractionAllowed(args, featureFlags),
            DebugOutput = GlobalOptions.IsPresent(args, "--debug") ? Console.Error : null,
            MessageOutput = progressSpinner.Error,
        };

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IProgressStatus>(progressSpinner);
        services.AddSingleton<IFeatureFlags>(featureFlags);
        services.AddSingleton<CommandTelemetrySessionAccessor>();
        services.AddSingleton<CommandTelemetryTenantObserver>();
        services.AddSingleton<IAuthenticationResultObserver>(
            sp => sp.GetRequiredService<CommandTelemetryTenantObserver>());
        services.AddSingleton<IAuthProfileStateObserver>(
            sp => sp.GetRequiredService<CommandTelemetryTenantObserver>());
        services.AddSingleton<CommandTelemetryEnvironmentObserver>();
        services.AddSingleton<IResolvedEnvironmentObserver>(
            sp => new CapturedProfileEnvironmentObserver(
                sp.GetRequiredService<CapturedProfileClients>(),
                sp.GetRequiredService<CommandTelemetryEnvironmentObserver>()));

        // The Power Platform API transport lives here, in the composition root, rather than in the auth
        // package: this is the only place that should decide how an HttpClient behaves. Automatic
        // redirect following is disabled so a host-changing redirect can never cause the delegated
        // bearer token to be replayed to a host the cloud map never approved.
        services.AddSingleton<AgentCreationTarget>();

        // Automatic redirect following is disabled at client construction — before any authorization
        // header exists — so a host-changing redirect can never cause the delegated bearer token to be
        // resent to a host that is not the configured one.
        var debugRequested = GlobalOptions.IsPresent(args, "--debug");
        services.AddSingleton<IAmsPackagePublicationTransport>(sp => new AmsPackagePublicationAdapter(
            new HttpClient(TransportDefaults.CreateRedirectRejectingHandler()),
            sp.GetRequiredService<ITokenProvider>(),
            sp.GetRequiredService<PpapiTokenClaimsValidator>(),
            profileTokens: sp.GetRequiredService<IProfileResourceTokenProvider>()));
        services.AddSingleton(sp => new AmsDeploymentAdapter(
            new HttpClient(TransportDefaults.CreateRedirectRejectingHandler()),
            sp.GetRequiredService<ITokenProvider>(),
            sp.GetRequiredService<PpapiTokenClaimsValidator>(),
            profileTokens: sp.GetRequiredService<IProfileResourceTokenProvider>()));
        services.AddSingleton<IAmsAgentReadTransport>(
            sp => sp.GetRequiredService<AmsDeploymentAdapter>());
        services.AddSingleton<IAmsDeploymentTransport>(
            sp => sp.GetRequiredService<AmsDeploymentAdapter>());
        services.AddSingleton<IAmsAgentCreationTransport>(sp => new AmsAgentCreationAdapter(
            new HttpClient(TransportDefaults.CreateRedirectRejectingHandler()),
            sp.GetRequiredService<ITokenProvider>(),
            sp.GetRequiredService<PpapiTokenClaimsValidator>(),
            profileTokens: sp.GetRequiredService<IProfileResourceTokenProvider>()));

        ConfigureProfileAuthentication(services, options, powerPlatformApiTarget, Environment.GetEnvironmentVariable,
            bootstrapProfile: invocation.Profile);
        services.AddSingleton<ICommandTelemetryProfileContextSource>(
            sp => new CommandTelemetryProfileContextSource(
                sp.GetRequiredService<IAuthProfileTelemetryStore>(),
                sp.GetRequiredService<AuthSessionContext>()));
        services.AddAgentHostCopilotStudio(progressSpinner.Error, powerPlatformApiTarget, debugRequested);

        services.AddSingleton<CopilotSchemaName>();
        services.AddSingleton<CopilotEnvironmentResolver>();
        services.AddSingleton<CopilotWorkspaceLocator>();
        services.AddSingleton<CopilotCatalogService>();
        services.AddSingleton<CopilotWorkspaceService>();
        services.AddSingleton<CopilotScaffoldService>();

        services.AddSingleton<AgentConfigLocator>();
        services.AddSingleton<CopilotProjectIdentityReader>();
        services.AddSingleton<JsonAgentConfigStore>();
        services.AddSingleton<IManagedAgentRegistry>(sp => sp.GetRequiredService<JsonAgentConfigStore>());
        services.AddSingleton<IAgentDeploymentRegistry>(sp => sp.GetRequiredService<JsonAgentConfigStore>());
        services.AddSingleton<IDeploymentStore, ProjectDeploymentStore>();
        services.AddSingleton<IConfirmationPrompt>(_ => new ConsoleConfirmationPrompt(PromptInput(progressSpinner), progressSpinner.Error));
        services.AddSingleton<IAuthProfilePicker>(_ => new ConsoleAuthProfilePicker(PromptInput(progressSpinner), progressSpinner.Error));
        services.AddSingleton<ManagedAgentPrincipalResolver>();
        services.AddSingleton<ManagedAgentResolver>();
        services.AddSingleton<LiveTestTargetResolver>();
        services.AddSingleton<
            ICommandHandler<LiveTestRequest, ILiveTestStreamingResult>,
            LiveTestCommandHandler>();
        services.AddSingleton<IPlaygroundProcessRunner, PlaygroundProcessRunner>();
        services.AddSingleton<IPlaygroundToolEnvironment, SystemPlaygroundToolEnvironment>();
        services.AddSingleton<IPlaygroundToolLocator, PlaygroundToolLocator>();
        services.AddSingleton<IPlaygroundGlobalInstaller, PlaygroundGlobalInstaller>();
        services.AddSingleton<IPlaygroundLauncher, PlaygroundTerminalSession>();
        services.AddSingleton<
            ICommandHandler<PlaygroundRequest, ILiveTestStreamingResult>>(
            sp => new PlaygroundCommandHandler(
                sp.GetRequiredService<LiveTestTargetResolver>().Resolve,
                async (target, nonInteractive, cancellationToken) =>
                {
                    await sp.GetRequiredService<MsalLiveTestTokenProvider>()
                        .AcquireDelegatedTokenAsync(target.TenantId, nonInteractive, cancellationToken)
                        .ConfigureAwait(false);
                    await using var session = new McsLiveTestSession(
                        new McsLiveTestTarget(target.EnvironmentId, target.TenantId, target.SchemaName),
                        sp.GetRequiredService<ITokenProvider>(),
                        cancellationToken: cancellationToken);
                    return target with
                    {
                        Endpoint = await session.DiscoverEndpointAsync(cancellationToken).ConfigureAwait(false),
                    };
                },
                sp.GetRequiredService<IPlaygroundToolLocator>(),
                sp.GetRequiredService<IPlaygroundGlobalInstaller>(),
                sp.GetRequiredService<IPlaygroundLauncher>(),
                sp.GetRequiredService<IConfirmationPrompt>(),
                () => AgentHostClientAppConfiguration.Create(
                    Environment.GetEnvironmentVariable(AgentHostClientAppConfiguration.ClientIdSettingName),
                    new RuntimePlatformInfo()),
                () => Console.IsInputRedirected,
                () => Console.IsOutputRedirected,
                details => sp.GetRequiredService<CommandTelemetrySessionAccessor>().Current.RecordPlaygroundDetails(details)));
        services.AddSingleton<ManagedAgentCloneMaterialiser>();
        services.AddSingleton<HomeDeploymentRegistrar>();
        services.AddSingleton<ManagedAgentCreateService>();
        services.AddSingleton<ManagedAgentInitService>();
        services.AddSingleton<ManagedAgentListService>();
        services.AddSingleton<ManagedAgentShowService>();
        services.AddSingleton<ManagedAgentDeleteService>();
        services.AddSingleton<ITenantUserDirectoryPort, GraphTenantUserDirectoryAdapter>();
        services.AddSingleton<ManagedAgentShareService>();
        services.AddSingleton<ManagedAgentPullService>();
        services.AddSingleton<ManagedAgentDeployService>();
        services.AddSingleton<AgentDeploymentCreateService>();
        services.AddSingleton<AgentDeploymentListService>();
        services.AddSingleton<AgentDeploymentShowService>();
        services.AddSingleton<AgentDeploymentUpdateService>();
        services.AddSingleton<AgentDeploymentDeleteService>();
        services.AddSingleton<DeploymentAgentTypeResolver>();
        services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IOperatorInterrupt, ConsoleEscapeInterrupt>();
        ConfigureConnectionConsent(services, powerPlatformApiTarget, debugRequested);
        services.AddSingleton(sp => new ConnectorReadinessWait(sp.GetRequiredService<IConnectorCatalogPort>()));
        services.AddSingleton<AgentDeploymentConnectionResolver>();
        services.AddSingleton<ConnectionTargetResolver>();
        services.AddSingleton<ConnectorResolver>();
        services.AddSingleton<WorkflowStateCache>();
        services.AddSingleton<WorkflowActivationPass>();
        services.AddSingleton<IConnectionSelectionPrompt>(_ => new ConsoleConnectionSelectionPrompt(PromptInput(progressSpinner), progressSpinner.Error));
        services.AddSingleton<ConnectionCreationFlow>();
        services.AddSingleton<ConnectionBindingCoordinator>();
        services.AddSingleton(sp => AgentDeploymentConnectionService.CreateDeferred(
            sp.GetRequiredService<AgentDeploymentConnectionResolver>(),
            sp.GetRequiredService<ICopilotConnectionPort>(),
            sp.GetRequiredService<ConnectionBindingCoordinator>(),
            sp.GetRequiredService<IConnectionSelectionPrompt>(),
            sp.GetRequiredService<WorkflowStateCache>(),
            sp.GetRequiredService<ManagedAgentPrincipalResolver>(),
            () => PowerAppsServiceTarget.ClusterFor(powerPlatformApiTarget)));
        services.AddSingleton(sp => AgentDeploymentConnectionReader.CreateDeferred(
            sp.GetRequiredService<AgentDeploymentConnectionResolver>(),
            sp.GetRequiredService<ICopilotConnectionPort>(),
            sp.GetRequiredService<WorkflowStateCache>(),
            () => PowerAppsServiceTarget.ClusterFor(powerPlatformApiTarget)));
        services.AddSingleton<AgentDeploymentConnectorListService>();
        services.AddSingleton(sp => new AgentDeploymentConnectionCreateService(
            sp.GetRequiredService<ConnectionTargetResolver>(),
            sp.GetRequiredService<ConnectorResolver>(),
            sp.GetRequiredService<ConnectionCreationFlow>(),
            sp.GetRequiredService<ManagedAgentPrincipalResolver>()));
        services.AddSingleton(sp => new AgentDeploymentConnectionReferenceCreateService(
            sp.GetRequiredService<AgentDeploymentConnectionResolver>(),
            sp.GetRequiredService<ConnectorResolver>(),
            sp.GetRequiredService<ICopilotConnectionPort>(),
            sp.GetRequiredService<ConnectionBindingCoordinator>(),
            sp.GetRequiredService<ManagedAgentPrincipalResolver>()));
        services.AddSingleton<AgentDeploymentConnectionRemoveService>();
        services.AddSingleton<WorkflowProjectResolver>();
        services.AddSingleton<WorkflowCreateService>();
        services.AddSingleton<WorkflowInitService>();
        services.AddSingleton<WorkflowDeployService>();
        services.AddSingleton<WorkflowPullService>();
        services.AddSingleton<WorkflowDeleteService>();
        services.AddSingleton<ManagedAgentServices>();
        services.AddSingleton<EvaluationTargetResolver>();
        services.AddSingleton<IEvaluationTargetResolver>(sp => sp.GetRequiredService<EvaluationTargetResolver>());
        services.AddSingleton<EvaluationTestSetLookup>();
        services.AddSingleton<EvaluationConversationProvider>();
        services.AddSingleton<EvaluationQueryService>();
        services.AddSingleton<EvaluationTestSetService>();
        services.AddSingleton(sp => new EvaluationRunService(
            sp.GetRequiredService<IEvaluationTargetResolver>(),
            sp.GetRequiredService<EvaluationTestSetLookup>(),
            sp.GetRequiredService<EvaluationTestSetService>(),
            sp.GetRequiredService<EvaluationConversationProvider>(),
            sp.GetRequiredService<IAgentEvaluationPort>(),
            sp.GetRequiredService<ICopilotFileSystem>(),
            sp.GetRequiredService<IInteractiveMessagePresenter>(),
            sp.GetRequiredService<IClock>(),
            progress: sp.GetRequiredService<IProgressStatus>()));
        services.AddSingleton<EvaluationServices>();

        var telemetryPreferences =
            FileTelemetryPreferenceStore.CreateForAhConfigDirectory(options.ConfigDirectory);
        services.AddSingleton<ITelemetryPreferenceStore>(telemetryPreferences);
        services.AddSingleton<ITelemetryDestinationResolver>(
            sp => new CapturedProfileTelemetryDestinationResolver(
                sp.GetRequiredService<CapturedProfileClients>(),
                sp.GetRequiredService<IEnvironmentGeographyClient>(),
                powerPlatformApiTarget, invocation.RingKnown,
                sp.GetRequiredService<CommandTelemetryTenantObserver>()));
        services.AddSingleton(sp => new TelemetryControlService(
            sp.GetRequiredService<ITelemetryPreferenceStore>(),
            () => Environment.GetEnvironmentVariable(
                TelemetryControlService.OptOutEnvironmentVariable),
            compositionFallbackEnabled: true,
            collectionActivationAvailable:
                sp.GetRequiredService<ITelemetryDestinationResolver>().ActivationAvailable));
        services.AddSingleton<
            ICommandHandler<TelemetryEnableRequest, TelemetryControlReport>>(
            sp => sp.GetRequiredService<TelemetryControlService>());
        services.AddSingleton<
            ICommandHandler<TelemetryDisableRequest, TelemetryControlReport>>(
            sp => sp.GetRequiredService<TelemetryControlService>());
        services.AddSingleton<
            ICommandHandler<TelemetryStatusRequest, TelemetryControlReport>>(
            sp => sp.GetRequiredService<TelemetryControlService>());
        services.AddSingleton(sp => new CommandTelemetrySessionFactory(
            sp.GetRequiredService<TelemetryControlService>(),
            sp.GetRequiredService<ITelemetryDestinationResolver>(),
            sp.GetRequiredService<IClock>(),
            typeof(Program).Assembly,
            sp.GetRequiredService<CommandTelemetryTenantObserver>(),
            sp.GetRequiredService<CommandTelemetryEnvironmentObserver>(),
            sp.GetRequiredService<ICommandTelemetryProfileContextSource>()));

        await using var provider = services.BuildServiceProvider();
        var operations = provider.GetRequiredService<IAgentHostingOperations>();

        using var updateHttp = UpdateCheckService.ShouldCheck(
            Console.IsOutputRedirected,
            Console.IsErrorRedirected,
            Environment.GetEnvironmentVariable("CI"),
            Environment.GetEnvironmentVariable("AH_CLI_SKIP_UPDATE_CHECK"))
            ? CreateUpdateHttpClient()
            : null;
        var updateCheck = updateHttp is null ? null : new UpdateCheckService(
            new NuGetUpdateClient(updateHttp, TimeProvider.System),
            configDirectory,
            ThisAssembly.AssemblyInformationalVersion,
            TimeProvider.System);

        var cli = new AgentHostCli(
            operations,
            provider.GetRequiredService<CommandTelemetrySessionFactory>(),
            new CompositeHandlerProvider(new FacadeHandlerProvider(operations, provider.GetRequiredService<IAuthProfileOperations>()), provider),
            featureFlags,
            provider.GetRequiredService<CommandTelemetrySessionAccessor>(),
            progressSpinner: progressSpinner,
            updateCheck: updateCheck);

        using var lifetime = new CancellationTokenSource();

        void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
        {
            if (lifetime.IsCancellationRequested)
            {
                return;
            }

            eventArgs.Cancel = true;
            lifetime.Cancel();
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            return await cli.InvokeAsync(args, Console.Out, Console.Error, color, lifetime.Token);
        }
        catch (CommandConfigurationException failure)
        {
            return ReportStartupFailure(failure.Message);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
        }
    }

    private static HttpClient CreateUpdateHttpClient()
    {
        var handler = TransportDefaults.CreateRedirectRejectingHandler();
        handler.UseCookies = false;
        handler.AutomaticDecompression = System.Net.DecompressionMethods.All;
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Reports a malformed command tree as a usage error. The tree is discovered by reflection and
    /// validated at startup, so this reaches a developer who has just added or renamed a command —
    /// a stack trace would say less than the message the reflector already composed.
    /// </summary>
    private static int ReportStartupFailure(string message)
    {
        Console.Error.WriteLine($"The command tree is misconfigured: {message}");

        return ExitCodes.Usage;
    }

    private static int ReportConfigurationFailure(string message)
    {
        Console.Error.WriteLine(message);
        return ExitCodes.Failure;
    }

    private static int ConsoleWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (Exception failure) when (failure is IOException or PlatformNotSupportedException)
        {
            return 0;
        }
    }

    private static TextReader PromptInput(ProgressSpinner progressSpinner) => progressSpinner.WrapInput(Console.In, echoed: !Console.IsInputRedirected);

    /// <summary>
    /// Resolves the Power Platform API ring from the ring the active sign-in records, before the
    /// container that depends on it exists.
    /// </summary>
    /// <param name="configDirectory">The resolved config directory holding the profiles file.</param>
    /// <returns>The target the host builds its clients against.</returns>
    internal static async Task<PowerPlatformApiTargetConfig> ResolvePowerPlatformApiTargetAsync(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        return (await ReadInvocationTargetAsync(configDirectory).ConfigureAwait(false)).Target;
    }

    /// <summary>
    /// The cloud one invocation runs against, and whether the stored sign-in actually named it.
    /// </summary>
    /// <param name="Target">The endpoints the host builds its clients for.</param>
    /// <param name="RingKnown">
    /// Whether <paramref name="Target"/> came from the stored sign-in. False where the ring could not
    /// be read and the command runs anyway, which is a fallback rather than a classification.
    /// </param>
    /// <param name="Profile">Token-free authentication coordinates captured by bootstrap.</param>
    internal readonly record struct InvocationTarget(PowerPlatformApiTargetConfig Target, bool RingKnown, AuthProfile? Profile = null);

    /// <summary>
    /// Resolves the cloud for one invocation, letting the commands that repair the stored sign-in run
    /// when it cannot be read.
    /// </summary>
    /// <param name="configDirectory">The resolved config directory holding the profiles file.</param>
    /// <param name="args">The raw command line, read only to recognise those commands.</param>
    /// <returns>The target, and whether the stored sign-in named it.</returns>
    internal static async Task<InvocationTarget> ResolveTargetForInvocationAsync(string configDirectory, IReadOnlyList<string> args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            return await ReadInvocationTargetAsync(configDirectory).ConfigureAwait(false);
        }
        catch (Exception failure) when (
            AgentHostCli.IsRingIndependent(args)
            && failure is AgentHostingException or IOException or UnauthorizedAccessException)
        {
            return new InvocationTarget(PowerPlatformApiRingSelection.Default, RingKnown: false);
        }
    }

    /// <summary>
    /// Picks where this invocation files its telemetry.
    /// </summary>
    /// <param name="ringKnown">Whether the stored sign-in named the ring this run targets.</param>
    /// <param name="classified">The resolver for a run whose ring is known.</param>
    /// <returns>The resolver the container exposes.</returns>
    internal static ITelemetryDestinationResolver SelectTelemetryDestinationResolver(
        bool ringKnown,
        TelemetryDestinationResolver classified)
    {
        ArgumentNullException.ThrowIfNull(classified);

        return ringKnown ? classified : new UnclassifiedTelemetryDestinationResolver();
    }

    /// <summary>
    /// Explains that the stored sign-in did not yield a cloud, naming the file that holds it.
    /// </summary>
    /// <param name="configDirectory">The resolved config directory holding the profiles file.</param>
    /// <param name="failure">What the profile store reported.</param>
    /// <returns>The message written to standard error.</returns>
    internal static string DescribeUnreadableRing(string configDirectory, Exception failure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ArgumentNullException.ThrowIfNull(failure);

        return $"The cloud this command targets could not be read from {ProfilesPath(configDirectory)}, so nothing was run. "
            + "Run `msagent auth clear` to clear local sign-in state and owned credentials, then `msagent auth create` to sign in again. "
            + "Deleting profile metadata alone does not remove owned credentials.";
    }

    private static string ProfilesPath(string configDirectory) =>
        Path.Combine(configDirectory, AgentHostAuthOptions.ProfilesFileName);

    private static async Task<InvocationTarget> ReadInvocationTargetAsync(string configDirectory)
    {
        var profile = await new FileAuthProfileStore(ProfilesPath(configDirectory))
            .GetActiveProfileAsync(CancellationToken.None).ConfigureAwait(false);
        return new(profile is null ? PowerPlatformApiTargetConfig.Public
            : PowerPlatformApiTargetSelection.TargetFor(profile.Cloud, profile.Ring), true, profile);
    }

    internal static void ConfigureConnectionConsent(IServiceCollection services, PowerPlatformApiTargetConfig target, bool debugRequested)
    {
        services.AddSingleton<IConnectionConsentFlow>(sp => new CapturedConnectionConsentFlow(environment =>
        {
            var cluster = environment.Context is { } context
                ? PowerAppsServiceTarget.ClusterFor(context) : PowerAppsServiceTarget.ClusterFor(target);
            return new ConnectionConsentLadder(
            [
                new ConsentLinkConnectionConsentFlow(
                    new PowerAppsConnectionClient(
                        sp.GetRequiredService<ITokenProvider>(), cluster,
                        diagnostics: debugRequested ? Console.Error : null,
                        profileCredentials: sp.GetRequiredService<IProfileCredentialProvider>()),
                    sp.GetRequiredService<IInteractiveMessagePresenter>(),
                    sp.GetRequiredService<IBrowserLauncher>(), cluster,
                    sp.GetRequiredService<IOperatorInterrupt>()),
                new BrowserConnectionConsentFlow(
                    sp.GetRequiredService<IBrowserLauncher>(),
                    sp.GetRequiredService<IInteractiveMessagePresenter>(),
                    sp.GetRequiredService<IConnectorCatalogPort>(),
                    sp.GetRequiredService<ManagedAgentPrincipalResolver>(), cluster,
                    sp.GetRequiredService<IOperatorInterrupt>(),
                    confirmations: sp.GetRequiredService<IConfirmationPrompt>()),
            ]);
        }));
    }

    internal static void ConfigureProfileAuthentication(
        IServiceCollection services, AgentHostAuthOptions options, PowerPlatformApiTargetConfig target,
        Func<string, string?> readEnvironment,
        Func<IOwnedProtectedStorageBackend>? createOwnedStorage = null,
        Func<ILegacyMsalTokenCacheBackend>? createLegacyCache = null,
        AuthProfile? bootstrapProfile = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        var nativeSettings = new Dictionary<string, string?>
        {
            ["DBUS_SESSION_BUS_ADDRESS"] = readEnvironment("DBUS_SESSION_BUS_ADDRESS"),
            ["G_DBUS_DEBUG"] = readEnvironment("G_DBUS_DEBUG"),
        };
        var macPolicy = new Lazy<MacOsKeychainProcessPolicy?>(() => OperatingSystem.IsMacOS()
            ? MacOsKeychainProcessPolicy.EstablishForCliProcess() : null);
        services.AddAgentHostAuthentication(options, target);
        var ownedStorage = new DeferredOwnedProtectedStorage(createOwnedStorage ?? (() =>
            OwnedProtectedStorageBackendFactory.Create(options.ConfigDirectory, macPolicy.Value, nativeSettings.GetValueOrDefault)),
            OperatingSystem.IsLinux() && options.AllowUnprotectedCache ? options.ConfigDirectory : null);
        var runtime = new ProfileCredentialRuntimeOptions
        {
            GitHubRequestUrl = readEnvironment("ACTIONS_ID_TOKEN_REQUEST_URL"),
            GitHubRequestToken = readEnvironment("ACTIONS_ID_TOKEN_REQUEST_TOKEN"),
            AzureDevOpsRequestUrl = readEnvironment("MSAGENT_ADO_ID_TOKEN_REQUEST_URL"),
            AzureDevOpsRequestToken = readEnvironment("MSAGENT_ADO_ID_TOKEN_REQUEST_TOKEN"),
            AzureTenantId = readEnvironment("AZURE_TENANT_ID"),
            AzureClientId = readEnvironment("AZURE_CLIENT_ID"),
            AzureClientSecret = readEnvironment("AZURE_CLIENT_SECRET"),
            AzureCertificatePath = readEnvironment("AZURE_CLIENT_CERTIFICATE_PATH"),
            AzureCertificatePassword = readEnvironment("AZURE_CLIENT_CERTIFICATE_PASSWORD"),
            AzureFederatedTokenFile = readEnvironment("AZURE_FEDERATED_TOKEN_FILE"),
            ManagedIdentityClientId = readEnvironment("AZURE_CLIENT_ID"),
            AzureCredentialSelection = readEnvironment("AZURE_TOKEN_CREDENTIALS"),
        };
        var legacyCache = new DeferredLegacyMsalTokenCache(createLegacyCache ?? (() =>
            OperatingSystem.IsLinux() && options.AllowUnprotectedCache
                ? LegacyMsalTokenCacheBackendFactory.CreateWithUnprotectedTokenCacheFallback(options.ConfigDirectory, nativeSettings.GetValueOrDefault)
                : LegacyMsalTokenCacheBackendFactory.Create(options.ConfigDirectory, macPolicy.Value, nativeSettings.GetValueOrDefault)));
        services.AddAgentHostProfileCredentials(ownedStorage, runtime, legacyCache);
        // These wrappers are host-owned, unlike arbitrary caller-supplied adapter instances.
        services.AddSingleton<IOwnedProtectedStorageBackend>(_ => ownedStorage);
        services.AddSingleton<ILegacyMsalTokenCacheBackend>(_ => legacyCache);
        services.AddKeyedSingleton<HttpClient>("profile-environments", (_, _) =>
            new HttpClient(TransportDefaults.CreateRedirectRejectingHandler()));
        services.AddSingleton<IProfileResourceTokenProvider>(sp => new ProfileResourceTokenProvider(
            sp.GetRequiredService<IProfileCredentialProvider>(), sp.GetRequiredService<PpapiTokenClaimsValidator>()));
        services.AddSingleton(sp => new CapturedProfileClients(
            sp.GetRequiredKeyedService<HttpClient>("profile-environments"),
            sp.GetRequiredService<IProfileResourceTokenProvider>(), bootstrapProfile));
        services.AddSingleton<IProfileEnvironmentClientFactory>(sp => sp.GetRequiredService<CapturedProfileClients>());
        services.AddKeyedSingleton<HttpClient>("profile-geography", (_, _) =>
            new HttpClient(TransportDefaults.CreateRedirectRejectingHandler()));
        services.AddSingleton<IEnvironmentGeographyClient>(sp => new CapturedProfileGeographyClient(
            sp.GetRequiredKeyedService<HttpClient>("profile-geography"), sp.GetRequiredService<CapturedProfileClients>(),
            sp.GetRequiredService<MsalSilentTokenProvider>()));
        services.AddSingleton(sp => new EnvironmentSelectionService(
            sp.GetRequiredService<IProfileEnvironmentClientFactory>(),
            sp.GetRequiredService<IEnvironmentSelectionStore>(),
            sp.GetRequiredService<IAuthProfileStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetService<IResolvedEnvironmentObserver>()));
    }

    /// <summary>
    /// Resolves the config directory. This is shared by every ring: identity state is ring-neutral,
    /// and the ring-specific files inside it are scoped by name.
    /// </summary>
    internal static string ResolveConfigDirectory(Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(readEnvironment);

        var configured = readEnvironment("AH_CLI_CONFIG_DIR");

        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "agenthost-cli-csharp")
            : configured;
    }

    private static bool ParseBoolean(string? value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

}
