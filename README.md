>[!IMPORTANT]
>This CLI and supporting componets are in **Public Preview**. This means:
>- **Features and capabilities are subject to change, and may not be in a fully finished form.**
>- **If a feature option described here is not in the public --help of the CLI, it is not available for general use.**
>- **The CLI will tell you when there is a new version, please accept those updates as we move though public preview as we are fixing bugs and enhancing support for things.**
>- These features are subject to [supplemental terms of use](https://www.microsoft.com/en-us/business-applications/legal/supp-powerplatform-preview/), and are available before an official release so that customers can get early access and provide feedback.

# What is the Microsoft.Agents.Hosting.CLI Tool (msagent)

The Microsoft.Agents.Hosting.CLI (`msagent`) connects local development workflows with Microsoft Copilot Studio. It lets teams define agents and workflows as code, create and update them while collaborating with developers using the Copilot Studio UI, and apply software development lifecycle (SDLC) and application lifecycle management (ALM) practices to manage connections, test and evaluate agents, and deploy changes.

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
- The CLI checks NuGet for newer versions when you run it interactively and displays an update command when one is available. To disable these notifications, set `AH_CLI_SKIP_UPDATE_CHECK=true`. If you installed the CLI locally or with `--tool-path`, use the same installation scope when updating.
- If you're using AI, provide [agent.md](docs/agent.md) and any compatible skills supplied by your organization to ground your coding assistant in the CLI's workflows. An editor or coding assistant is optional.

# Get started: Create your first agent

Sign in and select a Power Platform environment:

```powershell
msagent auth login
msagent env list
msagent env select <environment-id>
```

Create a local agent definition with a name, description, and instructions:

```powershell
msagent agent create --agent-type MCSAgent -n "Hello World" -d "A friendly greeting agent" -ip "Greet the user and ask how you can help."
```

Your agent files are ready to edit locally. Follow the [quickstart](docs/Quickstart.md) to deploy, evaluate, and publish your agent in Copilot Studio.

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


## Support

This project uses [GitHub issues](https://github.com/microsoft/AgentHosting.Cli/issues)
to track bugs and feature requests. Please search existing issues before filing
a new one to avoid duplicates. For a new bug,
[open an issue in this repository](https://github.com/microsoft/AgentHosting.Cli/issues/new/choose).

# Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of Microsoft 
trademarks or logos is subject to and must follow 
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion or imply Microsoft sponsorship.
Any use of third-party trademarks or logos is subject to those third parties' policies.
