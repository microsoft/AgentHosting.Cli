using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.E2E.Scenarios.Azure;

/// <summary>Azure Bot Service preview and apply, against a bot the harness creates and deletes.</summary>
public static class AbsScenarios
{
    private static readonly ScenarioBuilder Builder = new("abs");

    private static readonly Capability[] None = [];
    private static readonly Capability[] Read = [Capability.Auth, Capability.AzureRead];
    private static readonly Capability[] Create = [Capability.Auth, Capability.AzureCreate];
    private static readonly Capability[] ExistingBot = [Capability.Auth, Capability.AzureExistingBot];

    /// <summary>Every abs scenario, in the order the run executes them.</summary>
    public static IEnumerable<Scenario> All()
    {
        yield return Builder.Make("AB-01", "abs preview names every missing required flag", None, SafetyTier.ReadOnly, run: async context =>
        {
            var result = await context.Msagent.RunAsync(["abs", "preview", "--non-interactive"]).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "missing context is a usage error");
            context.Assert.Contains(result, "--bot-name", "the message names --bot-name");
        });

        yield return Builder.Make("AB-02", "abs preview on an absent bot plans a create", Read, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var result = await PreviewAsync(context, context.BotName("absent"), Endpoint(context, "absent")).ConfigureAwait(false);

            context.Assert.ExitZero(result);
            context.Assert.Contains(result, "create", "an absent bot plans a create");
        });

        yield return Builder.Make("AB-03", "abs apply --force creates the run's bot", Create, SafetyTier.Creating, smoke: true, run: async context =>
        {
            var bot = await RunBotAsync(context).ConfigureAwait(false);

            context.Assert.True(
                await context.Cloud.BotExistsAsync(bot.Name, context.Config.Azure.ResourceGroup!, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false),
                "the bot exists in ARM");
        });

        yield return Builder.Make("AB-04", "abs preview on that bot plans an update", Create, SafetyTier.ReadOnly, run: async context =>
        {
            var bot = await RunBotAsync(context).ConfigureAwait(false);
            var result = await PreviewAsync(context, bot.Name, Endpoint(context, "previewed"), bot.ApplicationId).ConfigureAwait(false);

            context.Assert.ExitZero(result);
            context.Assert.Contains(result, "update", "an existing bot with a different endpoint plans an update");
        });

        yield return Builder.Make("AB-05", "abs apply without --force only previews", Create, SafetyTier.ReadOnly, run: async context =>
        {
            var bot = await RunBotAsync(context).ConfigureAwait(false);
            var resourceGroup = bot.ResourceGroup;
            var before = await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false);

            var result = await ApplyAsync(context, bot.Name, Endpoint(context, "unapplied"), bot.ApplicationId, force: false).ConfigureAwait(false);
            context.Assert.ExitZero(result, "apply without --force succeeds as a plan");

            context.Assert.Equal(before, await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false), "the endpoint is unchanged");
        });

        yield return Builder.Make("AB-06", "abs apply --force round-trips the endpoint", Create, SafetyTier.Reversible, run: async context =>
        {
            var bot = await RunBotAsync(context).ConfigureAwait(false);
            var resourceGroup = context.Config.Azure.ResourceGroup!;
            var changed = Endpoint(context, "changed");

            context.Ledger.RecordConfigEdit($"{bot.Name}-endpoint", bot.Endpoint, context.ScenarioId);

            var apply = await ApplyAsync(context, bot.Name, changed, bot.ApplicationId, force: true).ConfigureAwait(false);
            context.Assert.ExitZero(apply, "the endpoint change applies");
            context.Assert.Equal(changed, await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false), "ARM shows the new endpoint");

            var restore = await ApplyAsync(context, bot.Name, bot.Endpoint, bot.ApplicationId, force: true).ConfigureAwait(false);
            context.Assert.ExitZero(restore, "the endpoint restores");
            context.Assert.Equal(bot.Endpoint, await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false), "ARM shows the original endpoint");

            context.Ledger.MarkRestored($"{bot.Name}-endpoint");
        });

        yield return Builder.Make("AB-07", "abs apply --force re-run is a no-op", Create, SafetyTier.ReadOnly, run: async context =>
        {
            var bot = await RunBotAsync(context).ConfigureAwait(false);
            var resourceGroup = bot.ResourceGroup;
            var before = await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false);

            var again = await ApplyAsync(context, bot.Name, bot.Endpoint, bot.ApplicationId, force: true).ConfigureAwait(false);
            context.Assert.ExitZero(again, "an identical apply succeeds");
            context.Assert.Equal(before, await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false), "nothing changed in ARM");
        });

        yield return Builder.Make("AB-08", "abs apply --force --location westus2 is refused", Create, SafetyTier.ReadOnly, smoke: true, run: async context =>
        {
            var bot = await RunBotAsync(context).ConfigureAwait(false);
            var resourceGroup = bot.ResourceGroup;
            var before = await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false);

            var result = await context.Msagent.RunAsync(
                [.. BaseArguments(context, bot.Name, Endpoint(context, "regional"), bot.ApplicationId, "westus2"), "--force", "--non-interactive"],
                timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);

            context.Assert.ExitCode(result, 2, "a regional location is refused");
            context.Assert.Contains(result, "global", "the message names --location global");
            context.Assert.Equal(before, await context.Cloud.BotEndpointAsync(bot.Name, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false), "the bot is unchanged");
        });

        yield return Builder.Make("AB-09", "abs preview against an operator's own bot", ExistingBot, SafetyTier.ReadOnly, run: async context =>
        {
            var botName = context.Config.Azure.ExistingBotName!;
            var resourceGroup = context.Config.Azure.ResourceGroup!;

            var boundApplicationId = await context.Cloud.BotPropertyAsync(botName, resourceGroup, "properties.msaAppId", subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false)
                ?? throw new ScenarioBlockedException($"{botName} reports no msaAppId, so a preview cannot target its identity", "bot has no app id");

            var result = await PreviewAsync(context, botName, Endpoint(context, "existing"), boundApplicationId).ConfigureAwait(false);

            context.Assert.ExitZero(result);
            context.Assert.Contains(result, "update", "an existing bot with a different endpoint plans an update");
            context.Assert.DoesNotContain(result, "conflict", "targeting the bot's own app id is not a rebind");
        });
    }

    /// <summary>
    /// Returns the run's own bot, creating it and its app registration on first use.
    ///
    /// <para>One bot serves every <c>abs</c> scenario. Azure permits a single bot per app id, so the
    /// two are a pair; both are removed by the end-of-run cleanup rather than by a scenario.</para>
    /// </summary>
    private static Task<SessionBot> RunBotAsync(ScenarioContext context) =>
        context.Session.BotAsync(SessionBotKind.AbsBot, () => ProvisionAsync(context));

    private static async Task<SessionBot> ProvisionAsync(ScenarioContext context)
    {
        var resourceGroup = context.Config.Azure.ResourceGroup!;
        var botName = context.BotName("abs");

        if (await context.Cloud.BotExistsAsync(botName, resourceGroup, subscriptionId: context.Config.Azure.SubscriptionId).ConfigureAwait(false))
        {
            throw new ScenarioBlockedException($"a bot named {botName} already exists; the harness never adopts a resource it did not create", "bot name collision");
        }

        var applicationId = await ResolveApplicationIdAsync(context, botName).ConfigureAwait(false);
        var endpoint = Endpoint(context, "created");

        var result = await ApplyAsync(context, botName, endpoint, applicationId, force: true).ConfigureAwait(false);
        context.Ledger.RecordAzureBot(botName, resourceGroup, context.Config.Azure.SubscriptionId, context.ScenarioId);
        context.Assert.ExitZero(result, "abs apply --force");

        return new SessionBot(SessionBotKind.AbsBot, botName, resourceGroup, applicationId, endpoint);
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

    private static Task<MsagentResult> PreviewAsync(ScenarioContext context, string botName, string endpoint, string? applicationId = null) =>
        context.Msagent.RunAsync(
            [.. BaseArguments(context, botName, endpoint, applicationId ?? EmptyApplicationId, "global", "preview"), "--non-interactive"],
            timeout: TimeSpan.FromMinutes(5));

    private static Task<MsagentResult> ApplyAsync(ScenarioContext context, string botName, string endpoint, string applicationId, bool force) =>
        context.Msagent.RunAsync(
            force
                ? [.. BaseArguments(context, botName, endpoint, applicationId, "global"), "--force", "--non-interactive"]
                : [.. BaseArguments(context, botName, endpoint, applicationId, "global"), "--non-interactive"],
            timeout: TimeSpan.FromMinutes(10));

    private static string[] BaseArguments(ScenarioContext context, string botName, string endpoint, string applicationId, string location, string verb = "apply") =>
    [
        "abs", verb,
        "--bot-name", botName,
        "--resource-group", context.Config.Azure.ResourceGroup!,
        "--subscription", context.Config.Azure.SubscriptionId!,
        "--tenant", context.Config.TenantId!,
        "--location", location,
        "--endpoint", endpoint,
        "--msa-app-id", applicationId,
    ];

    private const string EmptyApplicationId = "00000000-0000-0000-0000-000000000000";

    private static string Endpoint(ScenarioContext context, string label) =>
        $"https://{context.Config.Azure.BotNamePrefix}-{context.RunSuffix}-{label}.example.invalid/api/messages";

}
