# QuickStart

This covers steps on how to get started quickly with the core features of the msagent cli and the operations it provides to go from empty folder to deployed agent that is testable from your local machine.

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
msagent create --agent-type MCSAgent --name "Incident Management Agent" --description "helps manage issues" --initial-prompt "Support customers with logging their initial ticket and triage" --project "project1" --json
```

This command creates a new agent, creates a new 'project' called project1, and scaffolds all the required files for your agent

# Edit your agent

Your agent is now scaffolded with the environment connection for push/pull mechanics. Now you make edits or build

You can edit your agent using:

- The VS Code Extension
- Using GitHub Copilot agent or similar 
- Manually, using example documentation (will require an IDE, and familiarity with YAML)

# Working with Connectors

The CLI helps you when working with connectors. Connectors are utilized in various places when working with agents and workflows, mostly surfaced as tools or actions. 

In a Copilot Studio Agent definition on your local machine, connectors are seen in the following two files:

- Tool definitions
- Connection references

Connection references connect the tool definition with the connection, which is a specific binding in the Power Platform that holds the credentials and authorization for the connector definition, also on the Power Platform tenant you signed into. 

The Copilot Studio runtime uses these credentials when the tool is utilized. The CLI helps create a new binding or use an existing binding so working with connectors remains easy from your local machine.

The commands below are used when working with connectors locally with the CLI. Read the deep dive document on different scenarios for connectors to learn more.

```
msagent deployement update connection
```

# Deploying your agent

Once you are ready with your agent definition from your local machine, you are ready to deploy this to the copilot studio runtime, so you can see it at copilotstudio.microsoft.com

```
msagent agent deploy --agent-name "name" --deployment-name "dev" --publish --json
```

# Testing your agent (coming soon)

*Note, at the time of BUG BASH and initial PRIVATE PREVIEW, testing is not yet available as an `msagent` command. Locally testing your agent is still possible, with specific instructions which will be provided to you for triggering the test manually.

Your agent must be published to be able to test the agent locally. 

To test your agent locally, use the command below, and you may be prompted to confirm consent to using your identity to access the copilot studio agent, so the CLI can access copilot studio as you.

- Use the Agent Playground UX to test which provides a local UI to test your copilot studio agent
- Use the terminal to test using the command below

```
msagent test ask --input
```