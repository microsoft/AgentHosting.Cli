# Deployment


Once you are ready with your agent definition from your local machine, you are ready to deploy this to the Copilot Studio cloud, so you can see it at copilotstudio.microsoft.com

## 1. Create a deployment slot (first time only)

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
