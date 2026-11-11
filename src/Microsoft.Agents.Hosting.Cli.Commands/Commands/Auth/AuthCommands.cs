using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Auth;

/// <summary><c>msagent auth login</c>.</summary>
[Command("login", Description = "Sign in.", ParentGroup = typeof(AuthGroup), SortPriority = 10, SuppressProgress = true)]
[RequiredFeatureFlag(FeatureName.LegacyAuth)]
public sealed class AuthLoginCommand : CommandBase<AuthLoginRequest, AuthLoginResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--account", "Account UPN or email to target, used as the login hint."),
        OptionCatalog.Value("--tenant", "Explicit target tenant id."),
        OptionCatalog.Value("--resource", "Resource or audience the token targets. Defaults to the Power Platform API."),
        OptionCatalog.Strings("--scope", "Explicit delegated scope. Repeatable. Not with --resource."),
        OptionCatalog.Switch("--device-code", "Use the device-code flow."),
        OptionCatalog.Value("--cloud", $"Cloud: {string.Join(", ", PowerPlatformApiTargetSelection.AcceptedTokens)}. Also accepts prod/production, preprod and test. Omitted or blank uses the compatible profile target, or Public if none exists."),
    ];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.NonInteractive)
            throw new InteractionRequiredException("Explicit interactive authentication is not allowed in this invocation.");

        if (!string.IsNullOrWhiteSpace(input.GetString("--resource")) && input.GetStrings("--scope").Length > 0)
        {
            return "--resource and --scope cannot be combined.";
        }

        return AuthCloudBinding.Validate(input, legacy: true);
    }

    /// <inheritdoc />
    protected override AuthLoginRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AuthLoginRequest
        {
            Account = input.GetString("--account"),
            Tenant = input.GetString("--tenant"),
            Resource = input.GetString("--resource"),
            Scopes = input.GetStrings("--scope"),
            DeviceCode = input.GetSwitch("--device-code"),
            NonInteractive = input.NonInteractive,
            Target = string.IsNullOrWhiteSpace(input.GetString("--cloud")) ? null : AuthCloudBinding.Read(input),
        };
    }
}

/// <summary>
/// Reads the registered cloud targeted by create or retained login.
/// </summary>
internal static class AuthCloudBinding
{
    /// <summary>Rejects an unregistered cloud without resolving service clients.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <param name="legacy">Whether retained-login cloud aliases are accepted.</param>
    /// <returns>The rejection, or <see langword="null"/> when the value is absent or accepted.</returns>
    public static string? Validate(ICommandInput input, bool legacy = false)
    {
        var supplied = input.GetString("--cloud");

        if (!input.WasProvided("--cloud")) return null;
        if (!legacy && !PowerPlatformApiTargetSelection.AcceptedTokens.Contains(supplied ?? "", StringComparer.OrdinalIgnoreCase))
            return $"Supply --cloud as one of: {string.Join(", ", PowerPlatformApiTargetSelection.AcceptedTokens)}.";
        return PowerPlatformApiTargetSelection.TryResolve(supplied, out _, out _) ? null
            : $"Supply --cloud as one of: {string.Join(", ", PowerPlatformApiTargetSelection.AcceptedTokens)}.";
    }

    /// <summary>The registered cloud target, defaulting to Public when omitted.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <returns>The registered target.</returns>
    public static PowerPlatformApiTargetConfig Read(ICommandInput input)
    {
        var supplied = input.GetString("--cloud");

        return PowerPlatformApiTargetSelection.TryResolve(supplied, out var target, out _) ? target
            : throw new UsageException("A registered authentication cloud is required.");
    }
}

/// <summary><c>msagent auth status</c>.</summary>
[Command("status", Description = "Show the current sign-in state.", ParentGroup = typeof(AuthGroup), SortPriority = 20)]
public sealed class AuthStatusCommand : CommandBase<AuthStatusRequest, AuthStatusReport>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => [];

    /// <inheritdoc />
    protected override AuthStatusRequest Bind(ICommandInput input) => new();
}

/// <summary><c>msagent auth logout</c>.</summary>
[Command("logout", Description = "Sign out and clear only owned msagent state.", ParentGroup = typeof(AuthGroup), SortPriority = 40)]
[RequiredFeatureFlag(FeatureName.LegacyAuth)]
public sealed class AuthLogoutCommand : CommandBase<AuthLogoutRequest, AuthLogoutResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => [];

    /// <inheritdoc />
    protected override AuthLogoutRequest Bind(ICommandInput input) => new();
}

internal static class AuthOptions
{
    internal static CommandOption Value(string name, string alias, string description, bool sensitive = false) =>
        new(name, description) { ShortAlias = alias, IsSensitive = sensitive };
    internal static CommandOption Flag(string name, string alias, string description) =>
        new(name, description) { ShortAlias = alias, ValueType = typeof(bool), IsSwitch = true };
    internal static CommandOption Index(bool required = false) =>
        new("--index", "One-based transient profile position from auth list.") { ValueType = typeof(int?), IsRequired = required };
    internal static CommandOption Name(bool required = false) =>
        new("--name", "Case-sensitive profile name (1 to 30 characters).") { IsRequired = required };
    internal static AuthProfileSelector Selector(ICommandInput input) => new(input.GetInt("--index"), input.GetString("--name"));
    internal static string? ValidateSelector(ICommandInput input, bool optional = false)
    {
        bool index = input.WasProvided("--index"), name = input.WasProvided("--name");
        return index && name || !optional && !index && !name || index && input.GetInt("--index") is not > 0
            || name && string.IsNullOrEmpty(input.GetString("--name"))
            ? "Supply exactly one positive --index or case-sensitive --name." : null;
    }
}

/// <summary><c>msagent auth create</c>.</summary>
[Command("create", Description = "Create or replace and activate an authentication profile.", ParentGroup = typeof(AuthGroup), SortPriority = 5, SuppressProgress = true)]
public sealed class AuthCreateCommand : CommandBase<AuthCreateRequest, AuthProfileOperationResult>
{
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        AuthOptions.Value("--name", "-n", "Optional case-sensitive profile name, 1 to 30 characters."),
        AuthOptions.Value("--environment", "-env", "Default environment ID, trusted organization URL, or unambiguous name."),
        AuthOptions.Value("--cloud", "-ci", $"Cloud: {string.Join(", ", PowerPlatformApiTargetSelection.AcceptedTokens)}. Defaults to Public."),
        AuthOptions.Value("--tenant", "-t", "Tenant GUID; required for application credentials and federation."),
        AuthOptions.Flag("--deviceCode", "-dc", "Use interactive device-code sign-in; not allowed with --non-interactive."),
        AuthOptions.Value("--username", "-un", "Username for password authentication; requires --password."),
        AuthOptions.Value("--password", "-pw", "Password for password authentication; never persisted. Arguments may be exposed by the shell.", true),
        AuthOptions.Value("--applicationId", "-id", "Application GUID for a workload credential.") with { RequiredFeatures = [FeatureName.AppUserSupport] },
        AuthOptions.Value("--clientSecret", "-cs", "Application secret; requires tenant/applicationId. Arguments may be exposed by the shell.", true) with { RequiredFeatures = [FeatureName.AppUserSupport] },
        AuthOptions.Value("--certificateDiskPath", "-cdp", "PFX file with a private key; requires tenant/applicationId.") with { RequiredFeatures = [FeatureName.AppUserSupport] },
        AuthOptions.Value("--certificatePassword", "-cp", "PFX password; requires certificateDiskPath. Arguments may be exposed by the shell.", true) with { RequiredFeatures = [FeatureName.AppUserSupport] },
        AuthOptions.Flag("--managedIdentity", "-mi", "Use configured DefaultAzureCredential sources; managed identity is not guaranteed to win.") with { RequiredFeatures = [FeatureName.AppUserSupport] },
        AuthOptions.Flag("--githubFederated", "-ghf", "Use GitHub Actions federation; requires tenant/applicationId and Actions OIDC runtime.") with { RequiredFeatures = [FeatureName.AppUserSupport] },
        AuthOptions.Flag("--azureDevOpsFederated", "-adof", "Use Azure DevOps federation; requires tenant/applicationId and MSAGENT_ADO_* runtime.") with { RequiredFeatures = [FeatureName.AppUserSupport] },
    ];

    protected override void EnsureAvailable(ICommandInput input, IServiceProvider services)
    {
        if (Symbols.Any(symbol => symbol.RequiredFeatures.Contains(FeatureName.AppUserSupport) && input.WasProvided(symbol.Name)))
            FeatureGate.RequireEnabled(services, FeatureName.AppUserSupport, "Application identity profile creation");
    }

    protected override string? Validate(ICommandInput input)
    {
        AuthCreateInput.Validate(Read(input));
        return AuthCloudBinding.Validate(input);
    }

    protected override AuthCreateRequest Bind(ICommandInput input) =>
        Read(input, AuthCloudBinding.Read(input));

    private static AuthCreateRequest Read(ICommandInput input, PowerPlatformApiTargetConfig? target = null) =>
        new()
        {
            Target = target ?? PowerPlatformApiTargetConfig.Public,
            Name = input.GetString("--name"), Environment = input.GetString("--environment"),
            Tenant = input.GetString("--tenant"),
            DeviceCode = input.GetSwitch("--deviceCode"), Username = input.GetString("--username"),
            Password = input.GetString("--password"), ApplicationId = input.GetString("--applicationId"),
            ClientSecret = input.GetString("--clientSecret"), CertificateDiskPath = input.GetString("--certificateDiskPath"),
            CertificatePassword = input.GetString("--certificatePassword"), ManagedIdentity = input.GetSwitch("--managedIdentity"),
            GitHubFederated = input.GetSwitch("--githubFederated"), AzureDevOpsFederated = input.GetSwitch("--azureDevOpsFederated"),
            NonInteractive = input.NonInteractive,
        };
}

public sealed record AuthListRequest;
public sealed record AuthDeleteRequest(AuthProfileSelector Selector);
public sealed record AuthNameRequest(int Index, string Name);
public sealed record AuthClearRequest;
public sealed record AuthWhoRequest;

[Command("list", Description = "List profile metadata and transient positions without signing in.", ParentGroup = typeof(AuthGroup), SortPriority = 25)]
public sealed class AuthListCommand : CommandBase<AuthListRequest, AuthProfileListResult>
{
    public override IReadOnlyList<CommandSymbol> Symbols => [];
    protected override AuthListRequest Bind(ICommandInput input) => new();
}

[Command("select", Description = "Select a profile by index or case-sensitive name; otherwise use the interactive picker.", ParentGroup = typeof(AuthGroup), SortPriority = 30)]
public sealed class AuthSelectCommand : CommandBase<AuthProfileSelectRequest, AuthProfileOperationResult>
{
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        AuthOptions.Index() with { ShortAlias = "-i" },
        AuthOptions.Name() with { ShortAlias = "-n" },
    ];
    protected override string? Validate(ICommandInput input) => AuthOptions.ValidateSelector(input, optional: true);
    protected override AuthProfileSelectRequest Bind(ICommandInput input) =>
        new() { Selector = AuthOptions.Selector(input), NonInteractive = input.NonInteractive };
}

[Command("delete", Description = "Delete one owned profile without signing in; report the remaining active profile.", ParentGroup = typeof(AuthGroup), SortPriority = 35)]
public sealed class AuthDeleteCommand : CommandBase<AuthDeleteRequest, AuthProfileOperationResult>
{
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        AuthOptions.Index() with { ShortAlias = "-i" },
        AuthOptions.Name() with { ShortAlias = "-n" },
    ];
    protected override string? Validate(ICommandInput input) => AuthOptions.ValidateSelector(input);
    protected override AuthDeleteRequest Bind(ICommandInput input) => new(AuthOptions.Selector(input));
}

[Command("name", Description = "Name a profile by index, without activating it.", ParentGroup = typeof(AuthGroup), SortPriority = 36)]
public sealed class AuthNameCommand : CommandBase<AuthNameRequest, AuthProfileOperationResult>
{
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        AuthOptions.Index(required: true) with { ShortAlias = "-i" },
        AuthOptions.Name(required: true) with { ShortAlias = "-n" },
    ];
    protected override AuthNameRequest Bind(ICommandInput input) => new(input.GetInt("--index")!.Value, input.GetString("--name")!);
}

[Command("clear", Description = "Repair or clear owned msagent metadata, credentials and caches only.", ParentGroup = typeof(AuthGroup), SortPriority = 45)]
public sealed class AuthClearCommand : CommandBase<AuthClearRequest, AuthLogoutResult>
{
    public override IReadOnlyList<CommandSymbol> Symbols => [];
    protected override AuthClearRequest Bind(ICommandInput input) => new();
}

[Command("who", Description = "Show selected profile, identity and environment metadata; never acquire or export a token.", ParentGroup = typeof(AuthGroup), SortPriority = 50)]
public sealed class AuthWhoCommand : CommandBase<AuthWhoRequest, AuthWhoResult>
{
    public override IReadOnlyList<CommandSymbol> Symbols => [];
    protected override AuthWhoRequest Bind(ICommandInput input) => new();
}
