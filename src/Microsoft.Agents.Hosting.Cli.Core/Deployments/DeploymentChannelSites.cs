using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

internal sealed record ChannelSiteMutation(ChannelCollectionOperation Operation, IReadOnlyList<JsonElement> Items);

internal static class DeploymentChannelSites
{
    /// <summary>Validates initial membership; create accepts only a complete sites array.</summary>
    internal static ChannelSiteMutation ValidateCreate(
        DeploymentChannelCreateRequest request, ChannelDefinition channel, IReadOnlyList<ChannelParameterDefinition> fields) =>
        ValidateReplacement(request.Parameters, channel, fields, isCreate: true);

    /// <summary>Validates exact membership or one site operation, including opt-in clearing and typed settings.</summary>
    internal static ChannelSiteMutation ValidateUpdate(
        DeploymentChannelUpdateRequest request,
        ChannelDefinition channel,
        IReadOnlyList<ChannelParameterDefinition> fields)
    {
        var selectedOperation = DeploymentChannelPlan.SelectOperation(
            request.Parameters, request.AddSite, request.SetSite, request.RemoveSite, "site");
        if (request.Parameters.Count > 0)
        {
            return ValidateReplacement(request.Parameters, channel, fields, isCreate: false);
        }

        if (selectedOperation is not { } selected)
        {
            throw new UsageException($"No site configuration was provided for '{channel.Name}' channel update.");
        }

        var parsedAssignments = DeploymentParameterValidator.ParseParameterAssignments(
            selected.OptionName, selected.Assignments, selected.Operation == ChannelCollectionOperation.Add
                ? DeploymentParameterEmptyValuePolicy.Reject : DeploymentParameterEmptyValuePolicy.Allow);
        var typedFields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in parsedAssignments)
        {
            var field = FindWritableField(name, fields, channel.Name, "update", selected.Operation);
            var normalized = string.IsNullOrEmpty(value) ? null : DeploymentParameterValidator.NormalizeValue(
                field.Name, field.Type, value,
                (parameter, _) => DeploymentException.InvalidChannelParameterValue(
                    $"sites[].{parameter}", channel.Name, "must be true or false"));
            typedFields[field.Name] = string.IsNullOrEmpty(normalized) ? JsonSerializer.SerializeToElement<string?>(null) :
                field.Type == DeploymentParameterType.Boolean ? JsonSerializer.SerializeToElement(bool.Parse(normalized)) :
                field.Type == DeploymentParameterType.Array ? normalized.TrimStart().StartsWith('[')
                    ? ParseJson(normalized, field.Name, channel.Name)
                    : JsonSerializer.SerializeToElement(normalized.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) :
                JsonSerializer.SerializeToElement(normalized);
        }

        return new(selected.Operation,
            [NormalizeItem(JsonSerializer.SerializeToElement(typedFields), fields, channel.Name, "update", selected.Operation)]);
    }

    private static ChannelSiteMutation ValidateReplacement(
        IReadOnlyList<string> parameters, ChannelDefinition channel, IReadOnlyList<ChannelParameterDefinition> fields, bool isCreate)
    {
        if (parameters.Count == 0)
        {
            throw new UsageException($"No site configuration was provided for '{channel.Name}' channel create.");
        }

        var operationName = isCreate ? "create" : "update";
        var assignments = DeploymentParameterValidator.ParseParameterAssignments(
            "--parameters", parameters, isCreate
                ? DeploymentParameterEmptyValuePolicy.Reject : DeploymentParameterEmptyValuePolicy.Allow);
        DeploymentParameterValidator.RejectUnknownParameters(
            channel.Parameters.Where(field => !field.IsReadOnly),
            assignments, field => field.Name,
            name => DeploymentException.InvalidChannelParameter(name, channel.Name, operationName));
        var value = assignments["sites"];
        var array = DeploymentChannelPlan.ParseArray(string.IsNullOrEmpty(value) ? "[]" : value, "sites", "site");

        if (!isCreate && array.GetArrayLength() == 0 &&
            !channel.Parameters.Single(field => field.Name == "sites").CanClear)
        {
            throw DeploymentException.CannotClearChannelParameter("sites", channel.Name);
        }

        var items = array.EnumerateArray().Select(item =>
            NormalizeItem(item, fields, channel.Name, operationName, isCreate ? ChannelCollectionOperation.Add : ChannelCollectionOperation.Replace))
            .ToArray();
        DeploymentChannelPlan.EnsureUniqueSelectors(
            items.Select(item => SiteSelector(item, fields).Value.GetString()!), "sites[].siteName", StringComparer.OrdinalIgnoreCase);
        return new(ChannelCollectionOperation.Replace, items);
    }

    /// <summary>Builds the requested membership before Azure assigns ids to new sites.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> BuildCreate(ChannelSiteMutation mutation) =>
        Payload(mutation.Items);

    /// <summary>Only selectors are sent for new sites initially; their settings require service-assigned ids.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> BuildInitialWrite(
        ChannelDefinition channel,
        IReadOnlyDictionary<string, JsonElement> requestedParameters)
    {
        var fields = DeploymentChannelPlan.ItemFields(channel, "sites");
        return Payload(requestedParameters["sites"].EnumerateArray().Select(site =>
            site.TryGetProperty("siteId", out _) ? ToDictionary(site) :
                fields.Where(field => field.IsSelector && !field.IsReadOnly && site.TryGetProperty(field.Name, out _))
                    .ToDictionary(field => field.Name, field => site.GetProperty(field.Name).Clone())));
    }

    /// <summary>Merges settings for matched sites while preserving exact replacement membership and safe service fields.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> BuildUpdate(
        ChannelSiteMutation mutation,
        ChannelDefinition channel,
        DeploymentChannelRecord existingChannel)
    {
        if (mutation.Operation == ChannelCollectionOperation.Replace && mutation.Items.Count == 0)
        {
            return Payload(Array.Empty<JsonElement>());
        }

        var fields = DeploymentChannelPlan.ItemFields(channel, "sites");
        var array = DeploymentChannelPlan.ReadCollection(existingChannel, "sites");
        var sites = array.EnumerateArray().ToArray();
        var siteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var siteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            if (!siteNames.Add(DeploymentChannelPlan.ReadSelector(site, "siteName").Trim()) ||
                !siteIds.Add(DeploymentChannelPlan.ReadSelector(site, "siteId")))
            {
                throw DeploymentException.ChannelConfigurationUnavailable();
            }
        }

        var result = mutation.Operation == ChannelCollectionOperation.Replace
            ? [] : sites.Select(site => PreserveSite(site, fields)).ToList();
        if (mutation.Operation == ChannelCollectionOperation.Replace)
        {
            foreach (var item in mutation.Items)
            {
                var selector = SiteSelector(item, fields);
                var siteIndex = FindSiteIndex(sites, selector);
                if (siteIndex < 0)
                {
                    result.Add(ToDictionary(NormalizeItem(item, fields, channel.Name, "update", ChannelCollectionOperation.Add)));
                }
                else
                {
                    var changes = NormalizeItem(item, fields, channel.Name, "update", ChannelCollectionOperation.Set, requireChanges: false);
                    result.Add(ApplyChanges(PreserveSite(sites[siteIndex], fields), changes, fields));
                }
            }
        }
        else if (mutation.Operation == ChannelCollectionOperation.Add)
        {
            var item = mutation.Items.Single();
            if (siteNames.Contains(item.GetProperty("siteName").GetString()!))
            {
                throw DeploymentException.ChannelItemAlreadyExists(channel.Name, "siteName");
            }

            result.Add(ToDictionary(item));
        }
        else
        {
            var item = mutation.Items.Single();
            var siteIndex = FindSiteIndex(sites, SiteSelector(item, fields));
            if (siteIndex < 0)
            {
                throw DeploymentException.ChannelItemNotFound(channel.Name, "siteName");
            }

            if (mutation.Operation == ChannelCollectionOperation.Remove)
            {
                result.RemoveAt(siteIndex);
            }
            else
            {
                result[siteIndex] = ApplyChanges(result[siteIndex], item, fields);
            }
        }

        foreach (var site in result)
        {
            ValidateSettings(site);
        }

        return Payload(result);
    }

    /// <summary>Correlates the initial response with requested membership before configuring only newly created sites.</summary>
    internal static IReadOnlyDictionary<string, JsonElement>? BuildNewSiteConfiguration(
        ChannelDefinition channel,
        IReadOnlyDictionary<string, JsonElement> requestedParameters,
        DeploymentChannelRecord response)
    {
        var fields = DeploymentChannelPlan.ItemFields(channel, "sites");
        var expectedSites = requestedParameters["sites"].EnumerateArray()
            .ToDictionary(site => site.GetProperty("siteName").GetString()!, StringComparer.OrdinalIgnoreCase);
        var newSites = expectedSites.Values
            .Where(site => !site.TryGetProperty("siteId", out _) && fields.Any(field =>
                !field.IsReadOnly && !field.IsSelector && site.TryGetProperty(field.Name, out _)))
            .ToDictionary(site => site.GetProperty("siteName").GetString()!, StringComparer.OrdinalIgnoreCase);
        if (newSites.Count == 0)
        {
            return null;
        }

        var array = DeploymentChannelPlan.ReadCollection(response, "sites");
        if (array.GetArrayLength() != expectedSites.Count)
        {
            throw DeploymentException.ChannelConfigurationUnavailable();
        }

        var items = new List<JsonElement>();
        foreach (var site in array.EnumerateArray())
        {
            var siteName = DeploymentChannelPlan.ReadSelector(site, "siteName");
            var siteId = DeploymentChannelPlan.ReadSelector(site, "siteId");
            if (!expectedSites.Remove(siteName, out var expectedSite) ||
                expectedSite.TryGetProperty("siteId", out var priorId) &&
                !string.Equals(siteId, priorId.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw DeploymentException.ChannelConfigurationUnavailable();
            }

            var item = new Dictionary<string, JsonElement>
            {
                ["siteName"] = JsonSerializer.SerializeToElement(siteName),
            };
            if (newSites.Remove(siteName, out var changes))
            {
                foreach (var field in fields.Where(field => !field.IsReadOnly && !field.IsSelector))
                {
                    if (changes.TryGetProperty(field.Name, out var value))
                    {
                        item[field.Name] = value.Clone();
                    }
                }
            }

            items.Add(JsonSerializer.SerializeToElement(item));
        }

        return BuildUpdate(new(ChannelCollectionOperation.Replace, items), channel, response);
    }

    /// <summary>Normalizes site fields and enforces selector/change requirements before checking effective settings.</summary>
    private static JsonElement NormalizeItem(
        JsonElement item, IReadOnlyList<ChannelParameterDefinition> fields,
        string channelName, string operationName, ChannelCollectionOperation operation, bool requireChanges = true)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new UsageException("Each site must be a JSON object.");
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in item.EnumerateObject())
        {
            var field = FindWritableField(property.Name, fields, channelName, operationName, operation);
            if (!result.TryAdd(field.Name, NormalizeValue(property.Value, field, operation == ChannelCollectionOperation.Add,
                () => DeploymentException.InvalidChannelParameterValue(
                    $"sites[].{field.Name}", channelName, $"must have a valid {field.Type} value"))))
            {
                throw DeploymentException.DuplicateChannelParameter($"sites[].{field.Name}");
            }
        }

        var normalized = JsonSerializer.SerializeToElement(result);
        if (operation == ChannelCollectionOperation.Add)
        {
            foreach (var field in fields.Where(field => !field.IsReadOnly && field.IsRequired))
            {
                if (!result.ContainsKey(field.Name))
                {
                    throw new UsageException($"Parameter '{field.Name}' is required when adding a site.");
                }
            }
        }
        else
        {
            _ = SiteSelector(normalized, fields);
            if (operation == ChannelCollectionOperation.Set && requireChanges)
            {
                DeploymentChannelPlan.ValidateSelectorOperation(operation, normalized, "--set-site", "siteName");
            }
        }

        ValidateSettings(result);
        return normalized;
    }

    private static ChannelParameterDefinition FindWritableField(
        string name, IReadOnlyList<ChannelParameterDefinition> fields,
        string channelName, string operationName, ChannelCollectionOperation operation)
    {
        var field = fields.SingleOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (field is null || !(operation switch
        {
            ChannelCollectionOperation.Add => !field.IsReadOnly,
            ChannelCollectionOperation.Set => field.IsSelector || !field.IsReadOnly,
            ChannelCollectionOperation.Remove => field.IsSelector,
            _ => !field.IsReadOnly,
        }))
        {
            throw DeploymentException.InvalidChannelParameter(name, channelName, operationName);
        }

        return field;
    }

    /// <summary>
    /// Validates typed values; explicit array clearing becomes <c>[]</c>, while reusable service nulls remain null.
    /// </summary>
    private static JsonElement NormalizeValue(
        JsonElement value, ChannelParameterDefinition field, bool creating, Func<Exception> invalid,
        bool isUserInput = true)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (creating || field.IsRequired || isUserInput && !field.CanClear)
            {
                throw invalid();
            }

            return isUserInput && field.Type == DeploymentParameterType.Array
                ? JsonSerializer.SerializeToElement(Array.Empty<string>()) : value.Clone();
        }

        switch (field.Type)
        {
            case DeploymentParameterType.String:
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw invalid();
                }

                var text = value.GetString()!.Trim();
                if (text.Length == 0)
                {
                    return creating || field.IsRequired || isUserInput && !field.CanClear
                        ? throw invalid() : JsonSerializer.SerializeToElement<string?>(null);
                }

                if (isUserInput && field.AllowedValues is { } allowedValues)
                {
                    text = allowedValues.FirstOrDefault(allowed => string.Equals(allowed, text, StringComparison.OrdinalIgnoreCase))
                        ?? throw invalid();
                }

                return JsonSerializer.SerializeToElement(text);
            case DeploymentParameterType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return value.Clone();
            case DeploymentParameterType.Array when value.ValueKind == JsonValueKind.Array:
                if (isUserInput && !creating && value.GetArrayLength() == 0 && !field.CanClear ||
                    value.EnumerateArray().Any(element => element.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(element.GetString())))
                {
                    throw invalid();
                }

                return value.Clone();
            default:
                throw invalid();
        }
    }

    /// <summary>Validates reusable service values without imposing user-only clearing or enum restrictions.</summary>
    private static Dictionary<string, JsonElement> PreserveSite(
        JsonElement site, IReadOnlyList<ChannelParameterDefinition> fields)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields.Where(field => !field.IsSecret && field.Name != "eTag"))
        {
            if (site.TryGetProperty(field.Name, out var value))
            {
                _ = NormalizeValue(value, field, creating: false, DeploymentException.ChannelConfigurationUnavailable, isUserInput: false);
                result[field.Name] = value.Clone();
            }
        }

        return result;
    }

    private static Dictionary<string, JsonElement> ApplyChanges(
        Dictionary<string, JsonElement> site, JsonElement changes, IReadOnlyList<ChannelParameterDefinition> fields)
    {
        foreach (var property in changes.EnumerateObject().Where(property =>
            !fields.Single(field => field.Name == property.Name).IsSelector))
        {
            site[property.Name] = property.Value.Clone();
        }

        return site;
    }

    private static void ValidateSettings(Dictionary<string, JsonElement> site)
    {
        if (site.TryGetValue("isDetailedLoggingEnabled", out var detailedLogging) && detailedLogging.ValueKind == JsonValueKind.True &&
            site.TryGetValue("isNoStorageEnabled", out var noStorage) && noStorage.ValueKind == JsonValueKind.True)
        {
            throw new UsageException("isDetailedLoggingEnabled=true cannot be combined with isNoStorageEnabled=true.");
        }
    }

    private static JsonProperty SiteSelector(JsonElement item, IReadOnlyList<ChannelParameterDefinition> fields)
    {
        var selectors = item.EnumerateObject().Where(property =>
            fields.Any(field => field.IsSelector && field.Name == property.Name)).ToArray();
        if (selectors.Length != 1)
        {
            throw new UsageException("Specify siteName to identify a site.");
        }

        return selectors[0];
    }

    private static int FindSiteIndex(JsonElement[] sites, JsonProperty selector) =>
        Array.FindIndex(sites, site => string.Equals(
            site.GetProperty(selector.Name).GetString()!.Trim(), selector.Value.GetString(), StringComparison.OrdinalIgnoreCase));

    private static JsonElement ParseJson(string value, string fieldName, string channelName)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw DeploymentException.InvalidChannelParameterValue($"sites[].{fieldName}", channelName, "must contain valid JSON");
        }
    }

    private static Dictionary<string, JsonElement> ToDictionary(JsonElement item) =>
        item.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, JsonElement> Payload<T>(IEnumerable<T> items) =>
        new Dictionary<string, JsonElement> { ["sites"] = JsonSerializer.SerializeToElement(items) };
}
