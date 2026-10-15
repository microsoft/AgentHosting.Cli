namespace Microsoft.Agents.Hosting.Cli.Core.Ports;

/// <summary>
/// The service reported that an environment serves no such connector.
///
/// This is the one answer that proves absence. A lookup that fails for any other reason leaves the
/// question open, and is reported as itself rather than as a missing connector.
/// </summary>
/// <param name="connectorInternalId">The connector the service does not know.</param>
/// <param name="environmentId">The environment that was read.</param>
/// <param name="innerException">What the service answered with.</param>
public sealed class ConnectorNotFoundException(string connectorInternalId, string environmentId, Exception? innerException = null)
    : Exception($"Environment '{environmentId}' does not serve connector '{connectorInternalId}'.", innerException)
{
    /// <summary>The connector the service does not know.</summary>
    public string ConnectorInternalId { get; } = connectorInternalId;

    /// <summary>The environment that was read.</summary>
    public string EnvironmentId { get; } = environmentId;
}
