using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

[Collection(AmsMcsTestKit.Collection)]
public sealed class AmsMcsConfigTests
{
    private static readonly Dictionary<string, Func<AmsMcsConfig, string?>> Keys = new()
    {
        ["FEATURES"] = config => config.Features,
        ["DATA_PLANE_ENDPOINT"] = config => config.DataPlane.Endpoint,
        ["DATA_PLANE_AUDIENCE"] = config => config.DataPlane.Audience,
        ["ON_BEHALF_OF_ENDPOINT"] = config => config.OnBehalfOf.Endpoint,
        ["ON_BEHALF_OF_AUDIENCE"] = config => config.OnBehalfOf.Audience,
        ["CONTROL_PLANE_ENDPOINT"] = config => config.ControlPlane.Endpoint,
        ["CONTROL_PLANE_AUDIENCE"] = config => config.ControlPlane.Audience,
        ["GIT_HOST"] = config => config.GitHost,
        ["DEV_ENVIRONMENT_ID"] = config => config.DevEnvironmentId,
        ["TEST_ENVIRONMENT_ID"] = config => config.TestEnvironmentId,
        ["PROD_ENVIRONMENT_ID"] = config => config.ProdEnvironmentId,
        ["ORGANIZATIONS_DEV"] = config => config.Organizations.Dev,
        ["ORGANIZATIONS_TEST"] = config => config.Organizations.Test,
        ["ORGANIZATIONS_PROD"] = config => config.Organizations.Prod,
        ["WORKSPACE_ROOT"] = config => config.WorkspaceRoot,
        ["FORBIDDEN_ENVIRONMENT_IDS"] = config => config.ForbiddenEnvironmentIds is { } identifiers ? string.Join(',', identifiers) : null,
    };

    public static TheoryData<string> Suffixes() => [.. VariableScope.AmsMcsSuffixes];

    [Fact]
    public void EveryAmsMcsKeyIsReadFromTheConfigFile()
    {
        using var variables = new VariableScope();
        var config = LoadWithFile();

        Assert.All(Keys, key => Assert.Equal(FileValue(key.Key), key.Value(config)));
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void EachAmsMcsVariableWinsOverTheConfigFile(string suffix)
    {
        using var variables = new VariableScope();
        VariableScope.AmsMcs(suffix, "from-variable");

        var config = LoadWithFile();

        Assert.All(Keys, key => Assert.Equal(key.Key == suffix ? "from-variable" : FileValue(key.Key), key.Value(config)));
    }

    [Fact]
    public void TheLegacyPrefixIsStillHonouredForAnAmsMcsKey()
    {
        using var variables = new VariableScope();
        VariableScope.AmsMcs("GIT_HOST", "legacy.example.invalid", E2EConfig.LegacyEnvironmentPrefix);

        Assert.Equal("legacy.example.invalid", LoadWithFile().GitHost);
    }

    [Fact]
    public void TheCurrentPrefixWinsOverTheLegacyOneForAnAmsMcsKey()
    {
        using var variables = new VariableScope();
        VariableScope.AmsMcs("GIT_HOST", "current.example.invalid");
        VariableScope.AmsMcs("GIT_HOST", "legacy.example.invalid", E2EConfig.LegacyEnvironmentPrefix);

        Assert.Equal("current.example.invalid", LoadWithFile().GitHost);
    }

    [Fact]
    public void TheForbiddenVariableSplitsOnCommasAndSemicolons()
    {
        using var variables = new VariableScope();
        VariableScope.AmsMcs("FORBIDDEN_ENVIRONMENT_IDS", $" {AmsMcsTestKit.Dev} ,{AmsMcsTestKit.Test};;{AmsMcsTestKit.Prod}");

        Assert.Equal([AmsMcsTestKit.Dev, AmsMcsTestKit.Test, AmsMcsTestKit.Prod], LoadWithFile().ForbiddenEnvironmentIds);
    }

    [Fact]
    public void AConfigWithoutTheBlockReadsAsEmptyWithTheDefaultFlag()
    {
        using var variables = new VariableScope();
        using var directory = new ScratchDirectory();
        directory.Place("e2e.config.json", "{}");

        var config = E2EConfig.Load(directory.Path).AmsMcs;

        Assert.All(Keys, key => Assert.Null(key.Value(config)));
        Assert.Equal("AmsMcsAgents", config.FeatureValue);
        Assert.True(config.AmsFlagOn);
    }

    [Theory]
    [InlineData(null, "AmsMcsAgents", true, null)]
    [InlineData("  ", "AmsMcsAgents", true, null)]
    [InlineData(" amsmcsagents ", "amsmcsagents", true, null)]
    [InlineData("AmsMcsAgents; EvaluationFeatures", "AmsMcsAgents; EvaluationFeatures", true, "EvaluationFeatures")]
    [InlineData("EvaluationFeatures;Other", "EvaluationFeatures;Other", false, "EvaluationFeatures;Other")]
    public void TheFlagValueHoldsAmsMcsAgentsAndTheOffValueKeepsEveryOtherFeature(string? features, string value, bool on, string? withoutAms)
    {
        var config = new AmsMcsConfig { Features = features };

        Assert.Equal(value, config.FeatureValue);
        Assert.Equal(on, config.AmsFlagOn);
        Assert.Equal(withoutAms, config.FeaturesWithoutAms);
    }

    [Fact]
    public void TheOrganizationsAreKeyedByEnvironmentWithoutCaseAndSkipWhatIsMissing()
    {
        var config = AmsMcsTestKit.Config() with { TestEnvironmentId = null, Organizations = new AmsMcsOrganizations { Dev = "https://dev.example.invalid", Test = "https://test.example.invalid", Prod = "not a url" } };

        var organizations = config.OrganizationsByEnvironment();

        Assert.Equal(new Uri("https://dev.example.invalid"), Assert.Single(organizations).Value);
        Assert.True(organizations.ContainsKey(AmsMcsTestKit.Dev.ToUpperInvariant()));
    }

    [Fact]
    public void TwoRolesNamingOneEnvironmentGiveOneOrganization()
    {
        var config = AmsMcsTestKit.Config() with { ProdEnvironmentId = AmsMcsTestKit.Dev.ToUpperInvariant() };

        Assert.Equal(new Uri("https://dev.example.invalid"), config.OrganizationsByEnvironment()[AmsMcsTestKit.Dev]);
        Assert.Equal(2, config.OrganizationsByEnvironment().Count);
    }

    [Fact]
    public void TheSettingsPassEachValueToItsMsagentVariable()
    {
        var settings = AmsMcsTestKit.Config().Settings;

        Assert.Equal(["MSAGENT_AMS_DATAPLANE_ENDPOINT", "MSAGENT_AMS_DATAPLANE_AUDIENCE", "MSAGENT_AMS_ONBEHALFOF_ENDPOINT", "MSAGENT_AMS_ONBEHALFOF_AUDIENCE", "MSAGENT_AMS_CONTROLPLANE_ENDPOINT", "MSAGENT_AMS_CONTROLPLANE_AUDIENCE", "MSAGENT_GIT_HOST"], settings.Select(setting => setting.Variable));
        Assert.Equal(["https://data-plane.example.invalid", "https://data-plane-audience.example.invalid", "https://on-behalf-of.example.invalid", "https://on-behalf-of-audience.example.invalid", "https://control-plane.example.invalid", "https://control-plane-audience.example.invalid", AmsMcsTestKit.GitHost], settings.Select(setting => setting.Value));
    }

    private static string FileValue(string suffix) => "file-" + suffix.ToLowerInvariant().Replace('_', '-');

    private static AmsMcsConfig LoadWithFile()
    {
        using var directory = new ScratchDirectory();
        directory.Place("e2e.config.json", $$"""
            {
              "amsMcs": {
                "features": "{{FileValue("FEATURES")}}",
                "dataPlane": { "endpoint": "{{FileValue("DATA_PLANE_ENDPOINT")}}", "audience": "{{FileValue("DATA_PLANE_AUDIENCE")}}" },
                "onBehalfOf": { "endpoint": "{{FileValue("ON_BEHALF_OF_ENDPOINT")}}", "audience": "{{FileValue("ON_BEHALF_OF_AUDIENCE")}}" },
                "controlPlane": { "endpoint": "{{FileValue("CONTROL_PLANE_ENDPOINT")}}", "audience": "{{FileValue("CONTROL_PLANE_AUDIENCE")}}" },
                "gitHost": "{{FileValue("GIT_HOST")}}",
                "devEnvironmentId": "{{FileValue("DEV_ENVIRONMENT_ID")}}",
                "testEnvironmentId": "{{FileValue("TEST_ENVIRONMENT_ID")}}",
                "prodEnvironmentId": "{{FileValue("PROD_ENVIRONMENT_ID")}}",
                "organizations": { "dev": "{{FileValue("ORGANIZATIONS_DEV")}}", "test": "{{FileValue("ORGANIZATIONS_TEST")}}", "prod": "{{FileValue("ORGANIZATIONS_PROD")}}" },
                "workspaceRoot": "{{FileValue("WORKSPACE_ROOT")}}",
                "forbiddenEnvironmentIds": ["{{FileValue("FORBIDDEN_ENVIRONMENT_IDS")}}"],
              },
            }
            """);

        return E2EConfig.Load(directory.Path).AmsMcs;
    }
}
