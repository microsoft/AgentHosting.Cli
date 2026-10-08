# Working with Source Control 

When using the msagent cli, you are working with a local definition of a copilot studio agent, in YAML (and some json). This local representation also includes connection and sync details.

You can use standard git to commit your changes locally. For more information on git, you can find that here. Git is a tool that tracks every version of your files in a database inside your project folder, letting you save snapshots, compare changes and rewind history entirely on your own machine without needing a network or server.

From there, it is typical practice to connect to a remote source control system such as GitHub, that is an online service that hosts copied of your git repos in the cloud, letting you backup your code, share it with orders and collaborate through features like pull requires or issues.

There are many ways that remote repos like GitHub can be configured, and different ways a company works and collaborates within them. How companies work with branches, PRs, merge conflicts and others are up to the organization, and what is important that the definition of copilot studio can be managed this way.

What is not managed in a repo is the environment configuration and sync details. These contain irrelevant data and data that should be ignored once it is within source control. Ensure the following files are adding to your gitignore:

```
# Local agent/environment binding — tenant, environment, and agent IDs
# specific to whoever ran `agent create`/`agent init` locally
.config/agent.config.json
```
agent.config.json  ties the project to your specific tenant/environment/agent IDs. If a teammate clones the repo and runs agent init  (or  create ), they'll get their own binding — committing yours could cause confusion or accidental cross-environment deploys if someone reuses your IDs against the wrong tenan

The files/folders that should be committed are the definition itself, and the project configuration files that tell the CLI which environment you’re working in, the latest sync files and more can be recreated when they do not exist using msagent agent init. This looks for the definition files and creates the missing project config files that must be available to deploy and use other commands.

## Create a new repo with your copilot studio agent definition

1. Use git locally to save your commits you make, ensuring you create a gitignore file
2. When ready, connect to your chosen remote e.g. GitHub by providing the repo details and additional information it requires
3. Push to your remote when ready
4. The remote should only have your definition files, and not your config files
5. You have now got your saved version of copilot studio definition

## Pull down your copilot studio agent from an existing repo 

1. Connect to your remote using GitHub desktop or similar too (or CLI)
2. Clone your repo into a new working file directory on your local machine
3. Sign into the CLI and connect to the environment you want to establish connection with using `command: msagent env list` and `command: msagent env select <environment-id>`
4. Run msagent agent init `command: msagent agent init -an "<agent-name>" -mas <path-to-existing-project>`
5. Your project files should have been reestablished

## Managing conflicts if the remote Copilot Studio Agent has changed since you last pulled the definition

Where you have a local definition of a deployed agent (at least once), if this deployed agent has been modified, for example by yourself or another team member, on the browser, your local definition and the definition held in the cloud is now out of sync. This scenario may occur with a team who are all working locally and pushing changes to copilot studio using [msagent deploy]

This scenario can be avoided by following these practices:

- Source of truth is always on the remote e.g. GitHub and cloned from there
- Before working or making changes, check the copilot studio cloud for any changes made in the UX that are out of sync with your github source of truth and choose what to do with them e.g. abandon them or update your source of truth
- Following a simple collaboration process of PRs in your source control remote to manage diffs against the changes, which is especially useful when there are multiple people working on the same agent

To update local out-of-sync folders and get your local folder back in sync to work that has been made in the cloud:

1. Have your local out-of-sync working folder open and authenticated via the CLI
2. Ensure you have got your local-out-of-sync working folder backed up with a git commit and to your remote in the scenario you ever need to roll back
3. This path is assuming the source of truth in this scenario is on the cloud and you’re going to overwrite your local changes
4. Ensure your authenticated with the msagent CLI and your environment is selected which has the copilot studio agent you wish to sync with
5. If you have not got an active project with config file setup run `command: msagent agent init -an "<agent-name>" -mas <path-to-existing-project>`
6. Run `command: msagent agent pull -an "<agent-name>" -p <project-path>` to pull down and *overwrite* your local files with the definition from the cloud

Use  git diff  /  git status  to see exactly what differs between your last commit and the pulled cloud state.

The vs code interface provides an improved IDE experience for this scenario which can help manage the diff between cloud remote and local folder differences

### Collaborating in remote source control

Your organization will have it's own process for managing PRs in a repo, interaction between main and branches, and more. [We will publish recommendations on a simplified process for this soon.]

### Notes

- The local  agentId  in  .config/agent.config.json  is a local tracking handle — it can differ after re-init and it's fine.
- Agent init relinks via schema name, restoring the correct  mcsAgentId  pointing at the real cloud agent. No duplicate cloud agent gets created 
- Deployment records (e.g.  dev1 ) are not restored by  init  -  so you have to manually  msagent deployment create -dn dev1 -dt dev  again. Deploying after that correctly targets/updates the same cloud agent though a new deployment id is generated server-side and "Last deployed" history resets.

Important distinction found: this only works reliably if the agent was actually deployed at least once. If it was only ever  agent created (Saved, never deployed)  init  creates a fully disconnected new agent instead.
