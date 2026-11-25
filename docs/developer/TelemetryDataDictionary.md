# CLI telemetry data dictionary

This reference describes the payload constructed by the msagent CLI: field
names, JSON types, presence rules, attribution, and deploy milestones. It is not
an exhaustive SDK common-schema envelope or a collector-side projection contract.

For collection controls, privacy, routing, collector caching, delivery,
diagnostics, and counting, see [Telemetry details](Telemetry.md). User-facing
controls are in the [telemetry command reference](../../src/Microsoft.Agents.Hosting.Cli.Docs/Command_Telemetry.md).

## Event model

All records use the OneDS `event` envelope and an invocation-local batch.
An accepted leaf dispatch can produce one `CommandAttempt` and at most one
`CommandOutcome`; help, version, parser rejection, and group usage produce one
`HostCase`. Telemetry control commands do not measure themselves.

Attempts and outcomes are buffered until disposal. Facts learned during normal
execution can enrich both records before serialization, including after a
failure or cancellation. An attempt does not establish completion, and a missing
outcome proves neither success nor failure.

## Envelope and direct data fields

These are CLI-controlled fields and SDK settings, not every property the SDK
may add.

| Location / setting | JSON type | Meaning |
|---|---|---|
| Envelope `name` | string | `event` |
| Envelope `time` | timestamp string | Record occurrence time from the injected UTC clock |
| SDK instrumentation/routing | SDK-defined | Ingestion configuration for the selected production/nonproduction destination |
| SDK privacy flag | integer metadata | `Microsoft_IsPII_Hash`, `0x100000`; does not itself hash arbitrary dimensions |

The CLI defines 14 direct fields under `data`. Optional fields are omitted when
unavailable, not emitted as null.

| Field | JSON type | Meaning / presence |
|---|---|---|
| `data.app_Name` | string | `powerappsclient` in production; `powerappsclientnonprod` otherwise |
| `data.clientType` | string | `AgentHostingCLI` |
| `data.clusterCategory` | string | `prod` or `test` |
| `data.device_Id` | string | Literal `CLI`; not a unique device identifier or OS classification |
| `data.event_Name` | string | `CommandAttempt`, `CommandOutcome`, or `HostCase` |
| `data.sequenceNumber` | integer | 1-based position in the invocation's batch |
| `data.session_Id` | string, GUID | Random GUID allocated before execution for an enabled invocation and reused for its batch |
| `data.severity` | string | Literal `info`, including failure events |
| `data.timestamp` | string | Record occurrence timestamp using round-trip `O` format |
| `data.operation_Name` | string | `AgentHostingCLI.` plus the canonical dot-separated command path, or `AgentHostingCLI.Host` |
| `data.customDimensions` | string containing JSON | Parse this string to access the custom dimensions below |
| `data.telemetryLocation` | string | Selected collector location: `eu` or `global` |
| `data.tenantId` | string, GUID; optional | Validated tenant when known on attempts, outcomes, and host cases, independently of principal presence |
| `data.principalObjectId` | string, GUID; optional | Validated paired principal on attributed attempts and outcomes, including playground; omitted from host cases |

The session GUID is per invocation, not per terminal, sign-in lifetime, maker
journey, project, or installation. Its use in trusted Dataverse
`x-ms-client-session-id` headers adds no OneDS field. Per-call request IDs are
not emitted as arrays or dependency events. See
[Invocation and Dataverse correlation](Telemetry.md#invocation-and-dataverse-correlation).

## Custom dimensions

`data.customDimensions` is a JSON-encoded string, not a nested object in the
outer payload. Names and capitalization are exact. The table and serialized keys
are sorted alphabetically using ordinal comparison, including optional fields.
There are 24 possible dimensions; not every event contains every dimension.

MCS agent schema names, MCS agent cloud IDs, standalone MCS workflow IDs, and
environment IDs are explicitly permitted target metadata. Their values are not
hashed and may originate in command arguments. In particular, explicit
live-test schema and environment values can be observed after format checks
but before service confirmation, including on failed invocations. Character and
length checks do not anonymize a value or prove the target exists. This does
not permit collection of raw argument lists or unrelated argument values.

| Dimension | JSON type | Presence | Meaning / values |
|---|---|---|---|
| `agentType` | string | Known agent-addressing attempts and outcomes | `MCSAgent`, `MCSWorkflow`, or `CodeAgent`, from normal selected records or validated typed operations |
| `cliVersion` | string | Always | CLI assembly informational version, including build provenance where present |
| `cloud` | string | Always | `prod` / `test`; not Azure public/GCC/China or an environment business classification |
| `deploymentType` | string | Resolved MCS deploy slot; outcome only | `dev`, `test`, `prod`; not environment business purpose |
| `durationMs` | string | Outcomes with a successful duration measurement | Nonnegative elapsed milliseconds; ordinary outcomes use a monotonic clock, while playground uses its execution measurement |
| `environmentId` | string | Attempts and outcomes addressing one environment | Normally resolved environment or format-validated explicit live target; independent of tenant/principal presence |
| `errorCode` | string | Classified errors only | Numeric CLI error code formatted as a decimal string |
| `exitClass` | string | Host records and non-cancelled outcomes | `success`, `usage`, `not-signed-in`, `failure` |
| `hostCategory` | string | Host records only | `help`, `version`, `parser-rejection`, `group-usage` |
| `isFailure` | boolean | Always | Failure classification, not a completion signal |
| `mcsAgentId` | string, GUID | Resolved MCS agent pull, push/deploy, and agent-deleting targets | Actual cloud bot GUID, never the local record or stale home ID; includes deployment cloud deletion |
| `mcsAgentSchemaName` | string | Known schema on addressed MCS agent attempts and outcomes | Validated schema from normal resolution; copy/clone report the source schema |
| `mcsWorkflowId` | string, GUID | Known addressed standalone MCS workflow | Cloud workflow GUID from normal selection/scaffold/clone/init/copy source/operations; not component workflows |
| `operationName` | string | Always | Duplicate of direct `operation_Name` |
| `outcomeCategory` | string | Outcomes only | `success`, `usage`, `not-signed-in`, `failure`, `timeout`, `cancellation` |
| `playgroundInstallResult` | string | Playground outcomes with details | `not-needed`, `installed`, `declined`, `failed` |
| `playgroundVersion` | string | Playground outcomes when valid | Numeric three-component release version; arbitrary/prerelease strings are omitted |
| `principalObjectId` | string, GUID | Attributed attempts and outcomes, including playground | Validated tenant-local Entra user object ID |
| `publishRequested` | boolean | Known MCS deploy request; outcome only | Whether `--publish` was requested; `false` is emitted, not omitted |
| `publishStatus` | string | Known MCS deploy publication progress; outcome only | `not-requested`, `not-reached`, `succeeded`, `blocked`, `failed`, `unknown` |
| `resourceAction` | string | Confirmed MCS `agent deploy` mutation; outcome only | `created`, `updated`, `replaced`; survives later failure |
| `telemetryLocation` | string | Always | Duplicate of direct location: `eu` / `global` |
| `tenantId` | string, GUID | When known | Validated authenticated/fallback issuer tenant; not necessarily the resource's owning tenant |
| `workflowState` | string | Observed standalone MCS workflow deploy state; outcome only | `draft`, `published`, `unknown`; never parent-agent component workflows |

## Attribution and omission rules

### Identity

Known tenant attribution appears in both the direct field and custom dimension,
independently of principal presence, including on `HostCase`. Attributed
attempts and outcomes use the same validated invocation tenant/principal pair,
including normal enrichment, profile changes, and conflict clearing before
flush. Playground uses the same rules; host cases remain principal-free.

Unknown, invalid, conflicting, or cleared tenant attribution is omitted.
Missing, conflicting, and non-user contexts do not gain a principal. A
tenant-only row remains account-unknown. Outcomes can be queried by subject
without joining an attempt; see the [counting contract](Telemetry.md#counting-contract)
for distinct-account semantics and [Identity and privacy](Telemetry.md#identity-and-privacy)
for validation and data-exclusion boundaries.

### Agent type

`agentType` is only a custom dimension, not a direct data field or a
cloud-resource identifier.

| Exact value | Meaning |
|---|---|
| `MCSAgent` | Copilot Studio agent |
| `MCSWorkflow` | Standalone Copilot Studio workflow |
| `CodeAgent` | Code agent |

Command metadata explicitly enables observation for agent operations,
agent-scoped evaluations and deployment resources, and live tests. The type
comes from an existing selected record, normal catalog resolution, or a
validated typed operation; it is not inferred from a command name or unresolved
dispatch fallback.

| Situation | Emission |
|---|---|
| Explicit agent name/ID, schema, or clone target | Emit when the normal operation establishes the type |
| Implicit project/deployment target | Emit when normal resolution selects the addressed agent |
| Agent-scoped evaluations or deployment resources | Emit when the owning agent is known, including non-project evaluation catalog resolution |
| `test ask` or `test playground` | Emit when the explicit or configured target type is established |
| Failure after target resolution | Retain the known type on both buffered events, including package-update failure after current-deployment validation |
| Type becomes known after recording an event | Enrich both buffered events before flush; identity changes do not erase type |
| Repeated equal observations | Keep the same value |
| Unknown, invalid, or conflicting observations | Omit; no `Unknown` token or last-value-wins selection |
| Generic agent/deployment inventory, environment connector/OAuth provider catalogs, unrelated commands, help, controls, or HostCase | Omit |
| Disabled, completed, or disposed observation scope | Do not accept observations or attribute late work to another invocation |

Type observation adds no lookup or agent identifier. These categories do not
distinguish classic/CLI MCS authoring modes or identify a canonical
cross-product resource type.

### MCS agent schema name

`mcsAgentSchemaName` is only a custom dimension. The final observed type must be
`MCSAgent`; workflows and code agents do not emit it.

| Situation | Schema value / emission |
|---|---|
| Selected existing MCS record | Existing `McsSchemaName`, when known |
| Normal selected workspace/catalog resolution | Resolved schema, not a fallback ID or display name |
| Fresh MCS scaffold or registration | Normal scaffold result or parsed workspace schema |
| Copy/clone | **Source schema**, never the copied target's generated/proposed schema |
| Explicit or project/deployment-default target | Emit when normal work establishes the addressed schema |
| Normal resolution returns only a cloud ID | Omit unless another normal step already obtains the schema |
| Late resolution or subsequent failure/cancellation | Retain/enrich the same schema on both buffered events; identity changes do not clear it |
| Repeated casing variants | Compare ordinal-ignore-case; retain the first spelling |
| Missing value | Omit; a null observation does not clear a previously known schema |
| Invalid or differing known schemas | Sticky omission; invalid input uses bounded local diagnostics without its contents |
| Conflicting agent types | Omit schema along with type |
| Inventory, unrelated commands, controls, HostCase, disabled/completed/disposed observation scope | Omit |

Existing Copilot schema naming rules bound and validate the identifier. No
extra file, catalog, authentication, or network lookup is added. The raw schema
is a permitted identifier, including when supplied as a format-validated explicit
live-test argument. This is not authorization to send arbitrary agent IDs,
display names, unrelated argument values, paths, prompts, responses, results,
or free-form errors.

### MCS resource and environment IDs

`mcsAgentId`, `mcsWorkflowId`, and `environmentId` are optional custom dimensions,
not direct data fields.

For pull/push, `mcsAgentId` uses the normally validated connected workspace.
Deploy attach/create captures the returned target immediately, before
persistence/push can fail. Agent/deployment cloud deletion captures the normally
resolved deletion target before discovery, confirmation, or mutation; an absent
cloud target does not fall back to a stored ID. Other agent commands do not
emit stored home bot IDs. Copy/clone retain source-schema semantics but do not
emit source bot IDs in this target field.

`mcsWorkflowId` identifies the addressed standalone workflow, including implicit
project/deployment defaults and the source of a copy/clone. Parent-agent
component workflows never populate it. `environmentId` reuses normal selected
environment observations and format-validated explicit live targets; it is not
limited to agent-addressing commands. Generic enumeration is not selection.
HostCase omits all three fields.

Cloud bot/workflow IDs must be nonempty GUIDs, emitted in canonical D-format.
Environment IDs accept 1-128 ASCII letters, digits, hyphens, and underscores,
including GUID and `Default-<GUID>` forms. Null is unavailable and does not clear
a known value; case-insensitive repeats retain the first spelling. Invalid or
differing values cause sticky omission of the affected scalar. Multiple
environments omit `environmentId` without changing routing. Conflicting agent
types suppress type-specific IDs, not an independently known environment.

Both buffered events receive late-known IDs, including on failure/cancellation
and after profile mutation. They do not prove successful mutation or resource
existence at invocation start. Existing collection, completion,
disposed-nested-scope, and parallel-invocation guards apply. These are permitted
raw resource identifiers, not locally hashed values, local managed-agent IDs,
or a canonical cross-product/runtime join contract. No extra lookup is added.

## Deploy lifecycle and publication

Only `CommandOutcome` for targeted MCS `agent deploy` can carry the five deploy
dimensions. Attempts, HostCase, other commands, and code-agent deployments omit
them. The final addressed type must be `MCSAgent` or `MCSWorkflow`, and collection
must be enabled.

### When each field is set

"Set" means captured during normal command execution. Accepted facts are
serialized into the buffered outcome when telemetry flushes; they do not
create additional events.

| Field | MCS agent: capture point | Standalone MCS workflow: capture point | Retention / omission |
|---|---|---|---|
| `resourceAction` | `created` immediately after attach returns `AgentCreated=true`, before registry persistence or push. Otherwise `updated` only after push returns successfully, including a successfully pushed adopted agent. | Immediately after successful upsert: `created` if `Created`; otherwise `replaced` if `Replaced`; otherwise `updated`. | Retained through later failures. Failed existing-agent push or workflow upsert does not establish an action. Adoption/reattachment alone is not `updated`. MCS agent deploy does not report `replaced`. |
| `deploymentType` | After normal agent/deployment selection validates the MCS agent, before environment resolution/authentication. | After normal workflow/deployment/environment resolution returns, before principal binding and upload preflight. | Canonical slot stage `dev`, `test`, or `prod`. Omitted before that capture point. Not inferred from deployment name or environment ID. |
| `publishRequested` | On entry to the MCS deploy service, from the request's `Publish` flag. | On entry to the workflow deploy service, before workflow-specific validation. | Native boolean `true` or `false`. Retained if later work fails, provided the final MCS type is known. Describes explicit `--publish`, not automatic activation. |
| `publishStatus` | Initialized on service entry. Changes to `blocked` when incomplete-push blockers explicitly withhold publication; `unknown` just before publication; `succeeded` on confirmed return; `failed` on caught non-cancellation failure. | Initialized on service entry. After upsert, changes to `unknown` if activation will run (`ActivationRequested` or `--publish`). After activation returns, classified using confirmed activation, typed failure/refusal, and reported unresolved prerequisites, in that order. | Tracks this invocation's operation. Early failures retain initial status; cancellation during an attempted operation retains `unknown`. See exact values below. |
| `workflowState` | Never set, including for component workflows. | Captured immediately after successful upsert using the existing portal projection. Updated to `published` when activation of the addressed workflow is confirmed, before registry persistence. | `draft`, `published`, or explicit `unknown`; no observed state means omission. Upsert state survives cancelled activation. Confirmed activation survives a later non-cancellation workflow-state cache write failure. |

### Exact publishStatus conditions

| Value | When set |
|---|---|
| `not-requested` | Initial value when `--publish` is absent. Remains if no publication/activation is attempted; a standalone workflow with no activation requested also finishes with this value. |
| `not-reached` | Initial value when `--publish` is present. Remains if validation, environment/authentication, push/upsert, persistence, or another earlier step prevents reaching the attempt. Cancellation before that attempt does not change it to `unknown`. |
| `unknown` | Set immediately before publication/activation. Remains if cancelled without confirmation. Also used when workflow activation returns without confirming the addressed workflow, reporting a failure/refusal, or establishing the reported-target/unbound-prerequisite condition below. |
| `succeeded` | Agent publication returns successfully, or activation confirms the addressed standalone workflow as activated. Confirmed workflow activation takes precedence over a later non-cancellation cache-write failure. |
| `blocked` | Agent publication is explicitly withheld because push/activation left publication blockers; or workflow activation has no confirmed target success or typed failure, reports the addressed workflow, and reports unresolved connection prerequisites. |
| `failed` | Agent publication throws a caught non-cancellation exception; or workflow activation reports a caught failure/service refusal without confirmed activation of the addressed workflow. This does not claim that an older version stopped being live. |

### Example outcome combinations

These are illustrative combinations when the normal target/stage facts are
known, not extra event types. A dash means the field is omitted.

| Situation | `resourceAction` | `publishRequested` | `publishStatus` | `workflowState` |
|---|---|---|---|---|
| New MCS agent created, then push fails; no `--publish` | `created` | `false` | `not-requested` | - |
| Existing MCS agent push fails with `--publish` | - | `true` | `not-reached` | - |
| Existing MCS agent push succeeds, publication fails | `updated` | `true` | `failed` | - |
| New standalone workflow uploaded as draft, no activation requested | `created` | `false` | `not-requested` | `draft` |
| Standalone workflow updated and automatically activated without `--publish` | `updated` | `false` | `succeeded` | `published` |
| Workflow replacement succeeds; requested activation is cancelled, last upsert state was draft | `replaced` | `true` | `unknown` | `draft` |
| Workflow upsert reports an already-published state, but this activation is unconfirmed | `updated` | `true` | `unknown` | `published` |
| New workflow activation confirmed, then local state-cache write fails | `created` | `false` | `succeeded` | `published` |

Overall `outcomeCategory` is separate from these milestones. A failed command
can have `resourceAction=created`; a command that completes with activation
warnings can have `publishStatus=failed` or `blocked`. Neither
`publishRequested=true` nor `workflowState=published` alone proves this
invocation published successfully.

The observer is optional, typed, invocation-local, and failure-isolated.
Invalid values or conflicting stage/request/action observations suppress all
deploy details; final type conflict also suppresses them. Mutable publication
status/workflow state can progress. These best-effort milestones are not
authoritative inventory, creation provenance, a multi-command journey, or
runtime usage.

## Interpretation and compatibility

`isFailure` is a native JSON boolean inside the encoded dimensions, not a
direct data field. It is true for usage, not-signed-in, failure, timeout, parser
rejection, and group usage. It is false for success, caller cancellation, help,
version, and attempts. An attempt's `false` does not prove success.

Ordinary outcome `durationMs` measures from accepted attempt to outcome
recording, including command execution and rendering but excluding parsing,
policy reads, and telemetry flush. Attempts and host cases omit it. A failed
clock measurement omits duration without losing the outcome. Playground
duration is its execution measurement, not agent-turn, model, or production
conversation latency.

Success and caller cancellation omit `errorCode`. Playground-owned failures use
registered `19xxx` CLI codes; specific auth/configuration/target/service codes
are preserved. npm/child-process exit statuses are not CLI registry codes. See
the [error-code directory](../../src/Microsoft.Agents.Hosting.Cli.Docs/ErrorCodes.md).

Historical queries may encounter `IsFailure` instead of `isFailure`, or
`installResult` instead of `playgroundInstallResult`. Current events emit only
the current keys. Historical install-result projections can accept both keys,
preferring `playgroundInstallResult`. Historical `errorCategory` values are
not emitted now; do not reinterpret older generic errors such as `99001` as
specific Playground codes. Older outcomes may require a `session_Id` join to
an attempt for subject attribution.

`station`, `webPlayerScenario`, and `isOnePlayer` are not emitted.
`failureStage`, `failureReason`, and `httpStatusCode` in package-upload command
output are not telemetry dimensions. Command results are not automatically
copied into telemetry.

Keep command counts separated by event type and account counts based on
distinct attempt subjects. Repeating a subject on outcomes does not mean
additional commands or accounts. See the [counting contract](Telemetry.md#counting-contract).

## Implementation references

- [Payload construction and serialization](../../src/Microsoft.Agents.Hosting.Cli/Telemetry/CommandTelemetry.cs): `CommandTelemetryData.Create`.
- [Playground details](../../src/Microsoft.Agents.Hosting.Cli/Telemetry/PlaygroundTelemetryDetails.cs).
- [Deploy observation contract](../../src/Microsoft.Agents.Hosting.Cli.Core/Ports/DeploymentTelemetryObservation.cs).
- [Dataverse session headers](../../src/Microsoft.Agents.Hosting.Cli.CopilotStudio/AgentHostDataverseHttpClientAccessor.cs).
