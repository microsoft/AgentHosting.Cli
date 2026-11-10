using System.Net;
using Microsoft.Agents.Hosting.Cli.Core.Errors;
using Microsoft.Agents.Hosting.Cli.CopilotStudio;
using Microsoft.CopilotStudio.Sync.Dataverse;

namespace Microsoft.Agents.Hosting.Cli.Tests.Copilot;

public sealed class DataverseFailureRedactionTests
{
    private static readonly DataverseRequestException Refusal = new(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"0x80040220\",\"message\":\"denied\"},\"trace\":\"sample.person@contoso.com\"}");

    [Fact]
    public void ForReport_FindsARefusalInsideAnAggregateFailure()
    {
        var reported = Assert.IsType<CopilotStudioException>(DataverseFailureRedaction.ForReport(new AggregateException(new IOException("disk full"), new InvalidOperationException("wrapped", Refusal)), "msagent agent deploy"));

        Assert.Equal(ErrorCodes.CopilotStudio.ServiceError, reported.ErrorCode);
        Assert.Equal("The Copilot Studio service reported a failure during 'msagent agent deploy'. HTTP 403 Forbidden. denied (0x80040220)", reported.Message);
        Assert.Null(reported.InnerException);
    }

    [Fact]
    public void ForReport_LeavesAFailureWithoutADataverseRefusalAsItIs()
    {
        var failure = new InvalidOperationException("plain");

        Assert.Same(failure, DataverseFailureRedaction.ForReport(failure, "msagent agent deploy"));
    }

    [Fact]
    public void ForReport_LeavesATypedFailureThatQuotesNoResponseAsItIs()
    {
        var failure = CopilotStudioException.ServiceError("delete agent", Refusal.Describe(), Refusal);

        Assert.Same(failure, DataverseFailureRedaction.ForReport(failure, "msagent agent delete"));
    }

    [Fact]
    public void ForReport_RedactsARawResponseATypedFailureQuotesWithoutTheRefusalAttached()
    {
        var failure = new QuotingFailure($"The push failed: {Refusal.Message}", $"Grant the privilege the service names: {Refusal.Message}");

        var reported = Assert.IsAssignableFrom<AgentHostingException>(DataverseFailureRedaction.ForReport(failure, "msagent agent deploy"));

        Assert.Equal(("The push failed: HTTP 403 Forbidden. denied (0x80040220)", "Grant the privilege the service names: HTTP 403 Forbidden. denied (0x80040220)"), (reported.Message, reported.Remediation));
        Assert.Equal((failure.ErrorCode, failure.ExitCode, failure.ErrorKind), (reported.ErrorCode, reported.ExitCode, reported.ErrorKind));
    }

    private sealed class QuotingFailure(string message, string remediation) : AgentHostingException(message, ExitCodes.Usage, "quoting-failure", remediation, errorCode: ErrorCodes.CopilotStudio.ServiceError);
}
