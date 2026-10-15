namespace Microsoft.Agents.Hosting.Cli.Core.Operations;

/// <summary>Input for <c>msagent telemetry enable</c>.</summary>
public sealed record TelemetryEnableRequest;

/// <summary>Input for <c>msagent telemetry disable</c>.</summary>
public sealed record TelemetryDisableRequest;

/// <summary>Input for <c>msagent telemetry status</c>.</summary>
public sealed record TelemetryStatusRequest;

/// <summary>Privacy-safe telemetry policy state.</summary>
public sealed record TelemetryControlReport : ITextGridRenderable
{
    public int SchemaVersion { get; init; } = 1;

    public required string Status { get; init; }

    public required string RememberedPreference { get; init; }

    public required string EnvironmentOverride { get; init; }

    public bool CompositionFallbackEnabled { get; init; }

    public bool CollectionActivationAvailable { get; init; }

    public bool EffectiveCollectionEnabled { get; init; }

    public required string EffectiveReason { get; init; }

    public IReadOnlyList<string> ToTextLines() =>
    [
        $"Telemetry preference: {RememberedPreference}.",
        $"Environment override: {EnvironmentOverride}.",
        $"Composition fallback: {(CompositionFallbackEnabled ? "enabled" : "disabled")}.",
        $"Collection activation: {(CollectionActivationAvailable ? "available" : "unavailable")}.",
        $"Effective collection: {(EffectiveCollectionEnabled ? "enabled" : "disabled")} ({EffectiveReason}).",
    ];
}
