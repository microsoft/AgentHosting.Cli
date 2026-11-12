# Copilot instructions for AgentHosting.Cli

Read and follow root [CONTRIBUTING.md](../CONTRIBUTING.md) before changing this
repository. It is the authoritative contributor guide for project architecture,
dependency boundaries, build/test commands, CLI contracts, safety, and
documentation maintenance. Keep agent instructions consistent with it rather
than maintaining a second copy of contributor policy here.

This repository builds `Microsoft.Agents.Hosting.Cli`, the framework-dependent
`msagent` .NET tool. It is not the Microsoft 365 Agents SDK, an ASP.NET Core
agent application, or the agent-hosting runtime. Do not import those projects'
layouts or release assumptions.

Use the [developer reference](../src/Microsoft.Agents.Hosting.Cli/agent.md) for
host configuration and feature controls. Detailed references are:

- [Telemetry](../docs/developer/Telemetry.md): schema, privacy, routing, and counting.
- [Comparison](../docs/developer/Comparison.md): scoped offline fixture contracts.
- [Manual testing](../docs/developer/ManualTesting.md): approved live checks and records.

For code review, also follow the applicable files in `.github/instructions/`.
For writing agent projects with the installed CLI, use
[docs/agent.md](../docs/agent.md), not contributor instructions.
