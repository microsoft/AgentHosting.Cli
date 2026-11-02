using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

/// <summary>
/// The harness exists to exercise the working tree. <c>--add-source</c> adds a feed rather than
/// replacing the configured ones, so <c>dotnet tool install</c> was free to resolve a newer published
/// build of the same package and the suite then tested that instead — silently, and with every
/// scenario still reporting against it. These pin the two guards that stop it.
/// </summary>
public sealed class HarnessToolVersionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ahe2e-pack-" + Guid.NewGuid().ToString()[..8]);

    public HarnessToolVersionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string Package(string version)
    {
        var path = Path.Combine(_directory, $"Microsoft.Agents.Hosting.Cli.{version}.nupkg");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void ThePackedVersionIsReadFromThePackageTheBuildProduced()
    {
        Package("0.1.57-beta-g25087c755d");

        Assert.Equal("0.1.57-beta-g25087c755d", MsagentCliBuilder.PackedVersion(_directory));
    }

    [Fact]
    public void TheToolPackageIsReadPastTheRuntimeSpecificPackagesPackedBesideIt()
    {
        Package("0.2.47-beta-gece7dce1e1");
        Package("win-x64.0.2.47-beta-gece7dce1e1");
        Package("linux-arm64.0.2.47-beta-gece7dce1e1");
        Package("osx-x64.0.2.47-beta-gece7dce1e1");

        Assert.Equal("0.2.47-beta-gece7dce1e1", MsagentCliBuilder.PackedVersion(_directory));
    }

    [Fact]
    public void RuntimeSpecificPackagesWithoutTheToolPackageAreRefused()
    {
        Package("win-x64.0.2.47-beta-gece7dce1e1");

        Assert.Throws<E2EException>(() => MsagentCliBuilder.PackedVersion(_directory));
    }

    [Fact]
    public void TwoPackagesAreRefusedRatherThanGuessedBetween()
    {
        Package("0.1.57-beta-g25087c755d");
        Package("0.1.58-beta-gaaaaaaaaaa");

        Assert.Throws<E2EException>(() => MsagentCliBuilder.PackedVersion(_directory));
    }

    [Fact]
    public void APackDirectoryWithNoPackageIsRefused() =>
        Assert.Throws<E2EException>(() => MsagentCliBuilder.PackedVersion(_directory));

    [Fact]
    public void TheIsolatedFeedClearsEveryConfiguredSource()
    {
        var content = File.ReadAllText(MsagentCliBuilder.WriteIsolatedFeed(_directory));

        Assert.Contains("<clear />", content, StringComparison.Ordinal);
        Assert.Contains(_directory, content, StringComparison.Ordinal);
        Assert.DoesNotContain("pkgs.dev.azure.com", content, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget.org", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackageVersionAndTheAssemblyVersionAgreeDespiteTheirCommitSuffixes() =>
        MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.57-beta-g25087c755d", "0.1.57-beta+25087c755d", "25087c755dfull0000000000000000000000000");

    [Fact]
    public void AToolInstalledFromAFeedInsteadOfTheWorkingTreeIsRefused()
    {
        var failure = Assert.Throws<E2EException>(
            () => MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.57-beta-g25087c755d", "0.1.58-beta+5293d2d740", string.Empty));

        Assert.Contains("0.1.57-beta-g25087c755d", failure.Message, StringComparison.Ordinal);
        Assert.Contains("0.1.58-beta+5293d2d740", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APublicReleasePackageCarryingNoCommitStillMatchesTheToolBuiltFromTheCheckedOutCommit() =>
        MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.64-beta", "0.1.64-beta+659624319c", "659624319cfull000000000000000000000000");

    [Fact]
    public void APublicReleasePackageStillRefusesAnotherReleaseVersion() =>
        Assert.Throws<E2EException>(() => MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.64-beta", "0.1.65-beta+659624319c", "659624319cfull000000000000000000000000"));

    [Fact]
    public void AToolThatCannotReportItsVersionIsRefused()
    {
        var failure = Assert.Throws<E2EException>(
            () => MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.57-beta-g25087c755d", "unknown", string.Empty));

        Assert.Contains("unknown", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AToolBuiltFromAnotherCommitAtTheSameReleaseVersionIsRefused()
    {
        var failure = Assert.Throws<E2EException>(
            () => MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.57-beta-g25087c755d", "0.1.57-beta+aaaaaaaaaa", string.Empty));

        Assert.Contains("25087c755d", failure.Message, StringComparison.Ordinal);
        Assert.Contains("aaaaaaaaaa", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstalledToolCarryingNoCommitIsRefusedEvenWhenTheReleaseVersionMatches()
    {
        var failure = Assert.Throws<E2EException>(
            () => MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.57-beta-g25087c755d", "0.1.57-beta", string.Empty));

        Assert.Contains("names no commit", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReleasePackageIsRefusedWhenGitCannotNameTheCheckedOutCommit()
    {
        var failure = Assert.Throws<E2EException>(
            () => MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.64-beta", "0.1.64-beta+659624319c", string.Empty));

        Assert.Contains("cannot be tied to the source under test", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReleasePackageIsRefusedWhenTheToolCameFromAnotherCommitThanTheCheckedOutOne()
    {
        var failure = Assert.Throws<E2EException>(
            () => MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.64-beta", "0.1.64-beta+aaaaaaaaaa", "659624319cfull000000000000000000000000"));

        Assert.Contains("other than the checked-out", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABranchPackageIsRefusedWhenTheCheckedOutCommitDisagrees()
    {
        var failure = Assert.Throws<E2EException>(
            () => MsagentCliBuilder.VerifyInstalledVersionMatches("0.1.57-beta-g25087c755d", "0.1.57-beta+25087c755d", "bbbbbbbbbbfull000000000000000000000000"));

        Assert.Contains("other than the checked-out", failure.Message, StringComparison.Ordinal);
    }
}
