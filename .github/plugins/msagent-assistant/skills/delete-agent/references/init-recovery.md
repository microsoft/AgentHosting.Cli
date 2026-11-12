# Initialization failure recovery

Read only after `msagent agent init` fails in the unregistered route. Relay `errorMessage`,
`errorKind`, `remediation`, and any `partialResult`. Never retry without user consent. If any input
changes, repeat U2's exact typed-name confirmation before running init.

| Condition | What to do |
|---|---|
| `exitCode` is `3` | Offer `msagent auth login`. On consent, wait for sign-in, then re-run the same init once. |
| `environment-not-found`, `tenant-mismatch` | Compare `AccountInfo.TenantId` already read from the workspace with `tenantId` from `msagent auth status --json`. If different, offer `msagent auth login --tenant '<tenantId>'`; use the entrypoint's GUID/quoting rules. Re-run the same init once only on consent. |
| `already-registered` | Follow **Already registered** below; U2 confirmation does not cover the existing registration. |
| `workspace-schema-unreadable` | Stop: settings have no schema name the CLI can read. |
| `project-not-found` | Return to entrypoint step 2 with the user. |
| `transport-timeout` | Init may have applied. Do not retry blindly: load the registered workflow from the entrypoint and inspect records with step 3. If a registration exists, follow **Already registered**; otherwise report the observed state and obtain consent before retrying init. |
| Anything else | Relay the error/remediation as-is and stop. |

## Already registered

The U2 confirmation covers only a new registration, so it does not carry over. Read the complete
registered-project reference linked in the entrypoint before its commands. Run its step 3 command
with `<workspaceDir>` as the project directory and handle its failures as step 3 specifies. Keep
only records whose `agentLocation` is `.` and whose `mcsAgentId` equals `AgentId` from the workspace
connection (case-insensitive).

- **Exactly one record**: use it as the chosen agent. Stop if `agentType` is not `MCSAgent`.
  Apply the cloud target and orphan checks (registered step 4, items 2 and 3), then ask
  again for the typed display name in step 5 before running step 6.
- **None or several**: explain that the existing registration in `configPath` does not identify the
  bound cloud agent; nothing was deleted. Stop.
