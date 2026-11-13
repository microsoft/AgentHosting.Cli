using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Hosting.Cli.CopilotStudio.AskClient;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

[CollectionDefinition("McsLiveTestSession", DisableParallelization = true)]
public sealed class McsLiveTestSessionCollection
{
}

[Collection("McsLiveTestSession")]
public sealed class McsLiveTestSessionTests
{
    private static readonly McsLiveTestTarget Target = new(
        "00000000-0000-0000-0000-000000000001",
        "11111111-2222-3333-4444-555555555555",
        "contoso_schema");

    [Fact]
    public void PlainActivityFailsClosedInsteadOfUsingLossySerialization()
    {
        McsLiveActivityCodec.Initialize();
        var activity = new Activity
        {
            Type = "message",
            Text = "plain",
            Conversation = new ConversationAccount { Id = "c" },
        };

        var error = Assert.Throws<McsLiveTestProtocolException>(
            () => McsLiveActivityCodec.ToJsonElement(activity));

        Assert.Equal("The SDK returned an activity without lossless raw JSON.", error.Message);
    }

    [Fact]
    public async Task StartThenQueryPreservesOrderAndBindsServiceConversation()
    {
        var handler = new RecordingHandler(
            JsonResponse("""
            {
              "activities": [
                {"type":"message","text":"init-1","conversation":{"id":"service-conversation"}},
                {"type":"message","text":"init-2","conversation":{"id":"service-conversation"}}
              ],
              "conversationId":"service-conversation"
            }
            """),
            JsonResponse("""
            {
              "conversationId":"service-conversation",
              "activities": [
                {"type":"message","text":"query-1","conversation":{"id":"service-conversation"}},
                {"type":"message","text":"query-2","conversation":{"id":"service-conversation"}}
              ]
            }
            """));

        await using var session = new McsLiveTestSession(Target, "delegated-token", handler);

        var initialized = await ReadAsync(session.StartAsync());
        var queried = await ReadAsync(session.QueryAsync("unchanged query"));

        Assert.Equal(["init-1", "init-2"], initialized.Select(GetText));
        Assert.Equal(["query-1", "query-2"], queried.Select(GetText));
        Assert.Equal("service-conversation", session.ConversationId);
        Assert.Equal("service-conversation", session.ServiceConversationId);
        Assert.Equal(2, handler.Requests.Count);
        var discovery = Assert.Single(handler.DiscoveryRequests);
        Assert.Equal(HttpMethod.Get, discovery.Method);
        Assert.Equal(
            $"https://{McsLiveTargetValidation.GetAuthority(Target)}/copilotstudio/agents/contoso_schema/conversation-endpoints",
            discovery.RequestUri!.AbsoluteUri);
        Assert.Equal("delegated-token", discovery.Headers.Authorization?.Parameter);
        Assert.Contains("/conversations?api-version=2022-03-01-preview", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.EndsWith("/service-conversation", handler.Requests[1].RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer", request.Headers.Authorization?.Scheme));
        Assert.All(handler.Requests, request => Assert.Equal("delegated-token", request.Headers.Authorization?.Parameter));
        Assert.Equal("{\"emitStartConversationEvent\":true}", handler.RequestBodies[0]);
        Assert.Contains("\"activity\"", handler.RequestBodies[1]);
        Assert.Contains("\"text\":\"unchanged query\"", handler.RequestBodies[1]);
        Assert.Contains("\"id\":\"service-conversation\"", handler.RequestBodies[1]);
    }

    [Fact]
    public async Task DiscoverySelectsGitHubCopilotForStartAndQueryAndContinuesAfterEndOfConversation()
    {
        var endpoint = new Uri(
            "https://a47151cf4f34488fb377ebe84e17b4.78.environment.api.powerplatform.com/" +
            "copilotstudio/agenticruntime/3p/dataverse-backed/authenticated/bots/contoso_schema/conversations?api-version=1");
        var target = Target with
        {
            EnvironmentId = "A47151CF-4F34-488F-B377-EBE84E17B478",
        };
        var handler = new RecordingHandler(
            JsonResponse("""
            {
              "activities": [
                {"type":"endOfConversation","conversation":{"id":"harness-conversation"}}
              ],
              "conversationId":"harness-conversation"
            }
            """),
            JsonResponse("""
            {
              "activities": [
                {"type":"message","text":"still available","conversation":{"id":"harness-conversation"}}
              ]
            }
            """))
        {
            DiscoveryEndpoint = endpoint,
        };

        var tokenProvider = new RecordingTokenProvider("token");
        await using var session = new McsLiveTestSession(
            target,
            tokenProvider,
            handler);
        var started = await ReadAsync(session.StartAsync());
        var queried = await ReadAsync(session.QueryAsync("continue", "harness-conversation"));

        Assert.Equal("endOfConversation", started[0].GetProperty("type").GetString());
        Assert.Equal("still available", GetText(Assert.Single(queried)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(handler.DiscoveryRequests);
        Assert.Equal(endpoint.Host, handler.Requests[0].RequestUri!.Host);
        Assert.Equal(endpoint.AbsolutePath, handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("?api-version=1", handler.Requests[0].RequestUri!.Query);
        Assert.Equal(endpoint.AbsolutePath + "/harness-conversation", handler.Requests[1].RequestUri!.AbsolutePath);
        Assert.Equal("?api-version=1", handler.Requests[1].RequestUri!.Query);
        Assert.Equal(
            ["https://api.powerplatform.com/CopilotStudio.Copilots.Invoke"],
            tokenProvider.Scopes);
        Assert.Equal(target.TenantId, tokenProvider.TargetTenantId);
        Assert.All(handler.Requests, request =>
            Assert.Contains(request.Headers.Accept, header =>
                string.Equals(header.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void DiscoveryOnlyApprovesAdvertisedOperations()
    {
        var discovery = DiscoveredEndpoints();
        var endpoint = StandardEndpoint(discovery.DiscoveryUri);
        var conversation = endpoint.GetLeftPart(UriPartial.Path) + "/c" + endpoint.Query;

        Assert.True(discovery.IsExpectedRequestResource(discovery.DiscoveryUri.AbsoluteUri));
        Assert.True(discovery.IsExpectedRequestResource(endpoint.AbsoluteUri));
        Assert.True(discovery.IsExpectedRequestResource(conversation));
        Assert.False(new McsLiveEndpointDiscovery(Target).IsExpectedRequestResource(conversation));
        Assert.False(discovery.IsExpectedRequestResource(conversation.Replace("api-version=2022-03-01-preview", "api-version=1", StringComparison.Ordinal)));
        Assert.False(discovery.IsExpectedRequestResource(conversation.Replace("/c?", "/c/other?", StringComparison.Ordinal)));
        Assert.False(discovery.IsExpectedRequestResource(conversation.Replace("/c?", "/c%2fother?", StringComparison.Ordinal)));
        Assert.False(discovery.IsExpectedRequestResource(conversation.Replace(endpoint.Host, "untrusted.example", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("existing-id")]
    [InlineData("space id")]
    [InlineData("unicode-\u00e9")]
    public async Task DiscoveryPreservesSafeEncodedConversationIds(string conversationId)
    {
        var handler = new RecordingHandler(JsonResponse("""{"activities":[]}"""));
        await using var session = new McsLiveTestSession(Target, "token", handler);

        await ReadAsync(session.QueryAsync("query", conversationId));

        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/" + Uri.EscapeDataString(conversationId), request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryUsesAdvertisedExecuteUrlWithoutStartingAConversation()
    {
        var endpoint = new Uri("https://island.environment.api.powerplatform.com/runtime/conversations?api-version=7");
        var execute = "https://island.environment.api.powerplatform.com/runtime/turn/{conversationId}?api-version=8";
        var handler = new RecordingHandler(JsonResponse("""{"activities":[]}"""))
        {
            DiscoveryResponse = JsonResponse(JsonSerializer.Serialize(new
            {
                createConversation = endpoint.AbsoluteUri,
                executeTurn = execute,
                continueTurn = execute,
            })),
        };
        await using var session = new McsLiveTestSession(Target, "token", handler);

        await ReadAsync(session.QueryAsync("query", "existing-id"));

        Assert.Single(handler.DiscoveryRequests);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(execute.Replace("{conversationId}", "existing-id", StringComparison.Ordinal), request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task PlaygroundDiscoveryDoesNotStartAConversation()
    {
        var endpoint = new Uri(
            $"https://{McsLiveTargetValidation.GetAuthority(Target)}/copilotstudio/agenticruntime/3p/dataverse-backed/authenticated/bots/contoso_schema/conversations?api-version=1");
        var handler = new RecordingHandler { DiscoveryEndpoint = endpoint };
        await using var session = new McsLiveTestSession(Target, "token", handler);

        Assert.Equal(endpoint, await session.DiscoverEndpointAsync());
        Assert.Single(handler.DiscoveryRequests);
        Assert.Empty(handler.Requests);
        Assert.Null(session.ConversationId);
    }

    [Theory]
    [InlineData("https://untrusted.example/conversations")]
    [InlineData("https://host.environment.api.powerplatform.com.evil.example/conversations")]
    [InlineData("http://host.environment.api.powerplatform.com/conversations")]
    [InlineData("https://host.environment.api.powerplatform.com:444/conversations")]
    [InlineData("https://user@host.environment.api.powerplatform.com/conversations")]
    [InlineData("https://host.environment.api.powerplatform.com/conversations#fragment")]
    public async Task InvalidDiscoveredEndpointFailsBeforeAnyConversationRequest(string endpoint)
    {
        var handler = new RecordingHandler { DiscoveryEndpoint = new Uri(endpoint) };
        await using var session = new McsLiveTestSession(Target, "token", handler);

        await Assert.ThrowsAsync<McsLiveTestProtocolException>(
            async () => await ReadAsync(session.QueryAsync("query", "c")));
        Assert.Single(handler.DiscoveryRequests);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"createConversation\":42}")]
    [InlineData("{\"createConversation\":\"CANARY-secret\"}")]
    [InlineData("{\"CANARY-secret")]
    public async Task InvalidDiscoveryResponseIsSanitizedAndDoesNotFallBack(string json)
    {
        var handler = new RecordingHandler { DiscoveryResponse = JsonResponse(json) };
        await using var session = new McsLiveTestSession(Target, "token", handler);

        var error = await Assert.ThrowsAsync<McsLiveTestProtocolException>(
            async () => await ReadAsync(session.QueryAsync("query", "c")));

        Assert.DoesNotContain("CANARY", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Single(handler.DiscoveryRequests);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Found)]
    public async Task DiscoveryFailureIsNotRetriedAndDoesNotFallBack(HttpStatusCode status)
    {
        var handler = new RecordingHandler
        {
            DiscoveryResponse = new HttpResponseMessage(status)
            {
                Content = new StringContent("CANARY-secret-discovery-response"),
                Headers = { Location = new Uri("https://untrusted.example") },
            },
        };
        await using var session = new McsLiveTestSession(Target, "token", handler);

        var error = await Assert.ThrowsAsync<McsLiveTestTransportException>(
            async () => await ReadAsync(session.QueryAsync("query", "c")));

        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("CANARY", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.DiscoveryRequests);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CancelledPlaygroundDiscoveryDoesNotSendARequest()
    {
        var handler = new RecordingHandler();
        await using var session = new McsLiveTestSession(Target, "token", handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.DiscoverEndpointAsync(cancellation.Token));

        Assert.Empty(handler.DiscoveryRequests);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ContinuationUsesSuppliedIdWithoutStartingAndDoesNotReuseAnotherSession()
    {
        var handler = new RecordingHandler(JsonResponse("""
        {
          "activities": [
            {"type":"message","text":"continued","conversation":{"id":"supplied-id"}}
          ]
        }
        """));

        await using var session = new McsLiveTestSession(Target, "token-a", handler);
        var activities = await ReadAsync(session.QueryAsync("one", "supplied-id"));

        Assert.Single(activities);
        Assert.Equal("supplied-id", session.ConversationId);
        Assert.Equal("supplied-id", session.ServiceConversationId);
        Assert.Single(handler.Requests);
        Assert.Single(handler.DiscoveryRequests);
        Assert.DoesNotContain("/conversations?api-version=", handler.Requests[0].RequestUri!.PathAndQuery);

        await using var second = new McsLiveTestSession(Target, "token-b", handler);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await ReadAsync(second.QueryAsync("two")));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RawJsonIsLosslessForUnknownFieldsNullsLargeNumbersAndJsonStrings()
    {
        var handler = new RecordingHandler(JsonResponse("""
        {
          "unknownRoot":{"nested":[1,2,3]},
          "activities": [
            {
              "type":"message",
              "text":"literal {\"not\":\"an object\"}",
              "conversation":{"id":"c"},
              "explicitNull":null,
              "large":92233720368547758081234567890,
              "extension":{"deep":{"items":[true,false]}}
            }
          ]
        }
        """));

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var activity = Assert.Single(await ReadAsync(session.QueryAsync("query", "c")));

        Assert.Equal(JsonValueKind.Null, activity.GetProperty("explicitNull").ValueKind);
        Assert.Equal("literal {\"not\":\"an object\"}", activity.GetProperty("text").GetString());
        Assert.Equal("92233720368547758081234567890", activity.GetProperty("large").GetRawText());
        Assert.Equal("[true,false]", activity.GetProperty("extension").GetProperty("deep").GetProperty("items").GetRawText());
    }

    [Fact]
    public async Task SseFramesAreStreamedAndNonActivityFramesAreIgnored()
    {
        var first = """{"type":"message","text":"first","conversation":{"id":"c"}}""";
        var second = """{"type":"message","text":"second","conversation":{"id":"c"}}""";
        var content = new GatedSseContent(
            $": heartbeat\r\n\r\nevent: other\r\ndata: {{\"ignored\":true}}\r\n\r\nevent: activity\r\ndata: {first}\r\n\r\n",
            $"event: activity\r\ndata: {second}\r\n\r\n");
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var enumerator = session.QueryAsync("query", "c").GetAsyncEnumerator();
        try
        {
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("first", GetText(enumerator.Current));
            content.ReleaseTail();
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("second", GetText(enumerator.Current));
            Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            content.ReleaseTail();
            try
            {
                await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
        }
    }

    [Fact]
    public async Task SseFramesAcceptMixedLineTerminators()
    {
        var first = """{"type":"message","text":"first","conversation":{"id":"c"}}""";
        var second = """{"type":"message","text":"second","conversation":{"id":"c"}}""";
        var content = new ChunkedSseContent(
            $"event: activity\r\ndata: {first}\n\r",
            $"event: activity\rdata: {second}\r\n\n");
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var activities = await ReadAsync(session.QueryAsync("query", "c"));

        Assert.Equal(["first", "second"], activities.Select(GetText));
    }

    [Fact]
    public async Task SseFramesAcceptCrOnlyLineTerminators()
    {
        var first = """{"type":"message","text":"first","conversation":{"id":"c"}}""";
        var second = """{"type":"message","text":"second","conversation":{"id":"c"}}""";
        var body =
            $"event: activity\rdata: {first}\r\revent: activity\rdata: {second}\r\r";
        Assert.DoesNotContain('\n', body);

        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ChunkedSseContent(body),
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var activities = await ReadAsync(session.QueryAsync("query", "c"));

        Assert.Equal(["first", "second"], activities.Select(GetText));
    }

    [Fact]
    public async Task GatedCrOnlySseYieldsFirstActivityBeforeTailRelease()
    {
        var first = "event: activity\rdata: {\"type\":\"message\",\"text\":\"first\",\"conversation\":{\"id\":\"c\"}}\r\r";
        var second = "event: activity\rdata: {\"type\":\"message\",\"text\":\"second\",\"conversation\":{\"id\":\"c\"}}\r\r";
        Assert.DoesNotContain('\n', first);
        Assert.DoesNotContain('\n', second);
        var content = new GatedSseContent(first, second);
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var enumerator = session.QueryAsync("query", "c").GetAsyncEnumerator();
        try
        {
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("first", GetText(enumerator.Current));
            Assert.False(content.TailReadStarted.Task.IsCompleted);

            content.ReleaseTail();
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("second", GetText(enumerator.Current));
            Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            content.ReleaseTail();
            try
            {
                await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
        }
    }

    [Fact]
    public async Task SseFramesTreatCrLfSplitAcrossReadsAsOneTerminator()
    {
        var first = """{"type":"message","text":"first","conversation":{"id":"c"}}""";
        var second = """{"type":"message","text":"second","conversation":{"id":"c"}}""";
        var content = new ChunkedSseContent(
            $"event: activity\r\ndata: {first}\r\n\r",
            $"\nevent: activity\r\ndata: {second}\r\n\r\n");
        await using var stream = new McsSseSanitizingStream(
            await content.ReadAsStreamAsync());
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);

        var expected =
            $"event: activity\r\ndata: {first}\r\n\r\nevent: activity\r\ndata: {second}\r\n\r\n";
        Assert.Equal(expected, Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task SseFramesTolerateLeadingUtf8Bom()
    {
        var activity = """{"type":"message","text":"first","conversation":{"id":"c"}}""";
        var content = new ChunkedSseContent(
            $"\uFEFFevent: activity\r\ndata: {activity}\r\n\r\n");
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var activities = await ReadAsync(session.QueryAsync("query", "c"));

        Assert.Equal(["first"], activities.Select(GetText));
    }

    [Fact]
    public async Task SseFramesAreStreamedBySdk()
    {
        var first = "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"first\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n";
        var second = "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"second\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n";
        var content = new GatedSseContent(first, second);
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await using var session = new McsLiveTestSession(Target, "token", handler);
        var enumerator = session.QueryAsync("query", "c").GetAsyncEnumerator();
        try
        {
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("first", GetText(enumerator.Current));
            content.ReleaseTail();
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("second", GetText(enumerator.Current));
            Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            content.ReleaseTail();
            try
            {
                await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
        }
    }

    [Fact]
    public async Task GatedSseYieldsFirstActivityBeforeLaterFramesOrEofAreReleased()
    {
        var first = "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"first\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n";
        var second = "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"second\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n";
        var content = new GatedSseContent(first, second);
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var bridge = new McsResponseBridgeHandler(handler, DiscoveredEndpoints(), _ => { }, _ => { });
        using var invoker = new HttpMessageInvoker(bridge);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{McsLiveTargetValidation.GetAuthority(Target)}/copilotstudio/dataverse-backed/authenticated/bots/{Target.SchemaName}/conversations/c?api-version=2022-03-01-preview");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await invoker.SendAsync(request, CancellationToken.None);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];

        try
        {
            var firstRead = stream.ReadAsync(buffer).AsTask();
            Assert.Same(firstRead, await Task.WhenAny(firstRead, Task.Delay(TimeSpan.FromSeconds(1))));
            var firstCount = await firstRead;
            var firstPayload = Encoding.UTF8.GetString(buffer, 0, firstCount);
            Assert.Contains("\"text\":\"first\"", firstPayload, StringComparison.Ordinal);
            Assert.False(content.TailReadStarted.Task.IsCompleted);

            var tailRead = stream.ReadAsync(buffer).AsTask();
            Assert.Same(
                content.TailReadStarted.Task,
                await Task.WhenAny(content.TailReadStarted.Task, Task.Delay(TimeSpan.FromSeconds(1))));
            Assert.False(tailRead.IsCompleted);

            content.ReleaseTail();
            var tailCount = await tailRead;
            var tailPayload = Encoding.UTF8.GetString(buffer, 0, tailCount);
            var remaining = new StringBuilder(firstPayload).Append(tailPayload);
            while (tailCount > 0)
            {
                tailCount = await stream.ReadAsync(buffer);
                remaining.Append(Encoding.UTF8.GetString(buffer, 0, tailCount));
            }

            Assert.Contains("\"text\":\"second\"", remaining.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            content.ReleaseTail();
        }
    }

    [Fact]
    public async Task JsonFallbackBridgeStreamsFirstActivityBeforeTailRelease()
    {
        var first = "{\"activities\":[{\"type\":\"message\",\"text\":\"first\",\"conversation\":{\"id\":\"c\"}}";
        var second = ",{\"type\":\"message\",\"text\":\"second\",\"conversation\":{\"id\":\"c\"}}]}";
        var content = new GatedJsonContent(first, second);
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        using var bridge = new McsResponseBridgeHandler(handler, DiscoveredEndpoints(), _ => { }, _ => { });
        using var invoker = new HttpMessageInvoker(bridge);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://{McsLiveTargetValidation.GetAuthority(Target)}/copilotstudio/dataverse-backed/authenticated/bots/{Target.SchemaName}/conversations/c?api-version=2022-03-01-preview");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "token");
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await invoker.SendAsync(request, CancellationToken.None);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];
        var prefixCount = await stream.ReadAsync(buffer);
        Assert.Contains("{\"activities\":[", Encoding.UTF8.GetString(buffer, 0, prefixCount), StringComparison.Ordinal);

        var firstRead = stream.ReadAsync(buffer).AsTask();
        Assert.Same(firstRead, await Task.WhenAny(firstRead, Task.Delay(TimeSpan.FromSeconds(1))));
        var firstPayload = Encoding.UTF8.GetString(buffer, 0, await firstRead);
        Assert.Contains("\"text\":\"first\"", firstPayload, StringComparison.Ordinal);
        Assert.False(content.TailReadStarted.Task.IsCompleted);

        var tailRead = stream.ReadAsync(buffer).AsTask();
        Assert.Same(
            content.TailReadStarted.Task,
            await Task.WhenAny(content.TailReadStarted.Task, Task.Delay(TimeSpan.FromSeconds(1))));
        Assert.False(tailRead.IsCompleted);

        content.ReleaseTail();
        var tailCount = await tailRead;
        var remaining = new StringBuilder(Encoding.UTF8.GetString(buffer, 0, tailCount));
        while (tailCount > 0)
        {
            tailCount = await stream.ReadAsync(buffer);
            remaining.Append(Encoding.UTF8.GetString(buffer, 0, tailCount));
        }

        Assert.Contains("\"text\":\"second\"", remaining.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SdkSessionYieldsFirstSseActivityBeforeTailIsReleased()
    {
        var first = "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"first\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n";
        var second = "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"second\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n";
        var content = new GatedSseContent(first, second);
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await using var session = new McsLiveTestSession(Target, "token", handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var enumerator = session.QueryAsync("query", "c", cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var firstMove = enumerator.MoveNextAsync().AsTask();
        try
        {
            Assert.Same(firstMove, await Task.WhenAny(firstMove, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.True(firstMove.IsCompletedSuccessfully);
            Assert.Equal("first", GetText(enumerator.Current));
            Assert.False(content.TailReadStarted.Task.IsCompleted);
            Assert.Equal(1, handler.RequestCount);
            content.ReleaseTail();
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("second", GetText(enumerator.Current));
            Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            content.ReleaseTail();
            cancellation.Cancel();
            try
            {
                await firstMove.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
            catch (Exception) when (firstMove.IsCanceled || firstMove.IsFaulted)
            {
            }

            try
            {
                await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
        }
    }

    [Fact]
    public async Task SseActivityPreservesUnknownFieldsNullsLargeNumbersAndJsonStrings()
    {
        var activity = "{\"type\":\"message\",\"text\":\"literal {\\\"not\\\":\\\"an object\\\"}\",\"conversation\":{\"id\":\"c\"},\"explicitNull\":null,\"large\":92233720368547758081234567890,\"extension\":{\"deep\":{\"items\":[true,false]}}}";
        var content = new GatedSseContent($"event: activity\r\ndata: {activity}\r\n\r\n", string.Empty);
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await using var session = new McsLiveTestSession(Target, "token", handler);
        var enumerator = session.QueryAsync("query", "c").GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        var result = enumerator.Current;
        content.ReleaseTail();
        Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        await enumerator.DisposeAsync();
        Assert.Equal(JsonValueKind.Null, result.GetProperty("explicitNull").ValueKind);
        Assert.Equal("literal {\"not\":\"an object\"}", result.GetProperty("text").GetString());
        Assert.Equal("92233720368547758081234567890", result.GetProperty("large").GetRawText());
        Assert.Equal("[true,false]", result.GetProperty("extension").GetProperty("deep").GetProperty("items").GetRawText());
    }

    [Fact]
    public async Task StartSseYieldsFirstActivityBeforeTailIsReleased()
    {
        var first = "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"first\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n";
        var second = "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"second\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n";
        var content = new GatedSseContent(first, second);
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await using var session = new McsLiveTestSession(Target, "token", handler);
        var enumerator = session.StartAsync().GetAsyncEnumerator();

        try
        {
            var firstMove = enumerator.MoveNextAsync().AsTask();
            Assert.Same(firstMove, await Task.WhenAny(firstMove, Task.Delay(TimeSpan.FromSeconds(1))));
            Assert.True(firstMove.IsCompletedSuccessfully);
            Assert.Equal("first", GetText(enumerator.Current));
            Assert.False(content.TailReadStarted.Task.IsCompleted);
            Assert.Equal(1, handler.RequestCount);

            content.ReleaseTail();
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal("second", GetText(enumerator.Current));
            Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            content.ReleaseTail();
            try
            {
                await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
        }
    }

    [Fact]
    public async Task SseReadFailureAfterActivityIsSanitized()
    {
        var content = new ThrowingSseContent(
            "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"first\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n",
            new IOException("CANARY-secret-token-https://host/path?q=1"));
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        await using var session = new McsLiveTestSession(Target, "token", handler);
        var enumerator = session.QueryAsync("query", "c").GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("first", GetText(enumerator.Current));
        var error = await Assert.ThrowsAsync<McsLiveTestTransportException>(
            async () => await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.DoesNotContain("CANARY", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task MalformedSseActivityIsMappedToSafeProtocolFailure()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"CANARY-PAYLOAD\r\n\r\n",
                Encoding.UTF8,
                "text/event-stream")
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var error = await Assert.ThrowsAsync<McsLiveTestProtocolException>(
            async () => await ReadAsync(session.QueryAsync("query", "c")));

        Assert.DoesNotContain("CANARY", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task TruncatedSseAfterActivityIsNotReportedAsSuccess()
    {
        var content = new GatedSseContent(
            "event: activity\r\ndata: {\"type\":\"message\",\"text\":\"first\",\"conversation\":{\"id\":\"c\"}}\r\n\r\n",
            "event: activity\r\ndata: {\"type\":\"mess");
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var enumerator = session.QueryAsync("query", "c").GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal("first", GetText(enumerator.Current));
        content.ReleaseTail();
        var error = await Assert.ThrowsAsync<McsLiveTestProtocolException>(
            async () => await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.DoesNotContain("mess", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        await enumerator.DisposeAsync();
    }

    [Fact]
    public void SseSanitizingStreamIsForwardOnly()
    {
        using var stream = new McsSseSanitizingStream(new MemoryStream());

        Assert.False(stream.CanSeek);
        Assert.Throws<NotSupportedException>(() => _ = stream.Length);
        Assert.Throws<NotSupportedException>(() => _ = stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Position = 1);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
    }

    [Fact]
    public async Task SdkSessionBuffersJsonResponsesUntilComplete()
    {
        var first = "{\"activities\":[{\"type\":\"message\",\"text\":\"first\",\"conversation\":{\"id\":\"c\"}}";
        var second = ",{\"type\":\"message\",\"text\":\"second\",\"conversation\":{\"id\":\"c\"}}]}";
        var content = new GatedJsonContent(first, second);
        var handler = new YieldingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        using var cancellation = new CancellationTokenSource();
        var enumerator = session.QueryAsync("query", "c", cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var firstMove = Task.Run(async () => await enumerator.MoveNextAsync());

        try
        {
            await content.TailReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.False(firstMove.IsCompleted);
            Assert.Equal(1, handler.RequestCount);

            content.ReleaseTail();
            Assert.True(await firstMove.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("first", GetText(enumerator.Current));
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("second", GetText(enumerator.Current));
        }
        finally
        {
            content.ReleaseTail();
            cancellation.Cancel();
            try
            {
                await firstMove.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
            catch (Exception) when (firstMove.IsCanceled || firstMove.IsFaulted)
            {
            }

            try
            {
                await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (TimeoutException)
            {
            }
        }
    }

    [Fact]
    public async Task FailedHttpStatusDoesNotLeakBodyAndDoesNotRetry()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("secret-token-or-payload", Encoding.UTF8, "application/json")
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var error = await Assert.ThrowsAsync<McsLiveTestTransportException>(
            async () => await ReadAsync(session.QueryAsync("query", "c")));

        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.DoesNotContain("secret-token-or-payload", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TransportFailureDoesNotRetainUnsafeExceptionChain()
    {
        const string destinationCanary = "https://api.powerplatform.com/secret-destination";
        const string queryCanary = "query-with-secret-content";
        const string tokenCanary = "bearer-token-secret";
        var handler = new ThrowingHandler(new HttpRequestException(
            $"request failed for {destinationCanary} and {queryCanary}",
            new InvalidOperationException($"authorization {tokenCanary}")));

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var error = await Assert.ThrowsAsync<McsLiveTestTransportException>(
            async () => await ReadAsync(session.QueryAsync(queryCanary, "c")));

        var exceptionChain = string.Join(
            Environment.NewLine,
            EnumerateExceptions(error).Select(exception => exception.ToString()));
        Assert.DoesNotContain(destinationCanary, exceptionChain, StringComparison.Ordinal);
        Assert.DoesNotContain(queryCanary, exceptionChain, StringComparison.Ordinal);
        Assert.DoesNotContain(tokenCanary, exceptionChain, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task RedirectsAreRejectedWithoutFollowingLocation()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri("https://evil.example/leak") }
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var error = await Assert.ThrowsAsync<McsLiveTestTransportException>(
            async () => await ReadAsync(session.QueryAsync("query", "c")));

        Assert.Equal(HttpStatusCode.Found, error.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task InvalidTargetAndTokenAuthorityFailBeforeNetwork()
    {
        Assert.Throws<ArgumentException>(() => new McsLiveTestSession(
            Target with { SchemaName = "bad/schema" }, "token", new RecordingHandler()));
        Assert.Throws<ArgumentException>(() => new McsLiveTestSession(Target, " ", new RecordingHandler()));
        Assert.Throws<ArgumentException>(() => new McsLiveTestSession(
            Target with { TenantId = "invalid" }, "token", new RecordingHandler()));

        var handler = new RecordingHandler(JsonResponse("""{"activities":[]}"""));
        var tokenProvider = new RecordingTokenProvider("token");
        await using var session = new McsLiveTestSession(
            Target,
            tokenProvider,
            handler);

        await ReadAsync(session.QueryAsync("query", "c"));
        Assert.Equal(
            ["https://api.powerplatform.com/CopilotStudio.Copilots.Invoke"],
            tokenProvider.Scopes);
        Assert.Equal(Target.TenantId, tokenProvider.TargetTenantId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TokenProviderRejectsTokenIssuedForAnotherTenantBeforeNetwork()
    {
        var handler = new RecordingHandler(JsonResponse("""{"activities":[]}"""));
        var tokenProvider = new RecordingTokenProvider(
            "token",
            "22222222-2222-2222-2222-222222222222");
        await using var session = new McsLiveTestSession(Target, tokenProvider, handler);

        await Assert.ThrowsAsync<McsLiveTestTokenException>(
            async () => await ReadAsync(session.QueryAsync("query", "c")));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MalformedJsonAfterAnActivityPreservesThePartialActivityAndFails()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"activities":[{"type":"message","text":"first","conversation":{"id":"c"}},]""",
                Encoding.UTF8,
                "application/json")
        });

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var stream = session.QueryAsync("query", "c");
        var enumerator = stream.GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("first", GetText(enumerator.Current));
        await Assert.ThrowsAnyAsync<JsonException>(async () => await enumerator.MoveNextAsync().AsTask());
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task AdjacentActivitiesWithoutCommaAreRejectedAfterPartialActivity()
    {
        var handler = new RecordingHandler(JsonResponse(
            """{"activities":[{"type":"message","text":"first","conversation":{"id":"c"}}{"type":"message","text":"second","conversation":{"id":"c"}}]}"""));

        await using var session = new McsLiveTestSession(Target, "token", handler);
        var enumerator = session.QueryAsync("query", "c").GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("first", GetText(enumerator.Current));
        await Assert.ThrowsAnyAsync<JsonException>(async () => await enumerator.MoveNextAsync().AsTask());
        await enumerator.DisposeAsync();
    }

    [Fact]
    public async Task TrailingRootCommaIsRejected()
    {
        var handler = new RecordingHandler(JsonResponse(
            """{"activities":[],"conversationId":"c",}"""));

        await using var session = new McsLiveTestSession(Target, "token", handler);

        await Assert.ThrowsAnyAsync<JsonException>(
            async () => await ReadAsync(session.QueryAsync("query", "c")));
    }

    [Fact]
    public async Task ClientHasNoInheritedRequestTimeout()
    {
        using var session = new McsLiveTestSession(Target, "token", new RecordingHandler(JsonResponse("""{"activities":[]}""")));

        Assert.Equal(Timeout.InfiniteTimeSpan, session.HttpClientTimeout);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string DiscoveryJson(Uri endpoint) => JsonSerializer.Serialize(new
    {
        createConversation = endpoint.AbsoluteUri,
        executeTurn = endpoint.GetLeftPart(UriPartial.Path) + "/{conversationId}" + endpoint.Query,
        continueTurn = endpoint.GetLeftPart(UriPartial.Path) + "/{conversationId}/continue" + endpoint.Query,
    });

    private static Uri StandardEndpoint(Uri discoveryUri) => new(
        $"https://{discoveryUri.Host}/copilotstudio/dataverse-backed/authenticated/bots/contoso_schema/conversations?api-version=2022-03-01-preview");

    private static McsLiveEndpointDiscovery DiscoveredEndpoints()
    {
        var discovery = new McsLiveEndpointDiscovery(Target);
        discovery.ReadResponse(DiscoveryJson(StandardEndpoint(discovery.DiscoveryUri)));
        return discovery;
    }

    private static string GetText(JsonElement activity) => activity.GetProperty("text").GetString()!;

    private static IEnumerable<Exception> EnumerateExceptions(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }

    private static async Task<List<JsonElement>> ReadAsync(IAsyncEnumerable<JsonElement> stream)
    {
        var result = new List<JsonElement>();
        await foreach (var activity in stream)
        {
            result.Add(activity);
        }

        return result;
    }

    private sealed class RecordingTokenProvider(string token, string? tenantId = null) : ITokenProvider
    {
        public IReadOnlyList<string> Scopes { get; private set; } = [];
        public string? TargetTenantId { get; private set; }

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            string resource,
            string? targetTenantId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TokenAcquisitionResult> AcquireTokenSilentAsync(
            IReadOnlyList<string> scopes,
            string? targetTenantId,
            CancellationToken cancellationToken)
        {
            Scopes = scopes;
            TargetTenantId = targetTenantId;
            return Task.FromResult(new TokenAcquisitionResult
            {
                AccessToken = token,
                TenantId = tenantId ?? targetTenantId,
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1),
            });
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public RecordingHandler(params HttpResponseMessage[] responses)
        {
            foreach (var response in responses)
            {
                _responses.Enqueue(response);
            }
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<HttpRequestMessage> DiscoveryRequests { get; } = [];
        public List<string?> RequestBodies { get; } = [];
        public Uri? DiscoveryEndpoint { get; init; }
        public HttpResponseMessage? DiscoveryResponse { get; init; }
        public string TokenAudience { get; set; } = "https://api.powerplatform.com";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is null)
            {
                throw new InvalidOperationException("The SDK did not attach a bearer token.");
            }

            if (request.Method == HttpMethod.Get)
            {
                DiscoveryRequests.Add(request);
                return Task.FromResult(DiscoveryResponse ??
                    JsonResponse(DiscoveryJson(DiscoveryEndpoint ?? StandardEndpoint(request.RequestUri!))));
            }

            Requests.Add(request);
            RequestBodies.Add(request.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Unexpected replay.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class YieldingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Headers.Authorization is null)
            {
                throw new InvalidOperationException("The SDK did not attach a bearer token.");
            }

            if (request.Method == HttpMethod.Get)
            {
                return JsonResponse(DiscoveryJson(StandardEndpoint(request.RequestUri!)));
            }

            RequestCount++;
            await Task.Yield();
            return response;
        }
    }

    private sealed class GatedJsonContent : HttpContent
    {
        private readonly GatedJsonStream _stream;

        public GatedJsonContent(string first, string second)
        {
            _stream = new GatedJsonStream(first, second);
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        public TaskCompletionSource<bool> TailReadStarted => _stream.TailReadStarted;

        public void ReleaseTail() => _stream.ReleaseTail();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(_stream);
    }

    private sealed class GatedJsonStream : Stream
    {
        private readonly byte[] _first;
        private readonly byte[] _second;
        private readonly TaskCompletionSource<bool> _releaseTail =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _read;
        private int _offset;

        public GatedJsonStream(string first, string second)
        {
            _first = Encoding.UTF8.GetBytes(first);
            _second = Encoding.UTF8.GetBytes(second);
        }

        public TaskCompletionSource<bool> TailReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseTail() => _releaseTail.TrySetResult(true);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ReadCoreAsync(buffer, cancellationToken);
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_read == 0)
            {
                var firstCount = Math.Min(buffer.Length, _first.Length - _offset);
                _first.AsMemory(_offset, firstCount).CopyTo(buffer);
                _offset += firstCount;
                if (_offset == _first.Length)
                {
                    _read = 1;
                    _offset = 0;
                }

                return firstCount;
            }

            if (_read == 1)
            {
                TailReadStarted.TrySetResult(true);
                await _releaseTail.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
                _read = 2;
            }

            var count = Math.Min(buffer.Length, _second.Length - _offset);
            _second.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
    }

    private sealed class ThrowingSseContent : HttpContent
    {
        private readonly ThrowingSseStream _stream;

        public ThrowingSseContent(string first, IOException error)
        {
            _stream = new ThrowingSseStream(first, error);
            Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(_stream);
    }

    private sealed class ThrowingSseStream : Stream
    {
        private readonly byte[] _first;
        private readonly IOException _error;
        private int _read;

        public ThrowingSseStream(string first, IOException error)
        {
            _first = Encoding.UTF8.GetBytes(first);
            _error = error;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_read++ == 0)
            {
                _first.AsMemory().CopyTo(buffer);
                return ValueTask.FromResult(_first.Length);
            }

            return ValueTask.FromException<int>(_error);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChunkedSseContent : HttpContent
    {
        private readonly ChunkedSseStream _stream;

        public ChunkedSseContent(params string[] chunks)
        {
            _stream = new ChunkedSseStream(chunks);
            Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(_stream);
    }

    private sealed class ChunkedSseStream : Stream
    {
        private readonly byte[][] _chunks;
        private int _chunk;

        public ChunkedSseStream(IEnumerable<string> chunks) =>
            _chunks = chunks.Select(Encoding.UTF8.GetBytes).ToArray();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_chunk == _chunks.Length)
            {
                return ValueTask.FromResult(0);
            }

            var chunk = _chunks[_chunk++];
            chunk.AsMemory().CopyTo(buffer);
            return ValueTask.FromResult(chunk.Length);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class GatedSseContent : HttpContent
    {
        private readonly GatedSseStream _stream;

        public GatedSseContent(string first, string second)
        {
            _stream = new GatedSseStream(first, second);
            Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }

        public TaskCompletionSource<bool> TailReadStarted => _stream.TailReadStarted;

        public void ReleaseTail() => _stream.ReleaseTail();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            _stream.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(_stream);
    }

    private sealed class GatedSseStream : Stream
    {
        private readonly byte[] _first;
        private readonly byte[] _second;
        private readonly TaskCompletionSource<bool> _releaseTail =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _read;

        public GatedSseStream(string first, string second)
        {
            _first = Encoding.UTF8.GetBytes(first);
            _second = Encoding.UTF8.GetBytes(second);
        }

        public TaskCompletionSource<bool> TailReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseTail() => _releaseTail.TrySetResult(true);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return ReadCoreAsync(buffer, cancellationToken);
        }
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_read == 0)
            {
                _read = 1;
                _first.AsMemory().CopyTo(buffer);
                return _first.Length;
            }

            TailReadStarted.TrySetResult(true);
            await _releaseTail.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (_read == 1)
            {
                _read = 2;
                _second.AsMemory().CopyTo(buffer);
                return _second.Length;
            }

            return 0;
        }
    }
}
