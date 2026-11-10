using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Commands.Features;
using Microsoft.Agents.Hosting.Cli.Features;
using Microsoft.Agents.Hosting.Cli.Hosting;

namespace Microsoft.Agents.Hosting.Cli.Tests;

[CollectionDefinition(nameof(FeatureFlagsEnvironmentCollection), DisableParallelization = true)]
public sealed class FeatureFlagsEnvironmentCollection;

[Collection(nameof(FeatureFlagsEnvironmentCollection))]
public sealed class FeatureFlagsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        AppContext.BaseDirectory,
        $"feature-flags-{Guid.NewGuid():N}");
    private readonly string? _originalEnvironment =
        Environment.GetEnvironmentVariable(FeatureFlags.OverrideEnvironmentVariable);

    public FeatureFlagsTests()
    {
        Environment.SetEnvironmentVariable(FeatureFlags.OverrideEnvironmentVariable, null);
    }

    [Fact]
    public void ShippedDefaults_DisableReviewedSurfaces()
    {
        var flags = FeatureFlags.LoadFromAssemblyFolder(typeof(AgentHostCli).Assembly);

        Assert.False(flags.IsFeatureEnabled(FeatureName.AmsFeatures));
        Assert.False(flags.IsFeatureEnabled(FeatureName.AzureBotServiceFeatures));
        Assert.False(flags.IsFeatureEnabled(FeatureName.IdentityFeatures));
        Assert.False(flags.IsFeatureEnabled(FeatureName.LegacyAuth));
    }

    [Theory]
    [InlineData(nameof(FeatureName.AmsFeatures), FeatureName.AmsFeatures)]
    [InlineData(nameof(FeatureName.AzureBotServiceFeatures), FeatureName.AzureBotServiceFeatures)]
    [InlineData(nameof(FeatureName.IdentityFeatures), FeatureName.IdentityFeatures)]
    [InlineData(nameof(FeatureName.LegacyAuth), FeatureName.LegacyAuth)]
    [InlineData("legacyauth", FeatureName.LegacyAuth)]
    public void EnvironmentList_IndependentlyEnablesEachReviewedSurface(
        string environment,
        FeatureName expectedFeature)
    {
        var flags = LoadAllOff(environment);

        foreach (var feature in new[]
        {
            FeatureName.AmsFeatures,
            FeatureName.AzureBotServiceFeatures,
            FeatureName.IdentityFeatures,
            FeatureName.LegacyAuth,
        })
        {
            Assert.Equal(feature == expectedFeature, flags.IsFeatureEnabled(feature));
        }
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("off", false)]
    [InlineData("true", false)]
    [InlineData("unknown", false)]
    public void FileValue_OnlyOnEnables(string value, bool expected)
    {
        var flags = Load(value, environment: null);

        Assert.Equal(expected, flags.IsFeatureEnabled(FeatureName.AmsFeatures));
    }

    [Theory]
    [InlineData("AmsFeatures")]
    [InlineData("amsfeatures")]
    [InlineData("OtherFeature; AmsFeatures ;ThirdFeature")]
    public void EnvironmentList_EnablesAFeatureWhoseFileDefaultIsOff(string environment)
    {
        var flags = Load("off", environment);

        Assert.True(flags.IsFeatureEnabled(FeatureName.AmsFeatures));
    }

    [Fact]
    public void FileOn_SurvivesAnOverrideThatDoesNotListTheFeature()
    {
        var flags = Load("on", "IdentityFeatures");

        Assert.True(flags.IsFeatureEnabled(FeatureName.AmsFeatures));
    }

    [Fact]
    public void JsonKeys_AreCaseInsensitive()
    {
        var flags = LoadJson("""{"aMsFeAtUrEs":"on"}""", environment: null);

        Assert.True(flags.IsFeatureEnabled(FeatureName.AmsFeatures));
    }

    [Fact]
    public void Environment_IsReevaluatedAfterConstruction()
    {
        var flags = Load("off", environment: null);
        Assert.False(flags.IsFeatureEnabled(FeatureName.AmsFeatures));

        Environment.SetEnvironmentVariable(
            FeatureFlags.OverrideEnvironmentVariable,
            nameof(FeatureName.AmsFeatures));
        Assert.True(flags.IsFeatureEnabled(FeatureName.AmsFeatures));

        Environment.SetEnvironmentVariable(FeatureFlags.OverrideEnvironmentVariable, null);
        Assert.False(flags.IsFeatureEnabled(FeatureName.AmsFeatures));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("UnknownFeature")]
    [InlineData("AmsFeatures=true")]
    public void UnknownOrMalformedEnvironmentEntries_DoNotChangeTheFileDefault(string environment)
    {
        var flags = Load("off", environment);

        Assert.False(flags.IsFeatureEnabled(FeatureName.AmsFeatures));
    }

    [Fact]
    public void MalformedJson_FailsLoudly()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "featureflags.json");
        File.WriteAllText(path, "{");

        Assert.Throws<JsonException>(() => FeatureFlags.LoadFromFile(path));
    }

    [Fact]
    public void NullJson_FailsLoudly()
    {
        var path = WriteJson("null");

        Assert.Throws<JsonException>(() => FeatureFlags.LoadFromFile(path));
    }

    [Fact]
    public void MissingDefaultFile_ReportsInstallationErrorAndReinstallGuidance()
    {
        var missingName = $"missing-feature-flags-{Guid.NewGuid():N}.json";
        var expectedPath = Path.Combine(
            Path.GetDirectoryName(typeof(AgentHostCli).Assembly.Location)!,
            missingName);

        var failure = Assert.Throws<FileNotFoundException>(
            () => FeatureFlags.LoadFromAssemblyFolder(typeof(AgentHostCli).Assembly, missingName));

        Assert.Equal(expectedPath, failure.FileName);
        Assert.Equal(
            $"Installation error: the required feature controls file '{expectedPath}' is missing. " +
            "Please uninstall and reinstall the Microsoft.Agents.Hosting.Cli tool.",
            failure.Message);
    }

    [Fact]
    public void NullDefaultFileName_FailsLoudly()
    {
        Assert.Throws<ArgumentNullException>(
            () => FeatureFlags.LoadFromAssemblyFolder(typeof(AgentHostCli).Assembly, null!));
    }

    [Fact]
    public void NullFilePath_FailsLoudly()
    {
        Assert.Throws<ArgumentNullException>(() => FeatureFlags.LoadFromFile(null!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        Environment.SetEnvironmentVariable(
            FeatureFlags.OverrideEnvironmentVariable,
            _originalEnvironment);
    }

    private FeatureFlags Load(string value, string? environment)
    {
        var path = WriteJson($$"""{"AmsFeatures":"{{value}}"}""");
        Environment.SetEnvironmentVariable(FeatureFlags.OverrideEnvironmentVariable, environment);

        return FeatureFlags.LoadFromFile(path);
    }

    private FeatureFlags LoadJson(string json, string? environment)
    {
        var path = WriteJson(json);
        Environment.SetEnvironmentVariable(FeatureFlags.OverrideEnvironmentVariable, environment);

        return FeatureFlags.LoadFromFile(path);
    }

    private FeatureFlags LoadAllOff(string environment)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "featureflags.json");
        File.WriteAllText(
            path,
            """
            {
              "amsFeatures": "off",
              "azureBotServiceFeatures": "off",
              "identityFeatures": "off",
              "legacyAuth": "off"
            }
            """);
        Environment.SetEnvironmentVariable(FeatureFlags.OverrideEnvironmentVariable, environment);

        return FeatureFlags.LoadFromFile(path);
    }

    private string WriteJson(string json)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "featureflags.json");
        File.WriteAllText(path, json);
        return path;
    }
}
