# Testing your Agent

Once your agent has been deployed as published and made accessible to the copilot studio runtime you can test it from your local machine.

*Note, at the time of BUG BASH and initial PRIVATE PREVIEW testing may not be available as an ```msagent``` command. Locally testing your agent is still possible, with specific instructions which will be provided to you. 

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
Enable `LiveTestFeatures` in the CLI's `featureflags.json` if the test commands
are not visible. Use Node.js 22.17.1+ LTS and an interactive terminal with input
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
msagent test ask [--query "<your question>"] [--agent-name <agentname> | --agent-id <agentid> | --schema-name <schemaname>] [--deployment-name <deploymentname> | --deployment-id <deploymentid>]
```

- The Copilot Studio client's discovery service resolves the runtime endpoints from the environment and schema name; no harness argument is needed. DirectEngine Playground also discovers its app endpoint automatically, using the selected CLI account before the playground's own sign-in.
- `--query` (`-q`) is the literal question to send. Omit it to be prompted for it interactively. Live actions the query triggers are not rolled back, and the query may enter your shell history.
- Select the agent and deployment the same way as other `msagent` commands: `--project`, one of `--agent-id`/`--agent-name`/`--schema-name`, and optionally `--deployment-id`/`--deployment-name` (and `--environment-id`/`--tenant` to assert the destination).
- `--conversation-id` continues an existing conversation; omit it to start a new one (the id is not saved automatically, so save it yourself to continue later).
- `--non-interactive` fails instead of opening a browser/WAM sign-in prompt; `--json` emits machine-readable output; `--plain` prints only the final response, without transient progress.

Example:

```
msagent test ask --agent-name agentname --deployment-name dev --query "What is your return policy?"
```

Expect a browser to open if this is the first time you have used this to agree to using your credentials to run the copilot studio agent.

> **Note:** `msagent test ask` has shipped behind a feature flag in some CLI builds. If `msagent test` is not recognized, check the installed CLI tool's `featureflags.json` for a flag that enables live test features and turn it on.

### How it works

`test ask` brokers delegated Entra authentication for the selected CLI profile.
For `test playground`, the native playground owns the connection: DirectEngine
uses browser/device-code Entra sign-in, while Direct Line uses the credential
entered into its masked terminal prompt. Direct Line does not require Entra
consent and is only for published no-auth agents.
