using System.Net;
using System.Net.Http.Headers;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

internal sealed class McsResponseBridgeHandler(
    HttpMessageHandler innerHandler,
    McsLiveTestTarget target,
    Action<string> serviceConversationId,
    Action<Exception> responseError) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request, target);
        NormalizeApiVersion(request, target);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McsLiveTestTransportException(null, "The Copilot Studio transport timed out.");
        }
        catch (HttpRequestException)
        {
            throw new McsLiveTestTransportException(null, "The Copilot Studio transport failed.");
        }

        if ((int)response.StatusCode is >= 300 and < 400 || !response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new McsLiveTestTransportException(status, "The Copilot Studio service returned an unsuccessful response.");
        }

        if (response.Headers.TryGetValues("x-ms-conversationid", out var values))
        {
            var header = values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (header is not null)
            {
                serviceConversationId(header);
            }
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var content = response.Content;
            response.Content = new McsSsePassthroughContent(content, responseError);
            return response;
        }

        if (mediaType is not null &&
            (string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) ||
             mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            var content = response.Content;
            response.Content = new McsJsonActivityEnvelopeContent(
                content,
                serviceConversationId,
                responseError);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
            return response;
        }

        response.Dispose();
        throw new McsLiveTestProtocolException("The Copilot Studio service returned an unsupported response format.");
    }

    private static void ValidateRequest(HttpRequestMessage request, McsLiveTestTarget target)
    {
        var uri = request.RequestUri;
        var endpoint = target.Endpoint ?? McsLiveTargetValidation.GetEndpoint(target);
        var prefix = endpoint.AbsolutePath;

        if (uri is null ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.Equals(uri.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !McsLiveTargetValidation.IsSupportedSdkQuery(uri.Query, endpoint.Query, target.AgentType) ||
            !(uri.AbsolutePath.Equals(prefix, StringComparison.Ordinal) ||
              (uri.AbsolutePath.StartsWith(prefix + "/", StringComparison.Ordinal) &&
               uri.AbsolutePath[(prefix.Length + 1)..].IndexOf('/') < 0)))
        {
            throw new McsLiveTestProtocolException("The SDK generated an unapproved Copilot Studio request.");
        }
    }

    private static void NormalizeApiVersion(HttpRequestMessage request, McsLiveTestTarget target)
    {
        var endpoint = target.Endpoint ?? McsLiveTargetValidation.GetEndpoint(target);
        if (target.AgentType != LiveTestAgentType.GitHubCopilot ||
            request.RequestUri is not { } uri ||
            string.Equals(uri.Query, endpoint.Query, StringComparison.Ordinal))
        {
            return;
        }

        var builder = new UriBuilder(uri)
        {
            Query = endpoint.Query.TrimStart('?'),
        };
        // SDK 1.8.77 rebuilds DirectConnectUrl requests with its standard API version.
        request.RequestUri = builder.Uri;
    }
}
