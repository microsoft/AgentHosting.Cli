# Creating new Agents

Creating new agents using the msagent CLI means creating a local definition of the copilot studio agent and making it ready on your machine in your working directory. This can be done in 3 ways:

## Create new

This scaffolds a new Copilot Studio definition in your working directory using
your name, description, and instructions, and registers it locally. It does not
create a cloud agent or a synchronization baseline. Use this when you want a
brand new agent and intend to deploy it to Copilot Studio.

```
msagent agent create --agent-type MCSAgent -n "My Agent" -d "Description" -ip "Initial prompt/instructions"
```

`--mcs-agent-type CLI` is the default scaffold for GitHub Copilot-backed agents.
Use `--mcs-agent-type Classic` explicitly for a standard/classic Copilot Studio
agent. The coding assistant you use to author files does not choose the deployed
agent's runtime. Create a named deployment with `msagent deployment create`,
then deploy the definition; see the [deployment guide](Deploying%20your%20Agent%20from%20your%20local%20machine.md).

## Clone

This creates a new copilot studio definition on your working directory using the configuration from the cloud that you cloned it from. This is best used if you want to start from an existing agent that is on copilotstudio.microsoft.com already and makes it easier to use the VS Code extension to work through the definition that was pulled locally

```
msagent agent create --mcs-clone-agent-id <agent-id> --mcs-clone-environment-id <env-id>
```

A cloud clone establishes tracking and a sync baseline and creates a local
`develop` deployment record. A clone into the same environment remains associated
with the source cloud agent: changing its local display name does not fork it.
Inspect `msagent deployment list` before deploying. For a different target
environment, explicitly supply `--environment-id <target-environment-id>` and
review the target's existing agents and connections before deployment.

## Initialize

This registers existing definition files in a local project. Use it after cloning
source from Git or otherwise obtaining files without their local registration.
For a clean checkout without cloud tracking, it creates a disconnected local
record, not a connection to a previously deployed agent or a sync baseline.

```
msagent agent init -an "My Agent" -mas ./path/to/project
```

Initialization does not restore deployment records. To work with an existing
cloud agent, use the cloud-clone operation in a separate empty directory and
compare your source-controlled changes with that connected workspace. See
[source-control guidance](Source%20Control%20with%20msagent%20cli.md).