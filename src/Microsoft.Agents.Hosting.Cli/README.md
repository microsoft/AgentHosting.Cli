# AgentHost CLI — C# empirical demonstrator

This is the **C# empirical demonstrator** for the AgentHost CLI's authentication,
environment selection, agent lifecycle, Azure Bot Service, and Blueprint identity surfaces.
It exists to produce evidence for a later implementation-direction decision, compared against
the shipped TypeScript CLI. It is **not** a production release and does **not** replace the
TypeScript proposal.

- Product requirements: `agenthost_cli_csharp/PRD.md` (in the plans repo)
- Technical design: `agenthost_cli_csharp/TDD.md`
- Offline evaluation findings: `docs/agenthost-cli-csharp/offline-evaluation.md`
- The inherited command groups remain aligned with the shipped CLI. The fN13j package-upload and
  deploy spellings below are provisional empirical notation, not approved final taxonomy.

> **Documentation rule for agents:** `Tool_Readme.md` is the packaged, **public-facing** user guide.
> It must **not** contain a "Feature controls" section; do not add one or link to it. Feature-control
> documentation belongs only in this file (see [Feature controls](#feature-controls)).
>
> Likewise, `Tool_Readme.md` must document only features that are `on` by default in
> `featureflags.json`. Do not list commands, command groups, examples, or descriptions of features
> that ship `off` (for example `abs`, `identity`, AMS package upload/CodeAgent creation, or
> `test ask`). When a flag's shipped default changes to `on`, add its documentation to
> `Tool_Readme.md`; when it changes to `off`, remove it.

## Command surface

The packaged tool command is `msagent`. The table below describes the full implemented
surface, including features that ship disabled. AMS operations, the ABS group,
and the identity group require their respective [feature controls](#feature-controls).
MCS routes and shared authentication/environment commands remain enabled by default.

| Command | Purpose |
|---|---|
| `msagent auth login` | Sign in through the operating-system broker's account picker on Windows, macOS, and Linux (silently first for an `--account` the broker already holds), falling back to device code and reporting the broker's failure when the broker cannot sign in; `--device-code` skips the broker. The only command that may open identity UI, and only when interaction is allowed. |
| `msagent auth status` | Report the current sign-in state. Never acquires a token or opens UI. |
| `msagent auth switch --account <id>` | Activate a stored account. Never acquires a token. |
| `msagent auth logout` | Sign out and clear cached accounts. Never opens identity UI. |
| `msagent env list` | List environments available to the signed-in account. |
| `msagent env select <environment-id>` | Select the active environment used by agent commands. |
| `msagent agent create` | Create an agent: a CodeAgent record and repository, or a local MCS project registered for deployment. |
| `msagent agent init` | Register an MCS project that already exists on disk. Creates no files and nothing in Copilot Studio. |
| `msagent agent list` | List the MCS agents or standalone workflows in an environment or across the tenant. An agent that has never been deployed is not listed. Workflows listed are every unmanaged cloud flow, including the flows agents use, never a flow a managed solution installed. |
| `msagent agent show` | Show the managed-agent records this project holds. Reads the local configuration; contacts nothing. |
| `msagent agent pull` | Bring the remote MCS agent's content into the project, taking every component collection beside it. |
| `msagent agent delete` | Delete an MCS agent's cloud resources and every record for it. Local files are never touched. Asks you to type the agent's name first, unless `--non-interactive` supplies that consent. |
| `msagent agent package upload` | Upload exact opaque package bytes and return the durable Package Upload Operation and immutable Package Revision. Provisional fN13j notation. |
| `msagent agent deploy --agent-id … --package-revision …` | Create explicit AMS Deployment state selecting an existing Package Revision and await the current target-routed AHRG projection. Provisional fN13j notation. |
| `msagent agent deploy --deployment-id …` / `--deployment-name …` | Push an MCS workspace to an existing MCS deployment, with optional agent selectors, `--publish`, and `--overwrite`. |
| `msagent test ask --query <text>` | Send one live query to a selected standard or GitHub Copilot Harness agent. Requires `LiveTestFeatures`; may perform real actions. |
| `msagent test playground [--target directline\|directengine]` | Launch the M365 Agents Playground for a live agent. DirectEngine discovers its endpoint; Direct Line prompts for a credential. Requires `LiveTestFeatures`; may perform real actions. |
| `msagent agent eval run` | Run a Copilot Studio evaluation against an MCS agent: run a saved test set with `--testset`, or create one from `--from-csv` / `--generate` plus `--name`. `--watch` polls until it finishes, `--published` targets the published agent, and `--output` writes the finished run as JSON once `--watch` has waited for it. Uses Copilot Credits; the cost is reported when the run finishes. |
| `msagent agent eval list` | List the agent's evaluation runs, newest first. `--limit` bounds the listing (default 20). |
| `msagent agent eval show --run-id <run-id>` | Show one run with its score, credits and per-conversation grader results. `--transcript` adds every conversation's questions and answers, shortened; `--conversation <n>` adds just one; `--output <file>` writes the full, untruncated run (`.json` writes JSON, any other name writes Markdown). |
| `msagent agent eval cancel --run-id <run-id>` | Stop a run that is still queued or in progress. A finished run is refused. |
| `msagent agent eval delete --run-id <run-id>` | Delete a finished run. A run still in flight is refused with the `cancel` command to run first; the test set it used is left alone. |
| `msagent agent eval generate` | Generate conversations from the agent's own description and list them, creating no test set. `--count` and `--turns` size the request, `--output` writes CSV. |
| `msagent agent eval testset list` / `show` | List the agent's saved test sets, or show one with the conversations it holds. |
| `msagent agent eval testset create --name …` | Create a durable test set from `--from-csv` or `--generate`. A name already in use is refused rather than duplicated. |
| `msagent agent eval testset update --testset …` | Add conversations to a saved test set, or replace the ones it holds with `--replace`. |
| `msagent agent eval testset export --testset … --output …` | Write a test set's conversations out as CSV, in the same format `--from-csv` reads. |
| `msagent agent eval testset delete --testset …` | Delete a test set and the conversations it holds. Asks you to type the test set's name first, unless `--force` or `--non-interactive` supplies that consent. Evaluation runs that used it are unaffected. |
| `msagent abs preview` | Plan the Azure Bot Service changes. Structurally read-only; never mutates Azure. |
| `msagent abs apply` | Apply the plan. Requires `--force` to mutate; without it, behaves as a preview. |
| `msagent identity create blueprint` | Create or reconcile an Agent Identity Blueprint, permissions, credentials, OAuth, and Teams SSO through Graph/ARM REST. |
| `msagent identity create app` | Create or reconcile an ordinary App Registration identity (ClientSecret, FederatedCredentials, or UserManagedIdentity), its API permissions, OAuth redirect, and Teams SSO through Graph/ARM REST. |
| `msagent deployment create` | Create a deployment. An MCS deployment records a slot and writes nothing remote; a CodeAgent deployment provisions an Azure Bot Service resource. |
| `msagent deployment list` | List deployment slots, in promotion order. Entirely local. |
| `msagent deployment show (--deployment-id <id> \| --agent-id <id> --deployment-name <name>)` | Get an existing deployment. |
| `msagent deployment update (--deployment-id <id> \| --agent-id <id> --deployment-name <name>)` | Update an existing deployment. An MCS deployment can be renamed; a CodeAgent deployment patches its Azure Bot Service settings. |
| `msagent deployment channel create` / `update` / `show` / `list` / `delete` | Manage supported Azure Bot Service channels for a CodeAgent deployment. |
| `msagent deployment oauth create` / `update` / `show` / `list` / `delete` | Manage Azure Bot Service OAuth connection settings for a CodeAgent deployment. |
| `msagent deployment oauth list-providers` | List or inspect the live Azure Bot Service OAuth provider catalogue. |
| `msagent deployment delete (--deployment-id <id> \| --agent-id <id> --deployment-name <name>)` | Delete a deployment. An MCS deployment removes the record, and the Copilot Studio agent it targets when asked; a CodeAgent deployment deletes its backing bot. |
| `msagent deployment connection list` | Report the agent's connection references grouped by connector, with the connection behind each and how much of the connector is connected (`connected`, `not connected`, or `connected (1), not connected (2)` when it is partly bound). `--by-reference` lists each reference on its own row instead. Changes nothing. MCS only. |
| `msagent deployment connection add --connector <internal-id>` | Declare a connection reference for the connector and bind it: an existing connection you own, one you pick, or one created through browser consent. Prints the generated logical name to use in a component. `--connection-id` binds an explicit connection; `--connection-only` creates the connection and declares no reference. A reference it cannot bind is still declared, and the command exits 0. MCS only. |
| `msagent deployment connection update` | Rebind the agent's connection references. Lists the connectors and resolves one at a time: pick a connector, choose an existing connection or create a new one, and every reference on that connector is bound together. `--connection-reference`/`--connector` narrow the run; `--connection-id` binds an explicit connection; `--by-reference` resolves one reference at a time instead of a connector at a time. MCS only. |
| `msagent deployment connection remove` | Remove a connection reference from the workspace with `--connection-reference`, delete a connection from the environment with `--connection-id`, or both in one run. Asks before removing a reference components still name, and refuses to delete a connection other references still hold; `--force` is the consent for either. Removing a reference also deletes the environment's `connectionreference` record; the environment refuses that delete while another component still depends on it, which leaves the declaration removed and the record in place, and the command reports which. MCS only. |
| `msagent deployment connection connectors` | List the connectors the environment serves, so a reference can be declared against one. Reads the environment, not the agent: `--environment-id` names one directly, or a deployment selector resolves one. `--filter` matches display name, internal id and publisher; `--connector` matches an internal id exactly; `--limit` bounds the listing (default 50). Marks the connectors a declared reference already targets. The catalog omits connectors the environment does not publish, so `add` and `remove` accept any connector the connections API still answers for, and reject only one it reports as unknown. MCS only. |
| `msagent telemetry enable` / `disable` / `status` | Manage and inspect the remembered command-telemetry preference. These commands never measure themselves. |

Global flags: `--json` (machine-readable output), `--debug` (redacted API responses on stderr),
`--no-color`, `--non-interactive`
(do not open a browser or broker sign-in; fail instead). Several commands qualify that last flag:
`deployment connection add` and `deployment connection update` still create a connection, printing
the consent link to open by hand rather than launching a browser, and waiting for it to be
completed on this machine or another device;
`deployment connection update` also lists its connectors and reads the answer from stdin; plain
`deployment connection add` creates a connection only when the connector has no candidate to choose
between, since choosing one is a decision it cannot make without asking; and `agent delete` /
`deployment delete` / `deployment connection remove` take it as the operator's consent instead of
asking.

### Live agent test (controlled preview)

Build with `dotnet build src\Microsoft.Agents.Hosting.Cli.slnx` and test with
`dotnet test src\Microsoft.Agents.Hosting.Cli.Tests\Microsoft.Agents.Hosting.Cli.Tests.csproj`.
`LiveTestFeatures` ships off; opt in with `AH_CLI_FEATURES=LiveTestFeatures` or the
installed `featureflags.json`. This flag exposes the command but **does not grant
access or approve a preview**. Select the intended account with `msagent auth
login`, and configure `AH_CLI_CLIENT_ID` for an authorized public/native client
with public-client redirect flow and delegated Power Platform
`CopilotStudio.Copilots.Invoke` consent. The selected standard or GitHub Copilot Harness agent must be available in the
specified environment and shared with that account. Public cloud is the
supported target; CodeAgent/AHRG is not.

```text
msagent test ask --query "Explain this policy" --project <project> --agent-name <agent> --deployment-name <deployment>
msagent test ask --query "Hello" --schema-name <schema> --environment-id <environment> --tenant <tenant> --deployment-name <selected-label>
msagent test ask --query "Clarify" --conversation-id <returned-id> --json --agent-id <agent-id>
msagent test ask --query "Hello" --schema-name <schema> --environment-id <environment> --tenant <tenant> --deployment-name <selected-label> --plain
```

The beta Copilot Studio client uses the discovery service to resolve the runtime
endpoints from the environment and schema name. No harness selection is needed.
Discovery failure stops the invocation; the command never guesses or falls back
between runtime endpoints.

The configured route reads `agent.config.json` and requires an explicit deployment
or exactly one marked default; it never substitutes the home/current environment.
A single deployment is not automatically a default. If target selection reports
no configured default deployment, pass its name explicitly, for example
`msagent test ask -dn dev -q "What can you do?"`. This validation happens before
authentication, endpoint discovery, or any SDK request.
The fully explicit route needs schema, environment, tenant and a deployment label;
the label records your selection but is **not** a verified registry association.
An omitted conversation ID starts a new conversation; reusing an ID must be
explicit. `--json` writes one incrementally flushed document containing full
ordered activities and a final status; `--plain` emits accessible final-only
human output without transient progress. They cannot be combined. JSON does not
prohibit interactive sign-in; `--non-interactive` does.
Streamed (`text/event-stream`) responses are rendered activity by activity as
they arrive, through the SDK's incremental parser. A truncated, malformed or
interrupted stream fails with a sanitized error. A whole-JSON response is still
read completely before its first activity. Early delivery is verified only
against simulated responses, not yet against a live agent.

Queries may enter shell history. Agent actions are **not a dry run**, are not
rolled back, and may continue remotely after local cancellation. A network
failure does not trigger automatic query replay; check the destination before
choosing to retry. `completed` means only that the SDK response enumeration
ended normally and output closed, **not** that the answer or remote action was
successful. The CLI does not save queries, activities or conversation IDs;
terminal capture, redirection and service-side retention remain outside its
control. Do not treat returned URLs or attachments as trusted executable input.
This preview command is not exempt from the CLI's existing command telemetry.
Until its identifier-minimization policy is reviewed, keep the feature disabled
or set `AH_CLI_TELEMETRY_OPTOUT=1` before invoking it; the feature flag alone does
not change telemetry collection.

Live integration and accessibility checks require a separately approved
tenant, identity, published standard or GitHub Copilot Harness agent and harmless query. Population
and load, named support/destination-validation owners, rollout mechanics,
security/privacy review timing and any additional applicable standards remain
unassigned preview-readiness decisions. A future AHRG adapter would require its
own target, delegated-auth, transport and completion contracts; none is inferred
from the MCS implementation. No numerical release SLO or live baseline is claimed.

### M365 Agents Playground test (MVP)

`msagent test playground` launches the
M365 Agents Playground in the foreground for the same selected agent as `test ask`.
The `--target` option selects `directengine` (the default) or `directline`.
DirectEngine uses the same agent selectors as `test ask` and the same resolver.
Neither mode accepts `--query` or `--conversation-id`.
The command is gated by the existing
`LiveTestFeatures` flag, which is off by default; enabling that flag also enables
this command, with no separate playground flag.

```text
msagent test playground --project <project> --agent-name <agent> --deployment-name <deployment>
msagent test playground --schema-name <schema> --environment-id <environment> --tenant <tenant> --deployment-name <selected-label>
msagent test playground --target directline
```

For DirectEngine, the CLI uses the selected signed-in account and delegated
`CopilotStudio.Copilots.Invoke` permission to query the discovery service, without
starting a conversation. It passes the discovered DirectEngine endpoint and
public client configuration to the package. Discovery failure prevents launch.
The package owns device-code
or browser authentication in the attached foreground console; `msagent` does
not hand it tokens or secrets. The command reports the selected
`agentsplayground` path and version. Readiness is established immediately when
the process starts. The package owns authentication timing; `msagent` does not
wait for the package's authentication, and process start is not proof that authentication
succeeded.

#### Direct Line connection

`msagent test playground --target directline` requires an interactive terminal
with both input and output attached. It rejects `--non-interactive`, including
when `--install-playground` is supplied. It also rejects the DirectEngine-only
options `--project`, `--agent-id`, `--agent-name`,
`--schema-name`, `--deployment-id`, `--deployment-name`, `--environment-id`, and
`--tenant`. It does not resolve local project/deployment configuration, sign
the CLI into Entra ID, or load the AgentHost public-client configuration.

Publish the Copilot Studio agent with **no authentication** and enable its
global public-cloud Direct Line connectivity. At the native playground's
**masked terminal prompt**, enter either the complete HTTPS token endpoint from
**Channels > Mobile app**, or a raw Direct Line secret from
**Settings > Security > Web channel security**. Do not paste credentials into
chat, command arguments, environment variables, or configuration files.
Empty Enter, Escape, or Ctrl+C cancels native credential setup.

The native playground owns credential validation and the connection. `msagent`
does not acquire, save, or log the credential and uses an isolated, disposable
working directory with no inherited playground config. The native default
display name is used. The listener remains on `127.0.0.1`, native telemetry is
disabled, and connection/recording overrides are removed from the child
environment without changing the parent environment.

Use Node.js 22.17.1+ LTS for Direct Line. Regional/custom/sovereign Direct Line
endpoints, OAuth-card sign-in, file upload, headless interaction, and saved
connections are not supported by this integration. Use DirectEngine when the
agent requires Microsoft sign-in. Process start is not proof of a connected
conversation; wait for Connected and an enabled composer in the owning browser.

#### Playground installation and options

The command uses an existing `agentsplayground` available on `PATH`, backed by
the global `@microsoft/m365agentsplayground` package, and requires version
`0.2.29` or newer. A working installed tool does not trigger an npm probe. If
the tool is missing or too old, interactive use offers
`npm install -g @microsoft/m365agentsplayground@latest`; accepting it runs npm
lifecycle scripts that may execute code, so consent is required.
`--non-interactive` refuses that offer unless `--install-playground` is also
supplied. The install path checks for Node.js 22 or newer and npm, but never
installs or upgrades Node.js or npm. The Windows candidate was manually checked
with Node.js 22.17.1 and npm 10.9.2. Use a user-owned npm prefix or a version
manager when a global prefix requires elevation. The user's npm registry,
proxy, and authentication configuration is left intact.

`--plain` suppresses `msagent` transient progress; the package's own console
output is still inherited. DirectEngine authentication may require user action even
with `--non-interactive`; Direct Line rejects that option. `--json` is rejected in this MVP because structured
playground streaming is not supported. The MVP has no optional upgrades,
managed install store, detach, retry, or transcript feature.

#### Lifetime, effects, and limits

This is a live action: effects are not rolled back. On Ctrl+C or normal exit,
the command requests graceful shutdown, waits up to 10 seconds, then kills only
the child process tree and allows up to another 10 seconds for cleanup. It never
kills by process name or port, and the user's shared browser must survive. An
abrupt `msagent` parent crash can orphan the playground server; that is a known MVP limitation.
Version probes time out after 10 seconds and installation after 600 seconds.
There is no separate readiness-timeout promise beyond process-start readiness.

#### Manual MVP test matrix

Run this matrix with disposable agents and harmless turns; it is live validation,
not part of the offline test suite:

| Area | Guidance |
| --- | --- |
| Native package (Windows) | Before CLI endpoint discovery was introduced, the Windows native-package spike passed against standard and GitHub Copilot agents on `0.2.29`. This does not validate the CLI discovery flow. |
| DirectEngine discovery (offline) | Automated tests cover discovery responses, passing the discovered endpoint to the launcher, discovery failures, and cancellation using test doubles. These are not live connection checks. |
| DirectEngine discovery (Windows, live) | Run `msagent test playground` with the appropriate agent/deployment selectors for both standard and GitHub Copilot agents; the discovery service selects the endpoint automatically, with no harness argument. Live validation of this discovery flow remains pending. The earlier pre-discovery standard-agent run passed connected-state, two-reply, Ctrl+C, browser-survival, and owned-process-cleanup checks; it does not validate discovery. |
| `msagent` command (POSIX) | Not run on macOS or Linux/WSL; POSIX manual validation remains pending and no full certification is claimed. |
| Tool resolution | Cover an existing current tool, a missing tool, and an old tool; verify the version probe, consent text, `--install-playground`, npm registry/proxy/auth settings, permission failures, and network/install failures. |
| Safety and output | Verify LiveTestFeatures opt-in, `--json` rejection, `--plain` output, native device-code/browser behavior, and the warning that live effects are not rolled back. |
| Direct Line native contract | Installed `0.2.29` help and bundled setup code were inspected for target support, masked entry, default display name, cancellation, and isolated-config fallback. This is not a live connection check. |
| Direct Line live validation | The user manually validated that the Direct Line connection works. Reply, Ctrl+C, browser-survival, and owned-process-cleanup checks were not separately reported; those manual checks remain pending. Do not record credentials or conversation content. |

## Command telemetry

The CLI uses one command-telemetry sender and one shared schema. A leaf dispatch can produce one
`CommandAttempt` and at most one bounded
`CommandOutcome`; help, version, parser rejection, and group usage produce one `HostCase`. All use
the shared OneDS `event` envelope and one invocation-local batch. Outcomes distinguish success,
usage, not-signed-in, failure, timeout, and caller cancellation. A rendering failure leaves the
attempt without inventing an outcome. The `telemetry` control group never measures itself.

After collection policy enables an invocation, normal command authentication may enrich its
buffered events with the issuer tenant. A tenant-local Entra object id is accepted only when the
SDK result has an account, exactly one matching `tid`, exactly one nonempty `oid`, and no `idtyp`
or exactly one `idtyp=user`. App/workload identities and missing, malformed, duplicate, or
tenant-mismatched claims remain tenant-only. Unknown context does not prevent usage collection.
Conflicting fresh user contexts never merge subjects: the object id is removed, and a tenant is
retained only when the fresh contexts agree on it.

The profile selected at invocation startup supplies a fallback for local commands only when it has
token-free metadata previously persisted by a normal `auth login` under the same CLI client,
authority, and cloud. Silent and consent authentication can attribute their current invocation but
never update profile metadata. A fresh normal-auth result takes precedence over that fallback; a later
successful profile switch, login activation, or logout replaces or clears the selected context.
Old profiles remain readable and attribution-unknown for local commands until the next normal
login; telemetry never forces that login. The optional startup read is an independent,
delete-sharing snapshot and never takes the product profile-store mutex.

Telemetry does not discover operating-system accounts, initiate login or consent, acquire a token,
call Graph, refresh authentication beyond normal product work, or use the geography-only
selected-account acquisition for identity. The MSAL home-account id is only a local profile key:
it is never interpreted as an Entra object id or emitted. No username, email, home-account id,
PUID, token, hardware id, persistent local-instance id, or raw JWT is emitted. There is no
alternate identity mode or identity-selection environment variable. Disabled, opted-out,
policy-failed, and nested-suppressed invocations do not read profile attribution or inspect claims.

The shared OneDS event shape follows the Managed Apps provider placement: `operationName` and
`telemetryLocation` are promoted to `operation_Name` and `telemetryLocation` while remaining in
the JSON-string `customDimensions`; `tenantId`, `cliVersion`, `cloud`, `station`, `isOnePlayer`,
and `webPlayerScenario` remain dimensions. A subject-bearing `CommandAttempt` carries
`principalObjectId` alongside `tenantId` in those dimensions, so account queries do not depend on
a dedicated principal column in the downstream table. It also retains the standard PPUX
`data.tenantId` and `data.principalObjectId` fields as the same pair. Outcomes and `HostCase`
records have no principal dimension or direct subject fields; existing tenant dimensions remain.
No `objectId`, `pptoolsDeviceId`, identity mode, or payload schema-version field is emitted.
Payloads never carry arguments, paths,
URLs, free-form errors, results, tokens, or account-cache data.

Every event uses the PPUX SDK's standard `Microsoft_IsPII_Hash` (`0x100000`) metadata flag, matching
PAC's raw tenant/object-id event shape. The CLI does not claim that this flag hashes arbitrary
Part C fields: `tenantId` and `principalObjectId` are sent as the validated GUID strings shown
above, while collector-side classification/projection remains an ingestion-owner contract. These
records support host-case, command-attempt, command-outcome, observed-tenant, and observed
tenant-local-account proxy measures. The account proxy is not proof of a human operating each
command, a person across tenants, a device, an installation, all users, or an adoption rate.

The two OneDS public-client ingestion routing values are declared in the dedicated
`Telemetry/TelemetryIngestionConfiguration.cs` class, following PAC's code-based configuration
pattern. Update `ProductionKey` or `NonProductionKey` there when ingestion configuration changes.
They are public ingestion-routing identifiers, not authentication credentials. The code comment
documents that classification; it is not a secret-scanner suppression. No special classification
attributes are currently applied. There is no separate always-false
feature gate. Collection for the executable's selected ring is enabled by default, subject to
`AH_CLI_TELEMETRY_OPTOUT=1|true` and the remembered `msagent telemetry disable` preference. `0|false`
explicitly opts in. An invalid nonempty environment value or invalid preference fails closed.
Before ordinary command dispatch, the CLI waits at most 250 milliseconds for this optional
collection-policy read. A timeout disables telemetry for that invocation and cannot activate it
later, even if noncooperative policy I/O finishes afterward. This bounds only the caller's wait for
optional policy evaluation; it cannot forcibly terminate underlying OS I/O. Caller cancellation
still propagates. Explicit `msagent telemetry enable`, `disable`, and `status` operations are not subject
to this optional startup deadline and retain their normal errors and cancellation.

Public production and nonproduction rings select their corresponding routing value and
shared-table metadata.
The current executable selects `PowerPlatformApiTargetConfig.Public`, whose ring is production, in
`Program.cs`; therefore it uses the production key slot. Supplying the nonproduction key alone does
not select nonproduction. A nonproduction build must select `PowerPlatformApiTargetConfig.Test` at
that same composition point and then uses the nonproduction key slot.

Production collector geography follows the environments that normal product execution actually
resolves. Active, explicit, workspace, deployment, and `env select` resolution share one
invocation-scoped observation boundary; inventory enumeration alone is not treated as a target.
At disposal, the CLI derives the trusted environment-scoped Power Platform API host from each
resolved environment id and the configured cloud/ring, then requests
`/gateway/cluster?api-version=1`. Only the authoritative `geoLongName` response field is used.
Inventory geography is neither used nor persisted.

This metadata lookup can acquire or refresh a token only from the exact account already selected
in the CLI profile. It uses a non-broker silent MSAL client and never uses an operating-system
account, chooses another cached account, or falls back to browser, device-code, WAM, or any other
interactive sign-in. An absent selected account or any token, request, timeout, or response error
keeps the conservative EU route. Metadata-only authentication cannot add tenant attribution to
telemetry; tenant enrichment still comes only from normal product authentication.

The invocation keeps at most four distinct target environment/tenant pairs. All targets must
resolve to known non-EU production geographies to use the global/US collector. Any EU, unknown,
malformed, conflicting, or excess target context routes the whole attempt/outcome batch to EU, so
the batch never splits or changes route after sending begins. Environment-independent and
tenant-wide enumeration commands also use EU without metadata authentication. Nonproduction routes
directly to the global/US collector without a geography lookup. Known sovereign-cloud targets do
not send. Opt-out, telemetry control commands, and invocations suppressed by collection policy
perform no metadata work.

Sending is one invocation-local batch. Separately from the 250-millisecond policy budget, one
shared one-second deadline bounds destination resolution, event serialization, and transport.
Production metadata resolution has a 400-millisecond sub-deadline so fallback routing can leave
time for transport. These deadlines cannot forcibly terminate already-started noncooperative
dependencies, but late results cannot mutate the route, start a send, or cross into another
invocation. No retry is started. Telemetry failure cannot change command output, cancellation, or
exit behavior.

Optional measurement failures are silent by default. Local diagnostics can opt in through
EventPipe or ETW to the `Microsoft-AgentHosting-Cli-OptionalMeasurement` EventSource. Its
`OptionalMeasurementFailure` event carries only a fixed integer category code: it never includes an
exception, message, stack, path, URL, routing value, token, identity, claim, or command argument.
The provider writes no console or JSON output, persists no log, and emits no telemetry about
telemetry.

### Telemetry counting contract

Use the envelope occurrence timestamp (the Kusto `['time']` column) and a half-open UTC window:
`startUtc <= occurrenceUtc < endUtc`. Keep these measures separate:

- **Command attempts** count rows whose `data.event_Name` is `CommandAttempt`. They count accepted
  CLI leaf attempts, including retries; they are not logical operations, deployments, hosted-agent
  runtime use, or end-user activity.
- **Successful command outcomes** count `CommandOutcome` rows whose JSON-string
  `data.customDimensions` has `outcomeCategory == "success"`. Do not pool outcomes into attempt
  counts. A missing outcome does not prove success or failure.
- **Host cases** count `HostCase` separately for help, version, parser rejection, and group usage.
- **Known-tenant attempts** are attempt rows whose parsed dimensions contain a nonempty `tenantId`;
  all other attempt rows are unknown-tenant attempts. A distinct count of observed tenant IDs is a
  tenant count only, never a count of people, users, devices, installations, or eligible adopters.
- **Known-account attempts** are `CommandAttempt` rows with both nonempty
  `tenantId` and `principalObjectId` in parsed `customDimensions`. Distinct accounts are the
  distinct paired values, never object ids alone. The pair is a tenant-local selected/observed authenticated-account proxy,
  not a human count. Filter missing values before `dcount`.

`operation_Name` and `telemetryLocation` are promoted fields and are also retained as
`operationName` and `telemetryLocation` in the JSON-string dimensions. Aggregate tenant context is
in those dimensions; subject-bearing attempts add the principal dimension and retain the paired
direct fields on the wire. Delivery is best effort: event counts do not provide exactly-once,
loss-free, or deduplicated semantics, and no
installed-base denominator exists for an adoption rate.

The regional destinations are the US `powerappsclient.eastus2.kusto.windows.net` and EU
`powerappsclientneu.northeurope.kusto.windows.net`, database `PowerApps-1DS`, table `event`, with
`data_clientType == "AgentHostingCLI"`. The following bounded example is **unexecuted** and uses
the existing dynamic `data_customDimensions` column rather than requiring dedicated subject
columns. Confirm the new principal dimension's delivery before relying on account counts:

```kusto
let startUtc = datetime(2026-09-22T00:00:00Z);
let endUtc = datetime(2026-09-23T00:00:00Z);
let ah =
    union
        cluster('powerappsclient.eastus2.kusto.windows.net').database('PowerApps-1DS').event,
        cluster('powerappsclientneu.northeurope.kusto.windows.net').database('PowerApps-1DS').event
    | where data_clientType == "AgentHostingCLI"
    | extend occurrenceUtc = ['time']
    | where occurrenceUtc >= startUtc and occurrenceUtc < endUtc
    | extend dimensions = parse_json(tostring(data_customDimensions))
    | extend
        eventName = tostring(data_event_Name),
        operationName = tostring(data_operation_Name),
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

Union the filtered regional attempt rows before computing the distinct paired account count,
as above. Do not sum per-region, per-day, per-command,
tenant-only, or user/local distinct counts: the same subject can appear in more than one slice.
Historical rows without the principal dimension remain account-unknown. Dedicated top-level
subject-column projection is not required for this query and is not inferred from wire placement.
Allow for downstream ingestion delay when confirming delivery: `ext_ingest_time` records collector
time, while `ingestion_time()` reports Kusto ingestion time. An initially empty query does not prove
that sending failed.

The malformed preference-file corpus is deterministic parser regression coverage, not a fuzz
harness or an external tracking disposition. Transport remains the shared
`Microsoft.PowerPlatform.Tooling.BatchedTelemetry` 4.0.28 SDK. That SDK does not expose a caller
HTTP-handler/TLS seam here, so this CLI neither installs a decorative TLS handler nor claims that
the absence of one proves or exempts any TLS requirement. The
`Telemetry.TelemetryLocation` qualifier in projection code intentionally disambiguates the enum
from the string property of the same name.

### Concrete fN13j package and deploy profile

The current implementation exposes package upload and Deployment projection as independently
invokable effects. This is empirical proof scaffolding, not the final default-Deployment design or
an approval of these exact command spellings.

AMS-specific inputs select the package-revision path; they cannot be combined with MCS-only
inputs such as `--project`, `--agent-id`, `--deployment-id`, or `--publish`. `--deployment-name`
is shared: it names new desired state for AMS and selects an existing deployment for MCS.
MCS deployment keeps its own requests, registry, and backend. The separate `msagent deployment`
Azure Bot Service commands and `msagent mcs-deployment` slot commands do not create an AMS
Package Revision or project an AMS Deployment to AHRG.

The CodeAgent `msagent agent deploy --project ...` upload-only form is not supported. Supply an
already-built package to upload, then select the returned revision for deployment:

```pwsh
msagent agent package upload `
  --agent-id <agent-id> `
  --package <agent.zip> `
  --environment-id <home-environment-id>
```

Package upload hashes and sizes the exact supplied file without opening the archive, sends it once
to AMS-DataPlane, and validates the terminal Package Upload Operation and immutable Package
Revision. It does not call AMS-CMP or create or project a Deployment.

`--package` is an explicit operator-selected local file, not a path supplied by AMS or archive
contents. Relative paths are resolved against the process working directory using `Path.GetFullPath`
before opening; subsequent file-read errors identify that absolute, normalized path. If normalization
itself fails, the error identifies the original input. Files outside the repository are intentionally
supported, subject to the operating system's file permissions. There is no configured package root or
sandbox boundary: normalization does not resolve symlinks/junctions or claim directory confinement.
The same read-only stream is hashed, rewound, and uploaded.

A non-success response carrying a validated durable `Accepted` or `Failed` operation returns a
nonzero exit code. Under `--json`, `partialResult.packageOperationId` and
`partialResult.operationStatus` preserve that identity structurally; no Package Revision is
confirmed. The CLI does not poll or automatically retry the upload.

```pwsh
msagent agent deploy `
  --agent-id <agent-id> `
  --package-revision <package-revision-id> `
  --environment-id <home-environment-id> `
  --target-environment-id <target-environment-id> `
  --deployment-name <name> `
  --deployment-type <dev|test|prod> `
  --entra-id <bot-application-id> `
  --entra-id-type <app-reg|blueprint|default>
```

`--environment` defaults to the active environment. `--target-environment` is required, including
for intentional same-home Dev placement; it never defaults to the home environment.
`AH_CLI_AMSDP_ENDPOINT` and `AH_CLI_AMS_ENDPOINT` configure AMS-DataPlane and AMS-CMP; neither
endpoint can be supplied on the command line.

Deploy reads the bound CodeAgent through AMS-DataPlane and sends only identifiers and explicit
desired state through AMS-CMP. The current PPRP Deployment-create handler persists the authoritative
home Deployment and returns only after its target-routed AHRG ESP projection call completes. That is
the behavior this command reports as acknowledged; there is not yet a separate public durable
Deploy Operation. Deployment-create requests send the Agent language as the declared enum name
`DotNet`, `Python`, or `Node`; responses accept both named and numeric enum values.

The Agent-read and Deployment-create HTTP handler supports TLS 1.3 and TLS 1.2 and rejects redirects.

Deploy does not upload package bytes. The current GRS object remains the runtime package source;
target-local package staging is deferred. Success does not prove runtime readiness, recovery, or a
true cross-island call. In particular, a same-island personal-Dev exercise proves the target-routed
boundary shape only. Cross-island evidence requires an explicit distinct-island Test deployment.

#### Temporary AMS response-parser coverage

[Issue #120](https://microsoft.ghe.com/bic/agents-managed-hosting/issues/120) tracks mutation fuzzing
for `AmsDeploymentAdapter.GetAsync` (Agent read), `CreateAsync` (Deployment create), and
`AmsPackagePublicationAdapter.ParseOperation` (package-upload responses through both
`ParseSuccess` and `TryParseDurableFailure`).
Until that work lands, `AmsDeploymentAdapterTests.MalformedSuccessResponses` supplies a bounded,
deterministic malformed-response corpus exercised by `MalformedSuccess_IsRejected` through the
public adapter with fake HTTP and token providers. It covers malformed/truncated JSON, invalid
escapes and trailing data, non-object roots, excessive nesting, missing/null/wrong-type required
fields, blank strings, invalid enums, integer overflow/fractional values, and invalid booleans.
The assertions require typed `invalid-ams-response` failures, no response-body detail in public
diagnostics, one request without retry, and retained Deployment-create correlation with and without
a service request ID. Positive named/numeric enum coverage remains alongside the rejection cases.

`AmsPackagePublicationAdapterTests.RejectedOperationResponses` extends the deterministic corpus to
package-upload responses through the public `PublishAsync` boundary with fake HTTP and token
providers. It covers malformed JSON, non-object roots, excessive nesting, missing/null/wrong-type
fields, invalid or empty GUIDs, digest length/hex errors, non-positive/fractional/overflowing sizes,
invalid timestamps and reversed chronology, invalid statuses, and inconsistent status/revision or
HTTP-status/operation combinations. Each malformed operation is exercised on both HTTP success
and failure paths: malformed successes produce typed `invalid-ams-response` failures, while
malformed non-success bodies retain the HTTP error mapping rather than inventing a durable partial
result. The assertions also preserve client/service correlation, safe public diagnostics, no retry,
and caller ownership of the package stream. Positive cases retain named/numeric statuses and
accepted/failed operations with absent or null revision IDs.

This is temporary regression coverage, not a SharpFuzz/OneFuzz harness or mutation campaign, and
does not independently establish compatibility with deployed AMS contracts. The issue tracks the
remaining harness, runner integration, and campaign evidence; no hosted fuzzing is provisioned here.

### Blueprint identity

Sign in to the target tenant with the required delegated Graph scopes:

```pwsh
msagent auth login --tenant <tenant-id> `
  --scope https://graph.microsoft.com/AgentIdentityBlueprint.ReadWrite.All `
  --scope https://graph.microsoft.com/AgentIdentityBlueprintPrincipal.Create `
  --scope https://graph.microsoft.com/User.Read `
  --scope https://graph.microsoft.com/Application.Read.All
```

Create a client-secret Blueprint:

```pwsh
msagent identity create blueprint `
  --tenant <tenant-id> `
  --display-name "Contoso Agent" `
  --auth-type ClientSecret `
  --config-format dotnet `
  --oauth `
  --teams-sso
```

Use `--observability` and `--mcp-discovery` for the optional A365 permissions. For
`FederatedCredentials`, also supply `--subscription`, `--resource-group`, `--location`, and
`--managed-identity-name`. `--dry-run` validates and renders the deterministic plan without making
REST calls. Client-secret output is one-time material: store it immediately; the CLI does not persist
it. Apply returns an `adminConsentUrl`; a Global Administrator must open it to grant the selected
tenant-wide delegated permissions. The identity command never opens the browser itself.

Create an App Registration identity (the current, fully-featured identity type):

```pwsh
msagent identity create app `
  --tenant <tenant-id> `
  --display-name "Contoso Agent" `
  --auth-type ClientSecret `
  --config-format dotnet `
  --oauth `
  --teams-sso
```

`msagent identity create app` targets ordinary Graph application routes rather than Blueprint casts, so it
also supports `--auth-type UserManagedIdentity` (a user-assigned managed identity is the runtime
credential and a companion App Registration holds the OAuth identity and bot permissions). Pass
`--app-id` or `--object-id` to reconcile an existing App Registration instead of matching by display
name. `--oauth` adds only the redirect URI; `--teams-sso` includes that redirect behavior and also
exposes `access_as_user` and pre-authorizes the Teams/Office host clients. `--observability`,
`--mcp-discovery`, `--dry-run`, one-time secret handling, and the `adminConsentUrl` handoff behave the
same as for `identity create blueprint`. The command registers the Entra admin-center callback used by
that handoff, sets `requestedAccessTokenVersion` to `2` for Teams SSO, and directly verifies or creates
the Observability application-role assignment. Existing applications must already use the
`AzureADMultipleOrgs` audience; incompatible applications fail before mutation rather than being
silently reported as reconciled.

**Delegated Microsoft Graph consent (always on, paired with A365):** rather than depending on the CLI's
Power Platform first-party app being centrally preauthorized for Microsoft Graph, `identity create app`
acquires Graph tokens with a separate consent-capable client and expects a tenant admin to consent to
it once on first use — exactly A365's model. The plan and reports announce the selected Graph client
and emit a tenant admin-consent URL for the Graph scopes; if Graph reports consent is required at apply
time, the command returns a nonzero `consent-required` failure carrying that partial result and URL.
There is no flag — the default client is the well-known **Microsoft Graph Command Line Tools**
public client (the `Connect-MgGraph` SDK client, Microsoft-preauthorized for Graph delegated scopes), a
placeholder until a dedicated AgentHost client is provisioned. (A365's own default, its
`PowershellClientId` `1950a258…`, is actually *Azure PowerShell* and is not Graph-preauthorized —
requesting Graph through it fails with `AADSTS65002` — so the Graph path uses the Graph CLI Tools client
instead.) At apply time the command acquires the Graph token through that consent-capable client
(consenting interactively on first use, silently thereafter). Override the client with the
`AH_CLI_GRAPH_CLIENT_ID` environment variable; it must be a GUID and cannot be the CLI first-party
app id, which can only be preauthorized for Graph, never consented. An override must also set
`AH_CLI_GRAPH_REDIRECT_URI` to an HTTP loopback redirect registered on that public client. The
resolved client and redirect are shared by token acquisition, dry-run output, and consent remediation
so those surfaces cannot disagree. Dry-run omits the random anti-replay state; apply-time consent URLs
retain it.

**Delegated Azure Resource Manager consent (always on, paired with A365):** the
`federated-credentials` and `user-managed-identity` auth types provision a user-assigned managed
identity over Azure Resource Manager, which the CLI first-party app is not preauthorized for.
Both App Registration and Blueprint provisioning therefore acquire ARM tokens through a separate
consent-capable client, consenting on first use, while Blueprint Graph calls continue to use the
AgentHost client. This mirrors A365, which uses a separate Azure CLI authentication path for ARM.
ARM consent is user-consentable, so no admin-consent URL is required; under `--non-interactive` a
required first-use consent surfaces as a nonzero interaction error asking you to rerun
interactively. The default client is the Azure CLI public client (a placeholder until a dedicated
AgentHost client is provisioned); override it with the `AH_CLI_ARM_CLIENT_ID` environment variable
(same GUID and non-first-party rules as Graph) and set `AH_CLI_ARM_REDIRECT_URI` to that client's
registered HTTP loopback redirect. Dry-run reports the resolved ARM client. Generated Node
configuration includes the authority endpoint and indexed Bot Framework scope alongside the
credential identifiers.

Identity REST calls make at most three attempts. Throttled Graph and ARM requests honor
`Retry-After`, including writes; repeatable GET, PUT, PATCH, and DELETE requests also retry transient
network interruptions and 502/503/504 responses with exponential backoff. POST requests are not
replayed after an ambiguous service failure, preventing duplicate application, principal,
credential, or role-assignment creation.

Microsoft Graph's application resource does not expose an ETag or document `If-Match` support.
Reconciliation merges the state it reads and preserves unrelated values, but separate CLI processes
must not reconcile the same application concurrently because Graph cannot provide an atomic
read-modify-write precondition for these application collections. Dedicated AgentHost Graph and ARM
consent clients are also required before production rollout; issue
[`#105`](https://microsoft.ghe.com/bic/agents-managed-hosting/issues/105) tracks replacing the
temporary Microsoft Graph CLI and Azure CLI client defaults.

### Deployment, channels, and OAuth

Sign in for ARM Bot Service provisioning:

```pwsh
msagent auth login
```

Create a CodeAgent deployment (provisions a real Azure Bot Service resource):

```pwsh
msagent deployment create `
  --agent-id <agent-id> `
  --deployment-name <deployment-name> `
  --deployment-type Test `
  --entra-id <app-id> `
  --entra-id-type AppReg
```

Show a deployment:

```pwsh
msagent deployment show --deployment-id <deployment-id>
```

The subscription and tenant are auto-resolved from the signed-in account. The resource group is
auto-resolved when the subscription has exactly one.

Create a channel on a CodeAgent deployment:

```pwsh
msagent deployment channel create `
  --deployment-id <deployment-id> `
  --channel msteams `
  --parameters isEnabled=true enableCalling=true
```

List the channels on a CodeAgent deployment:

```pwsh
msagent deployment channel list --deployment-id <deployment-id>
```

Create an OAuth connection on a CodeAgent deployment:

```pwsh
msagent deployment oauth create `
  --deployment-id <deployment-id> `
  --connection <connection-name> `
  --provider-name Aadv2 `
  --client-id <app-id> `
  --client-secret <secret> `
  --scopes "openid profile" `
  --parameters TenantId=common TokenExchangeUrl=api://exchange
```

List the OAuth connections on a CodeAgent deployment:

```pwsh
msagent deployment oauth list --deployment-id <deployment-id>
```

## Projects

| Project | Purpose |
|---|---|
| `Microsoft.Agents.Hosting.Cli` | Thin executable and packaged `dotnet tool` host (`ToolCommandName` = `msagent`). Parse → dispatch → render only. |
| `Microsoft.Agents.Hosting.Cli.Commands` | The command model: one class per command, declaring its options and binding its request. References Core only. |
| `Microsoft.Agents.Hosting.Cli.Core` | Embeddable operations facade, models, planner, and ports. No console, MSAL, or Azure coupling. |
| `Microsoft.Agents.Hosting.Cli.Auth` | The MSAL adapter that implements the auth ports. The only project that references MSAL. |
| `Microsoft.Agents.Hosting.Cli.CopilotStudio` | The Copilot Studio adapter used by the MCS agent and deployment verbs. |
| `Microsoft.Agents.Hosting.Cli.Tests` | xUnit unit, contract, and command-host tests. |
| `Microsoft.Agents.Hosting.Cli.E2E` | The live end-to-end harness. Packs the tool and drives it against a real tenant, so it needs credentials and is not part of the offline test run. |
| `Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests` | The embeddability proof: references `Microsoft.Agents.Hosting.Cli.Core` **only** and drives the operation facade without the command host or MSAL. |

All are members of `src/Microsoft.Agents.Hosting.Cli.slnx`, which builds standalone. They also appear in
`src/AgentHosting.slnx`, the aggregate solution that additionally carries the runtime projects
(`AuthSidecar`, `HostingGateway`, `MockPlex`, `SampleAgent`, `SandboxShell`, and `Tests`). The two
clusters share no project reference in either direction; `SolutionBoundaryTests` enforces it.

### Adding a command

A command is one class under `Commands/<Group>/`. `CommandReflector` discovers it from its attribute,
so no registration list, group file, or switch statement is edited:

```csharp
[Command("diff", Description = "…", ParentGroup = typeof(AgentGroup), SortPriority = 45)]
public sealed class AgentDiffCommand : CommandBase<AgentDiffRequest, AgentDiffResult>
{
    public override IReadOnlyList<CommandSymbol> Symbols => [ … ];
    protected override string? Validate(ICommandInput input) => …;
    protected override AgentDiffRequest Bind(ICommandInput input) => …;
}
```

| Step | File |
|---|---|
| The command | `Microsoft.Agents.Hosting.Cli.Commands/Commands/<Group>/` — new |
| Its request, result, and service | `Microsoft.Agents.Hosting.Cli.Core/` — new. The result implements `ITextGridRenderable`; the service implements `ICommandHandler<TRequest, TResult>` |
| Register the service | one `AddSingleton` in `Program.cs` |
| Register the result for `--json` | one `[JsonSerializable]` in `CliJsonContext.cs` |

`CompositeHandlerProvider` resolves a handler from the operations facade first and the application
container second, so a service registered as its own `ICommandHandler` needs no entry in
`IAgentHostingOperations`, `AgentHostingOperations`, or `FacadeHandlerProvider`.

An option name must already exist in `OptionCatalog`, which owns its short alias. A name with no entry
throws at startup rather than shipping without one, and a duplicate alias throws too.

Two snapshot assertions pin the user-visible surface and are meant to be updated deliberately: the
group's child set in `CommandSurfaceSnapshotTests`, and its `Valid commands:` usage text. Everything
else is derived from the command tree and covers the new command automatically — help rendering,
option aliases, human rendering, and JSON registration.

### Embeddable Core (PRD R9)

MSAL lives only in `Microsoft.Agents.Hosting.Cli.Auth`; `Microsoft.Agents.Hosting.Cli.Core` names no MSAL, Azure, or console
type, so a future service can re-host the operation facade (`AgentHostingOperations`) without the CLI
shell. This is enforced two ways:

- the host offline-guarantee test (`Microsoft.Agents.Hosting.Cli.Tests`) asserts the `Microsoft.Agents.Hosting.Cli` assembly
  references no `Msal`/`Identity.Client`/`Azure`/`Graph`/`WAM` library, and
- the `Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests` project references Core only and drives `auth status`
  and `abs preview` against injected fakes, asserting the Core assembly references no host or identity
  library and that neither is loaded into the process.

## Developer commands (non-interactive, offline)

All commands below run without live cloud credentials and never open an interactive
authentication, browser, broker, or portal flow.

Run from the repository `src` directory unless noted.

### Build

```pwsh
dotnet build Microsoft.Agents.Hosting.Cli.slnx
```

### Test

```pwsh
# Host + Core unit / contract / command-host tests
dotnet test Microsoft.Agents.Hosting.Cli.Tests/Microsoft.Agents.Hosting.Cli.Tests.csproj

# Embeddability check (Core only, no command host loaded)
dotnet test Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests/Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests.csproj
```

No default test opens a browser, WAM dialog, account chooser, portal, or permission prompt.
Live tests (Windows broker / Azure) are the separate, user-gated evaluation nodes (cN9/cN10) and
are excluded from the default test commands.

### Pack the tool

```pwsh
dotnet pack Microsoft.Agents.Hosting.Cli/Microsoft.Agents.Hosting.Cli.csproj -o ./artifacts
```

This produces a framework-dependent .NET tool package
(`Microsoft.Agents.Hosting.Cli`).

### Test the package produced by a pull request

Use this developer/agent workflow to test the **actual PR build**, rather than rebuilding locally
or waiting for the nightly drop. `Tool_Readme.md` is shipped with the package and documents tool
usage; this README owns repository build and PR-artifact instructions.

The PR pipeline currently publishes a `.nupkg` as a **build artifact**, not a separate hosted
NuGet feed for each PR. Download that artifact and use the package's directory as a local NuGet
source. Do not assume the package was pushed to the `DPX-Tools-Upstream` feed in `nuget.config`;
that file configures dependency restore, not PR package publication.

#### Find the correct build

In the GitHub PR's checks, open **Agent-Hosting-CLI-pr** to reach its Azure DevOps run. Require a
successful run for the PR revision being tested. Packages may still be copied after a failed build
because the copy step uses `condition: always()`; an available artifact alone is not a passing build.

For agents, the equivalent discovery uses GitHub CLI and Azure CLI's `azure-devops` extension.
The examples below use PowerShell and existing authenticated sessions:

```powershell
$repo = 'microsoft.ghe.com/bic/agents-managed-hosting'
$prNumber = 144 # Replace with the PR being tested.
$prJson = gh pr view $prNumber --repo $repo --json headRefOid,statusCheckRollup
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the PR.' }
$pr = $prJson | ConvertFrom-Json
$checks = @($pr.statusCheckRollup | Where-Object { $_.name -eq 'Agent-Hosting-CLI-pr' })
if ($checks.Count -ne 1 -or $checks[0].conclusion -ne 'SUCCESS') {
    throw 'Find a successful Agent-Hosting-CLI-pr check for the requested revision.'
}
$match = [regex]::Match($checks[0].detailsUrl, '[?&]buildId=(\d+)')
if (-not $match.Success) { throw 'The check link does not contain a build ID.' }
$runId = $match.Groups[1].Value
$org = 'https://dev.azure.com/dynamicscrm'
$project = 'b276c3e1-2902-46bd-a686-484157b97f48' # OneCRM
$buildJson = az pipelines build show --id $runId --organization $org --project $project --output json
if ($LASTEXITCODE -ne 0) { throw 'Unable to read the linked build.' }
$build = $buildJson | ConvertFrom-Json
if ($build.status -ne 'completed' -or $build.result -ne 'succeeded' -or
    $build.triggerInfo.'pr.number' -ne "$prNumber" -or
    $build.triggerInfo.'pr.sourceSha' -ne $pr.headRefOid) {
    throw 'The build is unsuccessful or does not match this PR head.'
}
az pipelines runs artifact list --run-id $runId --organization $org --project $project `
    --query '[].{name:name,type:resource.type}' --output table
if ($LASTEXITCODE -ne 0) { throw 'Unable to list build artifacts.' }
```

The build normally compiles `refs/pull/<number>/merge`. Consequently, `sourceVersion` and the
package's Git suffix can differ from the PR's head SHA. Compare `triggerInfo.'pr.sourceSha'` to
`headRefOid`, and record both the PR head and the build's merge SHA. If metadata is absent, the
check is missing, or several candidate runs remain, inspect the check/checkout details rather
than silently selecting an older successful run. PR triggers currently include only `src/**`;
not every documentation-only PR necessarily produces a package.

#### Download and identify the package

Select **Published artifacts** on that run, then `drop_Build_Assets_Debug` and its `packagesDebug`
folder. Download the `Microsoft.Agents.Hosting.Cli.*.nupkg`, not a coverage, SDL, or signing-log artifact.
The artifact name and folder are the PR pipeline's expected output layout, defined by
`.azdo/OneBranch.PullRequest.yml`. Listing artifacts confirms that this output is available for
the selected run; it is not a search for a different artifact name on each build. The CLI package
filename/version inside that folder varies by build, so discover the package and read its
manifest rather than hard-coding the example package version below. If the expected artifact
is absent, stop and inspect the run instead of substituting another artifact.

The command-line alternative downloads the same artifact into a new scratch directory:

```powershell
$work = Join-Path ([IO.Path]::GetTempPath()) "ah-pr-$prNumber-$runId-$([guid]::NewGuid().ToString('N'))"
$download = Join-Path $work 'download'
$toolPath = Join-Path $work 'tool'
New-Item -ItemType Directory -Path $work | Out-Null
az pipelines runs artifact download --run-id $runId --artifact-name drop_Build_Assets_Debug `
    --organization $org --project $project --path $download
if ($LASTEXITCODE -ne 0) { throw 'Artifact download failed.' }
$packages = @(Get-ChildItem -LiteralPath $download -Recurse -File -Filter 'Microsoft.Agents.Hosting.Cli.*.nupkg')
if ($packages.Count -ne 1) { throw 'Expected exactly one Microsoft.Agents.Hosting.Cli tool package; inspect the artifact.' }
$package = $packages[0]
Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256
```

Read the package's `.nuspec` to obtain its **exact** ID and version. Use that version, not a
locally calculated version, `latest`, or a guessed nightly version:

```powershell
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $specs = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })
    if ($specs.Count -ne 1) { throw 'Expected one package manifest.' }
    $reader = [IO.StreamReader]::new($specs[0].Open())
    try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $id = $spec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']").InnerText
    $version = $spec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']").InnerText
    if ($id -ne 'Microsoft.Agents.Hosting.Cli' -or [string]::IsNullOrWhiteSpace($version)) {
        throw 'Unexpected tool package identity.'
    }
} finally {
    $archive.Dispose()
}
```

For example, [PR #144](https://microsoft.ghe.com/bic/agents-managed-hosting/pull/144)'s successful
[run 22417715](https://dev.azure.com/dynamicscrm/b276c3e1-2902-46bd-a686-484157b97f48/_build/results?buildId=22417715)
built PR head `1eed0d8fd01445524bf0c4f2fca75fcdf7ba0682` as merge commit
`4bf47132159cafe1740f5e27eaa5acd69eff258d`. Its package-copy log records
`packagesDebug\Microsoft.Agents.Hosting.Cli.0.1.43-beta-g4bf4713215.nupkg` in `drop_Build_Assets_Debug`.
This is a provenance example, not a permanent download/version pin; retained artifacts can expire.

#### Install side by side and run bounded checks

Review the PR before executing its package. Use the SDK/runtime required by that revision
(`global.json`; currently .NET 10), a fresh tool path, and a fresh package cache. With .NET 10,
`--source` replaces configured package sources; `--add-source` only adds another source and could
select a package from an unrelated feed. The following installation uses only the downloaded
package folder and does not replace the global tool or change a repository tool manifest:

```powershell
$savedPackages = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = Join-Path $work 'nuget-cache'
    dotnet tool install Microsoft.Agents.Hosting.Cli --version $version --tool-path $toolPath `
        --source $package.DirectoryName --no-http-cache
    if ($LASTEXITCODE -ne 0) { throw 'Isolated tool installation failed; inspect the failure before adding sources.' }
} finally {
    $env:NUGET_PACKAGES = $savedPackages
}
```

Invoke the installed executable by its **full path**, never a bare global `msagent`. Keep configuration
isolated and clear ambient feature overrides when checking shipped defaults:

```powershell
$msagent = Join-Path $toolPath 'msagent.exe' # Use 'msagent' on Linux/macOS.
$savedConfig = $env:AH_CLI_CONFIG_DIR
$savedFeatures = $env:AH_CLI_FEATURES
try {
    $env:AH_CLI_CONFIG_DIR = Join-Path $work 'config'
    $env:AH_CLI_FEATURES = $null
    & $msagent --version
    if ($LASTEXITCODE -ne 0) { throw 'Version check failed.' }
    & $msagent --help
    if ($LASTEXITCODE -ne 0) { throw 'Help check failed.' }
} finally {
    $env:AH_CLI_CONFIG_DIR = $savedConfig
    $env:AH_CLI_FEATURES = $savedFeatures
}
```

Compare the reported version/commit with the artifact metadata, and check the behavior relevant to
the PR. For feature-control builds, root help should omit `abs` and `identity` with overrides unset;
test deliberate overrides in a separate process scope. Help/version are smoke checks, not evidence
that live MCS/AMS/ABS workflows work. Record the PR/head, build URL/merge SHA, artifact, package
ID/version/hash, exact commands, exit codes, and outcome. Keep logs free of credentials. Remove
the isolated installation and scratch files when finished, as described below.

#### Clean up after testing

After testing, stop any processes you started from the isolated installation and save any results
you want to retain outside `$work`. Using the same `$toolPath` and `$work` values from this run,
uninstall the isolated tool and then remove its scratch directory:

```powershell
dotnet tool uninstall Microsoft.Agents.Hosting.Cli --tool-path $toolPath
if ($LASTEXITCODE -ne 0) { throw 'Isolated tool uninstall failed; inspect the failure before cleanup.' }

Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction Stop
```

Confirm that `$work` is this run's unique `ah-pr-...` directory before deleting it. This removes
the downloaded artifact, isolated package cache, configuration, and remaining tool files.
Do not use `--global`, delete a repository tool manifest, or clear shared caches; leave any
existing global installation untouched. If installation never succeeded, skip the uninstall
command and remove only this run's scratch directory after saving any failure evidence.

**Permission boundary:** discovering/downloading an artifact does not authorize authentication,
cloud changes, or arbitrary execution of PR code. Use existing access to `microsoft.ghe.com/bic`
and Azure DevOps `dynamicscrm/OneCRM`. If a tool is missing or access fails, stop and report the
blocker. Before installing helpers or opening login, feed-authentication, browser, account,
permission, or other interactive prompts, obtain approval with the exact command, purpose,
expected prompt, and the user's work account that has access to the repository/build. Never
request or record tokens in these instructions. Run non-interactive checks first; live-service
tests and tool sign-in require separate approval. Do not silently enable `--interactive`, add
remote feeds, replace the global tool, rebuild a missing artifact locally, or start a new CI run.

### Install and invoke a locally built package

Install into an isolated tool path from the locally produced package (no public feed):

```pwsh
dotnet tool install Microsoft.Agents.Hosting.Cli `
  --tool-path ./artifacts/tool `
  --add-source ./artifacts

./artifacts/tool/msagent --version
./artifacts/tool/msagent --help
./artifacts/tool/msagent auth status        # reports signed-out without prompting
```

`msagent auth status` reports the current sign-in state without acquiring a token or opening identity UI.
`msagent abs preview` plans against Azure reads only; against an unconfigured environment it fails cleanly
without mutating anything.

## Side-by-side comparison with the shipped TypeScript CLI

The two proposals are compared **offline** through the neutral Azure Bot Service conformance fixtures,
not by running the live CLIs against each other. Each of the 10 fixtures in
`src/AgentHostCli.Conformance/fixtures/` is exercised by two independent runners that consume the same
goldens:

```pwsh
# TypeScript (pinned, read-only) — requires bun + the pinned worktree's node_modules; offline
cd src/AgentHostCli.Conformance/ts-oracle
bun runOracle.ts                 # expect: "RESULT: all 10 fixtures passed."

# C# demonstrator — offline
cd ../../..
dotnet test src/Microsoft.Agents.Hosting.Cli.Tests/Microsoft.Agents.Hosting.Cli.Tests.csproj --filter ContractTests
```

The pinned TypeScript revision (repo `bic/agents-managed-hosting`, branch `ah-start`, commit
`5624d54`) is imported **read-only** and never modified. See
`src/AgentHostCli.Conformance/README.md` for the full closure argument, normalization rules, and the
known intended C#-vs-TS contract deltas.

Live, credentialed side-by-side invocation (real sign-in, real ARM apply) is **user-gated** and is
performed only in the separate evaluation nodes cN9 (live auth) and cN10 (live ABS); it is never part
of the default build/test/pack flow.

## Configuration (host environment)

The host resolves these from the process environment at its composition root; Core never reads the
environment itself:

| Variable | Purpose |
|---|---|
| `AH_CLI_CLIENT_ID` | AgentHost public-client application id. Sign-in always uses the current platform's broker reply URL on that app (Windows `ms-appx-web://microsoft.aad.brokerplugin/{clientId}`, macOS `msauth.com.msauth.unsignedapp://auth`, Linux `https://login.microsoftonline.com/common/oauth2/nativeclient`). |
| `AH_CLI_GRAPH_CLIENT_ID` | Consent-capable public client used to acquire Microsoft Graph tokens for identity provisioning. Defaults to the Microsoft Graph Command Line Tools client. Deliberately separate from `AH_CLI_CLIENT_ID`: Graph consent must not be requested against the CLI first-party app. |
| `AH_CLI_GRAPH_REDIRECT_URI` | Redirect URI for the Graph consent client. |
| `AH_CLI_ARM_CLIENT_ID` | ARM counterpart of `AH_CLI_GRAPH_CLIENT_ID`, used for managed-identity provisioning. Defaults to the well-known Azure CLI public client. |
| `AH_CLI_ARM_REDIRECT_URI` | Redirect URI for the ARM consent client. |
| `AH_CLI_CONFIG_DIR` | Override the profile/config directory (defaults under `LocalApplicationData`). Shared by every ring; only the ring-bound files inside it are suffixed. |
| `AH_CLI_ALLOW_UNPROTECTED_CACHE` | Allow an unprotected token cache when a protected store is unavailable. |
| `AH_CLI_FEATURES` | Semicolon-separated, case-insensitive feature names to enable over shipped defaults. Supported: `AmsFeatures`, `AzureBotServiceFeatures`, `IdentityFeatures`. |
| `AH_CLI_TELEMETRY_OPTOUT` | Overrides the remembered telemetry preference. `1`/`true` opts out, `0`/`false` opts in; unset defers to `msagent telemetry`. |
| `AH_CLI_AMS_ENDPOINT` | Agent Management Service **control plane** endpoint, used by deploy. Absolute HTTPS; deployment-configured, not settable per command. |
| `AH_CLI_AMSDP_ENDPOINT` | Agent Management Service **data plane** endpoint, used by package upload and by `agent create` as a direct Dev-island override. Absolute HTTPS. |
| `NO_COLOR` | Disable colored output (same effect as `--no-color`). |

The CLI supports TLS 1.3 and TLS 1.2.

## Selecting a deployment ring

`msagent auth login --cloud` records which Power Platform API ring the sign-in targets. The ring is a
property of the account, not a per-command argument, so the selected sign-in and the ring it
addresses cannot disagree:

```pwsh
msagent auth login --cloud preprod     # sign in, targeting preprod
msagent auth switch --cloud test       # retarget the active account; no sign-in
msagent auth status                    # shows the ring in use
```

| Ring | Global endpoint | Environment selection file |
|---|---|---|
| `prod` | `https://api.powerplatform.com` | `environment-selection-csharp.json` |
| `preprod` | `https://api.preprod.powerplatform.com` | `environment-selection-csharp-preprod.json` |
| `test` | `https://api.test.powerplatform.com` | `environment-selection-csharp-test.json` |

An account signs in once and keeps its ring until `auth switch --cloud` changes it; omitting
`--cloud` on a later `auth login` preserves the ring already recorded. Signing a new account in
inherits the ring the session is already on, so a login during a preprod session produces a preprod
account rather than one no command in that session could use; the very first sign-in, with nothing
active, defaults to `prod`. An unrecognized value is a usage error (exit 2) naming the accepted rings.

The ring is resolved once per sign-in and used both for the token request and for the stored profile,
so the audience a credential was minted for and the cloud the account is recorded against cannot
disagree.

`auth login --cloud` also asks for *that ring's* audience, so a ring whose resource needs consent or
resource-specific Conditional Access can be satisfied by signing in for it. `auth switch --cloud`
changes the account's ring without signing in, so the clients this process already built still
address the ring it started in: `env list`/`env select` refuse rather than use the old one, and
`auth status` reports the new ring without the selection it left behind. Running the next command
rebuilds against the new ring.

## The environment selection follows the account

The selected environment belongs to the account that selected it, in the cloud it was selected for.
`auth login` and `auth switch` therefore discard every stored selection whenever the now-active
account or cloud is not the one that was active before, and say so, rather than leaving `auth status`
reporting a target the operator did not choose:

```pwsh
msagent env select fa5c1b48-…        # selected by op@contoso.com
msagent auth login                   # signs in other@contoso.com
# Signed in as other@contoso.com (prod).
# Cleared the selected environment because the active account or cloud changed. Run `msagent env select` to choose one.
```

Signing the same account in again, against the same cloud, keeps the selection. Any other transition
— a different account (in this tenant or another), a different cloud, or a sign-in after `auth
logout` — discards it. Because selections are stored per cloud, every cloud's selection is discarded,
so switching back cannot resurrect one. A selection that cannot be read at all is discarded too, so a
damaged selection file cannot block a sign-in.

## Feature controls

`featureflags.json` is copied beside the CLI assembly and packed with the tool. Values are `on` or
`off` (case-insensitive); missing keys and every value other than `on` are disabled. As in PAC,
`AH_CLI_FEATURES` is an enable-only override: listing a feature turns it on, while unlisted features
retain their file default. It does not turn a shipped-on feature off. Unknown list entries are
ignored. Multiple
`[RequiredFeatureFlag]` attributes are ANDed, and a disabled parent group removes all descendants
before help, parsing, and dispatch. Feature-controlled options are likewise removed from help and
parsing, while mixed commands also enforce the resolved route immediately before handler dispatch.
Feature controls are rollout controls, not authorization or security boundaries.

A missing `featureflags.json` stops startup with an installation error naming the expected
file and directing the user to uninstall and reinstall the `Microsoft.Agents.Hosting.Cli` tool.
Environment overrides do not replace the required file.

Shipped defaults are:

| Feature | Default | Controlled surface |
|---|---:|---|
| `AmsFeatures` | `off` | CodeAgent creation, AMS package upload, and AMS package-revision deployment. |
| `AzureBotServiceFeatures` | `off` | The `abs` group and CodeAgent deployment routes backed by Azure Bot Service. |
| `IdentityFeatures` | `off` | The user-facing `identity` command group. |
| `EvaluationFeatures` | `on` | The `agent eval` group and its `testset` subgroup. |

The selected AMS, ABS, and identity surfaces ship off independently. Enable one or combine several
for a process:

```pwsh
$env:AH_CLI_FEATURES = "AmsFeatures"
$env:AH_CLI_FEATURES = "AzureBotServiceFeatures"
$env:AH_CLI_FEATURES = "IdentityFeatures"
$env:AH_CLI_FEATURES = "AmsFeatures;AzureBotServiceFeatures;IdentityFeatures"
```

Disabled pure groups and route-only options are removed from help and parsing. Dispatch guards also
prevent a stored CodeAgent record from bypassing a disabled route through a mixed command. A bare
`agent create` retains its historical CodeAgent selection and therefore reports that AMS is disabled
with guidance to use `--agent-type MCSAgent`; it is not silently rerouted.

The source-backed boundary is:

| Control | Commands/routes | Source and dispatched contracts |
|---|---|---|
| Always-on MCS/local project surface | `agent init/list/show/pull/delete/share/share show/unshare`; `agent create --agent-type MCSAgent`; the MCS route of `agent deploy`; `deployment list`; MCS routes of `deployment create/show/update/delete`; `deployment connection list/add/update/remove/connectors`; `deployment channel create/update/delete/show/list` | `Commands/Agent/*` and `Commands/Deployment/*`; `ManagedAgent*Request`, `AgentDeployment*Request`, deployment channel requests, and matching result contracts |
| `AmsFeatures` | `agent package upload`; default/CodeAgent route of `agent create`; AMS package-revision route of `agent deploy` | `AgentPackageUploadCommand.cs` → `AgentPackageUploadRequest`; `AgentCreateCommand.cs` → `AgentCreateRequest`; `AgentDeployCommand.cs` → `AgentDeployRequest` |
| `AzureBotServiceFeatures` | `abs preview/apply`; resolved CodeAgent routes of `deployment create/show/update/delete` | `Groups/AbsGroup.cs` and `Commands/Abs/AbsCommands.cs` → `AbsPreviewRequest`/`AbsApplyRequest`; mixed deployment command files → `DeploymentCreateRequest`, `DeploymentRequest`, or `DeploymentUpdateRequest` |
| `IdentityFeatures` | `identity create blueprint/app` | `Groups/IdentityGroup.cs` and `Commands/Identity/*` → `BlueprintIdentityRequest` or `AppRegistrationIdentityRequest` |
| `EvaluationFeatures` | `agent eval run/list/show/cancel/delete/generate`; `agent eval testset list/show/create/update/export/delete` | `Groups/AgentGroup.cs` (`AgentEvalGroup`, `AgentEvalTestSetGroup`) and `Commands/Agent/Eval/*` → `Evaluation*Request` and matching result contracts |
| Always-on shared prerequisites | `auth`, `env`, profile/configuration storage, common agent/deployment selectors | `Groups/AuthGroup.cs`, `Groups/EnvGroup.cs`, `Program.ResolveConfigDirectory`, and Core auth/environment/profile services |

`IdentityFeatures` gates only the user-facing `identity` metadata subtree. Identity, authentication,
environment, and configuration services remain registered because MCS operations can depend on that
infrastructure. Likewise, the mixed deployment commands remain present for MCS; only their
ABS-specific symbols and resolved CodeAgent dispatch paths are controlled.

### Adding command or group controls

Feature inclusion is metadata-driven, not maintained in a central command-path list:

1. Add the feature name to `Features/FeatureName.cs`.
2. Add its shipped `on` or `off` value to `featureflags.json`.
3. Attach `[RequiredFeatureFlag(FeatureName.YourFeature)]` to any `[Command]` leaf or
   `[CommandGroup]` class.

Actual AH declarations use the same annotation at both levels:

```csharp
[CommandGroup("identity", Description = "Create and manage agent identities.")]
[RequiredFeatureFlag(FeatureName.IdentityFeatures)]
public sealed class IdentityGroup;

[Command("upload", Description = "Upload exact package bytes.",
    ParentGroup = typeof(AgentPackageGroup))]
[RequiredFeatureFlag(FeatureName.AmsFeatures)]
public sealed class AgentPackageUploadCommand
    : CommandBase<AgentPackageUploadRequest, AgentPackageUploadResult>
{
    // Normal command implementation.
}
```

`CommandReflector` reads the requirements from normal AH command metadata and recursively filters the
tree. A leaf can be controlled independently of its enabled siblings. Requirements on an ancestor
and leaf compose: an enabled leaf never bypasses a disabled group. Multiple annotations on the same
type are ANDed. Filtering happens before System.CommandLine materialises help, parser symbols, and
option aliases, so direct invocation and short aliases cannot recover a disabled command.

Shared commands whose individual routes need different controls cannot be removed as one metadata
node. For those commands, put `RequiredFeatures` on route-only `CommandSymbol` records and call
`FeatureGate.RequireEnabled` after route resolution but before handler dispatch. This is the
operation-level complement to command metadata, not a replacement or a path-specific filter table.

AH has no local CodeAgent build command: `agent package upload` uploads exact existing bytes and does
not build or inspect them. Local build remains an external workflow; the CLI's local MCS project
commands remain available without enabling AMS, ABS, or identity.

### PAC source mapping and intentional differences

The feature-control implementation is a source-level port, with only AH naming, command-model, and
release-boundary adaptations:

| PAC source/symbol | AH source/symbol | Preserved behavior | Adaptation |
|---|---|---|---|
| `src/featureflags.json` | `Microsoft.Agents.Hosting.Cli/featureflags.json` | Assembly-adjacent JSON object containing `on`/`off` values | AH feature names and the three user-selected default-off surfaces replace PAC's feature inventory. |
| `bolt.system/IFeatureFlags.cs` / `IFeatureFlags.IsFeatureEnabled` | `Microsoft.Agents.Hosting.Cli.Commands/Features/IFeatureFlags.cs` / same method | One synchronous enum-based query | Namespace and enum values only. |
| `bolt.system/FeatureFlags.cs` / `LoadFromAssemblyFolder`, `LoadFromFile` | `Microsoft.Agents.Hosting.Cli/Features/FeatureFlags.cs` / same methods | Case-insensitive keys; only `on` enables; absent keys disable; defaults load beside the assembly; missing/malformed files fail | The override variable is `AH_CLI_FEATURES` instead of `PAC_CLI_FEATURES`. The missing-file diagnostic identifies an installation error and directs the user to uninstall and reinstall the tool. |
| `bolt.system/FeatureFlags.cs` / `IsEnabledByEnvironment` | `Microsoft.Agents.Hosting.Cli/Features/FeatureFlags.cs` / same helper | Reads the environment at evaluation time; splits on semicolons; trims entries; compares case-insensitively; entries only enable and otherwise fall back to JSON | Environment-variable prefix only. |
| `bolt.cli/CommandLine/RequiredFeatureFlagAttribute.cs` | `Microsoft.Agents.Hosting.Cli.Commands/Features/RequiredFeatureFlagAttribute.cs` | Class/property target, multiple attributes, AND semantics, and disabled-ancestor dominance | AH applies class annotations to its native `[CommandGroup]` and `[Command]` types. |
| `bolt.cli/CommandLine/ModuleReflector.cs` / `ParseRequiredFeatureFlags`, `AreRequiredFeaturesSatisfied`, `ConvertToVerbDefinitions` | `Microsoft.Agents.Hosting.Cli.Commands/Model/CommandReflector.cs` / `RequiredFeatures`, `Enabled`, `Filter` | Requirements are captured in cached metadata, all requirements must pass, and recursive conversion omits disabled nouns/verbs and their descendants | Filtering occurs before System.CommandLine materialization. AH options are `CommandSymbol` records rather than reflected properties, so their equivalent requirement metadata lives on the record. |
| `bolt/Session.cs` / `FeatureFlags.LoadFromAssemblyFolder` | `Microsoft.Agents.Hosting.Cli/Program.cs` / same load call and `AgentHostCli` composition | One loaded feature service is shared by discovery and invocation | AH uses constructor/service-provider composition rather than PAC's session/MEF composition. |

`FeatureGate.RequireEnabled` is the operation-level adapter for an AH command that resolves to
different backends only after parsing or lookup. It repeats the same `IFeatureFlags` evaluation
immediately before dispatch; it does not define defaults or maintain a separate path list.

NuGet update checking is deferred and no update command, startup probe, transport, cache, or update
feature flag ships in AH. PAC's last merged active checker at `897d1a617` used a seven-day background
`nuget.exe` probe. Merged PR 6118383 (`f0f41d8ea`) removed that path, and current PAC `master` does not
invoke an update check. A later bounded implementation at `413fbbc0c` on
`users/rishjain/dotnet_version_check` was not merged, so it is not used as a source baseline.
