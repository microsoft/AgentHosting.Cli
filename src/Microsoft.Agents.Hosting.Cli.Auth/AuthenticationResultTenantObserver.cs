using System.Security.Claims;
using Microsoft.Agents.Hosting.Cli.Core.Diagnostics;
using Microsoft.Identity.Client;

namespace Microsoft.Agents.Hosting.Cli.Auth;

/// <summary>
/// Token-free account context from a completed normal acquisition. <see cref="PrincipalObjectId"/>
/// is present only for a validated user principal in the issuer <see cref="TenantId"/>.
/// </summary>
public readonly record struct AuthenticatedTenantContext(
    Guid TenantId,
    Guid? PrincipalObjectId,
    string? AccountKey,
    string? ClientId)
{
    public AuthenticatedTenantContext(Guid tenantId)
        : this(
            tenantId,
            PrincipalObjectId: null,
            AccountKey: null,
            ClientId: null)
    {
    }
}

/// <summary>
/// Optional observer for tenant context already returned by normal authentication.
/// It must not initiate authentication or retain token material.
/// </summary>
public interface IAuthenticationResultObserver
{
    /// <summary>
    /// Whether this invocation has passed collection policy and currently accepts tenant context.
    /// </summary>
    bool IsObservationEnabled => true;

    /// <summary>Captures the current invocation-local authentication generation.</summary>
    long CaptureGeneration() => 0;

    /// <summary>Observes one validated authentication context.</summary>
    void Observe(AuthenticatedTenantContext context);

    /// <summary>Observes a context only for the generation captured before acquisition began.</summary>
    void Observe(AuthenticatedTenantContext context, long generation) =>
        Observe(context);
}

internal readonly record struct AuthenticationResultObservation(
    IAuthenticationResultObserver Observer,
    long Generation);

internal static class AuthenticationResultTenantObserver
{
    internal static AuthenticationResultObservation? BeginObservation(
        IAuthenticationResultObserver? observer)
    {
        if (observer is null)
        {
            return null;
        }

        try
        {
            return observer.IsObservationEnabled
                ? new(observer, observer.CaptureGeneration())
                : null;
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.AuthenticationObserverAvailability);
            return null;
        }
    }

    internal static AuthenticatedTenantContext? Observe(
        AuthenticationResult result,
        IAuthenticationResultObserver? observer,
        string? clientId = null) =>
        Observe(
            result,
            BeginObservation(observer),
            clientId);

    internal static AuthenticatedTenantContext? Observe(
        AuthenticationResult result,
        AuthenticationResultObservation? observation,
        string? clientId = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (observation is not { } active)
        {
            return null;
        }

        try
        {
            if (!active.Observer.IsObservationEnabled)
            {
                return null;
            }
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.AuthenticationObserverAvailability);
            return null;
        }

        AuthenticatedTenantContext context;
        try
        {
            if (!TryGetTenantContext(result, clientId, out context))
            {
                return null;
            }
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.AuthenticationTenantExtraction);
            return null;
        }

        try
        {
            active.Observer.Observe(context, active.Generation);
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.AuthenticationObserverCallback);
        }

        return context;
    }

    internal static bool TryGetTenantContext(
        AuthenticationResult result,
        out AuthenticatedTenantContext context)
        => TryGetTenantContext(result, clientId: null, out context);

    internal static bool TryGetTenantContext(
        AuthenticationResult result,
        string? clientId,
        out AuthenticatedTenantContext context)
    {
        ArgumentNullException.ThrowIfNull(result);
        context = default;

        if (!Guid.TryParse(result.TenantId, out Guid tenantId)
            || tenantId == Guid.Empty)
        {
            return false;
        }

        string? accountKey = result.Account?.HomeAccountId?.Identifier;
        Guid? principalObjectId = null;
        try
        {
            if (TryGetUserPrincipalObjectId(
                    result,
                    tenantId,
                    out Guid principal))
            {
                principalObjectId = principal;
            }
        }
        catch (Exception)
        {
            OptionalMeasurementDiagnostics.Report(
                OptionalMeasurementFailureCategory.AuthenticationTenantExtraction);
        }

        context = new AuthenticatedTenantContext(
            tenantId,
            principalObjectId,
            accountKey,
            string.IsNullOrWhiteSpace(clientId) ? null : clientId);
        return true;
    }

    private static bool TryGetUserPrincipalObjectId(
        AuthenticationResult result,
        Guid tenantId,
        out Guid principalObjectId)
    {
        principalObjectId = default;
        ClaimsPrincipal? principal = result.ClaimsPrincipal;
        if (result.Account is null
            || !HasUserOrAbsentIdentityType(principal)
            || !Guid.TryParse(UniqueClaim(principal, "tid"), out Guid claimTenantId)
            || claimTenantId != tenantId
            || !Guid.TryParse(
                UniqueClaim(principal, "oid"),
                out principalObjectId)
            || principalObjectId == Guid.Empty)
        {
            principalObjectId = default;
            return false;
        }

        return true;
    }

    private static bool HasUserOrAbsentIdentityType(ClaimsPrincipal? principal)
    {
        string[] values = ClaimValues(principal, "idtyp");
        return values.Length == 0
            || values.Length == 1
                && string.Equals(
                    values[0],
                    "user",
                    StringComparison.OrdinalIgnoreCase);
    }

    private static string? UniqueClaim(
        ClaimsPrincipal? principal,
        string type)
    {
        string[] values = ClaimValues(principal, type);
        return values.Length == 1 ? values[0] : null;
    }

    private static string[] ClaimValues(
        ClaimsPrincipal? principal,
        string type) =>
        principal?
            .FindAll(type)
            .Select(claim => claim.Value)
            .Take(2)
            .ToArray()
        ?? [];
}
