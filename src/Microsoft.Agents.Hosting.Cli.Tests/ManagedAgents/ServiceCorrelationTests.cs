using Microsoft.Agents.Hosting.Cli.Core.Http;

namespace Microsoft.Agents.Hosting.Cli.Tests.ManagedAgents;

public sealed class ServiceCorrelationTests
{
    [Theory]
    [InlineData("x-ms-request-id")]
    [InlineData("x-ms-correlation-request-id")]
    [InlineData("request-id")]
    public void ReadServiceRequestId_AcceptsEveryHeaderTheServicesUse(string headerName)
    {
        using var response = new HttpResponseMessage();

        response.Headers.TryAddWithoutValidation(headerName, "service-1");

        Assert.Equal("service-1", ServiceCorrelation.ReadServiceRequestId(response));
    }

    [Fact]
    public void ReadServiceRequestId_PrefersTheFirstHeaderTheServicesUse()
    {
        using var response = new HttpResponseMessage();

        response.Headers.TryAddWithoutValidation("request-id", "third");
        response.Headers.TryAddWithoutValidation("x-ms-request-id", "first");

        Assert.Equal("first", ServiceCorrelation.ReadServiceRequestId(response));
    }

    [Fact]
    public void ReadServiceRequestId_IsAbsentWhenTheServiceSentNone()
    {
        using var response = new HttpResponseMessage();

        Assert.Null(ServiceCorrelation.ReadServiceRequestId(response));
    }

    [Fact]
    public void ReadServiceRequestId_SkipsABlankValue()
    {
        using var response = new HttpResponseMessage();

        response.Headers.TryAddWithoutValidation("x-ms-request-id", " ");

        Assert.Null(ServiceCorrelation.ReadServiceRequestId(response));
    }

    [Theory]
    [InlineData("client-1", "service-1", "clientRequestId=client-1, serviceRequestId=service-1")]
    [InlineData("client-1", null, "clientRequestId=client-1")]
    [InlineData(null, "service-1", "serviceRequestId=service-1")]
    [InlineData(null, null, "")]
    [InlineData(" ", " ", "")]
    public void Describe_NamesOnlyTheIdentifiersThatExist(string? clientRequestId, string? serviceRequestId, string expected)
    {
        Assert.Equal(expected, new ServiceCorrelation(clientRequestId, serviceRequestId).Describe());
    }

    [Fact]
    public void Annotate_AppendsTheIdentifiersAnOperatorCanQuote()
    {
        Assert.Equal(
            "it failed (clientRequestId=client-1, serviceRequestId=service-1).",
            new ServiceCorrelation("client-1", "service-1").Annotate("it failed"));
    }

    [Fact]
    public void Annotate_LeavesAMessageAloneWhenThereIsNothingToQuote()
    {
        Assert.Equal("it failed", ServiceCorrelation.None.Annotate("it failed"));
    }
}
