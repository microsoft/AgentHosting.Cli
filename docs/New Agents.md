# Creating new Agents

Creating new agents using the msagent CLI means creating a local definition of the copilot studio agent and making it ready on your machine in your working directory. This can be done in 3 ways:

## Create new

This creates a new copilot studio definition using the GitHub harness in your working directory using the copilot studio defaults and your provided name, description and instructions, and establishes the environment connection. This is best used if you want a brand new agent and intend to deploy it to the runtime.

```
msagent agent create -n "My Agent" -d "Description" -ip "Initial prompt/instructions"
```

## Clone

This creates a new copilot studio definition on your working directory using the configuration from the cloud that you cloned it from. This is best used if you want to start from an existing agent that is on copilotstudio.microsoft.com already and makes it easier to use the VS Code extension to work through the definition that was pulled locally

```
msagent agent create --mcs-clone-agent-id <agent-id> --mcs-clone-environment-id <env-id>
```

## Initialize

This assumes an already working definition of a copilot studio agent in your working directory and completes the initialization step by creating the environment connection details and project, making it 'ready' to be used locally. This command exists to establish the connection to your environment and the sync status for an agent definition you already have. Use it when you have cloned a definition from a remote repo, or otherwise have the agent definition files in your working directory but not the configuration files that connect them to your environment.

```
msagent agent init -an "My Agent" -mas ./path/to/project
```