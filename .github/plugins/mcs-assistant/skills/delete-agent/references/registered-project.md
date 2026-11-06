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
  `environmentId` (the agent's home environment and its default deletion target), and, when known,
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
   - **The initial request named an agent.** Match the name case-insensitively against each agent's
     `displayName` and `agentId`. Never substitute an agent the user did not name.
     - **One match** - use it, and confirm the match with the user.
     - **Several matches** - list the matching agents (`displayName`, `agentId`) and ask the user for
       the `agentId` of the one to delete. Continue only if the answer matches exactly one of them;
       otherwise stop.
     - **No match** - tell the user that no agent named "<name>" is registered in `configPath`, list
       the registered agents (`displayName`, `agentId`), and stop. Nothing is deleted.
   - **No agent named.** If there is one agent, use it. If there are several, present a numbered
     pick-list (`displayName`, `agentId`, `agentType`).
   - If the chosen agent's `agentType` is not `MCSAgent`, stop: this skill deletes only MCS agents.
2. **Cloud target.** Consider only the deployments whose `agentId` equals the chosen agent's.
   - **No deployments** - pass no deployment selector. The CLI deletes the cloud agent the
     workspace's `.mcs` binding names or, when the workspace has none, the agent that has the
     agent's schema name in its home environment.
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

Show a summary like this:

```text
About to delete:
  agent         <displayName> (<agentId>)
  project       <projectDir>
  cloud target  deployment "<deploymentName>" in <environmentId>
                (or, with no deployment: the workspace-bound agent / home environment <environmentId>)
  records       1 ManagedAgent + <N> AgentDeployment row(s)
  orphaned      <deploymentName in environmentId, ...> | none

This deletes remote resources and cannot be undone. Local files are not deleted; msagent only
removes this agent's records from <configPath>.
```

Ask the user to **type the agent's display name exactly** to confirm. If what they type (ignoring
surrounding whitespace) is not exactly `displayName`, reply "That does not match the agent name, so
nothing was deleted." and stop. An empty answer is a decline; stop.

### 6. Run the delete

Run exactly one of these once, and wait for it to complete:

```bash
msagent agent delete --project '<projectDir>' --agent-id '<agentId>' --json --non-interactive
```

```bash
msagent agent delete --project '<projectDir>' --agent-id '<agentId>' --deployment-id '<deploymentId>' --json --non-interactive
```

With `--json`, stdout is a single JSON document for success or failure. Return to the entrypoint's
step 7 to report the result, loading deletion recovery only on failure.
