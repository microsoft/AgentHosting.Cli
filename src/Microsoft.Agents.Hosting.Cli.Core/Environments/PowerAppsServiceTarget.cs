using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Environments;

/// <summary>Separates the PowerApps service authentication audience from its API destination.</summary>
public static class PowerAppsServiceTarget
{
    /// <summary>Requires a registered target coherent with the captured profile.</summary>
    public static void RequireContext(EnvironmentResolutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        PowerPlatformApiTargetSelection.Require(context.Target);
        if (context.Profile.Cloud != context.Target.Cloud || context.Profile.Ring != context.Target.Ring)
            throw new CloudRetargetedException("The service target does not match the captured authentication profile.");
    }

    /// <summary>Returns the PAC PowerApps service audience, not the BAP endpoint.</summary>
    public static string AudienceFor(EnvironmentResolutionContext context)
    {
        RequireContext(context);
        return context.Target.Cloud switch
        {
            CloudInstance.Public => "https://service.powerapps.com/",
            CloudInstance.UsGov => "https://gov.service.powerapps.us/",
            CloudInstance.UsGovHigh => "https://high.service.powerapps.us/",
            CloudInstance.UsGovDod => "https://service.apps.appsplatform.us/",
            CloudInstance.China => "https://service.powerapps.cn",
            CloudInstance.UsNat => "https://api.bap.eaglex.ic.gov/",
            CloudInstance.UsSec => "https://api.bap.microsoft.scloud/",
            _ => throw new EndpointTrustException("The PowerApps service audience is not verified for this target."),
        };
    }

    /// <summary>Returns the registered API cluster, failing closed for unmapped coordinates.</summary>
    public static CoreServicesClusterCategory ClusterFor(EnvironmentResolutionContext context)
    {
        RequireContext(context);
        return ClusterFor(context.Target);
    }

    /// <summary>Returns the registered API cluster for a configured target, without a public fallback.</summary>
    public static CoreServicesClusterCategory ClusterFor(PowerPlatformApiTargetConfig target)
    {
        PowerPlatformApiTargetSelection.Require(target);
        try
        {
            return CoreServicesCluster.For(target);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new EndpointTrustException("The PowerApps API cluster is not verified for this target.");
        }
    }
}
