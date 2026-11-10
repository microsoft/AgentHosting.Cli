# MSAgent CLI agent commands

[Back to command index](ReadMe.md)

The command group is described in help as `Agent Hosting agent operations.`

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent agent create`](#msagent-agent-create) | Create an agent project or standalone workflow. Creating a CodeAgent is **Gated**; MCS agents and workflows are available by default. |
| [`msagent agent init`](#msagent-agent-init---mcs-specific) | **MCS-specific.** Register an MCS project or standalone workflow that already exists on disk. |
| [`msagent agent list`](#msagent-agent-list---mcs-specific) | **MCS-specific.** List MCS agents or standalone workflows in an environment or across the tenant. |
| [`msagent agent show`](#msagent-agent-show) | Show the managed-agent records held by a project. Reads local configuration only. |
| [`msagent agent pull`](#msagent-agent-pull---mcs-specific) | **MCS-specific.** Pull a remote agent or standalone workflow into the project. |
| [`msagent agent delete`](#msagent-agent-delete---mcs-specific) | **MCS-specific.** Delete one cloud agent or standalone workflow and every local record for it. |
| [`msagent agent deploy`](#msagent-agent-deploy) | Deploy an agent or standalone workflow to a named deployment. The package-revision (AMS) route is **Gated**. |
| [`msagent agent share`](#msagent-agent-share---mcs-specific) | **MCS-specific.** Share an MCS agent with a user, or with the organization. |
| [`msagent agent share show`](#msagent-agent-share-show---mcs-specific) | **MCS-specific.** Show who has access to an MCS agent. |
| [`msagent agent unshare`](#msagent-agent-unshare---mcs-specific) | **MCS-specific.** Remove a user's access to an MCS agent. |
| [`msagent agent capture`](#msagent-agent-capture-gated) (**Gated**) | Commit the Copilot Studio changes of an agent's Dev deployment to its GRS repository. |
| `msagent agent operation` | **(Gated)** Navigation group containing the `show` command. |
| [`msagent agent operation show`](#msagent-agent-operation-show-gated) (**Gated**) | Show a deploy or capture operation that AMS runs for an agent. |
| `msagent agent package` | **(Gated)** Navigation group containing the `upload` command. |
| [`msagent agent package upload`](#msagent-agent-package-upload-gated) (**Gated**) | Upload exact package bytes and confirm a durable AMS Package Revision. |
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
> - A gated group or command, such as `agent package` or `agent package upload`, is not listed in
>   help, and invoking it fails with `Unrecognized command or argument`.
> - A gated option, such as `--package-revision` on `agent deploy`, is not listed in help and is
>   rejected as unrecognized.
> - A gated route that is reached through a visible command, such as creating a CodeAgent, fails with
>   a message that the capability is disabled by the `AmsFeatures` rollout control.
>
> The `AmsMcsAgents` control adds [AMS mode](#ams-mode-gated) and its commands and options. While it
> is off, they are not listed in help and are rejected as unrecognized.
>
> Feature controls are availability switches, not authorization boundaries.

### AMS mode (Gated)

> **Gated.** Available only when `AmsMcsAgents` is enabled.

AMS mode adds the Agent Management Service (AMS) and a GRS git repository to the local project.
AMS mode is on while the `AmsMcsAgents` feature is on, for example with
`AH_CLI_FEATURES=AmsMcsAgents`, and off otherwise. While it is off, every command works as described
in this reference without it. While it is on, every command still works on the local project and its
`.config`, and also:

- [`agent create`](#ams-mode-for-agent-create-gated) and [`agent init`](#ams-mode-for-agent-init-gated)
  register the agent in AMS, with its folder in a GRS repository, then commit and push the folder;
  `agent init` also restores the agents AMS holds for the repository, in a clone or, with
  `--repository-id`, in an empty or missing folder that it clones the repository into, and connects
  each one Dev holds. A clone that `agent create` makes of a Copilot Studio agent or flow another AMS
  agent holds joins that agent instead;
- [`agent deploy`](#ams-mode-for-agent-deploy-gated) tells AMS which commit a deploy from the folder
  held, and `--remote` deploys a commit of the repository through AMS, connecting the agent's folder
  after a deploy to Dev of exactly the commit the folder holds;
- [`agent capture`](#msagent-agent-capture-gated) commits the edits made in Copilot Studio to the
  repository, and [`agent operation show`](#msagent-agent-operation-show-gated) shows the operations
  of remote deploys and captures;
- `agent show`, `agent list --view ams`, `agent delete`, and the
  [deployment commands](Command_Deployment.md#ams-mode-gated) read or change AMS's records too.

`share`, `share show`, `unshare`, and `eval` work as without AMS, and so does
[`agent pull`](#msagent-agent-pull---mcs-specific), except for its advice in a folder that is not
connected, and that it merges against the cloud cache msagent saved for the commit the folder
holds. The forms only AMS
mode has, `agent deploy --remote`, `agent capture`, `agent operation show`, `agent list --view ams`,
and `--repository-id` on `agent create` and `agent init`, exist only while it is on, and the commands
that act on a registered agent fail with [19062](ErrorCodes/19062.md) for an agent AMS does not know.
For a walkthrough, see [Managing agents with AMS](../../docs/Managing%20agents%20with%20AMS.md).

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
| `--agent-name` | `-an`, `--name`, `-n` | String | All agents | Agent or workflow display name. Required unless cloning, which inherits the source name. |
| `--description` | `-d` | String | All agents | Agent description. Required for a new MCS agent. |
| `--agent-type` | `-at` | `MCSAgent` (alias `mcs`), `MCSWorkflow`, or `CodeAgent` | All agents | Agent type, matched case-insensitively. Defaults to `MCSAgent` when `--mcs-clone-agent-id`, `--mcs-clone-agent-schemaname`, or `--mcs-clone-environment-id` is supplied without a CodeAgent-only option; otherwise defaults to `CodeAgent`, which is **Gated**. A workflow always needs explicit `MCSWorkflow`. |
| `--environment-id` | `-eid` | String | All agents | Target/home environment id. Defaults to the active environment for a scaffold, or the source environment for a clone. |
| `--project` | `-p` | Directory path | **MCS-specific** | The project directory to initialize. Defaults to the enclosing project, else the current directory; a missing directory is created. |
| `--schema-name` | `-sn` | String | MCSAgent only | MCS schema name. Derived from `--agent-name` when omitted. Not accepted when cloning. |
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
| `--repository-id` | `-repid` | GUID | AMS mode only (**Gated**) | GRS repository the project folder joins, as [`msagent agent show`](#msagent-agent-show) shows another agent's. See [AMS mode for agent create](#ams-mode-for-agent-create-gated). |

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

The display name can hold at most 42 characters, the most Copilot Studio accepts when it
creates or renames an agent, and needs a letter or digit and no control character such as
a tab; a name that breaks these rules is a usage error before anything is written. A
`displayName` edited in `settings.mcs.yml` later is held to the same 42 characters,
counting any spaces around it, whenever a deploy would create or rename the agent: the
deploy refuses a longer one before it creates or pushes to the Copilot Studio agent
([10022](ErrorCodes/10022.md)), while an agent that already carries a longer name keeps
it and pushes as before. The description is limited to 2000 characters. Creation writes a
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
cloud agent until deployment. A same-environment clone remains associated with the source agent,
which keeps its own name: `--agent-name` names only the record and the project folder. A clone from
another environment is a copy: `--agent-name` becomes the `displayName` in the copy's
`settings.mcs.yml` (or in its agent file when it has none), while the workspace's sync state keeps
the source's name, so the copy's first push sends the new name. A supplied `--agent-name` is checked
before any environment is contacted, and a name longer than 42 characters, without a letter or
digit, or with a control character such as a tab, is a usage error. The copy stays connected to
its source agent until its first deploy to its home environment creates its own agent; a dev
deployment in the source environment pushes to that source agent, replacing its content and name,
only once `--overwrite` accepts replacing it ([14003](ErrorCodes/14003.md)).
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
also registers a `develop` deployment. A clone from the home environment keeps the cloud flow's own
name, and `--agent-name` names only the record and the project folder. A clone from another
environment is a copy: `--agent-name` becomes the `name` in the copy's `metadata.yml` and names its
folder and definition file, while the workspace's sync state keeps the source's name. A supplied
name is checked before any environment is contacted: it must be at most 100 characters and hold no
control character, and a refused name leaves no folder or record behind. The
copy keeps the source's workflow id, so a dev deployment in an environment that already holds that
workflow, such as the source environment, needs `--overwrite` ([14003](ErrorCodes/14003.md)).

```text
msagent agent create --agent-type MCSWorkflow --name "Approval flow" --project <directory>

msagent agent create --agent-type MCSWorkflow --mcs-clone-workflow-id <workflow-id> \
  --mcs-clone-environment-id <source-environment-id> --project <directory>
```

### AMS mode for agent create (Gated)

> **Gated.** Applies only in [AMS mode](#ams-mode-gated), that is, while `AmsMcsAgents` is enabled.

With AMS mode on, creating or cloning an MCS agent or standalone workflow does everything described
above, and also registers it in AMS with its folder in a GRS repository. The project folder must be
the root of the repository's clone, and each agent is a top-level folder of it. Git must be
installed; msagent runs it with its own credential helper, which it saves in the clone's git config.

The repository is chosen before anything is written:

| Project folder | Without `--repository-id` | With `--repository-id <id>` |
| --- | --- | --- |
| Empty or new | AMS creates a repository, and the folder is linked to it with its `main` checked out | The repository is cloned into the folder |
| Holds only the project, not a git repository | AMS creates a repository, and the folder is linked to it with its `main` checked out | The folder is linked to the repository, whose `main` is checked out into it |
| A git repository without a remote | AMS creates a repository, which msagent adds as `origin`. Without commits, its `main` is checked out; with commits, the folder's history replaces the repository's initial commit | The repository is added as `origin` and its `main` is checked out |
| A clone of a GRS repository | That repository | The same repository, or [19055](ErrorCodes/19055.md) |

GRS starts every new repository with an initial commit that holds a `README.md`, so msagent links a new
repository the way it links an existing one: the agent's commit lands on that initial commit, and the
push fast-forwards `main`. A git repository that already has commits keeps them; its push replaces
only the initial commit (`git push --force-with-lease`), and git refuses it if `main` has changed
since.

msagent checks the agent's name, `--schema-name`, `--mcs-agent-type` and `--init-template` before it
reads or changes a repository, so a value it refuses asks nothing and clones or links nothing.
msagent asks before it clones, links, or creates a repository, for example
`Create a GRS repository in environment 'Contoso Dev' for <folder> and push agent 'Support agent' to it? [Y/n]`;
global `--non-interactive` answers yes. Declining reports `cancelled` and leaves the folder as it
was. A folder below its repository's root fails with [19052](ErrorCodes/19052.md), one that holds
other files with [19053](ErrorCodes/19053.md), one whose remote is not a GRS repository with
[19054](ErrorCodes/19054.md), a repository whose `main` already has the agent's folder with
[19057](ErrorCodes/19057.md), and a link that would overwrite local files, or join an unrelated git
history, with [19058](ErrorCodes/19058.md). A folder whose unpushed commits add or change a `.mcs`
folder fails with [19059](ErrorCodes/19059.md) before msagent asks, since the push would carry that
cache to GRS; msagent's own commit leaves out every `.mcs` folder.

After the local create, msagent registers the agent in AMS under the id `.config` holds, with its
folder and, for a clone, its `develop` deployment; a clone in its source's environment also claims
the Copilot Studio agent or flow it was cloned from. msagent records the repository and folder in
`.config`, adds `/.config/agent.config.json` and `.mcs/` to the project's `.gitignore`, then commits the agent's
folder and the collection folders it declares and pushes the commit to `main`. For a clone that
claimed its source, msagent then tells AMS that the `develop` deployment holds the pushed commit, so
that later captures and remote deploys know what it holds. When the register fails, msagent removes
the new record from `.config` and the files the command added to the project folder; a failed commit
or push, or a failed deploy record, is a warning, and the agent stays registered. A link to a new
repository that git cannot finish, for example because the folder already holds a `README.md`, is a
warning too: msagent takes the link back, commits nothing, and names the git commands that finish it.
A clone of a Copilot Studio agent or flow that another AMS agent holds joins that agent instead, as
below.

The result adds the repository, whether this command created it, its git url, and the commit:

```text
  repository   <repository-id> (Contoso agents), created
  git url      https://<host>/<path>
  commit       1a2b3c4 (pushed to main)
```

A clone from the Copilot Studio agent's or flow's own environment first asks AMS which AMS agent
holds it. With none, the command runs as above. Otherwise it joins that agent: the project becomes a
clone of the holder's repository with the holder's records, and msagent registers, commits, and
pushes nothing. 

```text
# New repository for a new project
msagent agent create --agent-type MCSAgent --agent-name "Support agent" --description "Answers questions" \
  --initial-prompt "Answer support questions." --project <empty-folder>

# Existing repository, cloned into an empty folder
msagent agent create --agent-type MCSWorkflow --agent-name "Approval flow" --repository-id <repository-id> \
  --project <empty-folder>
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
| `--repository-id` | `-repid` | GUID | No | None | AMS mode only (**Gated**). GRS repository the project folder joins. See [AMS mode for agent init](#ams-mode-for-agent-init-gated). |

For an agent, the source must hold exactly one unregistered agent workspace with a readable schema
name. Its existing connection supplies the environment when present; otherwise the active
environment is required. For a workflow, the source must hold exactly one readable workflow.
Workflow registration uses the active environment and registers beneath an enclosing project when
one exists. Neither route creates a deployment.

### AMS mode for agent init (Gated)

> **Gated.** Applies only in [AMS mode](#ams-mode-gated), that is, while `AmsMcsAgents` is enabled.

With AMS mode on, `agent init` acts on the whole project folder, which must be the root of its
repository's clone, with each agent and standalone workflow in a top-level folder; a project folder
that is itself an agent's folder fails with [19060](ErrorCodes/19060.md). It chooses the repository
as `agent create` does, asks before it clones or links one, and then:

- **restores** each agent AMS holds in the repository whose folder the project has but `.config`
  does not, with its deployments, so a clone becomes a working project. In an empty or missing
  folder, `--repository-id` clones the repository first, and the folders that clone or a link to an
  existing repository checks out count too. Before it asks, it refuses a folder it would restore,
  register or record whose `.mcs` folder git tracks, and unpushed commits that add or change a `.mcs`
  folder; after a clone or link, it refuses a repository whose git tracks the `.mcs` folder of an
  agent it would restore ([19059](ErrorCodes/19059.md));
- **registers** the new agent `--agent-name` names, as main's `agent init` does, and each MCS agent
  or standalone workflow `.config` holds that AMS does not know, with its deployments; AMS learns the
  Copilot Studio agent or flow a deployment holds from its next deploy;
- **completes** an agent AMS already knows but `.config` does not record as registered, creating the
  deployments AMS lacks;
- adds `/.config/agent.config.json` and `.mcs/` to `.gitignore`, then commits the folders it restored or
  registered and pushes the commit to `main`;
- **connects** each agent it restored that Dev holds, after that commit and push, as a clone of an
  agent AMS already holds.

`--agent-name` is not needed when the run only restores or registers agents. With nothing to restore
or register, `agent init` works as without AMS; after a clone into an empty or missing folder, it
then fails as without AMS and removes the clone. When AMS refuses the register, `agent init` takes
back what it did, the clone or link included. The result adds the repository, git url, and commit
lines of `agent create`, and one `restored` line per restored agent. Its `connected` field, and its
`connected to Copilot Studio` line, say whether the agent's folder is connected.

A top-level agent or workflow folder that `.config` doesn't list and the run doesn't restore is a new
agent, which needs `--agent-name`. Without it, `agent init` fails with [99002](ErrorCodes/99002.md),
names the folder, and takes back any clone or link. If the folder's agent was deleted, remove the
folder from the repository instead.

A teammate starts with one command, which clones the repository into a new folder and restores and
connects its agents, or with `git clone` and then `agent init` in the clone. Cloning one of its
agents from Copilot Studio with `agent create` into a new folder joins it the same way:

```text
msagent agent init --repository-id <repository-id> --mcs-agent-source contoso-agents

git clone <git-url> contoso-agents
cd contoso-agents
msagent agent init

msagent agent create --agent-type MCSAgent --mcs-clone-agent-id <copilot-studio-agent-id> --project contoso-hr
```

## `msagent agent list` - MCS-specific

Lists cloud MCS agents in an environment or across the tenant, or standalone workflows when
`--agent-type MCSWorkflow` is supplied. Local-only projects that have never reached the service
are not listed.

```text
msagent agent list [--environment-id <id> | --tenant-wide] [--limit <number>] [--project <directory>] \
  [--agent-type <MCSAgent|MCSWorkflow>] [--filter <text>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Enclosing project, else current directory | The project directory used to annotate matching cloud rows. |
| `--environment-id` | `-eid` | String | Active environment | List this environment instead of the active one. |
| `--agent-type` | `-at` | `MCSAgent` (alias `mcs`) or `MCSWorkflow` | `MCSAgent` | Select agents or standalone cloud flows. |
| `--filter` | `-flt` | String | None | Show workflows whose name contains this text, taken literally: `%` and `_` match themselves. Rejected for MCSAgent. |
| `--tenant-wide` | `-tw` | Flag | Off | List every environment the signed-in account can reach. |
| `--limit` | `-lim` | Whole number | `20` | Maximum rows to return across the selected scope. Must be positive. |
| `--view` | `-vw` | `cloud` or `ams` | `cloud` | **Gated** (`AmsMcsAgents`). `ams` lists the environment's AMS records instead. |
| `--repository-id` | `-repid` | GUID | None | **Gated** (`AmsMcsAgents`). With `--view ams`, list only the agents of this repository. |

`--environment-id` and `--tenant-wide` cannot be combined. Output includes cloud identity,
environment, lifecycle/workflow state, and last-change time. When a cloud row matches this project's
records, it also carries the managed-agent ID and deployment count. The result reports truncation.
Tenant-wide listing continues past individual environment failures and reports those diagnostics;
it does not promise a complete tenant inventory when the limit is reached. A listing that cannot be
read to its end (a malformed page, a page link that repeats or leaves the organization, or more than
1,000 pages) fails rather than showing the rows read before it; under `--tenant-wide` it becomes that
environment's diagnostic.

With AMS mode on (**Gated**), `--view ams` lists the AMS records of the environment's MCS agents and
standalone workflows, deployed or not: AGENT ID, DISPLAY NAME, TYPE, SCHEMA NAME, REPOSITORY ID,
FOLDER, HOME DEPLOYMENT, DEPLOYMENTS, and CLOUD STATE (`NotDeployed` until a deployment of the agent
completes its first deploy, then `Deployed`), with ENVIRONMENT added under `--tenant-wide`. It lists
both types unless `--agent-type` is given, and `--repository-id` keeps one repository's agents.
`--view cloud`, the default, is the listing above.

```text
msagent agent list --view ams --repository-id <repository-id>
```

## `msagent agent show`

Shows the managed-agent records a project holds. The command reads the local configuration and
contacts nothing. It includes each selected record's deployments and supports both agents and
standalone workflows.

```text
msagent agent show [--project <directory>] [--agent-id <id> | --agent-name <name>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Enclosing project, else current directory | The project directory to read. |
| `--agent-id` | `-aid` | String | None | The record to show. Wins over `--agent-name`. Every record is shown when no selector is supplied. |
| `--agent-name` | `-an` | String | None | The record to show, by name. |

With AMS mode on (**Gated**), the result adds, for each agent AMS knows, its `repositoryId`,
`folder`, and `gitUrl`, and for each of its deployments the `lastCommit` it deployed, from one AMS
read. When AMS cannot be read, the local records are shown with a warning.

## `msagent agent pull` - MCS-specific

Brings remote content into a registered project's workspace.

```text
msagent agent pull [--project <directory>] [--agent-id <id> | --agent-name <name>]

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
| `--overwrite` | `-ow` | Flag | Off | MCSWorkflow only. Replace local workflow edits not yet seen by the environment. |

An MCSAgent pull requires an existing workspace connection and reads that connection's environment,
not the active environment or a deployment. It merges remote changes into local content, and pulls
declared component collections only when they are connected to the same environment.
Deployment selectors and `--overwrite` are rejected for MCSAgent.
Merge conflicts are reported in the result/warnings rather than making the pull fail.

A standalone workflow pull resolves a deployment (the sole matching deployment may be implicit)
and replaces the local workflow from its environment, refusing unpushed local edits unless
`--overwrite` is supplied. It can recreate missing workflow files. It does not merge those edits.
The result reports the environment, changes, and whether the workspace was already up to date.

With AMS mode on (**Gated**), a folder that a clone joining its agent or `agent init` connected
pulls at once. A folder that neither could connect has no connection until a deploy to Dev connects
it: from the folder with `msagent agent deploy`, or from GRS with `msagent agent deploy --remote`
when the folder holds exactly the deployed commit. Until then, an MCSAgent pull of an agent AMS
registered fails with [10011](ErrorCodes/10011.md) and says how to bring Copilot Studio changes into
the repository instead: `msagent agent capture`, then `git pull`.

With AMS mode on (**Gated**), msagent keeps a copy of each folder's `.mcs` for every commit it
synced at, under the clone's git folder, and a pull merges against the copy for the commit the
folder holds, or for the nearest commit it builds on. When neither ever synced but the commit
Copilot Studio was last synced from builds on it, msagent first restores the folder's files to that
commit and a warning says so (a standalone workflow pull replaces the workflow instead); a folder
with uncommitted changes fails with [19071](ErrorCodes/19071.md), and a commit that lacks the synced
one fails with [19072](ErrorCodes/19072.md).

## `msagent agent delete` - MCS-specific

Deletes one cloud MCS agent or standalone workflow and removes its managed-agent record and all its
deployment records. Local workspace files are never touched. Cloud copies in other environments
are not deleted; the result reports the targets left orphaned.

```text
msagent agent delete [--project <directory>] [--agent-id <id> | --agent-name <name>] \
  [--deployment-id <id> | --deployment-name <name>]
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Enclosing project, else current directory | The project directory to act on. |
| `--agent-id` | `-aid` | String | None | The record to delete. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | The record to delete, by name. |
| `--deployment-id` | `-did` | String | None | Chooses which cloud agent is deleted. Wins over `--deployment-name`. |
| `--deployment-name` | `-dn` | String | None | Chooses which cloud agent is deleted, by name. |

For an MCSAgent, an omitted deployment uses the workspace's connection when present, otherwise its
home environment and schema name. If its workspace is gone, only the records are removed. For a
standalone workflow, an omitted deployment uses its home environment; an explicit deployment
selects that environment's copy of the workflow.

The command asks the operator to type the managed record's display name before deleting. Global
`--non-interactive` supplies consent; there is no command-specific `--force`. A mismatch reports
`name-mismatch`, and declining reports `cancelled`; neither deletes anything or counts as an error.

With AMS mode on (**Gated**), deleting an agent AMS knows also deletes its AMS records, after the
cloud delete and before the `.config` records are removed: the agent, its deployments, and its holds
on its repository folder and on Copilot Studio agents and flows. The repository and its content,
and the local folder, stay; msagent drops the copies of the cloud cache it kept for the agent's
folders. msagent deletes the AMS record only while it is the one it read, so a
concurrent change fails with [19069](ErrorCodes/19069.md) and removes nothing from `.config`; an
agent AMS no longer holds counts as deleted. The result adds `ams record   deleted` and
`repository   <repository-id> (kept)`.

## `msagent agent deploy`

Deploys an agent or standalone workflow to an existing named deployment. There is no
active-environment fallback on these MCS routes: both require `--deployment-id` or
`--deployment-name`. Create a slot first with `msagent deployment create` (cloning already creates
a `dev` slot). A deployment selector can identify its owning record without an agent selector.

```text
# MCS agent
msagent agent deploy (--deployment-id <id> | --deployment-name <name>) \
  [--project <directory>] [--agent-id <id> | --agent-name <name>] [--publish] [--overwrite]

# A GRS commit through AMS (Gated; AMS mode)
msagent agent deploy (--deployment-id <id> | --deployment-name <name>) --remote [--commit-id <commit>] \
  [--project <directory>] [--agent-id <id> | --agent-name <name>] [--publish] [--overwrite] [--async]

# AMS package revision (Gated)
msagent agent deploy --agent-id <id> --package-revision <revision-id> \
  --target-environment-id <id> --deployment-name <name> --deployment-type <dev|test|prod> \
  --entra-id <application-id> --entra-id-type <app-reg|blueprint|default> [--environment-id <id>]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | **MCS-specific** | The local MCS project directory. |
| `--agent-id` | `-aid` | String | All routes | The agent to deploy. |
| `--agent-name` | `-an` | String | **MCS-specific** | The record to deploy, by name. |
| `--deployment-id` | `-did` | String | **MCS-specific** | The deployment to target. Wins over `--deployment-name`. |
| `--deployment-name` | `-dn` | String | MCS and AMS routes | The deployment to target, by name. Required for AMS package-revision deployment. |
| `--schema-name` | `-sn` | String | **MCS-specific** | Overrides the project's schema name for this deploy. A workflow is addressed by its workflow id, so a workflow deploy rejects this option before anything is uploaded. |
| `--publish` | `-pub` | Flag | **MCS-specific** | Publish an agent after a push that fully landed and wait up to 5 minutes for Copilot Studio to finish publishing, or request activation of a standalone workflow. Off by default. With `--remote`, a Dev deploy publishes only with it, and Test and Prod deploys always publish. |
| `--overwrite` | `-ow` | Flag | **MCS-specific** | Overwrite cloud content that changed since the last sync, and accept replacing an existing agent or workflow this project cannot show is its own. Only a dev deployment refuses either without it; Test and Prod deployments replace the cloud copy. With `--remote`, a Dev deploy replaces the edits made in Copilot Studio only with it, after saving them. |
| `--commit-id` | `-cid` | String | Not supported; with `--remote` (**Gated**) | This build has no Git Repository Service, so deploy always uploads the current working tree. Supplying it is a usage error. While `AmsMcsAgents` is enabled, it names the commit `--remote` deploys, full or short; Dev and Test only, since Prod takes Test's last deploy. Without `--remote` it stays a usage error. |
| `--async` | `-as` | Flag | Not supported; with `--remote` (**Gated**) | The deploy is synchronous and returns when the push completes. Supplying it is a usage error. While `AmsMcsAgents` is enabled, `--remote --async` returns once AMS accepts the deploy, with its operation id. |
| `--remote` | `-rem` | Flag | AMS mode only (**Gated**) | Deploy a commit from GRS through AMS instead of the working folder. Listed only while `AmsMcsAgents` is enabled. |
| `--package-revision` | `-pr` | String | **Gated** | Existing immutable AMS Package Revision id. No package bytes are uploaded. |
| `--environment-id` | `-eid` | String | **Gated** | AMS Agent home environment. Defaults to the active environment. |
| `--target-environment-id` | `-tenv` | String | **Gated** | Explicit AMS target environment, including for same-home deployment. |
| `--deployment-type` | `-dt` | `dev`, `test`, or `prod` | **Gated** | AMS deployment stage. |
| `--entra-id` | `-ent` | String | **Gated** | AMS deployment Entra application id. |
| `--entra-id-type` | `-eit` | `app-reg`, `blueprint`, or `default` | **Gated** | AMS identity kind. |

### MCS deployment behavior

- The current working tree is uploaded synchronously. An MCSAgent deploy creates or adopts the
  target's cloud agent, reconnecting/retargeting the workspace when needed. Existing read-only
  managed agents are rejected. Adoption must be verifiable as this project's agent, or explicitly
  acknowledged with `--overwrite`.
- Declared component collections are pushed first when connected to the target environment.
  Missing or differently connected collections produce warnings.
- For an MCSAgent, deployment records are updated even when content or workflow-activation warnings remain.
  With `--publish`, content/activation blockers withhold publication and fail the command after
  recording the deploy; informational notices alone do not prevent publication.
- With `--publish`, an MCSAgent deploy asks Copilot Studio to publish, then waits up to 5 minutes
  for Copilot Studio to report that this publish finished; the status line shows `Publishing`
  meanwhile. The deploy succeeds only when the publish finished `Succeeded`. Any other status fails
  with [17033](ErrorCodes/17033.md), naming the status, and so does a publish whose progress cannot
  be read, giving the reason; that publish may have finished, so check it. No result within
  5 minutes fails with [17018](ErrorCodes/17018.md). Neither the timeout nor Ctrl+C cancels the
  publish: it keeps running in Copilot Studio, so run the deploy again to confirm it finished. A
  publish request the service rejects is retried once after 5 seconds, then fails with
  [14010](ErrorCodes/14010.md). Each of these failures is reported after the deploy was recorded.
- A standalone workflow deploy uploads over the target's copy by workflow ID. A dev deployment
  first refuses changes made in the environment since the last pull ([11056](ErrorCodes/11056.md)).
  On a dev deployment outside the record's home environment that never deployed it, it also refuses
  a workflow the environment already holds, such as the source of a workflow cloned from another
  environment, which keeps its source's workflow ID; deploying there would replace that workflow's
  content and name ([14003](ErrorCodes/14003.md)). `--overwrite` accepts replacing either; Test and
  Prod deployments always replace the copy. It rejects `--schema-name`. Activation is attempted
  when requested by the local workflow or `--publish`; unbound connections can leave it in draft
  with warnings. Bind them with `msagent deployment connection update` before retrying.
- Output identifies the record, deployment, target environment, action, pushed counts, publication
  or workflow state, and warnings. An MCSAgent deploy's `published` line says whether the deploy
  published the agent: `yes — the content is live`, or, without `--publish`,
  `no — this deploy's content is a draft; the version published 2026-10-25 19:02:38Z stays live. Re-run with --publish to make it live.`
  for an agent published before, naming when the version that stays live was published, and
  `no — the content is a draft. Re-run with --publish to make it live.` for one never published.
  The JSON carries that time as `lastPublishedTime`: when Copilot Studio finished this deploy's
  publish, or else when the agent was last published as the push read it, or `null`.

### AMS mode for agent deploy (Gated)

> **Gated.** Applies only in [AMS mode](#ams-mode-gated), that is, while `AmsMcsAgents` is enabled.

**A deploy from the working folder** runs as described above, then tells AMS about it: the
deployment, the Copilot Studio agent or flow it wrote, whether it published, and the commit the
folder held. msagent reads the commit with git: the folder's `HEAD` when it is pushed, else the
newest pushed commit it builds on, marked exact only when the agent's folder and its collection
folders hold no uncommitted changes and `HEAD` is pushed. Later captures and remote deploys merge
Dev's edits from that commit. When git cannot tell the commit, AMS gets the deploy without one and a
warning says so; when AMS refuses the record or cannot be reached, the deploy stands and a warning
says AMS does not know about it. For an agent AMS does not know, a warning names `msagent agent init`.
The deploy compares the folder with the copy of its `.mcs` kept for the commit it holds, or for the
nearest commit it builds on, and saves a copy for that commit afterwards, so after switching
branches it asks for `msagent agent pull` or `--overwrite` only when Copilot Studio changed since
that commit last synced.

**`--remote`** deploys a commit of the agent's GRS repository through AMS instead, for an agent AMS
knows ([19062](ErrorCodes/19062.md) otherwise), to one of its deployments. The working folder's
content is not read. msagent sends only what you gave; AMS chooses the commit for each stage and
applies the stage rules:

| Stage | With `--commit-id` | Without it |
| --- | --- | --- |
| Dev | Any commit of the repository that holds the agent's folder | `main`'s newest commit |
| Test | A commit Dev deployed ([19002](ErrorCodes/19002.md) otherwise) | The commit Dev deployed last |
| Prod | Refused ([19063](ErrorCodes/19063.md)) | The commit Test deployed last |

With nothing deployed to the preceding stage, Test and Prod fail with [19004](ErrorCodes/19004.md).
In a clone, msagent resolves `--commit-id` with git, so a short id works; a commit AMS cannot find
fails with [19065](ErrorCodes/19065.md). A Dev deploy first saves the edits made in Copilot Studio
since Dev's last deploy or capture as a commit on `main`, and refuses a commit that lacks them
([19051](ErrorCodes/19051.md)) unless `--overwrite` replaces them; a Dev deployment whose commit AMS
does not know is refused without `--overwrite` ([19064](ErrorCodes/19064.md)). Uncommitted or
unpushed changes in the agent's folder do not block a remote deploy; a warning says they are not in
it. A remote deploy sets no connection bindings or environment-variable values, and keeps those the
environment holds.

msagent waits for the operation, at most 45 minutes, unless `--async` returns once AMS accepted it.
Ctrl+C stops only the wait ([19029](ErrorCodes/19029.md)). On success, `.config` records the deploy
as a deploy from the folder does. A finished MCSAgent deploy to Dev also connects the agent's folder,
and the collection folders it declares, to Dev's Copilot Studio agent, so that `agent pull` there
brings in the edits made in Copilot Studio, but only when the folder holds exactly the deployed
commit: as git read it before the deploy, its `HEAD` is that commit and is pushed, and the agent's
folder and its collection folders hold no uncommitted changes. The connect writes only `.mcs`,
refreshes a folder already connected to Dev's agent, and records Dev's agent as the record's
`mcsAgentId`. A collection Dev doesn't hold yet leaves its folder unconnected, and a warning names
the folder. When the folder holds anything else, such as another branch's commit or uncommitted
edits, its `.mcs` is left exactly as it is, so that `agent pull` still brings in what changed in
Copilot Studio since the folder's last sync, and the JSON's `workspaceNotice` says why: a folder
that isn't connected is connected by a later Dev deploy of the commit it holds, and `agent pull`
brings the deployed changes into a connected one. An agent's folder connected to another
environment is left as it is without a message, and a Test or Prod deploy, `--async`, or a
standalone workflow connects nothing. When the connect fails, for example because a folder is
connected to another agent, collection or environment, the deploy still counts and `.config` still
records it; a warning gives the reason and says to run `msagent agent capture`, then `git pull`, to
bring Copilot Studio changes into the repository.

The result names the commit deployed and the operation, as a deploy from the folder does. With
`--json`, `capturedCommitSha` names the commit on `main` that holds Dev's edits when the deploy first
saved them, `workspaceConnected` says whether it connected the folder, and `workspaceNotice` says why
it left the folder as it was:

```text
Deployed Support agent to development (dev) from commit 1a2b3c4.
  commit       1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b
  operation    <operation-id>
  environment  <dev-environment-id>
  published    no — the content is a draft. Re-run with --publish to make it live.
```

```text
Deployed Support agent to test (test) from commit 1a2b3c4.
  commit       1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b
  operation    <operation-id>
  environment  <test-environment-id>
  published    yes — the content is live
```

```text
msagent agent deploy --deployment-name development --remote --commit-id 1a2b3c4
msagent agent deploy --deployment-name test --remote
msagent agent deploy --deployment-name production --remote --async
```

Gated behavior:

- The AMS options and the package-revision route are listed in help only when `AmsFeatures` is
  enabled. When it is off, they are rejected as unrecognized options.
- A CodeAgent's package bytes must first be uploaded with
  [`msagent agent package upload`](#msagent-agent-package-upload-gated) (**Gated**), then deployed
  with `--agent-id`, `--package-revision`, and explicit deployment inputs. `--project` alone does not
  deploy a CodeAgent.
- Supplying any AMS-only option selects the AMS route, even `--environment-id` alone. Every input
  shown in the AMS syntax above is required except the home `--environment-id`; MCS-only options
  cannot be mixed into that route. The target must belong to the home environment's tenant.
- AMS agent, package-revision, and Entra application IDs must be non-empty GUIDs; the deployment
  name is limited to 64 characters. Stage and identity-kind values are case-insensitive;
  `appreg` is also accepted for `app-reg`.
- The AMS route creates desired deployment state using an existing package revision and waits for
  its deployment projection acknowledgement. It does not upload bytes or promise runtime readiness.

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

## `msagent agent capture` (Gated)

> **Gated.** Available only in [AMS mode](#ams-mode-gated), that is, while `AmsMcsAgents` is enabled.

Commits the edits made in the Copilot Studio agent, or standalone cloud flow, of an agent's Dev
deployment to the agent's GRS repository, through AMS. AMS merges Dev's edits since its last deploy
or capture with the commits made on `main` since then, commits the result to `main`, and keeps
Dev's side on the branch `ams/captures/<deployment-id>`. Run `git pull` afterwards to bring the
commit into your clone.

```text
msagent agent capture [--project <directory>] [--agent-id <id> | --agent-name <name>] \
  [--message <message>] [--resolve cloud] [--async]
msagent agent capture --agent-id <id> --environment-id <home-environment-id>
```

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | Current directory | Project directory whose `.config` names the agent. |
| `--agent-id` | `-aid` | String | None | Agent to capture. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | None | Agent to capture, by name. |
| `--environment-id` | `-eid` | String | None | The agent's home environment, which selects the agent by `--agent-id` outside its project and reads its Dev deployment from AMS. Takes neither `--project` nor `--agent-name`. |
| `--message` | `-msg` | String | None | Commit message. |
| `--resolve` | `-res` | `cloud` | None | Conflict resolution: Dev's side wins every conflict with a newer commit; the other changes of both sides merge as usual. |
| `--async` | `-as` | Flag | Off | Return once AMS accepts the capture, without waiting for it. |

The agent needs a Dev deployment ([19031](ErrorCodes/19031.md)) whose Copilot Studio agent or flow
exists ([19038](ErrorCodes/19038.md)), and AMS must know which commit Dev holds
([19064](ErrorCodes/19064.md)). Edits and a newer commit that change the same lines fail with
[19050](ErrorCodes/19050.md) unless `--resolve cloud`. The result names the commit, the commit it
merged from, and the operation; Dev without edits reports `Dev of agent <agent-id> holds nothing to capture.`

```text
Captured Dev of agent <agent-id> as commit 1a2b3c4.
  commit       1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b
  base         9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c3b2a1f0e
  deployment   <dev-deployment-id>
  operation    <operation-id>
```

## `msagent agent operation show` (Gated)

> **Gated.** Available only in [AMS mode](#ams-mode-gated), that is, while `AmsMcsAgents` is enabled.

Shows a deploy or capture operation that AMS runs for an agent: its status, deployment, source and
result commits, who started it and when, and, for a failed one, the error and what to do about it.
The agent comes from the project's `.config`, or from `--agent-id` with `--environment-id` outside
its project.

```text
msagent agent operation show --operation-id <operation-id> [--project <directory>] \
  [--agent-id <id> | --agent-name <name>] [--environment-id <home-environment-id>]
```

| Option | Shortcut | Value | Required | Description |
| --- | --- | --- | --- | --- |
| `--operation-id` | `-opid` | String | Yes | Operation to show. |
| `--project` | `-p` | Directory path | No | Project directory whose `.config` names the agent. Defaults to the current directory. |
| `--agent-id` | `-aid` | String | No | Agent the operation belongs to. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | No | Agent the operation belongs to, by name. |
| `--environment-id` | `-eid` | String | No | The agent's home environment, which selects the agent by `--agent-id` outside its project. |

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

The agent ID must be a non-empty GUID and identify a repository-bound CodeAgent. The file must be
readable, non-empty, and no larger than 128 MiB (134,217,728 bytes). The CLI hashes the exact bytes
with SHA-256 and verifies the service's agent, digest, size, successful operation, and revision ID
before reporting success. Output includes the package operation ID, package revision ID, digest,
and size. Uploading does not create a deployment; use the returned revision with `agent deploy`.
