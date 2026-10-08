# MSAgent CLI identity commands

[Back to command index](ReadMe.md)

> **Gated.** The entire `identity` group is gated by `IdentityFeatures` and is hidden from `--help`
> (and rejected as unrecognized) unless that feature gate is enabled. Availability depends on the
> feature gates enabled for a given run; review `msagent --help` to see what is currently available.

## Command groups

| Group | Description |
| --- | --- |
| `msagent identity` | **(Gated)** Create and manage agent identities. Navigation group; currently contains only `create`. |
| `msagent identity create` | Create an agent identity. Navigation group; valid commands are `blueprint` and `app`. Invoking a group without a leaf command returns a usage error. |

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent identity create blueprint`](#msagent-identity-create-blueprint) **(Gated)** | Create or reconcile an Entra Agent Identity Blueprint. |
| [`msagent identity create app`](#msagent-identity-create-app) **(Gated)** | Create or reconcile an Entra App Registration identity. |

## `msagent identity create blueprint`

Creates or reconciles an Entra Agent Identity Blueprint, its API permissions, credential, optional
OAuth configuration, and optional Teams SSO configuration.

```text
msagent identity create blueprint --tenant <tenant-id> --display-name <name> \
  --auth-type <type> --config-format <format> [options]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--tenant` | `-t` | GUID | Yes | None | Target Entra tenant ID. |
| `--display-name` | `-disp` | String | Yes | None | Blueprint display name. Maximum 256 characters; control characters are rejected. |
| `--auth-type` | `-aut` | `FederatedCredentials` or `ClientSecret` | Yes | None | Credential mode. Values are case-insensitive. |
| `--config-format` | `-cf` | `dotnet` or `node` | Yes | None | Format of the generated runtime configuration. Values are case-insensitive. |
| `--subscription` | `-sub` | Non-empty GUID | Conditional | None | Azure subscription for the user-assigned managed identity. Required with `FederatedCredentials`; invalid with `ClientSecret`. |
| `--resource-group` | `-rg` | String | Conditional | None | Azure resource group for the managed identity. 1-90 characters; letters, digits, underscores, hyphens, periods, or parentheses; must not end with a period. Required with `FederatedCredentials`; invalid with `ClientSecret`. |
| `--location` | `-loc` | String | Conditional | None | Canonical Azure location name for the managed identity (for example `westus2`): 1-90 letters or digits only. Required with `FederatedCredentials`; invalid with `ClientSecret`. |
| `--managed-identity-name` | `-min` | String | Conditional | None | User-assigned managed identity name. Required with `FederatedCredentials`; invalid with `ClientSecret`. Must be 3-120 characters, start with a letter or digit, and contain only letters, digits, hyphens, or underscores. |
| `--oauth` | `-oa` | Boolean flag | No | `false` | Configure an OAuth redirect URI. |
| `--redirect-uri` | `-ru` | Absolute HTTPS URL | No | `https://token.botframework.com/.auth/web/redirect` when `--oauth` is set | OAuth redirect URI. Requires `--oauth`. |
| `--teams-sso` | `-ts` | Boolean flag | No | `false` | Expose `access_as_user` and preauthorize supported Teams/Office clients. |
| `--observability` | `-obs` | Boolean flag | No | `false` | Add A365 Observability permissions. |
| `--mcp-discovery` | `-md` | Boolean flag | No | `false` | Add A365 MCP Discovery permissions. |
| `--dry-run` | `-dr` | Boolean flag | No | `false` | Validate input and show the deterministic mutation plan without changing Entra or Azure. |

For `FederatedCredentials`, all four managed-identity coordinates (`--subscription`,
`--resource-group`, `--location`, and `--managed-identity-name`) must be supplied together.

## `msagent identity create app`

Creates or reconciles an ordinary Entra App Registration. It can create a new registration, match one
by display name, or target an existing registration by application ID or object ID.

```text
msagent identity create app --tenant <tenant-id> --display-name <name> \
  --auth-type <type> --config-format <format> [options]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--tenant` | `-t` | GUID | Yes | None | Target Entra tenant ID. |
| `--display-name` | `-disp` | String | Yes | None | App Registration display name. Maximum 256 characters; control characters are rejected. |
| `--auth-type` | `-aut` | `UserManagedIdentity`, `FederatedCredentials`, or `ClientSecret` | Yes | None | Credential mode. Values are case-insensitive. |
| `--config-format` | `-cf` | `dotnet` or `node` | Yes | None | Format of the generated runtime configuration. Values are case-insensitive. |
| `--subscription` | `-sub` | Non-empty GUID | Conditional | None | Azure subscription for the user-assigned managed identity. Required with `UserManagedIdentity` or `FederatedCredentials`; invalid with `ClientSecret`. |
| `--resource-group` | `-rg` | String | Conditional | None | Azure resource group for the managed identity. 1-90 characters; letters, digits, underscores, hyphens, periods, or parentheses; must not end with a period. Required with `UserManagedIdentity` or `FederatedCredentials`; invalid with `ClientSecret`. |
| `--location` | `-loc` | String | Conditional | None | Canonical Azure location name for the managed identity (for example `westus2`): 1-90 letters or digits only. Required with `UserManagedIdentity` or `FederatedCredentials`; invalid with `ClientSecret`. |
| `--managed-identity-name` | `-min` | String | Conditional | None | User-assigned managed identity name. Required with `UserManagedIdentity` or `FederatedCredentials`; invalid with `ClientSecret`. Must be 3-120 characters, start with a letter or digit, and contain only letters, digits, hyphens, or underscores. |
| `--oauth` | `-oa` | Boolean flag | No | `false` | Configure an OAuth redirect URI. |
| `--redirect-uri` | `-ru` | Absolute HTTPS URL | No | `https://token.botframework.com/.auth/web/redirect` when OAuth is enabled | OAuth redirect URI. Requires `--oauth` or `--teams-sso`. |
| `--teams-sso` | `-ts` | Boolean flag | No | `false` | Expose `access_as_user`, preauthorize supported Teams/Office clients, and enable the OAuth redirect behavior. |
| `--observability` | `-obs` | Boolean flag | No | `false` | Add A365 Observability permissions. |
| `--mcp-discovery` | `-md` | Boolean flag | No | `false` | Add A365 MCP Discovery permissions. |
| `--dry-run` | `-dr` | Boolean flag | No | `false` | Validate input and show the deterministic mutation plan without changing Entra or Azure. |
| `--app-id` | `-app` | GUID | No | None | Application/client ID of an existing App Registration to target. Cannot be combined with `--object-id`. |
| `--object-id` | `-oid` | GUID | No | None | Object ID of an existing App Registration to target. Cannot be combined with `--app-id`. |

For `UserManagedIdentity` and `FederatedCredentials`, all four managed-identity coordinates
(`--subscription`, `--resource-group`, `--location`, and `--managed-identity-name`) must be supplied
together.

## Validation

Input is validated before any call to Entra or Azure. A failure is a usage error and nothing is
changed. These rules apply to both commands unless noted.

| Rule | Detail |
| --- | --- |
| `--tenant` | Must be a GUID. |
| `--display-name` | Required; at most 256 characters; control characters are rejected. |
| `--auth-type`, `--config-format` | Must be one of the listed values, matched case-insensitively. |
| `--subscription` | Must be a GUID other than the all-zero GUID. |
| `--resource-group` | 1-90 characters; letters, digits, `_`, `-`, `.`, `(`, `)`; must not end with a period. |
| `--location` | 1-90 characters; ASCII letters and digits only. |
| `--managed-identity-name` | 3-120 characters; starts with an ASCII letter or digit; ASCII letters, digits, `-` and `_` only. |
| Managed-identity coordinates | The four coordinates are all required for `FederatedCredentials` (and, on `app`, `UserManagedIdentity`), and none may be supplied with `ClientSecret`. |
| `--redirect-uri` | Must be an absolute HTTPS URI. Supplying it requires `--oauth` on `blueprint`, or `--oauth` or `--teams-sso` on `app`. |
| `--app-id`, `--object-id` (`app` only) | Each must be a GUID, and they cannot be used together. |
