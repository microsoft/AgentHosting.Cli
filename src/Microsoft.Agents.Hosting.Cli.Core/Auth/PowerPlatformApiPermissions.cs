namespace Microsoft.Agents.Hosting.Cli.Core.Auth;

/// <summary>The existing, first-party-approvable delegated Power Platform API permissions the CLI requests, each named with the operations it authorizes.</summary>
public static class PowerPlatformApiPermissions
{
    /// <summary>Authorizes <c>msagent env list</c> and <c>msagent env select</c>.</summary>
    public const string EnvironmentsRead = "PowerApps.Environments.Read";

    /// <summary>Authorizes <c>msagent agent show</c>, <c>msagent agent list</c>, and <c>msagent agent status</c>.</summary>
    public const string AppsRead = "PowerApps.Apps.Read";

    /// <summary>Authorizes <c>msagent agent create</c>.</summary>
    public const string AppsCreate = "PowerApps.Apps.Create";

    /// <summary>Authorizes AMS writes such as <c>msagent agent deploy</c> and <c>msagent agent delete</c>.</summary>
    public const string AppsUpdate = "PowerApps.Apps.Update";

    /// <summary>The AgentHostCLI first-party application id that must appear as the token's <c>appid</c>.</summary>
    public const string AgentHostCliApplicationId = "21fadfba-178a-47f0-8ce6-07c05eb346ca";

    /// <summary>The non-production (Test) Power Platform API resource application id.</summary>
    public const string NonProductionResourceApplicationId = "8578e004-a5c6-46e7-913e-12f58912df43";

    /// <summary>The production Power Platform API resource application id.</summary>
    public const string ProductionResourceApplicationId = "0ddb742a-e7dc-4899-a31e-80e797ec7144";
}

/// <summary>
/// Builds the MSAL scopes the CLI requests for a Power Platform API audience.
///
/// The default form stays PAC-compatible: it delegates to <see cref="PacResourceScopeNormalizer"/>, so
/// a Power Platform API audience produces the same <c>//.default</c> shape PAC produces for any
/// resource and the CLI has exactly one scope-construction rule.
///
/// <see cref="ExplicitScopes"/> is the documented fallback for the case the TDD calls out: if
/// resource-side preauthorization does not put the expected permissions into <c>scp</c>, the CLI asks
/// for the existing Power Platform API permissions by name rather than inventing a new scope.
/// </summary>
public sealed class PpapiScopeBuilder
{
    private readonly PacResourceScopeNormalizer _normalizer;

    public PpapiScopeBuilder(PacResourceScopeNormalizer? normalizer = null)
    {
        _normalizer = normalizer ?? new PacResourceScopeNormalizer();
    }

    /// <summary>The single <c>//.default</c> scope for <paramref name="audience"/>.</summary>
    public string DefaultScope(string audience) => _normalizer.ToDefaultScope(audience);

    /// <summary>The single <c>//.default</c> scope for <paramref name="audience"/>.</summary>
    public string DefaultScope(Uri audience) => _normalizer.ToDefaultScope(audience);

    /// <summary>The <c>//.default</c> scope as the one-element scope set MSAL is handed.</summary>
    public IReadOnlyList<string> DefaultScopes(string audience) => [DefaultScope(audience)];

    /// <summary>
    /// Explicit per-permission scopes for <paramref name="audience"/>, in the order supplied — the
    /// fallback used only when the default scope does not yield the expected <c>scp</c> claim.
    /// </summary>
    // Deliberately an instance member: it is the documented fallback sibling of DefaultScope(s), which do
    // use _normalizer, so both scope-construction routes are reached through one injected builder.
#pragma warning disable CA1822
    public IReadOnlyList<string> ExplicitScopes(string audience, params string[] permissions)
#pragma warning restore CA1822
    {
        ArgumentNullException.ThrowIfNull(permissions);

        if (permissions.Length == 0)
        {
            throw new ArgumentException("At least one permission is required.", nameof(permissions));
        }

        if (string.IsNullOrWhiteSpace(audience))
        {
            throw new ArgumentException("Audience must be a non-empty absolute url.", nameof(audience));
        }

        if (!Uri.TryCreate(audience.Trim(), UriKind.Absolute, out var parsed))
        {
            throw new ArgumentException($"Audience '{audience}' is not an absolute url.", nameof(audience));
        }

        var authority = parsed.GetLeftPart(UriPartial.Authority);
        return [.. permissions.Select(permission => $"{authority}/{permission}")];
    }
}
