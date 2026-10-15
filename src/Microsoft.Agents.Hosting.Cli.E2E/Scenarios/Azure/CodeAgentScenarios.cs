using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.Azure;

/// <summary>CodeAgent routing, the ARM-backed deployment lifecycle, and the validation around it.</summary>
public static class CodeAgentScenarios
{
    private static readonly ScenarioBuilder Builder = new("code-agent");

    private static readonly Capability[] None = [];
    private static readonly Capability[] AzureCreate = [Capability.Auth, Capability.AzureCreate];
    private static readonly Capability[] Ams = [Capability.Auth, Capability.Ams];
    private static readonly Capability[] AmsAndAzure = [Capability.Auth, Capability.Ams, Capability.AzureCreate];

    /// <summary>Every code-agent scenario, in the order the run executes them.</summary>
    public static IEnumerable<Scenario> All() => [.. Routing(), .. Lifecycle(), .. Validation(), .. BlockedOnAms()];

    private static IEnumerable<Scenario> Routing()
    {
        yield return Builder.Make("CA-01", "deployment create defaults to CodeAgent", None, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(["deployment", "create", "--non-interactive"], directory).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "missing flags are a usage error");
            context.Assert.Contains(result, "--entra-id", "the failure names CodeAgent's flags, so the default resolved to CodeAgent");
        });

        yield return Builder.Make("CA-02", "an existing record wins over option shape", AzureCreate, SafetyTier.Creating, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(
                ["deployment", "show", .. Selector(bot), "--json", "--non-interactive"],
                bot.ProjectDirectory!).ConfigureAwait(false);

            context.Assert.ExitZero(result, "the stored record routes the verb");
        });

        yield return Builder.Make("CA-03", "one agent in the project resolves without a selector", AzureCreate, SafetyTier.Creating, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(
                ["deployment", "list", "--agent-id", bot.AgentId!, "--json", "--non-interactive"],
                bot.ProjectDirectory!).ConfigureAwait(false);

            context.Assert.ExitZero(result, "the sole-agent rule resolves the type");
        });

        yield return Builder.Make("CA-04", "--environment-id is rejected for a CodeAgent", None, SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["deployment", "create", "--agent-id", Guid.NewGuid().ToString(), "--deployment-name", "e2e",
                 "--entra-id", Guid.NewGuid().ToString(), "--entra-id-type", "AppReg",
                 "--environment-id", Guid.NewGuid().ToString(), "--non-interactive"],
                directory).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "--environment-id is MCS-only");
            context.Assert.Contains(result, "--environment-id", "the message names the offending option");
        });

        yield return Builder.Make("CA-05", "deployment connection update is rejected for a CodeAgent", None, SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["deployment", "connection", "update", "--deployment-name", "e2e", "--agent-id", Guid.NewGuid().ToString(), "--non-interactive"],
                directory).ConfigureAwait(false);

            context.Assert.NonZero(result, "connection binding is MCS-only");
        });

        yield return Builder.Make("CA-06", "a deployment name shared by both agent types is ambiguous", AzureCreate, SafetyTier.Creating, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(
                ["deployment", "show", .. Selector(bot), "--json", "--non-interactive"],
                bot.ProjectDirectory!).ConfigureAwait(false);

            context.Assert.ExitZero(result, "a single-type match is not ambiguous");
        });
    }

    private static IEnumerable<Scenario> Lifecycle()
    {
        yield return Builder.Make("CA-07", "deployment create provisions a bot", AzureCreate, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            context.Assert.True(
                await context.Cloud.BotExistsAsync(bot.Name, context.Config.Azure.ResourceGroup!, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false),
                "the bot exists in ARM");

            context.Assert.FileExists(Path.Combine(bot.ProjectDirectory!, ".config", "agent.config.json"), "the record persists to the project, not the home directory");
        });

        yield return Builder.Make("CA-08", "re-create adopts the existing bot", AzureCreate, SafetyTier.Creating, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            var again = await context.Msagent.RunAsync(
                ["deployment", "create", "--agent-id", bot.AgentId!, "--deployment-name", bot.Name,
                 "--entra-id", bot.ApplicationId, "--entra-id-type", "AppReg",
                 "--resource-group", context.Config.Azure.ResourceGroup!, "--json", "--non-interactive"],
                bot.ProjectDirectory!,
                timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            context.Assert.ExitZero(again, "an identical retry succeeds with only --resource-group");

            var adopted = again.Json<JsonElement>();
            context.Assert.Equal(
                bot.DeploymentId!,
                adopted.GetProperty("deployment").GetProperty("deploymentId").GetString(),
                "the deterministic id resolves the same record");
        });

        yield return Builder.Make("CA-09", "deployment list and show agree by id and by name", AzureCreate, SafetyTier.ReadOnly, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            var listed = await context.Msagent.RunAsync(["deployment", "list", "--agent-id", bot.AgentId!, "--json", "--non-interactive"], bot.ProjectDirectory!).ConfigureAwait(false);
            var byName = await context.Msagent.RunAsync(["deployment", "show", .. Selector(bot), "--json", "--non-interactive"], bot.ProjectDirectory!).ConfigureAwait(false);
            var byId = await context.Msagent.RunAsync(["deployment", "show", "--deployment-id", bot.DeploymentId!, "--json", "--non-interactive"], bot.ProjectDirectory!).ConfigureAwait(false);

            context.Assert.ExitZero(listed, "list");
            context.Assert.ExitZero(byName, "show by name");
            context.Assert.ExitZero(byId, "show by id");
            context.Assert.Equal(byName.StdOut.Trim(), byId.StdOut.Trim(), "both selectors return the same record");
        });

        yield return Builder.Make("CA-10", "show after the bot is deleted out of band", AzureCreate, SafetyTier.Creating, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);
            var azure = new AzureCli(context.Transcript);

            var removed = await azure.RunAsync(["bot", "delete", "--name", bot.Name, "--resource-group", bot.ResourceGroup, "--subscription", context.Config.Azure.SubscriptionId!]).ConfigureAwait(false);
            context.Assert.ExitZero(removed, "the bot is removed behind the CLI's back");
            ConsumeBot(context, bot);

            var show = await context.Msagent.RunAsync(
                ["deployment", "show", .. Selector(bot), "--non-interactive"],
                bot.ProjectDirectory!).ConfigureAwait(false);

            context.Assert.NonZero(show, "an absent bot is reported as not found");
        });

        yield return Builder.Make("CA-11", "deployment update --endpoint-url", AzureCreate, SafetyTier.Reversible, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);
            var resourceGroup = context.Config.Azure.ResourceGroup!;
            var original = await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false);
            var changed = $"https://{bot.Name}.example.invalid/api/messages2";

            var update = await context.Msagent.RunAsync(
                ["deployment", "update", .. Selector(bot), "--endpoint-url", changed, "--json", "--non-interactive"],
                bot.ProjectDirectory!,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            context.Assert.ExitZero(update);
            context.Assert.Equal(changed, await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false), "ARM shows the new endpoint");

            if (original is { Length: > 0 })
            {
                await context.Msagent.RunAsync(
                    ["deployment", "update", .. Selector(bot), "--endpoint-url", original, "--non-interactive"],
                    bot.ProjectDirectory!,
                    timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
        });

        yield return Builder.Make("CA-12", "deployment update --sku", AzureCreate, SafetyTier.Reversible, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);
            var resourceGroup = context.Config.Azure.ResourceGroup!;

            var update = await context.Msagent.RunAsync(
                ["deployment", "update", .. Selector(bot), "--sku", "S1", "--json", "--non-interactive"],
                bot.ProjectDirectory!,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            context.Assert.ExitZero(update);
            context.Assert.Equal("S1", await context.Cloud.BotPropertyAsync(bot.Name, resourceGroup, "sku.name", subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false), "ARM shows the new SKU");

            await context.Msagent.RunAsync(
                ["deployment", "update", .. Selector(bot), "--sku", "F0", "--non-interactive"],
                bot.ProjectDirectory!,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        });

        yield return Builder.Make("CA-13", "deployment update --display-name", AzureCreate, SafetyTier.Reversible, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            var update = await context.Msagent.RunAsync(
                ["deployment", "update", .. Selector(bot), "--display-name", "Renamed E2E", "--json", "--non-interactive"],
                bot.ProjectDirectory!,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            context.Assert.ExitZero(update);
        });

        yield return Builder.Make("CA-14", "deployment delete removes the bot", AzureCreate, SafetyTier.Creating, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            var delete = await context.Msagent.RunAsync(
                ["deployment", "delete", .. Selector(bot)],
                bot.ProjectDirectory!,
                stdin: bot.Name,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            context.Assert.ExitZero(delete);
            context.Assert.False(await context.Cloud.BotExistsAsync(bot.Name, bot.ResourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false), "the bot is gone from ARM");
            ConsumeBot(context, bot);

            var again = await context.Msagent.RunAsync(
                ["deployment", "delete", .. Selector(bot)],
                bot.ProjectDirectory!,
                stdin: bot.Name,
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            context.Assert.NonZero(again, "the record went with the bot, so a second delete has nothing left to address");
        });

    }

    private static IEnumerable<Scenario> Validation()
    {
        yield return Builder.Make("CA-15", "deployment create names every missing required flag", None, SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["deployment", "create", "--agent-id", Guid.NewGuid().ToString(), "--non-interactive"],
                directory).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "missing flags are a usage error");
            context.Assert.Contains(result, "--deployment-name", "the message names --deployment-name");
            context.Assert.Contains(result, "--entra-id", "the message names --entra-id");
        });

        yield return Builder.Make("CA-16", "--entra-id-type Blueprint is parsed but unsupported", None, SafetyTier.ReadOnly, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["deployment", "create", "--agent-id", Guid.NewGuid().ToString(), "--deployment-name", "ahe2e-blueprint",
                 "--entra-id", Guid.NewGuid().ToString(), "--entra-id-type", "Blueprint", "--non-interactive"],
                directory).ConfigureAwait(false);

            context.Assert.NonZero(result, "Blueprint is refused");
            context.Assert.Contains(result, "AppReg", "the message names the one supported value");
        });


        yield return Builder.Make("CA-17", "a CodeAgent deployment with no agent row still lists", AzureCreate, SafetyTier.ReadOnly, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(
                ["deployment", "list", "--agent-id", bot.AgentId!, "--json", "--non-interactive"],
                bot.ProjectDirectory!).ConfigureAwait(false);

            context.Assert.ExitZero(result, "listing does not require an agent row");
            context.Assert.Contains(result, bot.Name, "the slot is listed even though no agent record was ever created");
        });

        yield return Builder.Make("CA-18", "--json redacts the ARM payload", AzureCreate, SafetyTier.ReadOnly, run: async context =>
        {
            var bot = await BotAsync(context).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(
                ["deployment", "show", .. Selector(bot), "--json", "--non-interactive"],
                bot.ProjectDirectory!).ConfigureAwait(false);

            context.Assert.ExitZero(result);
            context.Assert.DoesNotContain(result, "clientSecret", "no client secret reaches stdout");
            context.Assert.DoesNotContain(result, "\"accessToken\"", "no token reaches stdout");
        });
    }

    private static IEnumerable<Scenario> BlockedOnAms()
    {
        yield return Builder.Make("CA-19", "agent create --agent-type CodeAgent", Ams, SafetyTier.Creating, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var result = await context.Msagent.RunAsync(
                ["agent", "create", "--agent-type", "CodeAgent", "--agent-name", $"ahe2e-code-{context.RunSuffix}", "--json", "--non-interactive"],
                directory,
                timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            context.Assert.ExitZero(result);
        });

        yield return Builder.Make("CA-20", "agent deploy a CodeAgent", AmsAndAzure, SafetyTier.Creating, run: async context =>
        {
            var directory = context.Workspaces.EmptyDirectory(context.ScenarioId);

            var created = await context.Msagent.RunAsync(
                ["agent", "create", "--agent-type", "CodeAgent", "--agent-name", $"ahe2e-code-{context.RunSuffix}", "--json", "--non-interactive"],
                directory,
                timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

            context.Assert.ExitZero(created, "agent create");

            var deployed = await context.Msagent.RunAsync(
                ["agent", "deploy", "--project", directory, "--json", "--non-interactive"],
                directory,
                timeout: TimeSpan.FromMinutes(15)).ConfigureAwait(false);

            context.Assert.ExitZero(deployed, "agent deploy");
        });
    }


    /// <summary>
    /// Returns the run's CodeAgent bot, provisioning it and its app registration on first use.
    ///
    /// <para>Nothing is created merely to be deleted: the delete scenarios run last and consume this,
    /// and whatever survives is removed by the end-of-run cleanup.</para>
    /// </summary>
    private static Task<SessionBot> BotAsync(ScenarioContext context) =>
        context.Session.BotAsync(SessionBotKind.CodeAgentDeployment, () => ProvisionAsync(context));

    private static async Task<SessionBot> ProvisionAsync(ScenarioContext context)
    {
        var azureConfig = context.Config.Azure;
        var directory = context.Workspaces.EmptyDirectory("session-codeagent");
        var agentId = Guid.NewGuid().ToString();
        var botName = context.BotName("code");

        if (await context.Cloud.BotExistsAsync(botName, azureConfig.ResourceGroup!, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false))
        {
            throw new ScenarioBlockedException($"a bot named {botName} already exists; the harness never adopts a resource it did not create", "bot name collision");
        }

        var applicationId = await ResolveApplicationIdAsync(context, botName).ConfigureAwait(false);
        var endpoint = $"https://{botName}.example.invalid/api/messages";

        var result = await context.Msagent.RunAsync(
            ["deployment", "create", "--agent-id", agentId, "--deployment-name", botName, "--deployment-type", "dev",
             "--entra-id", applicationId, "--entra-id-type", "AppReg",
             "--resource-group", azureConfig.ResourceGroup!, "--subscription", azureConfig.SubscriptionId!,
             "--tenant", context.Config.TenantId!, "--location", "global", "--sku", "F0",
             "--endpoint-url", endpoint,
             "--json", "--non-interactive"],
            directory,
            timeout: TimeSpan.FromMinutes(10)).ConfigureAwait(false);

        context.Ledger.RecordAzureBot(botName, azureConfig.ResourceGroup!, azureConfig.SubscriptionId, context.ScenarioId);
        context.Assert.ExitZero(result, "deployment create");

        var report = result.Json<JsonElement>();

        return new SessionBot(
            SessionBotKind.CodeAgentDeployment,
            botName,
            azureConfig.ResourceGroup!,
            applicationId,
            endpoint,
            agentId,
            report.GetProperty("deployment").GetProperty("deploymentId").GetString(),
            directory);
    }

    /// <summary>
    /// Addresses a CodeAgent deployment. The name alone is not enough: the CLI requires either
    /// <c>--deployment-id</c>, or <c>--agent-id</c> together with <c>--deployment-name</c>.
    /// </summary>
    private static string[] Selector(SessionBot bot) => ["--agent-id", bot.AgentId!, "--deployment-name", bot.Name];


    private static void ConsumeBot(ScenarioContext context, SessionBot bot)
    {
        context.Ledger.MarkDeleted(bot.Name);
        context.Session.ConsumeBot(bot.Kind);
    }

    private static async Task<string> ResolveApplicationIdAsync(ScenarioContext context, string botName)
    {
        if (context.Config.Azure.ExistingMsaAppId is { Length: > 0 } configured)
        {
            return configured;
        }

        var applicationId = await context.Cloud.CreateAppRegistrationAsync(botName).ConfigureAwait(false);
        context.Ledger.RecordEntraApp(applicationId, botName, context.ScenarioId);
        return applicationId;
    }

}