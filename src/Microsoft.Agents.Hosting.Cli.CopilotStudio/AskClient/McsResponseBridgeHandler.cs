using System.Net.Http.Headers;

namespace Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;

internal sealed class McsResponseBridgeHandler(
    HttpMessageHandler innerHandler,
    McsLiveEndpointDiscovery discovery,
    Action<string> serviceConversationId,
    Action<Exception> responseError) : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var isDiscovery = request.RequestUri == discovery.DiscoveryUri;
        if (request.RequestUri is null ||
            !discovery.IsExpectedRequestResource(request.RequestUri.AbsoluteUri) ||
            request.Method != (isDiscovery ? HttpMethod.Get : HttpMethod.Post))
        {
            throw new McsLiveTestProtocolException("The SDK generated an unapproved Copilot Studio request.");
        }

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

        if (isDiscovery)
        {
            try
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                discovery.ReadResponse(json);
                return response;
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                response.Dispose();
                throw new McsLiveTestTransportException(null, "The Copilot Studio discovery response could not be read.");
            }
            catch
            {
                response.Dispose();
                throw;
            }
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
}
