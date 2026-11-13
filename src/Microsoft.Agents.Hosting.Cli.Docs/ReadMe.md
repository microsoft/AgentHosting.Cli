# MSAgent CLI command reference

Last updated: 2026-11-06

This document describes the MSAgent CLI command surface. The packaged executable is named
`msagent`, and the CLI project is `Microsoft.Agents.Hosting.Cli`.

```text
msagent [command] [options]
```

The reference is derived from the command host in `Microsoft.Agents.Hosting.Cli`, command
definitions in `Microsoft.Agents.Hosting.Cli.Commands`, and validation performed by
`Microsoft.Agents.Hosting.Cli.Core`. It documents command-line arguments and options
only; host configuration environment variables are documented in
[agent.md](../Microsoft.Agents.Hosting.Cli/agent.md#configuration-host-environment).

Commands or options labeled **MCS-specific** apply to Microsoft Copilot Studio. Agent
`create`, `init`, `list`, `pull`, `delete`, and `deploy` also support standalone workflows
(`MCSWorkflow`); sharing and evaluation are **MCSAgent-only**. Unlabeled commands either
apply to all agent types or retain their existing CodeAgent behavior.

For error meanings, causes, and recovery steps, see the [error code reference](ErrorCodes.md).

## Global options

The first four options are available recursively on every command. Help is available on every
command; version is available only at the root.

| Option | Value | Default | Description |
| --- | --- | --- | --- |
| `-j`, `--json` | Boolean flag | `false` | Emit machine-readable JSON output. |
| `-dbg`, `--debug` | Boolean flag | `false` | Display debugging information, such as redacted API responses, on stderr. |
| `-nc`, `--no-color` | Boolean flag | `false` | Disable colored output. Color is also disabled when `NO_COLOR` is nonempty or both stdout and stderr are redirected. |
| `-ni`, `--non-interactive` | Boolean flag | `false` | Request non-interactive operation. Commands have specific limits: some connection commands still read stdin, and the external playground can still require sign-in. See the command reference. |
| `-?`, `-h`, `--help` | Boolean flag | `false` | Show help and usage information for the current command. |
| `-v`, `--version` | Boolean flag | `false` | Show version information. Available only at the root (`msagent --version`). |

In non-interactive mode, `deployment connection add --connection-only` and
`deployment connection update` can still create connections and print a consent link to open
manually. Plain `deployment connection add` creates a connection only when there is no existing
connection to choose from. The update command lists connectors and reads selections from stdin.

Invoking `msagent` with no command, including when only global options are supplied, displays root
help.

## Output and exit codes

In a terminal, eligible commands and help (including bare `msagent`) can first show a newer-version
notice on stderr, including labeled prereleases and a manual update command.
Notices are suppressed for `--json`, version output, invalid/navigation-only commands,
CI, or redirected stdout/stderr. No command-specific options
are added; `--non-interactive` alone does not suppress notices. See the
[update-check configuration](../Microsoft.Agents.Hosting.Cli/agent.md#automatic-version-notices)
for opt-out, cache, and deadline behavior.

Most commands write human-readable results to stdout and failures with recovery guidance to
stderr. With `--json`, ordinary command results use a JSON envelope on stdout: successful
results contain `success: true`, while failures contain `success: false`, `exitCode`,
the five-digit integer `errorCode`, and `errorMessage`. `errorKind` and `remediation` are
included when available; some failures also include a partial result. A usage error need not
have an `errorKind`. Error codes identify conditions and are not process exit codes.

| Exit code | Meaning |
| --- | --- |
| `0` | The command completed successfully. |
| `1` | The operation failed. |
| `2` | Invalid usage, configuration, context, or a conflicting operation. |
| `3` | Sign-in or user interaction is required. |
| `130` | Caller cancellation; authentication can also report safe committed/cleanup state. |

Live-test commands have their own output contract: `test ask --json` streams one activity/result
document, not the ordinary envelope, and `test playground` rejects `--json`. Playground
cancellation can return `130`, and a launched playground's nonzero exit status can be propagated.
See [live testing](Command_Test.md). Deployment `--debug` output can replace the ordinary success
payload with diagnostic output on stderr; do not combine it with `--json` when expecting a
normal success envelope.

## Command modules

| Module | Subcommands | Description |
| --- | --- | --- |
| [`msagent auth`](Command_Auth.md) | `create`, `list`, `select`, `delete`, `name`, `update`, `clear`, `who`, `login`, `status`, `logout` | Authenticate users/workloads and manage owned profiles without exporting tokens. |
| [`msagent env`](Command_Env.md) | `list`, `select` | List and select Power Platform environments. |
| [`msagent agent`](Command_Agent.md) | `create`, `init` (**MCS-specific**), `list` (**MCS-specific**), `show`, `pull` (**MCS-specific**), `delete` (**MCS-specific**), `deploy`, `share` (**MCSAgent-only**), `share show` (**MCSAgent-only**), `unshare` (**MCSAgent-only**), `package upload` (**Gated**), [`eval`](Command_Agent_Eval.md) {`run`, `list`, `show`, `cancel`, `delete`, `generate`, `testset`} (**Gated**, **MCSAgent-only**) | Create, list, inspect, and delete hosted agents or standalone workflows; initialize or pull MCS source; deploy agents and workflows; manage MCS agent sharing; run MCS evaluations; and upload agent packages. |
| [`msagent deployment`](Command_Deployment.md) | `create`, `list`, `show`, `update`, `delete`, [`channel`](Command_Deployment_Channel.md) {`create`, `update`, `delete`, `show`, `list`} (**CodeAgent-specific**), [`connection`](Command_Deployment_Connection.md) {`list`, `add`, `update`, `remove`, `connectors`} (**MCS-specific**), [`oauth`](Command_Deployment_OAuth.md) {`create`, `delete`, `list`, `list-providers`, `show`, `update`} (**CodeAgent-specific**) | Manage deployment records, CodeAgent channels and OAuth settings, and MCS connections. |
| [`msagent test`](Command_Test.md) (**MCS-specific**) | `ask`, `playground` | Send a live query or launch the M365 Agents Playground. Actions can have real effects. |
| [`msagent abs`](Command_Abs.md) (**Gated**) | `preview`, `apply` | Preview and apply Azure Bot Service configuration. |
| [`msagent identity`](Command_Identity.md) (**Gated**) | `create blueprint`, `create app` | Create and reconcile agent identities. |
| [`msagent telemetry`](Command_Telemetry.md) | `enable`, `disable`, `status` | Manage and inspect command telemetry collection preferences. |

**Gated:** commands tagged (**Gated**) may or may not be available, depending on the feature gates
enabled for a given run. Run `msagent --help` (or `--help` on any group) to see what is currently
available.

The intermediate groups `auth`, `env`, `agent`, `agent eval`, `agent eval testset`, `deployment`,
`deployment connection`, `deployment channel`, `deployment oauth`, `agent package`, `abs`, `identity`, `identity create`,
`test`, and `telemetry` are navigation commands. Invoking one without a leaf command returns a usage error
and lists its valid subcommands.

> [!IMPORTANT]
> For Microsoft Copilot Studio (MCS) agent types, this CLI replaces the following PAC CLI commands:
>
> | PAC CLI command | `msagent` equivalent |
> | --- | --- |
> | `pac copilot list` | [`msagent agent list`](Command_Agent.md#msagent-agent-list---mcs-specific) |
> | `pac copilot init` | [`msagent agent init`](Command_Agent.md#msagent-agent-init---mcs-specific) |
> | `pac copilot status` | No direct equivalent. [`msagent agent show`](Command_Agent.md#msagent-agent-show) and [`msagent deployment show`](Command_Deployment.md#msagent-deployment-show) read local state only. |
> | `pac copilot pack` | No standalone command. Does not apply to Agents created with msagent |
> | `pac copilot create` | [`msagent agent create --agent-type MCSAgent`](Command_Agent.md#msagent-agent-create) |
> | `pac copilot publish` | [`msagent agent deploy --publish`](Command_Agent.md#msagent-agent-deploy) |
> | `pac copilot pull` | [`msagent agent pull`](Command_Agent.md#msagent-agent-pull---mcs-specific) |
> | `pac copilot clone` | [`msagent agent create --agent-type MCSAgent --mcs-clone-agent-id <id>`](Command_Agent.md#msagent-agent-create) (or `--mcs-clone-agent-schemaname`) |
> | `pac copilot push` | [`msagent agent deploy`](Command_Agent.md#msagent-agent-deploy) |
> | `pac copilot delete` | [`msagent agent delete`](Command_Agent.md#msagent-agent-delete---mcs-specific) |
>
> Do not use the PAC CLI together with this CLI for these commands. Use `msagent` for them instead.
