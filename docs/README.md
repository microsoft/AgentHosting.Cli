## Docs (this repo)

All docs live in the [AgentHosting.Cli repo](https://github.com/microsoft/AgentHosting.Cli). Start with [About the msagent CLI](../README.md), then the [Quickstart](Quickstart.md).

| Topic | Doc |
|---|---|
| Create, clone, initialize | [New Agents](New%20Agents.md) |
| Edit | [Editing Agents](Editing%20Agents.md) |
| Coding agents | [Using msagent CLI with GitHub Copilot](Using%20msagents%20CLI%20with%20GitHub%20Copilot.md) |
| Connections | [Working with connections, locally](Working%20with%20connections%2C%20locally.md) |
| Source control | [Source Control with msagent cli](Source%20Control%20with%20msagent%20cli.md) |
| Deploy | [Deploying your Agent](Deploying%20your%20Agent%20from%20your%20local%20machine.md) |
| Test | [Testing your Agent](Testing%20your%20Agent%20from%20your%20local%20machine.md) |
| Workflows | [Working with workflows](Working%20with%20workflows.md) |
| Coding agent rules | [Additional coding agent instructions](Additional%20coding%20agent%20instructions.md) |

## Instructions

- Agent instructions for GitHub Copilot or Claude: [agent.md](agent.md). Attach it explicitly, or copy it to the instruction-file location supported by your assistant, such as `AGENTS.md`, `CLAUDE.md`, or `.github/copilot-instructions.md`.
- Local testing: use the maintained [terminal and Playground guide](Testing%20your%20Agent%20from%20your%20local%20machine.md). No separately supplied test client is required.
- Optional skills: see [coding-agent setup](Using%20msagents%20CLI%20with%20GitHub%20Copilot.md#setting-up-your-coding-agent-skills-and-agentmd).

## Command reference

- In the CLI: `msagent --help`, `msagent <command> --help`.
- Full reference: [MSAgent CLI command reference](../src/Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md)

## CLI development

These references are for changing the CLI itself, not authoring agent projects:

- [Contributing](../CONTRIBUTING.md): authoritative architecture, build, test, safety, and documentation requirements.
- [Developer reference](../src/Microsoft.Agents.Hosting.Cli/agent.md): host configuration and feature controls.
- [Telemetry details](developer/Telemetry.md): schema, privacy, routing, and counting.
- [Comparison](developer/Comparison.md): scoped offline conformance and pinned TypeScript input.
- [Manual testing and records](developer/ManualTesting.md): live checks, approval boundaries, and evidence.

## msagent info

- Install: See the [Prerequisites](../README.md#prerequisites) section of the root README.
- Use `msagent` for these workflows; do not mix it with other agent-management CLIs in the same workspace.
- `msagent test ask` sends live queries to a published agent; `msagent test playground` launches an interactive testing UI. Neither is a local runtime emulator.
- Use `--agent-type MCSAgent` explicitly when scaffolding an agent, or `MCSWorkflow` for a standalone workflow.
- Please file issues here: https://github.com/microsoft/AgentHosting.Cli/issues
