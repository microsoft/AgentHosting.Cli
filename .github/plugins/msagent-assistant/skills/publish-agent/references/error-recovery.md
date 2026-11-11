# Publication failure recovery

Read only after a command or result verification fails.
With `--json`, use `errorMessage`, `errorKind`, `remediation`, and `partialResult` when present.
Treat `partialResult` as authoritative for side effects completed before the failure: summarize its
authoritative partial side effects and do not infer that an omitted top-level success result means
that nothing changed.

| Failure | Action |
|---|---|
| Exit code 3 / not signed in | With consent, run `msagent auth login`, wait for completion, then repeat target resolution and confirmation before retrying once. |
| Config, agent, or deployment not found/ambiguous | Re-run `agent show`, present exact local records, and ask the user to choose. Never guess. |
| Pull merge conflict | Surface every conflict and stop before deploy or publish. |
| `push-conflicts-require-pull` after a successful pull | Explain that the target changed again and ask before restarting the full sequence. Never add `--overwrite` automatically. |
| `publish-withheld-incomplete-push` | Use `partialResult` when present as the authoritative record of pushed content, activated workflows, and related component changes. Report that the agent itself was not published. Eligible workflows or related components may already have changed; surface every workflow/component warning returned by the CLI. Follow the remediation, then ask before rerunning with `--publish`. |
| `publish-failed-after-push` | Use `partialResult` when present as the authoritative record of completed push side effects. Report that publish failed and do not claim the agent is live; follow the CLI remediation or use the portal. |
| `deploy-incomplete` | Surface the failed stage and remediation. Do not claim the deployment was recorded or published. |
| Explicit overwrite request | Explain what remote content could be replaced. Add `--overwrite` only after the user accepts that risk for the exact deployment, then reconfirm publish. |
| Configuration is locked | Wait for the other MSAGENT command to finish, then retry only with the user's approval. |

Never bypass the documented MSAGENT publish workflow: this skill preserves the selected deployment's
push-before-publish semantics and local records.
