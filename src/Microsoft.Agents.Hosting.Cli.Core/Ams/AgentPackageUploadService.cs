using System.Security.Cryptography;
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

    private readonly AmsAddressResolver _addresses;
    private readonly IAmsAgentReadTransport _agents;
    private readonly IAmsPackagePublicationTransport _packages;

    /// <summary>Creates the service over the AMS agent read and package upload transports.</summary>
    public AgentPackageUploadService(AmsAddressResolver addresses, IAmsAgentReadTransport agents, IAmsPackagePublicationTransport packages)
    {
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _packages = packages ?? throw new ArgumentNullException(nameof(packages));
    }

    public async Task<AgentPackageUploadResult> UploadAsync(
        AgentPackageUploadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var packagePath = Require(request.PackagePath, "--package");
        var address = await _addresses.ResolveAgentAsync(request, "agent package upload", cancellationToken).ConfigureAwait(false);
        var agentId = address.AgentId;
        var environment = address.Home.Environment;
        var serviceTarget = address.Home.DataPlane;

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
        var agent = await _agents.GetAsync(address.OnDataPlane, cancellationToken).ConfigureAwait(false);
        AmsAgentResponseValidator.ValidateBoundCodeAgent(agent, environment, agentId);

        var uploaded = await _packages
            .PublishAsync(
                new AmsPackagePublicationTransportRequest
                {
                    ServiceTarget = serviceTarget,
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
            CommitId = uploaded.CommitId,
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

    private static bool EqualsId(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool TryParseNonEmptyGuid(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty;
}
