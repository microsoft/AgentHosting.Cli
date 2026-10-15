using Microsoft.Agents.Hosting.Cli.E2E;
using Microsoft.Agents.Hosting.Cli.E2E.Infrastructure;

namespace Microsoft.Agents.Hosting.Cli.Tests.E2EHarness;

public sealed class HarnessRunIdTests
{
    [Theory]
    [InlineData("20260914-042324-355f")]
    [InlineData("abcd")]
    [InlineData("run_1-2")]
    public void APathSafeRunIdIsAccepted(string runId)
    {
        Assert.Equal(runId, CommandLine.Parse(["cleanup", "--run-id", runId]).RunId);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    public void ARunIdShorterThanTheSuffixIsRejected(string runId)
    {
        Assert.Throws<E2EException>(() => CommandLine.Parse(["cleanup", "--run-id", runId]));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:\\windows\\temp")]
    [InlineData("run/../../escape")]
    [InlineData("run id")]
    public void ARunIdThatCouldEscapeTheRunsRootIsRejected(string runId)
    {
        Assert.Throws<E2EException>(() => CommandLine.Parse(["cleanup", "--run-id", runId]));
    }

    [Fact]
    public void TheRejectionNamesTheOffendingValue()
    {
        var failure = Assert.Throws<E2EException>(() => CommandLine.Parse(["report", "--run-id", "../outside"]));

        Assert.Contains("../outside", failure.Message, StringComparison.Ordinal);
    }
}
