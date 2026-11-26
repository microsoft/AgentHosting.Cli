using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using static Microsoft.Agents.Hosting.Cli.Core.Ams.AmsJson;
using AmsRefusal = (string Message, int ExitCode, string ErrorKind, string Remediation);

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>Reads, lists, writes, deploys and deletes the deployment records of AMS agents through AMS-CMP, validating delegated-token claims before each call.</summary>
public sealed class AmsDeploymentAdapter : IAmsDeploymentTransport
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private static readonly AmsRefusal DeleteUnavailable = ("AMS does not delete code-agent deployment records in this environment yet, so nothing was deleted.", ExitCodes.Failure, "deployment-delete-unavailable", "The AMS release that serves this environment does not delete deployment records yet; ask the AMS team when it will.");

    private readonly AmsRequestSender _sender;

    /// <summary>Creates the adapter over a client whose handler must not follow redirects.</summary>
    public AmsDeploymentAdapter(HttpClient httpClient, ITokenProvider tokens, PpapiTokenClaimsValidator? claimsValidator = null, IProfileResourceTokenProvider? profileTokens = null) => _sender = new AmsRequestSender(httpClient, tokens, claimsValidator, profileTokens);

    private enum DeploymentRoute
    {
        Read,
        List,
        Create,
        Update,
        Deploy,
        Delete,
    }

    /// <inheritdoc />
    public Task<AmsDeploymentResult> ReadAsync(AmsDeploymentReadTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendForDeploymentAsync(Call(DeploymentRoute.Read, HttpMethod.Get, AmsRoutes.Deployment(request.ServiceTarget, request.AgentId, request.DeploymentId), request.ServiceTarget, PowerPlatformApiPermissions.AppsRead, "Deployment read"), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AmsDeploymentListResult> ListAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        IReadOnlyList<AgentAmsKeptBot> keptBots = [];
        var deployments = await AmsPaging.ReadAllAsync(_sender, skipToken => Call(DeploymentRoute.List, HttpMethod.Get, AmsRoutes.DeploymentListPage(request.ServiceTarget, request.AgentId, skipToken), request.ServiceTarget, PowerPlatformApiPermissions.AppsRead, "Deployment list"), ParseDeployment, root => keptBots = [.. (GetOptionalArray(root, "keptBots") ?? []).Select(ParseKeptBot)], cancellationToken).ConfigureAwait(false);
        return new AmsDeploymentListResult { Deployments = deployments, KeptBots = keptBots };
    }

    /// <inheritdoc />
    public Task<AmsDeploymentResult> CreateAsync(AmsDeploymentCreateTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new DeploymentCreateWireRequest { AgentType = "CodeAgent", DeploymentType = request.DeploymentType, DeploymentName = request.DeploymentName, EnvironmentId = request.TargetEnvironmentId, Audience = request.EntraId, EntraAppIdType = request.EntraIdType, Language = request.Language, LanguageVersion = request.LanguageVersion, RequestedEndpoint = request.RequestedEndpoint };
        return SendForDeploymentAsync(Call(DeploymentRoute.Create, HttpMethod.Post, AmsRoutes.Deployments(request.ServiceTarget, request.AgentId), request.ServiceTarget, PowerPlatformApiPermissions.AppsUpdate, "Deployment create") with { Content = JsonContent.Create(body, options: RequestJson), OutcomeMayBeIndeterminate = true }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AmsDeploymentResult> UpdateAsync(AmsDeploymentUpdateTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new DeploymentUpdateWireRequest { DeploymentName = request.DeploymentName, RequestedEndpoint = request.RequestedEndpoint };
        return SendForDeploymentAsync(Call(DeploymentRoute.Update, HttpMethod.Patch, AmsRoutes.Deployment(request.ServiceTarget, request.AgentId, request.DeploymentId), request.ServiceTarget, PowerPlatformApiPermissions.AppsUpdate, "Deployment update") with { Content = JsonContent.Create(body, options: RequestJson), IfMatch = request.ETag, OutcomeMayBeIndeterminate = true }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<AmsDeploymentResult> DeployAsync(AmsDeploymentDeployTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new DeploymentDeployWireRequest { CommitId = request.CommitId };
        return SendForDeploymentAsync(Call(DeploymentRoute.Deploy, HttpMethod.Post, AmsRoutes.DeploymentDeploy(request.ServiceTarget, request.AgentId, request.DeploymentId), request.ServiceTarget, PowerPlatformApiPermissions.AppsUpdate, "Deployment deploy") with { Content = JsonContent.Create(body, options: RequestJson), IfMatch = request.ETag, OutcomeMayBeIndeterminate = true }, cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(AmsDeploymentDeleteTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _sender.SendAsync(Call(DeploymentRoute.Delete, HttpMethod.Delete, AmsRoutes.Deployment(request.ServiceTarget, request.AgentId, request.DeploymentId), request.ServiceTarget, PowerPlatformApiPermissions.AppsUpdate, "Deployment delete") with { IfMatch = request.ETag, OutcomeMayBeIndeterminate = true }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AmsDeploymentResult> SendForDeploymentAsync(AmsCall call, CancellationToken cancellationToken)
    {
        using var content = call.Content;
        var response = await _sender.SendAsync(call, cancellationToken).ConfigureAwait(false);
        return AmsBotServiceCall.Read(response, call.Phase, "a valid deployment", "deployment", ParseDeployment) with { ClientRequestId = response.ClientRequestId, ServiceRequestId = response.ServiceRequestId };
    }

    private static AmsCall Call(DeploymentRoute route, HttpMethod method, Uri uri, AmsServiceTarget serviceTarget, string permission, string phase) => new() { Method = method, Uri = uri, ServiceTarget = serviceTarget, RequiredPermissions = [permission], Phase = phase, Refusal = (status, code, serviceMessage) => Refusal(route, status, code, serviceMessage) };

    private static AmsRefusal? Refusal(DeploymentRoute route, int status, string? code, string? serviceMessage) =>
        route == DeploymentRoute.Delete && status == (int)HttpStatusCode.NotImplemented ? DeleteUnavailable
        : AmsRefusals.Unavailable(status, code, isNewRoute: route is DeploymentRoute.List or DeploymentRoute.Deploy) ?? RouteRefusal(route, status, code) ?? AmsRefusals.Shared(status, code, serviceMessage, "deployment");

    private static AmsRefusal? RouteRefusal(DeploymentRoute route, int status, string? code) => (route, status) switch
    {
        (DeploymentRoute.Create, (int)HttpStatusCode.Conflict) when AmsRefusals.Is(code, "PreconditionFailed") => ("AMS refused the deployment record: the target environment already has a deployment of this agent with other settings.", ExitCodes.Failure, "agent-deployment-conflict", "`msagent deployment list` shows it. Put it live with `msagent agent deploy`, or delete its record with `msagent deployment delete` before creating another."),
        (DeploymentRoute.Deploy, (int)HttpStatusCode.Conflict) when AmsRefusals.Is(code, "PreconditionFailed") => ("AMS could not put the deployment live: the hosting runtime of the target environment still holds another deployment of this agent or of its Entra application, possibly from a deleted record.", ExitCodes.Failure, "agent-deployment-conflict", "The hosting runtime keeps one deployment per agent and environment until runtime cleanup is available. Use a deployment in another target environment, or delete the agent with `msagent agent delete`."),
        (DeploymentRoute.Deploy, (int)HttpStatusCode.Conflict) when AmsRefusals.Is(code, "AgentBotServiceAppIdInUse") => ("AMS could not create the deployment's Azure Bot Service instance, because another bot already uses the deployment's Entra application id and Azure Bot Service allows one bot per application id.", ExitCodes.Failure, "agent-bot-service-app-id-in-use", "Remove the other bot that uses this application id; when the deleted deployment record of an AMS agent kept it, `msagent agent delete` of that agent removes it. Then run `msagent agent deploy` again."),
        (DeploymentRoute.Deploy, (int)HttpStatusCode.Conflict) when AmsRefusals.Is(code, "AgentPackageRequired") => ("The agent has no package upload to deploy, so nothing changed.", ExitCodes.Failure, "package-required", "Upload a package first: `msagent agent package upload`."),
        (DeploymentRoute.Deploy, (int)HttpStatusCode.NotFound) when AmsRefusals.Is(code, "AgentPackageCommitNotFound") => ("No upload of this agent has that commit, so nothing changed.", ExitCodes.Usage, "commit-not-found", "See `msagent agent commit list` for the agent's commits."),
        (DeploymentRoute.Deploy, (int)HttpStatusCode.Conflict) when AmsRefusals.Is(code, "AgentPackageNotDeployedToTest") => ("AMS deploys the newest upload to Prod only after a Test deployment of the agent runs it, so nothing changed.", ExitCodes.Failure, "prod-needs-test", "Deploy it to Test first: run `msagent agent deploy` for a Test deployment of the agent, then deploy Prod again."),
        _ => null,
    };

    private static AmsDeploymentResult ParseDeployment(JsonElement root)
    {
        var isCustomEndpoint = GetRequiredBoolean(root, "isCustomEndpoint");
        var isDeleting = GetOptionalBoolean(root, "isDeleting") ?? false;
        var state = GetOptionalOpenEnum(root, "state", DeploymentStates) ?? (isDeleting ? AmsDeploymentResult.DeletingState : AmsDeploymentResult.DeployedState);
        var hasPackage = !isCustomEndpoint && state == AmsDeploymentResult.DeployedState;
        return new AmsDeploymentResult
        {
            DeploymentId = GetRequiredString(root, "id"),
            AgentId = GetRequiredString(root, "agentId"),
            TenantId = GetRequiredString(root, "tenantId"),
            HomeEnvironmentId = GetRequiredString(root, "homeEnvironmentId"),
            TargetEnvironmentId = GetRequiredString(root, "environmentId"),
            ETag = GetOptionalString(root, "eTag"),
            State = state,
            PackageRevisionId = hasPackage ? GetRequiredString(root, "packageRevisionId") : GetOptionalString(root, "packageRevisionId"),
            PackageSha256 = (hasPackage ? GetRequiredString(root, "packageSha256") : GetOptionalString(root, "packageSha256"))?.ToLowerInvariant(),
            PackageSizeInBytes = hasPackage ? GetRequiredInt64(root, "packageSizeInBytes") : GetOptionalInt64(root, "packageSizeInBytes"),
            CommitId = GetOptionalString(root, "commitId")?.ToLowerInvariant(),
            AgentType = GetOptionalOpenEnum(root, "agentType", AgentTypes) ?? throw new FormatException("agentType is missing."),
            DeploymentName = GetRequiredString(root, "deploymentName"),
            DeploymentType = GetEnum(root, "deploymentType", DeploymentTypes),
            EntraId = GetRequiredString(root, "entraAppId"),
            EntraIdType = GetEnum(root, "entraAppIdType", EntraIdTypes),
            Language = GetEnum(root, "language", Languages),
            LanguageVersion = GetRequiredString(root, "languageVersion"),
            RuntimeEndpoint = GetOptionalString(root, "endpoint"),
            RequestedEndpoint = GetOptionalString(root, "requestedEndpoint"),
            IsCustomEndpoint = isCustomEndpoint,
            IsQuarantined = GetRequiredBoolean(root, "isQuarantined"),
            IsDeleting = isDeleting,
            ManagedIdentityId = GetOptionalString(root, "managedIdentityId"),
            BotService = ParseBotService(root),
        };
    }

    private static AgentDeploymentBotService? ParseBotService(JsonElement root) => GetOptionalProperty(root, "botService") switch
    {
        null => null,
        { ValueKind: JsonValueKind.Object } binding => new AgentDeploymentBotService { ResourceId = GetRequiredString(binding, "resourceId"), Name = GetRequiredString(binding, "name"), MsaAppId = GetRequiredString(binding, "msaAppId"), MsaAppType = GetOptionalString(binding, "msaAppType"), MsaAppTenantId = GetOptionalString(binding, "msaAppTenantId"), Location = GetOptionalString(binding, "location"), Sku = GetOptionalString(binding, "sku"), Endpoint = GetOptionalString(binding, "endpoint") },
        _ => throw new FormatException("botService is invalid."),
    };

    private static AgentAmsKeptBot ParseKeptBot(JsonElement item) => new() { DeploymentId = GetRequiredString(item, "deploymentId"), DeploymentName = GetOptionalString(item, "deploymentName"), EnvironmentId = GetOptionalString(item, "environmentId"), DeploymentType = GetOptionalOpenEnum(item, "deploymentType", DeploymentTypes), EntraAppId = GetOptionalString(item, "entraAppId"), BotName = GetOptionalString(item, "botName"), DeletedTime = GetOptionalDate(item, "deletedTime") };

    private sealed record DeploymentCreateWireRequest
    {
        public required string AgentType { get; init; }
        public required string DeploymentType { get; init; }
        public required string DeploymentName { get; init; }
        public required string EnvironmentId { get; init; }
        public required string Audience { get; init; }
        public required string EntraAppIdType { get; init; }
        public required string Language { get; init; }
        public required string LanguageVersion { get; init; }
        public string? RequestedEndpoint { get; init; }
    }

    private sealed record DeploymentUpdateWireRequest
    {
        public string? DeploymentName { get; init; }
        public string? RequestedEndpoint { get; init; }
    }

    private sealed record DeploymentDeployWireRequest
    {
        public string? CommitId { get; init; }
    }
}
