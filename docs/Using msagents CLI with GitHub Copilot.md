# Using msagent CLI with GitHub Copilot

GitHub copilot (and other agents) is very good at using command line interface (CLI) tools. They often combine the use of multiple CLI tools together with the interaction of capabilities available from running on a local machine e.g. create folder directories and create scripts. 

The CLI tool itself has a help menu and is well understood by agents such as Github Copilot. Together with documentation that is used to ground the agent, and the skills that have been published to be used in scenarios like this, agents can be used to work with Copilot Studio locally from the desktop.

You can provide optional compatible skills and an agent.md file to assist your coding agent with the CLI.

- Start with [agent.md](agent.md). If your organization supplies compatible
  skills, review and install only those needed for the task; skills are optional.

## Using natural language

Coding agents are driven by natural language, and the CLI works well with local based coding agents that have access to other tools that help advanced makers and developers in their flow of work. Coding agents do best when they have an initial grounding, typically through an agents.md file or similar, and a set of optional skills to assist in the use of CLIs which can help guide them to a specific outcome or task. 

Compatible skills can be included at the project or global level of your coding
agent. Their availability and updates depend on the publisher; review the source
and use only the skills you need.

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
- Test: Invoke the published agent with live queries or Playground; custom scripts can check returned answers (see [Testing your Agent](Testing%20your%20Agent%20from%20your%20local%20machine.md)).
- Evaluate: Run Copilot Studio test sets and service-side grading with `msagent agent eval`. This is separate from locally scripted checks; see the [evaluation reference](../src/Microsoft.Agents.Hosting.Cli.Docs/Command_Agent_Eval.md).

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
msagent agent create --agent-type MCSAgent -n "<agent-name>" -d "<description>" -ip "<initial-prompt>" --project <project-path>

# Create the local deployment target and upload a draft
msagent deployment create -p <project-path> -dn develop --deployment-type dev
msagent agent deploy -p <project-path> -dn develop
```

Review the draft and connection/workflow diagnostics. Evaluate it if appropriate,
then publish only after the user approves:

```text
msagent agent eval run -p <project-path> --generate 3 --name "Smoke tests" --watch
msagent agent deploy -p <project-path> -dn develop --publish
```

### Work with an existing agent in another environment

Cloning into the same environment remains associated with the source cloud
agent. Renaming a local clone does not fork it. To experiment in a different
environment, explicitly distinguish the source from the target:

```
# Authenticate
msagent auth create

# Inspect available environments
msagent env list

# Clone from the source into a new empty local project, targeting a different environment
msagent agent create --agent-type MCSAgent --mcs-clone-agent-id <source-agent-id> --mcs-clone-environment-id <source-environment-id> --environment-id <target-environment-id> -p <project-path>

# Inspect the clone-generated target, then deploy a draft
msagent deployment list -p <project-path>
msagent agent deploy -p <project-path> -dn develop
```

Use distinct source and target environment IDs. Review the target's existing
agent identities and connection requirements first; deployment can adopt or
replace content matching the target identity. Do not add `--overwrite` to bypass
an unexpected ownership or drift error. This recipe is not a same-environment
forking command.

### Your deployed agent is giving the wrong answers or errors 

```
# Inspect the local target record (not a live health check)
msagent deployment show -p <project-path> -an "<agent-name>" -dn <deployment-name>

# Inspect the local agent record/config
msagent agent show -p <project-path>

# After reviewing and fixing source, upload a draft without forcing overwrite
msagent agent deploy -p <project-path> -dn <deployment-name>
```

If you need to bring cloud edits into the project, first commit or back up local
edits, then run `msagent agent pull -an "<agent-name>" -p <project-path>` in the
connected workspace. Pull changes local files; it is not read-only inspection.
If deployment reports drift, decide which changes to retain before considering
`--overwrite`. Publication is a separate approval, not a default recovery step.

# Guardrails

This section includes some useful areas of the CLI to take note of when using.

Most of the CLI commands do not cost the user copilot studio credits or consumption. Authenticated users with the CLI *do* need to have access to a copilot studio environment and the required permissions to operate an agent. This is because you are building the agent locally on your machine and your either using the CLI directly or the coding agent is being used to build the agent which typically has costs.

The CLI does allow your agent to be tested locally, which requires establishing a connection to the Copilot Studio and sends it messages using your consented authentication profile you have signed in with for the CLI, and accepted the permissions on the browser, and *using the agent in copilot studio* does cost money depending on your configured subscription or license you have setup in your tenant. This is the case regardless of method your using to test, human or agent, agent playground or non-interactively in another terminal/window,

```
msagent test ask --project <project-path> --deployment-name develop --query "What can you help me with?"
```

Native evaluations also consume Copilot Credits. The coding assistant's own
subscription and usage charges are separate from Copilot Studio billing.

The CLI's deploy command can be irreversible if you use add the overwrite tags, before deploying and if you use the overwrite flag, ensure you have a backup of the copy you’re overwriting.

When you are deploying, the CLI detects drift via sync state. If cloud content changed since your last local sync,  deploy refuses by default (only a dev deployment enforces this) — you must pass the flag  --overwrite  to force-push local over cloud. There's no merge; it's last-write-wins once you opt in with --overwrite

```
 msagent agent deploy - specifically its  -ow, --overwrite  flag is what controls the conflict/drift behavior.
```

The agent-delete command removes the selected cloud agent and its local
registration/deployment records, but leaves source files on disk. A deployment
selector chooses which cloud copy is deleted; without one, the workspace
connection or home target is used. If the workspace is missing, only local
records are removed. Interactive deletion requires typing the agent's display
name; `--non-interactive` supplies consent rather than making deletion harmless.
Obtain approval for the specific target and effects first.

```
msagent agent delete  — deletes the entire MCS agent's cloud resources and all records (local files untouched).
msagent deployment delete  — deletes just one deployment slot; add  --delete-deployed-mcs-agent  to also remove the Copilot Studio agent it targets
```

# Setting up your coding agent: skills and agent.md

Skills give a coding assistant repeatable instructions for msagent workflows;
they do not add CLI commands or change the deployed runtime. Each skill is a
folder containing a `SKILL.md` file and optional scripts or resources. For GitHub
Copilot, common locations include the following; other assistants can use
different discovery and installation rules:

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

Use a reviewed skill source supplied by your organization and the installation
method supported by your coding assistant. Do not execute a placeholder
installation command or assume every assistant reads the same skill directory.
The [agent.md](agent.md) file can be attached directly without installing skills.

## Error recovery

Read the error kind, numeric error code, and remediation before retrying. Use the
[error code reference](../src/Microsoft.Agents.Hosting.Cli.Docs/ErrorCodes.md)
to distinguish sign-in, target-selection, drift, connection, and service failures.
After a partial mutation, inspect the target before retrying; do not automatically
replay queries or add `--overwrite`, publish, or delete resources to clear an error.

## Additional coding agent instructions

Additional coding agent instructions have been provided [here](Additional%20coding%20agent%20instructions.md)

# Command reference

See the full [MSAgent CLI command reference](../src/Microsoft.Agents.Hosting.Cli.Docs/ReadMe.md).