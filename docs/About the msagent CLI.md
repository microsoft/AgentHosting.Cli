# What is the CLI

The CLI is for operating copilot studio agents from your local terminal, designed to offer developers and advanced makers the ability to work with copilot studio directly in their flow of work through the full lifecycle of a copilot studio agent, from creating to publishing and testing.

Before, you could only go through the browser to build and deploy an agent. Now you can build, test and ship copilot studio agents directly in your terminal with or without the help of AI. 

# Why is this important

The CLI creates a local definition of a copilot studio agent as a project that acts as your workspace, allowing you to edit the definition, adding functionality to it, commit changes locally and push to your remote repository. From your remote repo, you and your team can manage your changes through pull requests, see what changes have been made, and pull-down the definition or integrate into your dev ops pipelines to deploy to the same or other environments.

# Prerequisites

- Access to download the CLI - [here](https://github.com/microsoft/AgentHosting.Cli)
- Permissions to install applications to your laptop (the CLI)
- Permissions & access to Copilot Studio through a work/school account (verified through copilotstudio.microsoft.com
- Access to create your own agents and run those agents)
- Authenticated Power Platform connections for any tools the agents uses
- If your using AI, it is recommended to download the skills and agent.md file to assist your agent in working with the CLI 

# Hello World

- in the CLI, once authenticate with [msagent auth login] and select your environment with [msagent env list] and [msagent env select] 

- Run the following command to create a new copilot studio agent
[msagent create ]

- You have successfully created an agent with instructions. 

# What to do next

- Follow the [quickstart](Quickstart.md) guide to go through the full end to end lifecycle of a copilot studio agent
- [New Agents](New%20Agents.md): Create, Clone or Initialize
- [Editing Agents](Editing%20Agents.md)
  - [Using msagent CLI with GitHub Copilot](Using%20msagents%20CLI%20with%20GitHub%20Copilot.md)
  - [Working with connections, locally](Working%20with%20connections%2C%20locally.md)
- [Source Control](Source%20Control%20with%20msagent%20cli.md)
- [Deploying your Agent](Deploying%20your%20Agent%20from%20your%20local%20machine.md)
- [Testing your Agent](Testing%20your%20Agent%20from%20your%20local%20machine.md)
  - using the agent playground
  - using terminal
- Additional Activities
  - [Additional coding agent instructions](Additional%20coding%20agent%20instructions.md)
- Review the command reference
- [File an issue or ask a question](https://github.com/microsoft/AgentHosting.Cli/issues)

## Cost Management

For private previews, and any further previews including when generally available, the CLI uses the Copilot Studio runtime when remotely connecting to it using the test operations. These operations will require paid access to Copilot Studio. You must ensure you have valid cost controls applied depending on what cost management plan your organization uses, and your signed in user has access to create, use & operate. 
