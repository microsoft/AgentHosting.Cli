using System.Net;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Environments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class PowerAppsConnectionClientTests
{
    private static PowerAppsConnectionClient Client(ScriptedHandler handler, RecordingTokenProvider tokens, CoreServicesClusterCategory cloud = CoreServicesClusterCategory.Prod) =>
        new(tokens, cloud, () => handler);

    [Fact]
    public async Task ReadConnection_TargetsThePowerAppsApisResource()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok(ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler, new RecordingTokenProvider());

        await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("api.powerapps.com", request.Uri.Host);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_office365/connections/conn-1", request.Uri.AbsolutePath);
        Assert.Contains("api-version=2016-11-01", request.Uri.Query, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString($"environment eq '{ConnectionFixtures.EnvironmentId}'"), request.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("management-token", request.Authorization);
    }

    [Fact]
    public async Task CreateConnection_SendsTheEnvironmentNameBodyTheServiceRequires()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Json(HttpStatusCode.Created, ConnectionFixtures.Connection(authenticated: false)));
        using var client = Client(handler, new RecordingTokenProvider());

        await client.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", null, CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_office365/connections/conn-1", request.Uri.AbsolutePath);

        using var body = JsonDocument.Parse(request.Body);

        Assert.Equal(ConnectionFixtures.EnvironmentId, body.RootElement.GetProperty("properties").GetProperty("environment").GetProperty("name").GetString());
        Assert.False(body.RootElement.GetProperty("properties").TryGetProperty("connectionParametersSet", out _));
    }

    [Fact]
    public async Task CreateConnection_NamesTheParameterSetWhenOneIsSupplied()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Json(HttpStatusCode.Created, "{}"));
        using var client = Client(handler, new RecordingTokenProvider());

        await client.CreateConnectionAsync(ConnectionFixtures.Environment, "shared_sql", "conn-1", "oauth", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var set = body.RootElement.GetProperty("properties").GetProperty("connectionParametersSet");

        Assert.Equal("oauth", set.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Object, set.GetProperty("values").ValueKind);
    }

    [Fact]
    public async Task GetConsentLink_PostsTheCallerSuppliedLandingPage()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse()));
        using var client = Client(handler, new RecordingTokenProvider());

        await client.GetConsentLinkAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", ConnectionFixtures.LandingUrl, CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/connections/conn-1/getConsentLink", request.Uri.AbsolutePath, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(request.Body);

        Assert.Equal(ConnectionFixtures.LandingUrl, body.RootElement.GetProperty("redirectUrl").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetConsentLink_FallsBackToANeutralPageRatherThanTheConnectorOAuthCallback(string redirectUrl)
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok(ConnectionFixtures.ConsentResponse()));
        using var client = Client(handler, new RecordingTokenProvider());

        await client.GetConsentLinkAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", redirectUrl, CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var sent = body.RootElement.GetProperty("redirectUrl").GetString();

        Assert.Equal(PowerAppsConnectionClient.NeutralConsentRedirectUrl, sent);
        Assert.DoesNotContain("consent.azure-apim.net", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmConsentCode_PostsTheCodeForThePrincipalTheTokenNames()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok("{}"));
        using var client = Client(handler, new RecordingTokenProvider { AccessTokenReturns = FakeJwt.Create(tenantId: "tenant-7", objectId: "guest-oid") });

        await client.ConfirmConsentCodeAsync(ConnectionFixtures.EnvironmentIn("tenant-7"), "shared_office365", "conn-1", "consent-code", CancellationToken.None);

        var request = Assert.Single(handler.Requests);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/connections/conn-1/confirmConsentCode", request.Uri.AbsolutePath, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(request.Body);

        Assert.Equal("consent-code", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("guest-oid", body.RootElement.GetProperty("objectId").GetString());
        Assert.Equal("tenant-7", body.RootElement.GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task ConfirmConsentCode_OmitsTheObjectIdWhenTheTokenCarriesNoClaims()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok("{}"));
        using var client = Client(handler, new RecordingTokenProvider());

        await client.ConfirmConsentCodeAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", "consent-code", CancellationToken.None);

        using var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body);

        Assert.Equal("consent-code", body.RootElement.GetProperty("code").GetString());
        Assert.False(body.RootElement.TryGetProperty("objectId", out _));
    }

    [Fact]
    public async Task DeleteConnection_TargetsTheConnectionResource()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok("{}"));
        using var client = Client(handler, new RecordingTokenProvider());

        await client.DeleteConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.Equal(HttpMethod.Delete, Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task Requests_NormalizeAFullProviderPathToItsInternalId()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok("{}"));
        using var client = Client(handler, new RecordingTokenProvider());

        await client.ReadConnectionAsync(ConnectionFixtures.Environment, "/providers/Microsoft.PowerApps/apis/shared_teams", "/providers/Microsoft.PowerApps/apis/shared_teams/connections/conn-9", CancellationToken.None);

        Assert.Equal("/providers/Microsoft.PowerApps/apis/shared_teams/connections/conn-9", Assert.Single(handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task AccessToken_IsAcquiredOnceAndReusedAcrossRequests()
    {
        using var handler = new ScriptedHandler(_ => ConnectionFixtures.Ok("{}"));
        var tokens = new RecordingTokenProvider();
        using var client = Client(handler, tokens);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);
        }

        Assert.Single(tokens.Resources);
        Assert.Equal("https://api.bap.microsoft.com", tokens.Resources[0]);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task AccessToken_IsReacquiredWhenTheCachedOneIsAboutToExpire()
    {
        using var handler = new ScriptedHandler(_ => ConnectionFixtures.Ok("{}"));
        var tokens = new RecordingTokenProvider { ExpiresOn = DateTimeOffset.UtcNow.AddSeconds(30) };
        using var client = Client(handler, tokens);

        await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);
        await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.Equal(2, tokens.Resources.Count);
    }

    [Fact]
    public async Task AccessToken_IsRequestedForTheEnvironmentsTenant()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok("{}"));
        var tokens = new RecordingTokenProvider();
        using var client = Client(handler, tokens);

        await client.DeleteConnectionAsync(ConnectionFixtures.EnvironmentIn("tenant-1"), "shared_office365", "conn-1", CancellationToken.None);

        Assert.Equal("tenant-1", Assert.Single(tokens.Tenants));
    }

    [Theory]
    [InlineData("shared_office365")]
    [InlineData("/providers/Microsoft.PowerApps/apis/shared_office365")]
    public async Task AccessToken_IsRequestedForTheEnvironmentsTenantOnEveryCall(string connectorId)
    {
        using var handler = new ScriptedHandler(_ => ConnectionFixtures.Ok("{}"));
        var tokens = new RecordingTokenProvider();
        var environment = ConnectionFixtures.EnvironmentIn("guest-tenant");
        using var client = Client(handler, tokens);

        await client.CreateConnectionAsync(environment, connectorId, "conn-1", null, CancellationToken.None);
        await client.GetConsentLinkAsync(environment, connectorId, "conn-1", ConnectionFixtures.LandingUrl, CancellationToken.None);
        await client.ReadConnectionAsync(environment, connectorId, "conn-1", CancellationToken.None);
        await client.DeleteConnectionAsync(environment, connectorId, "conn-1", CancellationToken.None);

        Assert.All(tokens.Tenants, tenant => Assert.Equal("guest-tenant", tenant));
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task AccessToken_IsNotSentWhenTheLibraryAcquiredAnotherTenantsToken()
    {
        using var handler = new ScriptedHandler(_ => ConnectionFixtures.Ok("{}"));
        var tokens = new RecordingTokenProvider { AcquiredTenantId = "another-tenant" };
        using var client = Client(handler, tokens);

        var failure = await Assert.ThrowsAsync<TokenClaimException>(
            () => client.ReadConnectionAsync(ConnectionFixtures.EnvironmentIn("tenant-1"), "shared_office365", "conn-1", CancellationToken.None));

        Assert.Equal(TokenClaimFailure.WrongTenant, failure.Failure);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AccessToken_IsNotReusedAcrossTenants()
    {
        using var handler = new ScriptedHandler(_ => ConnectionFixtures.Ok("{}"));
        var tokens = new RecordingTokenProvider();
        using var client = Client(handler, tokens);

        await client.ReadConnectionAsync(ConnectionFixtures.EnvironmentIn("tenant-1"), "shared_office365", "conn-1", CancellationToken.None);
        await client.ReadConnectionAsync(ConnectionFixtures.EnvironmentIn("tenant-2"), "shared_office365", "conn-1", CancellationToken.None);

        Assert.Equal(["tenant-1", "tenant-2"], tokens.Tenants);
    }

    [Theory]
    [InlineData(CoreServicesClusterCategory.Prod, "api.powerapps.com")]
    [InlineData(CoreServicesClusterCategory.Gov, "gov.api.powerapps.us")]
    [InlineData(CoreServicesClusterCategory.Mooncake, "api.powerapps.cn")]
    public async Task Requests_UseTheCloudsApiHost(CoreServicesClusterCategory cloud, string expectedHost)
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok("{}"));
        using var client = Client(handler, new RecordingTokenProvider(), cloud);

        await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.Equal(expectedHost, Assert.Single(handler.Requests).Uri.Host);
    }

    [Fact]
    public async Task Response_ReportsFailureWithoutThrowing()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Json(HttpStatusCode.Forbidden, "denied"));
        using var client = Client(handler, new RecordingTokenProvider());

        var response = await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("denied", response.Body);
    }

    [Fact]
    public async Task Request_CarriesAClientRequestIdTheServiceCanBeAskedAbout()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok(ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler, new RecordingTokenProvider());

        var response = await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);
        var sent = Assert.Single(handler.Requests).ClientRequestId;

        Assert.True(Guid.TryParse(sent, out _));
        Assert.Equal(sent, response.Correlation?.ClientRequestId);
    }

    [Fact]
    public async Task Request_CarriesADistinctClientRequestIdEachTime()
    {
        using var handler = new ScriptedHandler(_ => ConnectionFixtures.Ok(ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler, new RecordingTokenProvider());

        await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);
        await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Select(request => request.ClientRequestId).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Response_CarriesTheServiceRequestIdTheServiceAnsweredWith()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Json(HttpStatusCode.Forbidden, "denied", "service-7"));
        using var client = Client(handler, new RecordingTokenProvider());

        var response = await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.Equal("service-7", response.Correlation?.ServiceRequestId);
        Assert.Contains("serviceRequestId=service-7", response.CorrelationDetail, StringComparison.Ordinal);
        Assert.Contains("(clientRequestId=", response.Correlated("it failed"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_ReportsABodyThatOutgrowsTheLimitRatherThanTruncatingIt()
    {
        var oversized = "{\"properties\":{\"displayName\":\"" + new string('x', (1024 * 1024) + 5000) + "\"}}";

        using var handler = new ScriptedHandler(ConnectionFixtures.Ok(oversized));
        using var client = Client(handler, new RecordingTokenProvider());

        var response = await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.True(response.Oversized);
        Assert.Equal(string.Empty, response.Body);
    }

    [Fact]
    public async Task Response_DoesNotReportAnOversizedBodyForOneWithinTheLimit()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Ok(ConnectionFixtures.ReadyConnection()));
        using var client = Client(handler, new RecordingTokenProvider());

        var response = await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.False(response.Oversized);
        Assert.Equal(ConnectionFixtures.ReadyConnection(), response.Body);
    }

    [Fact]
    public async Task Response_StopsReadingAnOversizedBodyInsteadOfConsumingIt()
    {
        using var content = new CountingContent((1024 * 1024) * 8);
        using var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = Client(handler, new RecordingTokenProvider());

        var response = await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.True(response.Oversized);
        Assert.True(content.BytesProduced <= (1024 * 1024) + 65536, $"read {content.BytesProduced} bytes of an 8 MiB response");
    }

    [Fact]
    public async Task Diagnostics_ReportAnOversizedBodyWithoutItsContent()
    {
        using var diagnostics = new StringWriter();
        var oversized = "{\"consentLink\":\"SECRET123\",\"pad\":\"" + new string('x', (1024 * 1024) + 5000) + "\"}";

        using var handler = new ScriptedHandler(ConnectionFixtures.Ok(oversized));
        using var client = new PowerAppsConnectionClient(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler, diagnostics);

        await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        var written = diagnostics.ToString();

        Assert.Contains("discarded unread", written, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET123", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Response_StopsAStalledBodyAtTheRequestTimeout()
    {
        using var content = new StallingContent();
        using var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new PowerAppsConnectionClient(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler, requestTimeout: TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Delete_StopsAStalledBodyEvenWhenNothingCanCancelIt()
    {
        using var content = new StallingContent();
        using var handler = new ScriptedHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        using var client = new PowerAppsConnectionClient(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler, requestTimeout: TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.DeleteConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class StallingContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new StallingStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.Delay(Timeout.InfiniteTimeSpan);

        protected override bool TryComputeLength(out long length)
        {
            length = long.MaxValue;

            return false;
        }
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);

            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingContent : HttpContent
    {
        private readonly long _length;
        private readonly GeneratedStream _stream;

        public CountingContent(long length)
        {
            _length = length;
            _stream = new GeneratedStream(length);
        }

        public long BytesProduced => _stream.BytesProduced;

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(_stream);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = _length;

            return true;
        }
    }

    private sealed class GeneratedStream : Stream
    {
        private const int ChunkSize = 64 * 1024;

        private readonly long _length;

        public GeneratedStream(long length)
        {
            _length = length;
        }

        public long BytesProduced { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => BytesProduced;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - BytesProduced;

            if (remaining <= 0)
            {
                return 0;
            }

            var take = (int)Math.Min(count, Math.Min(remaining, ChunkSize));

            Array.Fill(buffer, (byte)'x', offset, take);
            BytesProduced += take;

            return take;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Diagnostics_RecordTheRedactedAnswerWhenAnOperatorAskedForThem()
    {
        using var diagnostics = new StringWriter();
        using var handler = new ScriptedHandler(ConnectionFixtures.Json(HttpStatusCode.Forbidden, "{\"consentLink\":\"https://login.example/?code=abc123\",\"message\":\"contoso-sql-01 refused\"}", "service-7"));
        using var client = new PowerAppsConnectionClient(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler, diagnostics);

        await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        var written = diagnostics.ToString();

        Assert.Contains("HTTP 403", written, StringComparison.Ordinal);
        Assert.Contains("serviceRequestId=service-7", written, StringComparison.Ordinal);
        Assert.Contains("contoso-sql-01 refused", written, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_RecordATransportFailureAnOperatorFacingMessageOmits()
    {
        using var diagnostics = new StringWriter();
        using var handler = new ScriptedHandler(_ => throw new HttpRequestException("the network is unreachable"));
        using var client = new PowerAppsConnectionClient(new RecordingTokenProvider(), CoreServicesClusterCategory.Prod, () => handler, diagnostics);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None));

        var written = diagnostics.ToString();

        Assert.Contains("HttpRequestException", written, StringComparison.Ordinal);
        Assert.Contains("the network is unreachable", written, StringComparison.Ordinal);
        Assert.Contains("clientRequestId=", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_AreSilentWhenNoOperatorAskedForThem()
    {
        using var handler = new ScriptedHandler(ConnectionFixtures.Json(HttpStatusCode.Forbidden, "denied"));
        using var client = Client(handler, new RecordingTokenProvider());

        var response = await client.ReadConnectionAsync(ConnectionFixtures.Environment, "shared_office365", "conn-1", CancellationToken.None);

        Assert.Equal("denied", response.Body);
    }
}
