# Registered project discovery

Read only when the supplied directory is not already a registered project root.
Resolve the root in this order; do not read, print, or modify CLI-managed config files:

1. If the user provides an agent workspace or `.mcs/conn.json`, walk only its parent directories
   until the nearest `.config/agent.config.json` is found. If none is found, stop and ask for the
   correct registered project; do not search the current working tree instead.
2. If any other explicit project directory is not a registered root, stop and ask for the correct
   registered project. Never substitute a different project for an unresolved explicit path.
3. If no path is provided, inspect the current directory and walk upward through up to five parent
   directories for the nearest `.config/agent.config.json`, matching MSAGENT's native discovery.
4. Only for a pathless request, if no enclosing project root is found, search descendants of the
   current working tree for `.config/agent.config.json`.
5. If several descendant project roots match, present their paths and ask the user to choose one.

If no project root is found, stop and ask for the registered MSAGENT project. Never create or
register one implicitly.
