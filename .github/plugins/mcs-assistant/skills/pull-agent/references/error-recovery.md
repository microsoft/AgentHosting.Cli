# Pull failure recovery

Read only after a command or result verification fails. Surface the structured error and any
`partialResult`, including local writes already made.

| Failure | Action |
|---|---|
| Exit code 3 / not signed in | With consent, run `msagent auth login`, wait for completion, then repeat the identity checks and any failed catalog lookup. Retry pull once only if pull was the failed command and the same approved source is verified; a changed source requires fresh confirmation. |
| Config not found or no registered agent | Ask for the MSAGENT project root. Do not hand-edit `.config` or fall back to another project. |
| Agent selector is missing or ambiguous | Run `agent show`, present the local records, and ask for the exact local agent. |
| Workspace disconnected | Stop and explain that pull requires a registered, connected workspace. Use clone for an initial download. |
| Origin or tenant mismatch | Surface the remediation and require the correct signed-in tenant; never retarget sync metadata manually. |
| Source constraint mismatch, changed binding, or unverifiable identity | Show the expected and observed identity fields, retain any snapshot, and stop. If pull already ran, report possible wrong-target local writes. Do not rewrite sync metadata, substitute another target, or retry automatically. Resume only after the user corrects conflicting constraints and steps 3-5 establish a freshly confirmed source. |
| Local/remote merge conflict | Surface every reported conflict and stop so the user can resolve local files. Never discard either side. |
| Configuration is locked | Wait for the other MSAGENT command to finish, then retry only with the user's approval. |
| Service or network failure | Surface the full error. Retry only after the reported cause is resolved and the user asks. |

Never fall back to `msagent agent create`: create/clone registers a new local project, while pull
updates the selected existing registration.
