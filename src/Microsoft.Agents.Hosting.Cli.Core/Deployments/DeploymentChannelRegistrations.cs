using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

internal static class DeploymentChannelRegistrations
{
    /// <summary>Validates one registration operation; generatedId selects existing items and is never supplied on add.</summary>
    internal static ChannelItemMutation? ValidateOperation(
        DeploymentChannelUpdateRequest request, ChannelDefinition channel)
    {
        var selectedOperation = DeploymentChannelPlan.SelectOperation(
            request.Parameters, request.AddRegistration, request.SetRegistration, request.RemoveRegistration, "registration");
        if (selectedOperation is not { } selected)
        {
            return null;
        }

        var assignments = DeploymentParameterValidator.ParseParameterAssignments(
            selected.OptionName, selected.Assignments, DeploymentParameterEmptyValuePolicy.Reject);
        var fields = DeploymentChannelPlan.ItemFields(channel, "lineRegistrations");
        var selector = fields.Single(field => field.IsSelector);
        if (selected.Operation != ChannelCollectionOperation.Add && !assignments.ContainsKey(selector.Name))
        {
            throw new UsageException($"{selected.OptionName} requires the generatedId selector of an existing LINE registration.");
        }

        var item = NormalizeItem(JsonSerializer.SerializeToElement(assignments), fields, "update",
            allowExistingSelector: selected.Operation != ChannelCollectionOperation.Add);
        DeploymentChannelPlan.ValidateSelectorOperation(selected.Operation, item, selected.OptionName, selector.Name);

        return new(selected.Operation, item);
    }

    /// <summary>Validates exact membership, distinguishing retained generatedIds from new credential-bearing registrations.</summary>
    internal static JsonElement ParseArray(
        string? value, IReadOnlyList<ChannelParameterDefinition> fields, bool isCreate)
    {
        var array = DeploymentChannelPlan.ParseArray(value, "lineRegistrations", "registration");
        var registrations = array.EnumerateArray()
            .Select(registration => NormalizeItem(registration, fields, isCreate ? "create" : "update",
                allowExistingSelector: !isCreate)).ToArray();
        DeploymentChannelPlan.EnsureNotEmpty(registrations.Length, "lineRegistrations");
        DeploymentChannelPlan.EnsureUniqueSelectors(
            registrations.Where(registration => registration.TryGetProperty("generatedId", out _)).Select(RegistrationId),
            "lineRegistrations[].generatedId", StringComparer.Ordinal);

        return JsonSerializer.SerializeToElement(registrations);
    }

    /// <summary>
    /// Reuses generatedIds, never returned credentials.
    /// All-new replacements bypass unreadable state and may change callback URLs.
    /// </summary>
    internal static IReadOnlyDictionary<string, JsonElement> BuildUpdate(
        ChannelItemMutation? mutation,
        DeploymentChannelRecord existingChannel,
        IReadOnlyDictionary<string, JsonElement> requestedParameters,
        IReadOnlyDictionary<string, JsonElement> mergedParameters)
    {
        var result = mergedParameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (requestedParameters.TryGetValue("lineRegistrations", out var replacement) &&
            replacement.EnumerateArray().All(item => !item.TryGetProperty("generatedId", out _)))
        {
            result["lineRegistrations"] = replacement.Clone();
            return result;
        }

        var registrations = ReadExisting(existingChannel);
        if (requestedParameters.TryGetValue("lineRegistrations", out replacement))
        {
            foreach (var item in replacement.EnumerateArray().Where(item => item.TryGetProperty("generatedId", out _)))
            {
                if (!registrations.Any(existingRegistration => RegistrationId(existingRegistration) == RegistrationId(item)))
                {
                    throw DeploymentException.ChannelItemNotFound("line", "generatedId");
                }
            }

            result["lineRegistrations"] = replacement.Clone();
            return result;
        }

        if (mutation is { } change)
        {
            if (change.Operation == ChannelCollectionOperation.Add)
            {
                registrations = [.. registrations, change.Item];
            }
            else
            {
                var registrationIndex = Array.FindIndex(registrations, registration => RegistrationId(registration) == RegistrationId(change.Item));
                if (registrationIndex < 0)
                {
                    throw DeploymentException.ChannelItemNotFound("line", "generatedId");
                }

                if (change.Operation == ChannelCollectionOperation.Remove)
                {
                    registrations = registrations.Where((_, position) => position != registrationIndex).ToArray();
                }
                else
                {
                    registrations[registrationIndex] = change.Item;
                }
            }
        }

        DeploymentChannelPlan.EnsureNotEmpty(registrations.Length, "lineRegistrations");
        result["lineRegistrations"] = JsonSerializer.SerializeToElement(registrations);
        return result;
    }

    /// <summary>Allows generatedId only for retained registrations; new items must supply every required credential.</summary>
    private static JsonElement NormalizeItem(
        JsonElement item, IReadOnlyList<ChannelParameterDefinition> fields, string operation, bool allowExistingSelector)
    {
        var selector = fields.Single(field => field.IsSelector);
        var result = DeploymentChannelPlan.NormalizeStringItem(
            item, fields, "lineRegistrations", "line", operation, allowReadOnlySelector: allowExistingSelector);

        if (!allowExistingSelector || !result.ContainsKey(selector.Name))
        {
            foreach (var field in fields.Where(field => field.IsRequired && !field.IsReadOnly))
            {
                if (!result.ContainsKey(field.Name))
                {
                    throw new UsageException($"New LINE registrations require '{field.Name}'.");
                }
            }
        }

        return JsonSerializer.SerializeToElement(result);
    }

    /// <summary>Projects existing registration identities only; service-returned credentials are never replayed.</summary>
    private static JsonElement[] ReadExisting(DeploymentChannelRecord existingChannel)
    {
        var array = DeploymentChannelPlan.ReadCollection(existingChannel, "lineRegistrations");
        var registrations = new List<JsonElement>();
        foreach (var registration in array.EnumerateArray())
        {
            var registrationId = DeploymentChannelPlan.ReadSelector(registration, "generatedId").Trim();
            registrations.Add(JsonSerializer.SerializeToElement(new { generatedId = registrationId }));
        }

        if (registrations.Select(RegistrationId).Distinct(StringComparer.Ordinal).Count() != registrations.Count)
        {
            throw DeploymentException.ChannelConfigurationUnavailable();
        }

        return registrations.ToArray();
    }

    private static string RegistrationId(JsonElement registration) => registration.GetProperty("generatedId").GetString()!;
}
