# What is the Microsoft.Agents.Hosting.CLI Tool (msagent)

The Microsoft.Agents.Hosting.CLI(`msagent`) is a CLI tool that connects local development workflows with the Microsoft Copilot Platform for Microsoft Copilot Studio agents and Code Agents. For Copilot Studio, it lets teams define agents and Agent workflows as code, create and update them while collaborating with developers using the Copilot Studio UI, and apply software development lifecycle (SDLC) and application lifecycle management (ALM) practices to manage connections, test and evaluate agents, and deploy changes. For Code Agents built with the Microsoft 365 Agents SDK or related SDKs, it supports creating or initializing projects, configuring identity, user authentication, and communication channels, and managing SDLC and ALM processes for deployment to the platform.

## Why is this important

The CLI creates a local definition of a Copilot Studio agent as a project that acts as your workspace. This allows you to edit the definition, add functionality to it, commit changes locally, and push to your remote repository. From your remote repository, you and your team can manage changes through pull requests, see what has changed, pull down the definition, or integrate it into your DevOps pipelines to deploy to the same or other environments.

## Prerequisites

### Permissions:
- Permissions to install applications to your laptop (the CLI)
- Permissions and access to Copilot Studio through a work/school account (verified through copilotstudio.microsoft.com)
- Access to create your own agents and run those agents
- Authenticated Power Platform connections for any tools your agents use

### MSAgent CLI
- [.NET 10+ runtime](https://dotnet.microsoft.com/en-us/download/dotnet/10.0/runtime) - only required if you do not currently have .NET installed; only the Runtime package is needed, not the full SDK.
- (TEMP) Install the MSAgent Hosting CLI. This is a .NET tool and can be acquired using the following command:
```text
Initial install:
dotnet tool install microsoft.agents.hosting.cli --prerelease -g --source https://pkgs.dev.azure.com/dynamicscrm/OneCRM/_packaging/DPX-Tools-Upstream/nuget/v3/index.json

Apply update (use this to pick up the current version after you have installed it):
dotnet tool update microsoft.agents.hosting.cli --prerelease -g --source https://pkgs.dev.azure.com/dynamicscrm/OneCRM/_packaging/DPX-Tools-Upstream/nuget/v3/index.json
```
### Note:
- CLI updates happen frequently and are posted nightly when changes are pushed to the public feed. During the bug bash and private preview, it is useful to run the `dotnet tool update` command about every 24 hours before using the CLI.
- If you're using AI, it is recommended that you download the skills and `agent.md` file to assist your agent in working with the CLI.  

# Getting Started - Your first Copilot Studio Agent built with code: Hello World
- In the CLI, authenticate, then list and select your environment:

```
msagent auth login
msagent env list
msagent env select <environment-id>
```

- Run the following command to create a new Copilot Studio agent:

```
msagent agent create -n "My Agent" -d "Description" -ip "Initial prompt/instructions"
```

- You have successfully created an agent with instructions. 

## What to do next

- Follow the [quickstart](docs/Quickstart.md) guide to go through the full end-to-end lifecycle of a Copilot Studio agent.
- [New Agents](docs/New%20Agents.md): Create, Clone or Initialize
- [Editing Agents](docs/Editing%20Agents.md)
  - [Using msagent CLI with GitHub Copilot](docs/Using%20msagents%20CLI%20with%20GitHub%20Copilot.md)
  - [Working with connections, locally](docs/Working%20with%20connections%2C%20locally.md)
- [Source Control](docs/Source%20Control%20with%20msagent%20cli.md)
- [Deploying your Agent](docs/Deploying%20your%20Agent%20from%20your%20local%20machine.md)
- [Testing your Agent](docs/Testing%20your%20Agent%20from%20your%20local%20machine.md)
  - using the agent playground
  - using terminal
- Additional Activities
  - [Additional coding agent instructions](docs/Additional%20coding%20agent%20instructions.md)
- Review the [command reference](src/Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md)
- [File an issue or ask a question](https://github.com/microsoft/AgentHosting.Cli/issues)

## Cost Management

For private previews, and any further previews including when generally available, the CLI uses the Copilot Studio runtime when remotely connecting to it using the test operations. These operations will require paid access to Copilot Studio. You must ensure you have valid cost controls applied depending on what cost management plan your organization uses, and that your signed-in user has access to create, use, and operate agents.

# Telemetry
We use telemetry to capture command and feature useage inside the Microsoft.Agents.Hosting.CLI.  We use this inforamtion to inform product direction and investment decisions related to this CLI and the supporting services. 

- **Data Collection**. The software may collect information about you and your
  use of the software and send it to Microsoft. Microsoft may use this
  information to provide services and improve our products and services. You may
  turn off the telemetry as described in the repository. There are also some
  features in the software that may enable you and Microsoft to collect data
  from users of your applications. If you use these features, you must comply
  with applicable law, including providing appropriate notices to users of your
  applications together with a copy of Microsoft's privacy statement. Our
  privacy statement is located at
  https://go.microsoft.com/fwlink/?LinkID=824704. You can learn more about data
  collection and use in the help documentation and our privacy statement. Your
  use of the software operates as your consent to these practices.
- **How to see if telemetry is enabled**. You can see if Telemetry is enabled by using the command `msagent telemetry status`
- **How to disable telemetry**. You can disable telemtry using the `msagent telemtry disable` 
- **How to reenabled telemtry**. You can reenable telemtry using the command `msagent telemetry enabled`


# Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos is subject to those third parties' policies.
