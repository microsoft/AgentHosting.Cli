# MSAgent CLI deployment commands

[Back to command index](ReadMe.md)

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent deployment create`](#msagent-deployment-create) | Create a CodeAgent or MCS agent deployment record. CodeAgent options are **Gated**. |
| [`msagent deployment list`](#msagent-deployment-list) | List an agent's deployment records, in promotion order. Local. |
| [`msagent deployment update`](#msagent-deployment-update) | Rename an MCS deployment, update a CodeAgent deployment, or update a package revision. |
| [`msagent deployment delete`](#msagent-deployment-delete) | Delete a CodeAgent or MCS agent deployment record, and optionally the MCS cloud agent. |
| [`msagent deployment show`](#msagent-deployment-show) | Get an existing CodeAgent or MCS agent deployment. |
| [`msagent deployment channel`](#msagent-deployment-channel---codeagent-specific) | **CodeAgent-specific.** Group: `create`, `update`, `delete`, `show`, and `list`. Manage Azure Bot Service channels. |
| [`msagent deployment connection`](Command_Deployment_Connection.md) | **MCS-specific.** Group: `list`, `add`, `update`, `remove`, and `connectors`. Inspect and bind connection references and create connections. See [Command_Deployment_Connection.md](Command_Deployment_Connection.md) for details. |
| [`msagent deployment oauth`](Command_Deployment_OAuth.md) | **CodeAgent-specific.** Group: `create`, `delete`, `list`, `list-providers`, `show`, and `update`. Manage OAuth connection settings. See [Command_Deployment_OAuth.md](Command_Deployment_OAuth.md) for details. |

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
>   disabled.
> - The `--package-revision` and `--environment-id` options on `update` are gated by `AmsFeatures`.
> - The `channel`, `connection`, and `oauth` groups have no feature gate of their own. Channel and
>   OAuth deployment operations still require an existing local CodeAgent deployment record.
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
## `msagent deployment create`

Creates a named deployment record. An MCS agent's record is stored in the local project configuration
(`agent.config.json`); it resolves the environment through the signed-in session but does not mutate the cloud. A CodeAgent deployment provisions an Azure
Bot Service resource and is **Gated**. The route is chosen from the resolved agent type.

```text
msagent deployment create [--agent-id <id> | --agent-name <name>] --deployment-type <dev|test|prod> \
  --deployment-name <name> [--environment-id <id>] [--project <directory>]
```

| Option | Shortcut | Value | Required | Default | Applies to | Description |
| --- | --- | --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | No | Current directory | All agents | The project directory holding the configuration. |
| `--agent-id` | `-aid` | String | Conditional | The project's agent (MCS) | All agents | The owning agent id. Required for a CodeAgent deployment (a GUID). |
| `--agent-name` | `-an` | String | Conditional | None | **MCS-specific** | The owning agent name, used when the agent id is omitted. |
| `--deployment-name` | `-dn` | String | Yes | None | All agents | Deployment name, unique within the agent. CodeAgent names must be 4-42 characters; only `-`, `a-z`, `A-Z`, `0-9`, and `_`. MCS names are trimmed and must be nonblank. |
| `--deployment-type` | `-dt` | `dev`, `test`, or `prod` | Yes | None | All agents | The stage. Values are case-insensitive. |
| `--environment-id` | `-eid` | String | Conditional | The agent's home environment | **MCS-specific** | Target environment. Required for `test` and `prod`. |
| `--entra-id` | `-ent` | GUID | Yes (CodeAgent) | None | CodeAgent only (**Gated**) | The Entra application (client) id the deployment binds to. |
| `--entra-id-type` | `-eit` | `appReg` | Yes (CodeAgent) | None | CodeAgent only (**Gated**) | Entra identity kind. `blueprint` and `default` are not supported. |
| `--sku` | `-sku` | `F0` or `S1` | No | `F0` | CodeAgent only (**Gated**) | Deployment SKU. |
| `--location` | `-loc` | String | No | `global` | CodeAgent only (**Gated**) | Azure region. |
| `--resource-group` | `-rg` | String | No | Auto-resolved when the subscription has exactly one | CodeAgent only (**Gated**) | Target resource group. |
| `--subscription` | `-sub` | GUID | No | Auto-resolved from the signed-in account | CodeAgent only (**Gated**) | Target subscription id. |
| `--tenant` | `-t` | GUID | No | From the resolved subscription | CodeAgent only (**Gated**) | Target tenant id. |
| `--endpoint-url` | `-eu` | HTTPS URL | No | None | CodeAgent only (**Gated**) | Messaging endpoint. |
| `--display-name` | `-disp` | String | No | Deployment name | CodeAgent only (**Gated**) | Display name. |
| `--description` | `-d` | String | No | None | CodeAgent only (**Gated**) | Description. |
| `--default` | `-def` | Flag | No | `false` | CodeAgent only (**Gated**) | Mark the deployment as the agent's default. |

CodeAgent creation rejects MCS-only options; MCS creation rejects CodeAgent-only values.
CodeAgent value-bearing options reject explicitly blank values.


### Create rules

- `--deployment-name` and `--deployment-type` are required. The type is case-insensitive.
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

> **Storage.** `deployment list` is entirely local: it reads the deployment records in the project's
> configuration and contacts nothing. Deployment records are always local in `main`. `AzureBotServiceFeatures`
> does not change this command.

```text
msagent deployment list [--agent-id <id> | --agent-name <name>] [--project <directory>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | No | Current directory | The project directory holding the agent. |
| `--agent-id` | `-aid` | String | No | Every agent | The record whose deployments are listed. Wins over `--agent-name`. |
| `--agent-name` | `-an` | String | No | Every agent | The record whose deployments are listed, by name. |

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

## `msagent deployment update`

Updates an existing deployment. The route is chosen from the resolved agent type, or from
`--package-revision` / `--environment-id`.

```text
msagent deployment update (--deployment-id <id> | --deployment-name <name> [--agent-id <id> | --agent-name <name>]) \
  [--new-name <name>] [--project <directory>]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | All agents | The project directory holding the configuration. |
| `--deployment-id` | `-did` | String | All agents | The deployment id. Provide this, or the agent id and deployment name. |
| `--deployment-name` | `-dn` | String | All agents | The deployment name, used with the agent id to resolve the deployment. |
| `--agent-id` | `-aid` | String | All agents | The owning agent id. |
| `--agent-name` | `-an` | String | **MCS-specific** | The owning agent name, used when the agent id is omitted. |
| `--new-name` | `-nn` | String | **MCS-specific** | The deployment's new name, unique among the agent's deployments. |
| `--resource-group` | `-rg` | String | CodeAgent only (**Gated**) | Target resource group. |
| `--subscription` | `-sub` | GUID | CodeAgent only (**Gated**) | Target subscription id. |
| `--tenant` | `-t` | GUID | CodeAgent only (**Gated**) | Target tenant id. |
| `--deployment-type` | `-dt` | `dev`, `test`, or `prod` | CodeAgent only (**Gated**) | New stage. |
| `--sku` | `-sku` | `F0` or `S1` | CodeAgent only (**Gated**) | New SKU. |
| `--display-name` | `-disp` | String | CodeAgent only (**Gated**) | New display name. |
| `--description` | `-d` | String | CodeAgent only (**Gated**) | New description. |
| `--endpoint-url` | `-eu` | HTTPS URL | CodeAgent only (**Gated**) | New messaging endpoint. |
| `--default` | `-def` | Optional flag | CodeAgent only (**Gated**) | Mark the deployment as the agent's default. |
| `--package-revision` | `-pr` | String | **Gated** (`AmsFeatures`) | Existing immutable AMS Package Revision id. |
| `--environment-id` | `-eid` | String | **Gated** (`AmsFeatures`) | AMS Agent home environment. Defaults to the active environment. |

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

### Updating a deployment's package revision (Gated)

Requires `AmsFeatures`. Providing `--package-revision` or `--environment-id` selects this route, which
requires `--agent-id`, `--deployment-id`, and `--package-revision`. It cannot be combined with
`--project`, `--agent-name`, `--deployment-name`, the Azure options, or any other update option.
The agent ID, deployment ID, and package revision ID must be GUIDs. This route reads the current
revision, then conditionally updates the existing AMS deployment; it neither uploads a package nor
updates the local deployment registry. The result reports `agent-deployment-updated` with deployment
and package details. A concurrency conflict is not retried. A lost or malformed response after the
update may leave the outcome indeterminate: retain the reported correlation IDs and inspect service
state before retrying.

### Update rules and errors

- Deployment selectors: `--deployment-id` wins over `--deployment-name`. A name matching several deployments is
  ambiguous; with `--agent-id` or `--agent-name` the search is limited to that agent.
- Names are unique per agent, compared case-insensitively; renaming to a taken name is rejected when the
  configuration is written. Renaming to the identical name changes nothing.
- MCS: `--new-name` is required, and a non-empty `--endpoint-url` is an error. Any CodeAgent-only option is a
  usage error, and so is `--new-name` on a CodeAgent. The result reports the previous and new name, type, environment,
  and configuration path.
- CodeAgent: `--deployment-id` cannot be combined with `--agent-id` or `--deployment-name`. Supply either the ID alone, or both
  `--agent-id` and `--deployment-name`. Blank values are rejected. When only `--deployment-type` or
  `--default` change, no Azure call is made. If the bot no longer exists (404), its local record is removed and the command fails as not found.
## `msagent deployment delete`

Deletes deployment records. This command never deletes the project directory or its source files.

```text
msagent deployment delete (--deployment-id <id> | --deployment-name <name> [--agent-id <id> | --agent-name <name>]) \
  [--delete-deployed-mcs-agent] [--project <directory>]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | All agents | The project directory holding the configuration. |
| `--deployment-id` | `-did` | String | All agents | The deployment id. Provide this, or the agent id and deployment name. |
| `--deployment-name` | `-dn` | String | All agents | The deployment name, used with the agent id to resolve the deployment. |
| `--agent-id` | `-aid` | String | All agents | The owning agent id. |
| `--agent-name` | `-an` | String | **MCS-specific** | The owning agent name, used when the agent id is omitted. |
| `--delete-deployed-mcs-agent` | `-ddma` | Flag | **MCS-specific** | Also delete the Copilot Studio agent this deployment targets. |
| `--resource-group` | `-rg` | String | CodeAgent only (**Gated**) | Target resource group. |
| `--subscription` | `-sub` | GUID | CodeAgent only (**Gated**) | Target subscription id. |
| `--tenant` | `-t` | GUID | CodeAgent only (**Gated**) | Target tenant id. |

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
deployment in scope. Several candidates are ambiguous.

```text
msagent deployment show (--deployment-id <id> | --deployment-name <name> [--agent-id <id> | --agent-name <name>]) \
  [--project <directory>] [Azure options]
```

| Option | Shortcut | Value | Applies to | Description |
| --- | --- | --- | --- | --- |
| `--project` | `-p` | Directory path | All agents | The project directory holding the configuration. Defaults to the current directory. |
| `--deployment-id` | `-did` | String | All agents | The deployment id. Provide this, or the agent id and deployment name. |
| `--deployment-name` | `-dn` | String | All agents | The deployment name, used with the agent id to resolve the deployment. |
| `--agent-id` | `-aid` | String | All agents | The owning agent id, used with the deployment name to resolve the deployment. |
| `--agent-name` | `-an` | String | **MCS-specific** | The owning agent name, used when the agent id is omitted. |
| `--resource-group` | `-rg` | String | CodeAgent only (**Gated**) | Target resource group; auto-resolved when the subscription has exactly one. |
| `--subscription` | `-sub` | String | CodeAgent only (**Gated**) | Target subscription id; auto-resolved from the signed-in account when omitted. |
| `--tenant` | `-t` | String | CodeAgent only (**Gated**) | Target tenant id; taken from the resolved subscription when omitted. |

MCS `show` reads only the local deployment record, including its enabled/quarantined state, home and
target environments, and last-deployed time. CodeAgent `show` reads the bot from Azure and saves the
refreshed snapshot locally; with an agent ID and deployment name it can import an unrecorded bot.
An Azure 404 removes a stale local record and reports not found. Existing CodeAgent records pin the
Azure coordinates, so `--resource-group`, `--subscription`, and `--tenant` are used for discovery only
when no local record supplies them.

## `msagent deployment channel` - CodeAgent-specific

Manages channels on an existing locally recorded CodeAgent deployment. This group is not feature-gated
and is singular (`channel`, not `channels`). Invoking the group without a leaf command is a usage error.
All leaves accept `--project` (`-p`, current directory), and either `--deployment-id` (`-did`, GUID)
alone or `--agent-id` (`-aid`, GUID) with `--deployment-name` (`-dn`). They do not accept Azure address
overrides or `--agent-name`; the backing bot coordinates come from the local record.

| Command | Required options beyond the deployment selector | Behavior |
| --- | --- | --- |
| `msagent deployment channel create` | `--channel` | Enable a channel. Returns the existing channel without applying changes if it already exists. Required create parameters are still validated first. |
| `msagent deployment channel update` | `--channel`, `--parameters` | Read the live channel, merge supplied settings, and PUT the result. Fails if the channel does not exist. |
| `msagent deployment channel delete` | `--channel` | Delete the channel and remove its local snapshot. An already absent channel is treated as deleted. No confirmation prompt. |
| `msagent deployment channel show` | `--channel` | Read a live channel and refresh its local snapshot. |
| `msagent deployment channel list` | None | Read live channels and replace the local channel snapshots. Does not accept `--channel` or `--parameters`. |

`--channel` has **no short alias**. Values are case-insensitive: `directline`, `m365extensions`,
`msteams`, `slack`, or `webchat`. `create` and `update` accept repeatable `--parameters` (`-par`)
with one or more `Key=Value` assignments per occurrence. Quote an assignment containing spaces.
Parameter names are case-insensitive; unknown or duplicate keys, invalid booleans, and read-only
parameters are rejected. Create rejects blank values; update permits `Key=` to clear an optional
parameter, but required parameters cannot be cleared.

### Channel parameters and restrictions

Use `msagent deployment channel create --channel <name> --help` (or `update`) for the channel-specific
parameter schema. These are assignments, not separate typed command options.

| Channel | Parameters |
| --- | --- |
| `directline` | Required on create: `siteName`. Optional strings: `appId`, `tenantId`, `trustedOrigins` (comma-separated). Booleans: `isBlockUserUploadEnabled`, `isDetailedLoggingEnabled`, `isEnabled` (create default `true`), `isEndpointParametersEnabled`, `isNoStorageEnabled`, `isSecureSiteEnabled`, `isTokenEnabled`, `isV3Enabled`. `isV1Enabled` is read-only. |
| `webchat` | Required on create: `siteName`. Optional strings: `appId`, `tenantId`. Booleans: `isBlockUserUploadEnabled`, `isDetailedLoggingEnabled`, `isEnabled` (create default `true`), `isEndpointParametersEnabled`, `isNoStorageEnabled`, `isTokenEnabled`, `isWebchatPreviewEnabled`. |
| `msteams` | Strings: `callingWebhook`, `deploymentEnvironment` (create default `CommercialDeployment`), `incomingCallRoute`. Booleans: `acceptedTerms`, `enableCalling`, `isEnabled` (create default `true`). |
| `slack` | Required on create: `clientId`, `clientSecret`, `verificationToken`. Optional strings: `landingPageUrl`, `scopes`, `signingSecret`. Booleans: `isEnabled`, `registerBeforeOAuthFlow` (both create defaults `true`). Secrets are masked in results and local records. |
| `m365extensions` | No parameters. Create, show, list, and delete are supported; update is not. |

Direct Line and Web Chat are automatically created channels and cannot be deleted through this CLI.
**Their updates replace the entire sites array with one site**, merging from the first live site;
additional sites are not preserved and there is no site-selector option. Channel updates preserve safe
readable settings but do not replay masked secrets; resupply credentials when changing them.

```text
msagent deployment channel create --deployment-id <id> --channel msteams
msagent deployment channel update --deployment-id <id> --channel msteams --parameters enableCalling=true
msagent deployment channel list --deployment-id <id>
```

Create/update/show results contain a channel snapshot, list contains channel snapshots, and delete
reports deletion without a channel payload. Successful operations refresh the local aggregate after
the Azure call; a local-save failure is not a rollback.

## Selecting a deployment and project

The [`connection`](Command_Deployment_Connection.md), [`channel`](#msagent-deployment-channel---codeagent-specific), and [`oauth`](Command_Deployment_OAuth.md) commands, like `show`, `update`, and `delete`, use
deployment selectors: `--deployment-id` (`-did`), or `--deployment-name` (`-dn`) with `--agent-id`
(`-aid`). MCS commands also accept `--agent-name` (`-an`). `--project` (`-p`) is the project
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
