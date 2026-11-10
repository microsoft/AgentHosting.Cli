# MSAgent CLI authentication commands

[Back to command index](ReadMe.md)

Authentication commands can display the host's [version notice](ReadMe.md#output-and-exit-codes)
before their output or sign-in prompt. This adds no auth options or token acquisition; `--json`
suppresses the notice.

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent auth create`](#msagent-auth-create) | Authenticate, create or replace a profile, and activate it. |
| [`msagent auth list`](#msagent-auth-list) | List token-free metadata and transient positions. |
| [`msagent auth select`](#msagent-auth-select) | Activate a stored profile without authenticating. |
| [`msagent auth delete`](#msagent-auth-delete) | Delete one profile and its unshared owned credential state. |
| [`msagent auth name`](#msagent-auth-name) | Name a profile by index. |
| [`msagent auth clear`](#msagent-auth-clear) | Clear or repair only owned msagent state. |
| [`msagent auth who`](#msagent-auth-who) | Show selected profile, identity and environment metadata. |
| [`msagent auth login`](#msagent-auth-login) | Legacy interactive sign-in; requires `LegacyAuth` (off by default). |
| [`msagent auth status`](#msagent-auth-status) | Retained acquisition-free sign-in status contract. |
| [`msagent auth logout`](#msagent-auth-logout) | Legacy sign-out; requires `LegacyAuth` (off by default). |

Eight commands are available by default: create, list, select, delete, name, clear, who and status.
Login/logout are absent from help and parsing until the `legacyAuth` feature control is enabled.
For process-local compatibility opt-in:

```powershell
$env:AH_CLI_FEATURES = "LegacyAuth"
```

Use `auth create` for sign-in, `auth delete` to remove one profile, or `auth clear` to reset all
owned state. `auth status` is not gated. Enabling legacy authentication does not enable other
feature-controlled command groups. Create defaults to `Public`; explicitly supply `--cloud` when
recreating a sovereign or nonproduction profile rather than relying on legacy login inheritance.

`--json` changes presentation, not interaction permission. List, select, status, who and name
never renew tokens, start credential sources, open identity UI, or unlock native storage.
No command exports tokens.
Authentication has one `--cloud` selector, not a separate `--ring` option.

## `msagent auth create`

```text
msagent auth create [options]
```

| Option | Local shortcut | Meaning |
| --- | --- | --- |
| `--name` | `-n` | Optional case-sensitive name, 1–30 characters. |
| `--environment` | `-env` | Default environment ID, trusted organization URL, or unambiguous display name. |
| `--cloud` | `-ci` | `Public`, `Preprod`, `Test`, `UsGov`, `UsGovHigh`, `UsGovDod`, `China`, `UsNat`, `UsSec`; default `Public`. |
| `--tenant` | `-t` | Nonzero tenant GUID. |
| `--deviceCode` | `-dc` | Interactive device-code authentication. |
| `--username` | `-un` | Username; requires `--password`. |
| `--password` | `-pw` | ROPC password; requires `--username`. Never persisted. |
| `--applicationId` | `-id` | Nonzero application GUID for a workload method. |
| `--clientSecret` | `-cs` | Application secret. |
| `--certificateDiskPath` | `-cdp` | PFX file containing a private key. |
| `--certificatePassword` | `-cp` | PFX password; requires the certificate path. |
| `--managedIdentity` | `-mi` | Configured DefaultAzureCredential sources; not a guarantee that managed identity wins. |
| `--githubFederated` | `-ghf` | GitHub Actions workload federation. |
| `--azureDevOpsFederated` | `-adof` | Azure DevOps workload federation. |

These shortcuts are local to create. Lifecycle selector shortcuts are documented below.
Credential modes are exclusive. Client secret, certificate, and federation require explicit tenant
and application IDs. An application ID without an applicable workload method is invalid.
Names are ordinal case-sensitive and unique: `Dev` and `dev` are distinct.
Recreating a matching authenticated profile may reuse its existing name; a different profile cannot.

Without a workload method or username/password pair, create performs interactive authentication.
An explicitly interactive invocation, including device code, with `--non-interactive=true` fails
with **12007 / exit 3 before cache enumeration or authentication**; cached accounts do not bypass
this gate. Workload and ROPC methods are eligible for non-interactive use, subject to tenant policy.
ROPC cannot satisfy MFA or interactive Conditional Access and does not persist the password for
later renewal.

Broker-backed renewal resolves the profile's exact home account through its broker-enabled
cache reader; it never substitutes the machine's default OS account. If that account is no
longer available, run `msagent auth create` for the intended identity and cloud before retrying.

> Passwords, client secrets and certificate passwords passed as arguments may be visible in shell
> history or process inspection. CLI diagnostic redaction does not protect those external surfaces.
> Never include credentials, assertions or caches in logs, tickets or scripts committed to source control.

GitHub federation uses the Actions-provided `ACTIONS_ID_TOKEN_REQUEST_URL` and
`ACTIONS_ID_TOKEN_REQUEST_TOKEN`. Azure DevOps federation requires the host runtime variables
`MSAGENT_ADO_ID_TOKEN_REQUEST_URL` and `MSAGENT_ADO_ID_TOKEN_REQUEST_TOKEN`. Both request audience
`api://AzureADTokenExchange`; assertions are not persisted. Configure matching federated trust in
the explicit tenant/application. The CLI rejects redirects and does not replay a failed exchange.

DefaultAzureCredential baseline environment, workload-identity and managed-identity sources are
eligible in all interaction modes. A persisted developer-source pin (Azure CLI, PowerShell or azd)
requires interaction permission on renewal; non-interactive renewal fails before constructing the
source or launching a process. The CLI does not silently change the pinned identity.

Owned credentials, certificate passwords and caches use protected storage. Windows requires
current-user DPAPI; macOS requires usable Keychain access under the host's process-lifetime
no-prompt policy; Linux requires GIO/D-Bus and an accessible, already-unlocked Secret Service.
The CLI does not prompt to unlock stores. The optional Linux unprotected **token-cache-only**
baseline never permits plaintext credential persistence. Source PFX files remain caller-owned.
Native OS/RID operation requires separate platform validation; offline tests do not prove it.

Cloud values select immutable registered authentication and PPAPI coordinates. Sovereign
nonproduction combinations are not supported. China PPAPI uses
`https://api.powerplatform.partner.microsoftonline.cn`; unsupported derived downstream routes and
UsNat/UsSec Copilot Studio gateways fail closed. Profile creation is not proof of downstream
service support or tenant authorization.

An equal authentication identity replaces its profile while retaining its opaque ID; otherwise
creation appends a profile. Only validated/acquired state is activated. A failed precommit operation
does not replace the active profile. Cancellation after commit does not imply rollback.

## `msagent auth list`

```text
msagent auth list [--json]
```

Lists safe profile/name/cloud/principal/default metadata, an active marker, and **1-based transient**
positions. Rerun list before using an index after creation or deletion; positions are not persistent IDs.

## `msagent auth select`

```text
msagent auth select --index <position>
msagent auth select --name <case-sensitive-name>
msagent auth select
```

Use `-i` for `--index` and `-n` for `--name`.

Supply exactly one selector, or omit both to use the metadata-only picker when interaction is
allowed. There is no implicit first-profile selection. Without selectors in non-interactive mode,
the command fails with 12007 / exit 3. Selection does not authenticate or change the profile's cloud.

## `msagent auth delete`

```text
msagent auth delete --index <position>
msagent auth delete --name <case-sensitive-name>
```

Use `-i` for `--index` and `-n` for `--name`.

Exactly one selector is required. Inactive deletion leaves the active selection unchanged. Active
deletion reports the first persisted remaining Universal-kind fallback, or no active profile if none
remains. No authentication occurs. Cleanup removes only unshared msagent-owned credential/cache
state, never PAC/Azure/broker accounts or source certificates.

## `msagent auth name`

```text
msagent auth name --index <position> --name <new-name>
```

Use `-i` for `--index` and `-n` for `--name`.

Both options are required. Names contain 1–30 characters and are case-sensitive unique.
Renaming an inactive profile does not activate it.

## `msagent auth clear`

```text
msagent auth clear
```

Repairs even corrupt profile metadata and clears owned msagent profiles, environment overrides,
credentials and caches. It never clears shared PAC/Azure/broker state or source PFX files.
If owned cleanup is incomplete, safe durable-state guidance is reported and cleanup can be retried
after restoring storage access or waiting for in-flight owned reference use to finish.

## `msagent auth who`

```text
msagent auth who [--json]
```

Shows selected profile/identity metadata and the effective stored environment binding without
token acquisition. Target precedence is explicit command environment, compatible owned override,
profile default, then a missing-target error. `env select` updates the selected profile's default.
Foreign or incompatible selection state is not silently reused.

## `msagent auth login`

Requires `LegacyAuth` (off by default). Prefer `msagent auth create` for normal workflows.

```text
msagent auth login [--account <hint>] [--tenant <guid>] [--device-code]
                   [--resource <resource> | --scope <scope> ...] [--cloud <cloud>]
```

Retains `--account` (`-acc`), `--tenant` (`-t`, `--tenant-id`), `--resource` (`-r`),
repeatable `--scope` (`-s`), `--device-code` (`-dc`), and `--cloud` (`-ci`).
Login recognizes the nine clouds plus `prod`/`production` → `Public`, `preprod` → `Preprod`,
and `test` → `Test`. An omitted or blank cloud uses the approved compatible stored-profile inheritance.
The browser/broker/device flow follows platform policy; device-code instructions go to stderr.
`--non-interactive=true` always rejects explicit login before any account/cache enumeration.
The existing `signed-in`, `account`, `tenantId`, `cloud`, and `environmentCleared` JSON shape is
retained; it is not replaced by the new create result.

## `msagent auth status`

```text
msagent auth status [--json]
```

Retains the existing `signed-in`, `signed-out`, or `stale` status shape and environment metadata.
Only the selected profile's applicable private nonbroker user cache is inspected. Broker,
application and DefaultAzureCredential status do not enumerate external credentials.
Signed-in status does not prove token freshness or downstream service access.

## `msagent auth logout`

Requires `LegacyAuth` (off by default). Prefer `msagent auth clear` to clear all owned
authentication state, or `msagent auth delete` to remove only one profile.

```text
msagent auth logout [--json]
```

Retains the `signed-out` result and exit contracts while clearing owned authentication state.
Like clear, it does not sign out shared applications or the OS broker.

## Cancellation and partial operations

Caller cancellation returns **130**. Text guidance is on stderr; JSON is a single failure envelope
on stdout. Recognized partial state reports `metadataCommitted`, `cleanupIncomplete`, safe profile
metadata when available, and recovery guidance. Primary noncancel failures retain their original
numeric error and exit code. A precommit cancellation is not a commit; a postcommit cancellation
is not a rollback. Inspect `auth list`/`auth who` before another mutation, and retry owned cleanup
with `auth clear` only when clearing the retained state is intended.
Committed deletion also reports `profileDeleted` and the safe `activeProfile` snapshot when one
remains; the deleted profile is no longer active. This describes the selection at deletion commit,
not later selections by another invocation.
