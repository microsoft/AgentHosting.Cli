# MSAgent CLI authentication commands

[Back to command index](ReadMe.md)

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent auth login`](#msagent-auth-login) | Sign in using broker, browser, or device-code authentication. |
| [`msagent auth status`](#msagent-auth-status) | Show the current sign-in and active-environment state. |
| [`msagent auth switch`](#msagent-auth-switch) | Activate a stored account and/or retarget it at a Power Platform API ring. |
| [`msagent auth logout`](#msagent-auth-logout) | Sign out and clear cached accounts. |

## `msagent auth login`

Signs in using the platform broker, an interactive browser, or device-code flow. This is the only
authentication command that may open identity UI.

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
| `--cloud` | `-ci` | String | No | Ring the account already uses, otherwise `prod` | Power Platform API ring this sign-in targets: `prod`, `preprod`, `test`. |

`--non-interactive` prevents broker, browser, and device-code interaction; the command fails if the
requested token cannot be obtained silently.

## `msagent auth status`

Reports the current sign-in state and active environment. It never acquires a token or opens identity
UI.

```text
msagent auth status
```

This command has no command-specific arguments or options.

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

## `msagent auth logout`

Signs out and clears cached accounts. It never opens identity UI.

```text
msagent auth logout
```

This command has no command-specific arguments or options.
