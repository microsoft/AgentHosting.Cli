using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.Hosting.Cli.Core.Ams;
using Microsoft.Agents.Hosting.Cli.Core.Auth;

namespace Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

/// <summary>Everything the operator supplies, read from <c>e2e.config.json</c> or the environment.</summary>
public sealed record E2EConfig
{
    public string? TenantId { get; init; }

    public McsConfig Mcs { get; init; } = new();

    public AzureConfig Azure { get; init; } = new();

    public AmsConfig Ams { get; init; } = new();

    public AmsMcsConfig AmsMcs { get; init; } = new();

    /// <summary>Reads the config file when present, then lets environment variables override it.</summary>
    public static E2EConfig Load(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, "e2e.config.json");
        var config = File.Exists(path)
            ? JsonSerializer.Deserialize<E2EConfig>(ReadConfigText(path), ConfigJson.Options) ?? new E2EConfig()
            : new E2EConfig();

        return config.WithEnvironmentOverrides();
    }

    /// <summary>Creates the operator's config from the committed template, returning its path when it made one.</summary>
    public static string? CreateFromTemplate(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, "e2e.config.json");
        var template = Path.Combine(projectDirectory, "e2e.config.sample.json");

        if (File.Exists(path) || !File.Exists(template))
        {
            return null;
        }

        File.Copy(template, path);
        return path;
    }

    private static string ReadConfigText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException exception)
        {
            throw new E2EException($"Could not read {path}: {exception.Message}", exception);
        }
    }

    private E2EConfig WithEnvironmentOverrides() => this with
    {
        TenantId = Env("TENANT_ID") ?? TenantId,
        Mcs = Mcs with
        {
            DevEnvironmentId = Env("MCS_DEV_ENV") ?? Mcs.DevEnvironmentId,
            TestEnvironmentId = Env("MCS_TEST_ENV") ?? Mcs.TestEnvironmentId,
            ShareUserObjectId = Env("MCS_SHARE_USER") ?? Mcs.ShareUserObjectId,
            Workspaces = Mcs.Workspaces with
            {
                CliAgent = Env("WORKSPACE_CLI") ?? Mcs.Workspaces.CliAgent,
                ClassicAgent = Env("WORKSPACE_CLASSIC") ?? Mcs.Workspaces.ClassicAgent,
                Workflow = Env("WORKSPACE_WORKFLOW") ?? Mcs.Workspaces.Workflow,
            },
        },
        Azure = Azure with
        {
            SubscriptionId = Env("AZURE_SUBSCRIPTION") ?? Azure.SubscriptionId,
            ResourceGroup = Env("AZURE_RESOURCE_GROUP") ?? Azure.ResourceGroup,
            BotNamePrefix = Env("AZURE_BOT_PREFIX") ?? Azure.BotNamePrefix,
            ExistingMsaAppId = Env("AZURE_MSA_APP_ID") ?? Azure.ExistingMsaAppId,
            ExistingBotName = Env("AZURE_EXISTING_BOT") ?? Azure.ExistingBotName,
            AllowCreate = EnvFlag("AZURE_ALLOW_CREATE") ?? Azure.AllowCreate,
        },
        Ams = Ams with { Endpoint = Env("AMS_ENDPOINT") ?? Ams.Endpoint },
        AmsMcs = AmsMcs with
        {
            Features = Env("AMS_MCS_FEATURES") ?? AmsMcs.Features,
            DataPlane = AmsService("DATA_PLANE", AmsMcs.DataPlane),
            OnBehalfOf = AmsService("ON_BEHALF_OF", AmsMcs.OnBehalfOf),
            ControlPlane = AmsService("CONTROL_PLANE", AmsMcs.ControlPlane),
            GitHost = Env("AMS_MCS_GIT_HOST") ?? AmsMcs.GitHost,
            DevEnvironmentId = Env("AMS_MCS_DEV_ENVIRONMENT_ID") ?? AmsMcs.DevEnvironmentId,
            TestEnvironmentId = Env("AMS_MCS_TEST_ENVIRONMENT_ID") ?? AmsMcs.TestEnvironmentId,
            ProdEnvironmentId = Env("AMS_MCS_PROD_ENVIRONMENT_ID") ?? AmsMcs.ProdEnvironmentId,
            Organizations = new AmsMcsOrganizations
            {
                Dev = Env("AMS_MCS_ORGANIZATIONS_DEV") ?? AmsMcs.Organizations.Dev,
                Test = Env("AMS_MCS_ORGANIZATIONS_TEST") ?? AmsMcs.Organizations.Test,
                Prod = Env("AMS_MCS_ORGANIZATIONS_PROD") ?? AmsMcs.Organizations.Prod,
            },
            WorkspaceRoot = Env("AMS_MCS_WORKSPACE_ROOT") ?? AmsMcs.WorkspaceRoot,
            ForbiddenEnvironmentIds = Env("AMS_MCS_FORBIDDEN_ENVIRONMENT_IDS")?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? AmsMcs.ForbiddenEnvironmentIds,
        },
    };

    private static AmsServiceConfig AmsService(string key, AmsServiceConfig configured) => new() { Endpoint = Env($"AMS_MCS_{key}_ENDPOINT") ?? configured.Endpoint, Audience = Env($"AMS_MCS_{key}_AUDIENCE") ?? configured.Audience };

    /// <summary>The prefix every harness environment variable takes.</summary>
    public const string EnvironmentPrefix = "MSAGENT_E2E_";

    /// <summary>The prefix this harness answered to before the CLI was renamed, still honoured.</summary>
    public const string LegacyEnvironmentPrefix = "AH_E2E_";

    private static string? Env(string suffix) => Read(EnvironmentPrefix + suffix) ?? Read(LegacyEnvironmentPrefix + suffix);

    private static string? Read(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static bool? EnvFlag(string suffix) => Env(suffix) is { } value && bool.TryParse(value, out var parsed) ? parsed : null;
}

/// <summary>Copilot Studio environments and the workspaces deployed into them.</summary>
public sealed record McsConfig
{
    public string? DevEnvironmentId { get; init; }

    public string? TestEnvironmentId { get; init; }

    public string? ShareUserObjectId { get; init; }

    public WorkspaceConfig Workspaces { get; init; } = new();
}

/// <summary>Overrides for the bundled agent fixtures.</summary>
public sealed record WorkspaceConfig
{
    public string? CliAgent { get; init; }

    public string? ClassicAgent { get; init; }

    /// <summary>A standalone workflow workspace, which carries no agent project file.</summary>
    public string? Workflow { get; init; }
}

/// <summary>Azure Bot Service coordinates and the single gate guarding bot creation.</summary>
public sealed record AzureConfig
{
    public string? SubscriptionId { get; init; }

    public string? ResourceGroup { get; init; }

    public string BotNamePrefix { get; init; } = "msagent-e2e";

    public string? ExistingMsaAppId { get; init; }

    public string? ExistingBotName { get; init; }

    public bool AllowCreate { get; init; }
}

/// <summary>The Agent Management Service endpoint, absent until one exists.</summary>
public sealed record AmsConfig
{
    public string? Endpoint { get; init; }
}

public sealed record AmsMcsConfig
{
    public string? Features { get; init; }

    public AmsServiceConfig DataPlane { get; init; } = new();

    public AmsServiceConfig OnBehalfOf { get; init; } = new();

    public AmsServiceConfig ControlPlane { get; init; } = new();

    public string? GitHost { get; init; }

    public string? DevEnvironmentId { get; init; }

    public string? TestEnvironmentId { get; init; }

    public string? ProdEnvironmentId { get; init; }

    public AmsMcsOrganizations Organizations { get; init; } = new();

    public string? WorkspaceRoot { get; init; }

    public IReadOnlyList<string>? ForbiddenEnvironmentIds { get; init; }

    public string FeatureValue => string.IsNullOrWhiteSpace(Features) ? GitCredentialHelper.Feature : Features.Trim();

    public bool AmsFlagOn => Flags.Contains(GitCredentialHelper.Feature, StringComparer.OrdinalIgnoreCase);

    public string? FeaturesWithoutAms => string.Join(';', Flags.Where(flag => !string.Equals(flag, GitCredentialHelper.Feature, StringComparison.OrdinalIgnoreCase))) switch { "" => null, var others => others };

    public IReadOnlyList<AmsMcsSetting> Settings =>
    [
        new("amsMcs.dataPlane.endpoint", AmsEndpointProfile.DataPlaneEndpointVariable, DataPlane.Endpoint),
        new("amsMcs.dataPlane.audience", AmsEndpointProfile.DataPlaneAudienceVariable, DataPlane.Audience),
        new("amsMcs.onBehalfOf.endpoint", AmsEndpointProfile.OnBehalfOfEndpointVariable, OnBehalfOf.Endpoint),
        new("amsMcs.onBehalfOf.audience", AmsEndpointProfile.OnBehalfOfAudienceVariable, OnBehalfOf.Audience),
        new("amsMcs.controlPlane.endpoint", AmsEndpointProfile.ControlPlaneEndpointVariable, ControlPlane.Endpoint),
        new("amsMcs.controlPlane.audience", AmsEndpointProfile.ControlPlaneAudienceVariable, ControlPlane.Audience),
        new("amsMcs.gitHost", AmsEndpointProfile.GitHostVariable, GitHost),
    ];

    public IReadOnlyList<AmsMcsEnvironment> Environments =>
    [
        new("dev", "amsMcs.devEnvironmentId", DevEnvironmentId, "amsMcs.organizations.dev", Organizations.Dev),
        new("test", "amsMcs.testEnvironmentId", TestEnvironmentId, "amsMcs.organizations.test", Organizations.Test),
        new("prod", "amsMcs.prodEnvironmentId", ProdEnvironmentId, "amsMcs.organizations.prod", Organizations.Prod),
    ];

    public IReadOnlyDictionary<string, Uri> OrganizationsByEnvironment() => Environments.Where(environment => environment.Id is { Length: > 0 } && Uri.TryCreate(environment.Organization, UriKind.Absolute, out _)).DistinctBy(environment => environment.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(environment => environment.Id!, environment => new Uri(environment.Organization!), StringComparer.OrdinalIgnoreCase);

    private string[] Flags => FeatureValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record AmsServiceConfig
{
    public string? Endpoint { get; init; }

    public string? Audience { get; init; }
}

public sealed record AmsMcsOrganizations
{
    public string? Dev { get; init; }

    public string? Test { get; init; }

    public string? Prod { get; init; }
}

public sealed record AmsMcsSetting(string Key, string Variable, string? Value);

public sealed record AmsMcsEnvironment(string Role, string IdKey, string? Id, string OrganizationKey, string? Organization);

/// <summary>Serializer settings shared by the config reader and the report writer.</summary>
public static class ConfigJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
