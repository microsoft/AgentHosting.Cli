using System.Text;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.ManagedAgents;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class PowerAppsConnectionResourceFuzzTests
{
    private const int Seed = 20240216;
    private const int GeneratedCases = 4000;

    private static readonly string[] SeedCorpus =
    [
        string.Empty,
        "   ",
        "\0",
        "null",
        "true",
        "0",
        "[]",
        "\"a string, not an object\"",
        "{",
        "}",
        "{\"properties\":",
        "{\"properties\":{\"statuses\":[{\"error\":{\"code\":\"Unauthenticated\"}}]}}",
        "{\"properties\":{\"statuses\":[{\"status\":\"Error\",\"error\":{\"code\":\"Throttled\",\"message\":\"no capacity\"}}]}}",
        "{\"properties\":{\"statuses\":{}}}",
        "{\"properties\":{\"statuses\":[42]}}",
        "{\"properties\":{\"statuses\":[null]}}",
        "{\"properties\":{\"statuses\":[{\"error\":[]}]}}",
        "{\"properties\":{\"statuses\":[{\"error\":{\"code\":1}}]}}",
        "{\"properties\":{\"displayName\":{}}}",
        "{\"properties\":{\"displayName\":null}}",
        "{\"properties\":[]}",
        "{\"properties\":\"not an object\"}",
        "{\"consentLink\":\"https://login.example/?code=abc\"}",
        "{\"consentLink\":12345}",
        "{\"consentLink\":\"\\u0000\"}",
        "{\"consentLink\":\"not a uri\"}",
        "{\"\":\"\"}",
        "{\"a\":\"\\ud800\"}",
        "{\"a\":1e400}",
        "{\"a\":-0.0000000000000000000000001}",
        "[[[[[[[[[[]]]]]]]]]]",
        "{\"a\":{\"a\":{\"a\":{\"a\":{\"a\":{\"a\":1}}}}}}",
    ];

    public static TheoryData<string> Corpus()
    {
        var data = new TheoryData<string>();

        foreach (var seed in SeedCorpus)
        {
            data.Add(seed);
        }

        data.Add(Nested(PowerAppsConnectionBody.MaxDepth * 4));
        data.Add(Truncated(Nested(PowerAppsConnectionBody.MaxDepth * 4)));
        data.Add("{\"properties\":{\"displayName\":\"" + new string('x', 200_000) + "\"}}");
        data.Add("{\"properties\":{\"statuses\":[" + string.Join(',', Enumerable.Repeat("{\"status\":\"Error\"}", 20_000)) + "]}}");

        return data;
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void EveryReader_SurvivesAHostileBody(string body)
    {
        Drive(body);
    }

    [Fact]
    public void EveryReader_SurvivesAMutatedCorpus()
    {
        var random = new Random(Seed);

        for (var iteration = 0; iteration < GeneratedCases; iteration++)
        {
            var body = Mutate(SeedCorpus[random.Next(SeedCorpus.Length)], random);

            try
            {
                Drive(body);
            }
            catch (Exception failure)
            {
                Assert.Fail($"Reading a mutated body crashed the reader. Seed {Seed}, iteration {iteration}, body {body}, failure {failure}");
            }
        }
    }

    private static void Drive(string body)
    {
        PowerAppsConnectionBody.Excerpt(body);
        PowerAppsConnectionBody.Redacted(body);

        using var document = PowerAppsConnectionResource.Parse(body);

        if (document is null)
        {
            return;
        }

        var root = document.RootElement;

        PowerAppsConnectionResource.DisplayNameOf(root);
        PowerAppsConnectionResource.ConsentLinkOf(root);
        PowerAppsConnectionResource.ReportsReady(root);
        PowerAppsConnectionResource.ReportsUnauthenticated(root);
        PowerAppsConnectionResource.ErrorCodeOf(root);
    }

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
                    mutated.Insert(0, "{\"properties\":{\"statuses\":[");
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

    private static string Truncated(string body) => body[..(body.Length / 2)];
}
