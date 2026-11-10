# MSAgent CLI deployment connection commands

[Back to deployment commands](Command_Deployment.md)

> **MCS-specific group.** Inspects and binds a deployment's connection references, and creates the
> connections they use. It replaces the former `deployment update connection` command. Invoking
> `msagent deployment connection` without a leaf command returns a usage error that lists the valid
> subcommands.

## Subcommands

| Command | Description |
| --- | --- |
| [`msagent deployment connection list`](#msagent-deployment-connection-list---mcs-specific) | Report the agent's connection references and the connection behind each. Changes nothing. |
| [`msagent deployment connection add`](#msagent-deployment-connection-add---mcs-specific) | Declare a connection reference for a connector and bind it, creating the connection when none can be. |
| [`msagent deployment connection update`](#msagent-deployment-connection-update---mcs-specific) | Bind the agent's connection references, resolving one connector at a time. |
| [`msagent deployment connection remove`](#msagent-deployment-connection-remove---mcs-specific) | Remove a connection reference from the workspace, delete a connection from the environment, or both. |
| [`msagent deployment connection connectors`](#msagent-deployment-connection-connectors---mcs-specific) | List the connectors the environment serves, so a reference can be declared against one. |

These commands support MCS agents and standalone MCS workflows and are not feature-gated. They accept
`--project` (`-p`, current directory), `--deployment-id` (`-did`), `--deployment-name` (`-dn`),
`--agent-id` (`-aid`), and `--agent-name` (`-an`), as described in
[Selecting a deployment and project](Command_Deployment.md#selecting-a-deployment-and-project).
A deployment ID or name is required; there is no active-environment fallback. A unique deployment
name can be used without an agent selector.

`connection add --connection-only`, `connection remove --connection-id`, and `connection connectors`
can instead take `--environment-id`. In this mode, do not supply any deployment or agent selectors;
no agent workspace is required. `--project` does not select a deployment in that environment.

For MCS agents, `list`, ordinary `add`, and `update` must resolve the deployed agent by the workspace
schema (or `--schema-name`); a deployment slot alone does not create that cloud agent. Standalone
workflows resolve their matching local workflow instead and do not need a Copilot project schema or
cloud agent lookup.

The global `--non-interactive` option suppresses automatic browser launch, not all connection menus.
When consent is needed, the command prints a link and waits. It can be opened on this machine or
another device, but a connector that returns a consent code needs the browser on this machine for
the loopback callback. `connection update` still reads menu answers from stdin; end-of-input exits
the selection flow after any automatic bindings already made. Use `--connection-id` with a scope
resolving exactly one connector for an explicit update without a selection menu.

### AMS mode (Gated)

In [AMS mode](Command_Agent.md#ams-mode-gated), that is, while `AmsMcsAgents` is enabled, these
commands work exactly as described here: the Agent Management Service keeps no connection bindings,
and `add` and `remove` change the agent's folder, which reaches its GRS repository when you commit
and push it. A `msagent agent deploy --remote` keeps the bindings each environment already holds but
binds no reference itself, so after the first remote deploy into a new Test or Prod environment, run
`update` once for that deployment; it turns on each flow whose references are then all bound.

## `msagent deployment connection list` - MCS-specific

Reports the agent's connection references and the connection behind each. Changes nothing.
The result also reports connection/workflow readiness, usages, candidate owners, and workflow states
last observed for this environment. Unknown or stale workflow observations are not a fresh cloud
activation check. `--by-reference` changes the human report layout, not the set of references read.

| Option | Shortcut | Value | Description |
| --- | --- | --- | --- |
| `--schema-name` | `-sn` | String | Overrides the schema used for connection discovery. |
| `--by-reference` | `-bref` | Flag | List each connection reference on its own row instead of grouping by connector. |

## `msagent deployment connection add` - MCS-specific

Declares a connection reference for a connector and binds it, creating the connection when none can
be.

```text
msagent deployment connection add --connector <connector> [selectors] [options]
```

| Option | Shortcut | Value | Required | Description |
| --- | --- | --- | --- | --- |
| `--connector` | `-conn` | String | Yes | The connector to declare a reference for, by internal id. |
| `--connection-id` | `-conid` | String | No | Bind the new reference to this connection instead of resolving one. |
| `--environment-id` | `-eid` | String | No | Create in this environment instead of a deployment's. Only with `--connection-only`. |
| `--schema-name` | `-sn` | String | No | Overrides the schema used to resolve the deployed agent. |
| `--connection-only` | `-conly` | Flag | No | Create the connection and declare no connection reference. |

Without `--connection-only`, the command declares a new reference, then attempts to bind it.
`--connection-id` must identify a connected candidate for the requested connector. Otherwise it keeps
an existing binding or automatically chooses an eligible connected connection owned by the signed-in
operator; interactive selection can choose or create a connection even when others exist.
In non-interactive mode it creates a connection only when the catalogue proves there are no
connections to choose from; ambiguous/unreadable catalogues may leave the new reference unbound.

Check `ready`, `status` (`connection-reference-bound` or `connection-reference-declared`), diagnostics,
and the returned bind command. A successful declaration is not a guarantee of a usable binding.
If binding fails after declaration, the reference remains: run the suggested `connection update`
command rather than blindly repeating `add`, which declares another reference.

With `--connection-only`, `--connection-id` is rejected. Use either a deployment or `--environment-id`,
not both. This mode **always creates a new connection**, even when the operator already owns one,
and binds nothing. Its result reports `connection-created`, the connection ID, and a bind command.
If creation/consent fails, inspect diagnostics and the environment for a surviving connection before
retrying; incomplete connections are cleaned up where possible, but cleanup itself can fail.

```text
msagent deployment connection add --deployment-name Development --connector shared_office365
msagent deployment connection add --connection-only --environment-id <environment-id> --connector shared_office365
```

## `msagent deployment connection update` - MCS-specific

Binds the agent's connection references, resolving one connector at a time.

| Option | Shortcut | Value | Description |
| --- | --- | --- | --- |
| `--schema-name` | `-sn` | String | Overrides the schema used for connection discovery and binding. |
| `--connection-reference` | `-cref` | String | Act on this connection reference only, by logical name. |
| `--connector` | `-conn` | String | Act only on the references that target this connector. |
| `--connection-id` | `-conid` | String | Bind to this connection. The selectors must resolve to exactly one connector. |
| `--by-reference` | `-bref` | Flag | Resolve each connection reference on its own instead of a connector at a time. |

`--connection-reference` and `--connector` narrow the same scope and can be combined. A missing
reference or a connector with no matching reference is an error. With `--connection-id`, the selected
references must target exactly one connector; this explicit path skips menus.

Automatic proposals preserve already bound references and choose eligible connected candidates owned
by the signed-in operator. Bindings are written as each choice is settled, and newly unblocked
workflows are enabled immediately. Exiting a menu or a later failure does not undo those changes.
`--by-reference` controls selection granularity; the result still reports every reference, including
ones outside the selected scope.

The `connections-updated` result contains `connectionsReady`, `workflowsReady`, `ready`, connection and
workflow details, activated/refused workflows, and diagnostics. Success does not imply `ready=true`:
unresolved references or refused workflow activation can remain.

```text
msagent deployment connection update --deployment-id <id> --connection-reference <logical-name> --connection-id <connection-id>
```

## `msagent deployment connection remove` - MCS-specific

Removes a connection reference from the workspace, deletes a connection from the environment, or
both.

Removing a reference deletes the environment's `connectionreference` record as well as the
workspace declaration. The environment refuses that delete while another component still depends on
the record, which leaves the declaration removed and the record in place; the command reports which
of the two it removed.

| Option | Shortcut | Value | Description |
| --- | --- | --- | --- |
| `--connection-reference` | `-cref` | String | The connection reference to remove from the workspace, by logical name. |
| `--connection-id` | `-conid` | String | The connection to delete from the environment. |
| `--connector` | `-conn` | String | The connector the connection serves. Inferred from the workspace when omitted. |
| `--environment-id` | `-eid` | String | Delete from this environment instead of a deployment's. Not with a deployment selector. |
| `--schema-name` | `-sn` | String | Overrides the schema used to resolve the deployed agent. |
| `--force` | `-f` | Flag | Remove or delete even while something still uses what was named. |

At least one of `--connection-reference` and `--connection-id` is required; supplying both removes
the declaration first, then deletes the connection. `--connection-reference` requires a deployment
workspace, so it cannot be used with `--environment-id`. Environment-only connection deletion needs
`--connector`, since no workspace can infer it.

Without `--force`, a reference still used by components requires confirmation; the global
`--non-interactive` option follows the confirmation policy and proceeds without prompting. Connection
deletion is different: other references holding the connection cause a usage error unless `--force`
is supplied. This check sees the resolved workspace, not every possible consumer in the environment.
`--force` does not bypass server-side dependency checks or guarantee the environment record is deleted.

The result reports `connection-reference-removed` or `connection-removed`, usages, and
`connectionReferenceRecordDeleted`. If deleting a connection fails after its reference was removed,
the declaration stays removed. Use the reported connection-only delete command to retry rather than
repeating both actions.

## `msagent deployment connection connectors` - MCS-specific

Lists the connectors the environment serves, so a reference can be declared against one.

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--environment-id` | `-eid` | String | Deployment's environment | Read this environment instead of a deployment's. Not with a deployment selector. |
| `--filter` | `-flt` | String | None | Show only connectors whose name, internal id, or publisher contains this text. |
| `--connector` | `-conn` | String | None | Show only the connector with this internal id. |
| `--limit` | `-lim` | Whole number | `50` | Maximum rows to return. |

`--limit` must be positive. Text matching is case-insensitive; `--filter` and `--connector` intersect.
In-use connectors sort first, then display name and internal ID. The `connectors-listed` result includes
the applied limit and `truncated` flag; increase the limit or narrow the filter when truncated.
Each connector includes its ID, display name, publisher, tier, and in-use state. In-use is unknown
when using `--environment-id` or when workspace discovery fails; the latter also adds a diagnostic.
