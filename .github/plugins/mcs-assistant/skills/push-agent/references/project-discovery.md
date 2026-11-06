# Registered project discovery

Read only when the supplied directory is not already a registered project root.
Resolve the root in this order; do not read, print, or modify CLI-managed config files:

1. If the user provides an agent workspace or `.mcs/conn.json`, walk only its parent directories
   until the nearest `.config/agent.config.json` is found.
2. If no path is provided, inspect the current directory and walk upward through up to five parent
   directories for the nearest `.config/agent.config.json`, matching MSAGENT's native discovery.
3. If no enclosing project root is found, search descendants of the current working tree for
   `.config/agent.config.json`.
4. If several descendant project roots match, present their paths and ask the user to choose one.

If no project root is found, stop and ask for the registered MSAGENT project. Never create or
register one implicitly.
