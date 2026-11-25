# MSAgent CLI deployment commands

[Back to command index](ReadMe.md)

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent deployment create`](#msagent-deployment-create) | Create a CodeAgent or MCS agent deployment record, or an AMS code-agent deployment record (**Gated**). CodeAgent options are **Gated**. |
| [`msagent deployment list`](#msagent-deployment-list) | List an agent's deployment records, in promotion order. Local, or the deployments of an AMS code agent (**Gated**). |
| [`msagent deployment update`](#msagent-deployment-update) | Rename an MCS deployment, update a CodeAgent deployment, or rename an AMS deployment or change its dev endpoint (**Gated**). |
| [`msagent deployment delete`](#msagent-deployment-delete) | Delete a CodeAgent or MCS agent deployment record, and optionally the MCS cloud agent, or the record of an AMS deployment (**Gated**). |
| [`msagent deployment show`](#msagent-deployment-show) | Get an existing CodeAgent or MCS agent deployment, or an AMS deployment (**Gated**). |
| [`msagent deployment connection`](Command_Deployment_Connection.md) | **MCS-specific.** Group: `list`, `add`, `update`, `remove`, and `connectors`. Inspect and bind connection references and create connections. See [Command_Deployment_Connection.md](Command_Deployment_Connection.md) for details. |
| [`msagent deployment oauth`](Command_Deployment_OAuth.md) | **CodeAgent-specific.** Group: `create`, `delete`, `list`, `list-providers`, `show`, and `update`. Manage OAuth connection settings. See [Command_Deployment_OAuth.md](Command_Deployment_OAuth.md) for details. |
| [`msagent deployment channel`](Command_Deployment_Channel.md) | **CodeAgent-specific.** Group: `create`, `update`, `show`, `list`, and `delete`, plus `types`, `token`, and `webhook` (**Gated**). Manage channels configuration. |

`msagent deployment create`, `list`, `show`, `update`, and `delete` apply to CodeAgent and Microsoft
Copilot Studio agents and standalone MCS workflows unless an option says otherwise. The `connection` group is **MCS-specific** and
the `channel` and `oauth` groups are **CodeAgent-specific**. The `connection` group replaces the former
`deployment update connection` command.

> **Gated.** Items tagged **Gated** may or may not be available, depending on the feature gates
> enabled for a given run. Review `msagent deployment <command> --help` to see what is currently
> available. No deployment command or group is gated as a whole, but:
>
> - The CodeAgent options (Azure Bot Service resources, endpoint, display name, description, SKU,
>   `--default`, and so on) on `create`, `show`, `update`, and `delete` are gated by
>   `AzureBotServiceFeatures`. When it is off they are not listed in help, and CodeAgent
>   deployment create, show, update, and delete fail with a message that the capability is
>   disabled. `--entra-id` and `--entra-id-type` on `create`, and `--endpoint-url` on `create` and
>   `update`, serve both the Azure Bot Service and the AMS routes: they are listed when either
>   `AzureBotServiceFeatures` or `AmsFeatures` is on, and each route still requires its own feature.
> - The AMS routes are gated by `AmsFeatures`: `--target-environment-id` on `create`, `--environment-id`
>   on `list`, `show`, `update`, `delete`, and the `channel` and `oauth` commands, the
>   `--package-revision` pointer on `update`, the `channel types`, `channel token`, and `channel webhook`
>   commands, and the AMS options of `oauth list-providers`. With `AmsFeatures` off, every command routes
>   as it does without AMS.
> - The `channel`, `connection`, and `oauth` groups have no feature gate of their own. Channel and
>   OAuth deployment operations still require an existing local CodeAgent deployment record, except
>   the AMS route of the `channel` and `oauth` commands.
>
> Gated features are enabled through the `AH_CLI_FEATURES` environment variable.

Deployment records for both agent types are stored locally in the project's `.config\agent.config.json`.
Environment resolution for MCS create uses the signed-in session; reselecting the environment refreshes older selections.

CodeAgent lifecycle, channel, and OAuth mutations contact Azure before saving their local result.
A later local-save failure does not roll back the cloud change. Inspect the deployment or connection
before retrying. CodeAgent `show`, channel `show`/`list`, and OAuth `show`/`list` also refresh local
records; they are not read-only local inspections. With `--debug`, these deployment results are written
as a summary or redacted ARM response to stderr instead of the normal human/JSON success payload,
even when `--json` is supplied.

## Resolving the agent and project

`--project` defaults to the current directory. The MCS routes read `<project>\.config\agent.config.json`
and fail when it does not exist. The agent is resolved in this order:

1. `--agent-id`, when supplied. If the configuration holds exactly one agent and its ID differs, the
   command fails with an agent-ID conflict error; otherwise an unknown ID is "agent not found".
2. `--agent-name` (`create`, `list`, `update`, `delete`, `show`; compared case-insensitively). Zero matches is "not found";
   several is "ambiguous".
3. Otherwise the single agent in the configuration. No agent is "not found"; more than one is "ambiguous".

The MCS routes accept MCS agents and standalone MCS workflows; a CodeAgent is rejected.

## AMS code-agent deployments (Gated)

With `AmsFeatures` on, `list`, `show`, `update`, `delete`, and the `channel` and `oauth` commands reach the
deployments of an AMS code agent when they name an agent (`--agent-id` or `--agent-name`) and the local project
holds no record of that agent, nor a deployment record of that agent id with the deployment named (so an Azure Bot
Service deployment of the project keeps its local route), or when `--environment-id` (the agent's home environment)
is given; `create`
does so with `--target-environment-id`. The agent and deployment may be named by id or by name, and
`--environment-id` defaults to the active environment. See
[AMS code agents](Command_Agent.md#ams-code-agents-gated) for how the route is chosen.

## `msagent deployment create`

Creates a named deployment record. An MCS agent's record is stored in the local project configuration
(`agent.config.json`); it resolves the environment through the signed-in session but does not mutate the cloud. A CodeAgent deployment provisions an Azure
Bot Service resource and is **Gated**. The route is chosen from the resolved agent type, or AMS by
`--target-environment-id` (**Gated**).

```text
msagent deployment create [--agent-id <id> | --agent-name <name>] --deployment-type <dev|test|prod> \
  --deployment-name <name> [--environment-id <id>] [--project <directory>]

# AMS code agent (Gated)
msagent deployment create (--agent-id <id> | --agent-name <name>) --target-environment-id <id> \
  --deployment-name <name> --deployment-type <dev|test|prod> --entra-id <application-id> \
  --entra-id-type <app-reg|blueprint|default> [--endpoint-url <url>] [--environment-id <id>]
```

| Option | Shortcut | Value | Required | Default | Applies to | Description |
| --- | --- | --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | No | Current directory | MCS and Azure Bot Service | The project directory holding the configuration. |
| `--agent-id` | `-aid` | String | Conditional | The project's agent (MCS) | All agents | The owning agent id. Required for a CodeAgent deployment (a GUID), unless AMS gets `--agent-name`. |
| `--agent-name` | `-an` | String | Conditional | None | MCS and AMS | The owning agent name, used when the agent id is omitted. |
| `--deployment-name` | `-dn` | String | Yes | None | All agents | Deployment name, unique within the agent: 4-42 characters from `-`, `a-z`, `A-Z`, `0-9`, and `_`, after trimming. AMS allows up to 64 characters. |
| `--deployment-type` | `-dt` | `dev`, `test`, or `prod` | Yes | None | All agents | The stage. Values are case-insensitive. |
| `--environment-id` | `-eid` | String | Conditional | The agent's home environment (MCS); the active environment (AMS) | MCS and AMS | MCS: target environment, required for `test` and `prod`. AMS: the agent's home environment. |
| `--target-environment-id` | `-tenv` | String | Yes (AMS) | None | AMS only (**Gated**) | The environment the AMS deployment runs in, in the home environment's tenant. Selects the AMS route. |
| `--entra-id` | `-ent` | GUID | Yes (CodeAgent) | None | Azure Bot Service and AMS (**Gated**) | The Entra application (client) id the deployment binds to; for an AMS `blueprint`, the id of an agent identity created from the blueprint. |
| `--entra-id-type` | `-eit` | `appReg`; on AMS `app-reg`, `blueprint`, or `default` | Yes (CodeAgent) | None | Azure Bot Service and AMS (**Gated**) | Entra identity kind. Azure Bot Service supports only `appReg`. |
| `--sku` | `-sku` | `F0` or `S1` | No | `F0` | CodeAgent only (**Gated**) | Deployment SKU. |
| `--location` | `-loc` | String | No | `global` | CodeAgent only (**Gated**) | Azure region. |
| `--resource-group` | `-rg` | String | No | Auto-resolved when the subscription has exactly one | CodeAgent only (**Gated**) | Target resource group. |
| `--subscription` | `-sub` | GUID | No | Auto-resolved from the signed-in account | CodeAgent only (**Gated**) | Target subscription id. |
| `--tenant` | `-t` | GUID | No | From the resolved subscription | CodeAgent only (**Gated**) | Target tenant id. |
| `--endpoint-url` | `-eu` | HTTPS URL | No | None | Azure Bot Service and AMS (**Gated**) | Messaging endpoint. On AMS, the dev endpoint of a `dev` deployment, which `msagent agent deploy` without `--commit-id` puts live. |
| `--display-name` | `-disp` | String | No | Deployment name | CodeAgent only (**Gated**) | Display name. |
| `--description` | `-d` | String | No | None | CodeAgent only (**Gated**) | Description. |
| `--default` | `-def` | Flag | No | `false` | CodeAgent only (**Gated**) | Mark the deployment as the agent's default. |

CodeAgent creation rejects MCS-only options; MCS creation rejects CodeAgent-only values.
CodeAgent value-bearing options reject explicitly blank values.


### Create rules

- `--deployment-name` and `--deployment-type` are required. The type is case-insensitive.
- The name may use only `-`, `a-z`, `A-Z`, `0-9`, and `_`, and is 4-42 characters for every agent type. A
  clone registers its home slot as `develop`. A name outside the rule is a usage error and nothing is
  written. The rule applies only when a slot is created or renamed, so a record whose name predates it,
  such as the `dev` slot an earlier clone registered, still resolves.
- A `test` or `prod` deployment requires `--environment-id`; a `dev` deployment defaults to the agent's home environment.
- The name must be unique per agent, compared case-insensitively.
- An MCS agent can have at most one deployment per environment, whatever the type. Creating a second
  record for the same agent and environment is rejected.
- Both rules are enforced when the configuration is written, so a violating create changes nothing.
- `--default` is CodeAgent-only; at most one deployment per agent is the default.

### Creating an MCS agent deployment

The agent comes from `--agent-id`, `--agent-name`, or the project configuration. A `dev` deployment targets the agent's
home environment unless `--environment-id` is given; `test` and `prod` require `--environment-id`. The
target environment is resolved through the signed-in session. The command adds a record (generated
deployment ID, type, environment, creator) to `agent.config.json` and returns the deployment ID,
name, type, environment and schema name. No cloud agent is created or adopted.
For a standalone workflow, no Copilot project schema is required and the schema name is omitted.

```text
msagent deployment create --deployment-type dev --deployment-name "Development"
msagent deployment create --agent-name "My agent" --deployment-type dev --deployment-name "Development"
msagent deployment create --agent-id <agent-id> --deployment-type test \
  --deployment-name "Integration" --environment-id <environment-id> --project <directory>
```

### Creating a CodeAgent deployment (Gated)

Requires `AzureBotServiceFeatures`. Requires `--agent-id`, `--deployment-name`, `--deployment-type`,
`--entra-id`, and `--entra-id-type`; `--environment-id` and `--agent-name` are rejected. The deployment ID is derived from the
agent ID and name, so repeating the command adopts the existing bot instead of provisioning a duplicate.
The result is written to the local project configuration. A retry with an existing record reuses that
record's subscription, resource group, and tenant rather than resolving new coordinates. If the bot
already exists, supplied create settings do not update it; use `deployment update` for changes.

```text
msagent deployment create --agent-id <agent-id> --deployment-type dev \
  --deployment-name "Local-dev" --entra-id <app-id> --entra-id-type appReg
```

### Creating an AMS code-agent deployment (Gated)

Requires `AmsFeatures`. `--target-environment-id` selects this route, which requires the agent
(`--agent-id` or `--agent-name`), `--deployment-name`, `--deployment-type`, `--entra-id`, and
`--entra-id-type`; `--environment-id` names the agent's home environment and defaults to the active one.
`--project` and the Azure Bot Service options (`--sku`, `--location`, `--resource-group`, `--subscription`,
`--tenant`, `--display-name`, `--description`, `--default`) are refused. The target environment must belong
to the home environment's tenant, and the agent must be a repository-bound code agent: msagent reads it
and sends its language and language version with the record.

```text
msagent deployment create --agent-name "Contoso Agent" --target-environment-id <environment-id> \
  --deployment-name dev-proof --deployment-type dev --entra-id <app-id> --entra-id-type app-reg
```

### Create result and errors

MCS: on success the record is added to `agent.config.json` and the command returns the deployment ID,
name, type, environment, and schema name. It fails, writing nothing, when the configuration or agent
cannot be resolved, the agent is not an MCS agent, `--deployment-type` is not `dev`, `test`, or `prod`,
`--deployment-name` is missing, a `test`/`prod` deployment has no `--environment-id`, the environment cannot be resolved, the
MCS agent project does not declare a schema name, the name is already used by the agent, or the agent already has a
deployment in that environment. A CodeAgent-only option on an MCS agent (or an MCS-only option such as
`--environment-id` or `--agent-name` on a CodeAgent) is a usage error. When the project holds a configuration but nothing in it
identifies the agent — no matching deployment record, and no single agent — `create` does not fall back to
the CodeAgent route: it fails with a usage error asking for `--agent-id or --agent-name`.

CodeAgent: fails before any Azure call on a missing or non-GUID `--agent-id`, `--entra-id`, or
`--subscription`/`--tenant`; an invalid name; an unsupported `--entra-id-type` or SKU; a blank option; or a
non-HTTPS `--endpoint-url`. Without `AzureBotServiceFeatures` it fails with a message that the capability is disabled.
A re-run reports that the bot already existed.
## `msagent deployment list`

Lists deployment slots, in promotion order (dev, test, prod).

> **Storage.** The local route of `deployment list` is entirely local: it reads the deployment records in
> the project's configuration and contacts nothing. `AzureBotServiceFeatures` does not change this command.
> With `AmsFeatures` on, the deployments of a code agent the project holds no record of are read from AMS
> (see [AMS code-agent deployments](#ams-code-agent-deployments-gated)).

```text
msagent deployment list [--agent-id <id> | --agent-name <name>] [--project <directory>]

# AMS code agent (Gated)
msagent deployment list (--agent-id <id> | --agent-name <name>) [--environment-id <id>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | No | Current directory | The project directory holding the agent. |
| `--agent-id` | `-aid` | String | No | Every agent | The record whose deployments are listed. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | No | Every agent | The record whose deployments are listed, by name. |
| `--environment-id` | `-eid` | String | No | Active environment | **Gated** (`AmsFeatures`). The AMS agent's home environment. Selects the AMS route. |

With `--agent-id` or `--agent-name`, the agent is resolved as described in [Resolving the agent and project](#resolving-the-agent-and-project)
and only that agent's deployments are listed. With neither, every deployment the project holds is listed, ordered by agent
name, and the table gains `AGENT` and `AGENT ID` columns.

Within an agent, results are ordered by stage (`dev`, `test`, `prod`), then by deployment name (ordinal, case-sensitive). Each
result contains the deployment ID, name, type, environment ID, last-deployed time (empty when never deployed), and the owning
agent. An agent with no deployments returns an empty list. The command changes nothing and contacts no service.

```text
msagent deployment list
msagent deployment list --agent-name "My agent"
```

The AMS route reads every page of the agent's deployments once and shows, for each, its name, ID, stage,
kind (`package` or `dev-endpoint`), state, bot, and a pending endpoint change (a requested dev endpoint
that the next `msagent agent deploy` without `--commit-id` puts live; on a package deployment, that deploy
switches it to the dev endpoint).

## `msagent deployment update`

Updates an existing deployment. The route is chosen from the resolved agent type, or AMS as described in
[AMS code-agent deployments](#ams-code-agent-deployments-gated).

```text
msagent deployment update (--deployment-id <id> | --deployment-name <name> [--agent-id <id> | --agent-name <name>]) \
  [--new-name <name>] [--project <directory>]

# AMS code agent (Gated)
msagent deployment update (--agent-id <id> | --agent-name <name>) (--deployment-id <id> | --deployment-name <name>) \
  (--new-name <name> | --endpoint-url <url>) [--environment-id <id>]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | All agents | The project directory holding the configuration. |
| `--deployment-id` | `-did` | String | All agents | The deployment id. Provide this, or the agent id and deployment name. |
| `--deployment-name` | `-dn` | String | All agents | The deployment name, used with the agent id to resolve the deployment. |
| `--agent-id` | `-aid` | String | All agents | The owning agent id. |
| `--agent-name` | `-an` | String | MCS and AMS | The owning agent name, used when the agent id is omitted. |
| `--new-name` | `-nn` | String | MCS and AMS | The deployment's new name, unique among the agent's deployments. MCS: 4-42 characters from `-`, `a-z`, `A-Z`, `0-9`, and `_`; AMS: up to 64 characters. |
| `--resource-group` | `-rg` | String | CodeAgent only (**Gated**) | Target resource group. |
| `--subscription` | `-sub` | GUID | CodeAgent only (**Gated**) | Target subscription id. |
| `--tenant` | `-t` | GUID | CodeAgent only (**Gated**) | Target tenant id. |
| `--deployment-type` | `-dt` | `dev`, `test`, or `prod` | CodeAgent only (**Gated**) | New stage. |
| `--sku` | `-sku` | `F0` or `S1` | CodeAgent only (**Gated**) | New SKU. |
| `--display-name` | `-disp` | String | CodeAgent only (**Gated**) | New display name. |
| `--description` | `-d` | String | CodeAgent only (**Gated**) | New description. |
| `--endpoint-url` | `-eu` | HTTPS URL | Azure Bot Service and AMS (**Gated**) | New messaging endpoint. On AMS, the dev endpoint of a Dev deployment, which the next `msagent agent deploy` without `--commit-id` puts live, switching a package deployment to it. |
| `--default` | `-def` | Optional flag | CodeAgent only (**Gated**) | Mark the deployment as the agent's default. |
| `--package-revision` | `-pr` | | **Gated** (`AmsFeatures`), moved | Selects the AMS route, which refuses it: use `msagent agent deploy --commit-id`; `msagent agent commit list` shows the commits. |
| `--environment-id` | `-eid` | String | **Gated** (`AmsFeatures`) | AMS agent home environment. Selects the AMS route. Defaults to the active environment. |

### Updating an MCS agent deployment

A rename is the only mutation. `--new-name` is required, and the deployment type and environment cannot
be changed. The command updates the record in `agent.config.json` and changes nothing in the cloud.
`--endpoint-url` is an error for an MCS agent.
The deployment selector can be omitted when exactly one deployment remains in the selected agent's
scope (or the project when no owning agent selector is supplied); otherwise selection is ambiguous.

```text
msagent deployment update --deployment-name "Development" --new-name "Shared-dev"
```

### Updating a CodeAgent deployment (Gated)

Requires `AzureBotServiceFeatures`. At least one of `--deployment-type`, `--sku`, `--display-name`,
`--description`, `--endpoint-url`, or `--default` is required; `--new-name` is rejected. The selector
is either `--deployment-id` alone or both `--agent-id` and `--deployment-name` (GUID agent id). Only
`--sku`, `--display-name`, `--description`, and `--endpoint-url` change the Azure Bot Service resource;
`--deployment-type` and `--default` update only the local record. `--endpoint-url` must be an absolute
HTTPS URL.
Omitted fields are preserved; blank strings cannot clear bot fields. Setting a new default clears the
previous default for the same agent. Metadata-only updates require an existing local record; they do
not discover or import a bot from Azure.

### Updating an AMS deployment (Gated)

Requires `AmsFeatures`. The route is chosen as described in
[AMS code-agent deployments](#ams-code-agent-deployments-gated); it needs the agent and the deployment,
by id or by name, and at least one of `--new-name` and `--endpoint-url`. `--project` and the Azure Bot
Service options are refused. It changes only the record: msagent reads the deployment and sends the
change with its entity tag, and the result (`agent-deployment-updated`) shows the record as AMS returned
it.

- `--new-name` renames the deployment.
- `--endpoint-url` sets the requested dev endpoint of a Dev deployment of either kind; for a Test or Prod
  deployment, which always runs a package, msagent refuses it after reading the record and before any
  change. On a dev endpoint deployment the new endpoint goes live at the next `msagent agent deploy`
  without `--commit-id`. On a package deployment it is a pending switch: that deploy switches the
  deployment, with the same bot, to the dev endpoint, while a deploy with `--commit-id` runs that upload
  and clears the requested endpoint. The output says so, and until then `show` and `list` report the
  endpoint as not deployed yet.

### Update rules and errors

- Deployment selectors: `--deployment-id` wins over `--deployment-name`. A name matching several deployments is
  ambiguous; with `--agent-id` or `--agent-name` the search is limited to that agent.
- Names are unique per agent, compared case-insensitively; renaming to a taken name is rejected when the
  configuration is written. Renaming to the identical name changes nothing.
- A new name outside the naming rule (4-42 characters from `-`, `a-z`, `A-Z`, `0-9`, and `_`) is a usage
  error. The rule applies only to a name that changes, so a slot whose existing name predates the rule
  can still be selected and renamed.
- MCS: `--new-name` is required, and a non-empty `--endpoint-url` is an error. Any CodeAgent-only option is a
  usage error, and so is `--new-name` on a CodeAgent. The result reports the previous and new name, type, environment,
  and configuration path.
- CodeAgent: `--deployment-id` cannot be combined with `--agent-id` or `--deployment-name`. Supply either the ID alone, or both
  `--agent-id` and `--deployment-name`. Blank values are rejected. When only `--deployment-type` or
  `--default` change, no Azure call is made. If the bot no longer exists (404), its local record is removed and the command fails as not found.
## `msagent deployment delete`

Deletes deployment records, or the record of an AMS deployment (**Gated**). This command never deletes the
project directory or its source files.

```text
msagent deployment delete (--deployment-id <id> | --deployment-name <name> [--agent-id <id> | --agent-name <name>]) \
  [--delete-deployed-mcs-agent] [--project <directory>]

# AMS code agent (Gated)
msagent deployment delete (--agent-id <id> | --agent-name <name>) (--deployment-id <id> | --deployment-name <name>) \
  [--environment-id <id>]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | All agents | The project directory holding the configuration. |
| `--deployment-id` | `-did` | String | All agents | The deployment id. Provide this, or the agent id and deployment name. |
| `--deployment-name` | `-dn` | String | All agents | The deployment name, used with the agent id to resolve the deployment. |
| `--agent-id` | `-aid` | String | All agents | The owning agent id. |
| `--agent-name` | `-an` | String | MCS and AMS | The owning agent name, used when the agent id is omitted. |
| `--delete-deployed-mcs-agent` | `-ddma` | Flag | **MCS-specific** | Also delete the Copilot Studio agent this deployment targets. |
| `--resource-group` | `-rg` | String | CodeAgent only (**Gated**) | Target resource group. |
| `--subscription` | `-sub` | GUID | CodeAgent only (**Gated**) | Target subscription id. |
| `--tenant` | `-t` | GUID | CodeAgent only (**Gated**) | Target tenant id. |
| `--environment-id` | `-eid` | String | **Gated** (`AmsFeatures`) | AMS agent home environment. Selects the AMS route. Defaults to the active environment. |

### Deleting an MCS agent deployment

For an MCS agent, the command resolves the deployment from the selectors. With no deployment selector
(only the agent), it targets **all** of the agent's deployments. It then lists what will be deleted and asks
you to type the deployment name (or the agent's display name when several are targeted) to confirm.
The global `--non-interactive` option skips the prompt and proceeds. A declined or mismatched answer
deletes nothing.
When deleting several deployments, each is removed in turn; a later failure does not restore earlier
deletions. Re-list the surviving records before retrying. For a standalone workflow,
`--delete-deployed-mcs-agent` is rejected: the deployment has no Copilot Studio agent to delete.

By default only the records are removed from `agent.config.json`; the agent record is kept and the
Copilot Studio agent is left in place. With `--delete-deployed-mcs-agent`, the Copilot Studio agent in the
deployment's environment is deleted first. If it is already absent the delete continues; if deletion fails
the record is preserved for retry.

```text
msagent deployment delete --deployment-name "Development"
msagent deployment delete --deployment-id <id> --delete-deployed-mcs-agent
```

### Deleting a CodeAgent deployment (Gated)

Requires `AzureBotServiceFeatures`. Deletes the Azure Bot Service resource and then the local record.
An already absent Azure bot is treated as deleted. The deployment is
selected by `--deployment-id` alone or by `--agent-id` with `--deployment-name`. `--delete-deployed-mcs-agent`
is an error. Unlike MCS delete, this route does not ask for confirmation.
After the local record is removed, retry with the agent-ID/name address if needed: an unknown
deployment ID alone is rejected by command routing because it no longer identifies a local target.

### Deleting an AMS deployment (Gated)

Requires `AmsFeatures`. The route is chosen as described in
[AMS code-agent deployments](#ams-code-agent-deployments-gated); it needs the agent and the deployment, by id
or by name. `--project`, the Azure options, and `--delete-deployed-mcs-agent` are refused. It removes the
deployment record in any state, with the record's entity tag.

```text
msagent deployment delete --agent-name "Contoso Agent" --deployment-name dev-proof
```
### Delete rules and errors

- Selection follows the same rules as `update`. When no deployment selector is given for an MCS agent, every deployment
  of the agent is targeted; an agent with none fails as not found.
- The confirmation prompt shows each deployment, its environment, and whether its cloud agent will be deleted. The result
  status is `deployment-deleted`, `cancelled`, or `name-mismatch`.
- If the cloud agent was deleted but the record then cannot be removed, the command reports that records survived a cloud
  delete, because the cloud change cannot be undone. When `--delete-deployed-mcs-agent` finds no cloud agent, only the record is removed.
- If the deleted cloud agent is the one recorded on the agent record, that recorded ID is cleared.
- CodeAgent delete: a missing `--deployment-id`, or `--agent-id` and `--deployment-name` pair, is a usage error. The Azure Bot
  Service delete runs before the local record is removed.

## `msagent deployment show`

Gets an existing deployment. The deployment is selected by `--deployment-id`, or by
`--deployment-name` together with an owning agent (`--agent-id`, or `--agent-name` for MCS agents).
For MCS, a unique name needs no owner selector, and omitting a deployment selector resolves the sole
deployment in scope. Several candidates are ambiguous. With `AmsFeatures` on, it reads an AMS deployment
as described in [AMS code-agent deployments](#ams-code-agent-deployments-gated).

```text
msagent deployment show (--deployment-id <id> | --deployment-name <name> [--agent-id <id> | --agent-name <name>]) \
  [--project <directory>] [Azure options]

# AMS code agent (Gated)
msagent deployment show (--agent-id <id> | --agent-name <name>) (--deployment-id <id> | --deployment-name <name>) \
  [--environment-id <id>]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | All agents | The project directory holding the configuration. Defaults to the current directory. |
| `--deployment-id` | `-did` | String | All agents | The deployment id. Provide this, or the agent id and deployment name. |
| `--deployment-name` | `-dn` | String | All agents | The deployment name, used with the agent id to resolve the deployment. |
| `--agent-id` | `-aid` | String | All agents | The owning agent id, used with the deployment name to resolve the deployment. |
| `--agent-name` | `-an` | String | MCS and AMS | The owning agent name, used when the agent id is omitted. |
| `--resource-group` | `-rg` | String | CodeAgent only (**Gated**) | Target resource group; auto-resolved when the subscription has exactly one. |
| `--subscription` | `-sub` | String | CodeAgent only (**Gated**) | Target subscription id; auto-resolved from the signed-in account when omitted. |
| `--tenant` | `-t` | String | CodeAgent only (**Gated**) | Target tenant id; taken from the resolved subscription when omitted. |
| `--environment-id` | `-eid` | String | **Gated** (`AmsFeatures`) | AMS agent home environment. Selects the AMS route. Defaults to the active environment. |

MCS `show` reads only the local deployment record, including its enabled/quarantined state, home and
target environments, and last-deployed time. CodeAgent `show` reads the bot from Azure and saves the
refreshed snapshot locally; with an agent ID and deployment name it can import an unrecorded bot.
An Azure 404 removes a stale local record and reports not found. Existing CodeAgent records pin the
Azure coordinates, so `--resource-group`, `--subscription`, and `--tenant` are used for discovery only
when no local record supplies them.

AMS `show` (**Gated**, `AmsFeatures`) refuses `--project` and the Azure options. It reads the deployment by
id, or takes the entry of the agent's deployment list that its name matched, and changes nothing, locally
or in AMS. The result (`agent-deployment-read`) reports the target environment, the stage, the kind
(`package` or `dev-endpoint`), the state, the deployed commit and Package Revision or the deployed endpoint
(`none deployed yet` before the first deploy), the requested endpoint when it differs from the deployed one
(the next `msagent agent deploy` without `--commit-id` puts it live, switching a package deployment to it),
the Entra identity, and the Azure Bot Service instance, if any.

## `msagent deployment channel` - CodeAgent-specific

See [deployment channel commands](Command_Deployment_Channel.md) for supported channels,
parameters, nested configuration, Slack authorization, and the AMS route (**Gated**).

## Selecting a deployment and project

The [`connection`](Command_Deployment_Connection.md), [`channel`](Command_Deployment_Channel.md), and [`oauth`](Command_Deployment_OAuth.md) commands, like `show`, `update`, and `delete`, use
deployment selectors: `--deployment-id` (`-did`), or `--deployment-name` (`-dn`) with `--agent-id`
(`-aid`). MCS commands and the AMS routes also accept `--agent-name` (`-an`). `--project` (`-p`) is the project
directory holding the configuration and defaults to the current directory.

When the selectors identify nothing the project holds, `show`, `update` and `delete` report what was
rejected rather than guessing a route:

```text
Nothing in this project matches --agent-id 'agent-missing'. Run `msagent deployment list` to see what it holds.
```

The one exception is an address Azure can resolve on its own: `--agent-id` together with
`--deployment-name` is a complete Azure Bot Service address, so when `AzureBotServiceFeatures` is
enabled those verbs still reach Azure and import a bot this project has no record of. With the
control off there is no Azure route to take, so the same input is rejected as above. This import
behavior does not apply to channel/OAuth operations or metadata-only CodeAgent updates, which require
an existing local record. MCS name selection can omit an agent selector when the deployment name is
unique across the project; an ambiguous name requires an owning agent selector.

With `AmsFeatures` on, an agent the project holds no record of takes the AMS route instead of either
outcome above, as described in [AMS code-agent deployments](#ams-code-agent-deployments-gated). There
the agent and the deployment may each be named by id or by name, and a missing one is refused before
anything is read.
