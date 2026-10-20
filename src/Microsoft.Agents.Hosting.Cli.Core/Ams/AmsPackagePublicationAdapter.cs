using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Http;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Core.Ams;

/// <summary>
/// Streams the exact caller-supplied package once to the AMS-DP v1 upload route. Redirect following
/// and automatic mutation retries are deliberately absent.
/// </summary>
public sealed class AmsPackagePublicationAdapter : IAmsPackagePublicationTransport
{
    public const string ClientRequestIdHeaderName = "x-ms-client-request-id";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly ITokenProvider _tokens;
    private readonly PpapiTokenClaimsValidator _claimsValidator;

    public AmsPackagePublicationAdapter(
        HttpClient httpClient,
        ITokenProvider tokens,
        PpapiTokenClaimsValidator? claimsValidator = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _claimsValidator = claimsValidator ?? new PpapiTokenClaimsValidator();
    }

    public async Task<AmsPackagePublicationResult> PublishAsync(
        AmsPackagePublicationTransportRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.PackageStream);

        if (request.PackageLength <= 0 || !request.PackageStream.CanRead)
        {
            throw new ArgumentException("A non-empty readable package stream is required.", nameof(request));
        }

        var relativePath =
            $"agenthosting/agents/{Uri.EscapeDataString(request.AgentId)}/packages?api-version=1";
        var targetUri = new Uri(EnsureTrailingSlash(request.Endpoint), relativePath);
        var clientRequestId = Guid.NewGuid().ToString("D");
        var token = await _tokens
            .AcquireTokenSilentAsync(request.Audience, request.TenantId, cancellationToken)
            .ConfigureAwait(false);
        _claimsValidator.Validate(
            token.AccessToken,
            request.Audience,
            request.TenantId,
            PowerPlatformApiPermissions.AppsUpdate);

        using var message = new HttpRequestMessage(HttpMethod.Post, targetUri);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.TryAddWithoutValidation(ClientRequestIdHeaderName, clientRequestId);
        var content = new StreamContent(new BorrowedStream(request.PackageStream));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Headers.ContentLength = request.PackageLength;
        message.Content = content;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw PackagePublicationException.RequestTimedOut(
                request.AgentId,
                clientRequestId,
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw PackagePublicationException.RequestFailed(clientRequestId, ex);
        }

        using (response)
        {
            if (IsRedirect(response.StatusCode))
            {
                throw AgentDeployException.RedirectRejected(
                    "package upload",
                    (int)response.StatusCode,
                    request.Endpoint.Host);
            }

            string responseBody;
            try
            {
                responseBody = await response.Content
                    .ReadAsStringAsync(timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw PackagePublicationException.RequestTimedOut(
                    request.AgentId,
                    clientRequestId,
                    ex);
            }
            catch (InvalidOperationException ex)
            {
                throw PackagePublicationException.RequestFailed(clientRequestId, ex);
            }
            catch (HttpRequestException ex)
            {
                throw PackagePublicationException.RequestFailed(clientRequestId, ex);
            }
            if (!response.IsSuccessStatusCode)
            {
                var durableFailure = TryParseDurableFailure(responseBody);
                if (durableFailure != null)
                {
                    if (!string.Equals(durableFailure.AgentId, request.AgentId, StringComparison.OrdinalIgnoreCase))
                    {
                        throw PackagePublicationException.InvalidResponse(
                            AddCorrelation(
                                "The AMS-DP v1 failure response identified a different Agent.",
                                clientRequestId,
                                ReadServiceRequestId(response)));
                    }

                    throw PackagePublicationException.DurableOperationDidNotSucceed(
                        durableFailure.OperationId,
                        durableFailure.Status,
                        clientRequestId,
                        ReadServiceRequestId(response));
                }

                throw AmsPublicationErrorMap.Map(
                    (int)response.StatusCode,
                    responseBody,
                    HttpMethod.Post.Method,
                    targetUri.ToString(),
                    "msagent",
                    clientRequestId,
                    ReadServiceRequestId(response));
            }

            return ParseSuccess(responseBody, clientRequestId, ReadServiceRequestId(response));
        }
    }

    private static AmsPackagePublicationResult ParseSuccess(
        string responseBody,
        string clientRequestId,
        string? serviceRequestId)
    {
        try
        {
            var operation = ParseOperation(responseBody);
            if (!string.Equals(operation.Status, "Succeeded", StringComparison.Ordinal) ||
                !TryParseNonEmptyGuid(operation.PackageRevisionId))
            {
                throw new FormatException("The operation did not contain terminal success.");
            }

            return operation with
            {
                ClientRequestId = clientRequestId,
                ServiceRequestId = serviceRequestId,
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            throw PackagePublicationException.InvalidResponse(
                AddCorrelation("The AMS-DP v1 upload response did not contain a valid terminal operation and Package Revision.", clientRequestId, serviceRequestId),
                ex);
        }
    }

    private static AmsPackagePublicationResult? TryParseDurableFailure(string responseBody)
    {
        try
        {
            var operation = ParseOperation(responseBody);
            return operation.Status is "Accepted" or "Failed"
                ? operation
                : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static AmsPackagePublicationResult ParseOperation(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var operationId = GetRequiredString(root, "operationId");
        var agentId = GetRequiredString(root, "agentId");
        var sha256 = GetRequiredString(root, "sha256");
        var size = GetRequiredInt64(root, "sizeInBytes");
        var status = GetUploadStatus(root);
        var revisionId = GetOptionalString(root, "packageRevisionId");
        var createdTime = GetRequiredDate(root, "createdTime");
        var changedTime = GetRequiredDate(root, "changedTime");

        if (!TryParseNonEmptyGuid(operationId) ||
            !TryParseNonEmptyGuid(agentId) ||
            sha256.Length != 64 ||
            !sha256.All(Uri.IsHexDigit) ||
            size <= 0 ||
            changedTime < createdTime ||
            status is not ("Accepted" or "Succeeded" or "Failed") ||
            (status == "Succeeded" && !TryParseNonEmptyGuid(revisionId)) ||
            (status != "Succeeded" && revisionId != null))
        {
            throw new FormatException("Required operation identity is malformed.");
        }

        return new AmsPackagePublicationResult
        {
            OperationId = operationId,
            AgentId = agentId,
            Sha256 = sha256.ToLowerInvariant(),
            SizeInBytes = size,
            Status = status,
            PackageRevisionId = revisionId,
            CreatedTime = createdTime,
            ChangedTime = changedTime,
        };
    }

    private static string GetUploadStatus(JsonElement root)
    {
        var value = GetRequiredProperty(root, "status");
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric switch
            {
                0 => "NotSpecified",
                1 => "Accepted",
                2 => "Succeeded",
                3 => "Failed",
                _ => numeric.ToString(CultureInfo.InvariantCulture),
            };
        }

        throw new FormatException("status is invalid.");
    }

    private static string GetRequiredString(JsonElement root, string name)
    {
        var value = GetRequiredProperty(root, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new FormatException($"{name} is missing.");
        }

        return value.GetString()!;
    }

    private static string? GetOptionalString(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            if (property.Value.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                throw new FormatException($"{name} is invalid.");
            }

            return property.Value.GetString();
        }

        return null;
    }

    private static long GetRequiredInt64(JsonElement root, string name)
    {
        var value = GetRequiredProperty(root, name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new FormatException($"{name} is missing.");
        }

        return result;
    }

    private static DateTimeOffset GetRequiredDate(JsonElement root, string name)
    {
        var raw = GetRequiredString(root, name);
        return DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var value)
            ? value
            : throw new FormatException($"{name} is invalid.");
    }

    private static JsonElement GetRequiredProperty(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new FormatException($"{name} is missing.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static string? ReadServiceRequestId(HttpResponseMessage response) =>
        ServiceCorrelation.ReadServiceRequestId(response);

    private static string AddCorrelation(string message, string clientRequestId, string? serviceRequestId) =>
        new ServiceCorrelation(clientRequestId, serviceRequestId).Annotate(message);

    private static Uri EnsureTrailingSlash(Uri endpoint) =>
        endpoint.AbsoluteUri.EndsWith('/')
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);

    private static bool TryParseNonEmptyGuid(string? value) =>
        Guid.TryParse(value, out var parsed) && parsed != Guid.Empty;

    /// <summary>
    /// Lets <see cref="StreamContent"/> dispose its wrapper while the application service retains
    /// explicit ownership of the underlying package stream.
    /// </summary>
    private sealed class BorrowedStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
    }
}
