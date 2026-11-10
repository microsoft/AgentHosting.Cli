# MSAgent CLI environment commands

[Back to command index](ReadMe.md)

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent env list`](#msagent-env-list) | List environments available to the signed-in account. |
| [`msagent env select`](#msagent-env-select) | Select the active environment used by later commands. |

Both commands require a selected CLI profile and query the live Power Platform API
inventory using its captured credential. They never open identity UI, even without
`--non-interactive`. If sign-in or consent is required, run `msagent auth create` separately
for the intended account and cloud.
User inventory access requires the Power Platform API delegated permission `PowerApps.Environments.Read`;
application access is checked against the required application principal and product permissions.

The selected profile's registered cloud selects the API endpoint and compatible owned selection
state. These commands have no `--cloud` or separate authentication `--ring` option. Create/select
the intended cloud profile first with `msagent auth create` and `msagent auth select`. Registered
authentication coordinates do not guarantee every sovereign downstream service route is available.

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

Persists the selected profile's default environment and a compatible owned override for later commands.

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
environment or grant permissions. Target precedence is an explicit command environment, a compatible
owned override, the profile default, then a missing-target error. An explicit target on another
command does not change this persisted selection.

`msagent auth status` and `msagent auth who` show the stored binding. Incompatible or foreign overrides
are not silently reused. To change another profile's default, first select it with `msagent auth select`,
then run `msagent env select`. `auth clear` clears owned state; shared tool selections are not adopted.
