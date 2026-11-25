using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.Ams;

/// <summary>The code-agent lifecycle on the Agent Management Service, driven through msagent.</summary>
public static class AmsCodeAgentScenarios
{
    private static readonly ScenarioBuilder Builder = new(AmsSteps.Feature);

    private static readonly Capability[] Ams = [Capability.Auth, Capability.Ams];

    private static readonly Capability[] AmsCreate = [Capability.Auth, Capability.AmsCreate];

    private static readonly Capability[] AmsPackage = [Capability.Auth, Capability.AmsPackage];

    private static readonly Capability[] AmsHosted = [Capability.Auth, Capability.AmsHosted];

    private static readonly Capability[] AmsProd = [Capability.Auth, Capability.AmsProd];

    private static readonly TimeSpan BotBudget = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan HostedBudget = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan CommitListLag = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan WriteLag = TimeSpan.FromSeconds(60);

    private static readonly string[] AgentIdentity = ["agentId", "displayName", "agentType", "tenantId", "environmentId"];

    private static readonly string[] ScenarioChannelTypes = ["webchat", "directline", "msteams", "telegram"];

    /// <summary>Every Agent Management Service scenario, in the order the run executes them.</summary>
    public static IEnumerable<Scenario> All() => [.. Agents(), .. DeploymentRecords(), .. Deploys(), .. Commits(), .. Channels(), .. OAuthConnections(), .. Deletes()];

    private static IEnumerable<Scenario> Agents()
    {
        yield return Builder.Make("AC-01", "agent create makes a code agent in the home environment", AmsCreate, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var agent = await AmsSteps.PooledAgentAsync(context).ConfigureAwait(false);

            var shown = await AmsSteps.ReadAsync(context, ["agent", "show", "--agent-id", agent.AgentId]).ConfigureAwait(false);

            context.Assert.ExitZero(shown, "agent show");
            var root = AmsSteps.Root(shown);
            context.Assert.Equal(agent.AgentId, AmsOutput.Text(root, "agent", "agentId"), "the id agent create returned");
            context.Assert.Equal(agent.Name, AmsOutput.Text(root, "agent", "displayName"), "the name agent create was given");
            context.Assert.Equal("CodeAgent", AmsOutput.Text(root, "agent", "agentType"), "a code agent");
            context.Assert.Equal(AmsSteps.Services(context).HomeEnvironmentId, AmsOutput.Text(root, "agent", "environmentId"), "in the home environment");
        });

        yield return Builder.Make("AC-02", "agent show by id and by name agree", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var agent = await AmsSteps.PooledAgentAsync(context).ConfigureAwait(false);

            var byId = await AmsSteps.ReadAsync(context, ["agent", "show", "--agent-id", agent.AgentId]).ConfigureAwait(false);
            var byName = await AmsSteps.ReadAsync(context, ["agent", "show", "--agent-name", agent.Name]).ConfigureAwait(false);

            context.Assert.ExitZero(byId, "agent show --agent-id");
            context.Assert.ExitZero(byName, "agent show --agent-name");

            foreach (var property in AgentIdentity)
            {
                context.Assert.Equal(AmsOutput.Text(AmsSteps.Root(byId), "agent", property), AmsOutput.Text(AmsSteps.Root(byName), "agent", property), $"both show the same {property}");
            }
        });

        yield return Builder.Make("AC-03", "agent list --agent-type CodeAgent lists the run's agent", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var agent = await AmsSteps.PooledAgentAsync(context).ConfigureAwait(false);

            var listed = await AmsSteps.ReadAsync(context, ["agent", "list", "--agent-type", "CodeAgent"]).ConfigureAwait(false);

            context.Assert.ExitZero(listed, "agent list");
            var agents = AmsOutput.Items(AmsSteps.Root(listed), "agents");
            var listedAgent = agents.Where(candidate => AmsSteps.SameId(AmsOutput.Text(candidate, "agentId"), agent.AgentId)).ToArray();
            context.Assert.True(listedAgent.Length == 1, "the run's agent is listed once");
            context.Assert.Equal(agent.Name, AmsOutput.Text(listedAgent[0], "displayName"), "under its name");
            context.Assert.True(agents.All(candidate => AmsOutput.Text(candidate, "agentType") == "CodeAgent"), "every listed agent is a code agent");
        });

        yield return Builder.Make("AC-04", "agent update renames an agent and sets its description, then renames it back", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var agent = await AmsSteps.CreateAgentAsync(context, "ac04").ConfigureAwait(false);
            var newName = AmsSteps.Name(context, "ac04-renamed");
            var description = $"msagent e2e run {context.RunSuffix}";

            var renamed = await AmsSteps.RunAsync(context, ["agent", "update", "--agent-id", agent.AgentId, "--new-name", newName, "--description", description]).ConfigureAwait(false);

            context.Assert.ExitZero(renamed, "agent update --new-name --description");
            context.Assert.Equal("agent-updated", AmsOutput.Text(AmsSteps.Root(renamed), "status"), "reports the update");
            await ExpectAgentAsync(context, agent, newName, description, "after the rename").ConfigureAwait(false);
            context.Assert.RefusedWith(await AmsSteps.ReadAsync(context, ["agent", "show", "--agent-name", agent.Name]).ConfigureAwait(false), AmsOutput.AgentNotFound, "the old name");

            var restored = await AmsSteps.RunAsync(context, ["agent", "update", "--agent-id", agent.AgentId, "--new-name", agent.Name]).ConfigureAwait(false);

            context.Assert.ExitZero(restored, "agent update --new-name back");
            await ExpectAgentAsync(context, agent, agent.Name, description, "after the rename back, which keeps the description").ConfigureAwait(false);
        });

        yield return Builder.Make("AC-05", "a new agent with a name in use, in another case, is refused with the holder's id", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var holder = await AmsSteps.PooledAgentAsync(context).ConfigureAwait(false);
            var name = holder.Name.ToUpperInvariant();

            var created = await AmsSteps.RunAsync(context, AmsSteps.CreateAgentArguments(name)).ConfigureAwait(false);

            AmsSteps.RecordUnexpectedAgent(context, created, name);
            context.Assert.RefusedWith(created, "agent-name-in-use", "the create");
            context.Assert.Contains(created, holder.AgentId, "the refusal names the holder");

            var listed = await AmsSteps.ReadAsync(context, ["agent", "list", "--agent-type", "CodeAgent"]).ConfigureAwait(false);

            context.Assert.ExitZero(listed, "agent list");
            context.Assert.True(AmsOutput.Items(AmsSteps.Root(listed), "agents").Count(candidate => string.Equals(AmsOutput.Text(candidate, "displayName"), holder.Name, StringComparison.OrdinalIgnoreCase)) == 1, "no second agent has the name");
        });

        yield return Builder.Make("AC-06", "agent create of a code agent without --language is a usage error", Ams, SafetyTier.ReadOnly, run: async context =>
        {
            var name = AmsSteps.Name(context, "ac06");

            var created = await AmsSteps.RunAsync(context, ["agent", "create", "--agent-type", "CodeAgent", "--agent-name", name]).ConfigureAwait(false);

            AmsSteps.RecordUnexpectedAgent(context, created, name);
            context.Assert.Refused(created, 2, "--language and --language-version are required for a CodeAgent.", "agent create without --language");
        });
    }

    private static IEnumerable<Scenario> DeploymentRecords()
    {
        yield return Builder.Make("AC-10", "deployment create records a Dev deployment at a dev tunnel endpoint, not yet deployed and without a bot", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var record = await AmsSteps.PooledDevRecordAsync(context).ConfigureAwait(false);
            var created = record.Created;

            context.Assert.Equal("Dev", created.DeploymentType, "a Dev deployment");
            context.Assert.Equal(AmsSteps.Services(context).HomeEnvironmentId, created.TargetEnvironmentId, "in the home environment");
            context.Assert.Equal(record.ApplicationId, created.EntraId, "bound to its own App Registration");
            context.Assert.Equal("NotDeployed", created.State, "not deployed yet");
            context.Assert.Equal("dev-endpoint", created.Kind, "running a dev endpoint");
            context.Assert.Equal(record.Endpoint, created.RequestedEndpoint, "the endpoint it requests");
            context.Assert.Equal(null, created.Endpoint, "no live endpoint");
            context.Assert.True(created.EndpointChangePending, "the requested endpoint waits for agent deploy");
            context.Assert.Equal(null, created.BotName, "no bot");
        });

        yield return Builder.Make("AC-11", "deployment show by id and by name and deployment list agree", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var (agent, record) = await AmsSteps.PooledRecordAsync(context).ConfigureAwait(false);

            var byId = await AmsSteps.ShowDeploymentAsync(context, agent, record).ConfigureAwait(false);
            var byName = await AmsSteps.ReadAsync(context, ["deployment", "show", .. AmsSteps.ByName(agent, record)]).ConfigureAwait(false);
            var listed = await AmsSteps.ReadAsync(context, ["deployment", "list", "--agent-id", agent.AgentId]).ConfigureAwait(false);

            context.Assert.ExitZero(byId, "deployment show by id");
            context.Assert.ExitZero(byName, "deployment show by name");
            context.Assert.ExitZero(listed, "deployment list");
            var listedRecord = AmsOutput.Items(AmsSteps.Root(listed), "deployments").Where(candidate => AmsSteps.SameId(AmsOutput.Text(candidate, "deploymentId"), record.DeploymentId)).Select(AmsDeployment.From).ToArray();
            context.Assert.True(listedRecord.Length == 1, "the list holds the record once");
            context.Assert.Equal(AmsSteps.Deployment(byId).ToString(), AmsSteps.Deployment(byName).ToString(), "show by id and by name agree");
            context.Assert.Equal(AmsSteps.Deployment(byId).ToString(), listedRecord[0].ToString(), "show and list agree");
            context.Assert.True(AmsOutput.Find(AmsSteps.Root(listed), "keptBots") is { ValueKind: JsonValueKind.Array }, "the list carries the agent's kept bots");
        });

        yield return Builder.Make("AC-12", "deployment update renames a deployment and back, and refuses a name another deployment holds", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var (agent, record) = await AmsSteps.PooledRecordAsync(context).ConfigureAwait(false);
            var other = await AmsSteps.CreateDeploymentAsync(context, agent, "ac12-other", AmsSteps.Services(context).TestEnvironmentId, "dev", Guid.NewGuid().ToString(), AmsSteps.DevEndpoint(context, "ac12-other")).ConfigureAwait(false);
            var newName = AmsSteps.Name(context, "ac12-renamed");

            var renamed = await RenameDeploymentAsync(context, agent, record, newName).ConfigureAwait(false);

            context.Assert.ExitZero(renamed, "deployment update --new-name");
            context.Assert.Equal(newName, AmsSteps.Deployment(renamed).DeploymentName, "the update reports the new name");
            var shownByNewName = await AmsSteps.ReadAsync(context, ["deployment", "show", "--agent-id", agent.AgentId, "--deployment-name", newName]).ConfigureAwait(false);
            context.Assert.ExitZero(shownByNewName, "deployment show by the new name");
            context.Assert.Equal(record.DeploymentId, AmsSteps.Deployment(shownByNewName).DeploymentId, "the new name names the deployment");
            context.Assert.RefusedWith(await AmsSteps.ReadAsync(context, ["deployment", "show", "--agent-id", agent.AgentId, "--deployment-name", record.DeploymentName]).ConfigureAwait(false), "deployment-not-found", "the old name");

            var restored = await RenameDeploymentAsync(context, agent, record, record.DeploymentName).ConfigureAwait(false);

            context.Assert.ExitZero(restored, "deployment update --new-name back");
            context.Assert.Equal(record.DeploymentName, AmsSteps.Deployment(restored).DeploymentName, "the old name is back");

            var clash = await RenameDeploymentAsync(context, agent, record, other.DeploymentName.ToUpperInvariant()).ConfigureAwait(false);

            context.Assert.RefusedWith(clash, "deployment-name-in-use", "the name of another deployment, in another case");
            context.Assert.Contains(clash, other.DeploymentId, "the refusal names the holder");
        });

        yield return Builder.Make("AC-13", "deployment update --endpoint-url of a record not yet deployed changes only the endpoint it requests", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var (agent, record) = await AmsSteps.PooledRecordAsync(context).ConfigureAwait(false);
            var shownBefore = await AmsSteps.ShowDeploymentAsync(context, agent, record).ConfigureAwait(false);
            context.Assert.ExitZero(shownBefore, "deployment show before the update");
            var before = AmsSteps.Deployment(shownBefore);
            var endpoint = AmsSteps.DevEndpoint(context, "ac13");

            var updated = await AmsSteps.UpdateEndpointAsync(context, agent, record, endpoint).ConfigureAwait(false);

            context.Assert.ExitZero(updated, "deployment update --endpoint-url");
            var shownAfter = await AmsSteps.ShowDeploymentAsync(context, agent, record).ConfigureAwait(false);
            context.Assert.ExitZero(shownAfter, "deployment show after the update");
            context.Assert.Equal((before with { RequestedEndpoint = endpoint }).ToString(), AmsSteps.Deployment(shownAfter).ToString(), "only the requested endpoint changed");
            context.Assert.Equal("NotDeployed", AmsSteps.Deployment(shownAfter).State, "still not deployed");
            context.Assert.Equal(null, AmsSteps.Deployment(shownAfter).BotName, "still without a bot");
        });

        yield return Builder.Make("AC-14", "deployment create refuses a missing --entra-id, an unknown type and an endpoint for Prod before calling the service", Ams, SafetyTier.ReadOnly, run: async context =>
        {
            string[] create = ["deployment", "create", "--agent-name", AmsSteps.Name(context, "ac14-absent"), "--target-environment-id", AmsSteps.Services(context).HomeEnvironmentId, "--deployment-name", AmsSteps.Name(context, "ac14"), "--entra-id-type", "app-reg"];
            var entraId = Guid.NewGuid().ToString();

            context.Assert.Refused(await AmsSteps.RunAsync(context, [.. create, "--deployment-type", "dev"]).ConfigureAwait(false), 2, "Option '--entra-id' is required.", "no --entra-id");
            context.Assert.Refused(await AmsSteps.RunAsync(context, [.. create, "--deployment-type", "staging", "--entra-id", entraId]).ConfigureAwait(false), 2, "--deployment-type must be dev, test, or prod.", "an unknown --deployment-type");
            context.Assert.Refused(await AmsSteps.RunAsync(context, [.. create, "--deployment-type", "prod", "--entra-id", entraId, "--endpoint-url", AmsSteps.DevEndpoint(context, "ac14")]).ConfigureAwait(false), 2, "--endpoint-url is supported only for Dev deployments; a Prod deployment always runs a package.", "--endpoint-url for a Prod deployment");
        });

        yield return Builder.Make("AC-15", "deployment delete of a record never deployed removes it, and no bot ever existed", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var agent = await AmsSteps.CreateAgentAsync(context, "ac15").ConfigureAwait(false);
            var record = await AmsSteps.CreateDeploymentAsync(context, agent, "ac15", AmsSteps.Services(context).HomeEnvironmentId, "dev", Guid.NewGuid().ToString(), AmsSteps.DevEndpoint(context, "ac15")).ConfigureAwait(false);

            var deleted = await AmsSteps.RunAsync(context, ["deployment", "delete", .. AmsSteps.ById(agent, record)]).ConfigureAwait(false);

            context.Assert.ExitZero(deleted, "deployment delete --non-interactive");
            var root = AmsSteps.Root(deleted);
            context.Assert.Equal("agent-deployment-deleted", AmsOutput.Text(root, "status"), "reports the delete");
            context.Assert.True(AmsOutput.Find(root, "wasLive") is { ValueKind: JsonValueKind.False }, "the record was never live");
            context.Assert.True(AmsOutput.Find(root, "deployment", "botService") is null or { ValueKind: JsonValueKind.Null }, "no bot ever existed");
            context.Assert.RefusedWith(await AmsSteps.ReadAsync(context, ["deployment", "show", .. AmsSteps.ById(agent, record)]).ConfigureAwait(false), "deployment-not-found", "the deleted record");

            var shown = await AmsSteps.ReadAsync(context, ["agent", "show", "--agent-id", agent.AgentId]).ConfigureAwait(false);

            context.Assert.ExitZero(shown, "agent show");
            context.Assert.DoesNotContain(shown, record.DeploymentId, "the agent neither lists the record nor keeps a bot of it");
        });

        yield return Builder.Make("AC-16", "deployment create refuses a Dev endpoint whose host is not a dev tunnel, and creates nothing", AmsCreate, SafetyTier.Creating, run: async context =>
        {
            var agent = await AmsSteps.PooledAgentAsync(context).ConfigureAwait(false);
            var deploymentName = AmsSteps.Name(context, "ac16");

            var created = await AmsSteps.RunAsync(context, AmsSteps.CreateDeploymentArguments(agent, deploymentName, AmsSteps.Services(context).TestEnvironmentId, "dev", Guid.NewGuid().ToString(), $"https://{deploymentName}.example.com/api/messages")).ConfigureAwait(false);

            context.Assert.RefusedWith(created, AmsOutput.InvalidFieldValue, "an endpoint outside devtunnels.ms");
            context.Assert.RefusedWith(await AmsSteps.ReadAsync(context, ["deployment", "show", "--agent-id", agent.AgentId, "--deployment-name", deploymentName]).ConfigureAwait(false), "deployment-not-found", "the refused deployment");
        });
    }

    private static IEnumerable<Scenario> Deploys()
    {
        yield return Builder.Make("AC-20", "agent deploy by agent and deployment name makes the record Deployed and creates its bot at the endpoint", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);

            var shown = await AmsSteps.ShowDeploymentAsync(context, live.Agent, live.Deployment).ConfigureAwait(false);

            ExpectReported(context, live.FirstDeploy, "created", false, "that made the pooled deployment's bot");
            context.Assert.ExitZero(shown, "deployment show");
            var deployment = AmsSteps.Deployment(shown);
            context.Assert.Equal("Deployed", deployment.State, "the record is Deployed");
            context.Assert.Equal(live.Endpoint, deployment.Endpoint, "at its endpoint");
            context.Assert.False(deployment.EndpointChangePending, "with no endpoint change pending");
            context.Assert.False(string.IsNullOrEmpty(deployment.BotName), "with its bot");
            context.Assert.Equal(live.Endpoint, deployment.BotEndpoint, "whose messaging endpoint is the deployment's");
        });

        yield return Builder.Make("AC-21", "a new endpoint stays pending until the next agent deploy, which moves the same bot to it", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);
            var shown = await AmsSteps.ShowDeploymentAsync(context, live.Agent, live.Deployment).ConfigureAwait(false);
            context.Assert.ExitZero(shown, "deployment show before the change");
            var before = AmsSteps.Deployment(shown);
            var endpoint = AmsSteps.DevEndpoint(context, "ac21");

            var updated = await AmsSteps.UpdateEndpointAsync(context, live.Agent, before, endpoint).ConfigureAwait(false);

            context.Assert.ExitZero(updated, "deployment update --endpoint-url");
            var pending = AmsSteps.Deployment(updated);
            context.Assert.Equal(endpoint, pending.RequestedEndpoint, "the new endpoint is requested");
            context.Assert.True(pending.EndpointChangePending, "and pending");
            context.Assert.Equal(before.Endpoint, pending.Endpoint, "while the deployment still runs the old one");
            context.Assert.Equal(before.Endpoint, pending.BotEndpoint, "and so does its bot");

            var moved = await AmsSteps.DeployOrFailAsync(context, live.Agent, pending, "to the new endpoint").ConfigureAwait(false);

            ExpectReported(context, moved, "kept", false, "to the new endpoint", before.Endpoint);
            context.Assert.Equal(before.BotName, moved.Deployment.BotName, "the same bot");
            context.Assert.Equal(endpoint, moved.Deployment.Endpoint, "the deployment runs the new endpoint");
            context.Assert.Equal(endpoint, moved.Deployment.BotEndpoint, "and so does its bot");
            context.Assert.False(moved.Deployment.EndpointChangePending, "with nothing pending");
        });

        yield return Builder.Make("AC-22", "agent deploy of a Test deployment runs the commit it names, then the newest upload", AmsHosted, SafetyTier.Creating, pack: AmsSteps.HostedPack, timeout: HostedBudget, run: async context =>
        {
            var hosted = await AmsSteps.HostedAgentAsync(context).ConfigureAwait(false);
            var test = AmsSteps.Services(context).TestEnvironmentId;

            var older = await AmsSteps.DeployOrFailAsync(context, hosted.Agent, hosted.TestDeployment, "of the older upload", commitId: hosted.OlderCommit).ConfigureAwait(false);

            ExpectHosted(context, older.Deployment, hosted.Agent, test, hosted.OlderCommit, "the older upload");
            ExpectReported(context, older, "created", false, "of the older upload");

            var newest = await AmsSteps.DeployOrFailAsync(context, hosted.Agent, older.Deployment, "without --commit-id").ConfigureAwait(false);

            ExpectHosted(context, newest.Deployment, hosted.Agent, test, hosted.NewerCommit, "the newest upload");
            ExpectReported(context, newest, "kept", false, "without --commit-id");
            context.Assert.Equal(older.Deployment.BotName, newest.Deployment.BotName, "the same bot");
        });

        yield return Builder.Make("AC-23", "a Prod deployment runs only the newest upload, and only once a Test deployment runs it", AmsProd, SafetyTier.Creating, pack: AmsSteps.HostedPack, timeout: HostedBudget, run: async context =>
        {
            var hosted = await AmsSteps.HostedAgentAsync(context).ConfigureAwait(false);
            var prodEnvironmentId = AmsSteps.Services(context).ProdEnvironmentId;
            var prod = await AmsSteps.CreateDeploymentAsync(context, hosted.Agent, "hosted-prod", prodEnvironmentId, "prod", await AmsSteps.CreateAppRegistrationAsync(context, "hosted-prod").ConfigureAwait(false), null).ConfigureAwait(false);

            var named = await AmsSteps.RunAsync(context, ["agent", "deploy", .. AmsSteps.ById(hosted.Agent, prod), "--commit-id", hosted.NewerCommit]).ConfigureAwait(false);

            context.Assert.Refused(named, 2, "--commit-id cannot be used with a Prod deployment", "--commit-id for a Prod deployment");

            await AmsSteps.DeployOrFailAsync(context, hosted.Agent, hosted.TestDeployment, "of the older upload to Test", commitId: hosted.OlderCommit).ConfigureAwait(false);
            var early = await AmsSteps.DeployAsync(context, hosted.Agent, prod).ConfigureAwait(false);

            context.Assert.RefusedWith(early, "prod-needs-test", "Prod while Test runs an older upload");

            await AmsSteps.DeployOrFailAsync(context, hosted.Agent, hosted.TestDeployment, "of the newest upload to Test").ConfigureAwait(false);
            var promoted = await AmsSteps.DeployOrFailAsync(context, hosted.Agent, prod, "to Prod once Test runs the newest upload").ConfigureAwait(false);

            ExpectHosted(context, promoted.Deployment, hosted.Agent, prodEnvironmentId, hosted.NewerCommit, "the newest upload in Prod");
        });

        yield return Builder.Make("AC-24", "a Dev deployment switches between an endpoint and a package on one bot", AmsHosted, SafetyTier.Creating, pack: AmsSteps.HostedPack, timeout: HostedBudget, run: async context =>
        {
            var hosted = await AmsSteps.HostedAgentAsync(context).ConfigureAwait(false);
            var home = AmsSteps.Services(context).HomeEnvironmentId;

            var endpoint = await AmsSteps.DeployDevAsync(context, hosted.Agent, "ac24", home).ConfigureAwait(false);

            context.Assert.Equal(AmsSteps.DevEndpoint(context, "ac24"), endpoint.Deployment.Endpoint, "the deployment runs its endpoint");
            ExpectReported(context, endpoint, "created", false, "at the endpoint");

            var older = await AmsSteps.DeployOrFailAsync(context, hosted.Agent, endpoint.Deployment, "of the older upload", commitId: hosted.OlderCommit).ConfigureAwait(false);

            ExpectHosted(context, older.Deployment, hosted.Agent, home, hosted.OlderCommit, "the older upload");
            ExpectReported(context, older, "kept", true, "from the endpoint to the older upload");

            var newest = await AmsSteps.DeployOrFailAsync(context, hosted.Agent, older.Deployment, "of the newest upload", commitId: hosted.NewerCommit).ConfigureAwait(false);

            ExpectHosted(context, newest.Deployment, hosted.Agent, home, hosted.NewerCommit, "the newest upload");
            ExpectReported(context, newest, "kept", false, "from the older upload to the newest");

            var secondEndpoint = AmsSteps.DevEndpoint(context, "ac24-back");
            var updated = await AmsSteps.UpdateEndpointAsync(context, hosted.Agent, newest.Deployment, secondEndpoint).ConfigureAwait(false);
            context.Assert.ExitZero(updated, "deployment update --endpoint-url");
            var back = await AmsSteps.DeployOrFailAsync(context, hosted.Agent, AmsSteps.Deployment(updated), "back to an endpoint").ConfigureAwait(false);

            context.Assert.Equal(secondEndpoint, back.Deployment.Endpoint, "the deployment runs the new endpoint");
            context.Assert.Equal("dev-endpoint", back.Deployment.Kind, "as a dev endpoint again");
            ExpectReported(context, back, "kept", true, "from the newest upload back to an endpoint");
            context.Assert.Equal(endpoint.Deployment.DeploymentId, back.Deployment.DeploymentId, "one deployment throughout");
            context.Assert.Equal(endpoint.Deployment.BotName, back.Deployment.BotName, "on one bot throughout");
        });
    }

    private static IEnumerable<Scenario> Commits()
    {
        yield return Builder.Make("AC-30", "agent commit list shows two uploads newest first, used by no deployment", AmsPackage, SafetyTier.Creating, run: async context =>
        {
            var agent = await AmsSteps.PooledAgentAsync(context).ConfigureAwait(false);
            var older = await AmsSteps.UploadAsync(context, agent, "ac30-1").ConfigureAwait(false);
            var newer = await AmsSteps.UploadAsync(context, agent, "ac30-2").ConfigureAwait(false);
            IReadOnlyList<JsonElement> commits = [];

            await context.Assert.EventuallyAsync(async () =>
            {
                var listed = await AmsSteps.ReadAsync(context, ["agent", "commit", "list", "--agent-id", agent.AgentId]).ConfigureAwait(false);
                commits = AmsOutput.Succeeded(listed) ? AmsOutput.Items(AmsSteps.Root(listed), "commits") : [];
                return commits.Count(commit => AmsOutput.Text(commit, "commitId") is { } commitId && (AmsSteps.SameId(commitId, older) || AmsSteps.SameId(commitId, newer))) == 2;
            }, CommitListLag, "agent commit list shows both uploads").ConfigureAwait(false);

            context.Assert.Equal($"{newer},{older}", string.Join(',', commits.Select(commit => AmsOutput.Text(commit, "commitId"))), "newest first");
            context.Assert.True(commits.All(commit => AmsOutput.Items(commit, "deployments").Count == 0), "no deployment runs either");
        });
    }

    private static IEnumerable<Scenario> Channels()
    {
        yield return Builder.Make("AC-40", "deployment channel types lists the channel types the service supports and marks their secrets", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);

            var types = await DeploymentReadAsync(context, live, "channel", "types").ConfigureAwait(false);

            context.Assert.ExitZero(types, "deployment channel types");
            var channelTypes = AmsOutput.Items(AmsSteps.Root(types), "channelTypes");
            var listed = channelTypes.Select(type => AmsOutput.Text(type, "channelType")).OfType<string>().ToArray();
            context.Assert.Equal(string.Empty, string.Join(", ", ScenarioChannelTypes.Except(listed)), "lists every channel type the scenarios use");
            var accessToken = channelTypes.Where(type => AmsOutput.Text(type, "channelType") == "telegram").SelectMany(type => AmsOutput.Items(type, "parameters")).Where(parameter => AmsOutput.Text(parameter, "name") == "accessToken").ToArray();
            context.Assert.True(accessToken.Length == 1 && AmsOutput.Find(accessToken[0], "isSecret") is { ValueKind: JsonValueKind.True }, "marks Telegram's access token as a secret");
        });

        yield return Builder.Make("AC-41", "deployment channel list shows the channels every bot starts with", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);

            var channels = await ChannelNamesAsync(context, live).ConfigureAwait(false);

            context.Assert.True(channels.Contains("webchat"), "Web Chat");
            context.Assert.True(channels.Contains("directline"), "Direct Line");
        });

        yield return Builder.Make("AC-42", "deployment channel token issues a Direct Line token and conversation that no transcript holds", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);
            var site = AmsSteps.Name(context, "ac42");

            context.Assert.ExitZero(await DeploymentWriteAsync(context, live, "channel", "update", "--channel", "directline", "--add-site", $"siteName={site}", "isV3Enabled=true").ConfigureAwait(false), "add a site that issues tokens");
            await ReadSitesUntilAsync(context, live, sites => SiteEnabled(sites, site) == true && SiteFlag(sites, site, "isV3Enabled") == true, "show reads the added site enabled for Direct Line 3.0 clients").ConfigureAwait(false);

            var issued = await DeploymentWriteAsync(context, live, "channel", "token", "--channel", "directline", "--site", site).ConfigureAwait(false);

            context.Assert.ExitZero(issued, "deployment channel token");
            var root = AmsSteps.Root(issued);
            var token = AmsOutput.Text(root, "token", "token");
            context.Assert.True(token is { Length: > 0 }, "a token");
            context.Assert.Equal(site, AmsOutput.Text(root, "token", "siteName"), "from the added site");
            context.Assert.True(AmsOutput.Text(root, "token", "conversationId") is { Length: > 0 }, "for a conversation");
            context.Assert.True(AmsOutput.Find(root, "token", "expiresInSeconds") is { ValueKind: JsonValueKind.Number } expiry && expiry.GetInt32() > 0, "that expires");
            context.Assert.True(Uri.TryCreate(AmsOutput.Text(root, "token", "endpoint"), UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps, "at an https Direct Line endpoint");
            context.Assert.False(context.Transcript.Mentions(token!), "the token is in no transcript");

            context.Assert.ExitZero(await DeploymentWriteAsync(context, live, "channel", "update", "--channel", "directline", "--remove-site", $"siteName={site}").ConfigureAwait(false), "remove the site");
        });

        yield return Builder.Make("AC-43", "a Teams channel is created, shown, updated, listed and deleted", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);

            var created = await DeploymentWriteAsync(context, live, "channel", "create", "--channel", "msteams", "--parameters", "acceptedTerms=true").ConfigureAwait(false);

            context.Assert.ExitZero(created, "channel create msteams");
            context.Assert.Equal("msteams", AmsOutput.Text(AmsSteps.Root(created), "channel", "channelName"), "creates the Teams channel");
            await ReadChannelUntilAsync(context, live, "msteams", channel => AmsOutput.Find(channel, "parameters", "acceptedTerms") is { ValueKind: JsonValueKind.True }, "show reads Teams with its terms accepted").ConfigureAwait(false);

            var updated = await DeploymentWriteAsync(context, live, "channel", "update", "--channel", "msteams", "--parameters", "enableCalling=false").ConfigureAwait(false);

            context.Assert.ExitZero(updated, "channel update msteams");
            await ReadChannelUntilAsync(context, live, "msteams", channel => AmsOutput.Find(channel, "parameters", "enableCalling") is { ValueKind: JsonValueKind.False } && AmsOutput.Find(channel, "parameters", "acceptedTerms") is { ValueKind: JsonValueKind.True }, "show reads calling off with the terms still accepted").ConfigureAwait(false);
            context.Assert.True((await ChannelNamesAsync(context, live).ConfigureAwait(false)).Contains("msteams"), "list includes Teams");

            var deleted = await DeploymentWriteAsync(context, live, "channel", "delete", "--channel", "msteams").ConfigureAwait(false);

            context.Assert.ExitZero(deleted, "channel delete msteams");
            var gone = await ReadUntilAsync(context, () => DeploymentReadAsync(context, live, "channel", "show", "--channel", "msteams"), result => AmsOutput.IsRefused(result, "channel-not-found"), "show finds no Teams channel after the delete").ConfigureAwait(false);
            context.Assert.RefusedWith(gone, "channel-not-found", "the deleted Teams channel");
            context.Assert.False((await ChannelNamesAsync(context, live).ConfigureAwait(false)).Contains("msteams"), "list no longer includes Teams");
        });

        yield return Builder.Make("AC-44", "a Telegram channel with a fake token is refused, nothing is created, and the token appears in no output", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);
            var fakeToken = $"ahe2e-fake-{Guid.NewGuid():N}";

            var created = await DeploymentWriteAsync(context, live, "channel", "create", "--channel", "telegram", "--parameters", $"accessToken={fakeToken}").ConfigureAwait(false);

            context.Assert.RefusedWith(created, "channel-settings-refused", "the fake Telegram token");
            context.Assert.DoesNotContain(created, fakeToken, "the refusal does not echo the token");
            context.Assert.False((await ChannelNamesAsync(context, live).ConfigureAwait(false)).Contains("telegram"), "no Telegram channel exists");
            context.Assert.False(context.Transcript.Mentions(fakeToken), "the token is in no transcript");
        });

        yield return Builder.Make("AC-45", "deployment channel webhook of a Facebook channel the bot does not have is refused", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);

            var webhook = await DeploymentReadAsync(context, live, "channel", "webhook", "--channel", "facebook").ConfigureAwait(false);

            context.Assert.RefusedWith(webhook, "channel-not-found", "the webhook of a missing Facebook channel");
        });

        yield return Builder.Make("AC-46", "a Direct Line site is added, disabled and removed", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);
            var site = AmsSteps.Name(context, "ac46");

            context.Assert.ExitZero(await DeploymentWriteAsync(context, live, "channel", "update", "--channel", "directline", "--add-site", $"siteName={site}").ConfigureAwait(false), "add the site");
            await ReadSitesUntilAsync(context, live, sites => SiteEnabled(sites, site) == true, "show reads the added site enabled").ConfigureAwait(false);

            context.Assert.ExitZero(await DeploymentWriteAsync(context, live, "channel", "update", "--channel", "directline", "--set-site", $"siteName={site}", "isEnabled=false").ConfigureAwait(false), "disable the site");
            await ReadSitesUntilAsync(context, live, sites => SiteEnabled(sites, site) == false, "show reads the site disabled").ConfigureAwait(false);

            context.Assert.ExitZero(await DeploymentWriteAsync(context, live, "channel", "update", "--channel", "directline", "--remove-site", $"siteName={site}").ConfigureAwait(false), "remove the site");
            var remaining = await ReadSitesUntilAsync(context, live, sites => SiteEnabled(sites, site) is null, "show no longer reads the site").ConfigureAwait(false);

            context.Assert.True(SiteEnabled(remaining, "Default Site") == true, "the default site stays enabled");
        });
    }

    private static IEnumerable<Scenario> OAuthConnections()
    {
        yield return Builder.Make("AC-50", "deployment oauth list-providers lists the providers and one provider's parameters", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);

            var catalog = await DeploymentReadAsync(context, live, "oauth", "list-providers").ConfigureAwait(false);
            var narrowed = await DeploymentReadAsync(context, live, "oauth", "list-providers", "--provider-name", "Aadv2").ConfigureAwait(false);

            context.Assert.ExitZero(catalog, "oauth list-providers");
            context.Assert.ExitZero(narrowed, "oauth list-providers --provider-name Aadv2");
            var providers = AmsOutput.Items(AmsSteps.Root(catalog), "result");
            var aadv2 = AmsOutput.Items(AmsSteps.Root(narrowed), "result");
            context.Assert.True(providers.Count > 1 && providers.Any(provider => AmsOutput.Text(provider, "name") == "Aadv2"), "the catalog holds several providers, Aadv2 among them");
            context.Assert.True(aadv2.Count == 1 && AmsOutput.Text(aadv2[0], "name") == "Aadv2", "--provider-name narrows it to Aadv2");
            context.Assert.True(aadv2.Count == 1 && AmsOutput.Items(aadv2[0], "parameters").Any(parameter => AmsOutput.Text(parameter, "name") == "TenantId"), "with its TenantId parameter");
        });

        yield return Builder.Make("AC-51", "an OAuth connection whose secret comes from standard input is created, shown without it, listed, updated and deleted", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var live = await AmsSteps.PooledDeploymentAsync(context).ConfigureAwait(false);
            var connection = AmsSteps.Name(context, "ac51");
            var clientId = Guid.NewGuid().ToString();
            var fakeSecret = $"ahe2e-fake-{Guid.NewGuid():N}";
            var tenantId = AmsSteps.Services(context).TenantId;

            var created = await AmsSteps.RunAsync(context, ["deployment", "oauth", "create", .. AmsSteps.ById(live.Agent, live.Deployment), "--connection", connection, "--provider-name", "Aadv2", "--client-id", clientId, "--scopes", "User.Read", "--parameters", $"TenantId={tenantId}", "--client-secret-stdin"], stdin: fakeSecret).ConfigureAwait(false);

            context.Assert.ExitZero(created, "oauth create");
            context.Assert.Equal(clientId, AmsOutput.Text(AmsSteps.Root(created), "connection", "clientId"), "creates the connection for its client");
            var shown = await ReadConnectionUntilAsync(context, live, connection, found => Scopes(found) == "User.Read", "show reads the connection as created").ConfigureAwait(false);
            context.Assert.Equal(tenantId, AmsOutput.Text(AmsSteps.Root(shown), "connection", "parameters", "TenantId"), "with its tenant");
            var listed = await DeploymentReadAsync(context, live, "oauth", "list").ConfigureAwait(false);
            context.Assert.ExitZero(listed, "oauth list");
            context.Assert.True(AmsOutput.Items(AmsSteps.Root(listed), "connections").Any(item => AmsOutput.Text(item, "connectionName") == connection), "list includes the connection");

            var updated = await DeploymentWriteAsync(context, live, "oauth", "update", "--connection", connection, "--scopes", "User.Read openid").ConfigureAwait(false);

            context.Assert.ExitZero(updated, "oauth update --scopes");
            var reshown = await ReadConnectionUntilAsync(context, live, connection, found => Scopes(found) == "User.Read openid", "show reads the new scopes").ConfigureAwait(false);

            var deleted = await DeploymentWriteAsync(context, live, "oauth", "delete", "--connection", connection).ConfigureAwait(false);

            context.Assert.ExitZero(deleted, "oauth delete");
            var gone = await ReadUntilAsync(context, () => DeploymentReadAsync(context, live, "oauth", "show", "--connection", connection), result => AmsOutput.IsRefused(result, "oauth-connection-not-found"), "show finds no connection after the delete").ConfigureAwait(false);
            context.Assert.RefusedWith(gone, "oauth-connection-not-found", "the deleted connection");
            context.Assert.False(new[] { created, shown, listed, updated, reshown, deleted }.Any(result => result.Output.Contains(fakeSecret, StringComparison.Ordinal)), "no output holds the secret");
            context.Assert.False(context.Transcript.Mentions(fakeSecret), "the secret is in no transcript");
        });
    }

    private static IEnumerable<Scenario> Deletes()
    {
        yield return Builder.Make("AC-60", "deployment delete of a live deployment keeps it on a wrong name, then removes the record and keeps its bot", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var home = AmsSteps.Services(context).HomeEnvironmentId;
            var agent = await AmsSteps.CreateAgentAsync(context, "ac60").ConfigureAwait(false);
            var live = (await AmsSteps.DeployDevAsync(context, agent, "ac60", home).ConfigureAwait(false)).Deployment;

            var declined = await AmsSteps.RunPromptedAsync(context, ["deployment", "delete", .. AmsSteps.ById(agent, live), "--environment-id", home], stdin: $"not-{live.DeploymentName}{Environment.NewLine}").ConfigureAwait(false);

            context.Assert.Contains(declined, $"Confirm by typing ({live.DeploymentName})", "the delete asks for the deployment's name");
            context.Assert.Contains(declined, "nothing was deleted", "and deletes nothing on a wrong name");
            var kept = await AmsSteps.ShowDeploymentAsync(context, agent, live).ConfigureAwait(false);
            context.Assert.ExitZero(kept, "deployment show after the wrong name");
            context.Assert.Equal("Deployed", AmsSteps.Deployment(kept).State, "the deployment is still Deployed");

            var deleted = await AmsSteps.RunAsync(context, ["deployment", "delete", .. AmsSteps.ById(agent, live)]).ConfigureAwait(false);

            context.Assert.ExitZero(deleted, "deployment delete --non-interactive");
            var root = AmsSteps.Root(deleted);
            context.Assert.Equal("agent-deployment-deleted", AmsOutput.Text(root, "status"), "removes the record");
            context.Assert.True(AmsOutput.Find(root, "wasLive") is { ValueKind: JsonValueKind.True }, "of a live deployment");
            context.Assert.RefusedWith(await AmsSteps.ReadAsync(context, ["deployment", "show", .. AmsSteps.ById(agent, live)]).ConfigureAwait(false), "deployment-not-found", "the deleted record");
            var shown = await AmsSteps.ReadAsync(context, ["agent", "show", "--agent-id", agent.AgentId]).ConfigureAwait(false);
            context.Assert.ExitZero(shown, "agent show");
            var keptBot = AmsOutput.Items(AmsSteps.Root(shown), "keptBots").Where(bot => AmsSteps.SameId(AmsOutput.Text(bot, "deploymentId"), live.DeploymentId)).ToArray();
            context.Assert.True(keptBot.Length == 1, "agent show lists the kept bot");
            context.Assert.Equal(live.BotName, AmsOutput.Text(keptBot[0], "botName"), "under the deployment's bot name");
        });

        yield return Builder.Make("AC-61", "agent delete removes the agent with its live deployment and the bot it kept in another environment", AmsCreate, SafetyTier.Creating, timeout: BotBudget, run: async context =>
        {
            var services = AmsSteps.Services(context);
            var agent = await AmsSteps.CreateAgentAsync(context, "ac61").ConfigureAwait(false);
            var live = (await AmsSteps.DeployDevAsync(context, agent, "ac61-dev", services.HomeEnvironmentId).ConfigureAwait(false)).Deployment;
            var kept = (await AmsSteps.DeployDevAsync(context, agent, "ac61-test", services.TestEnvironmentId).ConfigureAwait(false)).Deployment;
            context.Assert.ExitZero(await AmsSteps.RunAsync(context, ["deployment", "delete", .. AmsSteps.ById(agent, kept)]).ConfigureAwait(false), "deployment delete of the Test environment deployment, which keeps its bot");

            var deleted = await AmsSteps.RunAsync(context, ["agent", "delete", "--agent-id", agent.AgentId]).ConfigureAwait(false);

            context.Assert.ExitZero(deleted, "agent delete --non-interactive");
            var root = AmsSteps.Root(deleted);
            context.Assert.Equal("agent-deleted", AmsOutput.Text(root, "status"), "deletes the agent");
            context.Assert.True(RemovedWithItsBot(AmsOutput.Items(root, "deployments"), live), "with its live deployment and that deployment's bot");
            context.Assert.True(RemovedWithItsBot(AmsOutput.Items(root, "keptBots"), kept), "and the bot kept in the Test environment");
            context.Assert.RefusedWith(await AmsSteps.ReadAsync(context, ["agent", "show", "--agent-id", agent.AgentId]).ConfigureAwait(false), AmsOutput.AgentNotFound, "the deleted agent");
            context.Assert.RefusedWith(await AmsSteps.RunAsync(context, ["agent", "delete", "--agent-id", agent.AgentId]).ConfigureAwait(false), AmsOutput.AgentNotFound, "a second agent delete");
            context.Ledger.MarkDeleted(agent.AgentId);
        });
    }

    private static Task<MsagentResult> DeploymentWriteAsync(ScenarioContext context, AmsLiveDeployment live, string group, string verb, params string[] options) => AmsSteps.RunAsync(context, ["deployment", group, verb, .. AmsSteps.ById(live.Agent, live.Deployment), .. options]);

    private static Task<MsagentResult> DeploymentReadAsync(ScenarioContext context, AmsLiveDeployment live, string group, string verb, params string[] options) => AmsSteps.ReadAsync(context, ["deployment", group, verb, .. AmsSteps.ById(live.Agent, live.Deployment), .. options]);

    private static async Task<IReadOnlyList<string>> ChannelNamesAsync(ScenarioContext context, AmsLiveDeployment live)
    {
        var listed = await DeploymentReadAsync(context, live, "channel", "list").ConfigureAwait(false);

        context.Assert.ExitZero(listed, "deployment channel list");
        return [.. AmsOutput.Items(AmsSteps.Root(listed), "channels").Select(channel => AmsOutput.Text(channel, "channelName")).OfType<string>()];
    }

    private static async Task<MsagentResult> ReadUntilAsync(ScenarioContext context, Func<Task<MsagentResult>> read, Func<MsagentResult, bool> shows, string check)
    {
        MsagentResult? last = null;
        await context.Assert.EventuallyAsync(async () => shows(last = await read().ConfigureAwait(false)), WriteLag, check).ConfigureAwait(false);
        return last!;
    }

    private static Task<MsagentResult> ReadChannelUntilAsync(ScenarioContext context, AmsLiveDeployment live, string channelName, Func<JsonElement, bool> shows, string check) =>
        ReadUntilAsync(context, () => DeploymentReadAsync(context, live, "channel", "show", "--channel", channelName), result => AmsOutput.Succeeded(result) && AmsOutput.Find(AmsSteps.Root(result), "channel") is { } channel && shows(channel), check);

    private static async Task<IReadOnlyList<JsonElement>> ReadSitesUntilAsync(ScenarioContext context, AmsLiveDeployment live, Func<IReadOnlyList<JsonElement>, bool> shows, string check)
    {
        var shown = await ReadChannelUntilAsync(context, live, "directline", channel => shows(AmsOutput.Items(channel, "parameters", "sites")), check).ConfigureAwait(false);
        return AmsOutput.Items(AmsSteps.Root(shown), "channel", "parameters", "sites");
    }

    private static bool? SiteEnabled(IReadOnlyList<JsonElement> sites, string siteName) => SiteFlag(sites, siteName, "isEnabled");

    private static bool? SiteFlag(IReadOnlyList<JsonElement> sites, string siteName, string flag) => sites.FirstOrDefault(site => string.Equals(AmsOutput.Text(site, "siteName"), siteName, StringComparison.OrdinalIgnoreCase)) is { ValueKind: JsonValueKind.Object } found ? AmsOutput.Find(found, flag) is { ValueKind: JsonValueKind.True } : null;

    private static Task<MsagentResult> ReadConnectionUntilAsync(ScenarioContext context, AmsLiveDeployment live, string connection, Func<JsonElement, bool> shows, string check) =>
        ReadUntilAsync(context, () => DeploymentReadAsync(context, live, "oauth", "show", "--connection", connection), result => AmsOutput.Succeeded(result) && AmsOutput.Find(AmsSteps.Root(result), "connection") is { } found && shows(found), check);

    private static string Scopes(JsonElement connection) => string.Join(' ', AmsOutput.Items(connection, "scopes").Select(scope => scope.GetString()));

    private static bool RemovedWithItsBot(IReadOnlyList<JsonElement> removed, AmsDeployment deployment) => removed.Any(item => AmsSteps.SameId(AmsOutput.Text(item, "deploymentId"), deployment.DeploymentId) && string.Equals(AmsOutput.Text(item, "botName"), deployment.BotName, StringComparison.OrdinalIgnoreCase) && AmsOutput.Find(item, "botDeleted") is { ValueKind: JsonValueKind.True });

    private static void ExpectHosted(ScenarioContext context, AmsDeployment deployment, AmsAgent agent, string environmentId, string commitId, string what)
    {
        context.Assert.Equal("Deployed", deployment.State, $"Deployed with {what}");
        context.Assert.Equal("package", deployment.Kind, $"running a package with {what}");
        context.Assert.Equal(commitId, deployment.CommitId, $"running the commit of {what}");
        context.Assert.True(AmsSteps.IsHostedRuntimeEndpoint(deployment.Endpoint, agent, environmentId), $"at the agent's hosted runtime with {what}");
        context.Assert.Equal(deployment.Endpoint, deployment.BotEndpoint, $"where its bot points with {what}");
    }

    private static void ExpectReported(ScenarioContext context, AmsDeployOutcome outcome, string bot, bool kindSwitched, string step, string? previousEndpoint = null)
    {
        if (outcome.Retried)
        {
            context.Transcript.Note($"agent deploy {step} needed a second attempt, so what msagent reports about the bot, the kind and the previous endpoint covers only that attempt and is not checked");
            return;
        }

        context.Assert.Equal(bot, outcome.Bot, $"agent deploy {step} reports the bot {bot}");
        context.Assert.True(outcome.KindSwitched == kindSwitched, $"agent deploy {step} reports {(kindSwitched ? "a" : "no")} kind switch");

        if (previousEndpoint is not null)
        {
            context.Assert.Equal(previousEndpoint, outcome.PreviousEndpoint, $"agent deploy {step} reports the endpoint it left");
        }
    }

    private static Task<MsagentResult> RenameDeploymentAsync(ScenarioContext context, AmsAgent agent, AmsDeployment deployment, string newName) => AmsSteps.RunAsync(context, ["deployment", "update", .. AmsSteps.ById(agent, deployment), "--new-name", newName]);

    private static async Task ExpectAgentAsync(ScenarioContext context, AmsAgent agent, string name, string description, string when)
    {
        var shown = await AmsSteps.ReadAsync(context, ["agent", "show", "--agent-name", name]).ConfigureAwait(false);

        context.Assert.ExitZero(shown, $"agent show --agent-name {when}");
        var root = AmsSteps.Root(shown);
        context.Assert.Equal(agent.AgentId, AmsOutput.Text(root, "agent", "agentId"), $"the name names the agent {when}");
        context.Assert.Equal(name, AmsOutput.Text(root, "agent", "displayName"), $"its name {when}");
        context.Assert.Equal(description, AmsOutput.Text(root, "agent", "description"), $"its description {when}");
    }
}
