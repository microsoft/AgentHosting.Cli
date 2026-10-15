using System.Net;
using System.Text;
using Microsoft.Agents.Hosting.Cli.Core.Deployments;
using Microsoft.Agents.Hosting.Cli.Core.Errors;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Deterministic FUZZ-1 coverage for Azure Bot Service channel response parsing.
/// </summary>
public sealed class DeploymentChannelResponseFuzzTests
{
    private const int Seed = 20261013;
    private const int GeneratedCases = 2000;
    private const string BotResourceId =
        "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.BotService/botServices/bot-1";

    private static readonly string[] SeedCorpus =
    [
        string.Empty,
        " ",
        "\0",
        "null",
        "true",
        "0",
        "[]",
        "\"channel\"",
        "{",
        "}",
        "{\"properties\":",
        "{\"properties\":null}",
        "{\"properties\":[]}",
        "{\"properties\":{}}",
        "{\"properties\":{\"channelName\":null}}",
        "{\"properties\":{\"channelName\":42}}",
        "{\"properties\":{\"channelName\":\"UnknownChannel\"}}",
        "{\"properties\":{\"channelName\":\"SlackChannel\",\"properties\":[]}}",
        "{\"properties\":{\"channelName\":\"DirectLineChannel\",\"properties\":{\"sites\":{}}}}",
        "{\"properties\":{\"channelName\":\"DirectLineChannel\",\"properties\":{\"sites\":[null,42,{}]}}}",
        "{\"value\":null}",
        "{\"value\":{}}",
        "{\"value\":[null]}",
        "{\"value\":[42]}",
        "{\"value\":[{}]}",
        "{\"value\":[{\"properties\":{\"channelName\":\"SlackChannel\",\"properties\":{\"clientSecret\":\"secret\"}}}]}",
        "{\"value\":[],\"nextLink\":42}",
        "{\"value\":[],\"nextLink\":\"https://management.azure.com/next\"}",
        "{\"a\":\"\\ud800\"}",
        "{\"a\":1e400}",
        Nested(256),
    ];

    [Theory]
    [MemberData(nameof(Corpus))]
    public async Task ChannelReaders_ReturnResultOrTypedFailure(string body)
    {
        await DriveAsync(body);
    }

    [Fact]
    public async Task ChannelReaders_SurviveDeterministicMutations()
    {
        var random = new Random(Seed);

        for (var iteration = 0; iteration < GeneratedCases; iteration++)
        {
            var body = Mutate(SeedCorpus[random.Next(SeedCorpus.Length)], random);

            try
            {
                await DriveAsync(body);
            }
            catch (Exception failure)
            {
                Assert.Fail(
                    $"A mutated channel response escaped as an untyped failure. " +
                    $"Seed {Seed}, iteration {iteration}, body {body}, failure {failure}");
            }
        }
    }

    public static TheoryData<string> Corpus()
    {
        var data = new TheoryData<string>();
        foreach (var body in SeedCorpus)
        {
            data.Add(body);
        }

        data.Add("{\"properties\":{\"channelName\":\"SlackChannel\",\"properties\":{\"clientId\":\"" +
            new string('x', 200_000) + "\"}}}");
        data.Add("{\"value\":[" +
            string.Join(',', Enumerable.Repeat(
                "{\"properties\":{\"channelName\":\"MsteamsChannel\"}}",
                10_000)) +
            "]}");

        return data;
    }

    private static async Task DriveAsync(string body)
    {
        await AssertResultOrTypedFailureAsync(() => ReadSingleAsync(body));
        await AssertResultOrTypedFailureAsync(() => ReadListAsync(body));
    }

    private static async Task ReadSingleAsync(string body)
    {
        var handler = new StubHttpMessageHandler(_ => Json(body));
        await Adapter(handler).GetChannelAsync("SlackChannel", BotResourceId, targetTenantId: null, default);
    }

    private static async Task ReadListAsync(string body)
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
            Json(requestCount++ == 0 ? body : """{"value":[]}"""));

        await Adapter(handler).ListChannelsAsync(BotResourceId, targetTenantId: null, default);
    }

    private static async Task AssertResultOrTypedFailureAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (AgentHostingException failure)
        {
            Assert.True(
                failure.ErrorKind is "invalid-response" or "channel-definition-not-found",
                $"Unexpected typed failure: {failure.ErrorKind}");
            Assert.NotNull(failure.Remediation);
        }
    }

    private static DeploymentChannelAdapter Adapter(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler), new StubTokenProvider { AccessToken = "arm-token", TenantId = "home" });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static string Mutate(string body, Random random)
    {
        var mutated = new StringBuilder(body);

        for (var edit = 0; edit <= random.Next(4); edit++)
        {
            switch (random.Next(6))
            {
                case 0 when mutated.Length > 0:
                    mutated.Remove(random.Next(mutated.Length), 1);
                    break;

                case 1:
                    mutated.Insert(random.Next(mutated.Length + 1), "{}[]\":,\\\0\uFFFD"[random.Next(10)]);
                    break;

                case 2 when mutated.Length > 0:
                    mutated[random.Next(mutated.Length)] = (char)random.Next(char.MaxValue);
                    break;

                case 3:
                    mutated.Insert(0, "{\"value\":[");
                    break;

                case 4 when mutated.Length > 1:
                    mutated.Length = random.Next(1, mutated.Length);
                    break;

                default:
                    mutated.Append(new string('}', random.Next(1, 64)));
                    break;
            }
        }

        return mutated.ToString();
    }

    private static string Nested(int depth) =>
        string.Concat(Enumerable.Repeat("{\"a\":", depth)) + "1" + new string('}', depth);
}
