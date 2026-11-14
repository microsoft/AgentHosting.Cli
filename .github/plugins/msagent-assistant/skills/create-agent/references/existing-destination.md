# Existing destination rules

Read only when the resolved project root already exists and is not empty. Never remove, move, merge,
or overwrite existing content.

- If the project root path is an existing file, stop and ask for another location.
- If it contains `.config/agent.config.json`, it is an existing registered multi-agent project.
  Run `msagent agent show --project '<project>' --json --non-interactive` and continue only when no
  record or child workspace conflicts with the new display name.
- If it has no `.config/agent.config.json` but contains `settings.mcs.yml` at the root, it is an
  unregistered authored workspace, not a registration container. Use its parent as the project root
  only when that produces the requested expected workspace; otherwise stop.
- Otherwise it may contain unrelated sibling folders. State that MSAGENT will create
  `.config/agent.config.json` at the root; do not inspect, move, or modify the siblings.
- If the root was inferred by replacing an agent-name-derived target with its parent, show the
  inferred root and expected workspace and require confirmation before creating
  `.config/agent.config.json` there.
- If the expected authored workspace exists and contains anything, stop and ask for another name or
  project root.
