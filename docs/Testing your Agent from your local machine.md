# Testing your Agent

Once your agent has been deployed as published and made accessible to the copilot studio runtime you can test it from your local machine.

*Note, at the time of BUG BASH and initial PRIVATE PREVIEW testing may not be available as an ```msagent``` command. Locally testing your agent is still possible, with specific instructions which will be provided to you. 

## Interactive testing

If you want to test it yourself with a UX, you can use the Agent Playground which has been updated to support copilot studio agents using either directline or copilot studio client connectivity (direct)

### Setup Agent Playground with DirectLine

Directline is the only method to support annon configuration in a Copilot Studio Chat client and requires directline secret configuration setup. The secret is obtainable from copilotstudio.microsoft.com within the settings area of your agent

```
msagent test playground --an agentname -dn deploymentname to be added
```

### Setup Agent Playground with direct

```
msagent test playground --an agentname -dn deploymentname to be added
```

Direct configuration uses Entra ID to connect to the copilot studio agent using delegated access that you give it with your identity.

### Testing using Terminal commands non-interactively (including with AI coding agents)

If you want to test your agent in a terminal, typically using non-interactive with GitHub Copilot, use the commands below. Non-interactive commands for testing only support direct (using copilot studio client) using Entra ID and does not support directline. The agent will run using the signed in users’ credentials that agree to the interaction. If you require testing the agent in annon mode, directline must be used via the agent playground.

```
msagent test ask --an agentname -dn deployment name
```

Expect a browser to open if this is the first time you have used this to agree to using your credentials to run the copilot studio agent

### How it works

The CLI brokers the authentication between the local machine and accessing Copilot Studio, which must be using integrated auth or manual authentication using Azure Entra Active Directory v2 when using non-interactive or copilot studio client with agent playground. If using directline with the agent playground it uses directline tokens which must be obtained from the Copilot Studio UI under the settings area. The CLI user will be required to give the CLI permission to access Copilot Studio on their behalf, and this will need to be granted in the browser by the person giving consent. This should only be needed once and refreshed periodically.
