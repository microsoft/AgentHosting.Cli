## Unregistered agent workspace

Use this path when step 2 chose a folder that contains `settings.mcs.yml` and `.mcs\conn.json` but
no registered project. `msagent agent delete` cannot act on it until the workspace is registered, so
you confirm first, then register it with `msagent agent init`, then delete it.

### U1. Read the workspace identity (local only)

Read these files with the Read tool. Do not modify them.

- `<workspaceDir>\.mcs\conn.json` (JSON): `EnvironmentId`, `AgentId` (the cloud agent),
  `DataverseEndpoint`, and `AccountInfo.TenantId`.
- `<workspaceDir>\settings.mcs.yml`: the top-level `displayName` and `schemaName` values. The Read
  tool returns raw text and no YAML parser is available, so accept only these forms of value and
  treat anything else as unreadable:
  - **Double-quoted** (`"..."`) - the only backslash sequences allowed inside are `\"` (a `"`) and
    `\\` (a `\`). Remove the quotes and unescape those two. Any other backslash sequence, such as
    `\u0061` or `\n`, makes the value unreadable.
  - **Single-quoted** (`'...'`) - remove the quotes and replace each `''` with `'`.
  - **Unquoted** - plain text on the same line as the key. Drop a trailing ` #` comment and the
    surrounding whitespace. The value is unreadable if what remains is, ignoring case, `true`,
    `false`, `yes`, `no`, `on`, `off`, `null`, or `~`, if it looks like a number, if it contains
    `: `, if it starts with `- `, `? `, or `: `, or if it starts with `&`, `*`, `!`, `|`, `>`, `[`,
    `{`, `@`, `` ` ``, or `%`, because YAML reads those as something other than a plain string.

  After a closing quote, only whitespace or a ` #` comment may follow on the same line. The value is
  also unreadable if it continues on the next line or if its key appears more than once.

  You use these values only for display, the msagent record name, and the typed confirmation. msagent
  reads `settings.mcs.yml` itself for its schema-name check.

Then:

- If `EnvironmentId` or `AgentId` is missing or empty, tell the user the workspace is not linked to a
  cloud agent, so there is nothing to delete, and stop.
- If `schemaName` is missing, empty, or unreadable, stop: msagent cannot register a workspace
  without a readable `schemaName`.
- If `displayName` is missing, empty, or unreadable, stop: the workspace does not provide the exact
  display name that the deletion confirmation requires.
- If the initial request named an agent, compare it case-insensitively with `displayName` and
  `schemaName`. If it matches neither, tell the user that this workspace holds `<displayName>`
  (`<schemaName>`), not the agent they named, and stop. Nothing is deleted.

### U2. Confirm with the typed agent name (blocking)

Show a summary like this:

```text
About to delete:
  agent         <displayName> (schema name <schemaName>)
  cloud agent   <AgentId> in environment <EnvironmentId>
  Dataverse     <DataverseEndpoint>
  workspace     <workspaceDir>
  registration  This workspace is not registered with msagent. It will first be registered with
                `msagent agent init`, which writes <workspaceDir>\.config\agent.config.json.

This deletes remote resources and cannot be undone. Local files are not deleted, and the
.config\agent.config.json created by the registration stays in the workspace.
```

Ask the user to **type the agent's display name exactly** to confirm:
compare the trimmed answer with `displayName` exactly, reply "That does not match the agent name, so
nothing was deleted." on a mismatch and stop, and treat an empty answer as a decline.

### U3. Register the workspace

```bash
msagent agent init --agent-name '<displayName>' --mcs-agent-source '<workspaceDir>' --json --non-interactive
```

**Success** - `{ "success": true, "status": "agent-initialized", agentId, displayName, agentType,
tenantId, environmentId, connected, projectDirectory, configPath }`.

Before deleting, verify all of these:

- `agentType` is `MCSAgent`, because this skill deletes only MCS agents;
- `connected` is `true`, which means msagent read the workspace's `.mcs` binding; and
- `environmentId` equals `EnvironmentId` from `.mcs\conn.json` (case-insensitive).

If any check fails, **do not delete**. Tell the user which check failed and that msagent did not
register the workspace as the MCS agent that `.mcs\conn.json` names, so a delete could target a
different agent, and that the registration was written to `configPath`. Stop.

**Failure** - relay `errorMessage` and `remediation`, then load the initialization recovery reference
linked in the entrypoint, completely, before any recovery action. Never continue to U4 on failure.
An `already-registered` result needs fresh binding/target/orphan checks and renewed typed
confirmation in that recovery; the U2 confirmation does not cover an existing registration.

### U4. Run the delete

A newly registered workspace has no deployments, so pass no deployment selector:

```bash
msagent agent delete --project '<projectDirectory>' --agent-id '<agentId>' --json --non-interactive
```

Use `projectDirectory` and `agentId` from the U3 result. The CLI deletes the cloud agent that
`.mcs\conn.json` names, after verifying that the workspace's Dataverse organization matches the
environment and that the cloud agent's schema name matches `schemaName` in `settings.mcs.yml`. Then
report the result as in step 7.

If the delete fails, the workspace is now registered: load the deletion recovery reference linked
in the entrypoint before any recovery. A retry uses the same delete command and requires consent.
