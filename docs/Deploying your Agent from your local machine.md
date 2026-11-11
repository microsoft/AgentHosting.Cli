# Deployment


Once you are ready with your agent definition from your local machine, you are ready to deploy this to the Copilot Studio cloud, so you can see it at copilotstudio.microsoft.com

| Operation | Local or cloud effect |
|---|---|
| `deployment create` | Saves a named target in local project configuration; it does not create the cloud agent. |
| `agent deploy` | Uploads the working tree to the target and updates the conversational-agent draft. |
| `agent eval run` | Runs a Copilot Studio evaluation against the cloud agent. |
| `agent deploy --publish` | Uploads and publishes the agent after blocking content/workflow issues are resolved. |
| `test ask` / `test playground` | Invokes the published agent through live service APIs. |

## 1. Create a deployment slot (first time only)

Your local agent is only a definition on your machine. A deployment is the named target (for example `develop`) that tells the CLI which Copilot Studio environment and agent to send that definition to, and it keeps track of what has been deployed there. This is why a deployment must exist before you can deploy, and it lets you deploy the same local agent to more than one target.

Before deploying, a named deployment slot must exist for your agent:

```
msagent deployment create --deployment-name "develop" --deployment-type dev
```

Run this in the registered project, or supply `--project <project-path>`.
Cloud cloning already creates a `develop` record; inspect `msagent deployment list`
before adding one. A deployment's stored environment determines its destination;
changing `env select` does not retarget an existing record.

## 2. Deploy

```
msagent agent deploy --deployment-name develop
```

This sends the agent definition you have created or modified locally into the connected environment. Once completed, the agent can be seen on copilotstudio.microsoft.com.

Omitting `--publish` updates the conversational-agent draft. Draft evaluation is
available before publishing; live terminal and Playground testing require the
published agent. Attached workflows can activate during draft deployment or
connection binding, so inspect their state and obtain approval for their effects.

When the changes are ready, publish:

```text
msagent agent deploy --deployment-name develop --publish
```

Publication does not configure channels, share the agent, or grant access.
Configure channels such as Teams separately and ensure intended users can access
the published agent. Inspect any deployment diagnostics: a recorded deployment
is not proof that every connection is ready or every workflow has activated.

## Deploy to test and production

Create separate records with explicit target environments, then deploy the
reviewed working tree:

```text
msagent deployment create --deployment-name integration --deployment-type test --environment-id <test-environment-id>
msagent agent deploy --deployment-name integration
msagent deployment create --deployment-name production --deployment-type prod --environment-id <production-environment-id>
msagent agent deploy --deployment-name production
```

Each environment needs its own authorized connections and bindings. Review
diagnostics and verify the target before adding `--publish`. Deploy uploads the
current local files, not a Git commit selected through `--commit-id`. Only one
deployment record per agent/environment is allowed.

## Overwriting changes made in the cloud

When you deploy, the CLI checks the sync state to detect drift. If the agent in Copilot Studio has changed since you last synced your local copy (for example, someone edited it in the browser), the deploy is refused by default. This check is enforced for `dev` deployments.

To replace the cloud version with your local definition, run the deploy again with the `--overwrite` flag (`-ow`):

```
msagent agent deploy --deployment-name develop --overwrite
```

Be careful with `--overwrite`:

- There is no merge. It is last-write-wins, and the cloud changes are replaced by your local definition.
- Preserve both local and cloud changes before replacing either copy. Commit or back up local edits before using `agent pull`; pulling changes local source and is not a read-only inspection. A local Git commit alone does not back up newer cloud edits.
- If you want to keep the cloud changes instead, pull them down rather than overwriting. See [Source Control with msagent cli](Source%20Control%20with%20msagent%20cli.md) for how to bring your local copy back in sync.

Test and production deployments do not provide the same development drift guard.
An overwrite is not a merge or an automatic recovery strategy. To restore an
earlier definition, restore reviewed source from Git, deploy it to the intended
target, resolve diagnostics, then publish only when ready.
