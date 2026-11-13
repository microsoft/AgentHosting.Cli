# Explicit publication handoff

Read only after explicit publication intent and fully verified creation, validation, draft
deployment, and final synchronization. Never use this handoff after a failed or partial phase.

First report that creation, draft deployment, and synchronization succeeded and identify the exact
project root, local agent ID, development deployment ID, target environment, cloud MCS agent ID,
verified `schemaName` from the authoritative synchronized settings, deploy result, and final-pull
result. Then invoke `msagent-assistant:publish-agent` with those exact values. Pass the environment
and `schemaName` explicitly alongside `mcsAgentId`; never substitute the local agent ID or infer
the target from the development deployment label.

If the publish skill is unavailable, report the verified draft and that publication was not
performed, then stop. Do not improvise an inline publishing command.

Do not pass `--publish` from creation. The publish skill must re-resolve the same registered project
and deployment, pull before deploying, and obtain its own final confirmation immediately before
making the agent live. The initial create-and-publish wording is intent, not that final confirmation.
