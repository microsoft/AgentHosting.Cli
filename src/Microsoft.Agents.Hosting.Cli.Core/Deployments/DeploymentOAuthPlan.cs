using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;
using System.Text.RegularExpressions;
using CommonOAuthParameters = Microsoft.Agents.Hosting.Cli.Core.Deployments.DeploymentOAuthParameters.CommonOAuthParameters;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

internal sealed record DeploymentOAuthOperationPlan<TRequest>
    where TRequest : DeploymentOAuthUpdateRequest
{
    public required TRequest Request { get; init; }
    public required IReadOnlyDictionary<string, string?> ParsedParameters { get; init; }
}

/// <summary>
/// Validates and normalizes every <c>msagent deployment oauth</c> request, then builds the complete connection
/// PUT after the live Azure Bot Service provider schema (and, for update, the existing connection) is known.
/// The first stage is entirely offline; the second stage owns all provider parameter parsing, defaults,
/// required/type rules, update merging, and clearing semantics.
/// </summary>
public static class DeploymentOAuthPlan
{
    private const int MinConnectionNameLength = 2;
    private const int MaxConnectionNameLength = 64;
    private static readonly Regex ConnectionNamePattern = new(
        @"^[a-zA-Z0-9][\sa-zA-Z0-9_.-]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Providers currently supported by the CLI. Update this list when support for a new provider is requested.
    public static IReadOnlyList<string> ServiceProviderNames { get; } =
    [
        "Aad",
        "Aadb2c",
        "Aadv2",
        "Aadv2WithCerts",
        "Aadv2WithFic",
        "appFigures",
        "AWeber",
        "basecamp",
        "bitly",
        "box",
        "docusign",
        "dropbox",
        "DynamicsCrmOnline",
        "echosign",
        "facebook",
        "FactSet",
        "flickr",
        "github",
        "google",
        "instagram",
        "intuit",
        "linkedin",
        "lithium",
        "live",
        "mailchimp",
        "marketo",
        "oauth2",
        "oauth2generic",
        "Office365",
        "Office365User",
        "onedrive",
        "outlook",
        "pinterest",
        "salesforce",
        "SharePointOnline",
        "SharepointServer",
        "SkypeForBusiness",
        "slack",
        "smartsheet",
        "spotify",
        "stripe",
        "sugarcrm",
        "todoist",
        "trello",
        "tumblr",
        "uservoice",
        "wunderlist",
        "yammer",
        "zendesk",
    ];

    private sealed record MergedOAuthConnectionParameters(
        IReadOnlyDictionary<string, string?> CommonParameters,
        KeyValuePair<string, string?>[] ProviderParameters);

    public static DeploymentOAuthRequest Validate(DeploymentOAuthRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parsedRequest = (DeploymentOAuthRequest)DeploymentRequestValidator.Validate(request);
        return parsedRequest with
        {
            ConnectionName = ValidateConnectionName(request.ConnectionName),
        };
    }

    internal static DeploymentOAuthOperationPlan<DeploymentOAuthCreateRequest> ValidateCreate(
        DeploymentOAuthCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parsedRequest = (DeploymentOAuthCreateRequest)DeploymentRequestValidator.Validate(request);
        var normalizedRequest = parsedRequest with
        {
            ConnectionName = ValidateConnectionName(request.ConnectionName),
            ServiceProviderName = DeploymentRequestValidator.RequiredEnum(
                "--provider-name",
                request.ServiceProviderName,
                ServiceProviderNames,
                "Run `msagent deployment oauth list-providers` to inspect the live Azure Bot Service catalogue."),
            ClientId = DeploymentRequestValidator.Optional("--client-id", request.ClientId),
            ClientSecret = DeploymentRequestValidator.Optional("--client-secret", request.ClientSecret),
            Scopes = DeploymentRequestValidator.Optional("--scopes", request.Scopes),
        };
        var parsedParameters = DeploymentParameterValidator.ParseParameterAssignments(
            "--parameters",
            request.Parameters,
            DeploymentParameterEmptyValuePolicy.Reject);
        RejectCommonOAuthParametersInGenericOptions(parsedParameters);

        return new DeploymentOAuthOperationPlan<DeploymentOAuthCreateRequest>
        {
            Request = normalizedRequest,
            ParsedParameters = parsedParameters,
        };
    }

    internal static DeploymentOAuthOperationPlan<DeploymentOAuthUpdateRequest> ValidateUpdate(
        DeploymentOAuthUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parsedRequest = (DeploymentOAuthUpdateRequest)DeploymentRequestValidator.Validate(request);

        if (request.ClientId is null &&
            request.ClientSecret is null &&
            request.Scopes is null &&
            request.Parameters.Count == 0)
        {
            throw new UsageException(
                "At least one of these fields must be specified: --client-id, --client-secret, --scopes, --parameters.");
        }

        var normalizedRequest = parsedRequest with
        {
            ConnectionName = ValidateConnectionName(request.ConnectionName),
            ClientId = DeploymentRequestValidator.Trim(request.ClientId, preserveEmpty: true),
            ClientSecret = DeploymentRequestValidator.Trim(request.ClientSecret, preserveEmpty: true),
            Scopes = DeploymentRequestValidator.Trim(request.Scopes, preserveEmpty: true),
        };
        var parsedParameters = DeploymentParameterValidator.ParseParameterAssignments(
            "--parameters",
            request.Parameters,
            DeploymentParameterEmptyValuePolicy.Allow);
        RejectCommonOAuthParametersInGenericOptions(parsedParameters);

        return new DeploymentOAuthOperationPlan<DeploymentOAuthUpdateRequest>
        {
            Request = normalizedRequest,
            ParsedParameters = parsedParameters,
        };
    }

    public static DeploymentOAuthListRequest ValidateList(DeploymentOAuthListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (DeploymentOAuthListRequest)DeploymentRequestValidator.Validate(request);
    }

    public static DeploymentOAuthProviderListRequest ValidateProviderList(
        DeploymentOAuthProviderListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request with
        {
            ServiceProviderName = DeploymentRequestValidator.Optional("--provider-name", request.ServiceProviderName),
            SubscriptionId = DeploymentRequestValidator.OptionalGuid("--subscription", request.SubscriptionId),
            TenantId = DeploymentRequestValidator.OptionalGuid("--tenant", request.TenantId),
        };
    }

    internal static string ValidateConnectionName(string? connectionName)
    {
        var normalized = DeploymentRequestValidator.Required("--connection", connectionName);
        if (normalized.Length is < MinConnectionNameLength or > MaxConnectionNameLength ||
            !ConnectionNamePattern.IsMatch(normalized))
        {
            throw new UsageException(
                $"--connection must be {MinConnectionNameLength}-{MaxConnectionNameLength} characters, " +
                "start with a letter or digit, and contain only whitespace, letters, digits, '.', '_', or '-'.");
        }

        return normalized;
    }

    /// <summary>
    /// Builds an OAuth create after the live provider catalogue has supplied the selected provider definition.
    /// Provider-specific names, required values, types, and defaults are validated here before the connection GET
    /// or write because they cannot be validated from the CLI request alone.
    /// </summary>
    internal static BotServiceOAuthUpsertRequest BuildCreate(
        OAuthProviderDefinition provider,
        DeploymentOAuthOperationPlan<DeploymentOAuthCreateRequest> plan,
        string botResourceId,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(plan);

        var normalizedRequest = plan.Request;
        var parsedParameters = plan.ParsedParameters;
        DeploymentParameterValidator.RejectUnknownParameters(
            provider.Parameters,
            parsedParameters,
            parameter => parameter.Name,
            parameterName => DeploymentException.ParameterNotDeclared(
                parameterName,
                provider.Name,
                useOAuthOption: true,
                guidance: $"Run `msagent deployment oauth list-providers --provider-name {provider.Name}` to see its parameters."));
        RejectUnsupportedCommonOAuthParameters(
            provider,
            normalizedRequest,
            static value => !string.IsNullOrWhiteSpace(value));

        var suppliedParameters = CombineWithCommonOAuthParameters(normalizedRequest, parsedParameters);
        DeploymentParameterValidator.RejectOmittedOrBlankRequiredParameters(
            provider.Parameters,
            suppliedParameters,
            parameter => parameter.Name,
            parameter => parameter.IsRequired,
            parameterName => DeploymentException.RequiredParameter(
                parameterName,
                provider.Name,
                useOAuthOption: true));
        var normalizedParameters = DeploymentParameterValidator.NormalizeParameters(
            provider.Parameters,
            suppliedParameters,
            parameter => parameter.Name,
            ToDeploymentParameterType,
            (parameterName, _) => DeploymentException.InvalidParameterValue(
                parameterName,
                provider.Name,
                useOAuthOption: true));
        normalizedParameters = DeploymentParameterValidator.ApplyOptionalParameterDefaults(
            provider.Parameters,
            normalizedParameters,
            parameter => parameter.Name,
            parameter => parameter.IsRequired,
            ToDeploymentParameterType,
            parameter => parameter.DefaultValue,
            (parameterName, _) => DeploymentException.InvalidParameterValue(
                parameterName,
                provider.Name,
                useOAuthOption: true));

        return new BotServiceOAuthUpsertRequest
        {
            ConnectionName = normalizedRequest.ConnectionName!,
            ServiceProviderId = provider.Id,
            ClientId = ParameterValue(normalizedParameters, CommonOAuthParameters.ClientId),
            ClientSecret = ParameterValue(normalizedParameters, CommonOAuthParameters.ClientSecret),
            Scopes = ParameterValue(normalizedParameters, CommonOAuthParameters.Scopes) ??
                     DeploymentRequestValidator.Trim(normalizedRequest.Scopes),
            Parameters = GenericParameters(provider, normalizedParameters),
            BotResourceId = botResourceId,
            TenantId = tenantId,
        };
    }

    /// <summary>
    /// Builds an OAuth update against the live connection's provider catalog. Supplied values are validated and
    /// normalized before they are merged with the safe existing connection: omission preserves a readable
    /// existing value, an explicit blank clears only an optional value, and an omitted secret is never recovered
    /// from or replayed using a masked GET response.
    /// </summary>
    internal static BotServiceOAuthUpsertRequest BuildUpdate(
        OAuthProviderDefinition provider,
        DeploymentOAuthOperationPlan<DeploymentOAuthUpdateRequest> plan,
        DeploymentOAuthConnection existingConnection,
        string botResourceId,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(existingConnection);

        var normalizedRequest = plan.Request;
        var parsedParameters = plan.ParsedParameters;
        DeploymentParameterValidator.RejectUnknownParameters(
            provider.Parameters,
            parsedParameters,
            parameter => parameter.Name,
            parameterName => DeploymentException.ParameterNotDeclared(
                parameterName,
                provider.Name,
                useOAuthOption: true,
                guidance: $"Run `msagent deployment oauth list-providers --provider-name {provider.Name}` to see its parameters."));
        RejectUnsupportedCommonOAuthParameters(
            provider,
            normalizedRequest,
            static value => value is not null);

        var suppliedParameters = CombineWithCommonOAuthParameters(normalizedRequest, parsedParameters);
        DeploymentParameterValidator.RejectClearingRequiredParameters(
            provider.Parameters,
            suppliedParameters,
            parameter => parameter.Name,
            parameter => parameter.IsRequired,
            parameterName => DeploymentException.CannotClearRequiredParameter(
                parameterName,
                provider.Name,
                useOAuthOption: true,
                guidance: "Omit it to keep the current value or provide a new non-empty value."));
        var normalizedParameters = DeploymentParameterValidator.NormalizeParameters(
            provider.Parameters,
            suppliedParameters,
            parameter => parameter.Name,
            ToDeploymentParameterType,
            (parameterName, _) => DeploymentException.InvalidParameterValue(
                parameterName,
                provider.Name,
                useOAuthOption: true));

        var mergedParameters = MergeWithExistingOAuthConnection(
            normalizedRequest,
            existingConnection,
            normalizedParameters);

        return new BotServiceOAuthUpsertRequest
        {
            ConnectionName = normalizedRequest.ConnectionName!,
            ServiceProviderId = provider.Id,
            ClientId = ParameterValue(mergedParameters.CommonParameters, CommonOAuthParameters.ClientId),
            ClientSecret = ParameterValue(mergedParameters.CommonParameters, CommonOAuthParameters.ClientSecret),
            Scopes = ParameterValue(mergedParameters.CommonParameters, CommonOAuthParameters.Scopes),
            Parameters = mergedParameters.ProviderParameters,
            BotResourceId = botResourceId,
            TenantId = tenantId,
        };
    }

    /// <summary>
    /// Rejects common OAuth parameters supplied through <c>--parameters</c> so callers use their dedicated
    /// options, such as <c>--client-id</c>, and receive one unambiguous source for each value.
    /// </summary>
    private static void RejectCommonOAuthParametersInGenericOptions(
        IReadOnlyDictionary<string, string?> parameters)
    {
        foreach (var parameter in CommonOAuthParameters.All)
        {
            if (parameters.ContainsKey(parameter.Name))
            {
                throw new UsageException(
                    $"Use {parameter.Option} instead of --parameters {parameter.Name}=... .");
            }
        }
    }

    /// <summary>
    /// Rejects a supplied common OAuth option when the selected provider does not declare that parameter.
    /// Parameters that do not require provider declaration are accepted for every provider.
    /// </summary>
    private static void RejectUnsupportedCommonOAuthParameters(
        OAuthProviderDefinition provider,
        DeploymentOAuthUpdateRequest request,
        Func<string?, bool> isSupplied)
    {
        var declaredParameters = provider.Parameters
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in CommonOAuthParameters.All.Where(parameter =>
                     parameter.IsProviderDeclarationRequired))
        {
            if (isSupplied(parameter.RequestValue(request)) &&
                !declaredParameters.Contains(parameter.Name))
            {
                throw DeploymentException.ParameterNotDeclared(
                    parameter.Name,
                    provider.Name,
                    useOAuthOption: true,
                    guidance: $"Run `msagent deployment oauth list-providers --provider-name {provider.Name}` to see its parameters.");
            }
        }
    }

    /// <summary>
    /// Combines provider-specific <c>--parameters</c> assignments with supplied common OAuth options so the
    /// provider catalogue can validate and normalize all declared parameters in one pass.
    /// </summary>
    private static Dictionary<string, string?> CombineWithCommonOAuthParameters(
        DeploymentOAuthUpdateRequest request,
        IReadOnlyDictionary<string, string?> parsedParameters)
    {
        var suppliedParameters = parsedParameters.ToDictionary(
            parameter => parameter.Key,
            parameter => parameter.Value,
            StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in CommonOAuthParameters.All)
        {
            var value = parameter.RequestValue(request);
            if (value is not null)
            {
                suppliedParameters[parameter.Name] = value;
            }
        }

        return suppliedParameters;
    }

    /// <summary>
    /// Overlays normalized update values on the safe readable values from the existing connection. Omitted
    /// common parameters retain their existing value, while omitted provider parameters remain unchanged.
    /// </summary>
    private static MergedOAuthConnectionParameters MergeWithExistingOAuthConnection(
        DeploymentOAuthUpdateRequest request,
        DeploymentOAuthConnection existingChannel,
        Dictionary<string, string?> normalizedParameters)
    {
        var mergedParameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in CommonOAuthParameters.All)
        {
            var suppliedValue = parameter.RequestValue(request);
            mergedParameters[parameter.Name] = suppliedValue is null
                ? parameter.ExistingValue(existingChannel)
                : normalizedParameters.TryGetValue(parameter.Name, out var normalizedValue)
                    ? normalizedValue
                    : suppliedValue;
        }

        var providerSpecific = existingChannel.Parameters
            .Where(entry => !CommonOAuthParameters.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in normalizedParameters.Where(parameter =>
                     !CommonOAuthParameters.Contains(parameter.Key)))
        {
            providerSpecific[parameter.Key] = parameter.Value;
        }

        return new(
            mergedParameters,
            providerSpecific
                .Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value))
                .ToArray());
    }

    /// <summary>
    /// Projects normalized provider-specific values into the generic parameter array expected by the OAuth
    /// connection PUT, excluding common parameters that have dedicated request properties.
    /// </summary>
    private static KeyValuePair<string, string?>[] GenericParameters(
        OAuthProviderDefinition provider,
        Dictionary<string, string?> parameters) =>
        provider.Parameters
            .Where(parameter => !CommonOAuthParameters.Contains(parameter.Name))
            .Where(parameter => parameters.ContainsKey(parameter.Name))
            .Select(parameter => new KeyValuePair<string, string?>(
                parameter.Name,
                parameters[parameter.Name]))
            .ToArray();

    /// <summary>
    /// Reads a normalized parameter value by its case-insensitive catalogue name, returning <see langword="null"/>
    /// when the parameter was not supplied or retained.
    /// </summary>
    private static string? ParameterValue(
        IReadOnlyDictionary<string, string?> parameters,
        string parameterName) =>
        parameters.TryGetValue(parameterName, out var value) ? value : null;

    /// <summary>
    /// Converts the provider catalogue's string type into the shared parameter type used by normalization.
    /// Azure reports Boolean parameters as <c>bool</c> or <c>boolean</c>; every other provider type, including
    /// <c>string</c> and <c>securestring</c>, is normalized as a string.
    /// </summary>
    private static DeploymentParameterType ToDeploymentParameterType(
        OAuthProviderParameterDefinition parameter) =>
        string.Equals(parameter.Type, "bool", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(parameter.Type, "boolean", StringComparison.OrdinalIgnoreCase)
            ? DeploymentParameterType.Boolean
            : DeploymentParameterType.String;

}
