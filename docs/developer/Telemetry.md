# CLI telemetry details

This document describes collection policy, privacy boundaries, routing, collector
caching, delivery, diagnostics, and analysis. The
[telemetry data dictionary](TelemetryDataDictionary.md) defines emitted fields,
presence rules, attribution, and deploy milestones.
Follow [CONTRIBUTING.md](../../CONTRIBUTING.md) for
contributor requirements and the
[command reference](../../src/Microsoft.Agents.Hosting.Cli.Docs/Command_Telemetry.md)
for user-facing controls.

## Command telemetry

The CLI uses one command-telemetry sender and one shared schema. A leaf dispatch
can produce one `CommandAttempt` and at most one `CommandOutcome`; help, version,
parser rejection, and group usage produce one `HostCase`. All use the shared
OneDS `event` envelope and one invocation-local batch. Outcomes distinguish
success, usage, not-signed-in, failure, timeout, and caller cancellation. A
rendering failure leaves the attempt without inventing an outcome. The
`telemetry` control group never measures itself.

## Data dictionary

The [telemetry data dictionary](TelemetryDataDictionary.md) is the authoritative
field reference. It covers the encoded dimensions, exact values and types,
presence and omission rules, and interpretation of historical events.

| Topic | Reference |
|---|---|
| Envelope and direct data fields | [Direct fields](TelemetryDataDictionary.md#envelope-and-direct-data-fields) |
| Custom dimensions | [Dimension table](TelemetryDataDictionary.md#custom-dimensions) |
| Tenant/principal field presence | [Identity attribution](TelemetryDataDictionary.md#identity) |
| Addressed agent and resource fields | [Attribution and omission rules](TelemetryDataDictionary.md#attribution-and-omission-rules) |
| MCS deploy milestones | [Capture timing, publication status, and examples](TelemetryDataDictionary.md#deploy-lifecycle-and-publication) |
| Failure flags, durations, errors, and historical names | [Interpretation and compatibility](TelemetryDataDictionary.md#interpretation-and-compatibility) |

## Identity and privacy

After collection policy enables an invocation, normal command authentication may
enrich buffered events with the issuer tenant. A tenant-local Entra object ID is
accepted only when the SDK result has an account, exactly one matching `tid`,
exactly one nonempty `oid`, and either no `idtyp` or exactly one `idtyp=user`.
App/workload identities and missing, malformed, duplicate, or tenant-mismatched
claims remain tenant-only. Unknown context does not prevent collection.
Conflicting fresh user contexts remove the object ID and retain a tenant only
when the contexts agree on it.

The selected startup profile supplies a fallback for local commands only when
it has token-free metadata persisted by normal `auth login` under the same CLI
client, authority, and cloud. Silent and consent authentication can attribute
the current invocation but never update profile metadata. Fresh normal-auth
results take precedence; profile switch, login activation, and logout replace
or clear selected context. Profiles without metadata remain attribution-unknown
until normal login; telemetry never forces login. The optional profile read is
an independent, delete-sharing snapshot and never takes the profile-store mutex.

Identity attribution does not discover operating-system accounts, initiate
login or consent, acquire tokens, call Graph, or use geography-only
authentication. The MSAL home-account ID is only a local profile key, never an
Entra object ID or emitted identifier. Disabled, opted-out, policy-failed, and
nested-suppressed invocations do not read profile attribution or inspect claims.

Payloads never carry usernames, email addresses, home-account IDs, PUIDs, tokens,
hardware IDs, persistent local-instance IDs, raw JWTs, paths, URLs, free-form
errors, results, or account-cache data. Raw argument lists and arbitrary argument
values are excluded, with explicit exceptions for the allowlisted MCS agent
schema name, MCS agent cloud ID, standalone MCS workflow ID, and environment ID
described in the [data dictionary](TelemetryDataDictionary.md#custom-dimensions).
These identifiers are not hashed. Format-validated explicit live-test schema
and environment arguments can be observed before service confirmation and remain
on failed invocations; character and length checks do not anonymize the values
or establish that the target exists. There is no alternate identity mode or
identity-selection environment variable.

The [identity field rules](TelemetryDataDictionary.md#identity) define when the
validated tenant/principal pair appears on each event, including playground.
A tenant-only event does not identify an account; account counts require both
validated values. No `objectId`, `pptoolsDeviceId`, identity mode, or payload
schema-version field is emitted. Playground does not add identity acquisition
or collect prompts, responses, or external playground session identifiers.

Every event uses the PPUX SDK's `Microsoft_IsPII_Hash` (`0x100000`) metadata flag.
This does not mean arbitrary Part C fields are hashed: tenant and principal IDs
are sent as validated GUID strings. Collector-side classification/projection is
an ingestion-owner contract. The pair is an authenticated tenant-local account
proxy, not proof of a human, a person across tenants, a device, an installation,
all users, or an adoption rate.

## Collection policy and routing

Public ingestion-routing identifiers are declared in
`src/Microsoft.Agents.Hosting.Cli/Telemetry/TelemetryIngestionConfiguration.cs`
as `ProductionKey` and `NonProductionKey`. These are routing identifiers, not
authentication credentials.

The host resolves the Power Platform ring from the active profile before
composition. Use `msagent auth create --cloud <cloud>` to create and activate
a profile for the intended cloud/ring, or `msagent auth select --name <name>`
to activate an existing profile. Selection does not change a profile's cloud/ring;
there is no need to change `Program.cs` or rebuild for nonproduction.
Public production and nonproduction rings select their corresponding routing
value and metadata. A missing or unreadable ring cannot activate collection.
Sovereign-cloud targets do not fall back to public ingestion.

Collection defaults to enabled when activation is available.
`AH_CLI_TELEMETRY_OPTOUT=1|true` opts out; `0|false` explicitly opts in.
An unset value defers to the remembered preference and composition default.
Invalid nonempty overrides or invalid preferences fail closed when they decide
policy; an environment override takes precedence over the remembered preference.
Before ordinary dispatch, the optional policy read has a 250-millisecond caller
deadline. Timeout disables collection for that invocation, even if I/O finishes
later. Caller cancellation propagates. Explicit `telemetry enable`, `disable`,
and `status` retain normal errors and cancellation without that optional deadline.

Production collector geography follows environments actually resolved by normal
product work. Active, explicit, workspace, deployment, and `env select`
resolution share an invocation-scoped observation boundary; inventory enumeration
alone is not a target. At disposal, on a collector-cache miss the host derives a
trusted environment-scoped Power Platform API host from the environment ID and selected cloud/ring, then
requests `/gateway/cluster?api-version=1`. Only `geoLongName` is used. Inventory
geography is neither used nor persisted.

That lookup can acquire or refresh a token only for the exact selected CLI
account, through its exact-profile silent MSAL path, including broker/WAM-backed
user profiles. Broker account listing does not enable discovery of unrelated OS
accounts, and acquisition requires the exact selected cached account. It cannot
choose the default OS account, another cached account, or an interactive fallback.
Workload/Azure credential profiles remain excluded from this optional path.
Missing account, token,
request, timeout, and response errors use the default collector route.
Metadata-only authentication never enriches telemetry identity.

### Collector decision cache

The host caches confirmed per-environment collector decisions for 24 hours at
`<config directory>/telemetry/collector-cache.json`. A valid hit skips both
metadata-only token acquisition and the cluster HTTP request. This lifetime is
CLI policy, not a confirmed 1DS cache requirement. The cache is separate from
NuGet update metadata but follows its timestamped, atomic-replacement pattern.

Each key is a SHA-256 digest of cloud, ring, tenant and environment, with
case-insensitive tenant/environment identifiers. The schema/policy-versioned
document contains only these digests, `EU` or `Global`, and successful lookup
timestamps. It stores no raw identifiers, geography strings, tokens, ingestion
keys or endpoint URLs. Bump the routing policy version when changing the
recognized-geography-to-collector mapping.

Only recognized `geoLongName` responses populate the cache. Timeout, token or
HTTP failure, missing/unknown geography and fallback decisions do not.
Expired entries are never used as stale fallbacks; misses retain the existing
live lookup. Cached and fresh decisions use the same conservative aggregation:
Global requires every observed environment to be confirmed non-EU.
Captured-profile/context validation, public-cloud/ingestion-key gates and
collection policy remain ahead of cache use; cached data cannot enable sending
for an otherwise suppressed invocation.

Reads are bounded to 64 KiB and 128 entries. Invalid schema/policy versions,
duplicate/invalid keys, invalid collector labels and future timestamps invalidate
the document. Writes prune expired entries, evict the oldest entries when full,
and use unique temporary files followed by atomic replacement. There is no
cross-process lock: competing writers can lose another writer's additions,
causing a later safe miss, but never expose partially written JSON.
Expected cache I/O failures remain optional and use fixed local diagnostic
category 20 without changing command output or exit behavior. Cache operations
share the existing lookup/flush deadlines; cancellation prevents a late lookup
result from being persisted. Deleting only this cache file forces fresh lookups
without changing telemetry consent or account/token state.

Production defaults to the EU collector. Missing, empty, or unrecognized
geography, unavailable geography clients, and lookup failures/deadlines retain
this conservative fallback. Incomplete, conflicting, or excess environment
observations also default to EU without a metadata lookup. Missing, ambiguous,
or conflicting captured-profile context retains its separate no-send boundary.

At most four distinct environment/tenant pairs are retained. Global is selected
only when every observed target has a recognized non-EU geography or valid
confirmed non-EU cache entry. Any EU or unknown target selects EU for the whole
batch. A batch never splits or changes route after sending begins.
Environment-independent and tenant-wide enumeration commands use EU without
metadata authentication.
Nonproduction continues to use global/US without geography lookup.
Opt-out, control commands, and policy-suppressed invocations do no metadata work.
Sovereign selections retain their no-send boundary, and ingestion keys and
prod/nonprod application names remain unchanged.

## Invocation and Dataverse correlation

An enabled invocation allocates one random GUID before command execution. That
GUID is reused as `data.session_Id` on its batch, preserving attempt/outcome
pairing and sequence numbers. It is not a persistent installation, maker
journey, project, or conversation ID.

The host exposes the GUID through the dependency-free Core
`ICommandInvocationContext` port only while an accepted leaf is active. The
existing async-scoped accessor isolates invocations; suppressed/nested-suppressed,
control, absent, and ended scopes expose no correlation. CopilotStudio composition
without the optional port remains supported.

The CLI-owned Dataverse accessor captures the active GUID before async
authentication and sets `x-ms-client-session-id` on each trusted Dataverse
request, never shared `HttpClient.DefaultRequestHeaders`. When active, this
CLI-owned header uses the invocation GUID. Suppression does not add the header
or remove existing operational request IDs. Empty GUIDs are not added.

Sync's per-logical-call `x-ms-client-request-id` remains distinct and unchanged,
including across retries. The existing retry and no-replay behavior is preserved.
Gateway/non-Dataverse calls receive no new session header. No request-ID arrays,
dependency event stream, extra service calls, or header/body/URL capture are added.
Correlation failures use local optional diagnostic category 17 without altering
product authentication or HTTP behavior.

Offline tests exercise the pinned package's retry behavior, event/header matching,
parallel isolation, and suppression. Backend retention, query columns, and actual
service joins require separately approved live evidence; propagation alone does
not establish runtime resource attribution or CMR compatibility.

## Delivery and diagnostics

A shared six-second default deadline bounds destination resolution, serialization, and
transport, separately from the policy-read budget. Production metadata resolution
has a five-second default/maximum sub-deadline, leaving one second of the total
budget for serialization and transport. Deadlines
cannot terminate already-started noncooperative dependencies, but late results
cannot mutate the route, start sending, or cross invocations. There is no retry.
Telemetry failures cannot change command output, cancellation, or exit behavior.

Optional failures are silent by default. Local EventPipe/ETW diagnostics can
enable `Microsoft-AgentHosting-Cli-OptionalMeasurement`. Its
`OptionalMeasurementFailure` event contains only a fixed integer category, never
an exception, message, stack, path, URL, routing value, token, identity, claim, or
argument. It writes no console/JSON output, persists no log, and emits no
telemetry about telemetry.

With `--debug`, a separate `[telemetry-routing]` trace is written to stderr
while the existing destination lookup runs. It records monotonic elapsed
milliseconds, captured-context availability, cloud/ring, target completeness and
count, the metadata budget, silent-token availability, request start, numeric
HTTP status, response parsing, lookup failure/deadline/cancellation, and final
`collector=EU`, `Global`, or `none`. It does not add a request or a 1DS field,
change the five-second budget, prompt for authentication, or write to stdout/JSON.
When the lookup budget expires, `lookup-deadline` includes `pendingStage` from
the last recorded lookup stage. For example, `pendingStage=TokenAcquisitionStarted`
means silent token acquisition had not reported completion when the deadline was
recorded; `pendingStage=RequestStarted` means the cluster HTTP request was pending.
This is a stage snapshot, not evidence that a noncooperative dependency stopped.

Cache diagnostics use fixed stages: `cache-hit collector=EU|Global`,
`cache-miss`, `cache-expired`, `cache-written collector=EU|Global`,
`cache-invalid`, `cache-read-failed`, `cache-write-failed` and
`cache-cleanup-failed`. They expose no key digests, identifiers, paths or failure
text. A hit is reuse of an earlier confirmed decision, not a fresh HTTP result
or evidence that 1DS accepted a batch.

The `geography-result` stage contains only a recognized normalized routing value,
`missing`, or `unrecognized`; arbitrary `geoLongName` strings are not printed.
Token validation failures report `TokenRejected`, a missing property reports
`GeographyMissing`, and malformed JSON reports `ResponseInvalid`. Tokens,
profile/account/tenant/environment identifiers, URLs, exception text, headers and
response bodies are not included in these trace lines. Environment ordinals
distinguish multiple lookups without exposing identifiers.

Selected-profile authentication adds fixed reason stages before a no-token or
failed lookup: `TokenProfileUnsupported`, `TokenProviderUnavailable`,
`TokenAccountUnavailable`, `TokenInteractionRequired`, `TokenBrokerUnavailable`,
`TokenAccountMismatch`, and `TokenAcquisitionFailed`. An owned profile whose
exact cached account is absent can report `TokenInteractionRequired`, matching
its existing renewal contract. Interaction-required results never trigger UI.
Throttling and other failures retain their existing typed behavior and are
isolated by the optional routing boundary; they do not select another identity.

Tracing is opt-in, invocation-owned and closed after destination resolution, so
late workers cannot append misleading post-decision lookup results. Observer or
writer failures retain the existing optional-failure isolation and report only
fixed category 19 through the existing EventSource. Without `--debug`, no trace
sink is registered. Opt-out, control/policy suppression and unavailable
collection activation can prevent destination resolution altogether; absent
trace lines do not establish that a lookup succeeded or even ran. See the
[manual capture procedure](ManualTesting.md#telemetry-geography-diagnostic-capture)
for a later, explicitly targeted live check.

The transport is the shared PPUX batched telemetry SDK. It does not expose a
caller HTTP-handler/TLS seam here; do not infer TLS compliance or an exemption
from the absence of a CLI-supplied handler.

## Counting contract

Use envelope occurrence time (`['time']` in Kusto) and a half-open UTC window:
`startUtc <= occurrenceUtc < endUtc`.

| Measure | Meaning |
|---|---|
| Command attempts | `CommandAttempt` rows: accepted leaf attempts, including retries, not logical operations or runtime use. |
| Successful outcomes | `CommandOutcome` rows whose dimensions have `outcomeCategory == "success"`. Missing outcomes prove neither success nor failure. |
| Host cases | Count `HostCase` separately for help, version, parser rejection, and group usage. |
| Known tenants | Attempts with a nonempty `tenantId`. Distinct tenants are not people, devices, installations, or eligible adopters. |
| Known accounts | Attempts with both `tenantId` and `principalObjectId`; distinct paired values, never object IDs alone. |

Delivery is best effort, not exactly-once, loss-free, or deduplicated. There is
no installed-base denominator for an adoption rate.

The regional destinations are US `powerappsclient.eastus2.kusto.windows.net` and
EU `powerappsclientneu.northeurope.kusto.windows.net`, database `PowerApps-1DS`,
table `event`, with `data_clientType == "AgentHostingCLI"`. This illustrative,
unexecuted query uses `data_customDimensions`, not dedicated subject columns:

```kusto
let endUtc = now();
let startUtc = endUtc - 1d;
let ah =
    union
        cluster('powerappsclient.eastus2.kusto.windows.net').database('PowerApps-1DS').event,
        cluster('powerappsclientneu.northeurope.kusto.windows.net').database('PowerApps-1DS').event
    | where data_clientType == "AgentHostingCLI"
    | where ['time'] >= startUtc and ['time'] < endUtc
    | extend dimensions = parse_json(tostring(data_customDimensions))
    | extend
        eventName = tostring(data_event_Name),
        outcomeCategory = tostring(dimensions.outcomeCategory),
        observedTenantId = tostring(dimensions.tenantId),
        principalObjectId = tostring(dimensions.principalObjectId);
ah
| summarize
    commandAttempts = countif(eventName == "CommandAttempt"),
    successfulOutcomes = countif(
        eventName == "CommandOutcome" and outcomeCategory == "success"),
    hostCases = countif(eventName == "HostCase");
ah
| where eventName == "CommandAttempt"
| summarize
    knownTenantAttempts = countif(isnotempty(observedTenantId)),
    unknownTenantAttempts = countif(isempty(observedTenantId)),
    distinctObservedTenants =
        dcountif(observedTenantId, isnotempty(observedTenantId)),
    knownAccountAttempts = countif(
        isnotempty(observedTenantId) and isnotempty(principalObjectId)),
    unknownAccountAttempts = countif(
        isempty(observedTenantId) or isempty(principalObjectId)),
    distinctObservedTenantLocalAccounts = dcountif(
        strcat(observedTenantId, "/", principalObjectId),
        isnotempty(observedTenantId) and isnotempty(principalObjectId));
```

Union filtered regional attempt rows before computing distinct paired accounts.
Do not sum distinct counts across regions, days, commands, or tenant-only slices.
Rows without principal dimensions remain account-unknown. Confirm dimension
delivery before relying on account counts. Allow ingestion delay:
`ext_ingest_time` is collector time and `ingestion_time()` is Kusto ingestion
time. An initially empty query does not prove that sending failed.
