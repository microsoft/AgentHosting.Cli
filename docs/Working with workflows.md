# Working with workflows

> **Note:** The CLI supports both classic and 'new' (coral) based workflows for the
> GitHub Harness when linked to an agent. Standalone workflow support releasing in
> approx 1 week.

A quick-reference guide for how a Copilot Studio **workflow** component should be
authored on disk when working with `msagent`, based on
[Workflow Authoring Template (CLI-Native vs Designer-Authored).md](./Workflow%20Authoring%20Template%20(CLI-Native%20vs%20Designer-Authored).md).

## The two shapes

Both are the same kind of thing under the hood — a modern cloud flow
(`modernFlowType: 1` in `metadata.yml`) — and both deploy, activate, and run via
`msagent`. The difference is what's inside `triggers.manual.metadata` (and the
accompanying `metadata.nodeId` on each action) in `workflow.json`:

- **Designer-authored (the one to produce)** — the full shape, as if the workflow
  had been built in the classic visual workflow designer in the maker portal.
  `triggers.manual.metadata.associatedData.graph` contains a `nodes` array (every
  action's id, type, canvas x/y `position`, and a `data.config` that mirrors the
  action's connector/operation schema), an `edges` array wiring the nodes together,
  and a `nodeActionMapping` linking node ids back to the `actions` block by name.
  Each action in `actions` also carries a `metadata.nodeId` pointing back to its
  graph node, and the flow's top-level `metadata.telemetryMetadata.creationSource`
  is set to `WorkflowDesigner`. This is the shape to author — it's what keeps the
  workflow fully editable and correctly laid out if/when a human opens it in the
  visual designer, and it's the shape already used by real example agents in this
  repo (e.g. `demo-agent5`'s `RespondtotheagentandSendanemail` workflow).
- **CLI-native (lean, no graph)** — `triggers.manual.metadata` is just
  `{"flowSystemMetadata": {"flowKind": "Stateful"}}`, with no graph, no node
  duplication, and no `nodeActionMapping`. Smaller file, but it skips the
  information the visual designer and tooling expect to see.

## What to produce

When authoring or regenerating a workflow by hand (or via a coding agent), **always
build the full designer-authored shape**, including:

- `triggers.manual.metadata.associatedData.graph.nodes` — one node per action/trigger
  step, each with a stable `id`, `type` (`start`, `connector`, `builtinFunction`,
  etc.), `version`, canvas `position` (`x`/`y`), and a `data.config` that matches the
  corresponding action's inputs/operation.
- `graph.edges` — connects node ids in execution order, using `sourceHandle`/
  `targetHandle` of `output`/`input` where a node has multiple connections.
- `graph.nodeActionMapping` — maps each non-trigger node id to the action name(s) in
  `actions` that it represents.
- `graph.connectionReferences` — mirrors the top-level `properties.connectionReferences`
  entry for any connector used.
- Each entry in `actions` — add `metadata: { description, nodeId }`, where `nodeId`
  matches the node id from the graph.
- Top-level `definition.metadata.telemetryMetadata` — set
  `{ "creationSource": "WorkflowDesigner", "modifiedSources": "WorkflowDesigner" }`.

See `Workflow definition examples - important.md` in this folder for the full
copy-paste template and a worked example, intended specifically for GitHub Copilot
to follow when generating these files.

## Deploy order (important — avoids a real failure)

Confirmed by direct testing: getting this order wrong causes `msagent agent deploy`
to fail with a Dataverse duplicate-key error (`ExportKeyInvalidCreate`).

1. Write the workflow + tool files, referencing a **new, not-yet-existing** connection
   reference logical name.
2. Run `msagent agent deploy` — this creates the connection reference automatically
   and activates the workflow. If a usable connection for that connector already
   exists in the environment, it auto-binds immediately.
3. Only if `msagent deployment connection list --json` shows `connectionsReady: false`
   do you run `msagent deployment connection add`/`update` — and only **after** the
   first successful deploy, never before.

Running `deployment connection add`/`update` for a connector *before* the workflow
referencing it has been deployed can also silently delete local `workflows/*` source
folders that the CLI considers already cloud-synced. Keep backups of anything
authored but not yet deployed.

## Standalone workflows (`--agent-type MCSWorkflow`)

A standalone workflow is a cloud flow registered on its own, not as a component of an agent.

```text
msagent agent create --agent-type MCSWorkflow --agent-name <name>
msagent agent create --agent-type MCSWorkflow --mcs-clone-workflow-id <id> [--mcs-clone-environment-id <source>]
msagent agent deploy --deployment-name <name> [--publish] [--overwrite]
msagent agent pull --deployment-name <name> [--overwrite]
msagent agent list --agent-type MCSWorkflow [--filter <text>] [--tenant-wide]
```
