using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

internal static class DeploymentChannelPages
{
    /// <summary>Validates one page operation; adding requires a token, and every operation requires an id selector.</summary>
    internal static ChannelItemMutation? ValidateOperation(
        DeploymentChannelUpdateRequest request, ChannelDefinition channel)
    {
        var selectedOperation = DeploymentChannelPlan.SelectOperation(
            request.Parameters, request.AddPage, request.SetPage, request.RemovePage, "page");
        if (selectedOperation is not { } selected)
        {
            return null;
        }

        var assignments = DeploymentParameterValidator.ParseParameterAssignments(
            selected.OptionName, selected.Assignments, DeploymentParameterEmptyValuePolicy.Reject);
        var fields = DeploymentChannelPlan.ItemFields(channel, "pages");
        var item = NormalizeItem(JsonSerializer.SerializeToElement(assignments), fields, "update");
        DeploymentChannelPlan.ValidateSelectorOperation(selected.Operation, item, selected.OptionName, "id");
        if (selected.Operation == ChannelCollectionOperation.Add)
        {
            RequireToken(item);
        }

        return new(selected.Operation, item);
    }

    /// <summary>Validates exact page membership and requires tokens for every page on create.</summary>
    internal static JsonElement ParseArray(
        string? value, IReadOnlyList<ChannelParameterDefinition> fields, bool isCreate)
    {
        var array = DeploymentChannelPlan.ParseArray(value, "pages", "page");
        var pages = array.EnumerateArray()
            .Select(page => NormalizeItem(page, fields, isCreate ? "create" : "update")).ToArray();
        DeploymentChannelPlan.EnsureUniqueSelectors(pages.Select(PageId), "pages[].id", StringComparer.Ordinal);
        DeploymentChannelPlan.EnsureNotEmpty(pages.Length, "pages");
        if (isCreate)
        {
            foreach (var page in pages)
            {
                RequireToken(page);
            }
        }

        return JsonSerializer.SerializeToElement(pages);
    }

    /// <summary>
    /// Omitted tokens are valid only for retained ids.
    /// Complete token-bearing replacements bypass unreadable existing pages.
    /// </summary>
    internal static IReadOnlyDictionary<string, JsonElement> BuildUpdate(
        ChannelItemMutation? mutation,
        DeploymentChannelRecord existingChannel,
        IReadOnlyDictionary<string, JsonElement> requestedParameters,
        IReadOnlyDictionary<string, JsonElement> mergedParameters)
    {
        var result = mergedParameters.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (requestedParameters.TryGetValue("pages", out var replacement) &&
            replacement.EnumerateArray().All(page => page.TryGetProperty("accessToken", out _)))
        {
            result["pages"] = replacement.Clone();
            return result;
        }

        var pages = ReadExisting(existingChannel);
        if (requestedParameters.TryGetValue("pages", out replacement))
        {
            pages = replacement.EnumerateArray().Select(page =>
            {
                var existingPage = pages.SingleOrDefault(candidatePage => PageId(candidatePage) == PageId(page));
                if (!page.TryGetProperty("accessToken", out _) && existingPage.ValueKind == JsonValueKind.Undefined)
                {
                    RequireToken(page);
                }

                return MergePage(existingPage, page);
            }).ToArray();
        }
        else if (mutation is { } change)
        {
            var pageIndex = Array.FindIndex(pages, page => PageId(page) == PageId(change.Item));
            if (change.Operation == ChannelCollectionOperation.Add)
            {
                if (pageIndex >= 0)
                {
                    throw DeploymentException.ChannelItemAlreadyExists("facebook", "id");
                }

                pages = [.. pages, change.Item];
            }
            else
            {
                if (pageIndex < 0)
                {
                    throw DeploymentException.ChannelItemNotFound("facebook", "id");
                }

                if (change.Operation == ChannelCollectionOperation.Remove)
                {
                    pages = pages.Where((_, position) => position != pageIndex).ToArray();
                }
                else
                {
                    pages[pageIndex] = MergePage(pages[pageIndex], change.Item);
                }
            }
        }

        DeploymentChannelPlan.EnsureNotEmpty(pages.Length, "pages");
        result["pages"] = JsonSerializer.SerializeToElement(pages);
        return result;
    }

    private static JsonElement NormalizeItem(
        JsonElement item, IReadOnlyList<ChannelParameterDefinition> fields, string operation)
    {
        var selector = fields.Single(field => field.IsSelector);
        var result = DeploymentChannelPlan.NormalizeStringItem(
            item, fields, "pages", "facebook", operation, allowReadOnlySelector: false);

        if (!result.ContainsKey(selector.Name))
        {
            throw new UsageException("Each Facebook page requires an id selector.");
        }

        return JsonSerializer.SerializeToElement(result);
    }

    /// <summary>Keeps page ids and unmasked tokens; drops unknown fields.</summary>
    private static JsonElement[] ReadExisting(DeploymentChannelRecord existingChannel)
    {
        var array = DeploymentChannelPlan.ReadCollection(existingChannel, "pages");
        var pages = new List<JsonElement>();
        foreach (var page in array.EnumerateArray())
        {
            var pageId = DeploymentChannelPlan.ReadSelector(page, "id").Trim();
            var fields = new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(pageId) };
            if (page.TryGetProperty("accessToken", out var token) && token.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(token.GetString()) && token.GetString() != BotServiceResponseRedactor.Mask)
            {
                fields["accessToken"] = token.Clone();
            }

            pages.Add(JsonSerializer.SerializeToElement(fields));
        }

        if (pages.Select(PageId).Distinct(StringComparer.Ordinal).Count() != pages.Count)
        {
            throw DeploymentException.ChannelConfigurationUnavailable();
        }

        return pages.ToArray();
    }

    private static JsonElement MergePage(JsonElement existing, JsonElement changes)
    {
        var fields = existing.ValueKind == JsonValueKind.Object
            ? existing.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())
            : new Dictionary<string, JsonElement>();
        foreach (var property in changes.EnumerateObject())
        {
            fields[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.SerializeToElement(fields);
    }

    private static string PageId(JsonElement page) => page.GetProperty("id").GetString()!;

    private static void RequireToken(JsonElement page)
    {
        if (!page.TryGetProperty("accessToken", out _))
        {
            throw new UsageException("New Facebook pages require 'accessToken'.");
        }
    }
}
