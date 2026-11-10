namespace Microsoft.Agents.Hosting.Cli.Commands.Features;

/// <summary>Feature controls understood by the shipped CLI.</summary>
public enum FeatureName
{
    /// <summary>AMS CodeAgent creation, package upload, and package-revision deployment.</summary>
    AmsFeatures,

    /// <summary>Azure Bot Service commands and CodeAgent deployment routes backed by ABS.</summary>
    AzureBotServiceFeatures,

    /// <summary>User-facing identity creation commands.</summary>
    IdentityFeatures,

    /// <summary>Live, delegated Copilot Studio query testing.</summary>
    LiveTestFeatures,

    /// <summary>Copilot Studio evaluation commands.</summary>
    EvaluationFeatures,

    /// <summary>Legacy authentication login and logout commands.</summary>
    LegacyAuth,
}
