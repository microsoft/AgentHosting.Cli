namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>The msagent rollout features that are off by default, which the harness turns on only for the commands that need them.</summary>
public static class MsagentFeatures
{
    /// <summary>The variable msagent reads its enabled features from.</summary>
    public const string Variable = "AH_CLI_FEATURES";

    /// <summary>The feature that turns on msagent's Agent Management Service routes.</summary>
    public const string Ams = "AmsFeatures";

    /// <summary>The feature that turns on the abs group and the CodeAgent deployment routes backed by Azure Bot Service.</summary>
    public const string AzureBotService = "AzureBotServiceFeatures";

    /// <summary>The feature that turns on the identity group.</summary>
    public const string Identity = "IdentityFeatures";

    /// <summary>The overlay that turns on exactly the given feature, whatever the operator's environment turns on.</summary>
    public static EnvironmentOverlay Only(string feature) => EnvironmentOverlay.Empty.Set(Variable, feature);
}
