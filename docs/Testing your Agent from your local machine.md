# Testing your Agent

Once your agent has been deployed as published and made accessible to the copilot studio runtime you can test it from your local machine.

Use `msagent test ask` for terminal queries or `msagent test playground` for an
interactive UI. Testing is available without a feature-flag opt-in. The agent
runs in Copilot Studio; the CLI and Playground are clients, not local emulators.

## Billing and channel IDs in usage telemetry

**Testing through the CLI invokes the real Copilot Studio service and incurs
billable usage.** Running from a local terminal, GitHub Copilot, Claude, or another
automated runner does not make the interaction free or turn it into a simulation.
Agent responses and any actions triggered during testing can consume Copilot
Credits and may incur other applicable service charges. Live actions are not
rolled back.

For direct invocation of a published agent through `msagent test ask`, usage is
currently attributed to the channel ID **`pva-published-engine-direct`** in billing
and consumption telemetry surfaced through the **Power Platform admin center
(PPAC)**. This is a service invocation identifier, not a customer-facing channel
you need to configure.

Copilot Studio **evaluations are also billable**, including evaluations started
with `msagent agent eval run`. Evaluation activity can appear under the following
channel IDs, depending on which stages the evaluation uses:

| Channel ID | Usage category |
|---|---|
| `pva-eval-generation` | Evaluation test/conversation generation. |
| `pva-maker-evaluation` | Evaluation execution against the agent. |
| `pva-eval-evaluator` | Evaluation grading. |

For example, running an existing test set does not necessarily require generating
new test conversations, so do not expect every evaluation run to produce all three
channel IDs. Direct-invoke testing and evaluation activity can both appear when
you use both features.

**These are not the only Copilot Studio channel IDs.** Other features have their
own identifiers; workflows, for example, have their own set. Review all relevant
usage categories for the features your agent and tests exercise rather than
filtering only to the four IDs above. A channel ID identifies a usage path; it
does not by itself establish the total cost of a test or evaluation.

Channel attribution described here reflects the current PPAC reporting behavior
as of October 21, 2026 and may change. Review your environment's consumption,
applicable licensing, and current Copilot Studio billing guidance before running
large test batches, repeated evaluations, or unattended test loops.

## Interactive testing

If you want to test it yourself with a UX, you can use the Agent Playground which has been updated to support copilot studio agents using either directline or copilot studio client connectivity (direct)

### Setup Agent Playground with DirectLine

Direct Line supports a published Copilot Studio agent configured for **no
authentication**, using the global public-cloud Direct Line service.
Use Node.js 22.17.1+ LTS and an interactive terminal with input
and output attached.

```text
msagent test playground --target directline
```

Enter the complete HTTPS MCS token endpoint from **Channels > Mobile app**, or
the raw Direct Line secret from **Settings > Security > Web channel security**,
only at the native playground's **masked terminal prompt**. Do not put
credentials into chat, command arguments, environment variables, or config.
The CLI does not retrieve or persist this credential, read project/deployment
configuration, or require CLI Entra sign-in. Empty Enter, Escape, or Ctrl+C
cancels native credential setup.

Direct Line rejects `--non-interactive`, even with `--install-playground`, and
does not accept `--project`, `--agent-id`, `--agent-name`,
`--schema-name`, `--deployment-id`, `--deployment-name`, `--environment-id`, or
`--tenant`. The credential determines the agent. `--plain` suppresses only CLI
progress; `--json` is unsupported. A missing/old playground offers a consented
global npm install that may execute package lifecycle scripts.
Before offering installation, the CLI checks Node.js 22.17.1 or newer and npm in
the original terminal. Existing playground installations also get a Node.js
runtime check before a new window opens. Missing or unsupported dependencies
are reported there with recovery steps; install Node.js with npm from
https://nodejs.org/en/download, open a new terminal, and retry.
If the launched playground fails, its terminal keeps the output visible until
you press Enter or Ctrl+C. Normal exits and cancellations do not pause, and
redirected input or output never waits for acknowledgment.

The CLI isolates playground config, uses the native default display name, binds
the listener to `127.0.0.1`, and disables native telemetry. Regional/custom/
sovereign endpoints, OAuth-card sign-in, file upload, and headless interaction
are not supported. Wait for Connected and an enabled composer in the owning
browser; process start alone is not connection success. Live agent actions
have real effects and are not rolled back. Ctrl+C stops the owned playground
process, not the user's browser.

### Setup Agent Playground using Entra ID (DirectEngine)

```text
msagent test playground --agent-name agentname --deployment-name deploymentname
```

DirectEngine is the default; `--target directengine` is optional. It uses the same
agent/deployment selectors as `test ask`. The CLI uses the selected signed-in
account to discover the endpoint from the environment and schema name; no
harness argument is needed. The native playground owns browser/device-code Entra
sign-in using delegated access. It may still need user action with
`--non-interactive`. Use this mode for agents requiring Microsoft sign-in.

### Testing using Terminal commands non-interactively (including with AI coding agents)

If you want to test your agent in a terminal, typically using non-interactive with GitHub Copilot, use `msagent test ask`. Non-interactive commands for testing only support direct (using copilot studio client) using Entra ID and does not support directline. The agent will run using the signed in users’ credentials that agree to the interaction. If you require testing the agent in annon mode, directline must be used via the agent playground.

```
msagent test ask --project <project-path> --agent-name <agentname> --deployment-name <deploymentname> --query "<your question>" --non-interactive --json
```

- The Copilot Studio client's discovery service resolves the runtime endpoints from the environment and schema name; no harness argument is needed. DirectEngine Playground also discovers its app endpoint automatically, using the selected CLI account before the playground's own sign-in.
- `--query` (`-q`) is a required nonblank literal question. Live actions it triggers are not rolled back, and the query may enter your shell history.
- For a registered project, select an agent and deployment with `--project`, `--agent-id`/`--agent-name`, and `--deployment-id`/`--deployment-name`. A deployment selector can identify its owning agent. An omitted deployment requires exactly one marked default; a single non-default deployment is not automatically selected.
- The deployment determines the environment. Additional `--schema-name`, `--environment-id`, and `--tenant` values assert that destination, rather than retargeting it.
- `--conversation-id` continues an existing conversation; omit it to start a new one (the id is not saved automatically, so save it yourself to continue later).
- `--non-interactive` fails instead of opening a browser/WAM sign-in prompt; `--json` emits machine-readable output; `--plain` prints only the final response, without transient progress.

Sign in interactively with `msagent auth login` before running unattended
queries. `--json` does not itself prevent authentication prompts, and `--plain`
cannot be combined with `--json`. The JSON result is one incrementally streamed
activity/result document, not the ordinary command envelope. Check its final
status and answer; a completed response does not prove a remote action succeeded.

To target an agent without local project registration, supply the complete
explicit route. The deployment name here is a label, not a verified local record:

```text
msagent test ask --schema-name <schema-name> --environment-id <environment-id> --tenant <tenant-id> --deployment-name explicit-target --query "What can you help me with?" --non-interactive --json
```

Example:

```
msagent test ask --agent-name agentname --deployment-name develop --query "What is your return policy?"
```

Expect a browser to open if this is the first time you have used this to agree to using your credentials to run the copilot studio agent.

Use `--conversation-id <returned-id>` with the same destination to continue a
conversation. Without it, each command starts a new conversation. The CLI does
not save the last conversation ID or automatically replay a failed query.

### How it works

`test ask` brokers delegated Entra authentication for the selected CLI profile.
For `test playground`, the native playground owns the connection: DirectEngine
uses browser/device-code Entra sign-in, while Direct Line uses the credential
entered into its masked terminal prompt. Direct Line does not require Entra
consent and is only for published no-auth agents.
