# Deletion failure recovery

Read only after `msagent agent delete` fails. Always surface `errorMessage` and `remediation`.
If `partialResult` is present, summarize it. Never retry without user consent; re-confirm the
exact typed display name if any input changed. Retain the entrypoint's command-validation rules.

| Condition | Meaning | What to do |
|---|---|---|
| `exitCode` is `3` | Not signed in, or sign-in is required and `--non-interactive` prevents it | Explain, then offer to run `msagent auth login` (opens an interactive browser sign-in). On consent, run it and wait for the user to finish, then re-run the **same** delete command once. Re-confirm (step 5) if any input changed. |
| `records-survived-cloud-delete` | The cloud agent was deleted, but its records could not be removed | Offer to re-run the same delete; the CLI documents that re-running removes the records. |
| `transport-timeout` | The request timed out and may have completed | Do not retry blindly. Re-read records with the registered workflow's step 3 to see whether the record still exists, tell the user, and offer to re-run only with their consent. Load that workflow from the entrypoint first if the unregistered route has not loaded it. |
| `config-locked` | Another msagent command is writing the configuration | Ask the user to wait for it to finish, then offer to retry. |
| `environment-not-found`, `tenant-mismatch` | The target environment is not available to the signed-in msagent account | Relay the remediation. If the agent's tenant (the record's `tenantId` or the unregistered workspace's `AccountInfo.TenantId`) differs from the signed-in `tenantId` reported by `msagent auth status --json`, offer `msagent auth login --tenant '<tenantId>'` using the entrypoint's GUID/quoting validation, then re-run the same delete once on consent. |
| `project-not-found`, `config-not-found`, `agent-not-found`, `agent-ambiguous`, `agent-id-conflict`, `deployment-not-found`, `not-an-mcs-agent` | Project/selector does not match records | Return to entrypoint step 2 or registered step 4 with the user; load the registered workflow before its commands if needed. Re-select and re-confirm any changed target. |
| `workspace-schema-unknown`, `workspace-identity-mismatch`, `untrusted-dataverse-origin` | CLI could not prove the workspace binding identifies this agent/environment | Relay the remediation. Do not edit `.mcs\`, `.config\`, or project files to work around it. |
| Anything else | Unknown failure | Relay `errorMessage`, `errorKind`, and `remediation` as-is and stop. |
