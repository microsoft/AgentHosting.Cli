using System.Security.Cryptography;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentPackageUploadServiceTests
{
    private const string DataPlaneEndpoint = "https://amsdp.example.invalid/";
    private const string Agent = "44444444-4444-4444-4444-444444444444";
    private const string Environment = "22222222-2222-2222-2222-222222222222";
    private const string Repository = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Operation = "55555555-5555-5555-5555-555555555555";
    private const string Revision = "66666666-6666-6666-6666-666666666666";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_HashesAndSendsTheExactFile_AndReturnsTheDurableRevision(bool useRelativePath)
    {
        var packagePath = await WritePackageAsync([1, 2, 3, 4, 5]);
        try
        {
            var agents = new RecordingAgentReadTransport(BoundAgent());
            var packages = new RecordingAmsTransport(SucceededUpload);
            var service = Service(agents, packages);

            var selectedPath = useRelativePath
                ? Path.GetRelativePath(Directory.GetCurrentDirectory(), packagePath)
                : packagePath;
            selectedPath = Path.Combine(
                Path.GetDirectoryName(selectedPath)!,
                ".",
                Path.GetFileName(selectedPath));

            var result = await service.UploadAsync(Request(selectedPath), default);

            var expectedDigest = Convert.ToHexString(SHA256.HashData([1, 2, 3, 4, 5]))
                .ToLowerInvariant();
            Assert.Equal(1, agents.Calls);
            Assert.Equal(1, packages.Calls);
            Assert.Equal(packagePath, Assert.IsType<FileStream>(packages.LastRequest!.PackageStream).Name);
            Assert.Equal(DataPlaneEndpoint, packages.LastRequest!.Endpoint.AbsoluteUri);
            Assert.Equal(expectedDigest, packages.SentDigest);
            Assert.Equal(5, packages.LastRequest.PackageLength);
            Assert.Equal("package-uploaded", result.Status);
            Assert.Equal(Operation, result.PackageOperationId);
            Assert.Equal(Revision, result.PackageRevisionId);
            Assert.Equal(expectedDigest, result.PackageDigest);
            Assert.Equal(5, result.PackageSizeInBytes);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task Upload_MissingPackage_ReportsCanonicalPathBeforeReadingAgentOrSendingBytes()
    {
        var packagePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        var relativePath = Path.GetRelativePath(Directory.GetCurrentDirectory(), packagePath);
        var selectedPath = Path.Combine(Path.GetDirectoryName(relativePath)!, ".", Path.GetFileName(relativePath));
        var agents = new RecordingAgentReadTransport(BoundAgent());
        var packages = new RecordingAmsTransport(SucceededUpload);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(
            () => Service(agents, packages).UploadAsync(Request(selectedPath), default));

        Assert.Equal("package-read-failed", exception.ErrorKind);
        Assert.Contains($"'{packagePath}'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, agents.Calls);
        Assert.Equal(0, packages.Calls);
    }

    [Fact]
    public async Task Upload_InvalidPackagePath_ReturnsTypedFailureBeforeReadingAgentOrSendingBytes()
    {
        var agents = new RecordingAgentReadTransport(BoundAgent());
        var packages = new RecordingAmsTransport(SucceededUpload);

        var exception = await Assert.ThrowsAsync<AgentDeployException>(
            () => Service(agents, packages).UploadAsync(Request("invalid\0package.zip"), default));

        Assert.Equal("package-read-failed", exception.ErrorKind);
        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Equal(0, agents.Calls);
        Assert.Equal(0, packages.Calls);
    }

    [Fact]
    public async Task Upload_NeedsOnlyTheDataPlaneTarget_AndHasNoDeploymentDependency()
    {
        var packagePath = await WritePackageAsync([42]);
        try
        {
            var service = Service(
                new RecordingAgentReadTransport(BoundAgent()),
                new RecordingAmsTransport(SucceededUpload),
                new AgentHostingServiceTargets { DataPlaneEndpoint = DataPlaneEndpoint });

            var result = await service.UploadAsync(Request(packagePath), default);

            Assert.Equal(Revision, result.PackageRevisionId);
            Assert.DoesNotContain(
                typeof(AgentPackageUploadService).GetConstructors().Single().GetParameters(),
                parameter => parameter.ParameterType == typeof(IAmsDeploymentTransport));
            Assert.DoesNotContain(
                typeof(AgentPackageUploadResult).GetProperties(),
                property => property.Name.Contains("Deployment", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task Upload_RejectsAnEmptyPackageBeforeReadingAgentOrSendingBytes()
    {
        var packagePath = await WritePackageAsync([]);
        try
        {
            var agents = new RecordingAgentReadTransport(BoundAgent());
            var packages = new RecordingAmsTransport(SucceededUpload);

            var exception = await Assert.ThrowsAsync<AgentDeployException>(
                () => Service(agents, packages).UploadAsync(Request(packagePath), default));

            Assert.Equal("package-empty", exception.ErrorKind);
            Assert.Equal(0, agents.Calls);
            Assert.Equal(0, packages.Calls);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Theory]
    [InlineData("agentId", false)]
    [InlineData("agentId", true)]
    [InlineData("sha256", false)]
    [InlineData("sha256", true)]
    [InlineData("sizeInBytes", false)]
    [InlineData("sizeInBytes", true)]
    [InlineData("status", false)]
    [InlineData("status", true)]
    [InlineData("operationId", false)]
    [InlineData("operationId", true)]
    [InlineData("packageRevisionId", false)]
    [InlineData("packageRevisionId", true)]
    public async Task Upload_RejectsAnUnverifiedReceiptAndRetainsCorrelation(
        string field,
        bool includeServiceRequestId)
    {
        var packagePath = await WritePackageAsync([1, 2, 3]);
        try
        {
            var packages = new RecordingAmsTransport((request, digest) =>
            {
                var receipt = SucceededUpload(request, digest) with
                {
                    ClientRequestId = "client-request-id",
                    ServiceRequestId = includeServiceRequestId ? "service-request-id" : null,
                };
                return field switch
                {
                    "agentId" => receipt with { AgentId = Guid.Empty.ToString("D") },
                    "sha256" => receipt with { Sha256 = new string('f', 64) },
                    "sizeInBytes" => receipt with { SizeInBytes = request.PackageLength + 1 },
                    "status" => receipt with { Status = "Accepted" },
                    "operationId" => receipt with { OperationId = Guid.Empty.ToString("D") },
                    "packageRevisionId" => receipt with { PackageRevisionId = null },
                    _ => throw new ArgumentOutOfRangeException(nameof(field)),
                };
            });

            var exception = await Assert.ThrowsAsync<AgentDeployException>(
                () => Service(new RecordingAgentReadTransport(BoundAgent()), packages)
                    .UploadAsync(Request(packagePath), default));

            Assert.Equal(
                field is "agentId" or "sha256" or "sizeInBytes"
                    ? "ams-response-identity-mismatch"
                    : "invalid-ams-response",
                exception.ErrorKind);
            Assert.Equal(ExitCodes.Failure, exception.ExitCode);
            Assert.Contains("clientRequestId=client-request-id", exception.Message, StringComparison.Ordinal);
            if (includeServiceRequestId)
            {
                Assert.Contains("serviceRequestId=service-request-id", exception.Message, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("serviceRequestId=", exception.Message, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(Revision, exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, packages.Calls);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    [Fact]
    public async Task Upload_RejectsAnUnboundAgentBeforeSendingBytes()
    {
        var packagePath = await WritePackageAsync([1]);
        try
        {
            var packages = new RecordingAmsTransport(SucceededUpload);

            var exception = await Assert.ThrowsAsync<AgentDeployException>(
                () => Service(
                        new RecordingAgentReadTransport(
                            BoundAgent() with { RepositoryBindingStatus = "Unbound" }),
                        packages)
                    .UploadAsync(Request(packagePath), default));

            Assert.Equal("agent-not-deployable", exception.ErrorKind);
            Assert.Equal(0, packages.Calls);
        }
        finally
        {
            File.Delete(packagePath);
        }
    }

    private static AgentPackageUploadRequest Request(string packagePath) => new()
    {
        AgentId = Agent,
        PackagePath = packagePath,
        EnvironmentId = Environment,
    };

    private static AmsAgentReadResult BoundAgent() => new()
    {
        AgentId = Agent,
        TenantId = EnvironmentFixtures.ContosoTenant,
        EnvironmentId = Environment,
        RepositoryId = Repository,
        RepositoryBindingStatus = "Bound",
        AgentType = "CodeAgent",
        Language = "DotNet",
        LanguageVersion = "10",
    };

    private static AmsPackagePublicationResult SucceededUpload(
        AmsPackagePublicationTransportRequest request,
        string digest) => new()
        {
            OperationId = Operation,
            AgentId = request.AgentId,
            Sha256 = digest,
            SizeInBytes = request.PackageLength,
            Status = "Succeeded",
            PackageRevisionId = Revision,
            CreatedTime = DateTimeOffset.UnixEpoch,
            ChangedTime = DateTimeOffset.UnixEpoch,
        };

    private static AgentPackageUploadService Service(
        IAmsAgentReadTransport agents,
        IAmsPackagePublicationTransport packages,
        AgentHostingServiceTargets? targets = null)
    {
        var profiles = InMemoryAuthProfileStore.SignedIn(
            "op@contoso.com",
            EnvironmentFixtures.ContosoTenant);
        var environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(EnvironmentFixtures.Environment(Environment)),
            new InMemoryEnvironmentSelectionStore(),
            profiles);
        environments.SelectAsync(Environment, default).GetAwaiter().GetResult();

        return new AgentPackageUploadService(
            targets ?? new AgentHostingServiceTargets { DataPlaneEndpoint = DataPlaneEndpoint },
            environments,
            agents,
            packages);
    }

    private static async Task<string> WritePackageAsync(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private sealed class RecordingAgentReadTransport(AmsAgentReadResult result) : IAmsAgentReadTransport
    {
        public int Calls { get; private set; }

        public Task<AmsAgentReadResult> GetAsync(
            AmsAgentReadTransportRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
