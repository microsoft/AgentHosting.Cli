# MSAgent CLI environment commands

[Back to command index](ReadMe.md)

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent env list`](#msagent-env-list) | List environments available to the signed-in account. |
| [`msagent env select`](#msagent-env-select) | Select the active environment used by later commands. |

Both commands require an active signed-in CLI account and query the live Power Platform API
inventory using silent token acquisition. They never open identity UI, even without
`--non-interactive`. If sign-in or consent is required, run `msagent auth login` separately.
Inventory access requires the Power Platform API delegated permission `PowerApps.Environments.Read`.

The active account's `prod`, `preprod`, or `test` ring selects the API endpoint and ring-specific
selection store. These are public-cloud API rings, not sovereign-cloud selectors; these commands
have no `--cloud` option. Use `msagent auth switch --cloud <ring>` before listing/selecting in a
different ring.

## `msagent env list`

Lists the Power Platform environments available to the signed-in account and identifies the active
selection.

```text
msagent env list
```

This command has no command-specific arguments or options.

The result status is `listed`; entries include environment ID, display name, tenant ID, API
endpoint, and active-selection marker. An inventory endpoint must pass the CLI's trusted
Power Platform host checks; an untrusted endpoint fails the listing rather than becoming a target.
Listing does not change the active selection.

## `msagent env select`

Persists the environment that later agent commands use.

```text
msagent env select <environment-id>
```

| Argument | Value | Required | Description |
| --- | --- | --- | --- |
| `environment-id` | String | Yes | Environment ID to make active. It must identify an environment available to the signed-in account. |

The ID is trimmed and matched case-insensitively against the live inventory; a display name is not
a selector. Selection also requires a known environment tenant matching the active account's tenant
and a trusted API endpoint. An unavailable ID, unknown tenant, or tenant mismatch is rejected
without replacing the existing selection.

The result status is `selected` and includes environment ID, display name, tenant ID, API endpoint,
and selection timestamp. This persists local targeting state; it does not modify the cloud
environment or grant permissions. Later environment-dependent commands require a selection rather
than silently falling back to a default, unless they support an explicit command-scoped target.
An explicit target on another command does not change this persisted selection.

`msagent auth status` shows the stored selection and whether it remains tenant-bound. Login or
account switching clears selections across all rings when the account, tenant, or ring changes;
select again afterward. Logout leaves the selection on disk but it cannot be used while signed out.
