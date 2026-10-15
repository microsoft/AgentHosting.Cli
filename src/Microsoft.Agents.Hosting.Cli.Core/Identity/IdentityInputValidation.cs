namespace Microsoft.Agents.Hosting.Cli.Core.Identity;

/// <summary>
/// Shared, message-free input predicates used by the identity provisioning plans. Keeping these
/// pure predicates in one place lets the Blueprint and App Registration plans validate the same
/// Azure/Graph coordinates identically while each plan keeps its own error messages.
/// </summary>
internal static class IdentityInputValidation
{
    public const int MaximumDisplayNameLength = 256;

    /// <summary>Azure resource-group name contract: 1-90 chars, no trailing period, limited symbols.</summary>
    public static bool IsValidResourceGroupName(string name) =>
        name.Length is >= 1 and <= 90 &&
        name[^1] != '.' &&
        name.All(character =>
            char.IsLetterOrDigit(character) ||
            character is '_' or '-' or '.' or '(' or ')');

    /// <summary>Canonical Azure location name: 1-90 alphanumeric characters.</summary>
    public static bool IsValidLocation(string location) =>
        location.Length is >= 1 and <= 90 &&
        location.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// User-assigned managed identity name contract, constrained to what can also serve as a
    /// federated-credential name: 3-120 URL-safe characters beginning with a letter or digit.
    /// </summary>
    public static bool IsValidManagedIdentityName(string name) =>
        name.Length is >= 3 and <= 120 &&
        char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_');
}
