# Registered project deletion

Read only for a registered project, or after initialization recovery identifies an existing
registration. Apply the entrypoint's confirmation, command-validation, and no-local-deletion rules
throughout. For ordinary registered deletion, do not read unregistered or recovery instructions.

### 3. Read the project's records (local only)

```bash
msagent agent show --project '<projectDir>' --json
```

This reads the local configuration only and contacts nothing. The JSON envelope is
`{ success, schemaVersion, projectDirectory, configPath, agents, deployments }`:

- `agents[]` - each has `agentId`, `displayName`, `agentType`, `agentLocation`, `tenantId`,
  `environmentId` (the agent's home environment, not necessarily its deletion target), and, when known,
  `mcsAgentId` and `mcsSchemaName`.
- `deployments[]` - each has `deploymentId`, `agentId` (its owning agent), `deploymentName`,
  `deploymentType` (the stage), and optionally `environmentId`. A deployment without
  `environmentId` targets its agent's home `environmentId`.

If `success` is `false` and `errorKind` is `project-not-found` or `config-not-found`, tell the user the
folder is not a registered msagent project and return to entrypoint step 2. For any other failure,
relay `errorMessage` and `remediation` and stop. If `agents` is empty, tell the user no agents are
registered in `configPath` and return to entrypoint step 2.

### 4. Choose the agent and the cloud target (blocking)

1. **Agent.**
   - **A workspace was supplied.** Resolve every MCS record's `agentLocation` relative to
     `projectDir`, normalize to its canonical filesystem path, and require exactly one match
     to the retained workspace constraint. Apply any supplied name or ID cumulatively.
     On a missing or ambiguous match, stop before init or delete and report the mismatch.
     Never substitute the project's sole agent or register the workspace implicitly.
   - **The initial request named an agent.** Match the name case-insensitively against each agent's
     `displayName` and `agentId`. Never substitute an agent the user did not name.
     - **One match** - use it, and confirm the match with the user.
     - **Several matches** - list the matching agents (`displayName`, `agentId`) and ask the user for
       the `agentId` of the one to delete. Continue only if the answer matches exactly one of them;
       otherwise stop.
     - **No match** - tell the user that no agent named "<name>" is registered in `configPath`, list
       the registered agents (`displayName`, `agentId`), and stop. Nothing is deleted.
   - **No agent named and no workspace supplied.** If there is one agent, use it. If there are several, present a numbered
     pick-list (`displayName`, `agentId`, `agentType`).
   - If the chosen agent's `agentType` is not `MCSAgent`, stop: this skill deletes only MCS agents.
2. **Cloud target.** Consider only the deployments whose `agentId` equals the chosen agent's.
   - **No deployments** - pass no deployment selector. The CLI deletes the cloud agent the
     workspace's `.mcs` binding names or, when the workspace has none, the agent that has the
     agent's schema name in its home environment. Resolve the actual target before confirmation:
     - Resolve `agentLocation` relative to `projectDir` and retain the canonical workspace path.
     - If the workspace is absent, show a records-only deletion with no cloud target.
     - If `.mcs/conn.json` exists, read it without modifying it. Require nonempty `AgentId` and
       `EnvironmentId`; show that bound cloud ID and environment, never the record's home
       environment alongside a bound cloud ID. An unreadable or incomplete binding is an error,
       not permission to fall back to the home environment.
     - Only when the workspace exists and has no binding, use its known `mcsSchemaName` and the
       record's home `environmentId`. Show this schema-based home-environment target explicitly.
       If the schema or target environment cannot be established, stop before confirmation.
   - **One deployment** - confirm it with the user and pass its `--deployment-id`, so that the target
     is exact.
   - **Several deployments** - present a numbered pick-list (`deploymentName`, `deploymentType`,
     environment) and ask which deployment's cloud agent to delete. Pass its `--deployment-id`.
3. **Orphans.** When a deployment is selected, every **other** deployment of this agent becomes an
   orphaned target. If there are any, list them (`deploymentName` and environment) and warn:

   > These cloud agents will **not** be deleted and will keep running, but their records will be
   > removed, so msagent will no longer track them. Remove them separately if they should not keep
   > running.

   Require the user to explicitly acknowledge this before continuing. If they do not, stop; nothing
   is deleted.

### 5. Confirm with the typed agent name (blocking)

Show a summary like this, substituting the actual target resolved in step 4:

```text
About to delete:
  agent         <displayName> (<agentId>)
  project       <projectDir>
  cloud target  <resolved cloud agent ID or schema> in <resolved environmentId>
  records       1 ManagedAgent + <N> AgentDeployment row(s)
  orphaned      <deploymentName in environmentId, ...> | none

This deletes remote resources and cannot be undone. Local files are not deleted; msagent only
removes this agent's records from <configPath>.
```

For a selected deployment, show its name and effective environment. With no deployment, show the
bound cloud ID and bound environment, the unbound schema-based home target, or `none (records
only)` for an absent workspace. Do not print unresolved alternatives or label a bound target with
the home environment.

Ask the user to **type the agent's display name exactly** to confirm. If what they type (ignoring
surrounding whitespace) is not exactly `displayName`, reply "That does not match the agent name, so
nothing was deleted." and stop. An empty answer is a decline; stop.

### 6. Run the delete

Immediately before deletion, re-run step 3's local projection and resolve step 4's cloud target
again, including whether the workspace and binding now exist. Re-read any present binding without
modifying it. Require the selected record, canonical workspace, schema, deployment selection, and actual
cloud target to match the confirmed values. A binding appearing, disappearing, or changing cloud
ID or environment is a target change. Stop and repeat steps 4-5 with fresh typed confirmation
before deleting if any identity or the orphan list changed; never replace the approved target
with newly discovered values.

Run exactly one of these once, and wait for it to complete:

```bash
msagent agent delete --project '<projectDir>' --agent-id '<agentId>' --json --non-interactive
```

```bash
msagent agent delete --project '<projectDir>' --agent-id '<agentId>' --deployment-id '<deploymentId>' --json --non-interactive
```

With `--json`, stdout is a single JSON document for success or failure. Return to the entrypoint's
step 7 to report the result, loading deletion recovery only on failure.
