# Working with workflows

> **Note:** The CLI supports attached agent workflows and standalone workflows.
> Use `--agent-type MCSWorkflow` for a standalone flow. Attached versus standalone
> describes ownership and lifecycle, not whether designer metadata is present.

A quick-reference guide for how a Copilot Studio **workflow** component should be
authored on disk when working with `msagent`. Start with the installed CLI's
workflow scaffold and preserve its metadata. For a checked-in designer-compatible
definition, see the [workflow fixture](../src/Microsoft.Agents.Hosting.Cli.E2E/fixtures/MCSWorkflow/workflows/WFHook2-36af9d18-cdab-eb87-8bdb-e11ba6806e75/workflow.json)
and its [metadata](../src/Microsoft.Agents.Hosting.Cli.E2E/fixtures/MCSWorkflow/workflows/WFHook2-36af9d18-cdab-eb87-8bdb-e11ba6806e75/metadata.yml).
These fixtures are implementation examples, not authorization to deploy or run
the E2E harness.

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
  and a sibling `associatedData.nodeActionMapping` linking node ids back to the `actions` block by name.
  Each action in `actions` also carries a `metadata.nodeId` pointing back to its
  graph node, and the flow's top-level `metadata.telemetryMetadata.creationSource`
  is set to `WorkflowDesigner`. This is the shape to author — it's what keeps the
  workflow fully editable and correctly laid out if/when a human opens it in the
  visual designer. The linked fixture demonstrates this structure.
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
- `associatedData.nodeActionMapping` — a sibling of `associatedData.graph`, mapping
  each non-trigger node id to the action name(s) in `actions` that it represents.
- `graph.connectionReferences` — mirrors the top-level `properties.connectionReferences`
  entry for any connector used.
- Each entry in `actions` — add `metadata: { description, nodeId }`, where `nodeId`
  matches the node id from the graph.
- Top-level `definition.metadata.telemetryMetadata` — set
  `{ "creationSource": "WorkflowDesigner", "modifiedSources": "WorkflowDesigner" }`.

Compare edits against the linked fixture and your generated scaffold. Do not copy
another workflow's identity or assume that `modernFlowType` selects the designer
generation. Keep graph nodes, executable actions, schemas, and workflow-tool
input/output names consistent.

## Connection references and deployment

The workflow must name the intended connection reference in both its definition
and metadata. A reference does **not** have to be new: an existing reference for
the same connector can be reused in the target environment.

1. Back up or commit the authored workflow and tool files before connection or
   deployment operations.
2. Inspect `msagent deployment connection list --project <project> --agent-id <id>
   --deployment-name <deployment> --json`. Do not assume an available connection
   has already been bound to every reference.
3. If a reference needs binding, use the exact approved reference and connection:

   ```text
   msagent deployment connection update --project <project> --agent-id <id> --deployment-name <deployment> --connection-reference <logical-name> --connection-id <connection-id> --json --non-interactive
   ```

   Binding can run before the workflow's first successful deploy. If the command
   reports saved bindings but failed workflow activation, do not treat that as a
   ready workflow; inspect the diagnostic and retry deployment after resolving it.
4. Run `msagent agent deploy --project <project> --agent-id <id> --deployment-name
   <deployment>` to save the agent draft, then inspect connection and workflow
   readiness again. Verify authored files remain present and independently read
   back the deployed workflow.

On CLI `0.2.52-beta`, a fresh reference named only in the workflow did not
automatically become bound during the first push, even with an owned connection
available. The push returned `deploy-incomplete` with a missing-workflow
dependency error. Binding that exact reference and retrying the draft deploy
succeeded. Do not treat “deploy first, bind only after success” as a universal
requirement, or a duplicate-key error from older testing as inevitable.

Only use `deployment connection add` when a new reference is actually needed.
Stop rather than guessing between ambiguous connection candidates. These draft
deployment checks do not authorize connector execution or agent publication.

## Standalone workflows (`--agent-type MCSWorkflow`)

A standalone workflow is a cloud flow registered on its own, not as a component of an agent.

```text
msagent agent create --agent-type MCSWorkflow --agent-name <name> --project <project>
msagent deployment create --project <project> --deployment-name develop --deployment-type dev
msagent agent deploy --project <project> --deployment-name develop
```

Clone an existing flow into a separate empty project instead when needed:

```text
msagent agent create --agent-type MCSWorkflow --mcs-clone-workflow-id <id> --project <project> [--mcs-clone-environment-id <source>]
```

Cloning already creates a `develop` deployment record. Inspect
`msagent deployment list --project <project>` before creating another.
Other standalone operations include:

```text
msagent agent deploy --deployment-name <name> [--publish] [--overwrite]
msagent agent pull --deployment-name <name> [--overwrite]
msagent agent list --agent-type MCSWorkflow [--filter <text>] [--tenant-wide]
```

`--publish` requests activation, but source state can also request activation.
Unbound connections can prevent readiness. Standalone deploy replaces the target
workflow by ID; `--overwrite` does not add a separate drift guard on that route.
Review the target, connections, and activation effects before deploying.
