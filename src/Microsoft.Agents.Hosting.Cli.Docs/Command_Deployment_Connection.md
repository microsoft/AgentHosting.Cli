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

Every connection command accepts the deployment selectors and `--project` described in
[Selecting a deployment and project](Command_Deployment.md#selecting-a-deployment-and-project). With the global
`--non-interactive` option, `connection add --connection-only` and `connection update` still create
connections and print the consent link instead of opening a browser. Plain `connection add` creates
a connection only when the connector has no existing connection to choose between. `connection
update` lists its connectors and reads the answer from stdin; end-of-input ends the run.

## `msagent deployment connection list` - MCS-specific

Reports the agent's connection references and the connection behind each. Changes nothing.

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

## `msagent deployment connection update` - MCS-specific

Binds the agent's connection references, resolving one connector at a time.

| Option | Shortcut | Value | Description |
| --- | --- | --- | --- |
| `--schema-name` | `-sn` | String | Overrides the schema used for connection discovery and binding. |
| `--connection-reference` | `-cref` | String | Act on this connection reference only, by logical name. |
| `--connector` | `-conn` | String | Act only on the references that target this connector. |
| `--connection-id` | `-conid` | String | Bind to this connection. The selectors must resolve to exactly one connector. |
| `--by-reference` | `-bref` | Flag | Resolve each connection reference on its own instead of a connector at a time. |

## `msagent deployment connection remove` - MCS-specific

Removes a connection reference from the workspace, deletes a connection from the environment, or
both.

| Option | Shortcut | Value | Description |
| --- | --- | --- | --- |
| `--connection-reference` | `-cref` | String | The connection reference to remove from the workspace, by logical name. |
| `--connection-id` | `-conid` | String | The connection to delete from the environment. |
| `--connector` | `-conn` | String | The connector the connection serves. Inferred from the workspace when omitted. |
| `--environment-id` | `-eid` | String | Delete from this environment instead of a deployment's. Not with a deployment selector. |
| `--schema-name` | `-sn` | String | Overrides the schema used to resolve the deployed agent. |
| `--force` | `-f` | Flag | Remove or delete even while something still uses what was named. |

## `msagent deployment connection connectors` - MCS-specific

Lists the connectors the environment serves, so a reference can be declared against one.

| Option | Shortcut | Value | Default | Description |
| --- | --- | --- | --- | --- |
| `--environment-id` | `-eid` | String | Deployment's environment | Read this environment instead of a deployment's. Not with a deployment selector. |
| `--filter` | `-flt` | String | None | Show only connectors whose name, internal id, or publisher contains this text. |
| `--connector` | `-conn` | String | None | Show only the connector with this internal id. |
| `--limit` | `-lim` | Whole number | `50` | Maximum rows to return. |
