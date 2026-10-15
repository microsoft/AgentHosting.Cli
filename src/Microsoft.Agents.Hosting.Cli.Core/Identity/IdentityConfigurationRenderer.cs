using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

internal static class IdentityConfigurationRenderer
{
    public static IdentityGeneratedConfig Render(
        BlueprintIdentityRequest request,
        string applicationId,
        string? federatedClientId) =>
        RenderCore(
            request.AuthType,
            request.TenantId,
            request.ConfigFormat,
            serviceConnectionClientId: applicationId,
            userAuthClientId: applicationId,
            federatedClientId);

    /// <summary>
    /// Renders configuration for an App Registration identity. For
    /// <see cref="IdentityCredentialType.UserManagedIdentity"/> the runtime service connection uses the
    /// managed identity's client id while user-facing OAuth keeps the (companion) App Registration id;
    /// the other auth types use the App Registration id for both.
    /// </summary>
    public static IdentityGeneratedConfig Render(
        AppRegistrationIdentityRequest request,
        string oauthApplicationId,
        string? federatedClientId,
        string? managedIdentityClientId)
    {
        var serviceConnectionClientId =
            request.AuthType == IdentityCredentialType.UserManagedIdentity
                ? managedIdentityClientId
                    ?? throw new InvalidOperationException(
                        "A user-assigned managed identity client id is required for UserManagedIdentity configuration.")
                : oauthApplicationId;
        return RenderCore(
            request.AuthType,
            request.TenantId,
            request.ConfigFormat,
            serviceConnectionClientId,
            userAuthClientId: oauthApplicationId,
            federatedClientId);
    }

    private static IdentityGeneratedConfig RenderCore(
        IdentityCredentialType authType,
        string tenantId,
        IdentityConfigFormat configFormat,
        string serviceConnectionClientId,
        string userAuthClientId,
        string? federatedClientId)
    {
        var content = configFormat == IdentityConfigFormat.DotNet
            ? RenderDotNet(authType, tenantId, serviceConnectionClientId, userAuthClientId, federatedClientId)
            : RenderNode(authType, tenantId, serviceConnectionClientId, userAuthClientId, federatedClientId);
        return new IdentityGeneratedConfig
        {
            Format = configFormat.ToString().ToLowerInvariant(),
            Content = content,
        };
    }

    private static string RenderDotNet(
        IdentityCredentialType authType,
        string tenantId,
        string serviceConnectionClientId,
        string userAuthClientId,
        string? federatedClientId)
    {
        var settings = new JsonObject
        {
            ["AuthType"] = authType.ToString(),
            ["AuthorityEndpoint"] = $"https://login.microsoftonline.com/{tenantId}",
            ["ClientId"] = serviceConnectionClientId,
            ["Scopes"] = new JsonArray("https://api.botframework.com/.default"),
        };
        if (federatedClientId is not null)
        {
            settings["FederatedClientId"] = federatedClientId;
        }

        var root = new JsonObject
        {
            ["Connections"] = new JsonObject
            {
                ["ServiceConnection"] = new JsonObject { ["Settings"] = settings },
            },
            ["Applications"] = new JsonObject
            {
                ["UserAuth"] = new JsonObject
                {
                    ["ClientId"] = userAuthClientId,
                    ["TenantId"] = tenantId,
                },
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string RenderNode(
        IdentityCredentialType authType,
        string tenantId,
        string serviceConnectionClientId,
        string userAuthClientId,
        string? federatedClientId)
    {
        var lines = new List<string>
        {
            $"connections__serviceConnection__settings__authType={authType}",
            $"connections__serviceConnection__settings__authorityEndpoint=https://login.microsoftonline.com/{tenantId}",
            $"connections__serviceConnection__settings__clientId={serviceConnectionClientId}",
            "connections__serviceConnection__settings__scopes__0=https://api.botframework.com/.default",
            $"connections__serviceConnection__settings__tenantId={tenantId}",
            $"applications__userAuth__clientId={userAuthClientId}",
            $"applications__userAuth__tenantId={tenantId}",
        };
        if (federatedClientId is not null)
        {
            lines.Add($"connections__serviceConnection__settings__federatedClientId={federatedClientId}");
        }

        return string.Join(Environment.NewLine, lines);
    }
}
