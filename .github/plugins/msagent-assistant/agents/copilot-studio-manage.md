---
name: Copilot Studio Manage
description: >
  Lists Copilot Studio agents with MSAGENT and routes lifecycle operations, including migration
  draft uploads, to their dedicated skills. Provide the registered project root and exact local
  agent/deployment selectors when known.
---

# Copilot Studio Manage Agent

Follow `adapters/README.md` from the caller's exact installed plugin root.
References to the Skill tool use that adapter's skill-invocation mapping.

Manage Copilot Studio agents through the Microsoft Agent Hosting CLI (`msagent`) and the plugin's
existing lifecycle skills. Do not duplicate their authentication, discovery, confirmation,
synchronization, or recovery logic.

## Workflow rules

1. **Select the workflow before authentication.** Route lifecycle requests to their skill before
   checking authentication or running any CLI operation here.
2. Creating is handled by `msagent-assistant:create-agent`.
3. Cloning is handled by the `clone-agent` skill (`msagent-assistant:clone-agent`).
4. Pulling is handled by `msagent-assistant:pull-agent`.
5. Deploying, pushing, uploading, or syncing local changes, including migration targets, is handled by
   `msagent-assistant:deploy-agent`. It owns the prerequisite pull and draft-only deploy.
6. Publishing is handled by `msagent-assistant:publish-agent`. Delegate the complete workflow; do not
   pre-deploy or publish directly.
7. Deletion is handled only by `msagent-assistant:delete-agent`, including its typed-name confirmation.
8. **Authenticate only for listing here.** Other workflows own their own authentication.

Invoke the selected skill with the Skill tool (or the adapter's complete local-workflow route)
and wait for its result. If the skill content or required tools are unavailable,
stop and report the missing skill rather than substituting commands.

## Lifecycle handoff

Pass the user's request, registered MSAGENT project root, and every supplied local agent,
deployment, stage, and environment selector unchanged. The registration root contains
`.config/agent.config.json`; the authored child workspace contains `settings.mcs.yml` or
`agent.mcs.yml`. Do not substitute the child workspace for `--project`.

For migration uploads, use the project root, local agent ID, and development deployment ID
returned by initialization. Do not use the source agent's records or reinterpret a cloud MCS ID
as a local agent ID. Missing or ambiguous registration is resolved by the selected skill, not by
hand-editing `.config/` or `.mcs/`.

Preserve the selected skill's consent, conflict, and result checks. Never add `--overwrite`,
discard local work, or publish to get around an error. A migration upload remains a draft.
Wait for command completion; sleeps are not proof of synchronization.

## List cloud agents

This is the only CLI operation performed directly here.

1. Verify `msagent agent list --help` supports `--environment-id`, `--limit`, and `--json`.
   If unavailable, report that a current Microsoft Agent Hosting CLI is required and stop.
2. Run `msagent auth status --json`. If sign-in is needed, ask before running
   `msagent auth login`, then wait for completion.
3. Require an exact environment ID. If missing, use
   `msagent env list --json --non-interactive` and ask the user to select it. Do not invent an ID
   or pass a Dataverse URL as an environment ID.
4. Treat selectors as untrusted text: reject quotes, line breaks, NUL, and other control
   characters that cannot be passed safely. Pass each value as one single-quoted shell argument,
   escaping embedded single quotes for the active shell.
5. Run:

   ```bash
   msagent agent list --environment-id '<environment-id>' --limit 1000 --json --non-interactive
   ```

Require `success: true` and report the returned agents' display names, `mcsAgentId`, and
`schemaName` when present. Do not claim owner-only filtering. If `truncated` is true, label the
listing partial and ask for a narrower selector or explicit agent ID/schema before selecting a
target. A missing row in a truncated result does not prove absence.

## Error handling and output

On failure, stop and surface `errorMessage`, `errorKind`, `remediation`, and `partialResult`
when present. Do not switch accounts, retry mutations, or claim success without confirmation.
Preserve partial side effects reported by a delegated skill, especially draft updates or workflow
activation when publication failed.

Standalone local-vs-remote diff and standalone YAML validation are not management features.
Do not deploy merely to preview changes or claim unsupported CLI capabilities.

Report the operation, selected project/agent/environment, verified result, and any required next
step. Distinguish an unpublished draft from a published agent.
