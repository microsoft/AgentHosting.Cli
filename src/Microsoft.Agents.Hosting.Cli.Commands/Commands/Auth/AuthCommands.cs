using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Commands.Common;
using Microsoft.Agents.Hosting.Cli.Commands.Groups;
using Microsoft.Agents.Hosting.Cli.Commands.Model;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Commands.Commands.Auth;

/// <summary><c>msagent auth login</c>.</summary>
[Command("login", Description = "Sign in.", ParentGroup = typeof(AuthGroup), SortPriority = 10, SuppressProgress = true)]
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
        OptionCatalog.Value("--cloud", $"Power Platform API ring this sign-in targets: {PowerPlatformApiRingSelection.AcceptedTokenList}. Defaults to the ring the account already uses, or {PowerPlatformApiRingSelection.CanonicalToken(PowerPlatformApiRing.Production)}."),
    ];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (!string.IsNullOrWhiteSpace(input.GetString("--resource")) && input.GetStrings("--scope").Length > 0)
        {
            return "--resource and --scope cannot be combined.";
        }

        return AuthRingBinding.Validate(input);
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
            Ring = AuthRingBinding.Read(input),
        };
    }
}

/// <summary>
/// Reads the ring an auth command targets, shared by the two commands that can set it.
/// </summary>
internal static class AuthRingBinding
{
    /// <summary>Rejects a ring the host does not serve, naming the ones it does.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <returns>The rejection, or <see langword="null"/> when the value is absent or accepted.</returns>
    public static string? Validate(ICommandInput input)
    {
        var supplied = input.GetString("--cloud");

        return string.IsNullOrWhiteSpace(supplied) || PowerPlatformApiRingSelection.TryResolve(supplied, out _, out var error) ? null : error;
    }

    /// <summary>The ring the command names, or <see langword="null"/> when it names none.</summary>
    /// <param name="input">The parsed command line.</param>
    /// <returns>The ring.</returns>
    public static PowerPlatformApiRing? Read(ICommandInput input)
    {
        var supplied = input.GetString("--cloud");

        return !string.IsNullOrWhiteSpace(supplied) && PowerPlatformApiRingSelection.TryResolve(supplied, out var target, out _) ? target.Ring : null;
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

/// <summary><c>msagent auth switch</c>.</summary>
[Command("switch", Description = "Switch the active account.", ParentGroup = typeof(AuthGroup), SortPriority = 30)]
public sealed class AuthSwitchCommand : CommandBase<AuthSwitchRequest, AuthProfileSummary>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols =>
    [
        OptionCatalog.Value("--account", "Account UPN, email, or home account id to activate."),
        OptionCatalog.Value("--cloud", $"Retarget the active account at a Power Platform API ring: {PowerPlatformApiRingSelection.AcceptedTokenList}. No sign-in is performed."),
    ];

    /// <inheritdoc />
    protected override string? Validate(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return string.IsNullOrWhiteSpace(input.GetString("--account")) && string.IsNullOrWhiteSpace(input.GetString("--cloud")) ? "Supply --account, --cloud, or both." : AuthRingBinding.Validate(input);
    }

    /// <inheritdoc />
    protected override AuthSwitchRequest Bind(ICommandInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new AuthSwitchRequest
        {
            Account = input.GetString("--account") ?? string.Empty,
            Ring = AuthRingBinding.Read(input),
        };
    }
}

/// <summary><c>msagent auth logout</c>.</summary>
[Command("logout", Description = "Sign out and clear cached accounts.", ParentGroup = typeof(AuthGroup), SortPriority = 40)]
public sealed class AuthLogoutCommand : CommandBase<AuthLogoutRequest, AuthLogoutResult>
{
    /// <inheritdoc />
    public override IReadOnlyList<CommandSymbol> Symbols => [];

    /// <inheritdoc />
    protected override AuthLogoutRequest Bind(ICommandInput input) => new();
}
