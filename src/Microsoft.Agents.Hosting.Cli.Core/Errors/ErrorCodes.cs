namespace Microsoft.Agents.Hosting.Cli.Core.Errors;

/// <summary>Stable five-digit error identifiers. Append within each area; never renumber or reuse codes.</summary>
public static class ErrorCodes
{
    /// <summary>Error identifiers for the Workspace area (prefix 10).</summary>
    public static class Workspace
    {
        public const int ConfigNotFound = 10001;
        public const int ConfigInvalid = 10002;
        public const int ConfigUnsupportedVersion = 10003;
        public const int WorkspaceAmbiguous = 10004;
        public const int WorkspaceMissing = 10005;
        public const int SchemaNameUnavailable = 10006;
        public const int ETagMismatch = 10007;
        public const int ProjectNotFound = 10008;
        public const int AlreadyRegistered = 10009;
        public const int WorkspaceNotEmpty = 10010;
        public const int WorkspaceDisconnected = 10011;
        public const int WorkspaceIdentityMismatch = 10012;
        public const int WorkspaceSchemaUnreadable = 10013;
        public const int WorkspaceSchemaUnknown = 10014;
        public const int ConfigLocked = 10015;
        public const int CopilotWorkspaceNotFound = 10016;
        public const int CopilotWorkspaceNotEmpty = 10017;
        public const int CopilotWorkspaceOriginMismatch = 10018;
        public const int CopilotDestinationNotEmpty = 10019;
        public const int CopilotWorkspaceAlreadyConnected = 10020;
        public const int CopilotReattachNotSupported = 10021;
        public const int CopilotWorkspaceFilesUnreadable = 10022;
    }

    /// <summary>Error identifiers for the ManagedAgent area (prefix 11).</summary>
    public static class ManagedAgent
    {
        public const int AgentNotFound = 11001;
        public const int AgentIdConflictsWithProject = 11002;
        public const int AgentAmbiguous = 11003;
        public const int NotAnMcsAgent = 11004;
        public const int NotAnMcsWorkflow = 11005;
        public const int WorkflowIdMissing = 11006;
        public const int WorkflowNotFound = 11007;
        public const int WorkflowAmbiguous = 11008;
        public const int WorkflowMetadataUnreadable = 11009;
        public const int WorkflowSourceNotFound = 11010;
        public const int WorkflowPushFailed = 11011;
        public const int WorkflowConnectionsNotReady = 11012;
        public const int WorkflowFolderAddressesAnotherWorkflow = 11013;
        public const int WorkflowReplaceLostTheCloudCopy = 11014;
        public const int WorkflowProjectHoldsUnpushedChanges = 11015;
        public const int WorkflowLocalChangesWouldBeLost = 11016;
        public const int ShareRoleInvalid = 11017;
        public const int SharePrincipalSelectorRequired = 11018;
        public const int ShareAgentSelectorRequired = 11019;
        public const int ShareRoleNotApplicable = 11020;
        public const int ShareNotApplied = 11021;
        public const int ShareNotRemoved = 11022;
        public const int ShareTargetNotInEnvironment = 11023;
        public const int SharePrincipalNotFound = 11024;
        public const int ShareOwnerNotChangeable = 11025;
        public const int ShareStateAmbiguous = 11026;
        public const int CloneSourceNotFound = 11027;
        public const int ConnectionReferenceNotRemoved = 11028;
        public const int ConnectionReferenceNotDeclared = 11029;
        public const int ConnectionReferenceNotBound = 11030;
        public const int ConnectionReferenceDeclarationFailed = 11031;
        public const int ConnectionNotDeleted = 11032;
        public const int ConnectionNotDeletedAfterReferenceRemoved = 11033;
        public const int ConnectionNotRemoved = 11034;
        public const int ConnectionNotCreated = 11035;
        public const int WorkflowActivationFailed = 11036;
        public const int CloneNotRegistered = 11037;
        public const int CloneNotMaterialised = 11038;
        public const int RecordsSurvivedCloudDelete = 11039;
        public const int CreationEndpointNotUsable = 11040;
        public const int CreationRedirectRejected = 11041;
        public const int CreationTimedOut = 11042;
        public const int CreationCanceled = 11043;
        public const int CreationTransportFailed = 11044;
        public const int CreationResponseDecodingFailed = 11045;
        public const int CreationInvalidResponse = 11046;
        public const int CreationResponseMismatch = 11047;
        public const int CreationServiceRejected = 11048;
        public const int ConnectorNotServed = 11049;
        public const int ConnectionNotACandidate = 11050;
        public const int ConnectionNotServed = 11051;
        public const int SharePrincipalAmbiguous = 11052;
        public const int SharePrincipalLookupFailed = 11053;
        public const int WorkflowManaged = 11054;
        public const int WorkflowNameInvalid = 11055;
        public const int WorkflowCloudChangesWouldBeLost = 11056;
        public const int WorkflowUnpublishedDraft = 11057;
    }

    /// <summary>Error identifiers for the Authentication area (prefix 12).</summary>
    public static class Authentication
    {
        public const int AuthenticationFailed = 12001;
        public const int NotSignedIn = 12002;
        public const int ClientApplicationConfiguration = 12003;
        public const int AuthProfileState = 12004;
        public const int ActiveAccountMismatch = 12005;
        public const int CacheProtectionUnavailable = 12006;
        public const int InteractionRequired = 12007;
        public const int TokenThrottled = 12008;
        public const int CorporateAccountRequiresBroker = 12009;
        public const int BrokerUnavailable = 12010;
        public const int SignInCanceled = 12011;
        public const int TokenClaimMalformed = 12012;
        public const int TokenClaimWrongAudience = 12013;
        public const int TokenClaimWrongApplication = 12014;
        public const int TokenClaimWrongTenant = 12015;
        public const int TokenClaimMissingPermission = 12016;

        /// <summary>git is not on PATH, so msagent cannot point it at its credential helper.</summary>
        public const int GitNotFound = 12017;

        /// <summary>git could not write the credential helper into the user's global git config.</summary>
        public const int GitConfigurationFailed = 12018;
    }

    /// <summary>Error identifiers for the Environment area (prefix 13).</summary>
    public static class Environment
    {
        public const int EndpointTrust = 13001;
        public const int CloudRetargeted = 13002;
        public const int EnvironmentNotSelected = 13003;
        public const int EnvironmentNotFound = 13004;
        public const int EnvironmentTenantMismatch = 13005;
        public const int AgentHostingState = 13006;
        public const int TargetAuthExpired = 13007;
        public const int TargetWrongAudience = 13008;
        public const int TargetPermissionDenied = 13009;
        public const int TargetConsentRequired = 13010;
        public const int TargetRequestFailed = 13011;
        public const int LiveTestInvalidInput = 13012;
        public const int LiveTestInvalidConfiguration = 13013;
        public const int LiveTestUnsupportedTarget = 13014;
    }

    /// <summary>Error identifiers for the Deployment area (prefix 14).</summary>
    public static class Deployment
    {
        public const int DeploymentTargetDeleteFailed = 14001;
        public const int TargetAgentIsReadOnly = 14002;
        public const int UnverifiedAgentInTarget = 14003;
        public const int SlotNotFound = 14004;
        public const int SlotAmbiguous = 14005;
        public const int SlotAlreadyExists = 14006;
        public const int TargetAgentMissing = 14007;
        public const int NotDeployedYet = 14008;
        public const int DeployRecordedNowhere = 14009;
        public const int PublishFailedAfterPush = 14010;
        public const int PublishWithheldAfterIncompletePush = 14011;
        public const int DeployIncomplete = 14012;
        public const int DeploymentRecordsEmpty = 14013;
        public const int DeploymentSelectorRequired = 14014;
        public const int AgentDeployTargetNotConfigured = 14015;
        public const int AgentDeployEndpointNotUsable = 14016;
        public const int AgentDeployPackageReadFailed = 14017;
        public const int AgentDeployPackageEmpty = 14018;
        public const int AgentDeployPackageTooLarge = 14019;
        public const int AgentDeployAgentNotCompatible = 14020;
        public const int AgentDeployResponseMismatch = 14021;
        public const int AgentDeployDeploymentUpdateResponseMismatch = 14022;
        public const int AgentDeployInvalidResponse = 14023;
        public const int AgentDeployDeploymentUpdateInvalidResponse = 14024;
        public const int AgentDeployRedirectRejected = 14025;
        public const int AgentDeployDeploymentUpdateRedirectRejected = 14026;
        public const int AgentDeployTimedOut = 14027;
        public const int AgentDeployTransportFailed = 14028;
        public const int UpdateTimedOut = 14029;
        public const int UpdateReadTimedOut = 14030;
        public const int UpdateTransportFailed = 14031;
        public const int UpdateReadFailed = 14032;
        public const int AgentDeployServiceRejected = 14033;
        public const int DeploymentNotFound = 14034;
        public const int DeploymentReadFailed = 14035;
        public const int DeploymentInvalidBotResource = 14036;
        public const int DeploymentOAuthConnectionNotFound = 14037;
        public const int DeploymentOAuthServiceProviderNotFound = 14038;
        public const int DeploymentChannelNotFound = 14039;
        public const int DeploymentChannelDefinitionNotFound = 14040;
        public const int DeploymentChannelOperationNotSupported = 14041;
        public const int DeploymentParameterNotDeclared = 14042;
        public const int DeploymentRequiredParameter = 14043;
        public const int DeploymentReadOnlyParameter = 14044;
        public const int DeploymentCannotClearRequiredParameter = 14045;
        public const int DeploymentInvalidParameterValue = 14046;
    }

    /// <summary>Error identifiers for the Package area (prefix 15).</summary>
    public static class Package
    {
        public const int PublicationTargetNotConfigured = 15001;
        public const int PublicationEndpointNotUsable = 15002;
        public const int PublicationPackageReadFailed = 15003;
        public const int PublicationLocalDigestChanged = 15004;
        public const int PublicationRedirectRejected = 15005;
        public const int PublicationRequestTimedOut = 15006;
        public const int PublicationRequestFailed = 15007;
        public const int PublicationDurableOperationDidNotSucceed = 15008;
        public const int PublicationInvalidResponse = 15009;
        public const int PublicationDigestMismatch = 15010;
        public const int PublicationResponseIdentityMismatch = 15011;
        public const int PublicationServiceRejected = 15012;
        public const int EnvelopeSourceConflict = 15013;
        public const int ArchiveSizeExceeded = 15014;
        public const int EntryCountExceeded = 15015;
        public const int SourceLink = 15016;
        public const int SourceEntryNotAFile = 15017;
        public const int SourceUnreadable = 15018;
    }

    /// <summary>Error identifiers for the Provisioning area (prefix 16).</summary>
    public static class Provisioning
    {
        public const int MissingAzureAuth = 16001;
        public const int MissingContext = 16002;
        public const int AmbiguousContext = 16003;
        public const int NamingConflict = 16004;
        public const int ProviderNotRegistered = 16005;
        public const int SkuQuota = 16006;
        public const int PolicyDenied = 16007;
        public const int PermissionDenied = 16008;
        public const int InvalidResponse = 16009;
        public const int SecretMaterial = 16010;
    }

    /// <summary>Error identifiers for the CopilotStudio area (prefix 17).</summary>
    public static class CopilotStudio
    {
        public const int EnvironmentNotSelected = 17001;
        public const int EnvironmentMissingInstanceUrl = 17002;
        public const int AgentManagementEndpointMissing = 17003;
        public const int SharingStateNotReturned = 17004;
        public const int SharePrincipalAmbiguous = 17005;
        public const int ConnectionStateNotRecorded = 17006;
        public const int AgentNotFound = 17007;
        public const int AgentAlreadyExists = 17008;
        public const int PushConflictsRequirePull = 17009;
        public const int ConfirmationRequired = 17010;
        public const int TemplateNotFound = 17011;
        public const int UntrustedDataverseOrigin = 17012;
        public const int BootstrapReattachFailed = 17013;
        public const int BootstrapAttachFailed = 17014;
        public const int BootstrapAttachUncertain = 17015;
        public const int StatusTimeout = 17016;
        public const int ServiceError = 17017;
        public const int PublishTimeout = 17018;
        public const int ComponentCollectionNotFound = 17019;
        public const int QuarantinePermissionDenied = 17020;
        public const int ConnectionReferencesUnreadable = 17021;
        public const int ConnectionReferencesConflict = 17022;
        public const int KnowledgeUploadFailed = 17023;
        public const int ConnectedAgentNotInTarget = 17024;
        public const int RemoteChangedDuringPush = 17025;
        public const int PushFailedAfterRefusedComponents = 17026;
        public const int PushResponseUnreadable = 17027;
        public const int ConnectionReferenceNotDeleted = 17028;
        public const int ConnectionReferenceCacheStale = 17029;
        public const int WorkflowToolIdEmpty = 17030;
        public const int DuplicateConnectionReference = 17031;
        public const int MissingConnectionReference = 17032;

        /// <summary>Copilot Studio finished the publish with a status other than Succeeded, or its progress could not be read.</summary>
        public const int PublishFailed = 17033;

        /// <summary>Copilot Studio was still provisioning the classic agent when the wait ran out, so nothing was pushed into it or published.</summary>
        public const int AgentNotProvisioned = 17034;
    }

    /// <summary>Error identifiers for the Evaluation area (prefix 18).</summary>
    public static class Evaluation
    {
        public const int TestSetNotFound = 18001;
        public const int TestSetAmbiguous = 18002;
        public const int TestSetAlreadyExists = 18003;
        public const int RunNotFound = 18004;
        public const int RunNotTerminal = 18005;
        public const int RunNotCancellable = 18006;
        public const int ConversationFileInvalid = 18007;
        public const int ConversationLimitExceeded = 18008;
        public const int RunTimedOut = 18009;
        public const int TestSetEmpty = 18010;
        public const int ConversationsNotGenerated = 18011;
        public const int ConversationsNotStored = 18012;
        public const int ReplacementConflicted = 18013;
        public const int RunNotIdentified = 18014;
        public const int ReplacementInterrupted = 18015;
    }

    /// <summary>Error identifiers for the AMS agent area (prefix 19).</summary>
    public static class AmsAgent
    {
        /// <summary>Test takes only a commit Dev deployed.</summary>
        public const int CommitNotAllowedForStage = 19002;

        /// <summary>The deployment's stage does not fit its environment.</summary>
        public const int DeploymentStageEnvironmentInvalid = 19003;

        /// <summary>The preceding stage has nothing deployed to promote.</summary>
        public const int SourceDeploymentNotReady = 19004;

        /// <summary>Another operation is running on this deployment.</summary>
        public const int DeploymentOperationInProgress = 19006;

        /// <summary>The service has no free worker right now.</summary>
        public const int OperationCapacityExceeded = 19007;

        /// <summary>The agent's source kept changing in the repository while the operation ran.</summary>
        public const int SourceConflict = 19008;

        /// <summary>You cannot write to the repository.</summary>
        public const int RepositoryNotWritable = 19009;

        /// <summary>The repository was not found.</summary>
        public const int RepositoryNotFound = 19010;

        /// <summary>The repository cannot hold MCS agents of this environment.</summary>
        public const int RepositoryNotSupported = 19011;

        /// <summary>Another AMS agent holds the repository folder.</summary>
        public const int AgentFolderBound = 19012;

        /// <summary>Another AMS agent claims the Copilot Studio agent or flow.</summary>
        public const int BotClaimed = 19013;

        /// <summary>The agent source is not valid.</summary>
        public const int SourceValidationFailed = 19014;

        /// <summary>The agent source is too large.</summary>
        public const int SourceTooLarge = 19015;

        /// <summary>The repository checks rejected the source.</summary>
        public const int SourceCheckFailed = 19016;

        /// <summary>The service could not act on your behalf.</summary>
        public const int OnBehalfOfUnavailable = 19017;

        /// <summary>The operation failed in Copilot Studio.</summary>
        public const int OperationFailed = 19018;

        /// <summary>A test-only endpoint override is not usable.</summary>
        public const int EndpointOverrideNotUsable = 19026;

        /// <summary>This command needs the on-behalf-of endpoint.</summary>
        public const int OnBehalfOfEndpointRequired = 19027;

        /// <summary>Stopped waiting for the operation.</summary>
        public const int OperationWaitInterrupted = 19029;

        /// <summary>The operation took too long.</summary>
        public const int OperationWaitTimedOut = 19030;

        /// <summary>The agent cannot be used for this command.</summary>
        public const int AgentNotEligible = 19031;

        /// <summary>The Dev deployment has no Copilot Studio agent or workflow yet.</summary>
        public const int CaptureAgentNotInCloud = 19038;

        /// <summary>Dev's edits and a later commit changed the same lines, so nothing was captured.</summary>
        public const int CaptureConflict = 19050;

        /// <summary>Dev had edits the commit being deployed does not hold, so nothing was deployed.</summary>
        public const int DevEditsNotInCommit = 19051;

        /// <summary>The project folder is below its git repository's root.</summary>
        public const int ProjectNotRepositoryRoot = 19052;

        /// <summary>The project folder holds files that are not part of the project.</summary>
        public const int ProjectFolderNotDedicated = 19053;

        /// <summary>The project folder's remote is not a GRS repository.</summary>
        public const int NotAGrsRepository = 19054;

        /// <summary>--repository-id differs from the repository the project folder's remote names.</summary>
        public const int RepositoryMismatch = 19055;

        /// <summary>The Copilot Studio agent or workflow a register or deploy record names doesn't exist in its environment.</summary>
        public const int BotNotFound = 19056;

        /// <summary>The agent's folder already exists on the repository's main branch.</summary>
        public const int FolderExistsInRepository = 19057;

        /// <summary>Linking the repository would overwrite local files.</summary>
        public const int RepositoryWouldOverwrite = 19058;

        /// <summary>Git tracks a .mcs cache inside a folder msagent commits or restores, or a commit msagent would push holds one.</summary>
        public const int McsFolderInRepository = 19059;

        /// <summary>The project folder is itself the agent's folder.</summary>
        public const int WorkspaceAtProjectRoot = 19060;

        /// <summary>AMS does not know the agent.</summary>
        public const int AgentNotRegistered = 19062;

        /// <summary>A Prod deploy takes no commit.</summary>
        public const int CommitNotAccepted = 19063;

        /// <summary>AMS does not know which commit Dev holds.</summary>
        public const int CaptureBaseUnknown = 19064;

        /// <summary>The commit is not in the repository, or does not hold the agent's folder.</summary>
        public const int CommitNotFound = 19065;

        /// <summary>AMS already holds the agent id with other details.</summary>
        public const int AgentIdConflict = 19066;

        /// <summary>A git command failed.</summary>
        public const int GitCommandFailed = 19067;

        /// <summary>AMS already holds the deployment id with other details.</summary>
        public const int DeploymentIdConflict = 19068;

        /// <summary>AMS's record of the agent changed between msagent's read and its delete.</summary>
        public const int AgentRecordChanged = 19069;

        /// <summary>AMS refused a value of the request, such as the agent folder.</summary>
        public const int RequestRejected = 19070;

        /// <summary>A pull met uncommitted changes in a folder whose commit never synced with Copilot Studio.</summary>
        public const int UnsyncedCommitHasChanges = 19071;

        /// <summary>A pull met a folder whose commit never synced with Copilot Studio and lacks the commit Copilot Studio last synced from.</summary>
        public const int SyncedCommitNotMerged = 19072;
    }

    /// <summary>Error identifiers for the General area (prefix 99).</summary>
    public static class General
    {
        public const int Unknown = 99001;
        public const int Usage = 99002;
        public const int PendingImplementation = 99003;
        public const int TransportTimeout = 99004;
    }
}
