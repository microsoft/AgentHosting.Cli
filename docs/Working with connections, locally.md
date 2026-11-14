# Working with Connectors 

There are over 1500+ connectors and many of which are provided by Microsoft, such as excel, powerpoint and outlook. The msagent CLI makes working with connectors easier. 

## The CLI makes it easier to work with connections in Copilot Studio

Copilot Studio uses connectors as a tool itself, or within workflows (which can be used standalone, referenced by an agent, or with the new preview feature, hooks)

- Connectors: The API definition
- Connections: authenticated connector credentials held in a Power Platform environment
- Connection references: Used by copilot studio to manage the connection and the tool definition
- Tool definition: Tool definition information such as what tool, when it should be triggered and additional information for copilot studio

The connection is not and will never be on your local machine. These are credentials used to authenticate with the connector and is used by copilot studio runtime when triggering the tool definition with the connection reference.

The connection must exist in the target environment and be usable by the
appropriate identity. Local files declare references and tool definitions; they
do not carry the cloud connection's credentials. Binding maps a logical reference
to an authorized connection in that environment. A connection in development
does not automatically provide credentials in test or production.

## Option 1: Create your connections before you start

You can ensure your connections are available before you want to use with your agent. Go to make.powerapps.com and click connections and create the connections for the tools/connectors you wish to use.

## Option 2: Create and bind connections as part of deployment setup

This is common and sometimes easier if you’re working in the terminal

Use the connection commands to inspect references, create a connection when
needed, and bind it. Deploying source alone does not guarantee connection creation,
consent, or a usable binding:

```text
msagent deployment connection list --deployment-name develop
msagent deployment connection update --deployment-name develop
msagent deployment connection list --deployment-name develop
```

For an agent, these commands need the cloud agent to exist; creating a local
deployment record alone is not enough. Deploy a draft to establish it, then
inspect diagnostics. A missing workflow reference can leave a deployment
incomplete: binding the exact reference and retrying can be required before the
deploy fully succeeds. See [workflow connection readiness](Working%20with%20workflows.md#connection-references-and-deployment).

Complete connector consent when requested and inspect `ready`, status, and
diagnostics rather than relying only on the command's exit code. Credentials can
expire, consent can be revoked, and connection access can change. Binding can
also activate eligible workflows; confirm their intended effects before running
it. Coding assistants can help issue commands but cannot grant consent for you.

## Managing issues with connections

Sometimes you may encounter issues when creating or connecting the binding. 

Common fail states when running  deployment connection update  (or  add ):

- Ambiguous selection: multiple connectors/references match your selectors, and the command can't pick one automatically. Narrow with  --connector ,  --connection-reference, or use  --by-reference  to resolve one reference at a time instead of one connector at a time.
- Non-interactive mode blocks consent: with  --non-interactive , the CLI won't launch a browser/WAM sign-in to create a new connection — it instead prints a consent link you must open by hand, or fails outright if no link can be produced.
- No existing connection to choose from: plain  deployment connection add  only auto-creates a connection when the connector has none already; if multiple exist, it needs interactive input (or  --connection-id ) to pick one.
- Stdin-dependent resolution:  update  lists available connectors and reads your choice from stdin — piping/redirecting input or running non-interactively without an answer ends the run early.
- Wrong scope target:  --connection-id / --connector  selectors must resolve to exactly one connector/reference — if your selectors are too broad, binding fails.

Fix by pre-specifying  --connection-id  directly, scoping with  --connector / --connection-reference , or completing consent via the printed link when running non-interactively.

## Connector Command reference

```
# List the agent's connection references and their bound connections
msagent deployment connection list -an "<agent-name>" -dn <deployment-name>

# List connectors available in the environment
msagent deployment connection connectors -an "<agent-name>" -dn <deployment-name>

# Add/bind a connection reference for a connector (creates connection if none exists)
msagent deployment connection add -an "<agent-name>" -dn <deployment-name> --connector <connector-id>

# Create a connection only, without declaring a reference
msagent deployment connection add --connector <connector-id> --connection-only --environment-id <environment-id>

# Update/bind connection references (resolve one connector at a time)
msagent deployment connection update -an "<agent-name>" -dn <deployment-name>

# Update, resolving one reference at a time instead
msagent deployment connection update -an "<agent-name>" -dn <deployment-name> --by-reference

# Bind to a specific connector/reference and connection id
msagent deployment connection update -an "<agent-name>" -dn <deployment-name> --connector <connector-id> --connection-id <connection-id>

# Remove a connection reference and/or delete the connection
msagent deployment connection remove -an "<agent-name>" -dn <deployment-name>
```

Removing a local reference, unbinding a reference, and deleting a cloud
connection are different effects. A connection can be shared by other tools or
flows; review the selection and intended removal before confirming. See the
reference for the supported removal options and scope.

The full list of commands for connections can be found here: [MSAgent CLI connection command reference](../src/Microsoft.Agents.Hosting.Cli.Docs/Command_Deployment_Connection.md)

## Available connectors and reference material for all Microsoft published connectors

For more information on available connectors, see the [complete connector reference](https://learn.microsoft.com/en-us/connectors/connector-reference/), which includes a dedicated page for [all Microsoft-published connectors](https://learn.microsoft.com/en-us/connectors/connector-reference/connector-reference-microsoft-connectors). Documentation for individual connectors is also available, such as [Excel Online (Business)](https://learn.microsoft.com/en-us/connectors/excelonlinebusiness/) and [Word Online (Business)](https://learn.microsoft.com/en-us/connectors/wordonlinebusiness/). Developers who want to explore connector definitions or contribute their own can visit the [Power Platform Connectors GitHub repository](https://github.com/microsoft/PowerPlatformConnectors), and the [Power Platform CLI connector commands](https://learn.microsoft.com/en-us/power-platform/developer/cli/reference/connector) reference covers how to manage connectors from the command line.