# Real-time API debugging

## Current status and next steps

Completed through Sync diagnostic presentation, cleanup and final offline validation.

- The temporary Sync success-message preview and DI-wiring check were removed
  at user request after inspection. Earlier evidence remains in the session.
- Sync reports now receive UTC receipt timestamps and `[SYNC]` in debug mode.
  Only timestamps use ANSI gray, with the same host color policy as HTTP/MSAL.
  The known SDK messages are plain text, so no timestamp detection/parsing is needed.
  Message content and normal-mode failure output are unchanged.
  This is the Sync reporting boundary, not a global exception/output interceptor.
- Connection diagnostics were initially accepted unchanged. After live inspection,
  the user requested replacing duplicate `[connections]` summaries with the shared
  HTTP trace, preserving all information before removing the duplicate output.
  The CLI now supplies only the traced transport, not the legacy summary writer.
  Follow-up cleanup removed the client's unused optional writer parameter/field,
  Trace/Describe methods and summary-only catch block. All callers now rely on
  their injected shared HTTP tracing instead of a separate client logger.
- Status, full URL, client/service IDs and bounded textual response data were
  already present in the HTTP sections. The shared tracer now includes failure
  category, exception type/message and the connection caller's 1 MiB response-size
  rejection/discard detail. Original exceptions, normal results, auth header masks,
  generic HTTP body policy, 64 KiB capture and consent polling remain unchanged.
  No stack traces or exception objects are sent to the sink.
- Shared HTTP tracing, deployment-only dump removal, HTTP presentation, MSAL
  presentation/filtering and final-result ordering are implemented and installed.
- HTTP tracing covers repository-issued requests only. External-library HTTP
  traffic (MSAL, Copilot Studio Sync, PowerApps SDK and Copilot client SDK),
  NuGet update checks and telemetry ingestion are intentionally untraced.
- MSAL uses Warning, Error and Always only; Info and Verbose are excluded.
  PII logging and default platform logging remain disabled.
- HTTP headings use `timestamp - correlationId > Request: ...`,
  `timestamp - correlationId < Response ...` and an undirected `Failed` heading.
  No UTC legend or formatter-added blank separators are emitted.
- MSAL uses the SDK's UTC timestamp formatted `yyyy-MM-dd HH:mm:ss.fff`
  (whole-second SDK precision displays `.000`) and original SDK correlation ID:
  `timestamp - correlationId [MSAL] message`. Context and callbacks without IDs
  use `timestamp [MSAL]`. Raw pre-envelope context, including `False MSAL ...`,
  appears once per unchanged context without a derived PII label. Unknown
  envelopes retain their outer-trimmed text without an added tag or metadata.
  Only timestamp/correlation metadata is gray; messages have no added severity
  labels, operation boundaries or message colors. Interior body whitespace is
  preserved; outer body whitespace is trimmed.
- Final non-streaming human/JSON results follow diagnostics and invocation
  teardown in debug mode. Prompts and live streaming remain immediate; normal
  mode, result content, output destinations and exit codes remain unchanged.
- HTTP response body and completion/failure are emitted together. The five stages
  are Request, RequestBody, ResponseHeaders, ResponseBody and Failed. The sink
  retains response headers, not body snapshots awaiting another completion event.
  Unobserved-body markers, incomplete bodies and failure categories remain explicit.
  File boundaries, bounded capture and stream-observation safeguards remain.

No remaining implementation or offline-validation steps in this diagnostics scope.
The user approved the Sync presentation and continued to final validation.
Do not rerun live operations without appropriate authorization.

Cleanup validation (2026-11-25 UTC, working tree based on `55341f3`): consolidated
repeated redirect-safe transport creation and diagnostic color resolution.
Deferred result buffers now have explicit types and skip empty destinations,
fixing broken diagnostic stderr blocking otherwise valid stdout/JSON results.
All three new regression cases failed before the fix and passed afterward.
The complete main offline suite passed all 8,332 tests, and the isolated Core
embedding suite passed all 8 tests. Formatting/whitespace and installed offline
human/JSON parity checks passed. All eight Release packages were rebuilt, the
global CLI reinstalled and implementation DLL hashes verified before validation.
No live operation was run. Evidence: session `files\debug-cleanup`.

Earlier validation (2026-11-25 UTC, working tree based on `55341f3`): the complete
main offline suite passed all 8,328 tests, and the isolated Core embedding suite
passed all 8 tests, with no failures or skips. The main Debug test assembly was
built during Sync color validation; the embedding project was rebuilt for this
run. No live Sync, deployment, provisioning or evaluation operation was run.
The current global installation remains the hash-verified Sync color build,
whose installed offline human/JSON parity and formatting checks passed.
Evidence: session test execution and `files\debug-sync-color`.

Earlier HTTP validation: all eight Release packages built; the global tool was
reinstalled and CLI/Auth/Core/CopilotStudio hashes verified. All 680 selected
offline regression/integration tests and 8 isolated Core embedding tests passed,
including exact HTTP output, colors, body whitespace, concurrency, failures and
cancellation. Changed-file formatting and whitespace checks passed. Installed
synthetic-profile checks retained identical normal/debug human (56-byte) and
JSON (302-byte) stdout, no redirected ANSI and no sign-in or HTTP.
Evidence: session `files\debug-http-terminal-events`.

Sync formatting validation: timestamp detection and its tests were removed at
user request. All 81 selected offline tests passed, including exact receipt
timestamps, multiline preservation and unchanged normal-mode failure output.
Formatting/whitespace checks passed. All eight Release packages built; the
global tool was reinstalled and CLI/Auth/Core/CopilotStudio hashes verified before
testing. Installed human/JSON parity passed without sign-in or HTTP.
Evidence: session `files\debug-sync-simple-format`.

Sync timestamp color validation: all 83 selected offline tests passed, including
gray-only timestamp output and unchanged normal-mode failures with color enabled.
Host wiring reuses the HTTP/MSAL color decision, including stderr redirection.
Formatting/whitespace, eight Release packages, global reinstall/hash verification
and installed offline human/JSON parity checks passed. No live Sync operation ran.
Evidence: session `files\debug-sync-color`.

Connection deduplication validation: 1,086 selected offline tests and 8 Core
embedding tests passed. Focused cases verify one HTTP request/response without a
summary, service/client headers, response text beyond the old 400-character excerpt,
exception type/message with unchanged exception propagation, and known/unknown
length oversized bodies without consuming their remaining data. Existing standalone
redaction and consent/polling tests still pass. Formatting/whitespace checks,
all eight Release packages, global reinstall/hash verification and installed
synthetic-profile human/JSON parity checks passed. No live creation or consent
was run for this change. Evidence: session `files\debug-connection-single-trace`.

The current status above supersedes older design decisions and historical
validation snapshots below, particularly SDK HTTP interception, MSAL receipt-time
timestamps/operation grouping, Info logging and the original HTTP heading layout.

## Goal

Make the global `--debug` / `-dbg` option display HTTP API requests and responses
as they happen for all `msagent` CLI features, not just deployment. For the same
ARM request, show equivalent HTTP diagnostic information to `az bot --debug`.
Replace the deployment-only ARM response dump without changing normal command
output or transport behavior.

## CLI-wide scope

- Cover authentication, environments, agent management, Copilot Studio sync,
  sharing, connections, evaluations and live tests, as well as every deployment,
  channel and OAuth operation that makes HTTP calls.
- Exclude NuGet update checks and telemetry ingestion from HTTP debug output.
  Both continue running under their existing rules, with unchanged notifications,
  preferences, opt-outs and time budgets. This keeps command diagnostics focused.
- Trace every command-related repository-owned HTTP client, including explicit
  repository requests that support authentication and command execution.
  No command feature is excluded merely because it is not ARM or deployment-related.
- Account for SDK calls, automatic redirects, authentication brokers,
  browsers and child processes in the coverage inventory. Explicitly identify excluded traffic
  rather than claiming complete capture from one outer handler.
- Displaying an API call does not mean dumping every payload: retain the
  authentication protections, streaming/binary markers and capture limits below.

This clarifies the original project-wide design rather than introducing a new
tracing architecture. The shared handler, sink and policy from steps 1 and 2
remain applicable. Complexity lies in step 3's transport inventory, SDK isolation,
lifetimes and retry placement, and in proving coverage across commands. Literal
capture of traffic inside opaque SDKs or external processes could require
additional work beyond HTTP-handler integration.

## Azure CLI baseline

Use Azure CLI's
[SafeNetworkTraceLoggingPolicy](https://github.com/Azure/azure-cli/blob/dev/src/azure-cli-core/azure/cli/core/sdk/policies.py)
as the network logging contract, not a general-purpose secret-free serializer.

| Section | Azure CLI behavior to match for ARM |
|---|---|
| Request URL | Full URL, including query parameters and their original encoding |
| Request method | Actual HTTP method |
| Request headers | All headers; mask `Authorization` and `x-ms-authorization-auxiliary` as `*****`, case-insensitively |
| Request body | Normal textual payload, or `This request has no body`; streaming uploads use `File upload` |
| Response status | Actual HTTP status, including failures |
| Response headers | All response headers |
| Response content | Normal textual content, including service error details |
| Non-text responses | File attachment, binary, image or stream marker instead of reading and dumping content |

The initial format uses the section labels `Request URL:`, `Request method:`,
`Request headers:`, `Request body:`, `Response status:`, `Response headers:`
and `Response content:`. Match the diagnostic information, not Python string
representations, logger prefixes, authentication chatter or byte-for-byte output.
The readability follow-up below uses shorter labels inside explicit HTTP
stages, without removing the corresponding information or changing logging policy.

Debug remains opt-in, local stderr output, separate from normal results and
saved configuration. Do not emit sensitivity warnings in console output or help;
retain the Azure-compatible header masks and authentication-specific safeguards.

## Current behavior

- Shared real-time tracing covers repository-owned HTTP clients
  across the CLI, including intermediate requests and failed responses.
- The renderer no longer prints a final ARM response, and deployment results no
  longer retain raw ARM response payloads.
- Canonical result projections, authorization actions and secret-free
  configuration persistence remain unchanged.
- Useful authentication and sync messages remain alongside HTTP diagnostics.
- Background update-check and telemetry-ingestion HTTP calls are intentionally
  not traced; existing command diagnostics and update notifications are unchanged.

## Output contract

- Write diagnostics to stderr only; preserve stdout, JSON envelopes and exit codes.
- Emit the request before sending. Group finite responses at body consumption
  completion; emit unobserved-body headers and markers promptly.
- Show ordinary text bodies during the API call, not after the command finishes.
- Keep the actual request URL copyable: do not rebuild or re-encode it, replace
  query values, reorder parameters, or remove filters and pagination tokens.
- Include a local request ID, timestamp and duration without obscuring the
  Azure-compatible sections. Distinguish time to headers from body completion.
- Record transport failures, timeouts and cancellation when no response exists.
- Correlate concurrent requests and serialize complete diagnostic events.
- Disable the spinner and flush events promptly.
- Do not create trace files or export diagnostic bodies to telemetry.
  Users can redirect stderr explicitly.
- Completed readability follow-up: preserve diagnostics at the approved MSAL
  Warning/Error/Always threshold while reducing repeated presentation metadata.
  In debug mode, render the final
  human or JSON result after debug-producing cleanup; keep its content, stdout/
  stderr destination and exit code unchanged. This ordering change is intentional
  and is implemented alongside the tracing improvements.

## Implementation

### 1. Shared tracing

Add a reusable `DelegatingHandler` and small diagnostic sink in Core, independent
of ARM. Enable them per invocation through the existing debug option. Use a common
composition helper rather than logging separately in each API method.

Resolve debug once at the host and share the sink across all enabled transports,
including clients created outside normal service registration. Do not add
deployment-specific tracing gates or depend on deployment result types.

Keep the disabled path free of body capture and diagnostic serialization.

### 2. Azure-compatible logging policy

For ARM, apply the baseline above. Do not mask unfamiliar query parameters,
headers or body fields. Do not require per-service JSON field allowlists or omit
a body because it has no service policy. Do not route debug bodies through the
normal Bot Service result redactor.

Preserve textual request and response content, including plain-text errors and
malformed JSON. Logging must not depend on parsing the payload as JSON or form
data. Header masking must affect only the diagnostic copy, never the request.

Keep any extra protection narrowly scoped and explicitly documented rather than
silently changing the ARM baseline. Authentication/token endpoints outside ARM
need separate credential handling: do not log token responses, passwords, client
assertions or refresh tokens. Additional service-specific credential-header
masks may extend Azure CLI's header list without requiring body-field allowlists.

The initial `HttpDiagnosticRedactor` and `HttpDiagnosticBodyPolicy` implementation
has been replaced by `HttpDiagnosticLoggingPolicy`. URL rewriting, visible-field
allowlists and their omission messages are removed. The event/sink contract now
explicitly acknowledges sensitive diagnostic data. The tracing handler emits the
request URI's full absolute URL without diagnostic rewriting; focused tests
verify encoded values, duplicate parameters and filters.

### 3. Transport coverage

| Integration point | Calls |
|---|---|
| Host and Auth registrations | ARM, Graph, environments, AMS and package publication |
| Dataverse HTTP client accessor | Repository Dataverse requests; Sync receives a separate untraced accessor |
| Copilot Studio handler factories | Sharing, evaluations, Power Apps connections and catalogue |
| Live-test session | Repository endpoint discovery traced; Copilot SDK conversation requests untraced |
| MSAL | SDK-owned HTTP transport untraced; textual Warning/Error/Always callbacks retained |
| Update-check client | Intentionally untraced; normal NuGet checks and notifications remain enabled under existing rules |
| Telemetry ingestion | Intentionally untraced; SDK-managed sending, preferences and budgets remain unchanged |

Treat this table as an initial inventory, not an exhaustive list. Before wiring,
inventory all HTTP client construction, SDK factories and background services in
production code. Record how each reaches the shared sink, its policy and retry
position, or the specific boundary that prevents observation.

Place tracing inside retry handlers to show every attempt, including polling and
pagination. Preserve existing authentication, redirect, TLS, timeout, disposal
and connection-pooling behavior.

Audit SDK-created clients and automatic redirects explicitly. A handler outside
`HttpClientHandler` does not observe every internally followed redirect. Use
supported transport hooks where available and document remaining boundaries,
including broker/browser authentication and child-process traffic.

### 4. Body and streaming handling

- Bound diagnostic capture without truncating the actual request or response.
  A capture limit is a documented difference from Azure CLI; show when it is
  reached instead of presenting the capture as a complete body.
- Do not pre-read uploads or buffer entire responses solely for debugging.
- Preserve content headers, stream ownership, cancellation and backpressure.
- Match Azure CLI's streaming behavior: show headers and a stream marker for SSE
  and other streaming responses. Do not consume the stream to populate the log.
- Use file-upload, attachment, binary and image markers where appropriate.
- Label omitted, truncated, incomplete and partially consumed bodies.
- Do not emit `no service policy`, per-field approval or invalid-JSON placeholders
  for ordinary ARM text bodies.
- Report diagnostic failures without masking the original API failure or dumping
  credential-bearing authentication data.

### 5. Remove deployment-only plumbing

After transport coverage is in place, remove the final `ARM response:` dump and
debug-only `ArmResponse` / `RawArmResponse` propagation.

Retain parsing and fields used by normal operations, existing response redaction
where still required, secret-free configuration persistence, authorization
actions and normal output. Keep useful authentication and sync messages without
duplicating HTTP traces.

## Validation

- Compare equivalent ARM calls with Azure CLI as described below.
- URLs remain copyable and unchanged, including encoded values, duplicate query
  parameters, filters, API versions and pagination.
- Only the configured request-header masks replace header values; ordinary
  request and response headers remain visible.
- Ordinary JSON, form and plain-text bodies are visible without field approval.
- Service error codes, messages and malformed-JSON error bodies remain visible.
- Request output precedes completion of a blocked response.
- Successes, HTTP failures, transport failures, cancellation and timeouts appear.
- Retry attempts, polling, pagination and concurrent calls are correlated.
- Authentication-specific credentials remain protected outside the ARM baseline.
- Uploads, attachments, binary/image content, streams and capture limits have
  explicit markers without consuming or changing application data.
- Responses and uploads remain readable and unchanged.
- Streaming starts promptly without full-body buffering.
- Debug-disabled commands retain existing behavior.
- `--debug --json` preserves the normal single stdout JSON document.
- Existing transport policies, pooling and offline guarantees remain intact.
- Representative command-related clients from every integration point reach
  the tracing layer; update-check and telemetry transports remain unwrapped.
- Coverage is verified across command groups, not only deployment tests.
  Include supported command SDK transports in the integration matrix; exercise
  them with offline fake transports. Verify that debug does not disable background
  calls or add HTTP diagnostics for update checks and telemetry ingestion.
- Every command-related owned/injectable transport is traced when debug is enabled
  and unchanged when disabled. Document remaining opaque traffic separately;
  an ARM comparison alone is not evidence of CLI-wide coverage.

### Same-call ARM comparison

1. Record the Azure CLI version and inspect `az bot show --debug` for a test bot.
2. Compare the corresponding CLI request using the same resource, HTTP method
   and `api-version`. If `az bot` pins a different version, use
   `az rest --debug` for the exact URL and note its logging-path differences;
   retain `az bot` as the section and header-masking reference.
3. Check that the URI, method, relevant headers, status and textual payload
   information are equivalent. Allow client-specific headers, credentials,
   timestamps, request IDs, response changes and harmless formatting differences.
4. Repeat with a missing resource to verify that the full ARM error response
   appears before the CLI's normal error summary.
5. Test request bodies with fake transports first. Use an owned temporary
   resource with verified cleanup if a live mutation is needed.

Do not use production credentials or resources merely to collect example logs.
Keep sensitive live traces out of source control and telemetry.

Run focused tests first, then the solution suite and formatting checks. Repack
and reinstall the CLI after runtime changes.

## Delivery order

1. Define the diagnostic contract, Azure-compatible logging policy and focused
   tests. Completed and accepted for progression to step 2.
2. Implement shared tracing and non-disruptive body observation. Completed and
   accepted for progression to step 3. Added `HttpDiagnosticHandler`,
   `TextWriterHttpDiagnosticSink` and bounded content observation. All 77 focused
   tracing, policy, transport and existing Bot Service redaction tests pass,
   including 27 new tracing cases. Formatting verification and all eight Release
   packages pass; global CLI reinstallation, assembly-hash verification and
   version/help smoke tests pass. Live ARM parity remains unverified.
3. Integrate command-related owned transports and supported SDK hooks across the
   entire CLI; verify a complete transport inventory.
   Completed and accepted for progression to step 4. The full offline suite passes:
   8,254 CLI tests and 8 Core embedding tests, with no failures or skips.
   Formatting verification and all eight Release packages pass. The global CLI
   is reinstalled; all four implementation assembly hashes match the final
   publish. Installed help/version, both debug spellings, identical normal JSON
   stdout and telemetry opt-out smoke checks pass.
4. Remove deployment-only debug response plumbing. Completed and accepted for
   progression to step 5. Removed the final dump,
   result/model raw-response properties, list aggregation and renderer debug
   argument. Sanitization preserves the adapter's authorization link as-is;
   store projections clear transient links at persistence boundaries without
   an additional retention flag.
   All 2,420 focused regression tests and the full offline suite (8,254 CLI tests
   plus 8 Core embedding tests) pass, with no failures or skips. All eight
   Release packages pass. Reinstalled the global CLI and verified all four
   implementation assembly hashes, installed help/version, both debug spellings,
   identical JSON stdout and isolated telemetry opt-out behavior.
   Formatting passes for the other 24 changed C# files; the unchanged baseline
   formatting violations in `DeploymentOAuthAdapter.cs` line 129 and
   `DeploymentServiceTests.cs` lines 109-126 remain untouched. No new formatting
   violations were reported in either file; whitespace diff checks pass.
   Follow-up simplification removed the authorization-link retention flag:
   all 794 channel/persistence/serialization tests and changed-file formatting
   pass. Rebuilt Release packages, reinstalled the global CLI and repeated
   assembly-hash and installed smoke checks successfully.
5. Complete validation and document any coverage boundaries. Completed; awaiting
   user validation. All 66 live ARM parity/output checks pass against Azure CLI
   2.93.0, using the installed CLI, read-only calls and an isolated local project.
   No runtime changes were needed; existing build/test/install results above
   remain applicable. See the live comparison and visibility boundaries below.
6. Improve HTTP presentation. Completed, refined and globally installed;
   current formatting and latest validation are recorded at the top of this plan.
7. Improve non-HTTP diagnostic presentation and put the final human/JSON result
   after all debug information. MSAL presentation/filtering and result-last
   ordering are complete. Duplicate CLI connection summaries have been replaced
   by shared HTTP tracing with exception and response-limit detail parity.
   Sync diagnostics/progress are next, with separate approval and validation.

Stop after each delivery step for user validation before starting the next.

### Follow-up: command-focused HTTP diagnostics

At user request, NuGet update checks and telemetry ingestion are now excluded
from HTTP debug output. Removed only their tracing integration: update checks
keep their existing transport settings and notifications, and telemetry uses
its original SDK-managed send path in both normal and debug mode. Command-related
API calls and authentication tracing are unchanged; no additional flag was added.

All 551 focused tracing, telemetry, update-check and channel-command tests pass.
The obsolete telemetry-tracing test was removed with that integration, and the
update-client regression now verifies an unwrapped transport with unchanged
TLS, redirect, cookie, decompression and timeout settings. Changed-file formatting
and whitespace checks pass. All eight Release packages were rebuilt and the
global CLI reinstalled; all four implementation DLL hashes match the final publish.

Installed checks confirm both debug spellings preserve the requested command's
original output and leave its MCSAgent/CodeAgent error as the final line, with
no background HTTP trace blocks. A live read-only channel listing still displays
ARM HTTP calls with masked Authorization and identical normal/debug JSON stdout.
These checks did not set update-check or telemetry opt-out variables; existing
preferences and gates remained in effect. Standard isolated version/help,
JSON-stdout and telemetry opt-out smoke checks also pass.

### Follow-up: readable diagnostics and result-last output

The user accepted the focused `agent list` comparison: after normalizing volatile
MSAL timestamps, correlation IDs and timing values, the only differences were
added HTTP diagnostics. The next steps intentionally improve presentation rather
than preserve that format byte-for-byte.

The comparison also confirmed that human and JSON results are not missing.
They are byte-identical to main, but the human result precedes approximately
90 additional debug lines. A telemetry-routing `GET /gateway/cluster` still
uses an instrumented client and runs after result rendering. Telemetry ingestion
itself remains untraced. Do not silently hide that lookup or other existing
messages as part of this readability work.

#### A. HTTP readability first - original approved design (historical)

The current heading layout, absence of a UTC legend/blank separators and merged
terminal events supersede the corresponding original presentation details below.

- Group URL, method, headers and body under one logical request section; group
  status, headers and content under one logical response section. Preserve the
  information from request-body, response-body, completion and failure events
  as fields/continuations instead of printing six repetitive stage headings,
  except for the per-stage timing fields explicitly removed below.
  Do not remove diagnostic messages, header entries, content or policy markers.
- Use the existing full per-request trace GUID immediately after the timestamp
  on every section/continuation heading. The request, response and any failure
  share that GUID; each new handler invocation generates a new GUID. Do not add
  a sequential HTTP ID, repeat a separate Trace ID field or modify network
  correlation headers. This is a local diagnostic ID, not a server trace ID.
- Use full UTC date/time in `yyyy-MM-dd HH:mm:ss.fff` format on every diagnostic
  entry/continuation heading. Declare the UTC
  convention once; do not omit dates or use a date-only banner. This deliberately
  changes the display precision to milliseconds at user request.
- Retain request and response timestamps for the cross-day timeline. Use
  `Request`, `Response after 1128.0 ms` and `Failed after 30000.0 ms` headings.
  For fully consumed finite bodies, the response timestamp is body completion
  and duration is time from request start through body completion, not time to
  headers. Failure duration is time from request start to failure. Remove the
  separate Completed, Timing and Elapsed lines and per-stage duration breakdowns
  at user request.
- Put method and URL directly in the Request heading and available status in
  the Response/Failed heading (` | Status: 200`). Prefix request lines with `> `
  and response lines with `< `, with no added indentation or header-value
  alignment padding. Print headers directly without a Headers heading. Do not
  insert an empty line between request details and the response/failure heading;
  retain a separator before the next request.
  Keep the full original URL on one copyable line; do not shorten, rebuild,
  wrap deliberately, reorder or re-encode it.
- Do not pretty-print, reserialize, syntax-highlight, flatten or minify bodies.
  Preserve captured content's original whitespace, values, order and duplicate
  properties, adding only the display direction prefix to each line (including
  blank lines). Preserve original CR/LF line endings. Compact service JSON stays compact; existing
  multiline text stays multiline. Add no diagnostic JSON envelope. Preserve
  malformed, truncated and partially consumed text with existing markers.
- Work only on the already policy-filtered, bounded event snapshot. Keep the
  64 KiB capture boundary, authentication protections, header masks, streaming
  markers and non-text handling unchanged. Formatting must not pre-read content,
  wait for a whole command or affect application response consumption.
- Flush request information before sending. For observed finite responses,
  retain response headers and the existing bounded body snapshot until the
  consumption completion/failure callback, then emit the grouped response with
  the correct completion timestamp and duration. This intentionally delays
  finite-response diagnostics until consumption ends, not until command end.
  Do not add unbounded buffering, pre-read a response or change consumption.
  Streaming/binary bodies whose completion is not observed must retain prompt
  headers/omission markers and explicitly label completion as unobserved, never
  misrepresent a headers-only duration as body completion. Partially consumed
  bodies retain the incomplete marker; do not claim full body completion.
  Keep emitted events atomic; use dated trace-GUID continuation headings when
  needed for concurrency. Never pre-read an upload to populate its section.
- Keep failure categories explicit, including failures before response headers
  and failures while reading a body. HTTP error responses retain their actual
  Status, Headers and Content, including the service's error body, under a
  Response heading. A transport/timeout/cancellation failure uses Failed and
  includes only available information: Error (the available failure category)
  and Status only when an HTTP status was actually received. Omit unavailable
  fields rather than displaying placeholders. Retain previously received status for
  body-read failures. The current snapshot carries a category, not an exception
  message; do not fabricate details or expose unfiltered exception text.
  The specific per-request `HTTP failure: Transport` diagnostic is introduced
  by this branch's HTTP tracer, not present on origin/main; its formatting stays
  in phase A. Existing command-level error messages remain phase B work.
- Use explicit Request, Response and Failed labels with the user-approved
  gh-style direction prefixes above. Failures have no direction prefix, since
  an HTTP response may not exist. Keep the existing stderr-only debug convention.
- Add optional ANSI color through the existing host color decision, respecting
  `--no-color` and `NO_COLOR`; never color a redirected diagnostic stderr stream.
  Apply gray to the timestamp and trace GUID on each first line, independently
  from the message color:
  blue requests, green 2xx responses, yellow 3xx responses,
  red 4xx/5xx responses and transport failures; other/unknown statuses blue.
  Request URLs use gh's request-line yellow to contrast with blue request text.
  All subsequent lines, including header names/values, body labels/content and
  error details, remain plain, as do direction markers. Do not use bold or JSON
  syntax highlighting.
  Do not add a new flag or change global result-color behavior.

Illustrative successful request across midnight (UTC, not a service capture).
The response section is emitted at body completion:

```text
HTTP timestamps: UTC

> 2026-11-19 23:59:59.737 [55097603740341f490ac4858f764f9f3] Request: GET https://example.crm.dynamics.com/api/data/v9.2/bots?$select=name&$top=1
> Accept: application/json
> Authorization: *****
> Body: This request has no body
< 2026-11-20 00:00:00.866 [55097603740341f490ac4858f764f9f3] Response after 1129.0 ms | Status: 200
< Content-Type: application/json
< Content: {"value":[{"name":"Example agent"}]}
```

An HTTP error still has a response and displays its service error body:

```text
< 2026-11-20 00:00:02.101 [88f2816ee4d341c38e095fb394198727] Response after 365.0 ms | Status: 403
< Content-Type: application/json
< Content: {"error":{"code":"Forbidden","message":"Access denied."}}
```

Illustrative failure without a response:

```text
2026-11-20 00:00:30.000 [fbb9f4d872fc49a29a4e902edbc275fe] Failed after 30000.0 ms
Error: Timeout
```

Presentation reference:
[GitHub CLI stderr HTTP debugging](https://cli.github.com/manual/gh_help_environment).
Keep the Azure CLI logging policy above for payload/header visibility.

Implement presentation in the shared HTTP sink, adding only the event metadata
strictly needed for the format. Do not change each client or adapter. Test the
format with deterministic events and clocks: normal JSON/plain text, no-body,
empty headers, authentication/stream markers, incomplete or malformed JSON,
capture limits, concurrent GUIDs/continuations, cross-midnight timestamps,
body-completion heading timestamps/durations, absence of separate
completed/timing/elapsed fields, completion-unobserved streaming markers, color
enabled/disabled, stderr redirection and failures at
different stages.
Verify that formatting neither loses information nor changes transports,
disabled-debug behavior, stdout or exit codes. Repack/reinstall after runtime
changes, then validate the installed HTTP output before starting phase B.

Initial grouped-format validation completed (before the compact gh-style update):
- All 135 HTTP/policy/integration, affected adapter/session and color-policy
  tests pass; all 8 isolated Core embedding tests pass.
- Changed-file whitespace formatting and git whitespace checks pass.
- Built all eight Release packages and updated the global tool to
  `0.2.87-beta+55341f3d76`. Installed CLI/Core/Auth/CopilotStudio assembly hashes
  match the win-x64 Release publish output.
- Installed read-only agent listing exits 0 in human/JSON normal/debug modes.
  Normal/debug stdout is byte-identical (825 human bytes; 1,987 JSON bytes).
  Full dates, shared per-request GUIDs, body-completion headings, auth omission
  markers and Authorization masks are present; legacy HTTP headings and separate
  Completed/Timing/Elapsed lines are absent. Redirected stderr contains no ANSI,
  including with explicit `--no-color`; excluded background traces remain absent.
- Captures retain inherited preferences; stream redirection naturally gates the
  update check. HTTP color-on/off is covered by deterministic sink tests.
  Live captures and local packages are in the session's
  `files\debug-http-presentation` directory, not committed to the repository.

Compact gh-style update validated:
- All 138 focused HTTP/policy/integration, affected adapter/session and color-policy
  tests pass. Exact-output tests cover the compact headings, one-space direction
  prefixes, multiline/empty body preservation and gh-style ANSI header/status
  colors, including bold red 5xx. Body content remains unstyled.
- Changed-file whitespace formatting and git whitespace checks pass.
- Rebuilt all eight Release packages and reinstalled the same global tool version,
  `0.2.87-beta+55341f3d76`, using an isolated local package source and fresh cache.
  Installed CLI/Core/Auth/CopilotStudio hashes match the rebuilt Release publish.
- Installed read-only human/JSON agent listings exit 0 with and without debug.
  Stdout remains byte-identical (825 human bytes; 1,987 JSON bytes), including
  explicit `--no-color`. Debug captures have correlated compact request/response
  headings, masked Authorization, no legacy HTTP headings or excluded background
  traces, and no ANSI on redirected stderr. Color-enabled output is covered by
  deterministic sink tests.
- New packages and live captures are in `files\debug-http-compact`; earlier
  artifacts remain intact. General diagnostic formatting and final-result ordering
  remain phase B work.

Heading-only color and spacing refinement validated:
- All 138 focused tests and whitespace checks pass. Only request/response/failure
  headings receive a single color; all detail lines remain plain. Exact-output
  and body roundtrip tests cover removal of the request/response blank separator
  without altering body whitespace or line endings.
- Rebuilt all eight Release packages, reinstalled `0.2.87-beta+55341f3d76` from an
  isolated local source/fresh cache, and verified installed assembly hashes.
  Installed human/JSON normal/debug listings exit 0 with byte-identical stdout
  (825/1,987 bytes), no blank response separator and no ANSI on redirected stderr.
  Color-enabled headings and failure coloring are covered by deterministic tests.
- Latest artifacts are in `files\debug-http-headings`; all earlier evidence remains.

#### B. Other diagnostics and final-result ordering - remaining source work

Work through the non-HTTP sources individually, not as one combined change:
1. MSAL authentication (`MsalPublicClientProvider`): completed. Retain the raw
   pre-envelope context including the boolean, without deriving a PII label.
   Preserve `enablePiiLogging: false` and Warning/Error/Always filtering.
2. Connection diagnostics (`PowerAppsConnectionClient.Trace`): CLI deduplication
   completed after preserving correlation, response and exception/limit details
   in shared HTTP tracing. The unused legacy writer and Trace implementation
   have also been removed.
3. Sync diagnostics/progress (`StandardErrorSyncProgress.Report`): raw SDK
   messages. Preserve existing non-debug failure visibility and live progress.

Approve and validate each source's format before moving to the next: retain all
events/fields, compare message contents, run focused tests, verify normal-mode
behavior, then repack/reinstall and inspect installed output. Keep the approved
HTTP formatter unchanged. Host diagnostic warnings and final-result ordering
are already implemented and must remain unchanged.

- Reformat MSAL/authentication and other existing debug sources with concise,
  source-specific headings, correlation context and indented key/value details.
  Present shared library/runtime/OS metadata once per unchanged context instead
  of repeating it on every line. Retain every original message and its varying
  timestamp, severity, correlation and metric fields.
- For connection/Sync work, remove redundant prefixes, not diagnostic events. Do not filter by severity,
  silently drop repeated messages, merge different attempts, collapse sections
  or add an opt-in switch for information that is currently visible.
- Preserve chronological event order, source attribution and concurrency
  correlation. Keep authentication policy independent from display formatting.
- In debug mode, defer only the finite final success/failure result until
  debug-producing invocation cleanup has finished, including telemetry routing/
  flush, relevant disposal and diagnostic-write warnings. Keep normal commands
  unchanged. Do not capture arbitrary stdout, buffer HTTP bodies to command end,
  or delay prompts, authentication challenges and live conversation activities.
- Keep the exact human result or single JSON envelope, its existing destination
  and its exit code. JSON results must not gain a decorative heading or debug
  text on stdout. Apply result-last ordering to human and JSON errors as well
  as successes, retaining the current human-error stderr destination.
- Define the shutdown/completion boundary before implementation: account for
  late background callbacks under existing timeout, cancellation and telemetry
  budgets. Do not obtain ordering by extending those budgets, suppressing
  diagnostic messages or buffering unbounded output.
- Validate with fake transports and delayed cleanup so the final result is
  provably after the last debug event. Cover success, usage/API errors,
  cancellation, JSON mode and interactive/streaming behavior. Compare message
  contents before/after formatting, and repeat installed main-versus-branch
  comparisons with separate streams plus a merged-stream ordering capture.

HTTP presentation, MSAL formatting/filtering and result-last ordering are
implemented. Connection CLI deduplication is complete; only Sync source
presentation remains to be inspected and agreed.

### Phase B: earlier MSAL formatting validation (historical)

These are retained snapshots of earlier iterations, not the current implementation.
Receipt-time timestamps, labeled PII context, operation grouping/colors and Info
logging described below were superseded by the current status at the top.

- Added one provider-owned synchronized MSAL writer shared across client
  configurations. Full callback-receipt UTC timestamps and `N`-format GUIDs match
  HTTP headings. Missing SDK correlation IDs remain missing.
- Unchanged SDK/platform/PII configuration is shared context. Original SDK
  second-precision timestamps remain visible when they change; event timestamps
  do not pretend the SDK supplied milliseconds. Optional client information,
  multiline whitespace, metrics, duplicate events and unknown message formats
  remain visible.
- No `INFO` tag. Warning/error labels use actual callback levels, with yellow/red
  first-line messages only. Heading timestamps and IDs now use gray; context and
  continuation lines remain plain. `Always`, like `Info`, has no visible level
  label; other supplied levels retain explicit labels. Host-resolved stderr color policy,
  Info threshold, PII-disabled configuration and no-debug silence are preserved.
- 68 focused tests passed, including real offline `GetAccounts` callbacks from
  two client configurations, concurrency, unknown formats, exact colors and
  HTTP/color-policy regressions. Formatting and whitespace checks passed.
- All eight Release packages built. An isolated local tool installation reports
  `0.2.87-beta+55341f3d76`; installed CLI/Auth/Core/CopilotStudio hashes match the
  fresh publish. Following the user's explicit reinstall preference, the global
  tool was also reinstalled from these packages using a fresh NuGet cache, and its
  version and all four implementation hashes were verified.
- Installed `auth status` checks used only an isolated synthetic non-broker
  profile and empty local token cache, with telemetry/update checks disabled.
  Normal/debug human stdout was identical (56 bytes); normal/debug JSON stdout
  was identical (302 bytes). All four commands exited 0, redirected debug stderr
  had no ANSI or raw SDK prefixes, and no sign-in or HTTP requests occurred.
- Evidence is preserved in session `files\debug-msal-format`. Approved HTTP
  rendering, connections, sync and final-result ordering were not changed.
- The same four offline checks passed against the reinstalled global tool;
  their captures use the `global-` prefix. For subsequent runtime changes,
  rebuild/reinstall the global tool and verify that installed build before handoff.
- Metadata refinement: `Always` labels are removed without dropping the SDK
  events. MSAL and HTTP heading timestamps/full GUIDs use ANSI gray (`90`),
  separately reset before the existing message color; HTTP direction markers,
  shared context and detail/continuation lines remain plain. Color opt-outs and
  redirected stderr remain ANSI-free.
- All eight packages were rebuilt and the global tool reinstalled before the
  refinement's verification. 71 focused tests and formatting/whitespace checks
  passed. Installed hashes match fresh publish; all four offline status checks
  passed with identical normal/debug human and JSON stdout, no `Always` label,
  and retained internal-cache events. New evidence is preserved in session
  `files\debug-gray-metadata`.
- GetAccounts/AcquireTokenSilent operation-boundary rendering is implemented
  around all corresponding awaited SDK calls in authentication, normal/selected
  silent tokens and consent's silent-first path. Missing debug output bypasses
  operation tracing; original token/account/tenant selection, cancellation and
  error translation are unchanged.
- Each boundary carries a local call GUID, explicitly distinguished from the
  original SDK correlation IDs on the indented events. SDK messages without
  IDs stay without IDs. Parameters, request data, success details and metrics
  remain inside the actual call; completion occurs only after its task returns.
  Failures/cancellations retain their original exceptions and get explicit
  terminal headings. No banners are used to guess an operation boundary.
- Async-local scopes preserve ownership across awaits. Interleaving emits a
  dated `continued` heading for the correct local operation; late callbacks
  cannot reopen a completed operation. Events are flushed as they arrive, not
  buffered to operation/command completion.
- SDK repetition was verified against MSAL.NET 4.92.0: the legacy ADAL-cache
  eligibility helper logs whenever checked, and region metadata discovery logs
  the non-regional decision whenever evaluated. Our provider registers one
  callback per client, with default platform logging still disabled. Original
  repeated events are preserved, not filtered or merged across calls.
- Boundary output errors must not change authentication: IO/disposed-writer
  failures are recorded through the existing invocation diagnostic-error
  channel, whose final warning now accurately says "debug diagnostics" rather
  than only "HTTP diagnostics." No final-result ordering changes were made.
- Validation: 169 focused offline tests passed, including real seeded-cache
  token acquisition (zero HTTP), two client configurations, original IDs,
  parameter/metric ordering, duplicate retention, interleaving, late callbacks,
  failure/cancellation, unavailable stderr, auth selection/consent and existing
  HTTP/color/architecture regressions. Build has no warnings; whitespace and
  formatting checks passed.
- All eight Release packages were rebuilt and the global tool reinstalled
  before testing. Installed implementation hashes match fresh publish;
  installed human/JSON normal/debug status checks all exited 0 with unchanged
  stdout (56/302 bytes), matched start/end GUIDs and nested SDK callbacks.
  These checks use only the isolated synthetic profile/empty local cache,
  with no sign-in or HTTP. Evidence remains in session
  `files\debug-msal-boundaries`. Connections, sync and HTTP rendering are unchanged.
- Approved milestone-color refinement: MSAL operation start/continued messages
  now use cyan and successful completion messages green. Warning/cancellation
  remains yellow, failure/error red, metadata gray, routine SDK messages and
  detail lines plain. No new elapsed-time fields, blank separators, event
  suppression or correlation-ID changes were included in this color-only step.
- All eight packages were rebuilt and the global tool reinstalled before the
  color verification. 80 focused tests, format and whitespace checks passed.
  Installed hashes match fresh publish and the four offline status checks
  retain exact normal/debug human (56-byte) and JSON (302-byte) stdout parity.
  Evidence is preserved in session `files\debug-msal-operation-colors`.
- Clarified remaining clutter: the CLI's local call boundary and the SDK's own
  GetAccounts-start callback describe the same real call with different ID
  namespaces, not two authentications. Shared SDK/platform/PII context is already
  emitted once per unchanged context. The token SDK separately announces its
  assembly version when creating a request context; its correlation changes,
  not the SDK version or the CLI's fixed PII-disabled setting. Reclassifying
  these SDK announcements as metadata is a future cleanup, not implemented here.

### Step 3 original transport inventory and coverage (historical)

This records the original SDK-inclusive integration. The later repository-only
scope supersedes SDK transport hooks in this table: MSAL uses its own HTTP
transport; Sync gets an untraced Dataverse accessor; PowerApps and Copilot SDK
requests remain untraced. Explicit repository discovery uses a separate traced
client sharing the transport with the SDK client, with single-owner disposal.

The host resolves debug once and shares one synchronized stderr sink through
`HttpDiagnostics`. Disabled wrapping returns the original handler unchanged.
Debug disables the spinner without emitting an introductory warning.
Normal result redaction is unchanged. The legacy final ARM dump was retained
through step 3 and removed in step 4.

| Construction path | Integration and verification |
|---|---|
| Host Power Platform API | Environment, environment-geography, AMS package publication, deployment/read and agent-creation clients wrap their existing redirect-rejecting transports |
| Auth REST registrations | ABS provisioning, deployment, OAuth, channels, Blueprint Graph/ARM and App Registration Graph/ARM wrap their existing transports; enabled/disabled registration tests cover all six clients |
| Dataverse/sync | The accessor factory wraps its pooled transport inside `DataverseRetryHandler`; retry tests observe each attempt and reuse after client disposal |
| Copilot Studio factories | Sharing, evaluations, connection deletion and the Power Apps catalogue SDK use the common wrapper; enabled/disabled tests exercise each registered factory/client |
| Connection consent | The host supplies the same wrapped handler factory to `PowerAppsConnectionClient`; existing consent diagnostics remain until cleanup |
| Live testing and playground discovery | Both host creation paths pass the shared diagnostics into the session, inside `McsResponseBridgeHandler`; enabled/disabled discovery/start/query tests preserve activities, conversation IDs, bodies and authorization |
| MSAL | `WithHttpClientFactory` injects one provider-owned reusable client only when enabled; the authentication policy omits bodies and masks cookie headers; tests verify the actual built MSAL configuration and stop before network |
| Update checks | Intentionally excluded from HTTP tracing; existing `ShouldCheck` gate, cookie/decompression/redirect/TLS settings and infinite timeout are unchanged |
| Telemetry ingestion | Intentionally excluded from HTTP tracing; debug and normal mode both retain SDK `SendAsync`, existing preferences, collection/flush budgets and normal opt-out behavior |

Production HTTP construction and synchronous-send searches found no direct
synchronous sends. Injected SDK paths are Dataverse/sync, Power Apps catalogue,
the live-test Copilot client and MSAL. No global process interception or
unsupported SDK-private transport replacement is used.

Debug-enabled MSAL clients use the shared TLS policy; their normal
SDK-managed transports remain untouched when debug is disabled. MSAL's injected
client is reused until provider disposal, including across cached public-client
configurations. Telemetry retains its SDK-managed sending and existing
opt-out, destination-resolution, payload and bounded-flush rules.

Unavailable stderr errors are retained in the invocation's
`DiagnosticWriteError`. The
host attempts a static incomplete-output warning on exit; if stderr is still
unavailable, no stdout fallback, trace file or exit-code change is introduced.
The existing host/adapter timeout behavior is unchanged; the handler's linked
timeout-versus-caller-cancellation ambiguity below remains explicit.

Broker/browser authentication, playground/tool-installer child-process traffic,
headers added inside the underlying transport, and internally followed
redirects remain outside this HTTP-handler boundary. SSE/binary bodies use
markers, not payload capture. These are visibility boundaries, not claims that
the corresponding external traffic is captured.

### Step 5 live ARM comparison (historical)

These checks predate the final presentation and repository-only tracing scope.
In particular, the MSAL HTTP visibility recorded below is no longer enabled.
They are not a claim of fresh live validation for the latest installed build.

Validated the installed CLI against the existing nonproduction channel-test bot
and a nonexistent bot in the same development resource group. Only ARM GETs
were issued: no cloud resources or channels were created, updated or deleted.
The Azure Bot Service rollout feature was enabled only in child-process
environments. Telemetry was opted out and update checks disabled for these runs.
Project records and captured evidence stayed in the session workspace, outside
the repository; existing project configuration was not used or changed.

Azure CLI was 2.93.0 at final validation (earlier planning used 2.92.0).
`az bot show --debug` uses `2021-05-01-preview`; the CLI uses
`2023-09-15-preview`. Consequently, exact-URL comparisons used `az rest --debug`,
while `az bot` remained the section/header-masking policy reference.

All 66 live assertions pass:

- Exact method, full URL and preview API version match the corresponding
  `az rest` calls. Full response JSON matches for both HTTP 200 and HTTP 404,
  including the ARM `ResourceNotFound` code and message.
- Both `az bot` and the CLI mask request Authorization with `*****`.
  Content type and server request IDs remain visible. Client-specific headers,
  timestamps, IDs and formatting differ as expected. Azure CLI's normal bot
  stdout is an SDK projection, not a byte-for-byte copy of its HTTP body.
- Normal, `--debug` and `-dbg` successful stdout is identical and is one valid
  JSON document. The failed JSON command also produces one valid error document;
  debug details remain on stderr.
- The complete HTTP 404 error body precedes the normal text error summary.
  Success and failure exit codes retain their CLI-specific values, and no
  legacy `ARM response:` dump or incomplete/capture-limit marker appears.
- MSAL HTTP traffic is visible when authentication requires a network call;
  its request and response bodies use the authentication omission marker.

One Azure logging-path difference was observed: the tested `az rest` path
emitted an unmasked bearer header, unlike `az bot`. That value was removed
from the session evidence, and the capture helper now masks it before writing.
This behavior is not copied into the CLI; the intended baseline remains
`az bot`'s masked Authorization header.

The existing missing-deployment behavior removes a stale local record after
the ARM 404. Each command therefore restores its isolated fixture first, so
both JSON and text failures genuinely exercise ARM rather than failing local
selection. This does not mutate any cloud resource.

Live validation covers ARM reads and the authentication calls they triggered,
not every service, live retry, mutation or streaming path. Offline transport,
body, retry and integration tests provide the broader coverage described above.
Broker/browser/child-process traffic, inner-transport headers and redirects,
binary/SSE body omission, the 64 KiB text limit and timeout/cancellation
classification remain the documented visibility boundaries.

### Step 2 boundaries to verify during integration

- The handler traces `SendAsync`. Audit synchronous client sends before claiming
  coverage of every request.
- Trace headers are those visible at the handler boundary, not headers inserted
  later inside the transport, such as `Host`.
- Buffered request text is logged before sending; generated content such as
  `JsonContent` is observed while serialized. Request content ownership is
  restored afterward. Uploads and multipart content are not pre-read.
- Text responses are observed only as consumed. Seeking or early disposal emits
  an incomplete marker rather than a misleading complete payload. Captures over
  64 KiB emit the capture-limit marker without shortening application data.
- Streaming, attachment and binary/image responses retain their original
  content, with an immediate marker. Their later body completion and read errors
  are not observed; elapsed time in their trace is time to headers.
- Cancellation is classified from the supplied or per-read token; an unrequested
  cancellation is classified as a timeout. `HttpClient` can express its overall
  timeout through a linked token, which is not distinguishable from caller
  cancellation at this boundary. Verify host-level timeout reporting in step 3.
- An unavailable diagnostic writer does not change HTTP results. The handler's
  `DiagnosticWriteError` exposes the output failure when the destination itself
  cannot report it. Step 3 aggregates this at invocation scope and attempts an
  incomplete-output warning without changing normal HTTP results.
