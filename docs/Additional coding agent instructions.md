# Additional Coding Agent Instructions

This document is provided in case the agent.md file has not been downloaded or made available to the coding agent's session. Coding agents should review this document for instructions on using the msagent CLI and should be used in combination with the wider msagent documentation here and the CLI's help menu.

## Do not use the PAC CLI together with msagent

- The PAC CLI is the power platform CLI and is the older version of the CLI made to interact with copilot studio. Whilst it is fully supported, using it together with msagent CLI can cause unexpected issues and conflicts, as they work differently. Never combine the two CLI's when working with copilot studio within a local folder directory.

## Select the Copilot Studio project type explicitly

- Use `--agent-type MCSAgent` for conversational agents and `--agent-type MCSWorkflow` for standalone workflows. Do not rely on an omitted agent type selecting the intended scaffold.

## When testing locally

- Clarify with the user how they want to test. Testing can be done with human interactions using the agent playground local UX application or coding agents can use one or more sessions to run tests interacting to copilot studio runtime from their local machine.

## Authenticating with copilot studio when testing locally

- `test ask` uses the selected CLI account and delegated invoke permission. Sign in interactively first; `--non-interactive` prevents new interactive authentication, not authorization checks or consent requirements.
- The default Playground transport uses the CLI account for endpoint discovery, then the Playground authenticates its own session. It can still require user interaction with `--non-interactive`.
- Direct Line Playground requires a published no-auth agent and an interactive terminal. Enter the token endpoint or Direct Line secret only at its native masked prompt, never in chat, command arguments, or source files. This path does not require CLI Entra sign-in.
- Consent and access can change. Do not promise one-time consent or ask the assistant to grant it for the user. See the [testing guide](Testing%20your%20Agent%20from%20your%20local%20machine.md).

## Workflows and disambiguating the different types

- There are different types of agent workflow available based on the different versions of both copilot studio (standard harness) and workflows (cloud flows, power automate, different designers). There are some differences between the definitions of these workflows (that use json) that determine how they are deployed to the runtime that is very important to understand. Ensure you read the reference material which covers these differences.

## Workflows standalone and workflows connected to an agent

- Workflows are created and can be referenced by an agent as a tool, or in the new preview feature for hooks. Workflows can also be used standalone, without an agent. 

Use `MCSWorkflow` for an independent cloud flow; an agent-callable workflow belongs
under the agent's `workflows` directory and needs a matching workflow tool.
Preserve workflow IDs, metadata, input/output names, and connector references.
See [Working with workflows](Working%20with%20workflows.md) for authoring shape,
registration, activation, and connection-readiness details.

## Understand the different types of copilot studio harness 

- `--mcs-agent-type CLI` is the default scaffold for GitHub Copilot-backed agents; `--mcs-agent-type Classic` explicitly selects the standard/classic scaffold. Both use `--agent-type MCSAgent`. The assistant authoring the files is separate from the deployed runtime.
- The scaffold selector is not a migration command. Do not change runtime identity or mix agent-management CLIs to convert an existing agent. Clone existing cloud content through `msagent` and preserve its identities unless a supported operation explicitly changes them. See the [agent command reference](../src/Microsoft.Agents.Hosting.Cli.Docs/Command_Agent.md#creating-a-new-mcs-agent).
