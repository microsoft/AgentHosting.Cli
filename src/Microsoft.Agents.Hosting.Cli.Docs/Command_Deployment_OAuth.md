# MSAgent CLI deployment OAuth commands

[Back to deployment commands](Command_Deployment.md)

> **CodeAgent-specific group.** Manages OAuth connection settings on a deployment. Invoking
> `msagent deployment oauth` without a leaf command returns a usage error that lists the valid
> subcommands.

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent deployment oauth create`](#msagent-deployment-oauth-create) | Create (enable) an OAuth connection setting on a deployment. |
| [`msagent deployment oauth delete`](#msagent-deployment-oauth-delete) | Delete an OAuth connection setting on a deployment. |
| [`msagent deployment oauth list`](#msagent-deployment-oauth-list) | List the OAuth connection settings on a deployment. |
| [`msagent deployment oauth list-providers`](#msagent-deployment-oauth-list-providers) | List the live Azure Bot Service OAuth provider catalogue. |
| [`msagent deployment oauth show`](#msagent-deployment-oauth-show) | Get an OAuth connection setting on a deployment. |
| [`msagent deployment oauth update`](#msagent-deployment-oauth-update) | Update an OAuth connection setting on a deployment. |

Except for `list-providers`, the commands require a deployment selector (`--deployment-id`, or
`--deployment-name` with `--agent-id`) and accept `--project`.

The `--connection` (`-con`) value is the OAuth connection name, unique within the bot. It must be
2-64 characters, start with a letter or digit, and contain only whitespace, letters, digits, `.`,
`_`, or `-`.

## `msagent deployment oauth create`

```text
msagent deployment oauth create --connection <name> --provider-name <provider> [selectors] [options]
```

| Option | Shortcut | Value | Required | Description |
| --- | --- | --- | --- | --- |
| `--connection` | `-con` | String | Yes | The OAuth connection name. |
| `--provider-name` | `-pn` | String | Yes | The OAuth service provider. Use `msagent deployment oauth list-providers` to inspect the live catalogue. |
| `--client-id` | `-clid` | String | Conditional | The application id. Required when the selected provider catalogue marks `ClientId` as required. |
| `--client-secret` | `-clsec` | String | Conditional | The write-only application secret. Required when the provider catalogue marks `ClientSecret` as required. |
| `--scopes` | `-scp` | String | No | The space-separated OAuth scopes. |
| `--parameters` | `-par` | `Key=Value` | No | Provider-specific assignments. Use `msagent deployment oauth list-providers --provider-name <name>` for the live schema. |

## `msagent deployment oauth delete`

Deletes an OAuth connection setting. Requires `--connection` (`-con`) plus the deployment selectors.

## `msagent deployment oauth list`

Lists the OAuth connection settings on a deployment. Accepts only the deployment selectors and
`--project`.

## `msagent deployment oauth list-providers`

Lists the live Azure Bot Service OAuth provider catalogue.

| Option | Shortcut | Value | Description |
| --- | --- | --- | --- |
| `--provider-name` | `-pn` | String | The provider name returned by the live catalogue; shows that provider's schema. |
| `--subscription` | `-sub` | String | Target subscription id; auto-resolved from the signed-in account when omitted. |
| `--tenant` | `-t` | String | Target tenant id; taken from the resolved subscription when omitted. |

## `msagent deployment oauth show`

Gets an OAuth connection setting. Requires `--connection` (`-con`) plus the deployment selectors.

## `msagent deployment oauth update`

Updates an OAuth connection setting. Requires `--connection` (`-con`) plus the deployment selectors,
and accepts `--client-id`, `--client-secret`, `--scopes`, and `--parameters` with the same meanings
as for `oauth create`.
