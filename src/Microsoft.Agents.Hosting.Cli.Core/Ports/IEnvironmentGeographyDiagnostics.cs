namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>Fixed stages of an optional environment-geography lookup.</summary>
public enum EnvironmentGeographyStage
{
    TargetIncomplete,
    TokenAcquisitionStarted,
    TokenUnavailable,
    TokenAvailable,
    TokenRejected,
    RequestStarted,
    ResponseReceived,
    GeographyMissing,
    ResponseInvalid,
    GeographyRead,
    TokenProfileUnsupported,
    TokenProviderUnavailable,
    TokenAccountUnavailable,
    TokenInteractionRequired,
    TokenBrokerUnavailable,
    TokenAccountMismatch,
    TokenAcquisitionFailed,
}

/// <summary>Optional local diagnostics; receives no tokens, identifiers, URLs or response data.</summary>
public interface IEnvironmentGeographyDiagnostics
{
    /// <summary>Reports a fixed stage and, for an HTTP response, its numeric status.</summary>
    void Report(EnvironmentGeographyStage stage, int? httpStatusCode = null);
}
