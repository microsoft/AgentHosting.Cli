# MCS GitHub-harness channel contract

These are the only two supported channels for CLI-authored MCS agents using the GitHub Copilot
harness. Do not generalize this contract to classic-harness MCS agents or CodeAgents.

| Requested channel / input alias | Exact `id` and `channelId` |
|---|---|
| Teams, Microsoft Teams, msteams, MsTeams | `MsTeams` |
| Copilot, Microsoft 365 Copilot, Microsoft365Copilot | `Microsoft365Copilot` |

Match input aliases case-insensitively, but the exported YAML identifiers are case-sensitive.
Here Copilot means the Microsoft 365 Copilot channel, not a GitHub Copilot integration.
`channelId` is the canonical channel kind, not an environment, deployment, or agent ID.

## YAML shape

Merge the requested entries into `configuration.channels` in the authored workspace's
`settings.mcs.yml`. Both channels together use:

```yaml
configuration:
  channels:
    - id: MsTeams
      channelId: MsTeams

    - id: Microsoft365Copilot
      channelId: Microsoft365Copilot
```

For a single-channel request add only its entry, not both. No new or additional file is needed.
These are settings entries, not event-trigger nodes, tools, or behavioral skills. Do not place
them under `agentSettings` or invent `mcs.metadata`, channel components, app manifests, or IDs.

## Validation and additive merge rules

1. Parse the settings before editing. The document must be a mapping with unambiguous keys.
   If `configuration` is absent, create a mapping; if it exists but is not a mapping, stop.
   If `channels` is absent, create a sequence. An explicit scalar, null, mapping, or malformed
   sequence in `configuration.channels` is invalid: stop and explain it; do not coerce it or
   replace it with an empty list.
2. Existing entries must be mappings with nonempty string `id` and `channelId` fields. If there
   are duplicate IDs or channel kinds, mismatched fields for a supported kind, or conflicting
   noncanonical casing, stop before any writes. Do not silently deduplicate, rename, or repair
   entries as part of an add request.
3. If a requested canonical entry is already configured exactly once, it is a no-op. Preserve
   its extra fields, comments, and position. For unknown existing channel kinds, preserve their
   entries and report them as outside this contract; their presence does not establish support.
4. Append only missing requested entries using the table's exact `id` and `channelId` values.
   Do not remove or reorder existing entries or replace the entire settings document with the
   example. Preserve instructions, model, memory, authentication, recognizer, identity, template,
   language, and every other unrelated setting and comment. Avoid whole-document serialization
   that reformats unrelated content.
5. Parse again after editing. Check the requested entries occur exactly once and use canonical
   fields. Review the diff: only the requested additions and necessary parent mapping/sequence
   should change. No other workspace files or CLI-managed state should change.

Unsupported requests must be resolved before writing anything, even when some requested channels
are supported or already present. Removing channels is outside this additive workflow.

## Configuration is not channel availability

Local YAML establishes authored configuration, not successful deployment, publication, Teams app
installation, Microsoft 365 Copilot availability, tenant approval, or user access. Those require
separate evidence. Keep deployment and publication in their separately authorized lifecycle
workflows; the CodeAgent-only `msagent deployment channel` commands are not an MCS shortcut.
