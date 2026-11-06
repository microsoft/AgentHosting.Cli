# Host integration

The commands, agents and skills in this plugin are the shared workflow source.
Use the adapter for the **current host**, not tool names copied from another host.
These mappings follow documentation; native execution is not yet verified.

| Host | Ask for a decision | Delegate a named agent | Continue work |
|---|---|---|---|
| Claude Code | `AskUserQuestion` | `Agent`, using the installed plugin agent | Resume the returned agent if supported; use `SendMessage` only for an actual teammate |
| GitHub Copilot CLI | `ask_user` | The available custom-agent/task tool, using its advertised agent identifier | `write_agent` only for a retained agent that supports messages |
| Agency Copilot | The exposed Copilot question tool | The exposed Agency/Copilot custom-agent tool | Use only the continuation mechanism actually exposed by the session |
| Gemini CLI | `ask_user`, when exposed | The generated agent's tool, e.g. `copilot-studio-describer` | Do not assume retained sessions or message APIs |

Inspect available tools before invoking them. Never invent tool names, agent IDs,
model IDs or continuation handles. Preserve the user's model preferences; do not
silently pick a hard-coded model to satisfy a workflow's qualitative model request.

If structured questions are unavailable, present the same choices in conversation
and wait for an explicit answer. Never interpret missing input as consent.
If a skill-invocation tool is available, invoke the named installed skill. Otherwise
read its complete local `skills/<name>/SKILL.md` and required references and execute
that workflow with the available tools. Missing files or tools stop the affected
operation; they do not justify bypassing its checks.

Prefer the host's named agent mechanism. If unavailable, read the corresponding
`agents/<file>.md` and run that role sequentially in the current session, preserving
its input requirements and scope boundaries. Do not impersonate a successful
delegation. Apply role instructions only to that phase, report its result, then
return to the main workflow; never merge a read-only role's permissions with an
authoring role's permissions. If a delegated agent cannot receive follow-up messages, start a new
read-only description with the original inputs, previous report and feedback.
If background execution is unavailable, run sequentially in the documented order.
The same checkpoint, exact-target, approval and side-effect rules always apply.

## Installed paths

Resolve the plugin root from the installed command/agent file, an explicitly
supplied plugin root, or the current host's plugin-root variable. Propagate that
exact root to delegated roles. Relative plugin references are relative to that
root, not the user's agent project or this repository's root.

Never read the old global `~/.copilot-studio-cli/plugin-paths.json`: another
host or cached plugin version can replace it. The helpers discover their own
installed root and use matching host data directories, or an installation-scoped
directory under `~/.copilot-studio-cli/mcs-assistant/instances/`. Conflicting
matching data directories are an error, not permission to choose arbitrarily.
Standalone copied skill directories remain supported.

## Execution and prerequisites

Use the host's shell, read, write and search tools. For shell arguments use native
quoting: PowerShell doubles embedded single quotes inside single-quoted arguments;
POSIX shells use the `'\''` sequence. Never interpolate untrusted selectors or
queries as shell code. All examples are templates, not permission for side effects.

MSAGENT is an external, publisher-approved prerequisite. Do not download, install,
upgrade it, enable feature flags, sign in, switch accounts, deploy or publish
implicitly. Report missing capabilities and stop the affected workflow.
The local optional Graph verifier has its own app registration and encrypted
cache; it does not use MSAGENT's authentication.

If a host leaves `$ARGUMENTS` unexpanded in a skill, use the user's original
request from the conversation. Never execute a literal placeholder or infer
missing selectors from it.
