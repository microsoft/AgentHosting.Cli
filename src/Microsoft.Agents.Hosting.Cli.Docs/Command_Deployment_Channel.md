# MSAgent CLI deployment channel commands

[Back to deployment commands](Command_Deployment.md)

> [!IMPORTANT]
> **CodeAgent: future state - do not use.** This entire command group is retained
> as implementation reference, not a supported public-preview workflow. Do not
> run these examples or use this group to configure Copilot Studio channels.

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
| [`msagent deployment channel update`](#msagent-deployment-channel-update) | Update a channel on a deployment. |

The commands require a deployment selector (`--deployment-id`, or `--deployment-name` with
`--agent-id`) and accept `--project`.

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
