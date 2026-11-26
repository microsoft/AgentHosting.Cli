using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Ams;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>The cloud and tenant <c>msagent auth status</c> reported.</summary>
public sealed record AmsSignIn(string? Cloud, string? TenantId);

/// <summary>How long the code-agent steps wait before a second try and after an upload.</summary>
public sealed record AmsStepTiming(TimeSpan RetryDelay, TimeSpan UploadSettle)
{
    /// <summary>Waits 30 seconds before a second try and a minute after an upload, longer than the service took to read an upload.</summary>
    public static AmsStepTiming Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
}

/// <summary>What the code-agent scenarios share to drive the Agent Management Service: the msagent that reaches it, its environments and the sign-in the run found.</summary>
public sealed class AmsServices
{
    private readonly string? _tenantId;

    public AmsServices(E2EConfig config, MsagentCli msagent, AmsStepTiming? timing = null)
    {
        Config = config.Ams;
        _tenantId = config.TenantId;
        Msagent = msagent.With(Overlay(Config.ControlPlaneEndpoint, Config.DataPlane.Endpoint));
        Timing = timing ?? AmsStepTiming.Default;
    }

    public AmsConfig Config { get; }

    /// <summary>How long the code-agent steps wait before a second try and after an upload.</summary>
    public AmsStepTiming Timing { get; }

    /// <summary>The msagent every code-agent scenario runs, which reaches the configured Agent Management Service.</summary>
    public MsagentCli Msagent { get; }

    /// <summary>The sign-in the probe found, recorded with every resource so cleanup deletes nothing under another one.</summary>
    public AmsSignIn? SignIn { get; private set; }

    /// <summary>The home environment of every agent the run creates.</summary>
    public string HomeEnvironmentId => Config.DevEnvironmentId ?? throw new E2EException("ams.devEnvironmentId is required, which the capability check should have caught.");

    /// <summary>The Test environment that Test deployments and the second target use.</summary>
    public string TestEnvironmentId => Config.TestEnvironmentId ?? throw new E2EException("ams.testEnvironmentId is required, which the capability check should have caught.");

    /// <summary>The Prod environment of the Prod-rule scenario.</summary>
    public string ProdEnvironmentId => Config.ProdEnvironmentId ?? throw new E2EException("ams.prodEnvironmentId is required, which the capability check should have caught.");

    /// <summary>The tenant every resource of the run belongs to.</summary>
    public string TenantId => _tenantId ?? throw new E2EException("tenantId is required, which the capability check should have caught.");

    /// <summary>The context every Agent Management Service ledger entry carries, so cleanup reaches the same service under the same sign-in.</summary>
    public AmsCleanupContext CleanupContext => new() { ControlPlaneEndpoint = Config.ControlPlaneEndpoint, DataPlaneEndpoint = Config.DataPlane.Endpoint, Cloud = SignIn?.Cloud ?? Config.Cloud, TenantId = _tenantId, HomeEnvironmentId = Config.DevEnvironmentId };

    /// <summary>The variables that point msagent at an Agent Management Service and turn its commands on.</summary>
    public static EnvironmentOverlay Overlay(string? controlPlaneEndpoint, string? dataPlaneEndpoint) => MsagentFeatures.Only(MsagentFeatures.Ams).Set(AmsEndpointProfile.ControlPlaneEndpointVariable, controlPlaneEndpoint).Set(AmsEndpointProfile.DataPlaneEndpointVariable, dataPlaneEndpoint);

    /// <summary>Whether a scenario needs any Agent Management Service capability.</summary>
    public static bool Serves(Scenario scenario) => scenario.Requires.Any(Capabilities.AmsChain.Contains);

    /// <summary>Reads the cloud and tenant from <c>msagent auth status --json</c>, or null when it reports no sign-in.</summary>
    public static AmsSignIn? ReadSignIn(MsagentResult status) => status.ExitCode == 0 && AmsOutput.Document(status) is { } root && AmsOutput.Find(root, "signedIn") is { ValueKind: JsonValueKind.True } ? new AmsSignIn(AmsOutput.Text(root, "cloud"), AmsOutput.Text(root, "tenantId")) : null;

    /// <summary>Why a sign-in is not the expected cloud and tenant, or null when it is.</summary>
    public static string? SignInMismatch(AmsSignIn? signIn, string? expectedCloud, string? expectedTenantId) =>
        signIn is null ? "msagent reports no sign-in"
        : expectedCloud is { Length: > 0 } && !string.Equals(signIn.Cloud, expectedCloud, StringComparison.OrdinalIgnoreCase) ? $"msagent is signed in to the {signIn.Cloud ?? "unknown"} cloud, not {expectedCloud}"
        : expectedTenantId is { Length: > 0 } && !string.Equals(signIn.TenantId, expectedTenantId, StringComparison.OrdinalIgnoreCase) ? $"msagent is signed in to tenant {signIn.TenantId ?? "unknown"}, not {expectedTenantId}"
        : null;

    /// <summary>Checks the sign-in behind the capabilities: msagent's cloud and tenant, then <c>az</c>'s tenant for the scenarios that create App Registrations.</summary>
    public async Task<Capabilities> ProbeAsync(Capabilities capabilities, MsagentResult signInStatus, AzureCli azure, CancellationToken cancellationToken)
    {
        if (!capabilities.Has(Capability.Ams))
        {
            return capabilities;
        }

        SignIn = ReadSignIn(signInStatus);

        if (SignInMismatch(SignIn, Config.Cloud, _tenantId) is { } mismatch)
        {
            return capabilities.WithoutAms(Capability.Ams, mismatch);
        }

        if (!capabilities.Has(Capability.AmsCreate))
        {
            return capabilities;
        }

        return await AzureTenantMismatchAsync(azure, _tenantId, cancellationToken).ConfigureAwait(false) is { } azureMismatch ? capabilities.WithoutAms(Capability.AmsCreate, azureMismatch) : capabilities;
    }

    /// <summary>Why <c>az</c> is not signed in to the expected tenant, or null when it is.</summary>
    public static async Task<string?> AzureTenantMismatchAsync(AzureCli azure, string? expectedTenantId, CancellationToken cancellationToken) => AzureTenantMismatch(await azure.RunAsync(["account", "show", "--query", "tenantId", "-o", "tsv"], cancellationToken).ConfigureAwait(false), expectedTenantId);

    /// <summary>Why <c>az account show</c> does not name the expected tenant, or null when it does.</summary>
    public static string? AzureTenantMismatch(MsagentResult account, string? expectedTenantId) =>
        account.ExitCode != 0 ? "az is not signed in, so the run could not create or delete its App Registrations"
        : !string.Equals(account.StdOut.Trim(), expectedTenantId, StringComparison.OrdinalIgnoreCase) ? $"az is signed in to tenant {account.StdOut.Trim()}, not {expectedTenantId}"
        : null;
}
