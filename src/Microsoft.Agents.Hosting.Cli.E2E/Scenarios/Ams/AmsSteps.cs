using System.IO.Compression;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.Ams;

/// <summary>A code agent a scenario created on the Agent Management Service.</summary>
public sealed record AmsAgent(string Name, string AgentId);

/// <summary>A deployment record as msagent printed it.</summary>
public sealed record AmsDeployment(string DeploymentId, string DeploymentName, string TargetEnvironmentId, string DeploymentType, string? EntraId, string State, string Kind, string? Endpoint, string? RequestedEndpoint, bool EndpointChangePending, string? CommitId, string? BotName, string? BotEndpoint)
{
    /// <summary>Reads a deployment object msagent printed.</summary>
    public static AmsDeployment From(JsonElement deployment) => new(Required(deployment, "deploymentId"), Required(deployment, "deploymentName"), Required(deployment, "targetEnvironmentId"), Required(deployment, "deploymentType"), AmsOutput.Text(deployment, "entraId"), Required(deployment, "state"), Required(deployment, "kind"), AmsOutput.Text(deployment, "endpoint"), AmsOutput.Text(deployment, "requestedEndpoint"), AmsOutput.Find(deployment, "endpointChangePending") is { ValueKind: JsonValueKind.True }, AmsOutput.Text(deployment, "commitId"), AmsOutput.Text(deployment, "botService", "name"), AmsOutput.Text(deployment, "botService", "endpoint"));

    private static string Required(JsonElement deployment, string property) => AmsOutput.Text(deployment, property) ?? throw new E2EException($"msagent printed a deployment without {property}.");
}

/// <summary>What an agent deploy printed: the deployment, whether the deploy created or kept the bot, the endpoint it left, whether it switched kind, and whether it needed a second attempt, after which those flags describe only that attempt.</summary>
public sealed record AmsDeployOutcome(AmsDeployment Deployment, string? Bot, string? PreviousEndpoint, bool KindSwitched, bool Retried = false);

/// <summary>A deployment record and the agent it belongs to.</summary>
public sealed record AmsRecordedDeployment(AmsAgent Agent, AmsDeployment Deployment);

/// <summary>The run's pooled Dev deployment record bound to its own App Registration, as its create printed it.</summary>
public sealed record AmsDevRecord(AmsAgent Agent, AmsDeployment Created, string Endpoint, string ApplicationId);

/// <summary>The run's pooled Dev deployment with its bot, and what the deploy that made the bot reported.</summary>
public sealed record AmsLiveDeployment(AmsAgent Agent, AmsDeployOutcome FirstDeploy, string Endpoint)
{
    /// <summary>The deployment as the deploy that made its bot left it.</summary>
    public AmsDeployment Deployment => FirstDeploy.Deployment;
}

/// <summary>The run's pooled agent for hosted deploys: two uploads and a Test deployment that is not yet deployed.</summary>
public sealed record AmsHostedAgent(AmsAgent Agent, string OlderCommit, string NewerCommit, AmsDeployment TestDeployment);

/// <summary>Steps every Agent Management Service scenario shares, each one reading what msagent printed.</summary>
public static class AmsSteps
{
    /// <summary>The feature of the code-agent scenarios that run against the Agent Management Service.</summary>
    public const string Feature = "ams-code-agent";

    /// <summary>The pack of the slow hosted-deploy scenarios that leave hosted runtime copies behind.</summary>
    public const string HostedPack = "ams-hosted";

    private const string AgentPool = "ams-agent";

    private const string DevRecordPool = "ams-dev-record";

    private const string DeployedPool = "ams-deployed";

    private const string RecordPool = "ams-record";

    private const string RecordAgentPool = "ams-record-agent";

    private const string HostedPool = "ams-hosted";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan DeployTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The run's Agent Management Service services.</summary>
    public static AmsServices Services(ScenarioContext context) => context.Ams ?? throw new E2EException($"{context.ScenarioId} needs the Agent Management Service, which the capability check should have provided.");

    /// <summary>A name unique to the run and the label.</summary>
    public static string Name(ScenarioContext context, string label) => $"ahe2e-{context.RunSuffix}-{label}".ToLowerInvariant();

    /// <summary>A dev tunnel shaped endpoint unique to the run and the label, which nothing listens on.</summary>
    public static string DevEndpoint(ScenarioContext context, string label) => $"https://{Name(context, label)}.usw2.devtunnels.ms/api/messages";

    /// <summary>Whether two service ids name the same record, which the service prints in either case.</summary>
    public static bool SameId(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>The options that name a deployment by its agent's id and its own.</summary>
    public static string[] ById(AmsAgent agent, AmsDeployment deployment) => ["--agent-id", agent.AgentId, "--deployment-id", deployment.DeploymentId];

    /// <summary>The options that name a deployment by its agent's name and its own.</summary>
    public static string[] ByName(AmsAgent agent, AmsDeployment deployment) => ["--agent-name", agent.Name, "--deployment-name", deployment.DeploymentName];

    /// <summary>Runs a command in the home environment, as JSON and without prompts.</summary>
    public static Task<MsagentResult> RunAsync(ScenarioContext context, IReadOnlyList<string> arguments, string? stdin = null, TimeSpan? timeout = null) => RunPromptedAsync(context, [.. arguments, "--environment-id", Services(context).HomeEnvironmentId, "--json", "--non-interactive"], stdin, timeout);

    /// <summary>Runs a command exactly as given, for the prompts a scenario answers itself.</summary>
    public static Task<MsagentResult> RunPromptedAsync(ScenarioContext context, IReadOnlyList<string> arguments, string? stdin = null, TimeSpan? timeout = null) => Services(context).Msagent.RunAsync(arguments, context.Workspaces.EmptyDirectory(context.ScenarioId, "ams"), stdin, timeout ?? CommandTimeout, cancellationToken: context.CancellationToken);

    /// <summary>The JSON object msagent printed, failing the scenario when there is none.</summary>
    public static JsonElement Root(MsagentResult result) => AmsOutput.Document(result) ?? throw new E2EException($"`{result.CommandLine}` printed no JSON object: {result.Output.Trim()}");

    /// <summary>The deployment an msagent result printed.</summary>
    public static AmsDeployment Deployment(MsagentResult result) => AmsOutput.Find(Root(result), "deployment") is { } deployment ? AmsDeployment.From(deployment) : throw new E2EException($"`{result.CommandLine}` printed no deployment.");

    /// <summary>What an agent deploy printed.</summary>
    public static AmsDeployOutcome Deployed(MsagentResult result)
    {
        var root = Root(result);
        return new AmsDeployOutcome(Deployment(result), AmsOutput.Text(root, "bot"), AmsOutput.Text(root, "previousEndpoint"), AmsOutput.Find(root, "kindSwitched") is { ValueKind: JsonValueKind.True });
    }

    /// <summary>Whether an endpoint is the hosted runtime address of the agent in an environment.</summary>
    public static bool IsHostedRuntimeEndpoint(string? endpoint, AmsAgent agent, string environmentId) => endpoint is not null && endpoint.Contains($"/environments/{environmentId}/agents/{agent.AgentId}/api/messages", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a failure is a server error, an outcome msagent could not learn, or a network failure before any request reached the service, the only failures a scenario tries again.</summary>
    public static bool IsTransient(MsagentResult result) => AmsOutput.ErrorKind(result) switch
    {
        "request-failed" => ErrorMessage(result) is { } message && message.Contains("HTTP 5", StringComparison.Ordinal),
        "deploy-request-timeout" or "deploy-transport-failed" => true,
        null => result.ExitCode != 0 && ErrorMessage(result) is { } message && message.StartsWith("HttpRequestException:", StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>Reads, trying once more after a server error.</summary>
    public static async Task<MsagentResult> ReadAsync(ScenarioContext context, IReadOnlyList<string> arguments)
    {
        var result = await RunAsync(context, arguments).ConfigureAwait(false);
        return IsTransient(result) ? await AfterDelayAsync(context, "read", () => RunAsync(context, arguments)).ConfigureAwait(false) : result;
    }

    /// <summary>Reads a deployment by its agent's id and its own.</summary>
    public static Task<MsagentResult> ShowDeploymentAsync(ScenarioContext context, AmsAgent agent, AmsDeployment deployment) => ReadAsync(context, ["deployment", "show", .. ById(agent, deployment)]);

    /// <summary>The arguments that create a code agent under a name with a fresh idempotency key.</summary>
    public static string[] CreateAgentArguments(string name) => ["agent", "create", "--agent-type", "CodeAgent", "--agent-name", name, "--language", "dotnet", "--language-version", "10", "--idempotency-key", Guid.NewGuid().ToString()];

    /// <summary>Creates a code agent under a run-unique name, recorded before the create so cleanup finds it even when the create never answers.</summary>
    public static async Task<AmsAgent> CreateAgentAsync(ScenarioContext context, string label)
    {
        var services = Services(context);
        var name = Name(context, label);
        await RequireFreeAgentNameAsync(context, name).ConfigureAwait(false);
        context.Ledger.RecordIntendedAmsAgent(name, services.CleanupContext, context.ScenarioId);

        var arguments = CreateAgentArguments(name);
        var created = await RunAsync(context, arguments).ConfigureAwait(false);
        created = IsReplayableCreate(created) ? await AfterDelayAsync(context, "agent create", () => RunAsync(context, arguments)).ConfigureAwait(false) : created;
        context.Assert.ExitZero(created, $"agent create {name}");

        var root = Root(created);
        var agentId = AmsOutput.Text(root, "agentId") ?? throw new E2EException($"agent create {name} printed no agentId.");
        context.Ledger.ResolveAmsAgent(name, agentId);
        context.Assert.Equal(name, AmsOutput.Text(root, "displayName"), $"agent create {name} returns its name");
        context.Assert.Equal(services.HomeEnvironmentId, AmsOutput.Text(root, "environmentId"), $"agent create {name} makes it in the home environment");
        return new AmsAgent(name, agentId);
    }

    /// <summary>Records for cleanup a code agent that a create the scenario expected to be refused made, or may have made.</summary>
    public static void RecordUnexpectedAgent(ScenarioContext context, MsagentResult created, string name)
    {
        if (AmsOutput.Succeeded(created) && AmsOutput.Text(Root(created), "agentId") is { } agentId)
        {
            context.Ledger.RecordAmsAgent(name, agentId, Services(context).CleanupContext, context.ScenarioId);
        }
        else if (IsReplayableCreate(created))
        {
            context.Ledger.RecordIntendedAmsAgent(name, Services(context).CleanupContext, context.ScenarioId);
        }
    }

    /// <summary>Creates an App Registration with its service principal under a run-unique name, recorded before the create.</summary>
    public static async Task<string> CreateAppRegistrationAsync(ScenarioContext context, string label)
    {
        var displayName = $"{Name(context, label)}-app";
        var (existing, failure) = await context.Cloud.AppRegistrationIdsAsync(displayName, context.CancellationToken).ConfigureAwait(false);

        if (failure is not null || existing.Count > 0)
        {
            throw new ScenarioBlockedException(failure ?? $"an App Registration named {displayName} exists already, and the harness never adopts a resource it did not create", failure is null ? "App Registration name in use" : "az could not list App Registrations");
        }

        context.Ledger.RecordIntendedEntraApp(displayName, Services(context).TenantId, context.ScenarioId);
        var applicationId = await context.Cloud.CreateAppRegistrationAsync(displayName, context.CancellationToken).ConfigureAwait(false);
        context.Ledger.ResolveEntraApp(displayName, applicationId);
        await context.Cloud.CreateServicePrincipalAsync(applicationId, context.CancellationToken).ConfigureAwait(false);
        return applicationId;
    }

    /// <summary>The arguments that create a deployment record of an agent.</summary>
    public static string[] CreateDeploymentArguments(AmsAgent agent, string deploymentName, string targetEnvironmentId, string deploymentType, string entraId, string? endpoint) => ["deployment", "create", "--agent-id", agent.AgentId, "--target-environment-id", targetEnvironmentId, "--deployment-name", deploymentName, "--deployment-type", deploymentType, "--entra-id", entraId, "--entra-id-type", "app-reg", .. endpoint is null ? Array.Empty<string>() : ["--endpoint-url", endpoint]];

    /// <summary>Creates a deployment record under a run-unique name; when msagent could not learn whether the create landed, it waits, reads, and creates again only if nothing landed.</summary>
    public static async Task<AmsDeployment> CreateDeploymentAsync(ScenarioContext context, AmsAgent agent, string label, string targetEnvironmentId, string deploymentType, string entraId, string? endpoint)
    {
        var deploymentName = Name(context, label);
        var arguments = CreateDeploymentArguments(agent, deploymentName, targetEnvironmentId, deploymentType, entraId, endpoint);
        var created = await RunAsync(context, arguments).ConfigureAwait(false);
        created = IsTransient(created) ? await AfterDelayAsync(context, "deployment create", () => LandedOrCreatedAsync(context, agent, deploymentName, arguments)).ConfigureAwait(false) : created;

        RequireOpenDevEndpointGate(created, endpoint);
        context.Assert.ExitZero(created, $"deployment create {deploymentName}");
        return Deployment(created);
    }

    /// <summary>Changes the endpoint a Dev deployment requests.</summary>
    public static async Task<MsagentResult> UpdateEndpointAsync(ScenarioContext context, AmsAgent agent, AmsDeployment deployment, string endpoint)
    {
        var updated = await RunAsync(context, ["deployment", "update", .. ById(agent, deployment), "--endpoint-url", endpoint]).ConfigureAwait(false);
        RequireOpenDevEndpointGate(updated, endpoint);
        return updated;
    }

    /// <summary>Puts a deployment record live, trying once more after a server error, with the hosted runtime copy a package deploy leaves recorded before the call.</summary>
    public static async Task<MsagentResult> DeployAsync(ScenarioContext context, AmsAgent agent, AmsDeployment deployment, IReadOnlyList<string>? selector = null, string? commitId = null) => (await AttemptDeployAsync(context, agent, deployment, selector, commitId).ConfigureAwait(false)).Result;

    /// <summary>Puts a deployment record live and reads what the deploy printed, failing the scenario unless the deploy succeeds.</summary>
    public static async Task<AmsDeployOutcome> DeployOrFailAsync(ScenarioContext context, AmsAgent agent, AmsDeployment deployment, string step, IReadOnlyList<string>? selector = null, string? commitId = null)
    {
        var (deployed, retried) = await AttemptDeployAsync(context, agent, deployment, selector, commitId).ConfigureAwait(false);
        context.Assert.ExitZero(deployed, $"agent deploy {step}");
        return Deployed(deployed) with { Retried = retried };
    }

    /// <summary>Records a Dev deployment at a dev tunnel shaped endpoint under its own App Registration and deploys it, which creates its bot.</summary>
    public static async Task<AmsDeployOutcome> DeployDevAsync(ScenarioContext context, AmsAgent agent, string label, string targetEnvironmentId)
    {
        var record = await CreateDeploymentAsync(context, agent, label, targetEnvironmentId, "dev", await CreateAppRegistrationAsync(context, label).ConfigureAwait(false), DevEndpoint(context, label)).ConfigureAwait(false);
        return await DeployOrFailAsync(context, agent, record, $"of {record.DeploymentName}").ConfigureAwait(false);
    }

    /// <summary>Uploads a copy of the hosted echo package with an entry naming the run, agent and key, so each upload has its own commit, and returns the commit.</summary>
    public static async Task<string> UploadAsync(ScenarioContext context, AmsAgent agent, string key)
    {
        var package = Path.Combine(context.Workspaces.EmptyDirectory(context.ScenarioId, "ams"), $"upload-{key}.zip");
        File.Copy(Services(context).Config.PackagePath ?? throw new E2EException("ams.packagePath is required, which the capability check should have caught."), package, overwrite: true);

        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(archive.CreateEntry("ahe2e-upload.txt").Open());
            await writer.WriteAsync($"msagent e2e run {context.RunSuffix}, agent {agent.AgentId}, upload {key}").ConfigureAwait(false);
        }

        var uploaded = await RunAsync(context, ["agent", "package", "upload", "--agent-id", agent.AgentId, "--package", package], timeout: DeployTimeout).ConfigureAwait(false);
        context.Assert.ExitZero(uploaded, $"package upload {key}");
        return AmsOutput.Text(Root(uploaded), "commitId") ?? throw new E2EException($"package upload {key} printed no commitId.");
    }

    /// <summary>Waits until the service can deploy the newest upload, which it cannot do for about a minute after the upload.</summary>
    public static Task WaitForUploadsAsync(ScenarioContext context)
    {
        var settle = Services(context).Timing.UploadSettle;
        context.Transcript.Note($"waiting {settle.TotalSeconds:N0}s, until the service can deploy the newest upload");
        return Task.Delay(settle, context.CancellationToken);
    }

    /// <summary>The run's pooled agent, created on first use.</summary>
    public static Task<AmsAgent> PooledAgentAsync(ScenarioContext context) => context.Session.SettledAsync(AgentPool, "the pooled agent", () => CreateAgentAsync(context, "pool"));

    /// <summary>The run's pooled Dev deployment record at a dev tunnel shaped endpoint, bound to its own App Registration, created on first use.</summary>
    public static Task<AmsDevRecord> PooledDevRecordAsync(ScenarioContext context) => context.Session.SettledAsync(DevRecordPool, "the pooled Dev deployment record", async () =>
    {
        var agent = await PooledAgentAsync(context).ConfigureAwait(false);
        var endpoint = DevEndpoint(context, "pool-dev");
        var applicationId = await CreateAppRegistrationAsync(context, "pool-dev").ConfigureAwait(false);
        return new AmsDevRecord(agent, await CreateDeploymentAsync(context, agent, "pool-dev", Services(context).HomeEnvironmentId, "dev", applicationId, endpoint).ConfigureAwait(false), endpoint, applicationId);
    });

    /// <summary>The run's pooled Dev deployment, deployed with its bot by agent and deployment name on first use.</summary>
    public static Task<AmsLiveDeployment> PooledDeploymentAsync(ScenarioContext context) => context.Session.SettledAsync(DeployedPool, "the pooled Dev deployment", async () =>
    {
        var record = await PooledDevRecordAsync(context).ConfigureAwait(false);
        return new AmsLiveDeployment(record.Agent, await DeployOrFailAsync(context, record.Agent, record.Created, "of the pooled Dev deployment", ByName(record.Agent, record.Created)).ConfigureAwait(false), record.Endpoint);
    });

    /// <summary>The run's second pooled agent, which holds the pooled deployment record, since an agent has one deployment per target environment.</summary>
    public static Task<AmsAgent> RecordAgentAsync(ScenarioContext context) => context.Session.SettledAsync(RecordAgentPool, "the pooled record agent", () => CreateAgentAsync(context, "records"));

    /// <summary>The run's pooled Dev deployment record in the home environment of the pooled record agent, never deployed, so it needs no App Registration.</summary>
    public static Task<AmsRecordedDeployment> PooledRecordAsync(ScenarioContext context) => context.Session.SettledAsync(RecordPool, "the pooled deployment record", async () =>
    {
        var agent = await RecordAgentAsync(context).ConfigureAwait(false);
        return new AmsRecordedDeployment(agent, await CreateDeploymentAsync(context, agent, "pool-record", Services(context).HomeEnvironmentId, "dev", Guid.NewGuid().ToString(), DevEndpoint(context, "pool-record")).ConfigureAwait(false));
    });

    /// <summary>The run's pooled agent for hosted deploys, with two uploads and a Test deployment, created on first use.</summary>
    public static Task<AmsHostedAgent> HostedAgentAsync(ScenarioContext context) => context.Session.SettledAsync(HostedPool, "the hosted agent", async () =>
    {
        var agent = await CreateAgentAsync(context, "hosted").ConfigureAwait(false);
        var older = await UploadAsync(context, agent, "1").ConfigureAwait(false);
        var newer = await UploadAsync(context, agent, "2").ConfigureAwait(false);
        var test = await CreateDeploymentAsync(context, agent, "hosted-test", Services(context).TestEnvironmentId, "test", await CreateAppRegistrationAsync(context, "hosted-test").ConfigureAwait(false), null).ConfigureAwait(false);
        await WaitForUploadsAsync(context).ConfigureAwait(false);
        return new AmsHostedAgent(agent, older, newer, test);
    });

    private static bool IsReplayableCreate(MsagentResult result) => IsTransient(result) || AmsOutput.ErrorKind(result) is "agent-create-timeout" or "agent-create-transport-failed" or "agent-create-response-unreadable" or "agent-create-in-progress" or "agent-create-concurrency-conflict";

    private static string? ErrorMessage(MsagentResult result) => AmsOutput.Document(result) is { } root ? AmsOutput.Text(root, "errorMessage") : null;

    private static bool RunsPackage(AmsDeployment deployment, string? commitId) => commitId is not null || deployment.DeploymentType != "Dev" || (deployment.Kind == "package" && deployment.RequestedEndpoint is null);

    private static async Task<(MsagentResult Result, bool Retried)> AttemptDeployAsync(ScenarioContext context, AmsAgent agent, AmsDeployment deployment, IReadOnlyList<string>? selector, string? commitId)
    {
        var runsPackage = RunsPackage(deployment, commitId);

        if (runsPackage)
        {
            context.Ledger.RecordRetainedAmsRuntimeCopy(agent.AgentId, agent.Name, deployment.DeploymentName, deployment.TargetEnvironmentId, context.ScenarioId);
        }

        string[] arguments = ["agent", "deploy", .. selector ?? ById(agent, deployment), .. commitId is null ? Array.Empty<string>() : ["--commit-id", commitId]];
        var deployed = await RunAsync(context, arguments, timeout: DeployTimeout).ConfigureAwait(false);
        var retried = IsTransient(deployed);
        deployed = retried ? await AfterDelayAsync(context, "agent deploy", () => RunAsync(context, arguments, timeout: DeployTimeout)).ConfigureAwait(false) : deployed;
        RequireOpenDevEndpointGate(deployed, runsPackage ? null : deployment.RequestedEndpoint ?? deployment.Endpoint);
        return (deployed, retried);
    }

    private static async Task<MsagentResult> LandedOrCreatedAsync(ScenarioContext context, AmsAgent agent, string deploymentName, IReadOnlyList<string> arguments)
    {
        string[] show = ["deployment", "show", "--agent-id", agent.AgentId, "--deployment-name", deploymentName];
        var landed = await ReadAsync(context, show).ConfigureAwait(false);

        if (AmsOutput.Succeeded(landed))
        {
            return landed;
        }

        var created = await RunAsync(context, arguments).ConfigureAwait(false);

        return AmsOutput.IsRefused(created, "deployment-name-in-use") && await ReadAsync(context, show).ConfigureAwait(false) is { } shown && AmsOutput.Succeeded(shown) ? shown : created;
    }

    private static void RequireOpenDevEndpointGate(MsagentResult result, string? endpoint)
    {
        if (endpoint is not null && AmsOutput.IsRefused(result, AmsOutput.InvalidFieldValue))
        {
            throw new ScenarioBlockedException($"AMS refused the dev tunnel endpoint {endpoint}, which it does while the EnableCodeAgentCustomDevEndpoint gate is closed for the target environment", "EnableCodeAgentCustomDevEndpoint is closed");
        }
    }

    private static async Task RequireFreeAgentNameAsync(ScenarioContext context, string name)
    {
        var shown = await ReadAsync(context, ["agent", "show", "--agent-name", name]).ConfigureAwait(false);

        if (!AmsOutput.IsRefused(shown, AmsOutput.AgentNotFound))
        {
            throw new ScenarioBlockedException($"agent show --agent-name {name} did not answer agent-not-found (exit {shown.ExitCode}), and the harness never adopts a resource it did not create", "agent name in use");
        }
    }

    private static async Task<MsagentResult> AfterDelayAsync(ScenarioContext context, string label, Func<Task<MsagentResult>> run)
    {
        var delay = Services(context).Timing.RetryDelay;
        context.Transcript.Note($"{label} hit a server error or an outcome msagent could not learn; one more try in {delay.TotalSeconds:N0}s");
        await Task.Delay(delay, context.CancellationToken).ConfigureAwait(false);
        return await run().ConfigureAwait(false);
    }
}
