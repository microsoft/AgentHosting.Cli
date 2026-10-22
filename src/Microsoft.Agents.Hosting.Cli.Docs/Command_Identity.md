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

## Before you run

These commands use public-cloud Microsoft Graph (`graph.microsoft.com`) and Azure Resource Manager
(`management.azure.com`) endpoints. They have no cloud/ring option; changing the account's Power
Platform API ring does not retarget identity provisioning to a sovereign cloud.

Use `--dry-run` first. It validates inputs and returns an intended plan without acquiring tokens or
making Graph/ARM requests; it does **not** discover existing resources or prove permissions,
tenant feature availability, or resource availability. Without `--dry-run`, the command performs
mutations immediately: there is no `--force` option or separate confirmation prompt.

Actual provisioning requires a signed-in account authorized in the explicit target tenant. `app`
uses a separate consent-capable Microsoft Graph client and requests delegated
`Application.ReadWrite.All`, `Application.Read.All`, `AppRoleAssignment.ReadWrite.All`, and
`User.Read` permissions. `blueprint` uses the CLI's silent Graph token path with
`AgentIdentityBlueprint.ReadWrite.All`, `AgentIdentityBlueprintPrincipal.Create`,
`Application.Read.All`, and `User.Read` as needed; the tenant must support the beta Blueprint APIs.
Managed-identity modes additionally require Azure access to read/create user-assigned identities
in the supplied, existing resource group.

Graph consent for `app` and ARM consent for managed-identity provisioning are silent-first and may
open broker/browser/device-code interaction when needed. `--non-interactive` disables that
interaction, not provisioning: operations can still mutate using cached, consented credentials.
If Graph consent is missing, `app` fails with a `consent-required` report and an administrator
consent URL; obtain consent and rerun. Follow returned `nextActions` for any additional permission
handoffs. Consent does not itself grant the signed-in operator every directory role or Azure RBAC
permission needed to perform the changes.

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
| `--teams-sso` | `-ts` | Boolean flag | No | `false` | Expose `access_as_user` and preauthorize the supported Teams clients. Does not imply `--oauth` on Blueprint. |
| `--observability` | `-obs` | Boolean flag | No | `false` | Add A365 Observability permissions. |
| `--mcp-discovery` | `-md` | Boolean flag | No | `false` | Add A365 MCP Discovery permissions. |
| `--dry-run` | `-dr` | Boolean flag | No | `false` | Validate input and show the deterministic mutation plan without changing Entra or Azure. |

For `FederatedCredentials`, all four managed-identity coordinates (`--subscription`,
`--resource-group`, `--location`, and `--managed-identity-name`) must be supplied together.

Blueprint discovery matches the exact display name. No match creates a Blueprint and principal;
one match reconciles it; multiple matches fail rather than choosing one. There is no Blueprint
`--app-id`/`--object-id` selector. `--teams-sso` alone does not enable the OAuth redirect on this
command; also pass `--oauth` when that redirect is needed.

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
| `--teams-sso` | `-ts` | Boolean flag | No | `false` | Expose `access_as_user`, preauthorize the supported Teams clients, and enable the OAuth redirect behavior. |
| `--observability` | `-obs` | Boolean flag | No | `false` | Add A365 Observability permissions. |
| `--mcp-discovery` | `-md` | Boolean flag | No | `false` | Add A365 MCP Discovery permissions. |
| `--dry-run` | `-dr` | Boolean flag | No | `false` | Validate input and show the deterministic mutation plan without changing Entra or Azure. |
| `--app-id` | `-app` | GUID | No | None | Application/client ID of an existing App Registration to target. Cannot be combined with `--object-id`. |
| `--object-id` | `-oid` | GUID | No | None | Object ID of an existing App Registration to target. Cannot be combined with `--app-id`. |

For `UserManagedIdentity` and `FederatedCredentials`, all four managed-identity coordinates
(`--subscription`, `--resource-group`, `--location`, and `--managed-identity-name`) must be supplied
together.

New registrations are multitenant (`AzureADMultipleOrgs`). An existing registration must already
use that audience: single-tenant and other audiences are rejected rather than converted.
Without an ID selector, discovery matches the exact display name and rejects duplicates.
`--app-id` and `--object-id` bypass display-name discovery; an unresolved explicit ID fails rather
than creating a replacement. `--display-name` remains required with an ID selector.

Credential modes have different runtime effects:

| Mode | Behavior |
| --- | --- |
| `ClientSecret` | Uses the App Registration as the runtime identity; creates a secret only if no usable CLI-named secret exists. |
| `FederatedCredentials` | Creates or reuses a user-assigned managed identity and reconciles a named federation credential on the App Registration. Runtime configuration uses the App Registration client ID and managed identity `FederatedClientId`. |
| `UserManagedIdentity` | Creates or reuses a user-assigned managed identity as the runtime identity. The App Registration is the companion user-OAuth app; no client secret or federation credential is created. |

Creating a managed identity does not attach it to your runtime host; arrange that separately.

## Permissions and output

Both commands include Messaging Bot `AgentData.ReadWrite` and Power Platform
`Connectivity.Connections.Read` permissions. `--observability` adds
`Agent365.Observability.OtelWrite` delegated permission and application role; `--mcp-discovery`
adds `McpServersMetadata.Read.All`. The resource service principals and named permissions must
exist in the target tenant. `app` merges permission declarations into `requiredResourceAccess`;
`blueprint` configures inheritable permissions (`allAllowed` scopes, and roles where requested).
Neither treats the generated admin-consent URL as completed tenant-wide delegated consent.
Blueprint Observability role-assignment denial can instead produce an administrator handoff.

`--teams-sso` exposes/enables `access_as_user` and preauthorizes the two configured Teams clients
(desktop/mobile and web). It does not configure arbitrary Office clients, publish a Teams package,
or create an Azure Bot Service resource. Reconciliation also registers the admin-consent callback
`https://entra.microsoft.com/TokenAuthorize` independently of the optional Bot Framework OAuth redirect.

Reports have `planned` (dry run), `created`, or `reconciled` status, resource IDs where known,
changes, next actions, and generated runtime configuration. `dotnet` emits JSON configuration;
`node` emits environment-variable-style lines. The configuration is returned in the report,
not written to an appsettings or `.env` file, and never contains a client secret.

**Treat command output as sensitive when using `ClientSecret`.** A newly created secret is emitted
once in text output and the JSON `oneTimeClientSecret` field. Store it securely and inject it into
runtime configuration separately; do not retain it in logs or source control. New secrets request
a one-year lifetime; `app` reports the authoritative returned expiry as `clientSecretExpiresOn`.
An existing usable `AgentHost CLI` secret is retained, but its value cannot be retrieved by rerunning.

Provisioning is not transactional: a later failure can leave applications, principals, permissions,
or managed identities already created. Partial reports retain known coordinates and completed
changes but omit secret values and generated configuration. Review the failed stage and rerun to
reconcile; do not assume a nonzero exit rolled changes back.

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
