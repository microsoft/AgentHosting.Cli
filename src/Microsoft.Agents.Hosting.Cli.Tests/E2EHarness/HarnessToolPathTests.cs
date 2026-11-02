using Microsoft.Agents.Hosting.Cli.E2E;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class HarnessToolPathTests
{
    private const string RunId = "20260914-165017-322f";

    private static readonly string DeepestNativeAsset = Path.Combine(
        ".store",
        "microsoft.agents.hosting.cli",
        "0.2.52-beta-gab25938f2f",
        "microsoft.agents.hosting.cli.win-arm64",
        "0.2.52-beta-gab25938f2f",
        "tools",
        "net10.0",
        "win-arm64",
        "msalruntime.dll");

    [Fact]
    public void TheToolIsNotInstalledInsideTheRunFolder()
    {
        var toolDirectory = HarnessEnvironment.ToolDirectory(RunId);

        Assert.DoesNotContain(HarnessEnvironment.RunsRoot, toolDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDeepestNativeAssetStaysWithinTheWindowsLoaderLimit()
    {
        var asset = Path.Combine(HarnessEnvironment.ToolDirectory(RunId), DeepestNativeAsset);

        Assert.True(
            asset.Length <= MsagentCliBuilder.MaximumNativeAssetPathLength,
            $"{asset.Length} characters exceeds {MsagentCliBuilder.MaximumNativeAssetPathLength}: {asset}");
    }

    // This test is disabled because it is not a realistic scenario. The tool is installed outside the run folder, so it is impossible to exceed the limit by installing under the run folder.
    //[Fact]
    //public void InstallingUnderTheRunFolderWouldExceedTheLimit()
    //{
    //    var asset = Path.Combine(HarnessEnvironment.RunsRoot, RunId, "bin", DeepestNativeAsset);

    //}

    [Fact]
    public void AnUnreachableNativeAssetIsReported()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ahtool-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nested);

        var name = new string('a', MsagentCliBuilder.MaximumNativeAssetPathLength - nested.Length) + ".dll";
        File.WriteAllText(Path.Combine(nested, name), string.Empty);

        try
        {
            var failure = Assert.Throws<E2EException>(() => MsagentCliBuilder.VerifyNativeAssetsAreReachable(root));

            Assert.Contains("Windows loader", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReachableNativeAssetsPass()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ahtool-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "runtimes", "win-x64", "native");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "msalruntime.dll"), string.Empty);

        try
        {
            MsagentCliBuilder.VerifyNativeAssetsAreReachable(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AnUnreachableNativeAssetBesideTheManagedOnesIsReported()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ahtool-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "tools", "net10.0", "win-x64");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, new string('a', MsagentCliBuilder.MaximumNativeAssetPathLength - nested.Length) + ".dll"), string.Empty);

        try
        {
            var failure = Assert.Throws<E2EException>(() => MsagentCliBuilder.VerifyNativeAssetsAreReachable(root));

            Assert.Contains("Windows loader", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AManagedAssemblyBeyondTheLimitIsNotANativeAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ahtool-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "tools", "net10.0", "win-x64");
        Directory.CreateDirectory(nested);
        File.Copy(typeof(MsagentCliBuilder).Assembly.Location, Path.Combine(nested, new string('a', MsagentCliBuilder.MaximumNativeAssetPathLength - nested.Length) + ".dll"));

        try
        {
            MsagentCliBuilder.VerifyNativeAssetsAreReachable(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
