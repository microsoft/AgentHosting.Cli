using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Validates authentication inputs without constructing a credential source or reading storage.</summary>
public static class AuthCreateInput
{
    public static void Validate(AuthCreateRequest request, bool interactionAllowed = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workload = new[] { request.ClientSecret is not null, request.CertificateDiskPath is not null,
            request.ManagedIdentity, request.GitHubFederated, request.AzureDevOpsFederated };
        bool password = request.Username is not null || request.Password is not null;
        if ((request.DeviceCode || !workload.Any(x => x) && !password)
            && (request.NonInteractive || !interactionAllowed))
            throw new InteractionRequiredException("Explicit interactive authentication is not allowed in this invocation.");
        if (request.Target is null || request.Scopes is null)
            throw new UsageException("An authentication target and scope collection are required.");
        try { PowerPlatformApiTargetSelection.Require(request.Target); }
        catch (ArgumentException) { throw new UsageException("A registered Power Platform API cloud target is required."); }
        if (workload.Count(x => x) > 1) throw new UsageException("Credential methods are mutually exclusive.");
        if (password && (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password)
            || workload.Any(x => x) || request.DeviceCode))
            throw new UsageException("Password authentication requires username and password, without another credential method or device code.");
        if (workload.Any(x => x) && request.DeviceCode)
            throw new UsageException("Device code cannot be combined with a workload credential.");
        if (request.CertificatePassword is not null && request.CertificateDiskPath is null)
            throw new UsageException("A certificate password requires a certificate path.");
        if (request.ApplicationId is not null && !workload.Any(x => x))
            throw new UsageException("An application id requires a workload credential method.");
        if (request.Name is not null && (string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Length > 30 || request.Name.Any(char.IsControl)))
            throw new UsageException("An authentication profile name must contain 1 to 30 characters.");
        if (request.Tenant is not null) RequireGuid(request.Tenant, "tenant");
        if (request.ApplicationId is not null) RequireGuid(request.ApplicationId, "application id");
        if (request.ClientSecret is not null || request.CertificateDiskPath is not null
            || request.GitHubFederated || request.AzureDevOpsFederated)
        {
            RequireGuid(request.Tenant, "tenant");
            RequireGuid(request.ApplicationId, "application id");
        }
        if (request.ClientSecret is "")
            throw new UsageException("A nonempty client secret is required.");
        if (request.CertificateDiskPath is not null && string.IsNullOrWhiteSpace(request.CertificateDiskPath))
            throw new UsageException("A certificate path is required.");
        if (request.Resource is not null && request.Scopes.Count != 0)
            throw new UsageException("Resource and explicit scopes cannot be combined.");
    }

    private static void RequireGuid(string? value, string field)
    {
        if (!Guid.TryParse(value, out var guid) || guid == Guid.Empty)
            throw new UsageException($"A nonzero GUID is required for {field}.");
    }
}
