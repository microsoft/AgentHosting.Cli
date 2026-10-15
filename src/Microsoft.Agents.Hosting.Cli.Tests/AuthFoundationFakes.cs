using Microsoft.Agents.Hosting.Cli.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Auth;
using Microsoft.Agents.Hosting.Cli.Core.Ports;

namespace Microsoft.Agents.Hosting.Cli.Tests;

/// <summary>
/// Configurable fake <see cref="IPlatformInfo"/> so cache-protection selection and public-client
/// configuration are exercised for every OS family without depending on the real host platform.
/// </summary>
internal sealed class FakePlatformInfo : IPlatformInfo
{
    public bool IsWindows { get; init; }
    public bool IsMacOs { get; init; }
    public bool IsLinux { get; init; }

    public static FakePlatformInfo Windows() => new() { IsWindows = true };
    public static FakePlatformInfo MacOs() => new() { IsMacOs = true };
    public static FakePlatformInfo Linux() => new() { IsLinux = true };

    /// <summary>An unsupported/unknown platform (all families false).</summary>
    public static FakePlatformInfo Unknown() => new();

    public static FakePlatformInfo Named(string name) => name switch
    {
        "windows" => Windows(),
        "macos" => MacOs(),
        "linux" => Linux(),
        _ => Unknown(),
    };

    public static FakePlatformInfo BrokerPlatformOtherThanThisHost() => OperatingSystem.IsLinux() ? MacOs() : Linux();
}

internal static class FakeClientProviders
{
    public static MsalPublicClientProvider Create(FakePlatformInfo platform, string? configDirectory = null, TextWriter? debugOutput = null) =>
        new(
            new AgentHostAuthOptions
            {
                ConfigDirectory = configDirectory ?? Path.Combine(Path.GetTempPath(), "ah-tests-" + Guid.NewGuid().ToString("N")),
                Cloud = CloudInstance.Public,
                AllowUnprotectedCache = true,
                DebugOutput = debugOutput,
            },
            platform,
            new FakeSecureTokenCacheProbe(protectedAvailable: false));
}

/// <summary>
/// Fake <see cref="ISecureTokenCacheProbe"/> that returns a preset availability answer and records the
/// descriptor it was asked about. It performs no MSAL call and acquires no token, so the
/// no-silent-fallback policy is testable entirely offline against temp directories.
/// </summary>
internal sealed class FakeSecureTokenCacheProbe : ISecureTokenCacheProbe
{
    private readonly bool _protectedAvailable;

    public FakeSecureTokenCacheProbe(bool protectedAvailable)
    {
        _protectedAvailable = protectedAvailable;
    }

    public AgentHostCacheDescriptor? LastDescriptor { get; private set; }
    public int CallCount { get; private set; }

    public Task<bool> IsProtectedPersistenceAvailableAsync(
        AgentHostCacheDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        LastDescriptor = descriptor;
        CallCount++;
        return Task.FromResult(_protectedAvailable);
    }
}
