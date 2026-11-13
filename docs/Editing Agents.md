# Editing your Agent

Once an agent has been created, you can work with the agent definition. You can do this in a number of ways:

## VS Code Extension

If you are working with an existing agent definition, for example one you cloned or initialized from a remote repo, VS Code is a great way to work with your agent in an IDE you're familiar with. The extension includes native VS Code capabilities for working with the agent alongside the CLI.

To install and configure the extension, see [Install and configure the Copilot Studio VS Code extension](https://learn.microsoft.com/en-us/microsoft-copilot-studio/visual-studio-code-extension-install-configure).

## Terminal

You can work from the terminal, alone or with an AI coding agent such as GitHub Copilot or Claude, to create and edit your agent's files. The coding agent can draw on CLI help, the msagent documentation, and compatible skills supplied by your organization. Coding assistants have their own licensing and billing, separate from Copilot Studio.

To learn more, see [Using msagent CLI with GitHub Copilot](Using%20msagents%20CLI%20with%20GitHub%20Copilot.md), which covers coding agents, skills and the agent.md file.

## Manually

Developers and advanced makers familiar in YAML and JSON can author their own declarative agent, although it would require familiarity with YAML object model for copilot studio and in some cases, JSON.

An IDE is optional. Preserve component identities and relationships when editing
tools, connection references, skills, or workflows. Do not hand-edit generated
local registration or sync-cache files. See [Working with workflows](Working%20with%20workflows.md)
for workflow metadata and connection readiness.

## Validate your changes

Deploy to a named development target without `--publish` to update the draft:

```text
msagent agent deploy --deployment-name develop
msagent agent eval run --generate 3 --name "Editing smoke tests" --watch
```

Review diagnostics and evaluation results before publishing. Attached workflows
can activate independently of conversational-agent publication, so review their
state and intended effects too. See [Deploying your Agent](Deploying%20your%20Agent%20from%20your%20local%20machine.md)
for publication and drift handling.
