---
name: delete-agent
description: Delete a Copilot Studio agent's cloud resources and its msagent project records with `msagent agent delete`, after an explicit typed-name confirmation. Works on a registered msagent project, or on a cloned agent workspace (`settings.mcs.yml` + `.mcs/conn.json`) that it first registers with `msagent agent init`. Local files are never deleted. Use when the user asks to delete, remove, or destroy a Copilot Studio agent in the cloud.
metadata:
  argument-hint: Optional project or agent workspace folder path and/or agent name
allowed-tools: Read Glob Grep
---

# Delete a Copilot Studio Agent

You are a workflow that deletes a Copilot Studio (MCS) agent from the cloud by running the
**msagent CLI** (`msagent agent delete`). You resolve every input up front, show the user exactly
what will be deleted, require them to type the agent's name, and only then run the delete. This is a
destructive, irreversible remote operation. You never invent behavior the CLI does not support.

Initial request: $ARGUMENTS

## Deletion scope

Delete one cloud MCS agent (`agentType: MCSAgent`), its `ManagedAgent` record, and
all its `AgentDeployment` records. Other deployments' cloud agents remain running
as untracked orphans. Never delete local files: only `.config\agent.config.json`
changes, through the CLI.

Accept a registered project (`.config\agent.config.json`, created by `agent create`
or `agent init`) or an unregistered workspace (`settings.mcs.yml` + `.mcs\conn.json`).
The latter must first be registered with `msagent agent init`: this writes the
local config, creates no cloud resources, and leaves the config after deletion.
The connection supplies cloud `AgentId` and `EnvironmentId`; settings supply
`schemaName` and `displayName`.

## Rules

- Before **either** `msagent agent init` or `msagent agent delete`, require the exact
  typed display name (step 5 or U2). `--non-interactive` bypasses the CLI confirmation;
  this skill's typed-name confirmation is mandatory. Do not pre-approve init or delete:
  retain the host's command-approval gate as an independent safeguard.
- Always pass `--json --non-interactive`; select deletion by `--agent-id`, never by name.
- Never hand-edit `.config\` or `.mcs\`, delete local files, or bypass the documented MSAGENT deletion flow.
  Only CLI init/delete may create or rewrite the config; leave it in place.
- Never retry without user consent. If any input changes, repeat exact typed-name confirmation
  before either init or delete. A timeout may have applied; never retry blindly.
- Treat the former CLI name `ah` in messages/remediation as `msagent`.

## Passing values to commands

All substituted values, including values from files, are untrusted. Never double-quote
them: Bash/PowerShell can expand `$(...)`, `$name`, or backticks into commands.

- `agentId`, `deploymentId`, and `tenantId` must match
  `^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$`.
  Otherwise stop, report a non-GUID and possible hand-edited config, and run nothing.
- For `projectDir`, `workspaceDir`, `projectDirectory`, and `displayName`, reject
  double quotes, line breaks, and any control character; report the invalid value
  and construct no command. Otherwise use one single-quoted literal, escaping:
  - Bash: each `'` becomes `'\''`; `Tom's Bot` becomes `'Tom'\''s Bot'`.
  - PowerShell: double `'`, `‘`, `’`, `‚`, and `‛`; `Tom's Bot` becomes
    `'Tom''s Bot'` and `O’Brien` becomes `'O’’Brien'` (passed as `O’Brien`).
    An undoubled smart quote terminates the literal and may execute remaining text.

Apply these rules to every `'<value>'` in the templates.

---

## Core Process

### 1. Verify the CLI (blocking on failure)

```bash
msagent --version
```

If the command is not found, tell the user: "The msagent CLI is required but was not found. Install
it, then retry." Stop.

### 2. Locate the project or agent workspace (blocking)

A registered project has `.config\agent.config.json`. An unregistered workspace
has `settings.mcs.yml` + `.mcs\conn.json` but no registered config. Workspaces may
be at the project root or in a direct child.

If a folder was supplied, check in order:

1. Registered config in that folder or its parent: use the directory containing
   `.config` and continue to step 3. If the supplied folder differs from the registration
   root, retain its canonical path as an exact workspace constraint even if workspace
   markers are missing. Also retain it when an authored workspace is at the project root.
   Discovering a parent project does not authorize another agent.
2. Both workspace markers: use [Unregistered agent workspace](#unregistered-agent-workspace).
3. Settings without a connection: explain it is not linked to a cloud agent and
   has nothing to delete; ask for another folder.
4. Otherwise inspect direct children only (one level). List matching registered
   projects and unregistered workspaces, indicating their type; require a numbered
   choice even for one match, then follow that route.
5. No child match: explain neither the folder nor its children qualify and ask again.

Without a supplied folder, discover `Glob: **/.config/agent.config.json` first.
Require a numbered choice even for one result; never silently select a match.
Only if none exist, discover `Glob: **/.mcs/conn.json` whose workspace also has
`settings.mcs.yml`; require a numbered choice and follow the unregistered route.
If nothing qualifies, report both missing marker sets and ask for a folder.
Repeat until the user supplies a usable folder or stops.

## Load the selected workflow

Read only the reference for the selected route, completely, before its commands. Every reference is
bundled inside this skill's directory; never use pluginRoot or sibling skills. If the reference
cannot be read, stop and report its path before any side effect.

## Registered project

For a project with `.config\agent.config.json`, read the complete
[registered-project workflow](references/registered-project.md) before running `agent show`.
Follow steps 3-6 to resolve an exact MCS agent and cloud target, obtain explicit orphan
acknowledgment if needed, and require the exact typed display name before deletion. Do not load the
unregistered-workspace instructions for this route.

## Unregistered agent workspace

Only for `settings.mcs.yml` + `.mcs\conn.json` with no registered config: before reading its identity
or running any command, read the complete
[unregistered-workspace workflow](references/unregistered-workspace.md) and follow U1-U4.
This validates identity, confirms the typed display name before init, verifies the returned binding,
and deletes by local agent ID. Do not load the registered workflow during successful new
registration. Load it only if failure recovery requires inspecting existing records; an existing
registration requires fresh target/orphan checks and renewed typed confirmation.
If the reference cannot be read, stop; never improvise this destructive path.

## 7. Report the result (either route)

**Success** - `{ "success": true, "status": "agent-deleted", agentId, displayName, environmentId,
deploymentId, mcsAgentAlreadyAbsent, removedAgentDeployments, orphanedTargets }`:

- Say `displayName` (`agentId`) was deleted.
- If `mcsAgentAlreadyAbsent` is `true`, no cloud agent existed, so only the records were removed.
  Otherwise, say the cloud agent was deleted from `environmentId`.
- Records removed: 1 ManagedAgent + `removedAgentDeployments` AgentDeployment row(s).
- List `orphanedTargets` (`deploymentName`, `environmentId`). This is the CLI's authoritative list,
  so use it even if it differs from the step 4 preview.
- Remind the user the local workspace is still on disk; they can remove it themselves if they no
  longer need it. The only local file that changed is `.config\agent.config.json`, which no longer
  holds this agent's records. If this run registered the workspace, also say that the file was created
  by that registration and remains in the workspace.

If `status` is `cancelled` or `name-mismatch`, tell the user that nothing was deleted.

**Failure** - `{ "success": false, exitCode, errorMessage, errorKind?, remediation?, partialResult? }`.
Always surface `errorMessage`, `errorKind`, and `remediation`; summarize `partialResult` when present.
Only after a command fails, read the matching recovery reference, completely, before acting on
remediation, authenticating, or retrying:

| Failed command | Required recovery |
|---|---|
| `msagent agent delete` (either route) | [Deletion recovery](references/delete-recovery.md) |
| `msagent agent init` | [Initialization recovery](references/init-recovery.md) |

Do not read these on success or cancellation. If required recovery cannot be read, stop and report
the missing file. Never claim that a failed or timed-out operation made no changes without evidence.

## Final answer

Keep it short and factual: which agent was deleted and from which environment, how many records were
removed, any orphaned targets, and that no local files were deleted: only `.config\agent.config.json`
changed (or, if the skill registered the workspace, was created), and it remains.
