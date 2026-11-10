# Manual testing and records

Follow [CONTRIBUTING.md](../../CONTRIBUTING.md) for offline-test requirements and
approval boundaries. This guide describes current live checks and how to record
their evidence. It does not certify a platform or carry forward undated results
from older implementations.

## Approval and setup

Live runs require explicit approval for the intended account, tenant,
environment, agent, deployment, and effects. Use disposable agents and harmless
queries. Sign-in, provisioning, deployment, evaluation, and live testing are not
part of the offline test suite; they can create/delete resources, perform agent
actions, or incur charges. Enabling a feature does not grant service access or
approval.

For the executable E2E harness, use its
[setup and safety guide](../../src/Microsoft.Agents.Hosting.Cli.E2E/README.md).
Record the exact build and package being exercised. Installing or executing a
PR package requires review and approval; a downloadable artifact alone is not
authorization to run it. Do not install helpers, open authentication prompts,
or enable shipped-off features without the relevant approval.

## Live agent checks

| Command | Purpose |
|---|---|
| `msagent test ask --query <text>` | Send one live query to an explicitly selected MCS agent. |
| `msagent test playground [--target directline\|directengine]` | Launch the M365 Agents Playground in its own terminal. |

### Live agent test (controlled preview)

`LiveTestFeatures` ships off. For an approved test, opt in for that process with
`AH_CLI_FEATURES=LiveTestFeatures`; do not change shipped defaults. Select the
intended account with `msagent auth login`, and configure `AH_CLI_CLIENT_ID` for
an authorized public/native client with public-client redirect flow and
delegated Power Platform `CopilotStudio.Copilots.Invoke` consent. The selected
standard or GitHub Copilot Harness agent must be published, available in the
specified environment, and shared with that account. Public cloud is supported;
CodeAgent/AHRG is not.

```text
msagent test ask --query "Explain this policy" --project <project> --agent-name <agent> --deployment-name <deployment>
msagent test ask --query "Hello" --schema-name <schema> --environment-id <environment> --tenant <tenant> --deployment-name <selected-label> --json
msagent test ask --query "Clarify" --conversation-id <returned-id> --agent-id <agent-id> --deployment-id <deployment-id>
```

The client uses the discovery service to resolve runtime endpoints from the
environment and schema name. Discovery failure stops the invocation, with no
guessed or fallback runtime.

The configured route reads `.config/agent.config.json` and requires an explicit
deployment or exactly one marked default. A single deployment is not
automatically a default. It never substitutes the home/current environment.
Selection is validated before authentication, discovery, or an SDK request.
The fully explicit route needs schema, environment, tenant, and a deployment
selector; that selector is a label, not a verified registry association.

An omitted conversation ID starts a new conversation. Reusing an ID must be
explicit. `--json` writes one incrementally flushed document with full ordered
activities and final status; `--plain` writes accessible final-only human output.
They cannot be combined. JSON does not prohibit interactive sign-in;
`--non-interactive` does.

Streamed responses are parsed and rendered incrementally. A truncated,
malformed, or interrupted stream must fail with a sanitized error. Whole-JSON
responses are read completely before the first activity. Offline streaming
tests are not evidence of live early delivery.

Queries can enter shell history. Effects are **not a dry run**, are not rolled
back, and may continue remotely after local cancellation. There is no automatic
query replay; check the destination before retrying. `completed` means response
enumeration and output ended normally, not that the answer or remote action
succeeded. The CLI does not save queries, activities, or conversation IDs;
terminal capture and service retention are separate. Do not execute returned
URLs or attachments as trusted input.

### M365 Agents Playground test (MVP)

`msagent test playground` launches the M365 Agents Playground in a new terminal window
for the same selected agent as `test ask`. The calling terminal returns after
the child confirms process startup; the new window owns the session.
The target is `directengine` (the default) or `directline`.
Wherever `--tenant` is accepted, `--tenant-id` and `-t` are equivalent aliases.
DirectEngine uses the same agent selectors as `test ask` and the same resolver.
Neither mode accepts `--query` or `--conversation-id`.
The command is gated by `LiveTestFeatures`, with no separate playground flag.

```text
msagent test playground --project <project> --agent-name <agent> --deployment-name <deployment>
msagent test playground --schema-name <schema> --environment-id <environment> --tenant <tenant> --deployment-name <selected-label>
msagent test playground --target directline
```

For DirectEngine, the CLI uses the selected account and delegated invoke
permission to query the discovery service without starting a conversation.
It passes the discovered endpoint and public-client configuration to the
package. Discovery failure prevents launch. The package owns authentication in
the new window; the CLI does not hand it tokens or secrets.
The command reports the selected executable path and version.
Readiness is established immediately when the process starts.
The package owns authentication timing; process start is not proof of
authentication or a connected conversation.

The process receives a scrubbed Playground environment, including the selected
`PATH` and TLS certificate settings. Child-owned disposable configuration paths
take precedence over caller overrides.

#### Direct Line connection

`msagent test playground --target directline` requires interactive input and
output. It rejects `--non-interactive`, including with `--install-playground`.
It rejects DirectEngine-only project, agent, schema, deployment, environment,
and tenant selectors. It does not resolve local project/deployment state, sign
the CLI into Entra ID, or load its public-client configuration.

Publish the disposable agent with **no authentication** and enable its global
public-cloud Direct Line connectivity. At the native **masked terminal prompt**,
enter a complete HTTPS token endpoint from **Channels > Mobile app**, or a raw
Direct Line secret from **Settings > Security > Web channel security**.
Never paste credentials into chat, command arguments, environment variables,
or configuration files. Enter them in the new window, not the calling terminal.
Empty Enter, Escape, or Ctrl+C cancels setup.

The native package owns credential validation and connection lifecycle. The
CLI does not acquire, save, or log the credential. It uses an isolated disposable
working directory without inherited Playground configuration. The listener stays
on `127.0.0.1`, native telemetry is disabled, and connection/recording overrides
are removed only from the child environment.

Use Node.js 22.17.1+ LTS. Regional/custom/sovereign Direct Line endpoints,
OAuth-card sign-in, file upload, headless interaction, and saved connections
are unsupported. Use DirectEngine for agents requiring Microsoft sign-in.
Wait for Connected and an enabled composer; process start is insufficient.

#### Playground installation and options

The command uses `agentsplayground` on `PATH`, backed by the global
`@microsoft/m365agentsplayground` package, version `0.2.29` or newer.
A working installed tool does not trigger an npm probe. For a missing or old
tool, interactive use offers
`npm install -g @microsoft/m365agentsplayground@latest`. This runs lifecycle scripts
that may execute code, so consent is required.
`--non-interactive` refuses that offer unless `--install-playground` is supplied.
Before offering installation, the CLI checks for Node.js 22.17.1 or newer and npm.
Existing installations receive a Node.js check without requiring npm.
Prerequisite failures are reported before a new window opens. Windows probes
and installation do not open additional console windows.

The CLI never installs or upgrades Node.js or npm. Install them from
<https://nodejs.org/en/download>, open a new terminal to refresh `PATH`, and retry.
Use a user-owned npm prefix or version manager if a global prefix needs
elevation. The user's registry, proxy, and authentication settings stay intact.

`--plain` suppresses CLI transient progress, not the package's new-window output.
DirectEngine authentication may need user action even with `--non-interactive`;
Direct Line rejects that flag. `--json` is rejected because structured Playground
streaming is unsupported. There is no managed install store, automatic upgrade,
retry, or transcript feature.

#### Lifetime, effects, and limits

Live effects are not rolled back. Use Ctrl+C in the Playground window to stop
the session. Closing the calling terminal after startup does not stop it.
The child CLI owns cleanup and removal of the disposable invocation directory.
On Ctrl+C or normal exit it requests graceful shutdown, waits up to 10 seconds,
then kills only its child process tree and allows another 10 seconds for cleanup.
It never kills by process name or port; the shared browser must survive.
An abrupt child CLI crash or forced window closure can orphan the server, a
known MVP limitation.

Version probes time out after 10 seconds, installation after 600 seconds, and
terminal startup/process-start handoff after 30 seconds. These are not
authentication or conversation-readiness deadlines. Later Playground exit codes
are reported only in its own terminal.
After unexpected failure, that terminal retains output and asks for Enter to
close; Ctrl+C also exits. Successful exits and intentional cancellations do not
pause. Redirected input/output never waits for acknowledgment.

A local desktop session is required. Windows opens a console window;
macOS opens Terminal and may request automation permission. POSIX validation
must use a real desktop: Linux requires GNOME Terminal, Konsole, Xfce Terminal,
xterm, or `x-terminal-emulator` on `PATH`, or fails explicitly. Linux launches
the child directly without a shell script. SSH/headless operation is unsupported.

#### Manual validation matrix

Each row needs a revision-specific record. An unrecorded check is pending, not
passed. Do not carry pre-discovery or older-package results forward as validation
of the current launch path.

| Area | Check |
|---|---|
| DirectEngine | Exercise discovery for standard and GitHub Copilot agents, connected state, two harmless replies, and discovery failure without fallback. |
| Direct Line | Verify masked entry, cancellation, credential rejection, connection, and harmless replies; retain no credentials. |
| Tool resolution | Cover current, missing, and old packages; installation consent/refusal; Node/npm failures; registry/proxy settings; permission and network failures. |
| Feature and output | Verify default-off behavior, approved opt-in, JSON rejection, plain output, and live-action warnings. |
| Terminal lifecycle | On each supported desktop OS, check input/output, survival after caller closure, startup timeout, failure acknowledgment, Ctrl+C, browser survival, and owned-process cleanup. |
| Streaming query | Verify early activity delivery, final status, malformed/truncated response, caller cancellation, sanitized errors, and no automatic replay. |

## Manual test records

Retain only records that identify the revision, environment, and checked
behavior. Earlier undated spike notes and unattributed pass statements are not
current validation evidence. No current-revision live pass is asserted by this
document.

For each approved run, record:

| Field | Required information |
|---|---|
| Build | Repository revision, PR/build URL when applicable, package ID/version/hash, and Playground version if used. |
| Platform | OS, desktop terminal, .NET runtime, and Node/npm versions where relevant. |
| Scope | Approval reference, scenario, and non-sensitive target labels; keep tenant/account identifiers out of committed records. |
| Execution | Exact sanitized commands, relevant feature overrides, exit codes, and the specific matrix checks performed. |
| Outcome | Observed result, cleanup/browser-survival result, limitations, and pending checks. |

Do not commit credentials, token caches, configuration directories, raw
conversations, or secret-bearing logs. Keep private evidence outside tracked
files and link only to appropriately controlled, sanitized records.
