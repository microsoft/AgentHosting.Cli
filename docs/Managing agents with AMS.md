# Managing agents with AMS

> **Preview.** AMS mode is on while the `AmsMcsAgents` feature control is on, and off otherwise.
> Without it, msagent works only with local projects, exactly as the other guides describe.

AMS mode adds the Agent Management Service (AMS) and a GRS git repository to the local project you
already know. Your agents still live in the project folder: `agent create`, `agent init`,
`agent pull`, and `agent deploy` work on it as without AMS, and `.config` still holds its records.
With AMS mode on, msagent also:

- links the project folder to a GRS repository, which you clone, pull, and push with plain git. Each
  agent, standalone workflow, and component collection is a top-level folder of it;
- keeps an AMS record of each agent and deployment, with the ids `.config` holds;
- deploys a commit of the repository through AMS when you ask (`agent deploy --remote`), and
  promotes it from Dev to Test to Prod;
- commits the edits made in Copilot Studio to the repository (`agent capture`).

Commits are the only versions: no command shows or takes a revision.

## 1. Turn AMS mode on

Turn the `AmsMcsAgents` feature on for the shell:

```
$env:AH_CLI_FEATURES = "AmsMcsAgents"
```

AMS mode needs git on PATH. msagent runs git with its own credential helper, so git signs in to GRS
with your msagent sign-in; `msagent auth login` signs in as usual.

## 2. Create an agent

Create the agent as without AMS, in an empty or new project folder:

```
msagent agent create --agent-type MCSAgent --agent-name "Support agent" --description "Answers questions" `
  --initial-prompt "Answer support questions." --project .\support
```

msagent asks before it changes the folder:

```
Create a GRS repository in environment 'Contoso Dev' for C:\work\support and push agent 'Support agent' to it? [Y/n]
```

Global `--non-interactive` answers yes. msagent then creates the agent as usual and registers it in
AMS, which creates the repository. GRS starts the repository with a `README.md`, so msagent links the
project folder to it as to an existing repository: it checks out `main`, commits the agent's folder on
top, and pushes. A git repository that already has commits keeps them; they replace that first
commit, through a push that git refuses if anyone has pushed to `main` since. The result adds:

```
  repository   <repository-id>, created
  git url      https://<host>/<path>
  commit       1a2b3c4 (pushed to main)
```

To add the agent to an existing repository instead, pass `--repository-id <id>`: an empty folder
becomes a clone of the repository, and a folder that already holds the project is linked to it. In a
folder that is already a clone of a GRS repository, msagent takes that repository, and the next agent
you create there joins it. Cloning an agent or workflow with `--mcs-clone-agent-id`,
`--mcs-clone-agent-schemaname`, or `--mcs-clone-workflow-id` does the same AMS and repository work.

## 3. Join a project someone else registered

One command clones the repository into a new or empty folder and restores the project's records:

```
msagent agent init --repository-id <repository-id> --mcs-agent-source contoso-agents
```

msagent asks before it clones (global `--non-interactive` doesn't ask). It signs git in with its own
credential helper and saves that helper in the clone, so this way needs no `auth setup-git`. To
clone with git instead, sign git in once, clone, and run `agent init` in the clone:

```
msagent auth setup-git
git clone <git-url> contoso-agents
cd contoso-agents
msagent agent init
```

## 4. Register a project you already have

In a project created while AMS mode was off, run `agent init` from the project folder:

```
msagent agent init
```

It registers each MCS agent and standalone workflow `.config` holds, with its deployments, links the
folder to a new repository, or to the one `--repository-id` names, and commits and pushes the agents'
folders. AMS learns which Copilot Studio agent or flow a deployment holds from its next deploy.

## 5. Edit, commit, and push

Edit the agent's files as usual, then share them with git:

```
git add "Support agent"
git commit -m "Answer billing questions"
git push
```

`msagent agent pull` brings the agent's content from Copilot Studio into your folder, as without AMS;
commit and push what it changed to share it.

## 6. Deploy from your folder

```
msagent agent deploy --deployment-name development
```

The deploy runs as without AMS, then tells AMS which commit your folder held: `HEAD` when you have
pushed it, else the newest pushed commit it builds on. AMS treats the deploy as exactly that commit
only when the agent's folders have no uncommitted changes and `HEAD` is pushed; either way, later
captures and remote deploys merge Dev's edits from that commit. When AMS cannot be told, the deploy
stands and a warning says so.

## 7. Deploy a commit through AMS

```
msagent agent deploy --deployment-name development --remote
msagent agent deploy --deployment-name development --remote --commit-id 1a2b3c4
```

`--remote` deploys a commit of the repository, not your folder: `main`'s newest commit for Dev, or
the one `--commit-id` names, full or short. msagent waits for the operation; with `--async` it returns
once AMS accepts it, and `msagent agent operation show --operation-id <id>` follows it. Uncommitted or
unpushed changes in your folder are not in the deploy, and a warning says so.

## 8. Promote to Test and Prod

Create a deployment per stage, as without AMS; msagent creates it in AMS too, and AMS allows one Dev
deployment, in the home environment, and Test and Prod deployments in other environments of the same
tenant, cloud, and geography ([19003](../src/Microsoft.Agents.Hosting.Cli.Docs/ErrorCodes/19003.md)):

```
msagent deployment create --deployment-name test --deployment-type test --environment-id <test-environment-id>
msagent deployment create --deployment-name production --deployment-type prod --environment-id <prod-environment-id>
```

Then promote with remote deploys:

```
msagent agent deploy --deployment-name test --remote
msagent agent deploy --deployment-name production --remote
```

Test takes the commit Dev deployed last, or a commit Dev deployed that `--commit-id` names. Prod
always takes the commit Test deployed last and takes no `--commit-id`. Test and Prod deploys always
publish. `msagent deployment list` shows the commit each deployment deployed last.

## 9. Bring back changes made in Copilot Studio

```
msagent agent capture
git pull
```

`agent capture` commits the edits made in the Copilot Studio agent of the agent's Dev deployment to
`main`, merged with the commits made since Dev's last deploy or capture; `git pull` brings the commit
into your clone.

## 10. Connections and environment variables

The `deployment connection` commands work as without AMS, and AMS keeps no bindings or values. A
remote deploy keeps the bindings and environment-variable values each environment holds, but binds
nothing itself: after the first remote deploy into a new Test or Prod environment, run
`msagent deployment connection update` once for that deployment to bind its references and turn on
its flows.

## 11. See what AMS holds

- `msagent agent show` adds each agent's repository id, folder, and git url, and each deployment's
  last deployed commit.
- `msagent deployment list` and `msagent deployment show` add the commit each deployment deployed
  last and its AMS state.
- `msagent agent list --view ams` lists the AMS records of the environment's agents and workflows,
  deployed or not; `--repository-id` keeps one repository's.

## 12. Delete

`msagent deployment delete` and `msagent agent delete` work as without AMS and delete the AMS records
too. Deleting an agent keeps its repository, the content of its folder there, and your local folder.
Remove the folder from the repository once nobody needs it, so a teammate's `agent init` doesn't take
it for a new agent (section 3).

## Turn AMS mode off

Take `AmsMcsAgents` out of `AH_CLI_FEATURES`, or clear the variable:

```
Remove-Item Env:AH_CLI_FEATURES
```
