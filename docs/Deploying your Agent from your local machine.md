# Deployment


Once you are ready with your agent definition from your local machine, you are ready to deploy this to the Copilot Studio cloud, so you can see it at copilotstudio.microsoft.com

## 1. Create a deployment slot (first time only)

Your local agent is only a definition on your machine. A deployment is the named target (for example `dev`) that tells the CLI which Copilot Studio environment and agent to send that definition to, and it keeps track of what has been deployed there. This is why a deployment must exist before you can deploy, and it lets you deploy the same local agent to more than one target.

Before deploying, a named deployment slot must exist for your agent:

```
msagent deployment create --deployment-name "dev" --deployment-type dev
```

## 2. Deploy

```
msagent agent deploy --agent-name "name" --deployment-name "dev" --publish
```

This sends the agent definition you have created or modified locally into the connected environment. Once completed, the agent can be seen on copilotstudio.microsoft.com.

You can deploy your agent into draft status by omitting the `--publish` flag. At this time, some functionality, such as testing, does require the agent to be published.

Once published, it is available to be seen by other clients (including your laptop for testing, and Microsoft Teams) and usable by the Copilot Studio runtime.

## Overwriting changes made in the cloud

When you deploy, the CLI checks the sync state to detect drift. If the agent in Copilot Studio has changed since you last synced your local copy (for example, someone edited it in the browser), the deploy is refused by default. This check is enforced for `dev` deployments.

To replace the cloud version with your local definition, run the deploy again with the `--overwrite` flag (`-ow`):

```
msagent agent deploy --agent-name "name" --deployment-name "dev" --overwrite --publish
```

Be careful with `--overwrite`:

- There is no merge. It is last-write-wins, and the cloud changes are replaced by your local definition.
- It can be irreversible. Before using it, make sure you have a backup of what you are overwriting, such as a git commit, or run `msagent agent pull` to inspect the cloud version first.
- If you want to keep the cloud changes instead, pull them down rather than overwriting. See [Source Control with msagent cli](Source%20Control%20with%20msagent%20cli.md) for how to bring your local copy back in sync.
