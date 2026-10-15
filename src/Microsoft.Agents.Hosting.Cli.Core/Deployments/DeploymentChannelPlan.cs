using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

internal sealed record DeploymentChannelOperationPlan<TRequest>
    where TRequest : DeploymentChannelRequest
{
    public required TRequest Request { get; init; }
    public required ChannelDefinition ChannelDefinition { get; init; }
    public IReadOnlyDictionary<string, JsonElement> NormalizedParameters { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Validates and normalizes every <c>msagent deployment channel</c> request and builds the
/// catalog-driven upsert before any Azure Bot Service write.
/// </summary>
public static class DeploymentChannelPlan
{
    private static readonly HashSet<string> AutomaticallyCreatedChannels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "directline",
            "webchat",
        };

    internal static DeploymentChannelRequest Validate(DeploymentChannelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validatedRequest = (DeploymentChannelRequest)DeploymentRequestValidator.Validate(request);

        return validatedRequest with
        {
            ChannelName = DeploymentRequestValidator.RequiredEnum(
                "--channel",
                request.ChannelName,
                DeploymentChannelCatalog.SupportedChannelNames),
        };
    }

    internal static DeploymentChannelOperationPlan<DeploymentChannelCreateRequest> ValidateCreate(
        DeploymentChannelCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validatedRequest = (DeploymentChannelCreateRequest)Validate(request);

        var (channelDefinition, normalizedParameters) = NormalizeParameters(validatedRequest, isCreateRequest: true);

        return new()
        {
            Request = validatedRequest,
            ChannelDefinition = channelDefinition,
            NormalizedParameters = normalizedParameters,
        };
    }

    internal static DeploymentChannelOperationPlan<DeploymentChannelUpdateRequest> ValidateUpdate(
        DeploymentChannelUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validatedRequest = (DeploymentChannelUpdateRequest)Validate(request);
        var channelDefinition = DeploymentChannelCatalog.FindChannelDefinition(
            validatedRequest.ChannelName,
            ChannelKeyType.Name);
        if (channelDefinition.Parameters.Count == 0)
        {
            throw DeploymentException.ChannelOperationNotSupported(
                channelDefinition.Name,
                "update");
        }

        var (_, normalizedParameters) = NormalizeParameters(
            validatedRequest,
            channelDefinition,
            isCreateRequest: false);

        return new()
        {
            Request = validatedRequest,
            ChannelDefinition = channelDefinition,
            NormalizedParameters = normalizedParameters,
        };
    }

    internal static DeploymentChannelDeleteRequest ValidateDelete(DeploymentChannelDeleteRequest request)
    {
        var validatedRequest = (DeploymentChannelDeleteRequest)Validate(request);
        if (AutomaticallyCreatedChannels.Contains(validatedRequest.ChannelName))
        {
            throw DeploymentException.ChannelOperationNotSupported(
                validatedRequest.ChannelName,
                "delete");
        }

        return validatedRequest;
    }

    internal static DeploymentChannelListRequest ValidateList(DeploymentChannelListRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (DeploymentChannelListRequest)DeploymentRequestValidator.Validate(request);
    }

    /// <summary>
    /// Builds the complete channel create PUT from an already validated plan after the deployment has supplied
    /// the backing bot resource id and tenant id. No request validation or parameter normalization occurs here.
    /// </summary>
    internal static BotServiceChannelUpsertRequest BuildCreate(
        DeploymentChannelOperationPlan<DeploymentChannelCreateRequest> plan,
        string botResourceId,
        string? tenantId) =>
        new()
        {
            ChannelArmName = plan.ChannelDefinition.ArmName,
            BotResourceId = botResourceId,
            TenantId = tenantId,
            Parameters = BuildArmChannelProperties(plan.ChannelDefinition, plan.NormalizedParameters),
        };

    /// <summary>
    /// Builds the complete channel update PUT after the live channel has been read. Safe reusable existing
    /// parameters are preserved, normalized user overrides are applied, and masked secrets are never replayed.
    /// </summary>
    internal static BotServiceChannelUpsertRequest BuildUpdate(
        DeploymentChannelOperationPlan<DeploymentChannelUpdateRequest> plan,
        DeploymentChannelRecord existingChannel,
        string botResourceId,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(existingChannel);

        var parameters = MergeWithExistingChannel(
            plan.ChannelDefinition,
            existingChannel,
            plan.NormalizedParameters);

        return new()
        {
            ChannelArmName = plan.ChannelDefinition.ArmName,
            BotResourceId = botResourceId,
            TenantId = tenantId,
            Parameters = parameters,
        };
    }

    /// <summary>
    /// Resolves the channel definition and runs the shared catalogue-driven parameter validation and
    /// normalization pipeline for create or update. For example, Slack create validates its required
    /// credentials and adds the catalog default <c>isEnabled=true</c>.
    /// </summary>
    private static (
        ChannelDefinition ChannelDefinition,
        IReadOnlyDictionary<string, JsonElement> NormalizedParameters) NormalizeParameters(
        DeploymentChannelCreateRequest request,
        ChannelDefinition? channelDefinition = null,
        bool isCreateRequest = true)
    {
        channelDefinition ??= DeploymentChannelCatalog.FindChannelDefinition(
            request.ChannelName,
            ChannelKeyType.Name);

        if (!isCreateRequest && request.Parameters.Count == 0)
        {
            throw new UsageException(
                $"At least one --parameters Key=Value assignment is required to update the {channelDefinition.Name} channel.");
        }

        var parsedParameters = DeploymentParameterValidator.ParseParameterAssignments(
            "--parameters",
            request.Parameters,
            isCreateRequest
                ? DeploymentParameterEmptyValuePolicy.Reject
                : DeploymentParameterEmptyValuePolicy.Allow);
        DeploymentParameterValidator.RejectUnknownParameters(
            channelDefinition.Parameters,
            parsedParameters,
            definition => definition.Name,
            parameterName => DeploymentException.ParameterNotDeclared(
                parameterName,
                channelDefinition.Name));
        foreach (var parameter in channelDefinition.Parameters.Where(parameter => parameter.IsReadOnly))
        {
            if (parsedParameters.ContainsKey(parameter.Name))
            {
                throw DeploymentException.ReadOnlyParameter(parameter.Name, channelDefinition.Name);
            }
        }

        if (isCreateRequest)
        {
            DeploymentParameterValidator.RejectOmittedOrBlankRequiredParameters(
                channelDefinition.Parameters,
                parsedParameters,
                definition => definition.Name,
                definition => definition.IsRequired,
                parameterName => DeploymentException.RequiredParameter(
                    parameterName,
                    channelDefinition.Name));
        }
        else
        {
            DeploymentParameterValidator.RejectClearingRequiredParameters(
                channelDefinition.Parameters,
                parsedParameters,
                definition => definition.Name,
                definition => definition.IsRequired,
                parameterName => DeploymentException.CannotClearRequiredParameter(
                    parameterName,
                    channelDefinition.Name));
        }

        var normalizedParameters = DeploymentParameterValidator.NormalizeParameters(
            channelDefinition.Parameters,
            parsedParameters,
            definition => definition.Name,
            definition => definition.Type,
            (parameterName, _) => DeploymentException.InvalidParameterValue(
                parameterName,
                channelDefinition.Name));

        if (isCreateRequest)
        {
            normalizedParameters = DeploymentParameterValidator.ApplyOptionalParameterDefaults(
                channelDefinition.Parameters,
                normalizedParameters,
                definition => definition.Name,
                definition => definition.IsRequired,
                definition => definition.Type,
                definition => definition.DefaultValue,
                (parameterName, _) => DeploymentException.InvalidParameterValue(
                    parameterName,
                    channelDefinition.Name));
        }

        return (channelDefinition, ToTypedParameters(channelDefinition, normalizedParameters));
    }

    /// <summary>
    /// Converts normalized string values to their catalog-declared JSON types. For example,
    /// <c>isEnabled="true"</c> becomes a JSON boolean while <c>clientId="app-1"</c> remains a JSON string.
    /// </summary>
    private static Dictionary<string, JsonElement> ToTypedParameters(
        ChannelDefinition channelDefinition,
        Dictionary<string, string?> normalizedParameters)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in channelDefinition.Parameters)
        {
            if (normalizedParameters.TryGetValue(definition.Name, out var value))
            {
                result[definition.Name] = ToTypedParameter(definition, value);
            }
        }

        return result;
    }

    /// <summary>
    /// Converts one normalized value to JSON. For example, an empty optional value becomes <c>null</c>,
    /// <c>trustedOrigins="https://a,https://b"</c> becomes an array, and <c>"false"</c> becomes a boolean.
    /// </summary>
    private static JsonElement ToTypedParameter(
        ChannelParameterDefinition definition,
        string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return JsonSerializer.SerializeToElement<string?>(null);
        }

        if (definition.Name.Equals(
                DeploymentChannelCatalog.TrustedOriginsParameterName,
                StringComparison.OrdinalIgnoreCase))
        {
            return JsonSerializer.SerializeToElement(
                value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        return definition.Type switch
        {
            DeploymentParameterType.Boolean => JsonSerializer.SerializeToElement(bool.Parse(value)),
            _ => JsonSerializer.SerializeToElement(value),
        };
    }

    /// <summary>
    /// Merges normalized update values over the safe reusable parameters from the live channel record.
    /// Masked secrets and values that do not match their catalogue type are omitted before overrides are applied.
    /// For example, <c>enableCalling=true</c> is added while an existing <c>callingWebhook</c> is preserved.
    /// </summary>
    private static Dictionary<string, JsonElement> MergeWithExistingChannel(
        ChannelDefinition channelDefinition,
        DeploymentChannelRecord existingChannel,
        IReadOnlyDictionary<string, JsonElement> normalizedParameters
       )
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var existingParameters = ExistingParameterValues(channelDefinition, existingChannel);
        foreach (var definition in channelDefinition.Parameters.Where(definition => !definition.IsReadOnly))
        {
            if (existingParameters.TryGetValue(definition.Name, out var value) &&
                ReadSafeExistingValue(definition, value) is { } normalized)
            {
                result[definition.Name] = normalized;
            }
        }

        foreach (var parameter in normalizedParameters)
        {
            result[parameter.Key] = parameter.Value;
        }

        return BuildArmChannelProperties(channelDefinition, result);
    }

    /// <summary>
    /// Builds the final ARM <c>properties.properties</c> value. For example, Slack parameters remain flat as
    /// <c>{ "isEnabled": true }</c>, while Direct Line parameters become
    /// <c>{ "sites": [{ "siteName": "Default Site", "isEnabled": true }] }</c>.
    /// </summary>
    private static Dictionary<string, JsonElement> BuildArmChannelProperties(
        ChannelDefinition channelDefinition,
        IReadOnlyDictionary<string, JsonElement> parameters)
    {
        if (!channelDefinition.IsNestedParameters)
        {
            return new Dictionary<string, JsonElement>(parameters, StringComparer.OrdinalIgnoreCase);
        }

        // Azure CLI only mutates one Direct Line/Web Chat site. Match that limitation by replacing the complete
        // ARM sites array with one site; future multi-site support will add explicit site selection and merging.
        return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["sites"] = JsonSerializer.SerializeToElement(new[] { parameters }),
        };
    }

    /// <summary>
    /// Converts a readable existing JSON value to the normalized string representation used by channel PUTs.
    /// Masked strings, nulls, and values whose JSON kind does not match the catalogue definition are not reused.
    /// For example, <c>"***"</c> is discarded, <c>"client-1"</c> is reusable for a string parameter, and
    /// <c>true</c> is reusable for a boolean parameter.
    /// </summary>
    private static JsonElement? ReadSafeExistingValue(
        ChannelParameterDefinition definition,
        JsonElement value) =>
        definition.Type switch
    {
        DeploymentParameterType.String when value.ValueKind == JsonValueKind.String &&
                                            value.GetString() != BotServiceResponseRedactor.Mask => value.Clone(),
        DeploymentParameterType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
            value.Clone(),
        _ => null,
    };

    /// <summary>
    /// Selects the live parameter object used as the update merge base. For example, Slack uses its flat
    /// parameter dictionary, while Direct Line uses the first object from <c>sites</c>; additional sites are
    /// preserved on reads but are not independently selectable until future multi-site support is added.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> ExistingParameterValues(
        ChannelDefinition channelDefinition,
        DeploymentChannelRecord existingChannel)
    {
        if (!channelDefinition.IsNestedParameters)
        {
            return existingChannel.Parameters;
        }

        if (!existingChannel.Parameters.TryGetValue("sites", out var sites) ||
            sites.ValueKind != JsonValueKind.Array ||
            sites.GetArrayLength() == 0 ||
            sites[0].ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        }

        return sites[0].EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.Clone(),
            StringComparer.OrdinalIgnoreCase);
    }
}
