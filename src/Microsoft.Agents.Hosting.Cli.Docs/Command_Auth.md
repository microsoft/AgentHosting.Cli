# MSAgent CLI authentication commands

[Back to command index](ReadMe.md)

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent auth login`](#msagent-auth-login) | Sign in using broker or device-code authentication. |
| [`msagent auth status`](#msagent-auth-status) | Show the current sign-in and active-environment state. |
| [`msagent auth switch`](#msagent-auth-switch) | Activate a stored account and/or retarget it at a Power Platform API ring. |
| [`msagent auth logout`](#msagent-auth-logout) | Sign out and clear cached accounts. |

## `msagent auth login`

Signs in using the platform broker or device-code flow, and makes the resulting account active.
On Windows, macOS, and Linux, the command first uses the OS broker unless `--device-code` is set.
If the broker is unavailable, it reports the reason and falls back to device code; canceling sign-in
does not trigger that fallback. On other platforms it uses device code directly. Device-code
instructions are written to stderr.

With `--account`, broker login first attempts silent sign-in for that account before opening its
picker. Without `--account`, it opens the picker rather than silently choosing an account.

```text
msagent auth login [options]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--account` | `-acc` | String | No | None | Account UPN/email to use as a login hint. |
| `--tenant` | `-t` | String | No | Default authority | Explicit target tenant ID. |
| `--resource` | `-r` | String | No | Power Platform API | Resource/audience for which to request a token. Cannot be combined with `--scope`. |
| `--scope` | `-s` | String, repeatable | No | None | Explicit delegated scope. Repeat the option to request multiple scopes. Cannot be combined with `--resource`. |
| `--device-code` | `-dc` | Boolean flag | No | `false` | Use the device-code flow. |
| `--cloud` | `-ci` | String | No | Named stored account's ring, otherwise active account's ring, otherwise `prod` | Power Platform API ring this sign-in targets: `prod`, `preprod`, `test`. |

`--non-interactive` always rejects `auth login` before attempting sign-in, even if a cached token
could be obtained silently. Sign in interactively first, then run other commands non-interactively.

`--cloud` selects a Power Platform API deployment ring, not a sovereign Azure cloud or a different
login authority. Ring values are case-insensitive, surrounding whitespace is ignored, and
`production` is accepted as an alias for `prod`. Accounts and the identity token cache are shared
across rings; each stored account records its current ring.

Login clears persisted environment selections across all rings if the account, tenant, or ring
changes (including when no account was previously active). Signing in again as the same account in
the same tenant and ring retains them. The result reports `signed-in`, the account, tenant, ring
(`cloud`), and whether any selection was cleared (`environmentCleared`); it does not emit a token.

## `msagent auth status`

Reports the current sign-in state and active environment. It never acquires a token or opens identity
UI.

```text
msagent auth status
```

This command has no command-specific arguments or options.

Status reports `signed-in`, `signed-out`, or `stale`, together with the account/tenant when available
and current Power Platform API ring. A stale profile names an account no longer present in the local
MSAL cache. Broker profiles are trusted without enumerating that cache; `signed-in` is not a live
token-validity or service-access check. This command reports the active profile, not an inventory of
all stored accounts.

When present, the persisted environment includes its ID, display name, tenant, and tenant-binding
state. An unbound selection is reported as unusable rather than silently used.

## `msagent auth switch`

Activates an account already present in the local profile store and/or retargets the active account
at a Power Platform API ring. It never acquires a token and performs no sign-in. At least one of
`--account` or `--cloud` must be supplied; otherwise the command fails with "Supply --account,
--cloud, or both."

```text
msagent auth switch [--account <account>] [--cloud <ring>]
```

| Option | Shortcut | Value | Required | Default | Description |
| --- | --- | --- | --- | --- | --- |
| `--account` | `-acc` | String | Conditional | None | Account UPN/email or home account ID to activate. Required unless `--cloud` is supplied. |
| `--cloud` | `-ci` | String | Conditional | None | Retarget the active account at a Power Platform API ring: `prod`, `preprod`, `test`. Required unless `--account` is supplied. |

An exact, case-sensitive home account ID takes precedence over a case-insensitive UPN/email match.
An ambiguous username is rejected; use the exact home account ID. `--cloud` alone requires an active
stored account. With both options, the ring is applied to the account being activated; switching
accounts without `--cloud` preserves that account's recorded ring.

Like login, switching the account, tenant, or ring clears environment selections across all rings;
select the intended environment again. The result reports `switched`, account, tenant, `cloud`, and
`environmentCleared`. No token is acquired for the new ring: subsequent commands acquire one
silently or fail with sign-in/consent guidance. A fresh CLI invocation picks up the new ring;
an embedded host must be rebuilt after retargeting.

## `msagent auth logout`

Clears all local CLI profiles and best-effort clears the CLI's MSAL account/token cache, not just the
active account. It never opens identity UI and does not sign the user out of the OS broker or other
applications. Persisted environment selections are not cleared by logout itself, but cannot be
used while signed out. The result status is `signed-out`.

```text
msagent auth logout
```

This command has no command-specific arguments or options.
