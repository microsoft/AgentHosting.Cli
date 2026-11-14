# Push failure recovery

Read only after a command or result verification fails. Surface the structured error and any
`partialResult`, including cloud changes already made.

| Failure | Action |
|---|---|
| Exit code 3 / not signed in | With consent, run `msagent auth login`, wait for completion, then restart the pull-then-deploy sequence once. |
| Config, agent, or deployment not found/ambiguous | Re-run `agent show`, present exact local records, and ask the user to choose. Never guess. |
| Pull merge conflict | Surface every conflict and stop. Never deploy until the local merge is resolved. |
| `workspace-disconnected` on the pre-push pull | Re-read `agent show` and confirm step 6's never-connected predicate before skipping the pull and using step 7's deploy to create and connect the cloud agent. Otherwise stop and report the connection mismatch; do not repair metadata manually. Ask before restarting the sequence after this failed pull. |
| `push-conflicts-require-pull` after a successful pull | Explain that the target changed again and ask before restarting the full sequence. Do not add `--overwrite` automatically. |
| Unverified existing target or explicit overwrite request | Explain what remote content could be replaced. Add `--overwrite` only after the user explicitly accepts that risk for the exact deployment. |
| `deploy-incomplete` | Surface what stage failed and the remediation. Do not claim the deployment was recorded or published. |
| `deploy-recorded-nowhere` | Report that cloud content was updated but local deployment state was not; follow the CLI remediation exactly. |
| Configuration is locked | Wait for the other MSAGENT command to finish, then retry only with the user's approval. |

Never add `--publish`. After recovery, preserve step 6's first-push predicate and connected-agent
pull-before-deploy sequence.
