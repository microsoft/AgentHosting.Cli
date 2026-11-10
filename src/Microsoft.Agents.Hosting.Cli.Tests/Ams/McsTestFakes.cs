using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Copilot;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents.Storage;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Playground;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.Agents.Hosting.Cli.Hosting;
using Microsoft.Agents.Hosting.Cli.Tests.Copilot;
using Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;
using Microsoft.CopilotStudio.McsCore;
using Microsoft.CopilotStudio.Sync;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

internal static class McsRecords
{
    public const string AgentId = "aaaaaaaa-1111-4111-8111-000000000001";
    public const string WorkflowAgentId = "aaaaaaaa-1111-4111-8111-000000000011";
    public const string RepositoryId = "cccccccc-3333-4333-8333-000000000003";
    public const string OtherRepositoryId = "cccccccc-3333-4333-8333-000000000013";
    public const string DevDeploymentId = "eeeeeeee-5555-4555-8555-000000000005";
    public const string TestDeploymentId = "ffffffff-6666-4666-8666-000000000006";
    public const string BotId = "b0b0b0b0-4444-4444-8444-000000000014";
    public const string WorkflowId = "0a1b2c3d-1111-4111-8111-00000000000a";
    public const string Commit = "9fceb02d0ae598e95dc970b74767f19372d61af8";
    public const string OlderCommit = "1f0e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c";
    public const string NewCommit = "2fd4e1c67a2d28fced849ee1bb76e7391b93eb12";
    public const string InitialCommit = "01cc0e77a1b2c3d4e5f60718293a4b5c6d7e8f90";
    public const string ETag = "\"etag-1\"";
    public const string GrsHost = "22222222222222222222222222222222.22.environment.api.powerplatform.com";
    public const string GitUrl = $"https://{GrsHost}/appframework/git/repositories/{RepositoryId}";
    public const string OtherGitUrl = $"https://{GrsHost}/appframework/git/repositories/{OtherRepositoryId}";

    public static AmsMcsAgentRecord Agent(string environmentId = "env-1") => new()
    {
        AgentId = AgentId,
        TenantId = EnvironmentFixtures.ContosoTenant,
        EnvironmentId = environmentId,
        DisplayName = "Contoso HR",
        Description = "HR helper",
        AgentType = AgentTypes.McsAgent,
        RepositoryId = RepositoryId,
        SourceControl = new AmsMcsSourceControl { RepositoryId = RepositoryId, GitUrl = GitUrl, Branch = "main", Directory = "ContosoHR" },
        Mcs = new AmsMcsAgentProperties { SchemaName = "cr1a2_contosoHr", AuthoringShape = "cliagent", HomeDeploymentId = DevDeploymentId },
        ETag = ETag,
    };

    public static AmsMcsAgentListItem ListItem(string environmentId = "env-1", params AmsMcsDeploymentRecord[] deployments) => new()
    {
        AgentId = AgentId,
        TenantId = EnvironmentFixtures.ContosoTenant,
        EnvironmentId = environmentId,
        DisplayName = "Contoso HR",
        Description = "HR helper",
        AgentType = AgentTypes.McsAgent,
        SchemaName = "cr1a2_contosoHr",
        RepositoryId = RepositoryId,
        AgentFolder = "ContosoHR",
        Branch = "main",
        HomeDeploymentId = DevDeploymentId,
        DeploymentCount = deployments.Length,
        CloudState = "Deployed",
        Deployments = deployments,
    };

    public static AmsMcsDeploymentRecord Deployment(string deploymentId = DevDeploymentId, string deploymentType = "Dev", string deploymentName = "contoso-dev", string environmentId = "env-1", string agentId = AgentId) => new()
    {
        DeploymentId = deploymentId,
        AgentId = agentId,
        HomeEnvironmentId = "env-1",
        EnvironmentId = environmentId,
        DeploymentName = deploymentName,
        DeploymentType = deploymentType,
        AgentType = AgentTypes.McsAgent,
        State = "Deployed",
        BotId = BotId,
        SchemaName = "cr1a2_contosoHr",
        LastDeployedCommitSha = Commit,
        LastDeployOrigin = "Remote",
        LastDeployedTime = new DateTimeOffset(2026, 8, 1, 9, 12, 5, TimeSpan.Zero),
    };

    public static AmsMcsRepository Repository(params AmsMcsAgentListItem[] agents) => new()
    {
        RepositoryId = RepositoryId,
        Name = "contoso-agents",
        GitUrl = GitUrl,
        Branch = "main",
        HeadCommitSha = Commit,
        Agents = agents,
    };

    public static AmsMcsAgentListItem WorkflowListItem(string? workflowId = WorkflowId) => ListItem("env-1", Deployment() with { AgentType = AgentTypes.McsWorkflow, BotId = null, WorkflowId = workflowId }) with { AgentId = WorkflowAgentId, AgentType = AgentTypes.McsWorkflow, DisplayName = "Escalate", SchemaName = null, AgentFolder = "Escalate" };

    public static AmsMcsOperationRecord Operation(string status, string kind = "Deploy", string operationId = "op-deploy") => new()
    {
        OperationId = operationId,
        Kind = kind,
        Status = status,
        AgentId = AgentId,
        DeploymentId = DevDeploymentId,
        SourceCommitSha = Commit,
        ResultCommitSha = status == AmsMcsOperationStatus.Succeeded && kind == "Capture" ? NewCommit : null,
        Publish = true,
        CompletedTime = status is AmsMcsOperationStatus.Succeeded or AmsMcsOperationStatus.Failed ? new DateTimeOffset(2026, 8, 2, 10, 15, 0, TimeSpan.Zero) : null,
        Error = status == AmsMcsOperationStatus.Failed ? new AmsMcsOperationError { Code = "McsSourceValidationFailed", Message = "topics/Greeting.mcs.yml: unknown kind" } : null,
    };

    public static PowerPlatformEnvironment Home(string environmentId = "env-1", string displayName = "Contoso Dev") => EnvironmentFixtures.Environment(environmentId, displayName, endpoint: $"https://{environmentId}.api.powerplatform.com/", instanceUrl: CopilotTestFixtures.DataverseOrganization);

    public static EnvironmentSelectionService Environments(params PowerPlatformEnvironment[] additional) => Environments(new FakeEnvironmentClient([Home(), .. additional]));

    public static EnvironmentSelectionService Environments(FakeEnvironmentClient client, string selectedEnvironmentId = "env-1")
    {
        var environments = EnvironmentFixtures.Service(client, new InMemoryEnvironmentSelectionStore(), InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));
        environments.SelectAsync(selectedEnvironmentId, default).GetAwaiter().GetResult();
        return environments;
    }

    public static AmsTargets Targets(AmsEndpointProfile? endpoints = null) => new(endpoints ?? new AmsEndpointProfile(), Environments(), PowerPlatformApiTargetConfig.Public);

    public static AmsAgentException Rejected(int errorCode, string kind, int exitCode = ExitCodes.Failure) => AmsAgentException.Rejected(errorCode, $"{kind} refused.", exitCode, kind, "Do something.");

    public static TargetRequestException Status(int status) => new($"The Agent Management Service answered {status}.", ExitCodes.Failure, TargetErrorKind.RequestFailed, status);
}

internal sealed class FakeMcsService : IAmsMcsAgentReadTransport, IAmsMcsOperationTransport, IAmsMcsDeploymentTransport, IAmsMcsAgentDeleteTransport, IAmsMcsRegistrationTransport
{
    public List<string> Calls { get; } = [];

    public List<AmsServiceTarget> Targets { get; } = [];

    public AmsMcsAgentRecord Agent { get; set; } = McsRecords.Agent();

    public Exception? AgentReadFailure { get; set; }

    public Queue<AmsMcsAgentPage> AgentPages { get; } = new();

    public AmsMcsRepository Repository { get; set; } = McsRecords.Repository();

    public Exception? RepositoryFailure { get; set; }

    public List<AmsMcsHolder> Holders { get; } = [];

    public Exception? HoldersFailure { get; set; }

    public List<AmsMcsRegistration> Registrations { get; } = [];

    public Func<AmsMcsRegistration, AmsMcsAgentRecord>? OnRegister { get; set; }

    public List<AmsMcsDeployTransportRequest> Deploys { get; } = [];

    public Func<AmsMcsDeployTransportRequest, AmsMcsOperationAccepted>? OnDeploy { get; set; }

    public List<AmsMcsCaptureTransportRequest> Captures { get; } = [];

    public Func<AmsMcsCaptureTransportRequest, AmsMcsOperationAccepted>? OnCapture { get; set; }

    public Queue<AmsMcsOperationRecord> Operations { get; } = new();

    public AmsMcsOperationRecord? Answered { get; set; }

    public List<AmsMcsDeployRecordRequest> DeployRecords { get; } = [];

    public Exception? DeployRecordFailure { get; set; }

    public List<AmsMcsDeploymentCreateTransportRequest> DeploymentCreates { get; } = [];

    public Exception? DeploymentCreateFailure { get; set; }

    public List<AmsMcsDeploymentRenameTransportRequest> DeploymentRenames { get; } = [];

    public Exception? DeploymentRenameFailure { get; set; }

    public List<(string AgentId, string DeploymentId, string AgentType)> DeploymentDeletes { get; } = [];

    public Exception? DeploymentDeleteFailure { get; set; }

    public List<(string AgentId, string ETag)> AgentDeletes { get; } = [];

    public Exception? AgentDeleteFailure { get; set; }

    public Task<AmsMcsAgentRecord> GetAgentAsync(AmsServiceTarget target, string agentId, CancellationToken cancellationToken)
    {
        Record($"get-agent {agentId}", target);
        return AgentReadFailure is null ? Task.FromResult(Agent) : Task.FromException<AmsMcsAgentRecord>(AgentReadFailure);
    }

    public Task<AmsMcsAgentPage> ListAgentsAsync(AmsServiceTarget target, int top, string? agentType, string? repositoryId, string? skipToken, CancellationToken cancellationToken)
    {
        Record($"list-agents {top}{Suffix(agentType)}{Suffix(repositoryId)}{Suffix(skipToken)}", target);
        return Task.FromResult(AgentPages.Count > 0 ? AgentPages.Dequeue() : new AmsMcsAgentPage { Agents = [McsRecords.ListItem(target.EnvironmentId)] });
    }

    public Task<AmsMcsRepository> ReadRepositoryAsync(AmsServiceTarget target, string repositoryId, string? agentFolder, bool includeDeployments, CancellationToken cancellationToken)
    {
        Record($"read-repository {repositoryId} {agentFolder ?? "-"} {(includeDeployments ? "with-deployments" : "agents")}", target);
        return RepositoryFailure is null ? Task.FromResult(string.Equals(Repository.RepositoryId, repositoryId, StringComparison.OrdinalIgnoreCase) ? Repository : Repository with { RepositoryId = repositoryId }) : Task.FromException<AmsMcsRepository>(RepositoryFailure);
    }

    public Task<IReadOnlyList<AmsMcsHolder>> FindHoldersAsync(AmsServiceTarget target, string? botId, string? workflowId, CancellationToken cancellationToken)
    {
        Record($"find-holders {botId ?? "-"} {workflowId ?? "-"}", target);
        return HoldersFailure is null ? Task.FromResult<IReadOnlyList<AmsMcsHolder>>([.. Holders]) : Task.FromException<IReadOnlyList<AmsMcsHolder>>(HoldersFailure);
    }

    public Task<AmsMcsAgentRecord> RegisterAsync(AmsMcsRegistration request, CancellationToken cancellationToken)
    {
        Record($"register {request.AgentId} {request.RepositoryId ?? "new"}", request.Target);
        Registrations.Add(request);
        return Task.FromResult(OnRegister?.Invoke(request) ?? Registered(request));
    }

    public Task<AmsMcsOperationAccepted> DeployAsync(AmsMcsDeployTransportRequest request, CancellationToken cancellationToken)
    {
        Record($"deploy {request.DeploymentId}", request.Target);
        Deploys.Add(request);
        return Task.FromResult(OnDeploy?.Invoke(request) ?? Accepted("op-deploy", request.CommitSha ?? McsRecords.Commit));
    }

    public Task<AmsMcsOperationAccepted> CaptureAsync(AmsMcsCaptureTransportRequest request, CancellationToken cancellationToken)
    {
        Record($"capture {request.DeploymentId}", request.Target);
        Captures.Add(request);
        return Task.FromResult(OnCapture?.Invoke(request) ?? Accepted("op-capture", McsRecords.Commit));
    }

    public Task<AmsMcsOperationRecord> GetOperationAsync(AmsServiceTarget target, string agentId, string operationId, CancellationToken cancellationToken)
    {
        Record($"get-operation {operationId}", target);
        return Task.FromResult(Operations.Count > 0 ? Operations.Dequeue() : McsRecords.Operation(AmsMcsOperationStatus.Succeeded, operationId: operationId));
    }

    public Task<AmsMcsDeploymentRecord> RecordDeployAsync(AmsMcsDeployRecordRequest request, CancellationToken cancellationToken)
    {
        Record($"deploy-record {request.DeploymentId}", request.Target);
        DeployRecords.Add(request);
        return DeployRecordFailure is null ? Task.FromResult(McsRecords.Deployment(request.DeploymentId) with { LastDeployedCommitSha = request.CommitSha, LastDeployOrigin = "Local" }) : Task.FromException<AmsMcsDeploymentRecord>(DeployRecordFailure);
    }

    public Task<AmsMcsDeploymentRecord> CreateMcsDeploymentAsync(AmsMcsDeploymentCreateTransportRequest request, CancellationToken cancellationToken)
    {
        Record($"create-deployment {request.DeploymentId}", request.Target);
        DeploymentCreates.Add(request);
        return DeploymentCreateFailure is null ? Task.FromResult(McsRecords.Deployment(request.DeploymentId, request.DeploymentType, request.DeploymentName, request.TargetEnvironmentId, request.AgentId) with { State = "NotDeployed", BotId = null, LastDeployedCommitSha = null, LastDeployOrigin = null, LastDeployedTime = null }) : Task.FromException<AmsMcsDeploymentRecord>(DeploymentCreateFailure);
    }

    public Task<AmsMcsDeploymentRecord> RenameMcsDeploymentAsync(AmsMcsDeploymentRenameTransportRequest request, CancellationToken cancellationToken)
    {
        Record($"rename-deployment {request.DeploymentId} {request.DeploymentName}", request.Target);
        DeploymentRenames.Add(request);
        return DeploymentRenameFailure is null ? Task.FromResult(McsRecords.Deployment(request.DeploymentId, deploymentName: request.DeploymentName)) : Task.FromException<AmsMcsDeploymentRecord>(DeploymentRenameFailure);
    }

    public Task DeleteDeploymentAsync(AmsServiceTarget target, string agentId, string deploymentId, string agentType, CancellationToken cancellationToken)
    {
        Record($"delete-deployment {deploymentId} {agentType}", target);
        DeploymentDeletes.Add((agentId, deploymentId, agentType));
        return DeploymentDeleteFailure is null ? Task.CompletedTask : Task.FromException(DeploymentDeleteFailure);
    }

    public Task DeleteAgentAsync(AmsServiceTarget target, string agentId, string eTag, CancellationToken cancellationToken)
    {
        Record($"delete-agent {agentId} {eTag}", target);
        AgentDeletes.Add((agentId, eTag));
        return AgentDeleteFailure is null ? Task.CompletedTask : Task.FromException(AgentDeleteFailure);
    }

    private static AmsMcsAgentRecord Registered(AmsMcsRegistration request) => new()
    {
        AgentId = request.AgentId,
        TenantId = EnvironmentFixtures.ContosoTenant,
        EnvironmentId = request.Target.EnvironmentId,
        DisplayName = request.DisplayName,
        Description = request.Description,
        AgentType = request.AgentType,
        RepositoryId = request.RepositoryId ?? McsRecords.RepositoryId,
        SourceControl = new AmsMcsSourceControl { RepositoryId = request.RepositoryId ?? McsRecords.RepositoryId, GitUrl = request.RepositoryId is null || request.RepositoryId == McsRecords.RepositoryId ? McsRecords.GitUrl : McsRecords.OtherGitUrl, Branch = request.Branch, Directory = request.AgentFolder, RepositoryCreated = request.RepositoryId is null },
        Mcs = new AmsMcsAgentProperties { SchemaName = request.SchemaName, AuthoringShape = request.AuthoringShape, HomeDeploymentId = request.Deployments.FirstOrDefault(deployment => deployment.DeploymentType == "Dev")?.DeploymentId },
        ETag = McsRecords.ETag,
    };

    private static string Suffix(string? value) => value is null ? string.Empty : $" {value}";

    private AmsMcsOperationAccepted Accepted(string operationId, string? sourceCommitSha) => Answered is { } answered ? new() { OperationId = answered.OperationId, Status = answered.Status, SourceCommitSha = answered.SourceCommitSha, Finished = answered } : new() { OperationId = operationId, Status = AmsMcsOperationStatus.Accepted, SourceCommitSha = sourceCommitSha };

    private void Record(string call, AmsServiceTarget target)
    {
        Calls.Add(call);
        Targets.Add(target);
    }
}

internal sealed class FakeGitPort : IGitPort
{
    public List<string> Calls { get; } = [];

    public bool Missing { get; set; }

    public GitWorkingTree? Tree { get; set; }

    public Dictionary<string, string> Commits { get; } = new(StringComparer.Ordinal) { ["HEAD"] = McsRecords.Commit };

    public bool HeadPushed { get; set; } = true;

    public string? MergeBase { get; set; } = McsRecords.Commit;

    public List<string> Changed { get; } = [];

    public List<string> Tracked { get; } = [];

    public List<string> Committed { get; } = [];

    public bool RemoteHasMain { get; set; }

    public Dictionary<string, string> RemoteFiles { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> BaseFiles { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Fetched { get; } = new(StringComparer.Ordinal);

    public GitCommitResult CommitResult { get; set; } = new(McsRecords.NewCommit, null);

    public GitPushResult PushResult { get; set; } = new(true, null);

    public List<(IReadOnlyList<string> Paths, string Message)> CommitRequests { get; } = [];

    public Exception? CloneFailure { get; set; }

    public Exception? InitFailure { get; set; }

    public Exception? HelperFailure { get; set; }

    public Exception? AddRemoteFailure { get; set; }

    public Exception? RemoveRemoteFailure { get; set; }

    public Exception? FetchFailure { get; set; }

    public Exception? CheckoutFailure { get; set; }

    public Exception? FindFailure { get; set; }

    public string? GitDirectory { get; set; }

    public HashSet<(string Ancestor, string Descendant)> Ancestry { get; } = [];

    public HashSet<string> LocalBranches { get; } = new(StringComparer.Ordinal);

    public Exception? LocalBranchesFailure { get; set; }

    public static string Log(params string[] revisions) => $"log {string.Join(' ', revisions)} -- :(icase).mcs/* :(icase)*/.mcs/*";

    public void HoldGrsInitialCommit()
    {
        RemoteHasMain = true;
        RemoteFiles["README.md"] = "# contoso-agents\n";
        Commits[$"{AmsProjectLink.Origin}/{AmsProjectLink.Branch}"] = McsRecords.InitialCommit;
    }

    public void RequireGit()
    {
        Calls.Add("require");
        if (Missing)
        {
            throw GitErrors.NotFound("git was not found on PATH, and AMS mode needs it.", "Install git, open a new terminal, then run the command again.");
        }
    }

    public Task<GitWorkingTree?> FindWorkingTreeAsync(string directory, CancellationToken cancellationToken)
    {
        Calls.Add("find");
        if (FindFailure is not null)
        {
            return Task.FromException<GitWorkingTree?>(FindFailure);
        }

        return Task.FromResult(Missing ? throw GitErrors.NotFound("git was not found.", "Install git.") : Tree is { } tree && Under(directory, tree.Root) ? tree : null);
    }

    public Task CloneAsync(string url, string directory, CancellationToken cancellationToken)
    {
        Calls.Add($"clone {url}");
        if (CloneFailure is not null)
        {
            return Task.FromException(CloneFailure);
        }

        if (!Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(directory))))
        {
            return Task.FromException(new InvalidOperationException($"git runs in the folder above {directory}, which doesn't exist."));
        }

        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        WriteRemoteFiles(directory);
        Tree = new GitWorkingTree(directory, [new GitRemote(AmsProjectLink.Origin, url)], AmsProjectLink.Branch, RemoteHasMain);
        return Task.CompletedTask;
    }

    public Task InitAsync(string directory, CancellationToken cancellationToken)
    {
        Calls.Add("init");
        if (InitFailure is not null)
        {
            return Task.FromException(InitFailure);
        }

        Directory.CreateDirectory(Path.Combine(directory, ".git"));
        Tree = new GitWorkingTree(directory, [], AmsProjectLink.Branch, false);
        return Task.CompletedTask;
    }

    public Task AddRemoteAsync(string root, string name, string url, CancellationToken cancellationToken)
    {
        Calls.Add($"remote add {name} {url}");
        if (AddRemoteFailure is not null)
        {
            return Task.FromException(AddRemoteFailure);
        }

        Tree = (Tree ?? new GitWorkingTree(root, [], AmsProjectLink.Branch, false)) with { Remotes = [.. Tree?.Remotes ?? [], new GitRemote(name, url)] };
        return Task.CompletedTask;
    }

    public Task RemoveRemoteAsync(string root, string name, CancellationToken cancellationToken)
    {
        Calls.Add($"remote remove {name}");
        if (RemoveRemoteFailure is not null)
        {
            return Task.FromException(RemoveRemoteFailure);
        }

        Tree = Tree! with { Remotes = [.. Tree.Remotes.Where(remote => remote.Name != name)] };
        return Task.CompletedTask;
    }

    public Task<bool> FetchBranchAsync(string root, string remote, string branch, CancellationToken cancellationToken)
    {
        Calls.Add($"fetch {remote} {branch}");
        foreach (var (revision, commit) in Fetched)
        {
            Commits[revision] = commit;
        }

        return FetchFailure is null ? Task.FromResult(RemoteHasMain) : Task.FromException<bool>(FetchFailure);
    }

    public Task CheckoutTrackingBranchAsync(string root, string branch, string remote, CancellationToken cancellationToken)
    {
        Calls.Add($"checkout {branch} {remote}");
        if (CheckoutFailure is not null)
        {
            return Task.FromException(CheckoutFailure);
        }

        WriteRemoteFiles(root);
        Tree = Tree! with { Branch = branch, HasCommits = true };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListTreeAsync(string root, string reference, CancellationToken cancellationToken)
    {
        Calls.Add($"ls-tree {reference}");
        return Task.FromResult<IReadOnlyList<string>>([.. RemoteFiles.Keys]);
    }

    public Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> ReadFilesAsync(string root, string commit, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        Calls.Add($"read-files {commit} {string.Join(' ', paths)}");
        return Task.FromResult<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>>(BaseFiles.Where(file => paths.Any(path => file.Key.StartsWith(path + "/", StringComparison.Ordinal))).ToDictionary(file => file.Key, file => new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(file.Value)), StringComparer.Ordinal));
    }

    public Task<IReadOnlyList<string>> ListTrackedAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        Calls.Add($"ls-files {string.Join(' ', paths)}");
        return Task.FromResult<IReadOnlyList<string>>([.. Tracked.Where(file => paths.Any(path => file.StartsWith(path + "/", StringComparison.Ordinal) || file == path))]);
    }

    public Task<IReadOnlyList<string>> ListCommittedPathsAsync(string root, IReadOnlyList<string> revisions, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        Calls.Add($"log {string.Join(' ', revisions)} -- {string.Join(' ', paths)}");
        return Task.FromResult<IReadOnlyList<string>>([.. Committed]);
    }

    public Task<IReadOnlyList<string>> StatusAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        Calls.Add($"status {string.Join(' ', paths)}");
        return Task.FromResult<IReadOnlyList<string>>([.. Changed.Where(file => paths.Any(path => file.StartsWith(path + "/", StringComparison.Ordinal) || file == path))]);
    }

    public Task<GitCommitResult> CommitAsync(string root, IReadOnlyList<string> paths, string message, CancellationToken cancellationToken)
    {
        Calls.Add($"commit {message}");
        CommitRequests.Add((paths, message));
        if (CommitResult.CommitSha is { } commit)
        {
            Commits["HEAD"] = commit;
        }

        return Task.FromResult(CommitResult);
    }

    public Task<GitPushResult> PushAsync(string root, string remote, string branch, string? replaces, CancellationToken cancellationToken)
    {
        Calls.Add(replaces is null ? $"push {remote} {branch}" : $"push --force-with-lease={branch}:{replaces} {remote} {branch}");
        return Task.FromResult(PushResult);
    }

    public Task<string?> ResolveCommitAsync(string root, string revision, CancellationToken cancellationToken)
    {
        Calls.Add($"rev-parse {revision}");
        return Task.FromResult(Commits.TryGetValue(revision, out var commit) ? commit : null);
    }

    public Task<bool> IsOnRemoteBranchAsync(string root, string commit, string remote, CancellationToken cancellationToken)
    {
        Calls.Add($"branch -r --contains {commit}");
        return Task.FromResult(HeadPushed);
    }

    public Task<string?> MergeBaseAsync(string root, string first, string second, CancellationToken cancellationToken)
    {
        Calls.Add($"merge-base {first} {second}");
        return Task.FromResult(Ancestry.Contains((first, second)) ? first : Ancestry.Contains((second, first)) ? second : MergeBase);
    }

    public Task<IReadOnlyList<string>> ListLocalBranchesAsync(string root, CancellationToken cancellationToken)
    {
        Calls.Add("for-each-ref refs/heads");
        return LocalBranchesFailure is null ? Task.FromResult<IReadOnlyList<string>>([.. LocalBranches]) : Task.FromException<IReadOnlyList<string>>(LocalBranchesFailure);
    }

    public Task<string> GitDirectoryAsync(string root, CancellationToken cancellationToken)
    {
        Calls.Add("git-dir");
        return Task.FromResult(GitDirectory ?? Path.Combine(root, ".git"));
    }

    public Task RestoreAsync(string root, string commit, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        Calls.Add($"restore {commit} {string.Join(' ', paths)}");
        return Task.CompletedTask;
    }

    public Task SetRepositoryHelperAsync(string root, string url, CancellationToken cancellationToken)
    {
        Calls.Add($"helper {url}");
        return HelperFailure is null ? Task.CompletedTask : Task.FromException(HelperFailure);
    }

    public Task SetGlobalHelpersAsync(IReadOnlyList<string> hostPatterns, CancellationToken cancellationToken)
    {
        Calls.Add($"global-helpers {string.Join(' ', hostPatterns)}");
        return Task.CompletedTask;
    }

    private static bool Under(string directory, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(full, top, StringComparison.OrdinalIgnoreCase) || full.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void WriteRemoteFiles(string root)
    {
        foreach (var (path, content) in RemoteFiles)
        {
            var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);
        }
    }
}

internal sealed class FakeYesNoPrompt : IYesNoPrompt
{
    public bool Answer { get; set; } = true;

    public List<string> Questions { get; } = [];

    public Task<bool> AskAsync(string question, CancellationToken cancellationToken)
    {
        Questions.Add(question);
        return Task.FromResult(Answer);
    }
}

internal sealed class AmsHarness : IDisposable
{
    public const string AgentFolder = "ContosoHR";
    public const string SchemaName = "cr1a2_contosoHr";

    public AmsHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "ams-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public JsonAgentConfigStore Store { get; } = ManagedAgentFixtures.Store();

    public AgentConfigLocator Locator { get; } = new();

    public FakeMcsService Service { get; } = new();

    public FakeGitPort Git { get; } = new();

    public FakeYesNoPrompt Prompt { get; } = new();

    public AmsTargets Targets { get; set; } = McsRecords.Targets();

    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 8, 2, 10, 15, 0, TimeSpan.Zero));

    public ICopilotComponentCollectionPort Collections { get; } = new SyncComponentCollectionAdapter();

    public FakeCopilotWorkspacePort Workspaces { get; } = new() { WorkspaceExists = false };

    public RecordingConnectPort Connect { get; } = new();

    public RecordingWorkflowClient Flows { get; } = new();

    public WorkflowDataverseClient FlowStore => new(Flows, new StandaloneWorkflowSynchronizer(Flows), new AgentHostDataverseHttpClientAccessor(new StubSyncAuthProvider()));

    public IAgentWorkspaceIdentityPort WorkspaceIdentities { get; set; } = new SyncAgentWorkspaceIdentityReader();

    public InMemoryAuthProfileStore Profiles { get; } = InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant);

    public GitHostTrust Trust { get; } = new(PowerPlatformApiTargetConfig.Public, new AmsEndpointProfile());

    public ManagedAgentResolver Resolver => ManagedAgentFixtures.AgentResolver(Store);

    public AmsRepositoryResolver Repositories => new(Trust, Locator, Collections);

    public AmsProjectLink Link => new(Git, Prompt, new SyncStatePathRule());

    public AmsCloudCacheShelf Shelf => new(Git, Clock);

    public AmsAgentDeletion Deletion => new(Service, Service, Targets, Shelf, Locator, Collections);

    public AmsAgentRegistrar Registrar => new(Service, Deletion, Store, ManagedAgentFixtures.Identities(), new SyncAgentWorkspaceIdentityReader(), Locator, Targets);

    public AmsCommitter Committer => new(Git, Collections);

    public AmsGitDeployStates States => new(Git, Collections);

    public AmsDeployRecords DeployRecords => new(States, Service, Targets);

    public AmsRecordMirror Mirror => new(Service, Deletion, Targets);

    public AmsAgentCreation Creation => CreationOver(Git);

    public AmsAgentCreation CreationOver(IGitPort git) => CreationOver(git, Connect);

    public AmsAgentCreation CreationOver(IGitPort git, ICopilotWorkspaceConnectPort connect) => new(git, Repositories, new(git, Prompt, new SyncStatePathRule()), Registrar, new(git, Collections), DeployRecords, Targets, Service, Store, Clock, new(git, Clock), Workspaces, JoinOver(git, connect));

    public AmsRepositoryJoin JoinOver(IGitPort git, ICopilotWorkspaceConnectPort connect) => new(Locator, Store, Store, new WorkflowWorkspaceAdapter(), new(git, Prompt, new SyncStatePathRule()), git, new(CopilotTestFixtures.Locator(Workspaces, new SystemCopilotFileSystem()), ManagedAgentFixtures.Resolver(), Collections, connect, git), WorkflowPullOver(git), new CopilotProjectIdentityReader(Locator, WorkspaceIdentities));

    public WorkflowPullService WorkflowPullOver(IGitPort git) => new(new WorkflowProjectResolver(Resolver, Locator, ManagedAgentFixtures.Resolver(), new WorkflowWorkspaceAdapter()), new AmsCloudCacheWorkflowStore(FlowStore, new AmsCloudCacheShelf(git, Clock)), ManagedAgentFixtures.WorkflowStates(), ManagedAgentFixtures.StateRules);

    public AmsAgentInitStep InitStep(IAgentHostingOperations operations) => new(operations, Git, Locator, Store, Store, Repositories, Link, JoinOver(Git, Connect), Registrar, Mirror, Committer, Targets, Service);

    public AmsLocalDeployStep LocalDeployStep(IAgentHostingOperations operations) => new(operations, Resolver, DeployRecords);

    public AmsWorkspaceConnector Connector => new(CopilotTestFixtures.Locator(Workspaces, new SystemCopilotFileSystem()), ManagedAgentFixtures.Resolver(), Collections, Connect, Git);

    public AmsRemoteDeployService RemoteDeploy(List<TimeSpan>? delays = null) => new(Resolver, Store, Store, new CopilotProjectIdentityReader(Locator, WorkspaceIdentities), new ManagedAgentPrincipalResolver(Profiles), Git, States, Targets, Service, new McsOperationWaiter(Service, Clock, (_, _) => Task.CompletedTask), Connector, Clock, (delay, _) =>
    {
        delays?.Add(delay);
        return Task.CompletedTask;
    });

    public string AddAgentWorkspace(string folder = AgentFolder, string schemaName = SchemaName, string displayName = "Contoso HR", string? project = null)
    {
        var workspace = Path.Combine(project ?? Root, folder);
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "settings.mcs.yml"), $"displayName: {displayName}\nschemaName: {schemaName}\n");
        return workspace;
    }

    public string DeclareCollection()
    {
        var collection = Path.Combine(Root, "Shared Tools");
        Directory.CreateDirectory(collection);
        File.WriteAllText(Path.Combine(collection, WorkspaceFileNames.Collection), "schemaName: cr1a2_sharedTools\n");
        File.WriteAllText(Path.Combine(Root, AgentFolder, WorkspaceFileNames.References), "componentCollections:\n  - schemaName:\n    directory: ../Shared Tools/\n");
        return collection;
    }

    public async Task<string> AddWorkflowFolderAsync()
    {
        var workflowId = Guid.Parse(McsRecords.WorkflowId);
        var folder = Path.Combine(Root, "Escalate");
        Flows.Cloud[workflowId] = new SyncDataverseClient.WorkflowMetadata { WorkflowId = workflowId, Name = "Escalate", StateCode = 1, StatusCode = 2, ClientData = "{\"properties\":{}}" };
        await FlowStore.DownloadAsync(ManagedAgentFixtures.EnvironmentContext(), workflowId, folder, CancellationToken.None);
        Directory.Delete(Path.Combine(folder, ".mcs"), recursive: true);
        return folder;
    }

    public ManagedAgentRecord AddAgent(string agentId = McsRecords.AgentId, string folder = AgentFolder, bool registered = true, string? mcsAgentId = null, string displayName = "Contoso HR", string? project = null)
    {
        AddAgentWorkspace(folder, displayName: displayName, project: project);
        return Store.Add(project ?? Root, ManagedAgentFixtures.Agent(agentId, displayName) with { AgentLocation = folder, McsSchemaName = SchemaName, McsAgentId = mcsAgentId, SourceControlMetadata = registered ? new AgentSourceControlMetadata(McsRecords.RepositoryId, "main", folder) : null });
    }

    public AgentDeploymentRecord AddDeployment(string agentId = McsRecords.AgentId, string deploymentId = McsRecords.DevDeploymentId, string name = "contoso-dev", string stage = DeploymentStages.Dev, string environmentId = "env-1", DateTimeOffset? lastDeployedTime = null) =>
        Store.Add(Root, ManagedAgentFixtures.Deployment(deploymentId, agentId, name, stage, environmentId, lastDeployedTime));

    public ManagedAgentRecord Agent(string agentId = McsRecords.AgentId) => Store.List(Root).Single(agent => agent.AgentId == agentId);

    public IReadOnlyList<AgentDeploymentRecord> Deployments() => ((IAgentDeploymentRegistry)Store).List(Root);

    public void LinkToRepository(string url = McsRecords.GitUrl, string? branch = AmsProjectLink.Branch)
    {
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
        Git.Tree = new GitWorkingTree(Root, [new GitRemote(AmsProjectLink.Origin, url)], branch, true);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class RecordingConnectPort : ICopilotWorkspaceConnectPort
{
    public const string DevBotId = "d0d0d0d0-7777-4777-8777-000000000017";

    public List<CopilotConnectCommand> Commands { get; } = [];

    public List<CopilotConnectMergeCommand> MergeCommands { get; } = [];

    public Exception? Failure { get; set; }

    public List<string> SkippedCollectionDirectories { get; } = [];

    public CopilotConnectMergeOutcome Merged { get; set; } = new() { Connected = new CopilotConnectOutcome { AgentId = DevBotId } };

    public Action? OnMerge { get; set; }

    public Task<CopilotConnectOutcome> ConnectAsync(CopilotConnectCommand command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return Failure is null ? Task.FromResult(new CopilotConnectOutcome { AgentId = DevBotId, SkippedCollectionDirectories = SkippedCollectionDirectories }) : Task.FromException<CopilotConnectOutcome>(Failure);
    }

    public Task<CopilotConnectMergeOutcome> ConnectAndMergeAsync(CopilotConnectMergeCommand command, CancellationToken cancellationToken)
    {
        MergeCommands.Add(command);
        if (Failure is not null)
        {
            return Task.FromException<CopilotConnectMergeOutcome>(Failure);
        }

        OnMerge?.Invoke();
        return Task.FromResult(Merged with { Connected = new CopilotConnectOutcome { AgentId = DevBotId, SkippedCollectionDirectories = SkippedCollectionDirectories } });
    }
}

internal sealed class FailingWorkspaceIdentityPort(Exception failure) : IAgentWorkspaceIdentityPort
{
    public ProjectIdentity? Read(string workspaceDirectory) => throw failure;

    public string? DetectAuthoringShape(string workspaceDirectory) => throw failure;
}

internal sealed class RecordingHandler<TRequest, TResult>(TResult result) : ICommandHandler<TRequest, TResult>
    where TResult : notnull
{
    public TRequest? Request { get; private set; }

    public Task<TResult> HandleAsync(TRequest request, CancellationToken cancellationToken)
    {
        Request = request;
        return Task.FromResult(result);
    }
}

internal static class RealGit
{
    private static readonly PlaygroundProcessRunner Runner = new();
    private static readonly PlaygroundToolLocator Tools = new(Runner);

    public static GitProcessAdapter Adapter() => new(Runner, Tools, new GitCredentialHelper(Tools, new GitHostTrust(PowerPlatformApiTargetConfig.Public, new AmsEndpointProfile())));

    public static async Task InitAsync(string directory)
    {
        await RunAsync(directory, "init", "--quiet", "--initial-branch=main");
        await RunAsync(directory, "config", "user.name", "Contoso Operator");
        await RunAsync(directory, "config", "user.email", "op@contoso.com");
        await RunAsync(directory, "config", "commit.gpgsign", "false");
    }

    public static async Task<string[]> RunAsync(string directory, params string[] arguments)
    {
        var result = await Runner.RunAsync(new PlaygroundProcessRequest { ExecutablePath = Tools.FindOnPath("git")!, Arguments = ["-c", "core.quotePath=false", .. arguments], WorkingDirectory = directory, Timeout = GitProcessAdapter.LocalTimeout }, CancellationToken.None);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static void Write(string root, string path, string content)
    {
        var target = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
    }
}
