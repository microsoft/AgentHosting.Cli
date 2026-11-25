# MSAgent CLI deployment channel commands

[Back to deployment commands](Command_Deployment.md)

> **CodeAgent-specific group.** Manages Azure Bot Service channels on a deployment. Invoking
> `msagent deployment channel` without a leaf command returns a usage error that lists the valid
> subcommands.

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent deployment channel create`](#msagent-deployment-channel-create) | Create (enable) a channel on a deployment. |
| [`msagent deployment channel delete`](#msagent-deployment-channel-delete) | Delete a channel from a deployment. |
| [`msagent deployment channel list`](#msagent-deployment-channel-list) | List the supported channels on a deployment. |
| [`msagent deployment channel show`](#msagent-deployment-channel-show) | Get a channel on a deployment. |
| [`msagent deployment channel token`](#msagent-deployment-channel-token) | **Gated.** Get a Direct Line token from AMS to start a conversation with the agent. |
| [`msagent deployment channel types`](#msagent-deployment-channel-types) | **Gated.** List the channel types AMS can create and edit on a deployment. |
| [`msagent deployment channel update`](#msagent-deployment-channel-update) | Update a channel on a deployment. |
| [`msagent deployment channel webhook`](#msagent-deployment-channel-webhook) | **Gated.** Get from AMS the callback URL and verify token for Facebook's webhook settings. |

The commands require a deployment selector (`--deployment-id`, or `--deployment-name` with
`--agent-id`) and accept `--project`. With `AmsFeatures` on (**Gated**), they manage the channels of
an AMS deployment instead when they name an agent the project holds no record of, or get
`--environment-id` or `--agent-name`; see [AMS deployments](#ams-deployments-gated).

The `--channel` value is one of `directline`, `facebook`, `line`, `m365extensions`, `msteams`,
`outlook`, `slack`, `sms`, `telegram`, or `webchat`.

Use `msagent deployment channel create --channel <name> --help` or
`msagent deployment channel update --channel <name> --help` to inspect that channel's parameters.
It lists required fields, defaults, and nested item fields. `m365extensions` and `outlook`
support only `create`, `list`, and `delete`.

## `msagent deployment channel create`

```text
msagent deployment channel create --channel <name> [selectors] [options]
```

| Option | Shortcut | Value | Required | Description |
| --- | --- | --- | --- | --- |
| `--channel` | - | String | Yes | The channel type. |
| `--parameters` | `-par` | `Key=Value` | Conditional | Channel-specific assignments. Use selected-channel help for required fields and defaults. |

Returns an existing channel unchanged. Otherwise, creates the channel using the supplied parameters
and creation defaults. For Slack, supply explicit `scopes` to request narrower permissions than
the broad ARM default.

Assignments split at the first `=`. Parameter names are case-insensitive. Use `true`/`false` for
booleans, whole numbers for integers, and inline JSON arrays for collections.
JSON parameter files are not supported; readonly settings are not writable.

## `msagent deployment channel delete`

Deletes a whole channel. Requires `--channel` plus the deployment selectors.
Direct Line and Web Chat cannot be deleted by the CLI; use `update` to remove their sites.

## `msagent deployment channel list`

Lists the supported channels on a deployment. Accepts only the deployment selectors and `--project`.
Unsupported channels appear only in redacted `--debug` diagnostics, not saved configuration.

## `msagent deployment channel show`

Gets a channel's current configuration. Requires `--channel` plus the deployment selectors.
Secrets are masked in output and omitted from saved configuration.

## `msagent deployment channel update`

Updates an existing channel. Requires `--channel` plus the deployment selectors, and either
`--parameters` (as for `channel create`) or one collection operation below. These cannot be combined.

| Options | Shortcuts | Channel | Selector |
| --- | --- | --- | --- |
| `--add-site`, `--set-site`, `--remove-site` | `-ads`, `-sts`, `-rms` | Direct Line, Web Chat | `siteName` |
| `--add-page`, `--set-page`, `--remove-page` | `-adp`, `-stp`, `-rmp` | Facebook | `id` |
| `--add-registration`, `--set-registration`, `--remove-registration` | `-adr`, `-str`, `-rmr` | LINE | `generatedId` (set/remove only) |

Pass unprefixed `Key=Value` fields, for example `--set-site siteName=Primary isEnabled=false`.
Add requires new-item fields; set requires an existing selector and at least one change;
remove accepts only the selector. Use `show` to read Azure-generated LINE registration ids.
Site names are case-insensitive; page and registration ids are case-sensitive.

Omitted fields preserve existing values; creation defaults are not reapplied. Complete arrays
replace membership, so include every item to keep. Retained items may omit unchanged fields;
new items require their creation fields. Facebook and LINE must retain at least one item.

For Direct Line/Web Chat, use `sites=` or `sites=[]` to remove all sites. Clear site `appId` or
`tenantId` with an empty value, and Direct Line `trustedOrigins` with an empty value or `[]`.
Complete JSON items also accept `null` for these fields. No other fields can be cleared;
`field=null` is literal text, not JSON null.

New sites are created first, then configured in a follow-up write. If that fails or is cancelled,
the initial write is retained with a warning and no rollback.

### Slack authorization

Slack create/update report `WriteAccepted`, not confirmed persistence. An existing channel returned
unchanged by `create` does not start authorization.

When Azure supplies an authorization URL, the CLI opens it unless `--non-interactive` (`-ni`) is set,
including with `--json`, `--debug`, or redirected output. Treat the URL as sensitive; it is not saved.
Browser-launch failures do not undo the write.

If no usable URL is available, configure Slack through Azure portal. Authorization may take time
and does not guarantee persistence; use `show` to check settings. The CLI does not verify completion.
See the [Slack setup guide](https://learn.microsoft.com/en-us/azure/bot-service/bot-service-channel-connect-slack?view=azure-bot-service-4.0)
for app configuration and redirect URLs.

## AMS deployments (Gated)

```text
msagent deployment channel <create|update|delete|show> (--agent-id <agent-id> | --agent-name <name>) \
  (--deployment-id <deployment-id> | --deployment-name <name>) --channel <name> [--environment-id <id>] [options]
msagent deployment channel list (--agent-id <agent-id> | --agent-name <name>) \
  (--deployment-id <deployment-id> | --deployment-name <name>) [--environment-id <id>] [options]
```

With `AmsFeatures` on, this route is taken when the command names an agent the project holds no record
of, or gets `--environment-id` (the agent's home environment, which defaults to the active environment)
or `--agent-name`; see [AMS code agents](Command_Agent.md#ams-code-agents-gated). It needs the agent and
the deployment, each by id or by name; `--project` cannot be combined with it. The route never uses the
local deployment registry or your Azure sign-in: AMS manages the channels of the Azure Bot Service
instance it bound to the deployment at its first deploy (see `msagent deployment show`).

AMS supports the channel types of local mode: `directline`, `facebook`, `line`, `m365extensions`,
`msteams`, `outlook`, `slack`, `sms`, `telegram`, and `webchat`; `msagent deployment channel types`
lists the channel types, operations, and parameters it accepts, marks credentials as secret, and marks
the selector of each list item. As in local mode, `m365extensions` and `outlook` have no
settings and support only `create`, `list`, and `delete`. The CLI checks `--channel` and the
parameters with the rules above before it sends anything, and AMS checks them again.

## `msagent deployment channel types`

**Gated, AMS only.**

```text
msagent deployment channel types (--agent-id <agent-id> | --agent-name <name>) (--deployment-id <deployment-id> | --deployment-name <name>) [--environment-id <id>] [options]
```

Lists the channel types AMS can create and edit on the deployment, with the operations each
supports and its parameters: type, required, read-only, clearable, secret (a credential that
requests can set but AMS never shows), selector (the field that picks one item of a list), allowed
values, default, and the fields of each item of a list (Direct Line and Web Chat sites, Facebook
pages, LINE registrations).

## `msagent deployment channel token`

**Gated, AMS only.**

```text
msagent deployment channel token (--agent-id <agent-id> | --agent-name <name>) (--deployment-id <deployment-id> | --deployment-name <name>) --channel directline [--environment-id <id>] [options]
```

| Option | Shortcut | Value | Required | Description |
| --- | --- | --- | --- | --- |
| `--channel` | - | String | Yes | The channel that issues the token. Only `directline` issues tokens. |
| `--site` | `-sit` | String | No | The Direct Line site that issues the token. |
| `--user-id` | `-uid` | String | No | The user the token is bound to. Must start with `dl_`. |

AMS generates the token with the site's key, which never leaves AMS. Without `--site`, AMS uses
the only enabled site that accepts Direct Line 3.0 clients; when several do, it refuses and names
them. The result has `token`, `conversationId`, `expiresInSeconds`, `siteName`, and `endpoint`.

A new bot can list no Direct Line site until its channel is first updated, so before its first token,
add a site with
`msagent deployment channel update ... --channel directline --add-site siteName=<name> isV3Enabled=true`
and pass `--site <name>`. When no site can issue the token, the error keeps AMS's reason and says how
to pass, add, or turn on a site. The CLI refuses a `--user-id` that does not start with `dl_` before it
calls AMS.

## `msagent deployment channel webhook`

**Gated, AMS only.**

```text
msagent deployment channel webhook (--agent-id <agent-id> | --agent-name <name>) (--deployment-id <deployment-id> | --deployment-name <name>) --channel facebook [--environment-id <id>] [options]
```

| Option | Shortcut | Value | Required | Description |
| --- | --- | --- | --- | --- |
| `--channel` | - | String | Yes | The channel whose webhook values to get. Only `facebook` has them. |

Gets the values that the Facebook webhook settings of the Meta app need: `callbackUrl` and
`verifyToken`. Azure Bot Service generates the verify token and returns it only to AMS, which hands it
to callers allowed to update the agent.
