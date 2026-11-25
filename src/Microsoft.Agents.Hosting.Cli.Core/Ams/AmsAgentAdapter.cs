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

/// <summary>Reads, lists, changes and deletes AMS agents and lists their package uploads, validating delegated-token claims before each call.</summary>
public sealed class AmsAgentAdapter : IAmsAgentReadTransport, IAmsAgentTransport
{
    private static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly AmsRequestSender _sender;

    /// <summary>Creates the adapter over a client whose handler must not follow redirects.</summary>
    public AmsAgentAdapter(HttpClient httpClient, ITokenProvider tokens, PpapiTokenClaimsValidator? claimsValidator = null, IProfileResourceTokenProvider? profileTokens = null) => _sender = new AmsRequestSender(httpClient, tokens, claimsValidator, profileTokens);

    /// <inheritdoc />
    public async Task<AmsAgentReadResult> GetAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await _sender.SendAsync(Call(HttpMethod.Get, AmsRoutes.Agent(request.ServiceTarget, request.AgentId), request.ServiceTarget, PowerPlatformApiPermissions.AppsRead, "Agent read", isNewRoute: false), cancellationToken).ConfigureAwait(false);
        return AmsBotServiceCall.Read(response, "Agent read", "a valid agent", "agent", ParseAgent);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AmsAgentReadResult>> ListAsync(AmsServiceTarget serviceTarget, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceTarget);

        return AmsPaging.ReadAllAsync(_sender, skipToken => Call(HttpMethod.Get, AmsRoutes.AgentListPage(serviceTarget, skipToken), serviceTarget, PowerPlatformApiPermissions.AppsRead, "Agent list", isNewRoute: false), ParseAgent, readFirstPage: null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AmsAgentReadResult> UpdateAsync(AmsAgentUpdateTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var content = JsonContent.Create(new AgentUpdateWireRequest { DisplayName = request.DisplayName, Description = request.Description }, options: RequestJson);
        var response = await _sender.SendAsync(Call(HttpMethod.Patch, AmsRoutes.Agent(request.ServiceTarget, request.AgentId), request.ServiceTarget, PowerPlatformApiPermissions.AppsUpdate, "Agent update", isNewRoute: false) with { Content = content, IfMatch = request.ETag, OutcomeMayBeIndeterminate = true }, cancellationToken).ConfigureAwait(false);
        return AmsBotServiceCall.Read(response, "Agent update", "a valid agent", "agent", ParseAgent);
    }

    /// <inheritdoc />
    public async Task<AmsAgentCascadeDeleteResult> CascadeDeleteAsync(AmsAgentCascadeDeleteTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = await _sender.SendAsync(Call(HttpMethod.Post, AmsRoutes.AgentCascadeDelete(request.ServiceTarget, request.AgentId), request.ServiceTarget, PowerPlatformApiPermissions.AppsDelete, "Agent delete", isNewRoute: true) with { IfMatch = request.ETag, OutcomeMayBeIndeterminate = true }, cancellationToken).ConfigureAwait(false);
        return AmsBotServiceCall.Read(response, "Agent delete", "a valid delete result", "agent", ParseCascadeDelete);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AmsAgentPackage>> ListPackagesAsync(AmsAgentReadTransportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return AmsPaging.ReadAllAsync(_sender, skipToken => Call(HttpMethod.Get, AmsRoutes.PackageListPage(request.ServiceTarget, request.AgentId, skipToken), request.ServiceTarget, PowerPlatformApiPermissions.AppsRead, "Package list", isNewRoute: true), ParsePackage, readFirstPage: null, cancellationToken);
    }

    /// <summary>Parses an agent definition or an agent list item.</summary>
    internal static AmsAgentReadResult ParseAgent(JsonElement root) => new()
    {
        AgentId = GetRequiredString(root, "id"),
        TenantId = GetRequiredString(root, "tenantId"),
        EnvironmentId = GetRequiredString(root, "environmentId"),
        AgentType = GetOptionalOpenEnum(root, "agentType", AgentTypes) ?? throw new FormatException("agentType is missing."),
        DisplayName = GetOptionalString(root, "displayName"),
        Description = GetOptionalString(root, "description"),
        ETag = GetOptionalString(root, "eTag"),
        State = GetOptionalOpenEnum(root, "state"),
        IsDeleting = GetOptionalBoolean(root, "isDeleting") ?? false,
        RepositoryId = GetOptionalString(root, "repositoryId"),
        RepositoryBindingStatus = GetOptionalOpenEnum(root, "repositoryBindingStatus", RepositoryBindingStatuses),
        Language = GetOptionalOpenEnum(root, "language", Languages),
        LanguageVersion = GetOptionalString(root, "languageVersion"),
        CreatedTime = GetOptionalDate(root, "createdTime"),
        ChangedTime = GetOptionalDate(root, "changedTime"),
    };

    private static AmsCall Call(HttpMethod method, Uri uri, AmsServiceTarget serviceTarget, string permission, string phase, bool isNewRoute) => new() { Method = method, Uri = uri, ServiceTarget = serviceTarget, RequiredPermissions = [permission], Phase = phase, Refusal = (status, code, serviceMessage) => AmsRefusals.Unavailable(status, code, isNewRoute) ?? DeleteRefusal(status, code, serviceMessage) ?? AmsRefusals.Shared(status, code, serviceMessage, "agent") };

    private static AmsRefusal? DeleteRefusal(int status, string? code, string? serviceMessage) => status switch
    {
        (int)HttpStatusCode.Conflict when AmsRefusals.Is(code, "AgentDeployInProgress") => ($"AMS cannot finish deleting the agent yet: {AmsRefusals.Echo(serviceMessage, "a deploy of one of its deployments is still running.")}", ExitCodes.Failure, "agent-deploy-in-progress", "Run `msagent agent delete` again at the time the message names. Until then the agent stays marked for deletion and refuses new deploys."),
        (int)HttpStatusCode.Conflict when AmsRefusals.Is(code, "AgentBotServiceConflict") => ("A bot bound to one of the agent's deployments does not belong to it, so AMS deleted nothing more.", ExitCodes.Failure, "agent-bot-service-conflict", "Do not retry. Ask the AMS team to investigate the bot with the correlation identifiers."),
        _ when AmsRefusals.Is(code, "AgentBotServiceDeletionFailed") || AmsRefusals.Is(code, "AgentBotServiceOutcomeUnknown") => ("AMS could not confirm that it deleted a bot of the agent, so the agent stays marked for deletion.", ExitCodes.Failure, "agent-bot-service-deletion-failed", "Run `msagent agent delete` again; it resumes where it stopped."),
        _ => null,
    };

    private static AmsAgentCascadeDeleteResult ParseCascadeDelete(JsonElement root) => new()
    {
        AgentId = GetRequiredString(root, "agentId"),
        Deployments = [.. (GetOptionalArray(root, "deployments") ?? []).Select(ParseRemovedDeployment)],
        KeptBots = [.. (GetOptionalArray(root, "keptBots") ?? []).Select(ParseRemovedDeployment)],
    };

    private static AgentAmsRemovedDeployment ParseRemovedDeployment(JsonElement item) => new() { DeploymentId = GetRequiredString(item, "deploymentId"), DeploymentName = GetOptionalString(item, "deploymentName"), EnvironmentId = GetOptionalString(item, "environmentId"), DeploymentType = GetOptionalOpenEnum(item, "deploymentType", DeploymentTypes), BotName = GetOptionalString(item, "botName"), BotDeleted = GetOptionalBoolean(item, "botDeleted") ?? false };

    private static AmsAgentPackage ParsePackage(JsonElement item) => new() { PackageRevisionId = GetRequiredString(item, "packageRevisionId"), CommitId = GetOptionalString(item, "commitId")?.ToLowerInvariant(), Sha256 = GetOptionalString(item, "sha256")?.ToLowerInvariant(), SizeInBytes = GetOptionalInt64(item, "sizeInBytes"), CreatedTime = GetOptionalDate(item, "createdTime") };

    private sealed record AgentUpdateWireRequest
    {
        public string? DisplayName { get; init; }
        public string? Description { get; init; }
    }
}
