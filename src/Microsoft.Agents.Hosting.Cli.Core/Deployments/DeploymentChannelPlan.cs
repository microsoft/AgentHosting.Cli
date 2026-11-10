using System.Globalization;
using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

internal enum ChannelCollectionOperation { Add, Set, Remove, Replace }

internal sealed record ChannelItemMutation(ChannelCollectionOperation Operation, JsonElement Item);

internal sealed record DeploymentChannelOperationPlan<TRequest>
    where TRequest : DeploymentChannelRequest
{
    public required TRequest Request { get; init; }
    public required ChannelDefinition ChannelDefinition { get; init; }
    public ChannelSiteMutation? SiteMutation { get; init; }
    public ChannelItemMutation? PageMutation { get; init; }
    public ChannelItemMutation? RegistrationMutation { get; init; }
    public IReadOnlyDictionary<string, JsonElement> NormalizedParameters { get; init; } =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Validates channel requests and builds catalogue-driven write payloads.
/// </summary>
public static class DeploymentChannelPlan
{
    internal static DeploymentChannelRequest Validate(DeploymentChannelRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validatedRequest = (DeploymentChannelRequest)DeploymentRequestValidator.Validate(request);

        return validatedRequest with
        {
            ChannelName = DeploymentChannelCatalog.ValidateChannelName(request.ChannelName),
        };
    }

    internal static DeploymentChannelShowRequest ValidateShow(DeploymentChannelShowRequest request)
    {
        var validatedRequest = (DeploymentChannelShowRequest)Validate(request);
        var channelDefinition = DeploymentChannelCatalog.FindChannelDefinition(validatedRequest.ChannelName, ChannelKeyType.Name);
        if (!DeploymentChannelCatalog.SupportsOperation(channelDefinition, "show"))
        {
            throw DeploymentException.ChannelOperationNotSupported(channelDefinition.Name, "show");
        }

        return validatedRequest;
    }

    /// <summary>Validates create inputs before deployment resolution or Azure writes.</summary>
    internal static DeploymentChannelOperationPlan<DeploymentChannelCreateRequest> ValidateCreate(
        DeploymentChannelCreateRequest request)
    {
        var validatedRequest = (DeploymentChannelCreateRequest)Validate(request);

        var channelDefinition = DeploymentChannelCatalog.FindChannelDefinition(validatedRequest.ChannelName, ChannelKeyType.Name);
        ValidateCollectionOperations(validatedRequest, channelDefinition, isCreate: true);
        if (channelDefinition.Parameters.SingleOrDefault(parameter => parameter.Name == "sites")?.ItemParameters is { } fields)
        {
            return new()
            {
                Request = validatedRequest,
                ChannelDefinition = channelDefinition,
                SiteMutation = DeploymentChannelSites.ValidateCreate(validatedRequest, channelDefinition, fields),
            };
        }

        var normalizedParameters = NormalizeParameters(validatedRequest, channelDefinition, isCreateRequest: true);

        return new()
        {
            Request = validatedRequest,
            ChannelDefinition = channelDefinition,
            NormalizedParameters = normalizedParameters,
        };
    }

    /// <summary>Validates assignments or one collection operation before reading existing state.</summary>
    internal static DeploymentChannelOperationPlan<DeploymentChannelUpdateRequest> ValidateUpdate(
        DeploymentChannelUpdateRequest request)
    {
        var validatedRequest = (DeploymentChannelUpdateRequest)Validate(request);
        var channelDefinition = DeploymentChannelCatalog.FindChannelDefinition(
            validatedRequest.ChannelName,
            ChannelKeyType.Name);
        ValidateCollectionOperations(validatedRequest, channelDefinition, isCreate: false);
        if (!DeploymentChannelCatalog.SupportsOperation(channelDefinition, "update"))
        {
            throw DeploymentException.ChannelOperationNotSupported(
                channelDefinition.Name,
                "update");
        }

        if (channelDefinition.Parameters.SingleOrDefault(parameter => parameter.Name == "sites")?.ItemParameters is { } fields)
        {
            return new()
            {
                Request = validatedRequest,
                ChannelDefinition = channelDefinition,
                SiteMutation = DeploymentChannelSites.ValidateUpdate(validatedRequest, channelDefinition, fields),
            };
        }

        var pageMutation = channelDefinition.Name == "facebook"
            ? DeploymentChannelPages.ValidateOperation(validatedRequest, channelDefinition)
            : null;
        var registrationMutation = channelDefinition.Name == "line"
            ? DeploymentChannelRegistrations.ValidateOperation(validatedRequest, channelDefinition)
            : null;
        var normalizedParameters = pageMutation is null && registrationMutation is null
            ? NormalizeParameters(validatedRequest, channelDefinition, isCreateRequest: false)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        return new()
        {
            Request = validatedRequest,
            ChannelDefinition = channelDefinition,
            PageMutation = pageMutation,
            RegistrationMutation = registrationMutation,
            NormalizedParameters = normalizedParameters,
        };
    }

    internal static DeploymentChannelDeleteRequest ValidateDelete(DeploymentChannelDeleteRequest request)
    {
        var validatedRequest = (DeploymentChannelDeleteRequest)Validate(request);
        var channelDefinition = DeploymentChannelCatalog.FindChannelDefinition(validatedRequest.ChannelName, ChannelKeyType.Name);
        if (!DeploymentChannelCatalog.SupportsOperation(channelDefinition, "delete"))
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

    /// <summary>Builds a create PUT payload from a validated plan.</summary>
    internal static BotServiceChannelWriteRequest BuildCreate(
        DeploymentChannelOperationPlan<DeploymentChannelCreateRequest> plan,
        string botResourceId,
        string? tenantId) =>
        new()
        {
            ChannelArmName = plan.ChannelDefinition.ArmName,
            BotResourceId = botResourceId,
            TenantId = tenantId,
            Parameters = plan.SiteMutation is { } mutation
                ? DeploymentChannelSites.BuildCreate(mutation)
                : plan.ChannelDefinition.Name == "slack"
                    ? DeploymentChannelSlack.WithRegistration(plan.NormalizedParameters)
                    : plan.NormalizedParameters,
        };

    /// <summary>
    /// Builds a PATCH payload from live settings and validated changes without replaying secret masks.
    /// </summary>
    internal static BotServiceChannelWriteRequest BuildUpdate(
        DeploymentChannelOperationPlan<DeploymentChannelUpdateRequest> plan,
        DeploymentChannelRecord existingChannel,
        string botResourceId,
        string? tenantId)
    {
        ArgumentNullException.ThrowIfNull(existingChannel);

        if (plan.ChannelDefinition.Name == "msteams" &&
            plan.NormalizedParameters.TryGetValue("acceptedTerms", out var acceptedTerms) &&
            acceptedTerms.ValueKind == JsonValueKind.False &&
            existingChannel.Parameters.TryGetValue("acceptedTerms", out var existingTerms) &&
            existingTerms.ValueKind == JsonValueKind.True)
        {
            throw new UsageException("Microsoft Teams terms acceptance cannot be revoked.");
        }

        var parameters = plan.SiteMutation is { } mutation
            ? DeploymentChannelSites.BuildUpdate(mutation, plan.ChannelDefinition, existingChannel)
            : MergeWithExistingChannel(
                plan.ChannelDefinition,
                existingChannel,
                plan.NormalizedParameters);
        if (plan.ChannelDefinition.Name == "facebook")
        {
            parameters = DeploymentChannelPages.BuildUpdate(
                plan.PageMutation, existingChannel, plan.NormalizedParameters, parameters);
        }
        else if (plan.ChannelDefinition.Name == "line")
        {
            parameters = DeploymentChannelRegistrations.BuildUpdate(
                plan.RegistrationMutation, existingChannel, plan.NormalizedParameters, parameters);
        }
        else if (plan.ChannelDefinition.Name == "slack")
        {
            parameters = DeploymentChannelSlack.WithRegistration(parameters);
        }

        return new()
        {
            ChannelArmName = plan.ChannelDefinition.ArmName,
            BotResourceId = botResourceId,
            TenantId = tenantId,
            Parameters = parameters,
        };
    }

    /// <summary>Validates assignments, applying catalogue defaults only on create.</summary>
    private static Dictionary<string, JsonElement> NormalizeParameters(
        DeploymentChannelCreateRequest request,
        ChannelDefinition channelDefinition,
        bool isCreateRequest)
    {
        var parsedParameters = DeploymentParameterValidator.ParseParameterAssignments(
            "--parameters",
            request.Parameters,
            isCreateRequest
                ? DeploymentParameterEmptyValuePolicy.Reject
                : DeploymentParameterEmptyValuePolicy.Allow);
        var writableDefinitions = channelDefinition.Parameters.Where(parameter => !parameter.IsReadOnly).ToArray();
        DeploymentParameterValidator.RejectUnknownParameters(
            writableDefinitions,
            parsedParameters,
            definition => definition.Name,
            parameterName => DeploymentException.InvalidChannelParameter(
                parameterName,
                channelDefinition.Name,
                isCreateRequest ? "create" : "update"));

        if (!isCreateRequest && request.Parameters.Count == 0)
        {
            throw new UsageException(
                $"At least one --parameters Key=Value assignment is required to update the {channelDefinition.Name} channel.");
        }

        if (isCreateRequest)
        {
            DeploymentParameterValidator.RejectOmittedOrBlankRequiredParameters(
                writableDefinitions,
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
                writableDefinitions,
                parsedParameters,
                definition => definition.Name,
                definition => definition.IsRequired,
                parameterName => DeploymentException.CannotClearRequiredParameter(
                    parameterName,
                    channelDefinition.Name));
        }

        foreach (var definition in writableDefinitions.Where(definition => !definition.CanClear))
        {
            if (parsedParameters.TryGetValue(definition.Name, out var suppliedValue) &&
                string.IsNullOrWhiteSpace(suppliedValue))
            {
                throw DeploymentException.CannotClearChannelParameter(definition.Name, channelDefinition.Name);
            }
        }

        var normalizedParameters = DeploymentParameterValidator.NormalizeParameters(
            writableDefinitions,
            parsedParameters,
            definition => definition.Name,
            definition => definition.Type,
            (parameterName, _) => DeploymentException.InvalidParameterValue(
                parameterName,
                channelDefinition.Name));

        if (isCreateRequest)
        {
            normalizedParameters = DeploymentParameterValidator.ApplyOptionalParameterDefaults(
                writableDefinitions,
                normalizedParameters,
                definition => definition.Name,
                definition => definition.IsRequired,
                definition => definition.Type,
                definition => definition.DefaultValue,
                (parameterName, _) => DeploymentException.InvalidParameterValue(
                    parameterName,
                    channelDefinition.Name));
        }

        foreach (var definition in writableDefinitions)
        {
            if (definition.AllowedValues is { } allowedValues &&
                normalizedParameters.TryGetValue(definition.Name, out var value))
            {
                var canonicalValue = allowedValues.FirstOrDefault(
                    allowed => string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase));
                if (canonicalValue is null)
                {
                    throw DeploymentException.InvalidChannelParameterValue(
                        definition.Name, channelDefinition.Name, $"must be one of: {string.Join(", ", allowedValues)}");
                }

                normalizedParameters[definition.Name] = canonicalValue;
            }
        }

        var typedParameters = ToTypedParameters(channelDefinition, normalizedParameters, isCreateRequest);
        if (channelDefinition.Name == "slack")
        {
            DeploymentChannelSlack.Validate(typedParameters, isCreateRequest);
        }

        return typedParameters;
    }

    /// <summary>
    /// Converts normalized string values to their catalog-declared JSON types. For example,
    /// <c>isEnabled="true"</c> becomes a JSON boolean while <c>clientId="app-1"</c> remains a JSON string.
    /// </summary>
    private static Dictionary<string, JsonElement> ToTypedParameters(
        ChannelDefinition channelDefinition,
        Dictionary<string, string?> normalizedParameters,
        bool isCreateRequest)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in channelDefinition.Parameters)
        {
            if (normalizedParameters.TryGetValue(definition.Name, out var value))
            {
                result[definition.Name] = definition.ItemParameters is { } fields
                    ? definition.Name switch
                    {
                        "pages" => DeploymentChannelPages.ParseArray(value, fields, isCreateRequest),
                        "lineRegistrations" => DeploymentChannelRegistrations.ParseArray(value, fields, isCreateRequest),
                        _ => ToTypedParameter(definition, value),
                    }
                    : ToTypedParameter(definition, value);
            }
        }

        return result;
    }

    /// <summary>Converts normalized values to typed JSON; cleared values become null or empty arrays.</summary>
    private static JsonElement ToTypedParameter(
        ChannelParameterDefinition definition,
        string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return definition.Type == DeploymentParameterType.Array
                ? JsonSerializer.SerializeToElement(Array.Empty<string>())
                : JsonSerializer.SerializeToElement<string?>(null);
        }

        return definition.Type switch
        {
            DeploymentParameterType.Boolean => JsonSerializer.SerializeToElement(bool.Parse(value)),
            DeploymentParameterType.Integer => JsonSerializer.SerializeToElement(int.Parse(value, CultureInfo.InvariantCulture)),
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
        IReadOnlyDictionary<string, JsonElement> normalizedParameters)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in channelDefinition.Parameters.Where(definition =>
            !definition.IsReadOnly || (!definition.IsSecret && channelDefinition.Name != "slack")))
        {
            if (existingChannel.Parameters.TryGetValue(definition.Name, out var value) &&
                ReadSafeExistingValue(definition, value) is { } normalized)
            {
                result[definition.Name] = normalized;
            }
        }

        foreach (var parameter in normalizedParameters)
        {
            result[parameter.Key] = parameter.Value;
        }

        return result;
    }

    /// <summary>Selects one item operation, rejecting combinations with other operations or scalar assignments.</summary>
    internal static (ChannelCollectionOperation Operation, string OptionName, IReadOnlyList<string> Assignments)? SelectOperation(
        IReadOnlyList<string> parameters,
        IReadOnlyList<string> addAssignments,
        IReadOnlyList<string> setAssignments,
        IReadOnlyList<string> removeAssignments,
        string itemName)
    {
        var operationCount = (addAssignments.Count > 0 ? 1 : 0) +
            (setAssignments.Count > 0 ? 1 : 0) + (removeAssignments.Count > 0 ? 1 : 0);
        if (operationCount == 0)
        {
            return null;
        }

        if (operationCount != 1 || parameters.Count > 0)
        {
            throw new UsageException($"Specify only one of --parameters, --add-{itemName}, --set-{itemName}, or --remove-{itemName}.");
        }

        return addAssignments.Count > 0
            ? (ChannelCollectionOperation.Add, $"--add-{itemName}", addAssignments)
            : setAssignments.Count > 0
                ? (ChannelCollectionOperation.Set, $"--set-{itemName}", setAssignments)
                : (ChannelCollectionOperation.Remove, $"--remove-{itemName}", removeAssignments);
    }

    /// <summary>Reads a detached JSON array without including malformed input values in diagnostics.</summary>
    internal static JsonElement ParseArray(string? value, string parameterName, string itemName)
    {
        try
        {
            using var document = JsonDocument.Parse(value ?? string.Empty);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidArray(parameterName, itemName);
            }

            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw InvalidArray(parameterName, itemName);
        }
    }

    internal static IReadOnlyList<ChannelParameterDefinition> ItemFields(ChannelDefinition channel, string parameterName) =>
        channel.Parameters.Single(parameter => parameter.Name == parameterName).ItemParameters
            ?? throw new InvalidOperationException($"The '{parameterName}' collection has no item schema.");

    /// <summary>
    /// Normalizes field casing and selector whitespace, preserving credential text.
    /// For example, <c>{"ID":" one "}</c> becomes <c>{"id":"one"}</c>.
    /// </summary>
    internal static Dictionary<string, string> NormalizeStringItem(
        JsonElement item,
        IReadOnlyList<ChannelParameterDefinition> fields,
        string collectionName,
        string channelName,
        string operation,
        bool allowReadOnlySelector)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new UsageException($"Each '{collectionName}' item must be a JSON object.");
        }

        var normalizedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in item.EnumerateObject())
        {
            var field = fields.SingleOrDefault(field =>
                field.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase) &&
                (!field.IsReadOnly || allowReadOnlySelector && field.IsSelector));
            if (field is null)
            {
                throw DeploymentException.InvalidChannelParameter(property.Name, channelName, operation);
            }

            var parameterName = $"{collectionName}[].{field.Name}";
            if (property.Value.ValueKind == JsonValueKind.Null ||
                property.Value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                throw DeploymentException.CannotClearChannelParameter(parameterName, channelName);
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                throw DeploymentException.InvalidChannelParameterValue(
                    parameterName, channelName, "must be a nonempty string");
            }

            var text = property.Value.GetString()!;
            if (!normalizedFields.TryAdd(field.Name, field.IsSelector ? text.Trim() : text))
            {
                throw DeploymentException.DuplicateChannelParameter(parameterName);
            }
        }

        return normalizedFields;
    }

    /// <summary>Rejects extra fields on remove or selector-only set operations after item normalization.</summary>
    internal static void ValidateSelectorOperation(
        ChannelCollectionOperation operation, JsonElement item, string optionName, string selectorName)
    {
        var fieldCount = item.EnumerateObject().Count();
        if (operation == ChannelCollectionOperation.Remove && fieldCount != 1)
        {
            throw new UsageException($"{optionName} accepts only the {selectorName} selector.");
        }

        if (operation == ChannelCollectionOperation.Set && fieldCount == 1)
        {
            throw new UsageException($"{optionName} requires at least one writable change in addition to its {selectorName} selector.");
        }
    }

    internal static void EnsureNotEmpty(int count, string parameterName)
    {
        if (count == 0)
        {
            throw new UsageException($"Parameter '{parameterName}' requires at least one item.");
        }
    }

    internal static void EnsureUniqueSelectors(IEnumerable<string> selectors, string parameterName, StringComparer comparer)
    {
        var seen = new HashSet<string>(comparer);
        if (selectors.Any(selector => !seen.Add(selector)))
        {
            throw new UsageException($"Values for '{parameterName}' must be unique.");
        }
    }

    internal static JsonElement ReadCollection(DeploymentChannelRecord channel, string parameterName) =>
        channel.Parameters.TryGetValue(parameterName, out var array) && array.ValueKind == JsonValueKind.Array
            ? array : throw DeploymentException.ChannelConfigurationUnavailable();

    /// <summary>Reads reusable identities without echoing their values or changing the original service text.</summary>
    internal static string ReadSelector(JsonElement item, string selectorName)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(selectorName, out var selector) || selector.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(selector.GetString()))
        {
            throw DeploymentException.ChannelConfigurationUnavailable();
        }

        return selector.GetString()!;
    }

    private static UsageException InvalidArray(string parameterName, string itemName) =>
        new($"Parameter '{parameterName}' must be a JSON array of {itemName} objects.");

    /// <summary>Rejects item options absent from the selected channel's catalog schema before channel-specific validation.</summary>
    private static void ValidateCollectionOperations(
        DeploymentChannelCreateRequest request, ChannelDefinition channel, bool isCreate)
    {
        if (request is not DeploymentChannelUpdateRequest updateRequest)
        {
            return;
        }

        (string OptionName, string CollectionName, IReadOnlyList<string> Assignments)[] operations =
        [
            ("--add-registration", "lineRegistrations", updateRequest.AddRegistration),
            ("--set-registration", "lineRegistrations", updateRequest.SetRegistration),
            ("--remove-registration", "lineRegistrations", updateRequest.RemoveRegistration),
            ("--add-page", "pages", updateRequest.AddPage),
            ("--set-page", "pages", updateRequest.SetPage),
            ("--remove-page", "pages", updateRequest.RemovePage),
            ("--add-site", "sites", updateRequest.AddSite),
            ("--set-site", "sites", updateRequest.SetSite),
            ("--remove-site", "sites", updateRequest.RemoveSite),
        ];
        foreach (var (optionName, collectionName, assignments) in operations.Where(operation => operation.Assignments.Count > 0))
        {
            if (isCreate || !channel.Parameters.Any(parameter => parameter.Name == collectionName && parameter.ItemParameters is not null))
            {
                throw DeploymentException.InvalidChannelOption(optionName, channel.Name, isCreate ? "create" : "update");
            }
        }
    }

    /// <summary>Reuses scalar values matching their catalogue type, excluding masked secrets.</summary>
    private static JsonElement? ReadSafeExistingValue(
        ChannelParameterDefinition definition,
        JsonElement value) =>
        definition.Type switch
        {
            DeploymentParameterType.String when value.ValueKind == JsonValueKind.String &&
                                                (!definition.IsSecret || value.GetString() != BotServiceResponseRedactor.Mask) => value.Clone(),
            DeploymentParameterType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                value.Clone(),
            DeploymentParameterType.Integer when value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _) =>
                value.Clone(),
            _ => null,
        };
}
