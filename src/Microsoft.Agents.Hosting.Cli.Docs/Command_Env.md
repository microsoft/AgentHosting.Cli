# MSAgent CLI environment commands

[Back to command index](ReadMe.md)

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent env list`](#msagent-env-list) | List environments available to the signed-in account. |
| [`msagent env select`](#msagent-env-select) | Select the active environment used by later commands. |

## `msagent env list`

Lists the Power Platform environments available to the signed-in account and identifies the active
selection.

```text
msagent env list
```

This command has no command-specific arguments or options.

## `msagent env select`

Persists the environment that later agent commands use.

```text
msagent env select <environment-id>
```

| Argument | Value | Required | Description |
| --- | --- | --- | --- |
| `environment-id` | String | Yes | Environment ID to make active. It must identify an environment available to the signed-in account. |
