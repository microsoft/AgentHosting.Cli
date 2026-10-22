using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Core.Abs;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using Microsoft.Agents.Hosting.Cli.Core.Ports;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Apply-path conformance contract runner. Drives the apply subset of the provisional ABS oracle fixtures
/// ({02,03,04,05,06,08} — every fixture whose verb is <c>apply</c>) through the full parse → dispatch →
/// render → exit host pipeline with fakes built from each fixture's <c>azureState</c>, then compares
/// against the frozen golden: JSON is deep-equal (order-insensitive); human output is compared by
/// ordered, ANSI-stripped, trailing-whitespace-trimmed, blank-dropped lines; the exit code and the
/// empty-stderr-in-JSON-mode invariant are exact. It additionally asserts the recorded mutation calls
/// exactly match the fixture's <c>assert.mutationCalls</c> — proving the confirmed create/update path
/// issues exactly one <c>arm.upsert</c>, apply-without-force and no-op perform zero mutation, and a
/// blocking conflict/preflight fails before any mutation.
/// </summary>
public sealed class AbsApplyContractTests
{
    public static IEnumerable<object[]> ApplyFixtures()
    {
        foreach (var path in Directory.EnumerateFiles(FixtureDirectory, "*.json").OrderBy(p => p))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var argv = doc.RootElement.GetProperty("argv");
            if (argv.GetArrayLength() >= 2 && argv[1].GetString() == "apply")
            {
                yield return [Path.GetFileName(path)];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ApplyFixtures))]
    public async Task Fixture_JsonMode_MatchesGoldenEnvelope(string fixtureFile)
    {
        var fixture = LoadFixture(fixtureFile);
        var argv = fixture.Argv.Append("--json").ToArray();

        var (exitCode, stdout, stderr, mutationCalls) = await RunHostAsync(fixture, argv);

        Assert.Equal(fixture.ExitCode, exitCode);
        Assert.Empty(NormalizeLines(stderr)); // JSON mode never writes to stderr.
        Assert.Equal(fixture.MutationCalls, mutationCalls);

        using var actual = JsonDocument.Parse(stdout);
        var comparable = AbsContractEnvelope.WithoutNumericErrorCode(actual.RootElement, fixtureFile);
        Assert.True(
            JsonDeepEquals(fixture.GoldenJson, comparable),
            $"JSON envelope diverged from golden for {fixtureFile}.\nExpected: " +
            $"{Canonicalize(fixture.GoldenJson)}\nActual:   {Canonicalize(actual.RootElement)}");
    }

    [Theory]
    [MemberData(nameof(ApplyFixtures))]
    public async Task Fixture_HumanMode_MatchesGoldenLines(string fixtureFile)
    {
        var fixture = LoadFixture(fixtureFile);

        var (exitCode, stdout, stderr, mutationCalls) = await RunHostAsync(fixture, fixture.Argv);

        Assert.Equal(fixture.ExitCode, exitCode);
        Assert.Equal(fixture.GoldenStdout, NormalizeLines(stdout));
        Assert.Equal(fixture.GoldenStderr, NormalizeLines(stderr));
        Assert.Equal(fixture.MutationCalls, mutationCalls);
    }

    // ── host harness ────────────────────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Stdout, string Stderr, IReadOnlyList<string> MutationCalls)>
        RunHostAsync(Fixture fixture, string[] argv)
    {
        using var dir = new TempDir();
        var mutation = new RecordingAbsMutationPort();
        var operations = BuildOperations(fixture, mutation, dir.File("profiles.json"));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await new AgentHostCli(
                operations,
                featureFlags: new TestFeatureFlags(FeatureName.AzureBotServiceFeatures))
            .InvokeAsync(argv, stdout, stderr, color: false);
        return (exitCode, stdout.ToString(), stderr.ToString(), mutation.Calls);
    }

    private static AgentHostingOperations BuildOperations(
        Fixture fixture,
        RecordingAbsMutationPort mutation,
        string profilesFile)
    {
        var absService = new AbsProvisioningService(fixture.Reader);
        var absApply = new AbsApplyService(
            fixture.Reader,
            mutation,
            new InMemoryConnectionMetadataStore());
        var profiles = new FileAuthProfileStore(profilesFile);
        var environments = EnvironmentFixtures.Service(
            new FakeEnvironmentClient(),
            new InMemoryEnvironmentSelectionStore(),
            profiles);
        return new AgentHostingOperations(
            new FakeAuthenticationSession(),
            profiles,
            new StaticInteractionPolicy(false),
            FakePlatformInfo.Windows(),
            new RecordingMessagePresenter(),
            new AuthSessionContext
            {
                ClientId = "contract-client",
                Cloud = CloudInstance.Public,
                Authority = "https://login.microsoftonline.com/organizations",
            },
            absService,
            absApply,
            environments,
            AgentCreateFixtures.PendingService(environments),
            AgentDeployFixtures.PendingUploadService(environments),
            AgentDeployFixtures.PendingService(environments));
    }

    // ── fixture loading ─────────────────────────────────────────────────────────────

    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "ConformanceFixtures");

    private sealed record Fixture
    {
        public required string[] Argv { get; init; }
        public required int ExitCode { get; init; }
        public required JsonElement GoldenJson { get; init; }
        public required IReadOnlyList<string> GoldenStdout { get; init; }
        public required IReadOnlyList<string> GoldenStderr { get; init; }
        public required IReadOnlyList<string> MutationCalls { get; init; }
        public required FakeAbsStateReader Reader { get; init; }
    }

    private static Fixture LoadFixture(string fixtureFile)
    {
        var json = File.ReadAllText(Path.Combine(FixtureDirectory, fixtureFile));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var azureState = root.GetProperty("azureState");
        var assert = root.GetProperty("assert");
        var golden = root.GetProperty("golden");
        var human = golden.GetProperty("human");

        return new Fixture
        {
            Argv = [.. root.GetProperty("argv").EnumerateArray().Select(e => e.GetString()!)],
            ExitCode = assert.GetProperty("exitCode").GetInt32(),
            GoldenJson = golden.GetProperty("json").Clone(),
            GoldenStdout = [.. human.GetProperty("stdout").EnumerateArray().Select(e => e.GetString()!)],
            GoldenStderr = [.. human.GetProperty("stderr").EnumerateArray().Select(e => e.GetString()!)],
            MutationCalls = [.. assert.GetProperty("mutationCalls").EnumerateArray().Select(e => e.GetString()!)],
            Reader = BuildReader(azureState),
        };
    }

    private static FakeAbsStateReader BuildReader(JsonElement azureState)
    {
        AbsBotSnapshot? existingBot = null;
        if (azureState.TryGetProperty("existingBot", out var bot) && bot.ValueKind == JsonValueKind.Object)
        {
            existingBot = new AbsBotSnapshot
            {
                BotResourceId = bot.GetProperty("botResourceId").GetString()!,
                AppId = OptionalString(bot, "msaAppId"),
                MsaAppType = OptionalString(bot, "msaAppType"),
                MessagingEndpoint = OptionalString(bot, "endpoint"),
                Sku = OptionalString(bot, "sku"),
            };
        }

        return new FakeAbsStateReader
        {
            ProviderRegistrationState = azureState.GetProperty("providerRegistration").GetString()!,
            Subscription = ScopeAccess(azureState.GetProperty("subscription")),
            ResourceGroup = ScopeAccess(azureState.GetProperty("resourceGroup")),
            ExistingBot = existingBot,
        };
    }

    private static AbsScopeAccess ScopeAccess(JsonElement element) => new()
    {
        Exists = element.GetProperty("exists").GetBoolean(),
        HasReadAccess = element.GetProperty("hasReadAccess").GetBoolean(),
    };

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ── comparison helpers ──────────────────────────────────────────────────────────

    private static readonly Regex AnsiPattern = new("\u001b\\[[0-9;]*m", RegexOptions.Compiled);

    private static List<string> NormalizeLines(string text) =>
        [.. AnsiPattern.Replace(text, string.Empty)
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Length > 0)];

    private static bool JsonDeepEquals(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind != actual.ValueKind)
        {
            return false;
        }

        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var expectedProps = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var actualProps = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                if (expectedProps.Count != actualProps.Count)
                {
                    return false;
                }

                foreach (var (name, value) in expectedProps)
                {
                    if (!actualProps.TryGetValue(name, out var other) || !JsonDeepEquals(value, other))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.Array:
                if (expected.GetArrayLength() != actual.GetArrayLength())
                {
                    return false;
                }

                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                for (var i = 0; i < expectedItems.Length; i++)
                {
                    if (!JsonDeepEquals(expectedItems[i], actualItems[i]))
                    {
                        return false;
                    }
                }

                return true;

            case JsonValueKind.String:
                return expected.GetString() == actual.GetString();

            case JsonValueKind.Number:
                return expected.GetRawText() == actual.GetRawText();

            default:
                return true; // true / false / null — kind already matched.
        }
    }

    private static string Canonicalize(JsonElement element)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText());
        return Sort(node)?.ToJsonString() ?? "null";
    }

    private static System.Text.Json.Nodes.JsonNode? Sort(System.Text.Json.Nodes.JsonNode? node)
    {
        switch (node)
        {
            case System.Text.Json.Nodes.JsonObject obj:
                var sorted = new System.Text.Json.Nodes.JsonObject();
                foreach (var (key, value) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    sorted[key] = Sort(value?.DeepClone());
                }

                return sorted;

            case System.Text.Json.Nodes.JsonArray array:
                var newArray = new System.Text.Json.Nodes.JsonArray();
                foreach (var item in array)
                {
                    newArray.Add(Sort(item?.DeepClone()));
                }

                return newArray;

            default:
                return node?.DeepClone();
        }
    }
}
