using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class AmsMcsPoolTests
{
    private readonly AmsPool _pool = new();

    [Fact]
    public async Task ASetupIsBuiltOnceAndSharedByEveryLaterScenario()
    {
        var builds = 0;

        var first = await _pool.Setup<string>("the home project").GetAsync(() => Task.FromResult($"built {++builds}"));
        var later = await _pool.Setup<string>("the home project").GetAsync(() => Task.FromResult($"built {++builds}"));

        Assert.Equal("built 1", first);
        Assert.Equal("built 1", later);
        Assert.Equal(1, builds);
        Assert.Same(_pool.Setup<string>("the home project"), _pool.Setup<string>("the home project"));
        Assert.Equal("the home project", _pool.Setup<string>("the home project").Name);
    }

    [Fact]
    public async Task EachNameHasItsOwnSetup()
    {
        Assert.Equal("a", await _pool.Setup<string>("A's deployments").GetAsync(() => Task.FromResult("a")));
        Assert.Equal("b", await _pool.Setup<string>("agent B").GetAsync(() => Task.FromResult("b")));
        Assert.NotSame(_pool.Setup<string>("A's deployments"), _pool.Setup<string>("agent B"));
    }

    [Fact]
    public async Task ASetupThatFailedBlocksEveryLaterScenarioWithItsReasonAndIsNotRetried()
    {
        var builds = 0;
        var setup = _pool.Setup<string>("the home project");

        var failure = await Assert.ThrowsAsync<AssertionException>(() => setup.GetAsync(() => { builds++; throw new AssertionException("agent create", "exit 0", "exit 1", null, null); }));
        var blocked = await Assert.ThrowsAsync<ScenarioBlockedException>(() => setup.GetAsync(() => Task.FromResult($"built {++builds}")));

        Assert.Equal($"the home project couldn't be set up earlier in the run: {failure.Message}", blocked.Message);
        Assert.Equal("the home project", blocked.Cause);
        Assert.Null(blocked.BlocksCapability);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task AReplacedValueIsWhatLaterScenariosGet()
    {
        var setup = _pool.Setup<string>("A's deployments");
        await setup.GetAsync(() => Task.FromResult("test-ring"));

        await setup.ReplaceAsync("test-renamed");

        Assert.Equal("test-renamed", await setup.GetAsync(() => Task.FromResult("rebuilt")));
    }
}
