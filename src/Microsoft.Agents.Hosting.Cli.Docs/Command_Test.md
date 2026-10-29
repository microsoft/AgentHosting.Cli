# Live agent testing

[Command reference](ReadMe.md)

The `msagent test` group is a controlled preview, gated by `LiveTestFeatures` and off by default.
Enable it through the [feature controls](../Microsoft.Agents.Hosting.Cli/README.md#feature-controls).
Enabling a command does not grant access to an agent or approve use of a preview.

These commands perform **live operations**, not dry runs. Remote actions are not rolled back
and may continue after local cancellation. Check remote effects before retrying a failed query.
The removed `--agent-harness` option is not accepted; endpoint discovery selects the runtime.

## Target selection

`test ask` and the default DirectEngine playground use the same selection rules.
Only public-cloud MCS targets are supported, not CodeAgent/AHRG targets.

| Option | Alias | Description |
| --- | --- | --- |
| `--project` | `-p` | Project directory containing `.config/agent.config.json`; otherwise search from the current directory. |
| `--agent-id` | `-aid` | Select a managed-agent record by ID. |
| `--agent-name` | `-an` | Select a managed-agent record by name. |
| `--schema-name` | `-sn` | MCS schema name, not a managed-agent ID. Can select a configured agent or assert its schema. |
| `--deployment-id` | `-did` | Select a deployment by ID. |
| `--deployment-name` | `-dn` | Select a deployment by name. |
| `--environment-id` | `-eid` | Destination environment; must match a configured deployment when using local records. |
| `--tenant` | `-t` | Tenant GUID; must match a configured deployment when using local records. |

For a configured project, select an agent and an explicit deployment, or use exactly one
deployment marked as the agent's default. A single non-default deployment is **not** selected
automatically. A deployment selector can identify its owning agent without a separate agent
selector; multiple or inconsistent matches are rejected. The selected deployment must be enabled,
not quarantined, and an MCS deployment consistent with its owning agent.

The destination comes from the **deployment**, not the agent's home environment or `env select`.
Additional schema, environment, and tenant options are assertions, not overrides.

The fully explicit route requires `--schema-name`, `--environment-id`, `--tenant`, and either
`--deployment-id` or `--deployment-name`, with no project or managed-agent selector. This route
does not require a local configuration file. The deployment selector is a label for the
selection, not a verified association with a local deployment record.

DirectEngine uses the selected CLI account and an authorized public/native client configuration
with delegated `CopilotStudio.Copilots.Invoke` consent. The agent must be published and shared
with that account. Configure the client as described in the
[live-test setup](../Microsoft.Agents.Hosting.Cli/README.md#live-agent-test-controlled-preview).
Endpoint discovery failure stops the command; it does not guess a fallback endpoint.

## `msagent test ask`

Send exactly one literal query.

```text
msagent test ask --query "Explain this policy" --project <project> --agent-name <agent> --deployment-name <deployment>
msagent test ask -q "Hello" --schema-name <schema> --environment-id <environment> --tenant <tenant-guid> --deployment-name <label> --json
msagent test ask -q "Clarify" --agent-id <agent-id> --deployment-id <deployment-id> --conversation-id <returned-id>
```

In addition to the [target selectors](#target-selection) and
[global options](ReadMe.md#global-options):

| Option | Alias | Default | Description |
| --- | --- | --- | --- |
| `--query` | `-q` | Required | One nonblank query. Its text can enter shell history. |
| `--conversation-id` | `-cvid` | New conversation | Explicitly continue a conversation using an ID returned for this destination. No last-used ID is saved. |
| `--plain` | `-pl` | `false` | Accessible final-only human output without transient progress. Cannot be combined with `--json`. |

`--json` does not prohibit interactive sign-in; use `--non-interactive` to prevent the CLI's
interactive sign-in. The CLI does not save queries, activities, or conversation IDs, but terminal
capture, shell history, and service-side retention are outside its control.

### Streaming output

`--json` writes one incrementally flushed document containing `schemaVersion: "1"` and an
ordered `activities` array. Final fields include `conversationId`, `conversationIdSource`,
`targetSource`, `completionBasis`, `status`, `success`, `exitCode`, `queryDelivery`, `partial`,
and `activityCount`. On failure or cancellation, it includes `errorKind`, `errorMessage`,
and `remediation`. This document does not include the standard envelope's numeric `errorCode`;
handled failure codes are still available to command telemetry.

`status` is `completed`, `failed`, or `cancelled`. `completionBasis: "sdk-stream"` means the
SDK response enumeration ended normally, **not** that an answer is correct or a remote action
succeeded. `partial: true` means activities were written before a failure or cancellation.
`queryDelivery` is `not-attempted` or `attempted`; an attempted query may already have caused
remote effects.

Streamed responses are rendered activity by activity. Whole-JSON responses are read before the
first activity is rendered. Malformed or interrupted responses fail without automatically
replaying the query. If the output destination itself fails, the result can be incomplete;
the host does not append a second JSON envelope. Returned links and attachments are untrusted
content, not instructions to execute.

Human output shows the activity type and name, when supplied, for updates without renderable
content, for example `CLI: Activity received: event (DynamicPlanStepTriggered)` or
`CLI: Activity received: endOfConversation`. These summaries do not display the event payload;
use `--json` for full activity data. Typing-only activities remain silent.

## `msagent test playground`

Launch the M365 Agents Playground in a new terminal window. This command rejects `--json`,
`--query`, and `--conversation-id`.

```text
msagent test playground --project <project> --agent-name <agent> --deployment-name <deployment>
msagent test playground --schema-name <schema> --environment-id <environment> --tenant <tenant-guid> --deployment-name <label>
msagent test playground --target directline
```

| Option | Alias | Default | Description |
| --- | --- | --- | --- |
| `--target` | `-tgt` | `directengine` | Transport: `directengine` or `directline`. |
| `--plain` | `-pl` | `false` | Suppress CLI transient progress; the playground's own console output is still inherited. |
| `--install-playground` | `-ipg` | `false` | Authorize global npm installation when needed in non-interactive/redirected-input use. Interactive use still asks for confirmation. Installation may execute package lifecycle scripts. |

### DirectEngine

The default transport accepts all [target selectors](#target-selection). The CLI uses its
selected account to discover the endpoint, then gives the playground the endpoint and public
client configuration, not tokens or secrets. The playground owns session authentication and
can require browser or device-code interaction **even with `--non-interactive`**.

Process start is not proof of successful authentication or a connected conversation.

### Direct Line

`--target directline` requires an interactive terminal with both input and output attached.
It rejects `--non-interactive` and all target selectors, including `--project`, even when
`--install-playground` is supplied. It does not resolve a project, sign the CLI into Entra,
or load the CLI's public-client configuration.

Publish an agent with **no authentication** and global public-cloud Direct Line connectivity.
Enter the complete HTTPS token endpoint from **Channels > Mobile app**, or a raw Direct Line
secret from **Settings > Security > Web channel security**, only at the native playground's
**masked terminal prompt**. Do not put credentials in command arguments, environment variables,
configuration files, or chat. Empty Enter, Escape, or Ctrl+C cancels native credential setup.

The native playground owns credential validation. The CLI does not acquire, save, or log the
credential. It launches with an isolated disposable working directory, no inherited playground
configuration, a loopback-only listener, and native playground telemetry disabled. These settings
do not disable the CLI's separate command telemetry.

Use Node.js 22.17.1+ LTS for Direct Line. Regional/custom/sovereign Direct Line endpoints,
OAuth-card sign-in, file upload, headless interaction, and saved connections are unsupported.
Use DirectEngine for agents requiring Microsoft sign-in. Wait for Connected and an enabled
composer in the browser rather than assuming process start means a connection is ready.

### Installation and lifetime

The CLI uses `agentsplayground` on `PATH` with version `0.2.29` or newer. If missing or too old,
interactive use offers `npm install -g @microsoft/m365agentsplayground@latest` and requires typing
`install`. Non-interactive or redirected-input use requires explicit `--install-playground`
consent. Before offering installation, the CLI checks for Node.js 22.17.1 or newer and npm.
An existing playground also requires a supported Node.js runtime, but does not require npm.
These checks and any prerequisite errors appear in the original terminal before a new window
opens. On Windows, dependency probes and npm installation do not open additional console windows.
The CLI does not install or upgrade Node.js or npm. If they are missing or unsupported, install
Node.js with npm from https://nodejs.org/en/download, open a new terminal so PATH is refreshed,
and rerun `msagent test playground`.
Use a user-owned npm prefix when the global prefix would require elevation.

After process startup, the calling terminal returns and the new terminal owns the playground.
If the session fails, the new terminal retains its output and waits for Enter before closing;
Ctrl+C also closes it. Successful exits and intentional cancellations do not pause, and no
acknowledgment is requested when input or output is redirected. Process startup is not proof
that the playground is ready or authenticated; later errors remain in the playground window.

Version probes have a 10-second timeout; installation has a 600-second timeout. On Ctrl+C or
normal exit, shutdown allows up to 10 seconds for graceful exit, then kills only the owned child
process tree and allows another 10 seconds for cleanup. An abrupt parent crash can orphan the
server. There is no detach, automatic replay, or transcript feature.

## Telemetry and preview limits

Neither command is exempt from [command telemetry controls](Command_Telemetry.md).
The preview README recommends keeping the feature disabled or setting
`AH_CLI_TELEMETRY_OPTOUT=1` until its identifier-minimization policy is reviewed.
These command descriptions do not claim that live integration, accessibility, or cross-platform
validation is complete; see the [preview status and limitations](../Microsoft.Agents.Hosting.Cli/README.md#live-agent-test-controlled-preview).
