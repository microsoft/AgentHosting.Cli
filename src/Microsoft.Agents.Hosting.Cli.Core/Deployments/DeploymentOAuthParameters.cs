using System.Text.Json;
using Microsoft.Agents.Hosting.Cli.Core.Operations;

namespace Microsoft.Agents.Hosting.Cli.Core.Deployments;

public enum OauthProviderKeyType
{
    Name,
    Id,
}

/// <summary>Projects OAuth connection parameters into canonical and provider-declared shapes.</summary>
internal static class DeploymentOAuthParameters
{
    internal static bool IsSecret(string name) =>
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        HasNumberedSuffix(name, "key") ||
        HasNumberedSuffix(name, "token") ||
        HasNumberedSuffix(name, "credential");

    internal static bool IsProviderSecret(string type) =>
        type.Equals("securestring", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Produces a safe OAuth connection shape. When a provider definition is supplied, only its declared
    /// parameters remain and its secret metadata drives masking. Without a provider definition, unknown parameters
    /// remain and recognizable secret names are masked. The raw ARM response is included only when requested and is
    /// redacted with the same provider metadata.
    /// </summary>
    internal static DeploymentOAuthConnection Sanitize(
        DeploymentOAuthConnection connection,
        OAuthProviderDefinition? provider,
        bool includeArmResponse = true)
    {
        var parameters = connection.Parameters ??
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var definitions = (provider?.Parameters ?? [])
            .ToDictionary(
                parameter => parameter.Name,
                StringComparer.OrdinalIgnoreCase);
        var secretNames = definitions.Values
            .Where(parameter => IsProviderSecret(parameter.Type))
            .Select(parameter => parameter.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sanitized = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in parameters)
        {
            if (parameter.Key.Equals(CommonOAuthParameters.ClientId, StringComparison.OrdinalIgnoreCase) ||
                parameter.Key.Equals(CommonOAuthParameters.Scopes, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (provider is not null)
            {
                if (definitions.TryGetValue(parameter.Key, out var definition))
                {
                    sanitized[definition.Name] = SanitizeValue(
                        parameter.Value,
                        IsProviderSecret(definition.Type));
                }
            }
            else
            {
                sanitized[parameter.Key] = SanitizeValue(
                    parameter.Value,
                    IsSecret(parameter.Key));
            }
        }

        return connection with
        {
            Parameters = sanitized,
            RawArmResponse = includeArmResponse
                ? BotServiceResponseRedactor.RedactOAuth(connection.RawArmResponse, secretNames)
                : null,
        };
    }

    private static string? SanitizeValue(string? value, bool isSecret) =>
        isSecret && value is not null
            ? BotServiceResponseRedactor.Mask
            : value;

    private static bool HasNumberedSuffix(string name, string suffix)
    {
        var suffixEnd = name.Length;
        while (suffixEnd > 0 && char.IsAsciiDigit(name[suffixEnd - 1]))
        {
            suffixEnd--;
        }

        return name.AsSpan(0, suffixEnd).EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Describes OAuth connection parameters that recur across providers and therefore have named CLI options and
    /// top-level connection fields. Membership does not mean that every provider supports the parameter; the live
    /// provider catalogue remains authoritative for provider-specific declaration and required-value rules.
    /// </summary>
    internal static class CommonOAuthParameters
    {
        internal const string ClientId = "ClientId";
        internal const string ClientSecret = "ClientSecret";
        internal const string Scopes = "Scopes";

        internal static IReadOnlyList<CommonOAuthParameterDefinition> All { get; } =
        [
            new(
                ClientId,
                "--client-id",
                IsProviderDeclarationRequired: true,
                request => request.ClientId,
                connection => connection.ClientId),
            new(
                ClientSecret,
                "--client-secret",
                IsProviderDeclarationRequired: true,
                request => request.ClientSecret,
                _ => null),
            new(
                Scopes,
                "--scopes",
                IsProviderDeclarationRequired: false,
                request => request.Scopes,
                connection => string.Join(' ', connection.Scopes)),
        ];

        private static readonly HashSet<string> Names =
            All.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        internal static bool Contains(string name) =>
            Names.Contains(name);

        internal static string OptionFor(string name) =>
            All.FirstOrDefault(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Option
            ?? $"--parameters {name}=...";
    }

    internal sealed record CommonOAuthParameterDefinition(
        string Name,
        string Option,
        bool IsProviderDeclarationRequired,
        Func<DeploymentOAuthUpdateRequest, string?> RequestValue,
        Func<DeploymentOAuthConnection, string?> ExistingValue);
}
