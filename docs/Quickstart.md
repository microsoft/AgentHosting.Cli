# QuickStart

This covers steps on how to get started quickly with the core features of the msagent cli and the operations it provides to go from empty folder to deployed agent that is testable from your local machine.

You will scaffold an incident management agent, edit its definition, deploy a draft,
evaluate it, publish it, and query the published agent. Authoring happens locally;
deployment, evaluations, and live queries use Copilot Studio cloud services.

# Prerequisites

See the [Prerequisites](../README.md#prerequisites) section of the root README.

# Authenticate & Select Environment

It is recommended to start in a new directory on your local machine. Sign in and select the environment you want to work in. 

```
msagent auth login
msagent env list
msagent env select [environmentid]
```

# Create an agent

Create an agent locally in your directory. This will build out the initial components of a copilot studio agent on your machine. Nothing is deployed to copilot studio until later with the deploy command.

```
msagent agent create --agent-type MCSAgent --name "Incident Management Agent" --description "helps manage issues" --initial-prompt "Support customers with logging their initial ticket and triage" --project "IncidentManagement" --json
```

This command creates a new agent, creates a new 'project' called IncidentManagement, and scaffolds all the required files for your agent

Work from that project for the remaining commands:

```powershell
Set-Location IncidentManagement
```

# Edit your agent

Your agent is now scaffolded and registered locally. It is not yet connected to a
cloud agent for synchronization. Deploy it below to create the cloud copy, or
[clone an existing cloud agent](New%20Agents.md#clone) when that is your starting point.

You can edit your agent using:

- The optional [Copilot Studio VS Code extension](https://marketplace.visualstudio.com/items?itemName=ms-CopilotStudio.vscode-copilotstudio)
- Using GitHub Copilot agent or similar 
- Manually, using a text editor and familiarity with the YAML/JSON definition format

# Working with Connectors

The CLI helps you when working with connectors. Connectors are utilized in various places when working with agents and workflows, mostly surfaced as tools or actions. 

In a Copilot Studio agent definition on your local machine, connectors appear in two kinds of definition files:

- Tool definitions
- Connection references

Connection references connect the tool definition with a cloud connection, which
holds the connector's credentials and authorization in the target Power Platform
environment. Source files declare references, not credentials.

The Copilot Studio runtime uses these credentials when the tool is utilized. The CLI helps create a new binding or use an existing binding so working with connectors remains easy from your local machine.

After deploying the agent draft, use the binding commands below if you added
connector-backed tools. See [Working with connections](Working%20with%20connections%2C%20locally.md)
for selection rules and incomplete-deployment recovery.

# Deploying your agent

Once you are ready with your agent definition from your local machine, you are ready to deploy this to the copilot studio runtime, so you can see it at copilotstudio.microsoft.com

Create a named local deployment record, then upload a draft:

```
msagent deployment create --agent-name "Incident Management Agent" --deployment-name develop --deployment-type dev
msagent agent deploy --agent-name "Incident Management Agent" --deployment-name develop --json
```

If the agent uses connectors, inspect and bind its references:

```text
msagent deployment connection list --deployment-name develop
msagent deployment connection update --deployment-name develop
msagent deployment connection list --deployment-name develop
```

Complete any required connector consent, then inspect readiness again. A missing
workflow reference can require binding and a deployment retry before the deploy
fully succeeds. Do not publish while blocking diagnostics remain.

# Evaluate and publish your agent

Review the uploaded draft and any connection or workflow diagnostics. You can
evaluate it before publishing:

```text
msagent agent eval run --generate 3 --name "Incident smoke tests" --watch
msagent agent eval list
```

Evaluations use Copilot Credits and service-side grading. Generated conversations
are a starting point; review whether they cover your expected behavior. See the
[evaluation reference](../src/Microsoft.Agents.Hosting.Cli.Docs/Command_Agent_Eval.md)
for reusable test sets and results.

When you are ready to make the changes available to users, publish:

```text
msagent agent deploy --deployment-name develop --publish
```

# Testing your agent

Live testing requires a published agent that the signed-in account can access.
You may be prompted for delegated permission to invoke it.

- Use the Agent Playground UX to test which provides a local UI to test your copilot studio agent
- Use the terminal to test using the command below

```
msagent test ask --deployment-name develop --query "Help me log a new incident."
msagent test playground --deployment-name develop
```

For automation, authenticate interactively first, then use an existing session:

```text
msagent test ask --deployment-name develop --query "Help me log a new incident." --non-interactive --json
```

These are live service calls and can trigger billable actions. See
[Testing your Agent](Testing%20your%20Agent%20from%20your%20local%20machine.md)
for conversations, output formats, Playground prerequisites, and billing.