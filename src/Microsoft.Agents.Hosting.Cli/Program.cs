using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Evaluations;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Features;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Ports;
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
            InteractionAllowed = !GlobalOptions.IsPresent(args, "--non-interactive"),
            DebugOutput = GlobalOptions.IsPresent(args, "--debug") ? Console.Error : null,
        };

        var services = new ServiceCollection();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IFeatureFlags>(featureFlags);
        services.AddSingleton<CommandTelemetrySessionAccessor>();
        services.AddSingleton<CommandTelemetryTenantObserver>();
        services.AddSingleton<IAuthenticationResultObserver>(
            sp => sp.GetRequiredService<CommandTelemetryTenantObserver>());
        services.AddSingleton<IAuthProfileStateObserver>(
            sp => sp.GetRequiredService<CommandTelemetryTenantObserver>());
        services.AddSingleton<CommandTelemetryEnvironmentObserver>();
        services.AddSingleton<IResolvedEnvironmentObserver>(
            sp => sp.GetRequiredService<CommandTelemetryEnvironmentObserver>());

        // The Power Platform API transport lives here, in the composition root, rather than in the auth
        // package: this is the only place that should decide how an HttpClient behaves. Automatic
        // redirect following is disabled so a host-changing redirect can never cause the delegated
        // bearer token to be replayed to a host the cloud map never approved.
        services.AddSingleton<IEnvironmentClient>(sp => new PowerPlatformApiEnvironmentClient(
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }),
            sp.GetRequiredService<ITokenProvider>(),
            powerPlatformApiTarget,
            claimsValidator: sp.GetRequiredService<PpapiTokenClaimsValidator>()));

        // Package upload and deploy resolve configured AMS-DP/AMS-CMP targets independently. Record creation uses the
        // environment-specific Power Platform API endpoint; its optional target is a direct-island
        // Dev override resolved only by that command.
        services.AddSingleton(new AgentHostingServiceTargets
        {
            DataPlaneEndpoint = Environment.GetEnvironmentVariable(
                AgentHostingServiceTargets.DataPlaneEndpointVariableName),
            ControlPlaneEndpoint = Environment.GetEnvironmentVariable(
                AgentHostingServiceTargets.ControlPlaneEndpointVariableName),
        });
        services.AddSingleton(new AgentCreationTarget
        {
            Endpoint = Environment.GetEnvironmentVariable(AgentCreationTarget.EndpointVariableName),
        });

        // Automatic redirect following is disabled at client construction — before any authorization
        // header exists — so a host-changing redirect can never cause the delegated bearer token to be
        // resent to a host that is not the configured one.
        var debugRequested = GlobalOptions.IsPresent(args, "--debug");
        services.AddSingleton<IAmsPackagePublicationTransport>(sp => new AmsPackagePublicationAdapter(
            new HttpClient(AmsPackagePublicationAdapter.CreateRedirectRejectingHandler()),
            sp.GetRequiredService<ITokenProvider>(),
            sp.GetRequiredService<PpapiTokenClaimsValidator>()));
        services.AddSingleton(sp => new AmsDeploymentAdapter(
            new HttpClient(AmsDeploymentAdapter.CreateRedirectRejectingHandler()),
            sp.GetRequiredService<ITokenProvider>(),
            sp.GetRequiredService<PpapiTokenClaimsValidator>()));
        services.AddSingleton<IAmsAgentReadTransport>(
            sp => sp.GetRequiredService<AmsDeploymentAdapter>());
        services.AddSingleton<IAmsDeploymentTransport>(
            sp => sp.GetRequiredService<AmsDeploymentAdapter>());
        services.AddSingleton<IAmsAgentCreationTransport>(sp => new AmsAgentCreationAdapter(
            new HttpClient(AmsAgentCreationAdapter.CreateRedirectRejectingHandler()),
            sp.GetRequiredService<ITokenProvider>(),
            sp.GetRequiredService<PpapiTokenClaimsValidator>()));

        services.AddAgentHostAuthentication(options, powerPlatformApiTarget);
        services.AddSingleton<ICommandTelemetryProfileContextSource>(
            sp => new CommandTelemetryProfileContextSource(
                sp.GetRequiredService<IAuthProfileTelemetryStore>(),
                sp.GetRequiredService<AuthSessionContext>()));
        services.AddAgentHostCopilotStudio(Console.Error, powerPlatformApiTarget, debugRequested);
        services.AddSingleton<IEnvironmentGeographyClient>(
            sp => new PowerPlatformApiEnvironmentGeographyClient(
                new HttpClient(
                    new HttpClientHandler { AllowAutoRedirect = false }),
                sp.GetRequiredService<ISelectedAccountTokenProvider>(),
                powerPlatformApiTarget,
                sp.GetRequiredService<PpapiTokenClaimsValidator>()));

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
        services.AddSingleton<IConfirmationPrompt>(_ => new ConsoleConfirmationPrompt(Console.In, Console.Error));
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
        services.AddSingleton<IPlaygroundLauncher, PlaygroundLauncher>();
        services.AddSingleton<
            ICommandHandler<PlaygroundRequest, ILiveTestStreamingResult>>(
            sp => new PlaygroundCommandHandler(
                sp.GetRequiredService<LiveTestTargetResolver>().Resolve,
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
        services.AddSingleton<ManagedAgentCreateService>();
        services.AddSingleton<ManagedAgentInitService>();
        services.AddSingleton<ManagedAgentListService>();
        services.AddSingleton<ManagedAgentShowService>();
        services.AddSingleton<ManagedAgentDeleteService>();
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
        services.AddSingleton<IConnectionConsentFlow>(sp => new ConnectionConsentLadder(
            [
                new ConsentLinkConnectionConsentFlow(
                    new PowerAppsConnectionClient(
                        sp.GetRequiredService<ITokenProvider>(),
                        CoreServicesCluster.For(powerPlatformApiTarget),
                        diagnostics: debugRequested ? Console.Error : null),
                    sp.GetRequiredService<IInteractiveMessagePresenter>(),
                    sp.GetRequiredService<IBrowserLauncher>(),
                    CoreServicesCluster.For(powerPlatformApiTarget),
                    sp.GetRequiredService<IOperatorInterrupt>()),
                new BrowserConnectionConsentFlow(
                    sp.GetRequiredService<IBrowserLauncher>(),
                    sp.GetRequiredService<IInteractiveMessagePresenter>(),
                    sp.GetRequiredService<IConnectorCatalogPort>(),
                    sp.GetRequiredService<ManagedAgentPrincipalResolver>(),
                    CoreServicesCluster.For(powerPlatformApiTarget),
                    sp.GetRequiredService<IOperatorInterrupt>(),
                    confirmations: sp.GetRequiredService<IConfirmationPrompt>()),
            ]));
        services.AddSingleton(sp => new ConnectorReadinessWait(sp.GetRequiredService<IConnectorCatalogPort>()));
        services.AddSingleton<AgentDeploymentConnectionResolver>();
        services.AddSingleton<ConnectionTargetResolver>();
        services.AddSingleton<ConnectorResolver>();
        services.AddSingleton<WorkflowStateCache>();
        services.AddSingleton<WorkflowActivationPass>();
        services.AddSingleton<IConnectionSelectionPrompt>(_ => new ConsoleConnectionSelectionPrompt(Console.In, Console.Error));
        services.AddSingleton<ConnectionCreationFlow>();
        services.AddSingleton<ConnectionBindingCoordinator>();
        services.AddSingleton(sp => new AgentDeploymentConnectionService(
            sp.GetRequiredService<AgentDeploymentConnectionResolver>(),
            sp.GetRequiredService<ICopilotConnectionPort>(),
            sp.GetRequiredService<ConnectionBindingCoordinator>(),
            sp.GetRequiredService<IConnectionSelectionPrompt>(),
            sp.GetRequiredService<WorkflowStateCache>(),
            sp.GetRequiredService<ManagedAgentPrincipalResolver>(),
            CoreServicesCluster.For(powerPlatformApiTarget)));
        services.AddSingleton(sp => new AgentDeploymentConnectionReader(
            sp.GetRequiredService<AgentDeploymentConnectionResolver>(),
            sp.GetRequiredService<ICopilotConnectionPort>(),
            sp.GetRequiredService<WorkflowStateCache>(),
            CoreServicesCluster.For(powerPlatformApiTarget)));
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
            sp.GetRequiredService<IClock>()));
        services.AddSingleton<EvaluationServices>();

        var telemetryPreferences =
            FileTelemetryPreferenceStore.CreateForAhConfigDirectory(options.ConfigDirectory);
        services.AddSingleton<ITelemetryPreferenceStore>(telemetryPreferences);
        services.AddSingleton(sp => new TelemetryDestinationResolver(
            powerPlatformApiTarget,
            sp.GetRequiredService<IEnvironmentGeographyClient>(),
            TelemetryIngestionConfiguration.ProductionKey,
            TelemetryIngestionConfiguration.NonProductionKey,
            sp.GetRequiredService<CommandTelemetryTenantObserver>()));
        services.AddSingleton<ITelemetryDestinationResolver>(
            sp => SelectTelemetryDestinationResolver(
                invocation.RingKnown,
                sp.GetRequiredService<TelemetryDestinationResolver>()));
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

        var cli = new AgentHostCli(
            operations,
            provider.GetRequiredService<CommandTelemetrySessionFactory>(),
            new CompositeHandlerProvider(new FacadeHandlerProvider(operations), provider),
            featureFlags,
            provider.GetRequiredService<CommandTelemetrySessionAccessor>());

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

    /// <summary>
    /// Resolves the Power Platform API ring from the ring the active sign-in records, before the
    /// container that depends on it exists.
    /// </summary>
    /// <param name="configDirectory">The resolved config directory holding the profiles file.</param>
    /// <returns>The target the host builds its clients against.</returns>
    internal static async Task<PowerPlatformApiTargetConfig> ResolvePowerPlatformApiTargetAsync(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);

        return PowerPlatformApiRingSelection.TargetFor(
            await ActiveProfileRing.ReadAsync(ProfilesPath(configDirectory)).ConfigureAwait(false));
    }

    /// <summary>
    /// The cloud one invocation runs against, and whether the stored sign-in actually named it.
    /// </summary>
    /// <param name="Target">The endpoints the host builds its clients for.</param>
    /// <param name="RingKnown">
    /// Whether <paramref name="Target"/> came from the stored sign-in. False where the ring could not
    /// be read and the command runs anyway, which is a fallback rather than a classification.
    /// </param>
    internal readonly record struct InvocationTarget(PowerPlatformApiTargetConfig Target, bool RingKnown);

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
            return new InvocationTarget(await ResolvePowerPlatformApiTargetAsync(configDirectory).ConfigureAwait(false), RingKnown: true);
        }
        catch (Exception failure) when (
            GlobalOptions.IsRingIndependent(args)
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

        return $"The cloud this command targets could not be read from the stored sign-in, so nothing was run: {failure.Message} "
            + $"Run `msagent auth logout` to clear the local sign-in state, then `msagent auth login` to sign in again. "
            + $"Deleting {ProfilesPath(configDirectory)} has the same effect as the logout.";
    }

    private static string ProfilesPath(string configDirectory) =>
        Path.Combine(configDirectory, AgentHostAuthOptions.ProfilesFileName);

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
