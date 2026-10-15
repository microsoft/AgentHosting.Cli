using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests.Ams;

public sealed class AgentCreateServiceTests
{
    private const string Endpoint = "https://amsdp-alias.example";

    [Fact]
    public async Task Create_UsesConfiguredDevOverrideAndSelectedEnvironment_WithoutRepositoryInput()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));

        var result = await Service(transport).CreateAsync(Request(), default);

        var sent = Assert.IsType<AmsAgentCreationTransportRequest>(transport.LastRequest);
        Assert.Equal(new Uri(Endpoint), sent.Endpoint);
        Assert.Equal(AmsAgentCreationRouteMode.DirectDataPlane, sent.RouteMode);
        Assert.Equal("env-1", sent.EnvironmentId);
        Assert.Equal(EnvironmentFixtures.ContosoTenant, sent.TenantId);
        Assert.Equal(EnvironmentFixtures.Audience, sent.Audience);
        Assert.True(Guid.TryParse(sent.IdempotencyKey, out _));
        Assert.Equal("My Agent", sent.DisplayName);
        Assert.Equal(0, sent.AgentType);
        Assert.Equal(1, sent.Language);
        Assert.Equal("net10.0", sent.LanguageVersion);
        Assert.Equal("AgentHostCLI", sent.Origin);

        Assert.DoesNotContain(
            typeof(AmsAgentCreationTransportRequest).GetProperties(),
            property => property.Name.Contains("Repositor", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Storage", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Owner", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Identity", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Entra", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("agent-created", result.Status);
        Assert.Equal("agent-1", result.AgentId);
        Assert.Equal("repo-1", result.RepositoryId);
    }

    [Fact]
    public async Task Create_WithExplicitIdempotencyKey_PreservesItForRetry()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));
        var request = Request() with { IdempotencyKey = "retry-key-1" };

        await Service(transport).CreateAsync(request, default);

        Assert.Equal("retry-key-1", transport.LastRequest!.IdempotencyKey);
    }

    [Fact]
    public async Task Create_WithGeneratedIdempotencyKey_RepositoryFailureSurfacesTheExactKey()
    {
        var transport = new RecordingAmsAgentCreationTransport(
            (_, _) => Task.FromException<AmsAgentCreationResult>(
                AgentCreationException.ServiceRejected(
                    "agent-repository-binding-failed",
                    "Repository binding failed.",
                    ExitCodes.Failure,
                    "Do not retry this create blindly. Inspect the recorded service state.")));

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Service(transport).CreateAsync(Request(), default));

        var generatedKey = transport.LastRequest!.IdempotencyKey;
        Assert.True(Guid.TryParse(generatedKey, out _));
        Assert.Equal("agent-repository-binding-failed", exception.ErrorKind);
        Assert.Contains("Do not retry this create blindly", exception.Remediation, StringComparison.Ordinal);
        Assert.Contains(generatedKey, exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("quote or escape", exception.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WithGeneratedIdempotencyKey_ResponseDecodeFailureSurfacesTheExactKey()
    {
        var transport = new RecordingAmsAgentCreationTransport(
            (request, _) => Task.FromException<AmsAgentCreationResult>(
                AgentCreationException.ResponseDecodingFailed(
                    "client-request-id",
                    request.IdempotencyKey,
                    new InvalidOperationException("unsupported response charset"))));

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Service(transport).CreateAsync(Request(), default));

        var generatedKey = transport.LastRequest!.IdempotencyKey;
        Assert.True(Guid.TryParse(generatedKey, out _));
        Assert.Equal("agent-create-response-unreadable", exception.ErrorKind);
        Assert.Contains(generatedKey, exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("quote or escape", exception.Remediation, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unsupported response charset", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_CallerCancellationSurfacesTheExactIdempotencyKey()
    {
        using var cancellation = new CancellationTokenSource();
        var transport = new RecordingAmsAgentCreationTransport(
            (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<AmsAgentCreationResult>(token);
            });

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Service(transport).CreateAsync(Request(), cancellation.Token));

        var generatedKey = transport.LastRequest!.IdempotencyKey;
        Assert.True(Guid.TryParse(generatedKey, out _));
        Assert.Equal("agent-create-canceled", exception.ErrorKind);
        Assert.Contains("outcome may be indeterminate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(generatedKey, exception.Remediation, StringComparison.Ordinal);
        Assert.Contains("quote or escape", exception.Remediation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_WithInvalidIdempotencyKey_SendsNothing()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));
        var request = Request() with { IdempotencyKey = "invalid key" };

        await Assert.ThrowsAsync<UsageException>(() =>
            Service(transport).CreateAsync(request, default));

        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Create_WithoutConfiguredOverride_UsesTheEnvironmentApiEndpoint()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));

        await Service(transport, new AgentCreationTarget()).CreateAsync(Request(), default);

        Assert.Equal(new Uri("https://env-1.api.powerplatform.com/"), transport.LastRequest!.Endpoint);
        Assert.Equal(
            AmsAgentCreationRouteMode.PowerPlatformApiFrontDoor,
            transport.LastRequest.RouteMode);
    }

    [Fact]
    public async Task Create_WithInvalidConfiguredOverride_SendsNothing()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Service(
                transport,
                new AgentCreationTarget { Endpoint = "http://amsdp-alias.example" })
                .CreateAsync(Request(), default));

        Assert.Equal("endpoint-untrusted", exception.ErrorKind);
        Assert.Contains(AgentCreationTarget.EndpointVariableName, exception.Remediation, StringComparison.Ordinal);
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Create_WithoutSelectedEnvironment_SendsNothing()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));
        var environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1")),
            new InMemoryEnvironmentSelectionStore(),
            InMemoryAuthProfileStore.SignedIn("op@contoso.com", EnvironmentFixtures.ContosoTenant));
        var service = new AgentCreateService(
            new AgentCreationTarget { Endpoint = Endpoint },
            environments,
            transport);

        await Assert.ThrowsAsync<EnvironmentNotSelectedException>(() =>
            service.CreateAsync(Request(), default));

        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Create_WithExplicitEnvironment_ResolvesLiveWithoutPersistingIt()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));
        var selection = new InMemoryEnvironmentSelectionStore();
        var environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(
                EnvironmentFixtures.Environment("env-1"),
                EnvironmentFixtures.Environment(
                    "env-2",
                    "Contoso Test",
                    endpoint: "https://env-2.api.powerplatform.com/")),
            selection,
            InMemoryAuthProfileStore.SignedIn(
                "op@contoso.com",
                EnvironmentFixtures.ContosoTenant));
        await environments.SelectAsync("env-1", default);
        var service = new AgentCreateService(new AgentCreationTarget(), environments, transport);

        await service.CreateAsync(Request() with { EnvironmentId = "env-2" }, default);

        Assert.Equal("env-2", transport.LastRequest!.EnvironmentId);
        Assert.Equal(new Uri("https://env-2.api.powerplatform.com/"), transport.LastRequest.Endpoint);
        Assert.Equal(
            AmsAgentCreationRouteMode.PowerPlatformApiFrontDoor,
            transport.LastRequest.RouteMode);
        Assert.Equal("env-1", selection.Active!.EnvironmentId);
        Assert.Equal(1, selection.WriteCalls);
    }

    [Fact]
    public async Task Create_WithExplicitEnvironment_DoesNotRequireAnActiveSelection()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));
        var selection = new InMemoryEnvironmentSelectionStore();
        var environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-2")),
            selection,
            InMemoryAuthProfileStore.SignedIn(
                "op@contoso.com",
                EnvironmentFixtures.ContosoTenant));
        var service = new AgentCreateService(new AgentCreationTarget(), environments, transport);

        await service.CreateAsync(Request() with { EnvironmentId = "env-2" }, default);

        Assert.Equal("env-2", transport.LastRequest!.EnvironmentId);
        Assert.Null(selection.Active);
        Assert.Equal(0, selection.WriteCalls);
    }

    [Fact]
    public async Task Create_WithUnavailableExplicitEnvironment_SendsNothing()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));
        var environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1")),
            new InMemoryEnvironmentSelectionStore(),
            InMemoryAuthProfileStore.SignedIn(
                "op@contoso.com",
                EnvironmentFixtures.ContosoTenant));
        var service = new AgentCreateService(new AgentCreationTarget(), environments, transport);

        await Assert.ThrowsAsync<EnvironmentNotFoundException>(() =>
            service.CreateAsync(Request() with { EnvironmentId = "env-missing" }, default));

        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Create_WithUnsupportedLanguage_SendsNothing()
    {
        var transport = new RecordingAmsAgentCreationTransport(request => Result(request));
        var request = Request() with { Language = "java" };

        await Assert.ThrowsAsync<UsageException>(() =>
            Service(transport).CreateAsync(request, default));

        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Create_ResponseForAnotherEnvironment_IsRejected()
    {
        var transport = new RecordingAmsAgentCreationTransport(request =>
            Result(request) with { EnvironmentId = "other-env" });

        var exception = await Assert.ThrowsAsync<AgentCreationException>(() =>
            Service(transport).CreateAsync(Request(), default));

        Assert.Equal("ams-response-identity-mismatch", exception.ErrorKind);
        Assert.Contains(
            transport.LastRequest!.IdempotencyKey,
            exception.Remediation,
            StringComparison.Ordinal);
    }

    private static AgentCreateRequest Request() => new()
    {
        DisplayName = "My Agent",
        Description = "description",
        Language = "dotnet",
        LanguageVersion = "net10.0",
    };

    private static AmsAgentCreationResult Result(AmsAgentCreationTransportRequest request) => new()
    {
        AgentId = "agent-1",
        TenantId = request.TenantId,
        EnvironmentId = request.EnvironmentId,
        DisplayName = request.DisplayName,
        RepositoryId = "repo-1",
    };

    private static AgentCreateService Service(
        IAmsAgentCreationTransport transport,
        AgentCreationTarget? target = null)
    {
        var profiles = InMemoryAuthProfileStore.SignedIn(
            "op@contoso.com",
            EnvironmentFixtures.ContosoTenant);
        var selectionStore = new InMemoryEnvironmentSelectionStore();
        var environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(EnvironmentFixtures.Environment("env-1")),
            selectionStore,
            profiles);
        environments.SelectAsync("env-1", default).GetAwaiter().GetResult();

        return new AgentCreateService(
            target ?? new AgentCreationTarget { Endpoint = Endpoint },
            environments,
            transport);
    }
}
