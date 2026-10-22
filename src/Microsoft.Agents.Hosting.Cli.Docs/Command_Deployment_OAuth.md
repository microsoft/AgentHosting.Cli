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
`--deployment-id` (`-did`) must be a GUID and cannot be combined with `--agent-id` (`-aid`) or
`--deployment-name` (`-dn`). With name addressing, the agent ID must be a GUID. `--project` (`-p`)
defaults to the current directory. `--agent-name` and Azure coordinate overrides are not accepted.
The deployment must already be recorded locally; its stored bot resource ID and tenant determine
the Azure target. The group has no feature gate of its own.

The `--connection` (`-con`) value is the OAuth connection name, unique within the bot. It must be
2-64 characters, start with a letter or digit, and contain only whitespace, letters, digits, `.`,
`_`, or `-`.
Names are trimmed before validation; letters and digits here are ASCII.

These are live Azure operations, not local-only configuration edits. Create/update/show save safe
connection snapshots locally, list replaces the stored OAuth snapshots, and delete removes a local
snapshot after the cloud delete. A local-save failure does not undo a successful Azure mutation.
Secrets are masked in output and local records and are never recovered from masks for an update.
With `--debug`, deployment results go to stderr as a summary or redacted ARM response instead of the
normal human/JSON success payload (including when `--json` is supplied).

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

Provider names are case-insensitive and must be supported by the CLI as well as present in the live
catalogue; listing a newly advertised provider does not necessarily make it creatable. The live schema
determines required fields, parameter types, and optional defaults. Unsupported common options are
rejected when the provider does not declare them.

`--parameters` can be repeated and accepts several assignments per occurrence. Quote assignments
containing spaces. Keys are case-insensitive; unknown keys and duplicates (including case-only
duplicates) are rejected. Use `--client-id`, `--client-secret`, and `--scopes` for common OAuth fields,
not `--parameters ClientId=...`, `ClientSecret=...`, or `Scopes=...`. Create rejects blank values and
requires all provider-required fields, even when the connection already exists.

After validation, an existing connection is returned with status `existed` without changing it.
A new connection returns `created`; use `oauth update`, not a repeated create, to change settings.

```text
msagent deployment oauth list-providers --provider-name Aadv2
msagent deployment oauth create --deployment-id <id> --connection MyOAuth --provider-name Aadv2 --client-id <app-id> --client-secret <secret> --scopes "User.Read" --parameters TenantId=<tenant-id>
```

## `msagent deployment oauth delete`

Deletes an OAuth connection setting. Requires `--connection` (`-con`) plus the deployment selectors.
There is no confirmation prompt. An already absent connection is treated as deleted; the local
deployment record itself must still exist. The result reports deletion without a connection payload.

## `msagent deployment oauth list`

Lists the OAuth connection settings on a deployment. Accepts only the deployment selectors and
`--project`.
Reads live connections and the provider catalogue, masks secrets, and refreshes the local snapshots.
The result contains a `connections` array.

## `msagent deployment oauth list-providers`

Lists the live Azure Bot Service OAuth provider catalogue.

| Option | Shortcut | Value | Description |
| --- | --- | --- | --- |
| `--provider-name` | `-pn` | String | The provider name returned by the live catalogue; shows that provider's schema. |
| `--subscription` | `-sub` | String | Target subscription id; auto-resolved from the signed-in account when omitted. |
| `--tenant` | `-t` | String | Target tenant id; taken from the resolved subscription when omitted. |

No deployment or project selector is accepted or needed. Subscription and tenant values must be
GUIDs when supplied; explicitly blank options are rejected. An omitted subscription requires an
unambiguous account context. With no provider filter, results are sorted by provider name; with
`--provider-name`, a case-insensitive exact match returns that provider's schema, or not found.

## `msagent deployment oauth show`

Gets an OAuth connection setting. Requires `--connection` (`-con`) plus the deployment selectors.
Returns the safe live `connection` snapshot and refreshes its local copy. Missing connections fail as
not found rather than falling back to cached data.

## `msagent deployment oauth update`

Updates an OAuth connection setting. Requires `--connection` (`-con`) plus the deployment selectors,
and accepts `--client-id`, `--client-secret`, `--scopes`, and `--parameters` with the same meanings
as for `oauth create`.

At least one update field is required. There is no `--provider-name` option: the live connection's
provider determines validation and cannot be changed through this command. The CLI reads the live
connection, overlays supplied settings, and writes the result. Omitted readable values are preserved;
omitted secrets are not replayed from masked responses. Resupply secrets when changing them.
An explicit empty option or `--parameters Key=` clears an optional field, but required fields cannot
be cleared. Optional defaults are applied on create, not reapplied over an update clear.
Missing connections fail as not found. Success returns `updated` with the safe connection snapshot.

```text
msagent deployment oauth update --deployment-id <id> --connection MyOAuth --scopes "User.Read Mail.Read"
```
