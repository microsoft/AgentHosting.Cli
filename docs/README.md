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
| Personal DEV CodeAgent validation (this branch) | [DEV stamp testing with AMS-CMP and AMS-DP](dev-stamp-testing.md) |
| Coding agent rules | [Additional coding agent instructions](Additional%20coding%20agent%20instructions.md) |

## Instructions

> **Note:** The files below are provided separately as part of the bug bash and are not in this repo at the moment.

- Agent instructions for GitHub Copilot or Claude: [agent.md](agent.md) (copy into your project as `AGENTS.md`, `CLAUDE.md` or `.github/copilot-instructions.md`).
- Bug bash instructions: `MSAGENTS bug bash instructions 10-8.docx`.
- Local testing: `Testing Package with Setup Instructions\TEST-CLIENT-ONBOARDING.md` in the Bug Bash folder.

## Command reference

- In the CLI: `msagent --help`, `msagent <command> --help`.
- Full reference: [MSAgent CLI command reference](../src/Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md)

## msagent info

- Install: See the [Prerequisites](../README.md#prerequisites) section of the root README.
- Never use `pac` during the bug bash, and never mix it with `msagent`.
- `msagent test` is not yet implemented. Use the provided test client (a shim for how `msagent test` will behave).
- CodeAgent paths are outside the ordinary Copilot Studio bug bash. This branch's separately gated [personal DEV walkthrough](dev-stamp-testing.md) covers native CodeAgent package/deployment validation, not general CodeAgent availability.
- Please file issues here: https://github.com/microsoft/AgentHosting.Cli
