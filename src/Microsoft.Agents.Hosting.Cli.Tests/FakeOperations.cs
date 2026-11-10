using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Tests;

internal sealed class FakeOperations : IAgentHostingOperations, IAgentEvaluationOperations, IAuthProfileOperations
{
    public Func<AuthCreateRequest, CancellationToken, Task<AuthProfileOperationResult>>? OnAuthCreate { get; init; }
    public AuthCreateRequest? LastAuthCreate { get; private set; }
    public AuthProfileSelectRequest? LastAuthSelect { get; private set; }
    public Core.Ports.AuthProfileSelector? LastAuthDelete { get; private set; }
    public (Core.Ports.AuthProfileSelector Selector, string Name)? LastAuthName { get; private set; }
    internal static AuthProfileInfo SafeAuthProfile => new()
    {
        ProfileId = "opaque-profile", Account = "operator@example.test", Cloud = "Public",
        Name = "Dev", Index = 1, IsActive = true,
    };
    public Task<AuthProfileOperationResult> CreateAsync(AuthCreateRequest request, CancellationToken token)
    {
        LastAuthCreate = request;
        return OnAuthCreate?.Invoke(request, token) ?? Task.FromResult(new AuthProfileOperationResult("created", SafeAuthProfile));
    }
    public Task<AuthProfileListResult> ListAsync(CancellationToken token) => Task.FromResult(new AuthProfileListResult([SafeAuthProfile]));
    public Task<AuthProfileOperationResult> SelectAsync(AuthProfileSelectRequest request, CancellationToken token)
    {
        LastAuthSelect = request;
        return Task.FromResult(new AuthProfileOperationResult("selected", SafeAuthProfile));
    }
    public Task<AuthProfileOperationResult> DeleteAsync(Core.Ports.AuthProfileSelector selector, CancellationToken token)
    {
        LastAuthDelete = selector;
        return Task.FromResult(new AuthProfileOperationResult("deleted", SafeAuthProfile));
    }
    public Task<AuthProfileOperationResult> NameAsync(Core.Ports.AuthProfileSelector selector, string name, CancellationToken token)
    {
        LastAuthName = (selector, name);
        return Task.FromResult(new AuthProfileOperationResult("named", SafeAuthProfile));
    }
    public Task<AuthWhoResult> WhoAsync(CancellationToken token) => Task.FromResult(new AuthWhoResult("selected", SafeAuthProfile, null, null));
    public Task<AuthLogoutResult> ClearAsync(CancellationToken token) => Task.FromResult(new AuthLogoutResult { Status = "signed-out" });
    public Func<AuthLoginRequest, AuthLoginResult>? OnLogin { get; init; }
    public Func<AuthStatusReport>? OnStatus { get; init; }
    public Func<CancellationToken, Task<AuthStatusReport>>? OnStatusAsync { get; init; }
    public Func<string, AuthProfileSummary>? OnSwitch { get; init; }
    public Func<AuthLogoutResult>? OnLogout { get; init; }
    public Func<EnvironmentListResult>? OnEnvironmentList { get; init; }
    public Func<string, EnvironmentSelectionResult>? OnEnvironmentSelect { get; init; }
    public Func<AbsProvisioningRequest, AbsProvisioningReport>? OnPreview { get; init; }
    public Func<AbsProvisioningRequest, AbsProvisioningReport>? OnApply { get; init; }
    public Func<BlueprintIdentityRequest, BlueprintIdentityReport>? OnCreateBlueprintIdentity { get; init; }
    public Func<AppRegistrationIdentityRequest, AppRegistrationIdentityReport>? OnCreateAppRegistrationIdentity { get; init; }
    public Func<AgentCreateRequest, AgentCreateResult>? OnAgentCreate { get; init; }
    public Func<AgentPackageUploadRequest, AgentPackageUploadResult>? OnAgentPackageUpload { get; init; }
    public Func<AgentDeployRequest, AgentDeployResult>? OnAgentDeploy { get; init; }
    public Func<AgentDeploymentPackageUpdateRequest, AgentDeployResult>? OnAgentDeploymentPackageUpdate { get; init; }
    public Func<DeploymentCreateRequest, DeploymentResult<CodeAgentDeployment>>? OnCreateDeployment { get; init; }
    public Func<DeploymentShowRequest, DeploymentResult<CodeAgentDeployment>>? OnGetDeployment { get; init; }
    public Func<DeploymentUpdateRequest, DeploymentResult<CodeAgentDeployment>>? OnUpdateDeployment { get; init; }
    public Func<DeploymentRequest, DeploymentResult<CodeAgentDeployment>>? OnDeleteDeployment { get; init; }
    public Func<DeploymentChannelCreateRequest, DeploymentResult<DeploymentChannelRecord>>? OnCreateChannel { get; init; }
    public Func<DeploymentChannelUpdateRequest, DeploymentResult<DeploymentChannelRecord>>? OnUpdateChannel { get; init; }
    public Func<DeploymentChannelDeleteRequest, DeploymentResult<DeploymentChannelRecord>>? OnDeleteChannel { get; init; }
    public Func<DeploymentChannelShowRequest, DeploymentResult<DeploymentChannelRecord>>? OnGetChannel { get; init; }
    public Func<DeploymentChannelListRequest, DeploymentResult<DeploymentChannelRecord[]>>? OnListChannels { get; init; }

    public AbsProvisioningRequest? LastAbsRequest { get; private set; }
    public DeploymentCreateRequest? LastDeploymentRequest { get; private set; }
    public DeploymentShowRequest? LastGetDeploymentRequest { get; private set; }
    public string? LastGetDeploymentId { get; private set; }
    public string? LastGetDeploymentAgent { get; private set; }
    public string? LastGetDeploymentName { get; private set; }
    public DeploymentUpdateRequest? LastUpdateDeploymentRequest { get; private set; }
    public DeploymentRequest? LastDeleteDeploymentRequest { get; private set; }
    public string? LastDeleteDeploymentId { get; private set; }
    public DeploymentChannelCreateRequest? LastCreateChannelRequest { get; private set; }
    public DeploymentChannelUpdateRequest? LastUpdateChannelRequest { get; private set; }
    public DeploymentChannelDeleteRequest? LastDeleteChannelRequest { get; private set; }
    public DeploymentChannelShowRequest? LastGetChannelRequest { get; private set; }
    public DeploymentChannelListRequest? LastListChannelsRequest { get; private set; }
    public AuthLoginRequest? LastLoginRequest { get; private set; }
    public BlueprintIdentityRequest? LastBlueprintIdentityRequest { get; private set; }

    public AppRegistrationIdentityRequest? LastAppRegistrationIdentityRequest { get; private set; }

    /// <summary>Records the account key passed to switch.</summary>
    public string? LastSwitchAccount { get; private set; }
    public string? LastSelectedEnvironmentId { get; private set; }
    public AgentDeployRequest? LastDeployRequest { get; private set; }
    public AgentDeploymentPackageUpdateRequest? LastAgentDeploymentPackageUpdateRequest { get; private set; }
    public AgentPackageUploadRequest? LastPackageUploadRequest { get; private set; }
    public AgentCreateRequest? LastCreateRequest { get; private set; }

    public Task<AuthLoginResult> LoginAsync(AuthLoginRequest request, CancellationToken cancellationToken)
    {
        LastLoginRequest = request;
        return Task.FromResult((OnLogin ?? (_ => new AuthLoginResult { Status = "signed-in", Account = "op@contoso.com" }))(request));
    }

    public Task<AuthStatusReport> GetAuthStatusAsync(CancellationToken cancellationToken) =>
        OnStatusAsync?.Invoke(cancellationToken) ??
        Task.FromResult((OnStatus ?? (() => new AuthStatusReport { Status = "signed-out", SignedIn = false }))());

    public Task<AuthProfileSummary> SwitchAuthProfileAsync(string accountKey, PowerPlatformApiRing? ring, CancellationToken cancellationToken)
    {
        LastSwitchAccount = accountKey;
        return Task.FromResult((OnSwitch ?? (a => new AuthProfileSummary { Status = "switched", Account = a }))(accountKey));
    }

    public Task<AuthLogoutResult> LogoutAsync(CancellationToken cancellationToken) =>
        Task.FromResult((OnLogout ?? (() => new AuthLogoutResult { Status = "signed-out" }))());

    public Task<EnvironmentListResult> ListEnvironmentsAsync(CancellationToken cancellationToken) =>
        Task.FromResult((OnEnvironmentList ?? (() => new EnvironmentListResult { Status = "listed", Environments = [] }))());

    public Task<EnvironmentSelectionResult> SelectEnvironmentAsync(string environmentId, CancellationToken cancellationToken)
    {
        LastSelectedEnvironmentId = environmentId;
        return Task.FromResult((OnEnvironmentSelect ?? (id => new EnvironmentSelectionResult
        {
            Status = "selected",
            EnvironmentId = id,
            DisplayName = "Contoso Dev",
            TenantId = "tenant-contoso",
            ApiEndpoint = "https://env-1.api.powerplatform.com/",
            SelectedAt = DateTimeOffset.UnixEpoch,
        }))(environmentId));
    }

    public Task<AgentDeployResult> DeployAgentAsync(AgentDeployRequest request, CancellationToken cancellationToken)
    {
        LastDeployRequest = request;
        return Task.FromResult((OnAgentDeploy ?? (input => CreateStubDeployResult() with
        {
            TargetEnvironmentId = input.TargetEnvironmentId,
        }))(request));
    }

    public Task<AgentDeployResult> UpdateAgentDeploymentPackageAsync(
        AgentDeploymentPackageUpdateRequest request,
        CancellationToken cancellationToken)
    {
        LastAgentDeploymentPackageUpdateRequest = request;
        return Task.FromResult((OnAgentDeploymentPackageUpdate ?? (input => CreateStubDeployResult() with
        {
            Status = "agent-deployment-updated",
            AgentId = input.AgentId ?? "44444444-4444-4444-4444-444444444444",
            DeploymentId = input.DeploymentId ?? "77777777-7777-7777-7777-777777777777",
            PackageRevisionId = input.PackageRevisionId ?? "66666666-6666-6666-6666-666666666666",
        }))(request));
    }

    public Task<AgentPackageUploadResult> UploadAgentPackageAsync(
        AgentPackageUploadRequest request,
        CancellationToken cancellationToken)
    {
        LastPackageUploadRequest = request;
        return Task.FromResult(
            (OnAgentPackageUpload ?? (_ => CreateStubPackageUploadResult()))(request));
    }

    public Task<AgentCreateResult> CreateAgentAsync(AgentCreateRequest request, CancellationToken cancellationToken)
    {
        LastCreateRequest = request;
        return Task.FromResult((OnAgentCreate ?? (_ => CreateStubCreateResult()))(request));
    }

    public Func<ManagedAgentCreateRequest, ManagedAgentCreateResult>? OnManagedAgentCreate { get; init; }

    public Func<ManagedAgentInitRequest, ManagedAgentInitResult>? OnManagedAgentInit { get; init; }

    public Func<ManagedAgentListRequest, ManagedAgentListResult>? OnManagedAgentList { get; init; }

    public Func<ManagedAgentDeleteRequest, ManagedAgentDeleteResult>? OnManagedAgentDelete { get; init; }

    public Func<ManagedAgentShareRequest, ManagedAgentShareResult>? OnManagedAgentShare { get; init; }

    public Func<ManagedAgentShareShowRequest, ManagedAgentShareShowResult>? OnManagedAgentShareShow { get; init; }

    public Func<ManagedAgentUnshareRequest, ManagedAgentUnshareResult>? OnManagedAgentUnshare { get; init; }

    public Func<ManagedAgentPullRequest, ManagedAgentPullResult>? OnManagedAgentPull { get; init; }

    public Func<ManagedAgentDeployRequest, ManagedAgentDeployResult>? OnManagedAgentDeploy { get; init; }

    public Func<AgentDeploymentCreateRequest, AgentDeploymentCreateResult>? OnAgentDeploymentCreate { get; init; }

    public Func<AgentDeploymentListRequest, AgentDeploymentListResult>? OnAgentDeploymentList { get; init; }

    public Func<AgentDeploymentConnectionRequest, AgentDeploymentConnectionResult>? OnAgentDeploymentConnection { get; init; }

    public Func<AgentDeploymentConnectionShowRequest, AgentDeploymentConnectionResult>? OnAgentDeploymentConnectionShow { get; init; }

    public AgentDeploymentConnectionShowRequest? LastAgentDeploymentConnectionShowRequest { get; private set; }

    public Func<AgentDeploymentConnectorListRequest, AgentDeploymentConnectorListResult>? OnAgentDeploymentConnectorList { get; init; }

    public AgentDeploymentConnectorListRequest? LastAgentDeploymentConnectorListRequest { get; private set; }

    public Func<AgentDeploymentConnectionCreateRequest, AgentDeploymentConnectionCreateResult>? OnAgentDeploymentConnectionCreate { get; init; }

    public AgentDeploymentConnectionCreateRequest? LastAgentDeploymentConnectionCreateRequest { get; private set; }

    public Func<AgentDeploymentConnectionReferenceCreateRequest, AgentDeploymentConnectionReferenceCreateResult>? OnAgentDeploymentConnectionReferenceCreate { get; init; }

    public AgentDeploymentConnectionReferenceCreateRequest? LastAgentDeploymentConnectionReferenceCreateRequest { get; private set; }

    public Func<AgentDeploymentConnectionRemoveRequest, AgentDeploymentConnectionRemoveResult>? OnAgentDeploymentConnectionRemove { get; init; }

    public AgentDeploymentConnectionRemoveRequest? LastAgentDeploymentConnectionRemoveRequest { get; private set; }

    public Func<DeploymentAgentTypeQuery, DeploymentAgentTypeResult>? OnResolveDeploymentAgentType { get; init; }

    public DeploymentAgentTypeQuery? LastDeploymentAgentTypeQuery { get; private set; }

    /// <summary>Records the last MCS create request the host built, so option binding can be asserted.</summary>
    public ManagedAgentCreateRequest? LastManagedAgentCreateRequest { get; private set; }

    /// <summary>Records the last MCS init request the host built.</summary>
    public ManagedAgentInitRequest? LastManagedAgentInitRequest { get; private set; }

    /// <summary>Records the last MCS list request the host built.</summary>
    public ManagedAgentShowRequest? LastManagedAgentShowRequest { get; private set; }

    public Task<ManagedAgentShowResult> ShowManagedAgentAsync(ManagedAgentShowRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentShowRequest = request;

        return Task.FromResult(new ManagedAgentShowResult
        {
            ProjectDirectory = "/work",
            ConfigPath = "/work/.config/agent.config.json",
            Agents =
            [
                new ManagedAgentRecord
                {
                    AgentId = "agent-1",
                    DisplayName = "Contoso Agent",
                    AgentType = AgentTypes.McsAgent,
                    AgentLocation = AgentConfigLocator.CurrentDirectoryLocation,
                    TenantId = "tenant-1",
                    EnvironmentId = "env-1",
                    Origin = ManagedAgentOrigins.AgentHostCli,
                    AgentState = EntityStates.Saved,
                    EntityTag = "1",
                },
            ],
            Deployments =
            [
                new AgentDeploymentRecord
                {
                    DeploymentId = "slot-1",
                    AgentId = "agent-1",
                    AgentType = AgentTypes.McsAgent,
                    DeploymentType = DeploymentStages.Dev,
                    DeploymentName = "dev",
                    TenantId = "tenant-1",
                    HomeEnvironmentId = "env-1",
                    EnvironmentId = "env-1",
                    EntityTag = "1",
                },
            ],
        });
    }

    public ManagedAgentListRequest? LastManagedAgentListRequest { get; private set; }

    /// <summary>Records the last MCS delete request the host built.</summary>
    public ManagedAgentDeleteRequest? LastManagedAgentDeleteRequest { get; private set; }

    public ManagedAgentShareRequest? LastManagedAgentShareRequest { get; private set; }

    public ManagedAgentShareShowRequest? LastManagedAgentShareShowRequest { get; private set; }

    public ManagedAgentUnshareRequest? LastManagedAgentUnshareRequest { get; private set; }

    /// <summary>Records the last MCS pull request the host built.</summary>
    public ManagedAgentPullRequest? LastManagedAgentPullRequest { get; private set; }

    /// <summary>Records the last MCS deploy request the host built.</summary>
    public ManagedAgentDeployRequest? LastManagedAgentDeployRequest { get; private set; }

    /// <summary>Records the last deployment create request the host built.</summary>
    public AgentDeploymentCreateRequest? LastAgentDeploymentCreateRequest { get; private set; }

    /// <summary>Records the last deployment list request the host built.</summary>
    public AgentDeploymentListRequest? LastAgentDeploymentListRequest { get; private set; }

    /// <summary>Records the last connection update request the host built.</summary>
    public AgentDeploymentConnectionRequest? LastAgentDeploymentConnectionRequest { get; private set; }

    /// <summary>Records the last deployment delete request the host built.</summary>
    public AgentDeploymentDeleteRequest? LastAgentDeploymentDeleteRequest { get; private set; }

    public Task<AgentDeploymentDeleteResult> DeleteAgentDeploymentAsync(AgentDeploymentDeleteRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentDeleteRequest = request;

        return Task.FromResult(new AgentDeploymentDeleteResult
        {
            Status = "deployment-deleted",
            AgentId = request.AgentId ?? "agent-1",
            DisplayName = "Contoso Agent",
            ConfigPath = "C:\\src\\agent\\.config\\agent.config.json",
            Deployments =
            [
                new DeletedDeployment
                {
                    DeploymentId = "slot-1",
                    DeploymentName = request.DeploymentName ?? "dev",
                    DeploymentType = "dev",
                    EnvironmentId = "env-1",
                },
            ],
        });
    }

    /// <summary>Records the last deployment update request the host built.</summary>
    public AgentDeploymentUpdateRequest? LastAgentDeploymentUpdateRequest { get; private set; }

    public Task<AgentDeploymentUpdateResult> UpdateAgentDeploymentAsync(AgentDeploymentUpdateRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentUpdateRequest = request;

        return Task.FromResult(new AgentDeploymentUpdateResult
        {
            AgentId = request.AgentId ?? "agent-1",
            DeploymentId = "slot-1",
            PreviousDeploymentName = request.DeploymentName ?? "dev",
            DeploymentName = request.NewName ?? "dev",
            DeploymentType = "dev",
            EnvironmentId = "env-1",
            ConfigPath = "C:\\src\\agent\\.config\\agent.config.json",
        });
    }

    public Task<ManagedAgentCreateResult> CreateManagedAgentAsync(ManagedAgentCreateRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentCreateRequest = request;
        return Task.FromResult((OnManagedAgentCreate ?? (_ => new ManagedAgentCreateResult
        {
            AgentId = "agent-1",
            DisplayName = request.Name ?? "Agent",
            AgentType = "MCSAgent",
            SchemaName = "cr_agent",
            TenantId = "tenant-contoso",
            EnvironmentId = "env-1",
            ProjectDirectory = "C:\\src\\agent",
            ConfigPath = "C:\\src\\agent\\.config\\agent.config.json",
            CreatedFrom = "scaffold",
        }))(request));
    }

    public Task<ManagedAgentInitResult> InitManagedAgentAsync(ManagedAgentInitRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentInitRequest = request;
        return Task.FromResult((OnManagedAgentInit ?? (_ => new ManagedAgentInitResult
        {
            AgentId = "agent-1",
            DisplayName = request.Name ?? "Agent",
            AgentType = "MCSAgent",
            TenantId = "tenant-contoso",
            EnvironmentId = "env-1",
            ProjectDirectory = "C:\\src\\agent",
            ConfigPath = "C:\\src\\agent\\.config\\agent.config.json",
        }))(request));
    }

    public Task<ManagedAgentListResult> ListManagedAgentsAsync(ManagedAgentListRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentListRequest = request;
        return Task.FromResult((OnManagedAgentList ?? (_ => new ManagedAgentListResult { Scope = request.Tenant ? "tenant" : "environment", Limit = request.Limit ?? 20 }))(request));
    }

    public Task<ManagedAgentDeleteResult> DeleteManagedAgentAsync(ManagedAgentDeleteRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentDeleteRequest = request;
        return Task.FromResult((OnManagedAgentDelete ?? (_ => new ManagedAgentDeleteResult
        {
            Status = "agent-deleted",
            AgentId = "agent-1",
            DisplayName = "Agent",
        }))(request));
    }

    public Task<ManagedAgentShareResult> ShareManagedAgentAsync(ManagedAgentShareRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentShareRequest = request;
        return Task.FromResult((OnManagedAgentShare ?? (_ => new ManagedAgentShareResult
        {
            Status = "shared",
            AgentId = "agent-1",
            DisplayName = "Agent",
            McsAgentId = "bot-1",
            EnvironmentId = "env-1",
            Principal = new ManagedAgentShareEntry { ObjectId = "user-1", PrincipalName = "Ada Lovelace", PrincipalType = "User", Role = "use" },
            Role = "use",
            Changed = true,
        }))(request));
    }

    public Task<ManagedAgentShareShowResult> ShowManagedAgentSharesAsync(ManagedAgentShareShowRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentShareShowRequest = request;
        return Task.FromResult((OnManagedAgentShareShow ?? (_ => new ManagedAgentShareShowResult
        {
            AgentId = "agent-1",
            DisplayName = "Agent",
            McsAgentId = "bot-1",
            EnvironmentId = "env-1",
        }))(request));
    }

    public Task<ManagedAgentUnshareResult> UnshareManagedAgentAsync(ManagedAgentUnshareRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentUnshareRequest = request;
        return Task.FromResult((OnManagedAgentUnshare ?? (_ => new ManagedAgentUnshareResult
        {
            Status = "unshared",
            AgentId = "agent-1",
            DisplayName = "Agent",
            McsAgentId = "bot-1",
            EnvironmentId = "env-1",
            Principal = new ManagedAgentShareEntry { ObjectId = "user-1", PrincipalName = "Ada Lovelace", PrincipalType = "User", Role = "use" },
            Changed = true,
        }))(request));
    }

    public Task<ManagedAgentPullResult> PullManagedAgentAsync(ManagedAgentPullRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentPullRequest = request;
        return Task.FromResult((OnManagedAgentPull ?? (_ => new ManagedAgentPullResult
        {
            Status = "agent-pulled",
            AgentId = "agent-1",
            EnvironmentId = "env-1",
            ProjectDirectory = "C:\\src\\agent",
            Mode = "pull",
        }))(request));
    }

    public Task<ManagedAgentDeployResult> DeployManagedAgentAsync(ManagedAgentDeployRequest request, CancellationToken cancellationToken)
    {
        LastManagedAgentDeployRequest = request;
        return Task.FromResult((OnManagedAgentDeploy ?? (_ => new ManagedAgentDeployResult
        {
            AgentId = "agent-1",
            DisplayName = "Agent",
            DeploymentId = "slot-1",
            DeploymentName = request.DeploymentName ?? "dev",
            DeploymentType = "dev",
            Action = "reattach",
            TargetEnvironmentId = "env-1",
            TargetMcsAgentId = "bot-1",
            SchemaName = "cr_agent",
            UploadedFileCount = 2,
        }))(request));
    }

    public Task<AgentDeploymentCreateResult> CreateAgentDeploymentAsync(AgentDeploymentCreateRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentCreateRequest = request;
        return Task.FromResult((OnAgentDeploymentCreate ?? (_ => new AgentDeploymentCreateResult
        {
            AgentId = request.AgentId ?? "agent-1",
            DeploymentId = "slot-1",
            DeploymentName = request.DeploymentName ?? "dev",
            DeploymentType = request.DeploymentType ?? "dev",
            EnvironmentId = request.EnvironmentId,
            TargetEnvironmentId = request.EnvironmentId ?? "env-1",
            SchemaName = "cr_agent",
        }))(request));
    }

    public Task<AgentDeploymentListResult> ListAgentDeploymentsAsync(AgentDeploymentListRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentListRequest = request;
        return Task.FromResult((OnAgentDeploymentList ?? (_ => new AgentDeploymentListResult { AgentId = request.AgentId ?? "agent-1" }))(request));
    }

    public Task<AgentDeploymentConnectionResult> UpdateAgentDeploymentConnectionAsync(AgentDeploymentConnectionRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentConnectionRequest = request;
        return Task.FromResult((OnAgentDeploymentConnection ?? (_ => new AgentDeploymentConnectionResult
        {
            Status = "connections-updated",
            AgentId = request.AgentId ?? "agent-1",
            DeploymentId = "slot-1",
            DeploymentName = request.DeploymentName ?? "dev",
            EnvironmentId = "env-1",
            Ready = true,
        }))(request));
    }

    public Task<AgentDeploymentConnectionResult> ShowAgentDeploymentConnectionAsync(AgentDeploymentConnectionShowRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentConnectionShowRequest = request;
        return Task.FromResult((OnAgentDeploymentConnectionShow ?? (_ => new AgentDeploymentConnectionResult
        {
            Status = "connections-previewed",
            AgentId = request.AgentId ?? "agent-1",
            DeploymentId = "slot-1",
            DeploymentName = request.DeploymentName ?? "dev",
            EnvironmentId = "env-1",
            ConnectionsReady = true,
            WorkflowsReady = true,
            Ready = true,
        }))(request));
    }

    public DeploymentAgentTypeResult ResolveDeploymentAgentType(DeploymentAgentTypeQuery query)
    {
        LastDeploymentAgentTypeQuery = query;
        return OnResolveDeploymentAgentType?.Invoke(query)
            ?? new DeploymentAgentTypeResult { AgentType = AgentTypes.CodeAgent, Source = DeploymentAgentTypeSource.Default };
    }

    public Task<AgentDeploymentConnectorListResult> ListAgentDeploymentConnectorsAsync(AgentDeploymentConnectorListRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentConnectorListRequest = request;
        return Task.FromResult((OnAgentDeploymentConnectorList ?? (_ => new AgentDeploymentConnectorListResult
        {
            Status = "connectors-listed",
            EnvironmentId = request.EnvironmentId ?? "env-1",
            Limit = request.Limit ?? 50,
        }))(request));
    }

    public Task<AgentDeploymentConnectionCreateResult> CreateAgentDeploymentConnectionAsync(AgentDeploymentConnectionCreateRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentConnectionCreateRequest = request;
        return Task.FromResult((OnAgentDeploymentConnectionCreate ?? (_ => new AgentDeploymentConnectionCreateResult
        {
            Status = "connection-created",
            EnvironmentId = request.EnvironmentId ?? "env-1",
            ConnectorId = request.Connector ?? "shared_teams",
            ConnectionId = "connection-1",
            BindCommand = ConnectionCommandHints.BindConnector(request, request.Connector ?? "shared_teams", "connection-1"),
        }))(request));
    }

    public Task<AgentDeploymentConnectionReferenceCreateResult> CreateAgentDeploymentConnectionReferenceAsync(AgentDeploymentConnectionReferenceCreateRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentConnectionReferenceCreateRequest = request;
        return Task.FromResult((OnAgentDeploymentConnectionReferenceCreate ?? (_ => new AgentDeploymentConnectionReferenceCreateResult
        {
            Status = "connection-reference-bound",
            EnvironmentId = "env-1",
            ConnectionReference = $"cre98_agent.{request.Connector ?? "shared_teams"}.0",
            ConnectorId = request.Connector ?? "shared_teams",
            ConnectionId = request.ConnectionId ?? "connection-1",
            Ready = true,
            BindCommand = ConnectionCommandHints.BindReference(request, $"cre98_agent.{request.Connector ?? "shared_teams"}.0"),
        }))(request));
    }

    public Task<AgentDeploymentConnectionRemoveResult> RemoveAgentDeploymentConnectionAsync(AgentDeploymentConnectionRemoveRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentConnectionRemoveRequest = request;
        return Task.FromResult((OnAgentDeploymentConnectionRemove ?? (_ => new AgentDeploymentConnectionRemoveResult
        {
            Status = request.ConnectionId is null ? "connection-reference-removed" : "connection-removed",
            EnvironmentId = request.EnvironmentId ?? "env-1",
            ConnectorId = request.ConnectionId is null ? null : request.Connector ?? "shared_teams",
            ConnectionId = request.ConnectionId,
            ConnectionReference = request.ConnectionReference,
        }))(request));
    }

    public Task<AbsProvisioningReport> PreviewAzureBotAsync(AbsProvisioningRequest request, CancellationToken cancellationToken)
    {
        LastAbsRequest = request;
        return Task.FromResult((OnPreview ?? (_ => CreateStubReport("planned")))(request));
    }

    public Task<AbsProvisioningReport> ApplyAzureBotAsync(AbsProvisioningRequest request, CancellationToken cancellationToken)
    {
        LastAbsRequest = request;
        return Task.FromResult((OnApply ?? (_ => CreateStubReport("applied")))(request));
    }

    public Task<BlueprintIdentityReport> CreateBlueprintIdentityAsync(BlueprintIdentityRequest request, CancellationToken cancellationToken)
    {
        LastBlueprintIdentityRequest = request;
        return Task.FromResult((OnCreateBlueprintIdentity ?? (_ => new BlueprintIdentityReport
        {
            Status = request.DryRun ? "planned" : "created",
            TenantId = request.TenantId,
            DisplayName = request.DisplayName,
            Changes = [],
            NextActions = [],
        }))(request));
    }

    public Task<AppRegistrationIdentityReport> CreateAppRegistrationIdentityAsync(
        AppRegistrationIdentityRequest request,
        CancellationToken cancellationToken)
    {
        LastAppRegistrationIdentityRequest = request;
        return Task.FromResult(
            (OnCreateAppRegistrationIdentity ?? (_ => new AppRegistrationIdentityReport
            {
                Status = request.DryRun ? "planned" : "created",
                TenantId = request.TenantId,
                DisplayName = request.DisplayName,
                Changes = [],
                NextActions = [],
            }))(request));
    }

    /// <summary>
    /// A secret-free package-upload result for the default host pipeline tests.
    /// </summary>
    internal static AgentPackageUploadResult CreateStubPackageUploadResult() => new()
    {
        Status = "package-uploaded",
        TenantId = "11111111-1111-1111-1111-111111111111",
        EnvironmentId = "22222222-2222-2222-2222-222222222222",
        AgentId = "44444444-4444-4444-4444-444444444444",
        PackageOperationId = "55555555-5555-5555-5555-555555555555",
        PackageRevisionId = "66666666-6666-6666-6666-666666666666",
        PackageDigest = new string('a', 64),
        PackageSizeInBytes = 1024,
    };

    /// <summary>
    /// A secret-free target-routed projection result for the default host pipeline tests.
    /// </summary>
    internal static AgentDeployResult CreateStubDeployResult() => new()
    {
        Status = "deployment-projection-acknowledged",
        TenantId = "11111111-1111-1111-1111-111111111111",
        HomeEnvironmentId = "22222222-2222-2222-2222-222222222222",
        TargetEnvironmentId = "33333333-3333-3333-3333-333333333333",
        AgentId = "44444444-4444-4444-4444-444444444444",
        PackageRevisionId = "66666666-6666-6666-6666-666666666666",
        PackageDigest = new string('a', 64),
        PackageSizeInBytes = 1024,
        DeploymentId = "77777777-7777-7777-7777-777777777777",
        DeploymentName = "dev-proof",
        DeploymentType = "Dev",
        EntraId = "88888888-8888-8888-8888-888888888888",
        EntraIdType = "AppReg",
        RuntimeEndpoint = "https://runtime.example/api/messages",
        PackageSource = "current-grs",
        ProjectionAcknowledged = true,
        PublicDeployOperationCreated = false,
        TargetLocalPackageStagingPerformed = false,
        RuntimeReadinessProven = false,
        CrossIslandDemonstrated = false,
    };

    internal static AgentCreateResult CreateStubCreateResult() => new()
    {
        Status = "agent-created",
        TenantId = "tenant-contoso",
        EnvironmentId = "env-1",
        AgentId = "agent-1",
        DisplayName = "My Agent",
        RepositoryId = "repo-1",
    };

    public Task<DeploymentResult<CodeAgentDeployment>> CreateDeploymentAsync(DeploymentCreateRequest request, CancellationToken cancellationToken)
    {
        LastDeploymentRequest = request;
        return Task.FromResult((OnCreateDeployment ?? (_ => new DeploymentResult<CodeAgentDeployment>
        {
            TargetName = "Deployment",
            Status = DeploymentOperationStatus.Created,
            Payload = StubDeployment("00000000-0000-0000-0000-000000000000"),
            TargetInstanceName = "dev",
        }))(request));
    }

    public Task<DeploymentResult<CodeAgentDeployment>> GetDeploymentAsync(DeploymentShowRequest request, CancellationToken cancellationToken)
    {
        LastGetDeploymentRequest = request;
        LastGetDeploymentId = request.DeploymentId;
        LastGetDeploymentAgent = request.AgentId;
        LastGetDeploymentName = request.DeploymentName;
        return Task.FromResult((OnGetDeployment ?? (r => new DeploymentResult<CodeAgentDeployment>
        {
            TargetName = "Deployment",
            Status = DeploymentOperationStatus.Retrieved,
            Payload = StubDeployment(r.DeploymentId ?? "00000000-0000-0000-0000-000000000000"),
        }))(request));
    }

    public Task<DeploymentResult<CodeAgentDeployment>> UpdateDeploymentAsync(DeploymentUpdateRequest request, CancellationToken cancellationToken)
    {
        LastUpdateDeploymentRequest = request;
        return Task.FromResult((OnUpdateDeployment ?? (r => new DeploymentResult<CodeAgentDeployment>
        {
            TargetName = "Deployment",
            Status = DeploymentOperationStatus.Updated,
            Payload = StubDeployment(r.DeploymentId ?? "dep"),
            TargetInstanceName = "dev",
        }))(request));
    }

    public Task<DeploymentResult<CodeAgentDeployment>> DeleteDeploymentAsync(DeploymentRequest request, CancellationToken cancellationToken)
    {
        LastDeleteDeploymentRequest = request;
        LastDeleteDeploymentId = request.DeploymentId;
        return Task.FromResult((OnDeleteDeployment ?? (_ => new DeploymentResult<CodeAgentDeployment>
        {
            TargetName = "Deployment",
            Status = DeploymentOperationStatus.Deleted,
        }))(request));
    }

    public Func<DeploymentOAuthCreateRequest, DeploymentResult<DeploymentOAuthConnection>>? OnCreateDeploymentOAuth { get; init; }

    public Func<DeploymentOAuthUpdateRequest, DeploymentResult<DeploymentOAuthConnection>>? OnUpdateDeploymentOAuth { get; init; }

    public Func<DeploymentOAuthShowRequest, DeploymentResult<DeploymentOAuthConnection>>? OnShowDeploymentOAuth { get; init; }

    public Func<DeploymentOAuthListRequest, DeploymentResult<DeploymentOAuthConnection[]>>? OnListDeploymentOAuth { get; init; }

    public Func<DeploymentOAuthDeleteRequest, DeploymentResult<DeploymentOAuthConnection>>? OnDeleteDeploymentOAuth { get; init; }

    public Func<DeploymentOAuthProviderListRequest, OAuthProviderDefinition[]>? OnListDeploymentOAuthProviders { get; init; }

    public DeploymentOAuthCreateRequest? LastCreateDeploymentOAuthRequest { get; private set; }

    public DeploymentOAuthUpdateRequest? LastUpdateDeploymentOAuthRequest { get; private set; }

    public DeploymentOAuthShowRequest? LastShowDeploymentOAuthRequest { get; private set; }

    public DeploymentOAuthListRequest? LastListDeploymentOAuthRequest { get; private set; }

    public DeploymentOAuthDeleteRequest? LastDeleteDeploymentOAuthRequest { get; private set; }

    public DeploymentOAuthProviderListRequest? LastListDeploymentOAuthProvidersRequest { get; private set; }

    public Task<DeploymentResult<DeploymentOAuthConnection>> CreateDeploymentOAuthAsync(DeploymentOAuthCreateRequest request, CancellationToken cancellationToken)
    {
        LastCreateDeploymentOAuthRequest = request;
        return Task.FromResult((OnCreateDeploymentOAuth ?? (r => new DeploymentResult<DeploymentOAuthConnection>
        {
            TargetName = "Connection",
            Status = DeploymentOperationStatus.Created,
            Payload = new DeploymentOAuthConnection
            {
                ResourceId = $"/connections/{r.ConnectionName ?? "conn"}",
                ConnectionName = r.ConnectionName ?? "conn",
                ServiceProviderId = "provider-id",
                ServiceProviderDisplayName = "Azure Active Directory v2",
            },
            TargetInstanceName = r.ConnectionName,
        }))(request));
    }

    public Task<DeploymentResult<DeploymentOAuthConnection>> UpdateDeploymentOAuthAsync(DeploymentOAuthUpdateRequest request, CancellationToken cancellationToken)
    {
        LastUpdateDeploymentOAuthRequest = request;
        return Task.FromResult((OnUpdateDeploymentOAuth ?? (r => new DeploymentResult<DeploymentOAuthConnection>
        {
            TargetName = "Connection",
            Status = DeploymentOperationStatus.Updated,
            Payload = new DeploymentOAuthConnection
            {
                ResourceId = $"/connections/{r.ConnectionName ?? "conn"}",
                ConnectionName = r.ConnectionName ?? "conn",
                ServiceProviderId = "provider-id",
                ServiceProviderDisplayName = "Azure Active Directory v2",
            },
            TargetInstanceName = r.ConnectionName,
        }))(request));
    }

    public Task<DeploymentResult<DeploymentOAuthConnection>> ShowDeploymentOAuthAsync(DeploymentOAuthShowRequest request, CancellationToken cancellationToken)
    {
        LastShowDeploymentOAuthRequest = request;
        return Task.FromResult((OnShowDeploymentOAuth ?? (r => new DeploymentResult<DeploymentOAuthConnection>
        {
            TargetName = "Connection",
            Status = DeploymentOperationStatus.Retrieved,
            Payload = new DeploymentOAuthConnection
            {
                ResourceId = $"/connections/{r.ConnectionName ?? "conn"}",
                ConnectionName = r.ConnectionName ?? "conn",
                ServiceProviderId = "provider-id",
                ServiceProviderDisplayName = "Azure Active Directory v2",
            },
        }))(request));
    }

    public Task<DeploymentResult<DeploymentOAuthConnection[]>> ListDeploymentOAuthAsync(DeploymentOAuthListRequest request, CancellationToken cancellationToken)
    {
        LastListDeploymentOAuthRequest = request;
        return Task.FromResult((OnListDeploymentOAuth ?? (_ => new DeploymentResult<DeploymentOAuthConnection[]>
        {
            TargetName = "Connections",
            Status = DeploymentOperationStatus.Listed,
            Payload = [],
        }))(request));
    }

    public Task<DeploymentResult<DeploymentOAuthConnection>> DeleteDeploymentOAuthAsync(DeploymentOAuthDeleteRequest request, CancellationToken cancellationToken)
    {
        LastDeleteDeploymentOAuthRequest = request;
        return Task.FromResult((OnDeleteDeploymentOAuth ?? (r => new DeploymentResult<DeploymentOAuthConnection>
        {
            TargetName = "Connection",
            Status = DeploymentOperationStatus.Deleted,
            TargetInstanceName = r.ConnectionName,
        }))(request));
    }

    public Task<OAuthProviderDefinition[]> ListDeploymentOAuthProvidersAsync(
        DeploymentOAuthProviderListRequest request,
        CancellationToken cancellationToken)
    {
        LastListDeploymentOAuthProvidersRequest = request;
        return Task.FromResult((OnListDeploymentOAuthProviders ?? (_ => StubOAuthProviderReport()))(request));
    }

    private static OAuthProviderDefinition[] StubOAuthProviderReport() =>
        [
            new OAuthProviderDefinition
            {
                Name = "Aadv2",
                Id = "provider-id",
                DisplayName = "Azure Active Directory v2",
            },
        ];

    public Task<DeploymentResult<DeploymentChannelRecord>> CreateChannelAsync(
        DeploymentChannelCreateRequest request,
        CancellationToken cancellationToken)
    {
        LastCreateChannelRequest = request;
        return Task.FromResult((OnCreateChannel ?? DefaultChannel<DeploymentChannelCreateRequest>(DeploymentOperationStatus.Created))(request));
    }

    public Task<DeploymentResult<DeploymentChannelRecord>> UpdateChannelAsync(
        DeploymentChannelUpdateRequest request,
        CancellationToken cancellationToken)
    {
        LastUpdateChannelRequest = request;
        return Task.FromResult((OnUpdateChannel ?? DefaultChannel<DeploymentChannelUpdateRequest>(DeploymentOperationStatus.Updated))(request));
    }

    public Task<DeploymentResult<DeploymentChannelRecord>> DeleteChannelAsync(
        DeploymentChannelDeleteRequest request,
        CancellationToken cancellationToken)
    {
        LastDeleteChannelRequest = request;
        return Task.FromResult((OnDeleteChannel ?? DefaultChannel<DeploymentChannelDeleteRequest>(DeploymentOperationStatus.Deleted))(request));
    }

    public Task<DeploymentResult<DeploymentChannelRecord>> GetChannelAsync(
        DeploymentChannelShowRequest request,
        CancellationToken cancellationToken)
    {
        LastGetChannelRequest = request;
        return Task.FromResult((OnGetChannel ?? DefaultGetChannel)(request));
    }

    public Task<DeploymentResult<DeploymentChannelRecord[]>> ListChannelsAsync(
        DeploymentChannelListRequest request,
        CancellationToken cancellationToken)
    {
        LastListChannelsRequest = request;
        return Task.FromResult((OnListChannels ?? (_ => new DeploymentResult<DeploymentChannelRecord[]>
        {
            Status = DeploymentOperationStatus.Listed,
            Payload = [],
            TargetName = "Channels",
        }))(request));
    }

    private static Func<TRequest, DeploymentResult<DeploymentChannelRecord>> DefaultChannel<TRequest>(
        DeploymentOperationStatus status)
        where TRequest : DeploymentChannelRequest =>
        request => new DeploymentResult<DeploymentChannelRecord>
        {
            Status = status,
            Payload = status == DeploymentOperationStatus.Deleted
                ? null
                : new DeploymentChannelRecord
                {
                    ChannelName = request.ChannelName,
                    ResourceId = "/channels/MsTeamsChannel",
                    Parameters = new Dictionary<string, System.Text.Json.JsonElement>
                    {
                        ["isEnabled"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                    },
                },
            TargetName = "Channel",
            TargetInstanceName = request.ChannelName,
        };

    private static DeploymentResult<DeploymentChannelRecord> DefaultGetChannel(
        DeploymentChannelShowRequest request) =>
        new()
        {
            Status = DeploymentOperationStatus.Retrieved,
            Payload = new DeploymentChannelRecord
            {
                ChannelName = "msteams",
                ResourceId = "/channels/MsTeamsChannel",
                Parameters = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["isEnabled"] = System.Text.Json.JsonSerializer.SerializeToElement(true),
                },
            },
            TargetName = "Channel",
            TargetInstanceName = "msteams",
        };

    public Task<AgentDeploymentShowResult> ShowAgentDeploymentAsync(AgentDeploymentShowRequest request, CancellationToken cancellationToken)
    {
        LastAgentDeploymentShowRequest = request;
        return Task.FromResult((OnShowAgentDeployment ?? (_ => new AgentDeploymentShowResult
        {
            AgentId = request.AgentId ?? "agent-1",
            AgentDisplayName = "Contoso Helpdesk",
            DeploymentId = request.DeploymentId ?? "slot-1",
            DeploymentName = request.DeploymentName ?? "dev",
            DeploymentType = "dev",
            EnvironmentId = "env-1",
            HomeEnvironmentId = "env-1",
            IsEnabled = true,
        }))(request));
    }

    public AgentDeploymentShowRequest? LastAgentDeploymentShowRequest { get; private set; }

    public Func<AgentDeploymentShowRequest, AgentDeploymentShowResult>? OnShowAgentDeployment { get; init; }


    private static CodeAgentDeployment StubDeployment(string id) => new()
    {
        DeploymentId = id,
        AgentId = "11111111-1111-1111-1111-111111111111",
        DeploymentName = "dep",
        DeploymentType = DeploymentType.Dev,
        Bot = new DeploymentBotResource
        {
            ResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.BotService/botServices/dep",
            Name = "dep",
            TenantId = "tenant",
            Sku = "F0",
            Location = "global",
            DisplayName = "dep",
            EntraId = "app",
            EntraIdType = "AppReg",
        },
    };

    internal static AbsProvisioningReport CreateStubReport(string status)
    {
        const string botResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.BotService/botServices/bot";
        return new AbsProvisioningReport
        {
            Status = status,
            AzureContext = new AbsAzureContext { TenantId = "tenant", SubscriptionId = "sub", ResourceGroup = "rg", Location = "westus" },
            Abs = new AbsDetails
            {
                BotName = "bot",
                BotResourceId = botResourceId,
                AppId = "11111111-1111-1111-1111-111111111111",
                TenantId = "tenant",
                MessagingEndpoint = "https://gateway.contoso.com/api/messages",
                Sku = "F0",
                MsaAppType = "SingleTenant",
            },
            Correlation = new AbsCorrelation { Arm = new AbsArmCorrelation { Mode = "resourceId", BotResourceId = botResourceId, Name = "bot" }, Graph = new AbsGraphCorrelation { Mode = "displayNameFilter", DisplayName = "bot" } },
            Identity = new AbsIdentityInfo { Mode = "ClientSecret", SecretMaterialReturned = false, SecretReference = null },
            Changes = [],
            Preflight = [],
            NextActions = [],
        };
    }

    public EvaluationListRequest? LastEvaluationListRequest { get; private set; }

    public EvaluationShowRequest? LastEvaluationShowRequest { get; private set; }

    public EvaluationRunRequest? LastEvaluationRunRequest { get; private set; }

    public EvaluationCancelRequest? LastEvaluationCancelRequest { get; private set; }

    public EvaluationDeleteRequest? LastEvaluationDeleteRequest { get; private set; }

    public EvaluationGenerateRequest? LastEvaluationGenerateRequest { get; private set; }

    public EvaluationTestSetListRequest? LastEvaluationTestSetListRequest { get; private set; }

    public EvaluationTestSetShowRequest? LastEvaluationTestSetShowRequest { get; private set; }

    public EvaluationTestSetCreateRequest? LastEvaluationTestSetCreateRequest { get; private set; }

    public EvaluationTestSetUpdateRequest? LastEvaluationTestSetUpdateRequest { get; private set; }

    public EvaluationTestSetExportRequest? LastEvaluationTestSetExportRequest { get; private set; }

    public EvaluationTestSetDeleteRequest? LastEvaluationTestSetDeleteRequest { get; private set; }

    public Func<EvaluationRunRequest, EvaluationRunResult>? OnEvaluationRun { get; init; }

    public Task<EvaluationListResult> ListEvaluationsAsync(EvaluationListRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationListRequest = request;
        return Task.FromResult(new EvaluationListResult { EnvironmentId = FakeEnvironmentId, McsAgentId = FakeAgentId, Limit = request.Limit ?? 20 });
    }

    public Task<EvaluationRunResult> ShowEvaluationAsync(EvaluationShowRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationShowRequest = request;
        return Task.FromResult(FakeRun("evaluation-shown", request.RunId));
    }

    public Task<EvaluationRunResult> RunEvaluationAsync(EvaluationRunRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationRunRequest = request;
        return Task.FromResult(OnEvaluationRun?.Invoke(request) ?? FakeRun("evaluation-started", "run-1"));
    }

    public Task<EvaluationRunResult> CancelEvaluationAsync(EvaluationCancelRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationCancelRequest = request;
        return Task.FromResult(FakeRun("evaluation-cancelled", request.RunId));
    }

    public Task<EvaluationRunResult> DeleteEvaluationAsync(EvaluationDeleteRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationDeleteRequest = request;
        return Task.FromResult(FakeRun("evaluation-deleted", request.RunId));
    }

    public Task<EvaluationGenerateResult> GenerateEvaluationConversationsAsync(EvaluationGenerateRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationGenerateRequest = request;
        return Task.FromResult(new EvaluationGenerateResult { EnvironmentId = FakeEnvironmentId, McsAgentId = FakeAgentId });
    }

    public Task<EvaluationTestSetListResult> ListEvaluationTestSetsAsync(EvaluationTestSetListRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationTestSetListRequest = request;
        return Task.FromResult(new EvaluationTestSetListResult { EnvironmentId = FakeEnvironmentId, McsAgentId = FakeAgentId });
    }

    public Task<EvaluationTestSetResult> ShowEvaluationTestSetAsync(EvaluationTestSetShowRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationTestSetShowRequest = request;
        return Task.FromResult(FakeTestSet("test-set-shown"));
    }

    public Task<EvaluationTestSetResult> CreateEvaluationTestSetAsync(EvaluationTestSetCreateRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationTestSetCreateRequest = request;
        return Task.FromResult(FakeTestSet("test-set-created"));
    }

    public Task<EvaluationTestSetResult> UpdateEvaluationTestSetAsync(EvaluationTestSetUpdateRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationTestSetUpdateRequest = request;
        return Task.FromResult(FakeTestSet("test-set-updated"));
    }

    public Task<EvaluationTestSetResult> ExportEvaluationTestSetAsync(EvaluationTestSetExportRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationTestSetExportRequest = request;
        return Task.FromResult(FakeTestSet("test-set-exported") with { OutputPath = request.Output });
    }

    public Task<EvaluationTestSetDeleteResult> DeleteEvaluationTestSetAsync(EvaluationTestSetDeleteRequest request, CancellationToken cancellationToken)
    {
        LastEvaluationTestSetDeleteRequest = request;
        return Task.FromResult(new EvaluationTestSetDeleteResult { EnvironmentId = FakeEnvironmentId, TestSetId = "testset-1", DisplayName = "Nightly" });
    }

    private const string FakeEnvironmentId = "env-1";

    private const string FakeAgentId = "bot-1";

    private static EvaluationRunResult FakeRun(string status, string? runId) => new()
    {
        Status = status,
        EnvironmentId = FakeEnvironmentId,
        McsAgentId = FakeAgentId,
        Run = new EvaluationRunRow { RunId = runId ?? "run-1", State = "Completed", TestCaseCount = 1 },
    };

    private static EvaluationTestSetResult FakeTestSet(string status) => new()
    {
        Status = status,
        EnvironmentId = FakeEnvironmentId,
        McsAgentId = FakeAgentId,
        TestSet = new EvaluationTestSetRow { TestSetId = "testset-1", DisplayName = "Nightly", ConversationCount = 1 },
    };
}
