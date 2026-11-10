# Using msagent CLI with GitHub Copilot

GitHub copilot (and other agents) is very good at using command line interface (CLI) tools. They often combine the use of multiple CLI tools together with the interaction of capabilities available from running on a local machine e.g. create folder directories and create scripts. 

The CLI tool itself has a help menu and is well understood by agents such as Github Copilot. Together with documentation that is used to ground the agent, and the skills that have been published to be used in scenarios like this, agents can be used to work with Copilot Studio locally from the desktop.

There are a set of skills that can be used by your agent, along with an agent.md file to assist you when working with your agent with the CLI

- Access the skills [here](url)

## Using natural language

Coding agents are driven by natural language, and the CLI works well with local based coding agents that have access to other tools that help advanced makers and developers in their flow of work. Coding agents do best when they have an initial grounding, typically through an agents.md file or similar, and a set of optional skills to assist in the use of CLIs which can help guide them to a specific outcome or task. 

There is a set of skills available for the msagent CLI which will be updated regularly and is designed to be included at the project or global level of your coding agent, using only what you require or want so your coding agent can access them. 

The agent.md file contains key information, together with examples that can be used by the agent when needed.

You can ask your agent:

```
"Create a Copilot Studio agent that can help users submit internal helpdesk tickets with SharePoint, and sends an automatic email once a new ticket is received to the person creating it"
```

# Lifecycle

The CLI's capabilities (verbs) are based around the lifecycle of a Copilot Studio agent

- Create: Create, Clone and Initialize depending on where you’re starting from (see [New Agents](New%20Agents.md))
- Edit: Humans and Agents modify the files in the project structure, and the CLI helps with managing connections (see [Editing Agents](Editing%20Agents.md) and [Working with connections, locally](Working%20with%20connections%2C%20locally.md))
- Deploy: Deploy the agent as a draft and published agent (see [Deploying your Agent](Deploying%20your%20Agent%20from%20your%20local%20machine.md))
- Test: Test the agent and can be used to run custom local evals (see [Testing your Agent](Testing%20your%20Agent%20from%20your%20local%20machine.md))
- Eval (just landed): Run configured test sets for Copilot Studio evaluations

## Golden path recipes

Below are recipes that agents can use to achieve a specific outcome 

### Create an agent and deploy it

```
# Authenticate
msagent auth create

# Select target environment
msagent env list
msagent env select <environment-id>

# Create the agent project
msagent agent create -n "<agent-name>" -d "<description>" -ip "<initial-prompt>"

# Deploy the agent
msagent agent deploy -p <project-path> -dn <deployment-name> --publish
```

### Create a variant of an existing agent for experimentation

```
# Authenticate
msagent auth create

# Select target environment
msagent env list
msagent env select <environment-id>

# Clone the existing agent into a new local project
msagent agent create --mcs-clone-agent-id <source-agent-id> -n "<variant-name>" -p <project-path>

# Deploy the variant to an experimental deployment slot
msagent agent deploy -p <project-path> -dn <experimental-deployment-name>
```

### Your deployed agent is giving the wrong answers or errors 

```
# Check deployment details/status
msagent deployment show -an "<agent-name>" -dn <deployment-name>

# Pull the live remote content locally to inspect what's deployed
msagent agent pull -an "<agent-name>" -p <project-path>

# Review the local agent record/config
msagent agent show -p <project-path>

# After fixing locally, redeploy
msagent agent deploy -p <project-path> -dn <deployment-name> --overwrite --publish
```

# Guardrails

This section includes some useful areas of the CLI to take note of when using.

Most of the CLI commands do not cost the user copilot studio credits or consumption. Authenticated users with the CLI *do* need to have access to a copilot studio environment and the required permissions to operate an agent. This is because you are building the agent locally on your machine and your either using the CLI directly or the coding agent is being used to build the agent which typically has costs.

The CLI does allow your agent to be tested locally, which requires establishing a connection to the Copilot Studio and sends it messages using your consented authentication profile you have signed in with for the CLI, and accepted the permissions on the browser, and *using the agent in copilot studio* does cost money depending on your configured subscription or license you have setup in your tenant. This is the case regardless of method your using to test, human or agent, agent playground or non-interactively in another terminal/window,

```
msagent agent test (coming soon)
```

The CLI's deploy command can be irreversible if you use add the overwrite tags, before deploying and if you use the overwrite flag, ensure you have a backup of the copy you’re overwriting.

When you are deploying, the CLI detects drift via sync state. If cloud content changed since your last local sync,  deploy refuses by default (only a dev deployment enforces this) — you must pass the flag  --overwrite  to force-push local over cloud. There's no merge; it's last-write-wins once you opt in with --overwrite

```
 msagent agent deploy - specifically its  -ow, --overwrite  flag is what controls the conflict/drift behavior.
```

The CLI has a delete command which deletes your agent on the cloud, whilst retaining local records. It does require the a user in interactive mode to confirm the delete by typing the agent name. In non-interactive mode it does not require this extra step.

```
msagent agent delete  — deletes the entire MCS agent's cloud resources and all records (local files untouched).
msagent deployment delete  — deletes just one deployment slot; add  --delete-deployed-mcs-agent  to also remove the Copilot Studio agent it targets
```

# Setting up your coding agent: skills and agent.md

Skills extend the msagent CLI with custom, repeatable instructions. Each skill is a folder containing a `SKILL.md` file (plus any optional supporting scripts or resources). A skill can live in one of two locations, depending on scope:

- **Project skills:** `.github/skills/<skill-name>/SKILL.md`
  Stored in the repository itself, so they're version-controlled and shared with everyone who clones the repo. Use this for team-wide or repo-specific workflows (e.g. standardized testing, deployment, or debugging steps).

- **Global (personal) skills:** `~/.copilot/skills/<skill-name>/SKILL.md`
  Stored in your user home directory, so they're available across all your projects but not shared with others by default. Use this for personal, reusable workflows you want everywhere.

Each `SKILL.md` starts with YAML frontmatter that defines the skill's `name` and `description`, followed by markdown instructions describing what the skill does and when Copilot should trigger it:

```markdown
---
name: my-skill-name
description: Short description of what this skill does and when to use it.
---

Instructions for Copilot go here.
```

You can get the skills from the repo [here](url), or run the following command to open an interactive picker and install skills locally:

```
npx skills add <org/repo>
```

## Error recovery

[coming soon]

## Additional coding agent instructions

Additional coding agent instructions have been provided [here](Additional%20coding%20agent%20instructions.md)

# Command reference

See the full [MSAgent CLI command reference](../src/Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md).