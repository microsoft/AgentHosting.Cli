# Clone failure recovery

Read only after a command or result verification fails. Use the structured error and any
`partialResult` to report completed local writes.

| Failure | Action |
|---|---|
| Exit code 3 / not signed in | With consent, run `msagent auth login`, wait for completion, then retry the same clone once. |
| `environment-not-found` | Run `msagent env list --json --non-interactive` and ask the user to choose an accessible environment. |
| `tenant-mismatch` | Relay the remediation; with consent, sign in to the environment's tenant or choose another environment. |
| `clone-source-not-found` | Read [source lookup](source-lookup.md), list the selected environment, and ask for the exact `mcsAgentId` or `schemaName`. |
| An agent or collection destination already has content | Ask for another project root. `--name` can resolve only an agent-folder/name collision; it cannot rename linked collections. Never delete, move, or overwrite existing content. |
| The local agent name is already registered | Ask for another project root or an explicit local `--name`. |
| `clone-not-materialised` | Surface the cause. Nothing was registered or sent to Copilot Studio; retry only after the cause is resolved and the user consents. |
| `clone-not-registered` | Surface the cause and remediation. The record was rolled back; report any local workspace paths that remain. |
| Configuration is locked | Wait for the other MSAGENT command to finish, then retry only with the user's approval. |

Never fall back to `msagent agent pull`: pull updates an existing registered workspace and cannot
perform the initial clone.
