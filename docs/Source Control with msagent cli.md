# Working with Source Control 

When using the msagent cli, you are working with a local definition of a copilot studio agent, in YAML (and some json). This local representation also includes connection and sync details.

You can use standard git to commit your changes locally. For more information on git, you can find that here. Git is a tool that tracks every version of your files in a database inside your project folder, letting you save snapshots, compare changes and rewind history entirely on your own machine without needing a network or server.

From there, it is typical practice to connect to a remote source control system such as GitHub, that is an online service that hosts copied of your git repos in the cloud, letting you backup your code, share it with orders and collaborate through features like pull requires or issues.

There are many ways that remote repos like GitHub can be configured, and different ways a company works and collaborates within them. How companies work with branches, PRs, merge conflicts and others are up to the organization, and what is important that the definition of copilot studio can be managed this way.

What is not managed in a repo is the environment configuration and local sync state. These files are specific to you and your environment, and they should be ignored once the agent is within source control. Ensure the following are added to your `.gitignore`:

```
# Local agent/environment binding — tenant, environment, and agent IDs
# specific to whoever ran `agent create`/`agent init` locally
.config/agent.config.json

# Local sync state and caches (cloud snapshot, change token, connection details)
.mcs/
```

- **`.config/agent.config.json`** ties the project to your specific tenant, environment and agent IDs, and holds your deployment records. If a teammate clones the repo and runs `agent init` (or `create`), they get their own binding. Committing yours could cause confusion or accidental cross-environment deploys if someone reuses your IDs against the wrong tenant.
- **`.mcs/`** (inside the agent folder, for example `<agent-folder>/.mcs/`) holds local sync state and caches such as `botdefinition.json`, `changetoken.txt`, `conn.json`, `.connections-cache.json` and `workflow-state.json`. The CLI creates it with its own `.gitignore`, so it is ignored by default, but adding it to your repo's `.gitignore` as well keeps it explicit.

The files and folders that should be committed are the agent definition itself, for example:

- `settings.mcs.yml`
- `agent.sync.yaml`
- Topics, `capabilities/` (tools), `knowledge/` and `workflows/`, where present
- `infrastructure/connections/*.sync.yaml` (the connection references, not the connections themselves)

The local project registration can be recreated by running `msagent agent init` against the existing definition files. This registers a new, disconnected local record; it does not restore a connection to a previously deployed cloud agent or recreate its sync baseline. Commands that require a cloud connection, such as `agent pull`, cannot run on that disconnected record.

## Create a new repo with your copilot studio agent definition

1. Use git locally to save your commits you make, ensuring you create a gitignore file
2. When ready, connect to your chosen remote e.g. GitHub by providing the repo details and additional information it requires
3. Push to your remote when ready
4. The remote should only have your definition files, and not your config files
5. You have now got your saved version of copilot studio definition

## Clone your Copilot Studio agent from an existing repo

1. Connect to your remote using GitHub desktop or similar too (or CLI)
2. Clone your repo into a new working file directory on your local machine
3. Sign into the CLI and connect to the environment you want to establish connection with using `command: msagent env list` and `command: msagent env select <environment-id>`
4. Run msagent agent init `command: msagent agent init -an "<agent-name>" -mas <path-to-existing-project>`
5. Your local registration has been reestablished, but it is disconnected from Copilot Studio even if this definition was deployed previously.

To work with an existing cloud agent, clone it through the supported CLI operation into a new, empty project directory:

```
msagent agent create --agent-type MCSAgent --mcs-clone-agent-id "<cloud-agent-id>" --mcs-clone-environment-id "<environment-id>" --project "<new-empty-project-directory>"
```

The clone establishes cloud tracking and a sync baseline. Compare the source-controlled definition with that connected workspace and review any changes before deploying. Do not overwrite an occupied workspace or assume that a matching schema name reconnects an initialized record.

## Managing conflicts if the remote Copilot Studio Agent has changed since you last pulled the definition

Where you have a local definition of a deployed agent (at least once), if this deployed agent has been modified, for example by yourself or another team member, on the browser, your local definition and the definition held in the cloud is now out of sync. This scenario may occur with a team who are all working locally and pushing changes to Copilot Studio using `msagent agent deploy`.

This scenario can be avoided by following these practices:

- Source of truth is always on the remote e.g. GitHub and cloned from there
- Before working or making changes, check the copilot studio cloud for any changes made in the UX that are out of sync with your github source of truth and choose what to do with them e.g. abandon them or update your source of truth
- Following a simple collaboration process of PRs in your source control remote to manage diffs against the changes, which is especially useful when there are multiple people working on the same agent

To update local out-of-sync folders and get your local folder back in sync to work that has been made in the cloud:

1. Have your local out-of-sync working folder open and authenticated via the CLI
2. Ensure you have got your local-out-of-sync working folder backed up with a git commit and to your remote in the scenario you ever need to roll back
3. This path is assuming the source of truth in this scenario is on the cloud and you’re going to overwrite your local changes
4. Ensure you are authenticated with the msagent CLI. Pull uses the environment and agent in the workspace's established cloud connection, not the current `env select` value.
5. Confirm that the project is connected to the intended cloud agent. If it is disconnected, use the cloud-clone procedure above; running `agent init` does not reconnect it.
6. In the connected project, run `command: msagent agent pull -an "<agent-name>" -p <project-path>` to retrieve the cloud definition, then review any reported conflicts before continuing.

Use  git diff  /  git status  to see exactly what differs between your last commit and the pulled cloud state.

The vs code interface provides an improved IDE experience for this scenario which can help manage the diff between cloud remote and local folder differences

### Collaborating in remote source control

Agree on your organization's process for branches, pull requests, cloud edits,
and deployments. Review definition changes in source control and make one
person or pipeline responsible for each target's deployment at a time. A Git
merge does not resolve independent cloud drift or retarget local deployment
records.

### Notes

- The local  agentId  in  .config/agent.config.json  is a local tracking handle — it can differ after re-init and it's fine.
- `agent init` registers a new disconnected record whether or not the definition was deployed previously. It does not restore `mcsAgentId` by schema name or create a cloud agent. A subsequent `agent pull` fails with `workspace-disconnected` until the project has a supported cloud connection.
- Deployment records are not restored by `init`. Do not assume that creating a deployment for an initialized record will target the old cloud agent. For an existing cloud agent, use the clone-generated tracking and inspect `msagent deployment list` before selecting a deployment.
