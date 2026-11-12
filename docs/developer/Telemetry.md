# CLI telemetry details

This document describes command telemetry implementation, privacy boundaries,
routing, and analysis. Follow [CONTRIBUTING.md](../../CONTRIBUTING.md) for
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

Every event includes a native JSON boolean `IsFailure` inside the JSON-encoded
`data.customDimensions`, not directly on the event data. It is true for usage,
not-signed-in, failure, timeout, parser rejection, and group usage. It is false
for success, caller cancellation, help, version, and command attempts. For an
attempt, false means no failure has been reported, not that execution completed.
Failure events can include `errorCode` as the string form of the numeric CLI
error code in the same dimensions.

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
hardware IDs, persistent local-instance IDs, raw JWTs, arguments, paths, URLs,
free-form errors, results, or account-cache data. There is no alternate identity
mode or identity-selection environment variable.

The shared OneDS shape promotes `operationName` to `operation_Name` and promotes
`telemetryLocation`, retaining both in JSON-string `customDimensions`.
`tenantId`, `cliVersion`, `cloud`, `station`, `isOnePlayer`, and
`webPlayerScenario` remain dimensions. A subject-bearing `CommandAttempt` adds
`principalObjectId` alongside `tenantId` and retains the standard PPUX
`data.tenantId` and `data.principalObjectId` fields as the same pair. Outcomes and
`HostCase` records have no principal dimension or direct subject fields; tenant
dimensions remain. No `objectId`, `pptoolsDeviceId`, identity mode, or payload
schema-version field is emitted.

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
composition. `auth login --cloud` and `auth switch --cloud` select that ring;
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
alone is not a target. At disposal, the host derives a trusted environment-scoped
Power Platform API host from the environment ID and selected cloud/ring, then
requests `/gateway/cluster?api-version=1`. Only `geoLongName` is used. Inventory
geography is neither used nor persisted.

That lookup can acquire or refresh a token only for the exact selected CLI
account, through a non-broker silent MSAL client. It cannot choose an OS account,
another cached account, or an interactive fallback. Missing account, token,
request, timeout, and response errors retain the conservative EU route.
Metadata-only authentication never enriches telemetry identity.

At most four distinct environment/tenant pairs are retained. All must resolve
to known non-EU production geographies to use the global/US collector. Any EU,
unknown, malformed, conflicting, or excess context routes the whole batch to
EU; a batch never splits or changes route after sending begins.
Environment-independent and tenant-wide enumeration commands use EU without
metadata authentication. Nonproduction uses global/US without geography lookup.
Opt-out, control commands, and policy-suppressed invocations do no metadata work.

## Delivery and diagnostics

A shared one-second deadline bounds destination resolution, serialization, and
transport, separately from the policy-read budget. Production metadata resolution
has a 400-millisecond sub-deadline to leave time for fallback transport. Deadlines
cannot terminate already-started noncooperative dependencies, but late results
cannot mutate the route, start sending, or cross invocations. There is no retry.
Telemetry failures cannot change command output, cancellation, or exit behavior.

Optional failures are silent by default. Local EventPipe/ETW diagnostics can
enable `Microsoft-AgentHosting-Cli-OptionalMeasurement`. Its
`OptionalMeasurementFailure` event contains only a fixed integer category, never
an exception, message, stack, path, URL, routing value, token, identity, claim, or
argument. It writes no console/JSON output, persists no log, and emits no
telemetry about telemetry.

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
