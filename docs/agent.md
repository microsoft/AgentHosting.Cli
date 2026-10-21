# MSAGENT CLI: quick context for humans and coding assistants

Read this before working on the project. Use the installed `msagent` CLI to build,
edit, deploy, and test GitHub Copilot harness-based agents for Microsoft Copilot
Studio and workflows in a Power Platform environment. Do not substitute other CLIs.

To use this file with GitHub Copilot or Claude, attach it or explicitly say:
"Read agent.md and follow it for this task." Do not assume this filename is
automatically loaded by every assistant.

## Start here

You need access to Copilot Studio and a suitable Power Platform environment.
Install the .NET 10 SDK (includes the .NET and ASP.NET Core runtimes), then:

```text
dotnet tool install --global Microsoft.Agents.Hosting.Cli --prerelease
msagent --version
msagent --help
```

Already installed? Use `dotnet tool update --global Microsoft.Agents.Hosting.Cli --prerelease`.
If your preview invitation specifies a package source, follow those instructions.

Package check: 2026-10-21, latest public and configured preview-feed package:
0.2.27-beta (build fbc49aea93). Before using a command, check its `--help`;
preview syntax changes. Use `--json` for automation. `--non-interactive` generally
fails instead of opening sign-in; it does not supply credentials or consent.

## Choose the right thing

| Need | Choice |
|---|---|
| A new conversational agent, using the GitHub Copilot harness by default | `--agent-type MCSAgent --mcs-agent-type CLI` (CLI is the current default). |
| A structured sequence of actions called by an agent | An attached workflow under that agent's `workflows\`, exposed through a workflow tool. |
| An independent cloud flow, without an owning conversational agent | `--agent-type MCSWorkflow`, not `MCSAgent`. |

This guide is designed for GitHub Copilot harness-based Copilot Studio agents and
workflows. Create CLI/GitHub Copilot-backed agents by default.[^classic]
The assistant helping you author files (Copilot or Claude) is separate from the
deployed agent's runtime/model. Do not change runtime just because you use Claude
to author it. Do not use the obsolete `--agent-harness` flag.

## Main lifecycle

**Sign in -> select environment -> create/clone -> edit -> deploy draft -> bind
connections -> verify -> publish with approval -> iterate.**

Replace `<environment-id>` with an ID returned by `env list`. Run each command
separately and inspect the result before continuing.

```text
msagent auth login
msagent auth status
msagent env list
msagent env select <environment-id>
msagent agent create --project MyAgent --agent-type MCSAgent --mcs-agent-type CLI --agent-name "My Agent" --description "Customer support assistant" --initial-prompt "Answer support questions using the provided knowledge. Ask when unsure."
```

Work from the new `MyAgent` project directory for the following commands:

```text
msagent agent show
msagent deployment create --deployment-name dev_slot --deployment-type dev
msagent agent deploy --deployment-name dev_slot
msagent deployment connection list --deployment-name dev_slot --json
```

Edit the generated source files to add instructions, knowledge, tools, and workflows.
For connector-backed features, deploy the definitions first, inspect connection
readiness, then use `msagent deployment connection update --deployment-name dev_slot`
if binding is needed. Let the user complete any required OAuth consent; never guess
between multiple candidate connections.

Review the draft before going live. A draft evaluation is available through
`msagent agent eval run --generate 3 --name "Smoke tests" --watch`; evaluations use
Copilot Credits and generated tests do not replace checking real expected behavior.

**Only after explicit publishing approval:**

```text
msagent agent deploy --deployment-name dev_slot --publish
msagent test ask --deployment-name dev_slot --query "What can you help me with?"
```

`msagent test playground --deployment-name dev_slot` opens an interactive testing
experience. Live testing can execute real actions. If a test command is unavailable,
report that limitation; do not silently change local feature flags or install tools.

## What a project looks like

Folders appear as features are added; not every new scaffold contains all of them.

```text
MyAgent\
  .config\agent.config.json       Local agent/deployment registration
  <generated-agent-folder>\
    settings.mcs.yml             Model, authentication, instructions, agent settings
    agent.sync.yaml              Generated cloud/sync tracking
    capabilities\
      knowledge\                Knowledge source definitions
      tools\                    Connector actions and workflow tools
    behaviors\<skill-name>\     Skill instructions (SKILL.md) and companion definitions
    infrastructure\connections\ Logical connection reference definitions
    workflows\<name>-<id>\       workflow.json and metadata.yml
    .mcs\                       Generated local sync state
```

Use the actual generated layout, not an assumed folder name. A connector defines
available operations; a connection reference is the logical binding in source;
a connection is the authenticated credential in the target environment.
Keep credentials out of source. Do not hand-edit generated registration/sync state.

## Testing from Copilot, Claude, or automation

In a pristine 0.2.27-beta installation, `liveTestFeatures` is **off** and
`evaluationFeatures` is **on**. If `msagent test` is missing, enable live testing
for the current shell, without editing the installed `featureflags.json`:

```powershell
$env:AH_CLI_FEATURES = (@($env:AH_CLI_FEATURES, 'liveTestFeatures') | Where-Object { $_ }) -join ';'
msagent test ask --help
```

On macOS/Linux, the equivalent is
`export AH_CLI_FEATURES="${AH_CLI_FEATURES:+$AH_CLI_FEATURES;}liveTestFeatures"`.
If evaluation commands are gated in another build, `evaluationFeatures` can be
added to the same semicolon-separated list. Overrides are shell-scoped; an
assistant must inherit them or set them in the process that runs `msagent`.

For a coding assistant testing the deployed CLI-native/GitHub Copilot-backed agent:

```text
msagent test ask --project MyAgent --deployment-name dev_slot --query "What can you help me with?" --json --non-interactive
```

The target agent determines the runtime; `test ask` has **no harness selector**.
Authenticate interactively first when needed; non-interactive mode cannot bypass
sign-in, authorization, or connector consent. Inspect the structured result and
actual answer, not just the exit code. Use `--conversation-id <id>` to continue a
conversation; otherwise each call starts a new one. Do not put secrets in queries.
Prefer `test ask` over Playground for unattended use: Playground is interactive,
does not support JSON in this release, and its DirectEngine sign-in may still need
user action even with `--non-interactive`.

## Workflows: make the intended type

**Attached vs. standalone is a lifecycle distinction.** For an agent-callable
workflow, add its definition under the agent's `workflows\` and a corresponding
`kind: WorkflowTool` in `capabilities\tools\`; the tool and metadata must reference
the same workflow ID, with matching input/output names.

For a **new standalone workflow**, use the latest CLI's built-in scaffolder:

```text
msagent agent create --project MyWorkflow --agent-type MCSWorkflow --agent-name "My Workflow"
```

This scaffolds the current workflow format and local registration, not a cloud
agent. From the new `MyWorkflow` project directory:

```text
msagent deployment create --deployment-name dev_slot --deployment-type dev
msagent agent deploy --deployment-name dev_slot
```

Review connection readiness and workflow state in the output. Activation makes a
flow runnable: request it with `--publish` only after approval. Existing source can
also request activation, so review its state before deploying a cloned/edited flow.
For workflow files that already exist, register their workspace root (the directory
containing `workflows\`, not the individual `workflow.json`):

```text
msagent agent init --agent-type MCSWorkflow --agent-name "My Workflow" --mcs-workflow-source <workflow-directory>
```

`init` registers existing files; it does not generate a flow or create one in the
cloud. Then create a deployment and deploy using the registered project/record.
Do not create an `MCSAgent` as a workaround. To clone an existing standalone flow,
use `agent create --agent-type MCSWorkflow --mcs-clone-workflow-id <workflow-id>`.

**Workflow format and standalone registration are separate concerns.** Both attached
and standalone workflows use `workflow.json` + `metadata.yml`. The **0.2.27-beta
scaffolder uses the newer designer-compatible shape**: trigger metadata contains
`associatedData.graph` (nodes and edges) and `associatedData.nodeActionMapping`;
actions carry matching `metadata.nodeId` values. Preserve these and keep graph
configuration, executable actions, and input/output schemas consistent when editing.
For new attached flows, reuse that current scaffold shape, with a unique workflow ID
and matching workflow tool; do not reuse another flow's identity.

Do not default to older graph-free examples or strip graph metadata to reduce file
size. `modernFlowType: 1` and `schemaVersion: "1.0.0.0"` are not selectors for a
designer generation or standalone registration. Use the generated template rather
than inventing version flags, schemas, or connector operation IDs.

A standalone project has `.config\agent.config.json` plus a generated workspace
containing `workflows\<name>-<id>\{workflow.json,metadata.yml}`; it does not need
conversational-agent settings, skills, or an attached `WorkflowTool`.

## Other commands and safe iteration

| Task | Command |
|---|---|
| Clone an existing cloud agent locally | `msagent agent create --project MyClone --mcs-clone-agent-id <cloud-agent-id>` |
| Register an existing local agent project | `msagent agent init --agent-type MCSAgent --agent-name "My Agent" --mcs-agent-source <agent-source-directory>` |
| Inspect local registrations / cloud agents | `msagent agent show` / `msagent agent list` |
| Inspect deployment records | `msagent deployment list` / `msagent deployment show --help` |
| Bring cloud changes into local source | `msagent agent pull` (review/back up local edits first) |
| Inspect evaluations | `msagent agent eval list` / `msagent agent eval show --help` |

Local record IDs, cloud agent IDs, schema names, and deployment IDs are different:
use the selector the command requests. Creating/cloning local files is not deployment,
and `init` is not a substitute for cloning an existing cloud agent.

Confirm the account, tenant, and deployment's target environment before any mutation;
selecting an active environment does not retarget existing deployment records.
Ask before publishing, overwriting drift, discarding local edits, deleting resources,
or running actions with external effects. Never add `--overwrite` just to make an
error disappear. Stop on failures and explain what remains blocked; do not report
success until the intended behavior is verified.

[^classic]: A standard/classic Copilot Studio agent, explicitly requested: use
    `--agent-type MCSAgent --mcs-agent-type Classic`. This is an exception, not the
    focus of this guide.
