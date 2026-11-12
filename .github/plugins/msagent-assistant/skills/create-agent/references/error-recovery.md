# Creation failure recovery

Read only after a command, field validation, or result verification fails. Surface the structured
error and any `partialResult`; never treat a partial phase as complete.

| Failure | Action |
|---|---|
| Field validation fails | Stop before MSAGENT execution. Show the invalid field, its limit or accepted format, and a compliant proposed value when one can be derived safely. These limits are stricter than MSAGENT so the agent opens in Preview; never send an invalid value and wait for a later rejection. Never blindly truncate explicit user text; ask when shortening would be ambiguous. For instructions over 8,000 characters, propose a condensed version that keeps every material requirement and state the condensation before creation. |
| Unknown command or option | Report that a current Microsoft Agent Hosting CLI is required. Use `partialResult` and `msagent agent show` / `msagent deployment list` to report what already completed, then stop. After the user upgrades, resume from the first incomplete phase. |
| Exit code 3 / not signed in | With consent, run `msagent auth login`, wait for completion, then retry the same failed command once. |
| `permission-denied` | Relay the required permission from `remediation`; do not bypass the documented MSAGENT workflow or switch to another account. |
| `environment-not-found` or `tenant-mismatch` | List accessible environments or sign in to the correct tenant, then retry only after the target is explicit and the user consents. |
| Destination or project already exists | Ask for another project directory or resume only an exact matching incomplete workflow. Never delete content. |
| Agent or deployment selector mismatch | Re-read `agent show` and `deployment list`; continue only with exact local GUID matches. |
| Configuration is locked | Wait for the other MSAGENT operation to finish, then retry only with user approval. |
| Remote drift or ownership conflict | Stop. Never add `--overwrite` automatically. |
| Deploy or pull conflict | Surface every conflict and stop for user resolution. Never discard either side. |
| Service, permission, or network failure | Surface the full structured error. Retry only after the cause is resolved and the user asks. |

Preserve a successfully scaffolded project after any later failure. Resume from the first
incomplete phase; never create duplicate agent or deployment records.
