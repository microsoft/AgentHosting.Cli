# MSAgent CLI agent commands

[Back to command index](ReadMe.md)

The command group is described in help as `Agent Hosting agent operations.`

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent agent create`](#msagent-agent-create) | Create an agent project or standalone workflow. Creating a CodeAgent is **Gated**; MCS agents and workflows are available by default. |
| [`msagent agent copy`](#msagent-agent-copy---mcs-specific) | **MCS-specific.** Create a new agent or standalone workflow from an existing one. Prepares local content only; the first deploy creates the new agent or workflow. |
| [`msagent agent init`](#msagent-agent-init---mcs-specific) | **MCS-specific.** Register an MCS project or standalone workflow that already exists on disk. |
| [`msagent agent list`](#msagent-agent-list) | List MCS agents or standalone workflows in an environment or across the tenant, or the code agents of an AMS environment (**Gated**). |
| [`msagent agent show`](#msagent-agent-show) | Show the managed-agent records held by a project, read locally, or an AMS code agent with its deployments (**Gated**). |
| [`msagent agent update`](#msagent-agent-update-gated) (**Gated**) | Rename an AMS code agent or change its description. |
| [`msagent agent pull`](#msagent-agent-pull---mcs-specific) | **MCS-specific.** Pull a remote agent or standalone workflow into the project. |
| [`msagent agent delete`](#msagent-agent-delete) | Delete one cloud MCS agent or standalone workflow and every local record for it, or an AMS code agent with its deployments and bots (**Gated**). |
| [`msagent agent deploy`](#msagent-agent-deploy) | Deploy an agent or standalone workflow to a named deployment, or put a deployment of an AMS code agent live (**Gated**). |
| [`msagent agent share`](#msagent-agent-share---mcs-specific) | **MCS-specific.** Share an MCS agent with a user, or with the organization. |
| [`msagent agent share show`](#msagent-agent-share-show---mcs-specific) | **MCS-specific.** Show who has access to an MCS agent. |
| [`msagent agent unshare`](#msagent-agent-unshare---mcs-specific) | **MCS-specific.** Remove a user's access to an MCS agent. |
| `msagent agent package` | **(Gated)** Navigation group containing the `upload` command. |
| [`msagent agent package upload`](#msagent-agent-package-upload-gated) (**Gated**) | Upload exact package bytes and confirm a durable AMS Package Revision and its commit. |
| `msagent agent commit` | **(Gated)** Navigation group containing the `list` command. |
| [`msagent agent commit list`](#msagent-agent-commit-list-gated) (**Gated**) | List the commits of an AMS code agent's uploads, newest first, with the deployments that run each. |
| [`msagent agent eval`](Command_Agent_Eval.md) | **MCSAgent only; gated by EvaluationFeatures.** Run Copilot Studio evaluations and manage test sets. See [Command_Agent_Eval.md](Command_Agent_Eval.md) for details. |

Options and behaviors labeled **MCS-specific** apply to Microsoft Copilot Studio agents
(`MCSAgent`) or standalone Power Automate cloud flows (`MCSWorkflow`), as noted per command.
Sharing and evaluation apply to `MCSAgent` only, not standalone workflows.

### Project and record selectors

Unless a command says otherwise, an omitted project directory discovers the nearest enclosing
project configuration, searching the current directory and up to five parents; if none exists,
it uses the current directory. An explicit directory is used as given.

For local-record commands, `--agent-id` names the CLI's managed-agent record, not the remote bot or
workflow ID, and wins over `--agent-name`. Names match case-insensitively. With neither selector,
commands that act on one record require the project to hold exactly one, unless a deployment
selector identifies its owner. `agent show` instead shows every record.

> **Gated.** Commands, options, and routes tagged **Gated** may or may not be available, depending on
> the feature gates enabled for a given run. Review `msagent agent --help` (or the help for a
> specific command) to see what is currently available. CodeAgent/AMS gated items are controlled by
> `AmsFeatures`, for example through the `AH_CLI_FEATURES` environment variable. Evaluation has the
> separate `EvaluationFeatures` gate. When `AmsFeatures` is off:
>
> - A gated group or command, such as `agent package`, `agent package upload`, `agent commit`, or
>   `agent update`, is not listed in help, and invoking it fails with `Unrecognized command or argument`.
> - A gated option, such as `--environment-id` on `agent deploy`, is not listed in help and is
>   rejected as unrecognized.
> - A gated route that is reached through a visible command, such as creating or listing CodeAgents, fails with
>   a message that the capability is disabled by the `AmsFeatures` rollout control.
>
> Feature controls are availability switches, not authorization boundaries.

### AMS code agents (Gated)

With `AmsFeatures` on, the commands that act on an existing agent reach an AMS code agent when the local
project does not know the agent named. With it off, every command routes as it does without AMS.

- A command that names an agent with `--agent-id` or `--agent-name` keeps its MCS route (or, for a
  `deployment` command, its local Azure Bot Service route) when the project (the current directory, or
  `--project`) holds a record of that agent or of the deployment named. Otherwise it takes the AMS route.
  A command that names no agent keeps its local route.
- `--environment-id` (`-eid`) names the agent's home environment and always selects the AMS route. It is
  optional on the AMS route and defaults to the active environment (`msagent env select`). The target
  environment of a deploy always comes from the deployment record.
- `agent create` and `agent list` take the AMS route with `--agent-type CodeAgent`, and
  `deployment create` with `--target-environment-id`. `agent update`, `agent package upload`, and
  `agent commit list` are AMS only.

## `msagent agent create`

The MCS routes create an agent project or standalone workflow in a local directory; the gated
CodeAgent route creates a cloud agent. Without `AmsFeatures`, use `--agent-type MCSAgent` or
`--agent-type MCSWorkflow`; an MCS agent clone selector can also select `MCSAgent` implicitly.

```text
# New MCS agent
msagent agent create --agent-type MCSAgent --agent-name <name> --description <description> \
  --initial-prompt <prompt> [options]

# Clone an MCS agent
msagent agent create [--agent-type MCSAgent] \
  (--mcs-clone-agent-id <id> | --mcs-clone-agent-schemaname <schema-name>) [options]

# New or cloned standalone workflow
msagent agent create --agent-type MCSWorkflow --agent-name <name> [options]
msagent agent create --agent-type MCSWorkflow --mcs-clone-workflow-id <workflow-id> [options]

# CodeAgent (Gated)
msagent agent create --agent-type CodeAgent --agent-name <name> \
  --language <language> --language-version <version> [options]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--agent-name` | `-an`, `--name`, `-n` | String | All agents | Agent or workflow display name. Required unless cloning, which inherits the source name. A new MCS agent's name is at most 42 characters, without `<` or `>`. |
| `--description` | `-d` | String | All agents | Agent description. Required for a new MCS agent. |
| `--agent-type` | `-at` | `MCSAgent` (alias `mcs`), `MCSWorkflow`, or `CodeAgent` | All agents | Agent type, matched case-insensitively. Defaults to `MCSAgent` when `--mcs-clone-agent-id`, `--mcs-clone-agent-schemaname`, or `--mcs-clone-environment-id` is supplied without a CodeAgent-only option; otherwise defaults to `CodeAgent`, which is **Gated**. A workflow always needs explicit `MCSWorkflow`. |
| `--environment-id` | `-eid` | String | All agents | Target/home environment id. Defaults to the active environment for a scaffold, or the source environment for a clone. |
| `--project` | `-p` | Directory path | **MCS-specific** | The project directory to initialize. Defaults to the enclosing project, else the current directory; a missing directory is created. |
| `--schema-name` | `-sn` | String | MCSAgent only | MCS schema name. Derived from the ASCII letters and digits of `--agent-name` when omitted. Not accepted when cloning. |
| `--mcs-agent-type` | `-mat` | `CLI` or `Classic` | MCSAgent only | Scaffold shape, matched case-insensitively. Defaults to `CLI`; also accepts `cli-copilot` and `clicopilot` for CLI. |
| `--init-template` | `-it` | String | MCSAgent only | Scaffold template name. Valid only with `--mcs-agent-type Classic`, whose only shipped template is `default`. |
| `--initial-prompt` | `-ip` | String | MCSAgent only | Instructions scaffolded into the project. Required for a new MCS agent. |
| `--mcs-clone-agent-schemaname` | `-mcas` | String | MCSAgent only | Clone the agent with this schema name instead of scaffolding. |
| `--mcs-clone-agent-id` | `-mcaid` | String | MCSAgent only | Clone the agent with this id instead of scaffolding. |
| `--mcs-clone-workflow-id` | `-mcwid` | Non-empty GUID | MCSWorkflow only | Download this standalone cloud flow instead of scaffolding. Requires explicit `--agent-type MCSWorkflow`. |
| `--mcs-clone-environment-id` | `-mceid` | String | **MCS-specific** | The environment holding the clone source. Defaults to `--environment-id`, then the active environment. |
| `--language` | `-lang` | `dotnet`, `python`, or `node` | CodeAgent only (**Gated**) | Runtime language. Required for a CodeAgent; the option is hidden when the gate is off. |
| `--language-version` | `-lv` | String | CodeAgent only (**Gated**) | Runtime language version. Required for a CodeAgent. |
| `--idempotency-key` | `-ik` | String | CodeAgent only (**Gated**) | Opaque create-retry key: 1–128 visible ASCII characters, without spaces. A new value is generated when omitted. |

### Validation

- An unknown `--agent-type` or `--mcs-agent-type` value is a usage error.
- When `--agent-type` is omitted, `--mcs-clone-agent-id`, `--mcs-clone-agent-schemaname`, or
  `--mcs-clone-environment-id` selects `MCSAgent`, unless `--language`, `--language-version`, or
  `--idempotency-key` is also supplied. An explicit `--agent-type` always wins.
- CodeAgent-only options (`--language`, `--language-version`, and `--idempotency-key`) are gated
  options and cannot be used with either MCS type.
- `MCSWorkflow` rejects agent-only scaffold options, schema names, and agent clone selectors.
  `MCSAgent` rejects `--mcs-clone-workflow-id`.
- CodeAgent rejects MCS-specific options, including `--project`.
- A CodeAgent requires `--agent-name`, `--language`, and `--language-version`.
- **Gated:** creating a CodeAgent (including the default)
  fails when `AmsFeatures` is off, with a message directing the user to `--agent-type MCSAgent`.

### Creating a new MCS agent

Creating an MCS agent from scratch requires `--agent-name`, `--description`, `--agent-type MCSAgent`, and
`--initial-prompt`. `--mcs-agent-type` selects the scaffold: `CLI` (the default) or `Classic`.
`--init-template` selects a template and is valid only with `Classic`.

The display name is limited to 42 characters, without `<` or `>`, and the description to 2000. Creation writes a
named workspace beneath the project and registers it in `.config\agent.config.json`; it does not
create a cloud agent. Create a deployment with `msagent deployment create`, then use `agent deploy`
to upload it.

```text
# Create a CLI-format MCS agent in the active environment
msagent agent create --agent-name "Support agent" \
  --description "Answers support questions" \
  --agent-type MCSAgent \
  --initial-prompt "Help users resolve support issues."

# Create a Classic MCS agent in an explicit environment and directory
msagent agent create --agent-name "Support agent" \
  --description "Answers support questions" \
  --agent-type MCSAgent \
  --initial-prompt "Help users resolve support issues." \
  --mcs-agent-type Classic \
  --environment-id <environment-id> --project <directory>
```

### Cloning an existing MCS agent

Cloning requires a clone selector, `--mcs-clone-agent-id` or `--mcs-clone-agent-schemaname`.
`--agent-type MCSAgent` is optional: when it is omitted, the clone selector or
`--mcs-clone-environment-id` selects `MCSAgent`. Supply exactly one agent clone selector;
`--mcs-clone-environment-id` alone is not enough. The clone inherits the source agent's name unless
`--agent-name` is supplied. The source environment is `--mcs-clone-environment-id`, then
`--environment-id`, then the active environment. The target/home environment is `--environment-id`
when supplied, otherwise the source environment.

Cloning downloads content immediately and registers a `develop` deployment; it does not duplicate the
cloud agent until deployment. A same-environment clone remains associated with the source agent.
`--schema-name` is rejected in clone mode. `--mcs-agent-type`, `--init-template`, and
`--initial-prompt` are ignored with warnings because the source supplies the content.

```text
# Clone by schema name using the active environment
msagent agent create --mcs-clone-agent-schemaname <schema-name> --project <directory>

# Clone from one environment into another
msagent agent create --mcs-clone-agent-id <agent-id> \
  --mcs-clone-environment-id <source-environment-id> \
  --environment-id <target-environment-id> --project <directory>
```

### Creating or cloning a standalone workflow

Use explicit `--agent-type MCSWorkflow`. A new workflow requires only a display name and an
environment (explicit or active); description is optional. It scaffolds files and a managed record,
not a cloud flow. A clone requires a non-empty workflow GUID and inherits the source name unless
overridden. Source/target environment selection follows the agent clone rules above, and a clone
also registers a `develop` deployment.

```text
msagent agent create --agent-type MCSWorkflow --name "Approval flow" --project <directory>

msagent agent create --agent-type MCSWorkflow --mcs-clone-workflow-id <workflow-id> \
  --mcs-clone-environment-id <source-environment-id> --project <directory>
```

### Creating a CodeAgent (Gated)

> **Gated.** Available only when `AmsFeatures` is enabled.

Creates a repository-bound CodeAgent in the selected environment and reports its agent ID and
repository ID. It does not scaffold local code or accept `--project`. Keep the idempotency key
when retrying an uncertain creation; failures preserve a generated key in their remediation.

```text
msagent agent create --agent-name "Support agent" --description "Answers support questions" \
  --language dotnet --language-version <version>
```

## `msagent agent copy` - MCS-specific

Creates a new agent or standalone workflow from an existing one: a local agent or workflow, an agent as
it is in its environment (`--mcs-clone-agent-id`), or a standalone workflow as it is in its environment
(`--mcs-clone-workflow-id`). Copying only prepares and registers a new local project. Nothing is sent
to Copilot Studio and the source is never changed. The copy is an agent or workflow of its own from the
start: work on it like any other, and its first deploy creates it in the cloud.

```text
# Copy a local agent or workflow project
msagent agent copy --agent-name <agent-name> [options]

# Copy an agent as it is in its environment
msagent agent copy --mcs-clone-agent-id <mcs-clone-agent-id> --agent-name <agent-name> [options]

# Copy a standalone workflow as it is in its environment
msagent agent copy --mcs-clone-workflow-id <mcs-clone-workflow-id> --agent-name <agent-name> [options]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--agent-name` | `-an` | String | Required | The copy's display name. An agent's name has at most 42 characters, without `<` or `>`, and must differ, ignoring case, from the source's name and from the name of every agent in the environment. A workflow's name has at most 100 characters, no control characters, and must differ, ignoring case, from the source flow's name and from the name of every unmanaged cloud flow in the environment. |
| `--mcs-clone-agent-id` | `-mcaid` | String | None | The cloud agent to copy as it is in its environment, instead of a local agent or workflow. |
| `--mcs-clone-workflow-id` | `-mcwid` | String | None | The cloud standalone workflow to copy as it is in its environment, by its workflow id, instead of a local agent or workflow. |
| `--project` | `-p` | Directory path | Current directory | The local agent or workflow to copy: its folder, or a project holding only that agent or workflow. With `--mcs-clone-agent-id` or `--mcs-clone-workflow-id`, the project to register the copy in. |
| `--environment-id` | `-eid` | String | Active environment | The environment holding the `--mcs-clone-agent-id` agent or the `--mcs-clone-workflow-id` workflow, or the environment to copy a local agent or workflow in when it names none. |
| `--schema-name` | `-sn` | String | Generated from `--agent-name`, short enough for the agent's longest component | The new agent's schema name, for agents only. A workflow has no schema name, so a workflow copy refuses it. |

Name one cloud source at most: `--mcs-clone-agent-id` together with `--mcs-clone-workflow-id` is a usage
error.

```text
# Copy the local agent in a folder
msagent agent copy --project <agent-folder> --agent-name "Support agent copy"

# Copy an agent and everything it depends on, then deploy the copy
msagent agent copy --mcs-clone-agent-id <mcs-clone-agent-id> --environment-id <environment-id> \
  --agent-name "Support agent copy" --project <directory>
msagent deployment create --project <directory> --agent-name "Support agent copy" \
  --deployment-name "Development" --deployment-type dev
msagent agent deploy --project <directory> --agent-name "Support agent copy" --deployment-name "Development"
```

### Copying a standalone workflow

A local workflow is copied when the record the location selects is a standalone workflow
(`MCSWorkflow`). A cloud flow is copied with `--mcs-clone-workflow-id`, which takes its Dataverse
workflow id: the flow is downloaded once into the copy's staging, and nothing else in the environment
is read before the copy is prepared.

```text
# Copy the local workflow in a folder
msagent agent copy --project <workflow-folder> --agent-name "Approve expenses copy"

# Copy a cloud flow as it is in its environment, then deploy and turn on the copy
msagent agent copy --mcs-clone-workflow-id <workflow-id> --environment-id <environment-id> \
  --agent-name "Approve expenses copy" --project <directory>
msagent deployment create --project <directory> --agent-name "Approve expenses copy" \
  --deployment-name "Development" --deployment-type dev
msagent agent deploy --project <directory> --agent-name "Approve expenses copy" --deployment-name "Development" --publish
```

## `msagent agent init` - MCS-specific

Registers an MCS project or standalone workflow that already exists on disk. It writes a local
managed-agent record, but creates nothing in Copilot Studio. Workflow registration also writes
the connection-reference declaration.

```text
msagent agent init --agent-name <name> [--mcs-agent-source <directory>]
msagent agent init --agent-name <name> --agent-type MCSWorkflow [--mcs-workflow-source <directory>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--agent-name` | `-an` | String | Yes | None | Names the managed-agent record. |
| `--agent-type` | `-at` | `MCSAgent` (alias `mcs`) or `MCSWorkflow` | No | `MCSAgent` | Type of project to register. |
| `--mcs-agent-source` | `-mas` | Directory path | No | Enclosing project, else current directory | The directory holding the agent project. For MCSWorkflow, a fallback source used literally rather than searching parents. |
| `--mcs-workflow-source` | `-mws` | Directory path | No | Current directory | The workflow workspace to register. MCSWorkflow only; takes precedence over `--mcs-agent-source` on that route. |

For an agent, the source must hold exactly one unregistered agent workspace with a readable schema
name. Its existing connection supplies the environment when present; otherwise the active
environment is required. For a workflow, the source must hold exactly one readable workflow.
Workflow registration uses the active environment and registers beneath an enclosing project when
one exists. Neither route creates a deployment.

## `msagent agent list`

Lists cloud MCS agents in an environment or across the tenant, or standalone workflows when
`--agent-type MCSWorkflow` is supplied. Local-only projects that have never reached the service
are not listed. With `--agent-type CodeAgent` (**Gated**), it lists the code agents of an AMS home
environment.

```text
msagent agent list [--environment-id <id> | --tenant-wide] [--limit <number>] [--project <directory>] \
  [--agent-type <MCSAgent|MCSWorkflow>] [--filter <text>]

# Code agents (Gated)
msagent agent list --agent-type CodeAgent [--environment-id <id>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Enclosing project, else current directory | The project directory used to annotate matching cloud rows. |
| `--environment-id` | `-eid` | String | Active environment | List this environment instead of the active one. |
| `--agent-type` | `-at` | `MCSAgent` (alias `mcs`), `MCSWorkflow`, or `CodeAgent` (**Gated**) | `MCSAgent` | Select agents, standalone cloud flows, or code agents. |
| `--filter` | `-flt` | String | None | Show workflows whose name contains this text, taken literally: `%` and `_` match themselves. Rejected for MCSAgent. |
| `--tenant-wide` | `-tw` | Flag | Off | List every environment the signed-in account can reach. |
| `--limit` | `-lim` | Whole number | `20` | Maximum rows to return across the selected scope. Must be positive. |

`--environment-id` and `--tenant-wide` cannot be combined. Output includes cloud identity,
environment, lifecycle/workflow state, and last-change time. When a cloud row matches this project's
records, it also carries the managed-agent ID and deployment count. The result reports truncation.
Tenant-wide listing continues past individual environment failures and reports those diagnostics;
it does not promise a complete tenant inventory when the limit is reached.

## `msagent agent show`

Shows the managed-agent records a project holds. The command reads the local configuration and
contacts nothing. It includes each selected record's deployments and supports both agents and
standalone workflows. With `AmsFeatures` on, an agent the project holds no record of is read from AMS
instead (see [AMS code agents](#ams-code-agents-gated)).

```text
msagent agent show [--project <directory>] [--agent-id <id> | --agent-name <name>]

# AMS code agent (Gated)
msagent agent show (--agent-id <id> | --agent-name <name>) [--environment-id <id>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Enclosing project, else current directory | The project directory to read. |
| `--agent-id` | `-aid` | String | None | The record to show. Wins over `--agent-name`. Every record is shown when no selector is supplied. |
| `--agent-name` | `-an` | String | None | The record to show, by name. |
| `--environment-id` | `-eid` | String | Active environment | **Gated.** The AMS agent's home environment. Selects the AMS route. |

The AMS route shows the agent (name, type, language, description, home environment, state, and last
change), a table of its deployments (stage, kind `package` or `dev-endpoint`, state, bot, and a pending
endpoint change), and the bots AMS keeps from deleted records of live deployments until
`msagent agent delete` removes them.

## `msagent agent update` (Gated)

> **Gated.** Available only when `AmsFeatures` is enabled. When it is off, `agent update` is not listed in
> help.

Renames an AMS code agent or changes its description. It reads the agent, then sends the change with the
agent's entity tag; the result shows the agent as AMS returned it (`agent-updated`).

```text
msagent agent update (--agent-id <id> | --agent-name <name>) (--new-name <name> | --description <text>) \
  [--environment-id <id>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--agent-id` | `-aid` | GUID | None | The agent. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The agent, by name in its home environment. |
| `--new-name` | `-nn` | String | None | The agent's new name. |
| `--description` | `-d` | String | None | The agent's new description. An empty value clears it. |
| `--environment-id` | `-eid` | String | Active environment | The agent's home environment. |

## `msagent agent pull` - MCS-specific

Brings remote content into a registered project's workspace. Knowledge files and skill files are
refreshed from the environment, except a file edited locally since its last sync, which is kept for the
next deploy to send.

```text
msagent agent pull [--project <directory>] [--agent-id <id> | --agent-name <name>] [--overwrite]

# Standalone workflow
msagent agent pull [--project <directory>] [--agent-id <id> | --agent-name <name>] \
  [--deployment-id <id> | --deployment-name <name>] [--overwrite]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Enclosing project, else current directory | The registered project whose workspace is pulled. |
| `--agent-id` | `-aid` | String | None | The record to pull. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The record to pull, by name. |
| `--deployment-id` | `-did` | String | The only matching deployment | MCSWorkflow only. Select the deployment whose environment is read; wins over `--deployment-name`. |
| `--deployment-name` | `-dn` | String | The only matching deployment | MCSWorkflow only. Select the deployment by name. Required to disambiguate when several match. |
| `--overwrite` | `-ow` | Flag | Off | MCSWorkflow: replace local workflow edits not yet seen by the environment. MCSAgent: delete locally changed knowledge files that the environment deleted or replaced, instead of keeping them or stopping. |

An MCSAgent pull requires an existing workspace connection and reads that connection's environment,
not the active environment or a deployment. It merges remote changes into local content, and pulls
declared component collections only when they are connected to the same environment.
Deployment selectors are rejected for MCSAgent.
Merge conflicts are reported in the result/warnings rather than making the pull fail.

A standalone workflow pull resolves a deployment (the sole matching deployment may be implicit)
and replaces the local workflow from its environment, refusing unpushed local edits unless
`--overwrite` is supplied. It can recreate missing workflow files. It does not merge those edits.
The result reports the environment, changes, and whether the workspace was already up to date.

## `msagent agent delete`

Deletes one cloud MCS agent or standalone workflow and removes its managed-agent record and all its
deployment records. Local workspace files are never touched. Cloud copies in other environments
are not deleted; the result reports the targets left orphaned. The flows, AI prompts, connection
references, and custom connectors an agent uses stay in the environment unless `--all-resources`
deletes them too. With `AmsFeatures` on, an agent the project holds no record of is deleted in AMS
instead (see [Deleting an AMS code agent](#deleting-an-ams-code-agent-gated)).

```text
msagent agent delete [--project <directory>] [--agent-id <id> | --agent-name <name>] \
  [--deployment-id <id> | --deployment-name <name>] [--all-resources]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Enclosing project, else current directory | The project directory to act on. |
| `--agent-id` | `-aid` | String | None | The record to delete. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The record to delete, by name. |
| `--deployment-id` | `-did` | String | None | Chooses which cloud agent is deleted. Wins over `--deployment-name`. |
| `--deployment-name` | `-dn` | String | None | Chooses which cloud agent is deleted, by name. |
| `--all-resources` | `-alr` | Flag | Off | MCSAgent only. Also delete the flows, AI prompts, connection references, and custom connectors the agent uses. |

The delete removes one cloud agent or flow: the one the workspace is bound to, the selected
deployment's, or the one in the record's home environment. The confirmation and `orphanedTargets` list
each copy it leaves in another environment that a deployment names (for a workflow, also its home
environment when no deployment names it). Once the records are gone, no `msagent` command can reach
those copies, so to remove an MCSAgent's copy in another environment, run
`msagent deployment delete --delete-deployed-mcs-agent` for that deployment first.

`--all-resources` also deletes what the agent uses: every flow, AI prompt, connection reference, and
custom connector its workspace holds. It deletes them after the agent, in the
environment the delete targets: AI prompts, flows (turned off first), connection references, then
custom connectors. A resource that environment does not hold is skipped. It does not check whether
anything else uses a resource: Dataverse refuses to delete some while something depends on them, such
as a connection reference a flow uses, but a flow that another agent calls is deleted anyway. When a
resource cannot be deleted, the command fails with `resource-delete-incomplete`, lists each resource
left and why, and keeps the records, so the same command can be run again. The result lists the
deleted resources in `resources`. The agent's workspace must be on disk, and a standalone workflow
refuses the option.

For an MCSAgent, an omitted deployment uses the workspace's connection when present, otherwise its
home environment and schema name. If its workspace is gone, only the records are removed. For a
standalone workflow, an omitted deployment uses its home environment; an explicit deployment
selects that environment's copy of the workflow.

Before deleting, the command lists everything it will delete, including each resource `--all-resources`
selects, and asks the operator to type the managed record's display name. Global `--non-interactive`
supplies consent; there is no command-specific `--force`. A mismatch reports `name-mismatch`, and
declining reports `cancelled`; neither deletes anything or counts as an error.

```text
# Delete a copy with everything it created: it lists what it will delete and asks first
msagent agent delete --agent-name "Support agent copy" --all-resources
```

### Deleting an AMS code agent (Gated)

```text
msagent agent delete (--agent-id <id> | --agent-name <name>) [--environment-id <id>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--agent-id` | `-aid` | GUID | None | The code agent. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The code agent, by name in its home environment. |
| `--environment-id` | `-eid` | String | Active environment | The agent's home environment. Selects the AMS route. |

The AMS route deletes the code agent with every deployment record and Azure Bot Service instance it owns,
including the bots AMS kept from deleted records of live deployments. `--project`, `--deployment-id`,
`--deployment-name`, and `--all-resources` keep the MCS route.

## `msagent agent deploy`

Deploys an agent or standalone workflow to an existing named deployment. There is no
active-environment fallback on these MCS routes: both require `--deployment-id` or
`--deployment-name`. Create a slot first with `msagent deployment create` (cloning already creates
a `dev` slot). A deployment selector can identify its owning record without an agent selector.
With `AmsFeatures` on, the AMS route (**Gated**) puts an existing deployment of a code agent live.

```text
# MCS agent
msagent agent deploy (--deployment-id <id> | --deployment-name <name>) \
  [--project <directory>] [--agent-id <id> | --agent-name <name>] [--publish] [--overwrite]

# AMS code agent (Gated)
msagent agent deploy (--agent-id <id> | --agent-name <name>) (--deployment-id <id> | --deployment-name <name>) \
  [--commit-id <commit>] [--environment-id <id>]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | **MCS-specific** | The local MCS project directory. |
| `--agent-id` | `-aid` | String | All routes | The agent to deploy. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | All routes | The agent to deploy, by name. |
| `--deployment-id` | `-did` | String | All routes | The deployment to target. Wins over `--deployment-name`. |
| `--deployment-name` | `-dn` | String | All routes | The deployment to target, by name. |
| `--schema-name` | `-sn` | String | **MCS-specific** | Overrides the project's schema name for this deploy. Valid only for a classic-layout agent: a CLI agent always deploys under the schema name in its `settings.mcs.yml`. A workflow is addressed by its workflow id, so a workflow deploy rejects this option before anything is uploaded. |
| `--publish` | `-pub` | Flag | **MCS-specific** | Publish an agent after a push that fully landed, or request activation of a standalone workflow. Off by default. |
| `--overwrite` | `-ow` | Flag | **MCS-specific** | Overwrite cloud content that changed since the last sync, and accept replacing an existing agent this project cannot show is its own. Only a dev deployment refuses drift without it. A workflow deploy always replaces the cloud copy, so the flag is accepted there and changes nothing. |
| `--commit-id` | `-cid` | 40 hexadecimal characters | AMS route (**Gated**) | The commit of one of the code agent's uploads to deploy to a Dev or Test deployment; a Dev dev endpoint deployment switches to that package. When omitted, the deployment's requested or deployed dev endpoint, else the newest upload. On the MCS route this build has no Git Repository Service, so deploy always uploads the current working tree and supplying it is a usage error. |
| `--async` | `-as` | Flag | Not supported | The deploy is synchronous and returns when the push completes. Supplying it is a usage error. |
| `--environment-id` | `-eid` | String | **Gated** | The AMS agent's home environment. Selects the AMS route. Defaults to the active environment. |
| `--target-environment-id`, `--deployment-type`, `--entra-id`, `--entra-id-type` | `-tenv`, `-dt`, `-ent`, `-eit` | | **Gated**, moved | Moved to `msagent deployment create`. Each selects the AMS route, which refuses it with that pointer. |
| `--endpoint-url` | `-eu` | | **Gated**, moved | Moved to `msagent deployment create` and `msagent deployment update`. Selects the AMS route, which refuses it with that pointer. |
| `--package-revision` | `-pr` | | **Gated**, moved | Replaced by `--commit-id`; `msagent agent commit list` shows the commits. Selects the AMS route, which refuses it with that pointer. |

### MCS deployment behavior

- The current working tree is uploaded synchronously. An MCSAgent deploy creates or adopts the
  target's cloud agent, reconnecting/retargeting the workspace when needed. Existing read-only
  managed agents are rejected. Adoption must be verifiable as this project's agent, or explicitly
  acknowledged with `--overwrite`.
- Declared component collections are pushed first when connected to the target environment.
  Missing or differently connected collections produce warnings.
- `--overwrite` doesn't replace cloud changes that would delete or replace knowledge files changed
  locally; the deploy stops with [17025](ErrorCodes/17025.md) so you can pull first.
- For an MCSAgent, deployment records are updated even when content or workflow-activation warnings remain.
  With `--publish`, content/activation blockers withhold publication and fail the command after
  recording the deploy; informational notices alone do not prevent publication.
- A standalone workflow deploy always uploads over the target's copy by workflow ID; `--overwrite`
  adds no protection or different behavior. It rejects `--schema-name`. Activation is attempted
  when requested by the local workflow or `--publish`; unbound connections can leave it in draft
  with warnings. Bind them with `msagent deployment connection update` before retrying.
- Output identifies the record, deployment, target environment, action, pushed counts, publication
  or workflow state, and warnings.

A deploy that creates the Copilot Studio agent, or connects the project to another one, records it in
`agent.config.json` before it sends any content. So when the deploy fails after that, `agent show`
names the agent, and `msagent deployment delete --delete-deployed-mcs-agent` deletes it. A deploy that
may create the agent first checks the display name in `settings.mcs.yml`: at most 42 characters,
without `<` or `>`.

In the JSON result, `pushedComponentCount` counts only the component changes the push sent and the
service stored. It leaves out changes to the agent's settings and icon, its component collection
links, connection references, and environment variables. Earlier builds also counted the settings and
icon changes, so the same push can now report a lower number. `auxiliaryChangeCount` counts the
workflows sent that the reported changes do not already include, and `uploadedFileCount` the knowledge
and attachment files.

### Deploying a copied agent or workflow - MCS-specific

A copied agent deploys like any other agent. Its first deploy creates its agent in the deployment's
environment, together with its own workflows, prompts, custom connectors, and connection references.
Its workflows were copied as drafts: the deploy warns how many workflows are not running and how many
connection references are not bound, the way any agent's deploy does; run
`msagent deployment connection update` to bind them and turn the workflows on.

A copied standalone workflow deploys like any other workflow. Its first deploy creates the flow in the
deployment's environment as a draft, declares its own connection references unbound, and writes the
copy's `.mcs` cache from what the environment returns. With `--publish`, the deploy binds each unbound
reference to a connected connection the signed-in user owns, the first by connection ID when the user
owns several, and turns the flow on once all its references are bound. A reference no such connection
fits stays unbound: bind it with `msagent deployment connection update` and deploy again with
`--publish`.

## `msagent agent share` - MCS-specific

Shares an MCS agent with a user, with the whole organization, or both.

An explicit managed-record selector is required even in a one-agent project. The agent must already
exist in the cloud. These commands target the most recently deployed environment, falling back to
the record's home environment, not the active environment. They do not accept deployment or
environment selectors and do not support standalone workflows.

```text
msagent agent share (--agent-id <id> | --agent-name <name>) [--project <directory>] \
  [--user <user> --role <use|edit|view>] [--share-with-org [true|false]]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Enclosing project, else current directory | The project directory to act on. |
| `--agent-id` | `-aid` | String | None | The agent to act on. Required unless `--agent-name` is supplied, and wins over it. |
| `--agent-name` | `-an` | String | None | The agent to act on, by name. Required unless `--agent-id` is supplied. |
| `--user` | `-u` | String | None | The user to grant access to, by sign-in address, email address, or Entra object id. |
| `--role` | `-rl` | `use`, `edit`, or `view` | None | The access to grant. Required with `--user`. |
| `--share-with-org` | `-swo` | Optional flag | Setting left unchanged | Let everyone in the organization use the agent. Omit to leave the current setting alone; `--share-with-org false` turns it off. |

Supply `--user` together with `--role`, or supply `--share-with-org`; both actions may be combined.
A role without a user does not grant user access; organization-only sharing accepts no role or
`use`, not `edit` or `view`. The roles map to Copilot Studio consumer (`use`), collaborator (`edit`),
and analyst (`view`) access, and match case-insensitively.
Sharing changes an existing user's agent role rather than adding a second role. The owner's access
cannot be changed or removed. A request that already matches reports `unchanged`; successful writes
are checked against the requested state.

### `msagent agent share show` - MCS-specific

Shows who has access to an MCS agent, and whether everyone in the organization can use it. It
accepts `--project`, `--agent-id`, and `--agent-name`, with the same meanings as above.
The owner is listed first. Principal names are best effort; object IDs remain visible when directory
lookups cannot name them.

## `msagent agent unshare` - MCS-specific

Removes a user's access to an MCS agent. Environment Maker is environment-wide and is never removed.
Organization-wide access is unchanged, so an unshared user can still use an agent shared with the
organization. Removing an absent user grant reports `unchanged`.

```text
msagent agent unshare (--agent-id <id> | --agent-name <name>) --user <user> [--project <directory>]
```

It accepts `--project`, `--agent-id`, `--agent-name`, and `--user` (`-u`; a sign-in address, email
address, or Entra object id), with the same meanings as for `msagent agent share`.

## `msagent agent package upload` (Gated)

> **Gated.** Available only when `AmsFeatures` is enabled. When it is off, `agent package` and
> `agent package upload` are not listed in help.

Uploads exact package bytes and confirms a durable AMS Package Revision and its commit.

```text
msagent agent package upload (--agent-id <id> | --agent-name <name>) --package <file> [--environment-id <id>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--agent-id` | `-aid` | GUID | One of the two | None | Existing repository-bound CodeAgent id. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | One of the two | None | The CodeAgent, by name in its home environment. |
| `--package` | `-pkg` | File path | Yes | None | Exact package file to upload. The CLI does not build or inspect its contents. |
| `--environment-id` | `-eid` | String | No | Active environment | Agent home environment. |

## `msagent agent commit list` (Gated)

> **Gated.** Available only when `AmsFeatures` is enabled. When it is off, `agent commit` and
> `agent commit list` are not listed in help.

Lists the commits of an AMS code agent's package uploads, newest first: each commit's upload time, size,
and the deployments that run it.

```text
msagent agent commit list (--agent-id <id> | --agent-name <name>) [--environment-id <id>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--agent-id` | `-aid` | GUID | None | The code agent. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The code agent, by name in its home environment. |
| `--environment-id` | `-eid` | String | Active environment | The agent's home environment. |

The command reads every page of the agent's uploads and of its deployments once, and names under each
commit the deployments whose deployed commit or Package Revision is that upload's.
