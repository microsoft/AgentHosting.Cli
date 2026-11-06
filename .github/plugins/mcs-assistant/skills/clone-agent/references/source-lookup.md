# Source-agent lookup

Read only to resolve a display name or when source-not-found recovery requires listing the selected
environment. Never substitute a different agent without the user's selection.

```bash
msagent agent list --environment-id '<environment-id>' --limit 1000 --json --non-interactive
```

Match `agents[].displayName` case-insensitively. If one row matches, present its `mcsAgentId` and
`schemaName` and use `mcsAgentId` after the user confirms the match. If several rows match, ask the
user to choose the exact `mcsAgentId`. If none match and `truncated` is true, do not claim the agent
is absent; ask for its ID or schema name. An agent that has never been deployed may not appear in
this list, so accept an explicit ID or schema name even when listing cannot find it.
