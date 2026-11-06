# Optional MCS2 component authoring

Read only when accepted requirements call for additional components or settings. Put final
agent components inside the resolved agent workspace, never at the registration root. The
`add-skill` workflow stages skill source manifests in private scratch space outside that workspace
before its helper imports the final components.

Classify each accepted requirement:

- global role, tone, safety, clarification, confirmation, and tool policy -> `settings.mcs.yml`;
- reusable multi-step procedures -> `behaviors/`;
- searchable factual sources -> `capabilities/knowledge/`;
- live data or external actions -> `capabilities/tools/` only with complete real connector,
  operation, input/output, authentication, and connection-reference metadata;
- supported agent-level configuration -> settings.

Before writing knowledge, read and follow `skills/add-knowledge/references/knowledge-schema.md`
plus only the selected source schema. Before creating or authoring any skill, read and follow
`skills/add-skill/SKILL.md`, including its author-from-requirements route, import helper and
acceptance checks. Do not bypass that entrypoint by using
`skills/add-skill/references/skill-schema.md` alone. Its selected source or variant references
define schemas, not an alternative workflow. The default for new skills is a real `SKILL.md`
plus helper-generated `skill.mcs.yml`, not a hand-written inline component.
Keep scratch source manifests outside the authored workspace; only the final skill components
belong inside it, using the helper for the default upload layout. If a required reference or
helper cannot be read, stop before authoring and report it. Every
authored component definition `*.mcs.yml` (other than `settings.mcs.yml`) must contain
`mcs.metadata` and `kind`. Metadata-only sidecars (skill payload files and uploaded knowledge
files) contain only `mcs.metadata`, as those schemas define.

Use the publisher prefix from the scaffolded `schemaName` for new flat component filenames. Keep
`<agent-schemaName> + "." + <filename-without-.mcs.yml>` at most 100 characters. Do not invent
connections, claim unavailable live data works, or create speculative components.
Leave `.config/`, `.mcs/`, and `agent.sync.yaml` untouched.

## Teams and Microsoft 365 Copilot channels

Before adding or changing channels for a GitHub-harness MCS agent, read and follow
`skills/add-channel/SKILL.md` from this plugin's root, including its skill-local channel schema.
If either required file cannot be read, stop before the channel edit and report the missing path.
Only add channels explicitly requested in the accepted requirements; do not enable both by default.
This is agent-level settings authoring, not a behavioral skill or event trigger. The workflow
preserves existing channels and remains local; deployment and publication are separate.

## Persistent user memory

This is the shared memory-setting contract for CLI-authored agentic-loop agents. In the authored
workspace's `settings.mcs.yml`, memory is an agent-level Boolean, not an instruction or skill:

```yaml
configuration:
  agentSettings:
    enableMemory: true
```

The field and nesting were confirmed by enabling Memory in Copilot Studio and syncing the agent
down with MSAGENT. Merge this field into the existing `agentSettings` mapping; do not replace its
model, instructions, or other settings.

- Enable memory only when explicitly requested. Use Boolean `false` to disable it on an explicit
  disable request; do not use quoted strings or invent another memory field.
- Otherwise preserve the existing `true`, `false`, or absent setting. An absent field means
  unspecified in local files, not confirmed disabled; do not infer a runtime default.
- Memory stores private context separately for each user and agent in Microsoft-managed storage.
  Memories expire after 28 days of inactivity. User memory is disabled in group chats and
  Microsoft Teams channels. Turning memory off does not delete stored memories.
- Editing this field is local configuration only; it does not deploy or publish the change.
  Use the separately authorized `push-agent` or `publish-agent` workflow for those operations.
  Do not claim runtime persistence was verified just because the field is present.

See [Memory for the GitHub Copilot harness](https://learn.microsoft.com/en-us/microsoft-copilot-studio/agents-experience/memory-overview)
for platform behavior and user controls. Do not apply a different harness's memory configuration
or retention rules to these agents.
