>[!IMPORTANT]
>This CLI and supporting componets are in **Public Preview**. This means:
>- **Features and capabilities are subject to change, and may not be in a fully finished form.**
>- **If a feature option described here is not in the public --help of the CLI, it is not avaialbe for general use.**
>- **The CLI will tell you when there is a new version, please accept those updates as we move though public preview as we are fixing bugs and enhancing support for things.**

# What is the Microsoft.Agents.Hosting.CLI Tool (msagent)

The Microsoft.Agents.Hosting.CLI (`msagent`) connects local development workflows with Microsoft Copilot Studio. It lets teams define agents and workflows as code, create and update them while collaborating with developers using the Copilot Studio UI, and apply software development lifecycle (SDLC) and application lifecycle management (ALM) practices to manage connections, test and evaluate agents, and deploy changes.

The CLI is preview software. Obtain the package and review the terms of the preview before installing it.

> [!IMPORTANT]
>
> - This is a preview feature.
> - These features are subject to [supplemental terms of use](https://www.microsoft.com/en-us/business-applications/legal/supp-powerplatform-preview/), and are available before an official release so that customers can get early access and provide feedback.

## Why is this important

The CLI creates a local definition of a Copilot Studio agent as a project that acts as your workspace. This allows you to edit the definition, add functionality to it, commit changes locally, and push to your remote repository. From your remote repository, you and your team can manage changes through pull requests, see what has changed, and deploy reviewed source to development, test, and production environments. Source control does not itself configure deployment authentication; non-interactive CLI use requires an existing authenticated session and cannot complete new sign-in or consent.

## Documentation

[See the Copilot Studio CLI documentation](https://learn.microsoft.com/en-us/microsoft-365/copilot-managed-runtime/).

For repository-specific guides and technical references, start with the
[documentation index](docs/README.md) and [command reference](src/Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md).
For building or changing the CLI itself, see [CONTRIBUTING.md](CONTRIBUTING.md).

## Support

This project uses [GitHub issues](https://github.com/microsoft/AgentHosting.Cli/issues)
to track bugs and feature requests. Please search existing issues before filing
a new one to avoid duplicates. For a new bug,
[open an issue in this repository](https://github.com/microsoft/AgentHosting.Cli/issues/new/choose).

## Repository structure

This repository contains the `msagent` CLI, its documentation, and its tests.
It does not contain the Copilot Studio service or the agent-hosting runtime.

| Folder | Contents |
|---|---|
| [`docs/`](docs/) | Guides for creating, editing, deploying, testing, and maintaining agents with the CLI. |
| [`docs/developer/`](docs/developer/) | Contributor references for telemetry, conformance comparisons, and manual testing. |
| [`src/`](src/) | CLI projects, the solution, shared build settings, package versions, and versioning configuration. |
| [`src/Microsoft.Agents.Hosting.Cli/`](src/Microsoft.Agents.Hosting.Cli/) | The `msagent` executable: composition, command hosting, output rendering, configuration, and telemetry. |
| [`src/Microsoft.Agents.Hosting.Cli.Commands/`](src/Microsoft.Agents.Hosting.Cli.Commands/) | Command definitions, options, validation, and dispatch into Core operations. |
| [`src/Microsoft.Agents.Hosting.Cli.Core/`](src/Microsoft.Agents.Hosting.Cli.Core/) | Embeddable operations, contracts, ports, and local project/deployment state. |
| [`src/Microsoft.Agents.Hosting.Cli.Auth/`](src/Microsoft.Agents.Hosting.Cli.Auth/) | Sign-in, broker integration, token caching, and authentication registration. |
| [`src/Microsoft.Agents.Hosting.Cli.CopilotStudio/`](src/Microsoft.Agents.Hosting.Cli.CopilotStudio/) | Copilot Studio synchronization, Dataverse, workspace, connection, template, and evaluation adapters. |
| [`src/Microsoft.Agents.Hosting.Cli.Docs/`](src/Microsoft.Agents.Hosting.Cli.Docs/) | Detailed command references and error-code recovery guidance. |
| [`src/Microsoft.Agents.Hosting.Cli.Tests/`](src/Microsoft.Agents.Hosting.Cli.Tests/) | Offline unit, contract, command-host, and adapter tests. |
| [`src/Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests/`](src/Microsoft.Agents.Hosting.Cli.Core.Embedding.Tests/) | Isolated tests for using Core without the CLI host or authentication adapter. |
| [`src/Microsoft.Agents.Hosting.Cli.E2E/`](src/Microsoft.Agents.Hosting.Cli.E2E/) | Explicitly invoked live-service scenario harness and fixtures. Runs can change cloud resources or incur charges; this is not part of `dotnet test`. |
| [`src/AgentHostCli.Conformance/`](src/AgentHostCli.Conformance/) | Language-neutral contract fixtures and a TypeScript comparison runner. |
| [`src/Images/`](src/Images/) | Image assets used by the CLI package. |
| [`.github/`](.github/) | GitHub workflows, contribution automation, issue templates, and coding-agent instructions. |
| [`.azdo/`](.azdo/) | OneBranch pull-request/nightly pipeline definitions and build-feed configuration. |
| [`.config/`](.config/) | Repository-local .NET tool manifest and compliance-tool configuration, not an agent workspace's local registration. |

## Prerequisites

### Permissions:
- Permissions to install applications to your laptop (the CLI)
- Permissions and access to Copilot Studio through a work/school account (verified through copilotstudio.microsoft.com)
- Access to create your own agents and run those agents
- Authenticated Power Platform connections in each target environment for any connector-backed tools your agents use. You can create and bind these when adding those tools.

### MSAgent CLI
- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) to run `dotnet tool install` and `dotnet tool update`. The SDK includes the .NET 10 and ASP.NET Core 10 runtimes; both are required to run the CLI. If you only run an already-installed tool, install both runtimes instead.
- Install the MSAgent Hosting CLI as a .NET tool:

Initial install:
```text
dotnet tool install microsoft.agents.hosting.cli --prerelease -g
```
Apply update (use this to pick up the current version after you have installed it):
```text
dotnet tool update microsoft.agents.hosting.cli --prerelease -g
```
### Note:
- In a terminal, msagent checks listed versions on nuget.org and shows newer stable/prerelease versions on stderr before eligible command output, prompts, or help. Results are cached for 24 hours; feed checks allow three seconds and fail silently. Checks are skipped for version output, invalid/navigation-only commands, `--json`, CI, or redirected stdout/stderr. Set `AH_CLI_SKIP_UPDATE_CHECK=true` (or `1`) to opt out. Suggested commands target global installations; use your installation's scope for local or `--tool-path` tools.
- If you're using AI, provide [agent.md](docs/agent.md) and any compatible skills supplied by your organization to ground your coding assistant in the CLI's workflows. An editor or coding assistant is optional.

# Getting Started - Your first Copilot Studio Agent built with code: Hello World
- In the CLI, authenticate, then list and select your environment:

```
msagent auth create --name Dev
msagent env list
msagent env select <environment-id>
```

Use `msagent auth list`, then `msagent auth select --name Dev` (or `--index <position>`) to
choose a saved profile without signing in. `auth who` shows token-free profile/environment metadata.
See the [authentication reference](src/Microsoft.Agents.Hosting.Cli.Docs/Command_Auth.md) for
clouds, local aliases, workload credentials, secret-argument limitations and owned-state repair.
`auth status` remains available. Use `auth delete` to remove one profile or `auth clear` to
clear all owned authentication state. Legacy `auth login` and `auth logout` are disabled by
default; their opt-in compatibility behavior is documented in the authentication reference.

- Run the following command to create a new Copilot Studio agent:

```
msagent agent create --agent-type MCSAgent -n "My Agent" -d "Description" -ip "Initial prompt/instructions"
```

- You have scaffolded a local agent with instructions, not a cloud agent. Create a deployment record and deploy it to upload a draft:

```text
msagent deployment create --deployment-name develop --deployment-type dev
msagent agent deploy --deployment-name develop
```

Review the draft and any connection/workflow diagnostics before publishing. See the [quickstart](docs/Quickstart.md) for evaluation, publication, and live testing.

## What to do next

- [Copilot Studio CLI Plugin](.github/plugins/msagent-assistant/README.md):
  shared Claude/Copilot workflows and generated host distributions, maintained
  separately from the CLI's NuGet contents.

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

Testing from a local machine invokes the real Copilot Studio runtime, not a local simulation. Agent responses, connector actions, and evaluations can consume Copilot Credits and incur other service charges. Apply your organization's cost controls and ensure the signed-in user has the required access. Coding assistants have their own licensing and billing, separate from Copilot Studio. See [testing usage and billing](docs/Testing%20your%20Agent%20from%20your%20local%20machine.md#billing-and-channel-ids-in-usage-telemetry) for technical details.

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
- **How to disable telemetry**. Use `msagent telemetry disable`.
- **How to reenable telemetry**. Use `msagent telemetry enable`. An environment override takes precedence over the saved preference; see the [telemetry command reference](src/Microsoft.Agents.Hosting.Cli.Docs/Command_Telemetry.md).


# Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos is subject to those third parties' policies.
