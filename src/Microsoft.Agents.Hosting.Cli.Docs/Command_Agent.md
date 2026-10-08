# MSAgent CLI agent commands

[Back to command index](ReadMe.md)

The command group is described in help as `Agent Hosting agent operations.`

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent agent create`](#msagent-agent-create) | Create an agent project. Creating a CodeAgent is **Gated**; MCS agents are available by default. |
| [`msagent agent init`](#msagent-agent-init---mcs-specific) | **MCS-specific.** Register an MCS project that already exists on disk. |
| [`msagent agent list`](#msagent-agent-list---mcs-specific) | **MCS-specific.** List MCS agents in an environment or across the tenant. |
| [`msagent agent show`](#msagent-agent-show) | Show the managed-agent records held by a project. Reads local configuration only. |
| [`msagent agent pull`](#msagent-agent-pull---mcs-specific) | **MCS-specific.** Pull the remote MCS agent's content into the project. |
| [`msagent agent delete`](#msagent-agent-delete---mcs-specific) | **MCS-specific.** Delete an MCS agent's cloud resources and every record for it. |
| [`msagent agent deploy`](#msagent-agent-deploy) | Deploy an agent to a named deployment. The package-revision (AMS) route is **Gated**. |
| [`msagent agent share`](#msagent-agent-share---mcs-specific) | **MCS-specific.** Share an MCS agent with a user, or with the organization. |
| [`msagent agent share show`](#msagent-agent-share-show---mcs-specific) | **MCS-specific.** Show who has access to an MCS agent. |
| [`msagent agent unshare`](#msagent-agent-unshare---mcs-specific) | **MCS-specific.** Remove a user's access to an MCS agent. |
| `msagent agent package` | **(Gated)** Navigation group containing the `upload` command. |
| [`msagent agent package upload`](#msagent-agent-package-upload-gated) (**Gated**) | Upload exact package bytes and confirm a durable AMS Package Revision. |
| [`msagent agent eval`](Command_Agent_Eval.md) | **MCS-specific.** Run Copilot Studio evaluations and manage test sets. See [Command_Agent_Eval.md](Command_Agent_Eval.md) for details. |

Options and behaviors labeled **MCS-specific** apply only to Microsoft Copilot Studio agents.

> **Gated.** Commands, options, and routes tagged **Gated** may or may not be available, depending on
> the feature gates enabled for a given run. Review `msagent agent --help` (or the help for a
> specific command) to see what is currently available. Gated items are enabled by the `AmsFeatures`
> feature, for example through the `AH_CLI_FEATURES` environment variable. When the feature is off:
>
> - A gated group or command, such as `agent package` or `agent package upload`, is not listed in
>   help, and invoking it fails with `Unrecognized command or argument`.
> - A gated option, such as `--package-revision` on `agent deploy`, is not listed in help and is
>   rejected as unrecognized.
> - A gated route that is reached through a visible command, such as creating a CodeAgent, fails with
>   a message that the capability is disabled by the `AmsFeatures` rollout control.
>
> Feature controls are availability switches, not authorization boundaries.

## `msagent agent create`

Creates an agent project in a local directory. Without the `AmsFeatures` feature, only MCS agents can
be created, so `--agent-type MCSAgent` must be supplied.

```text
# New MCS agent
msagent agent create --name <name> --description <description> \
  --agent-type MCSAgent --initial-prompt <prompt> [options]

# Clone an MCS agent
msagent agent create --agent-type MCSAgent \
  (--mcs-clone-agent-id <id> | --mcs-clone-agent-schemaname <schema-name>) [options]

# CodeAgent (Gated)
msagent agent create --name <name> --description <description> \
  --language <language> --language-version <version> [options]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--name` | `-n` | String | All agents | Agent display name. Required unless cloning, which inherits the source agent's name. |
| `--description` | `-d` | String | All agents | Agent description. Required for a new MCS agent. |
| `--agent-type` | `-at` | `MCSAgent` (alias `mcs`) or `CodeAgent` | All agents | Agent type. Defaults to `CodeAgent`, which is **Gated**; use `MCSAgent` for a local MCS project. |
| `--environment-id` | `-eid` | String | All agents | Target environment id. When omitted, the active environment is used. |
| `--project` | `-p` | Directory path | **MCS-specific** | The project directory to initialize. Defaults to the current directory; a missing directory is created. |
| `--schema-name` | `-sn` | String | **MCS-specific** | MCS schema name. Derived from `--name` when omitted. |
| `--mcs-agent-type` | `-mat` | `CLI` or `Classic` | **MCS-specific** | Scaffold shape. Defaults to `CLI`. |
| `--init-template` | `-it` | String | **MCS-specific** | Scaffold template name. Valid only with `--mcs-agent-type Classic`. |
| `--initial-prompt` | `-ip` | String | **MCS-specific** | Instructions scaffolded into the project. Required for a new MCS agent. |
| `--mcs-clone-agent-schemaname` | `-mcas` | String | **MCS-specific** | Clone the agent with this schema name instead of scaffolding. |
| `--mcs-clone-agent-id` | `-mcaid` | String | **MCS-specific** | Clone the agent with this id instead of scaffolding. |
| `--mcs-clone-environment-id` | `-mceid` | String | **MCS-specific** | The environment holding the clone source. Defaults to `--environment-id`, then the active environment. |
| `--language` | `-lang` | `dotnet`, `python`, or `node` | CodeAgent only (**Gated**) | Runtime language. Required for a CodeAgent. |
| `--language-version` | `-lv` | String | CodeAgent only (**Gated**) | Runtime language version. Required for a CodeAgent. |
| `--idempotency-key` | `-ik` | String | CodeAgent only (**Gated**) | Opaque create-retry key. A new value is generated when omitted. |

### Validation

- An unknown `--agent-type` or `--mcs-agent-type` value is a usage error.
- With `--agent-type MCSAgent`, supplying `--language`, `--language-version`, or `--idempotency-key`
  is a usage error because they apply to CodeAgent only.
- Without `MCSAgent`, supplying any MCS-specific option (`--schema-name`, `--mcs-agent-type`,
  `--init-template`, `--initial-prompt`, or a clone option) is a usage error that asks for
  `--agent-type MCSAgent`.
- A CodeAgent requires `--name`, `--language`, and `--language-version`.
- **Gated:** creating a CodeAgent (any `--agent-type` other than `MCSAgent`, including the default)
  fails when `AmsFeatures` is off, with a message directing the user to `--agent-type MCSAgent`.

### Creating a new MCS agent

Creating an MCS agent from scratch requires `--name`, `--description`, `--agent-type MCSAgent`, and
`--initial-prompt`. `--mcs-agent-type` selects the scaffold: `CLI` (the default) or `Classic`.
`--init-template` selects a template and is valid only with `Classic`.

```text
# Create a CLI-format MCS agent in the active environment
msagent agent create --name "Support agent" \
  --description "Answers support questions" \
  --agent-type MCSAgent \
  --initial-prompt "Help users resolve support issues."

# Create a Classic MCS agent in an explicit environment and directory
msagent agent create --name "Support agent" \
  --description "Answers support questions" \
  --agent-type MCSAgent \
  --initial-prompt "Help users resolve support issues." \
  --mcs-agent-type Classic \
  --environment-id <environment-id> --project <directory>
```

### Cloning an existing MCS agent

Cloning requires `--agent-type MCSAgent` and a clone selector, `--mcs-clone-agent-id` or
`--mcs-clone-agent-schemaname`. The clone inherits the source agent's name unless `--name` is
supplied. The environment holding the clone source is `--mcs-clone-environment-id`, then
`--environment-id`, then the active environment.

```text
# Clone by schema name using the active environment
msagent agent create --agent-type MCSAgent \
  --mcs-clone-agent-schemaname <schema-name> --project <directory>

# Clone from one environment into another
msagent agent create --agent-type MCSAgent \
  --mcs-clone-agent-id <agent-id> \
  --mcs-clone-environment-id <source-environment-id> \
  --environment-id <target-environment-id> --project <directory>
```

### Creating a CodeAgent (Gated)

> **Gated.** Available only when `AmsFeatures` is enabled.

```text
msagent agent create --name "Support agent" --description "Answers support questions" \
  --language dotnet --language-version <version>
```

## `msagent agent init` - MCS-specific

Registers an MCS project that already exists on disk. The command creates no files and nothing in
Copilot Studio.

```text
msagent agent init --agent-name <name> [--mcs-agent-source <directory>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--agent-name` | `-an` | String | Yes | None | Names the managed-agent record. |
| `--agent-type` | `-at` | `MCSAgent` | No | `MCSAgent` | Agent type; `MCSAgent` is the only supported value. |
| `--mcs-agent-source` | `-mas` | Directory path | No | Current directory | The directory holding the project. |

## `msagent agent list` - MCS-specific

Lists the MCS agents in an environment or across the tenant. An agent that has never been deployed
is not listed.

```text
msagent agent list [--environment-id <id> | --tenant-wide] [--limit <number>] [--project <directory>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Current directory | The project directory to act on. |
| `--environment-id` | `-eid` | String | Active environment | List this environment instead of the active one. |
| `--tenant-wide` | `-tw` | Flag | Off | List every environment the signed-in account can reach. |
| `--limit` | `-lim` | Whole number | `20` | Maximum rows to return. |

## `msagent agent show`

Shows the managed-agent records a project holds. The command reads the local configuration and
contacts nothing.

```text
msagent agent show [--project <directory>] [--agent-id <id> | --agent-name <name>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Current directory | The project directory to read. |
| `--agent-id` | `-aid` | String | None | The record to show. Wins over `--agent-name`. Every record is shown when no selector is supplied. |
| `--agent-name` | `-an` | String | None | The record to show, by name. |

## `msagent agent pull` - MCS-specific

Brings the remote MCS agent's content into the project, taking every component collection beside it.

```text
msagent agent pull [--project <directory>] [--agent-id <id> | --agent-name <name>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Current directory | The directory to pull into. |
| `--agent-id` | `-aid` | String | None | The record to pull. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The record to pull, by name. |

## `msagent agent delete` - MCS-specific

Deletes an MCS agent's cloud resources and every record for it. Local files are never touched.

```text
msagent agent delete [--project <directory>] [--agent-id <id> | --agent-name <name>] \
  [--deployment-id <id> | --deployment-name <name>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Current directory | The project directory to act on. |
| `--agent-id` | `-aid` | String | None | The record to delete. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The record to delete, by name. |
| `--deployment-id` | `-did` | String | None | Chooses which cloud agent is deleted. Wins over `--deployment-name`. |
| `--deployment-name` | `-dn` | String | None | Chooses which cloud agent is deleted, by name. |

## `msagent agent deploy`

Deploys an agent to a named deployment. By default the command deploys an MCS project to an existing
MCS deployment. There is no active-environment fallback: an MCS deploy requires `--deployment-id` or
`--deployment-name`.

```text
# MCS agent
msagent agent deploy (--deployment-id <id> | --deployment-name <name>) \
  [--project <directory>] [--agent-id <id> | --agent-name <name>] [--publish] [--overwrite]

# AMS package revision (Gated)
msagent agent deploy --agent-id <id> --package-revision <revision-id> [AMS options]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | **MCS-specific** | The local MCS project directory. |
| `--agent-id` | `-aid` | String | All routes | The agent to deploy. |
| `--agent-name` | `-an` | String | **MCS-specific** | The record to deploy, by name. |
| `--deployment-id` | `-did` | String | **MCS-specific** | The deployment to target. Wins over `--deployment-name`. |
| `--deployment-name` | `-dn` | String | MCS and AMS routes | The deployment to target, by name. Required for AMS package-revision deployment. |
| `--schema-name` | `-sn` | String | **MCS-specific** | Overrides the project's schema name for this deploy. |
| `--publish` | `-pub` | Flag | **MCS-specific** | Publish the agent after a push that fully landed, making the content live. |
| `--overwrite` | `-ow` | Flag | **MCS-specific** | Overwrite cloud content that changed since the last sync, and accept replacing an existing agent this project cannot show is its own. Only a dev deployment refuses drift without it. |
| `--commit-id` | `-cid` | String | Not supported | This build has no Git Repository Service, so deploy always publishes the current working tree. Supplying it is a usage error. |
| `--async` | `-as` | Flag | Not supported | The deploy is synchronous and returns when the push completes. Supplying it is a usage error. |
| `--package-revision` | `-pr` | String | **Gated** | Existing immutable AMS Package Revision id. No package bytes are uploaded. |
| `--environment-id` | `-eid` | String | **Gated** | AMS Agent home environment. Defaults to the active environment. |
| `--target-environment-id` | `-tenv` | String | **Gated** | Explicit AMS target environment, including for same-home deployment. |
| `--deployment-type` | `-dt` | `dev`, `test`, or `prod` | **Gated** | AMS deployment stage. |
| `--entra-id` | `-ent` | String | **Gated** | AMS deployment Entra application id. |
| `--entra-id-type` | `-eit` | `app-reg`, `blueprint`, or `default` | **Gated** | AMS identity kind. |

Gated behavior:

- The AMS options and the package-revision route are listed in help only when `AmsFeatures` is
  enabled. When it is off, they are rejected as unrecognized options.
- A CodeAgent's package bytes must first be uploaded with
  [`msagent agent package upload`](#msagent-agent-package-upload-gated) (**Gated**), then deployed
  with `--agent-id`, `--package-revision`, and explicit deployment inputs. `--project` alone does not
  deploy a CodeAgent.

## `msagent agent share` - MCS-specific

Shares an MCS agent with a user, with the whole organization, or both.

```text
msagent agent share [--project <directory>] [--agent-id <id> | --agent-name <name>] \
  [--user <user> --role <use|edit|view>] [--share-with-org [true|false]]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Current directory | The project directory to act on. |
| `--agent-id` | `-aid` | String | None | The agent to act on. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The agent to act on, by name. |
| `--user` | `-u` | String | None | The user to grant access to, by sign-in address or Entra object id. |
| `--role` | `-rl` | `use`, `edit`, or `view` | None | The access to grant. Required with `--user`. |
| `--share-with-org` | `-swo` | Optional flag | Setting left unchanged | Let everyone in the organization use the agent. Omit to leave the current setting alone; `--share-with-org false` turns it off. |

### `msagent agent share show` - MCS-specific

Shows who has access to an MCS agent, and whether everyone in the organization can use it. It
accepts `--project`, `--agent-id`, and `--agent-name`, with the same meanings as above.

## `msagent agent unshare` - MCS-specific

Removes a user's access to an MCS agent. Environment Maker is environment-wide and is never removed.

```text
msagent agent unshare [--project <directory>] [--agent-id <id> | --agent-name <name>] --user <user>
```

It accepts `--project`, `--agent-id`, `--agent-name`, and `--user` (`-u`; a sign-in address or Entra
object id), with the same meanings as for `msagent agent share`.

## `msagent agent package upload` (Gated)

> **Gated.** Available only when `AmsFeatures` is enabled. When it is off, `agent package` and
> `agent package upload` are not listed in help.

Uploads exact package bytes and confirms a durable AMS Package Revision.

```text
msagent agent package upload --agent-id <id> --package <file> [--environment-id <id>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--agent-id` | `-aid` | String | Yes | None | Existing repository-bound CodeAgent id. |
| `--package` | `-pkg` | File path | Yes | None | Exact package file to upload. The CLI does not build or inspect its contents. |
| `--environment-id` | `-eid` | String | No | Active environment | Agent home environment. |
