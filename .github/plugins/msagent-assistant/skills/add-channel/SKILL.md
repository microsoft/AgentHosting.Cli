---
name: add-channel
description: Use when a user asks to add, enable, or configure the Microsoft Teams or Microsoft 365 Copilot channel on a CLI-authored Copilot Studio MCS agent using the GitHub Copilot harness.
metadata:
  argument-hint: Teams, Copilot, or both, plus an optional agent workspace path
allowed-tools: Read Write Edit Glob Grep
---

# Add an MCS Agent Channel

Add requested channels in local `settings.mcs.yml` only. This is not
`msagent deployment channel`, which manages CodeAgent channels, not MCS channels.
Only Teams and Microsoft 365 Copilot are supported for this harness.

Initial request: $ARGUMENTS

## 1. Resolve the workspace and request

Use the supplied authored workspace, or discover `**/settings.mcs.yml` in the current project.
If multiple workspaces match, ask which to use. If no workspace matches, direct the user to
`clone-agent` or `create-agent` and stop; never clone or scaffold implicitly. The authored child
workspace, not the MSAGENT registration root, holds the settings.

Read the selected settings. Verify it is a CLI-authored MCS agent using the GitHub harness:
a versioned `cliagent-` template plus `configuration.authoringModel: CliCopilot` or
`configuration.recognizer.kind: CLICopilotRecognizer`. If the harness is unknown, ask for
clarification before editing; if another harness is identified, stop rather than apply this schema.

Resolve Teams, Copilot, or both from the request. Ask if the channel is missing or ambiguous.
Reject unsupported requested channels before any edit. Do not partially implement a mixed
supported/unsupported request; explain the limitation and ask for a supported selection.
Never infer that every available channel should be added.

## 2. Load the contract and edit

Read [the channel schema and merge rules](references/channel-schema.md) completely before
writing. Resolve the reference from this skill's own directory. If it cannot be read, stop
and report the missing path.

Follow its validation and additive merge rules. Preserve existing settings and channel metadata;
adding an already-configured channel is a no-op, not permission to normalize or replace it.
Do not create a channel component, manifest, topic, or trigger file.

## 3. Verify and report

Re-read and parse the resulting YAML with an available local YAML parser, and review the diff.
Verify that each requested channel appears exactly once with the canonical fields, all unrelated
content is preserved, and only the selected `settings.mcs.yml` changed. Report validation failures
explicitly; do not claim success if validation cannot be completed.

Report the settings path, channels added versus already present, and that changes remain local.
Do not claim the agent is published, installed, accessible in Teams/Copilot, or runtime-tested
from local channel entries alone.

Never pull, deploy, publish, retarget connections, or run cloud channel commands as part of this
skill. Leave `.config/`, `.mcs/`, and `agent.sync.yaml` untouched. If upload or publication is
requested, hand off separately to `deploy-agent` or `publish-agent` with exact deployment selection
and that workflow's consent requirements; do not treat adding a channel as publication consent.
