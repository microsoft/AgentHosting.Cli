using System.Security.Cryptography;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// Uploads one exact opaque package to home AMS-DataPlane and returns its durable Package Revision.
/// It does not create or project a Deployment.
/// </summary>
public sealed class AgentPackageUploadService
{
    public const long MaxPackageSizeBytes = 128L * 1024L * 1024L;
    private const int ReadBufferSizeBytes = 81_920;

    private readonly EnvironmentSelectionService _environments;
    private readonly IAmsAgentReadTransport _agents;
    private readonly IAmsPackagePublicationTransport _packages;

    public AgentPackageUploadService(
        EnvironmentSelectionService environments,
        IAmsAgentReadTransport agents,
        IAmsPackagePublicationTransport packages)
    {
        _environments = environments ?? throw new ArgumentNullException(nameof(environments));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
    }

    public async Task<AgentPackageUploadResult> UploadAsync(
        AgentPackageUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agentId = RequireGuid(request.AgentId, "--agent");
        var packagePath = Require(request.PackagePath, "--package");
        var environment = await _environments
            .ResolveForCommandAsync(request.EnvironmentId, cancellationToken)
            .ConfigureAwait(false);

        await using var package = OpenPackage(packagePath);
        if (package.Length == 0)
        {
            throw AgentDeployException.PackageEmpty();
        }

        if (package.Length > MaxPackageSizeBytes)
        {
            throw AgentDeployException.PackageTooLarge(package.Length, MaxPackageSizeBytes);
        }

        var packageDigest = await HashAndRewindAsync(package, cancellationToken)
            .ConfigureAwait(false);
        var agent = await _agents
            .GetAsync(
                new AmsAgentReadTransportRequest
                {
                    Endpoint = environment.ApiEndpoint,
                    Audience = environment.ApiAudience,
                    TenantId = environment.TenantId,
                    EnvironmentId = environment.EnvironmentId,
                    AgentId = agentId,
                },
                cancellationToken)
            .ConfigureAwait(false);
        AmsAgentResponseValidator.ValidateBoundCodeAgent(agent, environment, agentId);

        var uploaded = await _packages
            .PublishAsync(
                new AmsPackagePublicationTransportRequest
                {
                    Endpoint = environment.ApiEndpoint,
                    Audience = environment.ApiAudience,
                    TenantId = environment.TenantId,
                    EnvironmentId = environment.EnvironmentId,
                    AgentId = agentId,
                    PackageStream = package,
                    PackageLength = package.Length,
                },
                cancellationToken)
            .ConfigureAwait(false);
        ValidateUpload(uploaded, agentId, packageDigest, package.Length);

        return new AgentPackageUploadResult
        {
            Status = "package-uploaded",
            TenantId = environment.TenantId,
            EnvironmentId = environment.EnvironmentId,
            AgentId = agentId,
            PackageOperationId = uploaded.OperationId,
            PackageRevisionId = uploaded.PackageRevisionId!,
            PackageDigest = packageDigest,
            PackageSizeInBytes = package.Length,
        };
    }

    private static FileStream OpenPackage(string packagePath)
    {
        try
        {
            packagePath = Path.GetFullPath(packagePath);
            return new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ReadBufferSizeBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException)
        {
            throw AgentDeployException.PackageReadFailed(packagePath, ex);
        }
    }

    private static async Task<string> HashAndRewindAsync(
        FileStream package,
        CancellationToken cancellationToken)
    {
        try
        {
            var digest = await SHA256.HashDataAsync(package, cancellationToken).ConfigureAwait(false);
            package.Position = 0;
            return Convert.ToHexString(digest).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw AgentDeployException.PackageReadFailed(package.Name, ex);
        }
    }

    private static void ValidateUpload(
        AmsPackagePublicationResult upload,
        string agentId,
        string packageDigest,
        long packageLength)
    {
        if (!EqualsId(upload.AgentId, agentId))
        {
            throw AgentDeployException.ResponseMismatch(
                "package upload", "agentId", upload.ClientRequestId, upload.ServiceRequestId);
        }

        if (!string.Equals(upload.Sha256, packageDigest, StringComparison.OrdinalIgnoreCase))
        {
            throw AgentDeployException.ResponseMismatch(
                "package upload", "sha256", upload.ClientRequestId, upload.ServiceRequestId);
        }

        if (upload.SizeInBytes != packageLength)
        {
            throw AgentDeployException.ResponseMismatch(
                "package upload", "sizeInBytes", upload.ClientRequestId, upload.ServiceRequestId);
        }

        if (!string.Equals(upload.Status, "Succeeded", StringComparison.Ordinal) ||
            !TryParseNonEmptyGuid(upload.OperationId) ||
            !TryParseNonEmptyGuid(upload.PackageRevisionId))
        {
            throw AgentDeployException.InvalidResponse(
                "package upload", upload.ClientRequestId, upload.ServiceRequestId);
        }
    }

    private static string Require(string? value, string option)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new UsageException($"{option} is required for `msagent agent package upload`.");
        }

        return value.Trim();
    }

    private static string RequireGuid(string? value, string option)
    {
        var result = Require(value, option);
        if (!Guid.TryParse(result, out var parsed) || parsed == Guid.Empty)
        {
            throw new UsageException($"{option} must be a non-empty GUID.");
        }

        return parsed.ToString("D");
    }

    private static bool EqualsId(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseNonEmptyGuid(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty;
}
