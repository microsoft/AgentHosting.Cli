# Error code reference

Use this directory to look up an MSAgent CLI error code. Each linked page explains what
happened, why it can happen, how to fix it, and how to avoid it.

Read the message and any remediation printed with the code: they identify the affected
project, resource, or operation. Error messages can vary with that context.

Error codes are five-digit identifiers, not process exit codes. The first two digits
identify an area; the last three identify a condition within that area. A missing resource
can have different codes depending on which operation detected it.

The standard `--json` failure output includes `errorCode`, `errorMessage`, and `exitCode`,
with `errorKind` and `remediation` when available. Process exit codes remain `0` for success,
`1` for failure, `2` for usage or context errors, and `3` when sign-in or interaction is
required. Do not use a five-digit error code as the expected process exit status.

Before retrying an operation that changes resources, check its detail page. Some errors
mean a change partially completed or its result is uncertain.

## Areas

| Prefix | Area |
| --- | --- |
| 10 | [Workspace](#workspace) |
| 11 | [Managed agent](#managed-agent) |
| 12 | [Authentication](#authentication) |
| 13 | [Environment](#environment) |
| 14 | [Deployment](#deployment) |
| 15 | [Package](#package) |
| 16 | [Provisioning](#provisioning) |
| 17 | [Copilot Studio](#copilot-studio) |
| 18 | [Evaluation](#evaluation) |
| 99 | [General](#general) |

## Workspace

| Code | Description |
| --- | --- |
| [10001](ErrorCodes/10001.md) | The command could not find the project's agent configuration. |
| [10002](ErrorCodes/10002.md) | The agent configuration could not be read or validated. |
| [10003](ErrorCodes/10003.md) | The configuration version is outside this CLI build's supported range. |
| [10004](ErrorCodes/10004.md) | Several unregistered workspaces match the registration request. |
| [10005](ErrorCodes/10005.md) | The selected record's workspace location no longer resolves to the required local files. |
| [10006](ErrorCodes/10006.md) | The agent's Copilot Studio schema name could not be determined. |
| [10007](ErrorCodes/10007.md) | A newer record update prevented the command from saving stale state. |
| [10008](ErrorCodes/10008.md) | Initialization did not find an existing Copilot Studio project. |
| [10009](ErrorCodes/10009.md) | The request conflicts with an existing managed-agent registration. |
| [10010](ErrorCodes/10010.md) | Creation refused to overwrite an occupied workspace destination. |
| [10011](ErrorCodes/10011.md) | The local project has no usable cloud connection for pulling content. |
| [10012](ErrorCodes/10012.md) | The workspace binding does not match the intended agent identity. |
| [10013](ErrorCodes/10013.md) | Registration could not read the workspace's schema name. |
| [10014](ErrorCodes/10014.md) | A missing schema name prevents safe verification of the connected cloud agent. |
| [10015](ErrorCodes/10015.md) | Another process holds the configuration write lock. |
| [10016](ErrorCodes/10016.md) | The command did not find a Copilot Studio workspace at or above the selected directory. |
| [10017](ErrorCodes/10017.md) | The operation requires an empty output directory. |
| [10018](ErrorCodes/10018.md) | The workspace's recorded organization does not pass the selected environment's origin check. |
| [10019](ErrorCodes/10019.md) | The clone destination contains workspace content that must be preserved. |
| [10020](ErrorCodes/10020.md) | The workspace already has a cloud binding and this attempt cannot retarget it. |
| [10021](ErrorCodes/10021.md) | The project's authoring shape does not support CLI reattachment. |
| [10022](ErrorCodes/10022.md) | Workspace file validation found content that must be repaired before synchronization. |

## Managed agent

| Code | Description |
| --- | --- |
| [11001](ErrorCodes/11001.md) | No local managed-agent record matches the selection. |
| [11002](ErrorCodes/11002.md) | The supplied ID differs from this project's single registered agent. |
| [11003](ErrorCodes/11003.md) | More than one registered agent could match the command. |
| [11004](ErrorCodes/11004.md) | The selected agent is not a supported Copilot Studio type for this command. |
| [11005](ErrorCodes/11005.md) | The command requires an MCSWorkflow record. |
| [11006](ErrorCodes/11006.md) | The record lacks the valid Dataverse workflow ID needed to address its workflow. |
| [11007](ErrorCodes/11007.md) | The source does not contain the required workflow files and layout. |
| [11008](ErrorCodes/11008.md) | The source must identify one workflow per registered record. |
| [11009](ErrorCodes/11009.md) | The workflow's metadata file could not be interpreted. |
| [11010](ErrorCodes/11010.md) | The requested workflow ID was not found in the source environment. |
| [11011](ErrorCodes/11011.md) | The service did not successfully write the workflow to the target environment. |
| [11012](ErrorCodes/11012.md) | An existing workflow cannot be updated until its named connection reference is usable. |
| [11013](ErrorCodes/11013.md) | The workflow on disk has a different ID from the selected record. |
| [11014](ErrorCodes/11014.md) | Replacement and restoration both failed after cloud deletion, leaving the local copy as the recovery source. |
| [11015](ErrorCodes/11015.md) | The clone destination holds workflow changes that have not reached the environment. |
| [11016](ErrorCodes/11016.md) | Pulling would overwrite unpublished changes in the workflow folder. |
| [11017](ErrorCodes/11017.md) | The user-sharing request needs a supported use or edit role. |
| [11018](ErrorCodes/11018.md) | The per-user sharing operation requires a sign-in address or Entra object ID. |
| [11019](ErrorCodes/11019.md) | The sharing command requires an explicit agent ID or name. |
| [11020](ErrorCodes/11020.md) | Organization-wide use sharing does not accept a per-user role. |
| [11021](ErrorCodes/11021.md) | The returned access state differs from the grant request and may be partially changed. |
| [11022](ErrorCodes/11022.md) | The returned access state does not confirm the requested revoke. |
| [11023](ErrorCodes/11023.md) | The agent could not be found in the environment selected from its deployment history. |
| [11024](ErrorCodes/11024.md) | The environment could not resolve the supplied user name to an account. |
| [11025](ErrorCodes/11025.md) | Owner access must be managed through ownership transfer rather than sharing. |
| [11026](ErrorCodes/11026.md) | Duplicate access entries prevent an unambiguous sharing update. |
| [11027](ErrorCodes/11027.md) | The requested source agent did not resolve in the source environment. |
| [11028](ErrorCodes/11028.md) | Removal of the workspace's connection-reference declaration could not be confirmed. |
| [11029](ErrorCodes/11029.md) | The workspace does not declare the requested logical reference name. |
| [11030](ErrorCodes/11030.md) | The reference exists, but a later binding step failed. |
| [11031](ErrorCodes/11031.md) | Declaration failed and may have left a cloud reference absent from the workspace. |
| [11032](ErrorCodes/11032.md) | The service returned an unsuccessful response to the connection deletion request. |
| [11033](ErrorCodes/11033.md) | The reference is already gone, but connection deletion failed or remains uncertain. |
| [11034](ErrorCodes/11034.md) | A new connection survived after both sign-in and automatic cleanup failed. |
| [11035](ErrorCodes/11035.md) | The consent experience did not produce a usable new connection. |
| [11036](ErrorCodes/11036.md) | Bindings are saved, but dependent workflow activation must still succeed. |
| [11037](ErrorCodes/11037.md) | Saving the clone's deployment failed and its newly added agent record was removed. |
| [11038](ErrorCodes/11038.md) | Clone materialization failed before registration or writes to Copilot Studio. |
| [11039](ErrorCodes/11039.md) | Cloud deletion succeeded, but local registry cleanup must be retried. |
| [11040](ErrorCodes/11040.md) | The AMS endpoint override is not a valid credential-free HTTPS address. |
| [11041](ErrorCodes/11041.md) | The CLI refused to follow an authenticated create endpoint's redirect. |
| [11042](ErrorCodes/11042.md) | Creation timed out with an uncertain outcome that must be recovered using the same idempotency key. |
| [11043](ErrorCodes/11043.md) | Cancellation interrupted the request without proving that service-side creation stopped. |
| [11044](ErrorCodes/11044.md) | A transport failure prevented confirmation of the create request's outcome. |
| [11045](ErrorCodes/11045.md) | The service answered, but its response content could not be decoded. |
| [11046](ErrorCodes/11046.md) | The success response lacked a usable set of required creation fields. |
| [11047](ErrorCodes/11047.md) | The returned tenant, environment, or display name differs from the request. |
| [11048](ErrorCodes/11048.md) | A recognized AMS rejection requires the recovery path specified in its message. |
| [11049](ErrorCodes/11049.md) | The target environment's catalog does not contain the requested connector. |
| [11050](ErrorCodes/11050.md) | The supplied connection ID is not among the connector's available candidates. |
| [11051](ErrorCodes/11051.md) | The selected connector does not serve the connection requested for removal. |
| [11052](ErrorCodes/11052.md) | The recipient email address matches more than one user in the tenant directory. |
| [11053](ErrorCodes/11053.md) | Microsoft Graph could not resolve the recipient to a usable Entra user identity. |
| [11054](ErrorCodes/11054.md) | The workflow belongs to a managed solution, so it cannot be cloned or pulled. |
| [11055](ErrorCodes/11055.md) | The workflow name would be refused by Dataverse or cannot be used as a folder name. |
| [11056](ErrorCodes/11056.md) | Deploying would overwrite workflow changes made in the environment. |
| [11057](ErrorCodes/11057.md) | An unpublished draft saved in Copilot Studio blocks deploying the workflow. |

## Authentication

| Code | Description |
| --- | --- |
| [12001](ErrorCodes/12001.md) | The CLI could not complete authentication or account selection. |
| [12002](ErrorCodes/12002.md) | No usable active sign-in profile is available for this command. |
| [12003](ErrorCodes/12003.md) | The client application ID is missing, invalid, or not permitted. |
| [12004](ErrorCodes/12004.md) | Saved authentication profiles could not be safely read or written. |
| [12005](ErrorCodes/12005.md) | Interactive consent returned an identity other than the active CLI account. |
| [12006](ErrorCodes/12006.md) | Required native protection for owned credentials or token storage is unavailable or its replies are rejected; Linux diagnostics include a redacted category and operation. |
| [12007](ErrorCodes/12007.md) | The required token cannot be obtained without interactive sign-in or consent. |
| [12008](ErrorCodes/12008.md) | Microsoft Entra ID is limiting token requests; wait before retrying. |
| [12009](ErrorCodes/12009.md) | The Microsoft corporate account must authenticate through the operating-system broker. |
| [12010](ErrorCodes/12010.md) | The broker could not complete authentication for the requested account. |
| [12011](ErrorCodes/12011.md) | Broker sign-in was canceled instead of falling back to another flow. |
| [12012](ErrorCodes/12012.md) | The Power Platform API token's claims could not be read. |
| [12013](ErrorCodes/12013.md) | The token audience does not match the selected environment's API audience. |
| [12014](ErrorCodes/12014.md) | The token's client application does not match the application the CLI expects. |
| [12015](ErrorCodes/12015.md) | The acquired token does not match the operation's required tenant. |
| [12016](ErrorCodes/12016.md) | The token does not carry the delegated permission needed for the operation. |
| [12017](ErrorCodes/12017.md) | The token's user/application mode differs from the captured profile. |
| [12018](ErrorCodes/12018.md) | A required directory object ID is missing from the token. |
| [12019](ErrorCodes/12019.md) | The operation requires a user principal, not an application profile. |
| [12020](ErrorCodes/12020.md) | Owned credential finalization or cleanup is incomplete without another primary error. |

## Environment

| Code | Description |
| --- | --- |
| [13001](ErrorCodes/13001.md) | An endpoint or inventory identifier failed destination trust validation. |
| [13002](ErrorCodes/13002.md) | The active cloud changed after this process created its service clients. |
| [13003](ErrorCodes/13003.md) | The command requires an environment but no active selection is available. |
| [13004](ErrorCodes/13004.md) | The requested environment is absent from the active account's inventory. |
| [13005](ErrorCodes/13005.md) | The selected environment is not bound to the active account's tenant. |
| [13006](ErrorCodes/13006.md) | The persisted environment selection could not be read, validated, or saved. |
| [13007](ErrorCodes/13007.md) | The target API rejected a missing, invalid, or expired authentication token. |
| [13008](ErrorCodes/13008.md) | The target API rejected a token issued for another resource. |
| [13009](ErrorCodes/13009.md) | The target API denied the account permission for the requested operation. |
| [13010](ErrorCodes/13010.md) | The target API requires administrator consent for an application permission. |
| [13011](ErrorCodes/13011.md) | A target API request failed outside the specific authentication and permission categories. |
| [13012](ErrorCodes/13012.md) | The supplied live-test selectors or identifiers failed input validation. |
| [13013](ErrorCodes/13013.md) | Project records cannot resolve one consistent and enabled live-test destination. |
| [13014](ErrorCodes/13014.md) | Live MCS testing requires a supported public-cloud MCS destination. |

## Deployment

| Code | Description |
| --- | --- |
| [14001](ErrorCodes/14001.md) | The target cloud agent could not be deleted, so its deployment record was retained. |
| [14002](ErrorCodes/14002.md) | The target agent belongs to a managed solution and cannot accept this push. |
| [14003](ErrorCodes/14003.md) | An existing agent uses the target schema, but this project cannot prove ownership. |
| [14004](ErrorCodes/14004.md) | No deployment record matched the selected ID or name in this project and agent scope. |
| [14005](ErrorCodes/14005.md) | More than one deployment could match the requested destination. |
| [14006](ErrorCodes/14006.md) | The requested record violates a deployment name or destination uniqueness rule. |
| [14007](ErrorCodes/14007.md) | The recorded deployment's expected schema no longer resolves to a cloud agent. |
| [14008](ErrorCodes/14008.md) | The operation needs a cloud agent that has not yet been established. |
| [14009](ErrorCodes/14009.md) | Cloud content was pushed, but local deployment or agent records could not be fully updated. |
| [14010](ErrorCodes/14010.md) | The draft was deployed, but the separate publish step did not complete successfully. |
| [14011](ErrorCodes/14011.md) | Draft content was saved, but unresolved push or activation warnings prevented publication. |
| [14012](ErrorCodes/14012.md) | A push stage failed after the target agent had been connected, created, or adopted. |
| [14013](ErrorCodes/14013.md) | The project has no selectable MCS deployment records for the requested deployment route. |
| [14014](ErrorCodes/14014.md) | Deployment records exist, but no explicit destination selector was provided. |
| [14015](ErrorCodes/14015.md) | A required AMS data-plane or control-plane endpoint setting is missing. |
| [14016](ErrorCodes/14016.md) | The configured AMS endpoint is not an acceptable absolute HTTPS service URL. |
| [14017](ErrorCodes/14017.md) | The upload command could not open or hash the selected package file. |
| [14018](ErrorCodes/14018.md) | The selected upload file contains no bytes. |
| [14019](ErrorCodes/14019.md) | The selected package exceeds the 128 MiB upload bound. |
| [14020](ErrorCodes/14020.md) | The selected agent is not a fully repository-bound CodeAgent with runtime metadata. |
| [14021](ErrorCodes/14021.md) | An agent, upload, or deployment response disagrees with the requested or verified values. |
| [14022](ErrorCodes/14022.md) | A deployment update read or response identifies a different target or revision. |
| [14023](ErrorCodes/14023.md) | The upload result lacks the required successful operation and revision identifiers. |
| [14024](ErrorCodes/14024.md) | A deployment read or update returned information the CLI could not validate. |
| [14025](ErrorCodes/14025.md) | An AMS request redirected, and the CLI refused to forward delegated credentials. |
| [14026](ErrorCodes/14026.md) | The deployment read or update redirected instead of returning the expected service response. |
| [14027](ErrorCodes/14027.md) | An agent read or deployment creation timed out before its response completed. |
| [14028](ErrorCodes/14028.md) | An agent read or deployment creation lost its transport exchange. |
| [14029](ErrorCodes/14029.md) | A revision update was attempted, but its outcome was not confirmed before timeout. |
| [14030](ErrorCodes/14030.md) | The initial deployment read timed out, and no revision update request was sent. |
| [14031](ErrorCodes/14031.md) | A transport failure left the submitted revision update's outcome uncertain. |
| [14032](ErrorCodes/14032.md) | The initial deployment read failed in transport before any revision update request. |
| [14033](ErrorCodes/14033.md) | AMS rejected a request or returned an agent or deployment result that could not be confirmed. |
| [14034](ErrorCodes/14034.md) | The local deployment or its backing Azure bot could not be found. |
| [14035](ErrorCodes/14035.md) | Creation could not read the expected bot after its name was reported unavailable. |
| [14036](ErrorCodes/14036.md) | Deployment metadata does not supply a valid Azure Bot Service resource identifier. |
| [14037](ErrorCodes/14037.md) | The requested OAuth connection is absent from the selected deployment's bot. |
| [14038](ErrorCodes/14038.md) | The requested provider is absent from the live Azure Bot Service OAuth catalogue. |
| [14039](ErrorCodes/14039.md) | The channel requested for inspection or update is absent from the selected bot. |
| [14040](ErrorCodes/14040.md) | The CLI cannot identify the supplied or returned channel type in its catalogue. |
| [14041](ErrorCodes/14041.md) | The selected channel does not support the requested update or deletion operation. |
| [14042](ErrorCodes/14042.md) | A supplied setting is not declared in the selected provider or channel schema. |
| [14043](ErrorCodes/14043.md) | The provider or channel configuration lacks a required usable value. |
| [14044](ErrorCodes/14044.md) | A channel create or update attempted to assign a service-controlled parameter. |
| [14045](ErrorCodes/14045.md) | An update explicitly requested an empty value for a required setting. |
| [14046](ErrorCodes/14046.md) | A Boolean provider or channel setting is not expressed as true or false. |
| [14047](ErrorCodes/14047.md) | The channel response does not contain a usable configuration. |

## Package

| Code | Description |
| --- | --- |
| [15001](ErrorCodes/15001.md) | The package publication target configuration is missing or incomplete. |
| [15002](ErrorCodes/15002.md) | The configured endpoint cannot be used for a credential-bearing publication request. |
| [15003](ErrorCodes/15003.md) | The local package selected for publication could not be read. |
| [15004](ErrorCodes/15004.md) | The package bytes read for publication differ from the recorded build digest. |
| [15005](ErrorCodes/15005.md) | A publication redirect was not followed to protect delegated credentials. |
| [15006](ErrorCodes/15006.md) | The upload timed out without confirming whether the package was persisted. |
| [15007](ErrorCodes/15007.md) | The upload exchange failed without confirming remote package state. |
| [15008](ErrorCodes/15008.md) | A durable upload operation exists, but no successful Package Revision was confirmed. |
| [15009](ErrorCodes/15009.md) | The service response does not satisfy the expected terminal upload operation contract. |
| [15010](ErrorCodes/15010.md) | The service digest differs from the SHA-256 of the package bytes sent. |
| [15011](ErrorCodes/15011.md) | The publication result identifies a target different from the requested one. |
| [15012](ErrorCodes/15012.md) | AMS classified the upload rejection as an identity, enablement, binding, or request problem. |
| [15013](ErrorCodes/15013.md) | A root source file conflicts with the transport envelope generated by the package builder. |
| [15014](ErrorCodes/15014.md) | The local archive exceeds the version-zero size bound including metadata. |
| [15015](ErrorCodes/15015.md) | The package exceeds the entry limit including its generated root envelope. |
| [15016](ErrorCodes/15016.md) | The builder rejected a linked or reparse-point source entry. |
| [15017](ErrorCodes/15017.md) | The source includes an entry that cannot be packaged as an ordinary file. |
| [15018](ErrorCodes/15018.md) | The builder could not enumerate or open required source content. |

## Provisioning

| Code | Description |
| --- | --- |
| [16001](ErrorCodes/16001.md) | Provisioning lacks the Azure authentication required to continue. |
| [16002](ErrorCodes/16002.md) | A required argument, Azure scope, application, or permission definition is unavailable. |
| [16003](ErrorCodes/16003.md) | Multiple Azure subscriptions or resource groups require an explicit selection. |
| [16004](ErrorCodes/16004.md) | An existing bot or application cannot be safely reconciled with the requested identity. |
| [16005](ErrorCodes/16005.md) | The target subscription failed the Microsoft.BotService registration preflight. |
| [16006](ErrorCodes/16006.md) | The requested Azure Bot Service SKU or capacity is unavailable in the target context. |
| [16007](ErrorCodes/16007.md) | Organizational Azure Policy prevents the requested resource operation. |
| [16008](ErrorCodes/16008.md) | Missing scope access or a blocking preflight issue prevented provisioning. |
| [16009](ErrorCodes/16009.md) | An Azure resource response or ARM request destination failed validation. |
| [16010](ErrorCodes/16010.md) | Possible credentials were detected in an ABS report or connection metadata. |

## Copilot Studio

| Code | Description |
| --- | --- |
| [17001](ErrorCodes/17001.md) | The Copilot Studio operation has no selected or explicit environment. |
| [17002](ErrorCodes/17002.md) | The environment does not provide the Dataverse organization URL this operation needs. |
| [17003](ErrorCodes/17003.md) | Environment inventory did not advertise the required Copilot Studio endpoint. |
| [17004](ErrorCodes/17004.md) | The service did not return a complete readable sharing snapshot. |
| [17005](ErrorCodes/17005.md) | The recipient selector does not uniquely identify an account in the environment. |
| [17006](ErrorCodes/17006.md) | The workspace connection state needed for workflow readiness could not be saved. |
| [17007](ErrorCodes/17007.md) | The requested agent ID or schema name did not resolve in the destination. |
| [17008](ErrorCodes/17008.md) | An agent with the requested schema name already exists in the environment. |
| [17009](ErrorCodes/17009.md) | The component synchronization check found remote changes requiring review before push. |
| [17010](ErrorCodes/17010.md) | The requested destructive operation was not confirmed. |
| [17011](ErrorCodes/17011.md) | The requested scaffold template is not available in this build. |
| [17012](ErrorCodes/17012.md) | A request was blocked before sending its credential to a different Dataverse origin. |
| [17013](ErrorCodes/17013.md) | The agent exists in the cloud but could not be linked to the local workspace. |
| [17014](ErrorCodes/17014.md) | Attachment failed and the cloud-state check confirmed nothing was left behind. |
| [17015](ErrorCodes/17015.md) | Attachment failed and the CLI could not confirm whether an agent now exists. |
| [17016](ErrorCodes/17016.md) | Readiness polling ended before the agent became ready. |
| [17017](ErrorCodes/17017.md) | A service operation failed or the requested result could not be confirmed. |
| [17018](ErrorCodes/17018.md) | The CLI could not confirm publish completion within its wait window. |
| [17019](ErrorCodes/17019.md) | A requested component collection could not be resolved in the target environment. |
| [17020](ErrorCodes/17020.md) | The quarantine API rejected the request for insufficient administrative access. |
| [17021](ErrorCodes/17021.md) | A connection-reference declaration exists but could not be read or parsed. |
| [17022](ErrorCodes/17022.md) | The same logical reference is declared for conflicting connectors. |
| [17023](ErrorCodes/17023.md) | Agent components were stored but the later knowledge file upload failed. |
| [17024](ErrorCodes/17024.md) | A connected or child agent ID does not exist in the target environment. |
| [17025](ErrorCodes/17025.md) | The service rejected component changes because the cloud agent changed during preparation. |
| [17026](ErrorCodes/17026.md) | A component could not resolve a dependency refused earlier in the same push. |
| [17027](ErrorCodes/17027.md) | The CLI could not determine how much of the push the service stored. |
| [17028](ErrorCodes/17028.md) | The local declaration was removed but deleting its cloud record failed. |
| [17029](ErrorCodes/17029.md) | The cloud record was deleted but the cached baseline still lists it. |
| [17030](ErrorCodes/17030.md) | A workflow tool has a missing or all-zero workflowId. |
| [17031](ErrorCodes/17031.md) | A persistent duplicate-reference refusal stopped the component push. |
| [17032](ErrorCodes/17032.md) | A persistent missing-reference refusal stopped the component push. |

## Evaluation

| Code | Description |
| --- | --- |
| [18001](ErrorCodes/18001.md) | No test set matches the supplied selector for the selected destination. |
| [18002](ErrorCodes/18002.md) | Multiple evaluation test sets have the requested name. |
| [18003](ErrorCodes/18003.md) | An existing test set requires an explicit reuse or replacement decision. |
| [18004](ErrorCodes/18004.md) | The requested evaluation run ID was not found for the selected destination. |
| [18005](ErrorCodes/18005.md) | The evaluation run cannot be deleted before it reaches a terminal state. |
| [18006](ErrorCodes/18006.md) | The run has already reached a state that is not cancellable. |
| [18007](ErrorCodes/18007.md) | The conversation file could not be read as valid evaluation input. |
| [18008](ErrorCodes/18008.md) | Conversation content or the stored total violates an evaluation limit. |
| [18009](ErrorCodes/18009.md) | The polling window expired without confirming the run finished. |
| [18010](ErrorCodes/18010.md) | The selected test set has no conversations to evaluate. |
| [18011](ErrorCodes/18011.md) | Generation returned no conversations, so no test-set change was made. |
| [18012](ErrorCodes/18012.md) | The service acknowledged fewer conversations than were sent. |
| [18013](ErrorCodes/18013.md) | Concurrent changes prevented safe completion of a test-set replacement. |
| [18014](ErrorCodes/18014.md) | An accepted run may be charged and active but the service did not identify it. |
| [18015](ErrorCodes/18015.md) | A failed replacement left the test set short because recovery was incomplete. |

## General

| Code | Description |
| --- | --- |
| [99001](ErrorCodes/99001.md) | An unexpected error occurred without a more specific classification. |
| [99002](ErrorCodes/99002.md) | The command, arguments, or required context are not valid for this operation. |
| [99003](ErrorCodes/99003.md) | This CLI build does not implement the requested operation. |
| [99004](ErrorCodes/99004.md) | The target did not return a response before the operation timed out. |
